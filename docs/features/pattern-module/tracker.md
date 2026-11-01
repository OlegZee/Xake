# Tracker: pattern module

- [x] Starting documents: brief, kickoff, session, tracker
- [x] Phase 1: spec draft (`spec.md`, 1272 lines) by an opus agent from the brief and the frozen branch (2026-10-26)
- [x] Phase 1: spec review with the user, open items 1-15 of the brief decided (2026-11-01, D1-D23)
- [x] Phase 1: spec revised for D3-D23 and the glob critique (2026-11-01)
- [x] Phase 1: user approves the spec (2026-11-01, D30)
- [x] Phase 2: measurements on macOS (`measurements-engine.md`, `measurements-dotnet.md`, 2026-11-01); Windows-only cases (Win32 `?`, DOS wildcard mapping, junctions) still unmeasured
- [ ] Phase 2: conformance vectors (table-driven, glob-style cases, on-disk corpus), failing/skipped
- [ ] Phase 2: user approves the vectors
- [ ] Phase 3: module implementation (one AST, one matcher) passing the vectors; PR to `dev`
- [ ] Phase 4: migrate rule matching and `Fileset` onto the module; compat corpus green; PR
- [ ] Phase 4: release notes for behaviour changes and warnings
- [ ] Phase 5: port the typed layer (#91, `Xake.Typed`); PR
- [ ] Phase 5: docs (#92: typed-targets guide, overview, cheatsheet); `=>` to `Command` announcement; PR
- [ ] Decide the version (brief item f)
