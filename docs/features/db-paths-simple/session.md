# Session: db-paths-simple (Xake 5.0 phase 1)

**Where things stand (2026-11-18, end of session).** Phase 1 is done and in
[OlegZee/Xake#141](https://github.com/OlegZee/Xake/pull/141), CI green (build on Linux,
test-windows), mergeable; the owner merges it into `dev` by squash. The specification as built is
[brief.md](brief.md); the long-term engine rules are in [implnotes.md](../../implnotes.md); the work
items and the Later list in [tracker.md](tracker.md). Checked before the last code push: core 0
warnings, 1795 tests green on macOS, 1770 on the Windows VM, `build.fsx build test pack` green.

**What the session did.**
- Design with the owner (2026-11-17/18): `File` as Xake's normalised absolute name, one ordinal
  rule for files, targets and rules, filesets and masks ignoring case; database 0.7 with a path
  table relative to the project root (one-level `DirRec`, root numbers, roots never written);
  epochs (the run number) instead of per-dependency stamps; exclusive `.xake.lock`; always upgrade
  (old databases through `LegacyRead`); public read API. Measured on a real 84.5 MB database:
  3.07 MB after the upgrade (28.9x).
- A critic on the spec (overlapping engine runs, run groups, delegated rules, write failures, the
  lock, root spelling), an implementation in a separate session, a code review (R1-R7), then owner
  decisions after reading the code: a functional path table, the epoch in `ExecContext`,
  `LegacyRead`, `Path.matches` as filesets, `Name` vs `getSysFileName`/`getFileInfo` (`FullName`
  obsolete for about six months), int epochs, one case flag in `PatternMatch`, `Target.Name`.
- Windows CI: two causes fixed (the root form, the rule/mask root not normalised), then a test that
  left an engine and its lock alive. The docs were cut down to brief, tracker and session.

**Not verified.** A failed `Write` stopping appends until close (no way to inject one); the open
time of a 0.7 database with the immutable path table (the upgrade of the real database went from
1.3 s to 2.9 s); Windows-only paths ran in the normalisation table and the VM suite, not in a real
Windows build.

**Next step.** After the squash-merge, remove the worktree and branch (the owner's action):

```bash
git worktree remove .claude/worktrees/db-paths-simple-39d1fe
git branch -D feature/db-paths-simple
git push origin --delete feature/db-paths-simple
```

Then phase 2 (rule header, `also`) in a new feature folder; phase 3 (hashing and content cut-off)
builds on the 0.7 format as is. Xake.Dotnet moves to `File option` for `File.undefined` and to
`getSysFileName` at its 5.0.

| PR | Branch | Worktree |
|---|---|---|
| [OlegZee/Xake#141](https://github.com/OlegZee/Xake/pull/141) | `feature/db-paths-simple` | `.claude/worktrees/db-paths-simple-39d1fe` |
