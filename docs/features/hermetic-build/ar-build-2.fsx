// ActiveReports (ar-net-core-dataengine + ar-net-core-page, branch develop) built from project descriptions
// and locks (Xake docs/hermetic/lock.md). The brand is a prefix of every path and command:
// <brand>/projects/<name>/xake.project.json, <brand>/locks/<name>/<cfg>.json, <brand>/xake.fingerprints,
// out/<brand>/<cfg>/<fwk>/<name>/, commands import::mescius, update-locks::gccn, build::mescius::Release.
// `import` (msbuild, under the brand's global properties) and `update-locks` are the only writers of the
// committed files; `build` compiles from them with csc, no msbuild.
#i "nuget: /Users/olegz/Projects-work/ar/xake-ar/feed"
#r "nuget: Xake, 3.7.1-night"
#r "nuget: Xake.Hermetic.Dotnet, 0.3.1-night"

open System.IO
open System.Text.RegularExpressions
open Xake
open Xake.Tasks
open Xake.Hermetic.Dotnet

let vars = {| Version = Var.create<string>(description = "Package version") |> withDefault "4.6.0" |}

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
let nameOf (path: string) = Path.GetFileNameWithoutExtension path

let compile = recipe {                                          // a dll, or its doc .xml (side output of the same csc)
    let! b = getRuleMatch "b"
    let! cfg = getRuleMatch "cfg"
    let! fwk = getRuleMatch "fwk"
    let! name = getRuleMatch "name"
    do! Locks.compileAs (lockOf b name cfg) fwk versions
}

do xakeScript {
    filelog "build.log" Verbosity.Chatty
    varschema vars
    var "NUGET_PACKAGES" ".packages"

    rules [
        "main" <<< ["build"]
        "build" <== [ for b in brands do for cfg in configs -> $"build::{b}::{cfg}" ]

        // (a) project file -> description, under msbuild with the brand's props; records the msbuild files read
        command "import::(b:*)" {
            let! b = getRuleMatch "b"
            let! (Filelist files) = getFiles projects
            let! imported = Import.descriptions [ for f in files -> f.FullName ] (nameOf >> descOf b) (props b)
            for i in imported do do! Fingerprints.record (fingerprints b) i.Description i.Inputs
        }
        // (b) description -> lock, referenced projects first; the packages of all locks come from one restore
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
            for cfg in configs do do! Locks.restoreAll (descriptions b) cfg (lockOf b) (descOf b)
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
        "out/(b:*)/(cfg:*)/(fwk:*)/(name:*)/*.dll" ..> compile
        "out/(b:*)/(cfg:*)/(fwk:*)/(name:*)/*.xml" ..> compile
        command "clean::(b:*)" {
            let! b = getRuleMatch "b"
            do! rm { dir $"out/{b}" }
        }

        // ---- basic pack (PR 8 not done): the project's own .nuspec, tokens filled from the description and
        // the brand, lib/ files taken from out/<b>/Release instead of bin/Release; Pack.nupkg writes the zip
        command "pack::(b:*)" {
            let! b = getRuleMatch "b"
            do! need [ $"out/{b}/pkg/Export.Text.Page.nupkg" ]             // one library per brand
        }
        target "out/(b:*)/pkg/(name:*).nupkg" {
            let! b = getRuleMatch "b"
            let! name = getRuleMatch "name"
            let! version = vars.Version
            let! d = Description.load (descOf b name)
            let nuspec = Path.ChangeExtension (Path.GetFullPath (Path.Combine (Path.GetDirectoryName (descOf b name), d.Project)), ".nuspec")
            let iconUrl = if b = "gccn" then "https://download2.grapecity.com/logo/ar-nuget.png"   // IconUrl is dropped by Import
                          else "https://cdn.mescius.io/assets/developer/brand/logo-mescius.png"
            let m = d.Package.Value
            let tokens = [ "prefix", b.ToUpperInvariant (); "version", version; "versionRange", $"[{version},{int (version.Split '.').[0] + 1}.0.0)"
                           "authors", defaultArg m.Authors ""; "owners", defaultArg m.Authors ""; "copyright", defaultArg m.Copyright ""; "iconUrl", iconUrl ]
            let text = tokens |> List.fold (fun (t: string) (k, v) -> t.Replace ($"${k}$", v)) (File.ReadAllText nuspec)
            let files =
                [ for f in Regex.Matches (text, """<file src="([^"]+)" target="([^"]+)" */>""") ->
                    let src, target = f.Groups[1].Value.Replace ('\\', '/'), f.Groups[2].Value.Replace ('\\', '/')
                    match Regex.Match (src, "bin/Release/([^/]+)/([^/]+)$") with
                    | r when r.Success -> { Pack.Path = target; Pack.Source = Path.GetFullPath (outOf b "Release" r.Groups[1].Value name + "/" + r.Groups[2].Value) }
                    | _ -> { Pack.Path = target; Pack.Source = Path.GetFullPath (Path.Combine (Path.GetDirectoryName nuspec, src)) } ]
            do! needFiles (Filelist [ for f in files -> File.make f.Source ])
            let spec = $"obj/pack/{b}/{name}.nuspec"
            Directory.CreateDirectory (Path.GetDirectoryName spec) |> ignore
            File.WriteAllText (spec, Regex.Replace (text, "(?s)\\s*<files>.*</files>", ""))
            let! target = getTargetFile ()
            Directory.CreateDirectory (Path.GetDirectoryName target.FullName) |> ignore
            Pack.nupkg target.FullName spec files Pack.Options.Default
        }
    ]
}
