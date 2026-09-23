// Proving script for `Project.import` (brief §11, slice 1): imports the C# projects of a
// repository into one lock per (framework, brand) and prints what landed there.
//
// Run it from the repository being imported, so that `$(ProjectRoot)` in the lock is that
// repository -- e.g. a `git archive origin/develop` copy of ar-net-core-dataengine:
//
//     cd <repo copy> && dotnet fsi <xake>/docs/features/hermetic-build/import.fsx -- -- locks
//     dotnet fsi .../import.fsx -- -- show -d Lock=locks/netstandard2.0/GCCN.json   # or LOCK=... in the env
//     dotnet fsi .../import.fsx -- -- build                                         # locks, then compile from them
//
// Xake comes from `.bootstrap/` next to the Xake checkout (see build.fsc.fsx), not from a
// released package: this branch is not released.
#r "../../../.bootstrap/Xake.dll"
#r "../../../.bootstrap/Xake.Dotnet.dll"

open System.IO
open Xake
open Xake.Dotnet
open Xake.Tasks

let vars = {|
    Lock = Var.string(envVar = "LOCK", description = "Lock file to show")
|}

/// The projects to import. dataengine's three shipped libraries; page later.
let projects =
    [ "src/DataEngine/DataEngine.csproj"
      "src/ExpressionInfo/ExpressionInfo.csproj"
      "src/VBFunctionLib/VBFunctionLib.csproj" ]
    |> List.filter File.Exists

let frameworks = [ "netstandard2.0" ]
let brands = [ "MESCIUS"; "GCCN" ]

let lockFile framework brand = $"locks/%s{framework}/%s{brand}.json"

do xakeScript {
    filelog "import.log" Verbosity.Diag
    varschema vars

    rules [
        "locks" <== [ for f in frameworks do for b in brands -> lockFile f b ]

        // one msbuild design-time build per project, msbuild runs only when a project or
        // one of the files it imports changed
        target "locks/(fwk:*)/(brand:*).json" {
            let! framework = getRuleMatch "fwk"
            let! brand = getRuleMatch "brand"
            let! result = getTargetFile()

            do! Project.import {
                Project.ImportOptions.Default with
                    Projects = projects
                    Framework = framework
                    Properties = [ "Brand", brand ]
                    Variant = brand
                    Output = result.FullName
            }
        }

        // compiles one project from its lock. The lock's project references are unhashed
        // (they point at what the referenced project's own build produces, not built yet):
        // rewrite those to the path this rule builds them at, and need that first
        target "src/(proj:*)/obj/xake/(fwk:*)/(brand:*)/(name:*).dll" {
            let! fwk = getRuleMatch "fwk"
            let! brand = getRuleMatch "brand"
            let! name = getRuleMatch "name"
            let! options = getCtxOptions()
            let relative (p: string) = Path.GetRelativePath (options.ProjectRoot, p)

            do! need [lockFile fwk brand]
            let! lock = Lock.load (lockFile fwk brand)
            let project = Lock.project name lock

            // the referenced projects' own outputs, as this run of the lock will build them
            let outputOf refPath =
                (Lock.project (Path.GetFileNameWithoutExtension (refPath: string)) lock).Output
                |> Option.defaultWith (fun () -> failwithf "project reference '%s' has no /out: in its own lock entry" refPath)

            let unbuilt = project.References |> List.filter (fun r -> r.Sha256 = "") |> List.map (fun r -> r.Path) |> Set.ofList
            let mapped = project |> Lock.mapPaths (fun p -> if unbuilt.Contains p then outputOf p else p)

            do! need (unbuilt |> Set.toList |> List.map (outputOf >> relative))
            do! CscLock.compile mapped
        }

        // compiles every project the locks name, for every framework and brand. Dynamic
        // because the assembly names -- the `name` rule match above -- come from the locks,
        // not from a static list
        command "build" {
            let! options = getCtxOptions()
            let relative (p: string) = Path.GetRelativePath (options.ProjectRoot, p)

            for f in frameworks do
                for b in brands do
                    do! need [lockFile f b]
                    let! lock = Lock.load (lockFile f b)
                    do! need
                            [ for project in lock.Projects do
                                match project.Output with
                                | Some out -> yield relative out
                                | None -> () ]
        }

        command "show" {
            let! lockPath = vars.Lock
            let! lock = Lock.load (lockPath |> Option.defaultValue (lockFile "netstandard2.0" "MESCIUS"))
            for project in lock.Projects do
                do! trace Message "%s (%s, sdk %s)" project.Name project.Project project.Compiler.Sdk
                do! trace Message "  compiler   %s %s" project.Compiler.Path (project.Compiler.Sha256.Substring(0, 12))
                do! trace Message "  args       %d, sources %d, out %A" project.Args.Length project.Sources.Length project.Output
                do! trace Message "  references %d (%d unhashed), analyzers %d" project.References.Length
                        (project.References |> List.filter (fun r -> r.Sha256 = "") |> List.length) project.Analyzers.Length
                do! trace Message "  imports    %d: %s" project.Imports.Length
                        (project.Imports |> List.map (fun i -> Path.GetFileName i.Path) |> String.concat ", ")
                do! trace Message "  generated  %s" (project.Generated |> List.map (fst >> Path.GetFileName) |> String.concat ", ")
        }
    ]
}
