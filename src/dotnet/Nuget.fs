namespace Xake.Dotnet

open System.IO
open System.Xml

open Xake

/// Reads what a restore already knows about the packages a project depends on:
/// `project.assets.json` for the graph, and the package cache (`.nupkg.metadata` and the
/// nuspec) for what identifies and licenses each package. Pure, read-only -- nothing here
/// writes to the cache or talks to the network. `Nuget` is the evidence half of the SBOM: it
/// answers "what did the compile actually see" from files restore already produced, the same
/// way `Project.import` answers "what did the compiler actually run".
module Nuget =

    /// One package as the cache and the restore graph describe it.
    type Package = {
        Id: string
        Version: string
        /// base64 sha512 from `.nupkg.metadata`'s `contentHash` ("" when the cache has no metadata)
        Sha512: string
        /// feed URL from `.nupkg.metadata`'s `source` ("" when unknown)
        Source: string
        /// SPDX expression, or the license file name, or the legacy `licenseUrl` ("" when none)
        License: string
        /// `authors` from the nuspec
        Supplier: string
        /// repository url from the nuspec ("" when none)
        Repository: string
        /// repository commit from the nuspec ("" when none)
        Commit: string
        /// the package folder in the cache ("" when the cache has no such package)
        Directory: string
    }

    /// What one target framework's restore graph looked like.
    type Assets = {
        /// every package in the restore graph for the target, id/version -- `type: project`
        /// entries are excluded here, they carry no cache package to describe
        Packages: (string * string) list
        /// dependency edges: (id, version) -> (dependency id, resolved version). Includes
        /// `type: project` entries as edge sources (their own id/version, so a project
        /// reference's dependencies are still visible), just not in `Packages`
        Graph: ((string * string) * (string * string)) list
        /// the project's direct PackageReferences (ids), from `project.frameworks.<tfm>.dependencies`
        Direct: string list
        Framework: string
    }

    let private ic = System.StringComparison.OrdinalIgnoreCase

    /// Splits a `targets` entry key `"<Id>/<Version>"` on the first '/' -- a package id never
    /// contains one.
    let private splitIdVersion (key: string) =
        match key.IndexOf '/' with
        | -1 -> key, ""
        | i -> key.Substring (0, i), key.Substring (i + 1)

    /// The full framework name NuGet writes as a `targets` key when a project has a single
    /// `TargetFramework` (a multi-target project's keys are the aliases): `netstandard2.0` ->
    /// `.NETStandard,Version=v2.0`, `net472` -> `.NETFramework,Version=v4.7.2`, `netcoreapp3.1`
    /// -> `.NETCoreApp,Version=v3.1`; `net5.0` and later are their own full name. Anything else
    /// is returned unchanged.
    let internal frameworkFullName (alias: string) =
        let a = alias.ToLowerInvariant ()
        let dotted (digits: string) =
            if digits.Contains "." then digits else digits |> Seq.map string |> String.concat "."
        let version (prefix: string) = a.Substring prefix.Length |> fun v -> (match v.IndexOf '-' with | -1 -> v | i -> v.Substring (0, i))
        if a.StartsWith "netstandard" then sprintf ".NETStandard,Version=v%s" (version "netstandard")
        elif a.StartsWith "netcoreapp" then sprintf ".NETCoreApp,Version=v%s" (version "netcoreapp")
        elif System.Text.RegularExpressions.Regex.IsMatch (a, @"^net[1-4]\d*$") then sprintf ".NETFramework,Version=v%s" (dotted (a.Substring 3))
        else alias

    /// Picks the `targets` entry for `framework`: the key is the framework alone (no rid) or
    /// the framework followed by "/<rid>" (a self-contained publish, not the compile). The key
    /// is the alias for a multi-target project and the full framework name
    /// (`frameworkFullName`) for a single-target one -- both are tried, exact first; then a key
    /// merely starting with either, in case a restore ever wrote something else there.
    let private selectTarget (framework: string) (targets: (string * Json.Value) list) =
        let candidates = [ framework; frameworkFullName framework ] |> List.distinct
        candidates |> List.tryPick (fun name -> targets |> List.tryFind (fun (key, _) -> System.String.Equals (key, name, ic)))
        |> Option.orElseWith (fun () ->
            candidates |> List.tryPick (fun name -> targets |> List.tryFind (fun (key, _) -> key.StartsWith (name, ic))))

    /// Reads `project.assets.json` and builds the restore graph for one target framework.
    let readAssets (assetsFile: string) (framework: string) : Assets =
        let root = File.ReadAllText assetsFile |> Json.parse

        let targets =
            Json.field "targets" root
            |> function
                | Some (Json.JObject members) -> members
                | _ -> []

        let entries =
            match selectTarget framework targets with
            | Some (_, Json.JObject members) -> members
            | _ -> []

        // id -> version, for resolving a dependency's version range to what was actually
        // restored: "just match by id" (a project's own dependencies list only carries ranges)
        let versionOf =
            entries
            |> List.map (fst >> splitIdVersion)
            |> List.map (fun (id, version) -> id, version)

        let resolveVersion depId =
            versionOf
            |> List.tryPick (fun (id, version) -> if System.String.Equals (id, depId, ic) then Some version else None)
            |> Option.defaultValue ""

        let isProject entry = Json.field "type" entry |> Option.bind Json.asString = Some "project"

        let dependenciesOf entry =
            Json.field "dependencies" entry
            |> function
                | Some (Json.JObject members) -> members |> List.map fst
                | _ -> []

        let packages =
            entries
            |> List.choose (fun (key, entry) -> if isProject entry then None else Some (splitIdVersion key))

        let graph =
            entries
            |> List.collect (fun (key, entry) ->
                let self = splitIdVersion key
                dependenciesOf entry |> List.map (fun depId -> self, (depId, resolveVersion depId)))

        let direct =
            Json.field "project" root
            |> Option.bind (Json.field "frameworks")
            |> function
                | Some (Json.JObject frameworks) ->
                    frameworks
                    |> List.tryFind (fun (alias, _) -> System.String.Equals (alias, framework, ic))
                    |> Option.map snd
                | _ -> None
            |> Option.bind (Json.field "dependencies")
            |> function
                | Some (Json.JObject members) -> members |> List.map fst
                | _ -> []

        { Packages = packages; Graph = graph; Direct = direct; Framework = framework }

    /// Finds a child element by local name (nuspec namespaces vary by schema version --
    /// matching on the qualified name would miss packages using an older/newer one).
    let private child (name: string) (el: XmlElement) =
        el.ChildNodes
        |> Seq.cast<XmlNode>
        |> Seq.tryPick (function :? XmlElement as e when e.LocalName = name -> Some e | _ -> None)

    let private attr (name: string) (el: XmlElement) =
        match el.GetAttribute name with
        | null | "" -> None
        | v -> Some v

    let private text (el: XmlElement option) = el |> Option.map (fun e -> e.InnerText.Trim ()) |> Option.defaultValue ""

    /// Reads one package's identity, license and provenance from the cache. Never throws: a
    /// missing package directory yields `Directory = ""` and every other field empty -- the
    /// caller decides whether that is fatal. A missing `.nupkg.metadata` or nuspec inside an
    /// existing package directory likewise yields empty fields for just what is missing.
    let readCache (cacheRoot: string) (id: string) (version: string) : Package =
        let empty = { Id = id; Version = version; Sha512 = ""; Source = ""; License = ""; Supplier = ""; Repository = ""; Commit = ""; Directory = "" }

        let dir = cacheRoot </> id.ToLowerInvariant () </> version.ToLowerInvariant ()
        if not (Directory.Exists dir) then empty else

        let metadataFile = dir </> ".nupkg.metadata"
        let sha512, source =
            if not (File.Exists metadataFile) then "", "" else
            let root = File.ReadAllText metadataFile |> Json.parse
            (Json.field "contentHash" root |> Option.bind Json.asString |> Option.defaultValue ""),
            (Json.field "source" root |> Option.bind Json.asString |> Option.defaultValue "")

        let nuspecFile = dir </> (id.ToLowerInvariant () + ".nuspec")
        let supplier, license, repository, commit =
            if not (File.Exists nuspecFile) then "", "", "", "" else
            let doc = XmlDocument ()
            doc.Load nuspecFile
            match doc.DocumentElement |> child "metadata" with
            | None -> "", "", "", ""
            | Some metadata ->
                let supplier = text (metadata |> child "authors")
                let license =
                    match metadata |> child "license" with
                    | Some licenseEl -> text (Some licenseEl)
                    | None -> text (metadata |> child "licenseUrl")
                let repoEl = metadata |> child "repository"
                let repository = repoEl |> Option.bind (attr "url") |> Option.defaultValue ""
                let commit = repoEl |> Option.bind (attr "commit") |> Option.defaultValue ""
                supplier, license, repository, commit

        { Id = id; Version = version; Sha512 = sha512; Source = source
          License = license; Supplier = supplier; Repository = repository; Commit = commit
          Directory = dir }

    /// Whether a package's cache entry has anything that ships -- a `lib/` or `runtimes/`
    /// directory with at least one file underneath. Used for a package the restore graph
    /// carries (`Assets.Packages`) but that no compiled reference was ever attributed to
    /// (`packageOf` found nothing for it): a runtime-only package (native assets under
    /// `runtimes/`, or a `lib/` the compiler never referenced) still ships and belongs in the
    /// SBOM as `required`; a pure reference-assembly-only or metadata-only package (nothing
    /// under `lib/` or `runtimes/`, or missing from the cache entirely) does not.
    let ships (cacheRoot: string) (id: string) (version: string) : bool =
        let dir = cacheRoot </> id.ToLowerInvariant () </> version.ToLowerInvariant ()
        if not (Directory.Exists dir) then false else
        [ "lib"; "runtimes" ]
        |> List.exists (fun sub ->
            let subDir = dir </> sub
            Directory.Exists subDir
            && Directory.EnumerateFiles (subDir, "*", SearchOption.AllDirectories) |> Seq.isEmpty |> not)

    /// The (id, version) a file under the package cache belongs to, from the first two path
    /// segments below `cacheRoot`; `None` when `path` is not under `cacheRoot` at all.
    let packageOf (cacheRoot: string) (path: string) : (string * string) option =
        let comparison = if Env.isUnix then System.StringComparison.Ordinal else System.StringComparison.OrdinalIgnoreCase
        let root = cacheRoot.Replace('\\', '/').TrimEnd '/'
        let path = path.Replace('\\', '/')
        if not (path.StartsWith (root + "/", comparison)) then None else
        let rel = path.Substring (root.Length + 1)
        match rel.Split '/' with
        | segments when segments.Length >= 2 && segments.[0] <> "" && segments.[1] <> "" -> Some (segments.[0], segments.[1])
        | _ -> None
