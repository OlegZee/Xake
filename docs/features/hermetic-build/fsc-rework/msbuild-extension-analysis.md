# A hermetic msbuild extension: feasibility and place in the plan

Status: analysis for the owner, 2026-10-12. Read-only research; no decision. Question: "set a narrow
goal and write an **msbuild extension** that builds hermetically, with lock files". Context:
[brief.md](brief.md) §1 (the fork: now the declarative fsc/csc route; later, separately, hermetic
builds of existing msbuild projects), [interview.md](interview.md) Q3/Q5, [paket-analysis.md](paket-analysis.md)
(NuGet lock facts reused, not redone), [../../../hermetic/lock.md](../../../hermetic/lock.md),
[../../../hermetic-architecture.md](../../../hermetic-architecture.md). URLs were fetched on
2026-10-12 unless marked **(search only)**; "(inferred)" is a conclusion from docs or source, not a
statement there.

## 1. A fact that frames the question

The msbuild route is not hypothetical: it is what `Project.import` already does, out of process.
It runs restore, a design-time build per TFM with `ProvideCommandLineArgs`/`SkipCompilerExecution`
(the `Csc`/`Fsc` task returns `CscCommandLineArgs`/`FscCommandLineArgs` instead of compiling), and a
`-pp` per TFM for the import list (`src/hermetic/Project.fs`). The lock format was designed around that
answer (`Evaluation.Imports/Sdk/SdkPin`, `Compilation.Generated`, `Packages`). Replaying such a lock
was **36/36 byte-identical to `dotnet build`** on dataengine and 90/90 on the larger fixture
(hermetic-architecture.md §7). What the declarative route gave up (byte identity, Q3) was given up by
*composing* options, not by leaving msbuild. So an extension is "the same capture, moved inside
msbuild's own build, with msbuild compiling instead of Xake replaying".

## 2. Extension points (what such a package can hook)

- **Distribution.** A NuGet package's `build/<id>.props|targets` are auto-imported into the project
  that references it; `buildTransitive/` reaches consumers. A custom MSBuild SDK
  (`<Sdk Name="..." Version="..."/>`) is the other shape; `DotNet.ReproducibleBuilds.Isolated` is one
  (https://github.com/dotnet/reproducible-builds). Without touching a repo, the standard
  `CustomBeforeMicrosoftCommonTargets`/`CustomAfterMicrosoftCommonTargets` global properties or an
  `ImportBefore` folder inject targets (inferred from Microsoft.Common.targets conventions; the
  `ImportBefore/ImportAfter` folders are documented at
  https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-your-local-build).
- **The compile step.** `CoreCompile` (Roslyn's `Microsoft.Managed.Core.targets`) has `Inputs`
  `$(MSBuildAllProjects); @(Compile); @(_CoreCompileResourceInputs); ... @(ReferencePathWithRefAssemblies);
  ... @(AdditionalFiles); @(EmbeddedFiles); @(Analyzer); @(EditorConfigFiles); $(SourceLink)` and passes
  `ToolExe=$(CscToolExe)`, `ToolPath=$(CscToolPath)`, `UseSharedCompilation`, `ProvideCommandLineArgs`,
  `SkipCompilerExecution`; output `@(CscCommandLineArgs)`
  (https://raw.githubusercontent.com/dotnet/roslyn/main/src/Compilers/Core/MSBuildTask/Microsoft.CSharp.Core.targets).
  F#'s `CoreCompile` calls `Fsc` with the same two switches (Project.fs relies on it today).
  Hooks: `BeforeTargets="CoreCompile"` (runs even when `CoreCompile` is then skipped as up to date;
  inferred from target-ordering semantics), `$(CoreCompileDependsOn)`, `TargetsTriggeredByCompilation`,
  or a wrapper compiler via `CscToolPath`/`CscToolExe` that sees the exact rsp (untested: interplay with
  `UseSharedCompilation`; F# needs `DotnetFscCompilerPath`/`FscToolPath`).
- **Incrementality.** When `CoreCompile` is skipped, `@(CscCommandLineArgs)` is not produced (task
  outputs are not inferred for skipped targets; inferred), so a gate must check *item inputs* before
  `CoreCompile`, not the command line after it.
- **Graph and isolation.** `-graph` builds a static project graph; `-graph -isolate` fails (MSB4252) on
  any `MSBuild` task call outside the declared `ProjectReferenceTargets` protocol; multi-targeting is
  modelled as outer/inner builds with speculative edges
  (https://github.com/dotnet/msbuild/blob/main/documentation/specs/static-graph.md).
- **Capture.** `-pp` (preprocessed import closure) and `-bl` binary logs (embed imported files and
  `@(EditorConfigFiles)`, which `CoreCompile` adds to `EmbedInBinlog`, per the targets above).

**What makes an evaluation non-hermetic by default** (each is an input the extension does not own):
environment variables are properties ("MSBuild treats environment variables as trusted inputs. They can
cause MSBuild to load build logic ... from custom locations";
https://learn.microsoft.com/en-us/visualstudio/msbuild/how-to-use-environment-variables-in-a-build);
`Directory.Build.props/targets` up the tree; `Directory.Build.rsp` up the tree and `MSBuild.rsp` next to
the executable, applied by `msbuild`/`dotnet build` but **not by Visual Studio**, `-noAutoResponse` disables
(https://learn.microsoft.com/en-us/visualstudio/msbuild/msbuild-response-files);
`$(MSBuildExtensionsPath)` and per-user `$(MSBuildUserExtensionsPath)` `ImportBefore/After/*.targets`,
disabled per file by `ImportUserLocationsByWildcardBefore<File>=false` (customize-your-local-build above);
`<Project>.user` files; `NuGet.Config` hierarchy and user-level config; the SDK's own targets; the
MSBuild version (VS's `MSBuild.exe` is not the SDK's; `global.json` is read by both the muxer and the
MSBuild SDK resolver, from different start directories;
https://learn.microsoft.com/en-us/dotnet/core/tools/global-json).

## 3. Prior art (none records-and-verifies compiler inputs)

- **Project cache plugins** (`ProjectCachePluginBase`: `BeginBuildAsync`, `GetCacheResultAsync`,
  `HandleProjectFinishedAsync`, `EndBuildAsync`, `HandleFileAccess`): registered by a `ProjectCachePlugin`
  item (can come from a PackageReference); cache get since 16.9 needs `/graph`; cache add since 17.8
  needs `/reportfileaccesses`, which uses **Detours on worker nodes, limited to MSBuild.exe from a VS
  install (not `dotnet`), x64, command line only**, no cache add in VS
  (https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md). So *observed*
  file accesses are Windows-only in practice (inferred from "MSBuild.exe from VS, Detours").
- **microsoft/MSBuildCache**: plugins over that API (Local via BuildXL's LocalCache, Azure Pipelines,
  Azure Blob); `msbuild /graph /m /reportfileaccesses`, VS 17.9+; "experimental"; a SharedCompilation
  package reports the compiler server's accesses manually; probes/enumerations can enter the
  fingerprint; options like `MSBuildCacheIgnoreDotNetSdkPatchVersion`; F# not mentioned
  (https://github.com/microsoft/MSBuildCache). It caches outputs keyed by observed inputs; it writes no
  reviewable lock and verifies nothing against one.
- **MSBuild.CompilerCache**: closest technically. Ships `.targets` that extend `CoreCompile` for `Csc`
  and `Fsc`, hashes the compile inputs before the task (references via generated ref assemblies), and
  its targets are **copy-pasted from each SDK, so "a new SDK requires a new copy"**; supported SDKs
  listed only up to 7.0.302, "must match ... precisely"; ignores `Analyzers`, `AnalyzerConfigFiles`,
  `ToolExe/ToolPath`, `DotnetFscCompilerPath`; experimental, 3 stars
  (https://github.com/safesparrow/MSBuild.CompilerCache). This is the maintenance surface, measured.
- **DotNet.ReproducibleBuilds**: sets `DebugType=embedded` and `ContinuousIntegrationBuild=true` when
  a CI variable is present. The **Isolated** SDK restricts `AssemblySearchPaths`, undefines
  `TargetFrameworkRootPath` (forces `Microsoft.NETFramework.ReferenceAssemblies`), sets
  `DisableImplicitNuGetFallbackFolder`/`DisableImplicitLibraryPacksFolder`, and runs
  `ValidateGlobalJsonSdkVersion` before `PrepareForBuild` (src/DotNet.ReproducibleBuilds*/ in
  https://github.com/dotnet/reproducible-builds, read 2026-10-12). It does not touch env vars,
  `Directory.Build.*`, user extensions or NuGet config, and records nothing.
- **NuGet**: `packages.lock.json` + `RestoreLockedMode` (NU1403 on content mismatch), `contentHash`
  SHA-512; docs pair it with `rollForward: disable` (paket-analysis.md §1; global-json page above).
- **SBOM in msbuild**: `Microsoft.Sbom.Targets` (`GenerateSBOM=true`, SPDX into the nupkg at pack) and
  `CycloneDX.MSBuild` **(search only)**: https://www.meziantou.net/generating-sbom-for-nuget-packages.htm,
  https://www.nuget.org/packages/Microsoft.Sbom.Targets. Both describe the restore graph, not compiler inputs.
- **Nix** `buildDotnetModule`: `nugetDeps` JSON lock of packages with hashes, sandboxed fetch
  **(search only)**: https://github.com/mdarocha/nuget-packageslock2nix and nixpkgs commits.
  reproducible-builds.org/tools lists no .NET tool (https://reproducible-builds.org/tools/).
- **Bazel `rules_dotnet`** (contrast): "an alternative to using MSBuild"; the toolchain is a pinned SDK
  version declared in `MODULE.bazel` (`dotnet.toolchain(dotnet_version=...)`), NuGet comes through
  Paket + `paket2bazel` into Bazel repository rules, actions run in Bazel's sandbox
  (https://github.com/bazel-contrib/rules_dotnet; registry docs **(search only)**:
  https://registry.bazel.build/docs/rules_auto_dotnet/0.3.5). Hermeticity there comes from not running
  msbuild at all, which is the declarative route's bet, not the extension's.

## 4. The hypothetical extension, concretely

`Xake.Hermetic.MSBuild`: one NuGet package (`build/` + `buildTransitive/`), task assembly for net472
(VS `MSBuild.exe`) and net8.0 (`dotnet build`), referencing the pure parts of `Xake.Hermetic.Dotnet`
(`Csc.ofArgs`/`FscArgs`, `Lock.format/parse/diff`, `Sbom`, `Verify`) - not the recipe parts, which need
the Xake engine. Modes by property `HermeticLock=record|verify|off` (default `verify` when a lock exists).

```xml
<Project>
  <PropertyGroup>
    <ProvideCommandLineArgs>true</ProvideCommandLineArgs>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <RestoreLockedMode Condition="'$(HermeticLock)' == 'verify'">true</RestoreLockedMode>
    <HermeticLockFile>$(HermeticLockDir)locks/$(AssemblyName)/$(Configuration).json</HermeticLockFile>
  </PropertyGroup>
  <UsingTask TaskName="HermeticVerify" AssemblyFile="$(XakeHermeticTasks)" />
  <UsingTask TaskName="HermeticRecord" AssemblyFile="$(XakeHermeticTasks)" />
  <Target Name="HermeticVerify" BeforeTargets="CoreCompile"
          Condition="'$(HermeticLock)' == 'verify' and '$(DesignTimeBuild)' != 'true'">
    <HermeticVerify Lock="$(HermeticLockFile)" Framework="$(TargetFramework)"
        Sources="@(Compile)" References="@(ReferencePathWithRefAssemblies)"
        Analyzers="@(Analyzer)" AdditionalFiles="@(AdditionalFiles);@(EditorConfigFiles)"
        Defines="$(DefineConstants)" CompilerPath="$(CscToolPath)$(DotnetFscCompilerPath)"
        SdkVersion="$(NETCoreSdkVersion)" Imports="$(MSBuildAllProjects)" />
  </Target>
  <Target Name="HermeticRecord" AfterTargets="CoreCompile" Condition="'$(HermeticLock)' == 'record'">
    <HermeticRecord Lock="$(HermeticLockFile)" Framework="$(TargetFramework)"
        CommandLine="@(CscCommandLineArgs);@(FscCommandLineArgs)" ProjectRefs="@(ProjectReference)"
        AssetsFile="$(ProjectAssetsFile)" SdkVersion="$(NETCoreSdkVersion)" />
  </Target>
</Project>
```

**`HermeticRecord` inputs**: the compiler command line from the task output (record mode forces a
rebuild, e.g. `-t:Rebuild`, because a skipped `CoreCompile` yields no command line); `$(ProjectAssetsFile)`
for the package graph and `.nupkg.metadata` SHA-512 (as `Lock.packagesOf`); the compiler path
(`CSharpCoreTargetsPath`/`bincore`, or `DotnetFscCompilerPath`, the rule in lock.md "The compiler a lock
names"); `NETCoreSdkVersion` + `global.json` for `Sdk`/`SdkPin`/`Prerequisites`; generated files under
`$(IntermediateOutputPath)` for `Generated`; `SourceRevisionId` for the token; the import closure for
`Evaluation.Imports` (gap: a task does not see the evaluation's import list; `$(MSBuildAllProjects)` may not
hold the whole closure **(unverified)**, so a faithful list needs `-pp` or a binlog outside the build);
roots for tokenisation (`$(ProjectRoot)` = repo root found by `.git`/`global.json`, `$(NuGetPackageRoot)`,
`$(DotnetRoot)`; sibling roots by property). It writes with `Lock.format`, round-trip-checked by
`Csc.ofArgs` as `parseImport` does today.

**`HermeticVerify`**: re-hashes every reference, analyzer, the compiler and the imports; compares
sources/defines as `Lock.diff` does; fails with the diff lines (the HERMETIC gate, messages 9-11 reused);
checks the SDK pin (or delegates to the Isolated SDK's `ValidateGlobalJsonSdkVersion`). Options other
than items (e.g. `/langversion`, `/nullable`, `/warnaserror`) are not visible as items; full option
comparison needs either a `SkipCompilerExecution` pre-pass of the `Csc` task, or the wrapper-compiler
route (verify the rsp, then exec the real compiler). Then msbuild compiles as usual.

**Can the frozen format serve?** Mostly, because it was made for this capture. Fits: `Evaluation`,
`Compilation`, `Dependencies`, `Packages`, `Prerequisites`, unhashed project-ref outputs (here the ref is
an `obj/ref/*.dll` of a sibling, built in the same run; hashing it would fail on every upstream change).
Pressure points: (1) layout: today an imported lock is one document per *variant* holding all
frameworks and projects; parallel msbuild nodes writing one shared file race, so the extension wants one
file per project x configuration (the rework's layout) or per project x TFM; (2) no slot for the
extension's own version, `NuGet.Config`/source identity, or environment variables read (a new
`Prerequisites.Kind` or an additive field; the brief allows optional additive fields); (3) `Imports` as
"what the evaluation read" is only as good as the capture (above); (4) `$(ProjectRoot)` must be derived
inside msbuild, not from `ExecOptions.ProjectRoot`.

**Effort (rough, unvalidated)**: targets ~150-250 lines; tasks: record ~300 (mostly reuse of
`parseImport`), verify ~250, SBOM/pack glue ~100; task packaging for two runtimes plus F# task assembly
loading in VS's net472 MSBuild and in the SDK's MSBuild load context (FSharp.Core version conflicts are a
known class of risk; inferred); tests on a matrix SDK 8/9/10 x csc/fsc x Windows/Linux/macOS x `dotnet
build`/VS, plus design-time builds that must not trip the gate. Weeks rather than days, dominated by the
matrix. **Maintenance**: every SDK band can rename or add `CoreCompile` inputs (`@(EmbeddedFiles)`,
`$(SourceLink)` and `@(EditorConfigFiles)` are all relatively recent); an item the extension does not
know is an unchecked input. MSBuild.CompilerCache stalled on exactly this.

## 5. What it could and could not guarantee

| Guarantee | Declarative Xake route | msbuild extension | Neither |
|---|---|---|---|
| Lock holds the exact compiler command line, reviewed in git | yes (composed) | yes (msbuild's own) | |
| Every outside input (refs, analyzers, compiler) by SHA-256, drift fails before compiling | yes | yes, for inputs it knows | |
| Package graph with content hash, locked restore | yes (`Packages`, `NUGET_FETCH`) | yes (NuGet lock + `RestoreLockedMode`) | |
| SDK pinned exactly, checked up front | yes (prerequisite) | yes (`global.json` + validate task) | |
| Build without msbuild on CI / fresh clone | yes (brief goal) | no | |
| Evaluation inputs (env vars, `Directory.Build.*`, rsp, ImportBefore, NuGet.Config) cannot change the build | yes at build time (no evaluation runs) | no: evaluation runs each time; changes are caught only when they alter compiler inputs it checks | |
| Steps after compile (copy, satellite assemblies, pack, sign) covered | own rules (`Pack.nupkg`) | no, msbuild's | |
| Byte identity with `dotnet build` | no (Q3) | yes by construction | |
| Works in VS and the customer's own build | no | yes for `dotnet build`; VS differs (own MSBuild, no rsp, design-time builds) | |
| Proof that nothing undeclared was read | | only with `/reportfileaccesses` (VS MSBuild.exe, x64, Windows) | in general neither: both are "declared inputs, hashed" |
| Analyzer / source-generator behaviour | | | neither (inputs hashed, behaviour not) |
| Byte identity across machines | with `--pathmap` | with `ContinuousIntegrationBuild`/`PathMap` | |
| SBOM from compiler inputs | yes | yes (same library) | |
| Description layer (what the project is, data only) | yes | no (the csproj is the description) | |

Honesty point for both: the current Xake lock is **declared inputs, hashed, gated**, not a sandbox.
The difference is *where the evaluation happens*: the declarative route evaluates once (import, by
hand) and never again on CI; the extension evaluates on every build, so its gate is only as wide as the
item list it compares.

## 6. Comparison with the current route

Better with the extension: works inside the customer's build and CI as-is; csproj/fsproj stay the only
truth (no description, no second build system, no F# script for a C# shop - the product brief's §8
"drop-in" question answered by a PackageReference rather than `dotnet xake`); byte identity with
`dotnet build` for free; msbuild's own handling of multi-targeting, project refs across repos (page's
`LocalBuild`), brands as global properties, CPM and source mapping.

Worse or impossible: no declarative layer; CI runs msbuild and restore; hermeticity bounded by the
evaluation surface (§2); the Xake engine is not involved (parallelism/incrementality are msbuild's,
incl. `obj/` state and `Inputs/Outputs` skipping, which the gate must survive); F# quirks remain
(`Fsc` task, implicit FSharp.Core, `fsc.dll` from the SDK as prerequisite); the extension's own version is
an input the lock must record; VS builds can be "green" without the gate (design-time builds, rsp ignored).

## 7. Three strategies

1. **Extension replaces the declarative route.** PRs 1-7 (descriptions, `Lock.generate`, layout,
   fingerprints, self-build) lose their purpose for customers; Xake's self-build either goes back to
   `dotnet build` + extension or keeps PR #42-style import. PR 8 (pack) becomes `dotnet pack` +
   deterministic settings or `Pack.nupkg` from a task; PR 10 (`cached`) is irrelevant. Contradicts Q3's
   "now: declarative" and the brief's "fresh clone needs no msbuild"; the declarative bet (description
   as data, Bazel-like) is dropped.
2. **Extension as the second fork, shared library.** This is the brief's "later, separately" fork
   given a concrete shape: same lock format (with the §4 pressure points as additive fields), same
   `Xake.Hermetic.Dotnet` pure modules (Lock, Csc/Fsc args, Sbom, Verify, Pack) as a task dependency.
   Cost: a second consumer that pulls the library toward netstandard2.0/net472 task loading and toward
   format fields the declarative route does not need; two test matrices. If PR 9 (ActiveReports) ran on
   the extension instead: no AR commits needed (injection via `CustomBeforeMicrosoftCommonTargets` on the
   command line), brands stay `-p:Brand=`, but the experiment's success criteria (script size and
   clarity, core utilisation) would measure msbuild, not the declarative route, so it would stop being
   the declarative route's acceptance; PR 9 would need splitting, not swapping.
3. **No extension.** The msbuild fork stays as `Project.import` + replay (already proven 36/36, 90/90)
   for customer repos; the extension idea is parked. Gaps that stay: no in-VS story, customers adopt an
   F# script.

Dead ends named: copying `CoreCompile` per SDK (MSBuild.CompilerCache's path); relying on
`/reportfileaccesses` for a cross-platform proof; MSBuildCache as the lock (it caches, keeps no
reviewable lock, needs `/graph` and Windows-centred tooling).

## 8. Open questions

- Is "VS builds are green but only `dotnet build` with `HermeticLock=verify` is gated" an acceptable
  customer message, or must the gate also run inside VS (design-time builds excluded)?
- Which item list is the gate's contract per SDK band, and who maintains it per SDK release?
- Wrapper compiler (sees the whole rsp, survives unknown options) vs `BeforeTargets` items (no command
  line): which is the verify mechanism? Interplay with `UseSharedCompilation` is untested.
- How does a task see the evaluation's import closure without `-pp`/binlog? Is `$(MSBuildAllProjects)`
  complete on current SDKs? (unverified)
- Lock layout for the extension: per project x configuration (rework layout) or per project x TFM?
  Does a per-variant document survive parallel nodes at all?
- Record the extension's version, env vars, NuGet sources: additive fields or a new prerequisite kind?
- Is `Microsoft.Sbom.Targets`/`CycloneDX.MSBuild` enough for customers, making our SBOM-from-inputs the
  differentiator only where the lock exists? (their content not verified here)
- Would the declarative route and the extension produce comparable locks for the same project, so one
  can verify the other (a cross-check the brief's "both meet at (c)" implies)?
