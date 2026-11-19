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

Wait for the owner's choice of option. Then implement option 1 without the static (the view on
the agent's handle or in the `NewRun` reply), add a guard, write the release notes (fix + FS0041
line), run `dotnet test src/tests`, and re-run the bench against 4.0.0.
