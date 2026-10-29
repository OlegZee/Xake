# Xake tasks

The examples below use current API style:

```fsharp
#r "nuget: Xake"
open Xake
open Xake.Tasks
```

## Shell tasks

### sh recommended

`sh` fails on non zero exit code by default.

```fsharp
do! sh "dotnet build src/core -c Release" { () }
```

`{ () }` is the empty settings block. The shorter `{}` also works, but only from F# 9 onward;
`{ () }` compiles on every SDK the package supports.

With arguments and working directory:

```fsharp
do! sh "dotnet" {
  args ["test"; "src/tests"; "-c"; "Release"]
  workdir "."
  failonerror
}
```

Capture output:

```fsharp
let! code, lines = sh "dotnet --list-sdks" { resultAndOutput }
```

## Copy tasks

Copy by file mask:

```fsharp
do! cp {file "bin/*.dll"; todir "deploy"}
```

Copy directory tree:

```fsharp
do! cp {dir "bin"; todir "deploy"}
```

Copy from fileset:

```fsharp
do! cp {
  files (fileset {
    basedir "bin"
    includes "*.dll"
    includes "*.exe"
  })
  todir "deploy"
}
```

`dir`, `file` and `files` may repeat and combine in one `cp`; sources copy in order (a `file`
source is always flattened):

```fsharp
do! cp {
  dir "bin"
  dir "content"
  file "README.md"
  todir "deploy"
}
```

Other copy helpers:

```fsharp
do! copyFile "src/App.config" "out/App.config"
"out/config.json" ..> copyFrom "src/config.json"
```

## Remove tasks

Delete file or mask:

```fsharp
do! rm {file "temp/*.tmp"}
```

Delete directory:

```fsharp
do! rm {dir "out"}
```

Delete files from fileset:

```fsharp
do! rm {
  files (fileset {
    basedir "out"
    includes "**/*.cache"
  })
  verbose
}
```

`dir`, `file` and `files` may repeat and combine in one `rm`; targets apply in order:

```fsharp
do! rm {
  dir "out"
  dir "obj"
  file "*.tmp"
}
```

## .NET tasks

The `csc`, `fsc`, `msbuild`, `resgen` and `resourceset` builders live in `Xake.Dotnet`, which
ships in the same package as the engine:

```fsharp
open Xake
open Xake.Dotnet
```

### csc

With no `out`, the compiler writes to the rule's target file:

```fsharp
"helloworld.exe" ..> csc { src !!"helloworld.cs" }
```

csc writes only the output. An application for `netN.0` (the SDK default) also needs
`<output>.runtimeconfig.json` for `dotnet <output>` to start it; Xake writes it when, and only
when, the rule declares it as a target next to the output:

```fsharp
["hw.dll"; "hw.runtimeconfig.json"] *..> csc { src !!"hw.cs" }   // dotnet hw.dll
```

The content is what `dotnet build` writes. Declaring it makes the compilation an executable
(`target Auto` with a `.dll` output included); it fails for a library target, for a
`netstandard` or .NET Framework target ("'hw.runtimeconfig.json' is declared as a target, but
net-4.6.2 applications do not use one"), and for a name that is not the output's. A single
target, or record syntax with `out` outside a file rule, writes none.

`targetfwk` picks the framework to compile against; it is optional. The first that is set
wins: `targetfwk`, the `NETFX-TARGET` script variable, the .NET framework of the SDK the build
runs on (`net10.0` for SDK 10.0.x, the SDK a `global.json` in or above the project root
selects). A .NET target `netN.0` (`net6.0`, `net8.0`, `net10.0`, also spelled `sdk-net10.0`)
works for any N the SDK knows, and compiles against all of that framework's targeting pack:
the one installed with the SDK (`<dotnet>/packs/Microsoft.NETCore.App.Ref`) when there is
one, else the NuGet package `Microsoft.NETCore.App.Ref` at the version the SDK names for the
framework, restored once into the package cache as `dotnet build` does (the script variable
`NETCORE_REF_VERSION`, a list such as `6.0.36;7.0.20` separated by `;`, `,` or whitespace, pins the
pack of the framework with the same major.minor: it always comes from NuGet at that version, never
from an installed pack, so lock paths are the same on every machine; a framework without an
entry follows the rule above; under `HERMETIC=on` every `netN.0` target used must
have an entry; see [dotnet-build.md](dotnet-build.md)). The example above, on the SDK's own framework,
therefore needs nothing but the SDK.

For a .NET Framework target, on Windows a Framework installation found through the registry
wins; everywhere else -- and as a fallback -- the compiler comes from the .NET SDK and the
reference assemblies from a NuGet package, so a full-framework binary can be built on any OS
with nothing pre-installed beyond the SDK:

```fsharp
"temp/helloworld.exe" ..> csc {
  targetfwk "net-4.6.2"
  src !!"helloworld.cs"
  grefs ["System.dll"]
  define ["TRACE"]
}
```

Every operation of `csc {}`:

| Operation | Argument | Effect |
|---|---|---|
| `out` | `File` | output file (default: the rule's target) |
| `target` | `TargetType` | `/target:`; resolved from the output name when left `Auto` |
| `platform` | `TargetPlatform` | `/platform:` (default `AnyCpu`) |
| `targetfwk` | `string` | framework to compile against: `net10.0`, `netstandard2.0`, `net-4.6.2`, ... (default: the `NETFX-TARGET` variable, else the SDK's own .NET framework) |
| `src` | `Fileset` | source files |
| `ref` / `refs` / `refif` | `Fileset`; `refs` replaces, `refif` takes `bool * Fileset` | references from files |
| `grefs` | `string list` | framework references by file name (e.g. `["System.dll"]`), resolved against the target framework's reference directory; not needed for a .NET target, whose whole targeting pack is referenced |
| `resources` / `resourceslist` | `ResourceFileset` / list | embedded resources; a `.resx` is compiled to `.resources` first |
| `define` | `string list` | conditional compilation symbols |
| `unsafe` | `bool` | allow unsafe code |
| `args` | `string list` | extra compiler arguments, appended last |
| `nofailonerror` | -- | a compile error does not fail the build |
| `cscpath` | `string` | run this compiler instead of the one the framework provides; overrides `toolset` |
| `toolset` | `string` (version) | take `csc.dll` from `Microsoft.Net.Compilers.Toolset/<version>` in the build's package folder, restoring the package first when it is missing, instead of the SDK's own compiler; overrides the `CSC_TOOLSET` variable |
| `noserver` | -- | compile in a fresh compiler process instead of through the Roslyn compiler server |
| `keepalive` | `int` (seconds) | idle time after which a compiler server this build starts exits (`/keepalive`; Roslyn's default is 600) |
| `throttle` | `Resource * int` | launch the compiler under this resource of the script, that many units per compile, in place of the `CSC_JOBS` limit; taken around the launch only, after every input is built |
| `resolve` | -- | return the resolved `Csc` instead of compiling; must be the last operation (see below) |

`lock "path"` is not part of the base package: it is added by the separate package
`Xake.Hermetic.Dotnet` (`open Xake.Hermetic.Dotnet`), and gates the compilation against a lock
file; see [hermetic/lock.md](hermetic/lock.md). The same package adds `project "path"`, `config`
and `packages` to `csc {}` and `fsc {}`: a compilation from a project description, with the
SDK's Debug/Release preset and packages restored beforehand by `Described.restore`; see
[csc-syntax.md](csc-syntax.md#from-a-project-description-project-config-packages). Details
behind the operations above: [csc-syntax.md](csc-syntax.md).

`toolset` example, see [samples/toolset.fsx](../samples/toolset.fsx):

```fsharp
"temp/helloworld.exe" ..> csc {
  toolset "4.11.0"
  targetfwk "net-4.6.2"
  src !!"helloworld.cs"
  grefs ["System.dll"]
}
```

#### Record syntax: `Csc.compile`

`Csc.compile` takes a `CscSettingsType` (`CscSettings` holds the defaults) and returns the
recipe that compiles; it is what `csc { ... }` itself runs. It replaces the function
`Csc settings` of 3.3, which no longer exists: `do! Csc settings` is now
`do! Csc.compile settings`.

```fsharp
"hw.exe" ..> recipe {
  do! Csc.compile { CscSettings with Src = !! "hw.cs" }
}
```

#### Resolving without compiling: `resolve`, `Csc.run`

`csc { ...; resolve }` stops before the compiler and returns the resolved compilation, a
`Csc` record (name, framework, directory, options, defines, sources, hashed dependencies), in a
recipe; record-syntax settings go through `Csc.ofSettings settings`. `Csc.run options c` compiles
such a record and is the only function that starts the compiler:

```fsharp
recipe {
  let! c = csc { src !!"helloworld.cs"; out (File.make "temp/hw.exe"); resolve }
  do! Csc.run RunOptions.Default c
}
```

`resolve` has to come last: an operation after it does not compile. Outside a file rule there is
no target to take the output from, so `out` must be set. `Csc.run` fails the build when a hashed
reference, analyzer or the compiler differs from its recorded SHA-256 (an empty hash is not
checked, and a composed compilation has none).

`RunOptions` (default `RunOptions.Default`):

| Field | Meaning | Default |
|---|---|---|
| `FailOnError` | a compile error fails the build | `true` |
| `CscPath` | compiler executable overriding the one the `Csc` names | `None` |
| `Server` | `Shared of keepAlive: int option` (`/shared`, plus `/keepalive:<n>`) or `InProcess` | `Shared None`, unless env `XAKE_CSC_SERVER` is `0`, `false`, `no` or `off` |
| `Environment` | environment variables of the compiler process | `[]`; the framework's own on the mono and Framework paths |
| `Throttle` | `(Resource * int) option`: a resource of the script and the units one compile takes; the launch acquires it in place of the `CSC_JOBS` limit, which is then not read | `None` (`CSC_JOBS`) |

`Csc.runOptions settings` builds the options `csc {}` runs with from composed settings: the
settings' `FailOnError`, `CscPath`, `Throttle`, the resolved server and the target framework's
environment.

#### Compiler server

Compiles go through the Roslyn compiler server (`VBCSCompiler`) when one sits next to the
compiler, so a build with many `csc` targets does not pay the compiler's startup each time. Turn
it off or tune it in one place for the whole script with the `CSC_SERVER` variable, or per target
with `noserver` / `keepalive`. Precedence, first that is set wins:

1. the target's `noserver` or `keepalive`;
2. the script variable `CSC_SERVER`: `on`, `off`, or a number of keepalive seconds
   (case-insensitive; anything else logs a warning and counts as `on`);
3. the environment variable `XAKE_CSC_SERVER` (`0`, `false`, `no`, `off` turn the server off);
4. the server, with Roslyn's own keepalive.

The switch is silently skipped (the compile runs in-process) for a compiler that has no
`VBCSCompiler.dll` next to it -- the legacy `csc.exe`, mono's `mcs`, an arbitrary `cscpath` --
for toolchains that need environment variables, and for a compiler under the temp directory.
`CSC_SERVER` is read like `NETFX`, so changing it rebuilds the target. Details:
[csc-server.md](csc-server.md).

#### Compiler toolset for the whole script

The script variable `CSC_TOOLSET` is `toolset` for every `csc` target that names none: a
`Microsoft.Net.Compilers.Toolset` version, restored the same way. Precedence: the block's
`toolset`, then `CSC_TOOLSET`, then the SDK's compiler (an empty value counts as unset);
`cscpath` still overrides all three. Setting it project-wide is how a build gets a compiler
that does not depend on what is installed: every compile, and so every lock entry recorded from
it, then names a restorable NuGet compiler with a fixed version instead of whichever SDK the
machine happens to have. Like `CSC_SERVER` it is read as a variable, so changing it rebuilds
the targets that read it.

The SDK compiler is the one from the SDK a `global.json` in (or above) the project root selects,
as `dotnet --version` there reports it; with no `global.json`, the newest SDK installed.

How the compiler and the reference assemblies are located, and how to force a particular
toolchain, is described in [dotnet-build.md](dotnet-build.md).

### fsc

Same shape as `csc`: the operations below, `resolve`, record syntax through `Fsc.compile`.
A `.runtimeconfig.json` declared as a target next to the output is written as for csc
(`["hw.dll"; "hw.runtimeconfig.json"] *..> fsc { src !!"hw.fs" }`).
`targetfwk` is optional, as for csc: with none (and no `NETFX-TARGET`) the target is the SDK's
own .NET framework, and the SDK's `FSharp.Core.dll` is referenced. This needs nothing but the
SDK:

```fsharp
"app.dll" ..> fsc {
  src (fileset { includes "src/*.fs" })
  args ["--utf8output"]
}
```

For netstandard or .NET Framework, name the framework and add framework assemblies by file
name with `grefs`; `FSharp.Core` (and, for .NET Framework, a `netstandard.dll` facade) is
referenced implicitly, so this too needs nothing but the SDK, on any OS:

```fsharp
"app.exe" ..> fsc {
  targetfwk "net-4.6.2"
  src (fileset { includes "src/*.fs" })
  grefs ["System.dll"; "System.Core.dll"]
}
```

| Operation | Value | Meaning |
|---|---|---|
| `targetfwk` | `string` | target framework (`net10.0`, `netstandard2.0`, `net-4.6.2`, ...); default: `NETFX-TARGET`, else the SDK's own .NET framework |
| `src` | `Fileset` | sources, in compile order |
| `out` | `File` | output (default: the rule's target) |
| `target` | `TargetType` | `Library`, `Exe`, ... (default: from the output's extension) |
| `ref` / `refif` / `refs` | `Fileset` | add / add when / set references (`-r:`) |
| `grefs` | `string list` | framework references by file name (`System.dll`, `System.Core.dll`, ...), resolved against the target framework's reference directory and passed as `-r:`; for a .NET target the whole targeting pack is referenced already |
| `resources` / `resourceslist` | `ResourceFileset` | embedded resources; a `.resx` is compiled to `obj/xake/<name>/*.resources` |
| `define` | `string list` | conditional compilation symbols, one `--define:` each |
| `doc` | `File` | xml documentation file |
| `notailcalls` | -- | `--tailcalls-` |
| `noframework` | -- | kept for compatibility; `--noframework` is always passed now |
| `fscver` | `string` | compiler version, Windows registry provider only (also `FSCVER`) |
| `platform` | `TargetPlatform` | recorded, not passed to fsc (it never was) |
| `args` | `string list` | extra arguments, appended last |
| `nofailonerror` | -- | a compile error does not fail the build |
| `throttle` | `Resource * int` | launch the compiler under this resource of the script, that many units per compile, in place of the `FSC_JOBS` limit |
| `resolve` | -- | return the resolved `Fsc` instead of compiling; must be the last operation |

The arguments are always fsc's `--name:value` form (`-r:` for references), on every OS. The
task passes `--noframework` and the framework's reference assemblies as ordinary `-r:`
references: for .NET (`net10.0`, the default, or any `netN.0` the SDK knows) every assembly
of the targeting pack -- installed with the SDK, else restored from NuGet as for `csc` -- plus
`--targetprofile:netcore`, for netstandard `netstandard.dll` plus
`--targetprofile:netstandard`, for .NET Framework `mscorlib.dll` and a `netstandard.dll` facade.
Unless a `ref` already names an `FSharp.Core.dll`, one is referenced: for netstandard and .NET
Framework the `FSharp.Core` NuGet package at `DotNetFwk.fsharpCoreVersion` (8.0.100, what Xake
itself pins) or the script variable `FSHARP_CORE_VERSION`, its `lib/netstandard2.0` build
(`lib/netstandard2.1` for netstandard2.1), restored into the package cache on first use; for a
.NET target the same package's `lib/netstandard2.1` build when `FSHARP_CORE_VERSION` is set,
else the SDK's (next to `fsc.dll`, the version the compiler ships with; `HERMETIC=on` requires
the variable, since the SDK's file cannot be restored). The package build
references `netstandard 2.0.0.0`, so a .NET Framework target also gets a type-forwarding
`netstandard.dll`: the reference assemblies' own `Facades/netstandard.dll` from 4.7.1 on, below
that the one in the `Microsoft.NET.Build.Extensions` 2.2.101 package
(`msbuildExtensions/Microsoft/Microsoft.NET.Build.Extensions/net461/lib/netstandard.dll`,
byte-identical to the SDK's `Microsoft/Microsoft.NET.Build.Extensions/net461/lib/netstandard.dll`),
restored like the others (unless a `ref` names a `netstandard.dll`). `NETStandard.Library`'s `netstandard.dll` is not used there: it
defines the types rather than forwarding them, and fsc then fails on clashes with `mscorlib`.
Every implicit reference is recorded in the resolved `Fsc` like any other. `csc` gets none of
this: it needs no FSharp.Core, and C# compiles for .NET Framework without the facade.

fsc copies the referenced `FSharp.Core.dll` next to the output (`args ["--nocopyfsharpcore"]`
turns that off). It does not copy the facade, and neither does Xake: an fsc-built exe for
.NET Framework below 4.7.2 needs a runtime `netstandard.dll` next to it (from 4.7.2 the
runtime has one; msbuild copies the SDK's facades into such an output, Xake does not).
fsc creates the directory of `--out` and the task that of `doc`.

`define` takes one symbol per switch: unlike `csc`, `fsc` reads `--define:A;B` as a single
symbol named `A;B`, so the task emits a separate `--define:` for each.

The compiler is the SDK's `<sdk>/FSharp/fsc.dll` (the SDK a `global.json` in or above the project
root selects), started as `dotnet fsc.dll`. There is no `fscpath`, and `fscver`/`FSCVER` only
matter to the Windows registry provider (an installed full-framework `fsc.exe`, untested).

#### Record syntax: `Fsc.compile`

`Fsc.compile` takes an `FscSettingsType` (`FscSettings` holds the defaults) and returns the recipe
that compiles; it is what `fsc { ... }` runs. It replaces the function `Fsc settings` of 3.4:
`do! Fsc settings` is now `do! Fsc.compile settings`.

```fsharp
"hw.dll" ..> recipe {
  do! Fsc.compile { FscSettings with Src = !! "hw.fs" }
}
```

#### Resolving without compiling: `resolve`

`fsc { ...; resolve }` (or `Fsc.ofSettings settings`) stops before the compiler and returns the
resolved compilation, an `Fsc` record with the same fields as `Csc` (name, framework, directory,
options, defines, sources, generated files, resx pairs, dependencies with the compiler and the
references; `Analyzers` is always empty). `Options` is in fsc's own spelling with the markers
`@Sources`, `@References`, `@Defines`; `FscArgs` parses and formats fsc command lines the way
`CscArgs` does csc's. `Fsc` has the labels of `Csc`, so constructing one by hand takes qualified
labels (`{ Fsc.Name = ...; ... }`), and `Fsc.ofArgs` builds one from a command line.

`Fsc.run options f` compiles such a record through the same runner as `Csc.run`: generated
files written back, output directories created (`--doc:` included), resx compiled, every input
`needFiles`d, and the SHA-256 of every hashed reference and of `fsc.dll` verified before the
compiler starts (`Fsc.rehash` records them; an empty hash is not checked). The arguments go
into a response file.

```fsharp
recipe {
  let! f = fsc { src !!"hw.fs"; out (File.make "out/hw.dll"); resolve }
  do! Fsc.run FscRunOptions.Default f
}
```

`FscRunOptions` (default `FscRunOptions.Default`; `Fsc.runOptions settings` builds them from
composed settings):

| Field | Meaning | Default |
|---|---|---|
| `FailOnError` | a compile error fails the build | `true` |
| `FscPath` | compiler executable overriding the one the `Fsc` names, run directly | `None` |
| `Environment` | environment variables of the compiler process | `[]`; the framework's own on the mono and registry paths |
| `Throttle` | a resource of the script and the units one compile takes, in place of the `FSC_JOBS` limit | `None` (`FSC_JOBS`) |

There is no compiler server for fsc. Like `FscRunOptions`' labels, which overlap `RunOptions`',
construct it with qualified labels or from `FscRunOptions.Default with ...`.

#### Compiling what a project file describes

A script that wants exactly what `dotnet build` would compile for an existing `.fsproj` does not
re-describe it as `fsc {}` settings: `Project.import` in `Xake.Hermetic.Dotnet` asks msbuild
for the compiler's own command line (a design-time build) and records it in a lock, and
`Lock.compile` replays it through `Fsc.run`. See [hermetic/lock.md](hermetic/lock.md).
`fsc { project "path"; config "Release"; packages p }` builds from a project description
([csc-syntax.md](csc-syntax.md#from-a-project-description-project-config-packages)). (The
`Fsproj` module of Xake.Hermetic.Dotnet 0.1, which evaluated a project's items for a composed
`fsc {}`, is gone in 0.2.)

### msbuild

```fsharp
"build" => msbuild {
  buildfile "MySolution.sln"
  target "Rebuild"
  prop ("Configuration", "Release")
  maxcpu 0            // one process per processor
  verbosity Minimal
}
```

`target`/`prop` accumulate; `targets`/`props` set the whole list at once.

### resourceset and resgen

`resourceset` describes a set of embedded resources and their naming:

```fsharp
let strings = resourceset {
  prefix "Sample.Application"
  dynamic true        // derive the rest of the name from the file location
  files (fileset { includes "**/*.resx" })
}

"app.dll" ..> csc { src !!"src/*.cs"; resources strings }
```

`resgen` compiles resx files to standalone `.resources` on any host, `dotnet fsi` included:

```fsharp
"resources" => resgen { resources strings; targetdir "out" }
```

### Script variables

| Variable | Effect |
|----------|--------|
| `NETFX` | Framework whose tools are used, overriding the target framework |
| `NETFX-TARGET` | Default `targetfwk` for all compiler tasks; unset, they target the SDK's own .NET framework |
| `FSCVER` | F# compiler version `fsc` asks for (Windows registry provider only) |
| `CSC_SERVER` | `csc` compiler server: `on`, `off` or keepalive seconds; overridden by a target's `noserver`/`keepalive`, overrides env `XAKE_CSC_SERVER` |
| `CSC_JOBS` / `FSC_JOBS` | At most this many `csc` / `fsc` processes at once (`0` or unset: unlimited); then env `XAKE_CSC_JOBS` / `XAKE_FSC_JOBS`; not a dependency, changing it rebuilds nothing; a target's `throttle` takes its place (see [csc-server.md](csc-server.md#limiting-concurrent-compiles-csc_jobs-fsc_jobs)) |
| `CSC_TOOLSET` | `csc` compiler package version (`Microsoft.Net.Compilers.Toolset`) for targets with no `toolset`; empty or unset means the SDK's compiler |
| `NUGET_PACKAGES` | The build's package folder, relative to the project root or absolute; overrides the environment variable of the same name for everything Xake restores or reads there (see below) |
| `NUGET_FETCH` | `off` turns off every download into the package folder (a missing package fails the build); default `on` |
| `NETCORE_REF_VERSION` | Pins of the `Microsoft.NETCore.App.Ref` targeting pack, one per major.minor (`6.0.36;7.0.20`); a pinned `netN.0` target always compiles against the NuGet package at that version, never an installed pack (see [csc](#csc)) |
| `NETFX_REFERENCE_ASSEMBLIES_VERSION` | Version of the `Microsoft.NETFramework.ReferenceAssemblies.*` packages .NET Framework targets compile against (default 1.0.3) |
| `NETSTANDARD_LIBRARY_VERSION` / `NETSTANDARD_LIBRARY_REF_VERSION` | Versions of the `NETStandard.Library` (netstandard2.0, default 2.0.3) and `NETStandard.Library.Ref` (netstandard2.1, default 2.1.0) reference packages |
| `FSHARP_CORE_VERSION` | `FSharp.Core` package version `fsc` references by default (8.0.100); for a `netN.0` target only when set, else the SDK's own |
| `HERMETIC` | `on`: every composed `csc`/`fsc` compilation must name only paths under the project root and the `NUGET_PACKAGES` script variable's folder; fsc's compiler may come from an SDK `global.json` pins exactly (`rollForward: disable`). Missing configuration fails at resolve time naming the variable that fixes it. `on`/`off` (also `true`/`yes`/`1`, `false`/`no`/`0`), default `off`; see [dotnet-build.md](dotnet-build.md#hermetic-mode) |

```bash
dotnet fsi build.fsx -- -- build -d NETFX-TARGET:net-4.6.2
dotnet fsi build.fsx -- -- build -d CSC_SERVER:off
```

Names such as `net-4.6.2`, `sdk-net462` and `mono-4.5` select both the framework and, through
the prefix, the provider that supplies the tools. See
[dotnet-build.md](dotnet-build.md#how-to-switch).

#### Where packages go

Xake restores a few NuGet packages itself: the `toolset` compiler (`toolset`, `CSC_TOOLSET`),
the reference packs (`NETStandard.Library` for netstandard2.0,
`Microsoft.NETFramework.ReferenceAssemblies.*` for .NET Framework) and `fsc`'s default
`FSharp.Core`. They go to, and are read from, one package folder:

1. the script variable `NUGET_PACKAGES` -- `var "NUGET_PACKAGES" ".nuget/packages"` in
   `xakeScript`, or `-d NUGET_PACKAGES:.nuget/packages` on the command line (`-d` wins over
   `var`); a relative path is under the project root. The child `dotnet restore` gets it as the environment variable, so
   NuGet puts the packages there. Every compile reads it, so changing it rebuilds them;
2. else the environment variable `NUGET_PACKAGES`;
3. else `~/.nuget/packages`.

`csc { ...; lock "app.json" }` and `fsc { lock }` (Xake.Hermetic.Dotnet) use the same folder
and write it in the lock as `$(NuGetPackageRoot)`; there is no per-target folder.
`DotNetFwk.locateFramework` and
`locateFrameworkIn`, which run without a build, have no script variables and use the
environment's folder.

Turning fetching off: `-d NUGET_FETCH:off` (or `var "NUGET_FETCH" "off"`) makes Xake download
nothing, for CI with a warm cache; a package missing from the folder then fails the build before
the compiler runs, naming the package and version and saying fetching is off. `nofetch` after
`lock` (Xake.Hermetic.Dotnet) is the same for one locked target.

The packages an F# script itself references with `#r "nuget: ..."` are restored by `dotnet
fsi` before the script, and so before Xake, runs: the script variable cannot reach them. Set the
environment variable `NUGET_PACKAGES` for those.

## Inner recipe helpers

Common helpers used inside `recipe`:

- `need`
- `needFiles`
- `dependsOn`
- `trace`
- `getVar`
- `getEnv`
- `getCtxOptions`
- `getTargetFile`
- `getTargetFullName`

Example:

```fsharp
recipe {
  do! dependsOn !! "src/**/*.fs"
  let! cfg = getVar "Config"
  do! trace Info "Config: %A" cfg
}
```

## Script variables

Every variable value is computed once, at launch: after the command line is applied and before
`setup` and the first target (run, `--dryrun`, `--dump`, engine `start`). A recipe only reads. A
read records the final value as one `Var (name, value)` dependency, never where it came from, so
moving an equal value between the environment, `-d` and the script does not rebuild.

The value of a name comes from the first of (first defined wins):

1. the engine's per-demand `Demand(target, vars)` (engine mode only);
2. `-d NAME=value` on the command line (the last `-d` of a name wins);
3. the script's one value: `var "NAME" "value"` or an `ExecOptions.Vars` entry;
4. the name's declaration: a `var` rule or a `varschema` field, its sources in list order.

A **`var` rule** declares where a value comes from:

```fsharp
xakeScript {
    var "CONFIG" "Release"                                            // a constant
    var "NUGET_PACKAGES" [Env "XX_NUGET_PACKAGES"; Default ".packages"]  // first defined source wins
    var "OUT" [Cli "out"; Env "OUT_DIR"; Default "bin"]              // set with -d out=..., not -d OUT=...
    var "TOKEN" [Secret; Env "ACME_API_TOKEN"]                       // a secret, see below
    ...
}
```

`Env "X"` defines the name when the environment variable `X` is present (an empty value
included), `Default "v"` always. `Cli "ext"` gives the name another command-line name; `Secret`
and `SecretHashed` make it a secret. These markers define nothing and may stand anywhere in the
list. A rule with no defining source leaves the name unset (`getVar` returns `None`).

A **name is defined once** in the script: two `var` ops of a name (either form), a `var` op and an
`ExecOptions.Vars` entry, or a `varschema` field and either of them stop the run before any target
(`Variable 'N' is defined twice in the script: by A and by B; define it once`). `-d` and the
engine's per-demand variables are not script definitions.

**Typed variables** are declared in `varschema`; a field is the rule of its sources:
`Var.string(envVar = "X") |> withDefault "d"` as field `N` is `var "N" [Env "X"; Default "d"]`,
plus its type, `required` flag and secret mode:

```fsharp
let vars = {|
    config = Var.string(cliArg = "config") |> withDefault "Debug"   // -d config=..., env CONFIG
    jobs   = Var.int(envVar = "XAKE_JOBS")                          // 't option
    feed   = Var.env<string>(envVar = "PACKAGE_FEED") |> required   // environment only
|}

xake ExecOptions.Default {
    varschema vars          // declares them; names, types, defaults and descriptions in --help
    phony "main" (recipe {
        let! config = vars.config
        ()
    })
}
```

- `Var.create`/`string`/`int`/`bool` take `-d` and the environment; `Var.env` only the
  environment (`-d` of it stops the run), `Var.arg` only `-d`. `describe` adds a description.
- The **key** of a field (what `getVar`, the banner and per-demand variables use) is its `name`,
  else the field name. Its **command-line name** is `cliArg`, else the key: with
  `p = Var.string(cliArg = "pkg")`, `-d pkg=v` sets `p`, and `-d p=v` stops the run
  (`-d p: the variable is set on the command line as pkg; use -d pkg=...`). The environment name
  is `envVar`, else the key in UPPER_SNAKE_CASE (`buildConfig` -> `BUILD_CONFIG`).
- A `required` field without a value, or a value that does not convert to the field's type,
  stops the run before any target. `withDefault` gives a value instead; a typed default is
  returned as is.
- **A `Var` read needs a declaration.** `let! x = vars.config` returns the value of the key from
  the variable set; it reads no environment itself. A `Var.*` value read in a recipe without a
  `varschema` field (or `var` rule) of its key fails that target ("... is read through Var but is
  not declared: declare it in varschema, or use getVar ..."); one whose declaration differs from
  the stored one (another env name, default, type, ...) fails too. Read script values with
  `getVar "N"` and the environment, live, with `getEnv "X"` (recorded as `EnvVar`).

The run banner (at `Info`, in `build.log`) lists each variable once with its source: `cli` (or
`cli:<name>` when given under another command-line name), `script`, `env:<NAME>`, `default`, or
`(not set)`; secrets as `***`. `--help` lists the `varschema` fields and the `var` rules, with
their command-line names, without evaluating anything.

### Secret variables

A token or license key must not end up in the build database (`.xake`) or the log. Mark the
variable secret:

```fsharp
let vars = {|
    AcmeApiToken = Var.secret<string>(envVar = "ACME_API_TOKEN", description = "API token") |> required
    LicenseKey   = Var.string(cliArg = "licenseKey", envVar = "LICENSE_KEY") |> secret
|}
// or as a rule: var "ACME_API_TOKEN" [Secret; Env "ACME_API_TOKEN"]
```

- `Var.secret<'t>(envVar = ...)` reads the environment only; `|> secret` works on any var
  (before or after `required`/`withDefault`); in a `var` rule, `Secret` or `SecretHashed`.
- A secret takes its value only from its own sources: `-d` of it, under its key, its
  command-line name or one of its environment names, stops the run ("secret variables come from
  the environment, set ACME_API_TOKEN instead of passing -d ..."), and so does a script value of
  its name. `Var.arg ... |> secret` thus takes its value only from `withDefault`.
- The database keeps whether the variable was set, never the value (presence only): a rotated
  token rebuilds nothing, unset to set (or back) rebuilds. `|> secretHashed` keeps a salted
  HMAC-SHA256 of the value instead (a random salt stored with the digest), so a changed value
  rebuilds the targets that read it. The change reason names the variable only: "Secret variable
  ACME_API_TOKEN changed" / "... is now set" / "... is now unset".
- `getVar` and `getEnv` of a secret return the real value; masking applies to output only. Every
  logger Xake creates (console, `filelog`, `CustomLogger`, and a custom `ILogger` given in
  the options) replaces each secret value of 4 characters or more with `***`: the banner, the
  `Debug` options dump, change reasons, `--dryrun`, `--dump`, the `shell` settings dump at Debug,
  tool output, errors. A conversion error (a `Var.secret<int>` that does not parse) does not quote
  the value. A value a tool transforms (base64, URL-encoded) is not recognised; values shorter than
  4 characters are not masked.
- `--help` lists a secret with "(secret)", its name, type and description, never a default.
- The name decides: a declared secret's key and environment names are secret for the whole run,
  so a plain `getEnv "ACME_API_TOKEN"` is recorded and masked as a secret too. Declaring secrets
  also turns a clear value an older Xake recorded under that name into a digest when the database
  is opened (the file is rewritten).
- Child processes inherit the environment, so an environment secret reaches tools as it is. A
  value reaches a tool otherwise only when the script passes it, e.g.
  `sh "tool" { envs ["ACME_API_TOKEN", token] }`. Prefer the environment over command-line
  arguments or msbuild `-p:` properties, which tools echo in their own output.

The database format is 0.6 with this change; 0.4 and 0.5 files are read and upgraded on open. An
older Xake meeting a 0.6 database recreates it (a full rebuild).

## Setup and teardown

```fsharp
do xakeScript {
    setup ["banner"]
    teardown ["cleanup"]
    command "banner" {
        let! mode = getVar "MODE"
        do! trace Message "building in %A mode" mode }
    ...
}
```

`setup` targets run sequentially, in the order given, before the first target (command line mode:
once options and variables are final; engine mode: once at start). A failing setup target is
logged, the build is skipped, `teardown` still runs and the run fails (exit code 2, or
`XakeException` with `ThrowOnError`). `--dryrun` and `--dump` do not run setup (the dry run lists
the setup targets).

The ops differ in one respect: several `setup` ops **accumulate** (`setup ["a"]` then `setup ["b"]`
runs `a`, `b`), while a later `teardown` op **replaces** the earlier list.

Setup targets go through the normal up-to-date check. A target that reads variables records `Var`
dependencies and would be skipped on the next run as up to date. Declare such targets with
`command "name" { ... }`, which is a phony rule that always reruns (it records `alwaysRerun()` for
you); with `phony` put `alwaysRerun()` inside the recipe.

## Notes

- Prefer `Xake.Tasks` namespace in new scripts
- Prefer `sh`, `cp`, and `rm` builders over older legacy APIs
- Prefer the `csc`/`fsc`/`msbuild`/`resgen` builders over calling `MSBuild`/`ResGen` with a settings record (`Csc` and `Fsc` are the records of a resolved compilation; use `Csc.compile`/`Fsc.compile` for settings)
- Prefer typed variables with `Var.*` and `varschema` for better help output
