# ActiveReports experiment (fsc-rework item 9): two brands from descriptions and locks

Date 2026-10-13. Experiment folder `~/Projects-work/ar/xake-ar/` (not a git repo): `build.fsx`,
`mescius/` and `gccn/` (descriptions, locks, fingerprints), `out/`, `tools/` (CPU meter, manifest
lister, measurement loop), `probe/` (engine-point probe), `logs/`. The AR sources are two detached
worktrees at `origin/develop` inside that folder (`ar-net-core-dataengine` 25b7e48,
`ar-net-core-page` 808ef9f249); nothing in them was edited (only their `obj/`/`bin/` from the
`dotnet build` baseline). Packages: `Xake 3.7.0.26`, `Xake.Hermetic.Dotnet 0.3.0.28`. Machine:
Apple M5 Pro, 18 cores (6P + 12E), macOS. Script copy: [../ar-build.fsx](../ar-build.fsx).

**SDK.** With no `global.json` the folder picks 10.0.401; `dotnet build` of the AR projects then
fails (`error CA2022: Avoid inexact read with 'System.IO.Stream.Read(byte[], int, int)'` in
Scripting, new analyzer + `TreatWarningsAsErrors`). The folder got dataengine's `global.json`
(8.0.100, latestFeature) and everything below ran on **SDK 8.0.425** (csc 4.11.0-3.25569.22),
`dotnet fsi` included.

## 1. Inventory

15 libraries, all `netstandard2.0;net472` (Scripting lists `net472` first), C#, LangVersion 12,
strong-named (`.keys/*.snk` committed in both repos), `TreatWarningsAsErrors`, doc XML on.
Assembly names `<Brand>.ActiveReports.Core.<X>` (dataengine `...Core.Data.<X>`), picked by
`$(Brand)` in `src/Directory.Build.props` (`Brand` global property; empty = MESCIUS).

| repo | project (C# files) | project refs |
|---|---|---|
| dataengine | VBFunctionLib (12), ExpressionInfo (117), DataEngine (78) | EI -> VB; DE -> EI |
| page | Rdl (426) | ExpressionInfo (dataengine) |
| page | Rendering (1308) | DataEngine (dataengine), Rdl |
| page | Scripting (17), Drawing.Gc (15) | Rendering |
| page | Drawing.Gdi (10) | Drawing.Gc, Rendering |
| page | Export.Excel (670), .Html (63), .Word (65) | Rdl, Rendering |
| page | Export.Image (14), .Pdf (36), .Svg (24) | Drawing.Gc, Rdl, Rendering |
| page | Export.Text (48) | Rendering |

Page -> dataengine: `ProjectReference Include="..\..\..\ar-net-core-dataengine\src\..."` under
`LocalBuild=true`, else a package `$(DataEnginePrefix).Data.*` from the private feed. The script
imports with `LocalBuild=true`, so the **private feed is never needed**: `ar-core-dev` (Azure
DevOps, `%AA_AR_TOKEN%`, unset here) is mapped only for `Babel.*`, `semverchecker*`,
`*.ActiveReports.Core.*`, `GrapeCity.DataVisualization.*`, none of which the 15 projects need
with `LocalBuild`. `dotnet restore` of Export.Pdf.Page (pulls 7 projects): 0.8 s, warm cache.
Packages (nuget.org): DS.Documents.* / GcDocs.* 9.2.1 by brand, OpenXml, BouncyCastle, etc.

Exotics: `.resx` string tables in Rdl, Rendering (2), Excel; embedded resources with `LogicalName`
and a `Res\*` glob; IVT items with `Key`; `CLSCompliant` via `AssemblyAttribute` (4); NoWarn
`1591;0618;0612`; config-dependent `DefineConstants` in Rendering; ~830 `<include file=...>` doc
fragments; `Directory.Build.targets` nuspec binding (`*.nuspec` with `$prefix$` tokens); InheritDoc
(Release). Babel obfuscation is not wired into any build. **Nothing excluded**: all 15 are in.

## 2. Import per brand

`import::mescius` / `import::gccn`: `Import.descriptions` over the 15 csproj with
`Brand=<BRAND>; LocalBuild=true`, 20-21 s per brand (msbuild evaluation, 5 runs per project).
15 descriptions per brand at `<brand>/projects/<name>/xake.project.json`, `project` =
`../../../ar-net-core-*/src/.../<name>.csproj`. Fingerprints: one file per brand,
`<brand>/xake.fingerprints` (30 + 15 lines), so `import::gccn`/`check::gccn` never touch the
other brand's lines and both brands can be imported in parallel. Inputs recorded per description:
the csproj, both `Directory.Build.props`, `Directory.Build.targets`, `src/<BRAND>.props` (and
dataengine's `Directory.Packages.props`), i.e. the brand switch is a tracked input.

`Dropped` (432 lines per brand): 15 x `VersionPrefix`, `Product`, `Company`, `Trademark`,
`IconUrl`, `AssemblyNamePrefix`, `Configurations`, `OutputPath`, `Optimize`, `EnableNETAnalyzers`,
`DelaySign`, `NuspecFile`, target `SetNuspecProperties`, implicit `NETStandard.Library`; 12 x (page)
`NoWarn`, `WarningsAsErrors`, `Title`, `GcPrefix`, target `AddInternalsVisibleTo`; 4 x `assembly
attribute System.CLSCompliantAttribute`. Harmless except NoWarn and CLSCompliant (G3, G4).

Brand difference (`diff -r mescius/projects gccn/projects`): `assemblyName`, `package.id`,
`authors`, `copyright` (15 each), Rendering's define `MESCIUS`/`GCCN`, and the `$(GcPrefix)`
packages `DS.Documents.Imaging[.Windows]`/`DS.Documents.Pdf` vs `GcDocs.*` (5 projects). Nothing
else: sources, refs and options are brand-independent, as expected.

Problems met (each worked around in the script's `fixup`, see Gaps): `.resx` listed as a plain
resource (G1, `Described` refuses it at lock time: "'Rdl': resource 'AssemblyResources/Resources.resx':
a .resx in a description is not compiled by `project` yet"); Rdl's `<PackageReference ...
version="8.0.5" />` (lower-case attribute) imported without a version (G2); NoWarn dropped (G3);
CLSCompliant dropped (G4); masks fine: `**/*.cs`, `!bin/**/*`, `!obj/**/*`, `!**/.*/**/*`.

## 3. Locks per brand (`update-locks::<brand>`, Debug + Release)

`update-locks::<b>` needs `lock::<b>::<name>::<cfg>` for every description x {Debug, Release}
(30 locks per brand, all at once; the lock rule needs its refs' lock rules, so the engine orders
them). Each lock: 2 entries (netstandard2.0, net472), 15-28 KB.

Restore timings: first `update-locks::mescius` ~16 s (per lock: Excel 7.8 s, Rendering 4.2 s,
Rdl 2.4 s, dataengine 0.1-0.2 s); `update-locks::gccn` 14.1 s, CPU 67.6 s (GcDocs.* new); 36
packages in `.packages` for both brands.

Ref choice across repos: page's refs to dataengine are ordinary described projects, so they land
as `$(ProjectRoot)/out/<brand>/<cfg>/<fwk>/DataEngine/<Brand>.ActiveReports.Core.Data.DataEngine.dll`,
unhashed, same configuration and framework (each project has both, so no fallback). Transitive
refs (G5) had to be made explicit. Tokenisation: every path in the 60 locks is `$(ProjectRoot)`,
`$(NuGetPackageRoot)` or `$(DotnetRoot)`, because the two worktrees sit **inside** the experiment
folder (`xake-ar/ar-net-core-*`), so `../../../ar-net-core-page/...` in a description resolves under
the project root. The outside-the-root case (the draft's layout, repos beside the folder) is
probed in step 5. Compiler: `$(DotnetRoot)/sdk/8.0.425/Roslyn/bincore/csc.dll`, unhashed. Re-running `update-locks::mescius update-locks::gccn` (60 locks, 32.7 s) wrote nothing (same
content and mtimes); re-import gave the same content but touched 19 files (G13).

## 4. Build from the committed locks

All four `build::<brand>::<cfg>` succeed: **15 projects x 2 frameworks = 30 dlls (+30 xml) per
brand x configuration, 120 in total**. Manifest resource names equal msbuild's (Rdl, Rendering,
Excel, Html checked). Measured with `tools/cputime.pl` (polls every process of the command's
process group: `/usr/bin/time -l` alone saw 6 s user of a 151 s CPU msbuild run). Fair setting: msbuild
`-nodeReuse:false -p:UseSharedCompilation=false`, Xake `-d CSC_SERVER=off` (no VBCSCompiler on
either side); msbuild clean = `dotnet build baseline.sln -c <cfg> -p:Brand=<B> -p:LocalBuild=true
-t:Rebuild` (baseline.sln = the 15 csproj), Xake clean = `rm -rf out/<b>/<cfg>` then
`build::<b>::<cfg>`; no-op = the same command again.

| brand / cfg | msbuild clean: wall, CPU, util | Xake clean: wall, CPU, util | no-op msbuild / Xake |
|---|---|---|---|
| mescius Debug | 13.2 s, 124 s, 9.4 | 34.3 s, 103 s, 3.0 | 2.2 s / 6.6 s |
| mescius Release | 27.9 s, 158 s, 5.7 | 33.4 s, 100 s, 3.0 | 2.1 s / 7.1 s |
| gccn Debug | 13.8 s, 124 s, 9.0 | 34.1 s, 104 s, 3.1 | 2.3 s / 6.6 s |
| gccn Release | 26.1 s, 159 s, 6.1 | 33.3 s, 101 s, 3.1 | 2.0 s / 6.6 s |
| mescius Release, mask `!**/.*/**/*` removed from the locks (G8) | | **21.0 s**, 86 s, 4.1 | / 4.4 s |
| mescius Release, compiler servers on (defaults) | 21.7 s | 32.2 s | |

(util = CPU / wall, 18 cores.) Xake spends less CPU (no analyzers, no InheritDoc, no RAR) but its
critical path VB -> EI -> Rdl -> Rendering -> Excel/Pdf carries 2-7 s of per-target preparation
before each csc: the log shows `compiling 'Rendering'` 7 s before its resources are even needed.
The cause is G8: expanding Rendering's masks takes 1.4 s with the `!**/.*/**/*` exclude and 11 ms
without, and it runs again in every no-op. msbuild Release is slower than Debug because of
InheritDoc; Xake's Release equals its Debug. One Xake run (mescius Release) failed with
`('MESCIUS.ActiveReports.Core.Export.Image.Page') failed with exit code '134'` (csc aborted, 30
csc processes in parallel); the rerun was clean, not reproduced.

**Fresh-clone run**: `.xake`, `out/`, `obj/` and `.packages` deleted, `build -d NUGET_FETCH=on -d
CSC_SERVER=off` from the committed descriptions and locks: exit 0, **120 dlls in 141 s** (CPU 416 s,
util 2.95), 12 package fetches (`Restore.download`, no msbuild of any AR project: 0 `msbuild`
lines in build.log), `check::mescius check::gccn` pass, the committed files unchanged (sha
compared). A no-op of all four afterwards: **33 s at util 1.04** (G9): 141 s for four
configurations is the sum of four single ones, so nothing overlaps.

## 5. Engine points (gaps.md)

| point | result |
|---|---|
| phony capture `build::(b:*)::(cfg:*)`, `import::(b:*)`, `lock::(b:*)::(name:*)::(cfg:*)` with dotted names (`Export.Pdf.Page`) | **works** |
| file patterns `out/(b:*)/(cfg:*)/(fwk:*)/(name:*)/*.dll`, `obj/resx/(b:*)/(name:*)/**/*.resources` | **work** |
| filesets over `../` | **work**: probe (project root `xake-ar/probe`) `!! "../ar-net-core-dataengine/src/VBFunctionLib/*.csproj"` and `**/*.cs` found 1 / 20 files; description masks across `../../../ar-net-core-*` and an extra source `../../../mescius/projects/<X>/AssemblyAttributes.cs` outside the first include's folder expand at build (`Described.expandCsc`) |
| lock tokenisation outside `$(ProjectRoot)` | **does not tokenise**: in the probe the lock keeps `/Users/.../ar-net-core-dataengine/src/VBFunctionLib/**/*.cs` and `/keyfile:/Users/...snk` absolute, `/pathmap` maps only `$(ProjectRoot)`; it builds locally but is not portable (G10). The main experiment avoids it: the worktrees sit inside the project root |

`HERMETIC=on` (probe and main) refuses before that: "HERMETIC=on: the lock
'mescius/locks/VBFunctionLib/Release.json' names $(DotnetRoot)/sdk/8.0.425/Roslyn/bincore/csc.dll,
outside $(ProjectRoot) and $(NuGetPackageRoot) and not covered by a prerequisite" (G11).

## 6. Basic pack

Cheap with the existing `Pack.nupkg` (34 script lines): `pack::<brand>` needs
`out/<b>/pkg/Export.Text.Page.nupkg`; the rule takes the project's own `.nuspec`, fills its
`$prefix$ $version$ $versionRange$ $authors$ $owners$ $copyright$ $iconUrl$` from the description
(`package.authors/copyright`) and the brand (iconUrl is a script dict: Import drops `IconUrl`),
maps `bin\Release\<fwk>\<file>` to `out/<b>/Release/<fwk>/<name>/<file>`, drops `<files>`, and
zips. Result per brand: `<BRAND>.ActiveReports.Core.Export.Text.Page` 4.6.0, 11 entries (nuspec with
the brand's dependency `<BRAND>.ActiveReports.Core.Rendering [4.6.0,5.0.0)` per TFM, `lib/net472`,
`lib/netstandard2.0` dll+xml, icon, license, readme). **Two clean builds** (`out`, `obj`, `.xake`
deleted; pack builds only its 24-dll closure): both nupkgs **byte-identical** (same folder). Not
done: the other 14 nuspecs (page ones use more tokens: `dataEngineRange`, `GcDocVersionRange`...),
InheritDoc trimming of the xml (msbuild's Release xml is 1.6 MB vs Xake's 2.8 MB for Rendering).

## 7. Judgement

**Script** (`ar-build.fsx`, 179 lines): ~95 lines are the draft's shape (header, vars, layout
lambdas, `import::`, `lock::`, `update-locks::`, `check::`, `build::`, the dll rule, `clean::`),
readable top-down as in `build.fsc.fsx`; a newcomer sees the brand only as `(b:*)` captures and
the `<b>/` prefix of each layout lambda. ~50 lines are workarounds (`fixup`, `transitive`, the
`.resx` rule) that rewrite descriptions after `Import` -- data processing the brief wants out of
scripts; they vanish with G1-G6. 34 lines are the pack rule (nuspec token filling, PR 8's job).
Boilerplate left: `let! b = getRuleMatch "b"` x 3-4 per rule (11 times), the `Filelist`
destructuring, the project folder from a description (written twice).

| gap | exact symptom | proposed fix | size |
|---|---|---|---|
| G1 `.resx` | "'Rdl': resource 'AssemblyResources/Resources.resx': a .resx in a description is not compiled by `project` yet" | `Described`/`compileAs` compile via `Xake.Dotnet.Resx` into `obj/`, manifest name = RootNamespace + path (verified equal to msbuild's) | ~60 |
| G2 metadata case | Rdl `version="8.0.5"` imported as `{ "id": "System.Text.Json" }` (restore would take the lowest) | case-insensitive metadata in `Import` | ~5 |
| G3 NoWarn | dropped; with `/warnaserror+` the page's `1591;0618;0612` and the SDK's `1701;1702` matter | `options.noWarn` in the schema + SDK defaults in `Described` | ~30 |
| G4 attributes | "error CS3021: '...' does not need a CLSCompliant attribute because the assembly does not have a CLSCompliant attribute" | `assemblyAttributes` field, generated like the IVT file | ~50 |
| G5 transitive refs | "error CS0234: The type or namespace name 'VisualBasicReplacement' does not exist in the namespace 'GrapeCity.Enterprise.Data'" (DataEngine -> VBFunctionLib via ExpressionInfo) | `Locks.generate` adds the refs' unhashed refs (closure) | ~20 |
| G6 framework assemblies of packages | "error CS0012: The type 'Package' is defined in an assembly that is not referenced. You must add a reference to assembly 'WindowsBase...'" (net472, OpenXml) | `Nuget.readAssets` reads `frameworkAssemblies` | ~30 |
| G7 compile directory | "error CS1589: Unable to include XML fragment ... of file '../../../docs/Common.xml' -- File not found." (Svg) | `Compilation.Directory` = project folder (existing slot) | ~10 |
| G8 fileset speed | `!**/.*/**/*` costs ~1 ms/file (1.4 s vs 11 ms for 1316 files); clean 33 -> 21 s without it | core `Fileset` `**` matching; or `Import` drops the exclude when no dot folder | ~40 / ~5 |
| G9 serial checks | no-op of 120 targets 33 s at util 1.04; 4 configs = sum of 4 | profile the engine's dependency check (likely G8 run serially) | ? |
| G10 tokenisation | probe lock keeps absolute source/keyfile paths outside `$(ProjectRoot)` | extra roots for `Locks.generate`/`compileAs` + a pathmap per root | ~40 |
| G11 HERMETIC | refused: compiler "not covered by a prerequisite" | `Locks.generate` records the dotnet-sdk prerequisite | ~20 |
| G12 `.xml` side output | "Neither rule nor file is found for '.../Export.Text.Page.xml'" when a rule needs it first | doc note, or the dll rule also matching `.xml` | ~10 |
| G13 rewrite churn | re-import touches 19 unchanged files (Import writes, `fixup` writes back) | goes with G1-G6 | 0 |

## Verdict against the brief's acceptance (item 9)

- **Both brands by prefix**: yes, every path and command (`import::gccn`, `build::mescius::Debug`,
  `out/gccn/...`); per-brand fingerprints file.
- **Debug + Release, the 15 projects from locks**: yes, 120 dlls, a fresh-clone run from the
  committed files only, no msbuild; but only through six script workarounds (G1-G6) and one
  suppressed warning (G7).
- **Basic packages**: one library per brand via `Pack.nupkg`, byte-identical across two clean builds.
- **Script size**: 179 lines vs the draft's 78; ~95 without workarounds and pack.
- **Core utilisation vs `dotnet build`**: Xake 3.0 vs 5.7-9.4; wall 33-34 s vs 13-28 s; with G8
  fixed 21 s and util 4.1, faster than msbuild's Release (28 s) with less CPU (86 s vs 158 s).
- **Engine points**: phony capture and filesets over `../` verified; tokenisation outside the
  project root verified **not** to work (G10).
