namespace Xake.Dotnet

open System
open System.IO
open System.Security.Cryptography
open System.Text

open Xake

/// Turns what the engine already knows about a compile -- the restore graph (`Nuget`) and the
/// exact files the compiler was handed (`Lock.Project`) -- into a CycloneDX bill of materials.
/// Pure: no file writes, no network, nothing but the data it is given plus reading the assembly
/// file itself to hash it. `Sbom` is the "what did we ship and what is it made of" half of
/// brief.md's hermetic-build pitch (docs/features/hermetic-build/brief.md §8e, §11): NuGet
/// packages are the primary components (`purl`), assembly-level evidence nests under them,
/// compile-time-only inputs (analyzers, the compiler, the SDK) go in `formulation` with
/// `scope: excluded` so a scanner does not mistake them for shipped code, and the document is
/// deterministic -- no wall-clock timestamp, a `serialNumber` derived from its own content, so
/// two builds of the same tag produce byte-identical SBOMs.
module Sbom =

    /// One integrity hash, CycloneDX's own shape (`hashes[{alg, content}]`).
    type Hash = { Alg: string; Content: string }

    /// One CycloneDX `components[]` entry. Assembly-level evidence nests under the package it
    /// came from (`Components`); compile-time-only components (analyzers, the compiler, the
    /// SDK) carry `Scope = "excluded"` and live in `Bom.Formulation` instead of `Bom.Components`.
    type Component = {
        /// "library" | "file" | "application"
        Type: string
        BomRef: string
        Name: string
        Version: string
        /// "" omits `supplier`
        Supplier: string
        /// "" omits `purl`
        Purl: string
        Hashes: Hash list
        /// SPDX expression or id; "" omits `licenses`
        License: string
        /// "" | "required" | "excluded"; "" omits `scope`
        Scope: string
        /// nested evidence, e.g. the files under a package
        Components: Component list
    }

    /// One bill of materials: `Root` is `metadata.component` (the shipped assembly or package),
    /// `Components` are the top-level `components[]` (packages and non-package files),
    /// `Dependencies` is the restore graph restricted to what is in the BOM, and `Formulation`
    /// is the toolchain (analyzers, compiler, SDK) that produced `Root` but did not ship in it.
    type Bom = {
        Root: Component
        Components: Component list
        /// bom-ref -> dependsOn bom-refs
        Dependencies: (string * string list) list
        Formulation: Component list
    }

    let private jstring (s: string) = Json.escape s

    let private indent (s: string) =
        if s = "" then s else
        s.Split '\n' |> Array.map (fun l -> "  " + l) |> String.concat "\n"

    /// A JSON object from already-rendered `(key, value)` pairs, `key` a plain (unescaped) name.
    let private jobj (fields: (string * string) list) =
        match fields with
        | [] -> "{}"
        | _ ->
            fields
            |> List.map (fun (k, v) -> sprintf "%s: %s" (jstring k) v)
            |> String.concat ",\n" |> indent
            |> sprintf "{\n%s\n}"

    /// A JSON array from already-rendered element strings.
    let private jarr (items: string list) =
        match items with
        | [] -> "[]"
        | _ -> items |> String.concat ",\n" |> indent |> sprintf "[\n%s\n]"

    let private hashJson (h: Hash) = jobj [ "alg", jstring h.Alg; "content", jstring h.Content ]

    /// A CycloneDX license entry: `{"license":{"id": "..."}}` for a bare SPDX id/short name
    /// (letters, digits, '.', '-' only -- "MIT", "Apache-2.0", "BSD-3-Clause"), otherwise
    /// `{"expression": "..."}` for anything with boolean operators or free text.
    let private licenseJson (license: string) =
        if System.Text.RegularExpressions.Regex.IsMatch (license, @"^[A-Za-z0-9.\-]+$") then
            jobj [ "license", jobj [ "id", jstring license ] ]
        else
            jobj [ "expression", jstring license ]

    /// Renders one component, nested components sorted by name for determinism.
    let rec private componentJson (c: Component) =
        [ "type", jstring c.Type
          "bom-ref", jstring c.BomRef
          "name", jstring c.Name
          if c.Version <> "" then "version", jstring c.Version
          if c.Supplier <> "" then "supplier", jobj [ "name", jstring c.Supplier ]
          if not c.Hashes.IsEmpty then "hashes", jarr (c.Hashes |> List.map hashJson)
          if c.License <> "" then "licenses", jarr [ licenseJson c.License ]
          if c.Purl <> "" then "purl", jstring c.Purl
          if c.Scope <> "" then "scope", jstring c.Scope
          if not c.Components.IsEmpty then
            "components", jarr (c.Components |> List.sortBy (fun x -> x.Name) |> List.map componentJson) ]
        |> jobj

    let private dependencyJson (bomRef: string, dependsOn: string list) =
        let sorted = dependsOn |> List.distinct |> List.sort
        [ "ref", jstring bomRef
          if not sorted.IsEmpty then "dependsOn", jarr (sorted |> List.map jstring) ]
        |> jobj

    let private toolsComponentJson (version: string) =
        jobj [ "type", jstring "application"; "name", jstring "Xake"; "version", jstring version ]

    /// CycloneDX 1.6 JSON for `bom`, deterministic: keys in a fixed order, arrays sorted where
    /// the schema carries no order of its own, no `metadata.timestamp` (a later option can set
    /// one from `SOURCE_DATE_EPOCH` -- not today, so two builds of the same tag are byte-
    /// identical), and `serialNumber` computed from the document's own content -- rendered once
    /// with a placeholder, hashed (SHA-256), the first 16 bytes reformatted as a version-5-
    /// looking UUID (version nibble and variant bits set, the rest is the hash) -- so the same
    /// content always yields the same serial and changing one hash changes it.
    let cycloneDx (bom: Bom) : string =
        let toolVersion =
            match System.Reflection.Assembly.GetExecutingAssembly().GetName().Version with
            | null -> ""
            | v -> v.ToString ()

        let componentsSorted = bom.Components |> List.sortBy (fun c -> c.BomRef)
        let dependenciesSorted = bom.Dependencies |> List.sortBy fst

        let formulationJson =
            match bom.Formulation with
            | [] -> "[]"
            | comps ->
                jarr [
                    jobj [
                        "bom-ref", jstring ("formula:compile:" + bom.Root.Name)
                        "components", jarr (comps |> List.sortBy (fun c -> c.BomRef) |> List.map componentJson)
                    ]
                ]

        let render (serial: string) =
            jobj [
                "bomFormat", jstring "CycloneDX"
                "specVersion", jstring "1.6"
                "serialNumber", jstring serial
                "version", jstring "1"
                "metadata", jobj [
                    "tools", jobj [ "components", jarr [ toolsComponentJson toolVersion ] ]
                    "component", componentJson bom.Root
                ]
                "components", jarr (componentsSorted |> List.map componentJson)
                "dependencies", jarr (dependenciesSorted |> List.map dependencyJson)
                "formulation", formulationJson
            ] + "\n"

        let placeholder = "urn:uuid:00000000-0000-0000-0000-000000000000"
        let draft = render placeholder

        let digest = SHA256.Create().ComputeHash (Encoding.UTF8.GetBytes draft)
        let bytes = Array.sub digest 0 16
        bytes.[6] <- (bytes.[6] &&& 0x0Fuy) ||| 0x50uy
        bytes.[8] <- (bytes.[8] &&& 0x3Fuy) ||| 0x80uy
        let hex = bytes |> Array.map (sprintf "%02x") |> String.concat ""
        let uuid =
            sprintf "%s-%s-%s-%s-%s"
                (hex.Substring (0, 8)) (hex.Substring (8, 4)) (hex.Substring (12, 4))
                (hex.Substring (16, 4)) (hex.Substring (20, 12))

        render ("urn:uuid:" + uuid)

    let private base64ToHex (b64: string) =
        if b64 = "" then "" else
        try Convert.FromBase64String b64 |> Array.map (sprintf "%02x") |> String.concat "" with _ -> ""

    /// A reference under a package's `ref/` folder is a compile-time facade (a reference
    /// assembly / API stub), not shipped code -- `lib/` is what actually runs. Matched as a
    /// whole path segment, case-insensitively, so `ref` only ever means the folder, never a
    /// substring of something else.
    let private underRefFolder (path: string) =
        (path.Replace ('\\', '/')).Split '/'
        |> Array.exists (fun seg -> String.Equals (seg, "ref", StringComparison.OrdinalIgnoreCase))

    let private fileComponent scope (h: Lock.Hashed) : Component =
        { Type = "file"; BomRef = "file:" + h.Path; Name = Path.GetFileName h.Path; Version = ""
          Supplier = ""; Purl = ""
          Hashes = if h.Sha256 = "" then [] else [ { Alg = "SHA-256"; Content = h.Sha256 } ]
          License = ""; Scope = scope; Components = [] }

    let private keyOf (id: string) (version: string) = id.ToLowerInvariant (), version.ToLowerInvariant ()

    /// The bill of materials for one compiled assembly: `Root` is the assembly itself.
    ///
    /// **Every package in the restore graph (`assets.Packages`) is a component** -- not just
    /// the ones a compiled reference happens to be attributed to -- because a scanner wants the
    /// graph, not just what got linked (brief.md §8e). `id`/`version` keep `project.assets.json`'s
    /// own casing for the `bom-ref`/`purl`; `Nuget.readCache`/`Nuget.packageOf` are matched
    /// case-insensitively against it (the cache's directory names are always lowercase). Scope:
    /// - a package with at least one referenced file (`Nuget.packageOf` attributes it): `excluded`
    ///   when every referenced file is under its `ref/` folder (a compile-time facade),
    ///   `required` when any is under `lib/` (or anywhere else that is not `ref/`) -- nested
    ///   `file` components carry those referenced dlls with their sha256 from the lock.
    /// - a package with **no** referenced file: `required`, with no nested files, when it is
    ///   reachable from a direct dependency (`assets.Direct`) by following `assets.Graph` edges
    ///   and `Nuget.ships` finds `lib/` or `runtimes/` content in the cache for it (a
    ///   runtime-only package that ships without ever being `/reference`d); `excluded` otherwise
    ///   (unreachable, or nothing in the cache that ships -- e.g. a pure reference-assembly pack
    ///   like `Microsoft.NETFramework.ReferenceAssemblies.*`).
    ///
    /// References outside the cache entirely (a project reference, an SDK reference pack under
    /// `$(DotnetRoot)`) become top-level `file` components, `scope: "required"`, hash omitted
    /// when the lock has none (a project reference not yet built). `Dependencies` has the root
    /// depending on every *direct*, *required* package, plus every package-to-package edge from
    /// `assets.Graph` whose two ends are both packages in this BOM. `Formulation` records what
    /// compiled it but did not ship: the analyzers, `csc` and the SDK, all `scope: "excluded"`.
    let forAssembly (cacheRoot: string) (assets: Nuget.Assets) (lock: Lock.Project) (assemblyPath: string) : Bom =
        let refsWithPackage = lock.References |> List.map (fun r -> r, Nuget.packageOf cacheRoot r.Path)
        let nonPackageRefs = refsWithPackage |> List.choose (fun (r, pkg) -> if pkg.IsNone then Some r else None)

        // referenced files, grouped by the (id, version) key `packageOf` found for them --
        // case-insensitive, so it lines up with `assets.Packages`'s own-cased entries below.
        let refsByKey =
            refsWithPackage
            |> List.choose (fun (r, pkg) -> pkg |> Option.map (fun (id, version) -> keyOf id version, r))
            |> List.groupBy fst
            |> List.map (fun (k, items) -> k, items |> List.map snd)
            |> Map.ofList

        // reachability from a direct dependency, following the restore graph -- for the
        // "no referenced file" packages, where evidence alone cannot tell required from excluded.
        let graphEdgesByKey =
            assets.Graph |> List.map (fun ((fromId, fromVer), (toId, toVer)) -> keyOf fromId fromVer, keyOf toId toVer)
        let directSet = assets.Direct |> List.map (fun s -> s.ToLowerInvariant ()) |> Set.ofList
        let directKeys =
            assets.Packages
            |> List.filter (fun (id, _) -> directSet.Contains (id.ToLowerInvariant ()))
            |> List.map (fun (id, version) -> keyOf id version)
            |> Set.ofList
        let reachable =
            let rec bfs (frontier: Set<string * string>) (visited: Set<string * string>) =
                if Set.isEmpty frontier then visited else
                let next =
                    graphEdgesByKey
                    |> List.choose (fun (f, t) -> if frontier.Contains f && not (visited.Contains t) then Some t else None)
                    |> Set.ofList
                bfs next (Set.union visited next)
            bfs directKeys directKeys

        let packageComponents =
            assets.Packages
            |> List.map (fun (id, version) ->
                let key = keyOf id version
                let refs = refsByKey |> Map.tryFind key |> Option.defaultValue []
                let pkg = Nuget.readCache cacheRoot id version
                let purl = sprintf "pkg:nuget/%s@%s" id version
                let scope =
                    if not refs.IsEmpty then
                        if refs |> List.forall (fun r -> underRefFolder r.Path) then "excluded" else "required"
                    elif reachable.Contains key && Nuget.ships cacheRoot id version then "required"
                    else "excluded"
                let hashes =
                    match base64ToHex pkg.Sha512 with
                    | "" -> []
                    | hex -> [ { Alg = "SHA-512"; Content = hex } ]
                let files =
                    refs |> List.filter (fun r -> r.Sha256 <> "")
                    |> List.map (fileComponent "") |> List.sortBy (fun f -> f.Name)
                { Type = "library"; BomRef = purl; Name = id; Version = version
                  Supplier = pkg.Supplier; Purl = purl; Hashes = hashes; License = pkg.License
                  Scope = scope; Components = files })

        let nonPackageComponents = nonPackageRefs |> List.map (fileComponent "required")

        let rootHash =
            match Lock.sha256 assemblyPath with
            | "" -> []
            | h -> [ { Alg = "SHA-256"; Content = h } ]
        let version =
            [ "Version"; "InformationalVersion" ]
            |> List.tryPick (fun k -> lock.Properties |> Map.tryFind k |> Option.filter ((<>) ""))
            |> Option.defaultValue ""
        let rootBomRef = "asm:" + lock.Name
        let root =
            { Type = "library"; BomRef = rootBomRef; Name = lock.Name; Version = version
              Supplier = ""; Purl = ""; Hashes = rootHash; License = ""; Scope = ""; Components = [] }

        // (id, version), case-insensitive -> the package's own bom-ref (== its purl, with
        // `assets.Packages`'s own casing), so the restore graph's edges line up regardless of
        // what case each side happens to spell the id in.
        let refMap =
            assets.Packages
            |> List.map (fun (id, version) -> keyOf id version, sprintf "pkg:nuget/%s@%s" id version)
            |> Map.ofList
        let scopeOf =
            packageComponents |> List.map (fun c -> c.BomRef, c.Scope) |> Map.ofList

        let rootDeps =
            assets.Packages
            |> List.choose (fun (id, version) ->
                let bomRef = refMap.[keyOf id version]
                if scopeOf.[bomRef] = "required" && directSet.Contains (id.ToLowerInvariant ()) then Some bomRef else None)

        let graphEdges =
            assets.Graph
            |> List.choose (fun ((fromId, fromVer), (toId, toVer)) ->
                match Map.tryFind (keyOf fromId fromVer) refMap, Map.tryFind (keyOf toId toVer) refMap with
                | Some f, Some t -> Some (f, t)
                | _ -> None)

        let dependencies =
            (rootBomRef, rootDeps)
            :: (graphEdges |> List.groupBy fst |> List.map (fun (f, edges) -> f, edges |> List.map snd))

        let analyzerComponents =
            lock.Analyzers |> List.map (fileComponent "excluded")
        let compilerComponent =
            { Type = "application"; BomRef = "tool:csc"; Name = "csc"; Version = lock.Compiler.Sdk
              Supplier = ""; Purl = ""
              Hashes = (if lock.Compiler.Sha256 = "" then [] else [ { Alg = "SHA-256"; Content = lock.Compiler.Sha256 } ])
              License = ""; Scope = "excluded"; Components = [] }
        let sdkComponent =
            { Type = "application"; BomRef = "tool:dotnet-sdk"; Name = ".NET SDK"; Version = lock.Compiler.Sdk
              Supplier = ""; Purl = ""; Hashes = []; License = ""; Scope = "excluded"; Components = [] }

        { Root = root
          Components = packageComponents @ nonPackageComponents
          Dependencies = dependencies
          Formulation = analyzerComponents @ [ compilerComponent; sdkComponent ] }

    /// Reads a nupkg's own `id`/`version` off the `.nuspec` entry inside it (the file at the
    /// zip root ending in `.nuspec` -- nupkgs carry exactly one). `System.IO.Compression.ZipFile`
    /// opens the nupkg as an ordinary zip; nuspec parsing mirrors `Nuget.readCache`'s
    /// (namespace-agnostic, local-name matching). Checked: `ZipArchive`/`ZipFile` compile and
    /// resolve on net462 too, from the SDK's own framework reference assemblies -- no
    /// `<Reference Include="System.IO.Compression" />` needed in the fsproj -- so this is one
    /// implementation for both target frameworks, no `#if NETFRAMEWORK`.
    let private readNuspecFromNupkg (nupkgPath: string) : string * string =
        use archive = System.IO.Compression.ZipFile.OpenRead nupkgPath
        let entry =
            archive.Entries
            |> Seq.find (fun e ->
                e.FullName.EndsWith (".nuspec", StringComparison.OrdinalIgnoreCase)
                && not (e.FullName.Contains "/") && not (e.FullName.Contains "\\"))
        let xml =
            use stream = entry.Open ()
            use reader = new StreamReader (stream)
            reader.ReadToEnd ()
        let doc = System.Xml.XmlDocument ()
        doc.LoadXml xml
        let child (name: string) (el: System.Xml.XmlElement) =
            el.ChildNodes |> Seq.cast<System.Xml.XmlNode>
            |> Seq.tryPick (function :? System.Xml.XmlElement as e when e.LocalName = name -> Some e | _ -> None)
        let text (el: System.Xml.XmlElement option) = el |> Option.map (fun e -> e.InnerText.Trim ()) |> Option.defaultValue ""
        match doc.DocumentElement |> child "metadata" with
        | None -> "", ""
        | Some metadata -> text (metadata |> child "id"), text (metadata |> child "version")

    let private hashBytes (algo: unit -> HashAlgorithm) (bytes: byte[]) =
        use a = algo ()
        a.ComputeHash bytes |> Array.map (sprintf "%02x") |> String.concat ""

    /// Merges two components that share a `bom-ref`: nested `Components` (file evidence) union
    /// by their own `bom-ref` (recursively); `Scope` widens to `required` if either side is;
    /// the first non-empty value wins for the fields that should not vary between sightings of
    /// the same package (`Hashes`, `License`, `Supplier`).
    let rec private mergeComponent (a: Component) (b: Component) : Component =
        let mergedChildren =
            (a.Components @ b.Components)
            |> List.groupBy (fun c -> c.BomRef)
            |> List.map (fun (_, cs) -> cs |> List.reduce mergeComponent)
            |> List.sortBy (fun c -> c.BomRef)
        { a with
            Components = mergedChildren
            Hashes = if a.Hashes.IsEmpty then b.Hashes else a.Hashes
            License = if a.License <> "" then a.License else b.License
            Supplier = if a.Supplier <> "" then a.Supplier else b.Supplier
            Scope = if a.Scope = "required" || b.Scope = "required" then "required" else a.Scope }

    /// Unions a list of components by `bom-ref`, merging duplicates with `mergeComponent`.
    let private mergeComponents (comps: Component list) : Component list =
        comps |> List.groupBy (fun c -> c.BomRef) |> List.map (fun (_, cs) -> cs |> List.reduce mergeComponent)

    /// Unions a `Dependencies` list by `ref`, merging `dependsOn` sets.
    let private mergeDependencies (deps: (string * string list) list) : (string * string list) list =
        deps
        |> List.groupBy fst
        |> List.map (fun (r, ds) -> r, ds |> List.collect snd |> List.distinct |> List.sort)

    /// The bill of materials for one shipped nupkg: `Root` is the package itself (identity from
    /// the nuspec inside the nupkg, hashes of the nupkg file, SHA-256 and SHA-512), with the
    /// given assemblies' own `Root`s nested under it as `library` components (their hashes, no
    /// further nesting -- their own evidence is already surfaced at the top level below).
    /// `Components` is the union, by `bom-ref`, of every assembly BOM's `Components` -- so a
    /// package referenced by two of the nupkg's assemblies appears once, its nested file
    /// evidence merged. `Dependencies` has the nupkg root depending on each assembly's root,
    /// plus the union of the assemblies' own dependency edges. `Formulation` is the union, by
    /// `bom-ref`, of the assemblies' formulations. Deterministic: every list sorted by its ref.
    let forPackage (nupkgPath: string) (assemblies: Bom list) : Bom =
        let id, version = readNuspecFromNupkg nupkgPath
        let bytes = File.ReadAllBytes nupkgPath
        let bomRef = "nupkg:" + Path.GetFileNameWithoutExtension nupkgPath

        let assemblyRootComponents =
            assemblies
            |> List.map (fun b -> { b.Root with Components = [] })
            |> List.sortBy (fun c -> c.BomRef)

        let root =
            { Type = "library"; BomRef = bomRef; Name = id; Version = version
              Supplier = ""; Purl = ""
              Hashes =
                [ { Alg = "SHA-256"; Content = hashBytes (fun () -> SHA256.Create () :> HashAlgorithm) bytes }
                  { Alg = "SHA-512"; Content = hashBytes (fun () -> SHA512.Create () :> HashAlgorithm) bytes } ]
              License = ""; Scope = ""; Components = assemblyRootComponents }

        let components =
            assemblies |> List.collect (fun b -> b.Components) |> mergeComponents |> List.sortBy (fun c -> c.BomRef)

        let rootDeps = bomRef, assemblies |> List.map (fun b -> b.Root.BomRef) |> List.sort
        let dependencies =
            rootDeps :: (assemblies |> List.collect (fun b -> b.Dependencies))
            |> mergeDependencies |> List.sortBy fst

        let formulation =
            assemblies |> List.collect (fun b -> b.Formulation) |> mergeComponents |> List.sortBy (fun c -> c.BomRef)

        { Root = root; Components = components; Dependencies = dependencies; Formulation = formulation }
