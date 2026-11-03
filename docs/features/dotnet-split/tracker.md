# Tracker: dotnet split

Steps from [brief.md](brief.md) §9 (revised 2026-11-01 for one package, S-D1/S-D2). Release-process
rows: D1 = this brief, D2 = S5, D3 = S6-S8.

| # | Item | Status |
|---|---|---|
| D1 | design note: seam, one-package layout, versioning, build/CI, old repo, sequencing | approved (merged as #121); S-D1–S-D3 applied |
| A0 | owner approves the revised note | done: #121 merged; owner re-read 2026-11-03, no questions |
| S1 | Xake: `ProcessExec` public, `hasRule`, `memoize` -> local cache, IVT `Xake.Dotnet` removed, hermetic `Lock.fs:1584` -> `withLogger` (B4) | done: #122 merged 2026-11-02 |
| S2 | Xake.Dotnet: tag `legacy-1.x`, delete `feature/refactoring-core` (owner) | not started |
| S3 | Xake: tag `v4.0.0-preview.1` | open: owner tags (S1 done) |
| S4 | Xake.Dotnet: scaffold + rehearsal history import, one-package `build.fsx`, testjobs, workflows (incl. windows leg) on the preview | waits for S3 |
| S5 | Xake: pattern-module Phase 4/5 incl. hermetic/`Xake.Dotnet` on 4.0 patterns (C3, `dir/**`), `feature/patterns2` -> `dev` (the gate) | pattern-module work |
| S6 | Xake.Dotnet: final history import from `dev` after S5, on a preview built after S5 | waits for S5, S4 |
| S7 | Xake: remove `src/dotnet`, `src/hermetic`, moved tests/docs/samples; `src/core` packs `Xake`; build/CI/publish reduced | waits for S6 |
| S8 | release: Xake 4.0.0, then one tag `v4.0.0` = package `Xake.Dotnet` 4.0.0 (both assemblies) | waits for S7 |
| S9 | bootstrap bumps, unlist 1.1.x betas, deprecate and unlist `Xake.Hermetic.Dotnet`, wiki `#r` lines, session docs of both repos | waits for S8 |
| Q-open | hermetic breaking changes under one version line: minor bump (proposed) or major | closed by S-D3 (2026-11-02): minor bump while "API may change" |
