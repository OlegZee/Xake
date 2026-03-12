#r "nuget: Xake, 2.9.6"

open Xake
open Xake.Tasks

let frameworks = ["netstandard2.0" (*; "net46" *)]
let libtargets =
    [ for fwk in frameworks do
      for ext in ["dll"; "xml"]
        -> $"out/%s{fwk}/Xake.%s{ext}"
    ]

let getVersion () = getEnv "VERSION" |> map (Option.defaultValue "0.0.1")

let makePackageName version = $"Xake.%s{version}.nupkg"

let dotnet arglist = sh "dotnet" { args arglist; failonerror }

do xakeScript {
    filelog "build.log" Diag

    rules [
        "main" <<< ["build"; "test"]

        "build" <== libtargets
        "clean" => rm {dir "out"}

        command "test" {
            let! where =
              getVar "FILTER"
              |> map (function |Some clause -> ["--filter"; $"Name~\"{clause}\""] | None -> [])

            do! dotnet <| ["test"; "src/tests"; "-c"; "Release"] @ where
        }

        targets libtargets {

            let! allFiles = getFiles <| fileset {
                basedir "src/core"
                includes "Xake.fsproj"
                includes "**/*.fs"
            }

            do! needFiles allFiles
            let! version = getVersion()

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
            let! version = getVersion()
            do! need ["out" </> makePackageName version]
        }

        "out/Xake.(ver:*).nupkg" ..> recipe {
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
            let! version = getVersion()

            let! nuget_key = getEnv "NUGET_KEY"
            do! dotnet [
                "nuget"; "push"
                "out" </> makePackageName version
                "--source"; "https://www.nuget.org/api/v2/package"
                "--api-key"; nuget_key |> Option.defaultValue ""
            ]
        }
    ]
}
