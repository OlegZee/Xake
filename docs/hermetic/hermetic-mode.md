# Hermetic mode (`HERMETIC=on`)

Status: **shipped** in Xake 3.6 (the resolve-side gate in `Csc.ofSettings`/`Fsc.ofSettings`,
messages 1 to 8) and Xake.Hermetic.Dotnet 0.2 (the lock side: `Entry.Prerequisites`, messages 9
to 11). **Pending**: SDK restore, an F# compiler toolset, and imports of `netN.0` projects
([Pending](#pending)).

## Purpose

Without the mode a lock is hermetic in the sense that the build reads only what the lock names
and fails on any difference. It is not necessarily *complete*: an entry may name files under
`$(DotnetRoot)` (the SDK's csc, the targeting pack in `packs/`, the SDK's `FSharp.Core`, the
SDK's net461 `netstandard.dll` facade) that no restore step can bring to a clean machine. The
user would learn this on a colleague's machine or on CI, from a path mismatch.

`HERMETIC=on` turns completeness into a checked property of the build: every input of every
compilation is either restorable from the package folder, part of the checkout, or an explicitly
declared **prerequisite** that is checked up front. Missing configuration fails at resolve time,
naming the offending path and the variable that fixes it.

`HERMETIC` is independent of `CI`. `CI` is about the process (a lock is never recorded, only
verified). `HERMETIC` is about the content (what a resolved compilation may name). Both can be
on, either alone, or neither.

## The invariant

> A resolved compilation, and therefore a lock entry, names no path outside `$(ProjectRoot)`
> and `$(NuGetPackageRoot)`, except paths covered by a prerequisite the entry declares.

"Names" covers every path the compilation carries: compiler, sources, references, analyzers,
resources, generated files, `/out:` and `/doc:`. `$(NuGetPackageRoot)` is the build's package
folder (the script variable `NUGET_PACKAGES`). Extra roots a script declares with `Roots.make`
or `ImportOptions.Roots` (sibling checkouts) count as project roots.

## Prerequisites

**Definition.** A prerequisite is an input the environment must provide by itself, which no
restore step fetches, and which the build checks before doing any work, with a message that
says exactly what to install. It is the declared compromise level of a hermetic build: the lock
is "hermetic plus prerequisites".

**What is one.** Exactly one kind: the **.NET SDK at an exact version**. It covers the paths
under `$(DotnetRoot)/sdk/<version>/` and nothing else (in particular not
`$(DotnetRoot)/packs/`). It is allowed only where no package can replace it:

- `fsc`: there is no NuGet F# compiler, so `<sdk>/FSharp/fsc.dll` can only be a prerequisite;
- an imported project (`Project.import`): msbuild decides, and the SDK's compiler and analyzers
  under `sdk/<version>/` are part of the evaluation. (A project that references
  `Microsoft.Net.Compilers.Toolset` itself imports with a compiler under `$(NuGetPackageRoot)`.)

A composed `csc {}` never needs it: `Microsoft.Net.Compilers.Toolset` replaces the compiler, and
every reference has a package. So under `HERMETIC=on` a composed csc must use `CSC_TOOLSET`.

**How it is declared.** The SDK is a prerequisite only when `global.json` (searched upwards from
the project root, as the host does) pins it exactly: `"version": "<v>", "rollForward":
"disable"` (`DotNetFwk.Exact`, `Lock.Pinned`). Any other pin (`RollsForward`, `NoVersion`,
`NoGlobalJson`) means the SDK the build uses depends on the machine, so it cannot be declared.

**How it is checked.** At resolve time (`Fsc.ofSettings`, `Project.import`): the pin is exact
and the SDK the build probed is that version. At replay (`Lock.compileWith`): the SDK the entry
declares is installed under `$(DotnetRoot)`, before anything else.

**How it is recorded.** A key in the entry's `Dependencies` section, written only when the list
is not empty, so every lock without one stays byte-identical:

```json
"Dependencies": {
  "Compiler": { "Tool": "fsc", "Path": "$(DotnetRoot)/sdk/10.0.401/FSharp/fsc.dll", ... },
  "References": [ ... ],
  "Analyzers": [ ... ],
  "Packages": [ ... ],
  "Prerequisites": [
    { "Kind": "dotnet-sdk", "Version": "10.0.401", "Pin": "$(ProjectRoot)/global.json" }
  ]
}
```

It is a field of `Lock.Entry`, not of `Csc`/`Fsc`. It is written whenever an entry depends on an
exactly pinned SDK, with the mode on or off (`Lock.record`, the first `Lock.build`,
`Project.import`); the mode only decides whether a missing one is an error. Details:
[lock.md](lock.md#prerequisites).

## What `HERMETIC=on` requires

`HERMETIC` is a script variable (`var "HERMETIC" "on"`, or `-d HERMETIC=on`), default off, read
through `getVar` (changing it reruns the compiles). Values as for `CI`: `on|true|yes|1`,
`off|false|no|0`, case-insensitive; anything else fails. There is no environment fallback: the
content rule belongs to the build definition, not to the machine.

| Concern | What makes it hermetic | If missing under `HERMETIC=on` |
|---|---|---|
| Package folder | script variable `NUGET_PACKAGES` (the folder to cache and ship) | resolve fails, asks for `NUGET_PACKAGES` |
| csc compiler | `CSC_TOOLSET` (or `toolset` in the block) | resolve fails naming the SDK csc path |
| fsc compiler | `global.json` exact pin; the SDK is the prerequisite | resolve fails naming the pin found |
| `netN.0` references | a `NETCORE_REF_VERSION` entry for that major.minor (then always the NuGet `Microsoft.NETCore.App.Ref`) | resolve fails naming the `packs/` path and the version this SDK bundles |
| `netstandard2.0` references | `NETStandard.Library` (pinned package, default 2.0.3) | nothing to do |
| `netstandard2.1` references | `NETStandard.Library.Ref` 2.1.0 package | nothing to do |
| .NET Framework references | `Microsoft.NETFramework.ReferenceAssemblies.<moniker>` (pinned, default 1.0.3) | nothing to do |
| fsc .NET Framework below 4.7.1: `netstandard.dll` facade | `Microsoft.NET.Build.Extensions` 2.2.101 package (byte-identical to the SDK's) | nothing to do |
| fsc `netN.0` default `FSharp.Core` | `FSHARP_CORE_VERSION` set explicitly, or a `ref` to an `FSharp.Core.dll` | resolve fails naming the SDK's `FSharp.Core.dll` |
| fsc netstandard / .NET Framework `FSharp.Core` | `FSharp.Core` package (default 8.0.100) | nothing to do |
| Import: compiler, analyzers | `global.json` exact pin; the SDK is the prerequisite | import fails (message 11) |
| Anything else (a `ref` or `cscpath` outside the roots) | move it under the checkout or into a package | resolve fails naming the path |

`HERMETIC` is a pure gate: it never changes where an input resolves from. Every fix is a
variable, a pin, or a base default that takes the identical file from a package.

A minimal script: [../hermetic-guide.md](../hermetic-guide.md#hermetic-mode).

## Checks and messages

`<name>` is the assembly name; `<lock>` the lock path as the script wrote it.

| # | Where | Message |
|---|---|---|
| 1 | variable | `HERMETIC='<x>': expected on or off` |
| 2 | `ofSettings`, `Lock.record`, `Lock.buildWith`, `Project.import` | `'<name>': HERMETIC=on needs a package folder of the build's own: set the NUGET_PACKAGES script variable (var "NUGET_PACKAGES" ".nuget/packages", or -d NUGET_PACKAGES=<dir>)` |
| 3 | `Csc.ofSettings` | `'<name>': HERMETIC=on: the compiler <path> comes from the .NET SDK; set CSC_TOOLSET (or toolset in the csc block) to take csc from the Microsoft.Net.Compilers.Toolset package` |
| 4 | `ofSettings` | `'<name>': HERMETIC=on: the reference assemblies for net10.0 come from <path> under the .NET SDK; add a 10.0 version to NETCORE_REF_VERSION (this SDK bundles 10.0.12)` |
| 5 | `Fsc.ofSettings` | `'<name>': HERMETIC=on: the default FSharp.Core <path> comes from the .NET SDK; set FSHARP_CORE_VERSION to take the FSharp.Core package, or reference an FSharp.Core.dll` |
| 6 | `Fsc.ofSettings` | `'<name>': HERMETIC=on: fsc has no NuGet package, so the .NET SDK is a prerequisite and must be pinned exactly (found: <pin>); pin it in global.json: { "sdk": { "version": "<v>", "rollForward": "disable" } }` |
| 7 | `Fsc.ofSettings`, `Project.import` | `'<name>': HERMETIC=on: global.json (<file>) pins the .NET SDK <v>, but '<root>' does not have it; install it (dotnet-install --version <v> --install-dir <root>)` |
| 8 | `ofSettings` | `'<name>': HERMETIC=on: <path> is outside the project root '<root>' and the package folder '<folder>', and no prerequisite covers it` |
| 9 | `Lock.record`, `Lock.buildWith`, `Project.import` | `'<name>': HERMETIC=on: refusing to write the lock '<lock>': <tokenized path> is outside $(ProjectRoot) and $(NuGetPackageRoot), and no prerequisite covers it` |
| 10 | `Lock.compileWith` | `'<name>': HERMETIC=on: the lock '<lock>' names <tokenized path>, outside $(ProjectRoot) and $(NuGetPackageRoot) and not covered by a prerequisite; re-record it with HERMETIC=on` |
| 11 | `Project.import` | `'<name>': HERMETIC=on: the project's SDK is not pinned (<pin>); the import's compiler and analyzers come from the SDK, so pin it in global.json: { "sdk": { "version": "<v>", "rollForward": "disable" } }` |

All violations of one compilation are collected and reported together (one line per path), not
one per run. Messages 3 to 6 are specializations of 8: the checker recognizes the known SDK
locations and names the variable; anything it does not recognize gets 8. Message 10 says "the
lock" without a path when `Lock.compileWith` is called with an entry whose file it does not know.
An import refused for its SDK pin (7, 11) is not reported again with 9 for the SDK paths that pin
would have covered. All of them are hard failures: `FailOnError = false` does not soften them.

## Interplay

- **`CI`.** Independent. `CI=on` alone: a missing lock fails, content unchecked. `HERMETIC=on`
  alone: a developer records locks, but only complete ones. Both: the recommended CI setting
  ([workflows.md](workflows.md#d-ci-github-actions-and-gitlab-ci)).
- **`NUGET_FETCH`.** Unchanged and separate. `HERMETIC=on` with `NUGET_FETCH=off` and a warm
  folder is the offline / cached-CI build: everything the lock names is either in the folder
  or a prerequisite that was checked first. `nofetch` after `lock` is the per-target form.
- **The package folder** is one per build (`NUGET_PACKAGES`); the per-target `packageroot` was
  removed in 0.2.
- **Compiler server.** Not touched. `CSC_SERVER` works as before with the toolset's compiler.

## The fsc compromise, spelled out

A hermetic fsc build is "packages plus one SDK": `fsc.dll` from
`$(DotnetRoot)/sdk/<v>/FSharp/`, declared as the `dotnet-sdk <v>` prerequisite and pinned by
`global.json` with `rollForward: disable`; everything it reads (references, `FSharp.Core`, the
netstandard facade) from the package folder. A clean machine needs exactly: the checkout, the
package folder (or network), and that SDK. Two machines that pass the prerequisite check run the
same compiler bytes (the lock records its sha256) over the same inputs.

## The net46x facade has a package

`Microsoft.NETFramework.ReferenceAssemblies.net462` stops at 1.0.3 on nuget.org, and 1.0.3 has
no `netstandard.dll` among its `Facades` entries. `Microsoft.NET.Build.Extensions` **2.2.101**
(listed, deprecated as "Legacy") carries
`msbuildExtensions/Microsoft/Microsoft.NET.Build.Extensions/net461/lib/netstandard.dll`,
byte-identical to the SDK's facade (same in SDK 8.0.425 and 10.0.401). The base takes the
facade from that package when the reference assemblies have none, with no change to the
compilation.

## Pending

- **SDK restore** ([../roadmap.md](../roadmap.md)): `dotnet-install --version <v>` into a
  project-owned `DOTNET_ROOT`, checked against Microsoft's release hashes, would turn the
  `dotnet-sdk` prerequisite into a restorable dependency. The `Prerequisite` record is shaped so
  that it can later carry a hash and become a restore request.
- **An F# compiler toolset** ([../roadmap.md](../roadmap.md)): a small host over
  `FSharp.Compiler.Service` would remove the fsc prerequisite altogether.
- **Imports of `netN.0` projects** name `$(DotnetRoot)/packs/...` (references and the pack's
  analyzers), which the SDK prerequisite does not cover, so under `HERMETIC=on` such an import
  is refused (message 9). Candidate fix: import with `-p:NetCoreTargetingPackRoot=<empty dir>`
  so msbuild downloads the pack as a package (unverified).
