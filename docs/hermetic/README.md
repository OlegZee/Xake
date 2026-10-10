# Xake.Hermetic.Dotnet

Version **0.2.0, preview**: the API may break while the package is 0.x. 0.2 is built on
**Xake 3.6** (`[3.6.0.24, 3.7)`) and adds:

- **F# in the lock**: a lock entry holds a C# or an F# compilation (`Lock.Compilation`),
  `fsc { lock }` gates an `fsc {}` block, and `Project.import` imports an fsproj like a csproj.
- **Prerequisites**: the .NET SDK at an exact version, recorded per entry and checked before a
  replay (below).
- **`nofetch`** after `lock`, and `Restore.Options.Default` following the base's `NUGET_FETCH`.
- **The `HERMETIC=on` gate on the lock side**: a lock that names a path outside
  `$(ProjectRoot)` and `$(NuGetPackageRoot)` not covered by a prerequisite is neither written
  nor replayed, and an import of a project whose SDK is not pinned exactly fails
  ([hermetic-mode.md](hermetic-mode.md), [lock.md](lock.md#hermetic-mode)).
- **The base's helpers** instead of this package's copies: the build's package folder
  (`DotNetFwk.packageRoot ()`, so the script variable `NUGET_PACKAGES` tokenizes as
  `$(NuGetPackageRoot)`), `DotNetFwk.fetchEnabled ()`, the `global.json` reader
  (`DotNetFwk.globalJsonPin`) and `DotNetFwk.sdkVersionOf`.
- **`RuntimeConfig`** (Xake 3.6): a `.runtimeconfig.json` the rule declares is recorded in the
  entry (`"RuntimeConfig"` under `Compilation`, written only when there is one) and written again
  by a replay.

Breaking against 0.1 and the earlier 0.2 previews:

- **`Lock.build` takes a `Compilation`** (`Lock.Compilation.Csc c` / `Lock.Compilation.Fsc f`),
  and so do `compile`, `record` and `verify`; **`Entry.Csc` became `Entry.Compilation`** (the
  `entry.Csc` member remains, and fails for an F# entry).
- **`Fsproj` removed**: `Project.import` imports an fsproj.
- **Depends on Xake 3.6** (`[3.6.0.24, 3.7)`): the `Csc`/`Fsc` records gained fields, and F#
  records are not binary-compatible across such a change, so 0.2 does not run on 3.5. The upper
  bound is the next minor for the same reason.

- **`Entry.Prerequisites`**: a lock entry records what the environment must provide by itself,
  today the .NET SDK at an exact version (`{ "Kind": "dotnet-sdk", "Version": "<v>", "Pin":
  "$(ProjectRoot)/global.json" }`), written at record time when the entry depends on an SDK that
  `global.json` pins exactly, and checked before every replay with a message naming what to
  install. Written only when not empty, so existing locks stay byte-identical. Code that builds a
  `Lock.Entry` record by hand adds `Prerequisites = []`. See
  [lock.md](lock.md#prerequisites).
- **`packageroot` removed** from `csc { lock }` / `fsc { lock }`: the package folder is the
  build's one (`NUGET_PACKAGES`). `Restore.into` and `Lock.loadWith (Roots.packageRootOverride
  dir)` stay for scripts that read a lock against another folder.
- **`norestore` renamed `nofetch`**, after the base's `NUGET_FETCH`.
- **`Restore.Options.Default` follows `NUGET_FETCH`** and the build's package folder: where a
  build context exists (`Restore.ensure`, `Lock.compileWith`, `Lock.buildWith`), `PackageRoot =
  None` is `DotNetFwk.packageRoot ()` (the script variable `NUGET_PACKAGES` first) and
  `Enabled = true` means "fetch unless `NUGET_FETCH=off`" (`Restore.resolve`).

`Xake.Hermetic.Dotnet` builds .NET assemblies and packages so that what shipped can be
explained and checked: a **lock** file records exactly what the compiler was handed and the hash
of everything it read, the build compiles from the lock and fails when anything differs, and the
result can be turned into a deterministic package with an SBOM and a signing step. It is a
separate NuGet package that depends on `Xake` (the engine and the `csc`/`fsc`/`msbuild`/`resgen`
tasks of `Xake.Dotnet`, Xake 3.6.0.24 or later, below 3.7); nothing in `Xake` knows about it.

```fsharp
#r "nuget: Xake.Hermetic.Dotnet, 0.2.0"

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet
```

## The pieces

| Piece | What it is | Page |
|---|---|---|
| Lock | the lock file format (C# and F# entries), `Lock.build` / `compile` / `record` / `verify`, `csc { lock }`, `fsc { lock }`, `Project.import` (msbuild design-time build of a csproj or fsproj into a lock) | [lock.md](lock.md) |
| Hermetic mode | `HERMETIC=on`: the invariant, prerequisites, what each input needs, the messages; what is shipped and what is pending | [hermetic-mode.md](hermetic-mode.md) |
| Restore | fetch the packages a lock names into a folder of the build's choice, one `dotnet restore` for the whole set | [restore.md](restore.md) |
| Nuget and Sbom | read the restore graph and package cache; write a deterministic CycloneDX 1.6 SBOM per assembly and per package | [nuget-sbom.md](nuget-sbom.md) |
| Verify | Authenticode PE hash and labelled byte differences between two binaries | [verify.md](verify.md) |
| Pack | deterministic zip and `.nupkg` (sorted entries, fixed timestamps, content-derived ids) | [pack.md](pack.md) |
| StrongName | PE timestamp and checksum normalisation, strong-name (re-)signing without `sn.exe` | [strongname.md](strongname.md) |
| Sign | Authenticode and NuGet signing as a delegated rule with a pluggable signer | [signing.md](signing.md) |
| Workflows | developer, teammate, CI (GitHub Actions, GitLab CI), release and offline flows end to end; what to commit; how a missing lock fails CI | [workflows.md](workflows.md) |

The compilation itself, `csc {}`/`fsc {}`, `Csc.ofSettings`/`Fsc.ofSettings` and the runners
`Csc.run`/`Fsc.run`, stay in `Xake.Dotnet`:
[../csc-syntax.md](../csc-syntax.md), [../tasks.md](../tasks.md).

- [Architecture](../hermetic-architecture.md): the design, where it meets `Xake`, the evidence
  and the known limits, for maintainers and reviewers.
- [Guide](../hermetic-guide.md) (draft): a task-oriented walk-through, from the first lock to
  packing and signing.

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
    do! Lock.build "locks/app.json" (Lock.Compilation.Csc c)
}
```

`Lock.build` gates against the lock, restores what it names, resolves the revision token and
then calls `Csc.run` (`Fsc.run` for `Lock.Compilation.Fsc f`).

An F# library the same way. The compiler is the SDK's `fsc.dll`, so pin the SDK
(`global.json` with `rollForward: disable`):

```fsharp
"out/lib.dll" ..> fsc {
    targetfwk "net8.0"
    src !!"src/Lib.fs"
    lock "locks/lib.json"
}
```

To update a lock deliberately, delete the file or run a target that calls
`Lock.record`.

## Building from source

`src/hermetic` compiles against the published `Xake` package (the range above, `[3.6.0.24, 3.7)`),
not against `src/core` and `src/dotnet` in the same checkout. A plain restore takes it from
nuget.org; nothing else is needed:

```bash
dotnet restore src/hermetic.tests
dotnet build src/hermetic -c Release --no-restore
dotnet test src/hermetic.tests -c Release --no-restore
```

The lower bound is the exact version nuget.org carries (the release pipeline appends the run
number to the version, so 3.6.0 is published as 3.6.0.24). A bound with no published package at
it makes NuGet pick the next one up and warn NU1603 on every restore.

Only when working against an *unreleased* base (a change in `src/core` or `src/dotnet` that
`src/hermetic` needs before it is on nuget.org) pack the base into a folder with a version inside
the range and hand that folder to the restore as an extra source; nothing in the repository
points at it:

```bash
dotnet pack src/dotnet -c Release -p:Version=3.6.0.99 -o /tmp/xake-feed
dotnet restore src/hermetic.tests -p:RestoreAdditionalProjectSources=/tmp/xake-feed
dotnet test src/hermetic.tests -c Release --no-restore
```

The package lands in the NuGet global cache (`~/.nuget/packages/xake/3.6.0.99`) and wins over
the released one from there on; delete that folder when done, and raise the lower bound in
`Xake.Hermetic.Dotnet.fsproj` to the released version once the base is published.
