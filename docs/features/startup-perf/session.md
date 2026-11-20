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
- Q1: open. The consumer's window starts at `XAKE build tool` (before the database opens) and
  ends at the first flush of their setup target `info`; the load is in it on both versions and
  costs about the same on both (their 4.x database is 361 MB), so 5.0's extra ~1 s is elsewhere.
  A marks build, `Xake 5.0.0-preview.3.marks1` in the local feed `~/xake-profiling/feed`
  (`marks2.diff` in the notes folder), was handed to the consumer's session for one run.
- Not in the repository (session notes folder of the orchestrator, `startup-perf/`): the
  harness (`harness-Program.fs`, `harness-prof.fsproj`: a console app with AssemblyName `tests`
  so it sees the internals), `toy.fsx` and `run.sh`, the prototype diffs `fixA.diff`,
  `fixA+arr-experiment.diff` (a static switch, measurement only) and `marks.diff` (Stopwatch
  marks, `XAKE_MARKS=1`). The database copies are in `~/xake-profiling/consumer-db/`.

## Next step

The owner reads findings.md and decides: fix A (and C?), and a compaction policy (B1, B2 or B3).
When the consumer's `[mark]` lines arrive: find the phase with ~1 s on 5.0, reproduce it here,
and add it to findings.md, Q1.
