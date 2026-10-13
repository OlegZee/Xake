# ActiveReports desktop stack on macOS, with the private feed

Date: 2026-10-13. Machine: macOS (Darwin 27), SDK 8.0.425 (csc 4.11, MSBuild 17.11). Meta repo `/Users/olegz/Projects-work/ar/ar-net-meta`,
branch `develop/20`, submodules at their pins. No repo files were edited. This run follows `macos-build.md` (55 of 94 built, 39 failed
restore on the private feed). Logs: `logs/feed-*.log` (text only, no binlogs). Probe files: `scripts/`.

## Bottom line

* **All 94 csproj of `ar.meta.win.sln` build on macOS**, every TFM (net472, netstandard2.0, net8.0, net8.0-windows), exit 0, 0 errors.
  That includes the WinForms designer (Design.Win), Advanced.Win, Viewer.Win, Viewer.Wpf (XAML compiled), all Export.*, the ActiveReports
  facade, and the 5 dev-app exes (Designer, DesignerClassic, Viewer, WpfViewer, ThemeEditor).
* It needs **3 command-line-only changes** (no repo edits):
  1. `-p:EnableWindowsTargeting=true` (as before).
  2. `-p:RestoreConfigFile=scripts/nuget-feed.config`: a flat NuGet config. **The repo NuGet configs do not work inside the meta repo, even
     with a valid token** (see "Restore" below). This is a layout problem, not a credentials problem.
  3. `-p:CustomAfterMicrosoftCommonTargets=scripts/macos-probe.targets` (resx preserialized resources for net472 WinForms/WPF, plus CS9057 as a warning).
     Without it, 23 projects lose their net472 TFM (Chart.Win net472 fails, and everything above it is skipped).
* **Wall time of the full clean `ar.meta.win.sln` build: 26.8 s** (`--no-incremental -m`, build servers shut down first). Restore: 4-21 s.
* Credentials: every NuGet.config in every submodule uses `%AA_AR_TOKEN%`, which matches `.env`. **No name mismatch.** The token is valid:
  both `ar-dev` and `ar-core-dev` answer 200, and every pinned private version exists in both feeds (DVC 0.6.1, SpreadBuilder 3.0.4-alpha-283,
  Editor.Net 1.55.40, QueryDesigner 2.0.3-alpha-201, Core.DataProviders 4.1.3-alpha-1538, Core.Data.DataEngine 5.5.1-alpha-1971).
* `ar.meta.all.sln` (extra 48 projects): imports 7/7 built; vs 9/25; web-designer 4/8; web-viewer 3/8. The failures are classic-VS project
  types (VSSDK/VSIX, non-SDK net472 template projects, `Microsoft.WebApplication.targets`), missing npm-built frontend assets, and 3 code/config
  problems (CS1591, CS0436, SQLite AnyCPU).

## Restore

### Run 1: repo configs as they are (`feed-restore1.log`), 21 s, exit 1

55 projects restore, **the same 39 fail as without the feed**. No NU1301 and no 401: authentication works. Every failure is
`MESCIUS.DataVisualization.Chart` (DVC), and the cause is **package source mapping after NuGet merges the config hierarchy**:

* Every project inside the meta repo sees two configs: `<submodule>/NuGet.config` and the meta repo's `NuGet.config` (`dotnet nuget config paths`
  shows both). NuGet merges them. The child's `<clear/>` removes the parent's *sources*, but the parent's *mapping* patterns stay.
* **ar-net-core-reportitems-chart, ar-net-ai-assistance** (sources: nuget.org, ar-core-dev): the parent maps the exact id
  `MESCIUS.DataVisualization.Chart` to `ar-dev`. That pattern is more specific than the child's `MESCIUS.DataVisualization.*` → ar-core-dev, so it wins.
  But `ar-dev` is not a source there, so no source is considered:
  ```
  ReportItems.Chart.csproj : error NU1100: Unable to resolve 'MESCIUS.DataVisualization.Chart (>= 0.6.1)' for '.NETStandard,Version=v2.0'. PackageSourceMapping is enabled, the following source(s) were not considered: ar-core-dev, nuget.org.
  ```
* **ar-net-win** (sources: nuget.org, ar-dev): its own `ar-dev` mapping lists only `GrapeCity.DataVisualization.*`, not `MESCIUS.DataVisualization.*`.
  The child's entry for the same source key replaces the parent's, so DVC falls through to `nuget.org` `*`:
  ```
  ActiveReports.csproj : error NU1101: Unable to find package MESCIUS.DataVisualization.Chart. No packages exist with this id in source(s): nuget.org. PackageSourceMapping is enabled, the following source(s) were not considered: ar-dev.
  ```
* This explains why the same pins presumably restore in CI, where each repo is cloned alone with no parent config.
  Fix options (not applied): map `MESCIUS.DataVisualization.*` to ar-dev in ar-net-win/NuGet.config and drop the exact-id DVC patterns from the meta
  NuGet.config, or give the meta solution one flat config (what the probe does).

### Run 2: flat config `scripts/nuget-feed.config` (`feed-restore2.log`), 6 s, exit 0

Sources: nuget.org (`*`) and ar-dev (Babel.*, Editor*, semverchecker*, MESCIUS/GCCN.ActiveReports*, MESCIUS/GrapeCity/GCCN.DataVisualization.*,
MESCIUS.Data.*, Gccn.Data.*). The credential is `%AA_AR_TOKEN%`; no secret is stored in the file. **94/94 restored**, no errors or warnings.
`DS.Documents.*` now comes from nuget.org, so DataProviders.Excel no longer needs the public-only probe from the previous run.

Run 3 (`feed-restore3-probe.log`, 4 s, exit 0) repeats it with the probe targets. **This is required**: the probe adds a
`System.Resources.Extensions` PackageReference, so a restore without it leaves MSB3822 behind (`feed-build2-probe.log`: 92x MSB3822, Chart.Win net472).

## Build of ar.meta.win.sln

| Run | Log | Switches beyond the flat config | Result |
|---|---|---|---|
| plain | `feed-build1.log` | none, incremental | exit 1, 20 s. 184 errors, all Chart.Win net472 (92 MSB3822 + 92 MSB3823). 71 projects complete, 19 lose net472, 4 dev apps (net472 only) produce nothing |
| probe, restore without probe | `feed-build2-probe.log` | probe targets, `--no-incremental` | exit 1, 23 s, 92 MSB3822 (System.Resources.Extensions not restored) |
| **probe, restore with probe** | `feed-build3-probe.log` | probe targets, `--no-incremental` | **exit 0, 26.8 s, 0 errors, 52 warnings, 185 primary outputs from 94 projects** |

Note on the plain run: it was incremental over objects from the previous session's probe build, so Design.AI.Ollama was not recompiled there.
In a clean plain build it still fails with CS9057 (`macos-build.md`, category 5); in the probe run CS9057 shows as 18 warnings.

The 23 projects that lose net472 in the plain run: Chart.Win and Chart.Tests (direct MSB3822/3823), then everything that references Chart.Win net472:
ActiveReports.Win, Viewer.Common, Viewer.Win, Viewer.Wpf, Design.Win, Advanced.Win, ThemeEditor; tests Tests, Design.Tests, ThemeEditor.Tests,
Viewer.Tests, Viewer.Wpf.Tests, Export.{Excel,Html,Image,Pdf,Word}.Tests; dev apps Designer, DesignerClassic, Viewer, WpfViewer (none built).
On the Windows/VS toolchain those resx files would compile without the probe. Of all these projects, only Chart.Win itself has an MSB3822/3823 error
in the plain run. The ar-net-win projects were never reached for net472, so it is not known whether they would also need the probe on their own.

### Summary by repo (ar.meta.win.sln, probe run)

| Repo | Total | Built | Failed restore | Failed build | Skipped |
|---|---|---|---|---|---|
| ar-net-core-dataengine | 6 | 6 | 0 | 0 | 0 |
| ar-net-core-page | 24 | 24 | 0 | 0 | 0 |
| ar-net-core-document | 10 | 10 | 0 | 0 | 0 |
| ar-net-core-dataproviders | 2 | 2 | 0 | 0 | 0 |
| ar-net-core-dataproviders-excel | 2 | 2 | 0 | 0 | 0 |
| ar-net-core-reportitems-chart | 3 | 3 | 0 | 0 | 0 |
| ar-net-chart | 3 | 3 | 0 | 0 | 0 |
| ar-net-ai-assistance | 10 | 10 | 0 | 0 | 0 |
| ar-net-win | 34 csproj + 7 shproj | 34 | 0 | 0 | 7 (.shproj, shared code) |
| **Total** | **94 + 7** | **94** | **0** | **0** | **7** |

### Per-project result (all BUILT)

| Repo / projects | TFMs built |
|---|---|
| core-dataengine: DataEngine, ExpressionInfo, VBFunctionLib | netstandard2.0, net472 |
| core-dataengine tests (3) | net8.0, net472 |
| core-page src (12: Drawing.Gc/Gdi, Rdl, Rendering, Scripting, Export.{Excel,Html,Image,Pdf,Svg,Text,Word}.Page) | netstandard2.0, net472 |
| core-page tests (11) | net8.0, net472 |
| core-page dev-apps/Export (new vs. baseline) | net472 |
| core-document src (5) | netstandard2.0, net472 |
| core-document tests (5) | net8.0, net472 |
| core-dataproviders(-excel): DataProviders, DataProviders.Excel | netstandard2.0, net472 |
| core-dataproviders(-excel) tests (2) | net8.0, net472 |
| core-reportitems-chart: ReportItems.Chart (new) | netstandard2.0, net472 |
| core-reportitems-chart: Acceptance, UnitTests (new) | net8.0, net472 |
| chart: Chart / Chart.Win, Chart.Tests | netstandard2.0, net472 / net8.0-windows, net472 (net472 only with probe) |
| ai-assistance: Design.AI.Core, Design.AI, 5 providers, Design.AI.Tests | net8.0, net472 (Ollama: CS9057 as warning via probe) |
| ai-assistance: Design.AI.Reporting, Design.AI.Reporting.Tests (new) | net8.0, net472 |
| ar-net-win: ActiveReports | net472, net8.0-windows, net8.0 |
| ar-net-win: ActiveReports.Win, Viewer.Common, Viewer.Win, Viewer.Wpf, Design.Win, Advanced.Win | net472 (probe), net8.0-windows |
| ar-net-win: ActiveReports.Interop | net472 |
| ar-net-win: Export.Html, Export.Excel, Export.Word, Export.Rdf, Export.Xml | net472, net8.0 |
| ar-net-win: Export.Image, Export.Pdf | net472, net8.0-windows, net8.0 |
| ar-net-win: Export.Image.Win, Export.Pdf.Win | net472, net8.0-windows |
| ar-net-win dev-apps: Designer, DesignerClassic, Viewer, WpfViewer | net472 (exe; probe) |
| ar-net-win dev-apps: ThemeEditor | net472 (exe; probe), net8.0-windows (dll + apphost exe) |
| ar-net-win tests: Tests, Design.Tests, ThemeEditor.Tests, Viewer.Tests, Viewer.Wpf.Tests, Export.{Excel,Html,Image,Pdf,Word}.Tests | net472 (probe), net8.0-windows |
| ar-net-win tests: Export.Rdf.Tests, Export.Xml.Tests | net472, net8.0-windows |

### ar-net-win assemblies produced (probe run, sizes in bytes)

| Assembly (path relative to ar-net-meta/ar-net-win) | net472 | net8.0-windows | net8.0 |
|---|---|---|---|
| src/WinForms/Design.Win/.../MESCIUS.ActiveReports.Design.Win.dll | 11,381,248 | 11,393,024 | |
| src/ReportsEngineCore/ActiveReports/.../MESCIUS.ActiveReports.dll | 1,741,312 | 1,731,584 | 1,731,072 |
| src/WinForms/Viewer.Win/.../MESCIUS.ActiveReports.Viewer.Win.dll | 1,348,096 | 1,349,120 | |
| src/Exports/Export.Pdf.Win/.../MESCIUS.ActiveReports.Export.Pdf.Win.dll | 836,608 | 837,120 | |
| src/WinForms/Viewer.Common/.../MESCIUS.ActiveReports.Viewer.Common.dll | 709,120 | 704,000 | |
| src/Wpf/Viewer.Wpf/.../MESCIUS.ActiveReports.Viewer.Wpf.dll | 701,440 | 702,464 | |
| src/WinForms/Advanced.Win/.../MESCIUS.ActiveReports.Advanced.Win.dll | 697,856 | 698,368 | |
| src/ReportsEngineCore/ActiveReports.Win/.../MESCIUS.ActiveReports.Win.dll | 493,568 | 493,568 | |
| src/ReportsEngineCore/ActiveReports.Interop/.../MESCIUS.ActiveReports.Interop.dll | 252,928 | | |
| src/Exports/Export.Word/.../MESCIUS.ActiveReports.Export.Word.dll | 216,064 | | 216,576 |
| src/Exports/Export.Excel/.../MESCIUS.ActiveReports.Export.Excel.dll | 168,960 | | 168,960 |
| src/Exports/Export.Pdf/.../MESCIUS.ActiveReports.Export.Pdf.dll | 167,424 | 168,448 | 167,936 |
| src/Exports/Export.Html/.../MESCIUS.ActiveReports.Export.Html.dll | 139,264 | | 139,264 |
| src/Exports/Export.Xml/.../MESCIUS.ActiveReports.Export.Xml.dll | 108,032 | | 108,032 |
| src/Exports/Export.Image/.../MESCIUS.ActiveReports.Export.Image.dll | 87,040 | 87,040 | 87,040 |
| src/Exports/Export.Rdf/.../MESCIUS.ActiveReports.Export.Rdf.dll | 67,072 | | 67,072 |
| src/Exports/Export.Image.Win/.../MESCIUS.ActiveReports.Export.Image.Win.dll | 41,472 | 41,472 | |

(`...` = `bin/Release/<TFM>`.) Dev apps in `dev-apps/bin/Release/net472/`: ActiveReports.Designer.exe 122,880; ActiveReports.DesignerClassic.exe 250,368;
ActiveReports.ThemeEditor.exe 232,448; ActiveReports.Viewer.exe 176,128; ActiveReports.WpfViewer.exe 173,568. In `dev-apps/bin/Release/net8.0-windows/`:
ActiveReports.ThemeEditor.dll 232,960 + apphost ActiveReports.ThemeEditor.exe. Full lists: `logs/feed-build3-outputs.txt`, `logs/feed-build3-sizes.txt`.

**So the designer assemblies are produced for both net472 and net8.0-windows** (Design.Win about 11.4 MB each), all signed by csc with `activereportskey.snk`.

## Categories seen (ar.meta.win.sln)

1. **Restore: source mapping after the config merge** (run 1, 39 projects). See "Restore". Not auth, not a missing package.
2. **resx, net472, .NET MSBuild** (plain run, Chart.Win net472, then cascades to 23 projects):
   ```
   error MSB3823: Non-string resources require the property GenerateResourceUsePreserializedResources to be set to true. [Chart.Win.csproj::TargetFramework=net472]
   error MSB3822: Non-string resources require the System.Resources.Extensions assembly at runtime, but it was not found in this project's references. [Chart.Win.csproj::TargetFramework=net472]
   ```
   The probe's PackageReference must be present at restore as well as at build.
3. **Analyzer for a newer Roslyn** (Design.AI.Ollama, CS9057): made a warning by the probe (18 warnings). Unchanged from the baseline.
4. Warnings only (52 unique in the probe run; not blocking):
   * MSB3568 (64 lines): duplicate resource names in `dev-apps/DesignerClassic/DesignerForm.resx`. This is source content, so presumably the same on Windows.
   * MSB3245/MSB3243 (22): `dev-apps/ThemeEditor` net8.0-windows has explicit `<Reference Include="System.Windows.Forms|System.Drawing|System|...">`
     without HintPath, and Export.Excel.Page.Tests net8.0 references `System.Management`. The framework-pack versions are used.
     On Windows these may resolve from the GAC instead. It does not affect compile here.

Not seen: no WPF/XAML failure (Viewer.Wpf: 66 .baml and 7 .g.cs generated in obj; WpfViewer built), no COM interop (no `COMReference` or
`EmbedInteropTypes` in ar-net-win), no licx/LC.exe (the only `licenses.licx` is in `src/Licensing/DemoApp`, which no solution project uses; LicenseType=None),
no Babel (not referenced in any ar-net-win props/targets/csproj; obfuscation lives outside the MSBuild projects), no strong-name failure.

## ar.meta.all.sln (extra repos), dotnet parts only

Restore needed a variant probe `scripts/feed-probe.targets`: the original probe adds `System.Resources.Extensions` to
`ar-net-vs/tests/ShadowVisualStudio.2022`, which already references it, so restore aborts with `NU1504 Duplicate 'PackageReference'` (`feed-restore4-all.log`).
The variant adds it only when the project has no such reference. Restore then succeeds (`feed-restore5-all.log`, 19 s; NU1603 warnings for
VS SDK packages). Build: `feed-build4-all.log`, incremental, 28 s, exit 1, 40 unique errors.

| Repo | Total | Built | Failed restore | Failed build | Skipped (dependency failed) |
|---|---|---|---|---|---|
| ar-net-imports | 7 | 7 (net472; Imports.Excel also netstandard2.0; Imports.exe, Imports.Win.exe) | 0 | 0 | 0 |
| ar-net-vs | 25 | 9 | 0 | 15 | 1 (VisualStudio.2022.Tests) |
| ar-net-web-designer | 8 | 4 | 0 | 3 | 1 (Aspnet.Designer, references Aspnet.Viewer) |
| ar-net-web-viewer | 8 | 3 | 0 | 5 | 0 |

Built: vs: PreviewGeneratorCore (net8.0-windows), ProjectTemplates.2022, RoslynSerializer, RoslynSerializer.Tests, Serializer.2022, VisualStudio.Core,
VisualStudio.Core.Tests, Web.Design.2022, Wizard.2022 (net472). web-designer: AI.Web, AI.Web.Tests, Aspnetcore.Designer, Web.Designer.Tests (net8.0).
web-viewer: Aspnetcore.Viewer (net8.0), Web.Viewer (net472, net8.0), Web.Viewer.Tests (net8.0).

| Failed project(s) | Category | First error |
|---|---|---|
| vs: ItemTemplates CS/VB (8), ProjectTemplates WinFormsCmnRpt{Cs,CsCore,Vb,VbCore} (4) | non-SDK (ToolsVersion 15) net472 projects, no ReferenceAssemblies package | `MSB3644: The reference assemblies for .NETFramework,Version=v4.7.2 were not found.` |
| vs: VisualStudio.2022 | VSIX (non-SDK, imports `VSSDK\Microsoft.VsSDK.targets` via VSToolsPath) | `MSB3644` (first). VSSDK targets would follow |
| vs: ShadowVisualStudio.2022 | build-order input: embeds `ar-net-vs/temp/Dependencies.json`, generated by another step | `CS1566: Error reading resource 'GrapeCity.ActiveReports.VisualStudio.Dependencies.json' -- Could not find a part of the path 'ar-net-vs/temp/Dependencies.json'` |
| vs: PreviewGenerator | platform: SourceGear.sqlite3 rejects AnyCPU | `SourceGear.sqlite3.props(6,9): error : This package does not support Any CPU builds` |
| web-designer: WebDesigner_MVC, web-viewer: JSViewer_MVC | classic ASP.NET (VS-only targets) | `MSB4019: The imported project "Microsoft/VisualStudio/v17.0/WebApplications/Microsoft.WebApplication.targets" was not found.` |
| web-designer: Blazor.Designer, web-viewer: Blazor.Viewer | npm-built frontend assets missing | `MSB3030: Could not copy the file "../DesignerApp/node_modules/@dt/ar-web-designer/dist/esm/web-designer.js"`, `... Viewer/release/dist/jsViewer.min.css` |
| web-designer: WebDesigner_MVC_Core, web-viewer: JSViewer_MVC_Core | analyzer/doc warnings as errors (18 CS1591 each) | `CS1591: Missing XML comment for publicly visible type or member 'DesignController.Index()'` |
| web-viewer: Aspnet.Viewer, Web | licensing stub vs. LocalBuild: `Licensing.None/LicenseStub.cs` compiled in, and also imported from Web.Viewer | `CS0436: The type 'ActiveReportsWebViewerLicenseProvider' in 'ar-net-win/src/Licensing/Licensing.None/LicenseStub.cs' conflicts with the imported type ... in 'MESCIUS.ActiveReports.Web.Viewer'` |

The CS1591, CS0436 and SQLite-AnyCPU failures do not look macOS-specific. They are probably meta-solution configuration issues: MVC_Core dev
apps with doc warnings as errors, the stub licensing conflict under LocalBuild + LicenseType=None, and the solution building PreviewGenerator as Any CPU.
The MSB3644 group could probably be made to build with `FrameworkPathOverride` pointing at the
`microsoft.netframework.referenceassemblies.net472` package in the NuGet cache. That was not tried.

## What remains Windows-only

* **Nothing in `ar.meta.win.sln` at compile time.** All of ar-net-win (WinForms designer, WPF viewer, exports, engine, dev apps) compiles and is signed on macOS.
  The only toolchain difference is net472 resx with binary resources (preserialized resources + System.Resources.Extensions). That changes
  the shipped net472 assemblies compared with a Windows/VS build: an extra runtime dependency and a different resource format.
* **Running** anything: the net472 dev apps and tests, and WinForms/WPF/GDI+ at runtime.
* From `ar.meta.all.sln`: the VSIX (`VisualStudio.2022`, VSSDK) and the classic ASP.NET MVC dev apps (`Microsoft.WebApplication.targets`) need VS MSBuild.
  The non-SDK template projects need .NET Framework reference assemblies (installed with VS, or via an override).
* The web frontends (npm, private registries) were not attempted. The Blazor wrappers fail only because those assets are missing.

## Commands that worked

```bash
# wrapper (scripts/feed-run.sh) loads the token for each call and writes logs/<name>.log; it contains:
#   set -a; source /Users/olegz/Projects-work/.env; set +a
#   cd /Users/olegz/Projects-work/ar/ar-net-meta && dotnet "$@" > logs/<name>.log 2>&1
cd /Users/olegz/Projects-work/ar/ar-net-meta
set -a; source /Users/olegz/Projects-work/.env; set +a
F=/Users/olegz/Projects-work/ar/xake-ar/full

# ar.meta.win.sln: 94/94, exit 0, 26.8 s clean
dotnet restore ar.meta.win.sln -p:LocalBuild=true -p:NuGetAudit=false -p:EnableWindowsTargeting=true \
  -p:RestoreConfigFile=$F/scripts/nuget-feed.config -p:CustomAfterMicrosoftCommonTargets=$F/scripts/macos-probe.targets -v:m
dotnet build-server shutdown
dotnet build ar.meta.win.sln -p:LocalBuild=true -p:NuGetAudit=false -p:EnableWindowsTargeting=true \
  -p:RestoreConfigFile=$F/scripts/nuget-feed.config -p:CustomAfterMicrosoftCommonTargets=$F/scripts/macos-probe.targets \
  -c Release --no-restore --no-incremental -m -v:m

# ar.meta.all.sln: use feed-probe.targets (avoids NU1504 in ar-net-vs)
dotnet restore ar.meta.all.sln -p:LocalBuild=true -p:NuGetAudit=false -p:EnableWindowsTargeting=true \
  -p:RestoreConfigFile=$F/scripts/nuget-feed.config -p:CustomAfterMicrosoftCommonTargets=$F/scripts/feed-probe.targets -v:m
dotnet build ar.meta.all.sln -p:LocalBuild=true -p:NuGetAudit=false -p:EnableWindowsTargeting=true \
  -p:RestoreConfigFile=$F/scripts/nuget-feed.config -p:CustomAfterMicrosoftCommonTargets=$F/scripts/feed-probe.targets \
  -c Release --no-restore -m -v:m
```

Helper scripts (no secrets in them; the token comes from `.env` and is never printed): `scripts/feed-run.sh` (wrapper),
`scripts/feed-has.sh <feed> <id> <version>` and `scripts/feed-query.sh <feed> <id>` (check the Azure Artifacts flat container, print yes/no or versions only),
`scripts/feed-sizes.sh <log>` (sizes of primary outputs). Invocations used: 5 restores + 4 builds (one restore went to the NU1504 dead end).
