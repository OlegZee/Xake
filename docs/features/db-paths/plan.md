# Plan: compact, relocatable paths in the build database (database 0.7)

Status: questions answered by the owner 2026-11-17 (brief.md "Decisions"); critic review next. Nothing is implemented. Brief: [brief.md](brief.md) (engine facts from
`43706d3`); checked here against the sources at `4ece63b` (`feature/dep-cutoff`, step 1).

## Where the brief and the sources differ

The sources win; each item is a fact, then what it changes.

1. **Target names are absolute already.** `ExecCore.makeTarget` and the file-rule target list (`ExecCore.fs:79, 383`)
   make a target as `ProjectRoot </> name |> File.make`, so `File.Name` of a script target is an absolute path
   whenever `ProjectRoot` is (the default, `GetCurrentDirectory()`). The 0.6 database stores `FileTarget` by that
   `Name`, so targets are no more relocatable than `FileDep`s, and a loaded target already shows an absolute name.
   Brief §5 ("a target shows the same name whether it came from the database or the script") is then met by giving a
   loaded `File` its full path as `Name`, not a root-relative one: see question Q1.
2. **`ProjectRoot` is on `ExecOptions` (`XakeScript.fs`) and `EngineOptions` (`ExecTypes.fs`)**; `Storage.openDb`
   takes no root today (`ScriptRunner.createScriptContext` passes only the path). `openDb` gains the roots.
3. **No database dump exists.** `--dump` is `DependencyAnalysis.dumpDeps`, which prints dependencies of the requested
   targets with `FullName`/`Name`. Brief §6 applies there.
4. **The case rules disagree three ways** (noted, not fixed, as the brief says): `File` compares names ignoring case
   on Unix (`compareNames`, `ignoreCase = Env.isUnix`, macOS and Linux alike) and hashes them case-sensitively there;
   on Windows the reverse. `Target` (`Types.fs`) uses `Ordinal` on Unix and `OrdinalIgnoreCase` on Windows, the
   opposite of `File`. The database map is keyed by `Target`. Consequence for this work: the path table interns by the
   exact string (ordinal), so it never merges two spellings that `Target` keeps apart; the root split follows
   `Target`'s rule (see "Roots"), since that is the one that decides whether a loaded record is found.
5. **Other path strings in a record:** `GetFiles` keeps its `Fileset` (`FilesetOptions.BaseDir : string option` and
   the patterns) as strings. Patterns are relative; a `BaseDir` given as an absolute path stays absolute and is not
   relocated. Out of scope unless the owner says otherwise (Q5).
6. **Variables that hold paths** (`Var`, `EnvVar` values) are recorded as strings and compared by value. A declared
   root whose value comes from a variable that rules also read (a package cache folder, say) moves with that
   variable's value, and every rule that read it reruns anyway. Relocation of a declared root then helps only rules
   that never read the variable (Q3).

## Format 0.7

The file stays: header (`XAKE`, version, date), then an append-only sequence of records. Each record starts with a
one-byte tag:

| tag | record | owner |
|---:|---|---|
| 0 | `DirRec (id, parent, name)`; `parent = -1`: a root, `name` is `$(ProjectRoot)`, `$(NAME)` or a file-system root (`/`, `C:\`, `\\server\share\`) | db-paths |
| 1 | `FileRec (id, dir, name)` | db-paths |
| 2 | `ResultRec of BuildResult` | db-paths (body changes in dep-cutoff step 3) |
| 3 | `RootRec (dirId, absolutePath)`: the value a named root had in the run that appended it (for "root moved") | db-paths |
| 4-15 | reserved for dep-cutoff (below) | dep-cutoff |
| 16+ | free | |

- Ids are 7-bit variable-length integers (a local helper in `Database.fs`; `BinaryWriter.Write7BitEncodedInt` is
  not public on netstandard2.0/net462). `Pickler.fs` does not change.
- A `File` anywhere in a `ResultRec` (`FileTarget`, `FileDep`, `ArtifactDep2`'s target, the `Filelist` of
  `GetFiles`) is its `FileRec` id. `PhonyAction` names stay strings.
- Invariant: a `DirRec`/`FileRec` precedes every record that names it; a parent precedes its children; ids are
  unique in the file.
- An unknown tag fails the read (the file is recreated, as for any unreadable database today). The two features land
  in one release, so no skipping of unknown records is needed; reserving the tags is enough.

**Room for dep-cutoff (step 3).** Its database part (brief "Database (0.7)") maps onto this layout without a second
format:

- No content epochs in 5.0 (dep-cutoff owner decision, 2026-11-17): no epoch counter and no `EpochRec`.
- *Output records* `(Target, Stamp, Size, History)`, `History` a list of `((Stamp, Size), Hash option)`, live in
  `BuildResult`, so they are part of the result pickle; the target inside is a file id like any other.
- *Per-file source table* `(fileId, Stamp, Size, History)` with the same `History`: a record keyed by the file id,
  e.g. tag 4 `SourceRec`, appended when an entry changes; the last one per id wins on load, compaction keeps the live
  ones. The file table gives it a 2-3 byte key instead of a path.
- *Dependency order and grouping of dynamic needs, the static `needs` mark:* fields of the dependency pickle inside
  the result (for instance a `NeedGroup` case or a group index per dependency, a `static` flag). Files in them are
  ids. Whether a changed result body keeps tag 2 or takes a new tag is critic M6 / Q-D, open.
- *`produces` mappings:* part of the producer's `ResultRec`, or tag 6 if they need their own record.
- The picklers become functions of the path table (`result table`); dep-cutoff's new fields reuse the same `file`
  pickler, so whatever it adds is compact and relocatable for free.
- One version: `XakeDbVersion = "0.7"`; 0.4-0.6 read with the 0.6 picklers kept as `Persist.V06`, then rewritten.
  Whichever feature merges second rebases onto the other's 0.7 picklers; no 0.8.

## Roots

- Roots of a run: `$(ProjectRoot)` = `ExecOptions.ProjectRoot` (full, normalized), always; plus
  `ExecOptions.PathRoots` (new, also on `EngineOptions`) and the `pathRoot` script operation (Q2), no command line.
  A root's value is an absolute path, a path relative to `ProjectRoot`, or a variable (`fromVar "NAME"`, Q3: the
  launch-time value; unset = the root is undeclared this run). Shape, e.g.
  `PathRoots : (string * RootValue) list` with `RootValue = RootPath of string | RootVar of string`.
- A relative root is resolved against `ProjectRoot` for this run and recorded as relative (its `RootRec` holds the
  relative text), so it moves with the project.
  Checked at launch: a name is `[A-Za-z0-9_]+` and not `ProjectRoot`, names unique, a value made full and
  normalized; a violation is a launch error like the variable errors.
- Split: the longest root whose path is a prefix of the file's directory on a separator boundary; else the
  file-system root (`Path.GetPathRoot`). Comparison by `Target`'s rule (ordinal on Unix, ignore-case on Windows).
- Load: a root `DirRec` resolves to its value in this run; `/`, `C:\` resolve to themselves. A `ResultRec` that names
  a file under a root this run does not declare is **stale** (Q4): it is not loaded, and the targets it names that are
  resolvable are removed from the map (so an older record of the same target cannot win). The run does not fail;
  those targets rebuild. Counted in the Info line.
- `RootRec`: on the first `Store` of a run, if a named root's value differs from the last `RootRec` for it (or there
  is none), one `RootRec` is appended in the same buffer. On load, a differing value logs at Info
  `root $(X) moved: <old> -> <new>`.

## The database agent

- The agent (`Storage.openDb`) owns the table: `Map<string, int>` for directories and files keyed by full path
  (ordinal), the id -> `File` array from load, and `nextId`.
- `Store result`: collect the files of `result` not in the table and their missing directories; give tentative ids
  from `nextId`; pickle `DirRec`/`FileRec` (and a pending `RootRec`), then the `ResultRec`, into the `MemoryStream`;
  one `Write`; only then commit the ids and `nextId`. A failure in pickling or writing commits nothing, so the next
  `Store` naming the same paths writes their tables again (the brief's guarantee; a failed `Write` may still leave a
  partial tail record, as today).
- Load: the 0.7 loop reads records by tag; `FileRec` builds one `File` per id (interning: every reference is that
  object, one `FileInfo` per distinct file); new ids continue from `max + 1`.
- Compaction and upgrade (the existing branch in `openDatabaseFile`): rewrite with a fresh table holding only the
  paths live records name, ids renumbered, a `RootRec` per named root. Threshold unchanged (`recordCount > Count * 5`,
  counting `ResultRec`s only).
- `openDb` gains the roots: `Storage.openDb (roots: PathRoots) dbpath logger`. `noopDb` is unchanged (no file).
- `Storage.resultPU` (public, used by tests) is replaced by a table-taking pickler plus a test helper that writes and
  reads a whole database stream.

## Display and diagnostics

- A loaded `File` is `T (fullPath, FileInfo fullPath)` (Q1: `Name` is the full path, as in 4.0). Root-relative
  display in logs and progress is a separate later feature.
- `--dump` prints paths through a formatter that shows the root: `$(ProjectRoot)/src/a.cs`, `$(NAME)/rest`, else
  absolute.
- Info at open: `database: <n> records, <d> dirs, <f> files, <size>`; plus `root ... moved` and `<k> stale records
  (undeclared root $(X))` when they apply.

## Tests (one or more per Acceptance item)

- **Size and load time:** a generator in the tests writes a synthetic 0.6 database shaped like the measured one
  (about 600 results x 1 200 file references, 36 000 distinct files in 3 500 directories, one `GetFiles` list per
  result) and upgrades it; the test asserts size ratio >= 5. Load time (cold: first open in the process; warm: second)
  before and after is measured by an `[<Explicit>]` test and recorded in `docs/features/db-paths/measurements.md`,
  not asserted.
- **Upgrade without rebuild:** a project built with a 0.6 database (written by the kept 0.6 picklers), opened by 0.7:
  a no-change run reruns nothing; 0.4 and 0.5 files load too.
- **Relocation:** build in folder A, copy A to B with timestamps (and `.xake`), build in B: nothing reruns. Same with
  a declared root pointed at a copied folder.
- **Undeclared root:** build with `$(EXT)` declared, then without it: the targets depending on files under it
  rebuild, the run succeeds.
- **Failed `Store`:** an injected pickling failure (as `AgentFailureTests` does) leaves the file readable; the next
  `Store` naming the same new paths writes their `DirRec`/`FileRec` again; the file loads.
- **Concurrent stores:** N results posted from parallel tasks in two different orders: both files load to equal maps.
- **Behaviour without roots:** the existing suite passes with only `$(ProjectRoot)`.
- Unit: split by longest root, separator boundary (`/a/bc` not under `/a/b`), Windows drive and UNC roots (the
  split function is pure and tested with both case rules), interning (same object), id commit only after write.

## Order of work

1. Docs first: `docs/database.md` (format 0.7, roots, relocation), `PathRoots` in the options docs, release notes
   "5.0.0 (unreleased)".
2. Path table and 0.7 picklers, keeping 0.6 for read; upgrade path. Tests: unit, upgrade, failed store, concurrency.
3. Roots: options, launch checks, split, resolve, stale records, `RootRec`. Tests: relocation, undeclared root.
4. Display and diagnostics (`--dump`, Info lines).
5. Synthetic database, size test, load-time measurements.
6. Verify: `dotnet build src/core -c Release` 0 warnings, `dotnet test src/tests -c Release`,
   `dotnet fsi build.fsx -- -- build test pack`.

## Questions (answered 2026-11-17, see brief.md "Decisions")

Q1 stored relative to roots, `Name` stays full, display later; Q2 option + `pathRoot`, relative root paths allowed
and recorded relative; Q3 `fromVar` roots; Q4 not loaded; Q5 `BaseDir` stays a string; Q6 one shared 0.7, tags 4-15
for dep-cutoff. The original questions follow.

- **Q1. Name of a loaded `File`.** The brief wants it relative to its root so logs do not show long absolute paths,
  and equal to the name of the same target made by the script. In the sources a script target's `Name` is already
  absolute (`ProjectRoot </> name`). Options: (a) loaded `Name` = full path: identical to 4.0 output, consistent with
  script targets (recommended); (b) root-relative `Name` on load as the brief says: logs then show `out/a.dll` for a
  loaded target and `/abs/out/a.dll` for the same target made by the script; (c) (a) now, and root-relative display
  for all targets later as a separate change in the logger/progress, not in `File.Name`.
- **Q2. How a script declares roots.** `ExecOptions.PathRoots` only (set in `xake { }` options code), or also a
  `xakeScript` operation `pathRoot "NAME" "/abs/path"` and a command-line form? Proposal: the option plus a builder
  operation; no command line.
- **Q3. Roots from variables.** A package cache folder usually comes from a variable. Should a root be declarable
  by a variable (`pathRoot "NUGET_PACKAGES" (fromVar "NUGET_PACKAGES")`, value taken from the launch-time set, an
  unset variable = an undeclared root)? Note fact 6: rules that read that variable rerun on a move anyway.
- **Q4. Undeclared root: drop or keep.** Proposal above: records naming it are not loaded (and are gone at the next
  compaction). Alternative: keep them in the file untouched, so declaring the root again later revives them; costs
  carrying unresolved records through compaction. Proposal: drop.
- **Q5. `Fileset.BaseDir`.** Leave the `GetFiles` fileset's `BaseDir` as a string (an absolute `BaseDir` is not
  relocated; the file list itself is), or store it as a directory id too? Proposal: leave it; note it.
- **Q6. Coordination with dep-cutoff.** The tag table above reserves 4-15 for it. Agree that dep-cutoff takes its
  records from that range and that whichever branch merges second adapts its picklers (no separate 0.8)?
