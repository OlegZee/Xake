# Idle performance: tracker

- [x] Reproduce: synthetic no-change run, 4.0.0 0.6 s vs preview.2 2.4 s (`bench/build.fsx`)
- [x] Split the time: the group-start verdict walk, one agent round trip per dependency (brief.md)
- [x] Prototype option 1: 0.47-0.59 s (diff kept outside the repo, not committed)
- [x] Verify FS0041 for a recursive recipe on preview.2
- [x] Prototype the batched query (option 4): 0.66-0.93 s
- [x] Measure cold/hot, the agent's time per file, the full build (cold-hot.md)
- [x] Owner's decisions: no RunSynchronously, no I/O in the database, batch per target, existence per dependency
- [x] Design rewritten (design.md, 2026-11-20)
- [ ] Owner accepts the design
- [ ] The fix (design.md §2-§4)
- [ ] Tests (design.md §6) and the bench gates
- [ ] Release notes "5.0.0 (unreleased)": the fix; the FS0041 line for recursive recipes
