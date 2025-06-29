#r "nuget: Xake, 2.2.0"

open Xake
open Xake.Tasks

let frameworks = ["netstandard2.0" (*; "net46" *)]
let libtargets =
    [ for fwk in frameworks do
      for ext in ["dll"; "xml"]
        -> $"out/%s{fwk}/Xake.%s{ext}"
    ]

let getVersion () = recipe {
    let! verVar = getVar "VER"
    let! verEnv = getEnv "VER"
    let ver = verVar |> Option.defaultValue (verEnv |> Option.defaultValue "0.0.1")

    let! verSuffix =
        getVar "SUFFIX"
        |> map (
            function
            | None -> "-beta"
            | Some "" -> "" // this is release!
            | Some s -> "-" + s
            )
    return ver + verSuffix
}

let makePackageName = sprintf "Xake.%s.nupkg"

let dotnet arglist =
    shell {
        cmd "dotnet"
        args arglist
        failonerror
    } |> Ignore

do xakeScript {
    filelog "build.log" Diag

    rules [
        "main" <<< ["build"; "test"]

        "build" <== libtargets
        "clean" => rm {dir "out"}

        "test" => recipe {
            do! alwaysRerun()

            let! where =
              getVar "FILTER"
              |> map (function |Some clause -> ["--filter"; $"Name~\"{clause}\""] | None -> [])

            do! dotnet <| ["test"; "src/tests"; "-c"; "Release"] @ where
        }

        libtargets *..> recipe {

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
        "pack" => recipe {
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
        "push" => recipe {
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
