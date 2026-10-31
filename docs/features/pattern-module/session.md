# Session: pattern module (`feature/pattern-module`)

## Goal

A specified, tested pattern module (grammar, matcher, errors, API) that rules and filesets share;
then the typed layer (`Xake.Typed`) on top. Restart of `feature/typed-matches` (see `brief.md`).

## PR, branch, worktree

| PR | Branch | Worktree |
|----|--------|----------|
| — integration branch (target of every phase PR; merges to `dev` at the end) | `feature/patterns2` (cut from `dev` 2026-10-31) | — (no worktree; PR target only) |
| — spec documents; first PR into `feature/patterns2` once the spec is approved | `feature/pattern-module` (rebased on `dev` 2026-10-31) | `.claude/worktrees/feature/pattern-module` |
| — frozen reference, **never merge** | `feature/typed-matches` | `.claude/worktrees/feature/typed-matches` |

The frozen worktree has an uncommitted user edit to `experiments/08-consolidated/consumer/showcase.fsx`:
leave it alone.

## State

- 2026-10-26 (later session): `spec.md` drafted (opus agent). Core proposal: one grammar/AST, two semantics levels (Compat = 3.13 default with warnings, Standard = 4.0 default, opt-in via `XAKE_PATTERNS`). Open items 1-15 answered with recommendations in spec section 11; conflicts C1-C10 in section 12. Review with the user started.
- 2026-10-31: D1 revised: Standard only, no Compat (`dev` is already 4.0). D2: integration branch `feature/patterns2`. Branch rebased on `dev`. Next: an opus agent strips Compat from `spec.md`, then the item-by-item review continues from item 1.
- 2026-10-26: folder created with `brief.md`, `kickoff.md`,
  `session.md`, `tracker.md`. Nothing else done: no spec, no tests, no code.

## Next step

Fresh orchestrator session reads `kickoff.md` and launches the Phase-1 spec agent (opus) producing
`docs/features/pattern-module/spec.md`; then the question-by-question review with the user.
