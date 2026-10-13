# ActiveReports build scripts: what they do (2026-10-13)

Source: `ar-net-meta` develop/20 at the submodule pins (ar-net-win 20.2.2-alpha-3528). Extends `inventory.md` §2 (not repeated).
Line numbers refer to `ar-net-win/scripts/build.cake` (1379 lines, not 1386) unless a path is given. Nothing was run.
Corrections to the inventory: (a) `NUnit.ConsoleRunner`/`TeamCityEventListener` are declared (l.6-7) but never used, tests run via `dotnet test`;
(b) win's tool manifest is `scripts/.config/dotnet-tools.json` (babel.obfuscator.tool 10.9.0.0, cake.tool 5.0.0, reportgenerator 5.5.4, dotnet-coverage 18.5.2), not repo root;
(c) the installer repo in this checkout is Cake 4.0.0 + WiX **3.14.1** (no `.nuke`); (d) a 4th brand `MESCIUS_KO` exists (ar-net-vs props, deployment manifest `kr`), built from EN.

## 1. ar-net-win `scripts/build.cake`

### 1.1 Invocation and arguments (l.27-38)
Run as `cd ar-net-win/scripts; dotnet cake [--arg=value]` (cwd matters: all paths are `../` relative, l.62-95). Cake.Tool 5.0.0, addins
FileHelpers 7.0.0, SharpZipLib 1.4.2, Cake.Compression 0.3.0, System.Drawing.Common 8.0.19; tools vswhere 2.8.4 (l.4-14).

| arg | default | meaning |
|---|---|---|
| `target` | `MainTask` | task (l.27) |
| `build_number` | `0` | TeamCity counter, goes into the version suffix |
| `branchName` | `local` | drives suffix + obfuscate default |
| `obfuscate` | `auto` | bool, else `!(feature/ or bugfix/ or local)` (l.156) |
| `test` | `auto` | auto = `BuildSystem.IsRunningOnTeamCity` (l.161): tests never run locally by default |
| `Brand` | `MESCIUS_EN` | `MESCIUS_EN` / `MESCIUS_JP` / `GCCN` (l.34); selects `<Brand>.props` in repo root |
| `satellites` | "" | dir with compiled satellites (`<dir>/net472`, `<dir>/net8.0`), required for GCCN/JP (l.188-197) |
| `resources` | "" | dir with compiled `*.resources` to embed (`AdditionalResourcesDir`) |
| `xmldocs` | "" | JP only: dir with Japanese XML docs, required for `ja` (l.209-217) |
| `clean` | "" | `true`/`yes` runs `Clean` |
| `parallelism`, `bidbServer` | -1, `BIDBSERVER` | parallelism unused; bidbServer = SQL host in test env vars (l.526-527) |

Unused leftovers: `GetToolsAssemblies` (l.1130), `parallelism`, `keysDir`/`toolsDir` (l.69). There is no sign task in the win script (signing is done elsewhere in the TeamCity chain/installer).

### 1.2 Setup: Version, FileVersion, VersionSuffix, brand (l.131-184, l.1268-1289)
* `Product` is peeked from `Directory.Build.props` (`ActiveReports 20`), `AssemblyNamePrefix`/`Copyright` from `<Brand>.props`.
* `prefix` = `<Brand>.props /Project/PropertyGroup/VersionPrefix` (l.143-144). **The version line differs per brand**:
  `MESCIUS_EN.props` 20.2.2, `GCCN.props` 20.2.1, `MESCIUS_JP.props` 20.2.0. Same triple in web-viewer, web-designer, imports, vs (+`MESCIUS_KO` 20.2.2).
* `versionSuffix = GetSemanticSuffix(branch, build_number, brand)` (l.1268): brand tag `-cn` (GCCN) / `-ja` (MESCIUS_JP) / "";
  switch on `branch.Split('/')[0]`: `develop` -> `alpha-<n><tag>`; `master` -> "" (no suffix at all, not even -ja/-cn);
  `release`/`hotfix` -> `beta-<n><tag>`; `bugfix`/`feature` -> `parts[1]-<n>` (no brand tag; only the 2nd path segment);
  other (incl. `local`) -> `branch.Replace('/','-')-<n>`, e.g. `local-0`.
* `Version` = `prefix` or `prefix-suffix` (l.158); `FileVersion` = `prefix + ".0"` (l.159); `VersionPrefix` = prefix.
  Examples, develop #123: EN `20.2.2-alpha-123`, JP `20.2.0-alpha-123-ja`, GCCN `20.2.1-alpha-123-cn`; FileVersion `20.2.2.0` / `20.2.0.0` / `20.2.1.0`.
* `Culture`: en-US; `GCCN` -> zh-CN; `MESCIUS_JP` -> ja (l.151-153). `LicenseType` = `JP` for `MESCIUS_JP`, else `EN` (also for GCCN) (l.166).
* `ValidateSettings` (l.186): zh-CN/ja require an existing non-empty `satellites` dir; ja also needs `xmldocs`.
* TeamCity: `SetBuildNumber(Version)` (l.181); `TaskSetup/Teardown` write build blocks (l.113-129).

### 1.3 VersionInfo.cs (l.276-278, 1236-1265; file `ar-net-win/VersionInfo.cs`)
Checked-in file holds two consts (`Version = "20.2.2.0"`, `PackageVersion = "20.2.2-local-0"`). `SetVersion` -> `UpdateProperties(path, ("Version", FileVersion), ("PackageVersion", Version))`:
`UpdateProperty` finds `" <Name> "`, cuts to the next `;`, replaces with ` <Name> = "<value>"`. It is a text rewrite in place (the working-tree file is modified),
then copied to `build/temp/VersionInfo.cs` (l.278, `releaseTempDir`). Every csproj (src, dev-apps, tests) compiles `$(BuildTmpPath)\VersionInfo.cs`
(`BuildTmpPath` default = repo root; cake passes `build/temp`, l.303). `AssemblyNames.cs` uses `VersionInfo.Version` in strong-name strings. This is the only
generated source; assembly/file/informational version come from the MSBuild `Version`/`FileVersion` properties (l.307-308): AssemblyVersion = `Version` without suffix.

### 1.4 Task graph
```
MainTask -> [Build, RunTests, Pack]                  (l.863-869, declared order = execution order)
Build    -> [BuildLocalizationResources, SetupFolders]                       (l.270-272)
 BuildLocalizationResources -> PrepareLocalizationResources -> [SetupFolders, Clean]   (l.249-250, 560-562)
RunTests -> Build   (ContinueOnError, criteria settings.RunTests)            (l.518-521)
Pack     -> Obfuscate (criteria settings.Obfuscate)                          (l.797-798, 769-770)
```
Effective order in `MainTask`: SetupFolders (wipes and recreates `build/`, l.232) -> Clean (powershell `CleanupBinObj.ps1`, only if `clean=true`) ->
PrepareLocalizationResources -> BuildLocalizationResources -> Build -> RunTests -> Obfuscate -> Pack.
Consequences: **tests run on non-obfuscated binaries** (unlike core-page), and `Obfuscate` has no dependency on `Build` (it works on whatever is in `src/**/bin/Release`).

### 1.5 Tasks in detail
* **SetupFolders** (l.229-247): recreates `build/{Resources,WpfTheme,nugets,XmlDocs,temp,temp/temp-resources,test-results,test-coverage}` and `temp/`.
* **PrepareLocalizationResources** (l.560-767, runs for all brands, EN too): for 19 components copies resx sources into `build/temp/<X>/<X>/Res/...`, rewrites
  `ResXFileRef` paths via `ProcessResources` (l.922-987; drops `$this.Localizable`, `*.TrayLocation`), copies `assets/Localization/build.msbuild`, zips to
  `build/Resources/<X>.zip`: AdvancedWin, ARDesigner, CommonViewer, WinViewer, WpfViewer, DesignerApp, ViewerApp, WpfViewerApp, AREngine, AREngine.Win,
  ExcelExport, HtmlExport, ImageExport, PdfExport, PdfExport.Win, WordExport, XmlExport, AdvancedDesigner, ThemeEditor. File filter l.989-1042.
  This is the `ar-resx-sources` bundle of product.yaml (input for ar-net-localization).
* **BuildLocalizationResources** (l.249-267): copies `assets/Localization/*` (not `.msbuild`) to `build/Resources`; `Localize.bat` `1.0.0.0` -> FileVersion; for GCCN replaces
  `MESCIUS`->`GCCN` in the per-component .bat (bug l.265: the result is written to `Localize.bat`, not to the file that was read).
* **Build** (l.270-324):
  1. VersionInfo.cs (1.3).
  2. `DotNetRestore ../ar.win.sln --configfile scripts/NuGet.Config /p:Brand=<Brand> /p:Configuration=Release` (l.280-285). `LocalBuild` is forced false (`Directory.Build.props` l.34: `SolutionName == ar.win`), so
     core deps come as **packages** at `references.props` versions (ArCore 4.6.0-alpha-16337, DataEngine 5.5.1-alpha-1971, DocCore 6.2.0-alpha-1994, DataProviders 4.1.3-alpha-1538, ArChart 20.0.4-alpha-86, ArDvChart 1.2.5-alpha-1301, GcDoc 9.2.1, ...).
  3. **VS MSBuild** (not `dotnet build`; comment l.289-291: avoid implicit System.Resources.Extensions in net472 builds), path `<vswhere VS2022 BuildTools, or any [17,19) with MSBuild>/MSBuild/Current/Bin/amd64/MSBuild.exe` (l.1342-1372):
     `MSBuild ../ar.win.sln /p:Configuration=Release -m:4 PlatformTarget=MSIL` + properties (l.300-309):
     `Brand`, `LicenseType` (EN|JP), `BuildOutPath`=`build/`, `BuildTmpPath`=`build/temp`, `XmldocsOutput`=`build/XmlDocs`, `InheritDocEnabled`=IsRunningOnTeamCity, `NUnit=true`, `Version`, `FileVersion`, `AdditionalResourcesDir`=`resources`.
     Default target (Build) of the whole solution = src libs + dev-apps + all tests (no `-t:`; restore was done separately).
  4. `MoveDirectory build/net472 -> build/Applications` (l.311-312). The 5 dev-apps have `OutputPath=$(BuildOutPath)` (`dev-apps/app.props`), so net472 output lands in `build/net472`; ThemeEditor (also net8.0-windows) leaves `build/net8.0-windows`.
  5. `UpdateAppConfig` (l.887-901): `Applications/ActiveReports.config` (EN) or `assets/config-files/ActiveReportsJa.config` / `ActiveReportsCn.config` (JP/GCCN); GCCN `MESCIUS.`->`GCCN.`; written back to Applications.
  6. `CopyApplicationResources(culture)` (l.874-885): non-en: `satellites/net472` -> `build/Applications/<ja|zh-CN>`.
  7. `PrepareWpfViewerResources` (l.384-456): concatenates 24 `Viewer.Wpf` XAML ResourceDictionaries into one `DefaultWPFViewerTemplates.xaml`, de-duplicating xmlns, adds `;assembly=<prefix>.ActiveReports.Viewer.Wpf` to every `clr-namespace`;
     writes `Wpf/Viewer.Wpf/bin/Release/net472/`, copies to `.../net8.0-windows/` and `build/WpfTheme/`. Post-build step, but required by pack (`Viewer.Wpf.nuspec` l.57,63 packs it as `Themes\Generic.xaml`).
  8. `CreateBuildPropsFile` -> `build/ArBuild.props` (l.1293-1340): `ArVersion`=Version, `ArDll`=FileVersion + 14 dependency versions read from `references.props` (ArCore, DataEngine, GcDoc, SemanticKernel(+WithAlpha), DataProviders(+Excel), DocCore, ArChart, ArDvChart, SpreadBuilder, QueryDesigner, Editor, GcExcel, AiAssistance). Consumed by web-viewer/web-designer/imports/vs/installer.
  9. `CleanUpApplicationsFolder` (l.356-382): deletes `App.config` and culture dirs foreign to the brand (JP keeps `ja`, GCCN keeps `zh-CN`/`zh`, EN none).
  10. `GenerateApplicationSboms` (l.329-354): `powershell.exe scripts/devtools-sbom-app.ps1 build/Applications -Project dev-apps/*/*.csproj -Version <prefix> -AssemblyNamePrefix .. -LicenseUrl <EulaUrl>`; throws on non-zero.
* **RunTests** (l.518-558): env `TestSettings.SqlServerNorthWind/AdventureWorks` (SQL Server user `qatester`, host = `bidbServer`);
  `dotnet test ar.win.sln -c Release --no-build -f net472 --results-directory build/test-results --logger "trx;LogFilePrefix=nunit" --settings net472.runsettings`;
  then `-f net8.0-windows --settings net80.runsettings -tl:off --blame-hang-timeout 1800000 --collect "Code Coverage;Format=Cobertura"`; then `dotnet reportgenerator -reports:build/test-results/**/*.cobertura.xml -targetdir:build/test-coverage -reporttypes:Html`.
  runsettings (in `scripts/`): `MaxCpuCount 1`, NUnit `DefaultTimeout 1800000`, `DefaultTestNamePattern {m}{a}_net472|_net80`. 12 test projects, all `net472;net8.0-windows`. Failures do not fail the build (ContinueOnError).
* **Obfuscate** (l.769-784, 1073-1128): files = `src/**/<csproj dir>/bin/Release/<tfm>/<prefix>.ActiveReports.<csprojName>.dll` (prefix MESCIUS or GCCN; `ActiveReports.ActiveReports`->`ActiveReports`; tests excluded)
  for tfm in `net472`, `net8.0`, `net8.0-windows`. Per dll: `dotnet babel <dll> --license env:BABEL_LICENSE_KEY_10_9 --project ../src/ActiveReports.babel --rules ../src/ObfuscationRules.xml
  --flatns GrapeCity.<Name>.Internal --keyfile activereportskey.snk --output <same dll> --warnaserror W00000`. Key name derives from tool version `10.9.0.0` -> `BABEL_LICENSE_KEY_10_9`; a missing key is a warning on branch `local`, otherwise throws.
  Only libs in `src/*/bin/Release` are obfuscated; the copies already moved to `build/Applications` stay un-obfuscated (open question: the real chain may re-obfuscate/sign elsewhere).
* **Pack** (l.797-861, 1155-1212):
  * JP only (l.802-858): copies `MESCIUS.ActiveReports*.xml` Japanese docs from `xmldocs` into 17 `src/<proj>/bin/Release/<tfm>/` dirs, overwriting the English XML.
  * `DotNetPack ../ar.win.sln -c Release --no-build -o build/nugets /p:LocalBuild=false /p:Brand=<Brand> /p:JaSuffix=<""|".ja"> /p:Pattern=<"**.resources.dll" unless ja/zh-CN>
    -p:PackageVersion="<Version>" -p:BuildOutPath="<build/Applications>" -p:SatellitesPath="<satellites | temp/satellites*/>"` (l.1175-1188). Without satellites cake creates empty `temp/satellites/{net472,net8.0}`.
  * `MakeLowerCase`: renames `*.nupkg` files to lowercase (l.1224).
  * SBOM: `powershell.exe scripts/devtools-sbom-nuget.ps1 build/nugets -LicenseName EULA -LicenseUrl <EulaUrl>` rewrites each nupkg adding `sbom/{tfm}/bom.cdx.json`.

### 1.6 What `Brand` changes
| item | MESCIUS_EN | MESCIUS_JP | GCCN |
|---|---|---|---|
| props file | `MESCIUS_EN.props` | `MESCIUS_JP.props` | `GCCN.props` |
| VersionPrefix | 20.2.2 | 20.2.0 | 20.2.1 |
| AssemblyNamePrefix / asm names | `MESCIUS.ActiveReports.*` | `MESCIUS.ActiveReports.*` | `GCCN.ActiveReports.*` |
| DefineConstants (all libs) | `MESCIUS` | `MESCIUS;LICENSE_JP` | `GCCN` (`#if GCCN` in `CommonAssemblyInfo.cs`) |
| LicenseType -> shared projitems | EN (`Licensing.EN[.Web/.Win]`) | JP (`Licensing.JP[.Web/.Win]`) | EN |
| Product | `ActiveReports 20` (common) | same | same |
| GcPrefix (imaging package) | `DS.Documents.Imaging` | `DS.Documents.Imaging` | `GcDocs.Imaging` |
| Culture / satellites | en-US / none | ja / `satellites` + `xmldocs` required | zh-CN / `satellites` required |
| nupkg id | `MESCIUS.ActiveReports.X` | `MESCIUS.ActiveReports.X.ja` (`JaSuffix`) | `GCCN.ActiveReports.X` |
| version suffix | `alpha-n` | `alpha-n-ja` | `alpha-n-cn` |
| ActiveReports.config | stock | `ActiveReportsJa.config` | `ActiveReportsCn.config` + `MESCIUS.`->`GCCN.` |
| nuspec assets | `assets/NuGet/MESCIUS_EN/{license.txt,icon.png,readme/}` | `.../MESCIUS_JP/` | `.../GCCN/` |
| buildTransitive targets | `MESCIUS.ActiveReports.targets` | `MESCIUS.ActiveReports.Ja.targets` | `GCCN.ActiveReports.targets` |
| Authors/Eula/ProjectUrl | mescius.com | mescius.jp | grapecity.com.cn |

### 1.7 Outputs
`build/Applications/` (apps + all libs, net472, `ActiveReports.config`, `sbom/`, `ja|zh-CN/`), `build/net8.0-windows/` (ThemeEditor), `build/Resources/*.zip + *.bat`, `build/WpfTheme/`,
`build/XmlDocs/<tfm>/*.xml` (English docs, `CopyXmlDocs` target in `src/Directory.Build.targets`; always on because cake passes `XmldocsOutput`, `Directory.Build.props` l.48-49), `build/nugets/*.nupkg` (13 packages), `build/ArBuild.props`, `build/test-results`, `build/test-coverage`,
`build/temp/VersionInfo.cs`. Libs also stay in `src/<area>/<proj>/bin/Release/<tfm>/`, which is what the nuspecs pack (`src="..\X\bin\Release\<tfm>\..."`; Release is hard-coded in the nuspecs).

### 1.8 The real CI matrix (Configuration x Brand x TFM)
* **Configuration**: Release only. `Configuration=Release` is hard-coded in restore/build/test/pack (l.285,294,533,1177) and in nuspec paths. Debug exists in `ar.win.sln`/`ar.meta.win.sln` (only `Debug|Any CPU` and `Release|Any CPU`; no Release_NoTests/Release_Ja in win) but no script builds it.
  Other repos: cores `Release` or `Release_NoTests` (`test=false`), web-viewer/designer `Release` / `Release_Ja` (JP), chart/imports `Release` (`config` arg).
* **Brand**: 3 for win (`MESCIUS_EN`, `MESCIUS_JP`, `GCCN`) = product.yaml variants `en`, `ja`, `zh`; KR reuses EN (deployment-manifest.kr.yaml; `MESCIUS_KO` only in vs). Core repos have only `MESCIUS`/`GCCN` (JP uses MESCIUS packages without `.ja`).
  JP/GCCN need localization output first (build order: ar-net-localization -> win).
* **TFM per project** (win): net472 + net8.0-windows + net8.0 (ActiveReports, Export.Image, Export.Pdf); net472 + net8.0 (Export.Excel/Html/Word/Xml/Rdf); net472 + net8.0-windows (ActiveReports.Win, Export.Image.Win, Export.Pdf.Win, Advanced.Win, Design.Win, Viewer.Common, Viewer.Win, Viewer.Wpf, ThemeEditor, all 12 test projects);
  net472 only (Interop, Viewer, Designer, DesignerClassic, WpfViewer apps). Cores: net472 + netstandard2.0; chart: Chart netstandard2.0+net472, Chart.Win net8.0-windows+net472; web: net472 and net8.0 projects.
* Tests only on TeamCity (`test=auto`); obfuscation on non-feature/non-local branches; a Windows host is required for everything above.

## 2. Other repos (tool, entry, brand/config, runnable with `dotnet` alone?)
Common: Cake.Tool 5.0.0 local tool, TeamCity build blocks, identical `GetBabelLicense()`, copied `GetSemanticSuffix`, `Directory.Build.targets` with `SetNuspecProperties` (nuspec tokens) + `GetSemanticVersionRange` (RoslynCodeTaskFactory).
No Makefile, no `package.json` build scripts except the JS projects (web-viewer `src/Viewer`, web-designer `src/DesignerApp`); only `.fsx` is win `scripts/Verify-Deployment.fsx` (manifest verifier; docs has `Verify-Deployment.ps1`).

| repo | entry | tasks / chain | brand, config, version | needs |
|---|---|---|---|---|
| core-page | `dotnet cake scripts/build.cake` | `Pack -> Test -> Obfuscate -> Build -> CleanBinObj`. Pack = restore + `DotNetPack --no-build` + SBOM `dotnet script devtools-sbom-nuget.csx -SbomSettings src/<Brand>.sbom.settings.xml`; copies netstandard2.0 dlls to `artifacts/assemblies/core` | args `Brand` (MESCIUS), `versionSuffix` (auto), `test`, `obfuscate` (auto = No on feature/bugfix/local; `eval` = babel without warnaserror); `-c Release` or `Release_NoTests`; `dotnet build <sln> /p:Brand= --version-suffix`; VersionPrefix from `Directory.Build.props` (4.6.0); `src/{MESCIUS,GCCN}.props` | `dotnet` + babel license; Windows only to run net472 tests. **Tests run on obfuscated dlls** |
| core-dataengine, -document, -dataproviders, -excel, -reportitems-chart | same template | `Pack -> Obfuscate -> Test -> Build -> CleanBinObj` (Test BEFORE obfuscate; page is the odd one). excel/chart: suffix not overridable. reportitems-chart Pack also `MergeAssemblies` (babel `--internalize --noflatns` merge of DV chart into Rendering netstandard/net472, l.315-320). document uses `.keys/activereportskey.snk` + `--keyfile` | VersionPrefix 5.5.1 / 6.2.0 / 4.1.3 / 2.2.2 / 1.2.5 | `dotnet` (see inventory §4 for mac) |
| chart | `cd src; dotnet cake` | `Pack -> [Sign -> Obfuscate -> Test -> Build -> (CleanBinObj, BuildSatellites)], PackResources`. Satellites: `l10n/*.resources.csproj` x cultures ja, zh-CN built with **MSBuild.exe** (`/p:LocalBuild=false /p:Brand /p:MSBuild=true`, env `culture`, `OutputPath`). Build edits `VersionInfo.cs`, restore + MSBuild `ar.chart.sln`. Sign = AzureSignTool. Babel on Chart/Chart.Win. Pack JP copies Ja XML docs from `l10n/docs/ja`; `DotNetPack` with `ChartResourcesPath`, `NuspecsFolder`, `JaSuffix`; SBOM via powershell | `Brand` default `MESCIUS_EN` (also `GrapeCity_EN/JP.props`), `config` Release, `sign`/`obfuscate` auto, `certificateSignCommand`; 20.0.4; suffix without brand tag | Windows (VS MSBuild, powershell, AzureSignTool) |
| ai-assistance | `dotnet cake scripts/build.cake` | `Pack -> Obfuscate -> Test -> Build`; tests only for MESCIUS brands; writes `AiBuild.props`; `DotNetPack -p:PackageVersion` | `Brand` MESCIUS_EN, `xmldocs`; 20.2.1; 11 projects net472+net8.0 | `dotnet`; SBOM powershell |
| imports | `cd src; dotnet cake` (default `Deploy`) | `Deploy -> [GenerateSbom -> Publish -> (Sign -> Obfuscate -> Test -> Build -> (Clean, BuildSatellites), Pack)], PackResources`. Pack only for `MESCIUS_EN` (4 test-support nupkgs Access/Crystal/Excel/RPX). Build via MSBuild.exe; reads `../input/ArBuild.props` and pokes versions into props; zips `import-tools-<n>.zip` | brands as win; `packJa`, `JaBuild`; 20.2.2/20.2.1/20.2.0 | Windows (MSBuild, powershell, Crystal) |
| web-viewer | `dotnet cake scripts/build.cake` (default `Deploy`) | `Deploy -> [Clean, Pack -> Obfuscate -> Test -> Build -> (BuildJsViewer, ExtractArBuildProps)]`. JS: `npm ci --legacy-peer-deps`, scripts `build`, `build:esm`, `build:types`, `localize:*`, `vulnerability-audit`, `arf-sbom`, `npm version`, `npm pack` -> `output/npm/*.tgz`. .NET: restore/build `ar.web.viewer.sln` with `/p:Configuration=Release[_Ja]`, `ArDependenciesVersionsFile=<win ArBuild.props>` (`versions-file`), `AdditionalResourcesDir` | `Brand` MESCIUS_EN; npm folder `release_<Brand>` | node+npm, `dotnet` (net472 projects need Windows to run) |
| web-designer | `dotnet cake scripts/build.cake` | `Deploy -> Pack -> Obfuscate -> Test -> Build -> (Clean, BuildJs [npm script `build-<brand>`, `update-sbom-metadata-<brand>`], ExtractBuildProps)`; reads `../input/JsViewerBuild.props` | same | node+npm, `dotnet` |
| localization | `dotnet cake` (target `Compile`; `Sync` = SyncAr+SyncXmlDocs+GitCommit, CI actions `sync-ja`/`sync-zh` in product.yaml) | args `prefix` (MESCIUS/GCCN/GrapeCity), `culture` (zh-CN/ja), `build_number`, `certificateSignCommand`. `assemblies.csv` (18 components). Per component: resx -> `.resources` (GenerateResource, SdkToolsPath `.tools/.NET 4.0`) -> `al.exe` link into `<Asm>.resources.dll` (`BuildSat.msbuild`; version = build_number; strong-named `activereportskey.snk`) = **net472 satellites**; **net8.0 satellites** = `BuildSat.net8.csproj` via `dotnet build` (props OutputPath, AssemblyName, Culture, RootNamespace; System.Resources.Extensions 8.0.0); VSPackage `.vsct` by VS `vsct.exe`; `BuildRes.msbuild` makes `*.resources` for embedding; AzureSignTool per dll | win props unused; output `build/Satellites/<culture>/{net472,net8.0}`, `build/Resources/<culture>` | Windows (al.exe, resgen, vsct, MSBuild); the net8.0 half is pure `dotnet` |
| vs | `build.ps1/.sh/.cmd` -> Nuke `build/_build.csproj` (SDK 9.0.300) | `Clean`, `UpdateCurrentVersion`, `UpdateLicenseCopyright`, `CompilePreviewGenerators`, `BuildDependenciesFile` (restores AR nupkgs to temp/packages), `ExportNugetRestoreProjects`, `UpdateFavicon`, `CompileVs2019/2022` (MSBuild Restore+Build, `DotNetPack`), `Test`, `GeneratePackageSboms`, `Publish` (-> `ActiveReports.VisualStudio2019/2022.vsix`) | Nuke params `Brand` (GCCN/MESCIUS_EN/_JP/_KO), `build-number`, `branch`, `run-tests`; suffix `local` unless CI branch | Windows + VS 2022 + VSSDK |
| installer (`ar-net-installer`) | `cd build-scripts; dotnet cake` (Cake 4.0.0, target `MainTask`), `getnugetdependencies.cake` | `UpdateCopyright -> Setup -> NugetRestore` (restores AR nupkgs by Brand, unzips chart/querydesigner resource nupkgs), `DownloadNpmPackages`, `MergeVS` (babel merge of VS dlls), WiX 3.14.1 on `Setup/EnInstaller/ArInstaller.wixproj` + WPF `SetupWizard`/`Bootstrapper`, 7-Zip, signing | args `Brand` MESCIUS/GCCN (GrapeCity: no installers), `setupCulture`, `lightInstaller`, `arFolder` (=`ar-net-win/build`), `importFolder`, `webDesignerFolder`; outputs `ActiveReports.v<ver>.exe` (+`_light`), `.md5`, `.cdx.json` | Windows only (WiX 3) |
| docs | `scripts/Verify-Deployment.ps1` (+ win `.fsx`) | verifies `deployment/deployment-manifest.{en,gccn,ja,kr}.yaml` against the deploy folder | ja = `.ja` suffix on UI/export packages, core without | `dotnet fsi` / pwsh |

`product.yaml` (meta root) is the only declarative description of the CI graph: variants `en` (prefix MESCIUS), `zh` (GCCN, +localization), `ja` (MESCIUS, suffix `ja`, +localization, `installer-ja`),
`build_units` with `build.command` and `depends_on` (win depends on page, document, dataengine, dataproviders(+excel), reportitems-chart, chart, localization), and artifact flow `win-resources-to-localization`.
The meta `bitbucket-pipelines.yml` runs only AI review + gitleaks; real builds are TeamCity (not in the repos).

## 3. Pack / publish layer
* **Mechanism everywhere**: SDK `dotnet pack <sln> --no-build` with `NuspecFile=<ProjectName>.nuspec` (set in each repo's `Directory.Build.targets`; win: only when `PackageVersion` is given and `IsPackable`) + `NuspecProperties` tokens
  (`$version$`, `$prefix$`=AssemblyNamePrefix, `$jaSuffix$`, `$gcprefix$`, `$resourceFolder$`=zh-CN|ja, `$satellitesPath$`, `$pattern$`, `$brandFilePrefix$`=Brand, `$authors$`, `$copyright$`, `$iconUrl$`, `$projectUrl$`, `$packageDescription$`, `$*VersionRange$`).
  No `nuget.exe pack`. Nuspecs list assemblies by hard-coded `bin\Release\<tfm>\` paths, so a Release build is a precondition.
  Dependency ranges (`GetSemanticVersionRange`): prerelease or major 0 -> `[v]`, otherwise `[v,major+1.0.0)`.
* **Package ids**: win `<prefix>.ActiveReports[.<Area>]<jaSuffix>`; 13 nupkgs: ActiveReports (bundles ActiveReports.Win), .Interop, .Export.{Excel,Html,Image (+Image.Win),Pdf (+Pdf.Win),Rdf,Word,Xml}, .Design.Win (+Advanced.Win, Editor.Net), .Viewer.Common, .Viewer.Win, .Viewer.Wpf.
  (product.yaml lists Advanced.Win/Win/Image.Win/Pdf.Win as packages; they are `IsPackable=false` and ride inside others.) Cores: `<prefix>.ActiveReports.Core.{Data.DataEngine,Data.ExpressionInfo,Data.VBFunctionLib,Rdl,Rendering,Scripting,Drawing.Gc,Drawing.Gdi,Document,Document.Drawing.*,Export.*.Page,Export.Pdf.Section,Export.Tiff.Section,DataProviders[.Excel],ReportItems.Chart}` (no `.ja`);
  chart `<prefix>.ActiveReports.Chart[.Win][.ja]` + `.Chart.Resources`; AI `...Design.AI[.Core|.Reporting|providers]`; web `AspNet/AspNetCore/Blazor[.Designer]`, `Web`, `Web.Viewer`, `AI.Web`; vs `Serializer`, `Web.Design[.VS2022]`.
  GCCN = prefix GCCN everywhere; JP = prefix MESCIUS + `.ja` on win/chart/web/ai UI packages only. Cake lowercases the nupkg file names (win, web).
* **Feeds** (every `NuGet.config` has `<clear/>`, nuget.org `*`, plus an Azure DevOps feed `dt4dev/AR`, user `dt4dev`, `%AA_AR_TOKEN%`): cores/document/dataproviders/excel use **ar-core-dev**; win, chart, ai, web-*, imports, vs, ARO use **ar-dev**. CI-built alpha/beta packages are consumed from these feeds
  (win `references.props` pins `-alpha-N` versions). The push (`dotnet nuget push`) is in no script: it is TeamCity publishing. Public release (deployment manifests): nuget.org, npm (`@mescius/activereportsnet-viewer`, `-designer`), installers; signed nupkgs/assemblies, embedded SBOM `sbom/{tfm}/bom.cdx.json`.
* **Version lines (develop/20 pins)**: win/web/imports/vs per brand EN 20.2.2, GCCN 20.2.1, JP 20.2.0, KO 20.2.2; chart 20.0.4; ai-assistance 20.2.1; page 4.6.0; document 6.2.0; dataengine 5.5.1; dataproviders 4.1.3; excel 2.2.2; reportitems-chart 1.2.5; spreadbuilder 3.0.4; vqd 2.0.3; GcDoc 9.2.1.
  Suffix rules are identical in all scripts (alpha on develop, beta on release/hotfix, none on master); product repos append `-ja`/`-cn`, cores do not.

## 4. Reproduction checklist for Xake (one brand, Release)
The meta solution `ar.meta.win.sln` (94 csproj, `LocalBuild=true` from meta `Directory.build.props`) uses ProjectReferences to the cores; `ar.win.sln` uses the pinned nupkgs. Decide which one is the target.
1. Inputs: brand B in {MESCIUS_EN, MESCIUS_JP, GCCN}; branch, build number -> `Version`, `FileVersion`, `VersionSuffix` exactly as 1.2 (brand-specific VersionPrefix from `B.props`); culture, LicenseType (JP only for MESCIUS_JP).
2. Rewrite the `VersionInfo.cs` consts (1.3) in a copy under `build/temp` and point `BuildTmpPath` at it; avoid mutating the checked-in file.
3. Properties for every project: `Brand`, `LicenseType`, `BuildOutPath`, `BuildTmpPath`, `XmldocsOutput`, `InheritDocEnabled`, `NUnit=true`, `Version`, `FileVersion`, `AdditionalResourcesDir`, `Configuration=Release`, `LocalBuild` (win: false); for pack also `PackageVersion`, `JaSuffix`, `Pattern`, `SatellitesPath`, `BuildOutPath` (=Applications).
   Implied compile flags: `DefineConstants=<Prefix>[;LICENSE_JP]` (+`TRACE;DEBUG` if not Release), `LangVersion 12`, `TreatWarningsAsErrors` (except NU1901-4, CS9057), strong name `activereportskey.snk` (SignAssembly, no delay sign), `RuntimeIdentifier win-x64` (ActiveReports), assembly names `$(AssemblyNamePrefix).ActiveReports.<X>`.
4. Restore with `scripts/NuGet.Config` (token `AA_AR_TOKEN`); resolve core packages from `references.props` versions (ar.win.sln) or take project references (meta).
5. Compile order (per TFM, topological): cores (dataengine, page libs, document, dataproviders, reportitems-chart, chart) -> `ActiveReports` (net472, net8.0-windows, net8.0; embeds resx, shared `Licensing.<LicenseType>` projitems) -> `ActiveReports.Win`, `ActiveReports.Interop` -> Export.* (Excel, Html, Word, Xml, Rdf, Image, Pdf) -> Export.Image.Win, Export.Pdf.Win ->
   Viewer.Common -> Viewer.Win, Viewer.Wpf -> Advanced.Win -> Design.Win (+Editor.Net.dll) -> dev-apps (Designer, DesignerClassic, Viewer, WpfViewer, ThemeEditor) -> tests (optional). csc inputs: all `.cs` incl. linked `AssemblyNames.cs`, `CommonAssemblyInfo.cs`, `VersionInfo.cs`; resx compiled to `.resources` with the csproj `LogicalName`s; the net472 outputs must not reference `System.Resources.Extensions` (the reason for MSBuild.exe).
6. Resources: `AdditionalResourcesDir` embedding (net472 only): `<dir>/<AssemblyName>/*.*` as non-resx EmbeddedResource named `<file>.resources` (target `EmbedAdditionalResources`). XML docs: GenerateDocumentationFile, copy to `XmldocsOutput/<tfm>`.
7. Satellites (JP/GCCN): take from ar-net-localization output (`net472`, `net8.0` `*.resources.dll`), copy to `Applications/<ja|zh-CN>`, pack into `lib/<tfm>/<folder>/` (net8.0-windows7.0 uses the net8.0 set). EN: none. JP also overwrites the English XML docs with the Japanese ones before pack.
8. Post-build: move `build/net472` -> `build/Applications`; `ActiveReports.config` per brand; delete `App.config` and foreign culture dirs; generate `DefaultWPFViewerTemplates.xaml` (before pack); write `ArBuild.props`.
9. Pack the 13 nuspecs with the tokens of section 3 (zip layout `lib/<tfm>/`, `buildTransitive/<tfm>/`, `tools/`, license/icon/readme from `assets/NuGet/<Brand>/`); lowercase the file names.
10. Optional/CI: tests net472 + net8.0-windows with the runsettings; obfuscation (babel 10.9, license key) between test and pack; SBOM; 19 resx source zips (`PrepareLocalizationResources`).

Out of csc scope (need other tools): XAML markup compile for `Viewer.Wpf`/`WpfViewer` (`<Page>` items -> BAML, WPF MSBuild targets), WinForms/WPF reference assemblies (Windows or targeting packs), `ResXFileRef` resolution, Babel obfuscation/merge (license), AzureSignTool signing (win cake has none), `devtools-sbom-*.ps1`, satellite assemblies (`al.exe`, `resgen`, `vsct.exe`; the net8.0 variant is plain `dotnet`), VSIX (VSSDK, ar-net-vs), installer (WiX 3.14.1 + WPF bootstrapper), web JS bundles (npm), native `ActiveReports.Interop.C++`.
Open questions: (1) are the `Applications` copies obfuscated/signed by TeamCity after Cake? (2) what `NUnit=true` changes in test projects; (3) exact TeamCity step order across repos (only product.yaml hints).
