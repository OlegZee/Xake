# Brief: start the longest chains first (Xake 5.x)

Audience: the agent working in the Xake repository. Xake is public: this brief names no product or company of the
consumer. Engine facts are from `dev` (7018d64, Xake 5.0 unreleased): `src/core/ExecCore.fs`, `src/core/WorkerPool.fs`,
`src/core/Progress.fs`.

## Problem

Xake has no priorities: when more targets are ready than `-t` allows, it starts them in the order the recipes asked
for them. On the consumer's CI packaging run (202 s, 16 threads, at most 6 compilers at once):

- compilation ends at ~125 s;
- one assembly depends on a library built mid-way (~20 s into compilation); it is ready and merged at ~75 s;
- its obfuscation takes ~125 s under load (~75 s alone), from ~75 s to the end of the run; compilation and the other
  obfuscations finish at ~125–140 s, so for the last ~60–75 s this chain runs alone.

Every second this long chain waits behind short independent tasks is a second added to the whole run. The engine
already knows durations: `.xake` keeps each target's `OwnTime` and `WaitTime` from previous runs, and the progress
estimate (`Progress.fs`) uses them.

## What to add

### 1. Critical-path ordering (default on, from history)

- When choosing which ready task to start (worker pool queue, and the order in which `need [..]` fans out), prefer
  the task with the longest remaining path: its own expected time plus the longest expected path of the targets that
  (transitively) depend on it within this run.
- Expected times come from the database (last `OwnTime`, or a moving average if one is kept). Unknown tasks get the
  median of known ones, or 0; the ordering must not change semantics, only order.
- The dependents of a task in this run are known only partly before recipes run (dynamic `need`). Use what the
  database recorded from the previous run (the reverse of `Depends`) as the estimate; that is what the progress
  estimate already does.
- Option to turn it off: `ExecOptions.Schedule = CriticalPath | AsRequested` (default `CriticalPath` if measurements
  show no regressions, otherwise opt-in for the first release), `--schedule as-requested`.

### 2. Explicit priority (small, optional)

- A header operation `priority <int>` (higher first) for the cases history does not cover: a first run, or a task
  known to be long. It adds to the computed path length (or breaks ties); your call, document it.

### Resources

Tasks that wait on a resource (`requires`, a semaphore like the consumer's compiler slots) should be ordered by the
same key when the resource frees up, not FIFO; otherwise the order is lost at the resource.

## Acceptance

- A synthetic graph: 40 independent 1 s tasks and a chain `a (2 s) -> b (20 s)` where `b` is requested last, `-t 4`.
  With history, `a` starts in the first wave and the run ends at ~22 s instead of ~32 s (10 s of short tasks, then
  `a` and `b`). Without history, the order is as today.
- Resource-bound tasks: same synthetic graph with all tasks under one semaphore of 4; same result.
- The repository's tests pass with the ordering on; outputs and the database records are the same as with
  `as-requested` (only start order differs).
- The run log (debug level) names the computed priority of each started task; the summary states the scheduling mode.

Prior art: critical-path list scheduling; Ninja 1.12 orders ready edges by critical path using durations from
`.ninja_log`.
