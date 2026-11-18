# Session: rule-header (Xake 5.0 phase 2)

**Where things stand (2026-11-18).** Implemented and pushed as branch `feature/rule-header` (from
`dev` at `b6aa7c1`); PR [OlegZee/Xake#142](https://github.com/OlegZee/Xake/pull/142) to `dev`, squash. The specification as built is [brief.md](brief.md); work
items and the Later list in [tracker.md](tracker.md). Checked before the push: core Release 0
warnings; 1828 tests green on macOS; `build.fsx build test pack` green; Windows VM (`ssh win11`)
core 0 warnings, 1803 passed, 21 skipped. The owner accepted the size (519 changed core lines).

**Next step.** CI on the PR, then the owner's squash-merge. After the merge, remove the worktree
and branch (the owner's action); phase 3 (hashing and content cut-off) in a new feature folder.

| PR | Branch | Worktree |
|---|---|---|
| [OlegZee/Xake#142](https://github.com/OlegZee/Xake/pull/142) | `feature/rule-header` | `.claude/worktrees/dep-cutoff-dabef6` |
