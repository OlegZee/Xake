# PR 5 report: import project file -> description

Branch `pr/project-import` (worktree `.claude/worktrees/pr/project-import`, on `pr/description-model` 15eb8f0),
4 commits, not pushed: 7095128 runner, 2021b92 Import, 4caff35 tests, 13848f3 docs.

## What landed

- `src/hermetic/Project.fs`: the inline `msbuild` helper of `Project.import` became
  `Project.runMsbuild (env) (arguments) (name)` (internal), the one msbuild driver; behaviour unchanged.
- `src/hermetic/Import.fs` (new, after `Project.fs`; 373 lines with doc comments, ~260 of code):
  - `type Import.Imported = { Project; Description; Written: bool; Inputs: string list; Dropped: string list }`
  - `Import.project : string -> (string -> string) -> (string * string) list -> Recipe<ExecContext, Imported>`
  - `Import.descriptions : string list -> (string -> string) -> (string * string) list -> Recipe<ExecContext, Imported list>`
  - script: `let! imported = Import.descriptions projects Description.beside ["Configuration", "Release"]`
- `src/hermetic.tests/DescriptionImportTests.fs` (4 tests), docs: `workflows.md` "Importing a project
  description", `description.md` "Import" + 2 API rows. No cheatsheet (no script-level helper).

How it works: per project, msbuild *evaluation* only (no targets, no restore): `-getProperty/-getItem
-getResultOutputFile` once without `TargetFramework` and once per declared framework, plus `-pp` for each
of those evaluations. Fields: name = project file name; assemblyName/rootNamespace only if different;
F# sources = `Compile` order; C# sources = masks (below); defines/references/packages common to all
frameworks at top level, the rest in `byFramework`; `TRACE` + configuration define, SDK-implicit
packages/references (by `IsImplicitlyDefined` or `DefiningProjectFullPath` under the SDK) left out;
package versions from `Version`/`VersionOverride`/central `PackageVersion`; `PrivateAssets=all` -> private;
options doc/warningsAsErrors/nullable/keyFile(SignAssembly)/F# OtherFlags; `LangVersion`, `Description`,
`Authors` only when a repository file sets them (otherwise SDK defaults leak: C# netstandard2.0 gave
`langVersion 7.3`); package block when `IsPackable`; icon/readme resolved to the packed None item's path;
`bundle` = project refs with `PrivateAssets=all`. An existing description's `$schema` is kept.
Result for src/dotnet equals drafts/example-project.json except frameworks order (`net462` first, as msbuild has them).

## msbuild files read (for PR 6)

From `-pp` banners (existing `Project.parseImports`), union over the evaluations, minus files under
`NetCoreRoot`, `MSBuildExtensionsPath`, `MSBuildSDKsPath`, `MSBuildBinPath`, `NuGetPackageRoot`, the
build's package root and the project's `BaseIntermediateOutputPath` (restore's `nuget.g.props`). Relative
to the project root (`../` allowed: the trial project projects outside the experiment folder), `/`, sorted ordinal.
Xake's projects: just the project file. Brand fixture: `brand/Directory.Build.props`, `brand/Widgets/Widgets.csproj`.

## Dropped (reported in `Imported.Dropped` and logged)

Properties and item types of repository files outside the mapped set (e.g. `Version` in hermetic,
`PackageIconUrl` in core, `TargetsForTfmSpecificBuildOutput` and `target IncludeCoreOutput` in dotnet,
`Brand` in the fixture); non-IVT `AssemblyAttribute`s; SDK-implicit packages (`NETStandard.Library 2.0.3`,
`System.ValueTuple 4.6.2` for F# net462); `Reference HintPath`; `IncludeAssets`/`ExcludeAssets`/`Aliases`;
conditions on `Compile` items; differing F# compile lists per framework; C# mask/msbuild mismatches.
Not reported (no field, SDK-wide): NoWarn/WarnOn, Optimize/DebugType (configuration is the script's).

## Deviations

1. Name: `Import.descriptions`, not `Description.import`: `Description.fs` compiles before `Lock`/`Project`
   (PR 2 may need it there), and F# cannot add to a module from a later file.
2. Returns `Imported list`, so the draft's `do! Description.import ...` is `let! _ = Import.descriptions ...`.
3. C# exclude syntax is my choice: a `!` prefix, applied in order; msbuild `dir/**` written as Xake
   `dir/**/*` (Xake's `dir/**` matches directories only, checked). PR 2 must read the same convention.
4. C# resources are written expanded (files), only sources keep masks. Expected files not committed: the
   `project` path depends on where the test writes, and Dropped is SDK-version-sensitive.
5. Size ~260 code lines vs ~250 planned.

## Commands

- `dotnet build src/hermetic --no-incremental`: `Build succeeded. 0 Warning(s) 0 Error(s)` (net462 + netstandard2.0).
- `dotnet test src/hermetic.tests`: `Passed! - Failed: 0, Passed: 175, Skipped: 0, Total: 175` (171 + 4).
- `src/tests` not run: no `src/core`/`src/dotnet` change.

## Questions for the user

1. C# masks: `!` prefix for excludes and `dir/**/*` (Xake form) in the description: accept, and tell PR 2?
2. `Import.descriptions` name and `Imported list` result (vs `do!`-able unit + a separate inputs call)?
3. `bundle` inferred from `PrivateAssets=all` refs of a packable project: keep, or leave bundle to the script?
4. Package block for every `IsPackable` project (msbuild's default is true for libraries, so the trial project gets one
   per library): fine, or only when `PackageId` is set in a repository file?
