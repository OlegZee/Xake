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

**Implementation (2026-11-18, separate session).** Step 1 (docs) committed; step 2 (1a `File`, case, two spellings) in progress.

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
| (not opened yet) | `feature/db-paths-simple` | `.claude/worktrees/db-paths-simple-39d1fe` |
