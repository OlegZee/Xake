# Session state: hermetic-build

Updated 2026-09-22 (late: import, invocation mode, byte-identical proof).

`brief.md` next to this file (not committed yet, do not stage) is the working brief:
positioning, decisions, the design of the library surface (§11), and the day-zero results on
the two ActiveReports repositories (§8j). Everything decided so far is there; do not re-litigate.

`lock-from-settings.md` analyses how a lock is obtained from composed `csc {}` settings (nine
scenarios; migration of an existing tuned block is §9; the update mechanism is an open question,
a global `UPDATE_LOCKS` variable was rejected). `csc-syntax.md` documents the `csc {}` task as it stands on this branch: composed settings,
`invocation` from a lock, one runner.

State: **slice 1's core is done and proven** — `Project.import`, `Lock`, `csc { invocation }`,
and `import.fsx build` compiles dataengine develop (3 projects × 2 brands) **byte-identical to
`dotnet build`**, 18/18 files (dll, pdb, xml). All uncommitted. The **one-resolved-form refactor of `csc` is done** (2026-09-22, late):
`Dotnet.csc.fs` is now `run` (the only runner: generated files, output dirs, hash check,
`needFiles` on `CscArgs.inputs`, rsp with `/noconfig` outside, compiler selection, `shell`),
`resolve` (the composed settings turned into a `Lock.Project` with the exact argument list the
old code produced, `/noconfig` first when the target framework asks for it), and `Csc` choosing
between `Invocation` and `resolve`. Env vars and resx temp files are `run` parameters, not lock
fields -- the lock's shape is the file format. Two behaviour changes for the composed mode,
both intended: it creates the output directory (before, `samples/fullframework.fsx` needed
`samples/temp/` to exist), and it `needFiles` the framework references too. Trap: `samples/*.fsx`
load Xake from `out/`, so run `dotnet fsi build.fsx -- -- build` before judging them; a stale
`out/` made the refactor look like it had not fixed the directory issue. **Next step**: the remaining
slice-1 items — the `Microsoft.Net.Compilers.Toolset` compiler source (today the lock names the
SDK's `csc.dll`; a project pinning the toolset package needs `CscToolPath`/`CscToolExe` honoured,
the import already reads them), the resgen recipe (dataengine has no `.resx`, page does), then
page with `-p:LocalBuild=true` (cross-repo project references; `$(ProjectRoot)` is cwd, so the
sibling repo will not tokenize — decide on a `$(Root)` covering both). Fixture as before:
`git archive origin/develop` of `~/Projects-work/ar/ar-net-core-dataengine` into the job tmp dir
(its checkout is on a broken feature branch), then `ar-net-core-page`. Always
`-p:NuGetAudit=false` (the import sets it) or load the feed token with `cd <ar project dir> &&
source ~/set-secrets.sh` (never print it). Xake stays referenced via `#r` on `.bootstrap/` — no
release.

### What landed (2026-09-22, late): `csc { invocation }` and the end-to-end proof

- **`compileFromLock`** (`Dotnet.csc.fs`), reached through `CscSettingsType.Invocation:
  Lock.Project option` / the `invocation` custom operation. Replays `project.Args` verbatim:
  writes back `Generated` files that are missing or differ (the lock is the source of truth),
  creates output directories, verifies SHA-256 of every hashed reference, analyzer and the
  compiler (all mismatches reported at once, "missing" when absent, then fails), `needFiles` the
  `CscArgs.inputs`, and runs the compiler: `cscpath` if set, else `dotnet <csc.dll>` when the
  lock's compiler path ends in `.dll`, else the path itself. `/noconfig` stays on the command
  line — inside an rsp csc warns CS2023 and ignores it. Everything else goes through the rsp
  with `Impl.escapeArgument`.
- **`Lock.mapPaths f project`** rewrites every path (Args, References, Analyzers, Generated keys);
  a reference whose path changed loses its hash. The script uses it to point the unhashed
  project references (`src/<Other>/bin/Release/...`, msbuild's own output) at the lock's own
  `/out` for that project, and `need`s those first — so the build order comes from the lock.
- **`import.fsx build`**: rule `src/(proj:*)/obj/xake/(fwk:*)/(brand:*)/(name:*).dll` reads the
  lock, maps the project references, `csc { invocation }`; `build` reads every lock and needs
  every `Output`. `need` takes paths relative to `ProjectRoot`, so the absolute `Output` is
  relativized with `Path.GetRelativePath`.
- **Proof**: clean `build` 4.6 s for 6 assemblies; no-op 50 ms; `touch` a VBFunctionLib source →
  6 dlls recompile (both brands, all dependents), locks untouched, 4.9 s. Then `dotnet build`
  in the *same* directory with the same `IntermediateOutputPath`, and `cmp`: **all 18 files
  identical**, generated inputs identical too. A first comparison against a baseline built in a
  sibling copy differed by a few hundred bytes per dll — csc embeds absolute source paths in
  the PDB and, via `/deterministic`, in the PE — which is why the baseline must be built in
  place (or both builds must use `/pathmap`). Worth remembering for the auditor mode: a
  reproduction on another machine needs `/pathmap` or the same checkout path.
- **Tests**: `CscInvocationTests.fs` (3, two Integration). Suite: 246 passed, 1 skipped. Both
  projects 0 warnings. `DotNetFwk.locateFramework (Some "netstandard2.0")` returns the SDK's
  native `csc` launcher, not `csc.dll`, so the test takes `csc.dll` next to it — and it is the
  *latest* SDK (10.0.401 here), while the imported lock names 8.0.425 via `global.json`.
- Inspectable artifacts in `samples/hermetic/dataengine/` (untracked): both locks, `brand-diff.txt`,
  `generated/`, `console.txt`, `compile-console.txt`, `compare.txt`. The dataengine source is
  not copied there.

### What landed (2026-09-22, afternoon): `Project.import`, `Lock`, `CscArgs`

All in `src/dotnet/Project.fs`, after `Fsproj.fs` (it reuses `Fsproj.Json`, `roots`, `expand`).

- **`CscArgs`** — the compiler command line as data. `parse` tells a switch from a source
  (an absolute Unix path also starts with `/`: a switch has no second slash before the colon);
  `shapes` knows which switches name files and how (`Path`, comma-separated `PathList` with
  optional `alias=`, `PathFirst` for `/resource:file,name`); `inputs`, `outputs`, `sources`,
  `switchValues`, `mapPaths`, `absolutize` (also folds `..`, so the SDK's
  `targets/../analyzers/x.dll` and its real path are one file). Both the import and the
  future invocation mode read arguments through it — keep the switch tables here only.
- **`Lock`** — `Lock.File = { Framework; Configuration; Properties; Projects }`, one file per
  (TFM, variant), one `Lock.Project` per project: `Args` verbatim with absolute paths,
  `References`/`Analyzers`/`Imports` as `{ Path; Sha256 }` (empty hash = did not exist at
  import, i.e. a project reference's output), `Compiler = { Tool; Path; Sha256; Sdk }`,
  `Generated` = the files msbuild wrote under the intermediate dir that are compiler inputs
  (AssemblyInfo.cs, TFM attributes, GeneratedMSBuildEditorConfig) *with content*, and a short
  `Properties` whitelist. `write`/`parse`/`read`/`project`. Sources and `/out` are computed
  members, not stored twice. `write` tokenizes with `tokenizeAll`, a regex replace of a root
  followed by `/ = , ;` or the end — a prefix replace missed `/pathmap:<root>=/_/`.
- **`Fsproj.roots ()` gained `$(DotnetRoot)`** (from `DotNetFwk.sdkImpl.dotnetRoot`, made
  internal): compiler and analyzer paths live there. The kept `projects/*.json` did not change
  shape (nothing of Xake's references the SDK dir).
- **`Project.import options`** — per project, two msbuild runs: the design-time build
  (`-restore -p:ProvideCommandLineArgs=true -p:SkipCompilerExecution=true
  -p:BuildProjectReferences=false -p:NuGetAudit=false -p:IntermediateOutputPath=obj/xake/<fwk>/<variant>/
  -t:PrepareResources;Compile -getItem:CscCommandLineArgs,ReferencePath,Analyzer,ProjectReference
  -getProperty:...`) and `-pp` for the imports. `parseImports` takes the path line above each
  `====` banner rule. Imports under `NetCoreRoot` are dropped (the SDK version stands for them),
  and so are those under `BaseIntermediateOutputPath` (restore's `nuget.g.props/targets`); the
  rest are `needFiles`'d, which is what makes a brand-props edit re-import that brand only.
  `ImportOptions.Variant` names the obj subtree; the brand goes there.
- **Measured on dataengine develop** (3 projects, 2 brands, from `import.fsx` in this folder,
  run with cwd = the repo copy so `$(ProjectRoot)` is that repo): 3 s for both locks cold,
  msbuild node reuse doing most of it; no-op 25 ms; `touch src/MESCIUS.props` re-imports the
  MESCIUS lock only. Each lock is 153 KB / 1135 lines, every path tokenized (`$(ProjectRoot)`,
  `$(NuGetPackageRoot)`, `$(DotnetRoot)`). The brand diff is 92 lines and is exactly the
  brand delta: assembly names, `/out`/`/doc`, the two project-reference paths, the generated
  files' paths and content. The `git archive` copy has no `.git`, so no `/sourcelink` or
  `/embed` appears — those show up on a real checkout (brief §8j trap 4).
- **Tests**: `src/tests/ProjectImportTests.fs`, 6 tests, no msbuild needed. Suite: 243 passed,
  1 skipped. Both projects build with 0 warnings.

**Lock stability (discussed 2026-09-22).** As written, the lock changes for three reasons: source
layout (every PR, that is its nature as a compile manifest), SDK patches (monthly, and per
developer under `latestFeature`), and — on a real checkout — every commit, because
`AssemblyInfo.cs` in `Generated` carries the commit sha. The user wants the file split (compiler,
sources, dependencies, framework apart); decided to **defer** until locks are diffed for real,
since the in-memory `Lock.Project` stays the unit and only `write`/`parse` would change. The
sha tokenization is due when the script first runs on a live repository. Items in tracker.md.
Two more decisions from the same discussion: `global.json` pins the exact SDK unconditionally, so
the `Compiler` section changing is a deliberate, reviewed event, not noise; and when the split
happens the lock should become **structured** (fields for sources, references, defines,
options) rather than the raw `csc` argument list — §8c's "do not reconstruct" stays honoured by
a round-trip check at import: the command line rebuilt from the structure must equal msbuild's.

Traps from this step:

- `dotnet test --filter` rejects a value with a space (`Name~"Project import"` fails in
  msbuild property parsing); use `Name~import`.
- A `let mutable` local cannot be captured inside a `recipe { for ... do! }` loop — use a
  `ResizeArray`.
- The design-time build's `IntermediateOutputPath` must end with `/`, and the generated files
  land under it, but restore's `nuget.g.*` land under `BaseIntermediateOutputPath` (`obj/`),
  so filtering on the intermediate dir alone keeps them.

## Branch notes: the fsc-based build script (moved from docs/session.md)

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
  the `fsc` task already `needFiles` everything it references, and asking once is the way to
  write it. Back when a second request rebuilt the target, that one redundant `need` cost 3 of
  the 7 seconds a clean build took — which is what uncovered the engine bug below.
- **The `fsc` task stayed thin** — the whole diff against `dev` is `doc`, netstandard targeting
  (`DotNetFwk.sdkImpl`, see docs/dotnet-build.md) and a `define` fix: fsc reads `--define:A;B` as
  one symbol named `A;B`, so the task emits one switch per symbol.
- **One target, one execution per run** (`WorkerPool.fs`). A target asked for a second time
  after the first request had finished used to be built again, and it took two mechanisms to
  get there: the pool deduped only *in-flight* requests (dropping the entry on completion), and
  the "does it need rebuilding" verdict is memoized for the whole run
  (`getChangeReasons ctx |> memoizeRec`, `ExecCore.fs`) — that memo is how the recursive graph
  analysis ties its knot, not an optimization one can drop — so the second request never asked
  the database again and got the pre-build "Not built yet" answer.

  The pool now keeps two maps: `running`, shared by whoever asks whenever they ask, and
  `finished`, which serves the rest of the run and is emptied when the next one starts.
  `Scheduler.newRun` marks that boundary and is posted exactly where the memo is created — in
  `runBuild.runTargets` (a target group) and in `demandTarget` (one `Demand`). So within a run a
  target executes once; across runs the database decides again, which is what makes a second
  `Demand` notice a changed variable; and a task already going when a run begins still serves
  it, as it always did for concurrent demands. A run-numbered variant was tried first and
  dropped: same size, more to explain. Covered by `builds a target requested twice in one run
  only once` and `builds a file needed and then needFiled only once`.

Where the time goes, measured with everything cold (`obj/`, `bin/`, `out/`, `.xake` removed):

| | `build.fsx` (dotnet build) | `build.fsc.fsx` |
|---|---|---|
| everything cold | 4.6 s | 4.9 s |
| only `out/` removed | 2.1 s — msbuild's `obj/` is still warm, so it copies rather than compiles | 4.9 s — it has no such cache, it recompiles |
| nothing changed | 0.7 s | 0.7 s |
| a source file touched | — | 4.0 s, no msbuild run |
| a `.fsproj` touched | — | 4.5 s, one msbuild run, `projects/` unchanged |

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

## How to verify the fsc build

```bash
dotnet fsi build.fsx -- -- build                                         # dotnet build, produces out/
# and the fsc build, which needs its bootstrap staged first
mkdir -p .bootstrap && cp out/netstandard2.0/*.dll .bootstrap/
rm -rf out .xake && dotnet fsi build.fsc.fsx -- -- build test
dotnet fsi build.fsc.fsx -- -- build                                     # no-op, no msbuild run
```
