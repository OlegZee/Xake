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
- The database (`Storage.Db`, docs/features/idle-perf) is an agent and a writer. The agent keeps
  the records in memory and decides nothing: it accepts `Apply` (file records, each written only
  if the record is still the one the caller decided against: compare-and-set per file; replies
  the conflicts), `Store` (a result; replies once it is in memory), `NewRun`, `InMemory` and
  `Close`. After every message it publishes a snapshot (`Db.Snapshot`: `Status`, `Files`,
  `Epoch`), read without a message (`Storage.tryGetResult`). It hands the records to the writer
  in its order and does not wait for it.
- The writer owns the path table and the stream. Each batch, with its new `DirRec`/`PathRec`, is
  pickled into one buffer and written with one `Write`. The path table is an immutable value in
  the writer's state: a record that fails to pickle writes nothing and leaves the table as it
  was; a failed write stops appending until the database is closed (logged once), results stay
  in memory. `Close` drains the writer before the stream and the lock are released.
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
  `ExecContext.RunEpoch`, with a fresh verdict memo (`Verdicts`), spelling table and
  `ExecContext.RunFiles` (the files observed and recorded in the run).
- One epoch per run, not per event: everything built in run `r` has `Epoch = r` and everything
  seen changed in run `r` has `changed = r`, so a target that `need`s a dependee rebuilt in the
  same run (a dynamic need) is up to date on the next run (`r <= r`). A per-event counter would
  make it look out of date on every run.
- Runs may overlap in engine mode (`Demand` of different names): an observation is stamped with
  the newest epoch handed out, a result with the epoch its task started in. A delegated result
  that did not run its body keeps the local result's epoch; a foreign epoch is never stored.
- A file is observed through `Storage.observeFiles` (`DependencyAnalysis.observeMany`), a group
  of files at a time, in a mode: by a verdict once per run, then answered from the record; when a
  body records it as a `FileDep` it is statted again, and a moved stat counts as a change unless
  another body already recorded it in this run; a rule's outputs (every output of a multi-output
  rule) are statted again right after the rule. The sequence, on the caller's thread:
  1. the stats (existence, write time, length), taken by the caller;
  2. the rule `Storage.FileRule.decide` against the snapshot's record (pure: no I/O, no state),
     with the run's facts from `RunFiles`;
  3. the hashes the rule asks for, through the hasher;
  4. every write in one `Apply`;
  5. a conflict decided again on a fresh stat (at most 3 times), so an older stat never moves a
     record back.
  No record gives `changed` = the run's epoch and no hash; an unknown size is filled in with
  `changed` kept. A verdict's later observation of a file in the run waits for the first one, then
  answers from the record if the file exists now (existence is checked per dependency). `--dump`
  and `--dryrun` observe in memory only and append nothing.
- **Observing with `since`.** Each file's reply is `Absent`, `Unchanged state` or
  `Changed state` (content changed after the run `since`, `FileRule.classify`). The verdict passes
  the dependent's epoch, `observeOutputs` passes `RunEpoch - 1` (changed in this run); calls that
  only update the record use `DependencyAnalysis.observe` (`since` = max).
- **Hashing (docs/content-cutoff.md).** The rule decides, the caller hashes: a moved stat (or an
  output) that needs a hash is hashed through `Storage.Hasher`, a mailbox that computes one hash
  per (file, stat) while it runs (every other asker gets the same result), at most one per core
  at once, off the agent. The result is `Hashed (stamp, size, hash)` (stat, SHA-256, stat; first
  8 bytes) or `Unreadable stat` (the file could not be read, or its stat moved while it was
  hashed: its content was not stable, so it is a change without a hash), and
  `FileRule.decideHashed` decides against the same record.
  Moved with a hash: equal keeps `changed`, else `changed` = run. Moved without a hash: `changed` =
  run and the new hash stored. `Output true` hashes every output after its rule, even with an equal
  stat (coarse time stamps); `Output false` (`noContentCutoff`) stores no hash and sets `changed` =
  run; `PreHash` hashes an output with no hash and an equal stat before its rule runs. With
  `--content-cutoff=off` (`ExecOptions.ContentCutoff`, an engine option, so overlapping engine runs
  agree) nothing is hashed and a moved stat clears the hash.
- Verdict for a target with a stored result of epoch `e`: `FileDep f` is stale when `f` is missing
  or `changed > e`. `TargetDep t`: the worker pool is asked (`Query`) whether `t` finished in this
  run, is running, or has not started. Finished: compared exactly (`changed > e` for a file,
  `Epoch > e` for a phony). Running: the target's own verdict waits for it, then compares; a
  waits-for map (`RunCounters.Waits`, `WaitGraph`, set around the verdict wait and `execNeed`) stops a wait that
  would close a cycle (the dependee counts as changed). Not started: `changed`/`Epoch > e`, or the
  dependee's own no-build verdict non-empty (the 4.0 transitive rule, kept only here). The no-build
  verdict (`DependencyAnalysis.Verdicts`, memoised per run, async) never waits for a running
  dependee: inside it a running dependee is a change. It is decided in two phases: the target's
  files in one observation and the dependees' verdicts it needs, in parallel; then the rule
  (`getDepState`, pure). A verdict waiting for a dependee's verdict still in progress registers the
  edge; an edge that would close a cycle is refused and the dependee counts as changed. It serves
  the recursion, the fast path (`ExecContext.UpToDate`, async) and the progress estimate. `GetFiles` compares only the file list;
  `EnvVar`, `Var`, `SecretDep` and `AlwaysRerun` are as before.
- Order in a task (`execOne.run`): static `needs`, the verdict, the pre-hash, the locks, the body,
  the outputs, the `Store` (awaited: a dependent that sees the task finished sees its result).
  The plain files of a `need` list are recorded in one observation, next to the rule targets. The fast path: a target that is not running or finished in this run and whose
  no-build verdict is empty is skipped without a task. `RunCounters` (per run) feed `RunSummary`.
- `StaticCount`, the number of leading dependencies from the rule header. `execOne` builds the
  header's `needs` (before the verdict), runs the body on a fresh result and stores `headerDeps @ bodyDeps`, so the
  body's recording order (`need` appends; `getVar`, `getEnv`, `getFiles`, `alwaysRerun` prepend;
  `setResult` and delegated executors replace) cannot move them. The verdict (`getChangeReasons`)
  gets the rule's current outputs and static set from `ExecCore` (`current`): a missing current
  output, or a static set (file/target names, filesets) differing from the keys of the first
  `StaticCount` dependencies, makes the target out of date, and dependents see it through the memo.

### Step times

`BuildResult.Steps` holds `StepInfo { Name; Start; OwnTime; WaitTime }`; a task has one step,
`"all"`, started when the task starts (before the header's `needs`). `WaitTime` is the time spent
waiting: the header's `needs`, every `need` in the body, and every resource acquire (`requires`,
`requiring`, `withResource`; the span includes getting the CPU slot back). `OwnTime` is the rest of
the step: `(end - Start) - WaitTime`. So the sum of `OwnTime` over a run against its wall time is
the build's real parallelism. The pool's queue before the task and the verdict are outside the
step. The progress estimate takes `OwnTime` of the last run as a target's duration, and so does the critical path.

### Progress

Each target group of the command line is a run with its own progress (`Progress.openProgress`),
estimated when the group starts: an earlier group can change what a later one finds (`clean`), so
later groups are not forecast. With more than one group the status line starts with `[i/n names]`
and each finished group logs `group 'names' done in N s`. A task that is skipped after its verdict
counts as finished in the estimate. Engine mode shows no progress.

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

### Critical path

When more tasks are ready than slots, the one with the largest **key** starts first: its own
expected time plus the largest key among the targets that depend on it in this run. At the start
of each run (target group, demand) the engine decides, in parallel, the verdicts of the progress
estimate's graph (`Verdicts.Warm`, whatever the schedule), then walks it (`Verdicts.Estimate`:
`OwnTime` of the last result, and the dependees that will rebuild) from the run's targets and
hands the keys to the scheduler
(`DependencyAnalysis.criticalPath`, `Scheduler.newRun`). A target without a record, or new in this
run, has key 0; so without history the order is the order of request.

The key orders three queues: the pool's (`WorkerPool.Gate`: a slot goes to the waiter with the
largest key, equal keys in arrival order; a task taking its slot back after a yield waits there by
its own key), a resource's waiters (head-of-line: the best waiter must fit before anyone behind it
is granted, so a large request is not starved), and `need [..]` (sorted by key before its children
are requested). A queued task is still `NotStarted` for `Scheduler.query`: the verdict does not
see it as running. `--schedule=as-requested` (`ExecOptions.Schedule = AsRequested`) makes every
key 0: the queues are FIFO, as before 5.0. Only the order changes, never what runs.

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
