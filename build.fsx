#r "nuget: Xake, 3.15.0"
// #r "out/netstandard2.0/Xake.dll"

// Builds Xake (src/core), tests it (src/tests) and packs the `Xake` package.
// The .NET tasks and the hermetic library build in their own repository, OlegZee/Xake.Dotnet.
//
// Targets: main (= build, then test), build, test (test::jobs and test::rest in parallel),
// test-build, pack, push, clean. Variables: --help.

open Xake
open Xake.Tasks

let vars = {|
    // The env names are explicit: publish.yml exports VERSION, and from Xake 4.0 a field without
    // `envVar` reads XAKE__<NAME> (XAKE__VERSION) instead.
    Version    = Var.create<string>(envVar = "VERSION", description = "Version number for the Xake package, e.g. 1.2.3") |> withDefault "0.0.1"
    NUGET_KEY  = Var.env<string>(envVar = "NUGET_KEY", description = "API key for NuGet.org, required for pushing packages") |> withDefault "" |> secret
    TestFilter = Var.string(cliArg = "FILTER", envVar = "FILTER", description = "Runs only the tests whose name matches this regex, e.g. 'Rm'")
|}

/// One msbuild at a time: `build`, `test-build` and `pack` all build src/core into the same
/// bin/Release and obj folders, and two at once collide (MSB4018 GenerateDepsFile, "Xake.deps.json
/// is being used by another process", the v4.0.0-preview.1 publish run). The test runs
/// (`dotnet test --no-build`) do not take it.
let msbuild = Resource.newResource "msbuild" 1
let dotnet arglist = withResource msbuild 1 (sh "dotnet" { args arglist; failonerror })

/// `test` runs src/tests as two jobs in parallel (about 20 s instead of 36 s): `test::jobs` is the
/// delegated and resource fixture, slow on sleeps, `test::rest` every other fixture. Each writes
/// its trx and its whole output (console.log) into out/test/<job>/; the console gets the summary
/// lines. The NUnit selection is `NUnit.Where`, not vstest's `--filter`, which fails on a class
/// name with a space in it.
let delegatedFixture = "class == 'Tests.Delegated and resource tests'"
let testJobs = [ "jobs", delegatedFixture; "rest", $"!({delegatedFixture})" ]

/// The `Xake` nupkg carries Xake.dll and Xake.xml for every target framework and nothing else
/// under lib/; returns the problems found.
let checkPackage (nupkg: string) =
    use zip = System.IO.Compression.ZipFile.OpenRead nupkg
    let entries = [ for e in zip.Entries -> e.FullName ]
    [ for fwk in ["net462"; "netstandard2.0"] do
        for file in ["Xake.dll"; "Xake.xml"] do
            if not (List.contains $"lib/{fwk}/{file}" entries) then yield $"no lib/{fwk}/{file}"
      for e in entries do
        let file = System.IO.Path.GetFileName e
        if e.StartsWith "lib/" && file <> "Xake.dll" && file <> "Xake.xml" then yield $"carries {e}" ]

let nupkg version = $"out/pkg/Xake/Xake.{version}.nupkg"

do xakeScript {
    filelog "build.log" Diag
    varschema vars

    rules [
        "main" <<< ["build"; "test"]
        "clean" => rm {dir "out"}

        // netstandard2.0 only: the net462 asset comes from `dotnet pack`
        "build" <== ["out/netstandard2.0/Xake.dll"; "out/netstandard2.0/Xake.xml"]

        targets ["out/netstandard2.0/Xake.dll"; "out/netstandard2.0/Xake.xml"] {
            // not obj/: `dotnet build` and `pack` write the version into obj/**/Xake.AssemblyInfo.fs
            let! sources = getFiles (fileset { basedir "src/core"; includes "Xake.fsproj"; includes "**/*.fs"; excludes "obj/**" })
            do! needFiles sources
            let! version = vars.Version
            do! dotnet ["build"; "src/core"; "-c"; "Release"; "-f"; "netstandard2.0"; "-o"; "out/netstandard2.0"; $"/p:Version={version}"]
        }

        "test" <== [ for name, _ in testJobs -> $"test::{name}" ]

        "test-build" => dotnet ["build"; "src/tests"; "-c"; "Release"]

        "test::(job:*)" => recipe {
            let! name = getRuleMatch "job"
            let where = testJobs |> List.find (fst >> (=) name) |> snd
            let! filter = vars.TestFilter
            do! need ["test-build"]

            let dir = $"out/test/{name}"
            if System.IO.Directory.Exists dir then System.IO.Directory.Delete(dir, true)
            System.IO.Directory.CreateDirectory dir |> ignore
            let where = match filter with Some f -> $"({where}) && name =~ '{f}'" | None -> where
            // a test running longer than 10 minutes is a hang: the run stops and names it
            do! sh "dotnet test src/tests -c Release --no-build" {
                args [ "--blame-hang"; "--blame-hang-timeout"; "10m"; "--blame-hang-dump-type"; "none"
                       "--results-directory"; dir; "--logger"; "\"trx;LogFileName=test.trx\""
                       "--"; $"\"NUnit.Where={where}\"" ]
                logprefix $"[test::{name}]"
                stdoutlevel (fun l ->
                    let l = l.TrimStart()
                    if l.StartsWith "Passed!" || l.StartsWith "Failed!" || l.StartsWith "Skipped!" || l.StartsWith "Failed "
                    then Level.Message else Level.Info)
                captureOutput (ToFile (Both, dir + "/console.log"))
                failonerror }
        }

        // the `Xake` package at this build's version (-d Version), checked for its content
        command "pack" {
            let! version = vars.Version
            do! need [nupkg version]
        }

        target "out/pkg/Xake/Xake.(ver:*).nupkg" {
            let! ver = getRuleMatch "ver"
            let! file = getTargetFullName()
            do! dotnet ["pack"; "src/core"; "-c"; "Release"; $"/p:Version={ver}"; "--output"; "out/pkg/Xake/"]
            match checkPackage file with
            | [] -> do! trace Level.Info "%s: the engine only" (System.IO.Path.GetFileName file)
            | problems ->
                System.IO.File.Delete file
                failwithf "%s: %s" file (String.concat "; " problems)
        }

        // push needs pack to be explicitly called in advance
        command "push" {
            let! version = vars.Version
            let! key = vars.NUGET_KEY
            do! sh "dotnet nuget push" {
                args [nupkg version; "--source"; "https://www.nuget.org/api/v2/package"; "--api-key"; key]
                failonerror }
        }
    ]
}
