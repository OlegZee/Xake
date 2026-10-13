// ActiveReports (MESCIUS brand, Release) compiled straight from hand-kept project descriptions: no import,
// no locks, no fingerprints. Run from this folder. Layout: out/<cfg>/<fwk>/<name>/<assembly>.dll
#r "nuget: Xake, 3.7.0.26"
#r "nuget: Xake.Hermetic.Dotnet, 0.3.0.28"

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
        "main" <<< [ for n in names do for f in fwks -> dllOf "Release" f n ]

        target "out/(cfg:*)/(fwk:*)/(name:*)/*.dll" {
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            let! d = Description.load (descOf name)
            let! pkgs = Described.restore d fwk
            do! need [ for r in d.ProjectRefs -> dllOf cfg fwk r ]
            do! csc {
                targetfwk fwk
                ref (d.ProjectRefs |> List.fold (fun fs r -> fs ++ dllOf cfg fwk r) Fileset.Empty)
                project (descOf name)
                config cfg
                packages pkgs
            }
        }
        target "obj/resx/(name:*)/**/*.resources" {            // G1: .resx -> .resources by the script
            let! name = getRuleMatch "name"
            let! target = getTargetFile ()
            let! d = Description.load (descOf name)
            let dir = Path.GetDirectoryName (Path.GetFullPath (Path.Combine (Path.GetDirectoryName (descOf name), d.Project)))
            let resx = Path.Combine (dir, Path.ChangeExtension (Path.GetRelativePath (Path.GetFullPath $"obj/resx/{name}", target.FullName), ".resx"))
            do! needFiles (Filelist [ File.make resx ])
            Resx.compile resx target.FullName
        }
    ]
}
