# Tracker

| # | Item | Status |
|---|---|---|
| 1 | `Database.fs` holds only the database (owner's topic): map + layout | done: DatabaseFormat.fs, FileProbe.fs, FileTracker.fs; hasher in EngineState; `FileStaleness` renamed `FileTracker` |
| 2 | Public API of the four modules (with candidate 6, raw `DatabaseApi`) | done ([api-review.md](api-review.md)); `Dispose` closes |
| 3 | Per-run memo of observations in place of `RunFiles` claims (candidate 3) | open, owner's topic later |
| 4 | Two wait-for maps (candidate 1) | done, option a ([wait-for.md](wait-for.md)) |
| 5 | Simplify `FileRule.decide` (owner, via a side session): drop the dead `SeenByVerdict` branch, one small function per mode with its `decideHashed` next to it; optional: drop `PreHash`; then how `observe` uses the decision | proposed, [decide.md](decide.md); waiting for the owner |
