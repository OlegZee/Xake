# Idle performance: session

Branch `feature/idle-perf` from `5afb94b` (= `v5.0.0-preview.2`).

## Where things stand (2026-11-19)

- Analysis done, no code committed. Cause, numbers and options: [brief.md](brief.md).
- In short: a no-change run spends its time in the group-start walk of the no-build verdict; each
  file dependency is one `Observe` round trip to the database agent (181,764 in the benchmark),
  serial. 4.0 statted on the calling thread.
- The prototype of option 1 (with timing instrumentation and a static for the view, both to drop)
  is `idle-perf-prototype.diff` in the orchestrator's shared notes folder, not in the repository.
- Benchmark: `bench/build.fsx` (prepend `#r "nuget: Xake, <version>"` or `#r` of the local dll;
  `mksrc`, then `-t 16 build` twice).
- FS0041 on a recursive recipe reproduced on preview.2 (see brief.md); the release-notes line is
  not written yet.

## Next step

design.md (third version, by layers); the owner said "go async"; each step stops for review.
- Step 1 (L0-L1) 6473eff: pure `Storage.FileRule`.
- Step 2 (L2-L5) bc86f35: `Storage.Db` (snapshot, `Apply`, `Store` awaited, `NewRun`), hasher,
  `RunFiles`, the facade `Storage.observeFiles`.
- Step 3 (L6-L7) done: `DependencyAnalysis.Verdicts` (async verdict in two phases, memo per run
  with a wait-for map against cycles, `Warm` in parallel, `Ready`, `Estimate` from the snapshot),
  `ExecContext.UpToDate` async, `planRun` warms whatever the schedule, `dryRun` on `Verdicts`,
  plain files of a `need` list in one `observeFiles Recording` (`prepareOne`/`execParallel`).
  Tests: VerdictsTests.fs (two entries into one cycle, self-dependency, estimate); they hang
  without the guard. Full suite: 1,886 passed, 0 failed.
- Measured (cold-hot.md, "Step 3 measured"): no-change 0.16-0.19 s (4.0.0: 0.59-0.66 s); full
  build 0.97-1.34 s (4.0.0: 0.41-0.47 s), over the 2x gate.
- Writer (owner's option b) done: the agent publishes and replies; a writer mailbox owns the
  path table and the stream, encodes and appends in the agent's order; `Close` drains it. Full
  build 0.59-0.83 s (4.0.0: 0.47-0.54 s), no-change 0.19-0.22 s (4.0.0: 0.59-0.64 s); process
  wall times in cold-hot.md. Full suite: 1,887 passed, 0 failed.
- Release notes (4373335; FS0041 in its own commit cf1ac65) and docs/implnotes.md done.
- PR: OlegZee/Xake#148 (feature/idle-perf -> dev, squash). Next: CI green, the owner's review
  and merge; then ask the consumer to re-run their large build (no-change and full) on the next
  preview.
