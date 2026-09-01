#r "nuget: Xake, 3.0.1"
// #r "out/netstandard2.0/Xake.dll"

open Xake
open Xake.Tasks

let vars = {|
    Version    = Var.create<string>(description = "Version number for the package, e.g. 1.2.3") |> withDefault "0.0.1"
    NUGET_KEY  = Var.env<string>(description = "API key for NuGet.org, required for pushing packages") |> withDefault ""
    TestFilter = Var.string(envVar = "FILTER", description = "Optional filter clause for test selection, e.g. 'MyNamespace.*Tests'")
|}

let frameworks = ["netstandard2.0" (*; "net46" *)]

/// A library this script builds and publishes. `Needs` are the libraries it is compiled against.
type Library = { Name: string; Dir: string; Needs: string list }

let libraries =
    [ { Name = "Xake";        Dir = "src/core";   Needs = [] }
      { Name = "Xake.Dotnet"; Dir = "src/dotnet"; Needs = ["Xake"] } ]

let library name = libraries |> List.find (fun lib -> lib.Name = name)

/// Assembly and doc file a library produces, for every target framework.
let binaries name =
    [ for fwk in frameworks do
      for ext in ["dll"; "xml"]
        -> $"out/%s{fwk}/%s{name}.%s{ext}"
    ]

/// The single NuGet package. It is packed from the leaf project: that one references the
/// core and pulls its assembly into the same nupkg, so both share one version (see
/// src/dotnet/Xake.Dotnet.fsproj).
let packageProject = "src/dotnet"
let packageName version = $"Xake.%s{version}.nupkg"

let dotnet arglist = sh "dotnet" { args arglist; failonerror }

do xakeScript {
    filelog "build.log" Diag
    varschema vars

    rules [
        "main" <<< ["build"; "test"]

        "build" <== List.collect (fun lib -> binaries lib.Name) libraries
        "clean" => rm {dir "out"}

        command "test" {
            let! testFilter = vars.TestFilter
            let where = [ for f in Option.toList testFilter do yield $"--filter Name~\"{f}\"" ]

            do! sh "dotnet test src/tests -c Release" { args where; failonerror }
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
            let! version = vars.Version

            do! dotnet [
                "build"
                lib.Dir
                "/p:Version=" + version
                "--configuration"; "Release"
                "--framework"; framework
                "--output"; "./out/" + framework
            ]
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
