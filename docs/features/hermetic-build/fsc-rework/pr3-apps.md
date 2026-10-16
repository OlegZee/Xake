# PR 3: applications on the recorded lock (2026-10-16)

Branch `pr/apps` (worktree `.claude/worktrees/pr/apps`), stacked on `pr/resolved-inputs`
(e45435f). Binding decisions: interview.md, the two 2026-10-15 sections (no emulation; decision 3
"the apphost is part of the SDK"). Night source, read only: `pr/apphost-resx` `Desktop.fs`
(`HostWriter` call, `removeMachOSignature`, `appHostPackVersion`/`sdkRuntimeIdentifier`, the
template paths, `assemblyVersions`/`depsResolved`). Taken: those functions, into
`src/hermetic/Apps.fs` (≈330 lines with docs). Not taken: the night's runtimeconfig writer,
both deps.json writers (`depsText`, `depsTextOf`: built from the assets file, conflict
resolution emulated in `runtimeConflictLosers`), `appConfigText`, the description fields
`appHost`/`appConfig`/`runtimeConfig`, WPF, satellites.

## 1. What is recorded (the `App` section)

An entry whose project has runtime output (`HasRuntimeOutput`: exe, winexe, .NET test project)
gets `App` (in `Lock.Recorded.App`, written as its own `"App"` section of the entry). Every
value comes from msbuild's design-time build:

| Field | From msbuild | Build (`Apps.write`, after the compile in `Locks.compileAs`) |
|---|---|---|
| `OutputType` | `OutputType` | `WinExe` = GUI subsystem of a Windows apphost |
| `Host` | `UseAppHost`; `RuntimeIdentifier`; `ResolvedAppHostPack.Path` | see 2 |
| `Files` | the `runtimeconfig.json` and `deps.json` written by `GenerateBuildRuntimeConfigurationFiles`/`GenerateBuildDependencyFile` into the record's intermediate folder (`ProjectRuntimeConfigFilePath`/`ProjectDepsFilePath` pointed there); the config file `AppConfigWithTargetPath` names after `GenerateSupportedRuntime`, before `GenerateBindingRedirects` | written under msbuild's names (`ProjectRuntimeConfigFileName`, `ProjectDepsFileName`, `TargetPath`); hash checked; deps tokens resolved |
| `Copy` | `ReferenceCopyLocalPaths` (+ `DestinationSubDirectory`), in msbuild's order | copied (hash checked); a `project:` item with its related files |
| `Related` | `AllowedReferenceRelatedFileExtensions` | the files beside a copied project output with these extensions, when they exist |
| `BindingRedirects` | `AutoGenerateBindingRedirects` and `GenerateBindingRedirectsOutputType` | nothing (a recorded fact: msbuild's config has redirects, ours not; S55 open) |

Files beside the lock: `<lock without .json>.files/<tfm>/app/<name>` (LF text, sha256 in the
lock), with PR 2's binaries in the same folder (written when bytes change, stale ones removed).
A .NET application's own `App.config` (msbuild copies it as it is) is named where it is, by
hash, and copied byte for byte (BOM and CRLF kept). `Evaluation.Properties` gains
`RuntimeIdentifier` (only where set: no existing lock changes for it).

**deps.json and referenced projects.** msbuild reads a referenced project's versions from the
built file; SDK 8 fails (`MSB4018 FileNotFoundException` in `GenerateDepsFile`) when it is
missing, as for 37 AR test projects in the first run. The import does not build, so a second
msbuild run over the applications only (targets
`BuildOnlySettings;PrepareForBuild;ResolveReferences;XakeAppFiles`, same intermediate folders,
global `OutDir=<temp>/app-out-<cfg>/`) finds every referenced output as a stand-in written
there: a 4.5 KB empty library compiled once (embedded in `Import.fs`), its assembly name patched
to the reference's name (RAR keeps one assembly per name: one shared name dropped all project
entries), version `65534.65533.65532.65531`. Every value of that version in the written
deps.json becomes `$(AssemblyVersion:<file>)`/`$(FileVersion:<file>)` (also in SDK 8's
`<asm>.Reference/<version>` library keys and the dependency on them); the build replaces them
from the file it copied. What msbuild did not read from the file (SDK 8: a project's own
version as its assembly version, an empty file version) stays. Marker, not SDK-version rules:
the same code for SDK 8 and 10. Real outputs are never touched.

**Copy-local left out**: a file beside or below a referenced project's msbuild output (pdb,
satellite `ja/X.resources.dll`, a RAR dependency): msbuild lists it only when that project is
built (found on AR as an A≠C lock difference, fixed in 66c2a85).

**hostStrings** (critic PR 2, "PR 3/4 list" item 2): a RID is not a host string inside a
package's `runtimes/<rid>/` folder, in a deps.json `"rid"`, or where it is the project's
declared `RuntimeIdentifier`; a RID in a `$(DotnetRoot)` path still is. The check also runs over
the text files recorded beside the lock.

## 2. The apphost rule (decision 3)

- No `RuntimeIdentifier`: `"Host": { "Rid": "" }`. Build: the SDK the lock pins
  (`$(DotnetRoot)/sdk/<Evaluation.Sdk>`) names its own RID (`NETCoreSdkRuntimeIdentifier`) and
  the `KnownAppHostPack` version for the framework (base framework of `net8.0-windows`); the
  template is `packs/Microsoft.NETCore.App.Host.<rid>/<v>/...`, else that package (fetched by
  `DotNetFwk.restorePackage`). Not hashed: identified by the SDK version, as the SDK's compiler.
- Explicit RID: `{ "Rid": "win-x86", "Id": "pack:microsoft.netcore.app.host.win-x86/8.0.31/runtimes/win-x86/native/apphost.exe", "Sha256": "..." }`.
  `pack:` now also covers `*.app.host.*` and `*.app.runtime.*` (packs folder or package folder,
  one identity); the restore request names the template.
- Written by the SDK's `HostWriter.CreateAppHost` loaded from that SDK when its runtime is not
  newer than Xake's (SDK major ≤ `Environment.Version.Major`; the night's metadata check needed
  System.Reflection.Metadata, which the package does not reference); else the template with the
  name patched (signed on macOS) and a warning. Name `<assembly>` (+`.exe` for a Windows
  template).

## 3. What msbuild has that the build does not (yet)

- **Binding redirects** in `<exe>.config`/`<dll>.config` (15 of 16 recorded configs on AR differ
  only by `<assemblyBinding>` lines; .NET apps too). S55 open.
- **Satellites**: the project's own and a referenced project's (`ja/`, `zh-CN/`), and their
  `"resources"` entries in deps.json (16 of 37 AR deps.json differ only by those lines).
- **`None`/`Content` copy items** (PR 4): NUnit3TestAdapter/engine files of every test project,
  `TestFiles/`, `Resources/`, `SampleReports/`, `Assets/`, `ActiveReports.config`, the
  coverage source-root mapping files; a referenced application's own runtimeconfig/deps.json
  (the Page tests reference Rendering.Tests).
- A library's `deps.json`; copy-local of a library (a .NET Framework test library's packages
  and `.dll.config`): no `App` section without `HasRuntimeOutput`.
- Self-contained publishing (runtime pack copy-local would go through `Copy` as `pack:`; not
  exercised), HERMETIC=on for `App` inputs (copy items and the template are hashed, not passed
  through the path gate), Windows/Linux runs.

## 4. Tests

`dotnet fsi build.fsx -- -- build test`: green. tests.dll **341 passed + 1 skipped**
(unchanged); hermetic.tests **258 passed** (250 before; +8, `src/hermetic.tests/AppTests.fs`):

- `App recording` (3, pure): stand-in versions to tokens (reference keys, dependency values,
  runtime files; idempotent; SDK 8's project version kept); host strings (package
  `runtimes/<rid>/`, `"rid"`, declared RID allowed; host RID and `$(DotnetRoot)` refused); App
  section round trip.
- `Import record applications` (5, e2e under SDK 10.0.401, one import of lib + four apps; lib
  `AssemblyName` ≠ project name so the `.Reference` path is exercised):
  locks and beside-files hold no root/home/CR/host RID; **import after Xake's and msbuild's
  build records the same locks and files**; console (net8.0, no RID): `"Rid": ""`, copy-local
  incl. `runtimes/*/native/libe_sqlite3.*`, own `App.config` byte for byte, file set equal,
  deps/runtimeconfig equal, `dotnet console.dll` and the apphost print `[1,2]`; WinExe (net472):
  `.exe`, `.exe.config` equal (supportedRuntime added), file set equal, `BindingRedirects: true`;
  explicit RID (win-x64): pack pinned with sha256, file set and deps equal, the apphost is the
  template with the name patched (HostModel of SDK 10 does not load under the net8.0 test host;
  equality with msbuild's bytes when it does) -- not run (a win-x64 app does not start on macOS,
  msbuild's neither); Test.Sdk (net8.0): Xake's files ⊆ msbuild's, the difference is exactly the
  six NUnit adapter/engine files from `None` items (PR 4), deps/runtimeconfig equal,
  `dotnet tests.dll` runs.
- Updated: the generated-files test (beside-lock files as bytes), self-build description and
  lock (`Apps.fs`), the describe fixture.

## 5. ActiveReports (`/Users/olegz/.claude/jobs/7147ff60/tmp/pr3/ar`)

Setup cloned from pr2 (`meta` and `.packages` as APFS clones, bin/obj removed), `build.fsx` with
the four dev apps (Designer, DesignerClassic, Viewer: net472 WinExe win-x86; ThemeEditor:
net472 + net8.0-windows WinExe win-x86) added to stage 3 + stage 5a; mescius Release, SDK
8.0.425, Xake 3.7.10-night, local dll then the packed 0.4.1-night. Compile only, no test run.
Scripts: `pr3/scripts/ar-xake.sh`, `ar-ref.sh`, `ar-stability.sh`, `ar-apps.py`,
`metapairs.fsx`.

| | mescius Release |
|---|---|
| projects / entries / App entries | 89 / 177 / 41 (37 test projects, 4 dev apps; ThemeEditor 2 frameworks) |
| type + method counts, resources, refs, version equal msbuild's | **177/177** |
| clean tree → import (A) → Xake build → import (B) → `dotnet build` → import (C) | **A = B = C** (89 locks and the files beside them) |
| packed 0.4.1-night import after that | = A |
| host strings in locks and beside-files | 0 (RIDs only as package `runtimes/<rid>/`, `"rid"`, `win-x86` declared) |
| `runtimeconfig.json` equal | **37/37** |
| `deps.json` equal | 21/37; the 16 others differ only by referenced projects' satellite `"resources"` lines |
| config equal | 1/16; the 15 others differ only by binding-redirect lines |
| ThemeEditor net8.0-windows (win-x86, apphost from the pinned pack) | 142 of msbuild's 144 files (missing: 2 satellites), **apphost byte-identical** (SDK 8 HostWriter: resources, icon) |
| dev apps net472 (msbuild's shared `dev-apps/bin/Release/net472/`, 260 files) | Xake's union 245, missing 15: 11 `None`/`Content` items (App.config copy, `ActiveReports.config`, `SampleReports/`, `Assets/`, `Themes/`, `StyleSheets/`, VisualElementsManifest) + 4 satellites; extra 0 |
| test projects (net8.0/net8.0-windows) | extra 0; missing = test adapter `None` items (192 over 36), `TestFiles/` etc. (≈200), coverage mapping (36), referenced app's runtime files (10), satellites |

Times (clean, wall incl. fsi): import **29-30 s** (restore 2-3 s, evaluations 13-14 s,
design-time builds 5.7-6.8 s, applications' runtime files pass **1.9-2.3 s**); clean build
34.6-38 s (`CSC_SERVER=off`). PR 2 (85 projects, no apps): import 22-24 s, build 34 s. msbuild
reference: restore 3 s + build 29-32 s.

Lock sizes: 72,035 lock lines (PR 2: 61,730 for 85 projects; the `Copy` lists are most of the
growth) and 89 files beside the locks (4.1 MB, 95k lines, mostly deps.json).

Feed: `Xake.Hermetic.Dotnet.0.4.1-night.nupkg` added to `~/Projects-work/ar/xake-ar/feed/`
(and `pr3/feed`). gccn brand not run.

## 6. Commits

e0aeaa3 lock App section, `pack:` for host/runtime packs; 399e02a import records App,
hostStrings; f872e96 build writes App (`Apps.fs`); 78ce804 tests; 2853491 self-build
description/lock; 1b1a619 second run with stand-ins, marker tokens; 66c2a85 own config by
reference, redirect fact for every framework, copy items below project outputs left out;
2671d7c docs.

## 7. Open

- S55 binding redirects; satellites (own and referenced); PR 4 copy items and deploy.
- `HERMETIC=on` with `App` inputs; Windows/Linux (apphost by the build host's SDK RID, CRLF of
  recorded texts, `Path.GetFileName` of `\` paths in `To`).
- An F# application through `record` (path present, not exercised).
- `SelfContained=true` (runtime pack assets as `Copy` items): untested.
- The stand-in run evaluates the referenced projects again under `OutDir` (2 s on AR).
