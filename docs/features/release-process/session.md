# Release process — session

Working state of the release-process work on `dev`; each implementation PR updates this file
and [tracker.md](tracker.md).

## Where things stand (2026-10-29)

- [review.md](review.md): the release procedure, its causes, the release history 3.4..3.14 and
  options A-E. Decided (§6): A, B, E now; then D, after the vars (variant 3) and pattern module
  work, which D's scope includes. B4 deferred (owner, 2026-10-29).
- Done: A1 + E3 (#106), B1 (#107), B2 + B3 (#108). Xake 3.15.0 tagged 2026-10-29 (first
  release published as the tag version).
- In PR: E1 + E2 (`pr/bump-base`): `build.fsc.fsx bump-base`, the range written only in the
  fsproj, unversioned `#r` in the package readme, no version copies in devprocess/session docs.

**Next step**: tag `hermetic-v0.10.0` after the E1/E2 PR (first hermetic release with the 4.0
bound, published as `0.10.0`); then bump the bootstrap of `build.fsx` / `build.fsc.fsx` to
`3.15.0` / `0.10.0`. After that the release-process work waits for D0 (vars, pattern module).

| PR | Branch | Worktree | Status |
|---|---|---|---|
| #104..#108 | — | — | merged; worktrees removed by the owner (#108's: `.claude/worktrees/pr/hermetic-range-ci`) |
| E1 + E2 | `pr/bump-base` | `.claude/worktrees/pr/bump-base` | draft |
