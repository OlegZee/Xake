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
- Step 1 (L0-L1) done: `Storage.FileStat` and the pure `Storage.FileRule` (decide,
  decideHashed, classify) in Database.fs; the agent calls them, no behaviour change;
  src/tests/FileRuleTests.fs (15 table tests). Full suite: 1,875 passed, 0 failed.
- Next, after the owner's review of step 1: step 2 (L2-L5: snapshot, `Apply`, hasher, RunFiles,
  the `Storage.observe` facade), then measure no-change and full build.
