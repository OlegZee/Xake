// ActiveReports (ar-net-core-dataengine + ar-net-core-page, branch develop) built from project
// descriptions and locks (Xake docs/hermetic/lock.md). The brand is a prefix of every path and command:
// <brand>/projects/<name>/xake.project.json, <brand>/locks/<name>/<cfg>.json, <brand>/xake.fingerprints,
// out/<brand>/<cfg>/<fwk>/<name>/, commands import::mescius, update-locks::gccn, build::mescius::Release.
// `import` (msbuild, under the brand's global properties) and `update-locks` are the only writers of the
// committed files; `build` compiles from them with csc, no msbuild.
#r "nuget: Xake, 3.7.0.26"
#r "nuget: Xake.Hermetic.Dotnet, 0.3.0.28"

open System.IO
open System.Text.RegularExpressions
open Xake
open Xake.Tasks
open Xake.Hermetic.Dotnet

let vars = {|
    Version = Var.create<string>(description = "Package version") |> withDefault "4.6.0"
|}

let brands = [ "mescius"; "gccn" ]
let configs = [ "Debug"; "Release" ]
let projects = !! "ar-net-core-dataengine/src/*/*.csproj" ++ "ar-net-core-page/src/**/*.csproj"
let props (b: string) = [ "Brand", b.ToUpperInvariant (); "LocalBuild", "true" ]   // LocalBuild: page -> dataengine by ProjectReference
let descOf b name = $"{b}/projects/{name}/xake.project.json"
let descriptions b = !! $"{b}/projects/*/xake.project.json"
let lockOf b name cfg = $"{b}/locks/{name}/{cfg}.json"
let outOf b cfg fwk name = $"out/{b}/{cfg}/{fwk}/{name}"         // a folder per project: assembly names are branded
let fingerprints b = $"{b}/xake.fingerprints"
let versions = [ "**", vars.Version ]
let packed = [ "Export.Text.Page" ]                            // basic pack: one library per brand
let iconUrl = dict [ "mescius", "https://cdn.mescius.io/assets/developer/brand/logo-mescius.png"
                     "gccn", "https://download2.grapecity.com/logo/ar-nuget.png" ]   // IconUrl is dropped by Import
let nameOf (path: string) = Path.GetFileNameWithoutExtension path
let resxOut b name (resx: string) = $"obj/resx/{b}/{name}/" + Path.ChangeExtension (resx, ".resources")

// Workarounds for what Import/Described do not handle yet (ar-experiment.md, gaps): a .resx is compiled
// by the script and the description points at the .resources; NoWarn is dropped by Import; Rdl writes
// `version=` in lower case, which Import does not read; a dropped CLSCompliant assembly attribute is
// put back as a source file committed beside the description; msbuild passes transitive project
// references to csc, Locks.generate only direct ones, so projectRefs get the closure.
let pinned = dict [ "System.Text.Json", "8.0.5"; "System.Memory", "4.6.0" ]
let noWarn = "/nowarn:1701,1702,1591,0618,0612,1589"    // 1589: csc runs in the project root, not the project folder
let frameworkRefs = dict [ "net472", [ "WindowsBase" ] ]  // DocumentFormat.OpenXml's frameworkAssemblies, not read from assets
let transitive (paths: string list) =
    let ds = paths |> List.map (fun p -> p, Description.read p)
    let refsOf = dict [ for _, d in ds -> d.Name, d.ProjectRefs ]
    let rec close seen = function
        | [] -> seen
        | r :: rest when List.contains r seen -> close seen rest
        | r :: rest -> close (seen @ [ r ]) (rest @ (match refsOf.TryGetValue r with | true, rs -> rs | _ -> []))
    for p, d in ds do Description.write p { d with ProjectRefs = close [] d.ProjectRefs } |> ignore
let fixup b (path: string) (dropped: string list) =
    let d = Description.read path
    let attributes = Path.Combine (Path.GetDirectoryName path, "AssemblyAttributes.cs")
    let clsCompliant = dropped |> List.exists (fun s -> s.EndsWith "assembly attribute System.CLSCompliantAttribute")
    if clsCompliant then File.WriteAllText (attributes, "[assembly: System.CLSCompliant(true)]\n")
    let projectDir = Path.GetDirectoryName (Path.GetFullPath (Path.Combine (Path.GetDirectoryName path, d.Project)))
    let resource (r: Description.Resource) =
        if not (r.File.EndsWith ".resx") then r else
        let ns = defaultArg d.RootNamespace d.Name
        { File = (Path.GetRelativePath (projectDir, Path.GetFullPath (resxOut b d.Name r.File))).Replace ('\\', '/')
          Name = Some (ns + "." + (Path.ChangeExtension (r.File, null)).Replace ('/', '.') + ".resources") }
    let byFramework =
        if not (d.Packages |> List.exists (fun p -> p.Id = "DocumentFormat.OpenXml")) then d.ByFramework else
        d.ByFramework |> List.map (fun (fwk, i) ->
            fwk, (match frameworkRefs.TryGetValue fwk with
                  | true, rs -> { i with References = i.References @ (rs |> List.filter (fun r -> not (List.contains r i.References))) }
                  | _ -> i))
    let pin (p: Description.Package) = if p.Version.IsSome || not (pinned.ContainsKey p.Id) then p else { p with Version = Some pinned[p.Id] }
    Description.write path
        { d with Resources = List.map resource d.Resources; Packages = List.map pin d.Packages; ByFramework = byFramework
                 Sources = d.Sources @ [ for _ in 1 .. (if clsCompliant then 1 else 0) -> (Path.GetRelativePath (projectDir, Path.GetFullPath attributes)).Replace ('\\', '/') ]
                 Options = { d.Options with Args = d.Options.Args |> List.filter ((<>) noWarn) |> fun a -> a @ [ noWarn ] } } |> ignore

do xakeScript {
    filelog "build.log" Verbosity.Chatty
    varschema vars
    var "NUGET_PACKAGES" ".packages"

    rules [
        "main" <<< ["build"]
        "build" <<< [ for b in brands do for cfg in configs -> $"build::{b}::{cfg}" ]

        // (a) project file -> description, under msbuild with the brand's props; records the msbuild files read
        command "import::(b:*)" {
            let! b = getRuleMatch "b"
            let! (Filelist files) = getFiles projects
            let! imported = Import.descriptions [ for f in files -> f.FullName ] (nameOf >> descOf b) (props b)
            for i in imported do
                for d in i.Dropped do do! trace Level.Info "%s %s: dropped %s" b i.Description d
                fixup b i.Description i.Dropped
            transitive [ for i in imported -> i.Description ]
            for i in imported do do! Fingerprints.record (fingerprints b) i.Description i.Inputs
        }
        // (b) description -> lock, referenced projects first
        "lock::(b:*)::(name:*)::(cfg:*)" => recipe {
            let! b = getRuleMatch "b"
            let! name = getRuleMatch "name"
            let! cfg = getRuleMatch "cfg"
            let! d = Description.load (descOf b name)
            do! need [ for r in d.ProjectRefs -> $"lock::{b}::{r}::{cfg}" ]
            do! alwaysRerun ()
            do! Locks.generate (lockOf b) (outOf b) cfg (descOf b name)
            do! Fingerprints.record (fingerprints b) (lockOf b name cfg) (descOf b name :: [ for r in d.ProjectRefs -> lockOf b r cfg ])
        }
        command "update-locks::(b:*)" {
            let! b = getRuleMatch "b"
            let! (Filelist ds) = getFiles (descriptions b)
            do! need [ for d in ds do for cfg in configs -> $"lock::{b}::{Path.GetFileName (Path.GetDirectoryName d.FullName)}::{cfg}" ]
        }
        "check::(b:*)" => recipe {
            let! b = getRuleMatch "b"
            do! Fingerprints.check (fingerprints b)
        }

        // (c) from the committed descriptions and locks only (fresh clone, CI)
        command "build::(b:*)::(cfg:*)" {
            let! b = getRuleMatch "b"
            let! cfg = getRuleMatch "cfg"
            do! Fingerprints.warn (fingerprints b)
            do! Locks.needAll (descriptions b) cfg (lockOf b) (outOf b)
        }
        target "out/(b:*)/(cfg:*)/(fwk:*)/(name:*)/*.dll" {
            let! b = getRuleMatch "b"
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            do! Locks.compileAs (lockOf b name cfg) fwk versions
        }
        target "obj/resx/(b:*)/(name:*)/**/*.resources" {     // .resx -> .resources (plain strings, Xake.Dotnet.Resx)
            let! b = getRuleMatch "b"
            let! name = getRuleMatch "name"
            let! target = getTargetFile ()
            let! d = Description.load (descOf b name)
            let projectDir = Path.GetDirectoryName (Path.GetFullPath (Path.Combine (Path.GetDirectoryName (descOf b name), d.Project)))
            let relative = Path.GetRelativePath (Path.GetFullPath $"obj/resx/{b}/{name}", target.FullName)
            let resx = Path.Combine (projectDir, Path.ChangeExtension (relative, ".resx"))
            do! needFiles (Filelist [ File.make resx ])
            Xake.Dotnet.Resx.compile resx target.FullName
        }
        // basic pack (PR 8 not done): the project's own .nuspec, tokens filled from the description and the
        // brand, lib/ files taken from out/<b>/Release instead of bin/Release; Pack.nupkg writes the zip
        target "out/(b:*)/pkg/(name:*).nupkg" {
            let! b = getRuleMatch "b"
            let! name = getRuleMatch "name"
            let! version = vars.Version
            let! d = Description.load (descOf b name)
            let nuspec = Path.ChangeExtension (Path.GetFullPath (Path.Combine (Path.GetDirectoryName (descOf b name), d.Project)), ".nuspec")
            let m = d.Package.Value
            let tokens = [ "prefix", b.ToUpperInvariant (); "version", version; "versionRange", $"[{version},{int (version.Split '.').[0] + 1}.0.0)"
                           "authors", defaultArg m.Authors ""; "owners", defaultArg m.Authors ""; "copyright", defaultArg m.Copyright ""; "iconUrl", iconUrl[b] ]
            let text = tokens |> List.fold (fun (t: string) (k, v) -> t.Replace ($"${k}$", v)) (File.ReadAllText nuspec)
            let files =
                [ for f in Regex.Matches (text, """<file src="([^"]+)" target="([^"]+)" */>""") ->
                    let src = f.Groups[1].Value.Replace ('\\', '/')
                    let target = f.Groups[2].Value.Replace ('\\', '/')
                    match Regex.Match (src, "bin/Release/([^/]+)/([^/]+)$") with
                    | r when r.Success -> { Pack.Path = target; Pack.Source = Path.GetFullPath (outOf b "Release" r.Groups[1].Value name + "/" + r.Groups[2].Value) }
                    | _ -> { Pack.Path = target; Pack.Source = Path.GetFullPath (Path.Combine (Path.GetDirectoryName nuspec, src)) } ]
            let dll (f: Pack.Entry) = f.Source.EndsWith ".dll"
            do! needFiles (Filelist [ for f in files do if dll f then File.make f.Source ])   // the .xml is the dll rule's side output
            do! needFiles (Filelist [ for f in files do if not (dll f) then File.make f.Source ])
            let spec = $"obj/pack/{b}/{name}.nuspec"
            Directory.CreateDirectory (Path.GetDirectoryName spec) |> ignore
            File.WriteAllText (spec, Regex.Replace (text, "(?s)\\s*<files>.*</files>", ""))
            let! target = getTargetFile ()
            Directory.CreateDirectory (Path.GetDirectoryName target.FullName) |> ignore
            Pack.nupkg target.FullName spec files Pack.Options.Default
        }
        command "pack::(b:*)" {
            let! b = getRuleMatch "b"
            do! need [ for name in packed -> $"out/{b}/pkg/{name}.nupkg" ]
        }
        command "clean::(b:*)" {
            let! b = getRuleMatch "b"
            do! rm { dir $"out/{b}" }
        }
    ]
}
