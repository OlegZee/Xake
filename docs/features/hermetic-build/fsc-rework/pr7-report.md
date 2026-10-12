# PR 7 report (rev. 2): self-build `build.fsc.fsx` from descriptions, locks, fingerprints (#50)

History: rev. 1 (2aeb075) ran on `build.fsx`'s dlls, shared `.xake`, hashed the R2R `fsc.dll` (macOS CI), set
`consolelog Loud`, carried a `compileAs` fix; the library parts landed via #51/#52/#53. Rev. 2 (`pr/build-fsc`, not
pushed): c67787a merge origin/dev (one conflict, fsproj comment, dev's taken), c60a7f9 bootstrap, a5d4d1d regenerated,
dccf7a2 CI, 30052e4 docs. `git diff origin/dev --stat -- src/`: only the 3 generated `xake.project.json`. `build.fsx`
is on `nuget: Xake, 3.7.0.26`. `.xake` no longer shared: own `.xake.fsc` (phony names collide; ignored by `.xake*`).
## The script (76 lines), verbatim
```fsharp
// Declarative self-build (docs/hermetic/lock.md): what a project is lives in src/<dir>/xake.project.json,
// how it compiles in locks/<name>/Release.json; both committed, written only by `import` and `update-locks`.
// `build` compiles from them with fsc, no msbuild; `check` fails where `build` only warns (xake.fingerprints).
// Bootstrap: the published packages (exact X.Y.Z.<run>, bumped only after a release lands on nuget.org).
#r "nuget: Xake, 3.7.0.26"
#r "nuget: Xake.Hermetic.Dotnet, 0.3.0.28"

open Xake
open Xake.Tasks
open Xake.Hermetic.Dotnet

let vars = {|
    Version         = Var.create<string>(description = "Xake package version") |> withDefault "0.0.1"
    HermeticVersion = Var.create<string>(description = "Xake.Hermetic.Dotnet package version") |> withDefault "0.3.0"
    TestFilter      = Var.string(envVar = "FILTER", description = "Test filter, e.g. 'Lock*'")
|}

let projects = [ "Xake", "src/core"; "Xake.Dotnet", "src/dotnet"; "Xake.Hermetic.Dotnet", "src/hermetic" ]
let descriptions = !! "src/*/xake.project.json"
let dirOf = dict projects
let descriptionOf name = $"{dirOf[name]}/xake.project.json"
let lockOf name cfg = $"locks/{name}/{cfg}.json"
let outOf cfg fwk (_: string) = $"out/{cfg}/{fwk}"
let versions = [ "src/hermetic/**", vars.HermeticVersion; "**", vars.Version ]   // first mask matching the project
let fingerprints = "xake.fingerprints"

// Own database (.xake.fsc): the phony targets (build, test, clean) share names with build.fsx's.
do xake { ExecOptions.Default with DbFileName = ".xake.fsc" } {
    filelog "build.log" Verbosity.Chatty
    varschema vars
    var "NUGET_PACKAGES" ".packages"

    rules [
        "main" <<< ["build"; "test"]
        "clean" => rm { dir "out/Release" }   // out/netstandard2.0 is build.fsx's

        // (a) project file -> description, under msbuild; records the msbuild files it read
        command "import" {
            let! imported = Import.descriptions [ for name, dir in projects -> $"{dir}/{name}.fsproj" ] Description.beside []
            for i in imported do do! Fingerprints.record fingerprints i.Description i.Inputs
        }
        // (b) description -> lock, referenced projects first; `-d LOCK_REFRESH=FSharp.Core` (or `*`) re-resolves.
        // Lock rules run one at a time (`for`): each records its line into the one fingerprint file.
        "lock::(name:*)::(cfg:*)" => recipe {
            let! name = getRuleMatch "name"
            let! cfg  = getRuleMatch "cfg"
            let! d = Description.load (descriptionOf name)
            for r in d.ProjectRefs do do! need [ $"lock::{r}::{cfg}" ]
            do! alwaysRerun ()
            do! Locks.generate lockOf outOf cfg (descriptionOf name)
            do! Fingerprints.record fingerprints (lockOf name cfg) (descriptionOf name :: [ for r in d.ProjectRefs -> lockOf r cfg ])
        }
        command "update-locks" { for name, _ in projects do do! need [ $"lock::{name}::Release" ] }
        "check" => Fingerprints.check fingerprints

        // (c) every described project's Release outputs, from the committed files only (fresh clone, CI)
        command "build" {
            do! Fingerprints.warn fingerprints
            do! Locks.needAll descriptions "Release" lockOf outOf
        }
        target "out/(cfg:*)/(fwk:*)/(name:*).dll" {
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            do! Locks.compileAs (lockOf name cfg) fwk versions
        }

        command "test" {   // as build.fsx: dotnet test (msbuild builds the test projects)
            let! filter = vars.TestFilter
            let where = [ for f in Option.toList filter -> $"--filter Name~\"{f}\"" ]
            do! sh "dotnet test src/tests -c Release" { args where; failonerror }
            do! sh "dotnet test src/hermetic.tests -c Release" { args where; failonerror }
        }
        // pack (nupkg from out/Release, no second msbuild run) comes with Package.nupkg (fsc-rework PR 8)
    ]
}
```

## Committed / lock diff after regeneration
`import update-locks check` on 0.3.0.28, then `update-locks -d LOCK_REFRESH=Xake` (the kept Xake 3.6.0.24 stayed
outside dev's new range `[3.7.0.26, 3.8)`, Q1). Diff vs rev. 1: all 6 entries `Compiler.Sha256` "b79232...af4a" -> "";
hermetic lock: `xake/3.6.0.24/lib/*/Xake{,.Dotnet}.dll` and package `Xake 3.6.0.24` -> `3.7.0.26` (new hashes);
`src/hermetic/xake.project.json` range -> `[3.7.0.26, 3.8)`; `xake.fingerprints` 4 lines. Re-run writes nothing.

## Fresh-clone proof (`git clone --branch pr/build-fsc <worktree> .../tmp/fresh-clone-2`; no .xake/.packages/out)
```
$ dotnet fsi build.fsc.fsx -- -- build check -d NUGET_FETCH=on     # XAKE build tool 3.7.0.26, 7.1 s, exit 0
5> restoring 5 package(s) into '.../.packages' ... exitcode: 0        (3 restores)
6> compiling 'Xake' (fsc 15.2.401-servicing.26423.113)                (6 compiles, 3 projects x 2 fwks)
```
No `build.fsx` bootstrap, no msbuild of src/* (no `project.assets.json` created). Run 2 without `-d` recompiled all
6 ("Global script variable NUGET_FETCH was changed 'Some "on"'->'None'", vars are tracked); run 3, same flags:
`Skipped ... (up to date)` x6, 0.07 s. Stale (`<!-- stale -->` appended to src/core/Xake.fsproj), default console:
`build` exit 0 with `[MSG] 1> Fingerprints: warning: src/core/xake.project.json is stale: its inputs changed since
it was generated`; `check` exit 2 "Fingerprints check failed (xake.fingerprints): ... is stale". Clean after revert.

## CI (job `build-fsc`; the `build` job and publish.yml untouched; not run, no push)
ubuntu-latest, SDK `10.0.401`, one step `dotnet fsi build.fsc.fsx -- -- build check -d NUGET_FETCH=on`, uploads
build.log. Comment: locks hold for SDK 10.0.401 (version), any OS; HERMETIC off.

## Commands (exit 0; each `Passed! Failed 0, Passed 324, Skipped 1, Total 325 - tests.dll`, 206/206 hermetic.tests)
worktree `dotnet fsi build.fsx -- -- build test`; clone `dotnet fsi build.fsc.fsx -- -- test`; clone
`dotnet fsi build.fsx -- -- build test` (build of record). Docs: readme "The declarative build"; docs/session.md
Release prep (3.7.0.26 / 0.3.0.28, range) and "The fsc build" (packages, `.xake.fsc`, empty Sha256, warn at Message); docs/devprocess.md Bootstrapping note. lock.md: unchanged.

Deviations: (1) HERMETIC off (global.json floats `latestMajor`; a newer preinstalled 10.0.x on the runner would
win); (2) 59-byte cross-folder dll difference, described only; (3) `-d FILTER=` misses `TestFilter` (pre-existing);
(4) `update-locks` still serial though `record` is thread-safe since #51; (5) HermeticVersion default -> 0.3.0.
Questions: 1. A kept version outside the description's new range stays until `LOCK_REFRESH`: drop it instead (library)?
2. Parallelise `update-locks` now? 3. Pin the CI SDK tighter so roll-forward cannot pick a newer 10.0.x?
