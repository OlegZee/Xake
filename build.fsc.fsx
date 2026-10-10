// Hermetic self-hosting build: the three libraries are compiled by fsc from one lock,
// `locks/xake.json`, with no `dotnet build` and no msbuild compiling anything. The lock is
// msbuild's own answer -- `Project.import` runs a design-time build per project and records the
// exact fsc command line, the generated assembly attributes, and the SHA-256 of the compiler,
// every reference and every msbuild file that took part -- and `build` replays it
// (`Lock.compile`), failing if any of those inputs differs from what was recorded.
//
// The lock is committed and written by one target only, `update-locks`: run it after a change
// to a project file, a package version or global.json, review the diff, commit. `check-locks`
// re-imports into obj/ and fails if the committed lock is stale. `build` reads the lock as a
// plain file and never runs msbuild or a restore of the projects.
//
// The F# compiler is the SDK's own `fsc.dll` (no NuGet package carries a current one), so the
// SDK is a prerequisite of the lock: global.json pins it exactly (`rollForward: disable`), the
// import records that pin, and the replay checks the SDK is installed before anything else.
//
// Bootstrap: the script runs on the *published* packages, as build.fsx does -- `Xake` (which
// carries Xake.dll and Xake.Dotnet.dll) and `Xake.Hermetic.Dotnet` (the lock). Not on `out/`:
// this script overwrites `out/`, and overwriting the assemblies fsi has loaded kills the run
// with a BadImageFormatException.
//
// The versions are the exact ones nuget.org carries: publish.yml appends the run number to
// the tag (`X.Y.Z.<run>`), so no plain `X.Y.Z` exists and a lower bound of it resolves to the
// next one up with NU1603. Bump them only after a release has landed (docs/devprocess.md).
//
// Testing and packing still shell out to the SDK: the test projects are built by msbuild, and
// the nupkg's net462 asset comes from `dotnet pack` (see docs/session.md).
#r "nuget: Xake, 3.6.0.24"
#r "nuget: Xake.Hermetic.Dotnet, 0.2.0.25"

open System.IO
open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet   // Project.import, Lock
open Xake.Tasks

/// The literal `<Version>` a project file declares, if any.
let projectVersion (path: string) =
    let m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText path, "<Version>([^<]+)</Version>")
    if m.Success then m.Groups.[1].Value.Trim() else "0.0.1"

let vars = {|
    // the package versions; `pack` hands them to `dotnet pack`. The assemblies `build` compiles
    // carry the version the lock was imported with (the projects' own, see `update-locks`)
    Version    = Var.create<string>(description = "Version number for the Xake package, e.g. 1.2.3") |> withDefault "0.0.1"
    // versioned on its own, as in build.fsx; the default is what its project file says
    HermeticVersion = Var.create<string>(description = "Version number for the Xake.Hermetic.Dotnet package, e.g. 0.1.0") |> withDefault (projectVersion "src/hermetic/Xake.Hermetic.Dotnet.fsproj")
    NUGET_KEY  = Var.env<string>(description = "API key for NuGet.org, required for pushing packages") |> withDefault ""
    TestFilter = Var.string(envVar = "FILTER", description = "Optional filter clause for test selection, e.g. 'MyNamespace.*Tests'")
    // as in build.fsx: an extra restore source for Xake.Hermetic.Dotnet, only to develop it
    // against an unreleased Xake (docs/devprocess.md); normally it restores from nuget.org
    NugetSource = Var.string(cliArg = "NUGET_SOURCE", envVar = "NUGET_SOURCE", description = "Extra NuGet source for restoring Xake.Hermetic.Dotnet, for developing against an unreleased Xake")
|}

/// Only netstandard2.0 is compiled here; the nupkg gets its net462 asset from `dotnet pack`.
let frameworks = ["netstandard2.0"]

/// The libraries this script builds: the assembly each one produces, and its project.
let libraries =
    [ "Xake",                 "src/core/Xake.fsproj"
      "Xake.Dotnet",          "src/dotnet/Xake.Dotnet.fsproj"
      "Xake.Hermetic.Dotnet", "src/hermetic/Xake.Hermetic.Dotnet.fsproj" ]

/// The lock of all three, every framework in it. Committed; only `update-locks` writes it.
let lockPath = "locks/xake.json"

/// Assembly and doc file every library produces, for every target framework.
let binaries =
    [ for name, _ in libraries do
      for fwk in frameworks do
      for ext in ["dll"; "xml"]
        -> $"out/%s{fwk}/%s{name}.%s{ext}"
    ]

/// One `Project.import` of the three projects into `output` (relative to the project root).
/// No `Version` property: the lock carries the version each project declares, the generated
/// `AssemblyInfo.fs` included (its commit sha stays a `$(SourceRevisionId)` token, resolved
/// from HEAD when the lock is compiled). The hermetic project references the *published* Xake
/// package, so its entry compiles against that, not against the Xake.dll built here.
let importInto (output: string) = recipe {
    let! options = getCtxOptions ()
    do! Project.import {
        Project.ImportOptions.Default with
            Projects = libraries |> List.map snd
            Frameworks = frameworks
            Configuration = "Release"
            Output = Path.Combine (options.ProjectRoot, output)
    }
}

/// The nuspec dependencies of the hermetic package must name `Xake` (the package that carries
/// both Xake.dll and Xake.Dotnet.dll) and never `Xake.Dotnet`, which is not a package; returns
/// the problems found (none means the package is right). The range itself is the fsproj's.
let checkXakeDependency (nupkg: string) =
    use zip = System.IO.Compression.ZipFile.OpenRead nupkg
    let entry = zip.Entries |> Seq.find (fun e -> e.FullName.EndsWith ".nuspec" && not (e.FullName.Contains "/"))
    use reader = new StreamReader(entry.Open())
    let doc = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd())
    let ids =
        doc.Descendants()
        |> Seq.filter (fun e -> e.Name.LocalName = "dependency")
        |> Seq.map (fun e -> e.Attribute(System.Xml.Linq.XName.Get "id").Value)
        |> List.ofSeq
    [ if not (List.contains "Xake" ids) then yield "no dependency on package Xake"
      if List.contains "Xake.Dotnet" ids then yield "depends on Xake.Dotnet, which is not a package" ]

/// The two NuGet packages, versioned independently.
/// `Xake` is packed from src/dotnet, which pulls the core's assembly into the same nupkg (see
/// src/dotnet/Xake.Dotnet.fsproj). `Xake.Hermetic.Dotnet` is packed from src/hermetic and
/// depends on the published `Xake` through its PackageReference (the range is in the fsproj).
///
/// Each package is packed into a folder of its own, `out/pkg/<id>/<id>.<version>.nupkg`: a
/// single mask such as `out/(id:*).(ver:*).nupkg` cannot tell `Xake.Hermetic.Dotnet.0.1.0` from
/// a Xake version `Hermetic.Dotnet.0.1.0` (and the last matching rule wins), and a folder per
/// id also lets CI push `out/pkg/<id>/*.nupkg` without picking up the other package.
let xakePackage = "Xake"
let hermeticPackage = "Xake.Hermetic.Dotnet"
let packagePath id version = $"out/pkg/%s{id}/%s{id}.%s{version}.nupkg"

/// `-p:RestoreAdditionalProjectSources=...` when NUGET_SOURCE is set, nothing otherwise.
let restoreSource = recipe {
    let! source = vars.NugetSource
    return [ for s in Option.toList source -> "-p:RestoreAdditionalProjectSources=" + s ]
}

let dotnet arglist = sh "dotnet" { args arglist; failonerror }

do xakeScript {
    filelog "build.log" Verbosity.Diag
    varschema vars

    // every compilation names only paths under the checkout, the build's own package folder
    // and the pinned SDK's fsc (the one prerequisite)
    var "HERMETIC" "on"
    var "NUGET_PACKAGES" ".packages"

    rules [
        "main" <<< ["build"; "test"]

        "build" <== binaries
        "clean" => rm {dir "out"}

        command "test" {
            let! testFilter = vars.TestFilter
            let where = [ for f in Option.toList testFilter do yield $"--filter Name~\"{f}\"" ]

            do! sh "dotnet test src/tests -c Release" { args where; failonerror }
            do! sh "dotnet test src/hermetic.tests -c Release" { args where; failonerror }
        }

        (* The lock *)
        // the only place that writes the lock: one restore and one msbuild design-time build
        // per project; run it after a project change, review the diff, commit
        "update-locks" => importInto lockPath

        // a fresh import (under obj/, nothing committed is touched) against the committed lock
        "check-locks" => recipe {
            let fresh = "obj/xake/check/xake.json"
            do! importInto fresh
            let! options = getCtxOptions ()
            let! roots = Roots.current
            let read (p: string) = Lock.read roots (Path.Combine (options.ProjectRoot, p))
            let committed, current = read lockPath, read fresh
            let key (e: Lock.Entry) = e.Name, e.Framework
            let diffs =
                [ for e in current.Entries do
                    match committed.Entries |> List.tryFind (fun c -> key c = key e) with
                    | Some c -> for d in Lock.diffText roots c e -> sprintf "%s (%s): %s" e.Name e.Framework d
                    | None -> yield sprintf "+ entry %s (%s)" e.Name e.Framework
                  for c in committed.Entries do
                    if not (current.Entries |> List.exists (fun e -> key e = key c)) then
                        yield sprintf "- entry %s (%s)" c.Name c.Framework ]
            match diffs with
            | [] -> do! trace Level.Info "%s is up to date (%d entries)" lockPath current.Entries.Length
            | _ -> failwithf "%s is stale; run update-locks and commit:\n%s" lockPath (String.concat "\n" diffs)
        }

        // one rule compiles them all, from the lock: which library and which framework is read
        // off the target being built
        targets ["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"] {
            let! framework = getRuleMatch "fwk"
            let! name = getRuleMatch "lib"
            let! options = getCtxOptions ()
            let ours library ext = Path.Combine (options.ProjectRoot, "out", framework, $"%s{library}.%s{ext}")

            // the build's own package folder (NUGET_PACKAGES, below): the lock's
            // $(NuGetPackageRoot) is read as that folder, and the replay restores into it what
            // the lock names and the folder lacks
            let! packageRoot = DotNetFwk.packageRoot ()
            let! lock = Lock.loadWith (Roots.packageRootOverride packageRoot) lockPath
            let entry = Lock.entryFor framework name lock

            // msbuild compiles into the project's obj/ and points a project reference at the
            // referenced project's output there; this build has its own layout. The outputs
            // (`-o:`, `--doc:`) go to out/<fwk>/, and a project reference -- the one reference
            // the lock leaves unhashed, as it is not built yet -- to the assembly this rule
            // builds for it. Every other reference keeps its path and its hash.
            let intermediate = entry.Output |> Option.map Path.GetDirectoryName
            let unbuilt =
                entry.Dependencies.References
                |> List.filter (fun r -> r.Sha256 = "")
                |> List.map (fun r -> r.Path)
            let produced (p: string) =
                Some (Path.GetDirectoryName p) = intermediate
                && Path.GetFileNameWithoutExtension p = name
                && List.contains (Path.GetExtension p) [".dll"; ".xml"; ".pdb"]
            let mapped =
                entry |> Lock.mapPaths (fun p ->
                    if List.contains p unbuilt then ours (Path.GetFileNameWithoutExtension p) "dll"
                    elif produced p then ours name (Path.GetExtension(p).TrimStart '.')
                    else p)

            do! need [ for r in unbuilt -> Path.GetRelativePath (options.ProjectRoot, ours (Path.GetFileNameWithoutExtension r) "dll") ]
            // HERMETIC=on: Lock.compile (0.2) does not run the base's gate itself yet, so it is
            // applied here, over the compilation exactly as it is about to be replayed
            do! HermeticMode.enforce name (Fsc.hermeticInputs mapped.Fsc)
            do! Lock.compileWith
                    { Lock.Options.Default with Restore = { Restore.Options.Default with PackageRoot = Some packageRoot } }
                    mapped
        }

        (* Nuget publishing rules *)
        // both packages, each at its own version; they are independent (the hermetic one
        // builds on the published Xake), so a release of one can pack just that one
        command "pack" {
            let! version = vars.Version
            let! hermeticVersion = vars.HermeticVersion
            do! need [packagePath xakePackage version; packagePath hermeticPackage hermeticVersion]
        }

        command "pack-hermetic" {
            let! hermeticVersion = vars.HermeticVersion
            do! need [packagePath hermeticPackage hermeticVersion]
        }

        target $"out/pkg/%s{xakePackage}/%s{xakePackage}.(ver:*).nupkg" {
            let! ver = getRuleMatch "ver"
            do! dotnet [
                "pack"; "src/dotnet"
                "-c"; "Release"
                $"/p:Version={ver}"
                "--output"; $"out/pkg/%s{xakePackage}/"
            ]
        }

        target $"out/pkg/%s{hermeticPackage}/%s{hermeticPackage}.(ver:*).nupkg" {
            let! ver = getRuleMatch "ver"
            let! nupkg = getTargetFullName()
            let! source = restoreSource

            // nothing of this script's own Xake is involved: src/hermetic references the
            // published package, restored from nuget.org (or NUGET_SOURCE)
            do! dotnet ([
                "pack"; "src/hermetic"
                "-c"; "Release"
                $"/p:Version={ver}"
                "--output"; $"out/pkg/%s{hermeticPackage}/"
              ] @ source)

            match checkXakeDependency nupkg with
            | [] -> do! trace Level.Info "%s: depends on package Xake" (Path.GetFileName nupkg)
            | problems ->
                File.Delete nupkg
                failwithf "%s: %s" nupkg (String.concat "; " problems)
        }

        // push needs pack to be explicitly called in advance; one command per package, since
        // they are released independently
        command "push" {
            let! version = vars.Version
            let! nuget_key = vars.NUGET_KEY
            do! dotnet [
                "nuget"; "push"; packagePath xakePackage version
                "--source"; "https://www.nuget.org/api/v2/package"
                "--api-key"; nuget_key
            ]
        }

        command "push-hermetic" {
            let! version = vars.HermeticVersion
            let! nuget_key = vars.NUGET_KEY
            do! dotnet [
                "nuget"; "push"; packagePath hermeticPackage version
                "--source"; "https://www.nuget.org/api/v2/package"
                "--api-key"; nuget_key
            ]
        }
    ]
}
