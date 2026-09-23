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
// Cross-repo roots. `$(ProjectRoot)` (one of the three built-in roots `Roots.builtin` always
// tokenizes against) is the build's project root -- the page copy. dataengine's paths, and the
// "unbuilt" project-reference outputs the import records for the two page->dataengine
// `ProjectReference`s, live under a different tree entirely and would otherwise land in the
// lock as raw, machine-specific absolute paths. `Project.ImportOptions.Roots` (this branch,
// Part 1) declares one extra token for the sibling: `$(DataEngineRoot)`. Anything reading the
// lock back -- this script's compile rule and `build` command -- must expand with the same
// roots, via `Lock.loadWith extraRoots` rather than the plain `Lock.load`.
//
// `..`-targets. dataengine's own compile outputs sit outside `$(ProjectRoot)` (this script's
// cwd), e.g. `../ar-net-core-dataengine/src/ExpressionInfo/obj/xake/netstandard2.0/MESCIUS/
// ExpressionInfo.dll`. A `target` pattern spelled with a leading `..` used to not match (engine
// gap, now fixed in `.bootstrap/Xake.dll`: rule matching resolves the literal `..` in the
// pattern the same way `File.make` resolves it away from the target's normalized absolute
// path, so the two compare equal). The rule below is now a single relative pattern,
// `../ar-net-core-dataengine/src/(proj:*)/obj/xake/(fwk:*)/(brand:*)/(name:*).dll`, matching
// this script's own cwd-relative spelling of the sibling checkout -- no absolute-path
// workaround rule needed any more. `need`/`getTargetFile` still accept an absolute path just as
// well as a `$(ProjectRoot)`-relative one (`makeTarget` combines with `ProjectRoot` via
// `Path.Combine`, which leaves an absolute argument alone), so every `need` call in this script
// keeps passing dataengine's outputs as absolute paths (as expanded from the lock via
// `$(DataEngineRoot)`), unchanged.
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

let frameworks = [ "netstandard2.0" ]
let brands = [ "MESCIUS"; "GCCN" ]

let lockFile framework brand = $"locks/%s{framework}/%s{brand}.json"

/// The pattern for dataengine's own compile outputs: relative, cwd-anchored (see the header
/// note -- `..`-prefixed patterns now match, so no absolute-path workaround is needed).
let dataEngineObjPattern = "../ar-net-core-dataengine/src/(proj:*)/obj/xake/(fwk:*)/(brand:*)/(name:*).dll"

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
                    // Tried and reverted: adding `MSBuildProjectExtensionsPath` to brand-variant
                    // restore outputs (candidate fix for the brand-dependent-package-id race,
                    // tracker) does separate `project.assets.json`/`nuget.g.props` per brand and
                    // does stop the race, but it breaks reference resolution project-wide -- the
                    // design-time build's captured references drop from 113-134 (with the
                    // NETStandard.Library facade set) to 0-21 (direct PackageReferences/
                    // ProjectReferences only), and the smallest project (VBFunctionLib, which
                    // has no direct references of its own) then compiles with zero /reference
                    // args at all and fails on "Predefined type 'System.String' is not defined
                    // or imported". Confirmed by A/B: same script, same clean tree, only this
                    // property differs. Left for the library owner; not a fix to ship.
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
            let! lock = Lock.loadWith extraRoots (lockFile fwk brand)
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
            do! CscLock.compile mapped
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

        // the NuGet package cache root -- the same `$(NuGetPackageRoot)` token `Project.import`
        // and the lock tokenize against (Part 1).
        let cacheRoot = Roots.nugetRoot ()

        // one CycloneDX SBOM per compiled project (Part 1): needs the lock and the built
        // assembly (the compile rules above), reads the restore graph from *that project's own*
        // `obj/project.assets.json` -- the restore that produced it is per project directory,
        // not per lock -- and joins it with the lock's hashed references via `Sbom.forAssembly`.
        // `name` is the assembly name, same capture as the compile rule's, so `Lock.project`
        // finds the entry by `Name` directly.
        target "out/(fwk:*)/(brand:*)/(name:*).cdx.json" {
            let! fwk = getRuleMatch "fwk"
            let! brand = getRuleMatch "brand"
            let! name = getRuleMatch "name"

            do! need [lockFile fwk brand]
            let! lock = Lock.loadWith extraRoots (lockFile fwk brand)
            let project = Lock.project name lock
            let output =
                project.Output
                |> Option.defaultWith (fun () -> failwithf "project '%s' has no /out: in its lock entry" name)

            do! need [output]

            let assets = Nuget.readAssets (project.Directory </> "obj" </> "project.assets.json") fwk
            let bom = Sbom.forAssembly cacheRoot assets project output
            do! writeText (Sbom.cycloneDx bom)
        }

        // one sbom per lock project per brand, for every framework this script imports
        command "sbom" {
            for f in frameworks do
                for b in brands do
                    do! need [lockFile f b]
                    let! lock = Lock.loadWith extraRoots (lockFile f b)
                    do! need [ for project in lock.Projects -> $"out/%s{f}/%s{b}/%s{project.Name}.cdx.json" ]
        }

        // compiles every project the locks name, for every framework and brand, in both
        // repositories
        command "build" {
            for f in frameworks do
                for b in brands do
                    do! need [lockFile f b]
                    let! lock = Lock.loadWith extraRoots (lockFile f b)
                    do! need
                            [ for project in lock.Projects do
                                match project.Output with
                                | Some out -> yield out
                                | None -> () ]
        }

        command "show" {
            let! lockPath = vars.Lock
            let! lock = Lock.loadWith extraRoots (lockPath |> Option.defaultValue (lockFile "netstandard2.0" "MESCIUS"))
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
