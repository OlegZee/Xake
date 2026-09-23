namespace Xake.Dotnet

open System
open System.IO
open System.Security.Cryptography
open System.Text

open Xake

/// Turns what the lock already knows about a compile -- the restore graph (`Lock.Package`) and
/// the exact files the compiler was handed (`Lock.Entry`) -- into a CycloneDX bill of materials.
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

    /// One CycloneDX `properties[]` entry: a namespaced name and a string value, e.g.
    /// `dt:nuget:declaredVersionRange` = `[1.2.3, )`.
    type Property = { Name: string; Value: string }

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
        /// `properties[]`; empty omits it
        Properties: Property list
    }

    /// One `compositions[]` entry: what the document claims to be complete (or not) about.
    /// `Aggregate` is `complete` | `incomplete` | `unknown` (CycloneDX's own vocabulary);
    /// `Assemblies` are the bom-refs whose *composition* (what they are made of) has that
    /// completeness, `Dependencies` the bom-refs whose *dependency graph* does.
    type Composition = { Aggregate: string; Assemblies: string list; Dependencies: string list }

    /// One `annotations[]` entry: free text attached to `Subjects` (bom-refs), stamped by
    /// `Annotator` (the name of the tool component) at `Timestamp` -- the schema requires one;
    /// see `forPackageScoped` for how it stays deterministic.
    type Annotation = { BomRef: string; Subjects: string list; Annotator: string; Timestamp: string; Text: string }

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
        /// `compositions[]`; empty omits it (the restore-scope BOMs declare none)
        Compositions: Composition list
        /// `annotations[]`; empty omits it
        Annotations: Annotation list
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
            "components", jarr (c.Components |> List.sortBy (fun x -> x.Name) |> List.map componentJson)
          if not c.Properties.IsEmpty then
            "properties", jarr (c.Properties |> List.map (fun p -> jobj [ "name", jstring p.Name; "value", jstring p.Value ])) ]
        |> jobj

    let private dependencyJson (bomRef: string, dependsOn: string list) =
        let sorted = dependsOn |> List.distinct |> List.sort
        [ "ref", jstring bomRef
          if not sorted.IsEmpty then "dependsOn", jarr (sorted |> List.map jstring) ]
        |> jobj

    let private toolsComponentJson (version: string) =
        jobj [ "type", jstring "application"; "name", jstring "Xake"; "version", jstring version ]

    let private compositionJson (c: Composition) =
        [ "aggregate", jstring c.Aggregate
          if not c.Assemblies.IsEmpty then "assemblies", jarr (c.Assemblies |> List.distinct |> List.sort |> List.map jstring)
          if not c.Dependencies.IsEmpty then "dependencies", jarr (c.Dependencies |> List.distinct |> List.sort |> List.map jstring) ]
        |> jobj

    let private annotationJson (toolVersion: string) (a: Annotation) =
        jobj [
            "bom-ref", jstring a.BomRef
            "subjects", jarr (a.Subjects |> List.distinct |> List.sort |> List.map jstring)
            "annotator", jobj [ "component", jobj [ "type", jstring "application"; "name", jstring a.Annotator; "version", jstring toolVersion ] ]
            "timestamp", jstring a.Timestamp
            "text", jstring a.Text
        ]

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
                if not bom.Compositions.IsEmpty then
                    "compositions", jarr (bom.Compositions |> List.sortBy (fun c -> c.Aggregate) |> List.map compositionJson)
                if not bom.Annotations.IsEmpty then
                    "annotations", jarr (bom.Annotations |> List.sortBy (fun a -> a.BomRef) |> List.map (annotationJson toolVersion))
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
          License = ""; Scope = scope; Components = []; Properties = [] }

    let private keyOf (id: string) (version: string) = id.ToLowerInvariant (), version.ToLowerInvariant ()

    /// The bill of materials for one compiled assembly: `Root` is the assembly itself.
    ///
    /// **Every package in the lock's restore graph (`entry.Dependencies.Packages`) is a
    /// component** -- not just the ones a compiled reference happens to be attributed to --
    /// because a scanner wants the graph, not just what got linked (brief.md §8e). `Id`/`Version`
    /// keep `project.assets.json`'s own casing for the `bom-ref`/`purl`; `Nuget.readCache`/
    /// `Nuget.packageOf` are matched case-insensitively against it (the cache's directory names
    /// are always lowercase). Scope:
    /// - a package with at least one referenced file (`Nuget.packageOf` attributes it): `excluded`
    ///   when every referenced file is under its `ref/` folder (a compile-time facade),
    ///   `required` when any is under `lib/` (or anywhere else that is not `ref/`) -- nested
    ///   `file` components carry those referenced dlls with their sha256 from the lock.
    /// - a package with **no** referenced file: `required`, with no nested files, when it is
    ///   reachable from a direct dependency (`Package.Direct`) by following `DependsOn` edges
    ///   and `Nuget.ships` finds `lib/` or `runtimes/` content in the cache for it (a
    ///   runtime-only package that ships without ever being `/reference`d); `excluded` otherwise
    ///   (unreachable, or nothing in the cache that ships -- e.g. a pure reference-assembly pack
    ///   like `Microsoft.NETFramework.ReferenceAssemblies.*`).
    ///
    /// The package hash is the lock's own `Sha512` (recorded at import from `.nupkg.metadata`);
    /// supplier and license still come from the cache's nuspec (`Nuget.readCache`), the lock
    /// does not carry them. References outside the cache entirely (a project reference, an SDK
    /// reference pack under `$(DotnetRoot)`) become top-level `file` components, `scope:
    /// "required"`, hash omitted when the lock has none (a project reference not yet built).
    /// `Dependencies` has the root depending on every *direct*, *required* package, plus every
    /// package-to-package edge from `DependsOn` whose two ends are both packages in this BOM
    /// (a dependency's version is resolved by id within the same graph). `Formulation` records
    /// what compiled it but did not ship: the analyzers, `csc` (its own `Compiler.Version`) and
    /// the SDK (`Evaluation.Sdk`), all `scope: "excluded"`.
    let forAssembly (cacheRoot: string) (entry: Lock.Entry) (assemblyPath: string) : Bom =
        let packages = entry.Dependencies.Packages
        let references = entry.Dependencies.References |> List.map (fun r -> { Lock.Path = r.Path; Lock.Sha256 = r.Sha256 })
        let refsWithPackage = references |> List.map (fun r -> r, Nuget.packageOf cacheRoot r.Path)
        let nonPackageRefs = refsWithPackage |> List.choose (fun (r, pkg) -> if pkg.IsNone then Some r else None)

        // referenced files, grouped by the (id, version) key `packageOf` found for them --
        // case-insensitive, so it lines up with the lock's own-cased package entries below.
        let refsByKey =
            refsWithPackage
            |> List.choose (fun (r, pkg) -> pkg |> Option.map (fun (id, version) -> keyOf id version, r))
            |> List.groupBy fst
            |> List.map (fun (k, items) -> k, items |> List.map snd)
            |> Map.ofList

        // a dependency names an id only; its version is whatever the same graph resolved
        let versionOf (id: string) =
            packages |> List.tryPick (fun p -> if String.Equals (p.Id, id, StringComparison.OrdinalIgnoreCase) then Some p.Version else None)
        let graphEdgesByKey =
            packages |> List.collect (fun p ->
                p.DependsOn |> List.choose (fun depId ->
                    versionOf depId |> Option.map (fun v -> keyOf p.Id p.Version, keyOf depId v)))
        let directKeys =
            packages |> List.filter (fun p -> p.Direct) |> List.map (fun p -> keyOf p.Id p.Version) |> Set.ofList
        // reachability from a direct dependency, following the restore graph -- for the
        // "no referenced file" packages, where evidence alone cannot tell required from excluded.
        let reachable =
            let rec bfs (frontier: Set<string * string>) (visited: Set<string * string>) =
                if Set.isEmpty frontier then visited else
                let next =
                    graphEdgesByKey
                    |> List.choose (fun (f, t) -> if frontier.Contains f && not (visited.Contains t) then Some t else None)
                    |> Set.ofList
                bfs next (Set.union visited next)
            bfs directKeys directKeys

        let purlOf (p: Lock.Package) = sprintf "pkg:nuget/%s@%s" p.Id p.Version

        let packageComponents =
            packages
            |> List.map (fun p ->
                let key = keyOf p.Id p.Version
                let refs = refsByKey |> Map.tryFind key |> Option.defaultValue []
                let cached = Nuget.readCache cacheRoot p.Id p.Version
                let purl = purlOf p
                let scope =
                    if not refs.IsEmpty then
                        if refs |> List.forall (fun r -> underRefFolder r.Path) then "excluded" else "required"
                    elif reachable.Contains key && Nuget.ships cacheRoot p.Id p.Version then "required"
                    else "excluded"
                let hashes =
                    match base64ToHex p.Sha512 with
                    | "" -> []
                    | hex -> [ { Alg = "SHA-512"; Content = hex } ]
                let files =
                    refs |> List.filter (fun r -> r.Sha256 <> "")
                    |> List.map (fileComponent "") |> List.sortBy (fun f -> f.Name)
                { Type = "library"; BomRef = purl; Name = p.Id; Version = p.Version
                  Supplier = cached.Supplier; Purl = purl; Hashes = hashes; License = cached.License
                  Scope = scope; Components = files; Properties = [] })

        let nonPackageComponents = nonPackageRefs |> List.map (fileComponent "required")

        let rootHash =
            match Lock.sha256 assemblyPath with
            | "" -> []
            | h -> [ { Alg = "SHA-256"; Content = h } ]
        let version =
            [ "Version"; "InformationalVersion" ]
            |> List.tryPick (fun k -> entry.Evaluation.Properties |> Map.tryFind k |> Option.filter ((<>) ""))
            |> Option.defaultValue ""
        let rootBomRef = "asm:" + entry.Name
        let root =
            { Type = "library"; BomRef = rootBomRef; Name = entry.Name; Version = version
              Supplier = ""; Purl = ""; Hashes = rootHash; License = ""; Scope = ""; Components = []; Properties = [] }

        // (id, version), case-insensitive -> the package's own bom-ref (== its purl, with the
        // lock's own casing), so the graph's edges line up regardless of what case each side
        // happens to spell the id in.
        let refMap = packages |> List.map (fun p -> keyOf p.Id p.Version, purlOf p) |> Map.ofList
        let scopeOf = packageComponents |> List.map (fun c -> c.BomRef, c.Scope) |> Map.ofList

        let rootDeps =
            packages
            |> List.choose (fun p ->
                let bomRef = refMap.[keyOf p.Id p.Version]
                if scopeOf.[bomRef] = "required" && p.Direct then Some bomRef else None)

        let graphEdges =
            graphEdgesByKey
            |> List.choose (fun (f, t) ->
                match Map.tryFind f refMap, Map.tryFind t refMap with
                | Some f, Some t -> Some (f, t)
                | _ -> None)

        let dependencies =
            (rootBomRef, rootDeps)
            :: (graphEdges |> List.groupBy fst |> List.map (fun (f, edges) -> f, edges |> List.map snd))

        let compiler = entry.Dependencies.Compiler
        let analyzerComponents =
            entry.Dependencies.Analyzers |> List.map (fileComponent "excluded")
        let compilerComponent =
            { Type = "application"; BomRef = "tool:csc"; Name = "csc"; Version = compiler.Version
              Supplier = ""; Purl = ""
              Hashes = (if compiler.Sha256 = "" then [] else [ { Alg = "SHA-256"; Content = compiler.Sha256 } ])
              License = ""; Scope = "excluded"; Components = []; Properties = [] }
        let sdkComponent =
            { Type = "application"; BomRef = "tool:dotnet-sdk"; Name = ".NET SDK"; Version = entry.Evaluation.Sdk
              Supplier = ""; Purl = ""; Hashes = []; License = ""; Scope = "excluded"; Components = []; Properties = [] }

        { Root = root
          Components = packageComponents @ nonPackageComponents
          Dependencies = dependencies
          Formulation = analyzerComponents @ [ compilerComponent; sdkComponent ]
          Compositions = []; Annotations = [] }

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
              License = ""; Scope = ""; Components = assemblyRootComponents; Properties = [] }

        let components =
            assemblies |> List.collect (fun b -> b.Components) |> mergeComponents |> List.sortBy (fun c -> c.BomRef)

        let rootDeps = bomRef, assemblies |> List.map (fun b -> b.Root.BomRef) |> List.sort
        let dependencies =
            rootDeps :: (assemblies |> List.collect (fun b -> b.Dependencies))
            |> mergeDependencies |> List.sortBy fst

        let formulation =
            assemblies |> List.collect (fun b -> b.Formulation) |> mergeComponents |> List.sortBy (fun c -> c.BomRef)

        { Root = root; Components = components; Dependencies = dependencies; Formulation = formulation
          Compositions = []; Annotations = [] }

    // ---- package scope: the SBOM of what the customer receives (nuget-sbom.md "Package scope") ----

    /// Where the package-scope SBOM for `framework` lives inside a nupkg:
    /// `sbom/<tfm>/bom.cdx.json`. The `sbom/` tree is plumbing to the inventory below, so a
    /// nupkg re-packed with its own SBOMs added does not list them as shipped files.
    let packageSbomPath (framework: string) : string = sprintf "sbom/%s/bom.cdx.json" framework

    /// Every entry of a nupkg (zip) with its bytes, path with forward slashes, in archive order.
    /// Directory entries (a trailing `/`) are skipped.
    let internal nupkgEntries (nupkgPath: string) : (string * byte[]) list =
        use archive = System.IO.Compression.ZipFile.OpenRead nupkgPath
        [ for e in archive.Entries do
            let path = e.FullName.Replace ('\\', '/')
            if not (path.EndsWith "/") then
                use stream = e.Open ()
                use ms = new MemoryStream ()
                stream.CopyTo ms
                yield path, ms.ToArray () ]

    /// The nuspec inside a nupkg (the single `.nuspec` entry at the zip root), parsed.
    let internal nuspecOfEntries (entries: (string * byte[]) list) : Nuget.Nuspec =
        entries
        |> List.tryFind (fun (path, _) -> path.EndsWith (".nuspec", StringComparison.OrdinalIgnoreCase) && not (path.Contains "/"))
        |> function
            | Some (_, bytes) -> Nuget.parseNuspec (Encoding.UTF8.GetString bytes)
            | None -> Nuget.parseNuspec "<package><metadata /></package>"

    let private segments (path: string) = path.Split '/' |> List.ofArray
    let private eqi (a: string) (b: string) = String.Equals (a, b, StringComparison.OrdinalIgnoreCase)
    let private hasExtension (exts: string list) (path: string) =
        exts |> List.exists (fun ext -> path.EndsWith (ext, StringComparison.OrdinalIgnoreCase))

    /// The building blocks `defaultPackageScope` is assembled from -- each a pure predicate a
    /// script can reuse, wrap or replace in its own `PackageScopeOptions`.
    module PackageScope =

        /// A prefix matcher over package ids, case-insensitive: `idPrefixes [ "DS."; "Acme." ]`.
        let idPrefixes (prefixes: string list) : string -> bool =
            fun id -> prefixes |> List.exists (fun prefix -> id.StartsWith (prefix, StringComparison.OrdinalIgnoreCase))

        /// OPC and package metadata a nupkg carries that is not shipped code and never a
        /// component: `_rels/`, `[Content_Types].xml`, `package/` (the psmdcp core properties),
        /// the root nuspec, the signature, the `sbom/` tree itself, root-level icon/readme/
        /// license files, and `.xml` IntelliSense docs anywhere.
        let plumbing (path: string) : bool =
            match segments path with
            | [ single ] ->
                eqi single "[Content_Types].xml" || eqi single ".signature.p7s"
                || hasExtension [ ".nuspec"; ".png"; ".jpg"; ".ico"; ".md"; ".txt" ] single
                || single.StartsWith ("LICENSE", StringComparison.OrdinalIgnoreCase)
            | first :: _ ->
                eqi first "_rels" || eqi first "package" || eqi first "sbom"
                || hasExtension [ ".xml" ] path
            | [] -> true

        /// Whether a nupkg path is content shipped *for `framework`*: `lib/<tfm>/**` (satellite
        /// resource folders included), `runtimes/<rid>/lib/<tfm>/**`, `runtimes/<rid>/native/**`,
        /// `contentFiles/<lang>/<tfm>|any/**`, `analyzers/**`, `build/**`, `buildTransitive/**`,
        /// `buildMultiTargeting/**` (each either TFM-less or under `<tfm>/`), and `tools/**`.
        /// Folder names compared case-insensitively. `ref/` (compile-time facades) and any other
        /// TFM's folders are not shipped for this TFM.
        let shippedFor (framework: string) (path: string) : bool =
            let tfm (seg: string) = eqi seg framework
            let tfmOrNone = function
                | seg :: _ :: _ -> tfm seg
                | [ _ ] -> true
                | [] -> false
            match segments path |> List.map (fun seg -> seg.ToLowerInvariant ()) with
            | [ "lib"; t; _ ] | [ "lib"; t; _; _ ] -> tfm t
            | "runtimes" :: _ :: "lib" :: t :: _ :: _ -> tfm t
            | "runtimes" :: _ :: "native" :: _ :: _ -> true
            | "contentfiles" :: _ :: t :: _ :: _ -> tfm t || t = "any"
            | "analyzers" :: _ :: _ -> true
            | "tools" :: _ :: _ -> true
            | ("build" | "buildtransitive" | "buildmultitargeting") :: rest -> tfmOrNone rest
            | _ -> false

        /// `.dll` / `.exe` -- a `library` component, and one check 3.2 wants hashed.
        let assembly (path: string) : bool = hasExtension [ ".dll"; ".exe" ] path

        /// Anything under `runtimes/<rid>/native/`, or a `.so`/`.dylib` anywhere.
        let native (path: string) : bool =
            match segments path with
            | "runtimes" :: _ :: "native" :: _ -> true
            | _ -> hasExtension [ ".so"; ".dylib" ] path

        /// Supplier-internal package ids: `DS.*`, `MESCIUS.*`, `GrapeCity.*`.
        let internalIds : string -> bool = idPrefixes [ "DS."; "MESCIUS."; "GrapeCity." ]

        /// Build tooling that is never shipped code, even when a nuspec lists it:
        /// `CycloneDX.*`, `Microsoft.SourceLink.*`, `Microsoft.NETFramework.ReferenceAssemblies*`.
        let toolingIds : string -> bool =
            idPrefixes [ "CycloneDX."; "Microsoft.SourceLink."; "Microsoft.NETFramework.ReferenceAssemblies" ]

        /// The text of the boundary annotation every package-scope SBOM carries on its root.
        let boundaryText =
            "Dependencies are the package's own nuspec-declared dependencies for this target framework (depth 1, ranges as declared). "
            + "Transitive dependencies are not enumerated: their resolution is the consuming product's responsibility."

        /// The one wall-clock value the schema forces into the document
        /// (`annotations[].timestamp`), chosen the way `Pack` chooses zip entry times so it never
        /// varies between two builds of the same commit: `SOURCE_DATE_EPOCH` when set, else the
        /// DOS epoch `1980-01-01T00:00:00Z`.
        let timestamp () : string =
            let epoch = DateTime (1980, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            let stamp =
                match Environment.GetEnvironmentVariable "SOURCE_DATE_EPOCH" with
                | null | "" -> epoch
                | v ->
                    match Int64.TryParse v with
                    | true, seconds -> max epoch (DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime)
                    | false, _ -> epoch
            stamp.ToString ("yyyy-MM-dd'T'HH:mm:ss'Z'", Globalization.CultureInfo.InvariantCulture)

    /// The knobs of the package-scope rule (`forPackageScopedWith`, `Verify.sbomPackageScopeWith`):
    /// what counts as shipped content and as plumbing, how a declared dependency is classified,
    /// what the document says about itself. Pure functions and values, no global state; start
    /// from `defaultPackageScope` and override with `{ defaultPackageScope with ... }`. The
    /// verifier takes the same record, so a customised producer is checked by the same rule.
    type PackageScopeOptions = {
        /// nupkg path -> never a component (OPC parts, docs, the `sbom/` tree). Checked first.
        IsPlumbing: string -> bool
        /// framework -> nupkg path -> shipped for that framework (tier 1)
        IsShipped: string -> string -> bool
        /// shipped path -> a managed assembly: `library` type, and check 3.2 wants its hash
        IsAssembly: string -> bool
        /// shipped path -> a native binary: check 3.2 wants its hash
        IsNative: string -> bool
        /// package id -> internal: one line in tier 2 (id, range, resolved version, purl), no
        /// supplier/license/hash pulled from the restore evidence
        IsInternal: string -> bool
        /// package id -> build tooling: dropped from tier 2 even when the nuspec declares it
        IsTooling: string -> bool
        /// shipped path -> the registry-declared subcomponents to nest under its component
        /// (vendored / merged libraries, RFC §6). Default: none -- the registry does not exist
        /// yet; a script with one plugs it in here
        Subcomponents: string -> Component list
        /// the property that carries a tier-2 entry's range as the nuspec wrote it
        DeclaredRangeProperty: string
        /// extra `properties[]` on the root, after `xake:nuget:targetFramework`
        RootProperties: Property list
        /// the boundary annotation; "" emits no annotation (check 3.4 then fails, deliberately)
        BoundaryText: string
        /// `annotations[].timestamp`, ISO 8601 -- see `PackageScope.timestamp`
        AnnotationTimestamp: string
        /// carry the evidence BOMs' `formulation` (compiler, SDK, analyzers) into the package
        /// document. Off: the RFC lists what the customer receives, and they are not it
        KeepFormulation: bool
    }

    /// The RFC's rules as read on 2026-09-23 (nuget-sbom.md "Package scope"), assembled from
    /// `PackageScope`'s predicates.
    let defaultPackageScope : PackageScopeOptions =
        { IsPlumbing = PackageScope.plumbing
          IsShipped = PackageScope.shippedFor
          IsAssembly = PackageScope.assembly
          IsNative = PackageScope.native
          IsInternal = PackageScope.internalIds
          IsTooling = PackageScope.toolingIds
          Subcomponents = fun _ -> []
          DeclaredRangeProperty = "dt:nuget:declaredVersionRange"
          RootProperties = []
          BoundaryText = PackageScope.boundaryText
          AnnotationTimestamp = PackageScope.timestamp ()
          KeepFormulation = false }

    /// The nupkg paths that are shipped content for `framework` under `options` -- tier 1's
    /// inventory, shared with `Verify.sbomPackageScopeWith` so producer and checker agree.
    let shippedPaths (options: PackageScopeOptions) (framework: string) (entries: (string * byte[]) list) : string list =
        entries |> List.map fst |> List.filter (fun path -> not (options.IsPlumbing path) && options.IsShipped framework path)

    /// The property a tier-1 component names its nupkg path in; the join key between the
    /// document and the archive for the verifier. Not configurable.
    let pathProperty = "xake:nuget:path"

    let private bothHashes (bytes: byte[]) =
        [ { Alg = "SHA-256"; Content = hashBytes (fun () -> SHA256.Create () :> HashAlgorithm) bytes }
          { Alg = "SHA-512"; Content = hashBytes (fun () -> SHA512.Create () :> HashAlgorithm) bytes } ]

    /// The package-scope SBOM of one nupkg for one target framework -- the document the SDP
    /// RFC (nuget-sbom.md "Package scope") asks for: **what the customer receives**, not the
    /// supplier's restore graph. `assemblies` are the restore-scope BOMs (`forAssembly`) of the
    /// assemblies packed for that TFM; they are the *evidence* the rule consults, never emitted
    /// as they are. `options` decides the classifications; `defaultPackageScope` is the RFC's.
    ///
    /// - **Root** (`metadata.component`): the package, identity from the nuspec inside the
    ///   nupkg, `bom-ref = "nupkg:<file name>"`, the TFM in property `xake:nuget:targetFramework`
    ///   then `options.RootProperties`. No hash of the nupkg itself: this document is meant to
    ///   be packed *into* it (`packageSbomPath`), so the nupkg's bytes are not final here.
    /// - **Tier 1**, nested under the root's `components[]`: one component per file
    ///   `shippedPaths` lists, with SHA-256 and SHA-512 of the shipped bytes; `IsAssembly` paths
    ///   are `library`, everything else `file`; an assembly whose SHA-256 equals an input BOM's
    ///   root hash takes that root's `name`/`version` (it is one of our own). `Subcomponents`
    ///   nest under the file they belong to.
    /// - **Tier 2**, the top-level `components[]`: the nuspec `<dependency>` entries of the TFM's
    ///   group minus `IsTooling`, id verbatim, the range as written in `DeclaredRangeProperty`,
    ///   `version` and `purl` from the restore graph's resolution when the assemblies' BOMs
    ///   carry that package (so a scanner can still match), `""`/`pkg:nuget/<id>` otherwise.
    ///   Supplier, license and package hash come from that evidence too -- except for an
    ///   `IsInternal` package, which stays one line. Nothing from the restore graph that the
    ///   nuspec does not declare is emitted: no SDK packs, no analyzers, no transitive package
    ///   at any depth.
    /// - **`dependencies[]`**: the root depends on its own shipped assemblies and on every tier-2
    ///   component; each own assembly (matched to an input BOM by hash) depends on the tier-2
    ///   components its restore-scope BOM had it depend on directly. No entry ever has a
    ///   tier-2 component as its `ref` -- an empty `dependsOn` there would claim a closure this
    ///   document does not know.
    /// - **`compositions`**: `complete` over `assemblies: [root]`, `incomplete` over
    ///   `dependencies: [root; own assemblies]`.
    /// - **`annotations`**: one on the root with `BoundaryText`, annotator Xake, stamped
    ///   `AnnotationTimestamp`.
    /// - **`formulation`**: the evidence BOMs' union when `KeepFormulation`, else none.
    ///
    /// Signing the document (JSF) is out of scope here; a signer wraps the `cycloneDx` output.
    /// Deterministic: every list sorted by its ref, no wall clock other than the annotation's
    /// fixed stamp.
    let forPackageScopedWith (options: PackageScopeOptions) (nupkgPath: string) (framework: string) (assemblies: Bom list) : Bom =
        let entries = nupkgEntries nupkgPath
        let nuspec = nuspecOfEntries entries
        let packageRef = "nupkg:" + Path.GetFileNameWithoutExtension nupkgPath

        // tier 1
        let rootsByHash =
            assemblies
            |> List.choose (fun b -> b.Root.Hashes |> List.tryPick (fun h -> if h.Alg = "SHA-256" then Some (h.Content, b) else None))
            |> Map.ofList
        let shippedSet = shippedPaths options framework entries |> Set.ofList
        let shipped =
            entries
            |> List.filter (fun (path, _) -> shippedSet.Contains path)
            |> List.map (fun (path, bytes) ->
                let hashes = bothHashes bytes
                let sha256 = (hashes |> List.find (fun h -> h.Alg = "SHA-256")).Content
                let own = rootsByHash |> Map.tryFind sha256
                let name, version =
                    match own with
                    | Some b -> b.Root.Name, b.Root.Version
                    | None -> Path.GetFileName path, ""
                { Type = (if options.IsAssembly path then "library" else "file"); BomRef = packageRef + "/" + path
                  Name = name; Version = version; Supplier = ""; Purl = ""; Hashes = hashes; License = ""; Scope = ""
                  Components = options.Subcomponents path |> List.sortBy (fun c -> c.BomRef)
                  Properties = [ { Name = pathProperty; Value = path } ] }, own)
        let shippedComponents = shipped |> List.map fst |> List.sortBy (fun c -> c.BomRef)

        // tier 2
        let evidence = assemblies |> List.collect (fun b -> b.Components)
        let evidenceFor (id: string) =
            evidence |> List.tryFind (fun c -> c.Type = "library" && c.Purl <> "" && eqi c.Name id)
        let tier2 =
            Nuget.nuspecDependenciesFor framework nuspec
            |> List.filter (fun d -> d.Id <> "" && not (options.IsTooling d.Id))
            |> List.map (fun d ->
                let resolved = evidenceFor d.Id
                let enrichment = if options.IsInternal d.Id then None else resolved
                let version = resolved |> Option.map (fun c -> c.Version) |> Option.defaultValue ""
                let purl = if version = "" then "pkg:nuget/" + d.Id else sprintf "pkg:nuget/%s@%s" d.Id version
                let field (f: Component -> string) = enrichment |> Option.map f |> Option.defaultValue ""
                { Type = "library"; BomRef = purl; Name = d.Id; Version = version
                  Supplier = field (fun c -> c.Supplier); Purl = purl
                  Hashes = enrichment |> Option.map (fun c -> c.Hashes) |> Option.defaultValue []
                  License = field (fun c -> c.License)
                  Scope = "required"; Components = []
                  Properties = [ { Name = options.DeclaredRangeProperty; Value = d.Range } ] })
            |> List.sortBy (fun c -> c.BomRef)
        let tier2Refs = tier2 |> List.map (fun c -> c.BomRef)
        let tier2ById = tier2 |> List.map (fun c -> c.Name.ToLowerInvariant (), c.BomRef) |> Map.ofList

        // dependencies: root -> own assemblies + tier 2; own assembly -> the tier-2 packages its
        // restore-scope BOM had it depend on directly (matched by package id, not by version)
        let ownAssemblies = shipped |> List.choose (fun (c, own) -> own |> Option.map (fun b -> c, b))
        let ownRefs = ownAssemblies |> List.map (fun (c, _) -> c.BomRef)
        let assemblyDeps =
            ownAssemblies
            |> List.map (fun (c, b) ->
                let direct = b.Dependencies |> List.tryFind (fun (r, _) -> r = b.Root.BomRef) |> Option.map snd |> Option.defaultValue []
                let idOf (bomRef: string) = b.Components |> List.tryPick (fun p -> if p.BomRef = bomRef then Some (p.Name.ToLowerInvariant ()) else None)
                c.BomRef, direct |> List.choose (fun r -> idOf r |> Option.bind (fun id -> Map.tryFind id tier2ById)) |> List.distinct |> List.sort)
        let rootDeps = packageRef, (ownRefs @ tier2Refs) |> List.distinct |> List.sort
        let dependencies = (rootDeps :: assemblyDeps) |> List.sortBy fst

        let root =
            { Type = "library"; BomRef = packageRef; Name = nuspec.Id; Version = nuspec.Version
              Supplier = nuspec.Authors; Purl = (if nuspec.Id = "" then "" else sprintf "pkg:nuget/%s@%s" nuspec.Id nuspec.Version)
              Hashes = []; License = nuspec.License; Scope = ""
              Components = shippedComponents
              Properties = { Name = "xake:nuget:targetFramework"; Value = framework } :: options.RootProperties }

        let compositions =
            [ { Aggregate = "complete"; Assemblies = [ packageRef ]; Dependencies = [] }
              { Aggregate = "incomplete"; Assemblies = []; Dependencies = packageRef :: ownRefs |> List.sort } ]
        let annotations =
            if options.BoundaryText = "" then [] else
            [ { BomRef = packageRef + "/annotation:boundary"; Subjects = [ packageRef ]; Annotator = "Xake"
                Timestamp = options.AnnotationTimestamp; Text = options.BoundaryText } ]
        let formulation =
            if options.KeepFormulation then
                assemblies |> List.collect (fun b -> b.Formulation) |> mergeComponents |> List.sortBy (fun c -> c.BomRef)
            else []

        { Root = root; Components = tier2; Dependencies = dependencies; Formulation = formulation
          Compositions = compositions; Annotations = annotations }

    /// `forPackageScopedWith defaultPackageScope`: the RFC's rules as they stand.
    let forPackageScoped (nupkgPath: string) (framework: string) (assemblies: Bom list) : Bom =
        forPackageScopedWith defaultPackageScope nupkgPath framework assemblies
