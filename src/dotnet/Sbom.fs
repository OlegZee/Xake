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

    let private jstring (s: string) = Fsproj.Json.escape s

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

    /// The bill of materials for one compiled assembly: `Root` is the assembly itself; every
    /// reference under the NuGet cache (`Nuget.packageOf`) becomes one `library` component per
    /// package -- `purl`, identity and license from `Nuget.readCache`, the sha512 content hash
    /// (hex, from the cache's base64) when known, nested `file` components for the referenced
    /// dlls with their sha256 from the lock -- `scope: "excluded"` when every referenced file is
    /// under that package's `ref/` folder (a compile-time facade), `"required"` when any is under
    /// `lib/`. References outside the cache (a project reference, an SDK reference pack under
    /// `$(DotnetRoot)`) become top-level `file` components, `scope: "required"`, hash omitted
    /// when the lock has none (a project reference not yet built). `Dependencies` has the root
    /// depending on every *direct*, *required* package (`assets.Direct`), plus every
    /// package-to-package edge from `assets.Graph` whose two ends are both packages in this BOM.
    /// `Formulation` records what compiled it but did not ship: the analyzers, `csc` and the SDK,
    /// all `scope: "excluded"`.
    let forAssembly (cacheRoot: string) (assets: Nuget.Assets) (lock: Lock.Project) (assemblyPath: string) : Bom =
        let refsWithPackage = lock.References |> List.map (fun r -> r, Nuget.packageOf cacheRoot r.Path)

        let packageGroups =
            refsWithPackage
            |> List.choose (fun (r, pkg) -> pkg |> Option.map (fun p -> p, r))
            |> List.groupBy fst
            |> List.map (fun (idVer, items) -> idVer, items |> List.map snd)

        let nonPackageRefs = refsWithPackage |> List.choose (fun (r, pkg) -> if pkg.IsNone then Some r else None)

        let packageComponents =
            packageGroups
            |> List.map (fun ((id, version), refs) ->
                let pkg = Nuget.readCache cacheRoot id version
                let purl = sprintf "pkg:nuget/%s@%s" id version
                let scope = if refs |> List.forall (fun r -> underRefFolder r.Path) then "excluded" else "required"
                let hashes =
                    match base64ToHex pkg.Sha512 with
                    | "" -> []
                    | hex -> [ { Alg = "SHA-512"; Content = hex } ]
                let files =
                    refs |> List.filter (fun r -> r.Sha256 <> "")
                    |> List.map (fileComponent "") |> List.sortBy (fun f -> f.Name)
                { Type = "library"; BomRef = purl; Name = pkg.Id; Version = pkg.Version
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

        // (id, version), case-insensitive -> the package's own bom-ref (== its purl), so the
        // restore graph's ids (whatever case `project.assets.json` used) line up with the
        // packages actually found in the cache.
        let refMap =
            packageGroups
            |> List.map (fun ((id, version), _) -> keyOf id version, sprintf "pkg:nuget/%s@%s" id version)
            |> Map.ofList
        let scopeOf =
            packageComponents |> List.map (fun c -> c.BomRef, c.Scope) |> Map.ofList
        let directSet = assets.Direct |> List.map (fun s -> s.ToLowerInvariant ()) |> Set.ofList

        let rootDeps =
            packageGroups
            |> List.choose (fun ((id, version), _) ->
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
