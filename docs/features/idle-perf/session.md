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

The owner chose option 4, the batched verdict query (brief.md, Decisions). The design and the
critic's 13 remarks with proposals are in design.md (§8). Wait for the owner to accept the design
and the remarks; then implement per design.md §1-§7 as amended by §8, the tests of §5 and §8,
the release notes (the fix; FS0041 in its own commit), `dotnet test src/tests`, and the bench
against 4.0.0 (gate: within 1.5x).
