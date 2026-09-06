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
// Once the next release is out, these two lines become `#r "nuget: Xake, <version>"`, the
// staging goes away and this script takes over as build.fsx.
//
// Testing and packing still shell out to the SDK: the test project is built by msbuild, and
// the nupkg carries a net462 asset fsc cannot produce here (see docs/session.md).
#r ".bootstrap/Xake.dll"
#r ".bootstrap/Xake.Dotnet.dll"

open Xake
open Xake.Dotnet
open Xake.Tasks

let vars = {|
    Version    = Var.create<string>(description = "Version number for the package, e.g. 1.2.3") |> withDefault "0.0.1"
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
    [ "Xake",        "src/core/Xake.fsproj"
      "Xake.Dotnet", "src/dotnet/Xake.Dotnet.fsproj" ]

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

/// The single NuGet package. It is packed from the leaf project: that one references the
/// core and pulls its assembly into the same nupkg, so both share one version (see
/// src/dotnet/Xake.Dotnet.fsproj).
let packageProject = "src/dotnet"
let packageName version = $"Xake.%s{version}.nupkg"

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
        }

        // ask msbuild what the project says: sources in compile order (the generated
        // assembly attributes first), the resolved references, the define symbols
        target "projects/(fwk:*)/(lib:*).json" {

            let! framework = getRuleMatch "fwk"
            let! name = getRuleMatch "lib"
            let! version = vars.Version
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
            let project = Fsproj.parse (evaluated name framework)

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
        command "pack" {
            let! version = vars.Version
            do! need ["out" </> packageName version]
        }

        target "out/Xake.(ver:*).nupkg" {
            let! ver = getRuleMatch "ver"
            do! dotnet [
                "pack"; packageProject
                "-c"; "Release"
                $"/p:Version={ver}"
                "--output"; "out/"
            ]
        }

        // push need pack to be explicitly called in advance
        command "push" {
            let! version = vars.Version
            let! nuget_key = vars.NUGET_KEY

            do! dotnet [
                "nuget"; "push"
                "out" </> packageName version
                "--source"; "https://www.nuget.org/api/v2/package"
                "--api-key"; nuget_key
            ]
        }
    ]
}
