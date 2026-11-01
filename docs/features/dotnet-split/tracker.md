# Tracker: dotnet split

Steps from [brief.md](brief.md) §9. Release-process rows: D1 = this brief, D2 = S5, D3 = S6-S8.

| # | Item | Status |
|---|---|---|
| D1 | design note: seam, package layout, versioning, build/CI, old repo, sequencing | done (brief.md), waiting for the owner's answers (§11) |
| S1 | Xake: `ProcessExec` public, `hasRule`, `memoize` -> local cache, IVT `Xake.Dotnet` removed, hermetic `Lock.fs:1584` -> `withLogger` (B4) | not started; can merge before the pattern module |
| S2 | Xake.Dotnet: tag `legacy-1.x`, delete `feature/refactoring-core` (owner) | not started |
| S3 | Xake: tag `v4.0.0-preview.1` (if Q3 yes) | waits for S1 |
| S4 | Xake.Dotnet: scaffold + rehearsal history import, build.fsx, testjobs, workflows on the preview | waits for S3 |
| S5 | Xake: pattern-module Phase 4/5 incl. hermetic/`Xake.Dotnet` on 4.0 patterns (C3, `dir/**`), `feature/patterns2` -> `dev` | pattern-module work |
| S6 | Xake.Dotnet: final history import from `dev` after S5, on a preview built after S5 | waits for S5, S4 |
| S7 | Xake: remove `src/dotnet`, `src/hermetic`, moved tests/docs/samples; `src/core` packs `Xake`; build/CI/publish reduced | waits for S6 |
| S8 | release: Xake 4.0.0, then `Xake.Dotnet` 4.0.0 + `Xake.Hermetic.Dotnet` 0.11.0 | waits for S7 |
| S9 | bootstrap bumps, unlist 1.1.x betas, wiki `#r` lines, session docs of both repos | waits for S8 |
