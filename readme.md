Xake is a build utility that uses the full power of the F# programming language. Xake is inspired by [shake](https://github.com/ndmitchell/shake) build tool.

[![Build and Test](https://github.com/OlegZee/Xake/actions/workflows/build.yml/badge.svg)](https://github.com/OlegZee/Xake/actions/workflows/build.yml)

## Sample script

The simple script looks like:

```fsharp
#r "nuget: Xake"

open Xake
open Xake.Dotnet

do xakeScript {
    "main" <== ["helloworld.exe"]

    "helloworld.exe" ..> csc {
        targetfwk "net-4.6.2"
        src !!"helloworld.cs"
        grefs ["System.dll"]
    }
}
```

This script compiles helloworld assembly from helloworld.cs file, on any OS. Rules are written
straight in the body of `xakeScript` — no `rules [ ... ]` wrapper needed.

The single `Xake` package contains both the engine (`Xake`, `Xake.Tasks`) and the tasks for the
full .NET Framework (`Xake.Dotnet`) — one reference is enough.

## Getting started

Make sure dotnet SDK 8.0 or newer is installed. Save any of the scripts below as `build.fsx`
and run it with `dotnet fsi build.fsx` — Xake comes from NuGet, there is nothing else to
install.

### 1. Hello world

The smallest useful script: one phony rule, printing a message. `main` is the default target,
so no arguments are needed.

```fsharp
#r "nuget: Xake"

open Xake

do xakeScript {
    "main" => trace Message "Hello world!"
}
```

### 2. A rule that produces a file

`..>` declares a *file* target. The recipe writes the target file, and `main` demands it —
targets are built on demand, and only the ones that are actually needed.

```fsharp
#r "nuget: Xake"

open Xake
open Xake.Tasks

do xakeScript {
    "main" => need ["out/hello.txt"]

    "out/hello.txt" ..> writeText "Hello world!"

    "clean" => rm { dir "out" }
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
    "main" => need ["out/hello.txt"]

    "out/hello.txt" ..> recipe {
        // records name.txt as a dependency of this target
        let! name = readText "name.txt"

        do! trace Info "greeting %s" (name.Trim())
        do! writeText $"Hello, {name.Trim()}!"
    }

    "clean" => rm { dir "out" }
}
```

Create `name.txt` next to the script and run it twice: the second run reports
`Skipped main (up to date)`. Change `name.txt` and it rebuilds.

### More samples

```bash
git clone http://github.com/xakebuild/xake
cd xake
dotnet fsi build.fsx -- -- build      # features.fsx and fullframework.fsx use the local build
cd samples
dotnet fsi gettingstarted.fsx
dotnet fsi features.fsx
dotnet fsi fullframework.fsx
```

## Further reading

* See [the features.fsx](https://github.com/OlegZee/Xake/blob/dev/samples/features.fsx) script for various samples.
* See [docs/tasks.md](docs/tasks.md) for the task reference and [docs/dotnet-build.md](docs/dotnet-build.md) for building .NET Framework targets.
* We have the [introduction page](https://github.com/OlegZee/Xake/wiki/Introduction) for you to learn more about Xake.
* And there're the [documentation notes](https://github.com/OlegZee/Xake/wiki) for more details.

## Build the project

Once you cloned the repository you are ready to compile and test the binaries:

```
dotnet fsi build.fsx -- -- build test
```

... or use `build.cmd` (`build.sh`) in the root folder.

Releases are published to nuget.org by CI from a `v*` tag — see
[docs/devprocess.md](docs/devprocess.md) for the packaging and release procedure.

## Building .NET Framework targets

The `csc`, `fsc`, `msbuild` and `resgen` tasks build full-framework binaries on any OS with
nothing installed beyond the .NET SDK: the compilers come from the SDK and the reference
assemblies from the `Microsoft.NETFramework.ReferenceAssemblies.*` packages, restored on first
use. Mono is still supported as a fallback and can be requested explicitly with a `mono-`
prefixed framework name.

See [docs/dotnet-build.md](docs/dotnet-build.md) for the discovery rules, the supported
runtimes, and how to switch between them.

## Documentation

See [documentation](docs/overview.md) for more details.

## References

* [documentation](https://github.com/OlegZee/Xake/wiki) 
* [implementation notes](docs/implnotes.md)
* [Shake manual](https://github.com/ndmitchell/shake/blob/master/docs/Manual.md)
* [samples repository](https://github.com/xakebuild/Samples)

