# Tracker: hermetic-windows

One line per item.

- [x] Analyses: `analysis/lock-content.md`, `analysis/engine.md`, `analysis/import-msbuild.md`
- [x] Brief: `brief.md` (verdicts, options, decision list, PR split, roadmap corrections)
- [x] Roadmap scrub: consumer names removed from the seven 2026-10-26 sections
- [ ] Scrub the customer abbreviation in `analysis/lock-content.md` ("Tests")
- [ ] Owner interview over `brief.md` section 4
- [ ] Roadmap corrections (`brief.md` section 6) applied to `docs/roadmap.md`
- [x] PR1 lock portability: (a) line-ending-neutral hashes + (b) packs from NuGet (hermetic 0.8), branch `pr/lock-eol-packs`, not yet merged
  - [x] (a) `FileHash.textSha256`/`textMatches`: Imports, App.Files, own App.config; staleOf, Apps.write, rehash
  - [x] (b) `RecordOptions.PacksFrom = NuGet | Installed`, NuGet default (`NetCoreTargetingPackRoot`)
  - [ ] (b) follow-up: a restore failure for a missing pack on a private feed names the cause (brief §3 b)
  - [ ] (b) verify a missing pack root on SDK 8 and 9 imports (the tests ran the imports on SDK 10.0.402)
- [ ] PR2 import diagnostics: (c) msbuild errors + run in `ProjectRoot`, (d) MSBuild floor check
  - [x] PR2-core (`pr/msbuild-diagnostics-core`): `Tool.diagnosticLevel` msbuild/NuGet shapes,
    `Tool.DiagnosticLog` (warnings once per run, error summary), the msbuild task, csc/fsc and the
    core's `dotnet restore` runners; Xake 3.13
- [ ] PR3 secrets: (e) Xake 3.12 + hermetic `Environment` fields and range bump
- [ ] PR4 log fixes: (f) flush on exit, name clash, real `FileLog` name in messages (Xake)
- [ ] PR5 satellites: (g) after the owner reopens S71
