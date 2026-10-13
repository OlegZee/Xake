# `csc {}` as it stands

`csc {}` is the C# compiler task. It resolves its settings into one `Csc` record -- a project's
exact compiler command line, plus what it references and how to check it -- and hands that to a
single runner, `Csc.run`. Settings are intent, `Csc` is the resolved compilation: there are
several ways to arrive at a `Csc` (compose one from settings with `csc { ...; resolve }` or
`Csc.ofSettings`, take the `Csc` of a lock entry, or `Csc.ofArgs` from a command line) but only
one thing that ever shells out to the compiler.

The base reference for the settings, `Csc.compile`, `resolve`, `Csc.run` and `RunOptions` is
[tasks.md](tasks.md); this file keeps the details behind them. Locks -- the file that records a
`Csc` or `Fsc` with the hashes of everything it reads, `Lock.compile`, `Lock.build`,
`csc { ...; lock }`, `fsc { ...; lock }` and `Project.import` -- are not part of `Xake.Dotnet`; they live in the package
`Xake.Hermetic.Dotnet` and are described in [hermetic/lock.md](hermetic/lock.md).

## Composed mode

```fsharp
"temp/helloworld.exe" ..> csc {
    targetfwk "net-4.6.2"
    src !!"helloworld.cs"
    grefs ["System.dll"]
}
```

(`samples/fullframework.fsx`; `src/tests/DotnetTasksTests.fs`, `runs csc task (full test)`, shows
the same shape with `out`, `Src`, `TargetFramework`, `RefGlobal` set directly on the record.)

`csc {}` (through `Csc.compile`) calls `Csc.ofSettings`, which turns the settings below into a
`Csc` at recipe time -- same argument order the task has always produced, save that
references are now spelled `/reference:` rather than `/r:` -- and passes it to `Csc.run`.
`csc { ...; resolve }` stops after `Csc.ofSettings` and returns the `Csc`; `resolve` must be the
last operation, and outside a file rule needs an explicit `out`. (In a file rule `out` is
optional: without it, the output is the rule's file target, for `resolve` too. A phony rule has
no file target.) Every custom operation of
`CscSettingsBuilder`:

| Keyword | Argument | Does | Default |
|---|---|---|---|
| `platform` | `TargetPlatform` | `/platform:` | `AnyCpu` |
| `target` | `TargetType` | `/target:`, resolved from the output name when `Auto` | `Auto` |
| `targetfwk` | `string` | target framework to compile against; see [`docs/dotnet-build.md`](dotnet-build.md) | `null`: the `NETFX-TARGET` var, else the SDK's own .NET framework (`net10.0` for SDK 10.0.x) |
| `out` | `File` | output file; `/out:` | `File.undefined` (task infers a target file) |
| `src` | `Fileset` | source files | `Fileset.Empty` |
| `ref` | `Fileset` | adds to the reference fileset (`+`) | -- |
| `refif` | `bool * Fileset` | adds the fileset conditionally (`+?`) | -- |
| `refs` | `Fileset` | replaces the reference fileset | -- |
| `grefs` | `string list` | framework references by file name, resolved against the target framework's reference directory | `[]` |
| `resources` | `ResourceFileset` | adds one embedded-resource fileset (prepended) | -- |
| `resourceslist` | `ResourceFileset list` | adds several at once | -- |
| `define` | `string list` | `/define:`, joined with `;` | `[]` |
| `unsafe` | `bool` | `/unsafe` | `false` |
| `cscpath` | `string` | compiler executable, bypassing framework discovery entirely | `None` |
| `toolset` | `string` (package version) | compiler from `Microsoft.Net.Compilers.Toolset/<version>` in the NuGet cache instead of the SDK's; see below | `None` |
| `resolve` | (none) | returns the `Csc` (`Csc.ofSettings`) instead of compiling; must be the last operation | -- |
| `args` | `string list` | raw extra switches, appended last (`CommandArgs`) | `[]` |
| `nofailonerror` | (none) | do not fail the build on a compile error | `FailOnError = true` |
| `noserver` | (none) | compile in a fresh csc process instead of through the Roslyn compiler server (`VBCSCompiler`); see [csc-server.md](csc-server.md) | `Server = Shared None` (env `XAKE_CSC_SERVER=0` turns it off) |
| `keepalive` | `int` (seconds) | idle time after which a compiler server this build starts exits (`/keepalive`) | Roslyn's own default, 600 |

`out`, `platform`, `unsafe`, `nostdlib` (from `targetfwk`) and `define` are composition
settings; replaying a lock entry (`Lock.compile`, [hermetic/lock.md](hermetic/lock.md)) does not go through them at all. The
argument list `Csc.ofSettings` builds is,
in order: `/noconfig` (when the target framework requires it), `/nologo`, `/target:`,
`/platform:`, `/unsafe`, `/nostdlib+`, `/out:`, `/define:`, sources, `/reference:` refs, global
refs, `/res:`, then `CommandArgs`. The list then goes through `Csc.ofArgs` exactly
like a command line reported by msbuild and gets the same round-trip check (`Csc.Args` must equal
the list it was built from); it always passes, there being one item per switch.

**Composed-mode `.resx` resources.** A `resources`/`resourceslist` fileset entry
that names a `.resx` file is not compiled by `Csc.ofSettings` itself -- unlike an ordinary
embedded-resource file (already the file the compiler reads), a `.resx` needs turning into a
`.resources` first, and `resolve` used to do that eagerly into a temp file with a random name,
deleted once the compile was done. That left nothing for `resolve` to hand back: a lock
recorded from settings with `.resx` resources had `/res:` arguments naming files that no longer
existed. Instead `resolve` records a permanent
`(resx, .resources)` pair in `Csc.Resources` --
`<ProjectRoot>/obj/xake/<assembly name>/<manifestName>` where `manifestName` is the `/res:`
logical name `Impl.makeResourceName` computes (e.g. `Sample.Application.Strings.resources`) --
and emits `/res:<resourcesPath>,<manifestName>`, exactly the shape `Project.import`
([hermetic/lock.md](hermetic/lock.md)) produces for an imported project's resx. `run`'s existing resource step (see below) then compiles
it when the output is missing and `needFiles` the resx itself, so the engine decides when a resx
edit reruns the compile, not `resolve`. Non-resx resources are unaffected -- they were already
the file the compiler reads and stay a plain `/res:` file input. `resolve` no longer produces any
temp files of its own; the only temp file `run` still cleans up is its own response file.

### Composed-mode outputs: `runtimeconfig.json`

A rule is declared for the files it produces, and a compilation writes no file the rule did not
declare. The output is the first of the rule's targets that is not a `.runtimeconfig.json` (or
`out`); a `<output>.runtimeconfig.json` among the targets, as in
`["hw.dll"; "hw.runtimeconfig.json"] *..> csc { ... }`, is the one extra output a composed
compilation knows how to write -- without it `dotnet hw.dll` cannot start.

`resolve` (`Csc.ofSettings`/`Fsc.ofSettings`) checks it against the output and the framework and
records its absolute path in the compilation's `RuntimeConfig` field (`None` when none is
declared; not part of `Generated`, which holds inputs). The checks, all at resolve time:

- the name must be the output's: `app.dll` goes with `app.runtimeconfig.json`, anything else
  fails naming both;
- the framework must be `netN.0`: for `netstandard` or .NET Framework it fails with
  "'hw.runtimeconfig.json' is declared as a target, but net-4.6.2 applications do not use one";
- the compilation must be an application: `target Auto` becomes `Exe` (so a `.dll` output is
  an executable), `Exe`/`WinExe`/`AppContainerExe` stay, a library or module target fails.

`run` writes the file after the compiler succeeds, only when it is missing or its content
differs, with the content `dotnet build` writes by default: `tfm`, the `Microsoft.NETCore.App`
framework at `<major>.0.0`, and the `EnableUnsafeBinaryFormatterSerialization: false` config
property; no roll-forward settings, no timestamps.

There is no declaration outside a file rule: record syntax with `out` in a phony action writes
no runtimeconfig, and neither does a single-target rule (`"hw.exe" ..> csc { ... }`).

### Composed-mode reference assemblies

`targetfwk` (else `NETFX-TARGET`, else the SDK's own framework) decides where the framework's
reference assemblies come from. For .NET (`net8.0`, `net10.0`) every `*.dll` of the targeting
pack is referenced, as the SDK does; for netstandard and .NET Framework `mscorlib.dll` is. Every
`grefs` name is looked up there too. They become ordinary `/reference:` entries of the
resolved `Csc` (`Dependencies.References`), hashed and locked like any other reference.

The default, with neither `targetfwk` nor `NETFX-TARGET`, is the .NET framework of the SDK the
build runs on -- the one `dotnet --version` selects in the project root, `global.json`
honoured -- read from the SDK's `Microsoft.NETCoreSdk.BundledVersions.props`
(`BundledNETCoreAppTargetFrameworkVersion`): `net10.0` for SDK 10.0.x. Only when that cannot
be determined (no SDK) does the compile fail with `csc needs a target framework`.

| `targetfwk` | Reference assemblies | Version |
|---|---|---|
| `net10.0`, `net6.0`, ... (also `sdk-net10.0`) | `<dotnet>/packs/Microsoft.NETCore.App.Ref/<v>/ref/<moniker>`, the targeting pack installed with the SDK, when there is one; else `microsoft.netcore.app.ref/<v>/ref/<moniker>` in the package folder, restored | the `TargetingPackVersion` the SDK's `BundledVersions.props` names for the framework, a `NETCORE_REF_VERSION` entry (a list such as `6.0.36;7.0.20`, one per major.minor) pins that framework's pack to the NuGet package at exactly that version, never an installed one (under `HERMETIC=on` every `netN.0` target used must have an entry); otherwise an installed pack of another patch version is used rather than downloading. A framework the SDK does not know fails |
| `netstandard2.0` | `NETStandard.Library/<v>/build/netstandard2.0/ref` in the package folder | `2.0.3`, or the script variable `NETSTANDARD_LIBRARY_VERSION` |
| `netstandard2.1` | `NETStandard.Library.Ref/<v>/ref/netstandard2.1` in the package folder (the same files as the SDK's `packs/NETStandard.Library.Ref/2.1.0`) | `2.1.0`, or the script variable `NETSTANDARD_LIBRARY_REF_VERSION` |
| `net-4.6.2`, `net472`, ... (SDK provider) | `Microsoft.NETFramework.ReferenceAssemblies.<moniker>/<v>/build/.NETFramework/v4.x` (and its `Facades`) | `1.0.3`, or the script variable `NETFX_REFERENCE_ASSEMBLIES_VERSION` |

- The version is exact. A different version that happens to be in the cache is not used.
  The defaults are constants in `DotNetFwk` (`defaultReferencePackVersions`): 2.0.3 is the
  last `NETStandard.Library` 2.0 release, 2.1.0 the only `NETStandard.Library.Ref` release and
  1.0.3 the last `Microsoft.NETFramework.ReferenceAssemblies` release, so a cache that already
  had the newest version resolves the same paths as before. The variables are read through
  `getVar`, so changing one reruns the compile.
- When the package is not in the folder, `DotNetFwk.resolveFramework` fetches it with
  `DotNetFwk.restorePackage`, the same mechanism `toolset` uses: one synthesized
  `PackageDownload` project under `<ProjectRoot>/obj/xake/restore/<n>/`, so the repository's
  `nuget.config` and private feeds apply. The folder is `NUGET_PACKAGES`, else
  `~/.nuget/packages`.
- A failed download fails the build with
  `restoring 1 package(s) into '<folder>' failed with exit code 1 (see '<project>'): NETStandard.Library 2.0.3`.
  A package that downloads but has no reference assemblies where they are expected fails
  with a message naming the package and version.
- `DotNetFwk.locateFramework` (no build context, no script variables) uses the default
  versions and restores the same way, from a synthesized project under the directory it was
  given (the current directory for `locateFramework`).
- On Windows a .NET Framework found through the registry still wins over the package, and
  the package is fetched only when the SDK provider is the one consulted.
- In a lock the references read
  `$(NuGetPackageRoot)/netstandard.library/2.0.3/build/netstandard2.0/ref/...`. The path is
  the same as before this change, as long as the cache held the default versions.
- An installed .NET targeting pack is under the SDK installation, so in a lock its references
  read `$(DotnetRoot)/packs/Microsoft.NETCore.App.Ref/10.0.12/ref/net10.0/...`; a restored
  one (e.g. `net6.0` on a machine with only SDK 8 and 10) reads
  `$(NuGetPackageRoot)/microsoft.netcore.app.ref/6.0.36/ref/net6.0/...`. The lock therefore
  depends on where the pack came from; pin the framework with `NETCORE_REF_VERSION` (for
  example `6.0.36;7.0.20`) for a lock whose references are all NuGet-restorable: a pinned
  framework's pack always comes from NuGet, installed or not. See
  [dotnet-build.md](dotnet-build.md).

## From a project description: `project`, `config`, `packages`

`Xake.Hermetic.Dotnet` adds three operations to `csc {}` and `fsc {}` that build a compilation
from a project description (`xake.project.json`, [hermetic/description.md](hermetic/description.md))
instead of listing sources and defines in the script. `project "path"` takes the description file
(relative to the project root) and must follow every ordinary setting it builds on (`targetfwk`,
`out`, `ref` for the outputs of `projectRefs`); after it only `config`, `packages`, `lock`,
`nofetch` and `resolve` may come. Code: `src/hermetic/Described.fs`.

```fsharp
open Xake.Hermetic.Dotnet

"out/Release/netstandard2.0/Xake.Dotnet.dll" ..> recipe {
    let! d = Description.load "src/dotnet/xake.project.json"
    let! pkgs = Described.restore d "netstandard2.0"            // msbuild restore; lock generation's step
    do! fsc {
        targetfwk "netstandard2.0"
        ref !!"out/Release/netstandard2.0/Xake.dll"            // the projectRef's output: the script's layout
        project "src/dotnet/xake.project.json"
        config "Release"
        packages pkgs
        lock "locks/Xake.Dotnet/Release.json"
    }
}
```

| Operation | Argument | Does |
|---|---|---|
| `project` | `string` | the description at that path: sources (F# in order; C# as masks), defines, framework `references` (plus the SDK's implicit ones for .NET Framework), `byFramework` additions, options, `kind` as `target`, `internalsVisibleTo` and `assemblyAttributes` as one generated source, resources (`.resx` compiled), `noWarn` with the SDK's defaults, `doc` as `<out>.xml`, `/deterministic+` and `/pathmap:<project root>/=/_/` |
| `config` | `"Debug"` \| `"Release"` | the SDK's configuration preset (below); without it no preset applies |
| `packages` | `Described.Restored` | the description's packages as `Described.restore` (or `restoreWith`) resolved them; required when the description has packages for the framework; every reference of the restore is passed, and its `FrameworkReferences` on .NET Framework |

- **Framework.** `targetfwk` (it must be one of the description's `frameworks`), else the
  description's only framework, else `NETFX-TARGET`; otherwise it fails listing the frameworks.
  The SDK's implicit framework symbols are added (`NETSTANDARD2_0`, `NET462`, `..._OR_GREATER`,
  `Described.frameworkDefines`), as are the SDK's implicit framework assemblies for .NET Framework
  (`System`, `System.Core`, `System.Xml`, ..., `Described.implicitFrameworkReferences`); a C#
  netstandard compilation also references `netstandard.dll`.
- **Presets** (`Described.preset`; SDK 10.0.401 `Microsoft.NET.Sdk.props`,
  `Microsoft.FSharp.NetSdk.props` and the `ImplicitConfigurationDefine`, checked against the
  compiler command lines of `dotnet build -c Debug|Release`):

  | | F# | C# | Defines |
  |---|---|---|---|
  | Debug | `--debug+ --debug:portable --optimize- --tailcalls-` | `/debug+ /debug:portable /optimize-` | `TRACE`, `DEBUG` |
  | Release | `--debug:portable --optimize+` | `/debug- /debug:portable /optimize+` | `TRACE`, `RELEASE` |

  The SDK's configuration-independent switches (`--warn:3`, `/highentropyva+`,
  `/filealign:512`, ...) are not carried: byte identity with `dotnet build` is not a goal. The
  SDK's `NoWarn` is: `/nowarn:` is the SDK's defaults (`Described.sdkNoWarn`: C# `1701,1702`, F#
  none) then the description's `options.noWarn`; `/warnaserror+` (`--warnaserror+`) only when
  `options.warningsAsErrors`.
- **Packages** are never resolved by the compiler. `Described.restore d framework` writes
  `obj/xake/restore/<framework>-<hash>/restore.csproj` (one folder per package set; one `PackageReference` per package, the
  version as the description states it, floating left to msbuild; for F# on .NET Framework
  4.0-4.7 the SDK's implicit `System.ValueTuple` 4.6.2), runs `dotnet restore` on it into the
  build's package folder (`NUGET_PACKAGES`) -- unless the build restored the same set already,
  or `restoreAll` did in one batch: then the build's cache answers without a process -- and reads `project.assets.json`
  (`Nuget.readAssets`, `Nuget.compileItems`): `References` are the compile assets of the whole
  graph, `Packages` the graph as a lock entry records it (`Lock.packagesOf`), `FrameworkReferences`
  the packages' `frameworkAssemblies` for the framework (DocumentFormat.OpenXml on net472:
  `WindowsBase`), which `project` references by name for a .NET Framework target, as msbuild
  does (CS0012 otherwise). A description with packages and no `packages` fails, naming
  `Described.restore`.
- **Packages of project references** (no lock): msbuild flows a referenced project's packages to
  the referrer; a description lists only its own. `Described.restoreWith descriptionOf d
  framework` restores `d`'s packages plus those of the closure of its `projectRefs` (each
  description read at `descriptionOf name`, the script's own name-to-path mapping, the one its
  lock rule hands `Locks.generate`; for each, its nearest framework; declared versions; not
  `private` ones; an id `d` declares wins). Lock generation flows them from the referenced
  locks instead.
- **The direct route at one restore** (no lock): `Described.restoreAll descriptionOf
  descriptions frameworks` restores every description's `restoreWith` set in one `dotnet
  restore` before the targets need them, and `Described.closure descriptionOf d` lists the
  transitive project references (a project after the ones it references) the script passes with
  `ref`:
  ```fsharp
  "main" => recipe {
      do! Described.restoreAll descOf [ for n in names -> descOf n ] fwks
      do! need [ for n in names do for f in fwks -> dllOf "Release" f n ] }
  target "out/(cfg:*)/(fwk:*)/(name:*)/*.dll" {
      // ... cfg, fwk, name from getRuleMatch
      let! d = Description.load (descOf name)
      let! pkgs = Described.restoreWith descOf d fwk               // from the cache
      let! refs = Described.closure descOf d
      do! need [ for r in refs -> dllOf cfg fwk r ]
      do! csc { targetfwk fwk; ref (refs |> List.fold (fun fs r -> fs ++ dllOf cfg fwk r) Fileset.Empty)
                project (descOf name); config cfg; packages pkgs } }
  ```
- **Directory.** The compiler runs in the project's folder (`Csc/Fsc.Directory`, a lock's
  `Directory`), as under `dotnet build`: a relative `<include file=...>` resolves.
- **C# masks.** A C# description's `sources` are Xake masks with the import's convention: a `!`
  prefix is an exclude, applied in order (`["**/*.cs", "!obj/**/*", "!bin/**/*"]`), and
  `dir/**/*` is every file below `dir`. `resolve` returns a `Csc` whose `Sources` are those
  masks made absolute (the generated attributes file first); `lock` records them as
  they are and compares them as written; compiling expands them (`Described.expand`: one fileset
  based at the first include's fixed folder, through `getFiles`, so the target depends on the
  listing and an added `.cs` rebuilds without touching the lock).
- **Resources**: `--resource:`/`/resource:` with the given `name`, else msbuild's default
  (`Described.manifestName`: the root namespace, else the project name; the folder as an
  identifier; the file name, a `.resx` as `<name>.resources`). A `.resx` is embedded as the
  `.resources` it compiles to, `obj/xake/<assembly>/<framework>[/<config>]/<manifest>`: the pair
  (resx, `.resources`) goes into `Csc.Resources`/`Fsc.Resources` (and so the lock: the resx path,
  the manifest in the switch, no hash), and the runner compiles it with `Xake.Dotnet.Resx` (string
  entries) every time it runs (the resx is an input, so an edit reruns it), writing the
  `.resources` only when its bytes change. Xake 3.7's runner compiled a missing one only; while
  the hermetic package's floor is 3.7, `Described.refreshResources` (run by `project` and
  `Locks.compileAs`) deletes one older than its resx first.
- **Generated attributes.** One source per compile, `obj/xake/<assembly>/<framework>/`: the
  `internalsVisibleTo` friends, then `assemblyAttributes` (`[assembly: <type>(<args>)]`), in the
  description's order. It is `<assembly>.InternalsVisibleTo.cs|fs` while it holds friends only
  (so existing locks keep their text), `<assembly>.AssemblyAttributes.cs|fs` otherwise.
- **No `-o` decision here.** The output is `out` or the rule's target, as for composed settings;
  leaving `-o` out of a lock is the lock generation's business.

## Compiler sources

### Composed mode: three sources, first match wins

| Setting | Compiler | Notes |
|---|---|---|
| `cscpath "<exe>"` | that executable | no framework or package lookup at all |
| `toolset "<version>"` | `csc.dll` from `microsoft.net.compilers.toolset/<version>/tasks/netcore/bincore` in the NuGet cache | the package is fetched into the machine's NuGet cache if missing (`DotNetFwk.restorePackage`); still missing afterwards fails the build |
| neither | whatever `targetfwk` / `NETFX-TARGET` resolves through `DotNetFwk.resolveFramework` | the SDK's `csc.dll`, or `csc.exe` / `mcs` on a framework that ships one |

- `toolset` replaces only the compiler. References, defines and environment variables still
  come from the targeted framework.
- A lock records the compiler that ran (`Dependencies.Compiler`: path, SHA-256, version) and
  `Csc.run` verifies it; see [hermetic/lock.md](hermetic/lock.md).

Under `HERMETIC=on` (the script variable; see [dotnet-build.md](dotnet-build.md#hermetic-mode))
`Csc.ofSettings` ends with a check that every path of the composed compilation -- the compiler
(and `cscpath`), references, analyzers, sources, resources, outputs, generated files -- lies under
the project root or the package folder the `NUGET_PACKAGES` script variable names. A composed csc
has no prerequisite: the SDK's `csc.dll` fails the check and asks for `CSC_TOOLSET` (or `toolset`),
an installed `packs/Microsoft.NETCore.App.Ref` asks for a `NETCORE_REF_VERSION` entry, and any
other path outside both roots is named. All violations are reported together. `Csc.hermeticInputs`
lists the paths the check sees.

## The runner: `Csc.run`

`Csc.run` (`Csc.fs`) is the one runner for C#, shared by every entry point. Obtaining anything
that is missing (packages, a revision token) is not its business: `Lock.compile` does that before
it hands the `Csc` here. Since 3.5 the steps below live in an internal `CompilerRunner.run`
over a `RunPlan` record that `Csc.run` fills. It does, in order (after a
`trace Info "compiling '<name>' (<tool> <version>)"`):

1. A compiler that does not exist (with no `RunOptions.CscPath`) is traced as an error and fails
   the build when `FailOnError` is set (`'<name>': the compiler <path> does not exist`).
2. `needFiles` the compiler itself: the hash check
   below covers the compiler's path, but nothing before this made it a tracked
   dependency, so an SDK or toolset update that changed `csc.dll`'s bytes left the target looking
   up to date and the hash check never ran.
3. Writes back every `Generated` file that is missing or whose content differs from what is on
   disk -- the resolved compilation is the source of truth for msbuild-generated inputs like
   `AssemblyInfo.cs` (and `sourcelink.json` with its token already resolved, see [hermetic/lock.md](hermetic/lock.md)). The
   composed mode never populates `Generated`, so this is a no-op there.
4. Creates the output directories, for every path `CscArgs.outputs args` names.
5. `needFiles` every resx in `Csc.Resources` (so a resx edit rebuilds the dll) and, for each
   `(resx, resources)` pair, compiles the resx to that `.resources` path with `Xake.Dotnet.Resx`
   when the output is **missing** -- so a machine with only the lock, or a cleaned `obj/`, still
   ends up with the exact file the recorded `/resource:` switch names. This is the same step for
   both modes: the composed mode's `resolve` records its own `.resx` resources
   here too (see the composed-mode resx paragraph above), at a permanent path under
   `obj/xake/<name>/`, instead of compiling them itself into a temp file at recipe time. This
   dropped a `.resources`-vs-`.resx`
   timestamp comparison that used to gate regeneration as well: that
   was a second rebuilder living next to the engine's -- the engine already decides whether this
   recipe runs at all, from the `FileDep` `needFiles` puts on the resx, so `run` re-deciding with
   its own mtime check was redundant and, worse, implied a staleness check the lock does not
   actually make. **Gate semantics, stated plainly**: the engine decides *whether* a recipe runs
   (dependency tracking, `needFiles`/`FileDep`); `run`'s hash and existence checks only decide
   whether to *fail* a run that the engine already started -- they never trigger one. A swapped
   file with an unchanged timestamp is still caught (the hash check runs every time `run` runs
   for other reasons); a swapped file that also updates the timestamp is caught because the
   timestamp change is what makes the engine run `run` in the first place.
6. `needFiles` on `CscArgs.inputs args` -- every file any input switch names, plus the
   sources. For the composed mode this covers everything the args name, including the
   framework's global references, not only sources/refs/resources. It comes after step 5
   because the `.resources` files named by `/res:` have no rule and must already exist, and
   before the hash check because a reference produced by another rule of the script is
   (re)built here: the check has to see the file the compiler is about to read. A hashed file
   that is missing and that no rule of the script produces cannot be obtained this way, so just
   before this step every such file is reported at once, as a hash mismatch (`expected <sha>,
   got missing`), instead of `needFiles` stopping at the first with "Neither rule nor file".
7. Verifies the SHA-256 of every hashed reference, analyzer, and the compiler itself against what
   is on disk. An empty recorded hash means "not checked" (the composed mode never records one,
   and neither does an unbuilt project reference). Any mismatch is collected and reported
   together (`('<name>') hash mismatch:` then one line per path), then fails the build when
   `FailOnError` is set (`XakeException`, message containing the path).
8. Writes the arguments to a response file, with `Impl.escapeArgument`.
    `/noconfig` cannot go inside the rsp -- csc warns `CS2023` and ignores it there -- so it stays
    on the command line and everything else goes into `@<rspfile>`.
9. Picks the compiler: `RunOptions.CscPath` wins if set; otherwise, when the `Csc`'s recorded
    compiler path ends in `.dll`, it runs through `dotnet <path>`; otherwise the path is run
    directly (a native launcher, e.g. a `cscpath` naming the SDK's `csc` apphost). The composed
    mode records the SDK's `Roslyn/bincore/csc.dll` rather than the `csc` launcher the framework
    probe returns next to it (`Csc.managedCompiler`), the same compiler an imported lock
    hashes; a `cscpath` override is used verbatim.
10. Adds the compiler-server switches ([csc-server.md](csc-server.md)) -- `/shared`,
    plus `/keepalive:<s>` when `RunOptions.Server` names one -- on the command line, ahead of
    `/noconfig` and the `@rsp`, never in the rsp and never in the lock: csc parses them out on the
    client side before anything reaches `VBCSCompiler`, so `Csc.Args` is unchanged by them. Only when a
    `VBCSCompiler.dll` sits next to the compiler file about to run (the SDK's `Roslyn/bincore`,
    a toolset package's), `RunOptions.Environment` is empty, and the compiler is not under the temp
    directory (`serverArgs`); the legacy `csc.exe`, `mcs`, an arbitrary `cscpath` and a
    mono/registry toolchain compile in-process as before.
11. Runs the compiler with the working directory set to `Csc.Directory` (what `dotnet
    build` uses; an XML-doc `<include file='..'>` resolves against it), `RunOptions.Environment`
    as env vars (empty on `RunOptions.Default`), and fails on a non-zero exit when
    `FailOnError` is set.

The rsp file is deleted once the compiler exits, success or failure -- the only temp file `run`
produces now that the composed mode's `.resx` resources are permanent outputs (see above),
not temp files.

## Behaviour notes

- The one-resolved-form refactor changed composed mode in two ways: it now creates the output
  directory before compiling (previously a sample needed the directory to pre-exist), and it
  `needFiles` everything the args name, including framework global references, not only the
  sources/refs/resources it used to `needFiles` directly.
- `/noconfig` has to stay on the command line, never in the rsp: inside the rsp csc emits
  `CS2023` and ignores it.
- A hash mismatch reports every mismatching path at once, `"<path>: expected <hash>, got
  <hash-or-\"missing\">"`, one line per path, and fails when `FailOnError` is set.

## Not yet

- `Resx.read`/`compile` only support plain string entries (`<data name="X"><value>...</value></data>`).
  A typed value (a `type` attribute) or a `ResXFileRef`/binary value (`mimetype`) fails with a
  clear message rather than being compiled; the real projects this targets have none.
