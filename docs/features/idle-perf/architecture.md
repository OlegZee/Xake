# How the engine observes files and decides (as of v5.0.0-preview.3)

This describes the system as it is at `v5.0.0-preview.3` (407765f, feature/idle-perf), with the
file layout of the architecture review (feature/arch-review, topic 1), not the history of the
change. The history and the measurements are in [brief.md](brief.md),
[design.md](design.md) and [cold-hot.md](cold-hot.md). Section 7 lists candidates for review.

## 1. Layers and who owns what

```
 L7  ExecCore            tasks, need, outputs, store       (ExecCore.fs)
 L6  Verdicts            verdict + memo per run, Warm      (DependencyAnalysis.fs)
 L5  observe        stat -> rule -> hash -> Apply     (FileTracker.fs)
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
| `DatabaseFormat.fs` | `Xake.Storage` (namespace) | `Database` (the records in memory), `DbRecord`; internal `Database` (empty, addResult) and `Persist` (version, 0.7 header and picklers, path table, `read`, `encode`, `toDatabase`) | `Types`, `Pickler`, `LegacyRead` |
| `Database.fs` | `Xake.Storage` (namespace) | internal `DatabaseApi`, `DbFile` (lock, backup, upgrade, compaction), `DbAgent` (agent, writer, snapshot cell); the public `DbReader` (read API); internal `Db` (the handle and its module) | `Persist` |
| `FileProbe.fs` | `FileProbe` | `FileStat`, `HashResult`, `Content.hash`, `Hasher`: I/O only | `Types` |
| `FileTracker.fs` | `FileTracker` | `ObserveMode`, `Observation`, `FileRule` (pure), `RunFiles`, `Context`, `observe` | `Storage`, `FileProbe` |

`Database.fs` uses nothing of `FileProbe` or `FileTracker`; they use the database only through
`Db.Snapshot` and `Db.apply`. `makeResult` and `Step` are in `Types.fs`, the `Agent` alias in
`Prelude.fs`.

A layer calls only layers below it. The scheduler sits beside them: L6 asks it whether a dependee
is running (`Scheduler.query`), and L7 submits tasks to it.

| Part | Owns | Does not |
|---|---|---|
| `FileStat.take`, `Content.hash` (L0) | nothing; read the file system | keep state |
| `FileRule` (L1) | the decision for one observation: `decide known stat mode ctx`, `decideHashed`, `classify since`; one pair of rules per kind of observation (`input` for `Verdict`/`Recording`, `output`, `preHash`, each with its rule after hashing), none depending on another's order | I/O, mutation |
| Database agent (L2) | the records in memory (`Status`, `Files`, `Epoch`), the snapshot | read project files, decide, wait |
| Writer (L2) | the path table, the `.xake` stream, encoding, failure counts | touch the records in memory |
| `Hasher` (L3) | the hashes being computed, keyed by (file, stat) | know records, epochs, runs |
| `RunFiles` (L4) | per run: files observed (with the first observation's completion), files recorded by a body | hold records |
| `observe` (L5) | the sequence for a group of files | keep state of its own |
| `Verdicts` (L6) | per run: each target's no-build verdict (memo), the wait-for edges between verdicts, the warm walk | stat, hash, post to the agent |
| `ExecCore` (L7) | tasks, `need`, the fast path, the task's own verdict, outputs, `Store`, the A3 observation | decide file changes |
| Scheduler / WorkerPool | one task per target per run, the slot gate ordered by critical-path key, the running/finished maps | decide verdicts |

### The database (`Storage.Db`)

- **Agent messages** (internal `DatabaseApi`; the engine sends them through `Db.apply`,
  `Db.store`, `Db.newRun`, `Db.keepInMemory`, `Db.close`).
  - `Apply (writes)`: file records, each written only if the record is still the one the caller
    decided against (compare-and-set per file). Replies with the conflicts, each with its current
    record.
  - `Store (result)`: replies once the result is in memory.
  - `NewRun`: increments the epoch and replies with it.
  - `InMemory`: nothing more is written to disk.
  - `Close` / `CloseWait`: drains the writer, closes the file, releases the lock. Afterwards the
    agent answers a further close at once and ignores the rest. `Db.Dispose` is a synchronous
    close (the first one; a second does nothing), then disposes the agent.
- **The snapshot.** After every message the agent publishes `Db.Snapshot` (`Storage.Database`:
  immutable `Map`s behind a volatile field). Readers never post: `Db.tryGetResult`, a file's
  record, the epoch.
- **The writer.** The agent hands its accepted records to the writer, in its own order, and does
  not wait for it. The writer encodes each batch with the path table and appends it with one
  `Write` and one `Flush`. `Close` drains the writer before the stream and the lock are released.
- **Format.** The format is unchanged from preview.2: database 0.7, the same picklers. Each
  version opens the other's file without a rebuild (checked on the bench).

### The observation sequence (`FileTracker.observe oc mode since files`)

Three private steps, joined by `observe` (arch-review, topic 6):

1. **`claim`.** Each distinct file is claimed in `RunFiles`, except in `PreHash` mode (its record
   is the one before the rule runs; a verdict waiting for it would read a stale record).
   - The first observer of the file in the run holds a completion and sets it when done.
   - A later `Verdict` observation waits on that completion.
   - Later `Recording`/`Output` observations decide again (they follow writes made in this run);
     they claim so that a verdict after them waits for their write.
   - `Recording` also tests and sets "recorded before" (A1).
   - Each file decided here becomes a `Pending` (file, A1, record, stat): the record is read from
     the snapshot, then the full stat (`FileStat.take`) taken, on the calling thread.
2. **`settle`**, in rounds:
   - `FileRule.decide` against the record and the snapshot's epoch: `Decided (Keep | Write)` or
     `NeedHash`;
   - `NeedHash` goes through the hasher, the hashes of one call in parallel, then `decideHashed`,
     which always ends in `Keep | Write` (`Outcome`);
   - every `Write` in one `Db.apply`; a conflict is decided again on a fresh stat against the
     current record, up to `maxRounds` (3); after that the current record is the answer, and a
     Debug line names the file.
3. **Completion.** The claims are completed (`finally`, also when `settle` fails).
7. **Later verdict observations.** Each waits for the first observation, then answers from the
   current record if `File.exists` (existence is checked per dependency).
8. **Reply.** `classify since` per file, in the order given.

### Verdicts (`DependencyAnalysis.Verdicts`, one per run)

- **`Planned t`** (was `Of`) is the memoised no-build verdict (`Lazy<Task>` per target). It is decided in two
  phases:
  1. One `observeMany Verdict epoch` over the target's files, then, in parallel, the verdicts of
     the dependees the transitive rule needs. These are the dependees whose file is unchanged, or
     phony dependees not newer than the target, that have not started.
  2. `getDepState`: the rule over what phase 1 gathered. It is pure, apart from fileset scans.
- **Waiting on another verdict.** Before a verdict awaits another target's verdict that is still
  in progress, it registers the edge in `verdictWaits` (a `WaitFor`). An edge that would close a
  cycle is refused, and that dependee counts as changed.
- **`ForTask targets`** is a starting task's own verdict: it waits for the dependees running now
  (`TaskWaits`), then decides again (its private step `fresh`). Its dependees still come through
  the memo. Nothing waits for `fresh`, so it registers
  no edges.
- **Two wait-for maps, one primitive.** Tasks wait for tasks (`need`, a task's verdict waiting for
  a running dependee) in `ctx.Run.TaskWaits`; verdicts wait for verdicts in `verdictWaits`.
  Both are a `WaitFor` (ExecTypes.fs: edges by target name, `TryAdd` refuses a cycle). A verdict
  never waits for a task (it only queries the scheduler, and takes a dependee into the transitive
  rule only when its task has not started), so no cycle runs through both maps
  ([arch-review/wait-for.md](../arch-review/wait-for.md)).
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
  thread of its own. The engine's async code (tasks, verdicts, `observe`) runs on the pool
  too. The main thread waits at the top (`runBuild |> Async.RunSynchronously`).
- **Synchronous waits** are left only at the top level: `--dump` (`dumpDeps`), the `--dryrun`
  printing (after a parallel `Warm`), close, and `ProcessExec`'s synchronous helper. None is on
  the dependency path.

## 3. The path of one file

### A run where nothing changed

```
beginRun:  NewRun -> agent (epoch+1, publish)          new RunFiles, new Verdicts
planRun:   Verdicts.Warm goals                          (parallel over targets)
  Planned t: snapshot.Status[t]                           no message
           observe Verdict e [t's files]
             first claim of f:  stat (thread) -> decide: equal stat -> Keep  (no Apply)
             later claims of f: wait first -> File.exists -> snapshot.Files[f]
           dependees' Of (memo, parallel)  -> getDepState -> []
execParallel goals -> prepareOne t -> fast path: UpToDate t = Planned t (done) -> Skipped
           observe Verdict [t's output] (seen: File.exists + snapshot)
Store of the phony goals -> agent -> writer
```

On the bench, the agent receives one `NewRun` and the `Store`s of the 7 phony targets. Each of
181,764 dependencies costs one `File.exists` and one `Map` lookup, spread over the pool by
`Warm`.

### A full build (empty database)

```
Warm:      Planned t -> no result -> "Not built yet" (no observation)
execOne t -> pool (slot by key) -> run:
  needs, ForTask (rebuild), preHash: observe PreHash [out] (missing -> Keep)
  body: need [150 files] -> execParallel -> prepareOne each (rule? exists?)
        plain files -> one observe Recording [150]:
          claim, recordedBefore, stat -> decide: no record -> Write (changed = epoch)
          Apply [150] -> agent: CAS; a file another task just wrote conflicts
                         -> fresh stat -> decide vs new record: equal -> Keep
          agent publishes, replies, Append -> writer: encode, Write, Flush
  output written; observe (Output true) [out]: NeedHash -> Hasher -> decideHashed
          -> Apply -> writer
  storeResult: Store (awaited) -> agent; A3: deps with no record in the snapshot
          -> observe Verdict (no hashing) [those]  (none here)
  pool Done -> dependents: observe Verdict [out] (after the dependee)
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
| C8 | verdicts waiting on each other (memo) | deadlock on a cycle in the records | `verdictWaits` (`WaitFor`); an edge closing a cycle is refused (= changed) | `two entries into one cycle finish`, `a target depending on itself finishes` (both hang without the guard) |
| C9 | a task's verdict waits for a running dependee | deadlock with `need` waits | `ctx.Run.TaskWaits` (`WaitFor`): refuse or cancel a wait that closes a cycle | ContentCutoffTests (existing); `WaitFor` unit tests |
| C10 | one target requested twice | built twice | the pool's queued/running/finished maps; `NewRun` clears finished | `builds a target requested twice in one run only once` (existing) |
| C11 | writer order and close | records lost or reordered at exit | the agent posts in order; `Close` awaits `Drain` | `closing drains the writer, in order` |
| C12 | a failed hash or caller between decision and completion | claims never completed, so verdicts wait forever | `finally` completes claims; the hasher releases its slot in `finally`; `Content.hash` never throws | **no direct test** |
| C13 | overlapping engine runs | one run clears another's per-run state | `RunFiles` and `Verdicts` per run; the epoch is shared (the newest handed out) | **no direct test** |
| C14 | hold-and-wait on resources | a silent hang | `need` of a rule target while holding a resource is refused | ResourceGuardTests (existing) |
| C15 | the fast path floods the pool | too many verdicts at once; a verdict `Warm` did not decide stats files synchronously on a pool thread | `ctx.Run.FastGate` (a semaphore of cores); measured in arch-review/fastgate.md | none |

## 5. Public API and internals

The database, the tracker and the probe are engine internals (arch-review, topic 2). Public are
the read API and one opaque handle. A module is public or internal as a whole; `EngineState` has an
internal representation, so nothing internal shows through it:

- **`Xake.Storage`:** `Database`, `DbRecord`; the module `DbReader`: `readRecords`, `readDatabase`,
  `formatVersion`.
- **Public elsewhere:** `ExecContext` (a record, frozen within 5.x by `RecordShapeTests`):
  `Engine`, `Run`, `Vars`, `Targets`, `RuleMatches`, `Ordinal`, `Logger`, `HeldResources`;
  `EngineState` and `RunState` (records with internal fields: the engine's and the run's state,
  so per-run state changes without changing `ExecContext`; topic 7); `RunSummary`;
  `Scheduler`, `TaskState`, `ExecMessage`.
- **Internal:** all of `FileProbe` and `FileTracker`; `RunState`'s fields (with `TaskWaits`, `FastGate`); `RunCounters`; `Db` (the handle and its module); `ExecContext.Db`; `DatabaseApi`, `Db.Post*`, `Db.openWith`/`openFile`/`memoryOnly`/`delete`,
  `Db.apply`/`store`/`newRun`/`keepInMemory`/`close`, `Persist`, `DbFile`, `DbAgent`, `LegacyRead`;
  `FileStat`, `HashResult`, `Content.hash`, `Hasher.Hash`; `ObserveMode`, `Observation`,
  `FileRule`, `FileTracker.Context`, `FileTracker.observe`; `ExecContext.Observe`; the whole `DependencyAnalysis` module
  (`Verdicts`, `getChangeReasons`, `getDepState`, `criticalPath`); `WorkerPool.Gate`; `ExecCore`.

## 6. What a script author sees

There are no new keywords and no new options. Behaviour is the same as preview.2, with one
admitted change: overlapping engine runs keep their own per-run sets. The release notes list the
API changes for code that used `DatabaseApi` directly.

## 7. Candidates for review (looks accidental, over-complex or hard to explain)

1. **Two wait-for maps.** Done (arch-review, topic 4): two maps by design, one `WaitFor`
   primitive, `TaskWaits` and `verdictWaits`; `fresh` registers no edges (section 1).
2. **Two verdict entry points.** Done (arch-review, topic 8): `Planned` and `ForTask`, both in
   `Verdicts`, with the reason for two in its doc comment.
3. **`RunFiles` claims do two jobs.** Settled (arch-review, topic 6): a claim is an ordering
   barrier, not a cache of answers; a memo of the first answer would be stale when a rule rewrites
   the file in the run. The reasons are now in the comments on `RunFiles` and `claim`.
4. **Two kinds of stat in one call.** Settled with 3: the later verdict needs only existence.
   `answerLater` is its own step.
5. **`Database.fs` holds everything.** Done (arch-review, topic 1): `DatabaseFormat.fs`,
   `Database.fs`, `FileProbe.fs`, `FileTracker.fs`, section 1.
6. **`Db` exposes its raw mailbox.** Done (arch-review, topic 2): `DatabaseApi` and `Db.Post*`
   are internal; the engine uses `Db.apply`, `Db.store`, `Db.newRun`, `Db.keepInMemory`,
   `Db.close`.
7. **Engine internals in a public record.** Done (arch-review, topic 7): they are in
   `ExecContext.Run` (`RunState`, internal fields).
8. **`ExecContext.Observe`** (internal) builds a `FileTracker.Context` on every call. The A3 observation in
   `ExecCore.storeResult` overrides `Hashing = false` and uses `Verdict` mode, so it claims the
   files in `RunFiles` as a verdict would. This is correct, but subtle.
9. **The conflict retry limit of 3 is arbitrary.** Done (topic 6): `maxRounds`, and a Debug line
   when it is reached.
10. **`prepareOne` returns `Choice<File, Async<...>>`.** A named union (`PlainFile` | `Task`)
    would read better.
11. **The `Store`-before-`Done` ordering (C2) is implicit.** It holds because `storeResult` is
    awaited inside the task's action. Nothing enforces it, and no test targets it.
12. **Two epochs.** Decisions use the snapshot's epoch, the newest handed out. Results use
    `ctx.Run.RunEpoch`, the run's own. This is right for overlapping engine runs, but hard to see.
13. **`FastGate`** Settled (arch-review, topic 9): kept, measured (no gain without it; full builds
    slower and less stable); the reason is the synchronous stats of a verdict `Warm` did not
    decide. It and `TaskWaits` are in `RunState`, not in the counters.
14. **`getDurationDeps`** still carries the comment "No clue why one method does both".
    `Estimate` wraps it, and a target that was not warmed reads as 0.
15. **Lifetime and log wording.** The hasher is never stopped (its semaphore is not disposed),
    and the writer's failure messages still say "Database agent".
16. **Test gaps:** C4, C6, C12 and C13 have no direct test. The as-requested estimate and
    `--dryrun` on `Verdicts` are tested only at the `Verdicts` level.
