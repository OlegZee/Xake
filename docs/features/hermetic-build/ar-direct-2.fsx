// ActiveReports (MESCIUS brand, Release) compiled straight from project descriptions: no locks, no
// fingerprints. Run from this folder. Layout: out/<cfg>/<fwk>/<name>/<assembly>.dll
#i "nuget: /Users/olegz/Projects-work/ar/xake-ar/feed"
#r "nuget: Xake, 3.7.1-night"
#r "nuget: Xake.Hermetic.Dotnet, 0.3.1-night"

open System.IO
open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet

let fwks = [ "netstandard2.0"; "net472" ]
let names = Directory.GetDirectories "projects" |> Array.map Path.GetFileName |> List.ofArray
let descOf name = $"projects/{name}/xake.project.json"
let dllOf cfg fwk name =
    let d = Description.read (descOf name)
    $"out/{cfg}/{fwk}/{name}/{defaultArg d.AssemblyName d.Name}.dll"    // assembly names are branded

do xakeScript {
    var "NUGET_PACKAGES" "../.packages"
    rules [
        "main" => recipe {
            do! Described.restoreAll descOf [ for n in names -> descOf n ] fwks     // one restore for all
            do! need [ for n in names do for f in fwks -> dllOf "Release" f n ]
        }
        target "out/(cfg:*)/(fwk:*)/(name:*)/*.dll" {
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            let! d = Description.load (descOf name)
            let! deps = Described.closure descOf d
            let! pkgs = Described.restoreWith descOf d fwk
            do! need [ for r in deps -> dllOf cfg fwk r ]
            do! csc {
                targetfwk fwk
                ref (deps |> List.fold (fun fs r -> fs ++ dllOf cfg fwk r) Fileset.Empty)
                project (descOf name)
                config cfg
                packages pkgs
            }
        }
    ]
}
