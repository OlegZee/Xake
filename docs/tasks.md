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

Other operations: `out`, `target`, `platform`, `ref`/`refs`/`refif`, `resources`/`resourceslist`,
`unsafe`, `cscpath`, `args`, `nofailonerror`.

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
let project = Fsproj.parse (evaluated name framework)
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
change did. `Fsproj.parse` turns it back into a record:

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

```bash
dotnet fsi build.fsx -- -- build -d NETFX-TARGET:net-4.6.2
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
- Prefer the `csc`/`fsc`/`msbuild`/`resgen` builders over calling `Csc`/`Fsc`/`MSBuild`/`ResGen` with a settings record
- Prefer typed variables with `Var.*` and `varschema` for better help output
