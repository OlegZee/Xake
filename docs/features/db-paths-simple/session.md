# Session: db-paths-simple (Xake 5.0 phase 1)

**Where things stand (2026-11-18).** Branch `feature/db-paths-simple` from `dev` `1837856`. No code
yet. [design.md](design.md) carries the owner's decisions of 2026-11-18: one-level `DirRec`,
`StaticCount` without need groups, and the content model by **epochs** (epoch = run number;
`FileRec (stamp, size, hash?, changed)` per file, `built` per result, dependencies by id only,
exclusive database, built-in SHA-256 truncated to 8 bytes, no history). The per-run epoch replaced a
per-event counter shown in the discussion: the per-event one reruns dependents of a dynamic `need`
on every run.

**Decided later on 2026-11-18:** the verdict moves to epochs already in phase 1 (without hashing);
the database stays incremental (checked in design.md 1b). The decided 0.7 layout was measured on the
owner's real 84.5 MB database: 5.75 MB as is, 3.39 MB compacted (5.0 / 3.1 MB with varint list
lengths). Lists get a varint length in 0.7; the string table is Later; always upgrade, no record length.
Questions 1-4 answered (root-relative paths with roots given in the constructor and never written,
room for several roots; the database stays a public analysis tool; fileset case accepted, opt-out
asked; `Fingerprints` ordinal). The proposals were accepted the same day with changes: `File`
holds the absolute path (`Name` absolute), `Built` kept, filesets case-insensitive by default with a
`casesensitive` option. The orchestrator confirmed the reading of "`File` holds the
absolute path" and that phase 1 has one root (`openDb root`, `ProjectRoot` only; the root number in
`DirRec` is the only room kept for more roots). The design was **approved on 2026-11-18**; the normative spec is [decision.md](decision.md).

**Implementation (2026-11-18, separate session).** Phase 1 with A1-A9, then B1-B3, the review
fixes R1-R4 and R7, the simplifications a-j and the review's test gaps are committed; all tests
green, `build.fsx build test pack` green. B4 (`Path.matches` ignores case on every OS) is in.
Re-checked by the orchestrating session (0 warnings, 1795 passed / 13 skipped / 0 failed), pushed,
PR OlegZee/Xake#141 opened 2026-11-18. **Next step:** the owner's review and squash-merge into `dev`.

Measured (2026-11-18): synthetic 1000 results x 30 file dependencies (mean path 118 B): 0.6
3 960 017 B, 0.7 196 590 B (20.1x). A real 0.6 database of 88 564 308 B (84.5 MB) upgrades to
3 069 697 B (28.9x; the upgrade writes the live results only: 1 932 results, 24 687 files) in
2.9 s (1.3 s with the mutable path table of the first version: the immutable maps of B1).

Notes from the implementation:
- `Path.matches` ignores case on every OS, as filesets (B4).
- A fileset ignoring case on a Unix host lists directories instead of probing literal names; a
  probe hit takes the spelling on disk, an ordinal search drops a hit spelt otherwise (R2).
- `Target.Display root` is the engine's output name; `ShortName` (the absolute path) stays in the
  worker pool's Info/Never lines, which have no root.
- `DbRecord` is `RequireQualifiedAccess` (its `Result` case would shadow F#'s `Result`).
- `DatabaseApi`: `Observe` with an `ObserveMode` (`Verdict`, `Recording` A1, `Output`), `NewRun`
  (replies the run's epoch), `InMemory` (`--dump`, `--dryrun` post it and a `NewRun`).
  `ExecContext.RunEpoch`; `BuildLog.Database.Epoch`; `readDatabase` returns the database.
- `Storage.openDbWith` (internal) takes the `--resetdb` flag (A5); `cleanupDb` takes the lock.
- A9 as implemented: a file target is checked as a target when a rule matched it, a plain file
  passing through `execOne` as a file dependency (it is recorded as `FileDep` there).
- A torn write is cut back to the end of its last whole record, which may be a new `PathRec`
  written in the same `Write` as the torn record (harmless: an id with no user).
- Line budget against `dev`: `Database.fs` net +214, `LegacyRead.fs` +120, core net +473, tests
  net +645 (before B1-B3: +424, -, +580, +487).

**Next step (original plan).** Implementation in a separate session (Opus, medium effort) on this branch and
worktree: docs first (`docs/` + release notes "5.0.0 (unreleased)"),
then code in the order 1a -> 1b -> 1c, tests, the size measurement. No push, no PR without the owner.

## Orchestration (2026-11-18)

- Implementation: a separate session in this worktree, on `feature/db-paths-simple`, spec
  [decision.md](decision.md); it commits per step, no push.
- Critic of brief + decision: a subagent on a stronger model; correctness bugs go to the
  implementation, the rest to Later.
- Code review of the implementation when it reports; then push and the PR for phase 1.

| PR | Branch | Worktree |
|---|---|---|
| [OlegZee/Xake#141](https://github.com/OlegZee/Xake/pull/141) | `feature/db-paths-simple` | `.claude/worktrees/db-paths-simple-39d1fe` |
