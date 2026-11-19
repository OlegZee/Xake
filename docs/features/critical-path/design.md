# Design: start the longest chains first

Status: proposal, waiting for the owner (2026-11-19). Brief: [brief.md](brief.md). Base: `dev` 7018d64.

## 1. Where the order is decided today

| Point | Code | Order today |
|---|---|---|
| Pool queue | `WorkerPool.create`, `Run` handler: `throttler.WaitAsync` **inside** the mailbox loop | The agent blocks on the semaphore, so later `Run` messages wait in the mailbox: strict arrival order. Only one pending task is ever visible; nothing can be reordered there. |
| Resume after a yield | `Scheduler.withYieldedSlot` (need, verdict wait, lock acquire, `runDetached`, delegated): `Release`, then `WaitAsync` on the same `SemaphoreSlim` | Competes with the agent's one waiter, roughly FIFO. |
| `need [..]` fan-out | `execParallel` = `List.map execOne >> Async.Parallel` | List order, raced on the thread pool. |
| Resource wait | `Resource.newResource` agent: `Queue`, head-of-line drain | FIFO; a large request blocks smaller ones behind it (no starvation). |
| Fast path | `execOne`: `query = NotStarted` and `UpToDate` -> skipped, never scheduled | Not ordered; nothing to change. |

`Scheduler.query` reads a snapshot of `running`/`finished`; a task waiting for a slot is
`NotStarted` today (it is still in the mailbox or in the blocked handler). The content cut-off
verdict relies on that: a dependee not started is judged transitively, a running one is waited for.

## 2. Change

### 2.1 A priority gate instead of the semaphore (`WorkerPool.fs`)

`Gate(n)`: a lock, a free count and a list of waiters `(key, seq, TaskCompletionSource)`.
`Acquire key : Async<unit>` takes a slot at once if one is free and nobody waits, else queues.
`Release ()` hands the slot to the waiter with the largest key, ties by the smallest `seq`
(arrival). With every key 0 it is FIFO: the `as-requested` order.

The agent stops blocking. A new `Run` starts its task at once: `gate.Acquire key`, post
`Started keys`, the action, `Release`, post `Done keys`. The agent keeps three maps: `queued`
(for dedup only), `running`, `finished`. `query` still answers from `running`/`finished`, so a
queued task stays `NotStarted` as today: the verdict semantics do not move. A second request of
a queued target joins it (dedup covers `queued`). The `acquired` bookkeeping of the failure path
goes away (no slot is taken inside the agent any more).

`withYieldedSlot` takes the yielding task's target and reacquires with its key: a resumed task
competes with new starts by the same rule, no special "resumes first" class.

### 2.2 Resources (`Resource.fs`)

`Acquire` carries a key; `pending` becomes a list ordered by (key desc, seq asc); the drain stays
head-of-line (the best waiter must fit before anyone behind it is granted), so a large request
is still never starved. Public `Resource.acquire` keeps its signature (key 0); an internal
`acquireAt key` is used by `acquireTimed` and `XakeScript.wrapRecipe`.

### 2.3 Fan-out

`execParallel` sorts its list by key, descending, stable, before posting: the first free slots
of a run go to the long chains, not to whichever request arrived first. Two lines.

## 3. Cost model: one model with the progress estimate

The progress estimate walks `getDurationDeps ctx getDeps` (duration = sum of `OwnTime` of the last
result's steps; edges = `Depends`/`DependsMissingTarget` change reasons, i.e. only dependees that
will rebuild) from the group's targets, using the run's memo `getDeps`. The critical path uses the
**same function on the same memo**, at the same moment (group start, before any task), so it
adds no new verdict state:

- walk forward from the group's targets, collect nodes and the reverse edges (dependee -> dependents);
- `key t = own t + max (key d for d in dependents t)` (0 without dependents), memoised, a visiting
  set guards against cycles (a cycle edge counts 0);
- the result, a `name -> int` map (every output name of a multi-output result looks up its
  primary), is handed to the scheduler: `Scheduler.newRun s keys` (merged, so an overlapping
  demand does not wipe another run's keys).

Cases:

- **No record** (first run, new target): 0. Not the median: 0 keeps "no history -> today's order";
  a median would let unknown tasks jump known short ones on a guess.
- **Up-to-date target**: duration 0, no edges (skipped by the fast path anyway).
- **Cut-off / skipped last run**: the record is the last real build, its `OwnTime` is a real
  measurement.
- **Phony**: same as files; a phony with no recorded `Depends` is "No dependencies" -> rebuilt.
- **Dynamic `need` not in the last run's graph**: key 0 until the next run has it.
- **WaitTime** (resources, needs, verdict) is not counted: it is not the task's own work.
- Recomputed **once per run** (each target group, each demand). Cost: one walk over nodes the
  progress estimate already walks (`getExecTime` is one database round trip per node).

Starvation: none; each run has a finite set of tasks and a released slot always goes to some
waiter; low keys are only postponed behind higher ones of the same run.

## 4. Options, CLI, log

- `ExecOptions.Schedule` / `EngineOptions.Schedule : Schedule = CriticalPath | AsRequested`.
  `AsRequested` = every key 0 (the gate is then FIFO, no sort, no walk).
- CLI `--schedule=as-requested` / `--schedule=critical-path` (the style of `--content-cutoff=off`),
  one help line, one banner line `schedule: critical path`.
- Default: **`CriticalPath`**, provided this repository's build and tests show no regression;
  otherwise opt-in for the first release (the owner decides after the measurement).
- Log: `Task queued 'x' (key 22000 ms)` at Info (the existing line, extended); the run summary
  line gets `, schedule critical-path`.

## 5. `priority <int>` in the header: Later (recommended)

History covers the consumer's case from the second run on; a first run is today's order, which
is no regression. A header priority adds an operation, a field in `RuleHeader`, a rule for how
it combines with the computed key, docs and tests: not justified until a measured case asks.

## 6. Acceptance test (deterministic where possible)

`src/tests/CriticalPathTests.fs`, scaled time (1 s -> 40 ms):

1. 40 phony tasks of 40 ms and a chain `a` (80 ms) -> `b` (800 ms), `b` requested last, `-t 4`.
   Run once (history), then run again in each mode. Each recipe appends its name to a shared
   start list. Assert by **position**: `critical-path`: `b` among the first 4 starts and `a`
   among the first 8; `as-requested`: `a` after the 40th start. A loose time check only as a
   sanity bound (critical-path run shorter than as-requested).
   Expected shape: b takes a first slot, yields at `need a`, `a` takes the next free slot
   (key 22 > 1): ~23 units instead of ~32 (the one unit is `a` being discovered only when `b`
   runs; reaching 22 would need prefetching dependees, not proposed).
2. Same with every task under `requires (Resource 4)` and `-t 16`.
3. Same graph, both modes: the database records (targets, `Depends`, `StaticCount`, epochs
   relative) are equal; only times differ.
4. Gate unit tests: order by key, FIFO among equal keys, all-zero keys = arrival order.
5. Existing suites green with `CriticalPath` as the default.

## 7. Line budget

| Area | Lines (net) |
|---|---|
| `WorkerPool.fs`: gate, non-blocking agent, keyed yield, key map | ~+60 |
| `Resource.fs`: keyed pending | ~+12 |
| `DependencyAnalysis.fs`: `criticalPath` | ~+22 |
| `ExecCore.fs`: keys at run/demand start, sorted fan-out, keyed call sites, log | ~+12 (6 call sites changed) |
| `ScriptFuncs.fs`, `XakeScript.fs`: keyed call sites | ~2 changed |
| Options, CLI, help, banner, summary | ~+15 |
| **Code total** | **~120** |
| Tests (`CriticalPathTests.fs`, gate tests) | ~130 |
| Docs (`implnotes.md` section, CLI doc, release notes "5.0.0 (unreleased)") | ~40 |

## Later (owner)

- `priority <int>` header operation.
- Prefetching recorded dependees of a high-key task (the last unit in the acceptance example).
- `getDurationDeps` is not memoised; the progress agent re-walks it on every refresh.
- A builder op for `Schedule` in `xakeScript { }`.
