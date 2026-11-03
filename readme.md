Xake is a build utility that uses the full power of the F# programming language. Xake is inspired by [shake](https://github.com/ndmitchell/shake) build tool.

[![Build and Test](https://github.com/OlegZee/Xake/actions/workflows/build.yml/badge.svg)](https://github.com/OlegZee/Xake/actions/workflows/build.yml)

## Sample script

A script that builds a .NET project with the SDK, and only when it has to:

```fsharp
#r "nuget: Xake, 4.0.0"

open Xake
open Xake.Tasks

let vars = {|
    // -d CONFIGURATION=Debug, or XAKE__CONFIGURATION in the environment
    config = Var.string(name = "CONFIGURATION") |> withDefault "Release"
|}

do xakeScript {
    varschema vars

    command "main" { do! need ["out/app.dll"] }

    // rebuilt only when a source file, the project or CONFIGURATION changes
    target "out/app.dll" {
        let! config = vars.config
        let! sources = getFiles (!! "app/**/*.cs" ++ "app/*.csproj" -- "app/obj/" -- "app/bin/")
        do! needFiles sources
        do! sh "dotnet build app" { args ["-c"; config; "-o"; "out"] }
    }

    command "clean" { do! rm { dir "out" } }
}
```

Next to a project made with `dotnet new console -n app`, `dotnet fsi build.fsx` builds
`out/app.dll`; run it again and it reports `Skipped ... (up to date)`. Rules are written straight
in the body of `xakeScript`, `sh` runs a process and fails the build on a non-zero exit code, and
the variables are declared once and listed by `dotnet fsi build.fsx -- -- --help`
([docs/variables.md](docs/variables.md)).

The `Xake` package is the build engine alone (`Xake`, `Xake.Tasks`). The .NET tasks (`csc`, `fsc`,
`msbuild`, `resgen`, hermetic builds) live in the `Xake.Dotnet` package, built from its own
repository, https://github.com/OlegZee/Xake.Dotnet, and published with 4.0.0: add
`#r "nuget: Xake.Dotnet, 4.0.0"` and `open Xake.Dotnet` to use them.

## Getting started

Make sure dotnet SDK 8.0 or newer is installed. Save any of the scripts below as `build.fsx`
and run it with `dotnet fsi build.fsx` — Xake comes from NuGet, there is nothing else to
install.

### 1. Hello world

The smallest useful script: one command, printing a message. `main` is the default target,
so no arguments are needed.

```fsharp
#r "nuget: Xake"

open Xake

do xakeScript {
    command "main" { do! trace Message "Hello world!" }
}
```

### 2. A rule that produces a file

`target` declares a *file* target. The recipe writes the target file, and `main` demands it —
targets are built on demand, and only the ones that are actually needed.

```fsharp
#r "nuget: Xake"

open Xake
open Xake.Tasks

do xakeScript {
    command "main" { do! need ["out/hello.txt"] }

    target "out/hello.txt" { do! writeText "Hello world!" }

    command "clean" { do! rm { dir "out" } }
}
```

```bash
dotnet fsi build.fsx            # builds "main"
dotnet fsi build.fsx -- -- clean
```

### 3. A recipe with dependencies

A `recipe { ... }` is a sequence of steps. Anything it reads through Xake — `readText` here —
is recorded as a dependency, so the target is rebuilt when, and only when, one of its inputs
changes.

```fsharp
#r "nuget: Xake"

open Xake
open Xake.Tasks

do xakeScript {
    command "main" { do! need ["out/hello.txt"] }

    target "out/hello.txt" {
        // records name.txt as a dependency of this target
        let! name = readText "name.txt"

        do! trace Info "greeting %s" (name.Trim())
        do! writeText $"Hello, {name.Trim()}!"
    }

    command "clean" { do! rm { dir "out" } }
}
```

Create `name.txt` next to the script and run it twice: the second run reports
`Skipped main (up to date)`. Change `name.txt` and it rebuilds.

### More samples

```bash
git clone https://github.com/OlegZee/Xake
cd xake
dotnet fsi build.fsx -- -- build      # features.fsx uses the local build
cd samples
dotnet fsi gettingstarted.fsx
dotnet fsi features.fsx
```

Samples of the .NET tasks (`csc`, `fsc`, hermetic builds) are in the
[Xake.Dotnet repository](https://github.com/OlegZee/Xake.Dotnet/tree/dev/samples).

## Further reading

* See [the features.fsx](https://github.com/OlegZee/Xake/blob/dev/samples/features.fsx) script for various samples.
* See [docs/tasks.md](docs/tasks.md) for the task reference.
* [Xake.Dotnet](https://github.com/OlegZee/Xake.Dotnet): the .NET tasks (`csc`, `fsc`, `msbuild`, `resgen`, project descriptions) and the hermetic library (lock files, restore from a lock, deterministic packs, SBOMs and signing), one package with its own repository and docs.
* [Roadmap](docs/roadmap.md): what is planned and what is open for Xake.
* We have the [introduction page](https://github.com/OlegZee/Xake/wiki/Introduction) for you to learn more about Xake.
* And there're the [documentation notes](https://github.com/OlegZee/Xake/wiki) for more details.

## Build the project

Once you cloned the repository you are ready to compile and test the binaries:

```
dotnet fsi build.fsx -- -- build test
```

... or use `build.cmd` (`build.sh`) in the root folder. `build.fsx` drives `dotnet build`, the
tests and `dotnet pack`; packages and releases come from it.

Releases are published to nuget.org by CI from a `v*` tag — see
[docs/devprocess.md](docs/devprocess.md) for the packaging and release procedure.

## Building .NET targets

The `csc`, `fsc`, `msbuild` and `resgen` tasks build .NET and full-framework binaries on any OS
with nothing installed beyond the .NET SDK. They are the `Xake.Dotnet` package (from 4.0.0;
`#r "nuget: Xake.Dotnet, 4.0.0"`, which brings `Xake` with it), developed in its own repository,
https://github.com/OlegZee/Xake.Dotnet, together with the hermetic build library and their
documentation.

## Documentation

See [documentation](docs/overview.md) for more details.

## References

* [documentation](https://github.com/OlegZee/Xake/wiki) 
* [implementation notes](docs/implnotes.md)
* [Shake manual](https://github.com/ndmitchell/shake/blob/master/docs/Manual.md)
* [samples repository](https://github.com/xakebuild/Samples)

