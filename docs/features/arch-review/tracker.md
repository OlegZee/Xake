# Tracker

| # | Item | Status |
|---|---|---|
| 1 | `Database.fs` holds only the database (owner's topic): map + layout | done: DatabaseFormat.fs, FileProbe.fs, FileTracker.fs; hasher in EngineState; `FileStaleness` renamed `FileTracker` |
| 2 | Public API of the four modules (with candidate 6, raw `DatabaseApi`) | done ([api-review.md](api-review.md)); `Dispose` closes |
| 3 | Per-run memo of observations in place of `RunFiles` claims (candidate 3) | open, owner's topic later |
