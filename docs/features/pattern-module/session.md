# Session: pattern module (`feature/pattern-module`)

## Goal

A specified, tested pattern module (grammar, matcher, errors, API) that rules and filesets share;
then the typed layer (`Xake.Typed`) on top. Restart of `feature/typed-matches` (see `brief.md`).

## PR, branch, worktree

| PR | Branch | Worktree |
|----|--------|----------|
| — integration branch (target of every phase PR; merges to `dev` at the end) | `feature/patterns2` (cut from `dev` 2026-10-31, pushed 2026-11-01) | — (no worktree; PR target only) |
| [#118](https://github.com/OlegZee/Xake/pull/118) merged: PR 2 parser (453 vectors, `PatternAst.fs`) | `feature/pattern-parser` (from `feature/pattern-module`) | `.claude/worktrees/feature/pattern-parser` |
| PR 4 (scanner): 308 vectors, `PatternScan.fs` walker; D38, D39, D40 (provisional) | `feature/pattern-scanner` | `.claude/worktrees/feature/pattern-scanner` |
| [#119](https://github.com/OlegZee/Xake/pull/119) merged: PR 3 matcher | `feature/pattern-matcher` | `.claude/worktrees/feature/pattern-matcher` |
| [#117](https://github.com/OlegZee/Xake/pull/117) merged: PR 1 docs | `feature/pattern-module` (rebased on `dev` 2026-10-31) | main checkout `/Users/olegz/projects/Xake` (the worktree was removed 2026-10-31; the branch was reset to `c65d764`) |
| — frozen reference, **never merge** | `feature/typed-matches` | `.claude/worktrees/feature/typed-matches` |

The frozen worktree has an uncommitted user edit to `experiments/08-consolidated/consumer/showcase.fsx`:
leave it alone.

## State

- 2026-10-26 (later session): `spec.md` drafted (opus agent). Core proposal: one grammar/AST, two semantics levels (Compat = 3.13 default with warnings, Standard = 4.0 default, opt-in via `XAKE_PATTERNS`). Open items 1-15 answered with recommendations in spec section 11; conflicts C1-C10 in section 12. Review with the user started.
- 2026-10-31: D1 revised: Standard only, no Compat (`dev` is already 4.0). D2: integration branch `feature/patterns2`. Branch rebased on `dev`. Next: an opus agent strips Compat from `spec.md`, then the item-by-item review continues from item 1.
- 2026-10-26: folder created with `brief.md`, `kickoff.md`,
  `session.md`, `tracker.md`. Nothing else done: no spec, no tests, no code.

- 2026-11-01: spec review done. Decisions D1-D29 in `brief.md`; `spec.md` (1720 lines) revised
  for all of them plus the glob critique (`critique-glob.md`); explicit-name rule traced in
  `explicit-name-history.md` and kept (D13). Spec status: complete, pending the user's approval.
  Integration branch `feature/patterns2` created locally from `dev` (push is the user's).

## Next step

The user approves `spec.md` (Phase 1 exit). Then: ask how Phase 2 (conformance vectors) is
sliced into PRs against `feature/patterns2`; the Phase-2 measurements (D17: symlinks/junctions,
OS-listing quirks; 3.x results of `a//b`, `""`, `.` after a wildcard) go first, haiku agents.
