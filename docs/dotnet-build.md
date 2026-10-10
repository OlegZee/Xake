# .NET builds: toolchain discovery and runtime selection

This document describes how `Xake.Dotnet` — the `csc`, `fsc`, `msbuild`, `resgen` and
`resourceset` tasks — finds a compiler toolchain, which runtimes it supports, and how to
switch between them.

It does **not** apply to `sh "dotnet build ..."`: shelling out to the SDK involves none of the
machinery below. The discovery described here exists for one purpose — compiling **.NET
Framework** (full framework) binaries by invoking a compiler directly.

Source: [`src/dotnet/DotNetFwk.fs`](../src/dotnet/DotNetFwk.fs).

## Two different "runtimes"

Keep these apart, because they are chosen independently:

| | What it is | How it is chosen |
|---|---|---|
| **Host runtime** | The runtime the build script itself runs on — `dotnet fsi`, i.e. .NET 8/9/10. | The `dotnet` on `PATH`, constrained by `global.json`. |
| **Target framework** | The framework a compiled assembly is built *against* — `net10.0`, `net462`, `netstandard2.0`, mono 4.5, … | `targetfwk` on the task, else the `NETFX-TARGET` script variable, else (csc, fsc) the SDK's own .NET framework. |
| **Toolchain** | The compiler binaries and reference assemblies actually invoked. | Derived from the target framework, or overridden by the `NETFX` script variable. |

The host runtime is always modern .NET. Nothing in Xake runs on the full framework any more;
full framework is only ever a compilation *target*.

## What discovery produces

Every provider resolves a framework name to a single record:

```fsharp
type FrameworkInfo = {
    Version:      string                     // e.g. "net462", "4.0", "2.0.50727"
    InstallPath:  string
    AssemblyDirs: string list                // where grefs like "System.dll" are resolved
    ToolDir:      string
    CscTool:      string                     // absolute path to the C# compiler
    FscTool:      string option -> string option  // version -> path to the F# compiler
    MsbuildTool:  string
    EnvVars:      (string * string) list     // env vars applied to the compiler process
}
```

`DotNetFwk.locateFramework : string option -> FrameworkInfo` is the entry point, and it is
memoized (`CommonLib.memoize`) — probing happens once per framework name per process.
`DotNetFwk.locateAssembly` is likewise memoized and resolves a bare name such as
`System.Core.dll` against `AssemblyDirs`, falling back to the bare name when nothing matches.

Probing runs outside of any recipe, so it uses `pexecSync` (`Async.RunSynchronously` over the
engine's process launcher) rather than the `shell` task. That is deliberate and confined to the
memoized lookups — it holds a CPU slot while it runs.

## The three providers

### 1. `sdkImpl` — .NET SDK compilers over NuGet reference assemblies (default)

This is the modern path and the reason a full-framework binary can be produced on macOS or
Linux with nothing installed but the .NET SDK. Neither a Framework installation nor the
Windows registry is consulted.

- **Compilers** come from the installed SDK:
  - `csc` — `<sdk>/Roslyn/bincore/csc` (a native apphost, launched directly).
  - `fsc` — `<sdk>/FSharp/fsc.dll`, a managed dll. The `fsc` task records it
    (`DotNetFwk.fscCompiler`) and starts it as `dotnet fsc.dll`. `FrameworkInfo.FscTool` still
    returns a tiny launcher script (`.cmd` on Windows, `sh` elsewhere) in the temp directory
    that execs `dotnet <path>/fsc.dll "$@"`; the same trick wraps `dotnet msbuild`. The script
    name embeds a hash of the host+arguments, so it is stable and re-created at most once.
- **SDK location** is probed in this order, taking the first directory that contains an `sdk`
  subdirectory, then the highest version inside it:
  1. the directory of `DOTNET_HOST_PATH`, or of the current process's main module when it is
     named `dotnet`;
  2. `DOTNET_ROOT`;
  3. `/usr/local/share/dotnet`;
  4. `/usr/share/dotnet`;
  5. `%ProgramFiles%\dotnet`.
  "Highest version" is a numeric, component-wise comparison — `10.0.400` beats `8.0.424`, and a
  released version beats a preview (`10.0.100-rc.1`) when both are present.
- **`global.json` is honoured.** Within that root the SDK is the one the `dotnet` host selects
  for the project: `DotNetFwk` runs `<root>/dotnet --version` with the build's `ProjectRoot` as
  the working directory (so a `global.json` in it or any parent applies, `rollForward`
  included) and takes `<root>/sdk/<printed version>`. With no `global.json` the host prints the
  newest SDK, which is what the "highest version" pick above gives, so nothing changes. When the
  host fails — the pinned SDK is not installed — or prints a version that has no directory under
  this root, the highest version is taken and the compile traces a warning naming the version
  `global.json` asked for. The probe is a synchronous `pexecSync` call, cached per project root
  for the life of the process (a `global.json` edited mid-run is not re-read);
  `DotNetFwk.locateFrameworkIn root` is the entry point and `csc`, `fsc` and `msbuild` pass their
  `ProjectRoot`. `DotNetFwk.locateFramework`, which has no build context, probes from the
  current directory.
- **Reference assemblies** come from the `Microsoft.NETFramework.ReferenceAssemblies.<moniker>`
  NuGet package at an exact version, `1.0.3` unless the script variable
  `NETFX_REFERENCE_ASSEMBLIES_VERSION` says otherwise, read out of the build's package
  folder ([Where packages go](#where-packages-go)) at
  `<pkg>/<version>/build/.NETFramework/<version>`. `AssemblyDirs` is that directory plus its
  `Facades` subdirectory. Another version present in the cache is not used.
- **First-run restore**: when the package is not in the cache, the tasks
  (`DotNetFwk.resolveFramework`) fetch it with `DotNetFwk.restorePackage`: one synthesized
  `PackageDownload` project under `<ProjectRoot>/obj/xake/restore/<n>/`, so the repository's
  `nuget.config` applies. `locateFramework` without a build context does the same
  synchronously from the directory it probes. A failed restore fails with a message naming the
  package and version. A clean machine therefore needs no preparation, but the first
  full-framework compile requires network access (or a feed).
- **netstandard**, `netstandard2.0` and `netstandard2.1`, is resolved by the same provider but
  against a different reference set, each from a package restored the same way when missing:
  2.0 from `<pkg cache>/netstandard.library/<v>/build/netstandard2.0/ref`, `<v>` being `2.0.3`
  unless the script variable `NETSTANDARD_LIBRARY_VERSION` says otherwise; 2.1 from
  `<pkg cache>/netstandard.library.ref/<v>/ref/netstandard2.1`, `<v>` being `2.1.0` unless
  `NETSTANDARD_LIBRARY_REF_VERSION` says otherwise. The SDK's
  `<dotnet>/packs/NETStandard.Library.Ref/2.1.0` is that same package unpacked (its
  `ref/netstandard2.1` is byte-identical), but no restore step can bring it to a machine that
  lacks it, so it is not read. `netstandard.dll` alone carries the whole surface, so that single directory is the
  entire `AssemblyDirs`. The `fsc` task recognizes the profile and adds `--targetprofile:netstandard`
  and `-r:netstandard.dll` (instead of `mscorlib.dll`) on top of `--noframework`. `csc` has no
  netstandard support.
- **.NET** (`net5.0` and later: `net6.0`, `net8.0`, `net10.0`; also spelled `sdk-net10.0`) is
  resolved against the targeting pack `Microsoft.NETCore.App.Ref`
  (`DotNetFwk.sdkImpl.netcoreRefSource`):
  1. **A `NETCORE_REF_VERSION` entry for the framework's major.minor**: always the NuGet package
     `Microsoft.NETCore.App.Ref` at exactly that version, restored by `DotNetFwk.restorePackage`
     like the other reference packs (synthesized project under the project root, the repository's
     `nuget.config`, exact version) and read from
     `<pkg cache>/microsoft.netcore.app.ref/<ver>/ref/<moniker>`. An installed pack under
     `<dotnet>/packs` is never used then, not even one of that very version.
  2. **Else (no entry) the installed pack**:
     `<dotnet>/packs/Microsoft.NETCore.App.Ref/<ver>/ref/<moniker>` at the SDK's version (the
     `TargetingPackVersion` of the `KnownFrameworkReference` with `Include="Microsoft.NETCore.App"`,
     `TargetFramework="<moniker>"` in the probed SDK's
     `Microsoft.NETCoreSdk.BundledVersions.props`: 10.0.12 for net10.0 and 6.0.36 for net6.0 on SDK
     10.0.401), else the newest installed pack of that major.minor (one an installed SDK of that
     major brought along), so a machine that has a pack downloads nothing.
  3. **Else the NuGet package** at the SDK's version, restored as in 1.
  4. **A framework the SDK does not know** (one newer than the SDK, e.g. `net99.0`) has no
     version to restore and fails: `the SDK <v> does not know target framework 'net99.0' ...;
     use an SDK that supports it`. A pin makes such a framework usable.

  `NETCORE_REF_VERSION` is a script variable read through `getVar`: a list of versions separated
  by `;`, `,` or whitespace, e.g. `var "NETCORE_REF_VERSION" "6.0.36;7.0.20"`. Each entry pins
  the pack of the framework with the same major.minor (`6.0.36` pins `net6.0`); a framework
  without an entry follows rule 2. Two entries for one major.minor, or an entry that is not a
  3-part version, fail with a message naming `NETCORE_REF_VERSION` and the entry. Changing it
  reruns the compile. Under `HERMETIC=on` (see [Hermetic mode](#hermetic-mode)) every `netN.0`
  target used must have an entry.

  **Locks.** Without a pin the lock depends on where the pack came from: an installed pack is
  under the SDK (`$(DotnetRoot)/packs/Microsoft.NETCore.App.Ref/<ver>/...`), a restored one under
  the package cache (`$(NuGetPackageRoot)/microsoft.netcore.app.ref/<ver>/...`), so the same
  script can lock different paths on different machines. A pinned framework's references are
  always `$(NuGetPackageRoot)/microsoft.netcore.app.ref/<ver>/ref/<moniker>/...`, on every machine
  whatever SDKs it has installed -- a lock the restore step can always fetch.

  `csc` and `fsc` reference every
  `*.dll` of that directory (`DotNetFwk.frameworkReferences`), as the SDK does; `fsc` adds
  `--targetprofile:netcore` and, unless a `ref` names an `FSharp.Core.dll`, the SDK's own
  `<sdk>/FSharp/FSharp.Core.dll` (for netstandard and .NET Framework it is the `FSharp.Core`
  package instead, `DotNetFwk.fsharpCoreReference`). No other provider knows these monikers, so they go to the
  SDK provider on every OS.
- **The default target framework** of `csc` and `fsc`, when neither `targetfwk` nor
  `NETFX-TARGET` is set, is the .NET framework of the probed SDK (`DotNetFwk.sdkFramework`):
  `net10.0` when `dotnet --version` in the project root prints 10.0.x. Only when it cannot be
  determined does the task fail asking for a target framework.

Accepted framework names — anything that normalizes to one of the known monikers
`net20 net35 net40 net45 net451 net452 net46 net461 net462 net47 net471 net472 net48`, plus
`netstandard2.0` and `netstandard2.1` and the .NET monikers `net<major>.<minor>` from `net5.0`
on, as far as the probed SDK knows them (these three kinds accept no abbreviations beyond an
`sdk-` prefix).
Normalization lowercases and strips `sdk-`, `net-`, `net`, `-full`, dots and dashes, so all of
these mean `net462`: `net-4.6.2`, `4.6.2`, `net462`, `sdk-net462`. Likewise `4.5-full`, `4.5`
and `net-4.5` all mean `net45`.

### 2. `msImpl` — a real .NET Framework installation, via the Windows registry

Windows only. Reads `HKLM\SOFTWARE\Microsoft\.NETFramework\InstallRoot` and maps a profile name
to a framework directory and a set of reference-assembly directories:

| Profile | Version | Tools | Extra env |
|---|---|---|---|
| `net-20`, `net-2.0`, `2.0` | `2.0.50727` | `<root>/v2.0.50727` | — |
| `net-35`, `net-3.5`, `3.5` | `3.5` | `<root>/v3.5` + Reference Assemblies v3.0/v3.5 | `COMPLUS_VERSION=v2.0.50727` |
| `net-40`…`4.5-full` | `4.0` | `<root>/v4.0.30319` (+ WPF, + Reference Assemblies v4.0) | `COMPLUS_VERSION=v4.0.30319` |

`CscTool` is `csc.exe` from that directory, `MsbuildTool` is `msbuild.exe`, and `fsc.exe` is
located through `HKLM\SOFTWARE\Wow6432Node\Microsoft\FSharp\<ver>\Runtime\v4.0`, trying
`4.1`, `4.0`, `3.1`, `3.0` in order unless a version is pinned (see `fscver`/`FSCVER`).

This provider **throws** rather than returning an error when the registry key is missing; the
dispatcher below catches that and falls through.

### 3. `monoFwkImpl` — Mono

Legacy, kept as a fallback. Mono's prefix and library directory come from `pkg-config`
(`pkg-config --variable=prefix mono`), or, on Windows, from
`HKLM\SOFTWARE\[Wow6432Node\]Novell\Mono` / `…\Mono`.

- `CscTool` — `mcs` for mono ≥ 3.0, `dmcs` below that.
- `FscTool` — `fsharpc`.
- `MsbuildTool` — `xbuild`.
- `AssemblyDirs` — `<libdir>/mono/<profile>` for profile `2.0`, `3.5`, `4.0` or `4.5`.

Accepted names: `mono-20`/`mono-2.0`/`2.0`, `mono-35`/`3.5`, `mono-40`/`4.0`, `mono-45`/`4.5`.

Mono requires `pkg-config` on `PATH`. On Linux, `sudo apt-get install mono-complete` provides
both (root certificates may also need `mozroots --import --sync`). On macOS `pkg-config` ships
with mono but is *not* on `PATH` by default — the options are described on the
[monobjc mailing list](http://www.mail-archive.com/users@lists.monobjc.net/msg00235.html).

## Which provider is used

`impl.locateFramework` picks a provider from the framework name and the host OS, with a
fallback chain — `A |> orElse B` tries `A`, and on *any* failure (returned error **or**
exception) tries `B`, concatenating both error messages if neither works:

| Condition | Provider chain |
|---|---|
| name starts with `mono-` | mono only |
| name starts with `sdk-`, or is a .NET moniker (`net8.0`, `net10.0`) | SDK only |
| host is Unix (Linux, macOS) | **SDK**, then mono |
| host is Windows running on mono | mono, then SDK |
| host is Windows | **registry**, then SDK |

So the two prefixes are the explicit escape hatches: `mono-4.5` forces mono even where the SDK
would work, and `sdk-net462` forces the SDK path even on a Windows box that has the Framework
installed.

When no framework is requested at all (no `NETFX` for a bare `msbuild` task; `csc` and `fsc`
always request one, the SDK's own .NET by default), the profiles `4.0`, `3.5`, `3.0`, `2.0` are tried in that
order and the first one that resolves wins. Note that `3.0` is not a known SDK moniker, so on
Unix the practical outcome is `net40`.

Failure to resolve is fatal: `locateFramework` raises with the accumulated error message.

## How to switch

### Per task

```fsharp
"temp/helloworld.exe" ..> csc {
    targetfwk "net-4.6.2"          // framework to compile against
    src !!"helloworld.cs"
    grefs ["System.dll"]           // resolved through FrameworkInfo.AssemblyDirs
}
```

`csc` also takes `cscpath` to bypass discovery of the C# compiler entirely (`fsc` has no
equivalent: its compiler is the SDK's `<sdk>/FSharp/fsc.dll`, `DotNetFwk.fscCompiler`). `fsc`
takes `fscver` (or `FSCVER`) to pin the F# compiler version, which only the Windows registry
lookup reads (untested); the SDK path ignores it.

Setting a target framework has two side effects beyond tool selection: `grefs` are resolved to
absolute paths under the framework's `AssemblyDirs`, `mscorlib.dll` is added, and the compiler
is invoked with `/nostdlib+ /noconfig` (`--noframework` for `fsc`); a .NET target references its
whole targeting pack instead of `mscorlib.dll`. Neither `csc` nor `fsc` needs a target framework
named: with no `targetfwk` and no `NETFX-TARGET` they target the SDK's own .NET (`net10.0` on
SDK 10.0.x), and fail asking for one only when that cannot be determined.

### Per script, via variables

| Variable | Effect |
|---|---|
| `NETFX-TARGET` | Default `targetfwk` for every compiler task that does not set its own. |
| `NETFX` | Framework whose **tools** are used, overriding whatever is being targeted. Also the only framework selector the `msbuild` task reads. |
| `FSCVER` | F# compiler version `fsc` asks for, when not set per task via `fscver`; read only by the Windows registry provider. |
| `FSHARP_CORE_VERSION` | Version of the `FSharp.Core` package `fsc` references when no `ref` names an `FSharp.Core.dll`: for netstandard and .NET Framework always (default 8.0.100); for a .NET (`netN.0`) target only when set, then its `lib/netstandard2.1` build instead of the SDK's own `FSharp.Core.dll`. |
| `NETSTANDARD_LIBRARY_VERSION` | Version of the `NETStandard.Library` package netstandard2.0 compiles against (default 2.0.3). |
| `NETSTANDARD_LIBRARY_REF_VERSION` | Version of the `NETStandard.Library.Ref` package netstandard2.1 compiles against (default 2.1.0). |
| `NUGET_PACKAGES` | The build's package folder (relative to the project root, or absolute), overriding the environment variable; see [Where packages go](#where-packages-go). |
| `NUGET_FETCH` | `off` stops Xake from downloading anything into the package folder; a missing package then fails the compile. Default `on`. |
| `HERMETIC` | `on` checks that every composed compilation names only paths under the project root and the script variable `NUGET_PACKAGES`'s folder (plus fsc's compiler under an exactly pinned SDK); default `off`. See [Hermetic mode](#hermetic-mode). |
| `NETCORE_REF_VERSION` | Pins of the `Microsoft.NETCore.App.Ref` targeting pack, one per major.minor (`6.0.36;7.0.20`): a pinned `netN.0` framework always takes the NuGet package at that version, never an installed pack; see [the SDK provider](#1-sdkimpl--net-sdk-compilers-over-nuget-reference-assemblies-default). |

Precedence inside a task: `targetfwk` → `NETFX-TARGET` → the SDK's own .NET framework. The toolchain is then
`NETFX` if set, otherwise the resolved target framework, otherwise the default probe order.

```bash
dotnet fsi build.fsx -- -- build -d NETFX-TARGET:net-4.6.2
dotnet fsi build.fsx -- -- build -d NETFX:mono-4.5
```

### Relevant environment variables

`DOTNET_HOST_PATH`, `DOTNET_ROOT` — where the SDK is looked for.
`NUGET_PACKAGES` — where reference-assembly packages are read from and restored to, unless the
script variable of the same name says otherwise.

### Where packages go

The packages Xake restores or reads itself -- reference packs, the `toolset` compiler, `fsc`'s
default `FSharp.Core` -- live in one folder, `DotNetFwk.packageRoot ()` inside a recipe:

1. the script variable `NUGET_PACKAGES` (relative to the project root, or absolute), read and
   recorded as a dependency by every compile that resolves a framework;
2. else the environment variable `NUGET_PACKAGES`;
3. else `~/.nuget/packages`.

`DotNetFwk.restorePackage None` and `downloadPackages None` use that folder and pass it to the
child `dotnet restore` as the environment variable `NUGET_PACKAGES`; `Some dir` names a folder
explicitly (what `packageroot` after `lock` does in Xake.Hermetic.Dotnet, per target).
`resolveFramework` reads its reference packs from it and memoizes per folder. The functions with
no build context -- `DotNetFwk.nugetRoot`, `normalizedPackageRoot None`, `locateFramework`,
`locateFrameworkIn`, `locateFrameworkWith` -- see only the environment.

**Turning fetching off.** The script variable `NUGET_FETCH=off` (`-d NUGET_FETCH:off`, e.g.
on CI with a warm cache) stops every download into the package folder: `restorePackage` and
`downloadPackages` start no `dotnet restore`, and a package that is not there fails the build
before the compiler runs, naming the package id and version and saying fetching is off. Values
`off`, `false`, `no`, `0` turn it off; anything else, or unset, leaves it on.
`DotNetFwk.fetchEnabled ()` is the decision as a recipe (what `Restore.Options.Enabled` of
Xake.Hermetic.Dotnet is to default to); `norestore` after `lock` is the per-target form.

`#r "nuget: ..."` in the build script is restored by `dotnet fsi` before Xake runs; only the
environment variable reaches it.

## Hermetic mode

`HERMETIC=on` (`var "HERMETIC" "on"` in the script, or `-d HERMETIC=on`) turns completeness into
a checked property of every composed `csc`/`fsc` compilation:

> A resolved compilation names no path outside the project root and the build's own package
> folder, except paths covered by a prerequisite.

"Names" covers every path: the compiler, the references, the analyzers, the sources, every other
input switch (`/res:`, `/keyfile:`, ...), the outputs (`/out:`, `/doc:`), generated files, `.resx`
files and their `.resources`, and `cscpath`. The check runs at the end of `Csc.ofSettings` and
`Fsc.ofSettings` (so `resolve`, `compile` and `lock` alike) and reports **all** violations of one
compilation together, one line each, the compiler first, then the references, then the rest. It is
a pure gate: it never changes where an input resolves from. Every fix is a variable, a pin, or a
default that already comes from a package.

`HERMETIC` is read with `getVar` (changing it reruns the compiles); values as for `CI`:
`on|true|yes|1`, `off|false|no|0`, case-insensitive; default off; anything else fails with
`HERMETIC='<x>': expected on or off`. There is no environment fallback: the rule belongs to the
build definition, not to the machine. It is independent of `CI` (which is about whether a lock
may be recorded) and of `NUGET_FETCH` (`HERMETIC=on` with `NUGET_FETCH=off` and a warm package
folder is the offline build).

**Package folder.** Only the script variable `NUGET_PACKAGES` makes the package folder the
build's own; the environment variable (or `~/.nuget/packages`) does not satisfy the mode, since
the folder is then a property of the machine. `DotNetFwk.packageRootWithSource ()` returns the
folder with where it came from.

**Prerequisites.** A prerequisite is an input the environment must provide, which no restore
step fetches, checked up front. There is exactly one kind: the **.NET SDK at an exact version**
(`dotnet-sdk <v>`), covering `<dotnetRoot>/sdk/<v>/` and nothing else (not `packs/`). It is
allowed only for fsc's compiler (there is no NuGet F# compiler), and only when the `global.json`
that applies to the project root pins it exactly, `{ "sdk": { "version": "<v>", "rollForward":
"disable" } }`; any other pin means the SDK depends on the machine. A composed csc never needs
it (`CSC_TOOLSET`). `DotNetFwk.globalJsonPin` reads the pin, `DotNetFwk.sdkPrerequisite root path`
says which prerequisite covers a path (what the hermetic lock records), and
`HermeticMode.check name roots inputs` is the rule itself over any list of paths and roots, for
example the tokenized paths of a lock.

| Concern | What makes it hermetic | Under `HERMETIC=on` when missing |
|---|---|---|
| Package folder | script variable `NUGET_PACKAGES` | `'<name>': HERMETIC=on needs a package folder of the build's own: set the NUGET_PACKAGES script variable (...)` |
| csc compiler | `CSC_TOOLSET` (or `toolset` in the block) | `... the compiler <path> comes from the .NET SDK; set CSC_TOOLSET (...)` |
| fsc compiler | `global.json` exact pin; the SDK is the prerequisite | `... fsc has no NuGet package, so the .NET SDK is a prerequisite and must be pinned exactly (found: <pin>); pin it in global.json: ...`; a pinned SDK that is not installed: `... global.json (<file>) pins the .NET SDK <v>, but '<root>' does not have it; install it (dotnet-install --version <v> --install-dir <root>)` |
| `netN.0` references | a `NETCORE_REF_VERSION` entry for that major.minor | `... the reference assemblies for net10.0 come from <packs path> under the .NET SDK; add a 10.0 version to NETCORE_REF_VERSION (this SDK bundles 10.0.12)` (once per framework) |
| `netstandard2.0` / `netstandard2.1` references | `NETStandard.Library` / `NETStandard.Library.Ref` packages | nothing to do |
| .NET Framework references | `Microsoft.NETFramework.ReferenceAssemblies.<moniker>` | nothing to do |
| fsc .NET Framework `netstandard.dll` facade | `Microsoft.NET.Build.Extensions` 2.2.101 package | nothing to do |
| fsc `netN.0` default `FSharp.Core` | `FSHARP_CORE_VERSION` set, or a `ref` to an `FSharp.Core.dll` | `... the default FSharp.Core <path> comes from the .NET SDK; set FSHARP_CORE_VERSION (...)` |
| fsc netstandard / .NET Framework `FSharp.Core` | `FSharp.Core` package | nothing to do |
| anything else (a `ref`, `cscpath`, a source outside the checkout) | move it under the checkout or into a package | `... <path> is outside the project root '<root>' and the package folder '<folder>', and no prerequisite covers it` |

A hermetic setup for a csc build on SDK 10.0.x, and the extra pin an fsc build needs:

```fsharp
xakeScript {
    var "HERMETIC" "on"
    var "NUGET_PACKAGES" ".nuget/packages"
    var "CSC_TOOLSET" "4.12.0"
    var "NETCORE_REF_VERSION" "10.0.12"
    var "FSHARP_CORE_VERSION" "8.0.100"   // fsc for net10.0
    ...
}
```

```json
{ "sdk": { "version": "10.0.401", "rollForward": "disable" } }
```

Not covered here (the hermetic package's lock side): recording the prerequisite in a lock entry,
refusing to record or compile a lock that names other paths, and `Project.import`.

## Support matrix

| Target | Linux / macOS | Windows |
|---|---|---|
| `csc`, `fsc` → net5.0 and later (the default: the SDK's own) | yes, the SDK's targeting pack | yes, the SDK's targeting pack |
| `csc` → net20…net48 | yes, SDK Roslyn + NuGet reference assemblies | yes, registry Framework first, SDK as fallback |
| `fsc` → net4x | yes, SDK fsc + NuGet reference assemblies, FSharp.Core package and a netstandard facade (see below) | yes, as on Linux/macOS (the registry provider, when it knows the profile, takes the installed `fsc.exe`) |
| `msbuild` | `dotnet msbuild` through a launcher script | registry `msbuild.exe`, else `dotnet msbuild` |
| `resgen`, `.resx` embedded in `csc`/`fsc` | **no** — needs the full framework | yes, when Xake itself runs on net462 |
| mono profiles 2.0/3.5/4.0/4.5 | yes, with mono + `pkg-config` | yes, with mono installed |

## Known limitations

- **An `fsc` exe for .NET Framework below 4.7.2 needs a runtime `netstandard.dll`.** fsc
  compiles for .NET Framework from any host: the `FSharp.Core` package's netstandard2.0 build
  (`FSHARP_CORE_VERSION`, default 8.0.100) and a type-forwarding `netstandard.dll` facade are
  referenced implicitly (the reference assemblies' `Facades/netstandard.dll` from 4.7.1, else
  `msbuildExtensions/Microsoft/Microsoft.NET.Build.Extensions/net461/lib/netstandard.dll` of the
  `Microsoft.NET.Build.Extensions` 2.2.101 package, byte-identical to the SDK's own
  `Microsoft/Microsoft.NET.Build.Extensions/net461/lib/netstandard.dll`), so the
  output references `netstandard 2.0.0.0`. The 4.7.2+ runtime resolves it; an older one needs
  the facade next to the exe, and Xake does not copy it (msbuild does). fsc copies
  `FSharp.Core.dll` next to the output unless `args ["--nocopyfsharpcore"]`. Before 3.5 this
  failed with FS0074 ("You must add a reference to assembly 'netstandard'") unless the script
  referenced both itself.
- **`.resx` compilation requires the full framework.** `ResXResourceReader` lives in
  `System.Windows.Forms`; the netstandard2.0 build of `Xake.Dotnet` fails with an explicit
  message. Only the `net462` asset in the package can do it, i.e. this works when the build
  script itself runs on the full framework.
- **The first full-framework build needs the network** to restore the reference assemblies.
- **`resgen`/mono paths are lightly exercised.** There is no CI coverage for mono; the SDK path
  is what the tests and `samples/fullframework.fsx` exercise.

## Troubleshooting

- `the .NET SDK is not found, cannot locate the compilers` — `dotnet` is not on `PATH` and
  neither `DOTNET_HOST_PATH` nor `DOTNET_ROOT` points at an SDK install.
- `reference assemblies for 'netXXX' are not available` — the restore of
  `Microsoft.NETFramework.ReferenceAssemblies.netXXX` failed; run it by hand to see why.
- `'X' is not a known .NET Framework profile` — the name did not normalize to a known moniker.
- `Failed to obtain mono framework (check if mono and pkg-config are installed)` — `pkg-config`
  is missing from `PATH`.
- Compiler diagnostics are logged at `Warning`/`Error` and everything else at `Verbose`; run
  with `-vv` (or `filelog "build.log" Diag`) to see the full command line, which is traced at
  `Debug`.

## History: what changed and when

**Before** (the state `src/dotnet` arrived in when Xake.Dotnet was merged from its own
repository, commit `4209d6b`):

- Two providers only: mono and the Windows registry.
- The choice was rigid — Unix always meant mono; there was no fallback of any kind, so a
  missing mono or a missing registry key was a hard failure.
- Consequence: on macOS and Linux, `csc`/`fsc` were unusable without a mono installation, and
  full-framework output on CI was effectively Windows-only.
- The module carried its own copies of the engine's `ProcessExec` and `CommonLib`.

**Now** (branch `feature/rulesless-syntax`):

- `sdkImpl` added (commit `1edb19b`, *Build for full framework without a Framework
  installation*) — SDK compilers plus NuGet reference assemblies, with automatic restore,
  launcher scripts for the managed `fsc`/`msbuild`, and version-aware SDK probing.
- The dispatcher became a fallback chain, with `sdk-` and `mono-` as explicit overrides, and
  the SDK is now the default on Unix.
- `Xake.Dotnet` ships inside the single `Xake` NuGet package (commit `cdf1a0f`) instead of a
  package of its own, and reuses the engine's internals through `InternalsVisibleTo`
  (commit `18032d4`) rather than duplicating them.
- The compilers are invoked through the engine's `shell` task (commit `9be7870`), so their
  output goes through normal logging; `stdoutlevel`/`erroutlevel` were added to `shell` for
  that.
- `fsc`, `msbuild` and `resgen` gained computation-expression builders (commit `ce34454`),
  matching the existing `csc` one.
- Compiler diagnostics without a source position (`error FS0084: ...`) are now classified
  correctly; previously all compiler output was logged at `Verbose` and a failing compile
  looked silent.
- The `#if NET46` guards around resx support were dead — `net462` defines `NETFRAMEWORK`,
  `NET462` and `NET46_OR_GREATER`, but not `NET46`. They now use `NETFRAMEWORK`.
