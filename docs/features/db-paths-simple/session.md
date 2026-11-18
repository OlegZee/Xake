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
lengths). Lists get a varint length in 0.7; the string table is Later. **Open:** design.md questions 1-4.

**Next step.** Owner's answers to the open questions and approval of the design, then docs first (`docs/` + release notes "5.0.0 (unreleased)"),
then code in the order 1a -> 1b -> 1c, tests, the size measurement. No push, no PR without the owner.
