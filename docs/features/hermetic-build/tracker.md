# Tracker: hermetic-build

Status: `[ ]` todo · `[~]` in progress · `[x]` done · `[-]` dropped. Keep entries one line;
details live in brief.md (section refs) or session.md.

## Day zero (brief §8h/§8i/§8j)
- [x] E1 reproducibility of dataengine with stock `dotnet build` — green on origin/develop
- [x] E2 design-time import: DataEngine and page/Rdl (LocalBuild), both brands — complete
- [ ] E3 `packages.lock.json` stability and floating versions
- [x] E4 Babel `--randomseed` — deterministic except PE timestamp/checksum (+ eval expiry)
- [ ] E5 reproduce shipped `MESCIUS.ActiveReports.Core.*` nupkg vs local tag build

## Library slice 1 — import and compile (brief §11, §8j traps 1–7)
- [x] `Project.import`: design-time msbuild, per-brand `IntermediateOutputPath`, `-pp` imports list — `src/dotnet/Project.fs`, proven on dataengine develop (both brands) with `import.fsx`
- [x] `Lock` write/read: one file per (TFM, brand), tokenized paths, SHA-256 for libraries and compiler; generated inputs stored inline
- [x] `csc {}` verbatim `invocation` mode: inputs from args, `needFiles`, hash check, generated files written back, `/noconfig` kept out of the rsp — `compileFromLock` in `Dotnet.csc.fs`, `Lock.mapPaths` for project references
- [ ] `csc {}` compiler source: SDK or `Microsoft.Net.Compilers.Toolset`, recorded in the lock
- [ ] resgen recipe producing the `.resources` the imported command line expects
- [x] fsx over dataengine develop copy: `cmp` identical with `dotnet build` output — 18/18 files (dll, pdb, xml; 3 projects × 2 brands), `import.fsx build`, results in `samples/hermetic/dataengine/compare.txt`
- [ ] same over page with `LocalBuild=true`
- [x] tests in src/tests for import parsing, lock round-trip, invocation — `ProjectImportTests.fs` (6) and `CscInvocationTests.fs` (3: compiles from a hand-made lock and writes generated inputs, refuses a changed hash, `mapPaths`)

## Lock stability (raised 2026-09-22, decision: defer until someone diffs locks for real)
- [ ] split the lock file: dependencies (references, analyzers, compiler, imports — rare, reviewed) apart from compilation (args, sources, generated — every PR); framework/SDK its own section. `Lock.Project` in memory stays one record; only `Lock.write`/`parse` change
- [ ] `Generated` content: tokenize the commit sha (`$(SourceRevisionId)` in `AssemblyInfo.cs`, `sourcelink.json`) — without it the lock changes on every commit on a real checkout. Do when `import.fsx` first runs against a live repository instead of a `git archive` copy
- [x] SDK policy — decided 2026-09-22: `global.json` with the exact SDK is always in use, so a change in the `Compiler` section means "we updated the compiler" and is a reviewed diff. Nothing to build
- [ ] when splitting, also make the content structured instead of the raw tool output: sources, references, defines, options as fields, not a verbatim `csc` argument list. Keep §8c's fidelity by round-tripping at import: the command line rebuilt from the structure must equal msbuild's, or the import fails

## Slice 2 — SBOM and verification (brief §8e, §11)
- [ ] `Nuget.readAssets` / `readCache` (sha512, source, license, supplier)
- [ ] `Sbom.cycloneDx` deterministic; per assembly, per package; `formulation`
- [ ] compare with page's existing CycloneDX output (`GenerateSbom=true`)
- [ ] `Verify`: sha256, Authenticode PE hash, compare

## Slice 3 — ring 2/3 (brief §8f, §8g)
- [ ] Babel recipe with seed; PE timestamp normalisation + strong-name re-sign, or vendor option
- [ ] deterministic pack
- [ ] sign as delegated rule (later)

## Housekeeping
- [ ] release of `feature/hermetic-build` — deferred by the user; `#r` on `.bootstrap/` for now
- [ ] move brief/tracker into the commit once the user decides
