# Idle performance: tracker

- [x] Reproduce: synthetic no-change run, 4.0.0 0.6 s vs preview.2 2.4 s (`bench/build.fsx`)
- [x] Split the time: the group-start verdict walk, one agent round trip per dependency (brief.md)
- [x] Prototype option 1: 0.47-0.59 s (diff kept outside the repo, not committed)
- [x] Verify FS0041 for a recursive recipe on preview.2
- [x] Prototype the batched query (option 4): 0.66-0.93 s
- [ ] Owner picks an option (brief.md, "Fix options")
- [ ] The fix, without a static (the view reaches the caller through the agent's handle or `NewRun`)
- [ ] A guard: a test that a repeat verdict observation does not post to the agent, or the bench in the docs
- [ ] Release notes "5.0.0 (unreleased)": the fix; the FS0041 line for recursive recipes
