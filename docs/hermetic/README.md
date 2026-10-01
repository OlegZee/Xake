# Xake.Hermetic.Dotnet

Version **0.1.0, preview**: the API may break while the package is 0.x.

`Xake.Hermetic.Dotnet` builds .NET assemblies and packages so that what shipped can be
explained and checked: a **lock** file records exactly what the compiler was handed and the hash
of everything it read, the build compiles from the lock and fails when anything differs, and the
result can be turned into a deterministic package with an SBOM and a signing step. It is a
separate NuGet package that depends on `Xake` (the engine and the `csc`/`fsc`/`msbuild`/`resgen`
tasks of `Xake.Dotnet`, Xake 3.4.0.21 or later, below 4.0); nothing in `Xake` knows about it.

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
| Nuget and Sbom | read the restore graph and package cache; write a deterministic CycloneDX 1.6 SBOM per assembly and per package | [nuget-sbom.md](nuget-sbom.md) |
| Verify | Authenticode PE hash and labelled byte differences between two binaries | [verify.md](verify.md) |
| Pack | deterministic zip and `.nupkg` (sorted entries, fixed timestamps, content-derived ids) | [pack.md](pack.md) |
| StrongName | PE timestamp and checksum normalisation, strong-name (re-)signing without `sn.exe` | [strongname.md](strongname.md) |
| Sign | Authenticode and NuGet signing as a delegated rule with a pluggable signer | [signing.md](signing.md) |

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

## Building from source

`src/hermetic` compiles against the published `Xake` package (the range above, `[3.4.0.21, 4.0)`),
not against `src/core` and `src/dotnet` in the same checkout. A plain restore takes it from
nuget.org; nothing else is needed:

```bash
dotnet restore src/hermetic.tests
dotnet build src/hermetic -c Release --no-restore
dotnet test src/hermetic.tests -c Release --no-restore
```

The lower bound is the exact version nuget.org carries (the release pipeline appends the run
number to the version, so 3.4.0 is published as 3.4.0.21). A bound with no published package at
it makes NuGet pick the next one up and warn NU1603 on every restore.

Only when working against an *unreleased* base (a change in `src/core` or `src/dotnet` that
`src/hermetic` needs before it is on nuget.org) pack the base into a folder with a version inside
the range and hand that folder to the restore as an extra source; nothing in the repository
points at it:

```bash
dotnet pack src/dotnet -c Release -p:Version=3.4.0.99 -o /tmp/xake-feed
dotnet restore src/hermetic.tests -p:RestoreAdditionalProjectSources=/tmp/xake-feed
dotnet test src/hermetic.tests -c Release --no-restore
```

The package lands in the NuGet global cache (`~/.nuget/packages/xake/3.4.0.99`) and wins over
the released one from there on; delete that folder when done, and raise the lower bound in
`Xake.Hermetic.Dotnet.fsproj` to the released version once the base is published.
