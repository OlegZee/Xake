# Hermetic mode (`HERMETIC=on`): design note

Status: proposed 2026-10-07, decisions by the user the same day. Nothing below is implemented
yet; the implementation plan is at the end. Builds on PR #37 (`NETCORE_REF_VERSION`, a list of
targeting-pack versions) and PR #38 (`NUGET_PACKAGES`, `NUGET_FETCH`).

## Purpose

Today a lock is hermetic in the sense that the build reads only what the lock names and fails
on any difference. It is not necessarily *complete*: an entry may name files under
`$(DotnetRoot)` (the SDK's csc, the targeting pack in `packs/`, the SDK's `FSharp.Core`, the
SDK's net461 `netstandard.dll` facade) that no restore step can bring to a clean machine. The
user learns this on the colleague's machine or on CI, from a path mismatch.

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
resources, generated files, `/out:` and `/doc:`. `$(NuGetPackageRoot)` is the package folder in
effect for the compilation (`NUGET_PACKAGES`, or the locked target's `packageroot`). Extra roots
a script declares with `Roots.make` (sibling checkouts) count as project roots.

## Prerequisites

**Definition.** A prerequisite is an input the environment must provide by itself, which no
restore step fetches, and which the build checks before doing any work, with a message that
says exactly what to install. It is the declared compromise level of a hermetic build: the lock
is "hermetic plus prerequisites".

**What is one today.** Exactly one kind: the **.NET SDK at an exact version**. It covers the
paths under `$(DotnetRoot)/sdk/<version>/` and nothing else (in particular not
`$(DotnetRoot)/packs/`). It is allowed only where no package can replace it:

- `fsc`: there is no NuGet F# compiler, so `<sdk>/FSharp/fsc.dll` can only be a prerequisite;
- an imported project (`Project.import`): msbuild decides, and the SDK's compiler and analyzers
  under `sdk/<version>/` are part of the evaluation.

A composed `csc {}` never needs it: `Microsoft.Net.Compilers.Toolset` replaces the compiler, and
every reference has a package. So under `HERMETIC=on` a composed csc must use `CSC_TOOLSET`.

**How it is declared.** The SDK is a prerequisite only when `global.json` (searched upwards from
the project root, as the host does) pins it exactly: `"version": "<v>", "rollForward":
"disable"` (`Lock.SdkPin.Pinned v`). Any other pin (`RollsForward`, `NoVersion`,
`NoGlobalJson`) means the SDK the build uses depends on the machine, so it cannot be declared.

**How it is checked.** At resolve time (`Fsc.ofSettings`, `Project.import`): the pin is exact
and the SDK the build probed is that version. At compile time of a lock (`Lock.compileWith`):
the SDK the entry declares is installed under `$(DotnetRoot)`. Today `DotNetFwk` falls back to
the newest SDK with a warning when the pinned one is missing; under `HERMETIC=on` that warning
becomes an error.

**How it is recorded.** A new key in the entry's `Dependencies` section, written only when the
list is not empty, so every existing lock (and every csc golden lock) stays byte-identical:

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

It is a field of `Lock.Entry` (the hermetic package), not of `Csc`/`Fsc`, so the base records do
not change (see the binary-compatibility note in `docs/roadmap.md`). For an import it is derived
from `Evaluation.SdkPin`, which stays as it is. A composed csc entry under `HERMETIC=on` has no
prerequisites, so the key never appears there.

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
| `netN.0` references | a `NETCORE_REF_VERSION` entry for that major.minor (PR #37: then always the NuGet `Microsoft.NETCore.App.Ref`) | resolve fails naming the `packs/` path and the version this SDK bundles |
| `netstandard2.0` references | `NETStandard.Library` (pinned package, default 2.0.3) | nothing to do |
| `netstandard2.1` references | `NETStandard.Library.Ref` 2.1.0 package (base change, PR A) | until PR A: resolve fails naming the `packs/` path |
| .NET Framework references | `Microsoft.NETFramework.ReferenceAssemblies.<moniker>` (pinned, default 1.0.3) | nothing to do |
| fsc .NET Framework below 4.7.1: `netstandard.dll` facade | `Microsoft.NET.Build.Extensions` 2.2.101 package (base change, PR A) | until PR A: resolve fails naming the SDK facade |
| fsc `netN.0` default `FSharp.Core` | `FSHARP_CORE_VERSION` set explicitly, or a `ref` to an `FSharp.Core.dll` | resolve fails naming the SDK's `FSharp.Core.dll` |
| fsc netstandard / .NET Framework `FSharp.Core` | `FSharp.Core` package (default 8.0.100) | nothing to do |
| Import: compiler, analyzers | `global.json` exact pin; the SDK is the prerequisite | import fails (today: a warning) |
| Anything else (a `ref` or `cscpath` outside the roots) | move it under the checkout or into a package | resolve fails naming the path |

`HERMETIC` is a pure gate: it never changes where an input resolves from. Every fix is a
variable, a pin, or a base default that moved to a package carrying the identical file (PR A).

## Checks and messages

Proposed texts (`<name>` is the assembly name):

| # | Where | Message |
|---|---|---|
| 1 | variable | `HERMETIC='<x>': expected on or off` |
| 2 | `ofSettings` | `'<name>': HERMETIC=on needs a package folder of the build's own: set the NUGET_PACKAGES script variable (var "NUGET_PACKAGES" ".nuget/packages", or -d NUGET_PACKAGES=<dir>)` |
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
locations and names the variable; anything it does not recognize gets 8.

## Interplay

- **`CI`.** Independent. `CI=on` alone: a missing lock fails, content unchecked. `HERMETIC=on`
  alone: a developer records locks, but only complete ones. Both: the recommended CI setting.
- **`NUGET_FETCH`.** Unchanged and separate. `HERMETIC=on` with `NUGET_FETCH=off` and a warm
  folder is the offline / cached-CI build: everything the lock names is either in the folder
  or a prerequisite that was checked first.
- **`packageroot`.** Proposed removal before 0.2 ships: the package folder is one per build
  (`NUGET_PACKAGES`); a per-target folder is a leftover of the lock-centric design and makes no
  sense under `HERMETIC=on`. `norestore` is renamed `nofetch` with `NUGET_FETCH` (PR C).
- **Compiler server.** Not touched. `CSC_SERVER` works as before with the toolset's compiler.

## The fsc compromise, spelled out

A hermetic fsc build is "packages plus one SDK": `fsc.dll` from
`$(DotnetRoot)/sdk/<v>/FSharp/`, declared as the `dotnet-sdk <v>` prerequisite and pinned by
`global.json` with `rollForward: disable`; everything it reads (references, `FSharp.Core`, the
netstandard facade) from the package folder. A clean machine needs exactly: the checkout, the
package folder (or network), and that SDK. Two machines that pass the prerequisite check run the
same compiler bytes (the lock records its sha256) over the same inputs.

## Finding: the net462 facade has a package

`Microsoft.NETFramework.ReferenceAssemblies.net462` stops at 1.0.3 on nuget.org (versions
1.0.0-preview.1 to 1.0.3), and 1.0.3 has no `netstandard.dll` among its 53 `Facades` entries.
But `Microsoft.NET.Build.Extensions` **2.2.101** (listed, deprecated as "Legacy", license
acceptance flag set) carries
`msbuildExtensions/Microsoft/Microsoft.NET.Build.Extensions/net461/lib/netstandard.dll`, and it
is byte-identical to the SDK's facade (sha256 `8be4a227...20dd`, same in SDK 8.0.425 and
10.0.401). So the facade can come from that package with no change to the compilation.
Not verified yet: that `dotnet restore` of a `PackageDownload` accepts the license flag without
a prompt (it should; the flag is enforced by IDEs), and an fsc net462 build with it.

## Still open

- **SDK restore** (roadmap, "SDK restore by the lock"): `dotnet-install --version <v>` into a
  project-owned `DOTNET_ROOT`, checked against Microsoft's release hashes, would turn the
  `dotnet-sdk` prerequisite into a restorable dependency. The `Prerequisites` record is shaped
  so that it can later carry a hash and become a restore request.
- **An F# compiler toolset** (roadmap): a small host over `FSharp.Compiler.Service` would remove
  the fsc prerequisite altogether.
- **Imports of `netN.0` projects** name `$(DotnetRoot)/packs/...` (references and the pack's
  analyzers), which the SDK prerequisite does not cover. Candidate fix: import with
  `-p:NetCoreTargetingPackRoot=<empty dir>` so msbuild downloads the pack as a package
  (unverified).
- **fsc locks** exist since Xake.Hermetic.Dotnet 0.2 (PR #34); the lock-side gate applies to them.

## Implementation plan (small PRs)

**A. Base: package sources with identical bytes.** net46x facade from
`Microsoft.NET.Build.Extensions` 2.2.101 when the reference assemblies have none; `netstandard2.1`
from `NETStandard.Library.Ref` 2.1.0; `FSHARP_CORE_VERSION`, when set explicitly, also applies to
`netN.0` (`lib/netstandard2.1`). Acceptance: `samples/fullframework.fsx` fsc net462 builds and
runs; the facade's sha256 is unchanged; csc golden locks byte-identical (check for any
netstandard2.1 golden first).

**B. Base: the gate.** `HERMETIC` variable (parsed like `CI`); an exact-pin reader in `DotNetFwk`
(the logic of `Project.sdkPin`); the check over every path of the composed `Csc`/`Fsc` at the end
of `ofSettings`, messages 1 to 8, all violations at once. No field added to `Csc`/`Fsc`.
Needs #37 and #38 merged. Acceptance: one test per row of the requirements table (missing →
the message; set → a resolved compilation whose paths are all under the two roots); with
`HERMETIC` unset every existing test and golden lock unchanged.

**C. Hermetic: lock side.** `Entry.Prerequisites` and the `"Prerequisites"` key (written only
when not empty, read when present); the gate in `Lock.record`/`buildWith` (message 9),
`compileWith` (message 10, plus the installed-SDK check), and `Project.import` (messages 9 and
11, the unpinned warning becomes an error, `Prerequisites` filled from the pin). Acceptance:
golden locks byte-identical; an import under an exact pin writes the key and round-trips; a lock
recorded without `HERMETIC` that names `$(DotnetRoot)/packs` is refused under it.

**D. Docs.** `docs/tasks.md` (variable row), `docs/dotnet-build.md`, `docs/hermetic/lock.md`
(prerequisites, the key), `workflows-ru.md` (drop the "в работе" mark), `CHANGELOG.md`.

## Decisions needed from the user

1. **Imports and the SDK compiler.** Proposed: an import may keep the SDK's csc and analyzers as
   the pinned `dotnet-sdk` prerequisite. Alternative: require `Microsoft.Net.Compilers.Toolset`
   in the projects too (stricter, but SDK analyzers would still name `sdk/<v>/`).
2. **`NUGET_PACKAGES` from the environment.** Proposed: only the script variable satisfies
   `HERMETIC=on` (the folder is part of the build). Alternative: the environment variable counts
   too (CI images that already set it).
3. **The net462 facade source.** Proposed: always `Microsoft.NET.Build.Extensions` 2.2.101
   (identical bytes, but a deprecated package). Alternative: keep the SDK facade and let the
   `dotnet-sdk` prerequisite cover it (then fsc net462 needs no extra package, but references
   would come from the SDK).

## Notes from the review (2026-10-09)

- Who writes prerequisites: the hermetic package at record time (`Lock.record`, the first
  `Lock.build`, `Project.import`). Signal: a path under `$(DotnetRoot)/sdk/<v>/` in an allowed
  role (fsc compiler; the import's compiler and analyzers). With an exact `global.json` pin on
  `<v>` the entry gets `{ "Kind": "dotnet-sdk", "Version": "<v>", "Pin": "$(ProjectRoot)/global.json" }`;
  without one, an error under `HERMETIC=on` and a warning under `off`. Open: write prerequisites
  always when an entry depends on the SDK (proposed), or only under `HERMETIC=on`.
- Imports: a project that references `Microsoft.Net.Compilers.Toolset` itself imports with a
  compiler under `$(NuGetPackageRoot)`, so both paths (exact SDK pin as prerequisite, or the
  Toolset in the project) already work; the user defers the decision.
- Hermeticity is a property of the configuration: only the script variable `NUGET_PACKAGES`
  satisfies the mode (decided).
- The net462 facade from `Microsoft.NET.Build.Extensions 2.2.101` is acceptable (decided).
- An F# compiler toolset (`Xake.Fsc`, a small host over the `FSharp.Compiler.Service` package)
  would remove the fsc prerequisite; roadmap.
