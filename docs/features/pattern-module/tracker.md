# Tracker: pattern module

- [x] Starting documents: brief, kickoff, session, tracker
- [x] Phase 1: spec draft (`spec.md`, 1272 lines) by an opus agent from the brief and the frozen branch (2026-10-26)
- [x] Phase 1: spec review with the user, open items 1-15 of the brief decided (2026-11-01, D1-D23)
- [x] Phase 1: spec revised for D3-D23 and the glob critique (2026-11-01)
- [x] Phase 1: user approves the spec (2026-11-01, D30)
- [x] Phase 2: measurements on macOS (`measurements-engine.md`, `measurements-dotnet.md`, 2026-11-01); Windows-only cases (Win32 `?`, DOS wildcard mapping, junctions) still unmeasured
- [x] Windows measurements: `measure-windows.fsx` run on Windows 11 ARM64 by the user; results in `measurements-windows.md`, pinned in the spec (2026-11-01); symlinks (no privilege), .NET Framework, unreadable directory stay unmeasured (6.4)
- [ ] PR 1 (docs): spec, decisions, measurements — #117 `feature/pattern-module` → `feature/patterns2`, open 2026-11-01
- [x] PR 2 (parser): vectors written (453 cases, ignored fixture; commit 510b0ab on `feature/pattern-parser`, worktree `.claude/worktrees/feature/pattern-parser`, 2026-11-01); 23 SPEC? notes to decide
- [ ] PR 2 (parser): #118 open 2026-11-01 (`feature/pattern-parser` → `feature/patterns2`); user confirms D34 (P-065, P-222, M-018 still SPEC?) and reviews
- [x] PR 2 (parser): `src/core/PatternAst.fs` passes all 453 vectors; 8 vectors aligned with D34 / spec 5.7 (separate commit); API notes in spec 8.4 (2026-11-01)
- [ ] PR 3 (matcher): match/capture/format vectors; regex-backed matcher
- [ ] PR 4 (scanner): fixture-tree vectors; walker
- [ ] Phase 4: migrate rule matching and `Fileset` onto the module; compat corpus green; PR
- [ ] Phase 4: release notes for behaviour changes and warnings
- [ ] Phase 5: port the typed layer (#91, `Xake.Typed`); PR
- [ ] Phase 5: docs (#92: typed-targets guide, overview, cheatsheet); `=>` to `Command` announcement; PR
- [ ] Decide the version (brief item f)
