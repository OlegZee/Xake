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

`targetfwk` picks the framework to compile against; it is optional. The first that is set
wins: `targetfwk`, the `NETFX-TARGET` script variable, the .NET framework of the SDK the build
runs on (`net10.0` for SDK 10.0.x, the SDK a `global.json` in or above the project root
selects). A .NET target (`net8.0`, `net10.0`, also spelled `sdk-net10.0`) compiles against
the targeting pack installed with the SDK (`<dotnet>/packs/Microsoft.NETCore.App.Ref`), all
of it, with nothing downloaded; the example above therefore needs nothing but the SDK.

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
| `toolset` | `string` (version) | take `csc.dll` from `Microsoft.Net.Compilers.Toolset/<version>` in the NuGet cache, restoring the package first when it is missing, instead of the SDK's own compiler; overrides the `CSC_TOOLSET` variable |
| `noserver` | -- | compile in a fresh compiler process instead of through the Roslyn compiler server |
| `keepalive` | `int` (seconds) | idle time after which a compiler server this build starts exits (`/keepalive`; Roslyn's default is 600) |
| `resolve` | -- | return the resolved `Csc` instead of compiling; must be the last operation (see below) |

`lock "path"` is not part of the base package: it is added by the separate package
`Xake.Hermetic.Dotnet` (`open Xake.Hermetic.Dotnet`), and gates the compilation against a lock
file; see [hermetic/lock.md](hermetic/lock.md). Details behind the operations above:
[csc-syntax.md](csc-syntax.md).

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

`Csc.runOptions settings` builds the options `csc {}` runs with from composed settings: the
settings' `FailOnError`, `CscPath`, the resolved server and the target framework's environment.

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
| `resolve` | -- | return the resolved `Fsc` instead of compiling; must be the last operation |

The arguments are always fsc's `--name:value` form (`-r:` for references), on every OS. The
task passes `--noframework` and the framework's reference assemblies as ordinary `-r:`
references: for .NET (`net10.0`, the default) every assembly of the SDK's targeting pack plus
`--targetprofile:netcore`, for netstandard `netstandard.dll` plus
`--targetprofile:netstandard`, for .NET Framework `mscorlib.dll` and a `netstandard.dll` facade.
Unless a `ref` already names an `FSharp.Core.dll`, one is referenced: for a .NET target the
SDK's (next to `fsc.dll`, the version the compiler ships with); for netstandard and .NET
Framework the `FSharp.Core` NuGet package at `DotNetFwk.fsharpCoreVersion` (8.0.100, what Xake
itself pins) or the script variable `FSHARP_CORE_VERSION`, its `lib/netstandard2.0` build
(`lib/netstandard2.1` for netstandard2.1), restored into the package cache on first use. That
build references `netstandard 2.0.0.0`, so a .NET Framework target also gets a type-forwarding
`netstandard.dll`: the reference assemblies' own `Facades/netstandard.dll` from 4.7.1 on, the
SDK's `Microsoft/Microsoft.NET.Build.Extensions/net461/lib/netstandard.dll` below (unless a `ref`
names a `netstandard.dll`). `NETStandard.Library`'s `netstandard.dll` is not used there: it
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

There is no compiler server for fsc. Like `FscRunOptions`' labels, which overlap `RunOptions`',
construct it with qualified labels or from `FscRunOptions.Default with ...`.

#### Compiling what a project file describes

A script that wants exactly what `dotnet build` would compile for an existing `.fsproj` does not
re-describe it as `fsc {}` settings: `Project.import` in `Xake.Hermetic.Dotnet` asks msbuild
for the compiler's own command line (a design-time build) and records it in a lock, and
`Lock.compile` replays it through `Fsc.run`. See [hermetic/lock.md](hermetic/lock.md). (The
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

`resgen` compiles resx files to standalone `.resources` (full framework only):

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
| `CSC_TOOLSET` | `csc` compiler package version (`Microsoft.Net.Compilers.Toolset`) for targets with no `toolset`; empty or unset means the SDK's compiler |

```bash
dotnet fsi build.fsx -- -- build -d NETFX-TARGET:net-4.6.2
dotnet fsi build.fsx -- -- build -d CSC_SERVER:off
```

Names such as `net-4.6.2`, `sdk-net462` and `mono-4.5` select both the framework and, through
the prefix, the provider that supplies the tools. See
[dotnet-build.md](dotnet-build.md#how-to-switch).

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

## Notes

- Prefer `Xake.Tasks` namespace in new scripts
- Prefer `sh`, `cp`, and `rm` builders over older legacy APIs
- Prefer the `csc`/`fsc`/`msbuild`/`resgen` builders over calling `MSBuild`/`ResGen` with a settings record (`Csc` and `Fsc` are the records of a resolved compilation; use `Csc.compile`/`Fsc.compile` for settings)
- Prefer typed variables with `Var.*` and `varschema` for better help output
