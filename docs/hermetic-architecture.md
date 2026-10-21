# Xake.Hermetic.Dotnet: architecture

This document is for maintainers and reviewers of the pull requests that introduce the
`Xake.Hermetic.Dotnet` package. It explains what the package is for, how it is shaped, where it
meets the `Xake` package, and what is and is not proven. The user-facing reference lives in
[hermetic/README.md](hermetic/README.md) and the pages next to it; the task-oriented guide is
[hermetic-guide.md](hermetic-guide.md). The base task `csc {}` and its runner are described in
[tasks.md](tasks.md), [csc-syntax.md](csc-syntax.md) and [csc-server.md](csc-server.md).

Every name below was checked against the source on this branch (`src/hermetic/*.fs`,
`src/dotnet/Csc.fs`, `src/dotnet/Dotnet.csc.fs`, `src/dotnet/DotNetFwk.fs`,
`src/core/Fingerprints.fs`). The working notes
(the product brief, the extraction plan, the session log, the experiments) stay on the feature
branch and are not part of this documentation. This document summarizes the decisions taken
there; it does not reopen them.

## 1. The problem, and what "hermetic" means here

A stock .NET build is msbuild plus NuGet. Restore talks to the network. The package cache is a
mutable, machine-wide directory. `obj/` carries hidden incremental state. `RestorePackagesWithLockFile` locks
package versions, not compiler inputs. `/deterministic` gives identical bytes only if the inputs
are identical, and nothing in the stock toolchain proves that they were.

Xake already knows, per compiler invocation, which files went in: every input is a tracked
dependency of the target. The package builds on that. Its central object is the **lock**: a JSON
file, kept in git, that records per project and target framework exactly what the compiler is
handed (the command line, the generated inputs, the resx pairs) and the SHA-256 of everything it
reads from outside the repository (the compiler, every reference, every analyzer, the msbuild
files that produced the answer) plus the NuGet restore graph. A build then compiles *from* the
lock and fails when anything differs. Since 0.3 a lock can also come from a **project
description** (`xake.project.json`, section 5). Around the lock sit a deterministic packer, a
strong-name re-signer and a PE comparator. SBOM and signing: to be redone (0.4).

### What is controlled

| Input | How it is controlled |
|---|---|
| The compiler command line | recorded verbatim (structured, with section markers); the import checks that the recorded form rebuilds msbuild's exact list |
| References, analyzers, the compiler binary | path and SHA-256 in the lock; `Csc.run` refuses to compile when a recorded hash does not match the file on disk |
| Generated compiler inputs (assembly attributes, the derived `.editorconfig`, `sourcelink.json`) | content stored in the lock and written back before compiling |
| `.resx` resources | `(resx, .resources)` pairs; the `.resources` file is compiled from the resx whenever the compile runs (written when it changes) |
| The msbuild evaluation's own inputs | the project file and the imports outside the SDK, hashed (`Evaluation.Imports`); a change re-runs the import |
| The package graph | ids, versions, nupkg SHA-512, direct flags, edges (`Entry.Packages`) |
| Packages missing on this machine | fetched by exact id and version, the nupkg SHA-512 compared with the lock |
| The commit | kept out of the lock as the token `$(SourceRevisionId)`, resolved from `.git` at compile time |
| The SDK choice | recorded (`Evaluation.Sdk`, `Evaluation.SdkPin`); the import warns when `global.json` does not pin it exactly |
| The SDK an entry depends on | an exactly pinned SDK under `$(DotnetRoot)/sdk/<v>/` (fsc, an import's compiler and analyzers) is a `dotnet-sdk` prerequisite (`Entry.Prerequisites`), checked before a replay; without the exact pin the recording warns. A recorded entry (`Import.record`) has it when it names a file under `sdk/<v>/` (since 0.6, `Lock.withRecordedPrerequisites`): on the toolset compiler and package analyzers it has none. Since 0.3 a compiler of the SDK has no hash in the lock: the SDK version identifies it (`Lock.isSdkCompiler`) |

### What is not controlled

The brief calls the result "mostly hermetic", and that is the honest word.

- **msbuild is still the evaluator.** `Project.import` asks msbuild what it would run.
  `Import.descriptions` asks msbuild to evaluate a project file into a description.
  `Locks.generate` runs `dotnet restore` on a synthesized project to resolve the packages. The
  lock records the results, not the tools. A build from a lock starts no msbuild. Producing a
  description or a lock starts msbuild.
- **A description is a projection.** `Import.descriptions` keeps only what the schema has a field
  for. It reports the rest (`Import.Imported.Dropped`). A project that needs a dropped item is
  not built the same from its description.
- **The network.** Restore is allowed by default (`Restore.Options.Enabled = true`, which
  follows the base's `NUGET_FETCH`). A build can turn it off (`-d NUGET_FETCH=off`, `nofetch`),
  and then a missing package fails the build before the compiler runs. There is no vendored
  package source mode.
- **Completeness is opt-in.** Without `HERMETIC=on` a lock may name files under `$(DotnetRoot)`
  (the SDK's csc, the targeting pack) that no restore brings to a clean machine. With it, the base
  refuses such a resolve and this package refuses to write or replay such a lock
  ([hermetic/hermetic-mode.md](hermetic/hermetic-mode.md)).
- **The package cache is trusted by path and checked by hash.** The per-file SHA-256 check is the
  real gate. The package-level check compares the lock's SHA-512 with the `contentHash` NuGet
  wrote into `.nupkg.metadata`; it does not re-hash the `.nupkg`.
- **The SDK is a prerequisite, not a package.** fsc has no package, so an F# lock always names
  `$(DotnetRoot)/sdk/<v>/FSharp/fsc.dll`. An imported C# lock names the SDK's csc and analyzers,
  unless the project references the compiler package. A composed C# lock names the SDK's csc
  when no toolset is set. Under an exact `global.json` pin the lock records the SDK as a
  `dotnet-sdk` prerequisite. A replay checks the prerequisite first. Without the exact pin the
  recording warns, and a replay fails only when the compiler file is missing. A recorded
  (imported) entry carries the prerequisite of the SDK that recorded it exactly when it names a
  file under `sdk/<v>/` (since 0.6): a C# project on `Microsoft.Net.Compilers.Toolset`
  (`RoslynCompilerType=Toolset`) and `Microsoft.CodeAnalysis.NetAnalyzers` records none and
  replays with no SDK installed (`SdkFreeImportTests.fs`). fsc stays the remaining case.
- **A targeting pack is the installation's or the package's.** A `pack:` identity resolves to
  `$(DotnetRoot)/packs/` when this .NET has that version, else to the package of the same id
  and version in the package folder, which the lock's restore fetches. The two are not
  byte-identical: the nuget.org package signs its assemblies (Authenticode, strong name) and
  ships other builds of the facades and source generators, so a lock whose `pack:` hashes are
  the installed pack's fails the hash check against the package's copy (measured on
  `microsoft.netcore.app.ref` 8.0.32: 65 of 234 files differ beyond the signatures). A lock
  whose hashes are the package's (msbuild took the pack from the package folder) builds with an
  empty `packs/`.
- **The bytes of an SDK compiler are not checked.** Since 0.3 the lock records no hash for a
  compiler under `$(DotnetRoot)/sdk/<v>/` (`Lock.isSdkCompiler`). These files are ReadyToRun
  images for one OS and architecture, so a hash would tie the lock to one platform. The SDK
  version in the path and in the prerequisite identifies the compiler. A compiler from a package
  keeps its hash.
- **An import cannot choose the compiler.** `Project.import` records the compiler that msbuild
  wires up: the SDK's, or the compiler package that the project itself references. `ImportOptions` has no toolset field, and the import does not read `CSC_TOOLSET`. The
  description route can choose it: `Locks.generate` composes through `Csc.ofSettings`, which reads
  `CSC_TOOLSET` (C# only; section 5).
- **Byte identity across machines** also needs the same checkout path (csc embeds absolute
  source paths in the PDB and, under `/deterministic`, in the PE) or a `/pathmap`.
- **Obfuscation, installers, policy gates, provenance statements** are not built.

## 2. The model

### Three objects

| Object | Where | What it is |
|---|---|---|
| `CscSettingsType` | `Xake.Dotnet` (`Csc.fs`, `CscTypes`) | **intent**: what the script asks for (filesets, target, framework, defines, compiler source, server). The 3.3 settings record, extended with `Toolset` and `Server` |
| `Csc` | `Xake.Dotnet` (`Csc.fs`) | **the resolved compilation**: exactly what the compiler is handed. Filesets expanded, paths absolute, the compiler chosen, hashes empty or recorded |
| `Fsc` | `Xake.Dotnet` (`Fsc.fs`, 3.5) | the F# twin of `Csc`: the same fields, fsc's own argument dialect (`FscArgs`), run by `Fsc.run` with `FscRunOptions` |
| `Lock.Entry = { Compilation; Evaluation; Packages }` | `Xake.Hermetic.Dotnet` (`Lock.fs`) | a `Lock.Compilation = Csc of Csc \| Fsc of Fsc` **plus provenance**: where the answer came from (msbuild evaluation) and the restore graph. On disk the case is `Dependencies.Compiler.Tool`; a C# entry is written exactly as in 0.1. `Lock.Document = { Configuration; Properties; Entries }` is one lock file |

A `Csc` has three producers:

1. `Csc.ofSettings settings` (what `csc { ...; resolve }` returns) composes one from settings.
   `csc { project "path" }` (`Described.csc`) also ends in `Csc.ofSettings`: it turns the
   description into settings first. `Locks.generate` uses this producer.
2. `Project.import` builds one per project and framework from an msbuild design-time build, and
   wraps it in a `Lock.Entry` with a filled `Evaluation` and `Packages`. It does not go through
   `Csc.ofSettings`.
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
| `Lock.build path c` | hermetic | `Lock.buildWith`, `Lock.compileWith`, `Csc.run` (or `Fsc.run`, for `Lock.Compilation.Fsc`) |
| `Lock.compile entry` | hermetic | `Lock.compileWith`, `Csc.run` or `Fsc.run` by the entry's case |
| `fsc { ... }` | base | `Fsc.compile` = `Fsc.ofSettings`, `Fsc.runOptions`, `Fsc.run` |
| `fsc { ...; lock "path" }` | hermetic | `Fsc.ofSettings`, `Fsc.runOptions`, `Lock.buildWith`, `Lock.compileWith`, `Fsc.run` |
| `csc { project "d"; ...; lock "path" }` | hermetic | `Described.csc` (`Csc.ofSettings`), `Described.buildCsc` (`Lock.record` or `Lock.verify`), `Lock.compileWith`, `Csc.run` |
| `fsc { project "d"; ...; lock "path" }` | hermetic | `Described.fsc` (`Fsc.ofSettings`), `Lock.buildWith`, `Lock.compileWith`, `Fsc.run` |
| `Locks.compileAs lockPath fwk versions` | hermetic | `Lock.load`, `Lock.compileFrom`, `Csc.run` or `Fsc.run` |

`Fsc.run` shares the runner core (`CompilerRunner`, a plan record both runners fill) with
`Csc.run`; it is the only function that starts the F# compiler. `Lock.Options` carries one
options record per runner, `Run: RunOptions` and `FscRun: FscRunOptions`, each read only for
its own case.

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
  lock missing   -> under CI: fail                   (Lock.underCi; FailOnError = false does not change it)
                    else record (prerequisites, rehash, save) -> compileWith recorded
  lock matches   -> compileWith recorded            (the runner checks the recorded hashes)
  lock differs   -> fail with Lock.diff              (FailOnError = false: warn, compileWith resolved)

Lock.compileWith options entry
     an SDK compiler's hash is cleared              (withoutSdkCompilerHash; the SDK version identifies it)
  0. Entry.Prerequisites                            (a dotnet-sdk <v> not installed under the lock's $(DotnetRoot) fails here)
     HERMETIC=on: a path outside the roots fails     (message 10)
  1. Restore.ensure options.Restore (Lock.restoreRequest [entry])
  2. ensureCompilerAvailable                       (explain a compiler still missing)
  3. $(SourceRevisionId) -> Git.headSha c.Directory (only when the token occurs)
  4. Csc.run options.Run c  |  Fsc.run options.FscRun f   (by entry.Compilation)
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
`Restore` field; the package folder travels in `Lock.Options = { Run: RunOptions; FscRun:
FscRunOptions; Restore: Restore.Options }`.

### Lock strictness and CI

**Current behaviour (0.3).**

- `Lock.underCi` decides whether the build runs under CI. It reads the script variable `CI` first
  (`on`/`true`/`yes`/`1` or `off`/`false`/`no`/`0`; any other value fails). When the variable is
  not set, it reads the environment variable `CI`. GitHub Actions, GitLab CI and Azure Pipelines
  set it. Nothing reads `GITHUB_ACTIONS`. A script can read the flag with `let! ci =
  Lock.underCi`.
- `Lock.buildWith` is strict. `Lock.build`, `csc { lock }`, `fsc { lock }` and
  `csc { project; lock }` (`Described.buildCsc`) use the same rules:
  - A missing lock is recorded. Under CI, a missing lock fails the build.
  - A lock that differs from the resolved compilation fails the build with the differences.
    The build never rewrites a lock that exists, on CI or on a developer machine.
  - Only `Lock.record`, `Locks.generate`, `Project.import` or deleting the file changes a lock.
- `nofailonerror` (`FailOnError = false`) changes a mismatch into a traced error. The build then
  continues: `buildWith` compiles the resolved compilation, and `buildCsc` compiles the recorded
  entry. This also applies under CI. Only the missing-lock check ignores `FailOnError`.
- `Locks.compileAs` compiles the committed lock as it is. It does not compare the lock with the
  description. `Fingerprints.warn` reports a stale lock or description as a warning.
  `Fingerprints.check` fails on one. Neither function reads `CI`.
- `Locks.generate`, `Import.descriptions` and `Project.import` do not read `CI`. They write their
  files each time they run.
- `HERMETIC=on` and `NUGET_FETCH` are independent of `CI` ([hermetic/hermetic-mode.md](hermetic/hermetic-mode.md)).

**What to do on CI today** to never rewrite a lock and to fail on a mismatch:

1. Do not set `CI` yourself. The runner sets it, and a missing lock then fails.
2. Do not use `nofailonerror` in a block that has `lock`.
3. Do not run the targets that call `Import.descriptions`, `Project.import`, `Locks.generate` or
   `Lock.record` (for example `import` and `update-locks`).
4. For described projects, run `Fingerprints.check "xake.fingerprints"` before `build`. Or let
   `build` call `Fingerprints.check` when `Lock.underCi` is true and `Fingerprints.warn` when it
   is false.
5. As a safety net, run `git diff --exit-code` on the locks and descriptions after the build.
6. For completeness, also set `HERMETIC=on` ([hermetic/workflows.md](hermetic/workflows.md#d-ci-github-actions-and-gitlab-ci)).

**Proposal (not built; for decision).** These changes would put the rules above into the code:

- Under CI, a lock mismatch fails even with `FailOnError = false`. That is, CI disables the
  override.
- Under CI, `Locks.generate`, `Import.project` and `Project.import` fail when the file they
  would write differs from the committed one.
- A library function that runs `Fingerprints.check` under CI and `Fingerprints.warn` otherwise.

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
| 2 | `Hash.fs` | 2 | `Description.fs` |
| 3 | `DotNetFwk.fs` | 3 | `Roots.fs` |
| 4 | `ResourceFileset.fs` | 4 | `Nuget.fs` |
| 5 | `Resx.fs` | 5 | `Restore.fs` (also the namespace-level `Package` type) |
| 6 | `DotnetTasks.fs` | 6 | `Git.fs` |
| 7 | `CscArgs.fs`, `FscArgs.fs` | 7 | `Lock.fs` |
| 8 | `CompilerRunner.fs`, `Csc.fs`, `Fsc.fs` | 8 | `Described.fs` (also `CscDescribedBuilder`, `FscDescribedBuilder`, and since 0.4 `CscLocked`, `CscLockBuilder`, `FscLocked`, `FscLockBuilder`) |
| 9 | `Dotnet.csc.fs` | 9 | `Locks.fs` |
| 10 | `Dotnet.fsc.fs`, `Dotnet.resgen.fs`, `Dotnet.Msbuild.fs` | 10 | `Project.fs` |
| | | 11 | `Import.fs` |
| | | 12 | `Verify.fs` |
| | | 13 | `StrongName.fs` |
| | | 14 | `Pack.fs` |

The table leaves out `Sbom.fs` (after `Import.fs`) and `Sign.fs` (last). SBOM and signing: to be
redone (0.4).

Within the hermetic package the order follows the dependencies. `Restore` names no lock type,
so it compiles before `Lock` and takes a `Restore.Request`, not lock entries. `Git` compiles
before `Lock` (the lock needs `Git.headSha`), which is why `Git.tokenize` works on text and a
whole entry is tokenized with `entry |> Lock.mapText (Git.tokenize sha)`. `Description` uses only
`Json`, so it compiles early. `Described` uses `Lock` (`Described.buildCsc`), so it compiles
after `Lock`. `Locks` uses `Described` (`Described.restore` and the `project` operation), so
lock generation is a module of its own and not part of `Lock`. `Import` uses `Project.runMsbuild`
and `Project.parseImports`, so it compiles after `Project`.

Who uses whom inside the hermetic package (names used in code, not comments):

| Module | Uses |
|---|---|
| `Roots` | `DotNetFwk` |
| `Nuget` | `Json` |
| `Restore` | `Nuget`, `Roots`, `DotNetFwk` |
| `Git` | nothing |
| `Lock` | `Csc`, `Fsc`, `CompilerServer`, `RunOptions`, `FscRunOptions`, `Nuget`, `Restore`, `Roots`, `Git`, `Json` |
| `Description` | `Json` |
| `Described` | `Description`, `Lock`, `Restore`, `Nuget`, `Csc`, `Fsc`, `DotNetFwk`, `Tool` |
| `Locks` | `Description`, `Described`, `Lock`, `Roots`, `CscArgs`, `FscArgs`, `CompilerServer` |
| `Project` | `Lock`, `Csc`, `CscArgs`, `Fsc`, `FscArgs`, `Nuget`, `Roots`, `Git`, `Json`, `Tool` |
| `Import` | `Description`, `Project`, `Json`, `DotNetFwk` |
| `Verify` | nothing |
| `StrongName` | `Verify` (the internal PE `Layout`) |
| `Pack` | nothing |

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
| `Csc.ofSettings settings` | recipe | composes a `Csc`; the framework from `targetfwk`, else `NETFX-TARGET`, else the SDK's own .NET; the output from `out`, else the rule's file target; the compiler from `Toolset`, else `CSC_TOOLSET`, else the SDK `global.json` selects; never hashes |
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
| `nugetRoot ()` | the environment variable `NUGET_PACKAGES`, else `~/.nuget/packages` (no build context) |
| `packageRoot ()` | recipe: the build's package folder -- the script variable `NUGET_PACKAGES` (relative to the project root, or absolute), else `nugetRoot ()` |
| `fetchEnabled ()` | recipe: `false` when the script variable `NUGET_FETCH` is `off`/`false`/`no`/`0`; what `Restore.Options.Enabled = true` follows (`Restore.resolve`) |
| `packageRootWithSource ()` | recipe: `packageRoot ()` and where it came from (`ScriptVariable`, `EnvironmentVariable`, `UserProfile`); only the script variable satisfies `HERMETIC=on` |
| `globalJsonPin dir`, `sdkVersionOf root path`, `sdkPrerequisite`, `sdkTargetingPackVersion` | the `global.json` pin (`Exact`, `RollsForward`, `NoVersion`, `NoGlobalJson`); the SDK version a path belongs to; read by `Lock.sdkPinAt`/`sdkVersionsOf` |
| `HermeticMode.enabled ()`, `check name roots inputs`, `enforce`, `roots ()` | the `HERMETIC` variable; the rule over a list of `Input` (role, path) against `Roots` (tokenized ones work too); the gate of `ofSettings`; this build's roots. `Csc.hermeticInputs`/`Fsc.hermeticInputs` list a compilation's inputs |
| `dotnetRoot ()` | the SDK installation root, when one can be located |
| `normalizedPackageRoot root` | the package folder in effect (`None` = the machine's cache), forward slashes, no trailing slash |
| `restoreProjectText packages` | the synthesized restore project: `netstandard2.0`, `DisableImplicitFrameworkReferences`, one `PackageDownload` per package at an exact `[version]` |
| `downloadPackages root packages` | (`None` = `packageRoot ()`; with fetching off, fails naming the missing packages) one `dotnet restore` of that project under `obj/xake/restore/<n>/` of the project root (so `nuget.config` is found), with the repository's `Directory.Build.*` and central package management switched off, `NUGET_PACKAGES` pointed at the folder |
| `restorePackage root id version` | the package directory (`None` = `packageRoot ()`), downloading it first when absent (failing when fetching is off); `csc { toolset }` and `CSC_TOOLSET` use it |
| `locateFrameworkIn root fwk` | the toolchain for a framework, the SDK being the one `dotnet --version` reports in `root` (`global.json`); memoized per (root, framework) |
| `locateFramework fwk` | `locateFrameworkIn` with the current directory as the root |
| `sdkProbeWarning root` | the probe's warning for `root` (a `global.json` pin it could not honour), once that root has been probed |

### Hermetic modules

`Json` (internal): a small JSON reader and `escape`, written so that the package has no
`System.Text.Json` dependency on netstandard2.0.

`Roots`:

| Name | Kind | What it does |
|---|---|---|
| `nugetRoot ()`, `dotnetRoot ()` | forwarders | `DotNetFwk.nugetRoot` (the environment only: the folder of the context-free `builtin`/`make`), `DotNetFwk.dotnetRoot` |
| `nugetPackageRootToken` | value | `"$(NuGetPackageRoot)"` |
| `builtinTokens` | value | `$(NuGetPackageRoot)`, `$(ProjectRoot)`, `$(DotnetRoot)` |
| `packageRootOverride dir` | pure | `[ "$(NuGetPackageRoot)", dir ]`, the extra-root list that points the package token at a folder of the build's own |
| `builtin projectRoot` | pure | the three built-in roots, longest root first |
| `make projectRoot extra` | pure | built-in plus extra roots; validates `$(Name)`, resolves relative paths against the project root, an extra root replaces a built-in of the same token |
| `current`, `currentWith extra` | recipes | the same, with the project root from `ExecOptions.ProjectRoot` and `$(NuGetPackageRoot)` at the build's folder (`DotNetFwk.packageRoot ()`, the script variable `NUGET_PACKAGES` first); an extra `$(NuGetPackageRoot)` wins |

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
| `Options` / `Options.Default` | record | `PackageRoot: string option` (`None` = the build's folder, resolved where a build context exists), `Enabled: bool` (default `true` = follow `NUGET_FETCH`) |
| `packageRoot options` | pure | the folder in effect (`None` = the environment's here) |
| `resolve options` | recipe | `PackageRoot = None` to `DotNetFwk.packageRoot ()`, `Enabled` and `DotNetFwk.fetchEnabled ()`; called by `ensure`, `Lock.compileWith`, `Lock.buildWith` |
| `into dir` | recipe | options with the folder relative to the project root |
| `Request`, `Missing` | records | what must exist (graph + paths); one missing package with its files |
| `missing options request` | reads disk | missing packages, grouped; one `File.Exists` per path, nothing else |
| `verify options missing` | reads disk | absent package directories and nupkg SHA-512 mismatches |
| `download options packages` | recipe | `DotNetFwk.downloadPackages`, ignoring `Enabled` |
| `ensure options request` | recipe | `resolve`, then restore what is missing once per process, serialized per folder by a `Resource`; returns the problems |

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
| `Compilation` | union | `Csc of Csc \| Fsc of Fsc`, `RequireQualifiedAccess`; common members `Name`, `Framework`, `Directory`, `Options`, `Defines`, `Sources`, `Generated`, `Resources`, `RuntimeConfig`, `Dependencies`, `Args`, `Output` |
| `Prerequisite` / `DotnetSdk` | record, literal | `{ Kind; Version; Pin }`, `Pin` kept tokenized; `"dotnet-sdk"` |
| `sdkPinAt dir`, `ofGlobalJsonPin` | reads disk, pure | the `global.json` found upwards from `dir` and its `SdkPin`, read by the base (`DotNetFwk.globalJsonPin`) and mapped to the lock's type |
| `sdkVersionsOf roots paths`, `prerequisitesFor roots pinFile pin versions`, `sdkUnpinnedWarning` | pure | the SDK versions paths depend on (`DotNetFwk.sdkVersionOf` against the roots' `$(DotnetRoot)`); the prerequisites under a pin and the versions left without one; the warning text |
| `hermeticInputs c`, `hermeticViolations roots entry` | pure | the compilation's inputs (`Csc`/`Fsc.hermeticInputs`); its tokenized paths outside `$(ProjectRoot)`/extra roots and `$(NuGetPackageRoot)` not covered by a `dotnet-sdk` prerequisite (fsc's compiler; an import's compiler and analyzers), through `HermeticMode.check` |
| `hermeticRecordGate lock roots entries`, `hermeticRecordMessage`, `hermeticReplayMessage` | recipe, pure | under `HERMETIC=on` the reasons not to write a lock (message 2, then message 9 per path); the texts of messages 9 and 10 |
| `Entry`, `Document` | records | `{ Compilation; Evaluation; Packages; Prerequisites }` (members `Name`, `Framework`, `Dependencies`, `Args`, `Output`, and `Csc`/`Fsc`, which fail for the other case); `{ Configuration; Properties; Entries }` |
| `packagesOf cacheRoot assets` | reads disk | the entry's package graph from `Nuget.Assets` and the cache's SHA-512 |
| `ofCompilation c`, `ofCsc c`, `ofFsc f` | pure | a composed compilation as an entry |
| `rehash entry` | reads disk | `Csc.rehash`/`Fsc.rehash` plus the `Imports` hashes; a recorded entry's `dotnet-sdk` prerequisites derived again (`withRecordedPrerequisites` under `Roots.dotnetRoot ()`) |
| `recordedSdkVersions dotnetRoot entry`, `withRecordedPrerequisites dotnetRoot pin entry` | pure | the SDK versions a recorded entry names (paths under `<dotnetRoot>/sdk/<v>/` of its command line and application files, its own SDK for an `SdkHost` apphost); the entry with one `dotnet-sdk` prerequisite per such version (an existing one keeps its `Pin`), none when it names nothing there. `Import.entryOf` and `rehash` apply it |
| `mapPaths`, `mapText` | pure | `Csc.mapPaths`/`Fsc.mapPaths`; `mapText` of the case plus the evaluation's property values and the prerequisites' `Pin` |
| `diffList`, `diff a b` | pure | the ordered diff; the full entry diff (section 4) |
| `format roots doc`, `parse roots text`, `read roots path` | pure | the file format |
| `load path`, `loadWith extra path` | recipes | read against the build's roots; `needFiles` the lock itself |
| `save path doc`, `saveWith extra path doc` | recipes | write against the build's roots; deliberately no `need` |
| `entry name doc`, `entryFor framework name doc` | pure | lookup by assembly or project name; `entry` fails when the name matches several frameworks |
| `Options` / `Options.Default` | record | `{ Run: RunOptions; FscRun: FscRunOptions; Restore: Restore.Options }` |
| `restoreRequest entries` | pure | every compiler, reference and analyzer path, plus the combined graph |
| `restore options doc` | recipe | `Restore.ensure` over a whole lock; fails on a problem |
| `isSdkCompiler path`, `withoutSdkCompilerHash entry` | reads the SDK root | whether a compiler lies under `$(DotnetRoot)/sdk/<v>/`; the entry with that compiler's hash cleared (`rehash` and every replay apply it) |
| `underCi` | recipe | whether the build runs under CI: the script variable `CI`, else the environment variable `CI` (section 2) |
| `compileWith options entry`, `compile entry`, `compileFrom lockPath options entry` | recipes | the replay (section 2): prerequisites (`unmetPrerequisitesAt`: under the `$(DotnetRoot)` the lock is read against, a script's extra root of that name or this machine's), the `HERMETIC=on` gate (message 10), restore with `Restore.resolve`d options; `compile` resolves the server first; `compileFrom` names the lock in message 10 (`Locks.compileAs`) |
| `recorded path unhashed entry` | recipe | the record step without the write: `needFiles`, prerequisites, the `HERMETIC=on` gate, `rehash`; the references in `unhashed` (project-reference outputs) stay without a hash (`Locks.generate`) |
| `record path c` | recipe | `c: Compilation`; `needFiles` what it hashes, derive the prerequisites (warn without an exact pin), the `HERMETIC=on` gate (messages 2, 9), rehash, overwrite a one-entry lock; compiles nothing |
| `verify path c` | recipe | `diff (entry c.Name doc) (ofCompilation c with its prerequisites)`; writes and compiles nothing |
| `buildWith options path c`, `build path c` | recipes | compare `c` with the lock, then record, fail or compile (section 2, "Lock strictness and CI") |
| `lock "path"` | builder operation | `CscLockBuilder`: `Csc.ofSettings`, `Csc.runOptions`, `Lock.buildWith`; `FscLockBuilder`: the same with `Fsc.ofSettings`, `Fsc.runOptions` |
| `nofetch` | builder operation, after `lock` | `Restore.Options.Enabled = false` (was `norestore`), the per-target form of `NUGET_FETCH=off`; `packageroot` was removed in 0.2, the folder is the build's (`NUGET_PACKAGES`) |

`Project`:

| Name | Kind | What it does |
|---|---|---|
| `sdkPin projectDir` | reads disk | the `global.json` pin, searched upwards (`Lock.sdkPinAt`); only `rollForward: "disable"` is `Pinned` |
| `ImportOptions` / `ImportOptions.Default` | record | `Projects`, `Frameworks`, `Configuration` (default `Release`), `Properties`, `Variant`, `Output`, `Roots` |
| `import options` | recipe | per project: one restore without `TargetFramework` and with `RestoreRecursive=false`, then per framework a design-time build and a `-pp` preprocess; msbuild gets the build's package folder (`NUGET_PACKAGES`) when it is the script variable's; under `HERMETIC=on` an SDK not pinned exactly (message 11) or not installed (message 7) and the record gate (messages 2, 9) fail before anything is written; writes one lock |

Internals of `Project` that tests use: `withProjectLock` (one `Resource` per project path, so two
variants cannot restore over each other's `obj/project.assets.json`), `frameworksToImport` (only
the frameworks the project declares), `parseImports`, `parseImport`.

`Verify`: `authenticodeHash path`, `compare a b` (labelled `Difference` list), `verdict diffs`;
the PE `Layout` and `layout` are internal.

`StrongName`: `checksum`, `stamp`, `readSnk`, `sign`, `verify`, `signFile`, `normalise`.

`Pack`: `Entry`, `Options` / `Options.Default`, `zip`, `nupkg`, `list`.

`Description`, `Described`, `Locks`, `Import`: section 5, "Project descriptions".

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
      "Dependencies": { "Compiler", "References", "Analyzers", "Packages", "Prerequisites" (only when not empty) }
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
| | `Prerequisites` | `{ Kind; Version; Pin }` on one line each; the key only when the list is not empty, so a lock without prerequisites is byte-identical to one written before the key existed |

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

Both producers check the round trip. `Project.parseImport` fails with a diff when `entry.Args`
is not msbuild's absolute argument list, and nothing is written. `Csc.ofSettings` does the same
for the composed list. Two non-contiguous `/define:` switches are the one input that fails by
design; msbuild emits one.

### Tokenization

Paths are written with forward slashes and a root replaced by a token, so the file is the same
on every machine and a diff means a change of inputs.

| Token | Root |
|---|---|
| `$(NuGetPackageRoot)` | inside a recipe the build's folder (`DotNetFwk.packageRoot ()`: the script variable `NUGET_PACKAGES`, else the environment's); outside one `DotNetFwk.nugetRoot ()`; or a folder via `Roots.packageRootOverride` |
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
| `Hash.sha256` (base) | throws | a verification, which must never produce `""` by accident |
| `Csc.sha256` (base) | `""` | everything the lock records: import, `rehash`, the runner's check |

An empty `Sha256` in a lock means "not computed", never "zero bytes". The runner skips it, and
`Lock.diff` compares two hashes only when both are non-empty. That is why a composed compilation
(`Csc.ofSettings` never hashes) can be diffed against a recorded, hashed lock on every build
without reporting every reference. Imported project references are unhashed by design: they
point at the referenced project's own output, not built at import time.

Hashes are lowercase hex SHA-256. Package hashes are NuGet's base64 SHA-512 as found in
`.nupkg.metadata`.

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
calls `Lock.compile`. A lock that `Locks.generate` writes needs no remap: it records the path the
script's layout gives (`outOf`), without a hash (section 5). The maintainers' import script on the feature branch is the worked
example (the guide, section 6, shows the pattern); section 8 has the caveat about keying the
remap on the empty hash.

### Packages

`Lock.packagesOf` builds the graph at import time, inside the project lock, right after the
design-time build: every package of the framework's target in `project.assets.json` (`type:
project` entries excluded), the SHA-512 from the cache, `Direct` from the project's own framework
section, and `DependsOn` from the graph edges. The runner never reads it; `Restore` uses it for
the original id casing and the nupkg hash.

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

- A lock changes when the SDK changes (the compiler path; an SDK compiler carries no hash, being ReadyToRun code for one OS/architecture). An entry that depends on the
  SDK records it as a prerequisite under an exact pin; without one, the recording warns.
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
- With `Enabled = false` (`nofetch`) nothing is fetched; a warning names the packages (`fetching is
  off (nofetch / NUGET_FETCH=off)`), and the runner reports every missing file with its expected
  hash.
- The folder used to read the lock and the folder restored into must be the same:
  `Restore.into dir` and `Lock.loadWith (Roots.packageRootOverride (Restore.packageRoot o))`.

Tests (`RestoreTests`): missing-set grouping and casing; the restore project lists every package
at an exact version; `verify` reports an absent package and a wrong SHA-512; `into` is relative
to the project root; integration: a real restore into a scratch folder then a compile, restore
off reports instead of fetching, a wrong recorded SHA-512 fails by name.

### SBOM and signing

SBOM and signing: to be redone (0.4).

### Project descriptions (`xake.project.json`)

This part is the maintainers' view: where the code is and why it has this shape. The user pages
are [hermetic/description.md](hermetic/description.md) (the model and the import),
[hermetic/lock.md](hermetic/lock.md#xake-generated-locks) (lock generation and the build from
locks) and [fingerprints.md](fingerprints.md).

The flow has four steps. Each step is an explicit call in the script:

```
project file --Import.descriptions--> xake.project.json --Locks.generate--> lock --Locks.compileAs--> dll
   (msbuild evaluation)               (committed)      (dotnet restore,      (committed)  (no msbuild)
                                                         csc/fsc { project })
```

#### The model

`Description` (`src/hermetic/Description.fs`) holds the model: `Description.Project` and its
parts. A description is data only. It says what a project is: name, language, frameworks,
sources, references, packages, options and package metadata. It has no placeholders. A variant
that msbuild evaluates differently (a brand) gets its own description. Configurations, the
output layout and versions belong to the script. The schema is
`docs/schemas/xake.project.schema.json`. `Description.format` writes a fixed text form, so a
description changes only when its content changes. `Description.load` reads a file and makes it a
dependency of the target. `Description.FileName` is `xake.project.json`.

The model is data and needs no compiler code. That is why the roadmap moves it into the core
(see below).

#### The compile: `csc/fsc { project "path"; config "Release" }`

`Described` (`src/hermetic/Described.fs`) turns a description into settings. Since 0.4 (Xake
3.9) `project` is the core's setting (`CscSettingsType.Project`); this package's `config`,
`packages` and `lock` are type extensions of the base builders (`CscDescribedBuilder`,
`FscDescribedBuilder`, `CscLockBuilder`), the same technique as before. The block's other
operations stay in effect: `targetfwk` selects one framework of the description, `out` sets the
output, and `ref` passes the outputs of project references. After `config`/`packages` come
`config`, `packages`, `lock`, `nofetch` and `resolve`; `lock` may follow `project` directly.

- `Described.csc` and `Described.fsc` end in `Csc.ofSettings` and `Fsc.ofSettings`. So a
  description goes through the same composition, round-trip check and `HERMETIC=on` gate as any
  other block. They add `/deterministic+` (`--deterministic+`) and a `/pathmap` of the project
  root.
- `config` applies the SDK's preset (`Described.preset`). The preset adds the defines and the
  debug and optimize switches that `dotnet build -c Debug|Release` passes. Only `Debug` and
  `Release` are known; any other name fails.
- `packages` takes a `Described.Restored`. The compile does not resolve packages. A description
  with packages and no `packages` operation fails. `Described.restore` resolves them: it runs
  `dotnet restore` on a synthesized project into the build's package folder and reads
  `project.assets.json`. Package resolution stays with NuGet; that is the design decision.
- C# sources stay masks (msbuild's globs). The resolved `Csc` and its lock keep the masks.
  `Described.expandCsc` expands them at compile time through `getFiles`, so an added `.cs` file
  rebuilds the target and does not change the lock.

#### The import: `Import.descriptions`

`Import` (`src/hermetic/Import.fs`) writes descriptions from project files.
`Import.descriptions projects descriptionOf globals` evaluates each project with msbuild under
the global properties `globals`. A brand is one set of `globals` and one `descriptionOf` (for
example a brand folder). msbuild evaluates once without a framework and once per framework. No
target runs, and nothing is restored. `Description.write` writes the file only when the content
changes. The result `Import.Imported` lists the msbuild files the evaluation read (`Inputs`, for
fingerprints) and what has no field in the description (`Dropped`).

The import records no input. It is a command, not a rule: a rule would run again on every fresh
clone, because a fresh clone has no `.xake` database. Import shares `Project.runMsbuild` and
`Project.parseImports` with `Project.import`. Unlike `Project.import`, it produces a description,
not a lock.

#### The lock: `Locks.generate`

`Locks` (`src/hermetic/Locks.fs`) writes and builds the locks of descriptions. It is a module of
its own because it uses `Described`, which compiles after `Lock`.

`Locks.generate lockOf outOf config descriptionPath` writes one lock for one project and one
configuration. The lock has one entry per framework of the description. The script calls it from
a phony rule, for example `lock::(name:*)::(cfg:*)`. That rule `need`s the lock rules of the
project references first. A phony target `update-locks` needs all lock rules. Generation is
explicit for the same reason as the import.

- Each entry comes from `csc/fsc { targetfwk; out; ref; project; config; packages; resolve }`.
  So the lock is what the composed task would compile. `CSC_TOOLSET` applies to C# entries.
- The entry has no `/out:` (`-o`). The output path belongs to the build (`Locks.compileAs`).
- Packages come from `Described.restore`. A version that the current lock holds is passed as an
  exact version, so it does not float. `LOCK_REFRESH=<ids|*>` lets the named packages resolve
  again.
- A project reference gets a configuration (`Locks.refConfig`: the same, else `Release`, else
  `Debug`) and a framework (`Locks.nearestFramework`). The lock records the reference as the
  path of its output (`outOf`), without a hash (`Lock.recorded`).
- The file is written only when its text changes. The lock format is the same as before 0.3.

#### The build: `Locks.needAll` and `Locks.compileAs`

`Locks.needAll descriptions cfg lockOf outOf` needs the output `.dll` of every framework of
every description. A missing lock fails and names `update-locks`. Independent targets build in
parallel.

`Locks.compileAs lockPath fwk versions` builds one entry inside the file rule of the `.dll`. It
takes the output from the rule's target (`getTargetFile`) and inserts `/out:` (`--out:`). It
needs the outputs of the project references first. It expands the C# masks. It replaces a
`$(Version)` token in `Generated` with the version of the first matching mask in `versions`.
Then it calls `Lock.compileFrom`. `compileAs` does not compare the lock with the description
(section 2, "Lock strictness and CI").

#### Fingerprints

`Fingerprints` (`src/core/Fingerprints.fs`) is in the core package `Xake`, not in this package.
It is language-neutral. It records, in one committed file (`xake.fingerprints` by convention),
how each committed generated file was produced: the file's hash, the hash of its inputs, and the
inputs. `Fingerprints.record` adds a line. `Fingerprints.warn` reports a stale, hand-edited or
missing file and continues. `Fingerprints.check` fails. The design decision: a build warns and
uses the committed files, the same as CI. It does not regenerate them.

#### Planned

- The roadmap moves the descriptions (`xake.project.json`, `Description`, `Import`) into the
  core package `Xake`. The description is language-neutral data.
- A built-in Xake CLI command that writes `xake.project.json` from project files is a roadmap
  idea.

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
  `CscServerTests`, 36/36 files on the repo-a fixture).
- **Which compiler compiles.** The client finds the server in its own directory, and the default
  pipe name is derived from that directory, so another SDK or toolset version gets its own server.
  The request carries the client's commit hash; a mismatching server makes the client compile
  in-process. So the server is the same Roslyn build as the `csc.dll` the lock names. The lock
  names that file by its hash, or, for the SDK's `csc.dll`, by the SDK version (since 0.3).

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
a target's own `noserver` applies. `Locks.compileAs` also resolves the server with
`CompilerServer.resolve None`. `RunOptions.Default.Server` is `fromEnvironment ()`, which does
not read `CSC_SERVER`. fsc has no server: `Fsc.run` adds no client switches
(`CompilerRunner.RunPlan.ClientSwitches` is empty). Measured gains: about 25 percent of wall clock on a 12-assembly build,
about 3x per small compile. Verified on macOS only. Details: [csc-server.md](csc-server.md).

## 7. Evidence

### The repo-a verification

The working notes record an end-to-end run on a commercial C# library repository (called
*repo-a* in the notes: three shipped libraries, two brands selected by an msbuild property,
`netstandard2.0;net472`, central package management, strong-name keys, twelve assemblies in
total). The fixture is a `git archive` copy; the real checkout is never touched. The script
imports one lock per brand (both frameworks inside), builds all twelve assemblies from the
locks, builds the msbuild baseline in the same directory with the same `IntermediateOutputPath`
and `-t:Rebuild`, and compares.

| Check | Result |
|---|---|
| byte-identical to `dotnet build` (dll, pdb, xml) | **36/36** |
| deterministic: `obj/xake` deleted, compiled again | 36/36 |
| built against a freshly restored package folder | 36/36 identical to the build against the machine cache |
| with the compiler server on (SDK 8.0.425) | 36/36 in each byte check above; `/shared` in all 12 traced command lines |
| after the `Csc` record split (B2) and after the extraction (E1 to E4) | 36/36 in each byte check above |
| the repo-a locks, old writer vs new writer | identical (`cmp`) |

Behaviour observed on the same fixture:

- A build from committed locks starts no msbuild and no restore (`[msbuild]` count 0 in the log).
- An empty package folder triggers exactly one restore for the whole run, then all twelve
  compiles; with fetching off, the build stops before the compiler listing every missing file.
- A tampered reference stops the build with `expected <sha>, got <sha>`, all mismatches at once.
- Timings (macOS, 8 cores): importing both locks 13.4 s (30 msbuild runs); a cold build 7.3 s;
  a no-op 1.1 s wall (0.12 s engine). With the compiler server, the build phase median was
  3.43 s against 4.44 s in-process.

The matrix also found a defect: two frameworks of one project imported concurrently raced on
`obj/project.assets.json`. The fix is structural: one import per variant covers every framework,
one restore per project without `TargetFramework` and with `RestoreRecursive=false`, and a
`Resource` per project path.

A second, larger fixture (12 libraries plus a cross-repository reference into the first) was
90/90 byte-identical on 2026-09-24. (The notes disagree on whether the 90/90 run used the split lock format.) Its byte comparison cannot currently be re-run in a tree Xake has built in
(section 8).

A shipped, signed release of one repo-a library was also compared with a local build of the
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

`src/tests` (base) does not reference the hermetic package; `src/hermetic.tests` references
`src/hermetic` and gets `Xake`/`Xake.Dotnet` from the published package. At the last recorded
run: base 258 passed and 1 skipped, hermetic 118 passed, 0 warnings in both libraries.
`build.fsx` runs both test projects and checks that the packed nuspec depends on `Xake`.

### Verified on macOS only

| Claim | Status |
|---|---|
| repo-a 36/36 and the larger fixture 90/90 | macOS, SDK 8.0.423 and 8.0.425 |
| compiler server behaviour and gains | macOS; the `ps` assertion in `CscServerTests` is Unix-only |
| the `resx` `.resources` byte identity | the netstandard2.0 code path only (tests run on net8.0) |
| the net462 build of the library itself | compiles; its `Impl.compileResx` path (`ResXResourceReader`) is not exercised |
| Windows: registry-located .NET Framework, case-insensitive paths | not exercised by these runs |
| the CI workflows (`publish.yml`, `build.yml`) | the tag-to-package step was run locally; the workflows themselves have not run |

## 8. Known limitations and open items

| Item | Detail |
|---|---|
| The F# compiler is the SDK's | done in Xake 3.5 / hermetic 0.2: `Fsc.run` shares the runner with `Csc.run`, F# entries are in the lock, `Project.import` reads an fsproj, `Fsproj` is gone. What remains: no package carries a current F# compiler, so an F# lock names `<sdk>/FSharp/fsc.dll` and is reproducible only under an exact SDK pin. Since 0.3 that compiler has no hash in the lock: the SDK version identifies it, so one lock builds on every OS and architecture that has that SDK; `FscArgs` does not treat `--embed:` as a path switch, so an imported fsproj keeps msbuild's relative `--embed:obj/...` values (they resolve against the entry's `Directory`, and the output is byte-identical) |
| The net462 resx path | `Impl.compileResx` has two bodies on purpose: net462 uses `ResXResourceReader` (typed values, file refs), netstandard2.0 uses `Resx.compile`, which reads plain string values only and throws on typed entries. The runner calls `Resx.compile` for a missing `.resources`. No byte-identity claim is made for net462 |
| The published base as a dependency | `src/hermetic` takes `Xake` as a PackageReference `[3.7.0.26, 3.8)`, so it compiles against the assemblies on nuget.org, not the `src/core`/`src/dotnet` of the same checkout, and the nuspec range is the reference's own (no pack-time rewriting). The lower bound must be a version that exists: the release run number makes 3.7.0 `3.7.0.26`, and `[3.7.0, 3.8)` warns NU1603 on every restore. The upper bound is the next minor: the `Csc`/`Fsc` records gain fields in base minors, and F# records are not binary-compatible across that (3.6 added `RuntimeConfig`), so each base minor gets a release of this package. A change the hermetic package needs from the base is released first; until then it is developed against a locally packed base passed as an extra restore source (`NUGET_SOURCE`) |
| The fsx bootstrap | on this branch `build.fsx` bootstraps from `#r "nuget: Xake, 3.4.0.21"`, and `build.fsc.fsx` from that and `#r "nuget: Xake.Hermetic.Dotnet, 0.1.0.22"` (exact published versions); `build.fsx` stays on the base package only. #50 reworks `build.fsc.fsx` to `#r` the released 3.7 and 0.3 packages and to build from descriptions and locks. A fix in `src/hermetic` reaches `build.fsc.fsx` only after a release and a bump, or through a temporary `#r` on a copy of the built dlls (docs/devprocess.md) |
| The target framework of a composed block | `Csc.ofSettings` takes `targetfwk`, else the script variable `NETFX-TARGET`, else the .NET of the SDK the build runs on. It fails only when none of them gives a framework (the Windows `csc.rsp` fallback is gone). An unknown .NET Framework moniker fails with "not a known .NET Framework profile" |
| A locally built reference in `csc { lock }` | `Csc.run` checks hashes after `needFiles (CscArgs.inputs args)`, so a reference another rule of the script produces is rebuilt first and the check sees the bytes the compiler reads. If such a reference was recorded with a hash (`Lock.record` after it exists), any change to it is then a hash mismatch on the next build that rebuilds it. The imported-lock pattern avoids it by mapping project references (which drops the hash) and `need`ing them first |
| New source files and the gate | a composed `csc {}` expands its filesets without recording them as a dependency, so a new file matching the glob does not rerun the target; the lock gate only fires when something else changes (observed; base behaviour) |
| `Lock.build` and `Lock.verify` look up by name | they use `Lock.entry c.Name`, which fails when a lock holds the name for several frameworks. Composed locks hold one entry, so this only matters for a hand-merged lock |
| The one-call build of an imported lock | the extraction plan sketched `Lock.build lockFile framework name` and a `Lock.buildWith { RunOptions; ExtraRoots; OutputOf }` that would also map project references. Not built. `Lock.build` takes a composed compilation: it compares the compilation with the lock and compiles the lock entry only when the two are equal. A lock from `Project.import` is built with `Lock.load`, `Lock.entryFor`, `Lock.mapPaths`, `need`, `Lock.compile`. A lock from `Locks.generate` is built with `Locks.compileAs`, one entry per call (section 5) |
| Project-reference remap heuristic | keying the remap on "recorded hash is empty" breaks when a stale `bin/Release` output existed at import time (it gets hashed). `the repo-a verification script` keys on the name matching another entry of the same framework; `import.fsx` still uses the empty-hash key |
| MSB3577 interop | on a project with two same-named `.resx`, the msbuild baseline fails in a tree where Xake wrote `.resources` into msbuild's `obj/xake/...` paths. Likely fix: write imported `.resources` under a path Xake owns; the manifest name, not the path, is embedded |
| `Generated` / `.resources` are not engine targets | the runner writes them inline; a rule factory would make them first-class targets (review item, waits for a second consumer) |
| Restore scope | only what the compilation reads is restored (compiler, references, analyzers), not the whole package graph; `csc { toolset }` and `CSC_TOOLSET` restore the compiler package into the build's package folder (`DotNetFwk.packageRoot`) |
| Package integrity | the nupkg is not re-hashed; the check trusts `.nupkg.metadata`. The per-file SHA-256 is the real gate |
| `StrongName.signFile` | re-signs but does not recompute `CheckSum`; use `normalise`, or `checksum` after it |
| `Verify.verdict` and signer padding | a certificate table must start 8-byte aligned; when the unsigned file is not, the padding shows as a short `Content` range and the verdict says "content differs". PE files are 512-byte aligned in practice |
| Test-order fragility | `DotNetFwk.locateFramework` memoizes process-wide while one fixture points `NUGET_PACKAGES` at a scratch folder; worked around in `RestoreTests`, not fixed |
| Not built | `Sln` parsing, `Policy` gates, obfuscator task, signature validation, provenance statements |
| SBOM and signing | to be redone (0.4) |

## 9. Release and versioning

| | `Xake` | `Xake.Hermetic.Dotnet` |
|---|---|---|
| Project | `src/dotnet/Xake.Dotnet.fsproj` (`PackageId=Xake`, bundles `Xake.dll` from `src/core`) | `src/hermetic/Xake.Hermetic.Dotnet.fsproj` |
| Namespace | `Xake`, `Xake.Dotnet` | `Xake.Hermetic.Dotnet` |
| Targets | `net462;netstandard2.0` | `net462;netstandard2.0` |
| FSharp.Core floor | 8.0.100 | 8.0.100 |
| First version | 3.4.0 | 0.1.0 |
| Version source | `/p:Version` from `build.fsx` or CI (`VERSION`) | fsproj `<Version>0.3.0</Version>` (this PR), overridden by `HERMETIC_VERSION` |
| Tag | `v<X.Y.Z>` | `hermetic-v<X.Y.Z>` |
| Dependencies | FSharp.Core; Microsoft.Win32.Registry on netstandard2.0 | `Xake [3.7.0.26, 3.8)` (PackageReference on the published package), FSharp.Core |

The versions are independent. `Xake` stays semantic-versioned 3.x. `Xake.Hermetic.Dotnet` starts
at 0.1.0, a preview: its API may break while it is 0.x. The range is one base minor:
`[3.7.0.26, 3.8)` for 0.3. The lower bound is the exact published 3.7.0, and the upper bound is
the next minor, because F# records are not binary-compatible when a base minor adds a field
(section 8). The range works only because the hermetic package uses no internals of the base.
The base names it relies on (`Csc`, `Fsc`, `RunOptions`, `Csc.run`, `Csc.ofSettings`, `resolve`,
`CSC_SERVER`, `CSC_TOOLSET`, `Hash`, `Tool`, the `DotNetFwk` functions) are breaking to change.
Lockstep versions would have made CI simpler but forced a `Xake` release for every
hermetic-only change.

The build: `build.fsx` has a third library, built like the other two; it needs none of their
outputs, since it compiles against the restored `Xake` package. Packages go to
`out/pkg/<id>/<id>.<version>.nupkg`, one rule per package, and the two packs are independent:
the hermetic one restores from nuget.org (plus `NUGET_SOURCE` when set), packs, and checks that
the nuspec depends on `Xake` and not on `Xake.Dotnet`. `publish.yml` maps the tag prefix to the
package and the version variable, appends the run number as the fourth component, runs
`build test pack` for `v*` and `build test pack-hermetic` for `hermetic-v*` (the base is not
packed), and pushes only the tagged package with `--skip-duplicate`. `build.yml` runs
`build test pack` on SDK 8.0.x and 10.0.x.

Order of release: `Xake` first, then `Xake.Hermetic.Dotnet` against the published version.
`Xake` 3.7.0 is out as `3.7.0.26`, the floor of the 0.3 range. This PR makes
`Xake.Hermetic.Dotnet` 0.3.0. `Xake` 3.4.0 (`3.4.0.21`) and `Xake.Hermetic.Dotnet` 0.1.0
(`0.1.0.22`) are still the bootstrap of the fsx scripts (section 8).

Before the first hermetic release every hermetic name is free to change. After it, names may
still change while the package is 0.x; each change should be listed in the release notes.
