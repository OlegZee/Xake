# Release process — session

Branch `docs/release-process`, worktree `.claude/worktrees/docs/release-process`.

## Where things stand (2026-10-29)

- Hermetic 0.9.0 released on Xake 3.14 (#104, tag `hermetic-v0.9.0`, nuget.org `0.9.0.46`).
  The bootstrap bump of `build.fsx` / `build.fsc.fsx` to `3.14.0.45` / `0.9.0.46` is not done.
- [review.md](review.md): the release procedure, its causes, the release history 3.4..3.14 and
  options A-E with a recommendation. Waiting for the owner's decision (review.md §6).

**Next step**: the owner answers review.md §6; then the decided options become tracker items.

| PR | Branch | Worktree | Status |
|---|---|---|---|
| #104 hermetic 0.9.0 | `pr/hermetic-090` | `.claude/worktrees/pr+hermetic-090` | merged (02de528), tagged; worktree to remove |
| review | `docs/release-process` | `.claude/worktrees/docs/release-process` | draft |
