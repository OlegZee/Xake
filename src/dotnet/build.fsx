#r "paket:
    nuget Xake ~> 1.1 prerelease //"

#if !FAKE
#load ".fake/build.fsx/intellisense.fsx"
#endif

open Xake
open Xake.Tasks

let getVersion () = recipe {
    let! verVar = getVar("VER")
    let! verEnv = getEnv("VER")
    return verVar |> Option.defaultValue (verEnv |> Option.defaultValue "0.0.1")
}

let makePackageName = sprintf "Xake.Dotnet.%s.nupkg"

let dotnet arglist =
    shell {
        cmd "dotnet"
        args arglist
        failonerror
        } |> Recipe.Ignore

do xakeScript {
    filelog "build.log" Verbosity.Diag
    // consolelog Verbosity.Normal

    rules [
        "main" <<< ["build"; "test"]

        "clean" => rm {dir "out"}
        "build" <==
                [ for t in ["netstandard2.0"; "net46"] do
                  for e in ["dll"; "xml"]
                    -> sprintf "out/%s/Xake.Dotnet.%s" t e
                ]

        "test" => recipe {
            do! alwaysRerun()

            let! where =
              getVar("FILTER")
              |> Recipe.map (function |Some clause -> ["--filter"; sprintf "Name~\"%s\"" clause] | None -> [])

            // in case of travis only run tests for standard runtime, eventually will add more
            let! limitFwk = getEnv("TRAVIS") |> Recipe.map (function | Some _ -> ["-f:netcoreapp2.0"] | _ -> [])

            do! dotnet <| ["test"; "tests"; "-c"; "Release"] @ where @ limitFwk
        }

        [ "out/(fwk:*)/Xake.Dotnet.dll"
          "out/(fwk:*)/Xake.Dotnet.xml"] *..> recipe {

            do! fileset {
                    basedir "src"
                    includes "Xake.Dotnet.fsproj"
                    includes "**/*.fs"
                }
                |> getFiles
                |> Recipe.map needFiles
                |> Recipe.Ignore

            let! framework = getRuleMatch "fwk"
            let! version = getVersion()

            do! dotnet
                  [
                    "build"
                    "src"
                    "/p:Version=" + version
                    "--configuration"; "Release"
                    "--framework"; framework
                    "--output"; "../out/" + framework
                    "/p:DocumentationFile=Xake.Dotnet.xml"
                  ]
        }

        (* Nuget publishing rules *)
        "pack" => recipe {
            let! version = getVersion()
            do! need ["out" </> makePackageName version]
        }

        "out/Xake.Dotnet.(ver:*).nupkg" ..> recipe {
            let! ver = getRuleMatch("ver")
            do! dotnet
                  [
                      "pack"; "src"
                      "-c"; "Release"
                      "/p:Version=" + ver
                      "--output"; "../out/"
                      "/p:DocumentationFile=Xake.Dotnet.xml"
                  ]
        }

        // push need pack to be explicitly called in advance
        "push" => recipe {
            let! version = getVersion()
            let! nuget_key = getEnv("NUGET_KEY")

            do! dotnet
                  [
                    "nuget"; "push"
                    "out" </> makePackageName version
                    "--source"; "https://www.nuget.org/api/v2/package"
                    "--api-key"; nuget_key |> Option.defaultValue ""
                  ]
        }
    ]
}