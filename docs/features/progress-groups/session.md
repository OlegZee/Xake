# Session: progress-groups

Branch `feature/progress-groups`, from `dev` at fa5b33b. Not pushed, no PR.

**Where things stand (2026-11-19).** The owner chose 1A + 2C + 3A ([brief.md](brief.md)); it is
implemented, documented and tested (`src/tests/StepTimeTests.fs`); macOS release build 0 warnings,
all tests green, `build.fsx build test pack` green. Windows VM green (1821 passed, 21 skipped).

What changed in code:

- `ExecCore.headerNeeds` returns the task's step, started before the needs, their time as its
  wait; `run` adds the verdict's time to it; `runBody` adds each lock's acquire
  (`acquireTimed`); `withResource` (`ScriptFuncs.fs`) and `requiring` (`XakeScript.wrapRecipe`)
  add theirs to the current step. `Step.msSince` in `Database.fs`.
- `runBuild`: per group a label `[i/n names] ` (more than one group) for
  `Progress.openProgress` (new `label` parameter) and `ConsoleSink.formatStatus`, and
  `group 'names' done in N s` at `Message`.
- A task skipped after its verdict posts `TaskComplete`.

**Next step.** The owner's word to push and open the PR into `dev` (squash).
