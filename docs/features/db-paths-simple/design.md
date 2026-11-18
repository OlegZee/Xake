# Phase 1 design report: `File`, database 0.7, content data and epochs

Status: **for the owner's approval** (no code written); decisions recorded below. Base: `dev` at
`1837856`, branch `feature/db-paths-simple`. Inputs: [brief.md](brief.md),
[file-type-brief.md](file-type-brief.md), the dep-cutoff brief of the closed attempt (option B).

## Decisions (owner)

- 2026-11-18, **paths:** a one-level `DirRec` + `PathRec (id, dirId, name)`, from the measurement
  below.
- 2026-11-18, **static needs:** one count (`StaticCount`), not strings; **no need groups** (Later).
- 2026-11-18, **content data: epochs, no history** (supersedes the dep-cutoff decision of 2026-11-17
  "no epochs", the `(Stamp, Size) -> Hash` history and the per-dependency size):
  - **Epoch = the run number** (Shake's step): one value for a whole run (one `demandTarget`,
    `Scheduler.newRun`), next = the largest value in the file + 1 on load, not stored separately.
  - **Per file, one record** `FileRec (fileId, stamp, size, hash?, changed)`, the last one wins. It is
    the stat cache (`stamp`, `size` from one stat call) and the run in which the file's content was
    last seen to change.
  - **Per result** `built` = the epoch of the run that built it. **A dependency stores only the id**
    (`FileDep fileId`, `TargetDep fileId | phonyName`): no stamp, no size, no hash.
  - **Rule:** a target is up to date iff every file/target dependency has `changed <= built` (and,
    as in 4.0, a dependee target is itself up to date), and the other dependencies compare as today.
  - **A file is observed at most once per run** (an in-memory cache reset at `Scheduler.newRun`):
    stat equal to the record -> unchanged; stat moved -> with a hash, rehash and compare; without a
    hash (phase 1, `off`, a source not hashed yet) -> changed. Changed -> `changed = current run`.
    A new record gets `changed = current run`. A phony target's `changed` is its `built`.
  - **Exclusive database:** one writer; a second Xake run on the same database waits, then fails
    with a clear message (lock, see 1b).
  - **Hash:** the built-in SHA-256, first 8 bytes, in `FileRec` only. Measured 2026-11-18 on an
    Apple M5 Pro, .NET 11: 3.5 GB/s per core in memory, 3.0 GB/s in 10 KB pieces, 2.1 GB/s from a
    file in the page cache; no faster hash and no new package.
  - **Size** only in `FileRec` (part of the stat check: catches a rewrite inside the stamp tolerance
    or a copy with a preserved stamp and another length).
  - **`ArtifactDep2` -> `TargetDep`**, one record per file, no grouping by producer (cut-off is
    per file; the producer check is memoised already; a group's output set changes with `also`).
    The 0.4 `ArtifactDep` disappears from the types (converted on load, below).
  - What epochs give up against a hash in each dependency: A->B->A when the intermediate B was
    observed by another target: one extra rebuild of the nearest dependent, stopped there by
    cut-off (phase 3). Never a missed rebuild.

### Why the epoch is per run, not per event

A per-event counter (`built` taken when a body starts) breaks dynamic `need`: K starts (`built = 5`),
its body needs D, D is rebuilt with new bytes (`changed = 6`), and K, which read the new D, looks out
of date on every following run; a phony needed from a body would always look newer than its
dependent. With one value per run, everything built in run `r` has `built = r`, everything seen
changed in run `r` has `changed = r`, and `r <= r` holds. A file changed on disk after its
observation in run `r` (a source saved during the build) is seen in the next run: `changed = r + 1 >
built = r`, the dependent reruns.

### Example (phase 3 behaviour; `app` takes `main.o` through header `needs`)

| Run | Disk | Observed | Decisions | Appended |
|---|---|---|---|---|
| 1 | clean | all new: `changed = 1`; `main.o`, `app` hashed after build | build `main.o`, `app` (`built = 1`) | `D`, `P` x4, `F` x4, `R` x2 |
| 2 | no change | stat equal everywhere | skip both | nothing |
| 3 | comment edit in `main.c` | `main.c`: no hash yet -> `changed = 3`, hash `#C2`; `main.o` rebuilt, same hash -> `changed` stays 1 | `main.o` built (3); `app`: 1 <= 1, **skip** (cut-off) | `F main.c`, `F main.o` (new stamp), `R main.o` |
| 4 | code edit | `main.c` `#C3` -> 4; `main.o` new bytes -> 4 | both rebuilt (`built = 4`) | `F` x3, `R` x2 |
| 5 | revert `main.c` to `#C2` | `#C2 != #C3` -> 5 | both rebuilt: a real change against what `main.o` last read | `F` x3, `R` x2 |
| 6 | edit and revert without a build (or `touch`, branch switch and back) | stat moved, hash equal -> no change | skip both | `F main.c` (new stamp only) |
| 7 | edit to B, build only `test.o` (observes B: `changed = 7`), revert to A, build `app` | `#C2 != #C4` -> 8 | `main.o` rebuilt once more (the cost), same bytes -> `app` skipped | |

In phase 1 (no hashing, see question 5) every moved stat is a change, so runs 3 and 6 rebuild as
in 4.0.

## 1a. The `File` type

### Signature (`File.fsi`)

```fsharp
[<Sealed>]
type File =
    interface System.IComparable
    override Equals: obj -> bool          // ordinal on the normalised string
    override GetHashCode: unit -> int     // ordinal hash of the same string
    member Name : string                  // = File.display <current directory> f (kept, see below)
    member FullName : string              // absolute, OS form ('\' on Windows)
    // static member undefined  -- REMOVED

module File =
    val make : string -> File             // absolute as is, relative against the cwd at the call
    val display : root: string -> File -> string   // NEW: relative under root ('/'), else absolute
    val comparer : System.StringComparer  // NEW: StringComparer.Ordinal, the one path comparer
    val getFileName / getFileExt / getDirName / getFullName / exists / getLastWriteTime  // unchanged
```

- Representation: `File = private F of string`, the normalised absolute path with `/` separators.
  No `FileInfo`, no root, nothing cached. `exists` / `getLastWriteTime` ask the disk each call.
- Normalisation: one function, today's `Path.RuleImpl.collapsePath`, made an internal
  `Path.collapse` (`Path.fs` compiles before `File.fs`), called by `File.make` and by rule/mask
  matching. Rules: `\` -> `/` on Windows; `.`/`..`
  collapsed; repeated separators removed; root (`/`, `C:`) kept; a trailing separator dropped (new,
  except the root); case kept; lexical. A leading `//` (UNC) is kept on Windows (today's
  `collapsePath` loses it; fixed in the same function, 1 line).
- **`Name` decision: kept**, as `display (current directory)`. Reason: about 200 `.Name` reads in
  Xake.Dotnet (not all on `File`) and the engine's own logs (`Target.ShortName`); with
  `ProjectRoot` = cwd (the default) it is exactly the project-relative display. Release note: `Name`
  is no longer "the string as given" but the path relative to the current directory (or absolute).
- `undefined` removed. Xake.Dotnet uses it in `Csc`/`Fsc` settings (`Out`, `Doc`); it moves to
  `File option` there at its 5.0 (noted as a breaking change for Xake.Dotnet, not done here).

### One comparison rule

| Place | Today | After |
|---|---|---|
| `File` (`File.fs`) | ignore-case on Unix (inverted), hash inconsistent | `File.comparer` (ordinal) |
| `Target` (`Types.fs`) | `OrdinalIgnoreCase` on Windows | `File.comparer` |
| rule / mask matching (`PatternMatch.fs:201`, `PatternScan.fs:243,317-318`) | ignore-case on Windows | ordinal; `windows` keeps meaning `\` separator and UNC only |
| `WorkerPool.mapKey` | `Target.FullName` string key (F# `Map`, ordinal) | unchanged, already ordinal once `FullName` is normalised |
| `Fingerprints.fs:81,130` | ignore-case on Windows | `File.comparer` (2 lines; in core, so in scope; flag if you disagree) |

### Two spellings of one file

- `ExecContext` gets `Spellings: ConcurrentDictionary<string, string * bool>` (key: the path
  upper-cased invariant; value: first spelling, was-target), created per run with the memo.
- Checked where a `File` enters the run: a target when `execOne` starts it (`ExecCore`), a source
  when a `FileDep` is recorded (`ScriptFuncs.recordDependency`) and fileset results (`GetFiles`).
- Same key, different spelling: both targets -> error `target 'out/Foo.dll' is also named
  'out/foo.dll'` (the target fails); otherwise -> warning, once per pair. All OSes. Disk case is not
  checked.

## 1b. Database 0.7

### File layout

```
header  (XakeSign "XAKE", XakeVer "0.7", ScriptDate)          -- as today
record* -- each starts with a tag byte (new in 0.7):
  DirRec   (dirId, dir: string)                               -- before the first PathRec using it
  PathRec  (fileId, dirId, name: string)                      -- before the first record using it
  FileRec  (fileId, stamp, size, hash: 8 bytes option, changed: epoch)   -- last per file wins
  Result   (built: epoch, targets, deps, steps, staticCount)
    dep:   FileDep   fileId
           TargetDep fileId | phonyName
           GetFiles  fileset, fileId list
           EnvVar / Var / SecretDep / AlwaysRerun             -- as today
```

Ids, sizes and epochs are varints; the stamp is 8 bytes (ticks).

- Path string = `File.display ProjectRoot f` with `/`: relative under the root, absolute outside
  it. It is split at the last `/`: `DirRec` holds the directory part (one level, no parent chain;
  `""` for a file directly in the root), `PathRec` the file name and the directory id. Load:
  `File.make (root </> dir </> name)` (an absolute directory stays absolute). Targets, `FileDep`,
  `TargetDep` targets, `GetFiles` file lists, `FileRec` all go through the same `file` pickler.
  `Fileset` masks and `BaseDir` stay strings (as today).
- Path table: writer `Dictionary<File, int>` and `Dictionary<string, int>` for directories (ids
  0..n, seeded from the loaded table so appends continue), reader `ResizeArray<File>` and
  `ResizeArray<string>` (one `File` per path). New `DirRec`s and `PathRec`s go out before the record
  that uses them, in the same `Write`. Unknown id on load -> the read fails ->
  the database is recreated, as today for any unreadable file.
- Writing: pickle into a buffer with a codec that collects newly allocated ids, then write their
  `DirRec`/`PathRec`s and the record in one `Write`. **A failed write stops further appends for the
  run** (logged once; results stay in memory for the run). This replaces today's
  "count and continue" and avoids ids that were allocated but never written.
- Compaction / upgrade rewrites with a fresh table of live paths and the `FileRec`s that live
  results reference. The compaction trigger counts `Result` records only.
- **Lock** (decision 2026-11-18): the database is opened exclusively for the run (engine mode: for
  the engine's lifetime). A separate `.xake.lock` next to `.xake`, opened `FileShare.None`, so the
  database file can still be moved for compaction and a held lock is never taken for a corrupt
  file. A second run waits (proposal: 30 s, logged once) and then fails with "another Xake run
  uses this database".

### In memory

```fsharp
type FileState = { Stamp: Timestamp; Size: int64; Hash: uint64 option; Changed: int64 }
type Dependency =
    | FileDep of File            // a file without a rule
    | TargetDep of Target        // a rule's output file or a phony target
    | GetFiles of Fileset * Filelist
    | EnvVar of ... | Var of ... | SecretDep of ... | AlwaysRerun      // as today
type BuildResult = { Targets; Built: int64 (epoch); Depends; Steps; StaticCount: int }
type Database = { Status: Map<Target, BuildResult>; Files: Map<File, FileState> }
```

`Built` as a wall-clock time is gone: the start time is in `Steps`. Agent messages: `GetResult`,
`Store`, plus `Observe of File * reply` (the once-per-run observation, it appends a `FileRec` when the
state changed) and the run epoch handed out at `newRun`.

### Picklers

`Persist` becomes parameterised by the file pickler: `results (file: PU<File>)` builds `target`,
`dependency`, `filelist`, `result`. Two instances:

- 0.7: `file` = varint id through the codec;
- 0.4-0.6 (read only): `file` = string, `File.make (root </> s)` (old `FileDep` absolute
  `FullName`s load as is; old `FileTarget` raw `Name`s, usually relative to cwd, are joined to the
  root). Old files have no tags: the reader branches on the header version.

`Pickler.fs` gains `varint` (7-bit encoding; `Write7BitEncodedInt` is not public on
netstandard2.0) for ids, sizes and epochs.

### Upgrade and version

`XakeDbVersion = "0.7"`, `XakeDbMinReadVersion = "0.4"` unchanged. An old file is read with the
legacy picklers, converted, and rewritten as 0.7 (existing "Upgrading database" path). Conversion:

- per file, a `FileRec` with the latest stamp any result recorded for it, no hash, `changed = 1`;
- per result, `built = 1` if every stamp it recorded equals its file's latest stamp (and every
  phony dependee finished no later than recorded), else `built = 0` (it reruns, as 4.0 would:
  it saw an older file);
- `ArtifactDep` (0.4, no stamp): `built = 0`; the first run after the upgrade is epoch 2.

A no-change run after the upgrade rebuilds nothing (M11).

### Not done (by the brief)

No directory tree, named roots, framing, salvage, carried records, re-rooting, root records,
read-only open. (The lock is now in, by the decision above.)

## 1c. Content data

- `FileRec.hash` is written as absent in phase 1; phase 3 fills it (outputs right after their rule,
  sources at their first moved stat) and changes the verdict of a moved stat from "changed" to
  "compare hashes". The format does not change between the phases.
- Static needs: one count, not strings (decision 2026-11-18, owner). Static `needs` are recorded as
  ordinary dependencies (`FileDep`/`TargetDep` by file id or phony name, `GetFiles` with the mask),
  and they finish before the body starts, so they are the leading entries of `Depends`. M9 (the
  header's static set changed) reads the first `StaticCount` entries. The recipe layer of phase 2/3
  must keep header dependencies ahead of the body's; phase 1 writes 0 and round-trips it.
- No need groups (decision 2026-11-18, owner): see Later.

## Public breaking changes (release notes "5.0.0 (unreleased)")

1. Path case: ordinal on every OS for `File`, `Target`, rules and filesets. On Linux `A.cs`/`a.cs`
   are now two files (they were one!); on Windows/macOS rule masks and filesets become
   case-sensitive. Two spellings in one run: error for targets, warning for sources.
2. `File`: `undefined` removed; `Name` means display relative to the current directory; `make`
   normalises (trailing separator dropped, `\` -> `/` internally on Windows); `File.display`,
   `File.comparer` added.
3. `Dependency`: `FileDep of File` and `TargetDep of Target` (no stamp); `ArtifactDep` and
   `ArtifactDep2` removed. `BuildResult.Built` is an epoch (`int64`), `StaticCount` added.
   `BuildLog.Database.Files` and `FileState` added.
4. `Storage.openDb root dbpath logger` (new `root` parameter); the database is exclusive
   (`.xake.lock`); `DatabaseApi` gains `Observe`. `Storage.resultPU` (public, used by tests) is
   replaced by `Storage.resultPickler root` (string paths) for tests, or removed - see question 2.
5. `ExecContext` gains `Spellings` (record shape test updated).
6. Database 0.7: an older Xake cannot read it (as at every bump); 0.4-0.6 load and upgrade.

## Line budget (estimates, net)

| File | Change | Lines |
|---|---|---|
| `Database.fs` | tags, path codec with `DirRec`, parameterised picklers, `FileRec` table and `Observe`, epoch, lock, 0.6 conversion, stop-on-failure, root param | **+200** |
| `Pickler.fs` | `varint` | +12 |
| `File.fsi` / `File.fs` | new representation, `display`, `comparer`, `undefined` gone | +5 / -5 |
| `Path.fs` | `collapse` made internal + trailing/UNC fix | +5 |
| `PatternMatch.fs`, `PatternScan.fs` | drop ignore-case | ~0 (edits) |
| `Types.fs` | ordinal `Target`, `Dependency` cases, `FileState`, `Built`, `StaticCount` | +10 |
| `DependencyAnalysis.fs`, `ExecCore.fs`, `ScriptFuncs.fs`, `WorkerPool.fs` | verdict by epochs (question 5), record ids, observation per run | +40 |
| `ExecTypes.fs`, `ExecCore.fs`, `ScriptFuncs.fs` | two-spellings check | +35 |
| `ScriptRunner.fs`, `Fingerprints.fs` | root to `openDb`, comparer | ~+3 |
| **core total** | | **about +300** |
| tests (`FileTests`, `StorageTests`, new `DatabaseFormatTests`, epoch scenarios) | | +350 |

No new source module. `Database.fs` is over the brief's +150: the `FileRec` table, the epoch, the
lock and the 0.6 conversion came with the 2026-11-18 decisions.

## Tests and measurement

- `File`: normalisation table (Unix and Windows rules; the Windows rules run on every OS through the
  internal `collapse windows` flag), `Equals`/`GetHashCode`/`CompareTo` consistency (property test
  with case variants), case variants distinct in `Set<File>`, `Set<Target>`, rule lookup, `WorkerPool`.
- Two spellings: target error naming both, source warning.
- Round trip of every record kind (`DirRec`, `PathRec`, `FileRec` with and without a hash, each
  `Dependency` case, `StaticCount`); paths outside the root stay absolute.
- Epochs: the example's runs 1, 2, 4, 5 (and 3, 6 rebuilding as 4.0 without hashes); dynamic `need`
  of a rebuilt target and of a phony does not rerun the dependent on the next run; a source saved
  during the build (after its observation) reruns its dependents on the next run; a second run on
  the same database waits and fails.
- 0.6 -> 0.7: a 0.6 file written by the 4.0 pickler (kept in the test as a fixture writer), then a
  no-change run builds nothing and the file is 0.7; a 0.6 result that saw an older stamp reruns.
- Relocation: build, copy the tree with `.xake` (times preserved) to another folder, build: nothing
  rebuilt.
- Size: synthetic database, 1000 results x 30 file deps, paths of 114 bytes mean, 0.6 vs 0.7 bytes;
  target >= 5x (expected far more: a dependency is a tag and an id).
- `[<Explicit>]` test: measure `XAKE_DB_SAMPLE` (a real 0.6 file): bytes before / after the upgrade.
- `dotnet build src/core -c Release` 0 warnings; `dotnet test src/tests -c Release`;
  `dotnet fsi build.fsx -- -- build test pack`.

## Questions for the owner

1. `Name` kept as display relative to the current directory (recommended) or removed?
2. `Storage.resultPU`: drop it from the public surface (tests use an internal) - recommended - or keep
   a string-path pickler public?
3. Filesets on Windows become case-sensitive with rules (as the file-type brief says for masks):
   confirm, since `*.CS` stops finding `a.cs` there.
4. `Fingerprints.fs` path comparisons switch to ordinal too (in core): confirm.
5. **Scope of phase 1 after the epoch decision.** Dependencies no longer carry stamps, so the
   engine cannot keep the 4.0 verdict on 0.7 records: phase 1 moves the verdict to epochs without
   hashing (a moved stat is a change, the 4.0 transitive rule stays), which gives 4.0's decisions.
   Phase 3 then adds hashing and cut-off without a format change. The brief said "stored, not used";
   recommended: accept the move in phase 1.

## Measurement on a real 0.6 database (2026-11-18)

A working database of 84.5 MB, read with the 0.6 pickler, sizes recomputed for 0.7 (estimate, no
file written):

| | |
|---|---|
| results | 5 758 (1 932 live: not compacted for a long time) |
| path references | 709 804 (`FileDep` 407 775) |
| unique paths / directories | 24 829 / 3 249 (28.6 refs per path, 7.6 files per directory) |
| outside the root | 13 324 of 24 829; mean path 116 B (relative 113 B), file name 37 B |
| paths in the 0.6 file | 93% (78.3 of 84.5 MB) |

| | as is (84.5 MB) | compacted (~36 MB) |
|---|---|---|
| flat table | 8.9 + 2.8 = 11.7 MB (7.2x) | 5.9 MB (6.1x) |
| `DirRec` + `PathRec (id, dirId, name)` | 8.9 + 0.2 + 1.0 = 10.2 MB (8.3x) | 4.4 MB (8.2x) |

These rows still count a stamp and a size per reference. With epochs a dependency is a tag and an id
(about 3 bytes instead of about 14): at least 4.5 MB less as is, plus a `FileRec` table of about
0.5 MB; estimate about 6 MB as is and about 3 MB compacted (not measured).

## Later (not in this phase)

- Need groups (the order and grouping of dynamic `need` calls). Shake keeps `depends` as a list of
  groups, one per `need`/`apply` call, and uses them to check a key: it brings the groups up to date
  in order and stops at the first changed one, because a later group may depend on an earlier one's
  result. That is speculation, which 5.0 does not do (option B), so the groups would be written and
  never read. If a speculative mode comes, it adds them with a database version bump.
- Hashing and the cut-off verdict (phase 3; the format is ready).
- `Fileset.BaseDir` stored relative to the root.
- Version comparison is a string compare (`"0.10" < "0.7"`): fine until 0.10.
