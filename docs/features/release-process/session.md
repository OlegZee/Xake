# Release process — session

Working state of the release-process work on `dev`; each implementation PR updates this file
and [tracker.md](tracker.md).

## Where things stand (2026-10-29)

- [review.md](review.md): the release procedure, its causes, the release history 3.4..3.14 and
  options A-E. Decided (§6): A, B, E now; then D, after the vars (variant 3) and pattern module
  work, which D's scope includes.
- Done: A1 + E3 (#106: published version = tag; bootstrap on 3.14.0.45 / 0.9.0.46), B1 (#107:
  `ExecContext.withLogger`, `RecordShapeTests`, the record rule in devprocess.md; ships in 3.15).
- In PR: B2 + B3 (`pr/hermetic-range-ci`): hermetic on `Xake [3.14.0.45, 4.0)` through an
  overridable `XakeVersion`; CI job `hermetic-compat` runs the hermetic tests on the commit's
  base (packed as `3.999.0-dev`) and on the newest Xake on nuget.org.

**Next step**: E1/E2 (`bump-base`, one source for the range), then B4 once 3.15 is published.

| PR | Branch | Worktree | Status |
|---|---|---|---|
| #104 hermetic 0.9.0 | `pr/hermetic-090` | `.claude/worktrees/pr+hermetic-090` | merged; worktree to remove |
| #105 review | `docs/release-process` | `.claude/worktrees/docs/release-process` | merged; worktree to remove |
| #106 A1 + E3 | `pr/exact-versions` | `.claude/worktrees/pr/exact-versions` | merged; worktree to remove |
| #107 B1 | `pr/exec-context-with-logger` | `.claude/worktrees/pr/exec-context-with-logger` | merged; worktree to remove |
| B2 + B3 | `pr/hermetic-range-ci` | `.claude/worktrees/pr/hermetic-range-ci` | draft |
