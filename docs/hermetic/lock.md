# The lock: format, `Lock.*`, `Import.record` and `Project.import`

A lock is a JSON file that records, per project and target framework, exactly what the compiler
is handed (the command line, generated inputs, resx pairs) and the SHA-256 of everything it
reads that is not in the repository: the compiler, references, analyzers, the msbuild files
that produced the answer, and the package graph. `Lock.build` and `Lock.compile` then compile
*from* the lock and fail when anything differs. This page is the reference for the file
format, the `Lock` module, `Project.import` (csproj and fsproj) and the `csc { ...; lock "path" }`
and `fsc { ...; lock "path" }` operations. A lock entry holds a C# or an F# compilation
([F# entries](#f-entries)).

Everything here is in the package `Xake.Hermetic.Dotnet` (`src/hermetic/`: `Lock.fs`,
`Project.fs`, `Roots.fs`, `Git.fs`, `ToolJson.fs`); the compilations a lock entry wraps, `Csc` and
`Fsc`, and their runners `Csc.run` and `Fsc.run` belong to `Xake.Dotnet` (inside the `Xake`
package) and are described in [../csc-syntax.md](../csc-syntax.md). Xake.Hermetic.Dotnet 0.4 needs
Xake 3.9 (`[3.9.0.30, 3.10)`): the project description (`Xake.Dotnet.Description`) and
`csc { project }` / `fsc { project }` are the core's, this package adds `config`, `packages` and
`lock` to them; the upper bound is the next core minor because core minors may add fields to the F# records this package constructs, so the package is re-released per core minor; the run folder rule relies on `ArtifactDep2` (Xake 3.8). Scripts open both:

```fsharp
#r "nuget: Xake, 3.9.0.30"
#r "nuget: Xake.Hermetic.Dotnet"

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet
```

## The API at a glance

| Name | Kind | What it does |
|---|---|---|
| `Lock.Compilation = Csc of Csc \| Fsc of Fsc` | type | what the compiler is handed, C# or F#; `[<RequireQualifiedAccess>]`, so `Lock.Compilation.Csc c`. Members `Name`, `Framework`, `Directory`, `Options`, `Defines`, `Sources`, `Generated`, `Resources`, `Dependencies`, `Args`, `Output` |
| `Lock.Entry = { Compilation; Evaluation; Packages; Prerequisites; Recorded }` | type | one project's compilation, where the answer came from, the restore graph, what the environment must provide ([Prerequisites](#prerequisites)), and `Recorded` (`Some` for an entry recorded from msbuild by `Import.record`, `None` for a composed one). Members `Name`, `Framework`, `Dependencies`, `Args`, `Output` (the compilation's), and `Csc` / `Fsc`, which return that case and fail for the other |
| `Lock.Recorded = { Projects; Masks }`, `Lock.ProjectOutput = { Name; Configuration; Framework; Path }` | type | the marker of a recorded entry: the referenced projects' outputs (`project:<name>`) and the description's source masks ([Recorded locks](#recorded-locks-importrecord)) |
| `Lock.Identity.ofPath ctx path` / `toPath ctx id` | pure (`toPath` reads one directory) | the portable name of a file of a recorded entry (`pkg:`, `pack:`, `sdk:`, `project:`, or the tokenized path) and back |
| `Lock.FormatVersion` | literal | `2`: the version `format` writes as `"Format"` |
| `Import.record RecordOptions projects` | recipe | descriptions and recorded locks from msbuild's design-time builds ([Recorded locks](#recorded-locks-importrecord)) |
| `Lock.Document = { Configuration; Properties; Entries }` | type | one lock file |
| `Lock.Prerequisite = { Kind; Version; Pin }` | type | an input no restore fetches, checked before a replay; `Kind` is `Lock.DotnetSdk` (`"dotnet-sdk"`) today ([Prerequisites](#prerequisites)) |
| `Lock.sdkPinAt dir` | pure (reads disk) | the `global.json` found from `dir` upwards and the `SdkPin` it states (`Project.sdkPin` is its second half) |
| `Lock.sdkVersionsOf roots paths` / `prerequisitesFor roots pinFile pin versions` | pure | the SDK versions paths depend on (`$(DotnetRoot)/sdk/<v>/...`); the prerequisites under a pin, and the versions left without one |
| `Lock.recordedSdkVersions dotnetRoot entry` / `withRecordedPrerequisites dotnetRoot pin entry` | pure | the SDK versions a recorded entry names (its command line's and application's paths under `<dotnetRoot>/sdk/<v>/`, its own SDK when it has an apphost, which the SDK's HostModel writes); the entry with a `dotnet-sdk` prerequisite per version, none when it names nothing there ([Prerequisites](#prerequisites)) |
| `Project.import ImportOptions` | recipe | msbuild design-time build per project and framework, writes the lock ([below](#where-a-lockentry-comes-from)) |
| `Lock.load` / `loadWith extraRoots` | recipe | read a lock, `needFiles` it, expand paths for this machine |
| `Lock.save` / `saveWith extraRoots` | recipe | write a lock, tokenizing paths |
| `Lock.format roots doc` / `parse roots text` / `read roots path` | pure | the file format, with the roots passed explicitly |
| `Lock.entry name doc` / `entryFor framework name doc` | pure | look an entry up (`entryFor` is the unambiguous one) |
| `Lock.ofCompilation c` / `ofCsc c` / `ofFsc f` | pure | a composed compilation as an entry: empty `Evaluation`, no packages |
| `Lock.rehash entry` | pure (reads disk) | fill every empty `Sha256` from what is on disk now |
| `Lock.diff a b` | pure | the differences between two entries, `[]` = identical |
| `Lock.diffText roots a b` | pure | `diff` with every path tokenized against `roots`, as `buildWith`/`verify` print it |
| `Lock.underCi` | recipe | whether this build runs under CI (script variable `CI`, then the environment variable `CI`); under CI `buildWith` fails on a missing lock ([The CI flag](#the-ci-flag)) |
| `Lock.mapPaths f entry` / `mapText f entry` | pure | rewrite paths / text of an entry |
| `Lock.packagesOf cacheRoot assets` | pure | the package graph of an entry from a `Nuget.Assets` |
| `Lock.restoreRequest entries` / `Lock.restore options doc` | pure / recipe | what a restore must provide for entries; populate the package folder from a whole lock ([restore.md](restore.md)) |
| `Lock.compile entry` / `compileWith Lock.Options entry` | recipe | replay an entry: restore, compiler check, revision token, then `Csc.run` or `Fsc.run` |
| `Lock.build path c` / `buildWith Lock.Options path c` | recipe | build a `Lock.Compilation` gated by the lock at `path`; a missing lock is recorded, or fails under CI |
| `Lock.record path c` | recipe | hash `c` (a `Lock.Compilation`) and (over)write the lock at `path`, compile nothing |
| `Lock.verify path c` | recipe | the diff between the lock at `path` and `c`, write and compile nothing; a missing lock fails |
| `csc { ...; lock "path" }` | operation | `Lock.build` as an operation of a `csc {}` block; only `nofetch` may follow |
| `fsc { ...; lock "path" }` | operation | the same for an `fsc {}` block |
| `csc`/`fsc { ...; project "path"; config "Release"; packages p; lock "path" }` | operation | a compilation from a project description, gated the same way; a C# lock keeps the description's masks in `Sources` (an exclude as `!$(ProjectRoot)/...`), expanded when compiled ([../csc-syntax.md](../csc-syntax.md#from-a-project-description-project-config-packages)). No format change: `Sources` entries are strings the reader takes as written |
| `Described.restore d framework` | recipe | a description's packages through an msbuild restore: compile assets, the graph for `Entry.Packages`, the packages' `frameworkAssemblies` (`FrameworkReferences`, referenced by name on .NET Framework) |
| `Described.restoreWith descriptionOf d framework` | recipe | `restore` plus the packages `d`'s project references declare (their closure; each description at `descriptionOf name`; not `private` ones): for a project compiled without a lock |
| `Described.restoreAll descriptionOf descriptions frameworks` | recipe | the `restoreWith` sets of every description (paths) x framework (`[]`: each one's own) in ONE `dotnet restore`, kept in the build's cache: the direct route's batch ([one restore per build](#one-restore-per-build)) |
| `Described.prefetch requests` | recipe | (framework, packages) sets restored with one `dotnet restore` into the build's cache; what both `restoreAll`s call |
| `Described.closure descriptionOf d` | recipe | the transitive project references of `d`, a project after the ones it references, each once; a cycle fails: the direct route's `ref` list |
| `Locks.restoreAll descriptions cfg lockOf descriptionOf` | recipe | the sets the lock rules' `generate` will restore, in ONE `dotnet restore`, before `need`ing them ([one restore per build](#one-restore-per-build)) |
| `Locks.generate lockOf outOf config descriptionPath` | recipe | the lock of one described project in one configuration, nothing compiled, written only on change; the referenced projects' locks must exist ([Xake-generated locks](#xake-generated-locks)) |
| `Locks.compile settings lockPath fwk` | recipe | the layout's assembly rule (`.dll`, `.exe`): the lock's entry for `fwk` compiled into the rule's target, which must be the entry's output (`TargetFileName`, else `<Name>.dll`/`.exe`); an `.xml` target fails, that is `Locks.doc`. `settings` (`Locks.Settings`): extra roots, version masks, `Lock.Options` ([Building from Xake-generated locks](#building-from-xake-generated-locks)) |
| `Locks.doc settings lockPath fwk` | recipe | the layout's `.xml` rule: needs the assembly beside it, whose `compile` rule writes the doc, and fails when the lock asks for no doc; takes `compile`'s parameters, only `Roots` is used |
| `Locks.compileAs lockPath fwk versions` | recipe | `compile { Settings.Default with Versions = versions }`; `compileAsWith roots` adds `Roots = roots` |
| `Locks.needAll descriptions cfg lockOf outOf` | recipe | needs the layout `.dll` of every framework of every described project; a project without a lock fails naming `update-locks`; the packages all locks name and the folder lacks are fetched first in one restore |
| `Lock.compileFrom lockPath options entry` | recipe | `compileWith` of an entry read from the lock at `lockPath` (named by the HERMETIC refusal) |
| `Lock.recorded path unhashed entry` | recipe | the record-time step of `record` (hashes, prerequisites, the HERMETIC gate) without writing; the references in `unhashed` keep an empty hash |

`Lock.Options = { Run: RunOptions; FscRun: FscRunOptions; Restore: Restore.Options }` with
`Lock.Options.Default`. `Run` is read for a C# entry and `FscRun` for an F# one; each runner has
its own options type (fsc has no compiler server), and neither is derived from the other.

The two routes to a lock, and both end in the same functions:

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

// 1. from composed settings: a lock gates the compilation the script describes
"out/app.dll" ..> csc {
    targetfwk "net-4.6.2"
    src !!"src/*.cs"
    grefs ["System.dll"]
    lock "locks/app.json"
}

// 2. the same thing without the sugar: resolve, then Lock.build
"out/app.dll" ..> recipe {
    let! c = csc { src !!"src/*.cs"; targetfwk "net-4.6.2"; grefs ["System.dll"]; out (File.make "out/app.dll"); resolve }
    do! Lock.build "locks/app.json" (Lock.Compilation.Csc c)   // gate, restore, revision, Csc.run
}

// 3. an F# library, the same way
"out/lib.dll" ..> fsc {
    targetfwk "net8.0"
    src !!"src/Lib.fs"
    lock "locks/lib.json"
}
```

## Recorded locks: `Import.record`

A recorded lock is msbuild's resolution of a project, written down. Xake does not derive
references, defines, generated sources, resource names or compiler switches of an imported
project: msbuild resolves them, the import records them, the build materialises them. (The
composed route -- `csc {}`, hand-written descriptions and `Locks.generate` -- keeps its own
simple resolution, below.) One exception: the C# source **masks**. When the description's masks
name exactly msbuild's `Compile` files, the lock keeps the masks, and a build compiles what the
masks name then: an added `.cs` file needs no new import. Where msbuild excludes a file under a
condition the masks do not know, the two can drift; the import checks them only when it runs.

### What the import does

`Import.record options projects` (module `Import`, `src/hermetic/Import.fs`):

1. A restore of all `projects` under the first configuration: a generated solution that
   declares every configuration of `RecordOptions.Configurations` on the script's `Platform`
   (`Any CPU` when `Properties` has none), `-t:Restore`.
2. The evaluations of each project, for its description
   ([description.md](description.md#import-that-records-msbuilds-compile-inputs-importrecord)).
3. Per configuration, one after the other: the restore of that configuration (the first one is
   done), then one msbuild process that runs every design-time build of every (project,
   framework): a generated traversal project, `-m`, with `ProvideCommandLineArgs=true`,
   `SkipCompilerExecution=true`, `BuildProjectReferences=false` and the targets `CoreBuild`
   runs up to the compiler, except the project's `PreBuildEvent`
   (`BuildOnlySettings;PrepareForBuild;ResolveReferences;PrepareResources;ResolveKeySource;Compile`).
   A targets file given to every project (`CustomAfterMicrosoftCommonTargets`) writes the
   compiler's command line (`CscCommandLineArgs`/`FscCommandLineArgs`), `ReferencePath`,
   `Analyzer`, `Compile`, `EmbeddedResource`, `EditorConfigFiles` and some properties to a file.
   The project's own value of `CustomAfterMicrosoftCommonTargets` (set by the repository, for
   example in `Directory.Build.props`, or by the script) is imported from that file, so its
   targets run as in `dotnet build`. In one process a project that many others reference is
   evaluated once. The assets files are copied before the next configuration's restore: a
   package may depend on the configuration.

   The same targets file also writes, for an application (`HasRuntimeOutput`), msbuild's
   copy-local files (`ReferenceCopyLocalPaths`), the apphost template (`ResolvedAppHostPack`),
   the config file before binding redirects, the project's own config file and the related
   extensions (`AllowedReferenceRelatedFileExtensions`). A second msbuild run over the
   applications and every project msbuild generates binding redirects for (a .NET Framework
   test library) writes their `runtimeconfig.json` and `deps.json`
   (`GenerateBuildRuntimeConfigurationFiles`, `GenerateBuildDependencyFile`) and the config
   file with its binding redirects (`GenerateBindingRedirects`), see
   [Applications](#applications-the-app-section).

   Each design-time build writes what it generates (assembly info, compiled resources) into an
   intermediate folder of its own, `<BaseIntermediateOutputPath>/xake-record/<key>/<tfm>/`, where
   the key is a hash of the configuration and the properties. The restore output goes beside
   them, to `<MSBuildProjectExtensionsPath>/xake-record/<key>/` (`obj/xake-record/<key>/` by
   default): `project.assets.json`, `<project>.nuget.g.props`/`.targets`, `project.nuget.cache`.
   Every msbuild run of the import -- the restores, the evaluations, the design-time builds --
   reads that folder. The layout of one project:

   ```
   obj/xake-record/<key>/project.assets.json      the restore of that key
   obj/xake-record/<key>/<project>.nuget.g.props  (and .targets, project.nuget.cache)
   obj/xake-record/<key>/<tfm>/                   the design-time build's intermediate folder
   ```

   Two imports with different properties at the same time (two brands: `need [ "import::a";
   "import::b" ]` with packages that differ between them), an IDE and `dotnet build` do not share
   these files; the project's own `obj/project.assets.json` is not written by the import.
   `PrepareForBuild` still creates the `bin/` folders.

   The folder is set by a props file the import hands every project as the global property
   `CustomAfterDirectoryBuildProps` (imported by `Microsoft.Common.props` after
   `Directory.Build.props`, before the restore output is imported; `Import.restoreProps`), with
   the key as `XakeRecordKey`. It sets `MSBuildProjectExtensionsPath` from the project's own value
   (or `BaseIntermediateOutputPath`, or `obj/`) plus `xake-record/<key>/`. A global
   `MSBuildProjectExtensionsPath` would not do: msbuild does not make a global property absolute,
   and the restore output's relative import would resolve against the SDK's file. The global
   property hides two values, which the props file imports in their place: the script's own
   `CustomAfterDirectoryBuildProps` (in `Properties`) and the .NET SDK's
   (`UseArtifactsOutputPath.props`, for `UseArtifactsOutput`). A repository that sets
   `CustomAfterDirectoryBuildProps` in a file of its own loses that value in the import (it is an
   msbuild hook for the command line; `CustomAfterMicrosoftCommonTargets` is chained).
4. Each entry is the recorded command line, changed only as follows:
   - `/out:` and `/refout:` are removed (the output is the build's). `/sourcelink:` and `/embed`
     are removed: SourceLink is parked.
   - What msbuild generated under the intermediate folder is sorted by kind
     (`Import.isRecordedText`). A text kind (`.cs`, `.fs`, `.vb`, `.editorconfig`,
     `.globalconfig`, `.json`, `.txt`: assembly info, global usings, the framework attribute, the
     analyzer config) moves to `obj/xake/<key>/<tfm>/<cfg>/` and goes into `Generated` as text:
     LF line ends, the commit as `$(SourceRevisionId)`, paths under a root tokenized. A source
     from a package (Test.Sdk's entry point) is recorded the same way.
   - The `.resx` msbuild compiles go into `Resources`; their `/resource:` switches name the
     moved `.resources`, which the build compiles again.
   - Any other generated file (a WinForms `.licenses`, a WPF `g.resources`, a `.res`) is copied
     byte for byte beside the lock, to `<lock without .json>.files/<tfm>/`
     (`locks/app/Release.files/net8.0/app.g.resources`), and the switch names the copy. Commit
     that folder with the lock. The import writes a copy only when its bytes change and removes
     the copies it no longer names.
   - `Sources` is msbuild's `Compile` list in msbuild's order. For C#, when the description's
     masks name exactly these files, the masks go into `Recorded.Masks` (else a warning, and
     `Sources` keeps msbuild's whole list). With masks, the lock writes in `Sources` only the
     files the masks do not put in place, and `Recorded.Placed` says where each goes (below).
   - A project reference is the referenced project's output in the build's layout
     (`OutOf cfg <NearestTargetFramework> <name>`), in `Recorded.Projects`.
   - Every reference and analyzer is hashed, except a project's output (unhashed, as in every
     lock) and a file of the SDK (identified by the SDK version).
   - `Prerequisites` has `dotnet-sdk <NETCoreSdkVersion>` when the entry names a file of that
     SDK (an `sdk:` identity, any file under `$(DotnetRoot)/sdk/<v>/`, an apphost):
     the lock pins the SDK, and another SDK means a new import. On the toolset compiler and
     package analyzers it names none and has no prerequisite (since 0.6,
     [Prerequisites](#prerequisites)).
5. The lock is written only when its text changes. Before that, these checks stop the import
   and name the file or line:
   - every reference, analyzer and compiler must have a portable name (below), and so must every
     file a compiler switch or a source names (`/analyzerconfig:`, `/keyfile:`, `/ruleset:`,
     `/win32icon:`, `/win32manifest:`, `/additionalfile:`, `/resource:`, ...;
     `Import.unportablePaths`). A `/pathmap:` source path must be under a root. An
     `.editorconfig` above the repository fails here: move it in, or declare its folder as a
     root;
   - a `ProjectReference` to a project that is not in `projects` fails: its output would be
     `project:<name>` with no lock behind it. Two referenced project files with the same name
     fail too;
   - the text, and the text files recorded beside it, must contain no string of this machine
     (`Import.hostStrings`: the roots, the home and temp folders, a runtime identifier). A
     runtime identifier is allowed where a package names it -- its folder `runtimes/<rid>/` (a
     copy-local asset, a `deps.json` path) or a `deps.json` asset's `"rid"` -- and where the
     project declares it (`RuntimeIdentifier`), but not in a `$(DotnetRoot)` path.

   When the SDK that recorded the lock is pinned more strictly than `global.json` pins it (any
   `rollForward` but `disable`, or no `global.json`), the import warns and shows the `global.json`
   that says the same.

A design-time build that fails stops the import (msbuild's exit code). Nothing is written
from a partial answer.

### Identities

On disk, the references, analyzers and compiler of a recorded entry are **identities**, not
paths (`Lock.Identity`):

| Identity | The file | On another machine |
|---|---|---|
| `pkg:<id>/<version>/<path>` | a package file in the package folder | `$(NuGetPackageRoot)/<id>/<version>/<path>`; a missing package is fetched (`NUGET_FETCH`) |
| `pack:<pack>/<version>/<path>` | a file of a targeting pack (`Microsoft.<X>.App.Ref`), an apphost pack (`Microsoft.NETCore.App.Host.<rid>`) or a runtime pack (`Microsoft.<X>.App.Runtime.<rid>`), under `$(DotnetRoot)/packs` or downloaded into the package folder | the pack under this .NET's `packs/` when it has that version, else the nuget.org package of that id and version (`Microsoft.NETCore.App.Ref`, `Microsoft.WindowsDesktop.App.Ref`, `Microsoft.AspNetCore.App.Ref`, `Microsoft.NETCore.App.Host.<rid>`) in the package folder, fetched by the lock's restore like any package; accepted by the recorded hash (see below) |
| `sdk:<path>` | a file of the SDK the entry was recorded with (the compiler, the SDK's analyzers and shims) | `$(DotnetRoot)/sdk/<Evaluation.Sdk>/<path>`; the `dotnet-sdk` prerequisite fails first when that SDK is not installed |
| `project:<name>` | a referenced project's output | `Recorded.Projects[name].Path` (tokenized, the build's layout) |
| `$(ProjectRoot)/...`, `$(Sibling)/...` | a file under a root | the root on this machine |

Anything else (an absolute path outside the package folder, the packs, the SDK and the roots)
fails the import: declare its folder as a root (`RecordOptions.Roots`) or fix the project. The
same pack has the same identity whether this .NET has it or msbuild downloaded it, so two
machines record the same lock *text*.

The hashes are another matter: an installed pack is not byte-identical to the nuget.org package
of the same version. The package signs its assemblies (Authenticode certificate data, a
strong-name signature where the installed copy has zeros, the PE checksum) and carries other
builds of the type-forwarding facades and of the source generators under `analyzers/`
(measured on `microsoft.netcore.app.ref` 8.0.32: all 234 files differ, 65 of them beyond the
signatures). One identity, two possible contents: **a `pack:` resolves to the copy whose hash
the lock recorded** (`Lock.Identity.toPathHashed`, when the lock is read). The installed file
under `$(DotnetRoot)/packs/` when this .NET has it and its SHA-256 is the recorded one, else the
package folder's copy, which the lock's restore fetches. So a lock recorded from the package
folder (the importing .NET had not that pack, or `NetCoreTargetingPackRoot` pointed elsewhere)
builds on a machine with the pack installed and on one without it (`SdkFreeImportTests.fs`: a
net6.0 project against an empty `packs/`). A lock recorded from `packs/` builds only where that
pack is installed: after the restore, a package copy that matches neither fails the replay
naming both candidates (`Lock.packMismatches`):

```
'<name>': pack:microsoft.netcore.app.ref/8.0.32/ref/net8.0/System.Runtime.dll matches neither copy
the lock may name (recorded <sha>): the installed pack '<dotnet>/packs/...' (<sha>), the package
'<packages>/microsoft.netcore.app.ref/8.0.32/...' (<sha>)
```

The installed file is hashed through `FileHash` (memoised by path, length and write time), so
a build of many locks naming one pack hashes each of its files once.

### Format 2

```json
{
  "Format": 2,
  "Configuration": "Release",
  "Properties": { "Brand": "Globex", "RestoreConfigFile": "$(ProjectRoot)/NuGet.config" },
  "Entries": [
    {
      "Name": "app", "Framework": "net472",
      "Evaluation": { "Project": "$(ProjectRoot)/app/app.csproj", "ProjectRefs": [ "lib" ], "Sdk": "8.0.425", ... },
      "Compilation": { "Directory": "$(ProjectRoot)/app", "Options": [ ... ], "Sources": [ "$(ProjectRoot)/obj/xake/app/net472/Release/app.AssemblyInfo.cs" ], "Generated": { ... }, ... },
      "Dependencies": {
        "Compiler": { "Tool": "csc", "Id": "sdk:Roslyn/bincore/csc.dll", "Sha256": "", "Version": "4.11.0-3.25569.22" },
        "References": [
          { "Id": "project:lib", "Sha256": "" },
          { "Id": "pkg:newtonsoft.json/13.0.3/lib/net45/Newtonsoft.Json.dll", "Sha256": "..." },
          { "Id": "pkg:microsoft.netframework.referenceassemblies.net472/1.0.3/build/.NETFramework/v4.7.2/mscorlib.dll", "Sha256": "..." }
        ],
        "Analyzers": [ { "Id": "pack:microsoft.netcore.app.ref/8.0.11/analyzers/dotnet/cs/System.Text.Json.SourceGeneration.dll", "Sha256": "..." } ],
        "Packages": [ ... ],
        "Prerequisites": [ { "Kind": "dotnet-sdk", "Version": "8.0.425", "Pin": "$(ProjectRoot)/global.json" } ]
      },
      "Recorded": {
        "By": "msbuild",
        "Masks": [ "$(ProjectRoot)/app/**/*.cs", "!$(ProjectRoot)/app/bin/**/*", "!$(ProjectRoot)/app/obj/**/*" ],
        "Placed": { "$(ProjectRoot)/obj/xake/app/net472/Release/app.AssemblyInfo.cs": "$(ProjectRoot)/app/P.cs" },
        "Projects": [ { "Name": "lib", "Configuration": "Release", "Framework": "netstandard2.0", "Path": "$(ProjectRoot)/out/Release/netstandard2.0/lib/lib.dll" } ]
      }
    }
  ]
}
```

- `"Format": 2` is written in every lock of recorded or composed entries. A lock without it is
  format 1. A format-1 lock with an imported entry (`Evaluation.Project` not empty) is refused:
  *lock format 1 is older than 2: its imported entries are Xake's own resolution of the project,
  not msbuild's; regenerate the lock (run the import again: Import.record)*. A format-1 lock of
  composed entries only still reads. A format above 2 is refused.
- `Project.import` (the import before `Import.record`, which writes Xake's own resolution of the
  project) writes `"Format": 1` with `"Legacy": "Project.import"`. Such a lock reads as before.
  A format-2 lock with an imported entry that has no `Recorded` section is refused.
- With masks, `Sources` holds only the files the masks do not put in place: the generated ones
  and msbuild's exceptions to the masks' order. The masks' order is msbuild's globbing: for each
  include mask in turn, the files it names that no earlier one did, sorted ordinally ignoring
  case (`Lock.canonicalSources`). `Recorded.Placed` names, for each of those files, the file
  msbuild compiles before it (`""` for the first). For the trial product this removes about 19,600
  of the 20,010 source lines of 172 entries (Designer.App: 2,179 per framework down to 4).
- `Recorded` is the marker of an entry recorded from msbuild (`"By": "msbuild"`). A composed
  entry has no such section and keeps `Path` items.
- `Evaluation.Properties` keeps a few of msbuild's properties, among them `TargetFileName`:
  `Locks.needAll` and `Locks.compileAs` name the output after it (`app.exe` for a .NET Framework
  application, `app.dll` for .NET).
- Document `Properties` are tokenized like paths.

### Applications: the `App` section

An entry whose project has runtime output (`HasRuntimeOutput`: an exe, a winexe, a .NET test
project) has an `App` section: what msbuild makes of the project besides the compile, recorded
from msbuild, nothing derived. A library has one too when msbuild copies something into its
output: copy-local files (a referenced project's output; a .NET Framework test library's
packages) or its own copy-to-output items, or when msbuild generates binding redirects for it
(a .NET Framework test library). Then `OutputType` is `Library`, there is no `Host`, and
`Files` holds the config file only (`<name>.dll.config`, with its redirects), if any.

The compile writes the files named after the assembly beside it (`Apps.write`, called by
`Locks.compileAs`; side outputs of the assembly's rule, like the `.xml`): `Files` and the
apphost. One output folder can hold these for every project. The copy-local files and the
copy-to-output items go into a run folder (`Locks.runFolderWith`, [Run folders](#run-folders)).

```json
"App": {
  "OutputType": "Exe",
  "Host": { "Rid": "" },
  "Files": [
    { "Name": "app.runtimeconfig.json", "Path": "$(ProjectRoot)/locks/app/Release.files/net8.0/app/app.runtimeconfig.json", "Sha256": "..." },
    { "Name": "app.deps.json", "Path": "$(ProjectRoot)/locks/app/Release.files/net8.0/app/app.deps.json", "Sha256": "..." },
    { "Name": "app.dll.config", "Path": "$(ProjectRoot)/app/App.config", "Sha256": "..." }
  ],
  "Copy": [
    { "Id": "pkg:newtonsoft.json/13.0.3/lib/net6.0/Newtonsoft.Json.dll", "Sha256": "...", "To": "Newtonsoft.Json.dll" },
    { "Id": "pkg:sqlitepclraw.lib.e_sqlite3/2.1.6/runtimes/linux-x64/native/libe_sqlite3.so", "Sha256": "...", "To": "runtimes/linux-x64/native/libe_sqlite3.so" },
    { "Id": "project:lib", "Sha256": "", "To": "Lib.Core.dll" }
  ],
  "Content": [
    { "Id": "$(ProjectRoot)/app/data/input.txt", "Sha256": "", "To": "data/input.txt" },
    { "Id": "pkg:nunit3testadapter/4.2.1/build/netcoreapp2.1/NUnit3.TestAdapter.dll", "Sha256": "...", "To": "NUnit3.TestAdapter.dll" }
  ],
  "Related": [ ".pdb", ".xml", ".pri", ".dll.config", ".exe.config" ],
  "BindingRedirects": false
}
```

- **`Host`, the apphost** (only when msbuild uses one, `UseAppHost`), decision 3 "the apphost
  is part of the SDK":
  - no `RuntimeIdentifier`: `{ "Rid": "" }`. msbuild takes the template of the SDK's own
    runtime identifier, so the lock records only that. The build takes the template of the SDK
    the lock pins (`Evaluation.Sdk`): its `NETCoreSdkRuntimeIdentifier` and its
    `KnownAppHostPack` version for the framework, from `$(DotnetRoot)/packs`, else the package
    `Microsoft.NETCore.App.Host.<rid>` when msbuild's restore put it into the package folder (a
    framework older than the SDK's runtime). Nothing is fetched at build time: without either,
    the build fails as a broken prerequisite (*the SDK ... lacks its own apphost pack*; repair
    the SDK). Identified by the SDK version, not hashed and not in the `HERMETIC=on` path gate,
    like the SDK's compiler. On macOS the lock builds a
    Mach-O apphost, on Linux an ELF one, on Windows an `.exe`.
  - an explicit `RuntimeIdentifier`: `{ "Rid": "win-x64", "Id": "pack:microsoft.netcore.app.host.win-x64/8.0.31/runtimes/win-x64/native/apphost.exe", "Sha256": "..." }`,
    the template msbuild resolved, hashed; the restore fetches it with the lock's packages.
  - The apphost is written by the SDK's own `HostWriter.CreateAppHost`
    (`Microsoft.NET.HostModel.dll` of that SDK, loaded into the build's process): the
    application's file name patched in, the GUI subsystem for a `WinExe`, the assembly's
    version resource and icon, the macOS ad hoc signature, as `dotnet build`. An assembly of
    that name loads once per process, so **one SDK's HostModel per Xake process**: an
    application whose lock pins another SDK in the same run takes the fallback. The fallback
    is also taken when the HostModel cannot load (an SDK newer than the runtime Xake runs on:
    SDK 10 under .NET 8) or fails inside `CreateAppHost`: the template with the file name
    patched in, the GUI subsystem of a `WinExe` set (a byte of the PE header, on every host, as
    `HostWriter` does), signed on macOS; a warning names the reason and says it has no version
    resource and no icon. Named `<assembly name>` (`.exe` for a Windows template).
- **`Files`**: the `runtimeconfig.json` and `deps.json` msbuild generated, and the config file
  msbuild puts into the output (`<TargetFileName>.config`: `app.exe.config` for a .NET
  Framework application, `name.dll.config` for a test library or a .NET application), the
  first that exists of:
  1. msbuild's finished file, binding redirects included: what `GenerateBindingRedirects`
     wrote in the second run, against the stand-ins (see `deps.json` below). The import deletes
     the file before that run, so nothing from the first run or an earlier import is left in it;
  2. the file before redirects, when msbuild suggests none (for .NET Framework the SDK's
     `supportedRuntime` added, `_XakeAppConfigBeforeRedirects`);
  3. the project's own `App.config` (`AppConfig`, found by `PrepareForBuild`).

  A recorded config that names the stand-in version fails the import, naming the project and
  the config (a redirect to a stand-in). Recorded beside the lock in
  `<lock without .json>.files/<tfm>/app/` as text (LF), each with its hash; the project's own
  config that msbuild copies as it is (a .NET application's `App.config`) is named where it is,
  by its hash, and copied byte for byte. A changed file beside the lock fails the build (*does
  not have the hash the lock records; run the import again*). The repository files the
  recorded ones come from, the project's `App.config` (msbuild's `AppConfig`) and its
  `runtimeconfig.template.json` (`UserRuntimeConfig`), are in `Evaluation.Imports` with the
  project file: an edit fails the build with *changed since the import that wrote it; run the
  import again* (`LOCK_STALE=warn` makes it a warning). The `deps.json` comes from the restore's
  assets file, whose packages the lock hashes.
- **`deps.json` and the referenced projects**: msbuild reads a referenced project's versions
  from its built file (SDK 8 fails when the file is missing; SDK 10 writes `0.0.0.0`). The
  import does not build, so the second design-time run puts every output in a folder of its own
  (`OutDir`) where each referenced project's output is a stand-in: an empty library of that
  assembly name and of version `65534.65533.65532.65531` (`Import.stubAssembly`). Every version
  msbuild read from a stand-in becomes a token, `$(AssemblyVersion:<file>)` or
  `$(FileVersion:<file>)` (also in SDK 8's `<assembly>.Reference/<version>` library names), and
  the build replaces it by the version of the file it copied into the output
  (`Import.depsTokens`, `Apps.depsResolved`). When the stand-in version is still in the text
  after that (a `deps.json` layout the import does not know, from another SDK's writer), the
  import fails naming the file instead of recording the stand-in's version as msbuild's. What msbuild did not read from the file (SDK 8: a
  project's version as its assembly version) stays as written. The recorded `deps.json` is
  therefore the same whether the referenced projects were built when the import ran or not.
- **`Copy`**: msbuild's copy-local files (`ReferenceCopyLocalPaths`) in msbuild's order, each
  with its destination (`DestinationSubDirectory`): package files by identity and hash (a
  `runtimes/<rid>/` asset of a RID-agnostic application included), a referenced project's
  output as `project:<name>`. A file beside or below a referenced project's msbuild output (its
  pdb, a satellite assembly, a dependency RAR found there) is left out: msbuild finds it only
  when that project is built. The run folder takes the referenced project's output and the
  files beside it with one of the `Related` extensions that exist
  (`AllowedReferenceRelatedFileExtensions`). `_ReferenceRelatedPaths` (a package's `.xml`,
  `.pdb`) needs nothing of its own: the related files msbuild copies are in
  `ReferenceCopyLocalPaths` already.
- **`Content`**: the project's own items msbuild copies to the output, as
  `_GetCopyToOutputDirectoryItemsFromThisProject` returns them: `None`, `Content`,
  `EmbeddedResource` and `Compile` items with `CopyToOutputDirectory`, each at its `TargetPath`.
  A file of the repository by its path, unhashed: it is an input of the run folder, as a source
  is of the compile, and editing it does not make the lock stale. A package's file (the test
  adapter's `None` items) by identity, with its hash. Left out: a file under the intermediate or
  the output folder (written by a build target, such as the apphost or a coverage mapping file)
  and a missing one. A referencing project's run folder takes these items too, as msbuild's
  `GetCopyToOutputDirectoryItems` does transitively.
- **`BindingRedirects`**: `true` when msbuild adds binding redirects to the config
  (`AutoGenerateBindingRedirects` and `GenerateBindingRedirectsOutputType`). The recorded config
  is msbuild's finished one, redirects included. They come from the packages (identities and
  hashes in the lock), the reference order and the project's `App.config`, never from the
  referenced projects' built outputs (stand-ins): the same whether anything was built when the
  import ran. Limit: a conflict RAR sees only through a referenced project's compiled assembly
  (its reference to an older version of an assembly the application resolves newer) is not
  seen. On .NET (`net8.0-windows` with redirects on) msbuild's `.dll.config` can then have 1 to
  3 more redirects (framework assemblies such as `Microsoft.CSharp`); the .NET runtime ignores
  `<assemblyBinding>`. A lock written before Xake.Hermetic.Dotnet 0.5.1 has the config before
  redirects and no `.dll.config` for a .NET Framework test library: it builds as before;
  import again to record the redirects.

Not recorded: satellite assemblies (the project's own and a referenced project's; their
entries in `deps.json`), the binding redirects RAR finds only through a referenced project's
built assembly (see `BindingRedirects`), a library's `deps.json`, files a package's own build target copies (a `Copy` task
after the build, not an item: `Microsoft.Data.SqlClient.SNI`'s `CopySNIFiles`), files a build
target generates into the output (`.msCoverageSourceRootsMapping_*`,
`CoverletSourceRootsMapping_*`).

### Building a recorded lock

The build is the same as for any lock (`Locks.needAll`, `Locks.compileAs`, `Lock.compileFrom`):
the reader turns identities into paths for this machine, the restore fetches what is missing,
the runner checks the hashes, the prerequisite checks the SDK. Two points are specific:

- Sources: the files the masks name now, in the masks' order, with each placed file where
  msbuild had it (`Lock.reconstructSources`). An added `.cs` takes its place in the masks' order
  and needs no new import; a deleted one is left out. The masks are read through `getFiles`, so
  an added file reruns the compile. Without masks, `Sources` is msbuild's list as recorded.
- Staleness: the lock records the hash of each msbuild file the import read
  (`Evaluation.Imports`: the project file, `Directory.Build.props`, and so on). The build depends
  on them and compares the hashes. A changed or missing file fails the build: *the lock '...' is
  stale: '.../app.csproj' changed since the import that wrote it; run the import again*. With
  the variable `LOCK_STALE=warn` this is a warning, except under CI.
- The lock records `Recorded.Projects[].Path`, the script's `OutOf` layout at import time: a
  script that changes its layout imports again.
- `$(SourceRevisionId)` is resolved from the project's own git repository at build time: a
  lock imported in a git checkout does not build from a source tarball.
- The output name is `TargetFileName`; a script declares an `*.exe` rule beside its `*.dll` rule
  for .NET Framework applications.
- Analyzers: the replay runs msbuild's analyzer set, the same `/analyzer:` and
  `/analyzerconfig:` switches the design-time build passed (the SDK's NetAnalyzers, the
  `analysislevel_*.globalconfig`, the generated `.editorconfig`, package analyzers). Under the
  compiler server (`CSC_SERVER`, on by default) many compilations share one `VBCSCompiler`, as
  with `msbuild -m`, but a Xake build runs more of them at once. An analyzer that crashes there
  (`AD0001`, an error under `TreatWarningsAsErrors`) fails the compile intermittently; seen once
  in a large solution's clean build, not reproduced. A failure whose printed errors name a
  crashed analyzer is reported with the analyzer and compiled once more without the server: *('X') an analyzer
  crashed under the compiler server (AD0001): A.B (System.NullReferenceException); compiling once
  more without the server*. A crash without the server, or any other failure, fails as before.
  The compile adds no `/errorlog:` of its own: an error log makes csc report every diagnostic,
  hidden and suppressed ones included, a quarter to a half more compiler CPU per compile.

```fsharp
command "import" {
    let! (Filelist files) = getFiles (!! "src/*/*.csproj")
    let! _ = Import.record { Import.RecordOptions.Default with LockOf = lockOf; OutOf = outOf } [ for f in files -> f.FullName ]
    () }
command "build" { do! Locks.needAll (!! "src/*/xake.project.json") "Release" lockOf outOf }
"out/(cfg:*)/(fwk:*)/(name:*)/*.dll" ..> compile
"out/(cfg:*)/(fwk:*)/(name:*)/*.exe" ..> compile
```

There is no `update-locks` step for a recorded project: the import is that step.

### Run folders

A run folder is the folder an application or a test project runs from: what `dotnet build`
puts into its `bin/`, built only from that project's own lock closure. The compile output stays
where the script's layout puts it (`outOf`), one folder per framework when the layout is flat
(`Locks.flatOutOf`: `out/<cfg>/<fwk>`); a run folder links to it. The layout is the script's:
`Locks.runFolderWith` takes the output layout (`outOf`) and the run folder is the folder of the
rule's target.

```fsharp
command "build" { do! Locks.needAll (!! "src/*/xake.project.json") "Release" lockOf Locks.flatOutOf }
command "deploy" {
    let! folders = Locks.runFoldersWith [] (!! "src/*/xake.project.json") "Release" lockOf Locks.runOf
    do! need folders }
"run/(cfg:*)/(fwk:*)/(name:*)/.xake-run.txt" ..> recipe {
    let! cfg = getRuleMatch "cfg"
    let! fwk = getRuleMatch "fwk"
    let! name = getRuleMatch "name"
    do! Locks.runFolderWith [] RunFolder.Clone lockOf Locks.flatOutOf cfg fwk name }
"out/(cfg:*)/(fwk:*)/(asm:*).dll" ..> compile      // finds the project by the assembly name
"out/(cfg:*)/(fwk:*)/(asm:*).exe" ..> compile
```

What a run folder holds, step by step:

1. The root project's own files. They are never replaced:
   - its compile outputs in `outOf cfg fwk name`: the assembly (`TargetFileName`), the `.pdb`,
     the `.xml` when the lock asks for doc, `Files` (runtimeconfig, deps.json, config), the
     apphost;
   - its `Content` items;
   - its `Copy` files: the package runtime assets at the versions its own restore resolved.
2. Then the closure of project references, breadth first in msbuild's order. A recorded entry
   gives each reference's configuration and framework (`Recorded.Projects`); another entry uses
   `refConfig` and `nearestFramework`. For each referenced project:
   - its assembly in `outOf`, and the files beside it with the root's `Related` extensions;
   - for a referenced application, its `runtimeconfig.json`, `deps.json` and apphost (the SDK's
     items for referencing projects);
   - its `Content` items;
   - its `Copy` files that are not package files. A package's file in a referenced project's
     list is the version that project's restore resolved; msbuild never copies it here. This
     holds under PackageReference transitivity, where the root's restore has the referenced
     project's packages at its own versions. A package the root's restore lacks
     (`PrivateAssets="all"` or `ExcludeAssets` on a runtime package of a referenced .NET
     Framework library, whose `bin/` msbuild's RAR copies from) is not placed either, but the
     build warns naming the file (*is not placed: the root's restore lacks that package*); add
     the package to the root project, or place the file from the script.
3. One source per path (paths compare ignoring case). The first source is kept. In the closure
   a higher assembly version replaces a lower one (the assembly version, not the package
   version). Each dropped source with other content goes to `.xake-run.conflicts.txt` beside the
   manifest, and the build warns.
4. Each file is placed by `RunFolder.Mode`: `Clone` (APFS `clonefile`, Linux `FICLONE` on
   btrfs/XFS, else a copy; on Windows `CopyFile` clones on ReFS and Dev Drive by itself), `Link`
   (a hard link, else a clone, else a copy) or `Copy`. `Locks.runFolder` places by `Clone`; hard
   links are the explicit opt-in (see below). Every path, planned or read from the previous
   manifest, must stay inside the folder: a rooted path, a `..` step or a folder on the way that
   is a symbolic link or junction fails the rule naming the path and where it came from. A
   placed file is never written into: a changed one is unlinked and placed again, but only when
   it still is what a run placed (a link to its source, or the stamp the manifest records); a
   file changed since (by a test, by hand) is left alone and listed in the conflicts report. A
   file at a planned path that no run placed is kept when it has other content, and is listed
   in the conflicts report.
5. The manifest `.xake-run.txt` is the rule's target: one line per file (mode, path, source,
   and the source's stamp: size and write time). A later run compares with it. An entry whose
   file still is its source is kept (a link: the same file identity, `dev`/`ino` or volume and
   file index; a clone or a copy: the same size and write time). A path that is no longer
   planned is removed, with the folders it leaves empty, only when its file is unchanged since a
   run placed it; else it stays, listed in the conflicts report. Paths compare as the file
   system does: ignoring case on macOS and Windows, so a case-only rename keeps the file. The
   manifest is written only when its text changes: a no-op touches nothing, and a recompiled
   source (a new stamp, also for a hard link whose file csc rewrote in place) rewrites it, so a
   rule that needs the manifest (tests, a pack over the folder) runs again. A manifest written
   before stamps (0.4.2) is read as before.

Dependencies: the locks the rule reads; the closure's assemblies as the compile rules' targets,
needed before anything beside them is looked at (so `deploy` on an unbuilt tree builds first and
places the whole folder; Xake 3.8 records them with their change stamps (`ArtifactDep2`), so an
assembly rebuilt by an earlier run brings the folder up to date in the next one); the package
files and the items. The side outputs (pdb, xml, runtime
files, apphost) change with their assembly and are not needed through the compile rule. So a
.NET Framework application's `.xml` is never asked for as `<asm>.dll` (S64).

Hard links (`RunFolder.Link`) cost no copy at all, which is why they are offered, but they share
the file with its source:

- **Windows:** file locks apply to the file, not the name. A test or an application running from
  its run folder holds `out/X.dll` open, and the next compile of `X` fails with a sharing
  violation, which msbuild's copied `bin/` never does.
- **Unix:** rewriting a memory-mapped assembly in place under a running process can crash it.
- **Everywhere:** a recompile that writes its output in place (csc does) is visible in every run
  folder at once, and a test that writes into a file of its run folder writes into the source
  (the NuGet cache, a repository `Content` file).

`Clone` shares the blocks on APFS, btrfs/XFS and ReFS without sharing the file.

Copy items are the import's expansion: a wildcard item (`<None Include="TestFiles/**"
CopyToOutputDirectory=...>`) is recorded as the files it matched at import. A file added under
it later is not placed until the next import; a file deleted or renamed fails the rule
(*missing ...; run the import again*).

A script's check target uses `Locks.check locks` (fails naming every stale lock and its changed
files) or `Locks.staleWith extraRoots locks` (the stale locks and their messages, no failure),
the check `Locks.compileAs` runs before a compile. `Locks.staleOf entry` is the pure part: the
files of one entry's `Evaluation.Imports` that are missing or changed. A description is not
covered: the build reads the masks from the lock, so a hand edit of `xake.project.json` shows
only on the next import.

```fsharp
let locks () = [ for name in projects -> lockOf name "Release" ]
rules [
    // "are the committed locks still msbuild's answer?" -- before a build, in CI
    "check" => Locks.check (locks ())
    // or report and go on
    "check-report" => recipe {
        let! stale = Locks.staleWith [ "$(Shared)", "../shared" ] (locks ())
        for lock, messages in stale do
            do! trace Warning "%s: %s" lock (String.concat "; " messages)
    }
]
```

`Locks.runFoldersWith` lists the manifests of every project of a fileset whose entry runs
(`Locks.runs`: `OutputType` `Exe`/`WinExe`, generated runtime files, or `IsTestProject`), in
each framework of its lock.

Known differences from `dotnet build`'s `bin/` (checked on the trial product, 77 run folders):
satellite assemblies; binding redirects seen only through a referenced project's built assembly
(a .NET `.dll.config` with redirects on has 1 to 3 fewer than msbuild's; before 0.5.1 every
.NET Framework config differed by its `<assemblyBinding>` lines);
files a package's build target copies with a `Copy` task; coverage mapping files a build target
writes into the output.

### What is checked

- Import, build, import again: the same lock (also after `dotnet build` filled `bin/` and `obj/`).
  RAR does not take facades from the referenced projects' outputs here: the reference metadata
  (`TargetFrameworkIdentifier` of the project reference) is enough.
- The lock text holds no absolute path and no string of the importing machine.
- The outputs: the same files as `dotnet build` (assembly, `.pdb`, `.xml`), with the same types,
  methods, manifest resources, assembly references and version. Bytes are not a goal: msbuild
  writes some generated sources with CRLF on every OS, the lock records LF.
- An application or a test project: its run folder holds the same files as `dotnet build`'s
  `bin/`, but what is listed as not recorded above; `runtimeconfig.json`, `deps.json` and a
  .NET Framework `.exe.config`/`.dll.config` (binding redirects included) equal to msbuild's; it runs from its run folder (`dotnet app.dll`, the apphost on this OS,
  `dotnet test tests.dll`).

## F# entries

An entry holds `Lock.Compilation.Csc c` or `Lock.Compilation.Fsc f`. The two records have the
same fields with the same meaning (`Name`, `Framework`, `Directory`, `Options`, `Defines`,
`Sources`, `Generated`, `Resources`, `Dependencies`); they differ in the argument dialect
(`CscArgs` and `FscArgs`) and the runner (`Csc.run` with `RunOptions`, `Fsc.run` with
`FscRunOptions`). So the file has one shape for both, and the case is the compiler's
`Dependencies.Compiler.Tool`: `"fsc"` reads back as `Fsc`, anything else as `Csc` (every lock
written before 0.2 says `"csc"`, and reads as before; a C# entry is written byte for byte as
in 0.1). An F# entry:

```json
"Compilation": {
  "Directory": "$(ProjectRoot)/App",
  "Options": [
    "-o:$(ProjectRoot)/App/obj/xake/net10.0/App.dll",
    "--debug:portable",
    "--noframework",
    "@Defines",
    "--doc:$(ProjectRoot)/App/obj/xake/net10.0/App.xml",
    "--optimize+",
    "@References",
    "--target:library",
    "--targetprofile:netcore",
    "--nocopyfsharpcore",
    "--deterministic+",
    "--simpleresolution",
    "@Sources"
  ],
  "Defines": [ "TRACE", "RELEASE", "NET", ... ],
  "Sources": [ "$(ProjectRoot)/App/obj/xake/net10.0/App.AssemblyInfo.fs", ..., "$(ProjectRoot)/App/Program.fs" ],
  "Generated": { ... }, "Resources": { }
},
"Dependencies": {
  "Compiler": { "Tool": "fsc", "Path": "$(DotnetRoot)/sdk/10.0.401/FSharp/fsc.dll", "Sha256": "", "Version": "15.2.401-servicing.26423.113" },
  "References": [
    { "Path": "$(NuGetPackageRoot)/fsharp.core/.../lib/netstandard2.1/FSharp.Core.dll", "Sha256": "..." },
    ...
  ],
  "Analyzers": [

  ],
  "Packages": [ ... ]
}
```

- `Options` are fsc's own spelling (`-o:`, `--optimize+`), exactly as msbuild or `Fsc.ofSettings`
  gave them; the markers are `@Sources`, `@References` (one `-r:path` per reference) and
  `@Defines` (one `--define:X` per symbol). There is no `@Analyzers`: fsc has no analyzers.
- `Sources` are in compile order, which for F# is semantic.
- `Analyzers` is always written, empty, exactly as for a C# entry without analyzers; an F#
  entry that lists one is refused when read.
- `FSharp.Core` is an ordinary reference, hashed like any other.
- The compiler is the SDK's own `fsc.dll`: no package carries a current F# compiler, so the only
  way to fix it is to fix the SDK. Pin it (`global.json` with `rollForward: disable`): the lock
  then records the SDK as a prerequisite ([below](#prerequisites)), checked before every
  replay. Without the pin the recording warns, and a lock fails, naming the SDK, on every
  machine that does not have that SDK. Its `Sha256` is empty: the SDK version is its identity
  ([below](#the-compiler-a-lock-names)).

`entry.Csc` and `entry.Fsc` return the record of their case and fail with `'<name>' is an F#
entry; use entry.Compilation` (or the C# counterpart) for the other; code that handles both
matches on `entry.Compilation` or uses the common members (`entry.Name`, `entry.Framework`,
`entry.Dependencies`, `entry.Args`, `entry.Output`). `Lock.diff` reports a change of tool as
`~ Compiler.Tool: csc -> fsc`. The SBOM names the compiler by its tool (`tool:csc`, `tool:fsc`).

## Prerequisites

A prerequisite is an input the environment must provide by itself: no restore step fetches it,
and a replay checks it before doing any work, with a message that says what to install. There
is one kind, the **.NET SDK at an exact version** (`"dotnet-sdk"`), covering the paths under
`$(DotnetRoot)/sdk/<v>/` -- the SDK's `fsc.dll`, the compiler and analyzers of an imported
project (and every file of a recorded one under that folder). A file under
`$(DotnetRoot)/packs/` is not covered by it; a recorded entry names such a file by a `pack:`
identity and its hash.

**A recorded entry** (`Import.record`) has `dotnet-sdk <NETCoreSdkVersion>`, the SDK that
recorded it, **when it names a file of that SDK** (since 0.6; before, always): an `sdk:`
identity or any other path under `$(DotnetRoot)/sdk/<v>/` of its command line (the compiler,
the SDK's analyzers, an `/analyzerconfig:` of the SDK, shims) or of its application (an
apphost: the SDK's template, `SdkHost`, or a pack's, `PackHost` for an explicit
`RuntimeIdentifier`, both written through the SDK's `HostModel`, which adds the version
resource and the icon; without it Xake's fallback writes a smaller file, 119 296 bytes against
msbuild's 194 048 in a probe). The prerequisite follows the content:
`Lock.withRecordedPrerequisites` derives it from the paths, in `Import.record` and again in
`Lock.rehash`. This can be stricter than `global.json` (the trial project: `8.0.100
rollForward:latestFeature` with the prerequisite `8.0.425`); the import warns then. Another SDK
means a new import. For a recorded entry the prerequisite covers every file under
`$(DotnetRoot)/sdk/<v>/`, references included, and a file under `$(DotnetRoot)/packs/` is
accepted by its hash (a `pack:` identity). With no `global.json`, `Pin` is `""` and a message
names "the import" as the source.

A C# project that takes its compiler and analyzers from packages names nothing under `sdk/`, so
its lock has no prerequisite and replays on a machine with any SDK version (the replay starts
no msbuild; the `dotnet` host on the `PATH` runs the toolset's `csc.dll`):

```xml
<PropertyGroup><RoslynCompilerType>Toolset</RoslynCompilerType></PropertyGroup>
<ItemGroup>
  <PackageReference Include="Microsoft.Net.Compilers.Toolset" Version="<v>" PrivateAssets="all" />
  <PackageReference Include="Microsoft.CodeAnalysis.NetAnalyzers" Version="<v>" PrivateAssets="all" />
</ItemGroup>
```

Without `RoslynCompilerType=Toolset` the SDK reverts to its own compiler. The analyzers package
turns the SDK's copy off by setting `EnableNETAnalyzers=false` in its props: a project that sets
`EnableNETAnalyzers` to `true` in its body (or in a `.targets`) gets both copies, and the SDK's
analyzers bring the prerequisite back. The toolset package of a Roslyn version is not the
SDK's own build of it (SDK 10.0.402 bundles `5.9.0-1.26505.111`, the package 5.9.0 is
`5.9.0-1.26357.3`), so moving a project onto the packages is a compiler change: compare the
output once. F# has no compiler package: an F# entry keeps the prerequisite (fsc is under
`sdk/<v>/FSharp/`), and a lock mixing C# and F# keeps it for the F# entries.

For the other entries, **written at record time**, when the entry depends on the SDK:
`Lock.record`, the first `Lock.build` / `csc { lock }` / `fsc { lock }` (the recording branch of
`buildWith`), and `Project.import`. The signal is a tokenized path under `$(DotnetRoot)/sdk/<v>/`: the compiler
for a composed entry, the compiler and the analyzers for an imported one. Then:

- the `global.json` found from the project root upwards (for an import: from the project's
  directory, `Evaluation.SdkPin`) pins exactly `<v>` (`"version": "<v>", "rollForward":
  "disable"`): the entry gets `{ "Kind": "dotnet-sdk", "Version": "<v>", "Pin":
  "$(ProjectRoot)/global.json" }`;
- otherwise no prerequisite, and a warning:
  `'<name>': depends on the .NET SDK <v> but global.json does not pin it exactly; the lock will
  only build where that SDK is installed`.

A composed csc with the toolset compiler (`toolset`, `CSC_TOOLSET`) names nothing under `sdk/`,
so it has none. The same derivation runs on the resolved side of `buildWith` and `verify`, so a
pin loosened (or tightened) after recording is a difference:
`- Prerequisite dotnet-sdk 10.0.401 ($(ProjectRoot)/global.json)`.

**On disk** a key under `Dependencies`, after `Packages`, written **only when the list is not
empty**. Read back when present.

```json
"Dependencies": {
  "Compiler": { "Tool": "fsc", "Path": "$(DotnetRoot)/sdk/10.0.401/FSharp/fsc.dll", ... },
  "References": [ ... ],
  "Analyzers": [ ],
  "Packages": [ ... ],
  "Prerequisites": [
    { "Kind": "dotnet-sdk", "Version": "10.0.401", "Pin": "$(ProjectRoot)/global.json" }
  ]
}
```

`Pin` is kept tokenized in memory too: it says where the requirement comes from and is never
opened by a replay. `Lock.mapText` applies to it; `mapPaths` and `rehash` leave it alone.
`Lock.diff` reports `+`/`-` by kind and version and `~` for a moved pin.

**Checked before a replay** (`Lock.compileWith`, step 0 below): an entry with a `dotnet-sdk`
prerequisite whose `<DotnetRoot>/sdk/<v>` folder does not exist fails before anything is
restored:

```
'<name>': the lock needs the .NET SDK <v> (prerequisite from $(ProjectRoot)/global.json), which
is not installed under '<root>'; install it (dotnet-install --version <v> --install-dir <root>)
```

`HERMETIC=on` builds on this: under it a lock may name nothing outside `$(ProjectRoot)` and
`$(NuGetPackageRoot)` that a prerequisite does not cover ([Hermetic mode](#hermetic-mode)).

Tests: `RecordTests.fs` (`a recorded entry's SDK prerequisite follows its content`: the SDK
compiler, the package compiler and analyzers, an SDK analyzer config, an apphost of the SDK or a pack, the
probe under an empty .NET root), `SdkFreeImportTests.fs` (a net472 + netstandard2.0 library on
the toolset compiler and package analyzers records no prerequisite and builds against a .NET
root with empty `sdk/` and `packs/` into `dotnet build`'s bytes), `LockDiffTests.fs` (`Lock prerequisites`: the key written only when not empty and the
golden lock unchanged, the round trip, `diff`, `mapText`/`mapPaths`/`rehash`, the derivation)
and `FscLockTests.fs` (an exact pin records the prerequisite, the replay compiles, a loosened
pin is a difference; no pin, no prerequisite and the warning; a missing SDK fails the replay
with the install message).

## Hermetic mode

`HERMETIC=on` (the base's script variable: `var "HERMETIC" "on"`, or `-d HERMETIC=on`; see
[hermetic-mode.md](hermetic-mode.md)) makes completeness a checked property. The base gates the
*resolve* (`Csc.ofSettings`/`Fsc.ofSettings`, messages 1-8); this package gates the *lock*:

> A lock entry names no path outside `$(ProjectRoot)` and `$(NuGetPackageRoot)`, except paths
> covered by a prerequisite the entry declares.

The comparison is `HermeticMode.check` over the lock's tokenized paths (`Lock.hermeticViolations`):
the project root and any extra root a script declared (`Roots.make`, `ImportOptions.Roots`)
count as the checkout; `$(NuGetPackageRoot)` is the build's package folder; a `dotnet-sdk <v>`
prerequisite covers `$(DotnetRoot)/sdk/<v>/` for fsc's compiler, and for an imported entry's
compiler and analyzers -- never a reference, never `$(DotnetRoot)/packs/`, and never a composed
csc's compiler (the toolset replaces it: `CSC_TOOLSET`).

| Where | Under `HERMETIC=on` |
|---|---|
| `Lock.record`, the recording branch of `Lock.build`/`buildWith` (`csc { lock }`, `fsc { lock }`) | the lock is not written when the package folder is not the script variable `NUGET_PACKAGES` (the base's message 2) or when a path breaks the invariant (message 9, one line per path) |
| `Lock.compileWith`, `Lock.compile`, the replay in `buildWith` | after the prerequisite check, a lock that names a path outside the roots fails before any restore (message 10) |
| `Project.import` | the SDK not pinned exactly is an error (message 11; a warning with the mode off); pinned but not the SDK msbuild ran is message 7; then the gate of `record` over every entry. Nothing is written when anything fails |

The messages (`<lock>` as the caller wrote it; `compileWith` without a lock path says "the lock"):

```
'<name>': HERMETIC=on: refusing to write the lock '<lock>': <tokenized path> is outside $(ProjectRoot) and $(NuGetPackageRoot), and no prerequisite covers it
'<name>': HERMETIC=on: the lock '<lock>' names <tokenized path>, outside $(ProjectRoot) and $(NuGetPackageRoot) and not covered by a prerequisite; re-record it with HERMETIC=on
'<name>': HERMETIC=on: the project's SDK is not pinned (<pin>); the import's compiler and analyzers come from the SDK, so pin it in global.json: { "sdk": { "version": "<v>", "rollForward": "disable" } }
```

These are hard failures (`FailOnError = false` does not turn them into warnings), like the base's
gate. With the mode off nothing changes: the locks, the warnings and the replay are as before;
only the variable `HERMETIC` is read (and so is a dependency).

`Options.Default` follows the build: `Restore.Options.Default` (`PackageRoot = None`, `Enabled =
true`) means the build's package folder (`DotNetFwk.packageRoot ()`, the script variable
`NUGET_PACKAGES` first) and fetching unless `NUGET_FETCH=off` (`DotNetFwk.fetchEnabled ()`),
applied by `Restore.resolve` in `Restore.ensure`, `Lock.compileWith` and `Lock.buildWith`.
`Enabled = false` (`nofetch`) turns fetching off whatever `NUGET_FETCH` says. And the lock's
roots inside a recipe (`Roots.current`, `currentWith`) put `$(NuGetPackageRoot)` at that same
folder, so a lock recorded with `var "NUGET_PACKAGES" ".nuget/packages"` names
`$(NuGetPackageRoot)/...`, not the absolute path. `Project.import` hands the folder to msbuild
as the environment variable `NUGET_PACKAGES`, so the imported packages are there too.

A minimal hermetic script ([../hermetic-guide.md](../hermetic-guide.md#hermetic-mode)):

```fsharp
do xakeScript {
    var "HERMETIC" "on"
    var "NUGET_PACKAGES" ".nuget/packages"
    var "CSC_TOOLSET" "4.12.0"
    var "NETCORE_REF_VERSION" "8.0.0"
    rules [
        "out/app.dll" ..> csc { targetfwk "net8.0"; src !!"src/*.cs"; lock "locks/app.json" }
    ]
}
```

Tests: `HermeticLockTests.fs` (a csc lock records with `NUGET_PACKAGES`, `CSC_TOOLSET`,
`NETCORE_REF_VERSION` and names nothing under `$(DotnetRoot)`; the SDK compiler is refused with
message 9 and the package folder with message 2, and records with the mode off; an fsc lock under
an exact pin records the prerequisite and replays; a lock naming `$(DotnetRoot)/packs/` is not
replayed, message 10; an unpinned import fails with message 11 and writes nothing) and
`LockDiffTests.fs` (`Lock runtimeconfig and hermetic rule`: the coverage rules of
`hermeticViolations`).

## The compiler a lock names

The lock records what ran in `Dependencies.Compiler`: `Path` and `Sha256` name the compiler
file, `Version` is the compiler's own product version read from the file (`4.12.0-3.24572.7`
for the toolset package, `4.11.0-3.25569.22` for SDK 8.0.425's; for the SDK's native `csc`
launcher the `csc.dll` next to it is read). The SDK that evaluated the project is
`Evaluation.Sdk`, empty for a composed compilation. For a composed compilation the compiler
comes from `cscpath`, `toolset` or the framework (see [../csc-syntax.md](../csc-syntax.md));
`Lock.compile` restores a toolset package the lock names when it is not on this machine yet,
through its restore step (`Restore.ensure`, step 1 below), which treats the compiler as one
package among the entry's references and analyzers.

**A compiler of the .NET SDK has no hash.** The SDK's `fsc.dll` and Roslyn `csc.dll` (any
compiler under `$(DotnetRoot)/sdk/<v>/`, `Lock.isSdkCompiler`) are ReadyToRun images compiled
for one OS and architecture: the same SDK version has different bytes on macOS arm64 and on
linux x64, so a hash would tie the lock to one platform. Their identity is the SDK version: the
`<v>` in the path, and the `dotnet-sdk <v>` prerequisite when `global.json` pins it exactly
([Prerequisites](#prerequisites)). Recording writes `"Sha256": ""` for them (`Lock.rehash`,
`Project.import`), and a replay does not check one (`Lock.compileWith` clears it,
`Lock.withoutSdkCompilerHash`, before `Csc.run`/`Fsc.run`); what is checked is that the SDK is
installed -- the prerequisite, then the compiler's path (step 2 below). A lock written by 0.2
with a hash there reads and formats unchanged; the hash is ignored, so the lock builds on any
OS with that SDK. A compiler from a package (`Microsoft.Net.Compilers.Toolset`, `CSC_TOOLSET`)
or a `cscpath` outside the SDK keeps its hash and is checked as before. The SBOM's `tool:fsc` /
`tool:csc` component then carries no hash for an SDK compiler.

### Import: the project decides, msbuild answers

`Project.import` does not choose a source. `parseImport` reads which compiler msbuild wired up:

- The `Microsoft.Net.Compilers.Toolset` package does **not** set `CscToolPath`/`CscToolExe`.
  It redirects `CSharpCoreTargetsPath` (and the `Csc` task assembly) to its own `tasks/netcore/`
  directory; the task's tool path then defaults to the `bincore` next to that targets file.
- So the compiler is `<dir of CSharpCoreTargetsPath>/bincore/csc.dll`: the SDK's
  `Roslyn/bincore/csc.dll` for an unpinned project, the package's for a pinned one.
  `RoslynTargetsPath` always reports the SDK's Roslyn and is only the fallback.
- **A project referencing the package also needs `<RoslynCompilerType>Toolset</RoslynCompilerType>`**
  (csproj or `-p:`). Without it, on SDK 9 and later, `Microsoft.NET.Sdk.BeforeCommon.targets`
  silently sets `CSharpCoreTargetsPath` back to the SDK's, and the package has no effect.

## Replaying a lock: `Lock.compile`

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

do! Lock.compile entry                              // entry : Lock.Entry
do! Lock.compileWith { Lock.Options.Default with Run = { RunOptions.Default with FailOnError = false } } entry
do! Csc.run RunOptions.Default entry.Csc            // the runner alone: no restore, no revision token
do! Fsc.run FscRunOptions.Default entry.Fsc         // the same for an F# entry
```

`Lock.compile` hands a lock entry's `Csc` to the runner after the two hermetic steps that must
happen first (restore, revision token; below), with no extra env vars and no temp files: the
`Csc`'s own `Args` is the whole compilation. It is an entry point of its own, not a mode of
`csc {}`: a setting inside the record would let `csc { fromlock p; src !!"*.cs" }` compile while
silently ignoring `src` and every other composition setting. What applies to every entry point is
`RunOptions = { FailOnError; CscPath; Server; Environment }` -- how the runner behaves, not what it
compiles (`Server` is whether csc runs as a thin client of the Roslyn compiler server,
[../csc-server.md](../csc-server.md); `Environment` is the compiler process's env vars). `Csc.compile`
builds one with `Csc.runOptions settings` (the settings' `FailOnError`/`CscPath`, the server
resolved through `CompilerServer.resolve`, the target framework's `EnvVars`).
`Lock.compileWith` takes a `Lock.Options = { Run: RunOptions; FscRun: FscRunOptions; Restore:
Restore.Options }` and hands a C# entry to `Csc.run options.Run`, an F# entry to `Fsc.run
options.FscRun` (`FscRunOptions = { FailOnError; FscPath; Environment }`); the failures of the
steps below follow the `FailOnError` of the entry's own runner options. `Lock.compile` uses
`Lock.Options.Default` with the csc server resolved (`CSC_SERVER`, then `XAKE_CSC_SERVER`). Signatures: `Csc.run : RunOptions -> Csc -> Recipe<ExecContext, unit>`,
`Lock.compile : Lock.Entry -> Recipe<ExecContext, unit>`, `Lock.compileWith : Lock.Options ->
Lock.Entry -> Recipe<ExecContext, unit>`. The module is `Csc` (`[<ModuleSuffix>]` next to the
record `Csc`); the 3.3 function `Csc settings` no longer exists, `Csc.compile settings` replaces it.

`Lock.compileWith` does, before handing the entry to `Csc.run` or `Fsc.run` (`Lock.fs`):

0. **Checks the prerequisites** ([Prerequisites](#prerequisites)): a `dotnet-sdk <v>` whose SDK
   is not installed under the .NET root the lock is read against (a script's extra root
   `$(DotnetRoot)`, else this machine's) fails here, naming the version, the pin and the
   `dotnet-install` command, before any restore. Under `HERMETIC=on` a lock that names a path
   outside the roots then fails with message 10 ([Hermetic mode](#hermetic-mode)). The restore
   options are applied as the build says (`Restore.resolve`: the build's package folder,
   `NUGET_FETCH`).
1. **Restores what the lock names and this machine lacks** (`Restore.ensure options.Restore
   (Lock.restoreRequest [entry])`, see [restore.md](restore.md)): the compiler when it lives in a
   `Microsoft.Net.Compilers.Toolset`-shaped package, and every reference and analyzer under the
   package folder, in one `dotnet restore`. With nothing missing (the normal case) this is one
   `File.Exists` per path and no process. Any problem it reports fails with `('<name>') restoring
   the packages the lock names failed:` and the list.
2. **Explains a compiler that is still missing** (`ensureCompilerAvailable`; it restores
   nothing itself, step 1 did that):
   - already on disk (`Dependencies.Compiler.Path` exists) -- nothing to do.
   - under the package folder (`Restore.packageRoot options.Restore`) -- a toolset-shaped package
     the restore did not, or was not allowed to, provide: `'<name>': the compiler <path> is not
     available and restoring <id> <version> did not provide it`. A hash mismatch *after* a
     successful restore is left to the hash check below -- it means a different package build,
     not a missing one.
   - under `$(DotnetRoot)/sdk/<version>/` -- an SDK this machine does not have; nothing to
     restore, so this fails immediately: `'<name>': the lock names the compiler of SDK
     <version> (<path>), which is not installed; install that SDK or re-import with the
     installed one` (or, under `$(DotnetRoot)` but not `sdk/`, a generic "not installed"
     message naming the path).
   - anywhere else -- `'<name>': the compiler <path> named by the lock does not exist`.

   Every failure here goes through the same `trace Error` + `FailOnError`-gated `failwith` shape
   (`failStep`) as the hash-mismatch check and `Tool.failOnExitCode`.
3. **Resolves `$(SourceRevisionId)`**: the lock never
   carries a commit sha itself (see `Generated` and `Git.tokenize` below) -- when
   `Generated`'s content or the compilation's `Args` carries the literal token `$(SourceRevisionId)`,
   it is replaced everywhere `Csc.mapText` (`Fsc.mapText`) reaches (`Generated` content, `Options`, `Defines`)
   with `Git.headSha c.Directory` (walking up from the
   project's own directory for a `.git`; no `git` executable). No token anywhere -- nothing
   happens, the composed mode included, since it never populates `Generated`. A token present but no
   repository found (or `HEAD` unresolvable) fails with `'<name>': the lock needs
   $(SourceRevisionId) but no git repository was found at or above '<dir>' -- a lock that needs
   a revision must be compiled in a repository`, the same `trace Error` + `FailOnError` shape as
   the other checks here.

`Lock.build`/`Lock.buildWith` (see "Locking composed settings") do the lock gate first and then
this same `compileWith`.

## Where a `Lock.Entry` comes from

`Project.import` (`src/hermetic/Project.fs`) runs an msbuild design-time build per project and
target framework -- `ProvideCommandLineArgs`/`SkipCompilerExecution`, so the compiler reports
its command line instead of running -- and writes **one lock file per variant, holding every
framework**. A csproj and an fsproj are imported the same way and may share a lock: msbuild
answers with `CscCommandLineArgs` for C# and `FscCommandLineArgs` for F# (the F# targets' `Fsc`
task honours the same two properties); the import asks for both and parses whichever came back
with `CscArgs`/`Csc.ofArgs` or `FscArgs`/`Fsc.ofArgs`. The F# compiler is msbuild's
`DotnetFscCompilerPath`, the SDK's `fsc.dll`. Imported and compiled from the lock, a C# library
and an F# application referencing it come out byte-identical to `dotnet build` into the same
intermediate directory (`FsprojImportTests.fs`). `ImportOptions`:

| Field | Meaning |
|---|---|
| `Projects` | project files; all land in one lock |
| `Frameworks` | the target frameworks to import each project for; all of them land in that same lock, one `Lock.Entry` per (project, framework). A project gets entries only for the ones it declares -- see below |
| `Configuration` | msbuild `Configuration`, default `Release` |
| `Properties` | extra `-p:` properties, e.g. `["Brand", "Acme"]` |
| `Variant` | names the `obj/xake/<framework>/<variant>/` subtree; keeps distinct property sets from overwriting each other's generated files |
| `Output` | the lock file to write |
| `Roots` | extra `(token, path)` roots to tokenize against, beyond the built-in three (a relative path is taken against the project root; a built-in token's name replaces that built-in); default `[]` |

**One restore, then a design-time build per framework.** Three msbuild phases per
project, all of them inside that project's `withProjectLock`:

1. **`-t:Restore`, with no `TargetFramework` and with `-p:RestoreRecursive=false`.** Without
   `TargetFramework` NuGet resolves *every* target of a multi-targeted project into the one
   `obj/project.assets.json`; with `RestoreRecursive=false` it stops walking the project graph,
   so a project's restore no longer rewrites the assets files of the projects it references.
2. **A design-time build per framework**, `-p:TargetFramework=<f>` and **no** `-restore` -- the
   restore above already produced everything it reads. `IntermediateOutputPath` stays
   per (framework, variant).
3. **A `-pp` preprocess per framework**, for the evaluation's import list.

`Frameworks` is the matrix asked for, not a claim that every project has every leg of it, and
a repository rarely multi-targets all its projects the same way. The restore run -- the one
msbuild phase here that is not per framework -- therefore also reports the project's own
`TargetFrameworks` (or single `TargetFramework`), and the import builds only the intersection
(`Project.frameworksToImport`, pure and unit-tested), tracing each framework it skips. Asking
msbuild for a leg the project never declared would build one that was never configured,
against a restore that has no target for it -- the same `NETSDK1005` this design exists to
remove. A project reporting no framework list at all is "msbuild said nothing", not "nothing
declared": everything requested is kept.

*Why.* One lock per (framework, variant) meant the same multi-targeted project was imported
once per framework, each pass passing a global `-p:TargetFramework=X` on a `-restore`; NuGet
then wrote an assets file with only X's target, and `-restore`'s walk of the project graph
rewrote the *referenced* projects' assets files too -- which a per-project lock cannot cover.
Concurrent imports failed with `NETSDK1005 ... doesn't have a target for '<fwk>'` or silently
recorded an entry with `packages 0` (measured while verifying the lock against a real multi-project solution). The fix is structural: the
framework matrix of one variant is one import, so the two frameworks cannot race at all, and
`RestoreRecursive=false` keeps one project's restore out of another's obj. Both were verified
by hand on the repo-a fixture -- a restore without `TargetFramework` yields
`['.NETFramework,Version=v4.7.2', '.NETStandard,Version=v2.0']` where one with it yields a
single target, and with `RestoreRecursive=false` only the restored project's own assets file
appears. The `withProjectLock` `Resource` stays, now held for the whole project (restore plus
every framework's build), because two *brands* still restore into the same
`obj/project.assets.json`; what it no longer has to cover is the cross-project rewrite.
`Nuget.readAssets` now **fails** when the assets file has no target for the framework asked
for, rather than returning an empty graph -- an SBOM without its package components and
nothing saying so was the worst half of that defect.

**Extra roots.** The three built-in roots (`$(NuGetPackageRoot)`, `$(ProjectRoot)` = the
build's project root, `$(DotnetRoot)`) tokenize everything under the package cache, the checkout being
imported, and the SDK -- but a cross-repo `ProjectReference` (page's `LocalBuild=true` pointing
at a sibling `repo-a` checkout) resolves to paths under neither, and would
otherwise land in the lock untokenized and machine-specific. `ImportOptions.Roots` declares one
extra token per sibling repository (the decision: no shared parent root, since siblings can move
independently and a shared root would tokenize more than intended) -- e.g. `Roots = [
"$(RepoARoot)", "/abs/path/to/repo-a" ]`. `Project.import` combines them
with the built-in three via `Roots.make projectRoot extra` (inside the import:
`Roots.currentWith options.Roots`), which validates each token is well-formed (`$(Name)`) --
failing early rather than writing a lock that silently didn't tokenize -- takes a relative path
against the project root (not the process's cwd), lets an extra token of a built-in's name
*replace* that built-in (how `Roots.packageRootOverride dir` points `$(NuGetPackageRoot)` at a
build's own package folder; it used to be refused), and keeps the longest-root-first order
`Roots.builtin` already relies on. A script reading such a lock back must pass the same roots:
`Lock.loadWith extraRoots path` inside a recipe, or `Lock.read (Roots.make projectRoot
extra) path` outside one. `Lock.load`/`save` use the built-in three only.

**Where the project root comes from (review §2.5).** `$(ProjectRoot)` is the engine's
`ExecOptions.ProjectRoot` -- what `need`, `getFiles` and rule matching already resolve against --
not the process's current directory. The `Roots` module holds the whole thing: `nugetRoot ()`,
`dotnetRoot ()`, `nugetPackageRootToken`, `builtinTokens`, `packageRootOverride dir`, the pure `builtin projectRoot` / `make projectRoot extra`,
and the recipes `current` / `currentWith extra` that read the root from `getCtxOptions()`. The
recipe-level lock entry points (`Lock.load`/`loadWith`/`save`/`saveWith`) are recipes for
exactly this reason; the pure `format`/`parse`/`read` still take a roots list. The json reader
lives in its own `Json` module.

`Lock.Entry`, one per project, is `{ Compilation; Evaluation; Packages }` : the compilation (`Csc` or `Fsc`) holds identity (`Name`, `Framework`), what is compiled and its `Dependencies`; `Evaluation` and `Packages` are the provenance only the lock has. The three sections of the file  are:

- `Name` -- `AssemblyName`
- `Framework` -- the target framework this compilation is for. With every framework in one
  lock, `(Name, Framework)` and not `Name` is what identifies an entry. It sits next to `Name`
  rather than in `Evaluation` because it is identity, not evidence: it is what a script looks
  an entry up by, and a composed `csc {}` compilation (whose `Evaluation` is empty by
  contract) still has one, from `targetfwk`
- `Evaluation` -- where the answer came from; `run` never reads it, and every field is empty
  for a compilation composed from `csc {}` settings:
  - `Project` -- the project file path ("" when composed)
  - `ProjectRefs` -- `ProjectReference` items, as project files
  - `Imports` -- the msbuild files (outside the SDK) whose evaluation produced this entry, hashed
  - `Sdk` -- `NETCoreSdkVersion`, the SDK that evaluated the project
  - `SdkPin` -- `Lock.SdkPin option`, typed (`NoGlobalJson | Pinned v | RollsForward (v, policy)
    | NoVersion file`); written as `"none"`, `"exact <v>"`, `"<v> rollForward:<policy>"`, `"no
    version (<file>)"` and `""` for `None`
  - `Properties` -- a small whitelist (`AssemblyName`, `TargetFrameworkMoniker`, `LangVersion`,
    `Version`, `InformationalVersion`, `SignAssembly`, `AssemblyOriginatorKeyFile`,
    `Deterministic`, `TargetPath`, `IntermediateOutputPath`); `SdkPin` and `NETCoreSdkVersion`
    have their own fields now and are no longer in it
- `Compilation` -- what is compiled; changes with every PR (in memory these are fields of `Csc` or `Fsc` -- `Directory`, `Options`, `Defines`, `Sources`, `Generated`, `Resources` -- the file's `"Compilation"` key groups them):
  - `Directory` -- the compiler's working directory
  - `Options` -- every argument that is not a source, a reference, an analyzer or a define, in
    msbuild's order, with the four **section markers** in place (below)
  - `Defines` -- the `/define:` symbols, split on `;`
  - `Sources` -- the source files, absolute
  - `Generated` -- msbuild-written *text* compiler inputs, by path, with content (assembly
    attributes, the derived `.editorconfig`, SourceLink's `sourcelink.json` -- `sourcelink` is
    one of `CscArgs.inputSwitches` since it names a file the compiler reads); a compiled
    `.resources` file, though also a `/resource:` input under the intermediate directory, is
    excluded here and tracked in `Resources` instead, since it is binary and
    `File.ReadAllText`ing it would corrupt it
  - `Resources` -- `.resx` files this project embeds: `(resx path, .resources output path)`
    pairs, both absolute; `run` regenerates the output from the resx (byte-identical to
    msbuild's) when it is missing, so a machine with only the lock can still reproduce it
- `Dependencies` -- what it is compiled with and against, hashed; changes rarely and is
  reviewed when it does:
  - `Compiler` -- `{ Tool; Path; Sha256; Version }`, `Version` the compiler's own product version;
    `Sha256` is empty for a compiler of the .NET SDK ([why](#the-compiler-a-lock-names))
  - `References` -- `{ Path; Sha256; Alias }` in command-line order; `Alias` is the `alias=`
    prefix of an `extern alias` reference and "" otherwise (written to the file only when set)
  - `Analyzers` -- `{ Path; Sha256 }`
  - `Packages` -- the restore graph, `{ Id; Version; Sha512; Direct; DependsOn }` per package
    (below); in memory this is `Lock.Entry.Packages`, not part of `Csc.Dependencies`
- `Args` (member of `Csc`) -- the exact command line, rebuilt from `Options` and `Dependencies`;
  `Sources` and `Output` (from `/out:` in `Options`) are members too, nothing is stored twice

**Structured `Options` and the four section markers.** `Csc.Options` holds every
argument except the four factored sections, in msbuild's original order, with a marker string
at the position each section occupied: `"@Sources"`, `"@References"`, `"@Analyzers"`,
`"@Defines"`. `Csc.Args` rebuilds the list by expanding each marker: `@References` to one
`/reference:<alias=>path` per entry (a path containing a comma re-quoted, as msbuild quotes
it -- `CscArgs.quoteIfNeeded`), `@Analyzers` to one `/analyzer:path` per entry, `@Defines` to one
`/define:A;B`, `@Sources` to the sources. `Csc.ofArgs : string list -> Csc` does the factoring: a `/reference:`/`/r:` switch with exactly one
item goes to the references (alias split with the quote-aware `CscArgs` logic), `/analyzer:`/`/a:`
with one item to the analyzers, every `/define:`/`/d:` to `Defines` (concatenated, one marker at
the first), every source to `Sources` (one marker at the first source). A contiguous block
collapses to one marker; should msbuild ever emit two non-contiguous blocks of one kind, the
marker stays at the first and the round-trip check decides. A leading `@` in `Options` is
always a marker and never a csc response-file reference: msbuild never puts one on the compiler's
command line (the rsp is `run`'s own, written around the whole list).

*Why markers and not a canonical order.* The real locks are the argument: repo-a's have
`/warnaserror+:NU1605` **after** the sources, and the relative order of the reference, analyzer
and source blocks differs between Roslyn versions. A canonical order would have rebuilt a
different command line on a new SDK and failed every import there; markers keep msbuild's own
order without storing the flat list, so the structured form and the verbatim one are the same
information.

**The round-trip check (the fidelity guarantee: what the lock describes is what msbuild reported).** `parseImport` builds the entry
through `ofArgs`, then requires `entry.Args = msbuild's absolute args` -- otherwise it fails,
naming the project and printing `Lock.diffList` of the two lists (`- <msbuild's>` / `+
<rebuilt>`), and nothing is written. So the lock never describes a compilation other than the
one msbuild reported: "do not reconstruct" is checked at import rather than trusted. `resolve`
(composed mode) goes through the same `ofArgs` and the same check. Two `/define:` switches
would be the one case that fails by design (they fold into one section) -- msbuild emits one.

**Packages: the restore graph in the lock (decided 2026-09-24).** Right after the design-time
build, still inside `withProjectLock`, `import` reads `project.assets.json` (the
`ProjectAssetsFile` property from the same result dump) with `Nuget.readAssets` for the import's
framework and builds `Dependencies.Packages` with `Lock.packagesOf cacheRoot assets`, the cache
being the `NuGetPackageRoot` property (fallback `Roots.nugetRoot ()`): per package `Id`,
`Version` (the assets file's casing), `Sha512` (base64 `contentHash` from the cache's
`.nupkg.metadata`, "" when the cache lacks it), `Direct` (a `PackageReference` of the project's
own framework section) and `DependsOn` (the ids this package's assets entry depends on).
`type: project` entries are not packages and do not appear. Reading inside the lock is what
closes the second half of the import race (concurrent imports rewriting shared assets files): the per-variant assets copy and the `ProjectAssetsFile`
property hack are gone -- the lock carries the graph, and `Sbom.forAssembly` reads the lock only.
Trap found here: a single-`TargetFramework` project's assets keys `targets` by the *full*
framework name (`.NETStandard,Version=v2.0`), only a multi-target project uses the alias;
`Nuget.readAssets` tries both (`Nuget.frameworkFullName`).

**File format.** One entry is `{ "Name", "Framework", "Evaluation": {...}, "Compilation": {...},
"Dependencies": {...} }` under `"Entries"`; the document itself carries only `"Configuration"`,
`"Properties"` and `"Entries"`. A lock written when the framework was a property of the *file*
(a document-level `"Framework"`) still reads -- the value is distributed into every entry that
has none of its own -- and is always written back in the new shape; tokenization (`Roots.tokenizeAll`) and the
one-line-per-item style are unchanged, the file is deterministic (write, parse, write again is
byte-identical -- tested). The shape `Lock.format` produces (two-space indent per level, one
item per line; `Alias` only on a reference that has one; each package one inline object):

```json
{
  "Configuration": "Release",
  "Properties": {
    "Brand": "Acme"
  },
  "Entries": [
    {
      "Name": "CoreLib",
      "Framework": "netstandard2.0",
      "Evaluation": {
        "Project": "$(ProjectRoot)/src/CoreLib/CoreLib.csproj",
        "ProjectRefs": [ ... ],
        "Imports": [
          { "Path": "$(ProjectRoot)/Directory.Build.props", "Sha256": "..." }
        ],
        "Sdk": "8.0.425",
        "SdkPin": "exact 8.0.425",
        "Properties": {
          "AssemblyName": "CoreLib", ...
        }
      },
      "Compilation": {
        "Directory": "$(ProjectRoot)/src/CoreLib",
        "Options": [ "/noconfig", ..., "@Defines", "@References", "@Analyzers", "@Sources", ... ],
        "Defines": [ "TRACE", ... ],
        "Sources": [ "$(ProjectRoot)/src/CoreLib/A.cs", ... ],
        "Generated": { "<path>": "<content>", ... },
        "Resources": { "<resx path>": "<.resources path>", ... }
      },
      "Dependencies": {
        "Compiler": { "Tool": "csc", "Path": "$(DotnetRoot)/sdk/8.0.425/Roslyn/bincore/csc.dll", "Sha256": "", "Version": "4.11.0-3.25569.22" },
        "References": [
          { "Path": "$(NuGetPackageRoot)/...", "Sha256": "..." },
          { "Path": "...", "Sha256": "...", "Alias": "legacy" }
        ],
        "Analyzers": [ { "Path": "...", "Sha256": "..." } ],
        "Packages": [
          { "Id": "Newtonsoft.Json", "Version": "13.0.3", "Sha512": "...", "Direct": true, "DependsOn": [] }
        ]
      }
    }
  ]
}
```

(Lists are written one item per line; they are folded to `[ ... ]` above for space. `Generated`
and `Resources` are JSON objects keyed by path, `Properties` too.) A lock in the old flat format (`"Projects"` with `"Args"`) is refused
by `Lock.parse` with "lock written by an older Xake; re-import" -- nothing released used it, so
there is no reader for it. On the real fixture (a three-project library repository, kept privately) the locks have 889 lines per brand, 27 `Options` (`@Defines`, `@References`, `@Analyzers`, `@Sources`
in msbuild's positions), 12 `Defines`, 113-115 `References`, 4 `Packages`.

`Lock.diff a b` covers every section: `Framework`, `Options` and `Sources` as ordered lists, `Defines`,
`ProjectRefs` as sets, `Compiler` incl. `Version`, `Evaluation.Sdk`, the hashed lists,
`Generated`/`Resources` pairs, `Packages` (added, removed, version changed, sha512 changed).
`Lock.mapPaths f` rewrites `Options` (markers left alone), `Sources`, `References` and
`Analyzers` (each dropping its hash when the path changed), `Generated` keys and both paths of
`Resources`; `Lock.mapText f`
applies `f` to `Generated` content, `Options`, `Defines` and the evaluation's property values
(`Git.tokenize` and `run`'s resolution of `$(SourceRevisionId)` both use it).

`Lock.load path` (a recipe) parses a lock file (`Lock.Document`: `Configuration`, `Properties`,
`Entries` -- the framework is on the entry), paths expanded for this machine. **`load` and
`loadWith` `needFiles` the lock themselves**: reading a lock is depending on it, so a recipe
that reads one records the `FileDep` and, when a rule produces the lock, builds it first. The
`do! need [lockFile ...]` that used to precede every `Lock.load` in a script is gone -- it was
easy to forget and impossible to notice the absence of. `save`/`saveWith` deliberately do not:
writing a file is not depending on it, and a `need` there would be wrong in both of their uses
(a rule writing the lock as its own target would depend on itself, and `csc { lock }` writes
its lock from inside the compile recipe, where the lock is not a target at all). Both `load`
and `save` resolve a relative path against `ExecOptions.ProjectRoot`, the way a target path is
resolved.

`Lock.entry name lock` looks an entry up by assembly name or project file name and now fails,
naming the frameworks, when the name matches more than one entry; `Lock.entryFor framework name
lock` is the unambiguous lookup and the one a rule that carries the framework should use.

**`$(SourceRevisionId)` and the commit sha.** The SDK's
built-in SourceLink writes `sourcelink.json` (a Bitbucket/GitHub-shaped URL template with the
commit sha in it) and passes it on `/sourcelink:<path>` -- `sourcelink` is one of
`CscArgs.inputSwitches`, so that file lands in `Generated` like any other msbuild-written text
input. Left as is, its content would carry the commit sha, and so the lock's content -- and the
lock file itself -- would change on every commit even though the compilation did not change.
`parseImport` asks msbuild for the `SourceRevisionId` property (in `wantedProperties`) and, when
it is non-empty, calls `entry |> Lock.mapText (Git.tokenize sha)` (pure): every
occurrence of `sha` in `Generated` content, `Options`, `Defines` and the evaluation's `Properties`
values is replaced with the literal token `$(SourceRevisionId)`. The sha itself is **not** recorded anywhere in the lock --
`SourceRevisionId` is asked from msbuild only to drive this substitution, never added to the
`Properties` whitelist. `Lock.compileWith` (step 3 of "Replaying a lock") resolves the token back at
compile time, from the project's own repository (`Git.headSha entry.Compilation.Directory`) -- see below.

`Project.import` also `needFiles`s `Git.headFiles (project's directory)` -- `.git/HEAD` and the
ref file (or `packed-refs`) it resolves through -- for every project, whether or not it uses the
token: a token in the lock's *content* is commit-independent by design, so nothing else tracked
by the import changes on a new commit, and without this the lock would go stale (compile with an
out-of-date resolved sha) instead of re-importing.

**`Git` (`Xake.Hermetic.Dotnet.Git`, `Git.fs`).** Reads `.git` directly, no `git` executable:
`Git.headSha dir : string option` and `Git.headFiles dir : string list` both walk up from `dir`
for a `.git` entry (stopping at the filesystem root) -- a **directory** (an ordinary checkout:
`HEAD` lives there, and refs resolve against the same directory unless a `commondir` file says
otherwise) or a **file** (a linked worktree: `gitdir: <path>` names the worktree's own private
git directory, which holds its own `HEAD` but a `commondir` file pointing at the main
repository's `.git`, where `refs/` and `packed-refs` actually live). `HEAD` is either symbolic
(`ref: refs/heads/<name>\n`, resolved against a loose ref file under the common directory, or a
`packed-refs` line `<sha> <refname>` when there is no loose file) or detached (the sha directly).
`headFiles` returns exactly the files that change when the commit does -- `HEAD` alone for a
detached HEAD, `HEAD` plus the loose ref or `packed-refs` for a symbolic one -- so `import` can
`needFiles` them; `headSha` returns the sha itself, for `run`. Both return `[]`/`None` when `dir`
is not inside a repository.

**SDK pin check.** `Project.sdkPin` (pure) walks up from each project's directory for a
`global.json` and reads `sdk.version`/`sdk.rollForward`, producing a `Lock.SdkPin`:
`NoGlobalJson`, `Pinned version` (only `rollForward: "disable"` counts as pinned), `RollsForward
(version, policy)` (any other policy, or the absent-policy default `latestPatch`), or `NoVersion
file` (a `global.json` with no `sdk.version`). `import` computes it per project before calling
`parseImport`, which records it typed in `Evaluation.SdkPin` (written as `Lock.sdkPinText`, read
back with `Lock.parseSdkPin`), next to `Evaluation.Sdk`. An entry whose compiler or analyzers lie
under `$(DotnetRoot)/sdk/<v>/` gets the `dotnet-sdk <v>` prerequisite when the pin is exactly
`v`, and otherwise the warning `'<name>': depends on the .NET SDK <v> but global.json does not
pin it exactly; the lock will only build where that SDK is installed`
([Prerequisites](#prerequisites)). An unpinned project whose entry names nothing under the SDK
(a `Microsoft.Net.Compilers.Toolset` project) still gets the older warning `"'<name>': the SDK is
not pinned (<pin>) -- the lock's compiler (<version>, SDK <sdk>) will drift with every SDK the
machine picks; pin it with global.json { sdk: { version, rollForward: "disable" } }"`. When the
pin is `Pinned v` but msbuild's `NETCoreSdkVersion` differs from `v` -- the pinned SDK is not
installed and a different one ran -- it warns separately with both versions named.

The project-reference pattern, from `import.fsx`:

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

let unbuilt = project.Dependencies.References |> List.filter (fun r -> r.Sha256 = "") |> List.map (fun r -> r.Path) |> Set.ofList
let mapped = project |> Lock.mapPaths (fun p -> if unbuilt.Contains p then outputOf p else p)

do! need (unbuilt |> Set.toList |> List.map (outputOf >> relative))
do! Lock.compile mapped
```

A project reference in the lock is unhashed and points at the referenced project's own build
output (not built yet at import time). `Lock.mapPaths f entry` rewrites every path the
entry's `Options`, `Sources`, `References`, `Analyzers`, `Generated` and `Resources` keys carry through `f`;
a rewritten
`Hashed` entry loses its hash, since the recorded hash was computed for the old path. The script
maps those references to the path its own rule will produce them at, `need`s those targets
first, and only then runs `Lock.compile mapped` -- the runner itself does not `need` the
mapped outputs, it only `needFiles` what the (already-mapped) args name.

## Recording a lock from composed `csc` or `fsc` settings

`Project.import` produces a `Lock.Entry` from an msbuild project; `csc { ...; resolve }` produces
the `Csc` of one from composed settings (`Csc.ofSettings` for record syntax), `fsc { ...; resolve }`
the `Fsc` (`Fsc.ofSettings`), and `Lock.ofCsc`/`Lock.ofFsc` wrap it in an entry with an empty
`Evaluation` and no `Packages`, so a lock-recording rule can write it out the way an import rule
does.
The API:

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

let! c = csc { src !!"*.cs"; out (File.make "app.dll"); resolve }   // Csc
do! Lock.record "locks/app.json" (Lock.Compilation.Csc c)
let! f = fsc { src !!"*.fs"; out (File.make "lib.dll"); resolve }   // Fsc
do! Lock.record "locks/lib.json" (Lock.Compilation.Fsc f)
```

```fsharp
module Csc =
    val ofSettings : CscSettingsType -> Recipe<ExecContext, Csc>
module Fsc =
    val ofSettings : FscSettingsType -> Recipe<ExecContext, Fsc>

module Lock =
    val ofCompilation : Compilation -> Entry
    val ofCsc : Csc -> Entry
    val ofFsc : Fsc -> Entry
    val rehash : Entry -> Entry
    val diff : Entry -> Entry -> string list
```

`resolve` produces no temp files of its own to clean up (see the composed-mode resx paragraph
above), so a `Csc` it returns, `.resx` resources included, is compilable and recordable as is.
The record is `Csc` and the operations are `Csc.ofSettings`/`Csc.run`/`Csc.compile` because F#
does not let a `module` and a `let`-bound function share one name (`FS0039`, verified) -- which
is why the 3.3 function `Csc settings` went (`Csc.compile settings` replaces it).

**`Lock.rehash entry`** fills `Sha256` for every hashed entry (`References`, `Analyzers`,
`Imports`) and for `Compiler` (its `Version` too, when empty), from what is on disk right now
(`Csc.sha256`, which gives "" for a file that does not exist; `Hash.sha256` itself throws). `Csc.ofSettings` never hashes -- hashing every reference on every composed compile
would tax the common case for nothing -- so a lock-recording rule calls `rehash` itself, once,
after `resolve`; the cost is then paid only when that rule reruns, like everything else
in Xake.

**`Lock.diff a b`** is a pure, human-readable comparison of two lock entries of the same
project: `[]` means identical. In order: `Framework` (`~ Framework: <a> -> <b>`), then `Options` and `Sources` as ordered lists (bare `+`/`-`
lines from an LCS diff -- a moved argument shows as a removal at its old position and an
addition at its new one, there being no separate "moved" marker in an ordered diff), `Defines`
as a set, `Compiler` (`Tool`/`Path`/`Sha256`/`Version`, one line per differing field), `Evaluation.Sdk`,
each hashed list (`References`, `Analyzers`, `Imports`) by path (added, removed, or `~ <label>
<path>: <old> -> <new>` when both sides have a hash and they differ), `Generated`/`Resources` by
key (added, removed, or `~ <label> <key>: content changed`), `ProjectRefs` as a set
(added/removed), and `Packages` by id (`+ Package Id@Version`, `- Package Id@Version`, `~ Package
Id: <old> -> <new>` for a version change, `~ Package Id@Version: sha512 changed`). This is the primitive `csc { lock "path" }` (below) and `Lock.verify` are built on; no `Policy`
wiring exists. `Lock.diff` stays usable stand-alone (e.g. from an
fsx-level "verify" rule).

Tests: `src/hermetic.tests/LockDiffTests.fs` (`rehash`, `diff` identical and with changes, pure, no
msbuild/compiler needed); `src/hermetic.tests/FromLockTests.fs`, `Csc.ofSettings resolves composed
settings into a hashable, round-trippable lock` (Integration: resolves a trivial library,
asserts `Sources`/`Args`/`Compiler.Path`/empty reference hashes, then `Lock.rehash` and a
`Lock.format`/`Lock.parse` round trip).

## Locking composed settings: `lock`

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

"out/app.dll" ..> csc {
    src !!"src/*.cs"
    grefs ["System.dll"]
    targetfwk "net-4.6.2"
    out (File.make "out/app.dll")
    lock "locks/app.json"
}

"update-locks" => recipe {
    let! c = csc { src !!"src/*.cs"; grefs ["System.dll"]; targetfwk "net-4.6.2"
                   out (File.make "out/app.dll"); resolve }
    do! Lock.record "locks/app.json" (Lock.Compilation.Csc c)
}

"out/lib.dll" ..> fsc {
    src !!"src/Lib.fs"
    targetfwk "net8.0"
    lock "locks/lib.json"
}
```

`lock "<path>"` is the migration path for an existing script: a tuned `csc { }` block
stays where it is and gains a lock. It is a custom operation defined next to the lock, not in the
base builder (`CscLockBuilder` in `Lock.fs`, an extension of `CscSettingsBuilder` with its own
`Run`). Only `nofetch` may follow it (below); the block then means
`Csc.ofSettings`, `Csc.runOptions`, `Lock.buildWith`. The same thing without the sugar is
`Lock.build "locks/app.json" (Lock.Compilation.Csc c)` on a `Csc` from `resolve`. The path is
relative to the project root, like every other target path, or absolute. There is no `Lock`
field on `CscSettingsType`. `fsc { lock }` is the same operation for F# (`FscLockBuilder`, an
extension of `FscSettingsBuilder`, with `FscLocked`): `Fsc.ofSettings`, `Fsc.runOptions` into
`Lock.Options.FscRun`, `Lock.buildWith`; everything below holds for it unchanged.

The settings remain the source of truth for *what* is compiled (`Csc.ofSettings` runs on every
build); the lock decides whether this is the compilation that was recorded. An update is always
explicit (the user's decision, 2026-09-24: no "follow the settings and warn" default).

### What happens, step by step

On a developer machine:

1. **The first build records the lock.** There is no lock file yet: the resolved compilation is
   hashed, written to `locks/app.json` and compiled. Commit the lock with the change that
   introduced it.
2. **The next builds compile the lock.** The settings still match it, so the *recorded* entry is
   compiled and the lock file is not touched. Its hashes gate the build: a reference swapped on
   disk after recording fails the hash check, although the settings did not move.
3. **Drift fails the build.** When the settings move away from the lock (a source added, an
   option changed, another reference), the build fails with the differences and the two ways to
   update:

   ```
   'app': the resolved compilation differs from the lock 'locks/app.json':
   + $(ProjectRoot)/src/Extra.cs
   Update the lock deliberately: delete 'locks/app.json', or run the target that calls
   Lock.record "locks/app.json".
   ```

   Run `update-locks` (or delete the lock and build), review the lock's diff, commit it. Paths in
   the message carry the same tokens as the lock file (`$(ProjectRoot)`, `$(NuGetPackageRoot)`,
   `$(DotnetRoot)`), so it reads the same on every machine.

On CI:

4. **The committed lock is compiled.** The settings match it; the packages it names are restored
   (into the build's package folder, `NUGET_PACKAGES`) and the recorded entry is compiled, its
   hashes checked.
5. **Drift fails the build**, with the same message as step 3: CI never updates a lock.
6. **A missing lock fails the build**, before anything is recorded or compiled:

   ```
   'app': the lock 'locks/app.json' is not there. Under CI a lock is never recorded: record it
   on a developer machine (build once, or run the target that calls Lock.record
   "locks/app.json", e.g. update-locks) and commit it.
   ```

   Recording it on the runner would pass the build and leave a lock that exists only there.
   `nofailonerror` (`FailOnError = false`) does not turn this into a warning -- it would be a way
   for CI to pass without a lock.

`nofailonerror` turns the drift failure (steps 3 and 5) into a warning and compiles the
**resolved** entry: the settings are the source of truth, and nothing is written.

The comparison is structural, not hash-based: the resolved side never hashes anything
(hashing every reference on every compile would tax the common case), and both `diffHashed`
and `diffCompiler` skip a hash that is empty on either side -- an empty hash means "not
computed", not "zero bytes". So `Lock.diff recorded resolved` reports what the settings and the
filesets say: sources, options, defines, reference/analyzer paths, the compiler path. The
printed lines are `Lock.diffText roots`, the same diff with every path tokenized against the
roots the lock is written with; `Lock.diff` itself keeps the expanded paths.

**Updating.**

| Way | What it is |
|---|---|
| `dotnet fsi build.fsx -- -- update-locks` | the script's own phony target calling `Lock.record`; the normal way |
| `rm locks/app.json` | a missing lock is recorded on the next build -- not under CI (step 6) |

`Lock.record : string -> Lock.Compilation -> Recipe<ExecContext, unit>` rehashes and overwrites
the lock, compiling nothing. `Lock.verify : string -> Lock.Compilation -> Recipe<ExecContext, string list>`
returns the same diff `lock` fails on (tokenized paths; an empty list means the lock is
current), writing nothing and compiling nothing -- scenario 3 of `lock-from-settings.md` as a
stand-alone check, e.g. a `check-locks` target in CI. A lock that does not exist fails `verify`
with a message naming the path and `Lock.record` (it used to fail with the engine's
`Neither rule nor file is found`). `Lock.compile` remains the entry point for a lock that came
from `Project.import`.

### The CI flag

Whether a build is under CI is decided once per build, by `Lock.underCi : Recipe<ExecContext,
bool>`:

| Source | Value |
|---|---|
| script variable `CI` (`-d CI=on`, `xakeScript { var "CI" "on" }`, `Vars`), when set | `on`/`true`/`yes`/`1`: CI; `off`/`false`/`no`/`0`: not CI (case-insensitive); anything else fails |
| otherwise the environment variable `CI` (set by GitHub Actions, GitLab CI, Azure Pipelines and most others) | non-empty and not `0`/`false`: CI |
| neither | not CI |

Same convention as `NETFX` and `CSC_SERVER`: `-d CI=off` builds as a developer machine would
(records a missing lock) even on a runner, `-d CI=on` rehearses the CI behaviour locally. Reading
the flag (`getVar`, then `getEnv`) makes it a tracked dependency of the target, so flipping it
reruns the target.

`Lock.buildWith` reads the flag itself, so `Lock.build`, `csc { lock }` and every script calling
`buildWith` with options of its own get the CI behaviour; there is no option for it. A script
that must record on CI opts out with `-d CI=off`. The flag only says whether an *absent* lock may
be written; a present lock is only ever rewritten by `Lock.record` or by deleting it.

### Fetching: `nofetch`

`csc { lock }` compiles through `Lock.buildWith`, whose restore step needs to know whether a
package the lock names but the package folder lacks may be downloaded (`Restore.Options`,
[restore.md](restore.md)). One operation sets that, and it comes **after** `lock`:

```fsharp
"out/app.dll" ..> csc {
    src !!"src/*.cs"
    grefs ["System.dll"]
    targetfwk "net-4.6.2"
    out (File.make "out/app.dll")
    lock "locks/app.json"
    nofetch                     // Restore.Options.Enabled = false: never download
}
```

| Operation | Effect |
|---|---|
| `nofetch` | a package the folder lacks is not downloaded: a warning (`fetching is off (nofetch / NUGET_FETCH=off)`), then the hash check fails naming every missing file, before anything is compiled |

It was called `norestore` before 0.2; renamed with the base's `NUGET_FETCH` variable, of which it
is the per-target form. Why after `lock`: `lock` turns the block's state from `CscSettingsType`
into `CscLocked` (path, settings, fetch flag), and `nofetch` is an operation on that state.

**The package folder is the build's** (the script variable `NUGET_PACKAGES`, else the
environment variable, else `~/.nuget/packages`), and fetching follows `NUGET_FETCH` unless
`nofetch` turns it off; there is no
per-target folder. `packageroot "<dir>"` after `lock` was removed in 0.2 (decided 2026-10-09: one
package folder per build, and a per-target one makes no sense under `HERMETIC=on`). A script
that reads a lock against another folder still can, outside the builder: `Restore.into dir`,
`Lock.loadWith (Roots.packageRootOverride dir)`, and `Lock.buildWith` with `Restore = { ...
PackageRoot = Some dir }`, which compares and writes the lock with the build's ordinary roots and
re-roots the entry it compiles at the folder, in memory (`Lock.format`, then `Lock.parse` with
`Roots.packageRootOverride`). The default folder is the base's `DotNetFwk.packageRoot ()` and
the default `Restore.Options.Enabled = true` follows its `fetchEnabled ()` (`Restore.resolve`).

**The lock file is not a target of the engine on this path.** It is written from inside the
compile recipe, which is what lets the `csc { }` block stay in place; the engine neither
`need`s it nor rebuilds it, and the update target is an ordinary phony action of the script's
own. The alternative design (the lock as a real file target, built by a
rule of its own from `csc { ...; resolve }`) still stands for scripts that want it and needs nothing
new. As before, one lock file per compilation: two `csc` calls sharing one path would
read-modify-write the same file and is an authoring error, not something the library guards.

**Sharing the settings.** `csc { ...; resolve }` returns the `Csc`, so one block can feed both
the compile and `Lock.record`/`Lock.verify` -- define the block once as a recipe and use its
result. (Path B of §9, a second builder `cscSettings { ... }`, is not needed any more.)

Tests: `src/hermetic.tests/CscLockTests.fs` (18, all Integration but the `nofetch` one) -- first build records and compiles,
second build leaves the lock byte-identical, an added source fails with the diff, `Lock.record`
overwrites and the next build passes, a reference tampered after recording fails the hash check,
and `Lock.verify` reports the difference without writing or compiling; under CI (script
variable) a missing lock fails with the message above and nothing is recorded or compiled,
`FailOnError = false` does not help, the environment variable `CI=true` fails it through
`csc { lock }`, `-d CI=off` with `CI=true` in the environment records, not under CI records,
drift under CI fails as before, an unrecognized `CI` value fails; drift messages and `verify`
lines carry `$(ProjectRoot)`, not the absolute root; `verify` on a missing lock; `nofetch` turns
fetching off for csc and fsc, and with fetching off an empty package folder fails before
compiling. The tests that record set the script variable `CI=off`, so the suite also
passes with `CI=true` in the environment.

## Xake-generated locks

The locks of project descriptions ([description.md](description.md)) are written without
compiling and without msbuild evaluating anything, by a **phony rule in the script** that calls
the one-lock primitive `Locks.generate` (module `Locks`, `src/hermetic/Locks.fs`; not `Lock`: it
needs `Described`, which compiles after `Lock.fs`):

```fsharp
let lockOf name cfg = $"locks/{name}/{cfg}.json"
let outOf cfg fwk (_: string) = $"out/{cfg}/{fwk}"
let descriptionOf name = $"src/{name}/xake.project.json"

"lock::(name:*)::(cfg:*)" => recipe {
    let! name = getRuleMatch "name"
    let! cfg  = getRuleMatch "cfg"
    let! d = Description.load (descriptionOf name)
    do! need [ for r in d.ProjectRefs -> $"lock::{r}::{cfg}" ]
    do! alwaysRerun ()
    do! Locks.generate lockOf outOf cfg (descriptionOf name)
}
command "update-locks" {
    do! Locks.restoreAll (!! "src/*/xake.project.json") "Release" lockOf descriptionOf   // one dotnet restore
    do! need [ for n in projects do for cfg in ["Release"] -> $"lock::{n}::{cfg}" ] }
```

`xake update-locks` regenerates them; `xake lock::Xake.Dotnet::Debug` does one (and the locks it
references).

**Why a phony rule.** The ordering between projects is a `need` of the referenced projects'
lock rules, so it is ordinary Xake targets, not a sort inside the library; the script decides
what a lock depends on and can add settings per project or configuration (a different
configuration for the references, an extra `need`, a filter). The engine runs a target at most
once per run, so a project referenced by several others gets its lock generated once. The
library owns no loop and no layout: `lockOf`, `outOf` and `descriptionOf` are the script's.

**Why `alwaysRerun`.** The lock is written as a side effect, not as a target: the engine does
not notice a deleted or hand-edited lock file, and a phony rule whose dependencies did not
change would be skipped. Regeneration is cheap when nothing changed (nothing is written), so
the rule simply runs every time it is demanded.

**Why `update-locks` is explicit; fresh clone and CI.** The locks are committed. `build` reads
them through `Lock.load`, which `needFiles` the lock path; since no FILE rule matches
`locks/...`, the engine treats the lock as a plain input file and never runs a lock rule, even
with no `.xake` database (a fresh clone, a CI run). Locks change only when someone runs
`update-locks` and commits the result. Do not turn the lock rule into a file rule
(`"locks/(name:*)/(cfg:*).json" ..> ...`): every `Lock.load` would then run it on a fresh clone.

What `Locks.generate lockOf outOf config descriptionPath` does for the description at
`descriptionPath` (its `name` from the file) and `config`:

- **One lock** at `lockOf name config`, `Configuration` set to the configuration, one entry per
  framework of the description, in the description's order.
- **The compilation** is `fsc`/`csc { targetfwk; out; ref; project; config; packages; resolve }`
  ([../csc-syntax.md](../csc-syntax.md#from-a-project-description-project-config-packages)): the
  SDK preset, `--deterministic+`, `--pathmap:$(ProjectRoot)/=/_/`. The output switch (`--out:`,
  `/out:`) is removed: the output path is the build's. The doc switch stays, at the layout path
  `outOf cfg fwk name`.
- **A project reference** to B: B's lock must exist (at `lockOf B c`; the rule `need`s B's lock
  rule first). B's configuration is the first B has a lock for in the order *this
  configuration, Release, Debug* (`Locks.refConfig`; provisional, the brief's open "ref-config
  fallback order"; with the rule above B's lock in this configuration always exists, the
  fallback matters when the script needs B's locks in another configuration); B's framework is
  the nearest of the frameworks in that lock (`Locks.nearestFramework`: the same; else the
  highest of the same family not above it; else the highest `netstandard` the target
  implements). The choice is recorded only as the reference path `outOf cfgB fwkB B` +
  `/<B's assembly>.dll`, **with an empty `Sha256`**, also when a build left the file on disk
  (`Lock.recorded ... unhashed`; [roadmap](../roadmap.md): in-lock project references stay
  unhashed). `Evaluation.ProjectRefs` holds B's name, as the description names it. A reference
  without any lock fails, naming it and pointing at `update-locks`. B's own project references
  (the unhashed references of B's lock, so B's closure) are references of A too, after the
  direct ones, each once: msbuild hands the compiler the transitive project references, and a
  type of C that A uses through B needs C (CS0234/CS0012, FS0074 otherwise). A's description
  names only B.
- **Packages** come from `Described.restore` (an msbuild restore of a synthesized project). A
  version the current lock's entry holds is passed as exact (`[v]`), so it stays when the
  description floats (`8.0.*`) or a newer version appears. The script variable, else the
  environment variable, `LOCK_REFRESH` re-resolves the named ids as declared (`FSharp.Core`,
  a comma/semicolon list, or `*` for all): `xake update-locks -d LOCK_REFRESH=FSharp.Core`.
  B's direct packages (from B's lock) flow into A's restore at B's locked version, as msbuild
  flows them through a project reference.
- **One restore per build** <a id="one-restore-per-build"></a>. A restore is keyed by its
  framework, the package folder and the package set (sorted): the synthesized project is
  `obj/xake/restore/<framework>-<hash>/restore.csproj` and the result is kept for the build (one
  engine run), so projects with the same set restore once. `Locks.restoreAll descriptions cfg
  lockOf descriptionOf` computes, before the lock rules run, the set each `generate` will
  restore -- the versions the current locks hold, `LOCK_REFRESH`, a reference's packages from
  its lock or, before it has one, from its description (`descriptionOf`) at the exact declared
  version -- and restores them all with ONE `dotnet restore` of a plain msbuild project
  referencing the synthesized ones (`obj/xake/restore/batch-<hash>.proj`; restore resolves each
  graph on its own, in parallel, in one process). A set it cannot know ahead (a reference's
  floating version before its lock exists) or that the run changes (a reference whose lock is
  rewritten) is restored by its rule alone, as without `restoreAll`; a failing batch caches
  nothing and warns, and each rule then reports its own error. The direct route's twin is
  `Described.restoreAll descriptionOf descriptions frameworks` (the `restoreWith` sets).
  Self-build `update-locks` (3 descriptions x 2 frameworks): 6 restores, 2.3-2.5 s -> 1, 0.9 s.
- **`Directory`** is the project's folder (`$(ProjectRoot)/<folder of the project file>`): the
  compiler runs there, as under `dotnet build`, so a doc comment's relative
  `<include file='../docs/x.xml' .../>` resolves (CS1589 otherwise). `--pathmap` is unchanged.
- **Extra roots** <a id="extra-roots-for-generated-locks"></a>. Sources, a key file or a whole
  project in a sibling checkout (outside the project root) are tokenised only against a root the
  script declares, as for `Lock.loadWith`: `Locks.generateWith roots lockOf outOf cfg path` with
  `roots = [ "$(Engine)", "../engine-repo" ]` (`Roots.make`: `$(Name)`, a path relative to the
  project root or absolute). The lock then names `$(Engine)/src/**/*.cs`,
  `/keyfile:$(Engine)/key.snk`, `Directory` `$(Engine)/src/X`, and the compilation gets one
  `pathmap` per extra root, `<root>/=/Engine/`, in front of the project root's `/_/` (so the pdb
  and `CallerFilePath` name no machine path). Without the roots those paths stay absolute and the
  lock builds only in that checkout. The build passes the same list: `Locks.compileAsWith roots`,
  `Locks.needAllWith roots`, `Locks.restoreAllWith roots`; `generate`, `compileAs`, `needAll` and
  `restoreAll` are the same with no extra root. Under `HERMETIC=on` a path under an extra root
  counts as under the project root (record gate and replay). Test (`BuildFromLockTests`): a C#
  project in a sibling folder, its lock without an absolute path, built from a copy of both
  folders at another path into the same bytes.
- **Written only on change**: the text is compared with the file on disk (CRLF read as LF);
  regenerating with nothing changed writes nothing and leaves the timestamp alone.
- **Prerequisites**: as `Lock.record`: under an exact `global.json` pin the entry records
  `dotnet-sdk <v>` for an SDK compiler (fsc), without one the generation warns
  ([Prerequisites](#prerequisites)); see [hermetic-mode.md](hermetic-mode.md#generated-locks-under-the-mode).
- **No format change**: the locks use existing slots only, and the 0.2 reader (`Lock.parse`)
  reads them and `Lock.format` writes them back byte-identically.

Not done here: diamond references (A Debug -> B Release -> C) are not detected. Building from
these locks: next section.

Tests: `src/hermetic.tests/LocksTests.fs`, all through the rule above in a small script --
Xake's three projects (fixtures for src/core, src/dotnet, src/hermetic) get Release locks from
`update-locks` that read back unchanged, with no output switch and the tokenized pathmap,
regenerated byte-identically without a write even with a ref output on disk; `update-locks` run
twice runs each lock rule once per run (a library two projects reference: once) and the second
run writes nothing; with committed locks and the `.xake` database deleted, a `build` that
`Lock.load`s them runs no lock rule; a Debug project over a Release-only netstandard2.0 library
gets `$(ProjectRoot)/out/Release/netstandard2.0/Lib.dll` unhashed; a missing ref lock fails; a
floating version stays until `LOCK_REFRESH` names it; nearest framework, ref configuration and
kept versions as pure tests.

## Building from Xake-generated locks

The whole story: `xake update-locks` (the phony lock rule above) writes the locks, they are
committed, and `xake build` compiles from them. `Locks.compileAs` and `Locks.needAll` build the
committed locks into the script's layout -- the same layout (`outOf`) that `Locks.generate`
wrote into the project-reference paths. `build` never runs a lock rule: the locks are plain
input files to it, so a fresh clone or a CI run (no `.xake`) builds from what was committed:

```fsharp
let versions = [ "src/hermetic/**", vars.HermeticVersion; "**", vars.Version ]
command "build" { do! Locks.needAll (!! "src/*/xake.project.json") "Release" lockOf outOf }
target "out/(cfg:*)/(fwk:*)/(name:*).dll" {
    let! cfg = getRuleMatch "cfg"
    let! fwk = getRuleMatch "fwk"
    let! name = getRuleMatch "name"
    do! Locks.compileAs (lockOf name cfg) fwk versions }
```

- **`needAll descriptions cfg lockOf outOf`** reads every description and needs
  `outOf cfg fwk name` + `/<assembly>.dll` for each of its frameworks, all at once. A described
  project with no lock at `lockOf name cfg` fails the build pointing at `update-locks`; nothing
  regenerates a lock on its own. The packages all the locks name that the folder lacks are
  fetched first in one restore; if that fails (a bad feed, one wrong version) it is a warning, the
  compiles go on and each restores, and fails, alone.
- **`compileAs lockPath fwk versions`** runs inside the rule whose target is the assembly. It reads
  the lock (a dependency), takes its one entry for `fwk` (the target's file name must be the
  entry's output: the recorded `TargetFileName`, `app.exe` for a .NET Framework application,
  else `<Name>.dll` or `<Name>.exe`; any other target, a `.pdb` say, fails) and compiles it into the target: `--out:`/`/out:` is the rule's target
  (the lock has none); a `--doc:`/`/doc:` the lock carries is re-targeted beside it. The `.xml` is
  a side output of the `.dll` rule, so a lock without doc (a Debug lock, say) expects no `.xml` and
  is up to date on the next run; `needAll` needs the `.dll`s only.
- **The `.xml` first**: a script that needs the doc file before the dll (a pack rule) adds a rule
  over the `.xml` calling `Locks.doc`; it needs the assembly beside it (whose rule writes the
  `.xml`) and fails when the lock asks for no doc. `compile` over an `.xml` target is an error
  (it used to do this; 0.7.0):
  ```fsharp
  target "out/(cfg:*)/(fwk:*)/(name:*).xml" { ... do! Locks.doc settings (lockOf name cfg) fwk }
  ```
  One rule declaring both (`targets [dll; xml]`) is not the form: the engine reruns a rule while
  one of its targets is missing, and a lock without doc never writes the `.xml`.
- **Dependencies**: the unhashed project-reference outputs are needed before the compile, so
  the referenced `.dll` is built first by the same rule and a change to it rebuilds the
  referencer. The hashed references are packages: a missing one (an empty package folder on a
  fresh clone or CI) is fetched by the replay's restore first, then needed by the runner. C# source masks are expanded through `getFiles`
  (`Described.expandCsc`): an added or removed `.cs` under a mask rebuilds without touching the
  lock. The runner's own `needFiles` covers the sources and the compiler.
- **`$(Version)`**: a `$(Version)` token in `Generated` is replaced by the value of the variable
  of the first `versions` mask matching the project, taken as `<folder>/xake.project.json`
  relative to the project root (`xake.project.json` for a project at the root). The folder is the
  project's: `Directory` of the lock's compilation, else (a lock written with no project) where
  its first source lies; it is a path in the project tree, not where the description lives. A mask
  may start with a script root, `$(Sibling)/**`, expanded through the script's roots before
  matching. The variable is read (and so a dependency) only when the token is there; the lock keeps the token. The locks
  `Locks.generate` writes today carry none (assembly versioning is an open topic).
- **Extra roots**: a script whose locks were generated with `generateWith roots` builds with
  `needAllWith roots` and `compileAsWith roots` ([above](#extra-roots-for-generated-locks)).
- **Settings**: `Locks.compile settings lockPath fwk` is the entry point the others wrap. The
  record carries what `compileAs` takes as arguments, plus the runner options:
  ```fsharp
  type Locks.Settings = {
      Roots: (string * string) list                 // compileAsWith's extraRoots
      Versions: (string * RequiredVar<string>) list // compileAs's versions
      Lock: Lock.Options }                          // Run (csc), FscRun (fsc), Restore
  // Settings.Default = { Roots = []; Versions = []; Lock = Lock.Options.Default }

  let settings = { Locks.Settings.Default with Roots = roots; Versions = versions }
  let compileTarget = Locks.compile settings   // the script's own compile, partially applied
  target "out/(cfg:*)/(fwk:*)/(name:*).dll" {
      let! cfg = getRuleMatch "cfg"
      let! fwk = getRuleMatch "fwk"
      let! name = getRuleMatch "name"
      do! compileTarget (lockOf name cfg) fwk }
  ```
  `compileAsWith roots lockPath fwk versions` is `compile { Settings.Default with Roots = roots;
  Versions = versions } lockPath fwk`, `compileAs` the same without roots. `Lock` reaches
  `Lock.compileFromWith` as given (`Run.FailOnError`, `Run.CscPath`, `FscRun`, `Restore`, ...),
  with one thing filled in: `Lock.Run.Server` left at its default is resolved from `CSC_SERVER`,
  then `XAKE_CSC_SERVER`, as before; one the script set is used as is and the variable is not
  read. `Locks.doc` takes `Roots` only. With a hermetic release built on Xake 3.10, a script
  caps its compiles with its own resource through `Lock.Run.Throttle` (and `Lock.FscRun.Throttle`)
  here, in place of `CSC_JOBS`/`FSC_JOBS` ([../resources.md](../resources.md)); the current
  release builds on `[3.9.0.30, 3.10)`, whose runner options have no `Throttle`.
- **Replay** is `Lock.compileFrom` (what `buildWith` does with a matching lock): prerequisites,
  the **HERMETIC=on gate** (a lock naming a path outside the roots is refused, message 10, before
  anything compiles), restore of what the lock names (`NUGET_PACKAGES`, `NUGET_FETCH`), csc's
  server per `CSC_SERVER`, the hash check of every hashed reference. Nothing is recorded: the
  build never writes a lock.

With `--deterministic+` and the tokenized pathmap, two clean builds give identical assemblies.

Tests: `src/hermetic.tests/BuildFromLockTests.fs` -- A -> B (F#) built in Release and Debug,
the second build of each does nothing, B's `.xml` beside its dll and none for A, a change to B
rebuilds A, two clean builds byte-identical; a C# lock with masks rebuilds on an added `.cs`
without a lock change; a description without a lock fails pointing at `update-locks`;
`HERMETIC=on` refuses inside `compileAs`; `$(Version)` substituted and a new version rebuilds;
with `.xake` deleted, `build` over the committed locks of A -> B runs no lock rule; Xake's three
projects from the locks `update-locks` writes into `out/Release/<fwk>/`; A3 -> B3 -> C with A3
naming only B3 compiles (C's output in A3's lock after B3's); a C# doc `<include file='../docs/..'>`
resolves (lock `Directory` the project folder); B's `.xml` needed first through its own `Locks.doc` rule, a
no-doc lock's `.xml` refused; `compile` over an `.xml`, a `.pdb`, or a recorded application's
`.dll` (`app.exe`) fails, a generated lock's `.exe` compiles; the version mask matched against
the lock's project folder and a `$(SiblingRoot)/P/**` mask; `compile` with `Versions` builds the bytes `compileAsWith [] _ _
versions` builds and `compile Settings.Default` fails as `compileAsWith [] _ _ []` does (a C#
lock with a `$(Version)` token); `Settings.Lock` reaches the runner (`Run.FailOnError = false`
passes a broken source, an explicit `Run.Server` does not read `CSC_SERVER`); the sibling-root
copy built through `compile { Roots = roots }` gives the same bytes. Every test generates
its locks through the phony `lock::` rule and `update-locks`.

### Known difference: another checkout folder

Measured on Xake's own projects (SDK 10.0.401, macOS arm64): rebuilding from the locks in the
same folder gives byte-identical dlls. A clone in another folder, built from the same locks,
gives dlls that differ in 59 bytes -- the PE checksum and the timestamps of the debug directory
-- despite `--deterministic+` and `--pathmap`; the pdbs are identical. This is described, not
fixed. Comparing packages built in different folders (nupkg equality) therefore has to compare
entry content with this in mind, not the archive bytes or a plain hash of each dll.

## Behaviour notes

- A `Lock.Entry` in memory has absolute paths throughout; `Lock.save`/`format` tokenizes
  them against known roots (project root, NuGet package cache, SDK) so the file on disk is
  portable and diffable, and `Lock.load`/`parse` expands them back on load.
- A hash mismatch reports every mismatching path at once (`Csc.run`, see
  [../csc-syntax.md](../csc-syntax.md)).
