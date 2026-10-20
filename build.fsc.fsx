// Declarative self-build (docs/hermetic/lock.md): what a project is lives in src/<dir>/xake.project.json,
// how it compiles in locks/<name>/Release.json; both committed, written only by `import` and `update-locks`.
// `build` compiles from them with fsc, no msbuild; `check` fails where `build` only warns (xake.fingerprints).
// Bootstrap: the published packages (exact X.Y.Z.<run>, bumped only after a release lands on nuget.org).
#r "nuget: Xake, 3.10.0.35"
#r "nuget: Xake.Hermetic.Dotnet, 0.5.0.36"
#load "testjobs.fsx"   // the test jobs, shared with build.fsx

open Xake
open Xake.Tasks
open Xake.Dotnet
open Xake.Hermetic.Dotnet
open Testjobs

let vars = {|
    Version         = Var.create<string>(description = "Xake package version") |> withDefault "0.0.1"
    HermeticVersion = Var.create<string>(description = "Xake.Hermetic.Dotnet package version") |> withDefault "0.3.0"
    TestFilter      = Var.string(cliArg = "FILTER", envVar = "FILTER", description = "Runs only the tests whose name matches this regex, e.g. 'Lock'")
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

        // as build.fsx: the jobs of testjobs.fsx in parallel, the test projects built once by
        // msbuild (`dotnet build`); no --blame-hang here
        "test" <== [ for j in testJobs -> $"test::{testJobName j}" ]
        "test-build::(project:*)" => recipe {
            let! project = getRuleMatch "project"
            do! sh $"dotnet build {testProjectDir project} -c Release" {
                logprefix $"[test-build {project}]"
                stdoutlevel (fun l -> if l.Contains ": error " then Level.Error else Level.Info)
                failonerror }
        }
        "test::(project:*)/(slice:*)" => recipe {
            let! project = getRuleMatch "project"
            let! slice = getRuleMatch "slice"
            let job = testJob project slice
            do! need [ $"test-build::{project}" ]
            do! need (testJobNeeds job)   // the compiler server's users, one after another
            let! filter = vars.TestFilter
            resetTestJobDir job
            do! sh $"dotnet test {testProjectDir project} -c Release --no-build" {
                args (testArgs job filter)
                env (testJobEnv job)
                logprefix $"[{testJobName job}]"
                stdoutlevel (fun l -> if isTestSummary l then Level.Message else Level.Info)
                captureOutput (ToFile (Both, testJobDir job + "/console.log"))
                failonerror }
        }
        // pack (nupkg from out/Release, no second msbuild run) comes with Package.nupkg (fsc-rework PR 8)
    ]
}
