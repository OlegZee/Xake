# Session: pattern module (`feature/pattern-module`)

## Goal

A specified, tested pattern module (grammar, matcher, errors, API) that rules and filesets share;
then the typed layer (`Xake.Typed`) on top. Restart of `feature/typed-matches` (see `brief.md`).

## PR, branch, worktree

| PR | Branch | Worktree |
|----|--------|----------|
| — (starting documents; no PR yet) | `feature/pattern-module` (local) | `.claude/worktrees/feature/pattern-module` |
| — frozen reference, **never merge** | `feature/typed-matches` | `.claude/worktrees/feature/typed-matches` |

The frozen worktree has an uncommitted user edit to `experiments/08-consolidated/consumer/showcase.fsx`:
leave it alone.

## State

- 2026-10-26: folder created with `brief.md`, `kickoff.md`,
  `session.md`, `tracker.md`. Nothing else done: no spec, no tests, no code.

## Next step

Fresh orchestrator session reads `kickoff.md` and launches the Phase-1 spec agent (opus) producing
`docs/features/pattern-module/spec.md`; then the question-by-question review with the user.
