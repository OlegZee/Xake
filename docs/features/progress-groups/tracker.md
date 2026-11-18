# Tracker: progress-groups

Brief: [brief.md](brief.md). Decision 2026-11-19: 1A + 2C + 3A.

| Item | Status |
|---|---|
| Investigation: wait time (1), estimate with groups (2) | done 2026-11-18 |
| Owner's decision on the options | 1A + 2C + 3A (2A rejected: `clean` first) |
| Docs: implnotes "Step times", "Progress"; release notes; brief decisions | done |
| 1A: lock waits (`requires`, `requiring`, `withResource`) and header needs/verdict as wait time | done |
| 2C: a task skipped after its verdict leaves the estimate | done (no test: the progress agent has no observable output in tests) |
| 3A: `[i/n names]` prefix, `group 'names' done in N s` | done |
| Tests `StepTimeTests.fs` (5, all fail on the old code) | done |
| Release build 0 warnings, 1846 tests, `build.fsx build test pack` (macOS) | done |
| Windows VM (core 0 warnings, 1821 passed, 21 skipped) | done |

## Later (for the owner)

- Resources in the estimate (the simulation knows only `Threads`).
- `--dryrun` estimates every group up front, so `clean build` in dryrun says "nothing to do" for
  `build`: the same flaw as option 2A.
- Group times stored in the database as a forecast for later groups (option 2E).
- A separate `LockTime` in `StepInfo` (option 1B), if needs and resources must be told apart.
