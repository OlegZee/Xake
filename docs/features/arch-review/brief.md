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

(none yet; topic 1 proposal in [database-layout.md](database-layout.md))
