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
| **Target framework** | The framework a compiled assembly is built *against* — `net462`, `net40`, mono 4.5, … | `targetfwk` on the task, or the `NETFX-TARGET` script variable. |
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
  - `fsc` — `<sdk>/FSharp/fsc.dll`, a managed dll. It cannot be executed directly, so a tiny
    launcher script (`.cmd` on Windows, `sh` elsewhere) is written to the temp directory that
    execs `dotnet <path>/fsc.dll "$@"`. The same trick wraps `dotnet msbuild`. The script name
    embeds a hash of the host+arguments, so it is stable and re-created at most once.
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
- **Reference assemblies** come from the `Microsoft.NETFramework.ReferenceAssemblies.<moniker>`
  NuGet package, version `1.0.3`, read straight out of the package cache
  (`NUGET_PACKAGES`, or `~/.nuget/packages`) at
  `<pkg>/<version>/build/.NETFramework/<version>`. `AssemblyDirs` is that directory plus its
  `Facades` subdirectory.
- **First-run restore**: when the package is not in the cache, a throwaway
  `refasm.csproj` is written to the temp directory and `dotnet restore` is run on it, then the
  lookup is retried. A clean machine therefore needs no preparation, but the first
  full-framework compile requires network access.
- **netstandard**, `netstandard2.0` and `netstandard2.1`, is resolved by the same provider but
  against a different reference set: `<dotnet>/packs/NETStandard.Library.Ref/<ver>/ref/<moniker>`
  when the SDK carries it (2.1 only, today), otherwise
  `<pkg cache>/netstandard.library/2.0.3/build/<moniker>/ref`, restored the same way when
  missing. `netstandard.dll` alone carries the whole surface, so that single directory is the
  entire `AssemblyDirs`. The `fsc` task recognizes the profile and adds `--targetprofile:netstandard`
  and `-r:netstandard.dll` (instead of `mscorlib.dll`) on top of `--noframework`. `csc` has no
  netstandard support.

Accepted framework names — anything that normalizes to one of the known monikers
`net20 net35 net40 net45 net451 net452 net46 net461 net462 net47 net471 net472 net48`, plus
`netstandard2.0` and `netstandard2.1` (which accept no abbreviations beyond an `sdk-` prefix).
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
| name starts with `sdk-` | SDK only |
| host is Unix (Linux, macOS) | **SDK**, then mono |
| host is Windows running on mono | mono, then SDK |
| host is Windows | **registry**, then SDK |

So the two prefixes are the explicit escape hatches: `mono-4.5` forces mono even where the SDK
would work, and `sdk-net462` forces the SDK path even on a Windows box that has the Framework
installed.

When no framework is requested at all (no `targetfwk`, no `NETFX-TARGET`, no `NETFX` — which is
the case for a bare `msbuild` task), the profiles `4.0`, `3.5`, `3.0`, `2.0` are tried in that
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
equivalent — use `fscver`, or the `NETFX` variable). `fsc` takes `fscver` to pin the F#
compiler version the registry lookup asks for.

Setting a target framework has two side effects beyond tool selection: `grefs` are resolved to
absolute paths under the framework's `AssemblyDirs`, `mscorlib.dll` is added, and the compiler
is invoked with `/nostdlib+ /noconfig` (`--noframework` for `fsc`). Without it, `grefs` are
passed through verbatim and the compiler's default response file applies.

### Per script, via variables

| Variable | Effect |
|---|---|
| `NETFX-TARGET` | Default `targetfwk` for every compiler task that does not set its own. |
| `NETFX` | Framework whose **tools** are used, overriding whatever is being targeted. Also the only framework selector the `msbuild` task reads. |
| `FSCVER` | F# compiler version `fsc` asks for, when not set per task via `fscver`. |

Precedence inside a task: `targetfwk` → `NETFX-TARGET` → none. The toolchain is then
`NETFX` if set, otherwise the resolved target framework, otherwise the default probe order.

```bash
dotnet fsi build.fsx -- -- build -d NETFX-TARGET:net-4.6.2
dotnet fsi build.fsx -- -- build -d NETFX:mono-4.5
```

### Relevant environment variables

`DOTNET_HOST_PATH`, `DOTNET_ROOT` — where the SDK is looked for.
`NUGET_PACKAGES` — where reference-assembly packages are read from and restored to.

## Support matrix

| Target | Linux / macOS | Windows |
|---|---|---|
| `csc` → net20…net48 | yes, SDK Roslyn + NuGet reference assemblies | yes, registry Framework first, SDK as fallback |
| `fsc` → net4x | **no** (see below) | yes, when the Framework F# compiler is installed |
| `msbuild` | `dotnet msbuild` through a launcher script | registry `msbuild.exe`, else `dotnet msbuild` |
| `resgen`, `.resx` embedded in `csc`/`fsc` | **no** — needs the full framework | yes, when Xake itself runs on net462 |
| mono profiles 2.0/3.5/4.0/4.5 | yes, with mono + `pkg-config` | yes, with mono installed |

## Known limitations

- **`fsc` cannot target full framework through the SDK.** It needs a net462-compatible
  `FSharp.Core`, and the SDK ships only the netstandard2.0 one, which drags in a `netstandard`
  facade that the net4x reference-assembly packages do not carry. Workaround: reference an
  `FSharp.Core.dll` of your own, as `samples/features.fsx` does.
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
