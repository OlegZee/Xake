# Architecture review of Xake 5 (database, observation, verdicts): decisions

Branch `feature/arch-review`, cut from `feature/idle-perf` at 6f39dbb (PR OlegZee/Xake#148); PRs
target `feature/idle-perf`. Agenda: [../idle-perf/architecture.md](../idle-perf/architecture.md),
section 7. One decision per line, with its date. Decisions are not re-litigated.

## Standing rules (from the owner)

- 2026-11-20: a library function is a pure, single-purpose function over Xake primitives.
- 2026-11-20: breaking changes are fine until the release.
- 2026-11-20: no behaviour or database format change unless decided; no format work and no
  untracked variables here (separate roadmap items).

## Decisions

- 2026-11-20 (topic 1): the database format (`Persist`: header, picklers, path table, read,
  encode) moves to its own file, `DatabaseFormat.fs`.
- 2026-11-20 (topic 1): `Agent` to Prelude.fs, `makeResult` and `Step` to Types.fs: agreed.
- 2026-11-20 (topic 1): layout A: `FileProbe.fs` (stat, hash, hasher: I/O only) and
  `FileStaleness.fs` (`FileRule`, `RunFiles`, `observeFiles`: the epoch/staleness logic as one
  part); the hasher leaves `Db`. Renames of functions are left for the public-API topic.
- 2026-11-20 (topic 1): a per-run memo of observations in place of the `RunFiles` claims is a
  separate topic (candidate 3), not part of the move.
- 2026-11-20 (topic 1): `FileProbe.fs` and the staleness module stay two files (the hasher is an
  agent of its own).
- 2026-11-20 (topic 1): `FileStaleness` is renamed `FileTracker`: it keeps file records in line
  with the disk, talks to the database, starts hashing and orders the observers of a run.
- 2026-11-20 (topic 2, [api-review.md](api-review.md)): the four modules are engine internals.
  Public: the read API (`readRecords`, `readDatabase`, `DbRecord`, `Database`, the format
  version, `tryGetResult`) and opaque handles (`Db` with `Snapshot`, `Hasher`, `RunFiles`: public
  type, internal constructor and members). Everything else internal; the raw mailbox becomes
  functions (`newRun`, `inMemory`, `close`, `store`, `apply`); `ExecContext.Observe` internal;
  `openDb` internal.
- 2026-11-20 (topic 2): names, option 4b: one namespace `Xake.Storage` over DatabaseFormat.fs and
  Database.fs, a module `Db` with the functions; `BuildLog` and `DatabaseApi` go as public names.
  `FileTracker.observeFiles` -> `FileTracker.observe`, `ObserveContext` -> `FileTracker.Context`.
- 2026-11-20 (topic 2, question 1): `Db.Dispose` = a synchronous close (drain, file, lock); closing
  again is harmless.
