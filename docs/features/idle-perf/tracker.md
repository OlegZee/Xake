# Idle performance: tracker

- [x] Reproduce: synthetic no-change run, 4.0.0 0.6 s vs preview.2 2.4 s (`bench/build.fsx`)
- [x] Split the time: the group-start verdict walk, one agent round trip per dependency (brief.md)
- [x] Prototype option 1: 0.47-0.59 s (diff kept outside the repo, not committed)
- [x] Verify FS0041 for a recursive recipe on preview.2
- [x] Prototype the batched query (option 4): 0.66-0.93 s
- [x] Measure cold/hot, the agent's time per file, the full build (cold-hot.md)
- [x] Owner's decisions: no RunSynchronously, no I/O in the database, batch per target, existence per dependency
- [x] Design rewritten (design.md, 2026-11-20)
- [x] Owner: go async; design by layers (design.md, third version)
- [x] Step 1: pure FileRule (L0-L1), agent calls it, table tests
- [x] Step 2: snapshot, Apply, hasher, RunFiles, Storage.observeFiles (L2-L5); measured
- [ ] Step 3: async verdict and memo, Warm, callers (L6-L7)
- [ ] Tests per layer and the bench gates
- [ ] Release notes "5.0.0 (unreleased)": the fix; the FS0041 line for recursive recipes
