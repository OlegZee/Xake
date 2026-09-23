// Proving script for `Project.import` (brief §11, slice 1): imports the C# projects of a
// repository into one lock per brand -- every target framework inside -- and prints what
// landed there.
//
// Run it from the repository being imported, so that `$(ProjectRoot)` in the lock is that
// repository -- e.g. a `git archive origin/develop` copy of ar-net-core-dataengine:
//
//     cd <repo copy> && dotnet fsi <xake>/docs/features/hermetic-build/import.fsx -- -- locks
//     dotnet fsi .../import.fsx -- -- show -d Lock=locks/GCCN.json   # or LOCK=... in the env
//     dotnet fsi .../import.fsx -- -- build                          # locks, then compile from them
//
// One lock per brand, holding every framework in `frameworks`: `Project.import` takes
// `Frameworks: string list`, restores each project once (no `TargetFramework`) and runs a
// design-time build per framework against that restore. Each `Lock.Entry` carries its own
// `Framework`, so the lookup is `Lock.entryFor framework name lock`.
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
    |> function
       | ps when ps |> List.forall File.Exists -> ps
       | ps ->
           // without this the list comes out empty, the import writes an empty lock in
           // milliseconds, and `build`/`sbom` then have nothing to do and say so cheerfully
           failwithf "%s\n  cwd:     %s\n  missing: %s"
               "Run this script with the current directory set to a copy of ar-net-core-dataengine: it imports that repository's projects, not this folder's. See verify-dataengine.md, or verify-dataengine.sh which sets the whole thing up."
               (Directory.GetCurrentDirectory ())
               (ps |> List.filter (File.Exists >> not) |> String.concat ", ")

let frameworks = [ "netstandard2.0" ]
let brands = [ "MESCIUS"; "GCCN" ]

let lockFile brand = $"locks/%s{brand}.json"

do xakeScript {
    filelog "import.log" Verbosity.Diag
    varschema vars

    rules [
        "locks" <== [ for b in brands -> lockFile b ]

        // one restore plus one msbuild design-time build per (project, framework); msbuild
        // runs only when a project or one of the files it imports changed
        target "locks/(brand:*).json" {
            let! m = getRuleMatches()
            let! result = getTargetFile()

            do! Project.import {
                Project.ImportOptions.Default with
                    Projects = projects
                    Frameworks = frameworks
                    Properties = [ ("Brand", m.["brand"]) ]
                    Variant = m.["brand"]
                    Output = result.FullName
            }
        }

        // compiles one project from its lock. `Lock.load` depends on the lock itself, so the
        // import rule above runs first with no explicit `need`. The lock's project references
        // are unhashed (they point at what the referenced project's own build produces, not
        // built yet): rewrite those to the path this rule builds them at, and need that first
        target "src/(proj:*)/obj/xake/(fwk:*)/(brand:*)/(name:*).dll" {
            let! m = getRuleMatches()
            let fwk, brand, name = m.["fwk"], m.["brand"], m.["name"]
            let! options = getCtxOptions()
            let relative (p: string) = Path.GetRelativePath (options.ProjectRoot, p)

            let! lock = Lock.load (lockFile brand)
            let project = Lock.entryFor fwk name lock

            // the referenced projects' own outputs, as this run of the lock will build them
            let outputOf refPath =
                (Lock.entryFor fwk (Path.GetFileNameWithoutExtension (refPath: string)) lock).Output
                |> Option.defaultWith (fun () -> failwithf "project reference '%s' has no /out: in its own lock entry" refPath)

            let unbuilt = project.Dependencies.References |> List.filter (fun r -> r.Sha256 = "") |> List.map (fun r -> r.Path) |> Set.ofList
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

            for b in brands do
                let! lock = Lock.load (lockFile b)
                do! need
                        [ for entry in lock.Entries do
                            match entry.Output with
                            | Some out -> yield relative out
                            | None -> () ]
        }

        command "show" {
            let! lockPath = vars.Lock
            let! lock = Lock.load (lockPath |> Option.defaultValue (lockFile "MESCIUS"))
            for entry in lock.Entries do
                let e, c, d = entry.Evaluation, entry.Compilation, entry.Dependencies
                do! trace Message "%s [%s] (%s)" entry.Name entry.Framework e.Project
                do! trace Message "  evaluation   sdk %s, pin %s, imports %d: %s" e.Sdk
                        (e.SdkPin |> Option.map Lock.sdkPinText |> Option.defaultValue "-") e.Imports.Length
                        (e.Imports |> List.map (fun i -> Path.GetFileName i.Path) |> String.concat ", ")
                do! trace Message "  compilation  options %d, defines %d, sources %d, out %A" c.Options.Length c.Defines.Length c.Sources.Length entry.Output
                do! trace Message "               generated %s" (c.Generated |> List.map (fst >> Path.GetFileName) |> String.concat ", ")
                do! trace Message "  dependencies compiler %s %s %s" d.Compiler.Version d.Compiler.Path (d.Compiler.Sha256.Substring(0, 12))
                do! trace Message "               references %d (%d unhashed), analyzers %d, packages %d (%d direct)" d.References.Length
                        (d.References |> List.filter (fun r -> r.Sha256 = "") |> List.length) d.Analyzers.Length
                        d.Packages.Length (d.Packages |> List.filter (fun p -> p.Direct) |> List.length)
        }
    ]
}
