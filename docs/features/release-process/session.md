# Release process — session

Branch `docs/release-process`, worktree `.claude/worktrees/docs/release-process`.

## Where things stand (2026-10-29)

- Hermetic 0.9.0 released on Xake 3.14 (#104, tag `hermetic-v0.9.0`, nuget.org `0.9.0.46`).
  The bootstrap bump of `build.fsx` / `build.fsc.fsx` to `3.14.0.45` / `0.9.0.46` is not done.
- [review.md](review.md): the release procedure, its causes, the release history 3.4..3.14 and
  options A-E with a recommendation.
- Decided 2026-10-29 (review.md §6): A, B, E now; then D, after the vars (variant 3) and pattern
  module work, which D's scope includes. Items in [tracker.md](tracker.md).

**Next step**: implementation PRs in tracker order: A1 + E3 first, then B1 (core, rides the
next Xake release), B2/B3, E1/E2, B4 after the Xake release.

| PR | Branch | Worktree | Status |
|---|---|---|---|
| #104 hermetic 0.9.0 | `pr/hermetic-090` | `.claude/worktrees/pr+hermetic-090` | merged (02de528), tagged; worktree to remove |
| #105 review | `docs/release-process` | `.claude/worktrees/docs/release-process` | draft |
| #106 A1 + E3 | `pr/exact-versions` | `.claude/worktrees/pr/exact-versions` | draft |
