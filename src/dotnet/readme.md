Xake is a make utility made for .NET on F# language. Xake is inspired by [shake](https://github.com/ndmitchell/shake) build tool.

See [Xake documentation](https://github.com/OlegZee/Xake/wiki/Introduction) for more details.

These are the tasks for the full .NET framework: `csc`, `fsc`, `msbuild`, `resgen` and
`resourceset`. They ship in the same `Xake` package as the engine, so a single reference is
enough. See [Dotnet tasks](https://github.com/OlegZee/Xake/wiki/Tasks-%7C-Dotnet) for the full set of options.

## Prerequisites

The .NET SDK 8.0+ (see `global.json`). Nothing else: the compilers come from the SDK and the
.NET Framework reference assemblies from the
`Microsoft.NETFramework.ReferenceAssemblies.*` packages, restored on first use -- so
full-framework binaries can be built on any OS without a Framework installation.

## Csc task

The simple script looks like:

```fsharp
#r "nuget: Xake"

open Xake
open Xake.Dotnet

do xakeScript {
  rules [
    "main" <== ["helloworld.exe"]

    "helloworld.exe" ..> csc {src !!"helloworld.cs"}
  ]
}
```

This script compiles helloworld assembly from helloworld.cs file. See
[samples/fullframework.fsx](../../samples/fullframework.fsx) for targeting a specific framework,
and [docs/dotnet-build.md](../../docs/dotnet-build.md) for how the toolchain is discovered and
how to switch between the SDK, a Framework installation and Mono.
