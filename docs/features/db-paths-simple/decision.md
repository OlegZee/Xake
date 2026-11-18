# Phase 1 decision: `File`, database 0.7, epochs

Approved by the owner on 2026-11-18. This file is the specification for the implementation; the
reasoning, the alternatives, the worked example and the measurements are in [design.md](design.md),
the task in [brief.md](brief.md). Where the two differ, this file wins.

Phase 1 of Xake 5.0. Phase 2 (rule header, `also`) and phase 3 (hashing and content cut-off) come
later; the database format made here is final for them (phase 3 only starts filling the hash).

## 1a. `File`, case, two spellings

**`File`** (`File.fsi`, `File.fs`):

- `File = private F of string`: the normalised absolute path, `/` as the separator on every OS.
  No `FileInfo`, no root, nothing cached; `exists` and `getLastWriteTime` ask the disk each call.
- Normalisation is one internal function, `Path.collapse` (today's `Path.RuleImpl.collapsePath`
  made internal and shared): `\` -> `/` on Windows; `.` and `..` collapsed; repeated separators
  removed; the root (`/`, `C:`) kept and never popped; a trailing separator dropped (except the
  root); a leading `//` (UNC) kept on Windows; case kept; lexical (no symlink resolution). Rule
  and mask matching use the same function.
- `File.make path`: a rooted path as is, a relative one against the current directory at the call;
  empty or whitespace fails as today.
- `Name` = the absolute path in the `/` form; `FullName` = the same path in the OS form.
- `File.display (root: string) (f: File) : string`: relative to `root` with `/` when `f` is under it,
  else absolute. `File.comparer : StringComparer` = `StringComparer.Ordinal`, the one comparer.
- `Equals`, `GetHashCode`, `CompareTo`: ordinal on the normalised string, consistent.
- `File.undefined` removed (Xake.Dotnet moves to `File option` at its 5.0; not done here).

**One case rule:**

- `Target` (`Types.fs`): ordinal on every OS through `File.comparer`.
- Rule matching (`PatternMatch.fs:201`): ordinal on every OS. The `windows` flag keeps only its
  other meanings (`\` a separator, UNC roots).
- **Filesets ignore case by default on every OS.** `FilesetOptions.CaseSensitive: bool` (default
  `false`) and the builder operation `casesensitive` in `fileset { }` make the search ordinal. A
  separate flag from `windows` in `PatternScan.fs` (lines 197, 243, 317-318). Files found keep their
  spelling on disk and compare ordinally. The flag is part of the fileset pickle.
- `Fingerprints.fs:81,130`: ordinal.
- `WorkerPool.mapKey`: unchanged (the `FullName` string, ordinal).
- The engine's own output (`Target.ShortName`, change reasons, `--dump`) shows
  `File.display ProjectRoot f`.

**Two spellings of one file in a run:**

- `ExecContext.Spellings`: a `ConcurrentDictionary` keyed by the path upper-cased with the
  invariant culture, value = the first spelling and whether it was a target; created per run where
  the run memo is created (`demandTarget`, next to `Scheduler.newRun`).
- Checked when a target starts (`execOne`), when a `FileDep` is recorded, and for every file of a
  fileset result.
- Same key, another spelling: both targets -> error `target 'out/Foo.dll' is also named
  'out/foo.dll'` (the target fails); otherwise a warning, once per pair. On every OS. The case on
  disk is not checked.

## 1b. Database 0.7

**Version:** `XakeDbVersion = "0.7"`, `XakeDbMinReadVersion = "0.4"`. Header as today.

**Records**, each starting with a tag byte, appended, the last one per key wins:

| Tag | Record | Fields |
|---|---|---|
| 0 | `DirRec` | dirId, root, dir (string) |
| 1 | `PathRec` | fileId, dirId, name (string) |
| 2 | `FileRec` | fileId, stamp (int64 ticks), size, hash (option of 8 bytes), changed (epoch) |
| 3 | `Result` | epoch, built (int64 ticks), targets, dependencies, steps, staticCount |

- Ids, sizes, epochs, counts and list lengths are 7-bit varints (`Pickler.varint`;
  `Write7BitEncodedInt` is not public on netstandard2.0). **Lists in 0.7 records carry a varint
  length in front**; the 0.4-0.6 reader keeps today's marker-byte list. Size is written as
  `size + 1`, `0` meaning unknown.
- **Paths:** `File.display ProjectRoot f` split at the last `/`. `DirRec.root`: `0` = absolute
  directory, `1` = the project root (phase 1 uses only these two; the number is the room kept for
  another root later). `dir` is `""` for a file directly in the root. Load:
  `File.make (root </> dir </> name)` (an absolute directory stays absolute). `DirRec`/`PathRec`
  are written once, before the first record that uses the id, in the same `Write`. Writer:
  `Dictionary<File,int>` and `Dictionary<string,int>`, seeded from the loaded file; reader: arrays
  id -> `File`, id -> dir. An unknown id makes the read fail.
- **Targets** (in `Result.targets` and `TargetDep`): a file target is a file id, a phony target its
  name. **Dependencies:** `FileDep fileId`; `TargetDep` of a file (fileId) and of a phony (name) as
  two tags; `GetFiles` = fileset (today's fileset pickle plus `CaseSensitive`) and a list of file
  ids; `EnvVar`, `Var`, `SecretDep`, `AlwaysRerun` encoded as today (strings, options, digests).
- **Steps:** as today (name, start, own time, wait time), in a varint-length list.
- **Writing:** every record (with its new `DirRec`/`PathRec`) is pickled into a buffer and written
  with one `Write`. **A failed write stops further appends for the run**, logged once; results
  stay in memory for the run.
- **Incremental:** nothing is ever rewritten in place; the epoch is not stored (see 1c). The only
  rewrites are compaction and the upgrade, through the `.bak` file as today.
- **Compaction:** triggered when the number of `Result` records exceeds 5 x the live results;
  rewrites a fresh path table, the live results and the `FileRec`s of files they reference.

**Opening:**

- **Lock:** `<db>.lock` (`.xake.lock`) next to the database, opened with `FileShare.None` before the
  load and held until the database is closed (engine mode: the engine's lifetime). A second opener
  waits up to 30 s (logged once at Message: "waiting for another Xake run on this database"), then
  fails with "another Xake run uses this database (.xake.lock)". `noopDb` takes no lock.
- **Versions:** newer than this Xake knows -> recreated, message "database written by a newer Xake
  (version X), recreating"; older than `XakeDbMinReadVersion` -> recreated, message "database
  version X is no longer read, recreating"; another read failure -> recreated as today.
- **Torn last record:** an end of file inside the last record (a killed process) cuts the file
  back to the end of the previous record and keeps everything before it. Nothing in the middle is
  repaired.
- **Upgrade 0.4-0.6:** read with the legacy pickler (strings; `FileDep` by absolute `FullName`;
  `FileTarget` by its raw `Name`, joined with the root), converted, rewritten as 0.7:
  - per file, a `FileRec` with the latest stamp any result recorded for it, size unknown, no hash,
    `changed = 1`;
  - per result, `Epoch = 1` if every file stamp it recorded (`FileDep`, `ArtifactDep2` of a file)
    equals that file's latest stamp within the 10 ms tolerance and every phony dependee finished
    no later than the stamp recorded for it; else `Epoch = 0`; `ArtifactDep` (0.4) -> `Epoch = 0`.
    `Built` keeps its time;
  - `ArtifactDep`/`ArtifactDep2` become `TargetDep`, `FileDep (f, stamp)` becomes `FileDep f`;
  - the secret scrub of today stays.
  A no-change run right after the upgrade rebuilds nothing.
- `Storage.openDb (root: string) (dbpath: string) (logger: ILogger)`: the root is `ProjectRoot`;
  `ScriptRunner` passes it. The root's value is never written.

**Public read API** (replaces `Storage.resultPU`, which goes):

```fsharp
type DbRecord = Result of BuildResult | FileState of File * FileState
val Storage.readRecords  : root: string -> dbpath: string -> DbRecord seq          // file order
val Storage.readDatabase : root: string -> dbpath: string -> BuildLog.Database * int64  // last state, last epoch
```

No lock; the file is opened for reading with `FileShare.ReadWrite`, so a running build's database
can be read; a torn last record ends the sequence. An older version yields its converted records.

## 1c. Epochs (the verdict moves to them in phase 1, without hashing)

**Types** (`Types.fs`, `Database.fs`):

```fsharp
type FileState = { Stamp: Timestamp; Size: int64; Hash: uint64 option; Changed: int64 }   // Size -1 = unknown
type Dependency =
    | FileDep of File            // a file without a rule
    | TargetDep of Target        // a rule's output file or a phony target
    | GetFiles of Fileset * Filelist
    | EnvVar of string * string option
    | Var of string * string option
    | AlwaysRerun
    | SecretDep of name: string * source: SecretSource * digest: SecretDigest
type BuildResult = { Targets: Target list; Built: Timestamp; Epoch: int64; Depends: Dependency list
                     Steps: StepInfo list; StaticCount: int }
// BuildLog
type Database = { Status: Map<Target, BuildResult>; Files: Map<File, FileState> }
```

`ArtifactDep` and `ArtifactDep2` are removed from the types (converted on load).

**The epoch is the run number.** One value per run (`demandTarget`, `Scheduler.newRun`; in engine
mode each run takes the next one). On load: the largest `Result.epoch` or `FileRec.changed` in the
file + 1 (1 for an empty file). Never stored on its own. Every result built in run `r` gets
`Epoch = r`.

**Observation of a file** (through the database agent, message `Observe of File * reply`):

- At most once per file per run (an in-memory set cleared at `newRun`), **except a rule's output,
  which is observed again right after the rule finishes** (the run's own write).
- Stat the file (exists, last write time, length). No `FileRec` yet -> new record,
  `changed = r`. Stamp equal within 10 ms and size equal (or unknown) -> unchanged; an unknown size
  gets the real one (append a `FileRec`, `changed` kept). Otherwise the stat moved: phase 1 has no
  hash, so `changed = r`, new stamp and size, append a `FileRec`. A missing file: no record change;
  the verdict reports it as today.
- Hashes are never computed in phase 1 (`Hash = None`).

**Recording** (`ExecCore.execNeed`, `ScriptFuncs`): a file without a rule -> observe, record
`FileDep f`; a rule target -> after it finished (built or found up to date), observe its file
output(s) if any, record `TargetDep t`.

**Verdict** for a target with a stored result of epoch `e` (`DependencyAnalysis.getDepState`;
replaces the stamp comparison and `getChangeStamp`):

- `FileDep f`: missing -> files changed (as today); else observe; `changed > e` -> files changed.
- `TargetDep (FileTarget f)`: missing -> `DependsMissingTarget`; observe; `changed > e` ->
  `Depends`; else the dependee's own change reasons non-empty -> `Depends` (4.0's transitive rule,
  kept until phase 3).
- `TargetDep (PhonyAction p)`: no result or its `Epoch > e` -> `Depends`; else the transitive rule
  as above.
- `GetFiles`, `EnvVar`, `Var`, `SecretDep`, `AlwaysRerun`: as today.
- The decisions equal 4.0's (a moved stat is a change), up to one extra rebuild in the races
  described in design.md, never a missed one.

**`StaticCount`**: the number of leading `Depends` entries that came from the rule header; phase 1
writes 0 and round-trips it.

**`--dump`** (`DumpDeps`): shows each result's epoch and, for a file dependency, the file's stamp,
size and `changed`.

## Documentation (first, before the code)

- `docs/release-notes.md`: a section "5.0.0 (unreleased)" with the breaking changes of
  design.md ("Public breaking changes", as updated) and the fileset text:
  "Filesets ignore case by default on every OS; `casesensitive` in `fileset { }`
  (`FilesetOptions.CaseSensitive`) restores a case-sensitive search. Rules, targets and `File`
  stay ordinal, with no option. On Windows and macOS filesets behave as before; on Linux they
  become case-insensitive."
- `docs/implnotes.md`: the database section (0.7 records, epochs, the verdict, observation, lock,
  upgrade, read API); `docs/overview.md` (case rule, line 173 and the 4.0 note near 284);
  `docs/delegated.md` (`ArtifactDep2` -> `TargetDep`, epochs); `docs/tasks.md` (database format
  sentence, 0.6 -> 0.7); `docs/cheatsheet.md` if it names `File.undefined` or case.
- Public repository: no product, customer or company names anywhere.

## Tests (done means)

- `File`: normalisation table with the Unix and the Windows rules (the Windows rules run on every
  OS through an internal flag); `Equals`/`GetHashCode`/`CompareTo` consistent (property test with
  case variants); case variants distinct in `Set<File>`, `Set<Target>`, rule lookup, `WorkerPool`.
- Filesets: `*.CS` finds `a.cs` on every OS and keeps the disk spelling; with `casesensitive` it
  does not.
- Two spellings: target error naming both, source warning (also `a.cs` + `A.cs` from one fileset
  on Linux).
- Round trip of every record kind (`DirRec`, `PathRec`, `FileRec` with and without a hash and with
  an unknown size, each `Dependency` case, `StaticCount`, `Epoch`, `Built`); paths outside the root
  stay absolute.
- Epochs: no-change run rebuilds nothing and appends nothing; a source edit rebuilds the chain; a
  dynamic `need` of a rebuilt target and of a phony does not rerun the dependent on the next run; a
  source saved during the build after its observation reruns its dependents on the next run;
  engine mode: two runs get consecutive epochs.
- Lock: a second open of the same database waits and then fails (use a short time-out in the test
  through an internal parameter).
- Versions: a newer and a too-old database are recreated with their messages; a torn last record
  is cut off and the rest kept.
- 0.6 -> 0.7: a 0.6 file written by a 0.6 fixture writer (kept in the test; today's
  `StaleArtifactDepTests` shows how), then a no-change run builds nothing and the file is 0.7; a
  0.6 result that saw an older stamp reruns; a 0.4 `ArtifactDep` reruns once.
- Read API: `readRecords` gives every record in order, `readDatabase` the engine's state; both
  read a database whose lock is held.
- Relocation: build, copy the tree with `.xake` (times preserved) to another folder, build: nothing
  rebuilt.
- Size: synthetic database, 1000 results x 30 file dependencies, paths of 114 bytes mean: 0.6 vs
  0.7 bytes, at least 5x (expected far more). `[<Explicit>]` test measuring the file named by
  `XAKE_DB_SAMPLE` (a real 0.6 database): bytes before and after the upgrade.
- `dotnet build src/core -c Release`: 0 warnings; `dotnet test src/tests -c Release`: green;
  `dotnet fsi build.fsx -- -- build test pack`: green.

## Amendments after the critic (2026-11-18)

Correctness clarifications within the decisions above, from [critic.md](critic.md). Where they
differ from the sections above, they win.

- **A1, recording re-stats** (critic 1). When a body records `FileDep f` (also each file of a
  fileset result), `f` is statted again even if it was observed in this run. If the stat moved and
  no other body has recorded `f` in this run: `changed` = the newest epoch (A2), append the
  `FileRec`. If another body already recorded `f` in this run: leave the `FileRec` (the next run
  sees the move; both dependents rerun). Observations made only by a verdict stay cached.
- **A2, epoch hand-out** (critic 2, 3). Every `Scheduler.newRun` takes the next epoch: each target
  group in `runTargets`, each `demandTarget` (setup, teardown, engine `Demand`). The observed set
  and `Spellings` are cleared exactly when the epoch advances. Runs may overlap in engine mode
  (`Demand` of different names): `Observe` always stamps `changed` with the newest epoch handed
  out, and a result takes the epoch captured in its run context when its task started.
- **A3, delegated rules** (critic 4). A result produced by the body (`bodyThunk`) gets the run's
  epoch. A result returned without the body running keeps the epoch of the result already in the
  local database for that target (the run's epoch if there is none); a foreign `Epoch` is never
  stored. On `Store`, dependency files without a `FileRec` are observed. `docs/delegated.md` says so.
- **A4, write failures** (critic 5). A failed `Write` stops appends until the database is closed
  (in engine mode they do not resume at the next run). A pickling failure (nothing written)
  discards the ids it allocated and the agent continues; `AgentFailureTests` "survives a failed
  store" keeps passing.
- **A5, lock** (critic 6). The lock is taken first: `--resetdb` (today `cleanupDb` before
  `openDb`) and the `.bak` restore run under it (for example `openDb` takes the reset flag, or
  `cleanupDb` takes the lock; the smaller change). The lock file is never deleted. A failed
  `openDb` releases it. Readers open with `FileShare.ReadWrite ||| FileShare.Delete`; a file that
  is missing or short during a compaction gives an empty or shorter sequence, no exception.
  Release note: a recipe that starts Xake on the same project root waits 30 s and fails.
- **A6, roots and paths** (critic 7). `ProjectRoot` is normalised once at launch with
  `Path.collapse`. `collapse` upper-cases a Windows drive letter (`c:` -> `C:`) for every path.
  `File.display` tests the root prefix on a segment boundary (`/work/proj` is not a prefix of
  `/work/proj2/a.c`). On Windows `File.make` resolves `\foo` (no drive) and `C:foo` (drive-relative)
  with `Path.GetFullPath` before `collapse`.
- **A7, upgrade** (critic 8). A file's converted `FileRec` takes the stamp recorded by the result
  with the latest `Built` (not the largest time). An `ArtifactDep2` stamp of `DateTime.MinValue`
  counts as no stamp (`Epoch = 0`). On Linux an upgraded `GetFiles` is re-scanned ignoring case;
  with case variants on disk its dependent reruns once (said in the test and the release note).
- **A8, the rest of 1c** (critic 9). `noopDb` implements `Observe`, `Files` and epochs (no file, no
  lock). `--dump` and `--dryrun` observe in memory only and append nothing. The verdict keeps
  today's cases "No dependencies", "Not built yet" and "target file does not exist". When a
  multi-output rule ran, all its file targets are re-observed.
- **A9, two spellings and the case flag** (critic 10). The check covers file targets that matched
  a rule (not plain files passing through `execOne`) and file dependencies; phony names are
  excluded and never share the key space with paths. The `PatternMatch` compile cache key includes
  the case flag.

## Budget

`Database.fs` about +225 lines, core about +330, tests about +350 (estimates from design.md;
report the actuals). No new source module. Anything beyond this file goes to the "Later" list in
[tracker.md](tracker.md), not into code.
