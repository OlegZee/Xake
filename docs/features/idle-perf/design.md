# Idle performance: design (2026-11-20)

This design replaces the one of 2026-11-19 (commit d6522fe), which is kept only in the git
history. The decisions it follows are in [brief.md](brief.md):
- no `Async.RunSynchronously` on the dependency path;
- the database does no file I/O for observation;
- a batch of a target's files per request;
- existence is checked per dependency.

The measurements are in [cold-hot.md](cold-hot.md).

## 1. What changes, in one paragraph

A caller (a verdict, a body's `need`, a task's outputs) takes the stats of a group of files
itself, on its own thread, and sends them in one message. The agent compares each stat with the
file's `FileRec`, decides, appends the changed records in one write, and replies once. The
agent no longer stats anything. Every wait on the agent becomes `PostAndAsyncReply` inside an
async computation. The verdict becomes async, and so does its per-run memo. The start-of-group
walk awaits the verdicts in parallel before the critical path and the progress estimate read
them.

## 2. Messages (`Storage`, `Database.fs`)

```fsharp
/// A file's stat, taken by the caller: missing, or its write time and length.
type FileStat = Missing | Present of stamp: Timestamp * size: int64

module FileStat =
    /// One stat of the file, on the calling thread (never in the agent).
    let take (file: File) : FileStat

type DatabaseApi =
    | GetResult of Target * AsyncReplyChannel<BuildResult option>
    /// Stores a result; replies with its file dependencies that have no state (a delegated
    /// result, A3). The caller stats those and observes them (`Recording`).
    | Store of BuildResult * AsyncReplyChannel<File list>
    /// Observes a group of files in one mode against the stats the caller took: one reply per
    /// file, in the order given. `None`: the file is being hashed for another observation; ask
    /// it again (alone), which then waits in the agent as today.
    | Observe of (File * FileStat) list * ObserveMode * since: int * AsyncReplyChannel<Observation option list>
    /// `HashResult.Unreadable` carries the stat the hasher took after reading.
    | Settle of HashRequest * HashResult * AsyncReplyChannel<Observation>
    | NewRun ... | InMemory | Close | CloseWait ...   // unchanged
```

- `Observe` with a list replaces the single-file `Observe`. A single file is a list of one.
  Hashing is unchanged (`HashNeeded`, `Settle`), so "ask it again alone" goes through the same
  message.
- `HashResult` becomes `Hashed of stamp * size * hash | Unreadable of FileStat`. `settle` today
  re-stats a file it is told is unreadable; with this change the hasher sends that stat.
- `DatabaseApi`, `Observe` and `Settle` were new in 5.0 and are not released, so their shapes
  change with no compatibility layer.

## 3. The agent

The agent's `observe` takes `(file, stat)` in place of `FileInfo`. The decisions stay exactly as
they are today (implnotes, "Epochs"):
- no record: new, `changed` = the run's epoch;
- equal stat: unchanged, an unknown size filled in;
- moved stat: `HashNeeded` when hashing, else `changed` = the run's epoch;
- `Verdict` decides change once per run, then answers from the record, with the existence taken
  from the stat sent (per dependency, as today's `File.exists`);
- `Recording` and `Output` decide again, as today.

The batch is folded through the same functions:
- A file in `pending` replies `None`.
- A `HashNeeded` puts the file in `pending` as today.
- The `FileRec`s a batch changes are encoded into one buffer and written with one `Write` and
  one `Flush`, instead of one per record. The path table rule is unchanged: a batch that fails
  to pickle writes nothing.
- An exception for one file (for example a bad path in the record) is logged, that file replies
  `None`, and the agent stays alive. Today the agent dies.

`Store` replies with the dependency files that have no state and observes nothing.

## 4. The callers

`DependencyAnalysis`:
- `observeMany ctx mode since files : Async<Observation[]>`:
  1. Take the stats (`FileStat.take`, on the calling thread).
  2. Send one `Observe`.
  3. Hash every `HashNeeded`, in parallel, and settle it.
  4. Ask each `None` alone. `None` only happens while another caller is hashing the file, so
     this waits in the agent as today.
  5. A failure between the reply and the last `Settle` settles the remaining requests as
     `Unreadable` (try/finally), so no file stays pending.
- The order matters: a caller settles its own `HashNeeded` files before it asks for anything
  else (a `None` file, a dependee's verdict). This is what keeps two crossed batches from
  waiting on each other.

The verdict, `getChangeReasons ctx current getDeps target : Async<ChangeReason list>`:
1. `GetResult`, async.
2. One `observeMany Verdict epoch` over the distinct files of its `FileDep` and
   `TargetDep (FileTarget _)` dependencies.
3. `GetResult` of its phony dependees, async.
4. For each `TargetDep` whose file is unchanged and whose task has not started, the dependee's
   own verdict through the memo (the transitive rule). These are awaited in parallel.
5. `getDepState` stays a pure function over these lookups. Its order of reasons is unchanged.

The memo, `beginRun`:
- `getDeps` is a per-run `ConcurrentDictionary<Target, Lazy<Task<ChangeReason list>>>`: each
  target's verdict starts once and every asker awaits the same task.
- A cycle in the records cannot occur in a finished run. Even so, the recursion carries the
  targets on its path, and a dependee already on the path counts as changed. Without this guard
  a cycle in a hand-edited or stale database would hang; today it ends in a stack overflow.

The group start:
- `beginRun` returns the memo.
- `planRun` first awaits `warm goals`: an async walk of the same targets `getDurationDeps`
  visits (a target, then the dependees named by its reasons), with siblings in parallel.
- After that, `getDurationDeps` (critical path, progress estimate and its refreshes) reads the
  completed results synchronously. A target that was not warmed reads as `[]`, which only
  affects the estimate.
- The walk now runs on several threads, and so do the stats.

`ExecCore`:
- `UpToDate: Target -> Async<bool>` (`ExecContext`; it was new in 5.0). The fast path awaits it.
- `NeedRebuild` / `verdict` await the async verdict.
- `execParallel` takes the plain files (no rule) out of its targets: rule selection and the
  spelling check as today, stats on the calling thread, one `observeMany Recording`. Only the
  targets with a rule go to `execOne`. A missing plain file fails as today ("Neither rule nor
  file is found"). Results come back in the order given.
- `observeOutputs` and `preHash`: one `observeMany` per task over its outputs.
- The dependee's file after its task (`ExecCore.fs:379`): one `observeMany Verdict` of one file
  (it is per task, not per dependency).
- `Store`: `let! missing = PostAndAsyncReply (Store ...)`, then
  `observeMany Recording missing` when the list is not empty. In practice this happens only for
  delegated results.
- `dumpDeps` (`--dump`, main thread, diagnostics): one `observeMany` per target, run
  synchronously at the top. This is not on the build path.

Blocking waits that remain: the top level (`ScriptRunner`, the main thread), the loggers'
flushes, and `ProcessExec`'s synchronous helper. None of them is on the dependency path.

## 5. What does not change

- **The database format.** The same `FileRec`s are appended in the same cases; only several of
  them now go in one write.
- **Epochs, `since`, cut-off decisions, hashing once per file (`pending`), `--dryrun` /
  `--dump` (`InMemory`), overlapping engine runs.**
- **Verdict results and their reasons.** Existence is checked per dependency, as today.
- **Ordering:** the stat is taken before the agent decides, where today the agent decides as it
  stats. This window already exists for hashing. A file changed inside it is seen by the next
  observation, as a change made just after today's stat would be.

## 6. Tests

`StorageTests.fs`, agent level:
1. **The agent does no I/O.** Send a stat for a path that does not exist on disk, saying it is
   present, and the agent believes it. Send `Missing` for a file that exists, and the reply is
   `Absent`.
2. **A batch equals single observations.** Two agents on copies of one database receive the
   same stats, one as a batch and one file by file. Cover new, unchanged, moved with hashing on
   and off, the cached `Verdict`, and `Recording` twice. The replies are equal, and so are
   `readDatabase` results (`Files`, `Status`).
3. **Pending.** A moved file is `HashNeeded`. Before its `Settle`, a second batch with that
   file gets `None`, and a single `Observe` of it is answered only after the `Settle`. A
   duplicate file in one batch is safe.
4. **`Store` replies the files without state.** A result whose dependencies all have records
   gets `[]`.
5. **One write per batch.** A batch of new files appends one buffer (count `Write` calls on a
   test stream).
6. **A bad file** in a batch replies `None` and the agent keeps answering.

Engine level:
1. **Crossed batches.** Two targets in parallel share two moved files listed in opposite
   orders, with hashing on. Both finish within a timeout, each file is hashed once, and the
   verdicts match a sequential run.
2. **Existence per dependency.** In one run, target A's body deletes source X (A does not
   depend on X), then target B, which depends on X, is checked (sequenced with `<<<`). B sees X
   missing.
3. **A cycle in the records.** Hand-written results with A depending on B and B on A: the run
   ends (the cycle counts as a change) instead of hanging.
4. **Message count.** `RunSummary.ObserveRequests`, a new field (`RunSummary` is new in 5.0),
   counts `Observe` messages. A no-change run of N targets with file dependencies sends N verdict
   batches plus one per finished dependee task, never one per dependency.
5. **Delegated results** (the existing A3 tests) through `Store`'s reply.
6. The existing `EpochTests`, `ContentCutoffTests`, `StaleArtifactDepTests`,
   `CriticalPathTests`, `ProgressTests`, `DelegatedTests` and `RecordShapeTests` (with
   `UpToDate`'s new type).

Performance: `bench/build.fsx`, run by hand before the release, not in CI.

| Run | Gate |
|---|---|
| hot no-change | no slower than 4.0.0 on the same machine |
| full build | within 2x of 4.0.0 |

Expected:
- **Hot no-change, about 0.3-0.4 s** (4.0.0 measured 0.56-0.64 s):
  - the stats: about 180,000 x 1.5 us, spread over 16 threads;
  - the agent: about 180,000 x ~1 us (a map lookup, a set insert, a compare), serial, about
    0.2 s;
  - plus the memo and the walk.
- **Full build, about 1 s** (4.0.0 measured 0.40-0.47 s): 1,188 Recording batches instead of
  178,200 messages, which takes away most of the agent's thread-pool parking, and one write per
  batch.

## 7. Docs and release notes

- `docs/implnotes.md`, "Epochs":
  - Observation is a batch with the caller's stats, and the agent does no I/O.
  - The verdict and its memo are async, and the start-of-group walk is parallel.
  - `Store` replies with the files that have no state.
- `docs/release-notes.md`, "5.0.0 (unreleased)":
  - "A no-change run and a full build check and record file dependencies in batches, one
    database request per target, with the files' stats taken by the build's own threads; the
    preview.2 regression (a no-change run about twice as slow as 4.x on a large build) is fixed."
  - API: `ExecContext.UpToDate` is `Target -> Async<bool>`; `Storage.Observe` takes a list of
    files with their stats; `Store` replies; `HashResult.Unreadable` carries a stat;
    `FileStat`; `RunSummary.ObserveRequests`.
  - In its own commit: the FS0041 note for recursive recipes (brief.md).

## 8. Line budget

| Where | Lines changed (about) |
|---|---|
| `Database.fs`: `FileStat`, the messages, `observe` on a stat, the batch fold, one write per batch, `Store`'s reply | 70 |
| `DependencyAnalysis.fs`: `observeMany`, the async verdict, the async memo and cycle guard, `warm` | 110 |
| `ExecCore.fs`: plain files in `execParallel`, outputs/pre-hash batches, `Store`, async `UpToDate`, `planRun` | 70 |
| `ExecTypes.fs`: `UpToDate`, `RunSummary.ObserveRequests`, counter | 10 |
| tests | 250 |
| docs, release notes | 30 |

About 260 lines of code, of which roughly 120 are new and the rest rewritten. This is larger
than the 2026-11-19 design (about 60). The difference is the async verdict, the async memo and
the move of every stat out of the agent, including `Recording`.

## 9. Later (not in this change)

- The agent on its own thread instead of the thread pool. Measure after this change: with about
  1% of today's messages, the parking may be gone.
- `db.Files` as a `Dictionary` in the agent instead of an immutable `Map` (about 1 us -> 0.12 us
  per file). It interacts with the "a failed record leaves the table as it was" rule.
- `FastGate`, the semaphore that limits blocking lookups on the fast path, may no longer be
  needed once nothing there blocks.
