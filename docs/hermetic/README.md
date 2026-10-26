# Xake.Hermetic.Dotnet

## 0.8.0 (next release)

Lock portability: a lock set recorded on one OS builds on another, whatever line ends the
checkout gives the repository's files. No lock format change; existing locks stay readable.

- Line-end neutral hashes ([lock.md](lock.md#recorded-locks-importrecord), "Line ends"): the
  text inputs of an import (`Evaluation.Imports`, the `App.Files` beside the lock, the
  project's own `App.config`) are recorded with the hash of their content read with CRLF as
  LF (`FileHash.textSha256`), and checked against that hash or the raw one
  (`FileHash.textMatches`): a lock recorded over LF is current on a CRLF checkout
  (`core.autocrlf`) and the reverse, a lock recorded by 0.7 is current where it was. Binaries
  are still compared byte for byte.
- Targeting packs from NuGet by default ([lock.md](lock.md#recorded-locks-importrecord), "Packs"):
  `Import.RecordOptions.PacksFrom = NuGet | Installed`, `NuGet` the default. The import points
  `NetCoreTargetingPackRoot` at a folder that does not exist, so the targeting, apphost and
  runtime packs come from the package folder: the same hashes on every machine, the SBOM row of a
  pack has its `Sha512`. Every lock with a pack reference changes once on its next import, and
  the import needs the packs on its feed. `Installed` keeps the behaviour of 0.7 (offline imports).
  `RecordOptions` gains the field: a script that builds it without `RecordOptions.Default with`
  adds `PacksFrom`.

## 0.7.0

`Locks.compile` builds the assembly and checks its target
([design note](../features/hermetic-build/locks-compile-targets.md)). **Breaking: `compile`
over an `.xml` target fails**; the `.xml` rule is the new `Locks.doc settings lockPath fwk`
(`"out/(cfg:*)/(fwk:*)/(asm:*).xml" ..> doc`, `doc` a recipe calling it). No lock format change.

- `Locks.doc settings lockPath fwk`: the `.xml` rule's function (before: `compile` on an `.xml`
  target). Needs the assembly beside it, fails when the lock asks for no doc.
- `compile` fails when the target is not the entry's output: the recorded `TargetFileName`
  (`app.exe` for a .NET Framework application, so `app.dll` is refused), else `<Name>.dll` or
  `<Name>.exe` for a lock without one (Xake-generated); a `.pdb` or any other target is refused.
  Before, only the name without the extension was compared.
- The `$(Version)` mask is matched against the project's folder from the lock (`Directory` of
  the compilation) when it names one other than the project root, else where the first source
  lies, as before: a project whose first source is a shared file outside its folder gets its own
  mask's version. A project at the project root matches `xake.project.json` (was an absolute
  path). A mask may start with a script root, `$(Sibling)/**`, expanded through the script's
  roots before matching.
- Internal: `compileDll` is `compileAssembly` (C# and F#).
- Depends on `Xake [3.11.0.40, 3.12)` (was `[3.10.0.35, 3.11)`): the compiler runner of Xake
  3.11 quotes the compiler and the response file, so a compile from an SDK under a path with a
  space (`C:\Program Files\dotnet`) works; the restore and msbuild arguments are quoted by
  Xake's `Tool.quoteArgument` (the package's own copy is gone).
- Carries 0.6.0, which was not released on its own.

## 0.6.0 (not released on its own, shipped in 0.7.0)

The `dotnet-sdk` prerequisite of a recorded lock follows its content
([sdk-free-locks agenda](../features/hermetic-build/sdk-free-locks.md), steps 1-3). **A
re-import drops the prerequisite** from the locks of a project that names nothing under
`$(DotnetRoot)/sdk/<v>/`; every other lock is written as before. No API removed, no lock format
change.

- `Import.record` and `Lock.rehash` give a recorded entry `dotnet-sdk <v>` only when it names a
  file under `sdk/<v>/` (the SDK's compiler or analyzers, an SDK analyzer config, shims, the SDK's
  apphost): `Lock.withRecordedPrerequisites`, `Lock.recordedSdkVersions`. A C# project on
  `Microsoft.Net.Compilers.Toolset` (`RoslynCompilerType=Toolset`) and
  `Microsoft.CodeAnalysis.NetAnalyzers` records none and replays with no SDK of that version
  installed. The import's "stricter than global.json" warning follows. Before, every recorded
  entry carried the SDK that recorded it.
- The replay checks a prerequisite under the `$(DotnetRoot)` the lock is read against (a
  script's extra root of that name, else this machine's).
- A `pack:` identity resolves to the copy whose hash the lock recorded
  (`Lock.Identity.toPathHashed`): the installed pack under `packs/` when its file has that hash,
  else the nuget.org package of the same id and version, fetched by the lock's restore. An
  installed pack is not byte-identical to the package (the package is signed, its facades and
  source generators are other builds): until now a lock recorded from the package folder failed
  the hash check on a machine with the pack installed. A package copy matching neither fails
  the replay naming both (`Lock.packMismatches`); a lock recorded from `packs/` still needs that
  pack installed ([lock.md](lock.md#identities)).
- A recorded application with an apphost (also for an explicit `RuntimeIdentifier`) has the
  `dotnet-sdk` prerequisite: the apphost is written through the SDK's HostModel (version
  resource, icon). Before, without that SDK it was written by a fallback and the build only
  warned.

## 0.5.2

Concurrent imports of one project tree with different properties (two brands) no longer mix
their restores. No API and no lock format change; a re-import writes the same locks.

- `Import.record` restores into a folder of its own per project and key, as it already did for
  the design-time builds: `MSBuildProjectExtensionsPath` is `<obj>/xake-record/<key>/` (the key
  hashes the configuration and the properties) for the restore, the evaluations and the
  design-time builds, so `project.assets.json`, `nuget.g.props`/`.targets` and
  `project.nuget.cache` are the import's own. Until now the restore wrote the project's shared
  `obj/`: with `import::a` and `import::b` in one run, a brand's lock could hold the other brand's
  packages (and its build fail with CS0012), which `check` cannot see. The project's own
  `obj/project.assets.json` (an IDE's, `dotnet build`'s) is no longer written by the import.
- The property is set by a props file the import hands every project
  (`CustomAfterDirectoryBuildProps`), which imports the script's own value of that property and
  the .NET SDK's (`UseArtifactsOutput`) in turn ([lock.md](lock.md#what-the-import-does)).

## 0.5.1

Binding redirects recorded at import (S55): **re-import to record the redirects.** No API and no
lock format change (format 2); a lock written by 0.5.0 builds as before, without redirects.

- `Import.record` records msbuild's finished config file, redirects included, in the lock's
  `App.Files` (`<lock>.files/<framework>/app/`): a .NET Framework application's `<app>.exe.config`
  (until now the file before `GenerateBindingRedirects`: an application whose resources name
  `System.Resources.Extensions` 4.0.0.0 failed to start with the package's 8.0.0.0) and, new, a
  .NET Framework test library's `<name>.dll.config`. The second msbuild run (referenced projects
  as stand-ins) now covers every project msbuild generates redirects for, so the redirects come
  from the packages and never from what is built. A re-import changes these config files and
  their hashes only. The compile and the run folders place them as before.
- Known difference: a .NET (`net8.0-windows`) `.dll.config` with redirects on can lack the 1 to
  3 redirects RAR finds only through a referenced project's built assembly; the .NET runtime
  ignores binding redirects ([lock.md](lock.md#applications-the-app-section)).

## 0.5 (released as 0.5.0)

Built on Xake 3.10 (`[3.10.0.35, 3.11)`): the core added `HeldResources` to `ExecContext`, and a
hermetic build on 3.9 cannot run on it, hence a new minor. What it brings over 0.4.2:

- `Locks.compile settings lockPath fwk` with `Locks.Settings = { Roots; Versions; Lock: Lock.Options }`
  (lock.md); `compileAsWith`/`compileAs` stay as wrappers.
- The script's own compiler throttle: `Settings.Lock.Run.Throttle = Some (resource, units)`
  (`FscRun.Throttle` for fsc), taken by the core's runner around the process launch only; or the
  core's `CSC_JOBS`/`FSC_JOBS` variables with no code. The core now fails fast on a `need` of a rule
  target inside `withResource` (resources.md).

## 0.4 (released as 0.4.0-0.4.2)

Built on **Xake 3.9** (`[3.9.0.30, 3.10)`, nuget.org 3.9.0.30). Everything under "Next" below,
and:

- **Range policy.** The dependency is `Xake [3.9.0.30, 3.10)`, not the next major: a core minor may add fields to the F# records this package constructs (3.9 broke 0.3 that way), so Xake.Hermetic.Dotnet is re-released on each core minor.
- **Xake 3.9 floor; the project description is the core's.** `xake.project.json` is
  `Xake.Dotnet.Description` (the module, the schema `docs/schemas/xake.project.schema.json`) and
  `Xake.Dotnet.ProjectPlan`; this package's copies (`Description.fs`, `Json.fs` as a description
  reader, `docs/hermetic/xake.project.schema.json`) are gone. Its JSON reader of tool output
  (msbuild, `project.assets.json`) is the internal `ToolJson`. The helpers that moved with the
  description are `ProjectPlan`'s now: `frameworkDefines`, `baseFramework`,
  `implicitFrameworkReferences`, `sdkNoWarn`, `manifestName`, `rootNamespace`, `generatedKey`
  (`Described.*` of 0.3; `Described.platformOf`, `preset`, `nearestFramework` and the restore
  stay here).
- **`csc { project }` / `fsc { project }` are the core's.** The core's intrinsic `project`
  composes a description by itself (no packages, no lock, docs/description.md). This package
  adds to it: `config "Release"` (the SDK's preset), `packages p` (what `Described.restore`
  resolved) and `lock "path"` (with `nofetch`); any of them makes the compilation this package's
  (`Described.fsc`/`csc`: packages, `--pathmap`, C# masks kept in the lock), as in 0.3. What a
  script changes: nothing when `config`/`packages`/`lock` follow `project` (the 0.3 order);
  they cannot come before it any more. `csc { project p }` alone is the core's composition,
  which rejects a description with packages. The `lock` builder modules (`CscLocked`,
  `FscLocked`) moved from `Lock.fs` to `Described.fs` (same names, same namespace).
- **The import writes `frameworkReferences`** (msbuild's `FrameworkReference` items, the Web
  SDK's implicit `Microsoft.AspNetCore.App` included, `Microsoft.NETCore.App` left out, the
  WindowsDesktop profiles as `Microsoft.WindowsDesktop.App`), and the described composition
  references the shared frameworks' packs (`DotNetFwk.sharedFrameworkReferences`).
- **Xake 3.8** brought the run folder rule's `ArtifactDep2` (a dependent sees a dependee rebuilt
  by an earlier run). `Described.refreshResources` stays: Xake 3.9's runner still compiles a
  missing `.resources` only (a core follow-up: recompile it when the resx changed).
- **Upper bound `4.0`**: a 3.x minor that adds a field to `CscSettingsType`, `FscSettingsType`,
  `Csc`, `Fsc` or the run options breaks this build binary-wise (F# record construction) and
  needs a release of this package with it.
- **`Pack.fromNuspec`** (S68): the minimal pack from a project's own nuspec, with `$token$`s
  from the description (`Pack.tokensOf`: id, version, authors, description, ...) plus the
  script's, its `<files>`, and `lib/<tfm>` from flat output folders (`Pack.libOf`); `Pack.expand`
  fails naming a token without a value ([pack.md](pack.md#the-minimal-pack-from-a-projects-own-nuspec)).
- **An analyzer crash under the compiler server** (S72, `AD0001`): a failure whose printed
  errors name a crashed analyzer is reported with the analyzer and compiled once more without
  the server ([lock.md](lock.md#building-a-recorded-lock)).
- **No `/errorlog:` on every compile** (0.4.2; S74): 0.4.0 added an error log to each compile
  under the server to find an analyzer crash, and csc then reports every diagnostic, hidden and
  suppressed ones included: about a quarter to a half more compiler CPU per compile (on a trial
  project, 258 s of `VBCSCompiler` CPU against 161 s). The crash is now named from the failed
  compile's own output.
- **`Locks.check`** documented with an example for a script's check target (S67;
  [lock.md](lock.md#run-folders)).
- Verified with tests (fixed in the critic round): no host path in a description written by
  `Import.record` for an output folder outside the project (S70); one SDK pin warning per
  import.

## Next (hermetic, after PR 2; 0.4.3-night with PR 4 and the critic fixes)

- **`Import.record`**: the import that records msbuild's resolved compile inputs (references,
  defines, generated sources, resource names, compiler switches) per configuration and
  framework, and writes the locks itself; there is no `update-locks` step for such a project
  ([lock.md](lock.md#recorded-locks-importrecord)). It writes into the projects' `obj/` (the
  restore's assets file, the design-time builds' own `obj/xake-record/` folder) and creates
  their `bin/` folders, as an IDE does. Every configuration and the script's `Platform` are
  restored for themselves.
- **Lock format 2** (breaking for imported locks): the references, analyzers and compiler of a
  recorded entry are identities (`pkg:`, `pack:`, `sdk:`, `project:`), `Recorded` marks the
  entry, and with masks `Sources` holds only the files the masks do not put in place. A format-1
  lock of an imported project is refused: run the import again. Composed locks read as before.
- **`Project.import`** stays, as legacy: its locks are `"Format": 1` with
  `"Legacy": "Project.import"` and read as before. Use `Import.record` for new imports.
- **Outputs named by `TargetFileName`**: a .NET Framework application is `app.exe`; a script
  that builds one declares an `*.exe` rule beside its `*.dll` rule.
- **The SDK pin is stricter than `global.json`**: a recorded entry always has the `dotnet-sdk`
  prerequisite of the SDK that recorded it. Another SDK means a new import (all its locks
  change); the import warns and shows the exact `global.json` pin.
- **Stale locks fail the build**: a project file or other msbuild file changed since the import
  fails `Locks.compileAs` with "run the import again" (`LOCK_STALE=warn`: a warning, except
  under CI).
- **Generated binaries beside the lock**: a file msbuild generates that is not text (a
  `.licenses`, a `g.resources`) is copied to `<lock>.files/<framework>/`; commit the folder.
- **`Restore.download` in rounds**: one version of a package id per synthesized restore, so a
  lock set that needs two versions of one id fetches both.
- **Applications (PR 3)**: a recorded entry with runtime output has an `App` section -- the
  apphost (the SDK's own without a `RuntimeIdentifier`, identified by the SDK version; with one,
  the pack's template pinned with its hash), msbuild's `runtimeconfig.json`, `deps.json` and
  config file beside the lock (`<lock>.files/<framework>/app/`), msbuild's copy-local files.
  `Locks.compileAs` writes them beside the assembly; a lock of an application changes on the
  next import ([lock.md](lock.md#applications-the-app-section)). The import runs a second
  msbuild pass over the applications. Not yet: binding redirects (0.5.1), satellites.
- **Run folders (PR 4)**: `Locks.runFolderWith` builds the folder an application or a test
  project runs from, from its own lock closure only, by hard links (fallbacks: clone, copy), with
  the manifest `.xake-run.txt` as the rule's target, a conflicts report, stale files removed and
  a no-op that writes nothing; `Locks.runFoldersWith`, `Locks.flatOutOf`, `Locks.runOf`,
  `Locks.runs`; the file side in `RunFolder` ([lock.md](lock.md#run-folders)). Replaces the
  night builds' `Locks.deployWith` (it copied every file of the project's output folder, which a
  flat layout cannot use); it is not in this package.
- **Critic fixes (0.4.3-night)**: a run folder needs its closure's assemblies first, so
  `deploy` on an unbuilt tree builds and places the whole folder; every run folder path stays
  inside the folder (no rooted path, `..` or link on the way), a file changed since a run placed
  it is never replaced or removed, a case-only rename keeps the file; each manifest line carries
  the source's stamp, so a recompile rewrites the manifest and a rule needing it runs again;
  **`Locks.runFolder` places by clone** (APFS, btrfs/XFS reflink, ReFS; else a copy), hard
  links are the opt-in (`RunFolder.Link`; on Windows they lock the compile's output while a test
  runs); `Locks.check`/`checkWith`/`staleWith`/`staleOf` for a script's check target; a
  referenced project's package file the root's restore lacks is warned about. The import: a
  deps.json stand-in version left over fails it naming the file; an application's `App.config`
  and `runtimeconfig.template.json` are lock inputs (an edit: *run the import again*); a WPF
  project is refused up front; description excludes are relative and hold for every
  configuration; one SDK pin warning per import; package files hashed once (memo). The
  apphost: the SDK's own pack missing is a broken prerequisite (nothing fetched at build time);
  one SDK's HostModel per process, a failing one falls back to the byte-patch writer, which
  sets the GUI subsystem on every host.
- **Satellite assemblies: deferred by decision** (S71). Neither a project's own satellites
  (`ja/X.resources.dll`) nor a referenced project's are built or placed, and their
  `"resources"` lines are missing from `deps.json`; against 0.3.7-0.3.8 this is a regression
  for the package entries that shipped them. Recording msbuild's `GenerateSatelliteAssemblies`
  inputs is a later step.
- **The compile no longer copies copy-local files** (breaking against PR 3's previews):
  `Locks.compileAs` writes an application's `Files` and apphost only; the copy-local files and
  items are the run folder's. A script that ran an application from its output folder builds its
  run folder.
- **`App.Content`** and **an `App` for libraries**: the import records the project's own
  copy-to-output items (`None`, `Content`, `EmbeddedResource`, `Compile` with
  `CopyToOutputDirectory`) and gives a library an `App` section when msbuild copies something
  into its output (copy-local files of a .NET Framework test library: S59). `IsTestProject` is
  kept in `Evaluation.Properties`. Every lock with project references changes on the next import.
- **`pack:` covers apphost and runtime packs** (`Microsoft.NETCore.App.Host.<rid>`,
  `*.App.Runtime.<rid>`), wherever the machine has them.

Version **0.3.0, preview**: the API may break while the package is 0.x. 0.3 is built on
**Xake 3.7** (`[3.7.0.26, 3.8)`) and adds:

- **The SDK compiler by the SDK version**: a compiler of the .NET SDK (`fsc.dll`, Roslyn
  `csc.dll`) is identified in the lock by the SDK version, not by a hash, so a lock holds across
  OS and architecture ([lock.md](lock.md#the-compiler-a-lock-names)).
- **`Description` / `Import` / `Described`**: a project described once in JSON, imported and
  compiled with `fsc { project; config; packages }` / `csc { ... }`
  ([description.md](description.md), [lock.md](lock.md#import-the-project-decides-msbuild-answers)).
- **`Locks.generate` / `Locks.compileAs` / `Locks.needAll`**: Xake-generated locks, one per
  described project and configuration, and the layout rules that build from them
  ([lock.md](lock.md#xake-generated-locks)).

## 0.2

0.2 was built on **Xake 3.6** (`[3.6.0.24, 3.7)`) and added:

- **F# in the lock**: a lock entry holds a C# or an F# compilation (`Lock.Compilation`),
  `fsc { lock }` gates an `fsc {}` block, and `Project.import` imports an fsproj like a csproj.
- **Prerequisites**: the .NET SDK at an exact version, recorded per entry and checked before a
  replay (below).
- **`nofetch`** after `lock`, and `Restore.Options.Default` following the base's `NUGET_FETCH`.
- **The `HERMETIC=on` gate on the lock side**: a lock that names a path outside
  `$(ProjectRoot)` and `$(NuGetPackageRoot)` not covered by a prerequisite is neither written
  nor replayed, and an import of a project whose SDK is not pinned exactly fails
  ([hermetic-mode.md](hermetic-mode.md), [lock.md](lock.md#hermetic-mode)).
- **The base's helpers** instead of this package's copies: the build's package folder
  (`DotNetFwk.packageRoot ()`, so the script variable `NUGET_PACKAGES` tokenizes as
  `$(NuGetPackageRoot)`), `DotNetFwk.fetchEnabled ()`, the `global.json` reader
  (`DotNetFwk.globalJsonPin`) and `DotNetFwk.sdkVersionOf`.
- **`RuntimeConfig`** (Xake 3.6): a `.runtimeconfig.json` the rule declares is recorded in the
  entry (`"RuntimeConfig"` under `Compilation`, written only when there is one) and written again
  by a replay.

Breaking against 0.1 and the earlier 0.2 previews:

- **`Lock.build` takes a `Compilation`** (`Lock.Compilation.Csc c` / `Lock.Compilation.Fsc f`),
  and so do `compile`, `record` and `verify`; **`Entry.Csc` became `Entry.Compilation`** (the
  `entry.Csc` member remains, and fails for an F# entry).
- **`Fsproj` removed**: `Project.import` imports an fsproj.
- **Depends on Xake 3.6** (`[3.6.0.24, 3.7)` in 0.2; 0.3 moves to 3.7): the `Csc`/`Fsc` records gained fields, and F#
  records are not binary-compatible across such a change, so 0.2 does not run on 3.5. The upper
  bound is the next minor for the same reason.

- **`Entry.Prerequisites`**: a lock entry records what the environment must provide by itself,
  today the .NET SDK at an exact version (`{ "Kind": "dotnet-sdk", "Version": "<v>", "Pin":
  "$(ProjectRoot)/global.json" }`), written at record time when the entry depends on an SDK that
  `global.json` pins exactly, and checked before every replay with a message naming what to
  install. Written only when not empty, so existing locks stay byte-identical. Code that builds a
  `Lock.Entry` record by hand adds `Prerequisites = []`. See
  [lock.md](lock.md#prerequisites).
- **`packageroot` removed** from `csc { lock }` / `fsc { lock }`: the package folder is the
  build's one (`NUGET_PACKAGES`). `Restore.into` and `Lock.loadWith (Roots.packageRootOverride
  dir)` stay for scripts that read a lock against another folder.
- **`norestore` renamed `nofetch`**, after the base's `NUGET_FETCH`.
- **`Restore.Options.Default` follows `NUGET_FETCH`** and the build's package folder: where a
  build context exists (`Restore.ensure`, `Lock.compileWith`, `Lock.buildWith`), `PackageRoot =
  None` is `DotNetFwk.packageRoot ()` (the script variable `NUGET_PACKAGES` first) and
  `Enabled = true` means "fetch unless `NUGET_FETCH=off`" (`Restore.resolve`).

`Xake.Hermetic.Dotnet` builds .NET assemblies and packages so that what shipped can be
explained and checked: a **lock** file records exactly what the compiler was handed and the hash
of everything it read, the build compiles from the lock and fails when anything differs, and the
result can be turned into a deterministic package with an SBOM and a signing step. It is a
separate NuGet package that depends on `Xake` (the engine and the `csc`/`fsc`/`msbuild`/`resgen`
tasks of `Xake.Dotnet`, Xake 3.9.0.30 or later, below 3.10); nothing in `Xake` knows about it.

```fsharp
#r "nuget: Xake.Hermetic.Dotnet, 0.4.0"

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet
```

## The pieces

| Piece | What it is | Page |
|---|---|---|
| Lock | the lock file format (C# and F# entries), `Lock.build` / `compile` / `record` / `verify`, `csc { lock }`, `fsc { lock }`, `Project.import` (msbuild design-time build of a csproj or fsproj into a lock) | [lock.md](lock.md) |
| Description | `xake.project.json`: what a project is, as data (name, language, frameworks, sources, references, packages, options, package metadata); `Description.load`/`save`/`find`/`names`/`beside`; schema in [../schemas/xake.project.schema.json](../schemas/xake.project.schema.json) | [description.md](description.md) |
| Hermetic mode | `HERMETIC=on`: the invariant, prerequisites, what each input needs, the messages; what is shipped and what is pending | [hermetic-mode.md](hermetic-mode.md) |
| Restore | fetch the packages a lock names into a folder of the build's choice, one `dotnet restore` for the whole set | [restore.md](restore.md) |
| Nuget and Sbom | read the restore graph and package cache; write a deterministic CycloneDX 1.6 SBOM per assembly and per package | [nuget-sbom.md](nuget-sbom.md) |
| Verify | Authenticode PE hash and labelled byte differences between two binaries | [verify.md](verify.md) |
| Pack | deterministic zip and `.nupkg` (sorted entries, fixed timestamps, content-derived ids) | [pack.md](pack.md) |
| StrongName | PE timestamp and checksum normalisation, strong-name (re-)signing without `sn.exe` | [strongname.md](strongname.md) |
| Sign | Authenticode and NuGet signing as a delegated rule with a pluggable signer | [signing.md](signing.md) |
| Workflows | developer, teammate, CI (GitHub Actions, GitLab CI), release and offline flows end to end; what to commit; how a missing lock fails CI | [workflows.md](workflows.md) |

The compilation itself, `csc {}`/`fsc {}`, `Csc.ofSettings`/`Fsc.ofSettings` and the runners
`Csc.run`/`Fsc.run`, stay in `Xake.Dotnet`:
[../csc-syntax.md](../csc-syntax.md), [../tasks.md](../tasks.md).

- [Architecture](../hermetic-architecture.md): the design, where it meets `Xake`, the evidence
  and the known limits, for maintainers and reviewers.
- [Guide](../hermetic-guide.md) (draft): a task-oriented walk-through, from the first lock to
  packing and signing.

## Two ways in

Gate a `csc {}` block by a lock. The first build records `locks/app.json` and compiles; later
builds fail with a diff when the settings, a reference or the compiler no longer match it:

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

do xakeScript {
    rules [
        "out/app.dll" ..> csc {
            targetfwk "net-4.6.2"
            src !!"src/*.cs"
            grefs ["System.dll"]
            lock "locks/app.json"
        }
    ]
}
```

The same thing without the sugar, with the resolved `Csc` in hand:

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

"out/app.dll" ..> recipe {
    let! c = csc { targetfwk "net-4.6.2"; src !!"src/*.cs"; grefs ["System.dll"]
                   out (File.make "out/app.dll"); resolve }
    do! Lock.build "locks/app.json" (Lock.Compilation.Csc c)
}
```

`Lock.build` gates against the lock, restores what it names, resolves the revision token and
then calls `Csc.run` (`Fsc.run` for `Lock.Compilation.Fsc f`).

An F# library the same way. The compiler is the SDK's `fsc.dll`, so pin the SDK
(`global.json` with `rollForward: disable`):

```fsharp
"out/lib.dll" ..> fsc {
    targetfwk "net8.0"
    src !!"src/Lib.fs"
    lock "locks/lib.json"
}
```

To update a lock deliberately, delete the file or run a target that calls
`Lock.record`.

## Building from source

`src/hermetic` compiles against the published `Xake` package (the range above, `[3.6.0.24, 3.7)`),
not against `src/core` and `src/dotnet` in the same checkout. A plain restore takes it from
nuget.org; nothing else is needed:

```bash
dotnet restore src/hermetic.tests
dotnet build src/hermetic -c Release --no-restore
dotnet test src/hermetic.tests -c Release --no-restore
```

The lower bound is the exact version nuget.org carries (the release pipeline appends the run
number to the version, so 3.7.0 is published as 3.7.0.26). A bound with no published package at
it makes NuGet pick the next one up and warn NU1603 on every restore.

Only when working against an *unreleased* base (a change in `src/core` or `src/dotnet` that
`src/hermetic` needs before it is on nuget.org) pack the base into a folder with a version inside
the range and hand that folder to the restore as an extra source; nothing in the repository
points at it:

```bash
dotnet pack src/dotnet -c Release -p:Version=3.7.0.99 -o /tmp/xake-feed
dotnet restore src/hermetic.tests -p:RestoreAdditionalProjectSources=/tmp/xake-feed
dotnet test src/hermetic.tests -c Release --no-restore
```

The package lands in the NuGet global cache (`~/.nuget/packages/xake/3.7.0.99`) and wins over
the released one from there on; delete that folder when done, and raise the lower bound in
`Xake.Hermetic.Dotnet.fsproj` to the released version once the base is published.
