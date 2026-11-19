# Session: critical-path

Branch `feature/critical-path`, from `dev` at 7018d64 (Xake 5.0 phases 1-3, unreleased). PR [OlegZee/Xake#147](https://github.com/OlegZee/Xake/pull/147), open into `dev`.

**Where things stand (2026-11-19).** The owner approved [design.md](design.md) as proposed
(recorded in [brief.md](brief.md)). Implemented, documented, tested; committed locally.

- Code (net +124): `WorkerPool.Gate` (a slot to the largest key, FIFO among equal keys) replaces
  the semaphore; the pool agent no longer blocks (a `Started` message moves a queued task to
  running; a queued task is `NotStarted` for `query`); `withYieldedSlot` takes the task's targets
  and reacquires by their key; `Resource` waiters ordered by key, head-of-line (`acquireAt`
  internal, `acquire` = key 0); `DependencyAnalysis.criticalPath` over `getDurationDeps` and the
  run's memo, set per run/demand by `ExecCore.planRun`; `execParallel` requests by key and
  returns results in the given order (records do not depend on the mode); `Schedule` in
  `Types.fs`, `ExecOptions`/`EngineOptions.Schedule` (default `CriticalPath`),
  `--schedule=as-requested|critical-path`, banner line, `, schedule <mode>` on the summary line,
  `Task queued 'x' (key N ms)` at Info.
- Tests (`src/tests/CriticalPathTests.fs`, +139): gate and resource order; the acceptance graph
  (1 s = 40 ms) under the pool (`-t 4`) and under a resource of 4 (`-t 16`), by start position;
  database records equal in both modes. Measured: ~970 ms critical-path vs ~1300 ms as-requested
  (~24 vs ~32 units; the extra unit is `a` found only when `b` runs).
- Docs (+36): implnotes "Critical path", resources.md note, release notes 5.0.0 (unreleased).
- Checks: core Release 0 warnings; `dotnet test src/tests -c Release` 1860 passed, 13 skipped,
  in 50 s with either default; `build.fsx build test pack` green (it runs on the published
  engine 3.15.0, so it does not measure the new order).

**Next step.** CI on #147, the owner's review, squash merge into `dev`; then the worktree removal command to the owner.
