# Verifying the hermetic build on DataEngine: two brands × two frameworks

A self-contained scenario for checking, by hand or in one command, what this branch actually
delivers on a real customer repository — `ar-net-core-dataengine`, three shipped libraries,
two brands (`MESCIUS`, `GCCN`) and **both** legs of
`<TargetFrameworks>netstandard2.0;net472</TargetFrameworks>`. Twelve assemblies in total.

Everything below was re-measured on 2026-09-23 after the import was changed to **one lock per
variant holding every target framework** (macOS, 8 cores, SDK 8.0.423 as dataengine's
`global.json` selects it, commit `649f4b5`). Earlier proofs on this fixture covered
netstandard2.0 only; net472 was first verified in the 4-lock run this file used to describe.

- `verify-dataengine.fsx` — the build script (2 locks, 12 compiles, 12 SBOMs)
- `verify-dataengine.sh` — the whole scenario end to end, printing every timing and the
  identity table; `docs/features/hermetic-build/verify-dataengine.sh [work dir] [repo]`

## Just run it

```bash
cd <xake checkout>
dotnet fsi build.fsx -- -- build && mkdir -p .bootstrap && cp out/netstandard2.0/*.dll .bootstrap/
docs/features/hermetic-build/verify-dataengine.sh
```

That is the whole scenario: it makes its own fixture from
`~/Projects-work/ar/ar-net-core-dataengine` (never touching that checkout), imports, builds,
runs the msbuild baseline, compares all 90 file pairs, regenerates the SBOMs, exercises the
package folder, and times the stock .NET scenario alongside. Pass a work directory and a
source repository to override the defaults:
`verify-dataengine.sh /tmp/mywork ~/src/ar-net-core-dataengine`.

**The `.fsx` is driven from the fixture, not from this folder.** Every command below assumes
`cd <the dataengine copy>`; run it anywhere else and it now stops with the cwd it found and
the project files it did not. The sections after this one are what the script does, step by
step, so you can run any one of them by hand.

| command | what it does |
|---|---|
| `dotnet fsi $X -- -- locks` | imports both brands into `locks/<brand>.json`, every framework inside |
| `dotnet fsi $X -- -- build` | compiles the 12 assemblies the locks name; no msbuild, no restore |
| `dotnet fsi $X -- -- sbom` | a CycloneDX 1.6 per assembly under `sbom/<fwk>/<brand>/` |
| `dotnet fsi $X -- -- restore` | fills the package folder from the locks and compiles nothing |
| `dotnet fsi $X -- -- show -d LOCK=locks/GCCN.json` | prints what landed in a lock |
| `PACKAGES=.packages dotnet fsi $X -- -- build` | keeps the dependencies in a folder of the build's own |

where `X=<xake checkout>/docs/features/hermetic-build/verify-dataengine.fsx`.

## 0. Prerequisites

```bash
cd <xake checkout>
dotnet fsi build.fsx -- -- build            # this branch is unreleased: stage it
mkdir -p .bootstrap && cp out/netstandard2.0/*.dll .bootstrap/
```

The fixture is a `git archive origin/develop` copy — the working checkout is never touched, and
the copy has no `.git`, so no `/sourcelink` or `/embed` appears in the command line (on a real
checkout it does, and `$(SourceRevisionId)` tokenization applies; see session.md).

```bash
mkdir -p /tmp/de && cd ~/Projects-work/ar/ar-net-core-dataengine && git archive origin/develop | tar -x -C /tmp/de
cd /tmp/de
```

## 1. Import: one lock per brand, both frameworks inside

```bash
dotnet fsi <xake>/docs/features/hermetic-build/verify-dataengine.fsx -- -- locks
```

`Project.import` takes `Frameworks: string list` and covers the whole framework matrix of one
variant in a single call, so there are **two locks, not four**, and each `Lock.Entry` carries
its own `Framework`. Per project: one `-t:Restore` (no `TargetFramework`, so every target lands
in `obj/project.assets.json`; `-p:RestoreRecursive=false`, so the restore writes nobody else's
assets file), then per framework a design-time build (`ProvideCommandLineArgs`,
`SkipCompilerExecution`, `-t:PrepareResources;Compile`, **no `-restore`**) and a `-pp` for the
import list — 5 msbuild runs per project, 30 in total. **No `-t 1`**: see §6.

What lands (`-- show -d LOCK=locks/MESCIUS.json`):

| lock | size | per entry |
|---|---|---|
| `locks/{MESCIUS,GCCN}.json` | 166 KB, 1486 lines | 6 entries: 3 projects × 2 frameworks |
| — netstandard2.0 entries | | 80 / 119 / 14 sources, **113–115 references**, 12 defines, 4 packages, 2 analyzers |
| — net472 entries | | same sources, **10–12 references**, 17 defines, 4 packages, 2 analyzers |

The two frameworks differ exactly where they should: netstandard2.0 references the ~113 ref
assemblies of `netstandard.library` 2.0.3, net472 the 10–12 of
`Microsoft.NETFramework.ReferenceAssemblies.net472` 1.0.3; the define sets differ
(`NETSTANDARD2_0` + the `_OR_GREATER` chain vs `NET472` + `NETFRAMEWORK` + its chain). The
compiler is the same `$(DotnetRoot)/sdk/8.0.423/Roslyn/bincore/csc.dll`, recorded with its
SHA-256 and product version `4.11.0-3.25569.22`. Every path is tokenized, so the two locks
are machine-independent and diffable.

Check the guarantees the import makes for itself:

- **Round-trip**: the import fails if the command line rebuilt from the structured entry is not
  byte-for-byte msbuild's. All 12 entries (6 per lock) pass, silently — to see it fail, edit an `Options`
  entry in a lock and re-run a compile.
- **SDK pin**: dataengine pins `8.0.100 rollForward:latestFeature`, so any 8.0.x runs; the lock
  records both the pin and the SDK that actually ran (`8.0.423`).

## 2. Build from the locks

```bash
dotnet fsi <xake>/.../verify-dataengine.fsx -- -- build
```

No msbuild is started (`grep -c '\[msbuild\]'` on the log → 0), no restore happens, nothing
talks to the network. Every reference, analyzer and the compiler are SHA-256-checked before
`csc` runs; every input is a tracked `FileDep`.

## 3. Byte-identity against `dotnet build`

The baseline must be built **in the same directory with the same `IntermediateOutputPath`**
(csc embeds absolute source paths in the PDB and, under `/deterministic`, in the PE) and with
**`-t:Rebuild`** (msbuild's incremental caches otherwise reuse stale project-reference output —
this is what made the page run look like a near-miss). Save Xake's output first, then:

```bash
dotnet build src/DataEngine/DataEngine.csproj -c Release -t:Rebuild \
  -p:TargetFramework=$f -p:Brand=$b -p:NuGetAudit=false \
  -p:IntermediateOutputPath=obj/xake/$f/$b/
```

**Result: 36/36 byte-identical** — 3 projects × 2 brands × 2 frameworks × (dll, pdb, xml).

```
IDENTICAL netstandard2.0  MESCIUS  DataEngine.dll   391680      IDENTICAL net472 MESCIUS DataEngine.dll   391680
IDENTICAL netstandard2.0  MESCIUS  DataEngine.pdb   110748      IDENTICAL net472 MESCIUS DataEngine.pdb   104912
...                                                             (full table in verify-dataengine.sh's output)
identical=36 different=0
```

**Determinism**: delete `obj/xake` and compile the same locks again — 36/36 identical to the
previous run. **SBOMs**: 12/12 byte-identical when regenerated.

## 4. Speed, measured

One run of `verify-dataengine.sh`, wall clock including ~1.0 s of `dotnet fsi` startup per Xake
invocation (the engine's own figure is printed in each log and given in the third column).

| phase | wall | engine | note |
|---|---|---|---|
| **Xake** | | | |
| `locks`, cold, concurrent | **13.4 s** | 12.2 s | 2 locks, 30 msbuild runs (6 restores + 24 design-time/`-pp`) |
| `build`, cold outputs | **7.3 s** | 6.3 s | 12 assemblies, 0 msbuild, 0 restore, 0 network |
| `build`, nothing changed | **1.1 s** | 0.12 s | |
| `build`, one source touched | **7.6 s** | 6.7 s | 12/12 recompiled (VBFunctionLib is at the bottom of the graph), 0 locks re-imported |
| `build` from committed locks (fresh `obj`) | **7.6 s** | 6.5 s | 2/2 locks skipped — the CI shape: no msbuild, no restore |
| `sbom`, 12 CycloneDX files | **1.3 s** | 0.18 s | |
| `restore` into an empty folder | **2.0 s** | | 153 MB, 2 packages, compiles nothing |
| `build` against that folder, warm | **8.7 s** | | 0 restores |
| `build` against an *empty* folder | **9.5 s** | | one restore for the whole run, then 12 assemblies |
| **stock .NET** | | | |
| `dotnet restore`, first ever (HTTP cache cold) | **3.9 s** | | 156 MB, 6 packages, network |
| `dotnet restore`, no-op | **0.6 s** | | |
| `dotnet build -c Release -p:Brand=X`, clean `obj`, both frameworks | **2.3 / 2.4 s** | | 6 assemblies per brand |
| `dotnet build`, nothing changed | **1.0 s** | | |
| `dotnet build`, one source touched | **2.3 s** | | |
| `dotnet build -t:Rebuild`, one (framework, brand) | **2.9–4.2 s** | | 3 assemblies |

Against the 4-lock run this file used to record: the import is **13.4 s concurrent** where it
was **18.0 s with `-t 1`** (and ~9 s concurrent, which raced). It is not down to 9 s because
the framework matrix now costs 30 msbuild runs rather than 24 — six restores are separate
processes instead of riding on `-restore` — but it is correct concurrently, which the 9 s never
was. Everything downstream of the import is unchanged within noise (build 7.3 s vs 8.2 s,
no-op 0.12 s engine, touch 7.6 s, sbom 0.18 s engine).

Reading it honestly, for the same twelve assemblies:

- **Stock .NET, warm machine**: `restore` + build per brand (a brand switch invalidates `obj`,
  so each brand is a clean build) ≈ **6.2 s**. First time on a fresh machine: **+4 s** of
  network, and the build cannot start without it.
- **Xake with the locks already in the repository**: **7.6 s**, and no msbuild, no restore, no
  network, no NuGet — plus a SHA-256 check of all ~750 referenced files and a `FileDep` per
  input. Importing the locks is a separate, rare 13 s that a PR pays only when a project file
  changes.
- **Per assembly Xake's compile is ~1.6× msbuild's**, and the reason is visible in the log:
  each assembly takes 0.75–1.55 s, and there is no compiler server — the recorded command line
  has no `/shared`, so every compile is a fresh `dotnet csc.dll` process, while msbuild's `Csc`
  task talks to a warm `VBCSCompiler`. Passing `/shared` is an obvious, untried optimisation.
- The incremental story inverts: msbuild's touch-a-source rebuild is 2.3 s against Xake's 7.6 s
  for four times the assemblies, but msbuild reaches that only by keeping mutable state in
  `obj/` (which is exactly the state that made the page baseline wrong until `-t:Rebuild`),
  while Xake's verdict comes from hashes in `.xake`.

## 5. What happens when the machine does not have the dependencies

**An empty package folder is no longer a failure** — the build restores exactly what the lock
names and carries on:

```bash
PACKAGES=/tmp/fresh dotnet fsi .../verify-dataengine.fsx -- -- build
```

One `dotnet restore` for the whole run, then 12 assemblies: **9.5 s** against 8.7 s with the
folder already warm. The folder ends up with the two packages the compilation actually reads
(153 MB) — not the four in the restore graph, because `CycloneDX.MSBuild` and
`SauceControl.InheritDoc` are build-time only and no compiler input comes from them.

**And the bytes do not depend on where the packages live**: the 12 assemblies built against a
freshly restored folder are **36/36 identical** (dll, pdb, xml) to the ones built against the
machine's own NuGet cache. The lock fixes id and version; the per-file SHA-256 check that runs
after the restore is what makes that a guarantee rather than a hope.

**Restore turned off** (`Restore.Options.Enabled = false`) reproduces the old behaviour
exactly: the build stops before the compiler with every missing file listed as
`expected <sha256>, got missing` — 359 of them on this fixture — plus a warning naming the
packages that would have been fetched. A build that must never reach the network sets it and
loses nothing but the download.

**A tampered reference**: overwrite any file the lock hashes and build — the run stops with
`expected <sha>, got <sha>`, reporting every mismatch at once.

**A lock from another machine**: the paths are tokens (`$(NuGetPackageRoot)`, `$(DotnetRoot)`,
`$(ProjectRoot)`), so the lock transfers; what has to match is the SDK (the compiler's hash is
checked) and, for byte-identity, the checkout path (or `/pathmap`).

## 6. Defect found by this matrix — and fixed by restructuring the import

**Found (2026-09-23, the 4-lock run).** Importing one multi-targeted project for two frameworks
concurrently was broken. Every concurrent import of the four locks that was inspected showed
it, with a different victim each time: two failed outright,

```
error NETSDK1005: Assets file '.../src/VBFunctionLib/obj/project.assets.json' doesn't have a
target for 'netstandard2.0'. [.../VBFunctionLib.csproj::TargetFramework=netstandard2.0]
```

and two produced a lock entry silently recorded with `packages 0` — the same entry has 4
packages when imported alone, so **the SBOM of that assembly came out without its package
components and nothing said so**.

Mechanism, confirmed by hand:

1. The design-time build passed a global `-p:TargetFramework=X`. NuGet then restored that one
   framework, and `obj/project.assets.json` came out with **only that target** — verified:
   after such an import the file holds `['.NETFramework,Version=v4.7.2']` alone, while a
   restore without the property holds both targets.
2. `-restore` walks the project *graph*, so DataEngine's import rewrote ExpressionInfo's and
   VBFunctionLib's assets files too. `Project.withProjectLock` serializes the msbuild runs of
   *one* project and cannot cover that.
3. The other framework's import then read an assets file without its target: msbuild failed
   with NETSDK1005, or — if it got as far as `Nuget.readAssets` — that returned an empty graph
   without complaining.

**Fixed (2026-09-23), structurally rather than by serializing.** Three changes:

- **`ImportOptions.Frameworks: string list`.** The framework matrix of one variant is *one*
  import writing *one* lock, so two frameworks of a project are never two concurrent imports of
  it. The framework moved from `Lock.Document` onto `Lock.Entry`; `Lock.entryFor framework name
  lock` is the lookup.
- **One restore per project, with no `TargetFramework`** (`-t:Restore`), so the assets file
  holds every target; the design-time builds then run per framework **without `-restore`**.
  Verified by hand on this fixture, and again as part of the run above.
- **`-p:RestoreRecursive=false` on that restore**, so a project's restore stops writing the
  assets files of the projects it references. Verified by hand: with the flag, a clean-`obj`
  restore of `DataEngine.csproj` leaves `src/ExpressionInfo/obj` and `src/VBFunctionLib/obj`
  absent; without it, all three assets files appear. The design-time build does **not** need
  the referenced projects restored (`BuildProjectReferences=false` only asks them for their
  `TargetPath`) — also verified, by deleting the two referenced projects' `obj/` and running
  DataEngine's design-time build, which still reported its 221 arguments.

`withProjectLock` stays, and now brackets the whole project (restore plus every framework's
design-time build and `-pp`, and the assets read in between): two *brands* still restore into
the same `obj/project.assets.json`, so that half of the race is real and the `Resource` is
what handles it. What it no longer has to cover — and never could — is one project's restore
writing another project's obj. The alternative fallbacks (locking every referenced project, or
a single process-wide import `Resource`) were not needed.

Independently, **`Nuget.readAssets` now fails** when the assets file has no target for the
framework asked for, naming the targets it does have, instead of returning an empty graph.
That is the half that turned a wrong restore into a silently incomplete SBOM.

**Re-proved**: three consecutive cold, concurrent imports (`rm -rf locks .xake src/*/obj`, no
`-t 1`) — 0 × NETSDK1005, 12/12 entries with a 4-package graph, every run. And the run in §1–§5
above is itself concurrent.

## 7. The package folder as a build-agent cache

`PACKAGES=<dir>` (the script's variable; `Restore.into "<dir>"` in a script of your own) puts
this build's dependencies in a folder of the build's own instead of `~/.nuget/packages`. The
same folder does both jobs — it expands `$(NuGetPackageRoot)` when the lock is read
(`Roots.packageRootOverride`) and receives what `Restore` downloads — so a build agent caches
one directory next to the checkout and nothing else:

```bash
dotnet fsi .../verify-dataengine.fsx -- -- restore   # 2.0 s, 153 MB, compiles nothing
# <- the agent caches $PACKAGES here
dotnet fsi .../verify-dataengine.fsx -- -- build     # no restore, no network
```

How it works, in full: `restore.md`. The short version — nothing resolves a version or reads
`project.assets.json`; a reference under the package folder is spelled
`<root>/<id>/<version>/...`, so a missing file names its own package, and one `dotnet restore`
of a synthesized project with one `PackageDownload` per missing package fetches the set in a
single call. The nupkg is checked against the `Sha512` the lock recorded; the per-file SHA-256
check is untouched and remains the authority on the files themselves. On a machine that has
everything, the whole step is one `File.Exists` per distinct path — a no-op build stays a
no-op (0.09 s).

## 8. Traps this scenario has fallen into

- **A leftover `bin/Release` in the fixture changes what is compared.** A project reference is
  recorded by msbuild as the referenced project's own `bin/Release/...`; the import hashes it
  *if the file exists*, and a script that decides "this is mine to rebuild" by "the recorded
  hash is empty" then leaves it pointing at whatever an earlier `dotnet build` left there. The
  build silently compiles against that stale artifact, and only the projects that have project
  references differ from the baseline — which reads exactly like a regression in the compiler
  path and is not one. `verify-dataengine.fsx` now decides by *name* (a reference whose file
  name is another entry of the same framework in this lock), and `Lock.mapPaths` drops the
  hash of a path it rewrote. `import.fsx` and `import-page.fsx` still use the old heuristic
  (tracker).
- **In a tree Xake has built in, the page baseline recipe fails** with `MSB3577: two output
  file names resolved to the same output path` naming a `.resources` — for projects with more
  than one same-named `.resx` (page has them; dataengine has none, which is why this matrix
  is unaffected). A pristine copy runs the recipe fine; deleting the `.resources` Xake wrote
  into the intermediate directory rescues a single project's build but not the full sequence.
  Not fully diagnosed — tracker item, and interop rather than test hygiene, since a user who
  alternates Xake and `dotnet build` in one `obj` tree meets the same thing.
- **The baseline has to be the recorded recipe, property for property.** Adding
  `-p:NetCoreOnly=true` to the page baseline — a property the *import* passes — made 24 of 30
  baseline builds fail with `MSB3577: two output file names resolved to the same output path`.
  The comparison recipe in `samples/hermetic/page/compare.txt` is the one that has been
  proven; change it only with evidence.
