// Self-verification of the hermetic build on ar-net-core-dataengine, across both brands *and*
// both target frameworks the repository ships (netstandard2.0 and net472).
//
// RUN IT FROM A COPY OF ar-net-core-dataengine -- not from this folder. The whole scenario,
// fixture and msbuild baseline included, is one command:
//
//     docs/features/hermetic-build/verify-dataengine.sh            # makes its own fixture
//
// By hand, against a copy you made yourself (`git archive origin/develop | tar -x -C <dir>`):
//
//     cd <dataengine copy>
//     X=<xake checkout>/docs/features/hermetic-build/verify-dataengine.fsx
//     dotnet fsi $X -- -- locks                      # 2 locks, one per brand
//     dotnet fsi $X -- -- build                      # 12 assemblies from those locks
//     dotnet fsi $X -- -- sbom                       # 12 CycloneDX files under sbom/
//     dotnet fsi $X -- -- restore                    # fill a package folder, compile nothing
//     dotnet fsi $X -- -- show -d LOCK=locks/GCCN.json
//     PACKAGES=.packages dotnet fsi $X -- -- build   # keep the dependencies in a folder of
//                                                    # the build's own (a build agent's cache)
//
// `import.fsx` next to this file is the original proving script and imports netstandard2.0
// only; this one is the wider matrix, and the one `verify-dataengine.md` walks through.
//
// **One lock per brand, holding every framework.** `Project.import` takes
// `Frameworks: string list` and covers the whole matrix of one variant in a single call: one
// restore per project (no `TargetFramework`, so the assets file holds every target) and then a
// design-time build per framework against that restore. So there are two locks, not four, and
// each entry carries its own `Framework` -- `Lock.entryFor framework name lock` is the lookup.
// Importing the two brands concurrently is safe; `-t 1` is no longer needed.
//
// Xake comes from `.bootstrap/` next to the Xake checkout (see build.fsc.fsx), not from a
// released package: this branch is not released. Stage it first:
//
//     cd <xake> && dotnet fsi build.fsx -- -- build && mkdir -p .bootstrap && cp out/netstandard2.0/*.dll .bootstrap/
//
#r "../../../.bootstrap/Xake.dll"
#r "../../../.bootstrap/Xake.Dotnet.dll"

open System.IO
open Xake
open Xake.Dotnet
open Xake.Tasks

let vars = {|
    Lock = Var.string(envVar = "LOCK", description = "Lock file to show")
    Packages = Var.string(envVar = "PACKAGES", description = "Folder to keep this build's packages in (a build agent caches it); default: the machine's NuGet cache")
|}

/// dataengine's three shipped libraries.
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

/// Both legs of `<TargetFrameworks>netstandard2.0;net472</TargetFrameworks>`. They go into one
/// import, and so into one lock per brand: the framework is what the design-time build passes
/// as `-p:TargetFramework=`, and it is recorded on each `Lock.Entry`.
let frameworks = [ "netstandard2.0"; "net472" ]
let brands = [ "MESCIUS"; "GCCN" ]

let lockFile brand = $"locks/%s{brand}.json"

/// Where this build's dependencies live. `PACKAGES=<dir>` puts them in a folder of the build's
/// own -- the one a build agent caches -- instead of the machine's NuGet cache; the same folder
/// expands `$(NuGetPackageRoot)` when a lock is read and receives what `Restore` downloads.
let restoreOptions = recipe {
    let! dir = vars.Packages
    match dir with
    | Some dir ->
        let! options = Restore.into dir
        return options
    | None -> return Restore.Options.Default
}

/// A lock read against the package folder this build uses.
let loadLock (path: string) = recipe {
    let! options = restoreOptions
    let! lock = Lock.loadWith (Roots.packageRootOverride (Restore.packageRoot options)) path
    return lock
}

do xakeScript {
    filelog "verify.log" Verbosity.Diag
    varschema vars

    rules [
        "locks" <== [ for b in brands -> lockFile b ]

        // one restore plus one design-time msbuild build per (project, framework) for this
        // brand; msbuild runs only when a project file or one of the files it imports changed
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

            let! lock = loadLock (lockFile brand)
            let project = Lock.entryFor fwk name lock

            let outputOf refPath =
                (Lock.entryFor fwk (Path.GetFileNameWithoutExtension (refPath: string)) lock).Output
                |> Option.defaultWith (fun () -> failwithf "project reference '%s' has no /out: in its own lock entry" refPath)

            // a reference this lock builds itself -- msbuild recorded it as the referenced
            // project's own `bin/Release/...`, which is not where this build puts it. Decided
            // by *name*, not by "the hash is empty": a leftover `bin/Release` from some earlier
            // msbuild run makes the import hash that path, and a remap keyed on the empty hash
            // then silently compiles against the stale artifact instead of what this run
            // produces (found 2026-09-23, after `dotnet build` had populated the fixture's bin/)
            let ownEntries = lock.Entries |> List.filter (fun e -> e.Framework = fwk) |> List.map (fun e -> e.Name) |> Set.ofList
            let ours =
                project.Dependencies.References
                |> List.filter (fun r -> ownEntries.Contains (Path.GetFileNameWithoutExtension r.Path))
                |> List.map (fun r -> r.Path) |> Set.ofList
            // `Lock.mapPaths` drops the hash of a reference whose path it changed, so a stale
            // one cannot gate the build either
            let mapped = project |> Lock.mapPaths (fun p -> if ours.Contains p then outputOf p else p)

            do! need (ours |> Set.toList |> List.map (outputOf >> relative))
            let! options = restoreOptions
            do! CscLock.compileWith { RunOptions.Default with Restore = options } mapped
        }

        // a CycloneDX 1.6 SBOM per compiled assembly, read out of the lock alone
        target "sbom/(fwk:*)/(brand:*)/(name:*).cdx.json" {
            let! m = getRuleMatches()
            let fwk, brand, name = m.["fwk"], m.["brand"], m.["name"]
            let! options = getCtxOptions()
            let relative (p: string) = Path.GetRelativePath (options.ProjectRoot, p)

            let! lock = loadLock (lockFile brand)
            let project = Lock.entryFor fwk name lock
            let output =
                project.Output
                |> Option.defaultWith (fun () -> failwithf "project '%s' (%s) has no /out: in its lock entry" name fwk)

            do! need [relative output]
            let! options = restoreOptions
            do! writeText (Sbom.cycloneDx (Sbom.forAssembly (Restore.packageRoot options) project output))
        }

        // compiles every project the locks name, for every framework and brand. Dynamic
        // because the assembly names -- the `name` rule match above -- come from the locks.
        // Both locks are read first and then *one* `need` asks for all 12 assemblies, so the
        // whole matrix is one dependency graph and the engine schedules it across all workers;
        // a `need` per brand would serialize the two groups
        command "build" {
            let! options = getCtxOptions()
            let relative (p: string) = Path.GetRelativePath (options.ProjectRoot, p)

            let outputs = ResizeArray<string>()
            for b in brands do
                let! lock = loadLock (lockFile b)
                for entry in lock.Entries do
                    match entry.Output with
                    | Some out -> outputs.Add (relative out)
                    | None -> ()
            do! need (List.ofSeq outputs)
        }

        // populates the package folder from the locks alone and compiles nothing: what a build
        // agent runs once, then caches the folder
        command "restore" {
            let! options = restoreOptions
            for b in brands do
                let! lock = loadLock (lockFile b)
                do! Restore.prepare options lock
        }

        command "sbom" {
            for b in brands do
                let! lock = loadLock (lockFile b)
                do! need [ for entry in lock.Entries -> $"sbom/%s{entry.Framework}/%s{b}/%s{entry.Name}.cdx.json" ]
        }

        command "show" {
            let! lockPath = vars.Lock
            let! lock = loadLock (lockPath |> Option.defaultValue (lockFile "MESCIUS"))
            for entry in lock.Entries do
                let e, c, d = entry.Evaluation, entry.Compilation, entry.Dependencies
                do! trace Message "%s [%s] (%s)" entry.Name entry.Framework e.Project
                do! trace Message "  evaluation   sdk %s, pin %s, imports %d" e.Sdk
                        (e.SdkPin |> Option.map Lock.sdkPinText |> Option.defaultValue "-") e.Imports.Length
                do! trace Message "  compilation  options %d, defines %d, sources %d, out %A" c.Options.Length c.Defines.Length c.Sources.Length entry.Output
                do! trace Message "               generated %s" (c.Generated |> List.map (fst >> Path.GetFileName) |> String.concat ", ")
                do! trace Message "  dependencies compiler %s %s" d.Compiler.Version d.Compiler.Path
                do! trace Message "               references %d (%d unhashed), analyzers %d, packages %d (%d direct)" d.References.Length
                        (d.References |> List.filter (fun r -> r.Sha256 = "") |> List.length) d.Analyzers.Length
                        d.Packages.Length (d.Packages |> List.filter (fun p -> p.Direct) |> List.length)
        }
    ]
}
