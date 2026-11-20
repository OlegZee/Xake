# Startup performance: session

Branch `feature/startup-perf` from `407765f` (= `v5.0.0-preview.3`; PR #148 not merged into dev
yet). Analysis and measurement only; no engine code changed.

## Where things stand (2026-11-20)

- Findings with numbers and options: [findings.md](findings.md).
- Q2: the 0.7 load is slow because `Persist.PathTable` numbers entries by `Map.Count` (O(n) on an
  F# `Map`), once per path record: quadratic. Fix A (two counters, ~10 lines) halves the load;
  C (id array while reading, ~10-15 lines) takes a further ~0.15 s.
- Q3: the growth is history (4 full rebuilds x ~2.1 MB); compaction at open, results > 5 x live,
  is not due yet. Options B1-B3 change when it runs.
- Q1: closed. The consumer ran the marks build (`Xake 5.0.0-preview.3.marks1`, local feed
  `~/xake-profiling/feed`, `marks2.diff` in the notes folder): `database open` is 2.72 s of their
  window at 17.3 MB; everything else before the first recipe takes milliseconds. Q1 is Q2.
- Not in the repository (session notes folder of the orchestrator, `startup-perf/`): the
  harness (`harness-Program.fs`, `harness-prof.fsproj`: a console app with AssemblyName `tests`
  so it sees the internals), `toy.fsx` and `run.sh`, the prototype diffs `fixA.diff`,
  `fixA+arr-experiment.diff` (a static switch, measurement only) and `marks.diff` (Stopwatch
  marks, `XAKE_MARKS=1`). The database copies are in `~/xake-profiling/consumer-db/`.

## Next step

The owner reads findings.md and decides on A, C and the compaction policy (B1-B3).
