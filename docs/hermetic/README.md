# Xake.Hermetic.Dotnet

Version **0.1.0, preview**: the API may break while the package is 0.x.

`Xake.Hermetic.Dotnet` builds .NET assemblies and packages so that what shipped can be
explained and checked: a **lock** file records exactly what the compiler was handed and the hash
of everything it read, and the build compiles from the lock and fails when anything differs. It
is a separate NuGet package that depends on `Xake` (the engine and the `csc`/`fsc`/`msbuild`/`resgen`
tasks of `Xake.Dotnet`, version 3.4 or later, below 4.0); nothing in `Xake` knows about it.

```fsharp
#r "nuget: Xake.Hermetic.Dotnet"

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet
```

## The pieces

| Piece | What it is | Page |
|---|---|---|
| Lock | the lock file format, `Lock.build` / `compile` / `record` / `verify`, `csc { lock }`, `Project.import` (msbuild design-time build into a lock) | [lock.md](lock.md) |
| Restore | fetch the packages a lock names into a folder of the build's choice, one `dotnet restore` for the whole set | [restore.md](restore.md) |
| Nuget | read the restore graph (`project.assets.json`) and the package cache (nuspec, `.nupkg.metadata`) that Restore and the import use | |

The compilation itself, `csc {}`, `Csc.ofSettings` and the runner `Csc.run`, stay in `Xake.Dotnet`:
[../csc-syntax.md](../csc-syntax.md), [../tasks.md](../tasks.md).

## Two ways in

Gate a `csc {}` block by a lock. The first build records `locks/app.json` and compiles; later
builds fail with a diff when the settings, a reference or the compiler no longer match it:

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

do xakeScript {
    rules [
        "out/app.dll" ..> csc {
            targetfwk "net-4.6.2"
            src !!"src/*.cs"
            grefs ["System.dll"]
            lock "locks/app.json"
        }
    ]
}
```

The same thing without the sugar, with the resolved `Csc` in hand:

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

"out/app.dll" ..> recipe {
    let! c = csc { targetfwk "net-4.6.2"; src !!"src/*.cs"; grefs ["System.dll"]
                   out (File.make "out/app.dll"); resolve }
    do! Lock.build "locks/app.json" c
}
```

`Lock.build` gates against the lock, restores what it names, resolves the revision token and
then calls `Csc.run`. To update a lock deliberately, delete the file or run a target that calls
`Lock.record`.

Working notes for the package (brief, decisions, experiments) live in
[../features/hermetic-build/](../features/hermetic-build/README.md).
