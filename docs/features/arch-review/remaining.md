# Topic 12: the remaining candidates (8, 10, 11, 14, 15)

State at 97a46f9. All small; none changes behaviour except 15 (the hasher is stopped at the end).

| # | Today | Proposal |
|---|---|---|
| 8 | `ExecContext.Observe` builds a `FileTracker.Context` per call (a small record; no cost worth measuring). The A3 observation in `ExecCore.storeResult` (dependency files of a delegated result that have no record) uses `Verdict` mode with `Hashing = false`, so it claims them as a verdict would | Keep the code; a named step `recordUnseenDeps` with a comment saying why `Verdict` (claims: a later verdict waits for this record) and why no hashing (only the stat is needed to have a record) |
| 10 | `prepareOne` returns `Choice<File, Async<...>>`; callers match `Choice1Of2`/`Choice2Of2` | A named union `Prepared = PlainFile of File \| Scheduled of Async<Target * ExecStatus * Dependency>` |
| 11 | C2 (`Store` before the pool's `Done`) holds because `storeResult` is awaited inside the task's action | It has a test now (topic 10). A comment at the `storeResult` call sites names the order and the test |
| 14 | `getDurationDeps` carries "No clue why one method does both"; a target not warmed reads as 0 | Rename to `estimateOf` (the estimate's view of a target: its last own time and the dependees its verdict names, which is why both); the comment says a target `Warm` did not reach counts as up to date (0 ms, no dependees), which is right: `Warm` visits every target the estimate can reach |
| 15 | The hasher's mailbox and semaphore live until the process ends; the writer's failure lines say "Database agent" | `Hasher` is `IDisposable` (stops the mailbox, disposes the semaphore), disposed where the engine closes the database (`finalize`, `XakeEngine` stop); the writer's lines say "Database writer" |

One commit per candidate, or one for all five. Full suite after.
