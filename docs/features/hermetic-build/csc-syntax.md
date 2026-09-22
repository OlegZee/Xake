# `csc {}` as it stands

`csc {}` is the C# compiler task. It builds one `Lock.Project` -- a project's exact compiler
command line, plus what it references and how to check it -- and hands that to a single runner.
Settings are intent, `Lock.Project` is the resolved compilation: there are two ways to arrive
at a `Lock.Project` (compose one from settings, or bring one in from a lock) but only one thing
that ever shells out to the compiler.

## Composed mode

```fsharp
"temp/helloworld.exe" ..> csc {
    targetfwk "net-4.6.2"
    src !!"helloworld.cs"
    grefs ["System.dll"]
}
```

(`samples/fullframework.fsx`; `src/tests/DotnetTasksTests.fs`, `runs csc task (full test)`, shows
the same shape with `out`, `Src`, `TargetFramework`, `RefGlobal` set directly on the record.)

Without `fromlock`, `Csc` calls `resolve`, which turns the settings below into a
`Lock.Project` at recipe time -- same argument order the task has always produced -- and passes
it to the runner. Every custom operation of `CscSettingsBuilder`:

| Keyword | Argument | Does | Default |
|---|---|---|---|
| `platform` | `TargetPlatform` | `/platform:` | `AnyCpu` |
| `target` | `TargetType` | `/target:`, resolved from the output name when `Auto` | `Auto` |
| `targetfwk` | `string` | target framework to compile against; see [`docs/dotnet-build.md`](../../dotnet-build.md) | `null` (falls back to the `NETFX-TARGET` var) |
| `out` | `File` | output file; `/out:` | `File.undefined` (task infers a target file) |
| `src` | `Fileset` | source files | `Fileset.Empty` |
| `ref` | `Fileset` | adds to the reference fileset (`+`) | -- |
| `refif` | `bool * Fileset` | adds the fileset conditionally (`+?`) | -- |
| `refs` | `Fileset` | replaces the reference fileset | -- |
| `grefs` | `string list` | GAC/framework-global references, resolved through the framework's `AssemblyDirs` when `targetfwk`/`NETFX-TARGET` is set | `[]` |
| `resources` | `ResourceFileset` | adds one embedded-resource fileset (prepended) | -- |
| `resourceslist` | `ResourceFileset list` | adds several at once | -- |
| `define` | `string list` | `/define:`, joined with `;` | `[]` |
| `unsafe` | `bool` | `/unsafe` | `false` |
| `cscpath` | `string` | compiler executable, bypassing framework discovery entirely | `None` |
| `toolset` | `string` (package version) | compiler from `Microsoft.Net.Compilers.Toolset/<version>` in the NuGet cache instead of the SDK's; see below | `None` |
| `fromlock` | `Lock.Project` | switches to `fromlock` mode, see below | `None` |
| `args` | `string list` | raw extra switches, appended last (`CommandArgs`) | `[]` |
| `nofailonerror` | (none) | do not fail the build on a compile error | `FailOnError = true` |

`out`, `platform`, `unsafe`, `nostdlib` (from `targetfwk`) and `define` are only meaningful in
composed mode -- `fromlock` mode ignores them (see below). The argument list `resolve` builds is,
in order: `/noconfig` (when the target framework requires it), `/nologo`, `/target:`,
`/platform:`, `/unsafe`, `/nostdlib+`, `/out:`, `/define:`, sources, `/r:` refs, global refs,
`/res:`, then `CommandArgs`.

## Compiler sources

### Composed mode: three sources, first match wins

| Setting | Compiler | Notes |
|---|---|---|
| `cscpath "<exe>"` | that executable | no framework or package lookup at all |
| `toolset "<version>"` | `csc.dll` from `microsoft.net.compilers.toolset/<version>/tasks/netcore/bincore` in the NuGet cache | the package is restored into the cache if missing (same mechanism as the reference-assembly packages); still missing afterwards fails the build |
| neither | whatever `targetfwk` / `NETFX-TARGET` resolves through `DotNetFwk.locateFramework` | the SDK's `csc.dll`, or `csc.exe` / `mcs` on a framework that ships one |

- `toolset` replaces only the compiler. References, defines and environment variables still
  come from the targeted framework.
- The lock records what ran in `Lock.Project.Compiler`: `Path` and `Sha256` name the compiler
  file; `Sdk` is always the framework's version from `DotNetFwk.locateFramework`, not the
  toolset package's -- that version is part of `Path`.

### Import: the project decides, msbuild answers

`Project.import` does not choose a source. `parseImport` reads which compiler msbuild wired up:

- The `Microsoft.Net.Compilers.Toolset` package does **not** set `CscToolPath`/`CscToolExe`.
  It redirects `CSharpCoreTargetsPath` (and the `Csc` task assembly) to its own `tasks/netcore/`
  directory; the task's tool path then defaults to the `bincore` next to that targets file.
- So the compiler is `<dir of CSharpCoreTargetsPath>/bincore/csc.dll`: the SDK's
  `Roslyn/bincore/csc.dll` for an unpinned project, the package's for a pinned one.
  `RoslynTargetsPath` always reports the SDK's Roslyn and is only the fallback.
- **A project referencing the package also needs `<RoslynCompilerType>Toolset</RoslynCompilerType>`**
  (csproj or `-p:`). Without it, on SDK 9 and later, `Microsoft.NET.Sdk.BeforeCommon.targets`
  silently sets `CSharpCoreTargetsPath` back to the SDK's, and the package has no effect.

## `fromlock` mode

```fsharp
do! csc { fromlock mapped }   // mapped : Lock.Project
```

`fromlock` sets `CscSettingsType.FromLock`; when it is `Some project`, `Csc` skips `resolve`
entirely and calls the runner on `project` with no extra env vars and no temp files. Every other
setting on the record (`Src`, `Ref`, `Target`, `Platform`, `Out`, `TargetFramework`, ...) is
ignored -- the project's own `Args` is the whole compilation. Only `FailOnError` and `CscPath`
still apply, because they govern how the runner behaves, not what it compiles.

The runner (`run` in `Dotnet.csc.fs`, shared by both modes) does, in order:

1. Writes back every `Generated` file that is missing or whose content differs from what is on
   disk -- the resolved project is the source of truth for msbuild-generated inputs like
   `AssemblyInfo.cs`. The composed mode never populates `Generated`, so this is a no-op there.
2. Creates the output directories, for every path `CscArgs.outputs project.Args` names.
3. Verifies the SHA-256 of every hashed reference, analyzer, and the compiler itself against what
   is on disk. An empty recorded hash means "not checked" (the composed mode never records one,
   and neither does an unbuilt project reference). Any mismatch is collected and reported
   together, then fails the build when `FailOnError` is set (`XakeException`, message containing
   the path).
4. `needFiles` on `CscArgs.inputs project.Args` -- every file any input switch names, plus the
   sources. For the composed mode this now covers everything the args name, including the
   framework's global references, not only sources/refs/resources.
5. Writes the arguments to a response file, with `Impl.escapeArgument`, and runs the compiler.
   `/noconfig` cannot go inside the rsp -- csc warns `CS2023` and ignores it there -- so it stays
   on the command line and everything else goes into `@<rspfile>`.
6. Picks the compiler: `settings.CscPath` wins if set; otherwise, when the project's recorded
   compiler path ends in `.dll`, it runs through `dotnet <path>`; otherwise the path is run
   directly (a native launcher, e.g. the SDK's `csc` apphost).

The rsp file and any extra temp files (the composed mode's resx-compiled resources) are deleted
once the compiler exits, success or failure.

## Where a `Lock.Project` comes from

`Project.import` (`src/dotnet/Project.fs`) runs an msbuild design-time build per project --
`ProvideCommandLineArgs`/`SkipCompilerExecution`, so the compiler reports its command line
instead of running -- and writes one lock file per (framework, variant). `ImportOptions`:

| Field | Meaning |
|---|---|
| `Projects` | project files; all land in one lock |
| `Framework` | target framework the import runs for |
| `Configuration` | msbuild `Configuration`, default `Release` |
| `Properties` | extra `-p:` properties, e.g. `["Brand", "MESCIUS"]` |
| `Variant` | names the `obj/xake/<framework>/<variant>/` subtree; keeps distinct property sets from overwriting each other's generated files |
| `Output` | the lock file to write |

`Lock.Project`, one entry per project:

- `Name` -- `AssemblyName`
- `Project` -- the project file path
- `Directory` -- the compiler's working directory
- `Compiler` -- `{ Tool; Path; Sha256; Sdk }`
- `Args` -- the verbatim command line, paths absolute
- `References`, `Analyzers` -- `{ Path; Sha256 }` lists
- `ProjectRefs` -- `ProjectReference` items, as project files
- `Imports` -- the msbuild files (outside the SDK) whose evaluation produced this entry, hashed
- `Generated` -- msbuild-written compiler inputs, by path, with content
- `Properties` -- a small whitelist (`AssemblyName`, `TargetFrameworkMoniker`, ...)
- `Sources`/`Output` -- computed from `Args` via `CscArgs`, not stored twice

`Lock.read path` parses a lock file (paths expanded for this machine); `Lock.project name lock`
looks an entry up by assembly name or project file name.

The project-reference pattern, from `import.fsx`:

```fsharp
let unbuilt = project.References |> List.filter (fun r -> r.Sha256 = "") |> List.map (fun r -> r.Path) |> Set.ofList
let mapped = project |> Lock.mapPaths (fun p -> if unbuilt.Contains p then outputOf p else p)

do! need (unbuilt |> Set.toList |> List.map (outputOf >> relative))
do! csc { fromlock mapped }
```

A project reference in the lock is unhashed and points at the referenced project's own build
output (not built yet at import time). `Lock.mapPaths f project` rewrites every path the
project's `Args`, `References`, `Analyzers` and `Generated` keys carry through `f`; a rewritten
`Hashed` entry loses its hash, since the recorded hash was computed for the old path. The script
maps those references to the path its own rule will produce them at, `need`s those targets
first, and only then runs `csc { fromlock mapped }` -- the runner itself does not `need` the
mapped outputs, it only `needFiles` what the (already-mapped) args name.

## Behaviour notes

- The one-resolved-form refactor changed composed mode in two ways: it now creates the output
  directory before compiling (previously a sample needed the directory to pre-exist), and it
  `needFiles` everything the args name, including framework global references, not only the
  sources/refs/resources it used to `needFiles` directly.
- `/noconfig` has to stay on the command line, never in the rsp: inside the rsp csc emits
  `CS2023` and ignores it.
- A hash mismatch reports every mismatching path at once, `"<path>: expected <hash>, got
  <hash-or-\"missing\">"`, one line per path, and fails when `FailOnError` is set.
- A `Lock.Project` in memory has absolute paths throughout; `Lock.write` tokenizes them against
  known roots (project root, NuGet package cache, SDK) so the file on disk is portable and
  diffable, and `Lock.read`/`parse` expands them back on load.

## Not yet

- `resgen` for an imported project's `.resx` files -- not exercised, dataengine has none. See
  `tracker.md`.
- Running `fsc` through the same `Lock.Project`/runner pair -- today only `csc` does. See
  `tracker.md`.
