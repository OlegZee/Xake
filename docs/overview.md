## Xake overview

Xake is a build automation tool for .NET that uses F# scripts.
It tracks dependencies, supports incremental builds, and runs independent targets in parallel.

Current project baseline:

- .NET SDK 8.0 or newer
- Xake NuGet package `Xake` — the engine and the .NET Framework tasks in one

## Minimal script

```fsharp
#r "nuget: Xake"

open Xake

do xakeScript {
    "main" => trace Message "Hello from Xake"
}
```

## Run a build script

Run default target:

```bash
dotnet fsi build.fsx
```

Run specific targets and options after a double separator:

```bash
dotnet fsi build.fsx -- -- clean build test
```

Pass variables:

```bash
dotnet fsi build.fsx -- -- build -d Version=1.2.3
```

## Core concepts

- Rules map targets to recipes
- Recipes define how targets are built
- Filesets collect files with Ant style masks
- Dependencies are recorded during recipe execution

When dependencies do not change, Xake skips rebuilds.

## Rule styles

Rules are written directly in the body of `xakeScript`. Preferred builder style:

```fsharp
do xakeScript {
    command "build" {
        do! sh "dotnet build src/core -c Release" { () }
    }

    target "out/version.txt" {
        let! ver = getVar "VERSION"
        do! writeText <| Option.defaultValue "0.0.0" ver
    }

    "main" <== ["build"]
}
```

Operator style also works:

```fsharp
do xakeScript {
    "main" <== ["build"; "test"]
    "deploy" <<< ["build"; "test"; "package"]
    "clean" => rm {dir "out"}
}
```

Rules may also be generated with `for`, and the older `rules [ ... ]` wrapper is still
accepted — the two forms can be mixed in one script:

```fsharp
do xakeScript {
    for fwk in ["net8.0"; "netstandard2.0"] do
        $"out/{fwk}/lib.dll" ..> recipe {
            do! sh $"dotnet build src/lib -f {fwk} -o out/{fwk}" { () }
        }
}
```

## Useful links

- Wiki home: https://github.com/OlegZee/Xake/wiki
- Introduction: https://github.com/OlegZee/Xake/wiki/Introduction
- Command line reference: https://github.com/OlegZee/Xake/wiki/Reference-%7C-Command-Line
- Rules reference: https://github.com/OlegZee/Xake/wiki/Reference-%7C-Rules
- Recipe reference: https://github.com/OlegZee/Xake/wiki/Reference-%7C-Recipe
