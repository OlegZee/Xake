# Session: db-paths-simple (Xake 5.0 phase 1)

**Where things stand (2026-11-18).** Phase 1 is implemented on `feature/db-paths-simple`: `File`
as Xake's normalised name, the case policy, database 0.7 with epochs and `LegacyRead`, the lock,
the read API. The specification as built is [brief.md](brief.md); the long-term engine rules are
in [implnotes.md](../../implnotes.md); the work items and the Later list in
[tracker.md](tracker.md). Core builds with 0 warnings; the suite and `build.fsx build test pack`
are green on macOS; the Windows run is done on the VM before each push.

**Next step.** The owner reviews and squash-merges OlegZee/Xake#141 into `dev`; after the merge,
remove the worktree. Phase 2 (rule header, `also`) starts in a new feature folder.

| PR | Branch | Worktree |
|---|---|---|
| [OlegZee/Xake#141](https://github.com/OlegZee/Xake/pull/141) | `feature/db-paths-simple` | `.claude/worktrees/db-paths-simple-39d1fe` |
