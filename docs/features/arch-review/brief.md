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
- 2026-11-20 (topic 2): a module is public or internal as a whole, no half-open modules.
  `EngineState` gets an internal representation; `FileProbe` is internal; `Db` (handle and
  module) is internal; the public read API is the module `DbReader`. `RunFiles` leaves
  `ExecContext` with candidate 7.
- 2026-11-20 (topic 4, [wait-for.md](wait-for.md)): option a: two maps (task waits; verdict waits:
  no cycle can run through both), one shared primitive `WaitFor`, `Fresh` registers no edges,
  names `TaskWaits` and `verdictWaits`.
- 2026-11-20 (topic 5, [decide.md](decide.md)): `FileRule` split per mode (`input`, `output`,
  `preHash`, each with its `...Hashed` next to it); `SeenByVerdict` removed; `PreHash` kept;
  the continuation in `NeedHash` not now. `LegacyRead` (and the unknown size) stays for now.
- 2026-11-20 (topic 6, [observe.md](observe.md)): accepted: `Outcome` (`decideHashed` ends in
  `Keep | Write`), `Pending` and the steps `claim`/`settle`/`answerLater`, comments on the claims,
  `maxRounds` with a Debug line (`Context.Log`). No test for the log line (it needs a fake `Db`).
- 2026-11-21 (topic 7, [engine-state.md](engine-state.md)): option a: `ExecContext.Run: RunState`
  (internal representation) holds `Progress`, `ShowProgress`, `NeedRebuild`, `UpToDate`,
  `Spellings`, `RunFiles`, `RunEpoch`, `Counters`; the public shape is `Engine`, `Run`, `Vars`,
  `Targets`, `RuleMatches`, `Ordinal`, `Logger`, `HeldResources`.
- 2026-11-21 (topic 7): with `RunState` opaque, `FileTracker` (the whole module) and
  `RunCounters` are internal (no half-open modules, as dd3f1ad).
- 2026-11-21 (topic 8, [of-fresh.md](of-fresh.md)): option a: `Verdicts.Planned` (was `Of`) and
  `Verdicts.ForTask` (was `ExecCore.verdict` + `Fresh`; `Fresh` a private step).
- 2026-11-21 (topic 9, [fastgate.md](fastgate.md)): option a: keep `FastGate` (measured: no gain
  without it, full builds slower and less stable), state the real reason, and move it and
  `TaskWaits` from `RunCounters` to `RunState`.
- 2026-11-21 (topic 10, [test-gaps.md](test-gaps.md)): fix the C12 hole (`claim` completes the
  claims it took when it fails) with its test; tests for C6, C4 (`claim`/`settle` internal), C13,
  C2, `--dryrun`, the as-requested estimate.
- 2026-11-21 (topic 11, [epochs.md](epochs.md)): option a: in a run that is not the newest (and
  has a run epoch), a settle round stamps changes with a fresh epoch from the agent; test from the
  reproduction; `RuleContext.Epoch` -> `ChangeEpoch`.
