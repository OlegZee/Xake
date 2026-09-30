# Xake.Hermetic.Dotnet: architecture

This document is for maintainers and reviewers of the pull requests that introduce the
`Xake.Hermetic.Dotnet` package. It explains what the package is for, how it is shaped, where it
meets the `Xake` package, and what is and is not proven. The user-facing reference lives in
[hermetic/README.md](hermetic/README.md) and the pages next to it; the task-oriented guide is
[hermetic-guide.md](hermetic-guide.md). The base task `csc {}` and its runner are described in
[tasks.md](tasks.md), [csc-syntax.md](csc-syntax.md) and [csc-server.md](csc-server.md).

Every name below was checked against the source on this branch (`src/hermetic/*.fs`,
`src/dotnet/Csc.fs`, `src/dotnet/Dotnet.csc.fs`, `src/dotnet/DotNetFwk.fs`). The working notes
(the product brief, the extraction plan, the session log, the experiments) stay on the feature
branch and are not part of this documentation. This document summarizes the decisions taken
there; it does not reopen them.

## 1. The problem, and what "hermetic" means here

A stock .NET build is msbuild plus NuGet. Restore talks to the network. The package cache is a
mutable, machine-wide directory. `obj/` carries hidden incremental state. An SBOM, when there is
one, is produced afterwards by a separate tool that reads `project.assets.json`: it describes
what restore resolved, not what the compiler was handed. `RestorePackagesWithLockFile` locks
package versions, not compiler inputs. `/deterministic` gives identical bytes only if the inputs
are identical, and nothing in the stock toolchain proves that they were.

Xake already knows, per compiler invocation, which files went in: every input is a tracked
dependency of the target. The package builds on that. Its central object is the **lock**: a JSON
file, kept in git, that records per project and target framework exactly what the compiler is
handed (the command line, the generated inputs, the resx pairs) and the SHA-256 of everything it
reads from outside the repository (the compiler, every reference, every analyzer, the msbuild
files that produced the answer) plus the NuGet restore graph. A build then compiles *from* the
lock and fails when anything differs. From the same lock the package derives a deterministic
CycloneDX SBOM, and around it sit a deterministic packer, a strong-name re-signer, a PE
comparator and a signing rule.

### What is controlled

| Input | How it is controlled |
|---|---|
| The compiler command line | recorded verbatim (structured, with section markers); the import checks that the recorded form rebuilds msbuild's exact list |
| References, analyzers, the compiler binary | path and SHA-256 in the lock; `Csc.run` refuses to compile when a recorded hash does not match the file on disk |
| Generated compiler inputs (assembly attributes, the derived `.editorconfig`, `sourcelink.json`) | content stored in the lock and written back before compiling |
| `.resx` resources | `(resx, .resources)` pairs; the `.resources` file is regenerated from the resx when missing |
| The msbuild evaluation's own inputs | the project file and the imports outside the SDK, hashed (`Evaluation.Imports`); a change re-runs the import |
| The package graph | ids, versions, nupkg SHA-512, direct flags, edges (`Entry.Packages`) |
| Packages missing on this machine | fetched by exact id and version, the nupkg SHA-512 compared with the lock |
| The commit | kept out of the lock as the token `$(SourceRevisionId)`, resolved from `.git` at compile time |
| The SDK choice | recorded (`Evaluation.Sdk`, `Evaluation.SdkPin`); the import warns when `global.json` does not pin it exactly |

### What is not controlled

The brief calls the result "mostly hermetic", and that is the honest word.

- **msbuild is still the evaluator.** `Project.import` asks msbuild what it would run. The lock
  records the result of the evaluation, not the evaluator. Replaying a lock starts no msbuild,
  but producing one does.
- **The network.** Restore is allowed by default (`Restore.Options.Enabled = true`). A build can
  turn it off, and then a missing package fails the build before the compiler runs. There is no
  offline or vendored package source mode.
- **The package cache is trusted by path and checked by hash.** The per-file SHA-256 check is the
  real gate. The package-level check compares the lock's SHA-512 with the `contentHash` NuGet
  wrote into `.nupkg.metadata`; it does not re-hash the `.nupkg`.
- **The SDK is recorded, not enforced.** A lock names the compiler under
  `$(DotnetRoot)/sdk/<version>/`; a machine without that SDK fails with a message naming it.
- **Byte identity across machines** also needs the same checkout path (csc embeds absolute
  source paths in the PDB and, under `/deterministic`, in the PE) or a `/pathmap`.
- **Only C# goes through the lock.** `fsc` has no lock runner yet (section 8).
- **Obfuscation, installers, policy gates, provenance statements** are not built. The brief's
  capability ladder (compile lock, dependency map, SBOM, policy, attestation, offline mode) is
  implemented up to the SBOM rung.

## 2. The model

### Three objects

| Object | Where | What it is |
|---|---|---|
| `CscSettingsType` | `Xake.Dotnet` (`Csc.fs`, `CscTypes`) | **intent**: what the script asks for (filesets, target, framework, defines, compiler source, server). The 3.3 settings record, extended with `Toolset` and `Server` |
| `Csc` | `Xake.Dotnet` (`Csc.fs`) | **the resolved compilation**: exactly what the compiler is handed. Filesets expanded, paths absolute, the compiler chosen, hashes empty or recorded |
| `Lock.Entry = { Csc; Evaluation; Packages }` | `Xake.Hermetic.Dotnet` (`Lock.fs`) | a `Csc` **plus provenance**: where the answer came from (msbuild evaluation) and the restore graph. `Lock.Document = { Configuration; Properties; Entries }` is one lock file |

A `Csc` has three producers:

1. `Csc.ofSettings settings` (what `csc { ...; resolve }` returns) composes one from settings.
2. `Project.import` builds one per project and framework from an msbuild design-time build, and
   wraps it in a `Lock.Entry` with a filled `Evaluation` and `Packages`.
3. `Lock.load` / `Lock.parse` read one back from a lock file.

`Csc.ofArgs` is the shared factoring step: producers 1 and 2 both build a flat argument list and
pass it through `ofArgs`, then check that `c.Args` gives the same list back.

### One runner

`Csc.run : RunOptions -> Csc -> Recipe<ExecContext, unit>` is the only function in either package
that starts the C# compiler. Every path ends there:

| Entry point | Package | Path to the runner |
|---|---|---|
| `csc { ... }` | base | `Csc.compile` = `Csc.ofSettings`, `Csc.runOptions`, `Csc.run` |
| `csc { ...; resolve }` then `Csc.run` | base | direct |
| `csc { ...; lock "path" }` | hermetic | `Csc.ofSettings`, `Csc.runOptions`, `Lock.buildWith`, `Lock.compileWith`, `Csc.run` |
| `Lock.build path c` | hermetic | `Lock.buildWith`, `Lock.compileWith`, `Csc.run` |
| `Lock.compile entry` | hermetic | `Lock.compileWith`, `Csc.run` |

`RunOptions = { FailOnError; CscPath; Server; Environment }` carries how the runner behaves, not
what it compiles. It was split from `CscSettingsType` so that running a resolved compilation
never goes through a settings record whose other fields would be silently ignored.

### The seam: no hooks

The base package knows nothing about locks. The hermetic package calls the base, never the
other way round. Before the split, the runner contained exactly three hermetic steps, and all
three ran before the compiler was invoked: the package restore, the `$(SourceRevisionId)`
substitution, and the SDK- or package-specific "compiler missing" explanation. So they moved
out of the runner into `Lock.compileWith`, which runs them and then hands the `Csc` to
`Csc.run`:

```
Lock.buildWith options path c
  lock missing   -> record (rehash, save) -> compileWith recorded
  lock matches   -> compileWith recorded            (the recorded hashes gate the build)
  lock differs   -> fail with Lock.diff              (FailOnError = false: warn, compileWith resolved)

Lock.compileWith options entry
  1. Restore.ensure options.Restore (Lock.restoreRequest [entry])
  2. ensureCompilerAvailable                       (explain a compiler still missing)
  3. $(SourceRevisionId) -> Git.headSha c.Directory (only when the token occurs)
  4. Csc.run options.Run entry.Csc
```

The hash check stays in the base runner: it is what makes a replayed `Csc` trustworthy with or
without a lock. The restore runs before it, which is the order the runner had before the split.

The alternatives considered in the extraction plan, and why they lost:

| Mechanism | Verdict |
|---|---|
| Record + `resolve` + `Csc.run`, no hooks | adopted |
| Record + runner + `Prepare` hooks | superseded: hooks only existed so the base could call hermetic steps; once the script holds the object, the hermetic side calls the base |
| The whole `Lock.Entry` in the base | rejected: the base would carry provenance fields it never fills |
| An `ICompilationSource` interface | rejected: an interface for one method |
| A runner of the hermetic package's own | rejected: breaks "one runner" |

The base `CscSettingsType` has no `Lock` field and no `Prepare` field. `RunOptions` has no
`Restore` field; the package folder travels in `Lock.Options = { Run: RunOptions; Restore:
Restore.Options }`.

### `resolve` and the overloaded `Run`

`csc {}` returns a recipe that compiles, as in 3.3. The custom operation `resolve` changes the
builder state to a marker type, and `Run` is overloaded on it:

```fsharp
type CscRequest = CscRequest of CscSettingsType          // "resolve, do not run"
[<CustomOperation("resolve")>] member __.Resolve(s: CscSettingsType) = CscRequest s
member __.Run(s: CscSettingsType) = Csc.compile s         // Recipe<ExecContext, unit>
member __.Run(CscRequest s) = Csc.ofSettings s            // Recipe<ExecContext, Csc>
```

`resolve` must be the last operation: an operation after it is typed on `CscSettingsType` and
does not compile. A flag inside the settings would not have worked: `Run` would then always
return `Recipe<Csc>`, and `do! csc { ... }` from 3.3 would stop type-checking.

`csc { ...; lock "path" }` uses the same trick from the other assembly. `Lock.fs` defines the
marker `CscLocked of path: string * CscSettingsType` and, in the auto-opened module
`CscLockBuilder`, a type extension of `CscSettingsBuilder` with the `lock` operation and a third
`Run` overload. The spike showed this works on F# 8 across assemblies; without
`open Xake.Hermetic.Dotnet` the operation is unknown (FS0708).

### `Csc.x` and the function `Csc`

The 3.3 API had a function `Csc settings`. F# lets a type and a let-bound value share a name,
but the spike showed that in expression position `Csc.x` always binds to the value, never to the
type or a module of that name (FS0039, in both definition orders and with type extensions). So a
record named `Csc` could not expose `Csc.run` while the function existed. The user chose to
retire the function: the record is `Csc`, the module is `[<CompilationRepresentation(ModuleSuffix)>]
module Csc`, and `do! Csc settings` became `do! Csc.compile settings`. That is the one breaking
change of `Xake` 3.4 relative to 3.3.

The name is compiler-specific on purpose. When `fsc` goes through the runner it gets its own
`Fsc` record and the shared runner core becomes private, rather than one abstract record with a
tool dispatch.

### The compiler server switch

`CompilerServer = Shared of keepAlive: int option | InProcess` lives in the base, because it is
a parameter of the run and never enters a compilation's argument list (section 6).
`CscSettingsType.Server` is `CompilerServer option`; `None` inherits. `CompilerServer.resolve`
turns the option into a value with this precedence: the target's `noserver`/`keepalive`, the
script variable `CSC_SERVER`, the environment variable `XAKE_CSC_SERVER`, `Shared None`.

### Compiler selection

`Csc.ofSettings` picks the compiler it records in `Dependencies.Compiler` with this precedence:
`CscSettingsType.Toolset` (the block's `toolset`), the script variable `CSC_TOOLSET` (read with
`getVar` only when `Toolset` is `None`, so it is a tracked dependency; empty counts as unset),
then the SDK's `Roslyn/bincore/csc.dll`. Both toolset sources restore
`Microsoft.Net.Compilers.Toolset/<version>` into the machine's cache. `CscPath` is a run option
and overrides the recorded path at run time. The SDK is the one the `dotnet` host selects in the
project root: `DotNetFwk.locateFrameworkIn options.ProjectRoot` runs `dotnet --version` there, so
`global.json` (and its `rollForward`) applies, and takes `<dotnetRoot>/sdk/<version>`; with no
`global.json` that is the newest SDK, as before. A pin the host cannot satisfy falls back to the
newest SDK, and `ofSettings` traces the probe's warning (`DotNetFwk.sdkProbeWarning`). The probe
is cached per project root for the process; `DotNetFwk.locateFramework` (no build context)
probes from the current directory.

### Dependency direction

```
Xake.Hermetic.Dotnet  ->  Xake.Dotnet  ->  Xake (engine)
                          (package "Xake" = Xake.Dotnet.dll + Xake.dll)
```

`Xake.Dotnet` names no hermetic type. The engine (`src/core`) was changed on this branch only
for bug fixes (one target executed twice per run, a path literal prefix, a database open
retry); it has no new dependency or rule kind and knows nothing of locks. The hermetic package
uses only public names of the base: `Tool.diagnosticLevel`, `Tool.failOnExitCode`,
`Hash.sha256`, `DotNetFwk.nugetRoot`, `dotnetRoot`, `normalizedPackageRoot`, `downloadPackages`,
`restoreProjectText`, the `CscArgs` helpers, and the `Csc` module. There is no
`InternalsVisibleTo` between the packages; that is what makes independent versions possible
(section 9).

## 3. Module map

### Compile order

| # | Base (`src/dotnet`, `Xake.Dotnet`) | # | Hermetic (`src/hermetic`, `Xake.Hermetic.Dotnet`) |
|---|---|---|---|
| 1 | `Tool.fs` | 1 | `Json.fs` (internal) |
| 2 | `Hash.fs` | 2 | `Roots.fs` |
| 3 | `DotNetFwk.fs` | 3 | `Nuget.fs` |
| 4 | `ResourceFileset.fs` | 4 | `Restore.fs` (also the namespace-level `Package` type) |
| 5 | `Resx.fs` | 5 | `Fsproj.fs` |
| 6 | `DotnetTasks.fs` | 6 | `Git.fs` |
| 7 | `CscArgs.fs` | 7 | `Lock.fs` (also `CscLocked`, `CscLockBuilder`) |
| 8 | `Csc.fs` | 8 | `Project.fs` |
| 9 | `Dotnet.csc.fs` | 9 | `Sbom.fs` |
| 10 | `Dotnet.fsc.fs`, `Dotnet.resgen.fs`, `Dotnet.Msbuild.fs` | 10 | `Verify.fs` |
| | | 11 | `StrongName.fs` |
| | | 12 | `Pack.fs` |
| | | 13 | `Sign.fs` (also `SignState`, `SignBuilder`) |

Within the hermetic package the order follows the dependencies. `Restore` names no lock type,
so it compiles before `Lock` and takes a `Restore.Request`, not lock entries. `Git` compiles
before `Lock` (the lock needs `Git.headSha`), which is why `Git.tokenize` works on text and a
whole entry is tokenized with `entry |> Lock.mapText (Git.tokenize sha)`. `Verify` does not
depend on `Sbom`; the SBOM checker lives in `Sbom`. `Sign` uses `Verify`, `StrongName` and
`Pack`, so it compiles last.

Who uses whom inside the hermetic package (names used in code, not comments):

| Module | Uses |
|---|---|
| `Roots` | `DotNetFwk` |
| `Nuget` | `Json` |
| `Restore` | `Nuget`, `Roots`, `DotNetFwk` |
| `Fsproj` | `Json`, `Roots`, `Tool` |
| `Git` | nothing |
| `Lock` | `Csc`, `CompilerServer`, `RunOptions`, `Nuget`, `Restore`, `Roots`, `Git`, `Json` |
| `Project` | `Lock`, `Csc`, `CscArgs`, `Nuget`, `Roots`, `Git`, `Json`, `Tool` |
| `Sbom` | `Lock`, `Nuget`, `Csc`, `Json` |
| `Verify` | nothing |
| `StrongName` | `Verify` (the internal PE `Layout`) |
| `Pack` | nothing |
| `Sign` | `Verify`, `StrongName`, `Pack`, `Hash`, `Json` |

### Base pieces the hermetic package relies on

`Csc.fs`, module `CscTypes` (auto-opened):

| Name | Kind | What it does |
|---|---|---|
| `CompilerServer` | union | `Shared of keepAlive: int option` or `InProcess` |
| `CompilerServer.fromEnvironment ()` | function | `InProcess` when `XAKE_CSC_SERVER` is `0`, `false`, `off` or `no`; else `Shared None` |
| `CompilerServer.resolve setting` | recipe | the precedence above; reads `CSC_SERVER` with `getVar`, so it is a tracked dependency |
| `CscSettingsType`, `CscSettings` | record, value | the settings, with `Toolset: string option` and `Server: CompilerServer option` |
| `RunOptions` / `RunOptions.Default` | record | `FailOnError`, `CscPath`, `Server`, `Environment` |

`Csc.fs`, the types and `module Csc`:

| Name | Kind | What it does |
|---|---|---|
| `Hashed`, `Reference`, `Compiler`, `Dependencies` | records | a hashed file; a `/reference:` with its `Alias`; `{ Tool; Path; Sha256; Version }`; compiler, references, analyzers |
| `Csc` | record | `Name`, `Framework`, `Directory`, `Options`, `Defines`, `Sources`, `Generated`, `Resources`, `Dependencies`; members `Args` (the flat command line) and `Output` (the `/out:` value) |
| `Csc.isMarker` | pure | whether an `Options` element is one of `@Sources`, `@References`, `@Analyzers`, `@Defines` |
| `Csc.sha256` | reads disk | `Hash.sha256`, but `""` for a file that does not exist: the lock's "not hashed" value |
| `Csc.hashed` | reads disk | `{ Path; Sha256 = Csc.sha256 path }` |
| `Csc.compilerVersion` | reads disk | `ProductVersion` cut at `+` or space; for a native launcher, the `.dll` next to it |
| `Csc.managedCompiler` | reads disk | the SDK's `csc`/`csc.exe` launcher replaced by the `csc.dll` next to it; any other path unchanged |
| `Csc.ofArgs` | pure | factors a flat command line into the structured form (section 4) |
| `Csc.args` | pure | `c.Args` |
| `Csc.rehash` | reads disk | fills every empty reference, analyzer and compiler hash (and an empty `Version`) |
| `Csc.mapPaths f` | pure | rewrites every path; a rewritten hashed item loses its hash |
| `Csc.mapText f` | pure | rewrites `Generated` content, `Options`, `Defines` |
| `Csc.diffList` | pure | ordered LCS diff, `- x` / `+ x` lines |
| `Csc.runOptions settings` | recipe | `RunOptions` for composed settings: `FailOnError`, `CscPath`, resolved server, the framework's env vars |
| `Csc.ofSettings settings` | recipe | composes a `Csc`; fails without `targetfwk` or `NETFX-TARGET`; the compiler from `Toolset`, else `CSC_TOOLSET`, else the SDK `global.json` selects; never hashes |
| `Csc.run options c` | recipe | the runner (below) |
| `Csc.compile settings` | recipe | `ofSettings`, `runOptions`, `run`; replaces 3.3's `Csc settings` |

What `Csc.run` does, in order: trace `compiling '<name>'`; fail when the compiler file is missing
(unless `CscPath` is set); `needFiles` the compiler; write back every `Generated` file that is
missing or differs; create the output directories; `needFiles` the resx files and regenerate
each missing `.resources` (`Resx.compile`); report, with its expected hash, every hashed file
that is missing and that no rule of the script produces (so all of them are listed at once rather
than the first `needFiles` failure); `needFiles` every input the arguments name
(`CscArgs.inputs`), which (re)builds a reference another rule produces; then check every
non-empty hash (references, analyzers, compiler) and fail listing every mismatch, so the check
sees the files the compiler is about to read; write the response file (`/noconfig` stays on the command line, csc ignores
it inside an rsp); add the server switches; run the compiler with `workdir c.Directory`; fail on a
non-zero exit code when `FailOnError`.

`Dotnet.csc.fs`, module `CscBuilder` (auto-opened): the `csc` builder with every 3.3 operation
plus `toolset`, `noserver`, `keepalive` and `resolve`, the marker `CscRequest`, and the two
`Run` overloads.

`DotNetFwk.fs` (public names the hermetic package uses):

| Name | What it does |
|---|---|
| `nugetRoot ()` | `NUGET_PACKAGES`, else `~/.nuget/packages` |
| `dotnetRoot ()` | the SDK installation root, when one can be located |
| `normalizedPackageRoot root` | the package folder in effect (`None` = the machine's cache), forward slashes, no trailing slash |
| `restoreProjectText packages` | the synthesized restore project: `netstandard2.0`, `DisableImplicitFrameworkReferences`, one `PackageDownload` per package at an exact `[version]` |
| `downloadPackages root packages` | one `dotnet restore` of that project under `obj/xake/restore/<n>/` of the project root (so `nuget.config` is found), with the repository's `Directory.Build.*` and central package management switched off, `NUGET_PACKAGES` pointed at the folder |
| `restorePackage root id version` | the package directory, downloading it first when absent; `csc { toolset }` and `CSC_TOOLSET` use it |
| `locateFrameworkIn root fwk` | the toolchain for a framework, the SDK being the one `dotnet --version` reports in `root` (`global.json`); memoized per (root, framework) |
| `locateFramework fwk` | `locateFrameworkIn` with the current directory as the root |
| `sdkProbeWarning root` | the probe's warning for `root` (a `global.json` pin it could not honour), once that root has been probed |

### Hermetic modules

`Json` (internal): a small JSON reader and `escape`, written so that the package has no
`System.Text.Json` dependency on netstandard2.0.

`Roots`:

| Name | Kind | What it does |
|---|---|---|
| `nugetRoot ()`, `dotnetRoot ()` | forwarders | `DotNetFwk.nugetRoot`, `DotNetFwk.dotnetRoot` |
| `nugetPackageRootToken` | value | `"$(NuGetPackageRoot)"` |
| `builtinTokens` | value | `$(NuGetPackageRoot)`, `$(ProjectRoot)`, `$(DotnetRoot)` |
| `packageRootOverride dir` | pure | `[ "$(NuGetPackageRoot)", dir ]`, the extra-root list that points the package token at a folder of the build's own |
| `builtin projectRoot` | pure | the three built-in roots, longest root first |
| `make projectRoot extra` | pure | built-in plus extra roots; validates `$(Name)`, resolves relative paths against the project root, an extra root replaces a built-in of the same token |
| `current`, `currentWith extra` | recipes | the same, with the project root from `ExecOptions.ProjectRoot` |

`Nuget`:

| Name | Kind | What it does |
|---|---|---|
| `Package`, `Assets`, `NuspecDependency`, `NuspecGroup`, `Nuspec` | records | cache metadata of a package; one framework's restore graph; nuspec contents |
| `readAssets assetsFile framework` | reads disk | packages, edges and direct references of one target of `project.assets.json`; tries the alias and the full framework name; fails when the target is absent |
| `readCache cacheRoot id version` | reads disk | SHA-512 and feed from `.nupkg.metadata`, authors, license, repository and commit from the nuspec; never throws |
| `parseNuspec xml` | pure | namespace-agnostic nuspec parse |
| `nuspecFrameworkMatches`, `nuspecDependenciesFor` | pure | the dependency group for a framework (exact match, else the ungrouped group; never nearest-compatible) |
| `ships cacheRoot id version` | reads disk | whether the package has files under `lib/` or `runtimes/` |
| `packageOf cacheRoot path` | pure | `(id, version)` of a file under the cache, from the first two path segments |

`Restore` (plus the namespace-level `Package = { Id; Version; Sha512; Direct; DependsOn }`):

| Name | Kind | What it does |
|---|---|---|
| `Options` / `Options.Default` | record | `PackageRoot: string option` (`None` = machine cache), `Enabled: bool` (default `true`) |
| `packageRoot options` | pure | the folder in effect |
| `into dir` | recipe | options with the folder relative to the project root |
| `Request`, `Missing` | records | what must exist (graph + paths); one missing package with its files |
| `missing options request` | reads disk | missing packages, grouped; one `File.Exists` per path, nothing else |
| `verify options missing` | reads disk | absent package directories and nupkg SHA-512 mismatches |
| `download options packages` | recipe | `DotNetFwk.downloadPackages`, ignoring `Enabled` |
| `ensure options request` | recipe | restore what is missing once per process, serialized per folder by a `Resource`; returns the problems |

`Fsproj` (the kept F# evaluation, to be retired into `Project.import` once `fsc` goes through
the runner):

| Name | Kind | What it does |
|---|---|---|
| `ProjectInfo`, `EvalOptions` | records | the evaluation result; what to evaluate |
| `evaluate options` | recipe | `msbuild -t:Restore` as its own call, then the item query without `-restore`; writes a tokenized JSON file |
| `parse roots file`, `load file` | pure / recipe | read the file back |

`Git`:

| Name | Kind | What it does |
|---|---|---|
| `headFiles dir` | reads disk | `HEAD` and the loose ref or `packed-refs` it resolves through; linked worktrees supported |
| `headSha dir` | reads disk | the current commit, no `git` executable |
| `revisionToken` | value | `"$(SourceRevisionId)"` |
| `tokenize sha text` | pure | replaces the sha with the token; a no-op for an empty sha |

`Lock`:

| Name | Kind | What it does |
|---|---|---|
| `Hashed`, `Reference`, `Compiler`, `Package` | abbreviations | the base and `Restore` types, kept reachable as `Lock.*` |
| `SdkPin`, `sdkPinText`, `parseSdkPin` | union, pure | `NoGlobalJson`, `Pinned`, `RollsForward`, `NoVersion`; the text form written in the lock |
| `Evaluation` / `Evaluation.Empty` | record | `Project`, `ProjectRefs`, `Imports`, `Sdk`, `SdkPin`, `Properties`; all empty when composed |
| `Entry`, `Document` | records | `{ Csc; Evaluation; Packages }`; `{ Configuration; Properties; Entries }` |
| `packagesOf cacheRoot assets` | reads disk | the entry's package graph from `Nuget.Assets` and the cache's SHA-512 |
| `ofCsc c` | pure | a composed `Csc` as an entry |
| `rehash entry` | reads disk | `Csc.rehash` plus the `Imports` hashes |
| `mapPaths`, `mapText` | pure | `Csc.mapPaths`; `Csc.mapText` plus the evaluation's property values |
| `diffList`, `diff a b` | pure | the ordered diff; the full entry diff (section 4) |
| `format roots doc`, `parse roots text`, `read roots path` | pure | the file format |
| `load path`, `loadWith extra path` | recipes | read against the build's roots; `needFiles` the lock itself |
| `save path doc`, `saveWith extra path doc` | recipes | write against the build's roots; deliberately no `need` |
| `entry name doc`, `entryFor framework name doc` | pure | lookup by assembly or project name; `entry` fails when the name matches several frameworks |
| `Options` / `Options.Default` | record | `{ Run: RunOptions; Restore: Restore.Options }` |
| `restoreRequest entries` | pure | every compiler, reference and analyzer path, plus the combined graph |
| `restore options doc` | recipe | `Restore.ensure` over a whole lock; fails on a problem |
| `compileWith options entry`, `compile entry` | recipes | the replay (section 2); `compile` resolves the server first |
| `record path c` | recipe | `needFiles` what it hashes, rehash, overwrite a one-entry lock; compiles nothing |
| `verify path c` | recipe | `diff (entry c.Name doc) (ofCsc c)`; writes and compiles nothing |
| `buildWith options path c`, `build path c` | recipes | the strict gate (section 2) |
| `lock "path"` | builder operation | `CscLockBuilder`: `Csc.ofSettings`, `Csc.runOptions`, `Lock.buildWith` |

`Project`:

| Name | Kind | What it does |
|---|---|---|
| `sdkPin projectDir` | reads disk | the `global.json` pin, searched upwards; only `rollForward: "disable"` is `Pinned` |
| `ImportOptions` / `ImportOptions.Default` | record | `Projects`, `Frameworks`, `Configuration` (default `Release`), `Properties`, `Variant`, `Output`, `Roots` |
| `import options` | recipe | per project: one restore without `TargetFramework` and with `RestoreRecursive=false`, then per framework a design-time build and a `-pp` preprocess; writes one lock |

Internals of `Project` that tests use: `withProjectLock` (one `Resource` per project path, so two
variants cannot restore over each other's `obj/project.assets.json`), `frameworksToImport` (only
the frameworks the project declares), `parseImports`, `parseImport`.

`Sbom`:

| Name | Kind | What it does |
|---|---|---|
| `Hash`, `Property`, `Component`, `Composition`, `Annotation`, `Bom` | records | the CycloneDX model |
| `cycloneDx bom` | pure | CycloneDX 1.6 JSON, deterministic |
| `forAssembly cacheRoot entry assemblyPath` | reads disk | restore-scope BOM of one assembly |
| `forPackage nupkgPath assemblies` | reads disk | restore-scope BOM of one nupkg, the union of its assemblies' BOMs |
| `packageSbomPath framework` | pure | `sbom/<tfm>/bom.cdx.json` |
| `PackageScope.*` | pure | the predicates the package-scope default is built from |
| `PackageScopeOptions` / `.Default` | record | the knobs of the package-scope rule |
| `shippedPaths`, `pathProperty` | pure, value | tier-1 inventory; `"xake:nuget:path"` |
| `forPackageScoped`, `forPackageScopedWith options` | reads disk | package-scope BOM of one nupkg for one framework |
| `checkPackageScope options nupkg framework bom` | reads disk | acceptance checks 3.1 to 3.4; `[]` is a pass |

`Verify`: `authenticodeHash path`, `compare a b` (labelled `Difference` list), `verdict diffs`;
the PE `Layout` and `layout` are internal.

`StrongName`: `checksum`, `stamp`, `readSnk`, `sign`, `verify`, `signFile`, `normalise`.

`Pack`: `Entry`, `Options` / `Options.Default`, `zip`, `nupkg`, `list`.

`Sign`: `Algorithm`, `Certificate`, `Kind`, `Request`, `Signer`, `Store`, `Settings` /
`Settings.Default`, `kindOf`, `imageHash`, `identity`, `request`, `SignatureEntry`, `isSigned`,
`verifySameImage`, `fakeSigner`, `directoryStore`, `rule`, `executor`; the builder `sign {}`
(`SignBuilder`, auto-opened).

## 4. The lock file format

### Shape

One document holds every project of one variant for every framework it was imported for. The
framework is a property of the entry: `(Csc.Name, Csc.Framework)` identifies an entry, and
`Lock.entryFor framework name` is the unambiguous lookup.

```json
{
  "Configuration": "Release",
  "Properties": { "Brand": "X" },
  "Entries": [
    {
      "Name": "Sample",
      "Framework": "netstandard2.0",
      "Evaluation":   { "Project", "ProjectRefs", "Imports", "Sdk", "SdkPin", "Properties" },
      "Compilation":  { "Directory", "Options", "Defines", "Sources", "Generated", "Resources" },
      "Dependencies": { "Compiler", "References", "Analyzers", "Packages" }
    }
  ]
}
```

In memory `Compilation` is flattened into the `Csc` record, `Dependencies` is
`Csc.Dependencies`, and `Packages` is `Lock.Entry.Packages`. The writer regroups them into the
three sections, so the file kept the shape it had before the record split.

| Section | Field | Content |
|---|---|---|
| entry | `Name` | `AssemblyName` (composed: the output file name without extension) |
| entry | `Framework` | the target framework; for a composed entry the `targetfwk` value as written |
| `Evaluation` | `Project`, `ProjectRefs` | the project file; `ProjectReference` items as project files |
| | `Imports` | msbuild files outside the SDK and outside restore's `obj/`, hashed |
| | `Sdk`, `SdkPin` | `NETCoreSdkVersion`; `"none"`, `"exact <v>"`, `"<v> rollForward:<policy>"`, `"no version (<file>)"`, or `""` |
| | `Properties` | a whitelist: `AssemblyName`, `TargetFrameworkMoniker`, `LangVersion`, `Version`, `InformationalVersion`, `SignAssembly`, `AssemblyOriginatorKeyFile`, `Deterministic`, `TargetPath`, `IntermediateOutputPath` |
| `Compilation` | `Directory` | the compiler's working directory (the project directory; the project root when composed) |
| | `Options` | every other argument, in order, with the four section markers |
| | `Defines`, `Sources` | `/define:` symbols split on `;`; absolute source paths |
| | `Generated` | path to content of msbuild-written text inputs under the intermediate directory |
| | `Resources` | resx path to `.resources` output path |
| `Dependencies` | `Compiler` | `{ Tool; Path; Sha256; Version }` on one line |
| | `References` | `{ Path; Sha256 }`, plus `Alias` only when set |
| | `Analyzers` | `{ Path; Sha256 }` |
| | `Packages` | `{ Id; Version; Sha512; Direct; DependsOn }` on one line each |

### Section markers and the round trip

`Csc.ofArgs` moves every `/reference:` or `/r:` with exactly one item into `References` (the
`alias=` prefix split off), every `/analyzer:` or `/a:` with one item into `Analyzers`, every
`/define:` or `/d:` into `Defines`, every source into `Sources`, and leaves a marker string in
`Options` at the position of the first element of each block. `Csc.Args` expands the markers
back. A leading `@` in `Options` is always a marker, never a response file: the runner writes its
own rsp around the whole list.

Markers instead of a canonical order: real locks carry `/warnaserror+:NU1605` after the
sources, and the relative order of the reference, analyzer and source blocks differs between
Roslyn versions. A canonical order would rebuild a different command line on another SDK.

Both producers check the round trip. `Project.parseImport` fails with a diff when `entry.Csc.Args`
is not msbuild's absolute argument list, and nothing is written. `Csc.ofSettings` does the same
for the composed list. Two non-contiguous `/define:` switches are the one input that fails by
design; msbuild emits one.

### Tokenization

Paths are written with forward slashes and a root replaced by a token, so the file is the same
on every machine and a diff means a change of inputs.

| Token | Root |
|---|---|
| `$(NuGetPackageRoot)` | `DotNetFwk.nugetRoot ()`, or a folder of the build's own via `Roots.packageRootOverride` |
| `$(ProjectRoot)` | the engine's `ExecOptions.ProjectRoot`, not the process's current directory |
| `$(DotnetRoot)` | the SDK installation root, when found |
| `$(Name)` (extra) | one per sibling repository, declared in `ImportOptions.Roots` |

Roots are tried longest first. `Roots.tokenizeAll` replaces a root anywhere in a string when it
is followed by `/`, `=`, `,`, `;` or the end, because `/pathmap:<root>=/_/` carries one in the
middle. A lock must be read with the same roots (or a superset) it was written with, or a token
stays unexpanded. `Lock.load` and `save` use the built-in three; `loadWith`/`saveWith` take extra
roots. There is no shared parent root for sibling repositories: siblings move independently, and
a shared root would tokenize more than intended.

### Hashing rules

| Function | Missing file | Used for |
|---|---|---|
| `Hash.sha256` (base) | throws | a verification, which must never produce `""` by accident; `Sign.imageHash` of a nupkg |
| `Csc.sha256` (base) | `""` | everything the lock records: import, `rehash`, the runner's check, the SBOM root hash |

An empty `Sha256` in a lock means "not computed", never "zero bytes". The runner skips it, and
`Lock.diff` compares two hashes only when both are non-empty. That is why a composed compilation
(`Csc.ofSettings` never hashes) can be diffed against a recorded, hashed lock on every build
without reporting every reference. Imported project references are unhashed by design: they
point at the referenced project's own output, not built at import time.

Hashes are lowercase hex SHA-256. Package hashes are NuGet's base64 SHA-512 as found in
`.nupkg.metadata`; the SBOM converts them to hex.

### `$(SourceRevisionId)`

SourceLink writes `sourcelink.json` with the commit in its URL template, and `sourcelink` is an
input switch, so the file lands in `Generated`. Left as is, the lock would change on every
commit. `parseImport` asks msbuild for `SourceRevisionId` and, when it is set, applies
`Lock.mapText (Git.tokenize sha)`: every occurrence in `Generated` content, `Options`, `Defines`
and evaluation property values becomes the token. The sha itself is recorded nowhere.
`Lock.compileWith` resolves the token from `Git.headSha c.Directory` right before compiling, and
fails when there is no repository. Because the lock content no longer changes with the commit,
`Project.import` `needFiles` the git `HEAD` files of every project, so a new commit re-imports.

### Project references

An imported entry's project references are `Reference`s with an empty hash, at the path msbuild
would have built them (`bin/...`). A script maps them to the path its own rule produces them at
(`Lock.mapPaths`, which drops the hash of any rewritten item), `need`s those outputs, and then
calls `Lock.compile`. The maintainers' import script on the feature branch is the worked
example (the guide, section 6, shows the pattern); section 8 has the caveat about keying the
remap on the empty hash.

### Packages

`Lock.packagesOf` builds the graph at import time, inside the project lock, right after the
design-time build: every package of the framework's target in `project.assets.json` (`type:
project` entries excluded), the SHA-512 from the cache, `Direct` from the project's own framework
section, and `DependsOn` from the graph edges. The runner never reads it; `Restore` uses it for
the original id casing and the nupkg hash, and `Sbom` uses it for components and edges.

### `Lock.diff`

Pure, deterministic order, `[]` means identical: `Framework`; `Options` and `Sources` as ordered
lists (bare `+`/`-` lines); `Defines` as a set; the compiler's path, hash and version;
`Evaluation.Sdk`; `References`, `Analyzers`, `Imports` by path; `Generated` and `Resources` by
key; `ProjectRefs` as a set; `Packages` by id (added, removed, version changed, SHA-512 changed).
Line shapes are `+ x`, `- x`, `~ Label path: old -> new`.

### Determinism, and its limits

| Guarantee | Evidence |
|---|---|
| `format` is a pure function of the document and the roots | golden test (section 7) |
| write, parse, write again is byte-identical | `LockDiffTests`, "the pre-split text reads back into the same entry and writes back unchanged" |
| a second build that matches the lock does not touch the file | `CscLockTests`, "second build with unchanged settings leaves the lock untouched" |
| the lock does not change with the commit | `$(SourceRevisionId)` tokenization |
| the lock does not change with the machine | tokenization |

Limits:

- A lock changes when the SDK changes (the compiler path and hash), and the import warns when
  the SDK is not pinned exactly.
- A lock records absolute paths under `$(ProjectRoot)`; a lock is portable, but byte-identical
  *output* also needs the same checkout path or a `/pathmap`.
- An older lock with a document-level `"Framework"` still reads (the value goes into every entry
  without one). The flat pre-split format (`"Projects"` with `"Args"`) is refused with "re-import".
- Empty lists are written as an opening bracket, an empty line and a closing bracket. This is
  stable, but it reads oddly in a diff.
- One lock file per `csc { lock }` compilation. Two blocks sharing one path read and write the
  same file; the library does not guard against it.

## 5. The other modules, mechanism level

### Restore

Nothing in `Restore` resolves a version or reads `project.assets.json`. A reference under the
package folder is spelled `<root>/<id>/<version>/...`, so a missing file names its own package
(`Nuget.packageOf`). `Lock.restoreRequest` lists every compiler, reference and analyzer path of
the entries; `Restore.missing` keeps those under the folder that do not exist and groups them by
package; `Restore.ensure` fetches the whole missing set with one `dotnet restore` of a
synthesized `PackageDownload` project (no dependency walk, no framework compatibility check),
then compares each restored package's `.nupkg.metadata` SHA-512 with the lock.

- With nothing missing, the cost is one `File.Exists` per distinct path: no process, no network.
- Concurrent compiles needing the same packages share one restore: one `Resource` per package
  folder, and a recheck inside it.
- A package is memoized per process after a restore attempt, so a hundred entries naming a
  package the restore could not provide do not start a hundred restores. The runner's hash
  check then reports what is still missing.
- With `Enabled = false` nothing is fetched; a warning names the packages, and the runner reports
  every missing file with its expected hash.
- The folder used to read the lock and the folder restored into must be the same:
  `Restore.into dir` and `Lock.loadWith (Roots.packageRootOverride (Restore.packageRoot o))`.

Tests (`RestoreTests`): missing-set grouping and casing; the restore project lists every package
at an exact version; `verify` reports an absent package and a wrong SHA-512; `into` is relative
to the project root; integration: a real restore into a scratch folder then a compile, restore
off reports instead of fetching, a wrong recorded SHA-512 fails by name.

### SBOM

`Sbom.forAssembly cacheRoot entry assemblyPath` is the **restore scope**: what the compile saw.

- Every package of `entry.Packages` is a component with `purl` `pkg:nuget/<Id>@<Version>` in the
  assets file's own casing. The cache is matched case-insensitively.
- Scope: a package with at least one referenced file is `excluded` when every such file is under
  its `ref/` folder, else `required`, with the referenced files nested as `file` components. A
  package with no referenced file is `required` when it is reachable from a direct dependency and
  `Nuget.ships` finds `lib/` or `runtimes/` content; otherwise `excluded`.
- References outside the cache (project references, SDK packs) are top-level `file` components.
- `dependencies[]`: the root depends on every direct, required package; package edges from
  `DependsOn` when both ends are in the BOM.
- `formulation`: analyzers, `csc` with `Compiler.Version`, the SDK with `Evaluation.Sdk`, all
  `scope: excluded`.
- The root is the assembly, hashed with `Csc.sha256`, version from `Evaluation.Properties`.

`Sbom.forPackage nupkg assemblies` is the restore-scope union for one nupkg: the nupkg as root
(SHA-256 and SHA-512 of the file), the assemblies nested under it, components and edges merged
by `bom-ref`.

`Sbom.forPackageScoped nupkg framework assemblies` is the **package scope**: what the customer
receives, per a package-scope SBOM specification supplied by the first user of the package. Tier 1
is the physical inventory of the nupkg for that framework (`lib/`, `runtimes/`, `contentFiles/`,
`analyzers/`, `build*/`, `tools/`), each file with SHA-256 and SHA-512, nested under the root;
our own assemblies are recognized by hash against the evidence BOMs. Tier 2 is the nuspec
dependency group for that framework, verbatim, with the declared range in
`dt:nuget:declaredVersionRange` and the resolved version and purl taken from the evidence.
There is no tier 3: nothing transitive appears. The boundary is declared: a `complete`
composition over the root's assemblies, an `incomplete` one over dependencies, and one annotation.
No `dependencies[]` entry has a tier-2 component as its `ref`. The root carries no hash, because
the document is meant to be packed into the nupkg at `sbom/<tfm>/bom.cdx.json`.

`Sbom.checkPackageScope options nupkg framework bom` runs the acceptance checks mechanically
(nupkg, SBOM and nuspec against each other) and shares `shippedPaths` and the options with the
producer, so the two cannot drift.

Determinism: no `metadata.timestamp`; `serialNumber` is a UUID-shaped value taken from the
SHA-256 of the document rendered with a placeholder serial; every unordered array is sorted. The
only clock value is the package-scope annotation timestamp, which is `SOURCE_DATE_EPOCH` or
`1980-01-01T00:00:00Z`. The tool component is `{ "name": "Xake.Hermetic.Dotnet", "version": <the
assembly version> }`, so SBOM bytes change when the package version changes.

The six questions the package-scope work left for a human decision (still open; they also
decide the final names of the `Sbom.for*` functions):

| # | Question | Current behaviour |
|---|---|---|
| 1 | Keep `formulation` (compiler, SDK, analyzers) in the package document? | dropped (`KeepFormulation = false`) |
| 2 | The annotation timestamp: DOS epoch, `SOURCE_DATE_EPOCH`, or real time? | epoch unless `SOURCE_DATE_EPOCH`; real time would cost byte identity |
| 3 | `dependencies[]` entries for our own assemblies? | emitted |
| 4 | Apply NuGet's nearest-compatible dependency group? | not applied; a missing group gives no tier 2 |
| 5 | A declared dependency the evidence never resolved: keep or fail? | kept, purl without version |
| 6 | A hash of the root (the nupkg)? | none; would require producing the SBOM after packing and shipping it next to the nupkg |

Tests (`SbomTests`): deterministic CycloneDX; the BOM of an assembly from a lock; the JSON
parses; `forPackage` from assembly BOMs; tier 1 is the shipped files for the framework, hashed;
tier 2 is the nuspec group verbatim with nothing transitive; the boundary is declared and the
render is deterministic; one document per framework; the verifier passes the generated document
and catches tampering; options override content and classification; no boundary text means no
annotation and the verifier says so.

### Verify

`Verify.authenticodeHash path` is SHA-256 over the file bytes in order, skipping three ranges: the
optional header `CheckSum`, the Certificate Table data-directory entry, and the certificate table
itself. Two files with equal hashes are the same image modulo signature. `Verify.compare a b`
byte-compares two files, merges differing bytes into ranges (gaps under 4 bytes bridged) and
labels each range from file `a`'s layout: `TimeDateStamp`, `CheckSum`, `CertificateTable`,
`DebugDirectory/PDB id (MVID/GUID)`, `StrongNameSignature`, or `Content`. A tail that only a longer
`b` has is labelled from `b`'s own layout, so an appended certificate table reads as
`CertificateTable`. `Verify.verdict` gives `identical`, `identical except: <fields>, <n> bytes`,
or `content differs: <n> ranges, <n> bytes, largest ...`. Nothing validates a signature.

Tests (`VerifyTests`): the Authenticode hash ignores checksum and certificate table; `compare`
labels the known fields; `verdict` reports totals and the largest range; a signed copy's
appended certificate table is labelled from its own layout; identical files compare empty; PE32+
parses alongside PE32.

### Pack

`Pack.zip output entries options` and `Pack.nupkg output nuspec files options` write the zip
format by hand: entries sorted by path (ordinal), one fixed timestamp (`Options.Timestamp`:
`SOURCE_DATE_EPOCH` or 1980-01-01), no extra fields, no comments, "version made by" with no
host platform, UTF-8 names. `nupkg` adds the nuspec at the root, `[Content_Types].xml`,
`_rels/.rels` and a `.psmdcp` whose GUID is derived from SHA-256 of (id, version, sorted file
hashes) instead of a random one. `Pack.list` reads the central directory back (path, size,
CRC-32, time). It is hand-rolled because `ZipArchiveEntry.Crc32` is not available on
netstandard2.0 or net462, and `ZipArchive` would stamp platform bits.

Tests (`PackTests`): two packs of the same inputs are byte-identical; entry order and timestamps
do not depend on input order or file times; the nupkg has the OPC parts and a content-derived
GUID; `dotnet` can read it.

### StrongName

`StrongName.sign image key` recomputes the CLR strong-name signature: SHA-1 over the PE header
up to its file-alignment padding (with `CheckSum` and the Certificate Table entry zeroed), then
every section with the signature blob cut out; RSA PKCS#1 v1.5; written little-endian into the
blob the CLI header names; the strong-name flag set. This exclusion rule was derived by
decoding csc's own signature, and it reproduces csc byte for byte. `CheckSum` is not recomputed
by `sign`; `normalise path timeDateStamp snk` does the whole sequence: set `TimeDateStamp`, sign,
then recompute `CheckSum` last. The purpose is to make an obfuscator's output (which stamps
wall-clock time) byte-identical across runs. `.snk` keys only (`readSnk`); no delay signing, no
SHA-256 strong names.

Tests (`StrongNameTests`): `writeSnk`/`readSnk` round trip; `sign` reproduces csc's signature
byte for byte; `checksum` is idempotent and reacts to changes; `stamp` sets `TimeDateStamp` and
recomputes `CheckSum` only; `normalise` touches exactly `TimeDateStamp`, `CheckSum` and
`StrongNameSignature`.

### Sign

Signing is the one delegated rule in the pipeline, because the key lives elsewhere, the work is
I/O-bound waiting, and the output is not byte-reproducible (a timestamp countersignature embeds
the signing moment). So:

- `Signer = Request -> Async<byte[]>` is the only thing a deployment replaces. `Sign.fakeSigner`
  appends a genuine `WIN_CERTIFICATE` with a JSON payload (PE) or adds a `.signature.p7s` entry
  (nupkg), and refreshes `CheckSum`. No real signing tool is wired up.
- `Sign.identity settings input` is SHA-256 of (image hash, certificate id, timestamp server,
  hash algorithm). The image hash is `Verify.authenticodeHash` for a PE and `Hash.sha256` for a
  nupkg. `Description` is not part of the key. Two targets holding the same image share one
  signature; the wall clock never enters the key.
- `Sign.rule settings signer` is the local rule: `need` the input, call the signer, write the
  target. `Sign.executor settings signer store budget` is a `DelegatedExecutor` that never runs
  the body: store hit, join an in-flight job for the same key, or take one unit of `budget` and
  call the signer, then publish. It records the input as a `FileDep`.
- `sign { target ...; input ...; certificate ...; signer ... }` returns the local rule, or the
  delegated one when both `store` and `budget` are given. No `signer` fails at construction;
  `store` without `budget` (or the reverse) fails too.
- `Sign.verifySameImage input signed`: the signed file carries a signature and its Authenticode
  hash equals the input's (for a nupkg, every entry but the signature is unchanged).

Tests (`SignTests`): `sign {}` gives the local or the delegated rule; the fake signer leaves the
Authenticode hash alone and adds a certificate table; two targets sharing one image are signed
once; a second run serves every target from the store with no signer call; the budget caps
concurrent signer calls independently of `Threads`; a signed nupkg gains the signature entry and
keeps every other entry; the identity key follows the image and the policy, never the signing
moment.

## 6. Compiler server

`Csc.run` adds `/shared` (and `/keepalive:<n>` when asked) to the command line, ahead of
`/noconfig` and the `@rsp`. With `/shared`, `csc.dll` is a thin client: it connects to the
`VBCSCompiler` next to it, starting one if needed, and compiles in-process when no server can be
reached.

What it does not affect:

- **The lock and `Csc.Args`.** The switches are added at invocation time, not in the rsp and not
  in the argument list; csc strips them on the client side before the request is built. Locks
  recorded with and without the server are identical.
- **The bytes.** The request carries the full argument list, the working directory and the temp
  directory. Measured: identical output with and without the server (one library in
  `CscServerTests`, 36/36 files on the dataengine fixture).
- **Which compiler compiles.** The client finds the server in its own directory, and the default
  pipe name is derived from that directory, so another SDK or toolset version gets its own server.
  The request carries the client's commit hash; a mismatching server makes the client compile
  in-process. The hashed `csc.dll` and the compiling server are the same Roslyn build.

When the switch is not added even though the server is on (`serverArgs`): no `VBCSCompiler.dll`
next to the compiler file (legacy `csc.exe`, mono's `mcs`, an arbitrary `cscpath`, a Roslyn
`csc.exe` from a toolset's `tasks/net472`); the runner sets environment variables for the
compiler (mono and legacy Framework paths); the compiler lives under the temp directory (a
throwaway toolset restore would leave a server holding a deleted folder).

Precedence (`CompilerServer.resolve`), first that is set wins:

| # | Source | Values |
|---|---|---|
| 1 | the target's `noserver` / `keepalive n` | `Some InProcess` / `Some (Shared (Some n))` |
| 2 | script variable `CSC_SERVER` | `on`, `off`, or seconds; case-insensitive; anything else warns and means `on`; read with `getVar`, so changing it rebuilds |
| 3 | env `XAKE_CSC_SERVER` | `0`, `false`, `off`, `no` turn it off |
| 4 | default | `Shared None` (Roslyn's own keepalive, 600 seconds) |

`Lock.build` and `Lock.compile` resolve the server with `CompilerServer.resolve None`;
`buildWith` and `compileWith` take the options as given; `csc { lock }` uses `Csc.runOptions`, so
a target's own `noserver` applies. `RunOptions.Default.Server` is `fromEnvironment ()`, which
does not read `CSC_SERVER`. Measured gains: about 25 percent of wall clock on a 12-assembly build,
about 3x per small compile. Verified on macOS only. Details: [csc-server.md](csc-server.md).

## 7. Evidence

### The dataengine verification

The working notes record an end-to-end run on a commercial C# library repository (called
*dataengine* in the notes: three shipped libraries, two brands selected by an msbuild property,
`netstandard2.0;net472`, central package management, strong-name keys, twelve assemblies in
total). The fixture is a `git archive` copy; the real checkout is never touched. The script
imports one lock per brand (both frameworks inside), builds all twelve assemblies from the
locks, builds the msbuild baseline in the same directory with the same `IntermediateOutputPath`
and `-t:Rebuild`, and compares.

| Check | Result |
|---|---|
| byte-identical to `dotnet build` (dll, pdb, xml) | **36/36** |
| deterministic: `obj/xake` deleted, compiled again | 36/36 |
| SBOMs regenerated | 12/12 byte-identical |
| built against a freshly restored package folder | 36/36 identical to the build against the machine cache |
| with the compiler server on (SDK 8.0.425) | 36/36, 36/36, 12/12, 36/36; `/shared` in all 12 traced command lines |
| after the `Csc` record split (B2) and after the extraction (E1 to E4) | 36/36, 36/36, 12/12, 36/36 |
| the dataengine locks, old writer vs new writer | identical (`cmp`) |

Behaviour observed on the same fixture:

- A build from committed locks starts no msbuild and no restore (`[msbuild]` count 0 in the log).
- An empty package folder triggers exactly one restore for the whole run, then all twelve
  compiles; with restore off, the build stops before the compiler listing every missing file.
- A tampered reference stops the build with `expected <sha>, got <sha>`, all mismatches at once.
- Timings (macOS, 8 cores): importing both locks 13.4 s (30 msbuild runs); a cold build 7.3 s;
  a no-op 1.1 s wall (0.12 s engine). With the compiler server, the build phase median was
  3.43 s against 4.44 s in-process.

The matrix also found a defect: two frameworks of one project imported concurrently raced on
`obj/project.assets.json`. The fix is structural: one import per variant covers every framework,
one restore per project without `TargetFramework` and with `RestoreRecursive=false`, and a
`Resource` per project path.

A second, larger fixture (12 libraries plus a cross-repository reference into the first) was
90/90 byte-identical on 2026-09-24, and its 30 SBOMs regenerated byte-identical after the lock
split. (The notes disagree on whether the 90/90 run used the split lock format.) Its byte comparison cannot currently be re-run in a tree Xake has built in
(section 8).

A shipped, signed release of one dataengine library was also compared with a local build of the
same tag: the Authenticode hashes differ (140 small `Content` ranges), and with the certificate
table accounted for the images are the same size. That run showed the comparator works on real
signed binaries; it did not show reproducibility of that release.

### The golden lock-format test

`LockDiffTests`, fixture "Lock file format", holds a hand-written golden lock with an evaluation,
a pinned SDK, markers in msbuild order (`@Defines`, `@References`, `@Analyzers`, then `/out:`,
`@Sources`, `/warnaserror+:NU1605`), generated content with `\r\n`, a resx pair, an aliased
unhashed reference, and two packages. Two tests: `Lock.format roots document` equals the golden
text byte for byte, and `Lock.parse roots golden` equals the document and formats back to the
same text. The golden text is the output of the writer before the record split, so the split is
proven not to have changed the file format.

### Test suites

`src/tests` (base) does not reference the hermetic package; `src/hermetic.tests` references all
three assemblies. At the last recorded run: base 252 passed and 1 skipped, hermetic 118 passed,
0 warnings in both libraries. `build.fsx` runs both test projects and asserts the packed nuspec
range.

### Verified on macOS only

| Claim | Status |
|---|---|
| dataengine 36/36 and the larger fixture 90/90 | macOS, SDK 8.0.423 and 8.0.425 |
| compiler server behaviour and gains | macOS; the `ps` assertion in `CscServerTests` is Unix-only |
| the `resx` `.resources` byte identity | the netstandard2.0 code path only (tests run on net8.0) |
| the net462 build of the library itself | compiles; its `Impl.compileResx` path (`ResXResourceReader`) is not exercised |
| Windows: registry-located .NET Framework, case-insensitive paths | not exercised by these runs |
| the CI workflows (`publish.yml`, `build.yml`) | the tag-to-package step was run locally; the workflows themselves have not run |

## 8. Known limitations and open items

| Item | Detail |
|---|---|
| `fsc` is not through the runner | `Dotnet.fsc.fs` names no `Lock` or `RunOptions`. F# compilations are described by the kept evaluation (`Fsproj.evaluate`), not by a lock, and are not hash-gated. Planned after the 3.4.0 release as an `Fsc` record with `Fsc.ofSettings`/`Fsc.run` over a private runner core shared with `Csc.run`; then `Fsproj` retires into `Project.import`. Additive, not breaking |
| The net462 resx path | `Impl.compileResx` has two bodies on purpose: net462 uses `ResXResourceReader` (typed values, file refs), netstandard2.0 uses `Resx.compile`, which reads plain string values only and throws on typed entries. The runner calls `Resx.compile` for a missing `.resources`. No byte-identity claim is made for net462 |
| The nuspec range trick | `dotnet pack` would write the dependency on `Xake` as `>= 1.0.0` (or at the hermetic version under a global `/p:Version`). The target `XakeDependencyRange` rewrites `ProjectVersion` of NuGet's private item `_ProjectReferencesWithVersions` after `_GetProjectReferenceVersions`. If a future SDK renames that item, the range silently falls back; `build.fsx` therefore opens the packed nupkg and fails unless every `Xake` dependency is `[3.4.0, 4.0.0)` |
| The fsx bootstrap | until both packages are on nuget.org, `build.fsc.fsx` and the maintainers' scripts on the feature branch load `.bootstrap/*.dll`, an ignored folder copied by hand from `out/` or `bin/`; a fix in `src/hermetic` takes effect there only after a re-copy. After the release they switch to `#r "nuget: Xake, 3.4.0"` and `#r "nuget: Xake.Hermetic.Dotnet, 0.1.0"`; `build.fsx` stays on the base package only |
| `sign` shadowing | `open Xake.Hermetic.Dotnet` brings the builder `sign` into scope, shadowing FSharp.Core's numeric `sign` (still `Operators.sign`). A value named like an operation (`signer`, `store`, `budget`) cannot be passed inside the block (FS3095); tests name them `theSigner` and so on |
| SBOM tool identity | `metadata.tools.components[0]` is `Xake.Hermetic.Dotnet` with the executing assembly's version (the `AssemblyVersion`, for example `0.1.0.0`, not the package's full version), and the package-scope annotation's annotator is the same name (`Sbom.toolName`). There is no vendor field |
| Vendor defaults in `PackageScopeOptions.Default` | `PackageScope.internalIds` is `DS.*`, `MESCIUS.*`, `GrapeCity.*`, the first user's prefixes; other users must override `IsInternal` |
| The six SBOM questions | section 5; they also decide the final names of `Sbom.forPackage` / `forPackageScoped(With)` |
| `targetfwk` required | `Csc.ofSettings` fails without `targetfwk` or `NETFX-TARGET` (decided: an error, the Windows `csc.rsp` fallback is gone). The composed mode only locates .NET Framework monikers (`net-4.6.2`, `net472`, ...) and `netstandard2.0`/`2.1`, so the message suggests `targetfwk "netstandard2.0"`; `net8.0` fails with "not a known .NET Framework profile". A modern target framework goes through `Project.import` |
| A locally built reference in `csc { lock }` | `Csc.run` checks hashes after `needFiles (CscArgs.inputs args)`, so a reference another rule of the script produces is rebuilt first and the check sees the bytes the compiler reads. If such a reference was recorded with a hash (`Lock.record` after it exists), any change to it is then a hash mismatch on the next build that rebuilds it. The imported-lock pattern avoids it by mapping project references (which drops the hash) and `need`ing them first |
| New source files and the gate | a composed `csc {}` expands its filesets without recording them as a dependency, so a new file matching the glob does not rerun the target; the lock gate only fires when something else changes (observed; base behaviour) |
| `Lock.build` and `Lock.verify` look up by name | they use `Lock.entry c.Name`, which fails when a lock holds the name for several frameworks. Composed locks hold one entry, so this only matters for a hand-merged lock |
| The one-call build of an imported lock | the extraction plan sketched `Lock.build lockFile framework name` and a `Lock.buildWith { RunOptions; ExtraRoots; OutputOf }` that would also map project references. Not built: `Lock.build` gates a composed `Csc`; an imported lock is built with `Lock.load`, `Lock.entryFor`, `Lock.mapPaths`, `need`, `Lock.compile` |
| Project-reference remap heuristic | keying the remap on "recorded hash is empty" breaks when a stale `bin/Release` output existed at import time (it gets hashed). `verify-dataengine.fsx` keys on the name matching another entry of the same framework; `import.fsx` still uses the empty-hash key |
| MSB3577 interop | on a project with two same-named `.resx`, the msbuild baseline fails in a tree where Xake wrote `.resources` into msbuild's `obj/xake/...` paths. Likely fix: write imported `.resources` under a path Xake owns; the manifest name, not the path, is embedded |
| `Generated` / `.resources` are not engine targets | the runner writes them inline; a rule factory would make them first-class targets (review item, waits for a second consumer) |
| Restore scope | only what the compilation reads is restored (compiler, references, analyzers), not the whole package graph; `csc { toolset }` in composed mode always uses the machine's cache |
| Package integrity | the nupkg is not re-hashed; the check trusts `.nupkg.metadata`. The per-file SHA-256 is the real gate |
| `StrongName.signFile` | re-signs but does not recompute `CheckSum`; use `normalise`, or `checksum` after it |
| `Verify.verdict` and signer padding | a certificate table must start 8-byte aligned; when the unsigned file is not, the padding shows as a short `Content` range and the verdict says "content differs". PE files are 512-byte aligned in practice |
| Test-order fragility | `DotNetFwk.locateFramework` memoizes process-wide while one fixture points `NUGET_PACKAGES` at a scratch folder; worked around in `RestoreTests`, not fixed |
| Not built | `Sln` parsing, `Policy` gates, obfuscator task, SBOM items declared in msbuild (`SbomComponent`), JSF signing of the SBOM, a real signer, signature validation, provenance statements |

## 9. Release and versioning

| | `Xake` | `Xake.Hermetic.Dotnet` |
|---|---|---|
| Project | `src/dotnet/Xake.Dotnet.fsproj` (`PackageId=Xake`, bundles `Xake.dll` from `src/core`) | `src/hermetic/Xake.Hermetic.Dotnet.fsproj` |
| Namespace | `Xake`, `Xake.Dotnet` | `Xake.Hermetic.Dotnet` |
| Targets | `net462;netstandard2.0` | `net462;netstandard2.0` |
| FSharp.Core floor | 8.0.100 | 8.0.100 |
| First version | 3.4.0 | 0.1.0 |
| Version source | `/p:Version` from `build.fsx` or CI (`VERSION`) | fsproj `<Version>0.1.0</Version>`, overridden by `HERMETIC_VERSION` |
| Tag | `v<X.Y.Z>` | `hermetic-v<X.Y.Z>` |
| Dependencies | FSharp.Core; Microsoft.Win32.Registry on netstandard2.0 | `Xake [3.4.0, 4.0.0)`, FSharp.Core |

The versions are independent. `Xake` stays semantic-versioned 3.x. `Xake.Hermetic.Dotnet` starts
at 0.1.0, a preview: its API may break while it is 0.x. The range `[3.4.0, 4.0)` (written by
NuGet as `[3.4.0, 4.0.0)`) holds only because the hermetic package uses no internals of the base;
the base names it relies on (`Csc`, `RunOptions`, `Csc.run`, `Csc.ofSettings`, `resolve`,
`CSC_SERVER`, `CSC_TOOLSET`, `Hash`, `Tool`, the `DotNetFwk` functions) become breaking to change after 3.4.0.
Lockstep versions would have made CI simpler but forced a `Xake` release for every
hermetic-only change.

The build: `build.fsx` has a third library with `dotnet build -p:BuildProjectReferences=false`
(a global `/p:Version` flows into project references and would otherwise rebuild `Xake.dll` at
the hermetic version). Packages go to `out/pkg/<id>/<id>.<version>.nupkg`, one rule per package.
The hermetic pack first builds `src/dotnet` at the Xake version, so the hermetic assembly
references `Xake 3.4.0.0`, then packs with `BuildProjectReferences=false` and asserts the nuspec
range. `publish.yml` maps the tag prefix to the package and the version variable, appends the
run number as the fourth component, runs `build test pack` (a hermetic release also exports
`VERSION=3.4.0`), and pushes only the tagged package with `--skip-duplicate`. `build.yml` runs
`build test pack` on SDK 8.0.x and 10.0.x, so the range assertion runs on the floor SDK too.

Order of release: `Xake` 3.4.0 first (it is the range floor), then `Xake.Hermetic.Dotnet` 0.1.0,
then the bootstrap switch (section 8). The `Fsproj.evaluate` fix (restore as a separate msbuild
call) is in the tree and must be in 0.1.0.

Before the first hermetic release every hermetic name is free to change. After it, names may
still change while the package is 0.x; each change should be listed in the release notes.
