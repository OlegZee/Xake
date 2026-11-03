# Session: pattern module (`feature/pattern-module`)

## Goal

A specified, tested pattern module (grammar, matcher, errors, API) that rules and filesets share;
then the typed layer (`Xake.Typed`) on top. Restart of `feature/typed-matches` (see `brief.md`).

## PR, branch, worktree

| PR | Branch | Worktree |
|----|--------|----------|
| — integration branch (target of every phase PR; merges to `dev` at the end) | `feature/patterns2` (cut from `dev` 2026-10-31, pushed 2026-11-01) | — (no worktree; PR target only) |
| [#118](https://github.com/OlegZee/Xake/pull/118) merged: PR 2 parser (453 vectors, `PatternAst.fs`) | `feature/pattern-parser` (from `feature/pattern-module`) | `.claude/worktrees/feature/pattern-parser` |
| — Phase 5a (typed layer on the module, D48): PR pending | `feature/typed-port` (from `origin/feature/typed-port`) | agent worktree `.claude/worktrees/agent-a9684c9d8defcecc8` |
| [#124](https://github.com/OlegZee/Xake/pull/124) Phase 4b (Fileset scanning, D42): open; D44-D47 decided 2026-11-03 | `feature/pattern-fileset` | `.claude/worktrees/feature/pattern-fileset` (after the move) |
| — Phase 4b follow-up: D44-D47 applied, commits for #124 (owner pushes) | `feature/pattern-fileset-followup` (from `origin/feature/pattern-fileset`) | agent worktree `.claude/worktrees/agent-a98719db399551123` |
| [#123](https://github.com/OlegZee/Xake/pull/123) Phase 4a (rules, D42): open, D43 confirmed 2026-11-03 | `feature/pattern-rules` | `.claude/worktrees/feature/pattern-rules` |
| [#120](https://github.com/OlegZee/Xake/pull/120) PR 4 (scanner): 308 vectors, `PatternScan.fs` walker; D38, D39, D40 (provisional) | `feature/pattern-scanner` | `.claude/worktrees/feature/pattern-scanner` |
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

- 2026-11-02: Phase 4 split in two PRs (D42). Phase 4a done on `feature/pattern-rules`: rule
  matching on the module, creation-time checks, XP119, release notes (rules). Fileset scanning
  (4b) still runs the 3.x part matcher (`Path.LegacyMask`, `matchesPattern`).

- 2026-11-03: Phase 4b done on `feature/pattern-fileset` (PR pending): `Fileset` scanning on
  `PatternScan`, the 3.x part matcher deleted, hermetic's mask uses on `Path.matches`, release
  notes (masks). The import still writes msbuild `dir/**` as `dir/**/*` (same files in 4.0, no
  XP113 noise, lock text unchanged); open questions in the PR description.

- 2026-11-03 (later): Phase 4b is PR #124; the owner answered its four questions (D44-D47).
  Applied on `feature/pattern-fileset-followup`: the import escapes reserved characters in the
  masks and project paths it records (D45; `dir/**/*` kept, D44), `toFileList1` and `FileSystem`
  deleted (D46), spec 8.7 "Phase 4b notes" (D47). Trap: a recorded mask is not a path, so
  `Roots.tokenizeAll` (which turns `\` into `/`) must not touch it; use `Roots.tokenizeMask` /
  `expandMask`.

- 2026-11-03 (Phase 5a): the typed layer is ported onto the module on `feature/typed-port`
  (PR pending): `Xake.Typed` re-created from the frozen #91 on `PatternAst.Rule.check`,
  `checkOutputs`, `formatMatches` and a new `PatternAst.glob`; module errors (XP006, XP013, XP014,
  ...) reach typed users with the declaration in front. The docs snippet test of
  `TypedCompileTests` comes with 5b (`docs/typed-targets.md`).

## Next step

Fresh session: read `next-session.md` in this folder (hand-off of 2026-11-03).


Phase 4a: #123 open; D43 confirmed 2026-11-03; the owner reviews. Then Phase 4b (Fileset scanning on `PatternScan`), launched after #123 merges.
(`--repo OlegZee/Xake`). Then Phase 4b (D42): `Fileset` scanning on `PatternScan`, delete the
3.x part matcher, hermetic's direct mask uses, the mask items of the release notes.
