# Round E: ActiveReports rerun on the night's packages (2026-10-13)

Experiment folder `/Users/olegz/Projects-work/ar/xake-ar/` (AR worktrees untouched, `git status` clean in both).
Packages packed from `night-integration` 74bf4d9 (A1+B1+B2.1+D1 + C1+C2a): `dotnet fsi build.fsx -- -- pack -d Version=3.7.1-night
-d HermeticVersion=0.3.1-night` (the variables are `Version`/`HermeticVersion`, not `VERSION`; the first try packed 0.0.1/0.3.0,
deleted), copied to `feed/`. Hermetic builds against the published Xake 3.7.0.26 (no core API needed); at run time the
script's `#r Xake 3.7.1-night` wins. Checked by `tools/verify-night.fsx`: `Locks.restoreAll`, `Described.closure`,
`Locks.generateWith` resolve; Xake.dll 3.7.1.0 carries C2a's "Database agent failed to store" and C1's regex cache field.
SDK 8.0.425, M5 Pro 18 cores, quiet machine, one measurement at a time. Logs `logs/r3/`, stats `stats/`.

## Scripts

| | round 1 | now |
|---|---|---|
| lock route `build.fsx` ([ar-build-2.fsx](../ar-build-2.fsx)) | 179 lines, `fixup`/`transitive`/resx rule/1589 | **124 = 92 + 32 pack section**, zero `// G` |
| direct route `direct/build.fsx` ([ar-direct-2.fsx](../ar-direct-2.fsx)) | 48 lines, resx rule, hand-added packages | **43 lines**, zero `// G` |

Lock route: `fixup`, `transitive`, `frameworkRefs`, `pinned`, the 1589 nowarn and the `.resx` rule are gone; `update-locks`
calls `Locks.restoreAll` per config (it takes one cfg: a 1-line loop) then needs the `lock::` targets; `build` is `<==`
over the four; the dll and the new `.xml` rule share one `compile` recipe (`..>`); pack kept as a marked section (its
`needFiles` now takes dll and xml in one call, G12). Old script kept as `build.round1.fsx`, old data in `round1/`.
Direct route: descriptions are now copies of the fresh mescius import (no hand edits: G2-G7 data comes from Import);
`Described.restoreAll` in `main`, `Described.closure` + `restoreWith` in the dll rule.

Re-import (`import::mescius import::gccn`, 35 s both): descriptions gain `noWarn` (`1591 0618 0612`), `assemblyAttributes`
(CLSCompliant), resx back as `{ "file": "...Strings.resx" }`, `projectRefs` direct only, Rdl's `System.Text.Json 8.0.5`.
A second import touches no file. `update-locks::mescius update-locks::gccn`: 60 locks in **3.5 s** (one batch restore per
brand: 14 + 7 package sets); locks gain `Directory` = project folder, `/nowarn:1701,1702,1591,0618,0612`, resx pairs
under `obj/xake/<asm>/<fwk>/<cfg>/`, transitive refs, `WindowsBase` on net472 Excel. **A second `update-locks` writes
nothing** (mtimes compared). `check::mescius check::gccn` pass. Manifest resources of Excel and Rendering equal msbuild's.

## Timing (`tools/cputime.pl`, util = CPU/wall; clean = `out/`, `.xake`, generated `obj/xake/<asm>` deleted)

| run | round 1 / 2 | now | msbuild |
|---|---|---|---|
| clean `build::mescius::Release`, server off | 33.4 s, CPU 100, util 3.0 | **6.9 s**, CPU 57, util 8.3 | 24.2 s, CPU 148, util 6.1 (today; r1 27.9) |
| same, server on (warm) | 32.2 s | **6.0 s** (server CPU outside the group) | 21.7 s (r1) |
| no-op of the same | 7.1 s (4.4 s without G8 mask) | **0.9 s** (1.0 s server on) | 2.1-2.5 s |
| clean `build` all four `<==`, server off | 224 s (`<==`, r2), 141 s (`<<<`) | **19.7 s**, CPU 282, util **14.4** | sequential 80 s; concurrent: see below |
| same, server on | - | **11.1 s** (+105 s server CPU by `ps` delta: util ~10.7) | - |
| no-op all four | 33 s, util 1.04 | **1.3 s** | - |
| fresh clone (`.xake* out obj .packages` deleted), `build check::mescius check::gccn -d NUGET_FETCH=on`, off | 141 s | **26.2 s**, CPU 290, util 11.1; 120 dlls, 2 package fetches (30 + 4), committed files unchanged (sha) | - |
| direct, clean, server off | 57.1 s (46-57) | **8.5 s**, CPU 60, util 7.1 (one batch restore of 14 sets) | - |
| direct, clean, server on | 48.8 s | **5.6 s** | - |
| direct, no-op | 13.0 s | **0.8 s** | - |

msbuild concurrent (four Rebuilds of `baseline.sln` started together, as in ar-parallel.md) is **not a valid baseline**: it
fails in one tree. Round 2: gccn Debug and gccn Release exit 1 (8.3 s, 9.0 s), so its "~40 s" was two builds. Rerun now: mescius
Debug and gccn Release exit 1 (`CS0006: Metadata file '.../VBFunctionLib/bin/Release/netstandard2.0/GCCN...VBFunctionLib.dll'
could not be found`, `MSB3030: Could not copy the file ...`), the two survivors 22.1 s each. Brands share `bin/`/`obj/`.
So the fair msbuild "all four" number is the sequential 80 s. Xake builds all four in 19.7 s (4x), and one config in
6.9 s (3.5x). No csc crash (round 1's exit 134) in 9 clean runs (~330 csc processes); no hang in any run.

## Gantt (`tools/xakestats.fsx`, now also writing CSV with `CSV_PREFIX`)

- Sequential clean: wall 6.2 s, peak 20 targets at t=5 s, mean 6.9; the critical path VBFunctionLib 0.3 s -> ExpressionInfo 0.4 ->
  Rdl 0.9 -> Rendering 3.1-3.3 -> Excel 1.2 adds up to the wall: per-target overhead is gone (round 1: 2-7 s before each csc).
- Parallel clean: wall 18.9 s, peak 36 at t=15 s, mean 16.1, **1 second with 0 active** (round 2: 116 of 217 s). Seconds 4-12
  hold exactly the 8 Rendering compiles (2 brands x 2 cfg x 2 fwk), own **10.7-12.0 s each** vs 3.1 s alone: CPU contention
  (util 14.4 of 18), not engine waiting. Biggest waits: the 4-level leaves (Gdi, Image, Pdf, Svg) wait ~18.4 s on Rendering, i.e.
  the dependency chain. Server on: peak 48, wall 10.3 s.
- No-op: 30/120 `Skipped (up to date)`; db steps of a no-op are the clean run's (ar-stats.md caveat), so no Gantt for it.

CSV in `stats/`: `seq-clean.csv` + `seq-clean-concurrency.csv`, `parallel-clean.csv` + `parallel-clean-concurrency.csv`
(targets: `target,kind,start_s,wait_s,own_s,end_s`; concurrency: `second,active`), the same per run as `<run>-targets.csv`,
`runs.csv` (every run), `noop.csv`, `msbuild-concurrent.csv` (rounds 2 and 3, with exit codes). Text dumps `stats/<run>.txt`.

## Verdict

- Targets met: lock script 92 lines + pack section, zero `// G`; both brands x both configs with `<==`; clean parallel and
  no-op measured against msbuild sequential (concurrent msbuild cannot run in one tree); direct route measured beside it.
- The night closed G1-G9, G12-G15 for AR. The speedup is C1/C2a (regex cache: per-target preparation and serial no-op checks)
  plus B2.1 (one restore). Nothing got worse.
- Remaining:
  1. **HERMETIC=on** still refused (G11 for C#, D1 question 1): `'MESCIUS.ActiveReports.Core.Data.VBFunctionLib': HERMETIC=on: the lock
     'mescius/locks/VBFunctionLib/Release.json' names $(DotnetRoot)/sdk/8.0.425/Roslyn/bincore/csc.dll, outside $(ProjectRoot) and
     $(NuGetPackageRoot) and not covered by a prerequisite; re-record it with HERMETIC=on`.
  2. **Doc bug in B2.1's shape** (`Described.closure` doc comment, csc-syntax.md): `let! refs = Described.closure descOf d` then
     `ref (refs |> ...)` inside `csc { }` fails: `error FS3095: 'refs' is not used correctly. This is a custom operation in this
     query or computation expression.` Renamed to `deps` in the script; the docs should do the same.
  3. Pack is still 32 lines of script (PR 8). `Locks.restoreAll` takes one config (a loop line). G10 (repos outside the root) not
     exercised: the worktrees sit inside the experiment folder.
  4. Server-on CPU is invisible to `cputime.pl` (VBCSCompiler detaches); a VBCSCompiler (pid 57853) started by these runs stays
     until its idle timeout.

## Final lock-route script (`xake-ar/build.fsx`, verbatim but the pack section; full file [ar-build-2.fsx](../ar-build-2.fsx))

```fsharp
// ActiveReports (ar-net-core-dataengine + ar-net-core-page, branch develop) built from project descriptions
// and locks (Xake docs/hermetic/lock.md). The brand is a prefix of every path and command:
// <brand>/projects/<name>/xake.project.json, <brand>/locks/<name>/<cfg>.json, <brand>/xake.fingerprints,
// out/<brand>/<cfg>/<fwk>/<name>/, commands import::mescius, update-locks::gccn, build::mescius::Release.
// `import` (msbuild, under the brand's global properties) and `update-locks` are the only writers of the
// committed files; `build` compiles from them with csc, no msbuild.
#i "nuget: /Users/olegz/Projects-work/ar/xake-ar/feed"
#r "nuget: Xake, 3.7.1-night"
#r "nuget: Xake.Hermetic.Dotnet, 0.3.1-night"

open System.IO
open System.Text.RegularExpressions
open Xake
open Xake.Tasks
open Xake.Hermetic.Dotnet

let vars = {| Version = Var.create<string>(description = "Package version") |> withDefault "4.6.0" |}

let brands = [ "mescius"; "gccn" ]
let configs = [ "Debug"; "Release" ]
let projects = !! "ar-net-core-dataengine/src/*/*.csproj" ++ "ar-net-core-page/src/**/*.csproj"
let props (b: string) = [ "Brand", b.ToUpperInvariant (); "LocalBuild", "true" ]   // LocalBuild: page -> dataengine by ProjectReference
let descOf b name = $"{b}/projects/{name}/xake.project.json"
let descriptions b = !! $"{b}/projects/*/xake.project.json"
let lockOf b name cfg = $"{b}/locks/{name}/{cfg}.json"
let outOf b cfg fwk name = $"out/{b}/{cfg}/{fwk}/{name}"         // a folder per project: assembly names are branded
let fingerprints b = $"{b}/xake.fingerprints"
let versions = [ "**", vars.Version ]
let nameOf (path: string) = Path.GetFileNameWithoutExtension path

let compile = recipe {                                          // a dll, or its doc .xml (side output of the same csc)
    let! b = getRuleMatch "b"
    let! cfg = getRuleMatch "cfg"
    let! fwk = getRuleMatch "fwk"
    let! name = getRuleMatch "name"
    do! Locks.compileAs (lockOf b name cfg) fwk versions
}

do xakeScript {
    filelog "build.log" Verbosity.Chatty
    varschema vars
    var "NUGET_PACKAGES" ".packages"

    rules [
        "main" <<< ["build"]
        "build" <== [ for b in brands do for cfg in configs -> $"build::{b}::{cfg}" ]

        // (a) project file -> description, under msbuild with the brand's props; records the msbuild files read
        command "import::(b:*)" {
            let! b = getRuleMatch "b"
            let! (Filelist files) = getFiles projects
            let! imported = Import.descriptions [ for f in files -> f.FullName ] (nameOf >> descOf b) (props b)
            for i in imported do do! Fingerprints.record (fingerprints b) i.Description i.Inputs
        }
        // (b) description -> lock, referenced projects first; the packages of all locks come from one restore
        "lock::(b:*)::(name:*)::(cfg:*)" => recipe {
            let! b = getRuleMatch "b"
            let! name = getRuleMatch "name"
            let! cfg = getRuleMatch "cfg"
            let! d = Description.load (descOf b name)
            do! need [ for r in d.ProjectRefs -> $"lock::{b}::{r}::{cfg}" ]
            do! alwaysRerun ()
            do! Locks.generate (lockOf b) (outOf b) cfg (descOf b name)
            do! Fingerprints.record (fingerprints b) (lockOf b name cfg) (descOf b name :: [ for r in d.ProjectRefs -> lockOf b r cfg ])
        }
        command "update-locks::(b:*)" {
            let! b = getRuleMatch "b"
            for cfg in configs do do! Locks.restoreAll (descriptions b) cfg (lockOf b) (descOf b)
            let! (Filelist ds) = getFiles (descriptions b)
            do! need [ for d in ds do for cfg in configs -> $"lock::{b}::{Path.GetFileName (Path.GetDirectoryName d.FullName)}::{cfg}" ]
        }
        "check::(b:*)" => recipe {
            let! b = getRuleMatch "b"
            do! Fingerprints.check (fingerprints b)
        }

        // (c) from the committed descriptions and locks only (fresh clone, CI)
        command "build::(b:*)::(cfg:*)" {
            let! b = getRuleMatch "b"
            let! cfg = getRuleMatch "cfg"
            do! Fingerprints.warn (fingerprints b)
            do! Locks.needAll (descriptions b) cfg (lockOf b) (outOf b)
        }
        "out/(b:*)/(cfg:*)/(fwk:*)/(name:*)/*.dll" ..> compile
        "out/(b:*)/(cfg:*)/(fwk:*)/(name:*)/*.xml" ..> compile
        command "clean::(b:*)" {
            let! b = getRuleMatch "b"
            do! rm { dir $"out/{b}" }
        }

        // ---- basic pack section, 32 lines, unchanged from round 1 except one needFiles: see ../ar-build-2.fsx lines 91-122
    ]
}
```
