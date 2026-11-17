# Session: db-paths (Xake 5.0, database 0.7)

Branch `feature/db-paths` (worktree `.claude/worktrees/db-paths-ced680`), cut from local `feature/dep-cutoff` at
`4ece63b`. Merge later dep-cutoff commits only when the orchestrator says so. Brief: [brief.md](brief.md) (copied
as-is 2026-11-17; engine facts in it are from `43706d3`, the sources win). Shares the 0.7 version bump with
[dep-cutoff](../dep-cutoff/brief.md) ("Database (0.7)").

## Where things stand (2026-11-17)

Plan and open questions in [plan.md](plan.md), reported to the orchestrator; nothing implemented. The plan lists
six differences between the brief and the sources (target names already absolute, no db dump, three disagreeing
case rules, `Fileset.BaseDir`, path-valued variables) and questions Q1-Q6.

## Next step

Wait for the owner's answers to Q1-Q6. Then a fresh-eyes critic subagent (model Fable) on brief.md, plan.md and the
sources (`Database.fs`, `File.fs`, `Fileset.fs`, `Types.fs`, `ExecCore.fs`, `ScriptRunner.fs`), findings to
`critic.md`; owner triage; then docs, then code in the plan's order.
