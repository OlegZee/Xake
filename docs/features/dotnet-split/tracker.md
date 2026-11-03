# Tracker: dotnet split

Steps from [brief.md](brief.md) §9 (revised 2026-11-01 for one package, S-D1/S-D2). Release-process
rows: D1 = this brief, D2 = S5, D3 = S6-S8.

| # | Item | Status |
|---|---|---|
| D1 | design note: seam, one-package layout, versioning, build/CI, old repo, sequencing | approved (merged as #121); S-D1–S-D3 applied |
| A0 | owner approves the revised note | done: #121 merged; owner re-read 2026-11-03, no questions |
| S1 | Xake: `ProcessExec` public, `hasRule`, `memoize` -> local cache, IVT `Xake.Dotnet` removed, hermetic `Lock.fs:1584` -> `withLogger` (B4) | done: #122 merged 2026-11-02 |
| S2 | Xake.Dotnet: tag `legacy-1.x`, delete `feature/refactoring-core` (owner) | done 2026-11-03: `legacy-1.x` at ba4acca pushed (S-D4), branch deleted; `dev` default (S-D10) |
| S3 | Xake: tag `v4.0.0-preview.1` | done 2026-11-03: af45852 on nuget.org (third publish attempt: deps.json race, then an expired API key) |
| S4 | Xake.Dotnet: scaffold + rehearsal history import, one-package `build.fsx`, testjobs, workflows (incl. windows leg) on the preview | done 2026-11-03: scaffold on Xake.Dotnet `dev` (0422f3d), on preview.2 |
| S3b | Xake: stop packing `Xake.Dotnet.dll` into `Xake` (S-D14), tag `v4.0.0-preview.2` | done 2026-11-03: #129 merged, `v4.0.0-preview.2` (ebfab1e) on nuget.org |
| S5 | Xake: pattern-module Phase 4/5 incl. hermetic/`Xake.Dotnet` on 4.0 patterns (C3, `dir/**`), `feature/patterns2` -> `dev` (the gate) | done 2026-11-03: #131 merged (fe842d5) |
| S6 | Xake.Dotnet: final history import from `dev` after S5, on a preview built after S5 | done 2026-11-03: Xake.Dotnet PR #1 merged with a merge commit (6535bf8), on `4.0.0-preview.3`; CI green on `dev` |
| S7 | Xake: remove `src/dotnet`, `src/hermetic`, moved tests/docs/samples; `src/core` packs `Xake`; build/CI/publish reduced | on `feature/dotnet-split-remove`, PR to `dev`: code/tests/build/CI/samples, docs and links; tests 1737 passed, 10 skipped |
| S8 | release: Xake 4.0.0, then one tag `v4.0.0` = package `Xake.Dotnet` 4.0.0 (both assemblies) | Xake half done 2026-11-03: `v4.0.0` (4297ea3) published; Xake.Dotnet 4.0.0 next |
| S9 | bootstrap bumps, unlist 1.1.x betas, deprecate and unlist `Xake.Hermetic.Dotnet`, wiki `#r` lines, session docs of both repos | waits for S8 |
| Q-open | hermetic breaking changes under one version line: minor bump (proposed) or major | closed by S-D3 (2026-11-02): minor bump while "API may change" |
