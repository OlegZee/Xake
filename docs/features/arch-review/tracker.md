# Tracker

| # | Item | Status |
|---|---|---|
| 1 | `Database.fs` holds only the database (owner's topic): map + layout | done: DatabaseFormat.fs, FileProbe.fs, FileTracker.fs; hasher in EngineState; `FileStaleness` renamed `FileTracker` |
| 2 | Public API of the four modules (with candidate 6, raw `DatabaseApi`) | done ([api-review.md](api-review.md)); `Dispose` closes |
| 3 | Per-run memo of observations in place of `RunFiles` claims (candidate 3) | open, owner's topic later |
| 4 | Two wait-for maps (candidate 1) | done, option a ([wait-for.md](wait-for.md)) |
| 5 | Simplify `FileRule.decide` (owner, via a side session): drop the dead `SeenByVerdict` branch, one small function per mode with its `decideHashed` next to it; optional: drop `PreHash`; then how `observe` uses the decision | done: split per mode, `SeenByVerdict` removed, `PreHash` kept ([decide.md](decide.md)) |
| 6 | How `observe` uses the decision (candidates 3, 4, 9) | done ([observe.md](observe.md)) |
| 7 | Engine state in `ExecContext` (candidate 7) | done: `ExecContext.Run` (`RunState`); `FileTracker`, `RunCounters` internal |
| 8 | Two verdict entry points, `Of` and `Fresh` (candidate 2) | done: `Verdicts.Planned`, `Verdicts.ForTask` |
| 9 | `FastGate` (candidate 13) | done: kept, reason stated, in `RunState` with `TaskWaits` |
| 10 | Test gaps (candidate 16); C12 hole: a failed stat in `claim` leaves claims open | done: C12 fixed (43d81e6); tests for C2, C4, C6, C13, `--dryrun` |
| 11 | Two epochs (candidate 12); finding: overlapping runs can miss a change | done: fresh epoch for a change in a run that is not the newest; test; `ChangeEpoch` |
| 12 | Remaining candidates 8, 10, 11, 14, 15 | proposed, [remaining.md](remaining.md); open after the merge |
