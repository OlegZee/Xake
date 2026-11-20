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
