# Tracker: hermetic-windows

One line per item.

- [x] Analyses: `analysis/lock-content.md`, `analysis/engine.md`, `analysis/import-msbuild.md`
- [x] Brief: `brief.md` (verdicts, options, decision list, PR split, roadmap corrections)
- [x] Roadmap scrub: consumer names removed from the seven 2026-10-26 sections
- [x] Scrub the customer abbreviation in `analysis/lock-content.md` ("Tests")
- [x] Owner interview over `brief.md` section 4
- [x] Roadmap corrections (`brief.md` section 6) applied to `docs/roadmap.md`
- [x] PR1 lock portability: (a) line-ending-neutral hashes + (b) packs from NuGet (hermetic 0.8), branch `pr/lock-eol-packs`, not yet merged
  - [x] (a) `FileHash.textSha256`/`textMatches`: Imports, App.Files, own App.config; staleOf, Apps.write, rehash
  - [x] (b) `RecordOptions.PacksFrom = NuGet | Installed`, NuGet default (`NetCoreTargetingPackRoot`)
  - [x] (b) follow-up: a restore failure for a missing pack on a private feed names the cause (brief §3 b)
- [x] PR2 import diagnostics: (c) msbuild errors + run in `ProjectRoot`, (d) MSBuild floor check
  - [x] PR2-core (`pr/msbuild-diagnostics-core`): `Tool.diagnosticLevel` msbuild/NuGet shapes,
    `Tool.DiagnosticLog` (warnings once per run, error summary), the msbuild task, csc/fsc and the
    core's `dotnet restore` runners; Xake 3.13
  - [x] PR2-hermetic (`pr/import-diagnostics`): local `MsbuildLog` classifier + summary in `runMsbuild` and `Described` restore, msbuild in `ProjectRoot`, `dotnet msbuild -version` floor check, docs
  - [x] Hermetic adopts the core classifier: `Xake [3.13.0.43, 3.14)`, `MsbuildLog` keeps only the per-engine-run warning dedupe and the floor check
- [x] PR3 secrets, core part: `pr/secret-vars` (secret vars, `SecretDep`, db 0.6, masking, `-d` rules; release note under 3.13.0 because v3.12.0 is already tagged)
- [x] PR3 secrets: core delivered (#96); the hermetic `Environment` fields moved to the backlog below
- [x] PR4 log fixes: dropped by the owner (not now); defects recorded in the roadmap
- [x] PR5 satellites: deferred, S71 stays closed (roadmap)
- [x] bootstrap bump build.fsx / build.fsc.fsx to 3.13.0.43 / 0.8.0.44
- [x] Delivered: #93, #94, #95, #96, #97; releases v3.13.0 (3.13.0.43), hermetic-v0.8.0 (0.8.0.44); series closed 2026-10-27
- Backlog (roadmap, "Backlog from the Windows findings"):
  - [ ] `Import.RecordOptions.Environment` / `Project.ImportOptions.Environment`
  - [ ] Floor check through the SDK probe + `MSBuild.deps.json` (`sdkDir` public in the core)
  - [ ] Confirm the 17.10 floor on an SDK 8.0.2xx (decision 13)
  - [ ] Verify a missing pack root on SDK 8 and 9 imports (the tests ran on SDK 10.0.402)
  - [ ] Build log defects (flush on exit, name clash, relative name, hard-coded "See build.log")
  - [ ] Satellite assemblies (S71)
  - [ ] Consumer validation on Windows
