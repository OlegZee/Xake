# Phase 3 design report: content cut-off

Status: **approved** (2026-11-18, the five questions answered as recommended). Base: `dev` at `4f99e4a`, branch
`feature/content-cutoff`. Input: [brief.md](brief.md) (accepted 2026-11-18). No database format
change. Items marked **Proposal** are mine, not yet accepted.

## 0. A fact the brief relied on that the code does not hold

The brief (and the orchestrator's scope) say static `needs` are "built before the verdict since
phase 2". They are not: `execOne.run` asks `ctx.NeedRebuild` first and only a target that must run
calls `runHeaded`, which requests the `needs` (`ExecCore.fs`, `run`; rule-header brief section 2:
"when the verdict says the target must run"). Phase 2 kept the transitive rule, so this was
correct there. For cut-off through static `needs` the order must become: needs, verdict, locks,
body (the first attempt's accepted order). This is in scope (no format change), listed here because
it changes when `needs` are built: a target found up to date by the fast path (section 4) still
builds nothing; a target that is scheduled builds its `needs` even if the verdict then skips it.

## 1. Hashing

- `Content.hash : File -> (Timestamp * int64 * uint64) option` (new, `DependencyAnalysis.fs`):
  stat, SHA-256 of the bytes, stat again; a moved stat or an IO error gives `None` (nothing
  stored). The hash is the first 8 bytes, little-endian `uint64` (as `FileState.Hash`).
- **Not in the database agent.** Hashing a large output inside the agent loop would serialise all
  hashing and every `GetResult`. **Proposal:** the agent decides, the caller hashes:
  - `Observe` replies `Observation = Known of FileState option | Moved of FileState` (`Moved`:
    the record has a hash, or the mode needs one, and the stat moved; the record is not touched);
  - the caller hashes and posts `Settle (file, known, hashed, mode)`; the agent applies it only if
    the record is still `known` (compare-and-set), else replies the current state;
  - concurrent observers of one file in one run share one hash through a per-run
    `ConcurrentDictionary<File, Lazy<Async<..>>>` in the run state (section 3).
- Rules applied by the agent (brief "The rule"):

| Mode | Record | Stat | Result |
|---|---|---|---|
| Verdict / Recording | none | - | new record, `changed` = run, no hash (sources are hashed at their first move, not at first sight) |
| Verdict / Recording | hash h | moved | hash: = h -> `changed` kept, new stat; else `changed` = run, new hash |
| Verdict / Recording | no hash | moved | `changed` = run, new stat, hash computed |
| any | any | equal | unchanged (phase 1) |
| Output (cut-off on) | any | any | always hashed: = recorded hash -> `changed` kept (cut-off, counted); else `changed` = run unless stat equal and no recorded hash |
| Output (`noContentCutoff`) | any | any | no hash, `changed` = run |
| PreHash (new mode) | no hash | equal | hash stored, `changed` kept |
| any, `--content-cutoff=off` | any | moved | `changed` = run, **hash cleared**, nothing hashed |

  `ObserveMode.Output` gains `cutoff: bool`; `PreHash` is a new case. Phase 1's Recording rule
  ("another body recorded it in this run") is kept.

## 2. Outputs around a rule

In `execOne.run`, when the target is scheduled and the verdict says run:

1. **pre-hash**: each file output with a record, no hash and an equal stat -> `PreHash`
   (skipped for `noContentCutoff` and `off`);
2. body (`runHeaded` without its `needs` part, see section 0);
3. `observeOutputs` with `Output cutoff`; if every output kept its `changed`, log
   `[INF] cut-off: <target> rebuilt with the same content` and count it.

Delegated rules: the same after the executor returns (outputs observed and hashed by the engine;
the brief's "delegated results"). No pre-hash: the executor may not run the body.

## 3. The verdict

**Run state** (new, per run, in `ExecContext`, created by `beginRun`; replaces the bare memo):
the verdict memo, `Finished : ConcurrentDictionary<Target, unit>`, the hash dedup map, the
waits-for map (below), `RunCounters` (section 5).

`getDepState` for a result of epoch `e`, `TargetDep t`:

- `t` finished in this run -> observe (file: `changed > e`; phony: `Epoch > e`). No transitive
  rule.
- `t` running now -> wait for its task (CPU slot yielded), then as finished (Q1).
- `t` not started -> `changed`/`Epoch` `> e` -> changed; else `t`'s own no-build verdict
  (memoised) must be empty (the transitive rule, Q1).
- `FileDep`, the rest: as phase 1, with the observation rules of section 1.

Running/finished come from the worker pool: **Proposal:** a `Query of Target * reply` message to
`WorkerPool` answering `Running of Async<_> | Finished | NotStarted` from its two maps (it already
holds exactly this; the first attempt kept a parallel `Finished` set instead).

**Two verdicts.** The full verdict (with waiting) runs only in the target's own task, after its
`needs` (section 0). The no-build verdict (memoised, never waits: a running dependee inside the
recursion counts as changed — **Proposal**) serves the recursion, the fast path and the progress
estimate (`getDurationDeps`, which must not block).

**Waiting and cycles. Proposal.** Waiting for a running dependee can hang where 4.0 did not: the
recorded `T -> D` is from the last run, and the script may now have `D`'s body need `T` (a reversed
dependency). `T` waits in its verdict for `D`, `D` waits in `need` for `T`. A per-run waits-for
map (`waiter -> awaited`, set around the verdict wait and around `execNeed`'s wait) is checked
before the verdict waits: if `D` reaches `T`, `T` does not wait and counts `D` as changed (logged
at Info). About 25 lines; the alternative is to document the hang.

## 4. The no-change fast path (Q4)

In `execOne`, before posting `Run` to the pool: a non-delegated target not running or finished in
this run whose no-build verdict is empty is `Skipped` without a task (`Finished` marked, counted).
Blocking database lookups are gated by a semaphore of `max 2 ProcessorCount` (first attempt).
A run where nothing moved schedules no task.

## 5. `RunSummary`, control (Q4, brief)

- `RunSummary` record (`TargetsRun`, `TargetsSkipped`, `CutOff`, `FilesHashed`, `HashTime`,
  `TasksScheduled`), `RunCounters` (Interlocked), `RunSummary.format` -> one `Message` line at the
  end of a run; `onSummary` operation in `xakeScript` (an `IRunSummarySink` field in
  `ExecOptions`, keeps structural equality); `XakeEngine.LastSummary`. Ported from
  `archive/xake5-attempt1` (`ExecTypes.fs`, `XakeScriptBuilder.fs`, `ScriptRunner.fs`).
- `--content-cutoff=off` -> `ExecOptions.ContentCutoff: bool` (default true), command line only;
  it reaches the agent through the `Observe` mode (the agent stays option-free).

## 6. Public changes (release notes "5.0.0")

`RunSummary`, `IRunSummarySink`, `ExecOptions.ContentCutoff`, `ExecOptions.SummarySink`,
`onSummary`, `XakeEngine.LastSummary`, `--content-cutoff=off`. `Storage.Observation`, the `Settle`
message, `ObserveMode.Output of bool` / `PreHash` (the agent API is public). `ExecContext` gains
the run state (record shape test updated). Behaviour: the transitive rule is replaced as in the
brief; static `needs` are built before the verdict.

## 7. Line budget (core, net)

| File | Change | Lines |
|---|---|---|
| `Database.fs` | `Observation`, `Settle`, compare-and-set, modes, off, clear hash | +60 |
| `DependencyAnalysis.fs` | `Content.hash`, observe wrapper with dedup, finished/running/not-started, two verdicts | +70 |
| `ExecCore.fs` | needs before verdict, pre-hash, output modes, cut-off log/count, wait + cycle check, fast path, run state | +120 |
| `ExecTypes.fs` | `RunSummary`, `RunCounters`, run state, `ContentCutoff`, `SummarySink` | +75 |
| `WorkerPool.fs` | `Query` message | +15 |
| `XakeScript.fs`, `XakeScriptBuilder.fs`, `ScriptRunner.fs` | option, `onSummary`, `LastSummary`, summary line, `--content-cutoff=off` | +50 |
| **core total** | | **about +390** |
| tests (`ContentCutoffTests.fs` new, `StorageTests` additions, `RecordShapeTests`) | brief's Acceptance, by counters | +450 |
| docs | `docs/content-cutoff.md` (user), `implnotes.md` "Epochs" (verdict, hashing), release notes | +120 |

No new source module (hashing lives in `DependencyAnalysis.fs`).

## 8. Order of work after approval

Docs first; then 1 (hashing in the agent, unit tests on the observation table), 0 + 2 (needs
before the verdict, outputs), 3 (verdict, waiting), 5 (summary and control), 4 (fast path);
acceptance tests by counters; `dotnet build src/core -c Release` 0 warnings, `dotnet test`,
`dotnet fsi build.fsx -- -- build test pack`; Windows VM (`ssh win11`).

## Questions for the owner

1. Section 0: static `needs` built before the verdict (needed for cut-off through `needs`): OK?
2. Section 1: hashing outside the agent with `Moved` / `Settle` (compare-and-set), or inside the
   agent (simpler, serialises hashing)?
3. Section 3: a cycle check on the verdict wait (about 25 lines), or document the hang?
4. Section 3: inside the no-build recursion a running dependee counts as changed (only the target's
   own direct dependees are waited for): OK?
5. Section 3: running/finished from a `WorkerPool.Query` message rather than a parallel set?

## As built (2026-11-18)

Core +460 / -84 lines, **net +376** (budget about +390):

| File | Added / removed |
|---|---|
| `Database.fs` | +91 / -25 |
| `DependencyAnalysis.fs` | +50 / -12 |
| `ExecCore.fs` | +129 / -38 |
| `ExecTypes.fs` | +118 / -1 |
| `WorkerPool.fs` | +24 / -4 |
| `XakeScriptBuilder.fs`, `XakeScript.fs`, `ScriptRunner.fs`, `Types.fs` | +48 / -4 |

Tests: `ContentCutoffTests.fs` (12 tests, 271 lines), `RecordShapeTests`, `StorageTests` adapted.
Differences from the design above:

- **Hash dedup is in the agent**, not a per-run map: while a `HashNeeded` observation is unsettled,
  other `Observe`s of that file queue in the agent and are answered after the `Settle`. One move
  is hashed once; no `RunState` map is needed.
- **No separate run state:** `ExecContext.Counters` (`RunCounters`, per script run or `Demand`)
  holds the counters, the fast-path gate and the `WaitGraph`; the memo lives in the closures that
  `beginRun` puts in `NeedRebuild` (full verdict, async) and `UpToDate` (no-build verdict).
- `Scheduler.query` reads a snapshot of the pool's maps published after every message, not a
  mailbox message: a `Run` may hold the pool's loop while it waits for a CPU slot, and a verdict
  must not wait behind it.
- With `--content-cutoff=off` every dependee goes through the transitive rule (the pool is not
  asked), so the decisions are exactly those of phase 1.
- A verdict wait cancelled by the cycle check, or refused, counts the dependee as running (a
  change); a dependee that fails while waited for fails the dependent.

### Owner's review of PR #143 (2026-11-18): the agent API

- `Moved` became `HashNeeded of HashRequest`; `HashRequest` is opaque (internal constructor, only
  `File` public) and carries the known record, the mode and `since`, so a `Settle` cannot be
  given another mode.
- `Settle (request, result)`, `result = Hashed (stamp, size, hash) | Unreadable`. A stat that
  moved while hashing is `Unreadable` (the content was not stable: a change without a hash, as
  before). The reply is `Observe`'s, never `HashNeeded`; a lost compare-and-set replies the
  current state.
- `Observe (file, mode, hashing, since)` replies `Absent | Unchanged | Changed | HashNeeded`: the
  "changed after run `since`" comparison is only in the database. `getDepState` passes the
  dependent's epoch, `observeOutputs` `RunEpoch - 1`; record-only calls use `observe`
  (`since` = max). The `hashing` flag (`--content-cutoff=off`) stays a field of `Observe`, keeping
  the agent option-free. The phony comparison (`Epoch > e`) is unchanged.
