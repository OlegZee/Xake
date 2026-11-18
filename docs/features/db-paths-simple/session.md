# Session: db-paths-simple (Xake 5.0 phase 1)

**Where things stand (2026-11-18).** Branch `feature/db-paths-simple` from `dev` `1837856`. No code
yet. [design.md](design.md) carries the owner's decisions of 2026-11-18: one-level `DirRec`,
`StaticCount` without need groups, and the content model by **epochs** (epoch = run number;
`FileRec (stamp, size, hash?, changed)` per file, `built` per result, dependencies by id only,
exclusive database, built-in SHA-256 truncated to 8 bytes, no history). The per-run epoch replaced a
per-event counter shown in the discussion: the per-event one reruns dependents of a dynamic `need`
on every run.

**Open:** design.md questions 1-5; question 5 (move the verdict to epochs, without hashing, already
in phase 1) changes the scope of the brief.

**Next step.** The owner moves on to database storage optimisation (what remains after paths and
epochs); then approval of the design, then docs first (`docs/` + release notes "5.0.0 (unreleased)"),
then code in the order 1a -> 1b -> 1c, tests, the size measurement. No push, no PR without the owner.
