// DRAFT rev. 3 (fsc-rework, 2026-10-11): the desired self-build, not runnable; `// NEW` = missing (gaps.md).
// (a) src/<dir>/xake.project.json: what a project is; written by the explicit `import` (msbuild).
// (b) locks/<project>/<cfg>.json: one lock per project x configuration; written by the explicit `locks`
//     (packages resolved by msbuild restore; pinned versions stay unless LOCK_REFRESH names them).
// (c) `build`: from the committed descriptions and locks only, the same locally and on CI. `import` and
//     `locks` add one line per written file to xake.fingerprints; `build` warns when one is stale.
#r "nuget: Xake, 3.6.0.24"
#r "nuget: Xake.Hermetic.Dotnet, 0.2.0.25"

open Xake
open Xake.Tasks
open Xake.Dotnet
open Xake.Hermetic.Dotnet

let vars = {|
    Version         = Var.create<string>(description = "Xake package version") |> withDefault "0.0.1"
    HermeticVersion = Var.create<string>(description = "Xake.Hermetic.Dotnet package version") |> withDefault "0.2.0"
    Feed       = Var.create<string>(envVar = "LOCAL_FEED", description = "Shared folder: publish target and local feed") |> withDefault "../nuget-local"
    TestFilter = Var.string(envVar = "FILTER", description = "Test filter, e.g. 'Lock*'")
|}

let projects = !! "src/*/*.fsproj" -- "src/*tests/*.fsproj"
let descriptions = !! "src/*/xake.project.json"
let lockOf name cfg = $"locks/{name}/{cfg}.json"
let outOf cfg fwk (_: string) = $"out/{cfg}/{fwk}"          // layout A: a folder per (cfg, fwk); written into ref paths at (b)
let nupkgOf name ver = $"out/pkg/{name}/{ver}.nupkg"
let versions = [ "src/hermetic/**", vars.HermeticVersion; "**", vars.Version ]   // first mask matching the project path
let metadata = ["copyright", "(c) OlegZee 2014-2026"; "authors", "OlegZee"]   // over the descriptions'

do xakeScript {
    varschema vars
    var "HERMETIC" "on"
    var "NUGET_PACKAGES" ".packages"

    "main" <<< ["build"; "test"]
    "clean" => rm { dir "out" }

    // (a) every project file -> its description, under msbuild; a file is rewritten only when its content changes
    command "import" { do! Description.import projects Description.beside [] }                // NEW
    // (b) every description -> its Release lock, referenced projects first; each ref's config and TFM chosen
    //     here and written as its unhashed output path (outOf); `-d LOCK_REFRESH=FSharp.Core` (or `*`) re-resolves
    command "locks" { do! Lock.generateAll descriptions ["Release"] lockOf outOf }             // NEW
    "check" => Fingerprints.check "xake.fingerprints"                    // NEW: fails where `build` only warns

    // (c) every described project's Release outputs; a description without a lock is an error
    command "build" {
        do! Fingerprints.warn "xake.fingerprints"                                             // NEW
        do! Lock.needAll descriptions "Release" lockOf outOf                                  // NEW
    }
    // the .xml (when the lock asks for doc) is a side output of the .dll, not a target
    target "out/(cfg:*)/(fwk:*)/(name:*).dll" {
        let! cfg = getRuleMatch "cfg"
        let! fwk = getRuleMatch "fwk"
        let! name = getRuleMatch "name"
        do! Lock.compileAs (lockOf name cfg) fwk versions                                     // NEW
    }

    command "test" {
        let! filter = vars.TestFilter
        let where = [ for f in Option.toList filter -> $"--filter Name~\"{f}\"" ]
        do! sh "dotnet test src/tests -c Release" { args where; failonerror }
        do! sh "dotnet test src/hermetic.tests -c Release" { args where; failonerror }
    }

    // nupkg from the Release binaries of (c), no second msbuild run; `pack` copies them into the local feed
    target "out/pkg/(name:*)/(ver:*).nupkg" {
        let! name = getRuleMatch "name"
        let! ver = getRuleMatch "ver"
        do! Package.nupkg (Description.find descriptions name) metadata ver (outOf "Release")  // NEW
    }
    command "pack" { do! Package.push vars.Feed nupkgOf versions descriptions }               // NEW
}
