# ActiveReports .NET: build inventory (2026-10-13)

Sources: `ar-net-meta` develop/20 (checked out, submodules at pinned commits), `git show`/`git archive` of
`origin/*` refs of the sibling clones (read only; extracts in `/tmp/arx/`), `ar-net-docs` develop/21,
and `/Users/olegz/projects/Xake/docs/features/hermetic-build/fsc-rework/ar-experiment.md` (sections 1-2).
Paths are relative to the repo named in each citation. "ARO" = `ar-net-meta` (`/Users/olegz/Projects-work/ar/ar-net-meta`).
Facts marked (inferred) are not stated in any file.

## 0. Key findings (read this first)

1. There is a meta repo, `ar-net-meta` (git submodules + `ar.meta.{all,win,web,vs}.sln`). It is the intended way to build the
   product from source: its `Directory.build.props` sets `LocalBuild=true`, so every cross-repo dependency becomes a
   `ProjectReference` to a sibling folder (`ARO/readme.md`, `ARO/Directory.build.props`).
2. Real CI is **TeamCity** (`ar-net-docs/engineering/deployment-manifest-process.md`, `ARO/product.yaml` `mechanism.type: teamcity`,
   `Cake TeamCity.*` calls). Every `bitbucket-pipelines.yml` in every repo only runs "AI Code Review" (image
   `code.wynenterprise.io/docker/bitbucket-ai-review:ar`, runner `kodo`, secrets `CC_KODOTOKEN/CC_KODOMAIL/CC_TEAM`) and a gitleaks PR scan
   (`ghcr.io/gitleaks/gitleaks:v8.30.1`). No build, no signing, no publish in Bitbucket Pipelines.
3. Desktop bits target **net472** (.NET Framework 4.7.2) and **net8.0-windows** (plus net8.0 for non-UI libs). Nothing ships for
   net462/net48; the only `net462` projects are the two licensing demo apps (`ar-net-win/src/Licensing/DemoApp|DemoLib`). Core libs are
   `netstandard2.0;net472`.
4. Even with `LocalBuild=true`, ar-net-win still takes **4 packages from the private `ar-dev` feed** (needs `AA_AR_TOKEN`):
   `Editor.Net` (qwhale), `QueryDesigner` (vqd), `SpreadBuilder` (x2 projects), plus `MESCIUS.DataVisualization.Chart` in reportitems-chart
   (mapped to ar-dev in `ARO/NuGet.config`). Their repos (`ar-net-lib-qwhale`, `ar-net-win-vqd`, `ar-net-spreadbuilder`) are not submodules.
5. Trap: the meta file is named `Directory.build.props` (lower-case b); the sub-repos import it with
   `GetPathOfFileAbove('Directory.Build.props', ...)`. Fine on case-insensitive macOS/Windows, **breaks on a case-sensitive filesystem**.

## 1. Repo graph and build order

### 1.1 Coherent set: `ar-net-meta` branch `develop/20` (HEAD a9895a5, 2026-10-13 "Updated dependencies")

`ARO/.gitmodules` branches and pinned commits (`git submodule status`), with the version each is at:

| submodule | .gitmodules branch | pinned commit | version at pin |
|---|---|---|---|
| ar-net-core-dataengine | develop | 25b7e48c9a | 5.5.1-alpha-1971 |
| ar-net-core-page | develop | 808ef9f249 | 4.6.0-alpha-16341 |
| ar-net-core-document | develop | a7f4864c89 | 6.2.0-alpha-1994 |
| ar-net-core-dataproviders | develop | 443f1466e5 | 4.1.3-alpha-1552 |
| ar-net-core-dataproviders-excel | develop | 8dc1357948 | 2.2.2-alpha-323 |
| ar-net-core-reportitems-chart | develop | f50ce9c7a0 | 1.2.5-alpha-1301 |
| ar-net-chart | develop/20 | da2c95a5b8 | 20.0.4-alpha-87 |
| ar-net-ai-assistance | develop/20 | d644eb46ab | 20.2.1-alpha-193 |
| ar-net-win | develop/20 | 8c9bf3fbca | 20.2.2-alpha-3528 |
| ar-net-localization | develop/20 | 27df5a6d92 | 19.1.0 + 596 commits |
| ar-net-imports | develop/20 | 66969ac67a | 20.2.1-alpha-236-cn |
| ar-net-vs | develop/20 | 0782447a08 | 3529.2373 |
| ar-net-web-designer | develop/20 | 6b71ca8320 | 20.2.2-alpha-959 |
| ar-net-web-viewer | develop/20 | 046ca197a2 | 20.2.2-alpha-1330 |
| ar-net-docs | (none) | 4604c14d98 | develop/20 |

The pinned commits agree with ar-net-win develop/20 `references.props` (DocCore 6.2.0-alpha-1994, DataEngine 5.5.1-alpha-1971,
DataProvidersExcel 2.2.2-alpha-323, ArDvChart 1.2.5-alpha-1301, Chart 20.0.4-alpha-86). Small skew inside the set: ar-net-win at the pin
says `ArCoreVersion 4.6.0-alpha-16337` and `DataProvidersVersion 4.1.3-alpha-1538` while the pinned page is -16341 and dataproviders -1552;
reportitems-chart says `ArCoreVersion 4.6.0-alpha-16319`. These only matter for package builds; with `LocalBuild` the sources win.

**Recommendation: build the ar-net-meta develop/20 commit a9895a5.** It is a self-consistent, already-tested snapshot, and the user's
earlier experiment used the same page/dataengine commits (808ef9f249, 25b7e48). Version line: product **20.2.2** (`ar-net-win`
`MESCIUS_EN.props` `VersionPrefix` 20.2.2 on develop/20; GCCN same; JP `MESCIUS_JP.props` 20.2.0).

Other lines (for awareness):
* ar-net-win `develop/21` (HEAD 7330d80cc1, 21.0.0-alpha-3399) pins `ArCoreVersion 4.6.0-alpha-16325`, `DataEngineVersion 5.5.1-alpha-1967`,
  `DocCoreVersion 6.2.0-alpha-1989` (tag absent in the clone), `ArChartVersion 21.0.0-alpha-64` (chart `develop/21`), `AiAssistanceVersion
  21.0.0-alpha-187`, `QueryDesignerVersion 2.0.3-alpha-198`, `SpreadBuilderVersion 3.0.4-alpha-281`, `DataProvidersVersion 4.1.3-alpha-1532`
  (`references.props`). Cores have no `develop/21`: the same `develop`/`release/*` of the cores serve both 20.x and 21.x
  (page `release/4.6` has tag 4.6.0-alpha-16325 at ad8e842af0; dataengine `release/5.5` has 5.5.1-alpha-1967 at 5dbec75).
  The meta repo has no `develop/21` branch listed in `.gitmodules` (it is on develop/20). 21.x is only reachable by pinning cores by tag.
* Cores' `develop` moved on: page develop = **4.7.0** (VersionPrefix in `Directory.Build.props`, tag 4.7.0-alpha-16355), dataengine develop = **5.6.0**
  (tag 5.6.0-alpha-1973), document develop has AR-38006 "Split windows and cross-platform assemblies" (426e770, after the pin). Do not mix these with
  the win develop/20 pins.
* `master/NN` and `master/NN-jp` branches are release lines (ar-net-win, chart, localization, installer, web-*). Core repos use `master` + `release/X.Y`
  + `hotfix/X.Y.Z`.

### 1.2 Repo table (default branch on origin, tags, produces)

Tag counters are TeamCity build numbers: `<VersionPrefix>-alpha-<build>` on develop, `-beta-<build>` on release/hotfix, none on master
(`GetSemanticSuffix` in each `build.cake`; other branches `<branch with / as ->-<build>`).

| repo | purpose | origin default / dev branches | latest tag, version line | consumes | produces |
|---|---|---|---|---|---|
| ar-net-core-dataengine | RDLX data engine (3 libs) | develop, master, release/5.5 | 5.6.0-alpha-1973 (develop), 5.5.1 on release | nothing from AR | nupkgs `*.ActiveReports.Core.Data.{VBFunctionLib,ExpressionInfo,DataEngine}` |
| ar-net-core-page | RDLX model, rendering, exports (12 libs) | default **master**; develop, release/4.6 | 4.7.0-alpha-16355 (develop) | dataengine (ProjectRef under LocalBuild, package `$(DataEnginePrefix).Data.*` otherwise) | 12 nupkgs `*.Core.{Rdl,Rendering,Scripting,Drawing.Gc,Drawing.Gdi,Export.*.Page}` |
| ar-net-core-document | section-report document + PDF/TIFF | develop, master | 6.2.0-alpha-2003 | GcDocs only (no AR core) (inferred) | 5 nupkgs (`Document`, `Document.Drawing.Gc/Gdi`, `Export.Pdf.Section`, `Export.Tiff.Section`) |
| ar-net-core-dataproviders | CSV/JSON/XML providers | default master; develop | 4.1.3-alpha-1552 | none | 1 nupkg `*.Core.DataProviders` |
| ar-net-core-dataproviders-excel | Excel provider | develop, master | 2.2.2-alpha-330 | dataproviders (inferred) | 1 nupkg |
| ar-net-core-reportitems-chart | PageReport chart item | develop | 1.2.5-alpha-1301 | page Rendering + Export.Text/Html.Page (ProjectRef under LocalBuild); package `MESCIUS.DataVisualization.Chart` 0.6.1 (private feed mapping) | nupkg `*.Core.ReportItems.Chart` |
| ar-net-chart | section chart (Chart, Chart.Win) | default develop/20; develop/21, master/NN | 20.0.4-alpha-87; 21.0.0-alpha-64 on develop/21 | `src/Directory.Build.props` VersionPrefix 20.0.4 (develop/20), 21.0.0 (develop/21) | nupkgs `*.ActiveReports.Chart[.Win]` (+ `.resources` satellites) |
| ar-net-ai-assistance | designer AI features | develop/20 | 20.2.1-alpha-193 / 21.0.0-alpha-187 | Azure/SemanticKernel from nuget.org | 8 libs `*.ActiveReports.Design.AI.*` |
| ar-net-lib-qwhale | script editor control (WinForms) | **master** only | 1.55.31 (pin in win: Editor 1.55.40; tags stale) | none | nupkg `*.ActiveReports.Editor.Net` |
| ar-net-spreadbuilder | xlsx writer | default master; develop | tags 1.0.1-alpha-28 (stale); props 3.0.4 (develop), 3.0.3 (master) | OpenXml, BouncyCastle (nuget.org) | nupkg `*.ActiveReports.SpreadBuilder` |
| ar-net-win-vqd | visual query designer | default master; develop | 2.0.3-alpha-201 | **package** `GrapeCity.ActiveReports.Core.DataProviders` (from feed) | nupkg `*.ActiveReports.QueryDesigner` (merged with Babel) |
| ar-net-localization | satellite assemblies zh-CN, ja, resx sync | default develop/20; develop/21 | tags 19.1.0 (stale) | resx from ar-net-win via `Sync`; build tools in `.tools/` | `build/zh-CN/Satellites`, `Ja/Satellites`, XmlDocs |
| **ar-net-win** | desktop engine, viewer, designer, apps (**main target**) | default develop/20; develop/21, master/NN, master/NN-jp | 20.2.2-alpha-3531; 21.0.0-alpha-3399 | everything above | 20 nupkgs + 5 apps (see 3.1) |
| ar-net-imports | RPX/Crystal/Access/Excel importers | develop/20 | 20.2.1-alpha-236-cn | win packages | tools |
| ar-net-vs | VS extension, templates (VSIX) | develop/20 | 3529.2373 | win packages | VSIX 2019/2022, templates |
| ar-net-installer | WiX/Bootstrapper (Nuke) | default develop/18 (!); develop/21 | tags 3829.1596-en-us | all binaries | MSI/exe |
| ar-net-web-designer / web-viewer | ASP.NET/Blazor + JS | default develop/20; develop/21 | 20.2.2-alpha-959 / -1330; 21.0.0-alpha-886 | win, core packages | nupkgs + npm tgz |
| ar-web-designer (JS), ar-web-designer-qa-automation, ar-net-win-qa | JS designer, QA | `develop/v21.0` etc. | v21.0.0-dev.12 | n/a | out of scope |
| ar-net-docs | engineering docs, deployment manifests | default develop/20 | none | n/a | `deployment/deployment-manifest.{en,kr,gccn,ja}.yaml` |

Note `ar-net-core-page`'s `origin/HEAD` is `master`: a plain clone lands on master, not develop. `ar-net-installer` default is develop/18.

### 1.3 Dependency graph (LocalBuild=true)

```
dataengine(VBFunctionLib -> ExpressionInfo -> DataEngine)
   -> page(Rdl -> Rendering -> {Scripting, Drawing.Gc -> Drawing.Gdi, Export.*.Page})        [DataEngine -> Rendering, ExpressionInfo -> Rdl]
        -> reportitems-chart(ReportItems.Chart)  [needs MESCIUS.DataVisualization.Chart pkg]
dataproviders -> dataproviders-excel
document(Document -> Document.Drawing.Gc/Gdi, Export.Pdf.Section, Export.Tiff.Section)
chart(Chart -> Chart.Win)                          [independent of page; win ProjectRefs both]
ai-assistance(Design.AI.Core -> Design.AI -> Design.AI.Reporting, Providers/*)
[feed] spreadbuilder, qwhale (Editor.Net), vqd (QueryDesigner; itself needs DataProviders package)
   all of the above -> ar-net-win: ActiveReports -> ActiveReports.Win -> Viewer.Common -> Viewer.Win/Viewer.Wpf -> Advanced.Win -> Design.Win;
                       Exports/* ; dev-apps/*
ar-net-localization (satellites, after win resx patched) -> ar-net-win package step (GCCN/JP only)
ar-net-win -> ar-net-imports, ar-net-vs, ar-net-web-*, ar-net-installer
```

Win's LocalBuild ProjectReferences (from `src/*/Properties/references.props`, `dev-apps/references.props`): page {Rdl, Rendering, Drawing.Gc,
Drawing.Gdi, Scripting, Export.{Pdf,Word,Text,Svg,Image,Html,Excel}.Page}, document {Document, Document.Drawing.Gc, Document.Drawing.Gdi,
Export.Pdf.Section, Export.Tiff.Section}, dataengine {DataEngine, ExpressionInfo}, dataproviders, dataproviders-excel, chart {Chart, Chart.Win},
reportitems-chart, ai-assistance {Design.AI.Reporting, 5 Providers}.

### 1.4 Topological build order (one coherent product build)

1. spreadbuilder, qwhale, vqd (feed substitutes; see 4.3), dataproviders (needed by vqd) and chart: no ordering constraint among them.
2. dataengine: VBFunctionLib, ExpressionInfo, DataEngine.
3. page: Rdl, Rendering, then Scripting / Drawing.Gc / Export.* (Drawing.Gdi after Drawing.Gc); reportitems-chart after page Rendering.
4. document (any time before win), dataproviders-excel, ai-assistance.
5. ar-net-win: ActiveReports, ActiveReports.Win, Exports/*, Viewer.Common, Viewer.Win, Viewer.Wpf, Advanced.Win, Design.Win, dev-apps.
6. After win: localization satellites (GCCN/JA only), imports, vs, web-*, installer.

msbuild/`dotnet build ar.meta.win.sln` derives this order itself; for Xake the order is the topological order of imported descriptions.

### 1.5 The meta solutions (`ARO/ar.meta.*.sln`)

`ar.meta.win.sln` (155 entries, includes solution folders): dataengine (3 + 3 tests), page (Scripting, Rendering, Drawing.Gdi, Drawing.Gc, Rdl, 7 Export.*.Page,
5 test projects + 6 export tests, `dev-apps/Export`), document (5 + 3 tests), dataproviders (1 + test), dataproviders-excel (1 + test), reportitems-chart (1 + 2 test),
chart (Chart, Chart.Tests, Chart.Win), and from ar-net-win: ActiveReports, ActiveReports.Win, ActiveReports.Interop, Viewer.Win, Viewer.Common, Design.Win, Advanced.Win,
Viewer.Wpf, Export.{Excel,Html,Image,Image.Win,Pdf,Pdf.Win,Rdf,Word,Xml}, 7 `.shproj` Licensing projects, dev-apps Designer, DesignerClassic, ThemeEditor
(Viewer and WpfViewer are not in it: (inferred) from the list). `ar.meta.all.sln` 225, `.web.sln` 150, `.vs.sln` 172 entries.

LocalBuild wiring:
* `ARO/Directory.build.props`: one property, `<LocalBuild>true</LocalBuild>`, and empty "Setup project references" placeholders.
* `ar-net-win/Directory.Build.props` line 1-8: `Import GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)..\')` when
  `$(SolutionName) != 'ar.win'` and the parent file exists. Then `<LocalBuild Condition="'$(LocalBuild)'=='' Or '$(SolutionName)'=='ar.win'">false</LocalBuild>`:
  inside the meta repo the import runs first, so LocalBuild stays true, for solution builds and for a single `dotnet build x.csproj`
  (SolutionName is `*Undefined*`). Building `ar-net-win/ar.win.sln` forces false (package mode).
* The cores (`ar-net-core-page/Directory.Build.props`, same pattern with `SolutionName != 'ar.core'`) do the same.
* Output paths in win: `BuildOutPath` default `$(MSBuildThisFileDirectory)dev-apps\bin\$(Configuration)`; `BuildTmpPath` default = ar-net-win root (holds the
  generated `VersionInfo.cs`); `SatellitesPath` `bin\$(Configuration)\ch-CN`; apps set `OutputPath=$(BuildOutPath)` (`dev-apps/app.props`). Cake overrides them
  into `build/`. Configuration defaults to **Release** (`Directory.Build.props`).
* `ARO/NuGet.config` (feeds): `nuget.org` + `ar-dev` (`https://pkgs.dev.azure.com/dt4dev/AR/_packaging/ar-dev/nuget/v3/index.json`, user `dt4dev`, password
  `%AA_AR_TOKEN%`). `packageSourceMapping`: ar-dev only for `Babel.*`, `Editor*`, `semverchecker*`, `MESCIUS.ActiveReports*`, `GCCN.ActiveReports*`,
  `GrapeCity.DataVisualization.Chart`, `MESCIUS.DataVisualization.Chart`, `GCCN.DataVisualization.Chart`; everything else nuget.org.
  `ARO/global.json`: SDK 8.0.100, rollForward latestFeature. `ARO/common.tests.props`: test projects turn off doc file and binding redirects.
* `ARO/product.yaml` (schema 1): build units, variants (en: MESCIUS prefix; zh: GCCN + culture; ja: MESCIUS + `.ja` suffix + localization), `build.command` per unit
  (`dotnet cake scripts/build.cake` for cores). Also documents the localization sync flow `dotnet cake --target="Sync"` per culture.

## 2. Build tooling per repo

Common: Cake 5.0.0 as dotnet local tool (`dotnet cake`), `.config/dotnet-tools.json` in every repo (`cake.tool 5.0.0`,
`babel.obfuscator.tool 10.9.0.0`, `dotnet-coverage 18.5.2`, `dotnet-reportgenerator-globaltool 5.5.4`; cores also `dotnet-script 2.0.1`, `semverchecker 0.1.12`
(private)). `global.json`: **8.0.100 latestFeature** (dataengine, win, ARO, web-*), **8.0.0 latestFeature** (page, document, dataproviders, excel, chart/src,
spreadbuilder/src, vqd/src). Qwhale and localization have none. LangVersion 12, `TreatWarningsAsErrors`, `EnableNETAnalyzers` (SDK 10 fails with CA2022, see experiment).
Pipelines: only AI review + gitleaks (see 0.2). `*.runsettings` net472/net80 for tests.

Cores' `scripts/build.cake` (dataengine, page, document, dataproviders, excel; same template; `#addin Cake.FileHelpers 7.0.0`, `System.Text.Json`):
args `target` (default Pack), `solution`, `branch` (local), `buildNumber`, `obfuscate` (auto: No for feature/bugfix/local, else Yes), `test` (true),
`output` (artifacts), `Brand` (MESCIUS), `versionSuffix`. Tasks: `Clean`, `CleanBinObj`, `Build` (`DotNetBuild` of the single `*.sln`,
`Configuration` = Release, or `Release_NoTests` when `test=false`, `/p:Brand=`), `Test` (`DotNetTest` net472 + net8 with `*.runsettings`; page: Test depends on Obfuscate),
`Obfuscate` (`dotnet babel <dll> --license env:BABEL_LICENSE_KEY_<maj>_<min> --project src/ObfuscationConfig.babel --rules src/ObfuscationRules.xml --flatns GrapeCity.<Name>.I`;
`--warnaserror W00000` if obfuscate=yes), `Pack` (restore with `../NuGet.Config`, `DotNetPack --no-build`, then CycloneDX SBOM via `dotnet script devtools-sbom-nuget.csx`
with `src/<Brand>.sbom.settings.xml`). Default chain Pack -> Obfuscate -> Test -> Build. dataengine `scripts/` also has `benchmark.cake`, `verify-versions.cake`.
Plain `dotnet build <sln> -p:Brand=MESCIUS` works without Cake (the experiment did it).

Other repos:
| repo | tool and tasks |
|---|---|
| ar-net-win `scripts/build.cake` (1386 lines) | args `Brand` (MESCIUS_EN / GCCN / MESCIUS_JP), `satellites`, `xmldocs`, `resources`, `obfuscate`, `test`, `build_number`, `branchName`, `clean`. Tasks: `Clean` (powershell `CleanupBinObj.ps1`), `SetupFolders`, `PrepareLocalizationResources` (patches resx, zips AdvancedWin/ARDesigner resource packs), `BuildLocalizationResources`, `Build` (writes `VersionInfo.cs`, `DotNetRestore` with `scripts/NuGet.Config`, **`MSBuild` via vswhere** (not `dotnet build`: comment "avoid implicit reference to System.Resource.Extensions in net462 assemblies"), `MaxCpuCount 4`, props Brand, LicenseType, BuildOutPath, BuildTmpPath, XmldocsOutput, InheritDocEnabled, NUnit, Version, FileVersion, AdditionalResourcesDir; moves `net472` output to `build/Applications`, copies satellites for zh-CN/ja, writes `ArBuild.props`, SBOM via `devtools-sbom-app.ps1`), `RunTests` (TeamCity only by default; NUnit console net472, `dotnet test` net8), `Obfuscate` (babel over net472, net8.0, net8.0-windows outputs, project `src/ActiveReports.babel`), `Pack` (JP: copies localized XML docs; `DotNetPack` of the sln, nuspecs `*.nuspec` with tokens), `MainTask` = Build + RunTests + Pack. Readme: `cd scripts; dotnet cake [--brand=GCCN --satellites=...]`. |
| ar-net-chart `src/build.cake` | `BuildSatellites` (MSBuild of `l10n/*.resources.csproj`), `Build`, `Test`, `Obfuscate`, `Sign` (AzureSignTool, `signtool.bootstrap` 1.1.0, arg `certificateSignCommand`), `PackResources`, `Pack`. |
| ar-net-lib-qwhale `build.cake` | `Clean`, `Build` (MSBuild of `QWhale.All.sln`), `Obfuscate` (babel, default true), `PackResources`, `Pack`. |
| ar-net-spreadbuilder `src/build.cake` | `Build`, `Test`, `Obfuscate`, `Pack`. |
| ar-net-win-vqd `build-scripts/build.cake` | `CleanBinObj`, `Build` (MSBuild `ar.vqd.build.sln`), `Test`, `Obfuscate` (also merges assemblies with Babel instead of ILRepack), `PackResources`, `Pack`. |
| ar-net-localization `build.cake`, `build.ps1` | args `prefix` (MESCIUS/GCCN/GrapeCity), `culture` (zh-CN/ja), `build_number`, `certificateSignCommand`, `token`, `gitUser`. Tasks `Setup`, `Compile` (vsct, msbuild `BuildRes.msbuild`/`BuildSat.msbuild`, `BuildSat.net8.csproj`, signs with AzureSignTool), `SyncAr`, `SyncXmlDocs`, `GitCommit`, `Sync`. Tools in `.tools/` are old-style csprojs (net35/4.5/4.6.2). |
| ar-net-web-designer / web-viewer `scripts/build.cake` | `BuildJs`/`BuildJsViewer` (`Cake.Npm` 5.1.0, `npm ci`, internal npm mirror, cdxgen SBOM), `ExtractBuildProps` (reads win's `ArBuild.props`), Build, Test, Obfuscate, Pack, Deploy. |
| ar-net-vs | `build.ps1/.sh/.cmd` + Nuke `build/_build.csproj` (net8.0); needs VS/VSSDK. |
| ar-net-installer | Nuke (`.nuke`, `build-scripts`), WiX (`WixToolset.Sdk/6.0.2`), `.sln` files. |

Secrets/env referenced: `AA_AR_TOKEN` (every `NuGet.config`), `BABEL_LICENSE_KEY_<maj>_<min>` (e.g. `BABEL_LICENSE_KEY_10_9`, derived from the babel tool version;
cake throws if missing for obfuscate=yes), `certificateSignCommand` / AzureSignTool (chart, localization, win via installer chain), `CC_KODOTOKEN/MAIL/CC_TEAM`
(only AI review). Cake `Obfuscate` is skipped on local/feature builds (`auto`), so a local build needs no Babel license.

NuGet feeds per repo (all `<clear/>` + nuget.org + one Azure DevOps feed, `Username dt4dev`, `ClearTextPassword %AA_AR_TOKEN%`):
cores, document, dataproviders(+excel), qwhale, spreadbuilder, vqd: `ar-core-dev` (`.../_packaging/ar-core-dev/...`), mapping `Babel.*`, `semverchecker*`, and
(dataengine/page) `Mescius.ActiveReports.Core.*`, `GCCN.ActiveReports.Core.*`, `GrapeCity.DataVisualization.*`. ar-net-win, chart, web-*, ARO: `ar-dev`.

## 3. Project inventory

### 3.1 ar-net-win (origin/develop/21 extract, same layout on develop/20; C# counts from the extract)

Solution `ar.win.sln` (59 projects incl. tests). Common (`src/Directory.Build.props`-style root `Directory.Build.props`): `SignAssembly=true`, `DelaySign=false`,
`AssemblyOriginatorKeyFile=activereportskey.snk` (root; `publickey.snk` for IVT), `MajorVersion` from VersionPrefix, `AppendRuntimeIdentifierToOutputPath=false`,
`TreatWarningsAsErrors`, `WarningsNotAsErrors NU1901-NU1904,CS9057`, `GenerateDocumentationFile` only when `XmldocsOutput` set. Brand files `MESCIUS_EN.props`, `GCCN.props`,
`MESCIUS_JP.props` (VersionPrefix, Authors, Company, `AssemblyNamePrefix` MESCIUS|GCCN, `GcPrefix` DS.Documents|GcDocs, `JaSuffix`, IconUrl, ProjectUrl).
`Directory.Packages.props`: **central package management** (`ManagePackageVersionsCentrally`, `CentralPackageTransitivePinningEnabled`) importing `references.props` and
`dataproviderDrivers.props`; package ids built from `$(AssemblyNamePrefix)`, `$(JaSuffix)`, `$(GcPrefix)`. `src/Directory.Build.targets`: nuspec property binding (`NuspecFile=<proj>.nuspec`,
`GetSemanticVersionRanges` via `RoslynCodeTaskFactory`), `EmbedAdditionalResources` (net472 only, embeds compiled `*.resources` from `AdditionalResourcesDir\$(AssemblyName)` with `LogicalName`,
`Type=Non-Resx`), `CopyXmlDocs`, `AddInternalsVisibleTo`, `LogAfterBuild`. Compile-time shared files: `src/CommonAssemblyInfo.cs`, `src/AssemblyNames.cs` (PublicKeyToken
`cc4967777c49a3ff`, uses `VersionInfo.Version`), `$(BuildTmpPath)\VersionInfo.cs` (rewritten by Cake; repo copy has stale `20.2.1.0`), per-project `Properties/AdditionalAssemblyInfo.cs`.
`DefineConstants` = `$(AssemblyNamePrefix)` (MESCIUS or GCCN), `LICENSE_JP` when `LicenseType=JP`, `TRACE;DEBUG` unless Release. Licensing: shared projects
(`src/Licensing/Licensing.{None,EN,EN.Win,EN.Web,JP,JP.Win,JP.Web}/*.projitems`) imported by `Condition="'$(LicenseType)'=='EN'"` etc. (default `None`).

| project (src/...) | TFMs | out | assembly | .cs | notes |
|---|---|---|---|---|---|
| ReportsEngineCore/ActiveReports | net472;net8.0-windows;net8.0 | lib | `$(Prefix).ActiveReports` | 345 | 7 resx (`LogicalName`d), ~17 VSToolbox bmps as EmbeddedResource, `RuntimeIdentifier win-x64`, `EnableDefaultEmbeddedResourceItems=false`, babel, InheritDoc (Release); packages `$(GcPrefix).Imaging` |
| ReportsEngineCore/ActiveReports.Win | net472;net8.0-windows | lib | `...ActiveReports.Win` | 67 | WinForms, 4 resx |
| ReportsEngineCore/ActiveReports.Interop | net472 | lib | `...ActiveReports.Interop` | 4 | COM-visible helper |
| ReportsEngineCore/ActiveReports.Interop.C++ | v3.5 (vcxproj) | native | - | 0 | **C++/CLI, old msbuild; not in ar.meta.win.sln** |
| WinForms/Viewer.Common | net472;net8.0-windows | lib | `...Viewer.Common` | 264 | resx |
| WinForms/Viewer.Win | net472;net8.0-windows | lib | `...Viewer.Win` | 174 | 27 resx, 267 images |
| WinForms/Advanced.Win | net472;net8.0-windows | lib | `...Advanced.Win` | 93 | 26 resx, 163 images |
| WinForms/Design.Win | net472;net8.0-windows | lib | `...Design.Win` | **2142** | `UseWindowsForms`, `UseWpf`, 135 resx (designer resx, `.Designer.cs` DependentUpon), 1456 images, `GenerateResourceUsePreserializedResources=true` on net8, `NU1701` suppressed, `Editor.Net` + `QueryDesigner` packages (always package), Licensing projitems |
| Wpf/Viewer.Wpf | net472;net8.0-windows | lib | `...Viewer.Wpf` | 123 | **35 xaml (`Page Include`)**, `UseWpf`, 1 resx |
| Exports/Export.{Excel,Html,Word,Xml,Rdf} | net8.0;net472 | lib | `...ActiveReports.Export.X` | 38/43/36/27/32 | Excel+Word: SpreadBuilder package; resx |
| Exports/Export.Image, Export.Pdf | net472;net8.0-windows;net8.0 | lib | `...Export.Image/.Pdf` | 17/37 | babel flag |
| Exports/Export.Image.Win, Export.Pdf.Win | net8.0-windows;net472 | lib | `...Export.*.Win` | 7/151 | |
| dev-apps/Designer, DesignerClassic, Viewer, WpfViewer | net472 | WinExe | `ActiveReports.{Designer,DesignerClassic,Viewer,WpfViewer}` | 6/5/10/7 | `AutoGenerateBindingRedirects`; WpfViewer has 5 xaml (`ApplicationDefinition`) |
| dev-apps/ThemeEditor | net472;net8.0-windows | WinExe | `ActiveReports.ThemeEditor` | 12 | |
| Licensing/DemoApp, DemoLib | **net462** (`Microsoft.NET.Sdk.WindowsDesktop`) | exe/lib | - | 3/1 | only net462 in the product; in `src/Licensing.sln`, not in ar.win.sln |
| benchmarks/Performance | net8.0 | exe | - | 2 | |
| tests/* (14 projects) | net472;net8.0-windows | lib | `GrapeCity.ActiveReports.*.Tests` | 3-399 | NUnit 4.4, `common.tests.props` |

Win-level exotics: **no T4** in win (the installer SetupWizard has t4), **no COM references** (`COMReference` grep empty in win src), `.babel` project `src/ActiveReports.babel`
(Babel 9.3.3 msbuild-format header, `UsingTask Babel.Build`, flatten namespaces, `ObfuscationRules.xml` rules: skip public members, enums, `*ResourceManager`, IDesigner
implementations), `Directory.Build.props` wires no obfuscation into msbuild (Cake runs `dotnet babel` after build), `assets/Localization/*.bat`+`build.msbuild`+`NameCompleter.exe`
for satellite generation, `assets/NuGet/<Brand>/license.txt`, nuspecs per project with `$prefix$` / version-range tokens, `dataproviderDrivers.props` (SQLite 2.0.3, Npgsql 8.0.9, MySqlConnector 2.6.1).
`scripts/devtools-sbom-app.ps1`/`devtools-sbom-nuget.ps1` and CycloneDX settings `*.sbom.settings.xml` are CI-only post steps.
Satellite assemblies and `ch-CN`/`ja` resources arrive only through `--satellites`; EN build has none. Net472 builds embed extra `*.resources` (`EmbedAdditionalResources`).

### 3.2 Core and other repos

| repo/project | TFMs | .cs | notes (source: csproj in the extract / experiment doc) |
|---|---|---|---|
| dataengine: VBFunctionLib 12, ExpressionInfo 117, DataEngine 78 | netstandard2.0;net472 | 207 | `Directory.Packages.props` (central), `.keys/*.snk`, doc XML, babel config `src/ObfuscationConfig.babel` |
| page: Rdl 426, Rendering 1308, Scripting 17, Drawing.Gc 15, Drawing.Gdi 10, Export.Excel 670/.Html 63/.Word 65/.Image 14/.Pdf 36/.Svg 24/.Text 48 | netstandard2.0;net472 (Scripting lists net472 first) | 2736 | resx string tables (Rdl, Rendering x2, Excel), embedded `Res\*` with `LogicalName`, IVT `Key`, `CLSCompliant` via `AssemblyAttribute`, ~830 `<include file=...>` doc fragments, `*.nuspec` binding in `Directory.Build.targets`, config-dependent `DefineConstants` (Rendering), `GcPrefix` packages by brand |
| document: Document 137, Document.Drawing.Gc 95, Document.Drawing.Gdi 46, Export.Pdf.Section 6, Export.Tiff.Section 6 | netstandard2.0;net472; Drawing.Gdi `net8.0-windows;net472`; Tiff `net8.0;net8.0-windows;net472` | 290 | `MESCIUS.props`/`GCCN.props`; Gdi libs need Windows targeting |
| dataproviders: DataProviders 89; excel: DataProviders.Excel 12 | netstandard2.0;net472 / `net8.0;net8.0-windows;net472` | | resx |
| reportitems-chart: ReportItems.Chart 130 | netstandard2.0;net472 | | `MESCIUS.DataVisualization.Chart` package, `CopyLocalLockFileAssemblies` |
| chart: Chart 179, Chart.Win 99 (`Sdk.WindowsDesktop`), `l10n/*.resources`, `Resources` | netstandard2.0;net472 / `net8.0-windows;net472` | 278 | Chart.Win babel; strong-name key `.keys/activereportskey.snk` |
| ai-assistance: Design.AI.Core 17, Design.AI 40, Design.AI.Reporting 128, 5 Providers 5-19 | net472;net8.0 | ~260 | Semantic Kernel 1.74/1.81 + Azure packages (nuget.org) |
| qwhale: QWhale.All 450 (+Resources) | **net472;net6.0-windows** | 450 | WinForms, resx, babel, snk, `GCCN.props/MESCIUS.props`, EOL net6 TFM |
| spreadbuilder: SpreadBuilder 130 | net472;netstandard2.0 | 130 | resx |
| vqd: QueryDesigner.Backend 161 (+ `QueryDesigner` oldstyle stub, QueryDesignerApp net8.0-windows, Resources) | net472;net8.0-windows | 161 | `Sdk.WindowsDesktop`, resx |
| localization: BuildSat.net8.csproj (generates satellites from resx lists, `$(dllname)`), `.tools/*` | net8.0, net35-4.6.2 | | `assemblies.csv`, `ja/`, `zh-CN/` trees (5042 files) |
| imports: Access, Crystal, Excel, Imports.Win (WinExe), Imports (Exe), RPX | net472 (Excel +netstandard2.0) | ~130 | |
| vs: VisualStudio.2022 (117 cs, xaml, babel, VSIX), VisualStudio.2019, Core 104, RoslynSerializer 62, item/project templates (old-style, vsix), Web.Design, Wizard | net472, old-style v4.7.2 | | **VSIX; needs VS SDK** |
| installer: SetupWizard (xaml, t4), Bootstrapper, VsixLoader, `ArInstaller.wixproj` | v4.7.2, WiX 6.0.2 | | Windows only |
| web-designer / web-viewer | net472 (MVC, WebForms), net8.0 (Core, Blazor `Sdk.Razor`/`BlazorWebAssembly`) | | npm build, JS bundles |

## 4. What can build where

TFM facts: `net472` = .NET Framework 4.7.2 (readme: "Net 4.7.2 development tools"); `net8.0-windows` WinForms/WPF; `netstandard2.0` cores. No net462/net48 anywhere in the shipped bits
(except the licensing demo). Reference assemblies for net472 come from the `Microsoft.NETFramework.ReferenceAssemblies` package, so **net472 libs compile on macOS** (the experiment did it
for the 15 core libs). WinForms/WPF TFMs need `EnableWindowsTargeting=true` on non-Windows.

### 4.1 Buildable on macOS with `dotnet build` (SDK 8.0.x via `global.json`, not 10)
* dataengine (3), page (12), document (5; Gdi/net8.0-windows with `EnableWindowsTargeting`), dataproviders, dataproviders-excel, reportitems-chart (after package), chart/Chart,
  spreadbuilder, ai-assistance (8, nuget.org only), localization `BuildSat.net8.csproj`.
* ar-net-win libraries that are pure compile: ActiveReports (net472/net8.0 TFMs; net8.0-windows with `EnableWindowsTargeting`), Export.Excel/Html/Word/Xml/Rdf (net8.0;net472),
  Export.Image/Pdf (partly `-windows` TFMs). WinForms/WPF libs (Viewer.*, Advanced.Win, Design.Win, Viewer.Wpf) likely compile for `net8.0-windows` with `EnableWindowsTargeting=true`
  and for net472 reference assemblies, but **WPF markup compilation (`PresentationBuildTasks`, `Page`) and non-string resx (`System.Resources.Extensions`, `GenerateResourcePreserializedResources`)
  are not verified on macOS** (inferred; WPF XAML compile is Windows-only in practice; the readme itself notes MSB3823 trouble with `dotnet build` outside VS).
* tests net8.0 (page/dataengine/document tests) can run on macOS; tests with `net8.0-windows`, GDI, WinForms cannot.

### 4.2 Windows only
* **net472 builds by msbuild.exe of VS Build Tools 2022** (`GetMsBuildPath` via vswhere in `build.cake`; Cake uses full MSBuild, not `dotnet build`, for ar-net-win), the official path for
  Viewer.Wpf/Design.Win/dev-apps. A Windows VM with .NET SDK 8 + VS Build Tools 2022 (".NET desktop build tools" incl. .NET Framework 4.7.2 targeting pack, "Visual Studio extension development" for vs)
  can verify net472 and net8.0-windows builds (the readme lists exactly this).
* ActiveReports.Interop.C++ (vcxproj, v3.5, Babel), ar-net-vs (VSIX, templates), ar-net-installer (WiX, Bootstrapper, WPF wizard), ar-net-imports (net472 WinExe/WindowsDesktop; Crystal),
  `RunTests` on net472 (NUnit console), Babel obfuscation of net472 outputs (needs Babel license), Cake tasks calling `powershell.exe` (Clean, SBOM), localization `.tools` (net35 old-style).
* WPF `.xaml` projects: Viewer.Wpf (35), WpfViewer (5), SetupWizard, VisualStudio.2022/2019.
* qwhale `net6.0-windows` TFM (SDK 8 still builds it if the targeting pack downloads; EOL warnings).

### 4.3 Needs secrets / private feed (`AA_AR_TOKEN`)
* Restore in ar-net-win even with `LocalBuild=true`: `MESCIUS.ActiveReports.Editor.Net 1.55.40` (Design.Win), `MESCIUS.ActiveReports.QueryDesigner 2.0.3-alpha-201` (Design.Win),
  `MESCIUS.ActiveReports.SpreadBuilder 3.0.4-alpha-283` (Export.Excel, Export.Word); reportitems-chart `MESCIUS.DataVisualization.Chart 0.6.1` (mapped to ar-dev, may also be
  on nuget.org: (unverified)). All other cross-repo refs resolve to sibling projects. Without the token these projects do not restore and everything depending on Design.Win/Export.Excel/Word
  (dev-apps Designer, DesignerClassic, ThemeEditor, tests) fails. In package mode (ar-net-win alone): all `MESCIUS.ActiveReports.Core.*`, `Chart`, `Design.AI.*`, plus those four.
* Workaround without token (not tested): build `ar-net-spreadbuilder` (develop, netstandard2.0;net472, nuget.org deps only), `ar-net-lib-qwhale` (master) and `ar-net-win-vqd`
  (develop; but it requires package `GrapeCity.ActiveReports.Core.DataProviders` from the feed, or edit to a project reference to dataproviders) from source with matching versions, `dotnet pack` into a folder and use a local
  source (ar-net-win readme: copy nupkgs to `<ar-net-win>/packages`, restore with `Nuget.local.config`; neither the file nor `.nuget` exists in the extract: create one). Versions must equal
  `references.props` (`EditorVersion 1.55.40`, `QueryDesignerVersion`, `SpreadBuilderVersion`) or be overridden by a property. A package-source mapping for the local feed is needed because the mapping
  sends `Editor*`, `MESCIUS.ActiveReports*` only to ar-dev.
* Also secret-bound: Babel license (`BABEL_LICENSE_KEY_10_9`), code signing (AzureSignTool), TeamCity, npm mirror, `semverchecker`. None are needed for a non-obfuscated unsigned build.
* Not needed: nothing in the cores' `LocalBuild` path needs the feed (experiment confirmed: private feed never touched for the 15 libs).

## 5. Open questions and risks for the Xake reproduction

1. **Layout/identity**: sibling relative ProjectReferences (`..\..\..\..\ar-net-core-page\...`, four levels from `ar-net-win/src/X/Y`) force the meta-repo layout. Xake locks/descriptions record paths relative
   to the project root; the experiment found outside-root paths still pending (its step 5). Decide whether xake-ar lives beside or inside `ar-net-meta`.
2. **`LocalBuild` / brand evaluation depends on import of a parent props file by `GetPathOfFileAbove`** and on the lower-case file name `Directory.build.props` (case-sensitive FS trap). The importer must run msbuild
   evaluation with `Brand`, `LocalBuild=true`, and (win) `BuildTmpPath`, `BuildOutPath`, `Configuration=Release`, `LicenseType` (None|EN|JP; changes which shared `projitems` compile), `Version`, `FileVersion`.
3. **Shared projects (`.projitems`/`.shproj`)**: Licensing sources arrive via `<Import Project="...projitems" Label="Shared"/>` conditional on `LicenseType`. Verify the importer expands imported `Compile Include="$(MSBuildThisFileDirectory)..."`.
4. **Generated `VersionInfo.cs`** at `$(BuildTmpPath)\VersionInfo.cs` is created/overwritten by Cake (`SetVersion`) and linked into every win project; `AssemblyNames.cs` consumes `VersionInfo.Version` in constants (assembly name strings). A Xake build must generate it as a rule (or lock its content) and track it.
5. **WPF/XAML** (`Page`/`ApplicationDefinition`, `UseWpf`): csc-only cannot compile markup (BAML generation needs PresentationBuildTasks + `MarkupCompilePass1/2`, `.g.cs`, `.baml` resource). Viewer.Wpf (35 xaml), WpfViewer, VS and installer projects are out of reach for a csc-only approach; they would need a msbuild fallback step.
6. **WinForms resx with non-string resources** (images, `ImageList`, `Icon`): needs `System.Resources.Extensions` (net472) or preserialized resources (net8; Design.Win sets `GenerateResourceUsePreserializedResources`). Cake chooses full MSBuild to avoid the implicit `System.Resource.Extensions` reference on net472. The Xake `.resx` support is "string tables" only (G1 in experiment: `.resx` in a description is refused at lock time), so Design.Win (135), Viewer.Win (27), Advanced.Win (26) need resgen with binary resources, `LogicalName`, `DependentUpon`/`EmbeddedResourceUseDependentUponConvention` naming, culture-specific resx (satellites).
7. **Dropped properties** (from experiment): `NoWarn`, `WarningsAsErrors`, `TreatWarningsAsErrors` handling, `CLSCompliant`, `GenerateDocumentationFile` (+ `InheritDoc` task, `SauceControl.InheritDoc` only in Release), analyzers (`EnableNETAnalyzers`, `CA2022` appears on SDK 10), `NuspecFile` / nuspec token binding, `AddInternalsVisibleTo` target (IVT with `Key`), `EmbedAdditionalResources` (net472), `CopyXmlDocs`, `AutoGenerateBindingRedirects` + `App.config` rewriting (`UpdateAppConfig` in Cake) and `CopyLocalLockFileAssemblies`.
8. **Central package management + transitive pinning** (`CentralPackageTransitivePinningEnabled`) in ar-net-win and dataengine: the lock's transitive versions must follow pinning; version ids contain `$(AssemblyNamePrefix)`/`$(JaSuffix)`/`$(GcPrefix)` interpolations.
9. **Multi-TFM matrix**: `net472;net8.0-windows;net8.0` with `RuntimeIdentifier=win-x64` (set in the csproj even for AnyCPU libs): output path layout, `AppendRuntimeIdentifierToOutputPath=false`, Windows reference assemblies (`Microsoft.WindowsDesktop.App.Ref`) need `EnableWindowsTargeting` on macOS; net472 ref assemblies package. Cake moves only `net472` output into `build/Applications`.
10. **Strong naming**: all projects `SignAssembly` with `activereportskey.snk` (root of ar-net-win, `.keys/` in cores, `key/` in qwhale); win's `publickey.snk`; IVT uses `PublicKey=` constants. csc supports `/keyfile`; verify delay-sign false and `InternalsVisibleTo` key strings.
11. **Obfuscation/Babel** is applied post-build by Cake only for CI builds (`obfuscate=auto`); it is not part of csproj. A Xake reproduction of a *release* artifact needs the Babel license; for build verification stay with non-obfuscated output and say so.
12. **Native/old-style**: `ActiveReports.Interop.C++` vcxproj (v3.5), `QueryDesigner.csproj` stub (old-style), qwhale `net6.0-windows`, localization tools (net35), vs/installer old-style csproj: the importer must skip or flag them.
13. **Private feed packages** (4.3): Xake's `NUGET_FETCH` cannot restore without credentials; locally built nupkgs need a local source mapped in a `NuGet.config`. Package id patterns in `packageSourceMapping` (`Editor*`) are not what the Xake importer reads.
14. **Satellites**: GCCN/JP builds require `ar-net-localization` outputs (`Satellites` folder, compiled resources via `UpdateResources`, `AdditionalResourcesDir`) and also patch resx in ar-net-win before compile (`PatchResources`); EN needs none. Satellite assemblies are produced by `BuildSat.net8.csproj` with `$(dllname)` from `assemblies.csv`.
15. **Pin skew**: ARO develop/20 mixes `ArCoreVersion` 16337 (win) / 16341 (page) / 16319 (reportitems-chart); irrelevant under LocalBuild but visible if Xake locks record package versions. Also the dataengine `.sln` Brand default is `MESCIUS`, the win brand names are `MESCIUS_EN`/`GCCN`/`MESCIUS_JP` (property `Brand` selects `<Brand>.props`; cores accept `MESCIUS` or `GCCN`): use the right name per repo.
16. Not examined: `ar-web-designer` (JS), `ar-web-designer-qa-automation`, `ar-net-win-qa`, any TeamCity build configuration (not in the repos), `ar-net-docs/deployment/*.yaml` contents beyond process docs, and whether Windows msbuild of ar.meta.win.sln succeeds at the pinned commits (nothing was built).
