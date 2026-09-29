#r "nuget: Xake, 3.0.1"
// #r "out/netstandard2.0/Xake.dll"

open Xake
open Xake.Tasks

/// The literal `<Version>` a project file declares, if any.
let projectVersion (path: string) =
    let m = System.Text.RegularExpressions.Regex.Match(System.IO.File.ReadAllText path, "<Version>([^<]+)</Version>")
    if m.Success then m.Groups.[1].Value.Trim() else "0.0.1"

let vars = {|
    Version    = Var.create<string>(description = "Version number for the Xake package, e.g. 1.2.3") |> withDefault "0.0.1"
    // Xake.Hermetic.Dotnet is versioned on its own (0.x, may break): -d HermeticVersion=... or
    // $HERMETIC_VERSION. The default is the <Version> its project file carries.
    HermeticVersion = Var.create<string>(description = "Version number for the Xake.Hermetic.Dotnet package, e.g. 0.1.0") |> withDefault (projectVersion "src/hermetic/Xake.Hermetic.Dotnet.fsproj")
    NUGET_KEY  = Var.env<string>(description = "API key for NuGet.org, required for pushing packages") |> withDefault ""
    TestFilter = Var.string(envVar = "FILTER", description = "Optional filter clause for test selection, e.g. 'MyNamespace.*Tests'")
|}

let frameworks = ["netstandard2.0" (*; "net462" *)]

/// A library this script builds. `Needs` are the libraries it is compiled against, `Package`
/// is the NuGet package it ships in (which decides its version).
type Library = { Name: string; Dir: string; Needs: string list; Package: string }

let libraries =
    [ { Name = "Xake";                 Dir = "src/core";     Needs = [];                      Package = "Xake" }
      { Name = "Xake.Dotnet";          Dir = "src/dotnet";   Needs = ["Xake"];                Package = "Xake" }
      { Name = "Xake.Hermetic.Dotnet"; Dir = "src/hermetic"; Needs = ["Xake"; "Xake.Dotnet"]; Package = "Xake.Hermetic.Dotnet" } ]

let library name = libraries |> List.find (fun lib -> lib.Name = name)

/// Assembly and doc file a library produces, for every target framework.
let binaries name =
    [ for fwk in frameworks do
      for ext in ["dll"; "xml"]
        -> $"out/%s{fwk}/%s{name}.%s{ext}"
    ]

/// The two NuGet packages, versioned independently.
/// `Xake` is packed from src/dotnet, which pulls the core's assembly into the same nupkg (see
/// src/dotnet/Xake.Dotnet.fsproj). `Xake.Hermetic.Dotnet` is packed from src/hermetic and
/// depends on `Xake [3.4.0, 4.0.0)`.
///
/// Each package is packed into a folder of its own, `out/pkg/<id>/<id>.<version>.nupkg`: a
/// single mask such as `out/(id:*).(ver:*).nupkg` cannot tell `Xake.Hermetic.Dotnet.0.1.0` from
/// a Xake version `Hermetic.Dotnet.0.1.0` (and the last matching rule wins), and a folder per
/// id also lets CI push `out/pkg/<id>/*.nupkg` without picking up the other package.
let xakePackage = "Xake"
let hermeticPackage = "Xake.Hermetic.Dotnet"
let packagePath id version = $"out/pkg/%s{id}/%s{id}.%s{version}.nupkg"

let versionOf package =
    if package = hermeticPackage then vars.HermeticVersion else vars.Version

/// The nuspec dependency the hermetic package must declare on `Xake`. The range is written by
/// the `XakeDependencyRange` target in src/hermetic/Xake.Hermetic.Dotnet.fsproj, which relies
/// on a private NuGet item name; if a future SDK renames it, pack silently writes `>= 1.0.0`.
let expectedXakeRange = "[3.4.0, 4.0.0)"

/// Reads the nuspec out of a nupkg and checks every `Xake` dependency carries the range;
/// returns the problems found (none means the package is right).
let checkXakeRange (nupkg: string) =
    use zip = System.IO.Compression.ZipFile.OpenRead nupkg
    let entry = zip.Entries |> Seq.find (fun e -> e.FullName.EndsWith ".nuspec" && not (e.FullName.Contains "/"))
    use reader = new System.IO.StreamReader(entry.Open())
    let doc = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd())
    let deps =
        doc.Descendants()
        |> Seq.filter (fun e -> e.Name.LocalName = "dependency" && e.Attribute(System.Xml.Linq.XName.Get "id").Value = "Xake")
        |> Seq.map (fun e -> e.Attribute(System.Xml.Linq.XName.Get "version").Value)
        |> List.ofSeq
    [ if List.isEmpty deps then yield "no dependency on package Xake"
      for v in deps do
          if v <> expectedXakeRange then yield $"Xake dependency is '%s{v}', expected '%s{expectedXakeRange}'" ]

let dotnet arglist = sh "dotnet" { args arglist; failonerror }

do xakeScript {
    filelog "build.log" Diag
    varschema vars

    rules [
        "main" <<< ["build"; "test"]

        "build" <== List.collect (fun lib -> binaries lib.Name) libraries
        "clean" => rm {dir "out"}

        command "test" {
            let! testFilter = vars.TestFilter
            let where = [ for f in Option.toList testFilter do yield $"--filter Name~\"{f}\"" ]

            // one after the other: both build the same library projects into the same obj/
            do! sh "dotnet test src/tests -c Release" { args where; failonerror }
            do! sh "dotnet test src/hermetic.tests -c Release" { args where; failonerror }
        }

        // one rule compiles them all: which library and which framework is asked for
        // is read off the target being built
        targets ["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"] {

            let! framework = getRuleMatch "fwk"
            let! lib = getRuleMatch "lib" |> Recipe.map library

            let! allFiles = getFiles <| fileset {
                basedir lib.Dir
                includes $"%s{lib.Name}.fsproj"
                includes "**/*.fs"
            }

            do! needFiles allFiles
            do! need [for dep in lib.Needs -> $"out/%s{framework}/%s{dep}.dll"]
            let! version = versionOf lib.Package

            // BuildProjectReferences=false: the referenced libraries are already in out/ (the
            // `need` above), and a global /p:Version flows into project references -- building
            // Xake.Hermetic.Dotnet would otherwise recompile Xake and Xake.Dotnet at the
            // hermetic version and overwrite their out/ assemblies with it.
            do! dotnet [
                "build"
                lib.Dir
                "/p:Version=" + version
                "-p:BuildProjectReferences=false"
                "--configuration"; "Release"
                "--framework"; framework
                "--output"; "./out/" + framework
            ]
        }

        (* Nuget publishing rules *)
        // both packages, each at its own version; the hermetic one waits for Xake (see below)
        command "pack" {
            let! version = vars.Version
            let! hermeticVersion = vars.HermeticVersion
            do! need [packagePath xakePackage version; packagePath hermeticPackage hermeticVersion]
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
            let! xakeVersion = vars.Version
            let! nupkg = getTargetFullName()

            // The hermetic package is compiled against the Xake assemblies at *Xake's* version:
            // a global /p:Version flows into project references, so they are built first at
            // that version (this also serializes the two packs, which share the base projects'
            // obj/) and pack does not rebuild them.
            do! need [packagePath xakePackage xakeVersion]
            do! dotnet [
                "build"; "src/dotnet"
                "-c"; "Release"
                $"/p:Version={xakeVersion}"
            ]
            do! dotnet [
                "pack"; "src/hermetic"
                "-c"; "Release"
                $"/p:Version={ver}"
                "-p:BuildProjectReferences=false"
                "--output"; $"out/pkg/%s{hermeticPackage}/"
            ]

            // the nuspec's dependency range is produced by a target relying on a private NuGet
            // item name, so it is asserted here rather than trusted
            match checkXakeRange nupkg with
            | [] -> do! trace Level.Info "%s: depends on Xake %s" (System.IO.Path.GetFileName nupkg) expectedXakeRange
            | problems ->
                System.IO.File.Delete nupkg
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
