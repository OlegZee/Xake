# Plan of fixes after the ActiveReports experiment

Status: draft for the user, 2026-10-13. Input: [ar-experiment.md](ar-experiment.md) (gaps G1-G13),
the pending round-2 measurements ([ar-parallel.md](ar-parallel.md), [ar-direct.md](ar-direct.md)),
[questions.md](questions.md). Goal of the whole series: the AR script back to the draft's shape (one
screen, no data processing in rules) and a clean build at least as fast as `dotnet build` with
better core use. Order and PR boundaries below; each PR to `dev`, squash, with tests and docs.

## Round A: descriptions and import carry what msbuild carries (hermetic, ~200 lines)

One PR, **A1 "Hermetic: import and project cover resx, NoWarn, assembly attributes, package
metadata case"**, or two if the resx part grows. Removes the `fixup` block and the `.resx` rule
from the AR script (G1, G2, G3, G4, G13).

| gap | change | where | accept |
|---|---|---|---|
| G1 `.resx` | `csc/fsc { project }` compiles each `.resx` of the description with `Xake.Dotnet.Resx` into `obj/<cfg>/<fwk>/<name>/` and passes `/resource:<file>,<manifest>`; manifest = `RootNamespace` + relative path dots (verified equal to msbuild's for Rdl, Rendering, Excel, Html); the `.resources` files are inputs of the compile (fileset dependency), not lock entries (they are derived from repo files). Lock: `Resources` keeps the `.resx` source path and the manifest name; `Locks.compileAs` compiles resx before csc | `Described.fs` (expand step), `Locks.fs` (`compileAs`), `Description.fs` (schema: `resources[].manifest` optional), `Import.fs` (LogicalName / `Res\*` glob to the same field) | Rdl/Rendering/Excel of AR compile with no script rule; a fixture with a `.resx` round-trips through import -> lock -> build; manifest names equal msbuild's `.resources` names |
| G2 metadata case | `Import` reads item metadata case-insensitively (`Version`, `version`, `PrivateAssets`...) | `Import.fs` | Rdl's `System.Text.Json version="8.0.5"` imports with the version |
| G3 NoWarn | schema `options.noWarn: string list`; `Import` fills it from `NoWarn` (minus the SDK defaults); `project` emits `/nowarn:` with the SDK defaults (`1701;1702` C#, F#'s own) added; `/warnaserror+` only when `TreatWarningsAsErrors` | `Description.fs`, `Import.fs`, `Described.fs`, schema, `description.md` | the page projects compile with `TreatWarningsAsErrors` and no script `noWarn` string |
| G4 assembly attributes | schema `assemblyAttributes: [{ type, args }]` (data; `CLSCompliant`, `ComVisible`, `Guid`...); `Import` takes `<AssemblyAttribute Include=... _Parameter1=...>`; `project` generates them into the same generated file as `InternalsVisibleTo` | same files | CS3021 gone on the 4 AR projects; generated file content byte-stable |
| G13 churn | falls out: no `fixup` rewrite; `Import` writes only on content change (already) | - | re-import after A1 touches 0 files |

Size ~150-200 lines + tests. Model for the subagent: opus (schema + three modules).

## Round B: lock generation and build fidelity (hermetic, ~100 lines)

**B1 "Hermetic: transitive project references, framework assemblies of packages, compile
directory"** (G5, G6, G7, G12). Independent of A1; can run in parallel on a sibling branch.

| gap | change | where | accept |
|---|---|---|---|
| G5 transitive refs | `Locks.generate` adds, for each project ref B, B's own project-ref outputs (closure over the refs' locks, unhashed, deduplicated); csc gets the closure like msbuild does | `Locks.fs` | DataEngine -> ExpressionInfo -> VBFunctionLib compiles without `transitive` in the script; a 3-level fixture test |
| G6 framework assemblies | `Nuget.readAssets` returns `frameworkAssemblies` of the assets file; `Described.restore` adds them as `grefs`-style references for .NET Framework TFMs | `Nuget.fs`, `Described.fs` | net472 OpenXml project compiles (WindowsBase); test on a package with `frameworkAssemblies` |
| G7 compile directory | `Compilation.Directory` = the description's project folder (the slot exists); relative `<include file>` doc fragments resolve; `--pathmap` unchanged | `Described.fs`, `Locks.fs` | CS1589 gone on Svg; lock `Directory` is `$(ProjectRoot)/...` of the project |
| G12 `.xml` side output | the dll rule registers the `.xml` as a second target when the lock asks for doc (`targets [dll; xml]` form in the docs and in `Locks.needAll`), or document "need the dll" | `lock.md`, maybe `Locks.fs` | a rule that needs the xml first does not fail |

| G14 packages of project refs (direct route, ar-direct.md) | `csc/fsc { project }` without a lock gets only the description's own packages; the refs' packages (Rdl's System.Text.Json/System.Memory) are missing and Rendering fails with CS0246. Lock route already flows B's packages (PR 3). Fix: `Described` resolves the refs' descriptions (by `projectRefs` + `descriptionOf`) and adds their packages to the restore, same rule as `Locks.generate` | `Described.fs` | the direct AR script needs no hand-added packages |
| G15 restore per target (direct route) | 30 `Described.restore` msbuild runs inside the targets make the direct clean build 46-57 s vs 33 s via locks; fix: one restore per description set (batch the generated PackageReference project over all descriptions x TFMs, or cache the assets per (packages, TFM) within a run) | `Described.fs`, `Restore.fs` | direct clean build within 10% of the lock route |

Size ~150 lines + tests with G14/G15. Model: opus.

## Round B2: package fetching without msbuild where no resolution is needed (hermetic)

Decided 2026-10-13: **no `NuGet.Commands` / NuGet.Client libraries** in the package. Two steps.

| step | change | where | accept |
|---|---|---|---|
| B2.1 batch restore (G15) | `Described.restore` keeps `dotnet restore` (the resolver is still msbuild's), but one run per build: synthesize one project per description x TFM under `obj/xake/restore/` and restore them all with a single traversal project (or `dotnet restore` over the folder); results read per `project.assets.json` as now; within a run cache the assets per (package set, TFM) so identical sets restore once | `Described.fs`, `Restore.fs` | direct AR clean build: 1 msbuild restore instead of 30; `update-locks` of 15 x 2 descriptions: 1 restore; measure before/after |
| B2.2 direct download for locks | `Restore.download` fetches missing packages of a lock over HTTP itself: flat container `v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg` from the sources of `nuget.config` (+ `packageSourceMapping`), SHA-512 checked against the lock, extracted into NuGet's layout (`.nuspec`, `.nupkg.metadata`, `.sha512`, files); auth = basic/bearer from an env token when the source has `packageSourceCredentials` or the token variable the config names; anything needing a credential provider falls back to the current `dotnet restore` PackageDownload path | `Restore.fs`, `Nuget.fs` | fresh clone of the self-build and of AR (nuget.org only) restores with zero msbuild processes; a private feed with a token restores; a feed needing a credential provider still works via the fallback; hashes verified as before |

Order: B2.1 first (small, measurable); B2.2 after the parallel/no-op numbers show restore is still
on the critical path. Model: opus.

## Round C: engine performance (core, size unknown until profiled)

**C1 "Core: fileset `**` matching cost"** (G8). Small and measurable: `!**/.*/**/*` costs ~1 ms per
file (1.4 s for 1316 files vs 11 ms). Fix in `Fileset.fs` matching (compile the mask once; avoid
re-walking for excludes; or match excludes per directory). Accept: expansion of the AR Rendering
masks < 50 ms; existing fileset tests green; AR Release clean 33 -> ~21 s. Also `Import` stops
emitting the exclude when the project folder has no dot folders? No: keep msbuild's semantics,
fix the engine. Model: opus (matching code), with a benchmark test.

Findings of round 2 (ar-parallel.md) that round C must explain and fix:
- **G16 hang**: a parallel `build` of the four graphs hung twice (run 3 and, by the user's reading,
  run 5): two graphs complete, the `.resources` targets of Export.Excel.Page of each brand (same
  target path for Debug and Release of one brand, `obj/resx/(b:*)/(name:*)/...`) never complete,
  process idle at 0% CPU. Suspect: the engine when one target is needed by two running parents
  (WorkerPool join) under a full pool; reproduce on a small script (two parents, one shared child,
  pool of N) before any fix. Engine bug candidate, highest priority of round C.
- **G17 parallel graphs slower than sequential**: four independent graphs in one run take 224 s vs
  141 s for the four run one after another, while msbuild gains 2x from concurrency (80 -> 40 s).
  C0 must show whether targets wait (WaitTime) or work (OwnTime) and how many run at once.

**C0 "Statistics from the `.xake` database after a run"** (decided 2026-10-13, before any profiling).
The database holds, per target, `BuildResult.Steps` (`Name`, `Start`, `OwnTime`, `WaitTime`) and
`Depends` (file stamps, vars, `GetFiles` masks with their expansion). A `dotnet fsi` script (later a
`--stats` option of the CLI next to `--dump`/`--dryrun`) reads it through `Database` from the Xake
package and prints: a Gantt of all targets with the number of concurrently active targets per
moment (answers whether the engine caps parallelism or the targets wait inside themselves); a table
own/wait per target against the csc process time from `build.log` (the per-target preparation cost);
the count and kind of dependencies checked per target in a no-op (`GetFiles` with the `!**/.*/**/*`
mask, file stamps) to explain the no-op time; `--dryrun`'s estimate against the measured wall.
Missing today and to add as library `Step` marks (not engine): sub-steps inside `Locks.compileAs`
(mask expansion, lock read, reference hashing, csc) and the time of the dependency-check phase
before the first recipe. Accept: the script runs on the AR database of a parallel clean run and a
no-op and the report names the top three time sinks with numbers. Model: haiku. Roadmap: the CLI
`--stats` option.

**C2 "Core: profile the no-op and the per-target preparation"** (G9), only for what C0 cannot
answer. First a report, not a fix:
profile a no-op of the 120 AR targets (33 s at util 1.04) and a clean parallel run (round-2
numbers): where the time goes (dependency check serial? `getFiles` re-expansion per run? lock
reading and hashing per target? `Fingerprints.warn` per rule?). Then a fix PR with the measured
target: no-op of 120 targets < 5 s; parallel 4-config clean at util > 8. Model: opus for the
profiling, decision after the report.

Depends on: round-2 measurements (`ar-parallel.md`) to know the parallel baseline.

## Round D: portability and hermetic mode (hermetic, ~60 lines)

**D1 "Hermetic: extra roots and pathmap for generated locks; dotnet-sdk prerequisite"** (G10, G11).
Needed for the real AR layout (sibling repos) and for `HERMETIC=on` on the self-build.

| gap | change | accept |
|---|---|---|
| G10 | `Locks.generate`/`compileAs` take the script's extra roots (`Roots`, as `Lock.loadWith roots` does) and emit one `--pathmap` per root; sources/keyfile outside `$(ProjectRoot)` tokenise | AR probe with the repos as siblings: lock has no absolute path; builds from another checkout path |
| G11 | `Locks.generate` records the `dotnet-sdk` prerequisite when `global.json` pins exactly (what `Lock.record` does) | `HERMETIC=on` accepts a generated lock under an exact pin; self-build can turn the mode on (the open `global.json` question decides whether it does) |

Depends on the user's HERMETIC/pin decision for the self-build (questions.md PR 7 q4). Model: opus.

## Round E: the rerun and the script (experiment, no PR)

After A1 + B1 + C1: rerun the AR experiment on a locally packed hermetic (NUGET_SOURCE) or after
a 0.4 preview release; targets: script <= 100 lines with zero `// G` marks, both brands x both
configs with `<==`, clean parallel run and no-op measured against msbuild sequential and concurrent
(round-2 table), the direct route (`csc { project; config }`, no locks) measured next to the lock
route. Report appended to ar-experiment.md. Model: haiku for the measurements, sonnet for the script.

## Round F: pack (PR 8 of the plan, ~330 lines)

Unchanged from the brief, now with the AR nuspec facts: `Package.nupkg`/`projects`/`push`,
`Pack.compare`; nuspec token filling (`$prefix$`, `$version$`, `$versionRange$`, brand ranges) from
the description's `package` block plus script overrides; the InheritDoc trimming of xml is out of
scope (noted). Depends on the open answers: `package` block by default (PR 5 q4), `bundle` (q3),
assembly versioning (brief §6). Model: opus.

## Order and parallelism

1. A1 and B1 in parallel (disjoint files except `Described.fs`: A1 touches the expand/resx step,
   B1 the restore/refs step; merge order A1 then B1, second merges `dev`).
2. C1 right away (core only, independent). C0 right after the round-2 measurements (reads their database); C2 only for what C0 leaves open. B2.1 after B1 (same files); B2.2 after measuring.
3. D1 after the pin decision. E after A1+B1+C1 land. F after the pack answers.

Open decisions needed from the user: HERMETIC pin for the self-build (D1, E); `package` default
and `bundle` (F); whether G9's fix may touch the engine's scheduling (C2 report will say).
