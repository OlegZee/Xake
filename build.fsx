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
    Version    = Var.create<string>(description = "Version number for the Xake package, e.g. 1.2.3") |> withDefault "0.0.1"
    // Xake.Hermetic.Dotnet is versioned on its own (0.x, may break): -d HermeticVersion=... or
    // $HERMETIC_VERSION. The default is the <Version> its project file carries.
    HermeticVersion = Var.create<string>(description = "Version number for the Xake.Hermetic.Dotnet package, e.g. 0.1.0") |> withDefault (projectVersion "src/hermetic/Xake.Hermetic.Dotnet.fsproj")
    NUGET_KEY  = Var.env<string>(description = "API key for NuGet.org, required for pushing packages") |> withDefault "" |> secret
    TestFilter = Var.string(cliArg = "FILTER", envVar = "FILTER", description = "Runs only the tests whose name matches this regex, e.g. 'Rm'")
|}

let frameworks = ["netstandard2.0" (*; "net462" *)]

/// A library this script builds. `Needs` are the libraries (of this script) it is compiled
/// against, `Package` is the NuGet package it ships in (which decides its version).
/// Xake.Hermetic.Dotnet needs none of them: it references the published `Xake` package.
type Library = { Name: string; Dir: string; Needs: string list; Package: string }

let libraries =
    [ { Name = "Xake";                 Dir = "src/core";     Needs = [];                      Package = "Xake" }
      { Name = "Xake.Dotnet";          Dir = "src/dotnet";   Needs = ["Xake"];                Package = "Xake" }
      { Name = "Xake.Hermetic.Dotnet"; Dir = "src/hermetic"; Needs = ["Xake"; "Xake.Dotnet"]; Package = "Xake.Hermetic.Dotnet" } ]

let library name = libraries |> List.find (fun lib -> lib.Name = name)

/// Assembly and doc file a library produces, for every target framework.
let binaries name =
    [ for fwk in frameworks do
      for ext in ["dll"; "xml"]
        -> $"out/%s{fwk}/%s{name}.%s{ext}"
    ]

/// The two NuGet packages, versioned independently.
/// `Xake` is packed from src/dotnet, which pulls the core's assembly into the same nupkg (see
/// src/dotnet/Xake.Dotnet.fsproj). `Xake.Hermetic.Dotnet` is packed from src/hermetic, which
/// builds on the in-repo Xake until 4.0 (project references): its `Xake` dependency is the
/// Xake version of this build (-d Version), see `hermeticVersionArgs`.
///
/// Each package is packed into a folder of its own, `out/pkg/<id>/<id>.<version>.nupkg`: a
/// single mask such as `out/(id:*).(ver:*).nupkg` cannot tell `Xake.Hermetic.Dotnet.0.1.0` from
/// a Xake version `Hermetic.Dotnet.0.1.0` (and the last matching rule wins), and a folder per
/// id also lets CI push `out/pkg/<id>/*.nupkg` without picking up the other package.
let xakePackage = "Xake"
let hermeticPackage = "Xake.Hermetic.Dotnet"
let packagePath id version = $"out/pkg/%s{id}/%s{id}.%s{version}.nupkg"

let versionOf package =
    if package = hermeticPackage then vars.HermeticVersion else vars.Version

/// The nuspec dependencies of the hermetic package must name `Xake` (the package that carries
/// both Xake.dll and Xake.Dotnet.dll) and never `Xake.Dotnet`, which is not a package; returns
/// the problems found (none means the package is right). Its version is this build's Xake version.
let checkXakeDependency (nupkg: string) =
    use zip = System.IO.Compression.ZipFile.OpenRead nupkg
    let entry = zip.Entries |> Seq.find (fun e -> e.FullName.EndsWith ".nuspec" && not (e.FullName.Contains "/"))
    use reader = new System.IO.StreamReader(entry.Open())
    let doc = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd())
    let ids =
        doc.Descendants()
        |> Seq.filter (fun e -> e.Name.LocalName = "dependency")
        |> Seq.map (fun e -> e.Attribute(System.Xml.Linq.XName.Get "id").Value)
        |> List.ofSeq
    [ if not (List.contains "Xake" ids) then yield "no dependency on package Xake"
      if List.contains "Xake.Dotnet" ids then yield "depends on Xake.Dotnet, which is not a package" ]

/// The version arguments of a build or pack of src/hermetic: its own version, and the Xake
/// version of the referenced src/dotnet and src/core (and so of the package's `Xake`
/// dependency). Not /p:Version, which would give the referenced projects the hermetic version.
let hermeticVersionArgs (hermeticVersion: string) = recipe {
    let! xakeVersion = vars.Version
    return [ "-p:HermeticVersion=" + hermeticVersion; "-p:XakePackageVersion=" + xakeVersion ]
}

let dotnet arglist = sh "dotnet" { args arglist; failonerror }

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
            let! version = versionOf lib.Package
            let! versionArgs =
                if lib.Package = hermeticPackage then hermeticVersionArgs version
                else recipe { return [ "/p:Version=" + version ] }

            do! dotnet ([
                "build"
                lib.Dir
                "--configuration"; "Release"
                "--framework"; framework
                "--output"; "./out/" + framework
              ] @ versionArgs)
        }

        (* Nuget publishing rules *)
        // both packages, each at its own version; until Xake 4.0 the hermetic one builds on the
        // in-repo Xake and depends on the Xake version of this build (-d Version)
        command "pack" {
            let! version = vars.Version
            let! hermeticVersion = vars.HermeticVersion
            do! need [packagePath xakePackage version; packagePath hermeticPackage hermeticVersion]
        }

        command "pack-hermetic" {
            let! hermeticVersion = vars.HermeticVersion
            do! need [packagePath hermeticPackage hermeticVersion]
        }

        target $"out/pkg/%s{xakePackage}/%s{xakePackage}.(ver:*).nupkg" {
            let! ver = getRuleMatch "ver"
            do! dotnet [
                "pack"; "src/dotnet"
                "-c"; "Release"
                $"/p:Version={ver}"
                "--output"; $"out/pkg/%s{xakePackage}/"
            ]
        }

        target $"out/pkg/%s{hermeticPackage}/%s{hermeticPackage}.(ver:*).nupkg" {
            let! ver = getRuleMatch "ver"
            let! nupkg = getTargetFullName()
            let! versionArgs = hermeticVersionArgs ver

            // src/hermetic references src/core and src/dotnet (until Xake 4.0): the nuspec
            // depends on package `Xake` at this build's Xake version
            do! dotnet ([
                "pack"; "src/hermetic"
                "-c"; "Release"
                "--output"; $"out/pkg/%s{hermeticPackage}/"
              ] @ versionArgs)

            match checkXakeDependency nupkg with
            | [] -> do! trace Level.Info "%s: depends on package Xake" (System.IO.Path.GetFileName nupkg)
            | problems ->
                System.IO.File.Delete nupkg
                failwithf "%s: %s" nupkg (String.concat "; " problems)
        }

        // push needs pack to be explicitly called in advance; one command per package, since
        // they are released independently
        command "push" {
            let! version = vars.Version
            let! nuget_key = vars.NUGET_KEY
            do! dotnet [
                "nuget"; "push"; packagePath xakePackage version
                "--source"; "https://www.nuget.org/api/v2/package"
                "--api-key"; nuget_key
            ]
        }

        command "push-hermetic" {
            let! version = vars.HermeticVersion
            let! nuget_key = vars.NUGET_KEY
            do! dotnet [
                "nuget"; "push"; packagePath hermeticPackage version
                "--source"; "https://www.nuget.org/api/v2/package"
                "--api-key"; nuget_key
            ]
        }
    ]
}
