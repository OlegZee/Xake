// Proving script for `Project.import` across two sibling repositories (brief §11, slice 1,
// "next step"): imports ar-net-core-page's 12 shipped C# projects together with
// ar-net-core-dataengine's 3, `-p:LocalBuild=true` (page's `ProjectReference`s into dataengine
// instead of its NuGet packages), one lock per (framework, brand) holding all 15 entries so the
// cross-repo project-reference mapping (`Lock.mapPaths`) resolves the same way an in-repo one
// does.
//
// Run it from the ar-net-core-page copy, with ar-net-core-dataengine's copy as its sibling
// (`../ar-net-core-dataengine` from cwd) -- e.g. two `git archive origin/develop` copies under
// a job's tmp dir, laid out as `<tmp>/ar/ar-net-core-page` and `<tmp>/ar/ar-net-core-dataengine`:
//
//     cd <page copy> && dotnet fsi <xake>/docs/features/hermetic-build/import-page.fsx -- -- locks
//     dotnet fsi .../import-page.fsx -- -- build
//
// Xake comes from `.bootstrap/` next to the Xake checkout (see build.fsc.fsx), not from a
// released package: this branch is not released.
//
// Cross-repo roots. `$(ProjectRoot)` (one of the three built-in roots `Fsproj.roots ()` always
// tokenizes against) is the current directory -- the page copy. dataengine's paths, and the
// "unbuilt" project-reference outputs the import records for the two page->dataengine
// `ProjectReference`s, live under a different tree entirely and would otherwise land in the
// lock as raw, machine-specific absolute paths. `Project.ImportOptions.Roots` (this branch,
// Part 1) declares one extra token for the sibling: `$(DataEngineRoot)`. Anything reading the
// lock back -- this script's compile rule and `build` command -- must expand with the same
// roots, via `Lock.readWith (Fsproj.withRoots roots)` rather than the plain `Lock.read`.
//
// `..`-targets. dataengine's own compile outputs sit outside `$(ProjectRoot)` (this script's
// cwd), e.g. `../ar-net-core-dataengine/src/ExpressionInfo/obj/xake/netstandard2.0/MESCIUS/
// ExpressionInfo.dll`. A `target` pattern spelled with a leading `..` does NOT match: rule
// matching (`ExecCore.locateRule`, `FileRule` case) compares a target's *normalized* absolute
// path (`File.make` wraps `System.IO.FileInfo`, which resolves `..` away) against
// `Path.matchGroups pattern projectRoot`, whose pattern-to-regex step (`Path.maskToRegex`) does
// NOT collapse `..` -- it keeps the literal two dots and requires the matched string to
// literally contain them. `Path.Combine($(ProjectRoot), "../dataengine/...")` and
// `FileInfo("../dataengine/...").FullName` normalize to the same folder, but the first keeps
// the literal `..` in the regex source and the second has already thrown it away: they never
// match, and Xake reports "neither rule nor file is found" for every such target. Confirmed by
// direct repro against `Xake.dll` (see the library-gap note in this feature's session.md).
// The fix used below: a *second* file rule, with an absolute pattern
// (`<dataEngineRoot>/src/(proj:**)/obj/xake/(fwk:*)/(brand:*)/(name:*).dll`) -- `Path.Combine`
// leaves an already-rooted pattern alone, so the regex is built from the same normalized
// absolute string the target carries, and it matches. `need`/`getTargetFile` accept an
// absolute path just as well as a `$(ProjectRoot)`-relative one (`makeTarget` combines with
// `ProjectRoot` via `Path.Combine`, which also leaves an absolute argument alone), so every
// `need` call in this script passes dataengine's outputs as absolute paths, unchanged.
#r "../../../.bootstrap/Xake.dll"
#r "../../../.bootstrap/Xake.Dotnet.dll"

open System.IO
open Xake
open Xake.Dotnet
open Xake.Tasks

let vars = {|
    Lock = Var.string(envVar = "LOCK", description = "Lock file to show")
|}

/// page's 12 shipped projects, relative to this script's cwd (the page copy).
let pageProjects =
    [ "src/Rdl/Rdl.csproj"
      "src/Rendering/Rendering.csproj"
      "src/Scripting/Scripting.csproj"
      "src/Drawing.Gc/Drawing.Gc.csproj"
      "src/Drawing.Gdi/Drawing.Gdi.csproj"
      "src/Exports/Excel.Page/Export.Excel.Page.csproj"
      "src/Exports/Html.Page/Export.Html.Page.csproj"
      "src/Exports/Image/Export.Image.Page.csproj"
      "src/Exports/Pdf/Export.Pdf.Page.csproj"
      "src/Exports/Svg/Export.Svg.Page.csproj"
      "src/Exports/Text/Export.Text.Page.csproj"
      "src/Exports/Word.Page/Export.Word.Page.csproj" ]
    |> List.filter File.Exists

/// dataengine's copy, as this script's sibling: `<cwd>/../ar-net-core-dataengine`.
let dataEngineDir =
    Path.Combine (Directory.GetCurrentDirectory(), "..", "ar-net-core-dataengine")
    |> Path.GetFullPath
    |> fun p -> p.Replace ('\\', '/')

/// dataengine's 3 shipped projects, made absolute -- msbuild runs with cwd = the page copy, so
/// a relative `../ar-net-core-dataengine/...` project path would also work for msbuild itself,
/// but the lock and the rules below want the same absolute spelling `dataEngineDir` already is.
let dataEngineProjects =
    [ "src/DataEngine/DataEngine.csproj"; "src/ExpressionInfo/ExpressionInfo.csproj"; "src/VBFunctionLib/VBFunctionLib.csproj" ]
    |> List.map (fun p -> Path.GetFullPath (Path.Combine (dataEngineDir, p)))
    |> List.filter File.Exists

let projects = pageProjects @ dataEngineProjects

/// The one extra root this cross-repo import needs (Part 1): one token per sibling repository.
let extraRoots = [ "$(DataEngineRoot)", dataEngineDir ]
let allRoots () = Fsproj.withRoots extraRoots

let frameworks = [ "netstandard2.0" ]
let brands = [ "MESCIUS"; "GCCN" ]

let lockFile framework brand = $"locks/%s{framework}/%s{brand}.json"

/// The pattern for dataengine's own compile outputs: absolute, because a `..`-relative one does
/// not match (see the header note).
let dataEngineObjPattern = dataEngineDir + "/src/(proj:**)/obj/xake/(fwk:*)/(brand:*)/(name:*).dll"

do xakeScript {
    filelog "import.log" Verbosity.Diag
    varschema vars

    rules [
        "locks" <== [ for f in frameworks do for b in brands -> lockFile f b ]

        // one msbuild design-time build per project (page's 12, dataengine's 3), all landing
        // in the one lock for this (framework, brand): msbuild runs only when a project or one
        // of the files it imports changed
        target "locks/(fwk:*)/(brand:*).json" {
            let! framework = getRuleMatch "fwk"
            let! brand = getRuleMatch "brand"
            let! result = getTargetFile()

            do! Project.import {
                Project.ImportOptions.Default with
                    Projects = projects
                    Framework = framework
                    // LocalBuild swaps page's dataengine NuGet packages for ProjectReferences
                    // into the sibling checkout; NetCoreOnly narrows page's own
                    // TargetFrameworks (netstandard2.0;net472) down to netstandard2.0 alone --
                    // the same switch the real Cake build passes (scripts/build.cake)
                    Properties = [ "Brand", brand; "LocalBuild", "true"; "NetCoreOnly", "true" ]
                    Variant = brand
                    Output = result.FullName
                    Roots = extraRoots
            }
        }

        // compiles one project from its lock, whichever repository its output belongs to. The
        // lock's project references are unhashed (they point at what the referenced project's
        // own build produces, not built yet): rewrite those to the path this rule builds them
        // at, and need that first -- same pattern as import.fsx, now spanning two repositories
        // because the lock's project entries do
        let compileFromLock fwk brand name = recipe {
            do! need [lockFile fwk brand]
            let lock = Lock.readWith (allRoots ()) (lockFile fwk brand)
            let project = Lock.project name lock

            let outputOf refPath =
                (Lock.project (Path.GetFileNameWithoutExtension (refPath: string)) lock).Output
                |> Option.defaultWith (fun () -> failwithf "project reference '%s' has no /out: in its own lock entry" refPath)

            let unbuilt = project.References |> List.filter (fun r -> r.Sha256 = "") |> List.map (fun r -> r.Path) |> Set.ofList
            let mapped = project |> Lock.mapPaths (fun p -> if unbuilt.Contains p then outputOf p else p)

            // absolute paths throughout: `need`/`getTargetFile` resolve them unchanged (an
            // absolute argument to `Path.Combine` with `ProjectRoot` wins), whether the output
            // is page's own or dataengine's
            do! need (unbuilt |> Set.toList |> List.map outputOf)
            do! csc { fromlock mapped }
        }

        target "src/(proj:**)/obj/xake/(fwk:*)/(brand:*)/(name:*).dll" {
            let! fwk = getRuleMatch "fwk"
            let! brand = getRuleMatch "brand"
            let! name = getRuleMatch "name"
            do! compileFromLock fwk brand name
        }

        target dataEngineObjPattern {
            let! fwk = getRuleMatch "fwk"
            let! brand = getRuleMatch "brand"
            let! name = getRuleMatch "name"
            do! compileFromLock fwk brand name
        }

        // compiles every project the locks name, for every framework and brand, in both
        // repositories
        command "build" {
            for f in frameworks do
                for b in brands do
                    do! need [lockFile f b]
                    let lock = Lock.readWith (allRoots ()) (lockFile f b)
                    do! need
                            [ for project in lock.Projects do
                                match project.Output with
                                | Some out -> yield out
                                | None -> () ]
        }

        command "show" {
            let! lockPath = vars.Lock
            let lock = Lock.readWith (allRoots ()) (lockPath |> Option.defaultValue (lockFile "netstandard2.0" "MESCIUS"))
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
