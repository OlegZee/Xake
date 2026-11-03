#r "nuget: Xake, 3.15.0"
// #r "out/netstandard2.0/Xake.dll"
#load "testjobs.fsx"

// `test` runs the test jobs of testjobs.fsx in parallel (`test::tests/jobs`, `test::hermetic/run`,
// ...; one runs alone by naming it). The test projects are built once (`test-build::<project>`),
// each job runs `dotnet test --no-build` on its slice and writes into out/test/<project>/<slice>/
// (the trx, the blame sequence file and console.log, its whole output); the console gets the
// summary lines. FILTER (-d FILTER=Rm) narrows every job to its tests whose name matches Rm
// (NUnit's `name =~ 'Rm'`, a regex; no quote in it); a job left with no test passes.

open Xake
open Xake.Tasks
open Testjobs

/// The literal `<Version>` a project file declares, if any.
let projectVersion (path: string) =
    let m = System.Text.RegularExpressions.Regex.Match(System.IO.File.ReadAllText path, "<Version>([^<]+)</Version>")
    if m.Success then m.Groups.[1].Value.Trim() else "0.0.1"

let vars = {|
    // The env names are explicit: publish.yml exports VERSION, and from Xake 4.0 a field without
    // `envVar` reads XAKE__<NAME> (XAKE__VERSION) instead.
    Version    = Var.create<string>(envVar = "VERSION", description = "Version number for the Xake package, e.g. 1.2.3") |> withDefault "0.0.1"
    // The assembly version of Xake.Hermetic.Dotnet (not packed here any more): -d HermeticVersion=...
    // or $HERMETIC_VERSION. The default is the <Version> its project file carries.
    HermeticVersion = Var.create<string>(envVar = "HERMETIC_VERSION", description = "Version number for the Xake.Hermetic.Dotnet assembly, e.g. 0.1.0") |> withDefault (projectVersion "src/hermetic/Xake.Hermetic.Dotnet.fsproj")
    NUGET_KEY  = Var.env<string>(envVar = "NUGET_KEY", description = "API key for NuGet.org, required for pushing packages") |> withDefault "" |> secret
    TestFilter = Var.string(cliArg = "FILTER", envVar = "FILTER", description = "Runs only the tests whose name matches this regex, e.g. 'Rm'")
|}

let frameworks = ["netstandard2.0" (*; "net462" *)]

/// A library this script builds. `Needs` are the libraries (of this script) it is compiled
/// against. Only Xake is packed (see `xakePackage`); Xake.Hermetic.Dotnet has a version of its
/// own (`HermeticVersion`), the other two the Xake version.
type Library = { Name: string; Dir: string; Needs: string list }

let libraries =
    [ { Name = "Xake";                 Dir = "src/core";     Needs = [] }
      { Name = "Xake.Dotnet";          Dir = "src/dotnet";   Needs = ["Xake"] }
      { Name = "Xake.Hermetic.Dotnet"; Dir = "src/hermetic"; Needs = ["Xake"; "Xake.Dotnet"] } ]

let library name = libraries |> List.find (fun lib -> lib.Name = name)

/// Assembly and doc file a library produces, for every target framework.
let binaries name =
    [ for fwk in frameworks do
      for ext in ["dll"; "xml"]
        -> $"out/%s{fwk}/%s{name}.%s{ext}"
    ]

/// The NuGet package of this repository: `Xake`, the engine alone, packed from src/core. From
/// 4.0 Xake.Dotnet ships from its own repository (dotnet-split S-D14): src/dotnet is not packed,
/// and neither is src/hermetic (Xake.Hermetic.Dotnet ended at 0.10.0, S-D2); both still build
/// and test here until the split's removal step (S7).
///
/// The package goes into a folder of its own, `out/pkg/Xake/Xake.<version>.nupkg`, which CI
/// pushes.
let xakePackage = "Xake"
let packagePath id version = $"out/pkg/%s{id}/%s{id}.%s{version}.nupkg"

let versionOf (lib: Library) =
    if lib.Name = "Xake.Hermetic.Dotnet" then vars.HermeticVersion else vars.Version

/// The `Xake` nupkg must carry Xake.dll and Xake.xml for every target framework and nothing of
/// Xake.Dotnet (S-D14); returns the problems found (none means the package is right).
let checkXakePackage (nupkg: string) =
    use zip = System.IO.Compression.ZipFile.OpenRead nupkg
    let entries = [ for e in zip.Entries -> e.FullName ]
    [ for fwk in ["net462"; "netstandard2.0"] do
        for file in ["Xake.dll"; "Xake.xml"] do
            if not (List.contains $"lib/{fwk}/{file}" entries) then yield $"no lib/{fwk}/{file}"
      for e in entries do
        if e.StartsWith "lib/" && System.IO.Path.GetFileName(e).StartsWith "Xake.Dotnet." then
            yield $"carries {e}" ]

/// The version arguments of a build of src/hermetic: its own version, and the Xake version of
/// the referenced src/dotnet and src/core. Not /p:Version, which would give the referenced
/// projects the hermetic version.
let hermeticVersionArgs (hermeticVersion: string) = recipe {
    let! xakeVersion = vars.Version
    return [ "-p:HermeticVersion=" + hermeticVersion; "-p:XakePackageVersion=" + xakeVersion ]
}

let dotnet arglist = sh "dotnet" { args arglist; failonerror }

/// One msbuild at a time over the in-repo projects: every `dotnet build`/`pack` here also
/// builds src/core (and src/dotnet) through project references, into the same
/// src/core/bin/Release and obj folders, and two at once collide (MSB4018 GenerateDepsFile,
/// "Xake.deps.json is being used by another process", the two test builds of `test` in the
/// v4.0.0-preview.1 publish run). The test jobs (`dotnet test --no-build`) still run in parallel.
let msbuild = Resource.newResource "msbuild" 1
let oneMsbuild body = withResource msbuild 1 body

do xakeScript {
    filelog "build.log" Diag
    varschema vars

    rules [
        "main" <<< ["build"; "test"]

        "build" <== List.collect (fun lib -> binaries lib.Name) libraries
        "clean" => rm {dir "out"}

        "test" <== [ for j in testJobs -> $"test::{testJobName j}" ]

        // all jobs of one test project, e.g. `test-project::hermetic`
        "test-project::(project:*)" => recipe {
            let! project = getRuleMatch "project"
            do! need [ for j in testJobs do if j.Project = project then $"test::{testJobName j}" ]
        }

        // a test project, built once for all its jobs
        "test-build::(project:*)" => recipe {
            let! project = getRuleMatch "project"
            do! oneMsbuild (sh $"dotnet build {testProjectDir project} -c Release" {
                logprefix $"[test-build {project}]"
                stdoutlevel (fun l -> if l.Contains ": error " then Level.Error else Level.Info)
                failonerror })
        }

        "test::(project:*)/(slice:*)" => recipe {
            let! project = getRuleMatch "project"
            let! slice = getRuleMatch "slice"
            let job = testJob project slice
            do! need [ $"test-build::{project}" ]
            do! need (testJobNeeds job)   // the compiler server's users, one after another
            let! testFilter = vars.TestFilter

            // a test running longer than 10 minutes is a hang: the run is stopped and the
            // test named (the sequence file in the job's folder) instead of waiting forever
            let hang = [ "--blame-hang"; "--blame-hang-timeout"; "10m"; "--blame-hang-dump-type"; "none" ]
            resetTestJobDir job
            do! sh $"dotnet test {testProjectDir project} -c Release --no-build" {
                args (hang @ testArgs job testFilter)
                env (testJobEnv job)
                logprefix $"[{testJobName job}]"
                stdoutlevel (fun l -> if isTestSummary l then Level.Message else Level.Info)
                captureOutput (ToFile (Both, testJobDir job + "/console.log"))
                failonerror }
        }

        // one rule compiles them all: which library and which framework is asked for
        // is read off the target being built
        targets ["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"] {

            let! framework = getRuleMatch "fwk"
            let! lib = getRuleMatch "lib" |> Recipe.map library

            let! allFiles = getFiles <| fileset {
                basedir lib.Dir
                includes $"%s{lib.Name}.fsproj"
                includes "**/*.fs"
            }

            do! needFiles allFiles
            do! need [for dep in lib.Needs -> $"out/%s{framework}/%s{dep}.dll"]
            let! version = versionOf lib
            let! versionArgs =
                if lib.Name = "Xake.Hermetic.Dotnet" then hermeticVersionArgs version
                else recipe { return [ "/p:Version=" + version ] }

            do! oneMsbuild (dotnet ([
                "build"
                lib.Dir
                "--configuration"; "Release"
                "--framework"; framework
                "--output"; "./out/" + framework
              ] @ versionArgs))
        }

        (* Nuget publishing rules *)
        // the `Xake` package (the engine alone), at this build's version (-d Version)
        command "pack" {
            let! version = vars.Version
            do! need [packagePath xakePackage version]
        }

        target $"out/pkg/%s{xakePackage}/%s{xakePackage}.(ver:*).nupkg" {
            let! ver = getRuleMatch "ver"
            let! nupkg = getTargetFullName()
            do! oneMsbuild (dotnet [
                "pack"; "src/core"
                "-c"; "Release"
                $"/p:Version={ver}"
                "--output"; $"out/pkg/%s{xakePackage}/"
            ])

            match checkXakePackage nupkg with
            | [] -> do! trace Level.Info "%s: the engine only" (System.IO.Path.GetFileName nupkg)
            | problems ->
                System.IO.File.Delete nupkg
                failwithf "%s: %s" nupkg (String.concat "; " problems)
        }

        // push needs pack to be explicitly called in advance
        command "push" {
            let! version = vars.Version
            let! nuget_key = vars.NUGET_KEY
            do! dotnet [
                "nuget"; "push"; packagePath xakePackage version
                "--source"; "https://www.nuget.org/api/v2/package"
                "--api-key"; nuget_key
            ]
        }
    ]
}
