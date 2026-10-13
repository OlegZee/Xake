# ActiveReports desktop: reference build times, brand x configuration matrix (macOS)

Date: 2026-10-13, 21:55-22:18 CEST. Solution `ar.meta.win.sln` (94 csproj) in `/Users/olegz/Projects-work/ar/ar-net-meta`, branch `develop/20`
(HEAD a9895a5), submodules at pins, no repo edits. These are the baseline numbers the Xake reproduction is compared against.
Logs: `logs/matrix-20261013/` (text only). Driver: `scripts/matrix-run.sh`, analysis: `scripts/matrix-analyze.py`,
brand-switch recompile check: `scripts/matrix-recompile.sh`.

## Machine and toolchain

* Apple M5 Pro, 18 cores, 48 GB RAM, macOS 27.0.1 (26A434).
* .NET SDK 8.0.425 (selected by the meta repo `global.json`: 8.0.100, rollForward latestFeature; SDK 10.0.401 is also installed but not used).
  MSBuild 17.11.48.46605, csc = Roslyn `Microsoft.CodeAnalysis.CSharp` 4.11.0-3.25569.22 (`sdk/8.0.425/Roslyn/bincore/csc.dll`).
* Load before start: 2.0-3.3 (1-min). Background: VMware Fusion VM (~4-8 % CPU), WindowServer, iTerm. During and after every build
  **Spotlight (`mds_stores`) runs at ~100 % CPU** indexing the ~15,000 new files in bin/obj; it is the main reason the 1-min load stays
  at 20-45 for a minute or two after each build. Spotlight was not disabled (no system changes allowed); see "Variance".
* Load after the last run: 25.6 / 21.0 / 18.9 (mostly the builds themselves plus mds_stores).

## Results (two runs per cell)

Run 1: the four cells back to back (only the build-server shutdown and bin/obj delete between steps).
Run 2 (`rep2-*`): same, but each restore and each clean build waits until the 1-min load is below 6 (waited 0-165 s).
All times are wall seconds of the single `dotnet` command.

| Cell | Restore s (r1 / r2) | Clean build s (r1 / r2) | No-op s (r1 / r2) | Primary outputs by TFM | Warnings / errors | Exit |
|---|---|---|---|---|---|---|
| MESCIUS / Debug   | 3.58 / 3.37 | **33.47 / 32.82** | 4.94 / 5.76 | net472 93, net8.0 42, net8.0-windows 26, netstandard2.0 24 = **185** | 52 / 0 | 0 |
| MESCIUS / Release | 3.28 / 3.38 | **43.95 / 41.29** | 5.37 / 5.74 | 93 / 42 / 26 / 24 = **185** | 54 / 0 | 0 |
| GCCN / Debug      | 3.31 / 3.20 | **31.62 / 31.99** | 6.58 / 5.27 | 92 / 41 / 26 / 24 = **183** | 52 / 4 | 1 |
| GCCN / Release    | 3.83 / 3.18 | **45.78 / 51.66** | 6.13 / 5.95 | 92 / 41 / 26 / 24 = **183** | 54 / 4 | 1 |

Sum of the 4 clean builds: 154.8 s (r1), 157.8 s (r2). Restores (cold obj, warm NuGet cache) are flat at 3.2-3.8 s.
The no-op rebuild is 5-6.5 s and skips CoreCompile in all 183 compiled project/TFM pairs (0 csc runs, checked at `-v:n`).

"Primary outputs" are the `Project -> path.dll|exe` lines of the build log (one per project per TFM). Of the 185 (MESCIUS),
**102 carry the brand prefix** (`MESCIUS.ActiveReports.*`; in the GCCN cells `GCCN.ActiveReports.*`, also 102). The 83 (81) unprefixed ones are
the test assemblies (`GrapeCity.ActiveReports.*.Tests`, `DataEngine.Tests`, `ReportItems.Chart.*`, ...), the dev-app
exes (`ActiveReports.{Designer,DesignerClassic,Viewer,WpfViewer,ThemeEditor}.exe` + `ActiveReports.ThemeEditor.dll`) and `Export.dll`
(core-page dev-app): their names do not depend on Brand.

All dll+exe files under bin/ (excluding obj/ and ref/, i.e. including copied dependencies): MESCIUS 14,940
(net472 6,610, net8.0 4,659, net8.0-windows 3,542, netstandard2.0 129); GCCN 14,579 (6,470 / 4,438 / 3,542 / 129). Identical in both runs.
In the GCCN tree exactly one `MESCIUS.*` file remains: `ar-net-core-page/dev-apps/Export/bin/<cfg>/net8.0/MESCIUS.ActiveReports.Core.DataProviders.dll`,
which comes from a NuGet `PackageReference` (`MESCIUS.ActiveReports.Core.DataProviders`) in that project, not from a build.

### Warnings and errors

* 52 warnings (Debug) = MSB3568 x32 (duplicate resx resource names, e.g. `>>reportExplorerTabPage.*`), CS9057 x9 (Design.AI.Ollama, analyzer
  built with a newer compiler; downgraded by the probe targets), MSB3245 x6 and MSB3243 x5 (reference resolution).
  Release adds IDT003 x2 (doc-comment cref) = 54. The no-op build repeats only the 11 reference-resolution warnings.
* **GCCN: 4 errors = 2 x CS0433 in 2 TFMs (net472, net8.0) of one project**,
  `ar-net-core-page/tests/exports/Svg.Tests` (`GrapeCity.ActiveReports.Core.Export.Svg.Page.Tests`):
  `The type 'GcGraphics' / 'GcSvgGraphics' exists in both 'DS.Documents.Imaging' and 'GcDocs.Imaging'` (PathBuilderTests.cs:19).
  With `Brand=GCCN` the restore pulls the GCCN flavour `GcDocs.Imaging` next to the test's direct `DS.Documents.Imaging`. That is why GCCN has
  183 primaries (Svg.Tests missing in 2 TFMs) and exit 1. A test project only; every product assembly builds. Not investigated further
  (repo issue, likely not macOS-specific).

## Warm-switch sequence (no cleaning)

Starting from the warm GCCN/Release tree of run 1, servers warm, restore + build for each cell in turn:

| Step | Restore s | Build s | Exit |
|---|---|---|---|
| MESCIUS / Debug   | 3.99 | 22.85 | 0 |
| MESCIUS / Release | 3.59 | 35.70 | 0 |
| GCCN / Debug      | 4.46 | 21.55 | 1 (same CS0433) |
| GCCN / Release    | 3.81 | 34.62 | 1 (same CS0433) |
| **Total (wall, 8 commands)** | | | **130.7 s** |

* **Every switch recompiles everything.** Check at `-v:n` (`recompile-*.log`) on the warm tree: GCCN/Release -> MESCIUS/Release
  ran csc 196 times and skipped CoreCompile 0 times (35 s); the immediate no-op ran csc 0 times and skipped CoreCompile 183 times (6 s);
  back to GCCN/Release again 196 csc runs, 0 skipped (35 s). The brand changes the assembly names, hence all obj paths and every reference.
* The warm-switch builds are ~25-30 % faster than the cold clean builds (22-36 s vs 32-46 s) only because the compiler server, MSBuild nodes
  and file cache are warm and bin/obj need not be created; the compile work is the same.
* A warm tree after switching keeps all variants side by side in bin/: 30,142 dll+exe files after the sequence (both brands, both configs).
* **Restore is brand-dependent.** Building MESCIUS with `--no-restore` on a tree last restored for GCCN fails with the same CS0433 in Svg.Tests
  (`recompile-1`, exit 1), because `project.assets.json` carries the GCCN package graph. Each brand switch therefore needs a restore
  (it also explains why the first GCCN restore re-restored projects: prewarm 11.0 s vs 2.6 s for MESCIUS).

## Variance

* Clean builds repeat within 1-3 s, except GCCN/Release (45.8 vs 51.7 s). Run 2, with the cool-down, is not systematically faster, so the
  residual load from Spotlight is not a dominant factor; the GCCN/Release spread looks like ordinary noise of a parallel build. Use the
  minimum or the mean of the two runs as the baseline: MESCIUS/Debug ~33 s, MESCIUS/Release ~42-44 s, GCCN/Debug ~32 s, GCCN/Release ~46-52 s.
* Release is ~10-15 s slower than Debug (optimizing compiler, doc generation).

## Exact commands

```bash
cd /Users/olegz/Projects-work/ar/ar-net-meta
set -a; source /Users/olegz/Projects-work/.env; set +a
F=/Users/olegz/Projects-work/ar/xake-ar/full
P="-p:LocalBuild=true -p:NuGetAudit=false -p:EnableWindowsTargeting=true \
   -p:RestoreConfigFile=$F/scripts/nuget-feed.config -p:CustomAfterMicrosoftCommonTargets=$F/scripts/macos-probe.targets"
B=""                 # MESCIUS (Brand unset); for GCCN: B="-p:Brand=GCCN"
C=Debug              # or Release

# once, before the matrix: warm the NuGet cache for both brands (untimed)
dotnet restore ar.meta.win.sln $P -v:m ; dotnet restore ar.meta.win.sln $P -p:Brand=GCCN -v:m

# per cell
dotnet build-server shutdown
find /Users/olegz/Projects-work/ar/ar-net-meta -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +   # 184-263 dirs
dotnet restore ar.meta.win.sln $P $B -p:Configuration=$C -v:m                 # a. restore
dotnet build-server shutdown
dotnet build ar.meta.win.sln $P $B -c $C --no-restore -m -v:m                 # b. clean build
dotnet build ar.meta.win.sln $P $B -c $C --no-restore -m -v:m                 # c. no-op
```

Timing: `perl -MTime::HiRes` timestamps around each `dotnet` call (wall seconds, 10 ms resolution), recorded in
`logs/matrix-20261013/timings.tsv` together with `uptime` load after each step.

## Files

* `logs/matrix-20261013/timings.tsv`: all timings and loads.
* `<Cell>-{a-restore,b-build,c-noop}.log`, `rep2-<Cell>-*.log`, `switch-<Cell>-*.log`, `recompile-*.log`, `prewarm-restore-*.log`.
* `<Cell>-files.txt`, `rep2-<Cell>-files.txt`, `switch-final-files.txt`: dll/exe lists under bin/.

State left behind: the meta repo tree holds GCCN/Release outputs built last (restored for GCCN). Delete bin/obj before the next clean measurement.
