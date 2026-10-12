// DRAFT rev. 3 (fsc-rework, 2026-10-11): ActiveReports on the shape of build.fsc.fsx. Lives in an
// experiment folder beside the two AR repos (../ar-net-core-page and ../ar-net-core-dataengine, branch
// develop); nothing is written into the AR repos. C#, so the lock carries `csc` where Xake's has `fsc`.
// Brands are this script's knowledge, expressed by target prefixes: out/<brand>/..., <brand>/projects/,
// <brand>/locks/ and commands like `build::mescius`, `clean::gccn`. Import runs per brand under that
// brand's props (msbuild's own Brand switch picks src/<BRAND>.props), so descriptions and locks are
// stored per brand and no brand value is renamed by Xake.
#r "nuget: Xake, 3.6.0.24"
#r "nuget: Xake.Hermetic.Dotnet, 0.2.0.25"

open System.IO
open Xake
open Xake.Tasks
open Xake.Dotnet
open Xake.Hermetic.Dotnet

let vars = {|
    Version           = Var.create<string>(description = "Page packages version") |> withDefault "4.6.0"
    DataEngineVersion = Var.create<string>(description = "DataEngine packages version") |> withDefault "5.5.1"
    Feed = Var.create<string>(envVar = "LOCAL_FEED", description = "Shared folder: publish target and local feed") |> withDefault "../nuget-local"
|}

let projects = (!! "../ar-net-core-page/src/**/*.csproj" ++ "../ar-net-core-dataengine/src/*/*.csproj") -- "../**/*Tests*/*.csproj"
let props b = ["Brand", (string b).ToUpperInvariant(); "LocalBuild", "true"]       // b: mescius | gccn
let descriptions b = !! $"{b}/projects/*.json"
let descOf b (project: string) = $"{b}/projects/{Path.GetFileNameWithoutExtension project}.json"
let lockOf b name cfg = $"{b}/locks/{name}/{cfg}.json"
let outOf b cfg fwk name = $"out/{b}/{cfg}/{fwk}/{name}"    // a folder per project: assembly names are branded
let nupkgOf b name ver = $"out/{b}/pkg/{name}/{ver}.nupkg"
let versions = [ "../ar-net-core-dataengine/**", vars.DataEngineVersion; "**", vars.Version ]   // by repo

do xakeScript {
    varschema vars
    var "HERMETIC" "on"
    var "NUGET_PACKAGES" ".packages"

    "main" <<< ["build"]
    "build" <<< ["build::mescius"; "build::gccn"]

    command "import::(b:*)" {
        let! b = getRuleMatch "b"
        do! Description.import projects (descOf b) (props b)                                 // NEW (msbuild)
    }
    command "locks::(b:*)" {
        let! b = getRuleMatch "b"
        do! Lock.generateAll (descriptions b) ["Debug"; "Release"] (lockOf b) (outOf b)       // NEW
    }
    "check" => Fingerprints.check "xake.fingerprints"

    command "build::(b:*)" {
        let! b = getRuleMatch "b"
        do! Fingerprints.warn "xake.fingerprints"
        do! Lock.needAll (descriptions b) "Debug" (lockOf b) (outOf b)
        do! Lock.needAll (descriptions b) "Release" (lockOf b) (outOf b)
    }
    command "clean::(b:*)" {
        let! b = getRuleMatch "b"
        do! rm { dir $"out/{b}" }
    }
    target "out/(b:*)/(cfg:*)/(fwk:*)/(name:*)/*.dll" {
        let! b = getRuleMatch "b"
        let! cfg = getRuleMatch "cfg"
        let! fwk = getRuleMatch "fwk"
        let! name = getRuleMatch "name"
        do! Lock.compileAs (lockOf b name cfg) fwk versions
    }

    target "out/(b:*)/pkg/(name:*)/(ver:*).nupkg" {
        let! b = getRuleMatch "b"
        let! name = getRuleMatch "name"
        let! ver = getRuleMatch "ver"
        do! Package.nupkg (Description.find (descriptions b) name) [] ver (outOf b "Release")
    }
    command "pack::(b:*)" {
        let! b = getRuleMatch "b"
        do! Package.push vars.Feed (nupkgOf b) versions (descriptions b)
    }
}
