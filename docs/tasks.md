# Xake tasks

The examples below use current API style:

```fsharp
#r "nuget: Xake, 3.0.0"
open Xake
open Xake.Tasks
```

## Shell tasks

### sh recommended

`sh` fails on non zero exit code by default.

```fsharp
do! sh "dotnet build src/core -c Release" {}
```

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

### fsc

Same shape as `csc`, plus `fscver`, `noframework` and `notailcalls`:

```fsharp
"app.exe" ..> fsc {
  src (fileset { includes "src/*.fs" })
  ref !!"bin/FSharp.Core.dll"
  grefs ["System.dll"; "System.Core.dll"]
  args ["--utf8output"]
}
```

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
