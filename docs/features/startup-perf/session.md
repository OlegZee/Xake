# Startup performance: session

Branch `feature/startup-perf` from `407765f` (= `v5.0.0-preview.3`; PR #148 not merged into dev
yet). Analysis and measurement; fixes A and C implemented on the owner's word (2026-11-20).

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

- Fix A done (owner, 2026-11-20): `PathTable` keeps `NFiles`/`NDirs`; test `a large path table
  round-trips with its counters` (20,000 paths, 154 ms); full suite 1,888 passed. Measured with
  the commit: `openDb` 0.79-0.87 s on the 12.5 MB copy (was 1.33-1.79 s), 1.15-1.35 s on the
  17.3 MB copy; toy script on 17.3 MB 0.90-1.13 s (was 1.80-1.88 s). Release notes line added.

- Fix C done (owner, 2026-11-20): `Persist.record` takes `fileId`/`fileAt` instead of the table;
  `Persist.read` keeps the files by id in a `ResizeArray`; test `a file id without its path
  record fails the read`; full suite 1,889 passed. Toy script to the first recipe with A + C:
  12.5 MB 0.51-0.57 s, 17.3 MB 0.67-0.76 s, compacted 4.5 MB 0.30-0.31 s (preview.3: 1.46-1.48,
  1.80-1.88, 1.13-1.25 s). The harness copy in the notes folder predates the new `record`
  signature.

## Next step

The owner decides on the compaction policy (B1-B3); then a PR
(feature/startup-perf -> dev, squash) when the owner says so. Nothing is pushed.
