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

`targetfwk` picks the framework to compile against. On Windows a Framework installation found
through the registry wins; everywhere else -- and as a fallback -- the compiler comes from the
.NET SDK and the reference assemblies from a NuGet package, so a full-framework binary can be
built on any OS with nothing pre-installed beyond the SDK:

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
| `targetfwk` | `string` | framework to compile against (default: the `NETFX-TARGET` variable) |
| `src` | `Fileset` | source files |
| `ref` / `refs` / `refif` | `Fileset`; `refs` replaces, `refif` takes `bool * Fileset` | references from files |
| `grefs` | `string list` | framework/global references, e.g. `["System.dll"]` |
| `resources` / `resourceslist` | `ResourceFileset` / list | embedded resources; a `.resx` is compiled to `.resources` first |
| `define` | `string list` | conditional compilation symbols |
| `unsafe` | `bool` | allow unsafe code |
| `args` | `string list` | extra compiler arguments, appended last |
| `nofailonerror` | -- | a compile error does not fail the build |
| `cscpath` | `string` | run this compiler instead of the one the framework provides; overrides `toolset` |
| `toolset` | `string` (version) | take `csc.dll` from `Microsoft.Net.Compilers.Toolset/<version>` in the NuGet cache, restoring the package first when it is missing, instead of the SDK's own compiler |
| `noserver` | -- | compile in a fresh compiler process instead of through the Roslyn compiler server |
| `keepalive` | `int` (seconds) | idle time after which a compiler server this build starts exits (`/keepalive`; Roslyn's default is 600) |
| `resolve` | -- | return the resolved `Csc` instead of compiling; must be the last operation (see below) |

`lock "path"` is not part of the base package: it is added by `Xake.Dotnet`'s hermetic side
(`Lock.fs`, module `CscLockBuilder`), and gates the compilation against a lock file; see
[csc-syntax.md](features/hermetic-build/csc-syntax.md).

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
[csc-server.md](features/hermetic-build/csc-server.md).

How the compiler and the reference assemblies are located, and how to force a particular
toolchain, is described in [dotnet-build.md](dotnet-build.md).

### fsc

Same shape as `csc`, plus `fscver`, `noframework` and `notailcalls` (there is no `fscpath`
counterpart to `cscpath` — pin the toolchain with `fscver` or the `NETFX` variable instead):

```fsharp
"app.exe" ..> fsc {
  src (fileset { includes "src/*.fs" })
  ref !!"bin/FSharp.Core.dll"
  grefs ["System.dll"; "System.Core.dll"]
  args ["--utf8output"]
}
```

`targetfwk` also takes `netstandard2.0` and `netstandard2.1`: the task then compiles against
`netstandard.dll` with `--noframework --targetprofile:netstandard`. `doc` writes the xml
documentation file, creating its directory — fsc creates the one for `--out` only.

`define` takes one symbol per switch: unlike `csc`, `fsc` reads `--define:A;B` as a single
symbol named `A;B`, so the task emits a separate `--define:` for each.

#### Compiling what a project file describes

A script that drives the compiler itself still has to know what to compile, what to reference
and what to define. `Fsproj` asks msbuild, which is the only thing that reads a project file
correctly — conditions, imports, the resolved reference list and the generated assembly
attributes included — and the answer is cached in a file, so msbuild runs only when the project
file changes:

```fsharp
// the one rule that runs msbuild
"out/obj/(fwk:*)/(lib:*).json" ..> recipe {
    let! framework = getRuleMatch "fwk"
    let! name = getRuleMatch "lib"
    let! result = getTargetFile()
    do! needFiles (Filelist [File.make (projectOf name)])
    do! Fsproj.evaluate {
        Fsproj.EvalOptions.Default with
            Project = projectOf name
            Framework = framework
            Configuration = "Release"
            Properties = ["Version", "1.2.3"]
            Output = result.FullName
    }
}

// ... and the compile, which only reads the result
let! project = Fsproj.load (evaluated name framework)
do! fsc {
    targetfwk framework
    out (File.make outputPath)
    doc (File.make docPath)
    src (project.Sources |> List.fold (fun fs f -> fs ++ f) Fileset.Empty)
    refs (project.References |> List.fold (fun fs f -> fs ++ f) Fileset.Empty)
    define project.Defines
}
```

`Fsproj.evaluate` runs `dotnet msbuild -restore -t:PrepareForBuild;GenerateAssemblyInfo;
ResolveReferences` with `-getItem`/`-getProperty`. msbuild answers with every metadata field of
every item — some 200 KB and 3600 lines per project, of which the build reads one field — so
that dump goes to a scratch file and what is kept is only what gets consumed: a ~15 KB file of
plain lists, readable and diffable. Paths in it are written against `$(NuGetPackageRoot)` and
`$(ProjectRoot)` and expanded again on read, so the file is byte-identical on every machine and
belongs in the repository — a lockfile for the compilation, whose diff shows what a project
change did. The roots are the build's own: `$(ProjectRoot)` is the engine's
`ExecOptions.ProjectRoot`, not the process's current directory (see the `Roots` module).
`Fsproj.load` — a recipe, so that it can take those roots — turns the file back into a record:

| Field | What is in it |
|---|---|
| `Sources` | `CompileBefore`, `Compile`, `CompileAfter` in that order — the generated `AssemblyInfo.fs` (`InternalsVisibleTo`, copyright, the versions from the `Version` property) is the `CompileBefore` item, so it comes first |
| `References` | `ReferencePath`: every assembly resolved, framework references and packages alike |
| `ProjectRefs` | `ProjectReference` items — msbuild points `References` at the referenced project's own `bin/`, so a build with its own layout substitutes them |
| `Defines` | `DefineConstants`, including the symbols msbuild derives from the framework (`NETSTANDARD2_0`, the `_OR_GREATER` chain) |
| `Properties` | whatever was asked for: `AssemblyName`, `Optimize`, `DebugType`, ... |

`BuildProjectReferences=false` is passed for you: resolving a project reference must not make
msbuild build the very thing the script is about to compile. Nothing else is compiled either —
the evaluation only reads the project and writes the assembly attributes.

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
| `NETFX-TARGET` | Default `targetfwk` for all compiler tasks |
| `FSCVER` | F# compiler version `fsc` asks for |
| `CSC_SERVER` | `csc` compiler server: `on`, `off` or keepalive seconds; overridden by a target's `noserver`/`keepalive`, overrides env `XAKE_CSC_SERVER` |

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
- Prefer the `csc`/`fsc`/`msbuild`/`resgen` builders over calling `Fsc`/`MSBuild`/`ResGen` with a settings record (`Csc` is the record of a resolved compilation; use `Csc.compile` for settings)
- Prefer typed variables with `Var.*` and `varschema` for better help output
