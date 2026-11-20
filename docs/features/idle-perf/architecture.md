# How the engine observes files and decides (as of v5.0.0-preview.3)

This describes the system as it is at `v5.0.0-preview.3` (407765f, feature/idle-perf), with the
file layout of the architecture review (feature/arch-review, topic 1), not the history of the
change. The history and the measurements are in [brief.md](brief.md),
[design.md](design.md) and [cold-hot.md](cold-hot.md). Section 7 lists candidates for review.

## 1. Layers and who owns what

```
 L7  ExecCore            tasks, need, outputs, store       (ExecCore.fs)
 L6  Verdicts            verdict + memo per run, Warm      (DependencyAnalysis.fs)
 L5  observeFiles        stat -> rule -> hash -> Apply     (FileTracker.fs)
 L4  RunFiles            per-run sets                      (FileTracker.fs, in ExecContext)
 L3  Hasher              one hash per (file, stat)         (FileProbe.fs, mailbox, in EngineState)
 L2  Db: agent + writer  records, snapshot, .xake          (Database.fs, 2 mailboxes)
     format              header, picklers, path table      (DatabaseFormat.fs, internal Persist)
 L1  FileRule            pure decision                     (FileTracker.fs, internal)
 L0  FileStat, Content   stat, SHA-256                     (FileProbe.fs)
     Scheduler/WorkerPool  tasks once per run, slots by key (WorkerPool.fs, mailbox)
```

The files and their modules, in compile order:

| File | Module | Holds | Uses |
|---|---|---|---|
| `DatabaseFormat.fs` | `BuildLog` | the format version, `Database` (the records in memory), `DbRecord` | `Types` |
| | `Persist` (internal) | the 0.7 header and picklers, the path table, `read`, `encode`, `toDatabase` | `BuildLog`, `Pickler`, `LegacyRead` |
| `Database.fs` | `Storage` | `DatabaseApi`, the file lifecycle (lock, backup, upgrade, compaction), the agent, the writer, `Db`, `openDb`/`noopDb`/`cleanupDb`, `readRecords`/`readDatabase`, `tryGetResult`, `storeResult` | `Persist` |
| `FileProbe.fs` | `FileProbe` | `FileStat`, `HashResult`, `Content.hash`, `Hasher`: I/O only | `Types` |
| `FileTracker.fs` | `FileTracker` | `ObserveMode`, `Observation`, `FileRule` (internal, pure), `RunFiles`, `ObserveContext`, `observeFiles` | `Storage`, `FileProbe` |

`Database.fs` uses nothing of `FileProbe` or `FileTracker`; they use `Storage` only through
`Db.Snapshot` and `Apply`. `makeResult` and `Step` are in `Types.fs`, the `Agent` alias in
`Prelude.fs`.

A layer calls only layers below it. The scheduler sits beside them: L6 asks it whether a dependee
is running (`Scheduler.query`), and L7 submits tasks to it.

| Part | Owns | Does not |
|---|---|---|
| `FileStat.take`, `Content.hash` (L0) | nothing; read the file system | keep state |
| `FileRule` (L1) | the decision for one observation: `decide known stat mode ctx`, `decideHashed`, `classify since` | I/O, mutation |
| Database agent (L2) | the records in memory (`Status`, `Files`, `Epoch`), the snapshot | read project files, decide, wait |
| Writer (L2) | the path table, the `.xake` stream, encoding, failure counts | touch the records in memory |
| `Hasher` (L3) | the hashes being computed, keyed by (file, stat) | know records, epochs, runs |
| `RunFiles` (L4) | per run: files observed (with the first observation's completion), files recorded by a body | hold records |
| `observeFiles` (L5) | the sequence for a group of files | keep state of its own |
| `Verdicts` (L6) | per run: each target's no-build verdict (memo), the wait-for edges between verdicts, the warm walk | stat, hash, post to the agent |
| `ExecCore` (L7) | tasks, `need`, the fast path, the task's own verdict, outputs, `Store`, the A3 observation | decide file changes |
| Scheduler / WorkerPool | one task per target per run, the slot gate ordered by critical-path key, the running/finished maps | decide verdicts |

### The database (`Storage.Db`)

- **Agent messages.**
  - `Apply (writes)`: file records, each written only if the record is still the one the caller
    decided against (compare-and-set per file). Replies with the conflicts, each with its current
    record.
  - `Store (result)`: replies once the result is in memory.
  - `NewRun`: increments the epoch and replies with it.
  - `InMemory`: nothing more is written to disk.
  - `Close` / `CloseWait`.
- **The snapshot.** After every message the agent publishes `Db.Snapshot` (`BuildLog.Database`:
  immutable `Map`s behind a volatile field). Readers never post: `Storage.tryGetResult`, a file's
  record, the epoch.
- **The writer.** The agent hands its accepted records to the writer, in its own order, and does
  not wait for it. The writer encodes each batch with the path table and appends it with one
  `Write` and one `Flush`. `Close` drains the writer before the stream and the lock are released.
- **Format.** The format is unchanged from preview.2: database 0.7, the same picklers. Each
  version opens the other's file without a rebuild (checked on the bench).

### The observation sequence (`FileTracker.observeFiles oc mode since files`)

1. **Claims.** Each distinct file is claimed in `RunFiles`, except in `PreHash` mode.
   - The first observer of the file in the run holds a completion and sets it when done.
   - A later `Verdict` observation waits on that completion.
   - Later `Recording`/`Output` observations decide again.
   - `Recording` also tests and sets "recorded before" (A1).
2. **Stats.** A full stat (`FileStat.take`) is taken for every file this call decides, on the
   calling thread.
3. **Decisions.** `FileRule.decide` runs against the snapshot's record and the snapshot's epoch.
4. **Hashes.** `NeedHash` goes through the hasher, the hashes of one call in parallel, then
   `decideHashed`.
5. **Writes.** Every `Write` goes in one `Apply`. A conflict is decided again on a fresh stat
   against the current record, up to 3 rounds; after that the current record is the answer.
6. **Completion.** The claims are completed (`finally`).
7. **Later verdict observations.** Each waits for the first observation, then answers from the
   current record if `File.exists` (existence is checked per dependency).
8. **Reply.** `classify since` per file, in the order given.

### Verdicts (`DependencyAnalysis.Verdicts`, one per run)

- **`Of t`** is the memoised no-build verdict (`Lazy<Task>` per target). It is decided in two
  phases:
  1. One `observeMany Verdict epoch` over the target's files, then, in parallel, the verdicts of
     the dependees the transitive rule needs. These are the dependees whose file is unchanged, or
     phony dependees not newer than the target, that have not started.
  2. `getDepState`: the rule over what phase 1 gathered. It is pure, apart from fileset scans.
- **Waiting on another verdict.** Before a verdict awaits another target's verdict that is still
  in progress, it registers the edge in a private wait-for map. An edge that would close a cycle
  is refused, and that dependee counts as changed.
- **`Fresh t`** is the task's own verdict, decided again after the task waited for its running
  dependees. Its dependees still come through the memo.
- **`Warm goals`** decides, in parallel, the verdicts of the graph the estimate visits: each
  target, then the dependees named in its reasons.
- **`Ready t` / `Estimate t`** read synchronously what `Warm` decided. Durations are the last
  result's `OwnTime` from the snapshot.

## 2. Agents, mailboxes and threads

| Mailbox | Created | Serialises | Sync callers |
|---|---|---|---|
| Database agent | per `Db` (one per engine) | record updates and snapshot publishing | top level only (`CloseWait`, `NewRun` in `--dump` and `--dryrun`) |
| Writer | per `Db` | encoding and appends | the agent awaits `Drain` at close |
| Hasher | per engine (`EngineState.Hasher`) | the waiters per (file, stat); hashing itself runs outside, at most one per core (semaphore) | none |
| WorkerPool | per scheduler | the queued/running/finished maps (a snapshot for `query`) | none |
| Resource | per resource | the resource's waiters | none |
| Progress | per run | the estimate refreshes | none |
| File and console loggers | per log | log lines | flush at the end |

- **Threads.** Every mailbox is an F# `MailboxProcessor` on the .NET thread pool; nothing has a
  thread of its own. The engine's async code (tasks, verdicts, `observeFiles`) runs on the pool
  too. The main thread waits at the top (`runBuild |> Async.RunSynchronously`).
- **Synchronous waits** are left only at the top level: `--dump` (`dumpDeps`), the `--dryrun`
  printing (after a parallel `Warm`), close, and `ProcessExec`'s synchronous helper. None is on
  the dependency path.

## 3. The path of one file

### A run where nothing changed

```
beginRun:  NewRun -> agent (epoch+1, publish)          new RunFiles, new Verdicts
planRun:   Verdicts.Warm goals                          (parallel over targets)
  Of t:    snapshot.Status[t]                           no message
           observeFiles Verdict e [t's files]
             first claim of f:  stat (thread) -> decide: equal stat -> Keep  (no Apply)
             later claims of f: wait first -> File.exists -> snapshot.Files[f]
           dependees' Of (memo, parallel)  -> getDepState -> []
execParallel goals -> prepareOne t -> fast path: UpToDate t = Of t (done) -> Skipped
           observeFiles Verdict [t's output] (seen: File.exists + snapshot)
Store of the phony goals -> agent -> writer
```

On the bench, the agent receives one `NewRun` and the `Store`s of the 7 phony targets. Each of
181,764 dependencies costs one `File.exists` and one `Map` lookup, spread over the pool by
`Warm`.

### A full build (empty database)

```
Warm:      Of t -> no result -> "Not built yet" (no observation)
execOne t -> pool (slot by key) -> run:
  needs, Fresh t (rebuild), preHash: observeFiles PreHash [out] (missing -> Keep)
  body: need [150 files] -> execParallel -> prepareOne each (rule? exists?)
        plain files -> one observeFiles Recording [150]:
          claim, recordedBefore, stat -> decide: no record -> Write (changed = epoch)
          Apply [150] -> agent: CAS; a file another task just wrote conflicts
                         -> fresh stat -> decide vs new record: equal -> Keep
          agent publishes, replies, Append -> writer: encode, Write, Flush
  output written; observeFiles (Output true) [out]: NeedHash -> Hasher -> decideHashed
          -> Apply -> writer
  storeResult: Store (awaited) -> agent; A3: deps with no record in the snapshot
          -> observeFiles Verdict (no hashing) [those]  (none here)
  pool Done -> dependents: observeFiles Verdict [out] (after the dependee)
Close: agent -> Drain -> writer flushes the rest, closes, releases the lock
```

## 4. Concurrency points

| # | Point | Risk | How it is closed | Test |
|---|---|---|---|---|
| C1 | snapshot readers vs the agent | a torn or half-updated view | immutable maps behind a volatile field, replaced whole | `the snapshot holds a stored result once Store replies` |
| C2 | a dependent reads a dependee's result | sees `Finished` before the result is in the snapshot | `Store` is awaited inside the task action, before the pool's `Done` | covered by every engine test that rebuilds dependents; no dedicated test |
| C3 | two callers decide one file | both write; a lost update | `Apply` compare-and-set; the loser re-stats and decides against the winner's record | `racing observations of a new file write one record...` |
| C4 | an older stat after a newer record | the stamp moves back, so dependents rebuild once more | the conflict path re-stats; a stale `expected` never applies | **no direct test** (reverse-order stats) |
| C5 | a verdict's later observation vs the first | answers from a record the first has not written yet | `RunFiles` claim: later verdicts wait for the first's completion, then read the snapshot | `a verdict's later observation sees a deleted file` (sequential only) |
| C6 | deadlock between claims | A waits for B's claim while B waits for A's | a call completes all its own claims (`finally`) before it waits for others | **no direct test** |
| C7 | one file hashed twice | wasted work; different answers | the hasher shares one computation per (file, stat) | `the hasher computes a (file, stat) once while it runs` |
| C8 | verdicts waiting on each other (memo) | deadlock on a cycle in the records | private wait-for map; an edge closing a cycle is refused (= changed) | `two entries into one cycle finish`, `a target depending on itself finishes` (both hang without the guard) |
| C9 | a task's verdict waits for a running dependee | deadlock with `need` waits | `RunCounters.Waits` (`WaitGraph`): refuse or cancel a wait that closes a cycle | ContentCutoffTests (existing) |
| C10 | one target requested twice | built twice | the pool's queued/running/finished maps; `NewRun` clears finished | `builds a target requested twice in one run only once` (existing) |
| C11 | writer order and close | records lost or reordered at exit | the agent posts in order; `Close` awaits `Drain` | `closing drains the writer, in order` |
| C12 | a failed hash or caller between decision and completion | claims never completed, so verdicts wait forever | `finally` completes claims; the hasher releases its slot in `finally`; `Content.hash` never throws | **no direct test** |
| C13 | overlapping engine runs | one run clears another's per-run state | `RunFiles` and `Verdicts` per run; the epoch is shared (the newest handed out) | **no direct test** |
| C14 | hold-and-wait on resources | a silent hang | `need` of a rule target while holding a resource is refused | ResourceGuardTests (existing) |
| C15 | the fast path floods the pool | too many verdicts at once | `RunCounters.FastGate` (a semaphore of cores) | none |

## 5. Public API and internals

- **Public (`Xake.Storage`):**
  - `Db` (`Snapshot`, `Post`, `PostAndReply`, `PostAndAsyncReply`, `IDisposable`) and
    `DatabaseApi` (`Apply`, `Store`, `NewRun`, `InMemory`, `Close`, `CloseWait`);
  - `tryGetResult`, `storeResult`;
  - `openDb`, `noopDb`, `cleanupDb`, `readRecords`, `readDatabase`.
- **Public (`Xake.BuildLog`):** `XakeDbVersion`, `XakeDbMinReadVersion`, `Database`,
  `newDatabase`, `DbRecord`.
- **Public (`Xake.FileProbe`):** `FileStat`, `HashResult`, `Content.hash`, `Hasher`.
- **Public (`Xake.FileTracker`):** `ObserveMode`, `Observation`, `RunFiles` (its
  `Claim`/`RecordedBefore` are internal), `ObserveContext` (`Db`, `Hasher`, `Run`, `Hashing`,
  `OnHashed`), `observeFiles`.
- **Public elsewhere:** `ExecContext` (a record, frozen within 5.x by `RecordShapeTests`) with
  `UpToDate: Target -> Async<bool>`, `NeedRebuild`, `RunFiles`, `Counters`; `EngineState` (with
  `Db` and `Hasher`); `RunSummary`;
  `Scheduler`, `TaskState`, `ExecMessage`.
- **Internal:** `FileRule`, `Persist` (format, path table), `LegacyRead`, the agent and the
  writer functions, `SnapshotCell`, `Db.Create`; the whole `DependencyAnalysis` module
  (`Verdicts`, `getChangeReasons`, `getDepState`, `criticalPath`); `WorkerPool.Gate`; `ExecCore`.

## 6. What a script author sees

There are no new keywords and no new options. Behaviour is the same as preview.2, with one
admitted change: overlapping engine runs keep their own per-run sets. The release notes list the
API changes for code that used `DatabaseApi` directly.

## 7. Candidates for review (looks accidental, over-complex or hard to explain)

1. **Two wait-for maps.** `RunCounters.Waits` (`WaitGraph`: a task's verdict waiting for running
   dependees, and `need`) and the private map in `Verdicts` (the memo) solve the same problem for
   two kinds of wait. One graph over "who waits for whom" would be easier to explain.
2. **Two verdict entry points.** `Of` is memoised and never waits for running tasks; `Fresh` is a
   task's own verdict after it waited. The difference is real (implnotes, "Epochs"), but nothing in
   the types says which one a caller needs.
3. **`RunFiles` claims do two jobs.** They cache the verdict ("seen once per run") and they order
   the first write before later reads. `Recording`/`Output` also claim but never wait. This is
   asymmetric and needs a comment to understand.
4. **Two kinds of stat in one call.** The first observation takes a full stat; a later verdict
   observation takes only `File.exists`. This is correct, but it is a performance shortcut
   visible in the logic.
5. **`Database.fs` holds everything.** Done (arch-review, topic 1): `DatabaseFormat.fs`,
   `Database.fs`, `FileProbe.fs`, `FileTracker.fs`, section 1.
6. **`Db` exposes its raw mailbox** (`Post`/`PostAndReply` with `DatabaseApi`) publicly, so a
   caller can bypass `observeFiles` and the rule. Tests use it. `DatabaseApi` could become
   internal.
7. **Engine internals in a public record.** `RunFiles` sits in `ExecContext`, which is frozen
   within a major. The same holds for `Counters` and `Spellings`.
8. **`ExecContext.Observe`** builds an `ObserveContext` on every call. The A3 observation in
   `ExecCore.storeResult` overrides `Hashing = false` and uses `Verdict` mode, so it claims the
   files in `RunFiles` as a verdict would. This is correct, but subtle.
9. **The conflict retry limit of 3 is arbitrary.** After 3 rounds the current record is taken as
   is, and nothing is logged.
10. **`prepareOne` returns `Choice<File, Async<...>>`.** A named union (`PlainFile` | `Task`)
    would read better.
11. **The `Store`-before-`Done` ordering (C2) is implicit.** It holds because `storeResult` is
    awaited inside the task's action. Nothing enforces it, and no test targets it.
12. **Two epochs.** Decisions use the snapshot's epoch, the newest handed out. Results use
    `ctx.RunEpoch`, the run's own. This is right for overlapping engine runs, but hard to see.
13. **`FastGate`** was added to bound blocking database lookups on the fast path. Those lookups
    are async now; the gate may be unnecessary.
14. **`getDurationDeps`** still carries the comment "No clue why one method does both".
    `Estimate` wraps it, and a target that was not warmed reads as 0.
15. **Lifetime and log wording.** The hasher is never stopped (its semaphore is not disposed),
    and the writer's failure messages still say "Database agent".
16. **Test gaps:** C4, C6, C12 and C13 have no direct test. The as-requested estimate and
    `--dryrun` on `Verdicts` are tested only at the `Verdicts` level.
