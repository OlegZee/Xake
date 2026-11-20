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

design.md (third version, by layers) is the working design; the owner said "go async". Work
goes step by step, each stopping for the owner's review:
- Step 1 (L0-L1) done (6473eff): the pure `Storage.FileRule`, table tests.
- Step 2 (L2-L5) done: `Storage.Db` (the agent with `Apply`/`Store`/`NewRun` only, a published
  snapshot, the hasher), `Storage.RunFiles` in `ExecContext` (per run), the facade
  `Storage.observeFiles`; callers in DependencyAnalysis/ExecCore use it; `Store` is awaited and
  the A3 observation moved to the caller. Measured (cold-hot.md, "Step 2 measured"): full build
  5.5-8.0 s -> 1.51 s, no-change 2.3 s -> 0.87-0.92 s (4.0.0: 0.41-0.45 s and 0.58-0.63 s).
  Full suite: 1,883 passed, 0 failed.
- Next, after the owner's review of step 2: step 3 (L6-L7): the async verdict and memo with the
  wait-for map, `Warm` on several threads, durations from the snapshot, `dryRun`, async
  `UpToDate`, one `observeFiles Recording` per `need` list.
