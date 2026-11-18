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
- Each record, with its new `DirRec`/`PathRec`, is pickled into a buffer and written with one
  `Write`. A failed write stops further appends for the run (logged once); results stay in memory.
- Nothing is rewritten in place. Compaction runs when the `Result` records exceed 5 x the live
  results: it rewrites a fresh path table, the live results and the `FileRec`s they reference.
  Compaction and the upgrade go through the `.bak` file.
- Opening takes a lock, `<db>.lock` (`.xake.lock`), held until the database is closed. A second
  opener waits up to 30 s, then fails with "another Xake run uses this database (.xake.lock)".
  `noopDb` takes no lock.
- A newer version is recreated ("database written by a newer Xake (version X), recreating"), as is
  one older than `XakeDbMinReadVersion` ("database version X is no longer read, recreating") or
  one that fails to read. An end of file inside the last record (a killed process) cuts the file
  back to the end of the previous record and keeps everything before it.
- Upgrade of 0.4-0.6: read with the legacy pickler, converted, rewritten as 0.7. Per file a
  `FileRec` with the latest stamp any result recorded, unknown size, no hash, `changed = 1`. A
  result gets `Epoch = 1` if every file stamp it recorded equals that file's latest stamp (10 ms
  tolerance) and every phony dependee finished no later than the stamp recorded for it, else
  `Epoch = 0`; a 0.4 `ArtifactDep` gives `Epoch = 0`. `Built` keeps its time. `ArtifactDep` and
  `ArtifactDep2` become `TargetDep`; the secret scrub stays. A no-change run after the upgrade
  rebuilds nothing.
- `Storage.openDb root dbpath logger` takes the project root. The public read API is
  `Storage.readRecords root dbpath` (every `DbRecord`, `Result` or `FileState`, in file order) and
  `Storage.readDatabase root dbpath` (the last state and the last epoch). They take no lock, read
  with `FileShare.ReadWrite`, and a torn last record ends the sequence.

### Epochs

- The epoch is the run number: the largest `Result.epoch` or `FileRec.changed` in the file + 1 on
  load (1 for an empty file); in engine mode each run takes the next. It is not stored on its own.
  A result built in run `r` has `Epoch = r`.
- A file is observed through the database agent at most once per run, except a rule's output, which
  is observed again right after its rule finishes. The stat (existence, write time, length) is
  compared with the `FileRec`: no record, or a moved stamp or size, gives `changed = r` and a new
  record; an unknown size is filled in with `changed` kept. There is no hashing yet (`Hash = None`).
- Verdict for a target with a stored result of epoch `e`: `FileDep f` is stale when `f` is missing
  or `changed > e`; `TargetDep (FileTarget f)` when `f` is missing, `changed > e`, or the dependee
  itself has change reasons (the 4.0 transitive rule); `TargetDep (PhonyAction p)` when it has no
  result, its `Epoch > e`, or the same transitive rule. `GetFiles`, `EnvVar`, `Var`, `SecretDep`
  and `AlwaysRerun` are as before.
- `StaticCount`, the number of leading dependencies from the rule header, is 0 in phase 1 and
  round-trips.

### Case

`File`, `Target` and rule matching compare ordinally on every OS. Filesets ignore case by default;
`FilesetOptions.CaseSensitive` (`casesensitive`) makes the search ordinal, and files found keep their
spelling on disk. Per run `ExecContext.Spellings` maps the upper-cased path to its first spelling:
a second spelling of one file is an error when both are targets and a warning (once per pair)
otherwise.

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
