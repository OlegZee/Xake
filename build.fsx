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
let libtargets =
    [ for fwk in frameworks do
      for ext in ["dll"; "xml"]
        -> $"out/%s{fwk}/Xake.%s{ext}"
    ]

let makePackageName version = $"Xake.%s{version}.nupkg"

let dotnet arglist = sh "dotnet" { args arglist; failonerror }

do xakeScript {
    filelog "build.log" Diag
    varschema vars

    rules [
        "main" <<< ["build"; "test"]

        "build" <== libtargets
        "clean" => rm {dir "out"}

        command "test" {
            let! testFilter = vars.TestFilter
            let where = [ for f in Option.toList testFilter do yield $"--filter Name~\"{f}\"" ]

            do! sh "dotnet test src/tests -c Release" { args where; failonerror }
        }

        targets libtargets {

            let! allFiles = getFiles <| fileset {
                basedir "src/core"
                includes "Xake.fsproj"
                includes "**/*.fs"
            }

            do! needFiles allFiles
            let! version = vars.Version

            for framework in frameworks do
                do! dotnet [
                    "build"
                    "src/core"
                    "/p:Version=" + version
                    "--configuration"; "Release"
                    "--framework"; framework
                    "--output"; "./out/" + framework
                    "/p:DocumentationFile=Xake.xml"
                ]
        }
    ]

    (* Nuget publishing rules *)
    rules [
        command "pack" {
            let! version = vars.Version
            do! need ["out" </> makePackageName version]
        }

        target "out/Xake.(ver:*).nupkg" {
            let! ver = getRuleMatch "ver"
            do! dotnet [
                "pack"; "src/core"
                "-c"; "Release"
                $"/p:Version={ver}"
                "--output"; "out/"
                "/p:DocumentationFile=Xake.xml"
            ]
        }

        // push need pack to be explicitly called in advance
        command "push" {
            let! version = vars.Version
            let! nuget_key = vars.NUGET_KEY

            do! dotnet [
                "nuget"; "push"
                "out" </> makePackageName version
                "--source"; "https://www.nuget.org/api/v2/package"
                "--api-key"; nuget_key
            ]
        }
    ]
}
