# Brief: progress-groups

Owner's remarks (2026-11-18), from builds with the .NET tasks where csc runs take the `CSC_JOBS`
resource:

1. Time spent waiting for a resource slot is recorded as `OwnTime`, so the real parallelism
   cannot be read from `.xake`.
2. With several target groups on the command line the progress estimate is unreliable.
3. Idea: show the groups in the status line, and the time of finished groups.

Base: `dev` at fa5b33b (5.0 phases 1-3, not released).

## Findings

### 1. Where wait time is counted

`StepInfo` (`Types.fs:91`): `OwnTime = (now - Start) - WaitTime` (`Database.fs:662-664`,
`Step.updateTotalDuration`). `WaitTime` grows only in two places:

- `need` in a body: the whole `execNeed` span (`ExecCore.fs:676-686`), slot reacquire included;
- the header's static `needs` (`headerNeeds`, `ExecCore.fs:461`), added in `runBody`
  (`ExecCore.fs:476`).

Not counted as wait (so counted as `OwnTime`): every resource acquire, each through
`Scheduler.withYieldedSlot (Resource.acquire ..)`, which also covers the wait to get the CPU slot
back:

| Lock | Where |
|---|---|
| `requires` in a rule header | `runBody`, `ExecCore.fs:481` |
| `requiring` (phony rule) | `wrapRecipe`, `XakeScript.fs:157-163` |
| `withResource` in a body | `ScriptFuncs.fs:40-47` |
| Xake.Dotnet csc/fsc `CSC_JOBS` | `withResource` (Xake.Dotnet `CompilerRunner.fs:319,327`): the engine's case above |

So the fix is in the engine (`withResource` and the two lock sites); Xake.Dotnet needs nothing.

**Bug found alongside:** in `runBody` the step starts (`Step.start "all"`, `ExecCore.fs:476`)
*after* `headerNeeds` returned, yet the header-needs time is added to its `WaitTime`. That time is
subtracted from a span that never contained it: `OwnTime` is too small, negative when the needs
took longer than the body.

The time between the pool's queue and the task start (`WorkerPool.fs:66`) and the verdict (which
may wait for a running dependee) are outside the step: neither own nor wait.

### 2. Why the estimate is wrong with several groups

- Groups run one after another (`runBuild`, `ExecCore.fs:590-611`, `runSeq`); **each group opens
  its own progress** (`Progress.openProgress`, `ExecCore.fs:599`) over that group's targets only.
  The percentage restarts at 0 and the time left ignores the groups still to come. `--dryrun`
  estimates all groups (`Progress.estimateEndTime`, `ScriptRunner.fs:139`); the run does not.
- A target that got a task but was then skipped (its verdict false after waiting for a dependee:
  content cut-off) posts neither `TaskStart` nor `TaskComplete` (`ExecCore.fs:233-236`), so its
  predicted duration stays in the time left until the group ends.
- Durations are the last run's `OwnTime` (`DependencyAnalysis.fs:25`): inflated by resource
  waits (1), deflated by the header-needs bug.
- The simulation models `Threads` only, no resources (`Progress.fs`, `Estimate.exec`).
- Engine mode (`Demand`) shows no progress at all (`XakeScriptBuilder.fs:339`), so overlapping
  runs are not involved.

## Options

### 1. Wait time

- **1A (recommended).** Count each resource acquire (the whole `withYieldedSlot` span) as
  `WaitTime`: `withResource` updates its result, `runBody` and `wrapRecipe` add it to the step.
  Fix the header-needs bug by starting the step before the needs. No format change. ~25 lines.
- **1B.** 1A plus a separate field, `StepInfo.LockTime` (needs vs resources apart). Changes the
  public `StepInfo` shape and the 0.7 pickler (unreleased, so allowed); `LegacyRead` fills 0.
  ~45 lines.

### 2. Estimate

- **2A (recommended).** One progress for the whole command line: `openProgress` takes the
  groups, estimates as `estimateEndTime` (groups in sequence), percent from the build start; a
  `GroupStart` message switches to the next group's verdicts. Plus a `TaskSkipped` message for a
  task skipped after its verdict. ~60 lines.
- **2B.** Keep one progress per group, add the later groups' estimate to the time left and keep
  the build's start for the percentage; the skip fix too. ~30 lines, the bar still jumps at
  group boundaries.

### 3. Groups in the status line

- **3A (recommended).** Status line prefix `[2/3 test]` when there is more than one group; a
  `Message` line `group 'build' done in 12.3 s` as each finishes; the final "Build completed"
  unchanged. ~30 lines on top of 2A.
- **3B.** 3A plus the per-group times in `RunSummary` (public type, `onSummary`). ~+15.

Budget for 1A + 2A + 3A: about 120 lines of code, plus tests and docs.

## Decisions

(pending the owner)
