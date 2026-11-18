# Xake 5.0 phase 1: `File`, database 0.7, epochs

The specification of phase 1 as implemented (PR OlegZee/Xake#141). Phase 2 (rule header, `also`)
and phase 3 (hashing and content cut-off) come later; the database format of this phase is final
for them (phase 3 only fills the hash). Engine rules for later work are in
[implnotes.md](../../implnotes.md) ("Dependency storage"); the release notes are in
[release-notes.md](../../release-notes.md), "5.0.0".

## Why it is built this way

- **One epoch per run.** A target is up to date when every file or target it depends on changed no
  later than the run that built it. With one epoch for a whole run, everything built in run `r`
  has `Epoch = r` and everything seen changed in run `r` has `changed = r`: a target whose body
  `need`s a dependee rebuilt in the same run is up to date next time (`r <= r`). A per-event
  counter (the epoch taken when a body starts) would make it, and every dependent of a phony
  needed from a body, look out of date on every run. A source saved during a build after it was
  observed is seen by the next run (`changed = r + 1 > r`), so nothing is missed.
- **No hash history, no stamp per dependency.** A dependency stores only an id; the file's own
  record (`FileRec`) holds its last stat and the run it was last seen changed. What is given up
  against a hash in each dependency is one extra rebuild in a rare race (A -> B -> A seen by
  another target), never a missed one; phase 3's cut-off stops it.
- **No record length, always upgrade.** A new record kind or field is a new version, and an older
  database is rewritten on open. 0.4-0.6 are read by the self-contained module `LegacyRead`, to be
  removed in a later release (one call in `Database.fs` then becomes "no longer read").
- **Case policy.** A file's identity is its spelling (ordinal on every OS): targets and rules
  name files exactly. Searches of the disk (filesets, `Path.matches`) ignore case by default, as on
  Windows. A file named two ways in one run is reported. The direction for later (Linux strict
  mode on demand) is in the tracker.
- **Paths relative to the project root, the root never written.** A moved, copied or re-cloned
  project keeps its database.

## 1. `File`, names, case

**`File`** (`File.fsi`): `File = private F of string`, the normalised absolute path with `/` on
every OS; nothing cached, no disk state.

- `Path.collapse` (internal, also used for rule and mask matching): `\` -> `/` on Windows; `.` and
  `..` collapsed, a `..` above a root dropped; repeated separators removed; the root (`/`, `C:`)
  kept; a trailing separator dropped (except the root); a Windows drive letter upper-cased; a
  leading `//` (UNC) kept on Windows only; case otherwise kept; lexical (no symlinks).
- `File.make path`: a rooted path as is, a relative one against the current directory; on Windows
  `\foo` and `C:foo` through `GetFullPath` first; empty or whitespace fails.
- `Name`: Xake's name (the identity: what Xake stores, compares and shows). Every path string Xake
  hands out is in this form, `ProjectRoot` included (normalised once in `createContextCore`).
- `File.getSysFileName`: the OS form (`/` -> `\` on Windows, one to one), for tools.
  `File.getFileInfo`: a new `FileInfo` per call. `FullName`, `File.getFullName` and
  `getTargetFullName` are obsolete aliases of the OS form, to be removed in a later release.
- `File.display root f`: relative with `/` when `f` is under `root` (on a segment boundary), else
  absolute. `File.comparer` = `StringComparer.Ordinal`. `Equals`/`GetHashCode`/`CompareTo`
  ordinal on `Name`. `File.undefined` is removed.

**`Target`**: equality and order by `Name` (a file's `Name`, a phony's name), ordinal;
`Display root` is what the engine shows (`Started`, `Skipped`, change reasons, `EXPLAIN_RULE`,
errors, `--dump`). `WorkerPool.mapKey` is `Name`.

**Matching.** `PatternMatch` gets paths and anchors in Xake's form and has one flag, `ignoreCase`.
Rule matching is ordinal on every OS; a non-empty rule or mask root and a rule pattern's drive
letter are normalised as files are. `Path.matches` ignores case on every OS.

**Filesets** ignore case by default on every OS; `casesensitive` in `fileset { }`
(`FilesetOptions.CaseSensitive`, part of the fileset pickle) makes the search ordinal. Files found
keep their spelling on disk: a literal segment found by a probe takes its disk spelling, and an
ordinal search drops a hit spelt otherwise; on a Unix host an ignore-case search lists
directories instead of probing. `PatternScan` keeps its `windows` flag (the volume: probe or
listing, the directory comparer).

**Two spellings in one run:** `ExecContext.Spellings` (per run) maps the path upper-cased
(invariant) to the first `File` and whether it was a target. Checked for file targets that matched
a rule (when they start), for file dependencies (`FileDep`, recorded in `execOne`) and for the
files of a fileset result. Another spelling of the same key: both targets -> error `target
'out/Foo.dll' is also named 'out/foo.dll'`; otherwise a warning, once per pair. Phony names are not
checked. The case on disk is not checked.

## 2. Database 0.7

`XakeDbVersion = "0.7"`, `XakeDbMinReadVersion = "0.4"`, header as before. Records are appended,
each with a tag byte; the last one per key wins.

| Tag | Record | Fields |
|---|---|---|
| 0 | `DirRec` | dirId, root, dir |
| 1 | `PathRec` | fileId, dirId, name |
| 2 | `FileRec` | fileId, stamp (ticks), size + 1 (0 = unknown), hash (option of 8 bytes), changed |
| 3 | `Result` | epoch, built (ticks), targets, dependencies, steps, staticCount |

- Ids, sizes, epochs (`int`), counts and list lengths are varints; lists carry a varint length.
- **Paths:** `File.display ProjectRoot f` split at the last `/`. `DirRec.root` `1` = the project
  root, `0` = an absolute directory (the number is the room for more roots later); the root's value
  is never written. `dir` is `""` for a file directly in the root. A `DirRec`/`PathRec` goes out
  before the first record that uses it, in the same `Write`. An unknown id fails the read.
- **Path table** (`Persist.PathTable`): an immutable record (ids, files, directories) with
  `add : File -> PathTable -> PathTable * Rec list` and `load`; the agent keeps it in its loop
  state. A record that fails to pickle simply does not keep the new table.
- **Targets:** a file target is a file id, a phony its name. **Dependencies:** `FileDep fileId`;
  `TargetDep` as a file id or a phony name (two tags); `GetFiles` = the fileset and a list of file
  ids; `EnvVar`, `Var`, `SecretDep`, `AlwaysRerun` as in 0.6. Steps as before.
- **Writing:** one `Write` per call. A failed write stops appending until the database is closed
  (logged once); results stay in memory.
- **Compaction:** when the `Result` records exceed 5 x the live results; rewrites a fresh path
  table, the live results and the `FileRec`s of the files they refer to. Compaction and upgrade go
  through the `.bak` file; a failed rewrite keeps the backup (restored on the next open).
- **Lock:** `<db>.lock`, `FileShare.None`, taken first (before the `.bak` restore and
  `--resetdb`), held until close (engine mode: the engine's lifetime). A second opener waits up to
  30 s (logged once: "waiting for another Xake run on this database"), then fails with "another
  Xake run uses this database (.xake.lock)". Never deleted; a failed open releases it. `noopDb`
  takes none.
- **Versions:** newer -> "database written by a newer Xake (version X), recreating"; older than
  0.4 -> "database version X is no longer read, recreating"; another read failure -> recreated.
  A torn last record (end of file inside it) is cut off, the rest kept.
- **Upgrade 0.4-0.6** (`LegacyRead`): legacy picklers (paths as strings, joined with the root);
  per file a `FileRec` with the stamp of the result with the latest `Built` (`DateTime.MinValue`
  is no stamp), size unknown, `changed = 1`; per result `Epoch = 1` when every stamp it recorded is
  that one and every phony dependee finished no later than recorded, else 0; `ArtifactDep` (0.4)
  -> 0; `ArtifactDep`/`ArtifactDep2` -> `TargetDep`, `FileDep (f, stamp)` -> `FileDep f`; the
  secret scrub stays. A no-change run after the upgrade rebuilds nothing. On Linux an upgraded
  `GetFiles` is re-scanned ignoring case; with case variants on disk its dependent reruns once.
- `Storage.openDb root dbpath logger`. **Read API:** `DbRecord = Result | FileState`
  (`RequireQualifiedAccess`); `Storage.readRecords root dbpath` (file order) and
  `Storage.readDatabase root dbpath` (the last state, with the last epoch in `Database.Epoch`). No
  lock, `FileShare.ReadWrite ||| FileShare.Delete`; a torn last record ends the sequence, a missing
  or short file gives an empty one; an older version yields its converted records.

## 3. Epochs

**Types:**

```fsharp
type FileState = { Stamp: Timestamp; Size: int64; Hash: uint64 option; Changed: int }   // Size -1 = unknown
type Dependency =
    | FileDep of File | TargetDep of Target | GetFiles of Fileset * Filelist
    | EnvVar of string * string option | Var of string * string option | AlwaysRerun
    | SecretDep of name: string * source: SecretSource * digest: SecretDigest
type BuildResult = { Targets: Target list; Built: Timestamp; Epoch: int; Depends: Dependency list
                     Steps: StepInfo list; StaticCount: int }
type Database = { Status: Map<Target, BuildResult>; Files: Map<File, FileState>; Epoch: int }
```

- **Runs:** `ExecCore.beginRun` starts every run (each target group of the command line, each
  `demandTarget`): `Scheduler.newRun`, `NewRun` (the agent advances `Database.Epoch` and clears its
  observed and recorded sets), the verdict memo, a fresh `Spellings`, `ExecContext.RunEpoch`.
  `--dump` and `--dryrun` post `InMemory` and a `NewRun`: they append nothing.
- **Results** take `ctx.RunEpoch` (the run their task started in). A delegated result produced by
  its body takes the run's epoch; one returned without the body keeps the local result's epoch
  (the run's if none); on `Store` the dependency files without a state are observed.
- **Observation** (`Observe of File * ObserveMode`, stamped with the agent's last `NewRun` epoch):
  `Verdict` at most once per run, then cached; `Recording` (a body records a `FileDep`) stats
  again, and a moved stat is a change unless another body recorded the file in this run; `Output`
  (every file target of a rule, right after it ran) stats again. No `FileRec` -> new, `changed` =
  the epoch. Stamp equal within 10 ms and size equal or unknown -> unchanged (an unknown size is
  filled in, `changed` kept). Otherwise `changed` = the epoch, new stamp and size. A missing file
  changes no record. No hash is computed in phase 1. `getFiles` does not observe its members.
- **Verdict** for a stored result of epoch `e` (`DependencyAnalysis.getDepState`): `FileDep f`:
  missing or `changed > e` -> files changed. `TargetDep (FileTarget f)`: missing ->
  `DependsMissingTarget`; `changed > e` -> `Depends`; else the dependee's own change reasons
  non-empty -> `Depends` (4.0's transitive rule, kept until phase 3). `TargetDep (PhonyAction p)`:
  no result or `Epoch > e` -> `Depends`; else the transitive rule. `GetFiles` compares the list;
  the rest as in 4.0, and "No dependencies", "Not built yet", "target file does not exist" stay.
- `StaticCount`: the leading `Depends` entries from the rule header; 0 in phase 1.
- `--dump` shows each result's epoch and, for a file dependency, its stamp, size and `changed`.

## 4. Measurements

A real 0.6 database of 84.5 MB (5 758 results, 1 932 live; 24 829 unique paths in 3 249
directories, 13 324 of them outside the project root; mean path 116 B; paths 93% of the file):

| | bytes |
|---|---|
| 0.6 file as is | 88 564 308 |
| 0.7 estimate as is (all records) | 5.00 MB (17.7x) |
| 0.7 after the upgrade (live records only, measured) | 3 069 697 (28.9x) |
| of which path table (`DirRec` + `PathRec` + `FileRec`) | about 1.7 MB |

What grows per run is a rebuilt target's `Result`: about 3 bytes per file dependency. Synthetic
test (1000 results x 30 file dependencies, mean path 118 B): 0.6 3 960 017 B, 0.7 196 590 B
(20.1x). The upgrade of the real file takes about 3 s.
