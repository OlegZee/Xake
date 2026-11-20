# Topic 8: two verdict entry points, `Of` and `Fresh` (candidate 2)

State at 6ef186e.

## 1. What they are and who calls them

| | `Verdicts.Of t` | `Verdicts.Fresh t` |
|---|---|---|
| What | the run's no-build verdict, memoised (one task per target per run) | the verdict decided again, its dependees through the memo |
| Waits for tasks | never: running or started dependees are not taken into the transitive rule | it does not itself; its caller (`ExecCore.verdict`) first waits for the dependees running now |
| Callers | the fast path (`ctx.Run.UpToDate`), `Warm` (estimate, critical path), `--dryrun` printing, the recursion of other verdicts | only `ExecCore.verdict` (`ctx.Run.NeedRebuild`), when a task starts |

## 2. Why two

`Of t` is decided early, often in `Warm` before any task runs. It answers "can this target be
skipped without building anything?". When `t`'s task actually starts, its dependees may have run
in this run since. With content cut-off, a dependee rebuilt to the **same** content is not a change,
so `t` may be up to date after all, and `Of t` (which said "dependee not built yet" or "changed")
is stale. The task therefore waits for the dependees still running, then decides again: `Fresh`.
`Fresh` cannot reuse `Of`: the dependees' states (not started -> finished) changed between the two,
and the rule reads them.

The difference is real. What is hard to see:

1. The task's verdict is split across two files: the waiting is `ExecCore.verdict` (40 lines:
   `TaskWaits`, `Scheduler.query`, logging), the deciding is `Verdicts.Fresh`. `Fresh` alone is a
   half-step; called without the wait, it is just a slower `Of`.
2. The names say nothing of when to use which. `RunState.UpToDate` and `RunState.NeedRebuild` wrap
   them, with other names again.

## 3. Options

- **a. One owner, names by use (recommended).**
  - `Verdicts.Planned t` (was `Of`): never waits for a task; for planning, the fast path, the
    estimate, `--dryrun`.
  - `Verdicts.ForTask targets` (was `ExecCore.verdict` + `Fresh`): waits for the dependees running
    now (through `TaskWaits`), then decides again; what a starting task uses. `Fresh` becomes a
    private step of it.
  - `RunState.UpToDate` / `NeedRebuild` keep their names (they say the answer) and their doc
    comments name `Planned` / `ForTask`. They stay closures: `Verdicts` needs `ExecContext`, which
    is declared before it.
  - A doc comment on `Verdicts` with section 2's table.
  No behaviour change: the code moves, the order of steps is the same.
- **b. Rename and comment only**, leaving the wait in ExecCore.
- **c. Leave.**
