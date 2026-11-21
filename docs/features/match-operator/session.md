# Session: match-operator

**Where things stand (2026-11-19).** Branch `feature/match-operator` from `dev` at `fa5b33b`
(worktree `.claude/worktrees/match-operator-9e9a07`). The owner chose `let! m = matched` with a
strict indexer for every recipe ([brief.md](brief.md)); implemented with tests, see
[tracker.md](tracker.md).

`getRuleMatch` is obsolete (owner, 2026-11-19). Checked after merging `dev` (#145):
`build.fsx build test` 1853 passed, 13 skipped (macOS); core Release 0 warnings. Squash-merged on
the owner's word.

**Next step.** Open with the owner: a strict `?` operator on `RuleMatch` (`m?cfg`), and the name
(`matched` stays for now; `matchGroups` was asked about). After the merge, remove the worktree
and branch.

| PR | Branch | Worktree |
|---|---|---|
| [OlegZee/Xake#144](https://github.com/OlegZee/Xake/pull/144) | `feature/match-operator` | `.claude/worktrees/match-operator-9e9a07` |
