# Session notes

## Hermetic build script (branch `feature/hermetic-build`)

`build.fsx` is untouched and still the build of record. Next to it, `build.fsc.fsx` compiles both
assemblies with the `fsc` task — msbuild compiles nothing — and is meant to replace it after the
next release.

**The division of labour**: msbuild is the only thing that reads a project file correctly, so it
is asked, once per project, what to compile, what to reference and what to define; the answer is
cached in a file rule; the compilation itself is the `fsc` task's, driven by explicit arguments.

- **`Fsproj.evaluate`** (`src/dotnet/Fsproj.fs`) runs
  `dotnet msbuild -restore -t:PrepareForBuild;GenerateAssemblyInfo;ResolveReferences` with
  `-getItem`/`-getProperty` and writes msbuild's json to a file. Every part of that target list
  earns its place: `PrepareForBuild` triggers `AddImplicitDefineConstants` (that is where
  `NETSTANDARD2_0` and the `_OR_GREATER` chain come from), `GenerateAssemblyInfo` writes the
  attributes file, `ResolveReferences` produces the reference list. `BuildProjectReferences=false`
  is not optional: without it msbuild builds the referenced project, which is the thing being
  avoided.
  What msbuild writes is *dumped*, not kept: every metadata field of every item, 200 KB and 3600
  lines per project of which one field is read. `evaluate` rewrites it into the ~15 KB of lists
  the build actually consumes (`Fsproj.write`), with paths written against `$(NuGetPackageRoot)`
  and `$(ProjectRoot)`.
- **`Fsproj.parse`** reads that kept form (`parseEvaluation` reads msbuild's own). Sources are `CompileBefore @ Compile @ CompileAfter` — for
  F# the SDK puts the generated `AssemblyInfo.fs` in **`CompileBefore`** (see
  `FSharp/Microsoft.FSharp.Overrides.NetSdk.targets`), not `Compile`, so it lands first, which is
  what makes `InternalsVisibleTo("Xake.Dotnet")` reach the compiler. The json parser is
  hand-written: `System.Text.Json` is a package dependency on netstandard2.0 and would land on
  every consumer of Xake.
- **The kept evaluation is tracked in git** (`projects/<fwk>/<lib>.json`) and behaves as a
  lockfile for the compilation: because of the tokens, a regeneration on another machine is
  byte-identical, so a diff there means the project really changed. It is produced by a plain
  file rule that depends on the `.fsproj` and (through the recipe) on the `Version` var. Second
  build: 38 ms, msbuild not started. Touch a source file: recompiled, msbuild still not started.
  `clean` does not touch it.
- **`ReferencePath` points a project reference at that project's own `bin/`**, so the script
  filters those out and substitutes its own `out/<fwk>/<name>.dll`. It does *not* `need` them:
  the `fsc` task already `needFiles` everything it references, and a target asked for twice in
  one build is built twice (the WorkerPool dedup limitation below). That one redundant `need`
  cost 3 of the 7 seconds a clean build took.
- **The `fsc` task stayed thin** — the whole diff against `dev` is `doc`, netstandard targeting
  (`DotNetFwk.sdkImpl`, see docs/dotnet-build.md) and a `define` fix: fsc reads `--define:A;B` as
  one symbol named `A;B`, so the task emits one switch per symbol.

Where the time goes, measured with everything cold (`obj/`, `bin/`, `out/`, `.xake` removed):

| | `build.fsx` (dotnet build) | `build.fsc.fsx` |
|---|---|---|
| everything cold | 4.4 s | 4.6 s |
| only `out/` removed | 1.9 s — msbuild's `obj/` is still warm, so it copies rather than compiles | 4.7 s — it has no such cache, it recompiles |
| nothing changed | 0.7 s | 0.7 s |

The middle row is the whole of the difference: msbuild keeps its own incremental state in `obj/`
and `bin/`, and `dotnet build --output out` then degenerates into a copy. Comparing that against
a real compilation is what made the fsc build look twice as slow.

Traps worth remembering:

- A list expression that mixes literals with a `for` comprehension turns the literals into
  statements and **silently drops them** (`FS0020`); the msbuild command line lost every flag
  that way. Use `@` between lists, or `yield` on every element.
- A triple-quoted string whose closing `"""` sits left of the enclosing offside line breaks the
  parse of everything after it (`FS0010: Unexpected identifier in member definition`). Indent the
  literal into the block.

**Bootstrapping it** needs a *frozen copy* of the assemblies — the script overwrites `out/`, and
overwriting an assembly fsi has loaded kills the run with a `BadImageFormatException`:

```bash
dotnet fsi build.fsx -- -- build
mkdir -p .bootstrap && cp out/netstandard2.0/*.dll .bootstrap/
dotnet fsi build.fsc.fsx -- -- build test
```

After the release both `#r` lines become `#r "nuget: Xake, <version>"` and the staging goes away.
`dotnet test` and `dotnet pack` still shell out to the SDK: the test project is msbuild's, and the
nupkg carries a `net462` asset fsc cannot produce here (see the limitation below).

## Release prep (branch `feature/rulesless-syntax`)

Preparing the first release that ships `Xake.Dotnet` inside the `Xake` package. What was
touched, so it is not re-litigated:

- [`docs/dotnet-build.md`](dotnet-build.md) is the reference for toolchain discovery: the three
  providers (`sdkImpl`, `msImpl`, `monoFwkImpl`), the fallback chain, framework-name
  normalization, and the `NETFX`/`NETFX-TARGET`/`FSCVER` variables. Read it before touching
  `DotNetFwk.fs`.
- The nupkg had **no license metadata at all** and a deprecated `PackageIconUrl`. It now
  declares `MIT`, packs `Icon.png` and `readme.md`, and packs with zero warnings. The readme is
  referenced as `readme.md` — that is the git-tracked casing, and `README.md` would break the
  pack on a case-sensitive CI filesystem.
- The published version is *not* the tag: `publish.yml` appends the run number, so `v3.3.0`
  publishes as `3.3.0.<run>`.
- `FSharp.Core` in the nupkg is whatever the SDK pins — currently `10.1.400`. Bumping the SDK
  raises the floor for every consumer. Pin it explicitly in the projects if that matters.
- The mono provider built its `PATH` env var as `sdkroot </> ("bin" + ";" + PATH)` — `+` binds
  tighter than `</>`, and `;` is not the Unix separator. Fixed, but the mono path has no test
  coverage.
- `build.fsx`'s `#r "nuget: Xake, 3.0.1"` bootstrap is deliberately behind the release; bump it
  only after a release lands on nuget.org. Same for `samples/gettingstarted.fsx`, which
  references the published package and therefore cannot be run until this release is out.
- **The supported floor is .NET 8**, enforced in three places (`global.json`, the tests TFM, the
  CI matrix) and by an explicit `FSharp.Core` 8.0.100 pin with
  `DisableImplicitFSharpCoreReference`. Without the pin the SDK's own FSharp.Core lands in the
  nuspec and every consumer inherits it. See docs/devprocess.md.
- `builder {}` (the empty settings block) does **not** compile on F# 8 — `builder { () }` does,
  and is what the docs, samples and tests use.
- `csc` with no `targetfwk` passes no framework references at all, so it only ever worked on
  Windows via `csc.rsp`. Every documented `csc` sample now sets `targetfwk`. Making the default
  probe supply references too would be a real fix, and is not done.
- `samples/features.fsx` had a catch-all `"(dir:*)/(file:*).(ext:c*)"` rule declared *after*
  `temp/AssemblyInfo.cs`; since the last matching rule wins, it shadowed it and the sample had
  been failing for a long time. The catch-all now comes first.

## Earlier work: modernizing `src/dotnet`

`src/dotnet` (Xake.Dotnet) was merged in from a separate repository and had not caught up with
the engine. It has now been reworked to reuse the engine's APIs and match its style. What is
worth knowing before touching it again:

- **`src/dotnet` no longer contains engine code.** Its `ProcessExec.fs` and `CommonLib.fs`
  copies are gone; it uses the engine's via `InternalsVisibleTo("Xake.Dotnet")` declared in
  `src/core/Xake.fsproj`. If assembly signing is ever added, that attribute needs the public
  key or Xake.Dotnet silently loses access to the internals.
- **`pexecSync` exists for `DotNetFwk` only.** Framework and tool probing happens in plain
  memoized functions with no async context; everything inside a recipe uses the `shell`/`sh`
  task. `Async.RunSynchronously` is safe there (recipes run via `Async.StartAsTask` with no
  `SynchronizationContext`) but it holds a CPU slot, which is why it is confined to the
  memoized lookups.
- **`#if NET46` was dead code** -- the projects target `net462`, for which the SDK defines
  `NETFRAMEWORK`/`NET462`/`NET46_OR_GREATER` but *not* `NET46`. Use `NETFRAMEWORK`. Verify any
  new conditional empirically; a wrong one fails silently by compiling nothing.
- **resx compilation needs `System.Windows.Forms`** (`ResXResourceReader`), which is not one of
  the SDK's implicit net462 references. It is referenced explicitly for that TFM only.
- **Compiler diagnostics used to be invisible.** Roslyn writes them to stdout, which the old
  `_system` logged at `Verbose`, and `levelFromString` only recognized diagnostics carrying a
  source position -- so `error FS0084: ...` was missed too. Both are fixed; keep
  `Impl.levelFromString` covering both diagnostic shapes.

### Known limitations, not defects of this work

- **`fsc` cannot target full framework on macOS through the SDK.** It needs a
  net462-compatible `FSharp.Core`, and the SDK only supplies the netstandard2.0 one, which
  drags in a `netstandard` facade that the net4x reference-assembly packages do not carry.
  Hence there is no `fsc` end-to-end test; `samples/features.fsx` works around it by
  referencing an `FSharp.Core.dll` of its own.
- **A target requested twice in one run used to be built twice** — `need ["x"]` followed by
  `needFiles` on the same `x` rebuilt it. Fixed in `WorkerPool.fs`; what actually went wrong
  took two mechanisms, so it is worth writing down:
  - the pool deduped only *in-flight* requests, dropping the entry when the task finished, and
  - the "does it need rebuilding" verdict is memoized for the whole run
    (`getChangeReasons ctx |> memoizeRec`, `ExecCore.fs`) — the memo is how the recursive graph
    analysis ties its knot, not an optimization that can be dropped — so the second request did
    not ask the database again and got the pre-build "Not built yet" answer.

  The pool now keeps finished tasks, tagged with a run number: within a run a target executes
  once, across runs (a new group, another `Demand`) the database decides again. `Scheduler.newRun`
  marks the boundary and is posted exactly where the memo is created. Tasks still in flight are
  kept across that boundary, so overlapping demands keep collapsing into one. Covered by
  `builds a target requested twice in one run only once` and `builds a file needed and then
  needFiled only once`.
- `build.fsx` builds `netstandard2.0` only; the `net462` asset comes from `dotnet pack`. An
  fsc-built net462 leg would need an FSharp.Core with a net4x assembly, which the pinned
  package does not have.

## How to verify changes here

```bash
dotnet build src/core -c Release && dotnet build src/dotnet -c Release   # both TFMs
dotnet test src/tests                                                    # 233 passed, 1 skipped
dotnet test src/tests --filter 'Category=Integration'                    # real csc invocation
dotnet fsi build.fsx -- -- build test                                    # self-hosting, fsc only
dotnet fsi samples/fullframework.fsx                                     # end-to-end csc
```

`samples/*.fsx` reference `out/netstandard2.0/*.dll`, so run the self-hosting build first.

## Earlier work: delegated execution (merged, PR #15)

The delegated/distributed execution model (`Resource`, `runDetached`, `delegated`) is designed
in [`docs/delegated.md`](delegated.md). Two facts from that work still bite:

- Delegated rules are never bounded by the local CPU pool -- the engine runs executor and body
  detached, so `runDetached` inside a delegated body is redundant.
- `Scheduler.withYieldedSlot` assumes the caller holds exactly one CPU slot. Calling it without
  one releases a phantom permit and silently disables the `Threads` limit. Inside a
  `DelegatedExecutor` (already detached) use `Resource.acquire`/`withAcquired` instead.
