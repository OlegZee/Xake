# ActiveReports desktop stack on macOS: plain `dotnet build` baseline

Date: 2026-10-13. Machine: macOS (Darwin 27), meta repo `/Users/olegz/Projects-work/ar/ar-net-meta`, branch `develop/20`,
submodules at their pinned commits. No repo files were edited. Logs, binlogs and probe files are in
`/Users/olegz/Projects-work/ar/xake-ar/full/{logs,scripts}/`.

## Bottom line

* `ar.meta.win.sln` has **101 projects**: 94 csproj + 7 Licensing `.shproj` (shared projects, nothing to build).
  `ar.meta.all.sln` has **149** (the same 101 plus ar-net-imports 7, ar-net-vs 25, ar-net-web-designer 8, ar-net-web-viewer 8).
* **55 of 94 csproj build** on macOS, all TFMs (net472, netstandard2.0, net8.0, net8.0-windows). 39 fail, **all at restore**,
  because of private-feed packages. **None fails because the OS is macOS.**
* Pinned sources as they are (only `-p:` switches): 51 build. With two probes that stand in for small source changes
  (see "Probes"), 4 more build: DataProviders.Excel (+tests), Chart.Win net472 (+Chart.Tests net472), Design.AI.Ollama (+Design.AI.Tests).
* The main blocker is `MESCIUS.DataVisualization.Chart 0.6.1`. It is private, not on nuget.org, and not in the meta repo as source.
  `ReportItems.Chart` needs it, and every ar-net-win project needs `ReportItems.Chart` through `ActiveReports.csproj`
  (LocalBuild ProjectReference). So **no ar-net-win project except ActiveReports.Interop can be built without the ar-dev feed, on any OS**.
* Toolchain: SDK **8.0.425** (global.json 8.0.100 + latestFeature), csc **4.11.0-3.25569.22**, MSBuild 17.11.
  **`EnableWindowsTargeting=true` is required.** Without it restore stops with NETSDK1100 for every net8.0-windows TFM (a restore run that accidentally lacked the flag showed this).
* A clean rebuild (`--no-incremental`, build server shut down first) of the whole solution, i.e. the 55 buildable projects plus fast failures, took **28.8 s wall**
  (84 s user) with `-m`. Restore with a warm cache took 4-42 s.
* net472 outputs of ar-net-win: only `ar-net-win/src/ReportsEngineCore/ActiveReports.Interop/bin/Release/net472/MESCIUS.ActiveReports.Interop.dll`.
  `ar-net-win/dev-apps/bin/Release/{net472,net8.0-windows}` exist but are empty.

## Properties learned

| Property | Where | Meaning / value used |
|---|---|---|
| `LocalBuild` | meta `Directory.build.props` sets `true`; ar-net-win forces `false` only when `SolutionName=='ar.win'` | Switches `$(AssemblyNamePrefix).ActiveReports.*` PackageReferences to ProjectReferences into sibling submodules. Passed as `-p:LocalBuild=true` too |
| `Brand` | ar-net-win, ar-net-chart, ar-net-ai-assistance: default `MESCIUS_EN.props` (also `MESCIUS_JP`, `GCCN`, chart also `GrapeCity_EN/JP`); ar-net-core-document: default `MESCIUS.props` (also `GCCN`); core-page/dataengine/dataproviders: none | Picks `$(Brand).props` (VersionPrefix, AssemblyNamePrefix=MESCIUS, GcPrefix=DS.Documents). **Left unset.** A global `-p:Brand=` cannot fit every repo: `MESCIUS_EN` has no file in core-document, `MESCIUS` has none in win/chart/ai |
| `LicenseType` | ar-net-win, default `None` | Imports `Licensing.None.projitems` (shared project). EN/JP import the `.shproj` items |
| `BuildOutPath` | ar-net-win, default `ar-net-win/dev-apps/bin/$(Configuration)` | Output folder for the dev apps |
| `GenerateXmlDocs` | ar-net-win, true only if `XmldocsOutput` is set | Unset, so no XML docs |
| Others | ar-net-win | `TreatWarningsAsErrors=true`, `WarningsNotAsErrors=NU1901-4;CS9057`, `SignAssembly=true` (activereportskey.snk), `LangVersion=12.0`, MSB3822/MSB3823 listed in `MSBuildWarningsAsMessages` |
| Versions | `ar-net-win/references.props` | ArCore 4.6.0-alpha-16337, DocCore 6.2.0-alpha-1994, DataEngine 5.5.1-alpha-1971, ArDvChart 1.2.5-alpha-1301, SpreadBuilder 3.0.4-alpha-283, QueryDesigner 2.0.3-alpha-201, Editor 1.55.40, GcDoc/GcExcel 9.2.1, SemanticKernel 1.74.0 |

TFMs used by ar-net-win projects: `net472`, `net8.0`, `net8.0-windows` (combinations: `net472;net8.0-windows` for WinForms/WPF and tests;
`net8.0;net472` for exports; `net472;net8.0-windows;net8.0` for ActiveReports and Export.Image/Export.Pdf; net472 only for the dev apps and Interop).
Core repos: `netstandard2.0;net472` (libraries), `net472;net8.0` (tests). ar-net-chart: `netstandard2.0;net472` (Chart), `net8.0-windows;net472` (Chart.Win, Chart.Tests).

## Per-project result

Legend: BUILT (TFMs) = compiled. "probe" = built only with a probe from the "Probes" section. FAILED-RESTORE = a package is missing
(the feed is named). Root-cause packages: **DVC** = `MESCIUS.DataVisualization.Chart 0.6.1` (ar-dev / ar-core-dev, not on nuget.org at all);
**SB** = `MESCIUS.ActiveReports.SpreadBuilder 3.0.4-alpha-283`; **ED** = `MESCIUS.ActiveReports.Editor.Net 1.55.40`; **QD** = `MESCIUS.ActiveReports.QueryDesigner 2.0.3-alpha-201`
(SB/ED/QD: ar-dev; nuget.org has only older public versions 3.0.3 / 1.55.38 / 2.0.2).

### ar-net-core-dataengine (6): all BUILT
| Project | Result |
|---|---|
| src/DataEngine, src/ExpressionInfo, src/VBFunctionLib | BUILT netstandard2.0, net472 |
| tests/DataEngine.Tests, ExpressionInfo.Tests, VBFunctionLib.Tests | BUILT net8.0, net472 |

### ar-net-core-page (24): 23 BUILT, 1 FAILED-RESTORE
| Project | Result |
|---|---|
| src/Drawing.Gc, Drawing.Gdi, Rdl, Rendering, Scripting, Exports/{Excel.Page, Html.Page, Image, Pdf, Svg, Text, Word.Page} (12) | BUILT netstandard2.0, net472 |
| tests/{Drawing.Gdi, Drawing, Rdl, Rendering, Scripting}.Tests, tests/exports/{Excel.Page, Html.Page, Pdf.Page, Svg, Text, Word.Page}.Tests (11) | BUILT net8.0, net472 |
| dev-apps/Export | FAILED-RESTORE: `MESCIUS.ActiveReports.Core.DataProviders 4.1.3-alpha-1538` is an unconditional PackageReference (not under LocalBuild), from ar-core-dev; nuget.org has only up to 4.1.2 |

### ar-net-core-document (10): all BUILT
| Project | Result |
|---|---|
| src/Document, Document.Drawing.Gc, Document.Drawing.Gdi, Export.Pdf.Section, Export.Tiff.Section | BUILT netstandard2.0, net472 |
| tests/ (5 projects) | BUILT net8.0, net472 |

### ar-net-core-dataproviders (2): all BUILT
| Project | Result |
|---|---|
| src/DataProviders | BUILT netstandard2.0, net472 |
| tests/DataProviders.Tests | BUILT net8.0, net472 |

### ar-net-core-dataproviders-excel (2): BUILT with probe
| Project | Result |
|---|---|
| src/DataProviders.Excel | BUILT netstandard2.0, net472 (probe: public NuGet config). Plain: FAILED-RESTORE NU1301 because the repo NuGet.config maps `DS.Documents.Excel` to ar-core-dev. `DS.Documents.*` 9.2.1 are public on nuget.org |
| tests/DataProviders.Excel.Tests | BUILT net8.0, net472 (same probe) |

### ar-net-core-reportitems-chart (3): all FAILED-RESTORE (DVC)
| Project | Result |
|---|---|
| src/ReportItems.Chart | FAILED-RESTORE DVC (NU1100/NU1101). A later build still runs csc and fails with 422 CS0246/CS0234 per TFM (`IPlotModel`, `IRectangle`, ... from the DV chart assembly) |
| tests/ReportItems.Chart.Acceptance, ReportItems.Chart.UnitTests | FAILED-RESTORE DVC (transitive) |

### ar-net-chart (3): all BUILT
| Project | Result |
|---|---|
| src/Chart | BUILT netstandard2.0, net472 |
| src/Chart.Win (WinForms) | BUILT net8.0-windows plain; net472 only with probe (resx). Plain net472: 92x MSB3822 + 92x MSB3823 |
| src/Chart.Tests (WinForms) | BUILT net8.0-windows plain; net472 with probe (depends on Chart.Win net472) |

### ar-net-ai-assistance (10): 8 BUILT, 2 FAILED-RESTORE
| Project | Result |
|---|---|
| src/Design.AI.Core, Design.AI, Providers/{AzureAI.DocumentIntelligence, AzureOpenAI, Google, OpenAI} | BUILT net8.0, net472 |
| Providers/Design.AI.Ollama | BUILT net8.0, net472 with probe (CS9057 as warning). Plain: CS9057 error, see below |
| tests/Design.AI.Tests | BUILT net8.0, net472 with probe (references Ollama) |
| src/Design.AI.Reporting, tests/Design.AI.Reporting.Tests | FAILED-RESTORE DVC (through ReportItems.Chart) |

### ar-net-win (41): 1 BUILT, 33 FAILED-RESTORE, 7 SKIPPED (.shproj)
| Project | TFMs | Result |
|---|---|---|
| src/ReportsEngineCore/ActiveReports.Interop | net472 | **BUILT net472** |
| src/ReportsEngineCore/ActiveReports | net472;net8.0-windows;net8.0 | FAILED-RESTORE DVC |
| src/ReportsEngineCore/ActiveReports.Win | net472;net8.0-windows | FAILED-RESTORE DVC |
| src/Exports/Export.Html, Export.Image, Export.Image.Win, Export.Pdf, Export.Pdf.Win, Export.Rdf, Export.Xml | mixed | FAILED-RESTORE DVC |
| src/Exports/Export.Excel, Export.Word | net8.0;net472 | FAILED-RESTORE DVC + SB |
| src/WinForms/Viewer.Common, Viewer.Win | net472;net8.0-windows | FAILED-RESTORE DVC |
| src/WinForms/Design.Win, Advanced.Win | net472;net8.0-windows | FAILED-RESTORE DVC + ED + QD |
| src/Wpf/Viewer.Wpf | net472;net8.0-windows | FAILED-RESTORE DVC |
| dev-apps/Viewer, WpfViewer | net472 | FAILED-RESTORE DVC + SB |
| dev-apps/Designer, DesignerClassic | net472 | FAILED-RESTORE DVC + ED + QD + SB |
| dev-apps/ThemeEditor | net472;net8.0-windows | FAILED-RESTORE DVC + ED + QD |
| tests/Exports/Export.{Html,Image,Pdf,Rdf,Xml}.Tests, tests/ReportsEngineCore/Tests, tests/Wpf/Viewer.Wpf.Tests | net472;net8.0-windows | FAILED-RESTORE DVC |
| tests/Exports/Export.Excel.Tests, Export.Word.Tests, tests/WinForms/Viewer.Tests | net472;net8.0-windows | FAILED-RESTORE DVC + SB |
| tests/WinForms/Design.Tests, ThemeEditor.Tests | net472;net8.0-windows | FAILED-RESTORE DVC + ED + QD (Design.Tests also asks for `MESCIUS.ActiveReports.Core.Data.DataEngine/ExpressionInfo 5.5.1-alpha-1971` as packages) |
| src/Licensing/*.shproj (7) | n/a | SKIPPED: shared projects, imported as `.projitems` by the csproj files (LicenseType=None, so Licensing.None) |

Counts: BUILT 55 (51 plain + 4 only with a probe; Chart.Win and Chart.Tests count as plain because net8.0-windows builds without a probe),
FAILED-RESTORE 39 (38 have DVC as a root cause, including every ar-net-win failure; 1, core-page dev-apps/Export, is blocked only by the Core.DataProviders alpha package), FAILED-BUILD 0 once restore succeeds, SKIPPED 7.

## Error categories

1. **Private package, no public substitute (DVC)**: root cause for 38 projects.
   ```
   ReportItems.Chart.csproj : error NU1100: Unable to resolve 'MESCIUS.DataVisualization.Chart (>= 0.6.1)' for '.NETStandard,Version=v2.0'. PackageSourceMapping is enabled, the following source(s) were not considered: ar-core-dev, nuget.org.
   ar-net-win/src/ReportsEngineCore/ActiveReports/ActiveReports.csproj : error NU1101: Unable to find package MESCIUS.DataVisualization.Chart. No packages exist with this id in source(s): nuget.org.
   ReportItems/Chart/Rendering/ChartModel.cs(95,28): error CS0246: The type or namespace name 'IPlotModel' could not be found
   ```
   nuget.org has only `GrapeCity.DataVisualization.Chart` up to 0.4.136 (old, different id). The public `MESCIUS.ActiveReports.Core.ReportItems.Chart 1.2.4` embeds DV chart and has no dependency on it.
2. **Private package, public version older than the pin (SB, ED, QD, Core.DataProviders, Core.Data.*)**: the feed is unreachable, which shows as NU1301; with a nuget.org-only config it shows as NU1102.
   ```
   Export.Excel.csproj : error NU1301: Unable to load the service index for source https://pkgs.dev.azure.com/dt4dev/AR/_packaging/ar-dev/nuget/v3/index.json.
   error NU1102: Unable to find package MESCIUS.ActiveReports.SpreadBuilder with version (>= 3.0.4-alpha-283)  - Found 6 version(s) in nuget.org [ Nearest version: 3.0.3 ]
   ```
3. **Source mapping sends a public package to a private feed** (DataProviders.Excel: `DS.Documents.Excel` mapped to ar-core-dev in the repo NuGet.config). Fixed by `-p:RestoreConfigFile=<nuget.org-only config>`. NuGet.config change that would be needed: map `DS.Documents.*` to nuget.org.
4. **resx with non-string resources, net472, .NET MSBuild** (Chart.Win net472): the only macOS/.NET-MSBuild-specific failure.
   ```
   Microsoft.Common.CurrentVersion.targets(3413,5): error MSB3823: Non-string resources require the property GenerateResourceUsePreserializedResources to be set to true. [Chart.Win.csproj::TargetFramework=net472]
   Microsoft.Common.CurrentVersion.targets(3413,5): error MSB3822: Non-string resources require the System.Resources.Extensions assembly at runtime, but it was not found in this project's references. [Chart.Win.csproj::TargetFramework=net472]
   ```
   Change needed: in net472 WinForms/WPF projects, `<GenerateResourceUsePreserializedResources>true</...>` plus `PackageReference System.Resources.Extensions`.
   This adds a runtime dependency to the shipped net472 assembly and changes how resources are serialized, so the output differs from a
   Windows/VS build. The same will apply to every ar-net-win WinForms/WPF net472 project with images or icons in resx once DVC is available.
   (ar-net-win lists MSB3822/3823 in `MSBuildWarningsAsMessages`, but they are errors here, not warnings.)
5. **Analyzer built for a newer compiler** (Design.AI.Ollama, through Microsoft.SemanticKernel.Connectors.Ollama -> OllamaSharp 5.4.12), together with TreatWarningsAsErrors:
   ```
   CSC : error CS9057: The analyzer assembly '.../ollamasharp/5.4.12/analyzers/dotnet/cs/OllamaSharp.SourceGenerators.dll' references version '4.14.0.0' of the compiler, which is newer than the currently running version '4.11.0.0'.
   ```
   Not specific to macOS: SDK 8.0.4xx ships Roslyn 4.11. ar-net-win already lists CS9057 in WarningsNotAsErrors; ar-net-ai-assistance does not.
   The generator does not run, and the project still compiles. Options: add CS9057 to WarningsNotAsErrors there, or use an SDK with Roslyn >= 4.14 (9.0.3xx / 10.x), which needs a global.json change.
6. **Missing reference assemblies (MSB3644)**: a follow-on error only. It shows up for net472 TFMs of projects whose restore failed, so the
   Microsoft.NETFramework.ReferenceAssemblies package was never added to their assets file. It goes away once restore succeeds.

Not seen: no WPF/XAML markup-compile failures (no WPF project got past restore; the WinForms ones that did compile fine), no strong-name failures
(all assemblies signed with `activereportskey.snk`, csc signs on macOS), no VSIX/VSSDK (ar-net-vs is not in the win solution), no licensing tool (LicenseType=None), no Babel obfuscation in a plain build.

## What Windows is needed for (as far as this baseline shows)

* **Nothing so far, for the code that can be restored.** net472, netstandard2.0, net8.0 and net8.0-windows (WinForms: Chart.Win, Chart.Tests)
  all compile and sign on macOS once `EnableWindowsTargeting=true` is set.
* **net472 resx with binary resources** is the one real difference between .NET MSBuild and Windows MSBuild. On Windows, VS / .NET Framework MSBuild
  serializes them in-proc with BinaryFormatter and needs no extra reference. On macOS it needs the preserialized-resources change (category 4).
  The outputs are not byte-identical.
* Still unknown: WPF (Viewer.Wpf, WpfViewer) and the large WinForms designers (Design.Win, Advanced.Win). They are blocked by DVC before compile,
  so XAML compile on macOS, COM/ActiveX interop references, and licenses.licx (LC.exe) in the dev apps are not tested.
  Running the tests (WinForms/GDI+ at runtime, net472 under a .NET Framework runtime) does need Windows; this baseline only compiled them.
* For the hermetic Xake reproduction, the hard prerequisite is the **ar-dev feed** (DVC 0.6.1, SpreadBuilder, Editor.Net, QueryDesigner, Core.DataProviders alpha),
  not Windows.

## Probes (command-line only, nothing in the repos changed)

* `scripts/nuget-public.config`: nuget.org only, no source mapping. Used with `-p:RestoreConfigFile=...`. This unblocks DataProviders.Excel and turns NU1301 into exact NU1101/NU1102 messages.
* `scripts/macos-probe.targets`: used with `-p:CustomAfterMicrosoftCommonTargets=...`. (1) For net472 projects with UseWindowsForms/UseWPF, it sets
  GenerateResourceUsePreserializedResources=true and adds System.Resources.Extensions 8.0.0. (2) It appends CS9057 to WarningsNotAsErrors.
  Both stand in for the source changes described in categories 4 and 5.
* `scripts/private-refs.sh`: lists the private-feed PackageReferences per project/TFM under LocalBuild=true (output in `logs/private-refs.txt`).

## Commands that worked

```bash
cd /Users/olegz/Projects-work/ar/ar-net-meta
F=/Users/olegz/Projects-work/ar/xake-ar/full
# 1. plain baseline (51 projects build)
dotnet restore ar.meta.win.sln -p:LocalBuild=true -p:NuGetAudit=false -p:EnableWindowsTargeting=true
dotnet build   ar.meta.win.sln -p:LocalBuild=true -p:NuGetAudit=false -p:EnableWindowsTargeting=true -c Release --no-restore -m -bl:$F/logs/build1.binlog

# 2. with probes (55 projects build); in zsh do not keep these in one unquoted $VAR (zsh does not word-split it)
dotnet restore ar.meta.win.sln -p:LocalBuild=true -p:NuGetAudit=false -p:EnableWindowsTargeting=true \
  -p:RestoreConfigFile=$F/scripts/nuget-public.config -p:CustomAfterMicrosoftCommonTargets=$F/scripts/macos-probe.targets
dotnet build ar.meta.win.sln -p:LocalBuild=true -p:NuGetAudit=false -p:EnableWindowsTargeting=true \
  -p:RestoreConfigFile=$F/scripts/nuget-public.config -p:CustomAfterMicrosoftCommonTargets=$F/scripts/macos-probe.targets \
  -c Release --no-restore --no-incremental -m -bl:$F/logs/build5-final.binlog      # 28.8 s wall, exit 1 because of the 39 restore failures
```

Logs: `logs/restore1.log` (plain restore), `logs/build1.{log,binlog}` (plain build), `logs/build2-excel.*`, `logs/build3-chart.*`, `logs/build4-ai.*`
(single-project probes), `logs/restore4.log` + `logs/build5-final.{log,binlog}` (final combined run), `logs/build5-outputs.txt` (all 109 produced assemblies),
`logs/win-sln-list.txt`, `logs/all-sln-list.txt`.
