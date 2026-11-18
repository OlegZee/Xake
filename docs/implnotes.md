# Implementation notes

This file keeps high level technical notes about Xake internals.

## Build model

Xake scripts define rules for targets. During execution, the engine records dependencies and stores build state in `.xake`.

A target is rebuilt when at least one condition is true:

- output does not exist
- dependency files changed
- dependency target was rebuilt, by this run or an earlier one (its change stamp moved: see below)
- variable or environment dependency changed
- recipe requests rerun by `alwaysRerun`

## Dependency storage

The build database stores:

- target graph
- requested dependencies
- file list snapshots from filesets
- variable and environment reads

This enables incremental rebuild and `--dryrun` estimation.

A dependency on a file without a rule is stored as `FileDep file`, on a rule's output or a phony
target as `TargetDep target`; neither carries a stamp. Staleness is decided by epochs (below).

### Database 0.7

The file starts with a version header (`BuildLog.XakeDbVersion`, `0.7`; `XakeDbMinReadVersion` is
`0.4`). After it come tagged, append-only records; the last record per key wins.

| Tag | Record | Fields |
|---|---|---|
| 0 | `DirRec` | dirId, root, dir |
| 1 | `PathRec` | fileId, dirId, name |
| 2 | `FileRec` | fileId, stamp (ticks), size, hash (option of 8 bytes), changed (epoch) |
| 3 | `Result` | epoch, built (ticks), targets, dependencies, steps, staticCount |

- Ids, sizes, epochs, counts and list lengths are 7-bit varints; lists in 0.7 records carry a varint
  length. Size is written as `size + 1`, `0` meaning unknown.
- The path table is relative to the project root: `File.display ProjectRoot f` is split at the last
  `/` into a directory and a name. `DirRec.root` is `0` for an absolute directory and `1` for the
  project root; the root's value is never written, so a moved or copied project keeps its database.
  `dir` is empty for a file directly in the root. `DirRec` and `PathRec` are written once, before
  the first record that uses the id. An unknown id makes the read fail.
- A file target is stored as a file id, a phony target by name. `FileDep` holds a file id;
  `TargetDep` has a file form and a phony form; `GetFiles` holds the fileset (with
  `CaseSensitive`) and file ids.
- Each record, with its new `DirRec`/`PathRec`, is pickled into one buffer and written with one
  `Write`. The path table is an immutable value in the database agent's state: a record that fails
  to pickle writes nothing and leaves the table as it was; a failed write stops appending until
  the database is closed (logged once), results stay in memory.
- Nothing is rewritten in place. Compaction runs when the `Result` records exceed 5 x the live
  results: it rewrites a fresh path table, the live results and the `FileRec`s they refer to.
  Compaction and the upgrade go through the `.bak` file; when the rewrite fails the backup is kept
  and the next open restores it.
- The lock, `<db>.lock` (`.xake.lock`), is taken first, before the `.bak` restore and before
  `--resetdb` deletes anything, and is held until the database is closed (engine mode: the
  engine's lifetime). A second opener waits up to 30 s, then fails with "another Xake run uses
  this database (.xake.lock)". The lock file is never deleted (deleting it would let two runs hold
  two different files); a failed open releases it. `noopDb` takes no lock. A recipe that starts
  Xake on the same project root waits and fails.
- A newer version is recreated ("database written by a newer Xake (version X), recreating"), as is
  one older than `XakeDbMinReadVersion` ("database version X is no longer read, recreating") or
  one that fails to read. An end of file inside the last record (a killed process) cuts the file
  back to the end of the last whole record and keeps everything before it.
- Databases 0.4-0.6 are read by the module `LegacyRead` (its own picklers and the conversion),
  converted and rewritten as 0.7. Per file a `FileRec` with the stamp recorded by the result with
  the latest `Built` (`DateTime.MinValue` is no stamp), unknown size, `changed = 1`. A result gets
  `Epoch = 1` when every stamp it recorded is that one and every phony dependee finished no later
  than recorded, else 0 (it reruns once). A no-change run after the upgrade rebuilds nothing.
  `LegacyRead` is meant to be removed in a later release; its one call in `Database.fs` then
  becomes "no longer read, recreating".
- `Storage.openDb root dbpath logger` takes the project root. The public read API is
  `Storage.readRecords root dbpath` (every `DbRecord`, `Result` or `FileState`, in file order) and
  `Storage.readDatabase root dbpath` (the last result per target, the last state per file, the
  last epoch in `Database.Epoch`). They take no lock and open with `FileShare.ReadWrite |||
  FileShare.Delete`, so the database of a running build can be read; a torn last record ends the
  sequence, a missing or short file (a compaction in progress) gives an empty one.

### Epochs

- The epoch is the run number. On load the database's `Epoch` is the largest `Result.epoch` or
  `FileRec.changed` in the file (it is not stored on its own). Every run starts in
  `ExecCore.beginRun` (each target group of the command line, each `demandTarget`: setup,
  teardown, engine `Demand`), which takes the next epoch from the agent (`NewRun`) and keeps it in
  `ExecContext.RunEpoch`, with a fresh verdict memo and spelling table.
- One epoch per run, not per event: everything built in run `r` has `Epoch = r` and everything
  seen changed in run `r` has `changed = r`, so a target that `need`s a dependee rebuilt in the
  same run (a dynamic need) is up to date on the next run (`r <= r`). A per-event counter would
  make it look out of date on every run.
- Runs may overlap in engine mode (`Demand` of different names): an observation is stamped with
  the newest epoch handed out, a result with the epoch its task started in. A delegated result
  that did not run its body keeps the local result's epoch; a foreign epoch is never stored.
- A file is observed through the database agent (`Observe` with a mode): by a verdict at most once
  per run (then cached); when a body records it as a `FileDep` it is statted again, and a moved
  stat counts as a change unless another body already recorded it in this run; a rule's outputs
  (every output of a multi-output rule) are statted again right after the rule. The stat
  (existence, write time, length) is compared with the `FileRec`: no record, or a moved stamp or
  size, gives `changed` = the run's epoch and a new record; an unknown size is filled in with
  `changed` kept. There is no hashing yet (`Hash = None`). `--dump` and `--dryrun` observe in
  memory only and append nothing.
- Verdict for a target with a stored result of epoch `e`: `FileDep f` is stale when `f` is missing
  or `changed > e`; `TargetDep (FileTarget f)` when `f` is missing, `changed > e`, or the dependee
  itself has change reasons (the 4.0 transitive rule); `TargetDep (PhonyAction p)` when it has no
  result, its `Epoch > e`, or the same transitive rule. `GetFiles` compares only the file list;
  `EnvVar`, `Var`, `SecretDep` and `AlwaysRerun` are as before.
- `StaticCount`, the number of leading dependencies from the rule header. `execOne` builds the
  header's `needs`, runs the body on a fresh result and stores `headerDeps @ bodyDeps`, so the
  body's recording order (`need` appends; `getVar`, `getEnv`, `getFiles`, `alwaysRerun` prepend;
  `setResult` and delegated executors replace) cannot move them. The verdict (`getChangeReasons`)
  gets the rule's current outputs and static set from `ExecCore` (`current`): a missing current
  output, or a static set (file/target names, filesets) differing from the keys of the first
  `StaticCount` dependencies, makes the target out of date, and dependents see it through the memo.

### Case and names

`File.Name` is Xake's name of a file: the normalised absolute path with `/` (`Path.collapse`: `.`
and `..` resolved, repeated separators removed, a Windows drive letter upper-cased, a UNC root kept
on Windows). Every path string Xake hands out is in this form, `ProjectRoot` included;
`File.getSysFileName` gives the OS form for tools. Rule and mask roots, and a rule pattern's drive
letter, are normalised the same way, so the matcher compares Xake's forms only.

`File`, `Target` and rule matching compare ordinally on every OS. Filesets and `Path.matches`
ignore case by default; `FilesetOptions.CaseSensitive` (`casesensitive`) makes a fileset's search
ordinal, and files found keep their spelling on disk. Per run `ExecContext.Spellings` maps the
upper-cased path to its first spelling: a second spelling of one file is an error when both are
targets that matched a rule and a warning (once per pair) otherwise.

## Rules and scheduling

- `<==` demands dependencies in parallel
- `<<<` demands dependencies in sequence
- worker count defaults to CPU core count and can be overridden by `-t`

Parallel execution still respects dependency order.

## Filesets

Filesets support Ant style masks, recursive `**`, and named match groups in rule patterns.

Common behavior:

- path separators `/` and `\\` are accepted
- file list result is tracked as dependency input
- change in resolved file list can trigger rebuild

## Recipe behavior

`recipe` computation supports standard control flow and async style composition with `do!` and `let!`.

Notes:

- `do!` works in `with` blocks
- `do!` does not work in `finally` blocks due to F# computation expression limits
- `WhenError`, `FailWhen`, and `CheckErrorLevel` are available for explicit error flow

## Logging

Xake supports console and file logging with separate verbosity levels.

Log levels:

- `Silent`
- `Quiet`
- `Normal`
- `Loud`
- `Chatty`
- `Diag`

### Run banner

Every run (including `--dump` and `--dryrun`) logs one compact block at `Info`, so it is in the
default file log (`Chatty`) and not on the default console (`Normal`): the Xake version, mode,
effective targets, project root, database file, threads, rule matching, console and file log,
setup and teardown targets, and the script variables as `NAME=value`. It is written after the
secret variables are registered (`checkSecretVars`) and before setup; values of secret variables
are shown as `***`. `-d` and `var` values end up in one list, so every variable is labelled
`(script/cli)`. The `Options: %A` dump at `Debug` (`-fll Diag`) remains as the verbose form; it
does not include variables.

## Runtime and platform

The current repository targets modern `dotnet` workflow with .NET SDK 8.0+ (see `global.json`).

The build script itself always runs on modern .NET. Full framework and Mono are compilation
*targets* handled by the `Xake.Dotnet` tasks; see [dotnet-build.md](https://github.com/OlegZee/Xake.Dotnet/blob/dev/docs/dotnet-build.md) (OlegZee/Xake.Dotnet) for how
that toolchain is discovered and selected.

## Teardown behavior

Teardown rules are defined in script and run during shutdown when engine mode calls `StopAsync`.

The `setup` op is the counterpart: its targets run sequentially before the build (CLI mode, after the secret check; engine mode, once at start), a failure skips the build, runs teardown and fails the run (exit code 2). Several `setup` ops accumulate, a later `teardown` replaces. Setup does not run for `--dump`/`--dryrun`.

In CLI script mode the `teardown` targets run automatically after the build, whether it succeeded or failed (`ScriptRunner.fs`). A teardown failure is logged; it fails the run (exit code 2) only when the build itself succeeded, otherwise the original build error is kept.
