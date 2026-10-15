# PR 2: the import records msbuild's resolved compile inputs (2026-10-16)

Branch `pr/resolved-inputs` (worktree `.claude/worktrees/pr/resolved-inputs`), from
`feature/hermetic-build` b2a0d1e plus the night base 74bf4d9 (merged with `--no-ff`, no
conflicts). Binding decisions: interview.md, last section (2026-10-15). Design source:
`night-changes-triage.md` Part 5 and `night-changes-decisions-ru.md` section 2 (on `pr/apphost-resx`).

## 1. Design as built

**Principle.** For an imported project Xake derives nothing: msbuild resolves the references,
defines, generated sources, resource names and compiler switches; the import records them; the
build turns them into paths and runs the compiler. The composed route (`csc {}`, hand-written
descriptions, `Locks.generate`) keeps its pre-night resolution, unchanged and not extended.

**`Import.record options projects`** (`src/hermetic/Import.fs`) writes each project's
description and one lock per configuration (entries per framework):

1. One restore of all projects (a generated solution, `-t:Restore`).
2. The evaluations for the description (as `Import.descriptions`).
3. One msbuild process per configuration for all design-time builds: a generated traversal
   project over (project, framework), `-m`, `ProvideCommandLineArgs=true`,
   `SkipCompilerExecution=true`, `BuildProjectReferences=false`, targets
   `BuildOnlySettings;PrepareForBuild;ResolveReferences;PrepareResources;ResolveKeySource;Compile`
   (CoreBuild up to the compiler, no PreBuildEvent). A targets file handed to every project
   through `CustomAfterMicrosoftCommonTargets` (the script's own value is chained) writes
   `CscCommandLineArgs`/`FscCommandLineArgs`, `ReferencePath`, `Analyzer`, `Compile`,
   `EmbeddedResource`, `EditorConfigFiles` and the properties the import reads to one file per
   build. Why this way and not `-getItem` per process: in one process a project referenced by
   many others is evaluated once (AR: 172 builds in 2.8 s instead of 84 s, same locks).
4. `entryOf`: the recorded command line with `/out:`, `/refout:` removed (the build's output) and
   `/sourcelink:`, `/embed` removed (parked); what msbuild generated in its intermediate folder
   moved to `obj/xake/<key>/<tfm>/<cfg>/` and recorded as text (LF, `$(SourceRevisionId)`
   token, roots tokenized); a package source (Test.Sdk's `Program.cs`) recorded the same way;
   `.resx` pairs from `EmbeddedResource.OutputResource`; `Sources` = msbuild's `Compile` list in
   its order, the description's masks beside it (`Recorded.Masks`) when they name the same
   files; project references as the sibling's output in the build's layout
   (`OutOf cfg NearestTargetFramework name`); hashes for every reference/analyzer but project
   outputs and SDK files; `dotnet-sdk <NETCoreSdkVersion>` always a prerequisite (the SDK pin of
   decision 1); `TargetFileName` kept (the output is `X.exe` for a .NET Framework app).
5. Checks before writing: every file must have a portable identity (`Lock.Identity.ofPath`,
   else the import fails naming it), and the text must hold no host string
   (`Import.hostStrings`: roots, home, temp, a runtime identifier). A failed msbuild run fails
   the import.

**Identities** (`Lock.Identity`): `pkg:<id>/<ver>/<path>` (package folder);
`pack:<id lower>/<ver>/<path>` (a `*.App.Ref` targeting pack, in `$(DotnetRoot)/packs` or
downloaded into the package folder: one identity for both, so two machines record the same
lock); `sdk:<path>` (under `$(DotnetRoot)/sdk/<Evaluation.Sdk>/`); `project:<name>`
(`Recorded.Projects`); a tokenized path under a root. Materialisation is in the lock reader:
`pack:` uses this .NET's pack when it has the version, else the package folder (fetched by the
restore like any package). Why path-based and not item-metadata-based: the writer sees paths;
the msbuild metadata (`NuGetPackageId`, `FrameworkReferenceName`) gives the same answer for every
file seen on AR, and the path rule also covers the .NET Framework reference assemblies and
facades, which carry no package metadata.

**Build** (`Locks.needAll`, `Locks.compileAs`): unchanged path, plus: the output name is
`TargetFileName`; for a recorded C# entry the sources are msbuild's files in msbuild's order
without those the masks no longer name, then a file the masks name since (an added `.cs` needs
no re-import; the masks are read through `getFiles`).

**Fixes found on the way** (each in its own commit): msbuild quotes a path with `~`
(`/analyzerconfig:"..."`) -- unquoted before parsing; `Restore.download` fetched only one version
of an id per synthesized restore -- now in rounds (found by building a copy at another path
with an empty package folder); lock document `Properties` are tokenized; the global.json pin of
a prerequisite is tokenized (else its file name only).

**What stays from the night** (group 1, ported by hunk): `$(...)` in `Compile Include` and
`.projitems` masks (`csharpMasks`); `.projitems` resource paths relative to the project;
`byFramework.<tfm>.resources`; `platformOf`/`nearestFramework` for `net8.0-windows`;
`generatedKey` (`obj/xake/<key>`, S50); one msbuild run per (cfg, tfm) and concurrency (item 8,
now one process per configuration); msbuild failure = import failure (item 7); purity as a test
(item 6); LF/`$(SourceRevisionId)` hygiene. `ResxFormat`, `netcoreMoniker`, S17 are PR 1
(`pr/dotnet-resx`, core); the S.R.E guard is a check on the recorded references (msbuild's own
`GenerateResource` already fails MSB3822 without the package); apphost (BN1) and `App` are left
for PR 3 (the base has no `App` section to hang it on).

## 2. What was deleted (not carried over from the night)

The night's hermetic code (74bf4d9..pr/apphost-resx: +4979 lines in `src/hermetic`, +2383 in
`src/hermetic.tests`) is not merged. This PR adds +1617/-164 to the night base for hermetic code,
tests and docs, of which about 170 lines are night hunks (above). The emulation list of
decisions-ru section 5 row 2, by function, as it stands on `pr/apphost-resx` (lines incl. doc
comments): `resolveConflicts` 39, `frameworkConflicts` 27, `supersede` 13, `windowsDesktop*` 68
(three functions), `designTimeFacades` 18, `netstandardShims` 29, `dependsOn` 45,
`restoreGraphOf` 51, `PrivatePackages` 2, `analyzerItems` 45, `analyzerConfigFiles` 15,
`assemblyInfoOf` 92, `codeFragment*` 27, `globalUsingsText*` 28, `defineConstantsOf` 8,
`defaultLangVersion` 15, `firstClassName` 70, `isCulture` 22, `packageSources` 21,
`msbuildNewLine` 18, `refAssembly` (a field, no function): **653 lines** in the named functions
alone; with their callers, `SdkFacts`/`describeWith`, the description fields of 5.3 "Loses" and
`Desktop.fs` (1085, PR 3 territory) none of it is in this branch. Night tests not ported:
`DescFidelityTests` (379), `DesktopTests` (590), `FidelityFixesTests` (319),
`WinImportTests`/`2`/`3` (1019) -- byte-identity e2e and emulation unit tests (decision 6).

## 3. Lock schema diff (format 1 -> 2)

| | format 1 | format 2 |
|---|---|---|
| document | no version field | `"Format": 2` first; `Properties` values tokenized |
| entry marker | -- | `"Recorded": { "By": "msbuild", "Masks": [...], "Projects": [ { Name, Configuration, Framework, Path } ] }` on a recorded entry |
| reference / analyzer | `{ "Path": "$(NuGetPackageRoot)/...", "Sha256", "Alias"? }` | recorded: `{ "Id": "pkg:..." \| "pack:..." \| "sdk:..." \| "project:..." \| "$(ProjectRoot)/...", "Sha256", "Alias"? }`; composed: unchanged `Path` |
| compiler | `{ "Tool", "Path", "Sha256", "Version" }` | recorded: `"Id": "sdk:Roslyn/bincore/csc.dll"` (or `pkg:` for a toolset package) |
| `Compilation.Sources` (recorded) | -- | msbuild's files in msbuild's order |
| `Evaluation.Properties` (recorded) | -- | `AssemblyName`, `TargetFrameworkMoniker`, `LangVersion`, `Version`, `OutputType`, `Deterministic`, `SignAssembly`, `GenerateResourceUsePreserializedResources`, `TargetFileName` |
| `Dependencies.Prerequisites` (recorded) | only with an exact global.json pin | always `dotnet-sdk <sdk>` |
| reading old locks | -- | format 1 with an imported entry: refused, "regenerate the lock (run the import again)"; format 1 composed-only: read; format > 2: refused |

Description schema (`docs/schemas/xake.project.schema.json`): `items.resources` added
(`byFramework.<tfm>.resources`); the resource `name` note mentions `ManifestResourceName`.
Nothing removed: the night-only description fields were never in the base. Docs:
`docs/hermetic/lock.md` "Recorded locks: `Import.record`", `docs/hermetic/description.md`
"Import that records msbuild's compile inputs".

## 4. Tests

`dotnet fsi build.fsx -- -- build test`: tests 341 passed + 1 skipped (unchanged), hermetic.tests
**234 passed** (226 before; +8, `src/hermetic.tests/RecordTests.fs`):

- `Recorded lock identities` (6): names (`pkg:`, `pack:` both places, `sdk:`, `project:`, roots,
  refusals), materialisation (pack fallback), format-2 round trip (text equal after
  read+write on a machine without the pack), a non-portable file refused, format 1 refused for
  an imported entry / read for a composed one / format 3 refused, host strings.
- `Import record` (1, e2e, two-project solution: lib netstandard2.0+net8.0 with a package, app
  net472+net8.0 exe with a project reference, resx, named resource, implicit usings): no
  absolute path/home/CR in the locks; Xake builds all four outputs; **import -> Xake build ->
  import: lock unchanged**; `dotnet build`: same outputs, types, methods, resources; **import
  after `dotnet build`: lock unchanged**; an added `.cs` is compiled without a new import.
- `Restore rounds` (1).

Golden texts gained the `"Format": 2` line; one old-format test input got it too. No
byte-identity e2e test existed in the base to delete.

## 5. ActiveReports check (`/Users/olegz/.claude/jobs/7147ff60/tmp/pr2/`)

Setup: `ar/meta` = wi3-tests' ar-net-meta without `.git`, `bin`, `obj`; `ar/.packages` a clone
of wi3-tests' package folder; `ar/build.fsx` (import = `Import.record`, build =
`Locks.needAll`), `ar/scripts/macos-probe.targets` (copy of xake-ar's, the same probe both
sides); scripts in `pr2/scripts/` (`ar-xake.sh`, `ar-ref.sh` = `dotnet build` of a solution of
the imported projects with the same properties, `ar-compare.py`, `metapairs.fsx`). Set: the
stage-3 libraries (49 projects incl. Design.Win) + the stage-5a test projects (36), compile
only, no test run. SDK 8.0.425 (AR's global.json), Xake 3.7.10-night (night core: ResxFormat
for typed resx, until PR 1 lands), Xake.Hermetic.Dotnet from this branch.

| | mescius Release | gccn Release |
|---|---|---|
| projects / assemblies | 85 / 172 | 84 / 170 |
| compile outputs (dll/exe, pdb, xml) present on both sides | 444/444, Xake extra 0 | 440/440, Xake extra 0 |
| type + method counts equal msbuild's | **172/172** | **170/170** |
| manifest resources / assembly refs / version equal | 172 / 172 / 172 | 170 / 170 / 170 |
| byte-identical | 0 (not a goal) | 0 |
| import -> build -> import, lock unchanged | 85/85 | -- |
| import after `dotnet build`, lock unchanged | 85/85 | -- |

Times (clean, `CSC_SERVER=off`, wall incl. fsi start): **import 22-24 s** (restore 2.5 s,
evaluations 11 s, 172 design-time builds 2.8 s; engine 20 s) -- it writes the locks, there is no
update-locks step; **clean build 33.7-34.8 s**. Night, same set (wi3-tests): import 25.8 s +
update-locks 66.1 s + build 30.4 s. msbuild reference on this machine: restore 3 s + build
29-31 s (`-m`). First version (one process per design-time build) took 124 s.

Portability: a copy at another path with an **empty package folder**, build from the locks
only: 199 packages fetched from the identities (incl. `microsoft.windowsdesktop.app.ref`
through the `pack:` fallback), 172 assemblies, 39 s.

DataEngine bonus: with msbuild aligned to Xake's paths (output and generated-source folders),
all 6 assemblies are **identical in metadata and IL** (type order, IL bodies, attributes,
references, resources: `meta-diff.fsx` reports nothing); bytes differ only because msbuild
writes `<TFM>.AssemblyAttributes.cs` with CRLF on macOS and the lock records LF (decision 1), so
the PDB document checksum and the PDB id differ.

## 6. Open items

- PR 3: `App` (runtimeconfig, `<exe>.config`, deps.json), the apphost as part of the SDK
  (BN1), copy-local from `ReferenceCopyLocalPaths`; the recorded lock already carries
  `TargetFileName` and `/target:exe`.
- PR 4: deploy from the lock's outputs; `GetCopyToOutputDirectoryItems`/`AppConfigWithTargetPath`
  are not recorded yet (the targets file can add them).
- PR 1 must land for typed resx in a released Xake (AR used 3.7.10-night).
- F#: `Import.record` handles an fsproj through `FscCommandLineArgs` (code path present, not
  exercised on a real project here); the self-build stays on the composed route.
- The import writes msbuild's design-time products into the projects' own `obj/<cfg>/<tfm>/`
  (as a Visual Studio design-time build does) and creates `bin/` folders (`PrepareForBuild`).
- `versions` masks (`$(Version)` token) apply to composed locks only; a recorded AssemblyInfo
  keeps msbuild's version (AR uses none).
- `Project.import` (the old single-lock import) still writes tokenized paths; its locks are
  format 2 without `Recorded`. Retire or rebase on `Import.record` later.
- HERMETIC=on: a recorded `pack:` under `$(DotnetRoot)/packs` is accepted by its hash, `sdk:`
  files of any role by the SDK prerequisite; not run on AR under HERMETIC=on.
- Not verified on Windows/Linux: CRLF in recorded texts (normalised), case of
  `$(DotnetRoot)/packs` lookups (case-insensitive by design).
