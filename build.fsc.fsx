// Hermetic self-hosting build: both assemblies are compiled by the `fsc` task, with no
// `dotnet build` and no msbuild compiling anything. What to compile, what to reference and
// what to define is asked of msbuild once per project and cached: a file rule evaluates the
// `.fsproj` (`dotnet msbuild -getItem/-getProperty`), and everything downstream reads that
// result. So the answer is msbuild's own -- conditions, imports, the resolved reference list
// and the generated assembly attributes included -- while the compilation is ours.
//
// Bootstrap: none of this is in a published Xake package yet, so this script runs against a
// frozen copy of what build.fsx produces. The copy matters -- this script overwrites `out/`,
// and overwriting the assemblies fsi has loaded kills the run with a BadImageFormatException:
//
//     dotnet fsi build.fsx -- -- build                                  # through dotnet build
//     mkdir -p .bootstrap && cp out/netstandard2.0/*.dll .bootstrap/
//     dotnet fsi build.fsc.fsx -- -- build test
//
// `Fsproj` (the msbuild evaluation this script compiles from) lives in Xake.Hermetic.Dotnet,
// so the bootstrap is all three assemblies. Once Xake and Xake.Hermetic.Dotnet are on
// nuget.org, the three lines become `#r "nuget: Xake, <version>"` and
// `#r "nuget: Xake.Hermetic.Dotnet, <version>"`, the staging goes away and this script takes
// over as build.fsx (extraction-plan.md §4, E8).
//
// Testing and packing still shell out to the SDK: the test project is built by msbuild, and
// the nupkg carries a net462 asset fsc cannot produce here (see docs/session.md).
#r ".bootstrap/Xake.dll"
#r ".bootstrap/Xake.Dotnet.dll"
#r ".bootstrap/Xake.Hermetic.Dotnet.dll"

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet   // Fsproj
open Xake.Tasks

/// The literal `<Version>` a project file declares, if any.
let projectVersion (path: string) =
    let m = System.Text.RegularExpressions.Regex.Match(System.IO.File.ReadAllText path, "<Version>([^<]+)</Version>")
    if m.Success then m.Groups.[1].Value.Trim() else "0.0.1"

let vars = {|
    Version    = Var.create<string>(description = "Version number for the Xake package, e.g. 1.2.3") |> withDefault "0.0.1"
    // versioned on its own, as in build.fsx; the default is what its project file says
    HermeticVersion = Var.create<string>(description = "Version number for the Xake.Hermetic.Dotnet package, e.g. 0.1.0") |> withDefault (projectVersion "src/hermetic/Xake.Hermetic.Dotnet.fsproj")
    NUGET_KEY  = Var.env<string>(description = "API key for NuGet.org, required for pushing packages") |> withDefault ""
    TestFilter = Var.string(envVar = "FILTER", description = "Optional filter clause for test selection, e.g. 'MyNamespace.*Tests'")
|}

/// Only netstandard2.0 is compiled here. A net462 leg would need an FSharp.Core carrying a
/// net4x assembly, which the version the projects pin does not have -- the nupkg gets its
/// net462 asset from `dotnet pack`.
let frameworks = ["netstandard2.0"]

/// The libraries this script builds: the assembly each one produces, and the project it is
/// described by. Everything else about them is read out of the project file.
let libraries =
    [ "Xake",                 "src/core/Xake.fsproj"
      "Xake.Dotnet",          "src/dotnet/Xake.Dotnet.fsproj"
      "Xake.Hermetic.Dotnet", "src/hermetic/Xake.Hermetic.Dotnet.fsproj" ]

/// The version a library is compiled at: the package it ships in decides.
let versionOf name =
    if name = "Xake.Hermetic.Dotnet" then vars.HermeticVersion else vars.Version

let projectOf name = libraries |> List.find (fst >> (=) name) |> snd

/// The library a project file belongs to, if this script builds it.
let libraryOf projectFile =
    let fullPath (path: string) = System.IO.Path.GetFullPath path
    libraries |> List.tryFind (snd >> fullPath >> (=) (fullPath projectFile)) |> Option.map fst

/// Assembly and doc file every library produces, for every target framework.
let binaries =
    [ for name, _ in libraries do
      for fwk in frameworks do
      for ext in ["dll"; "xml"]
        -> $"out/%s{fwk}/%s{name}.%s{ext}"
    ]

/// What msbuild answered about a project, kept in the repository: paths in it are written
/// against `$(NuGetPackageRoot)` and `$(ProjectRoot)`, so the file is the same on every
/// machine and its diff shows what a project change did to the compilation. Its rule is the
/// only thing that runs msbuild, and only when the project file or the version changed.
let evaluated name framework = $"projects/%s{framework}/%s{name}.json"

/// A fileset made of exact paths, in the order given -- unlike a mask, this preserves the
/// compile order fsc is handed.
let filesetOf (files: string list) = files |> List.fold (fun fs file -> fs ++ file) Fileset.Empty

/// The two NuGet packages, versioned independently.
/// `Xake` is packed from src/dotnet, which pulls the core's assembly into the same nupkg (see
/// src/dotnet/Xake.Dotnet.fsproj). `Xake.Hermetic.Dotnet` is packed from src/hermetic and
/// depends on `Xake [3.4.0, 4.0.0)`.
///
/// Each package is packed into a folder of its own, `out/pkg/<id>/<id>.<version>.nupkg`: a
/// single mask such as `out/(id:*).(ver:*).nupkg` cannot tell `Xake.Hermetic.Dotnet.0.1.0` from
/// a Xake version `Hermetic.Dotnet.0.1.0` (and the last matching rule wins), and a folder per
/// id also lets CI push `out/pkg/<id>/*.nupkg` without picking up the other package.
let xakePackage = "Xake"
let hermeticPackage = "Xake.Hermetic.Dotnet"
let packagePath id version = $"out/pkg/%s{id}/%s{id}.%s{version}.nupkg"

/// The nuspec dependency the hermetic package must declare on `Xake`. The range is written by
/// the `XakeDependencyRange` target in src/hermetic/Xake.Hermetic.Dotnet.fsproj, which relies
/// on a private NuGet item name; if a future SDK renames it, pack silently writes `>= 1.0.0`.
let expectedXakeRange = "[3.4.0, 4.0.0)"

/// Reads the nuspec out of a nupkg and checks every `Xake` dependency carries the range;
/// returns the problems found (none means the package is right).
let checkXakeRange (nupkg: string) =
    use zip = System.IO.Compression.ZipFile.OpenRead nupkg
    let entry = zip.Entries |> Seq.find (fun e -> e.FullName.EndsWith ".nuspec" && not (e.FullName.Contains "/"))
    use reader = new System.IO.StreamReader(entry.Open())
    let doc = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd())
    let deps =
        doc.Descendants()
        |> Seq.filter (fun e -> e.Name.LocalName = "dependency" && e.Attribute(System.Xml.Linq.XName.Get "id").Value = "Xake")
        |> Seq.map (fun e -> e.Attribute(System.Xml.Linq.XName.Get "version").Value)
        |> List.ofSeq
    [ if List.isEmpty deps then yield "no dependency on package Xake"
      for v in deps do
          if v <> expectedXakeRange then yield $"Xake dependency is '%s{v}', expected '%s{expectedXakeRange}'" ]

let dotnet arglist = sh "dotnet" { args arglist; failonerror }

do xakeScript {
    filelog "build.log" Verbosity.Diag
    varschema vars

    rules [
        "main" <<< ["build"; "test"]

        "build" <== binaries
        "clean" => rm {dir "out"}

        command "test" {
            let! testFilter = vars.TestFilter
            let where = [ for f in Option.toList testFilter do yield $"--filter Name~\"{f}\"" ]

            do! sh "dotnet test src/tests -c Release" { args where; failonerror }
            do! sh "dotnet test src/hermetic.tests -c Release" { args where; failonerror }
        }

        // ask msbuild what the project says: sources in compile order (the generated
        // assembly attributes first), the resolved references, the define symbols
        target "projects/(fwk:*)/(lib:*).json" {

            let! framework = getRuleMatch "fwk"
            let! name = getRuleMatch "lib"
            let! version = versionOf name
            let! result = getTargetFile()

            let project = projectOf name
            do! needFiles (Filelist [File.make project])

            do! Fsproj.evaluate {
                Fsproj.EvalOptions.Default with
                    Project = project
                    Framework = framework
                    Configuration = "Release"
                    Properties = ["Version", version]
                    Output = result.FullName
            }
        }

        // one rule compiles them all: which library and which framework is asked for
        // is read off the target being built
        targets ["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"] {

            let! [outdll; outdoc] | OtherwiseFailErr "Expected two target files (dll and xml)" (outdoc, outdll)
                = getTargetFiles()
            let! framework = getRuleMatch "fwk"
            let! name = getRuleMatch "lib"

            do! need [evaluated name framework]
            let! project = Fsproj.load (evaluated name framework)

            // msbuild points a project reference at that project's own bin/; this build has
            // its own layout, so those references are swapped for the artifacts it produces.
            // They are not `need`ed here: the fsc task does that for everything it references.
            let referenced = project.ProjectRefs |> List.choose libraryOf
            let ours = [for library in referenced -> $"out/%s{framework}/%s{library}.dll"]

            let references =
                project.References
                |> List.filter (fun path ->
                    referenced |> List.contains (System.IO.Path.GetFileNameWithoutExtension path) |> not)

            do! fsc {
                targetfwk framework

                out outdll
                doc outdoc
                src (filesetOf project.Sources)
                refs (filesetOf (references @ ours))
                define project.Defines

                args [
                    "--optimize+"
                    "--debug:portable"
                    // same binary from the same sources, wherever it is built
                    "--deterministic+"
                ]
            }
        }

        (* Nuget publishing rules *)
        // both packages, each at its own version; the hermetic one waits for Xake (see below)
        command "pack" {
            let! version = vars.Version
            let! hermeticVersion = vars.HermeticVersion
            do! need [packagePath xakePackage version; packagePath hermeticPackage hermeticVersion]
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
            let! xakeVersion = vars.Version
            let! nupkg = getTargetFullName()

            // The hermetic package is compiled against the Xake assemblies at *Xake's* version:
            // a global /p:Version flows into project references, so they are built first at
            // that version (this also serializes the two packs, which share the base projects'
            // obj/) and pack does not rebuild them.
            do! need [packagePath xakePackage xakeVersion]
            do! dotnet [
                "build"; "src/dotnet"
                "-c"; "Release"
                $"/p:Version={xakeVersion}"
            ]
            do! dotnet [
                "pack"; "src/hermetic"
                "-c"; "Release"
                $"/p:Version={ver}"
                "-p:BuildProjectReferences=false"
                "--output"; $"out/pkg/%s{hermeticPackage}/"
            ]

            // the nuspec's dependency range is produced by a target relying on a private NuGet
            // item name, so it is asserted here rather than trusted
            match checkXakeRange nupkg with
            | [] -> do! trace Level.Info "%s: depends on Xake %s" (System.IO.Path.GetFileName nupkg) expectedXakeRange
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
