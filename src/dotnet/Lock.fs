namespace Xake.Dotnet

open System.IO

open Xake
open Xake.Tasks

/// The imported compilation of a project: exactly what `dotnet build` would have handed the
/// compiler, plus the identity (path and SHA-256) of everything the compiler would read that
/// is not in the repository -- packages, analyzers, the compiler itself, the msbuild files
/// that produced the answer. One lock file (`Document`) per (framework, variant) holds one
/// `Entry` per project, and an entry is three records: `Evaluation` (where the answer came
/// from -- the project file, its imports, the SDK; empty-valued for a compilation composed
/// from `csc {}` settings), `Compilation` (what is compiled: the structured command line, the
/// generated inputs, the resx pairs; changes with every PR) and `Dependencies` (what it is
/// compiled with and against, each hashed: the compiler, the references, the analyzers, the
/// package graph; changes rarely and is reviewed when it does).
module Lock =

    /// A file the build reads that is not source: where it is and what it was.
    type Hashed = {
        Path: string
        /// Lowercase hex SHA-256, or empty when the file did not exist at import time (a
        /// project reference points at the referenced project's own output, not built yet)
        Sha256: string
    }

    /// One `/reference:` item: a hashed assembly with the `alias=` prefix it carried, if any
    /// (`extern alias`; empty for the ordinary case).
    type Reference = {
        Path: string
        Sha256: string
        Alias: string
    }

    type Compiler = {
        /// "csc" or "fsc"
        Tool: string
        /// The compiler assembly, `csc.dll` under the SDK's Roslyn directory unless the project
        /// pins a compiler package
        Path: string
        Sha256: string
        /// The compiler's own version, from its file version resource (`ProductVersion` cut at
        /// the first `+` or space, e.g. `4.11.0-3.25569.22`); empty when the file is missing
        Version: string
    }

    /// One package of the restore graph, as `project.assets.json` and the package cache saw it
    /// at import time.
    type Package = {
        Id: string
        Version: string
        /// base64 sha512 from the cache's `.nupkg.metadata` (`contentHash`); "" when the cache
        /// lacked it at import
        Sha512: string
        /// a direct `PackageReference` of the project (as opposed to transitive)
        Direct: bool
        /// package ids this one depends on, resolved within the same graph
        DependsOn: string list
    }

    /// How a project's SDK is selected, from `global.json` searched upwards from the project's
    /// directory the way the .NET host itself resolves it (`sdk.version` plus
    /// `sdk.rollForward`, default `latestPatch` when a version is set and the policy is
    /// absent). Only `Pinned` makes the SDK a fixed input: with anything else the exact SDK
    /// `dotnet` picks depends on what is installed on the machine, and the compiler recorded
    /// in the lock drifts with it.
    type SdkPin =
        | NoGlobalJson
        | Pinned of version: string
        | RollsForward of version: string * policy: string
        | NoVersion of file: string

    /// The pin as written in the lock and quoted in trace messages.
    let sdkPinText = function
        | NoGlobalJson -> "none"
        | Pinned version -> "exact " + version
        | RollsForward (version, policy) -> sprintf "%s rollForward:%s" version policy
        | NoVersion file -> sprintf "no version (%s)" file

    /// The inverse of `sdkPinText`; `None` for an empty string (a composed compilation has no
    /// project and no pin).
    let parseSdkPin (text: string) : SdkPin option =
        match text with
        | "" -> None
        | "none" -> Some NoGlobalJson
        | t when t.StartsWith "exact " -> Some (Pinned (t.Substring 6))
        | t when t.StartsWith "no version (" && t.EndsWith ")" -> Some (NoVersion (t.Substring (12, t.Length - 13)))
        | t ->
            match t.IndexOf " rollForward:" with
            | -1 -> failwithf "'%s' is not a recognized SdkPin" t
            | i -> Some (RollsForward (t.Substring (0, i), t.Substring (i + 13)))

    /// Where the entry came from: the msbuild evaluation. `run` never reads it; the import
    /// rule `needFiles` the `Imports`, and the SBOM reads `Sdk` and `Properties`. For a
    /// compilation composed from `csc {}` settings every field is empty.
    type Evaluation = {
        /// The project file ("" when composed)
        Project: string
        /// ProjectReference items, as project files
        ProjectRefs: string list
        /// The msbuild files whose evaluation produced this, outside the SDK
        Imports: Hashed list
        /// The SDK that evaluated the project (`NETCoreSdkVersion`; "" when composed)
        Sdk: string
        /// The project's `global.json` pin (`None` when composed)
        SdkPin: SdkPin option
        /// A small whitelist of msbuild properties (`AssemblyName`, `TargetFrameworkMoniker`,
        /// `Version`, ...)
        Properties: Map<string, string>
    }

    /// What is compiled. `Options`, `Defines`, `Sources` and the references and analyzers of
    /// `Dependencies` together are the exact command line msbuild would have run
    /// (`Entry.Args` rebuilds it): `Options` holds every argument that is not one of those
    /// four sections, in msbuild's original order, with a marker string -- `"@Sources"`,
    /// `"@References"`, `"@Analyzers"`, `"@Defines"` -- at the position each section occupied.
    /// A leading `@` in `Options` is always such a marker and never a csc response-file
    /// reference: msbuild never emits one on the compiler's command line.
    type Compilation = {
        /// The project's directory: the compiler's working directory in `dotnet build`, and
        /// what relative arguments were resolved against
        Directory: string
        /// Every switch that is not a reference, analyzer or define, paths absolute, with the
        /// four section markers in place
        Options: string list
        /// Conditional compilation symbols (`/define:A;B` split)
        Defines: string list
        /// Source files, absolute
        Sources: string list
        /// Inputs msbuild generated during the import (assembly attributes, the editorconfig
        /// it derives from properties), by path, with their content: they depend on the
        /// commit and the properties, and are small
        Generated: (string * string) list
        /// `.resx` files this project embeds, compiled by `PrepareResources`: (resx path,
        /// `.resources` output path), both absolute. `run` regenerates the output from the
        /// resx (via `Xake.Dotnet.Resx`) whenever it is missing, so a machine with only the
        /// lock -- or a cleaned `obj/` -- can still reproduce the exact input the recorded
        /// `/resource:` switch names.
        Resources: (string * string) list
    }

    /// What the compilation is made with and against, each hashed.
    type Dependencies = {
        Compiler: Compiler
        /// Every assembly referenced, in command-line order
        References: Reference list
        Analyzers: Hashed list
        /// The restore graph (`project.assets.json`) at import time; empty when composed
        Packages: Package list
    }

    let private sourcesMarker = "@Sources"
    let private referencesMarker = "@References"
    let private analyzersMarker = "@Analyzers"
    let private definesMarker = "@Defines"

    /// Whether an `Options` element is one of the four section markers.
    let isMarker (option: string) =
        option = sourcesMarker || option = referencesMarker || option = analyzersMarker || option = definesMarker

    let private formatReference (r: Reference) =
        "/reference:" + (if r.Alias = "" then "" else r.Alias + "=") + CscArgs.quoteIfNeeded r.Path

    /// The structured command line, and the way back to the flat one.
    module Compilation =

        /// Factors a command line into the structured form: a `/reference:`/`/r:` switch
        /// carrying exactly one item goes into the references (its `alias=` prefix split off),
        /// a `/analyzer:`/`/a:` switch with one item into the analyzers, every `/define:`/`/d:`
        /// into `Defines` (all of them concatenated, one marker at the first), every source
        /// into `Sources` (one marker at the first). Everything else stays in `Options`, in
        /// order. A contiguous block collapses to one marker; should two non-contiguous
        /// blocks of one kind ever appear, the marker stays at the first and the round-trip
        /// check at import (`Entry.Args` against the original) decides. `Directory`,
        /// `Generated` and `Resources` of the returned compilation are empty: the caller
        /// knows them. Hashes of the returned references and analyzers are empty too.
        let ofArgs (args: string list) : Compilation * Reference list * Hashed list =
            let options = ResizeArray<string>()
            let defines = ResizeArray<string>()
            let sources = ResizeArray<string>()
            let references = ResizeArray<Reference>()
            let analyzers = ResizeArray<Hashed>()
            let marker (m: string) = if not (options.Contains m) then options.Add m
            let singleItem (value: string) =
                match CscArgs.splitList value |> List.filter ((<>) "") with
                | [ item ] -> Some item
                | _ -> None
            for arg in args do
                match CscArgs.parse arg with
                | CscArgs.Source path ->
                    marker sourcesMarker
                    sources.Add path
                | CscArgs.Switch (name, value) ->
                    match CscArgs.canonical name, singleItem value with
                    | "reference", Some item ->
                        marker referencesMarker
                        let alias, path =
                            match CscArgs.aliasSplitIndex item with
                            | -1 -> "", item
                            | i -> item.Substring (0, i), item.Substring (i + 1)
                        references.Add { Path = path; Sha256 = ""; Alias = alias }
                    | "analyzer", Some item ->
                        marker analyzersMarker
                        analyzers.Add { Path = item; Sha256 = "" }
                    | "define", _ ->
                        marker definesMarker
                        for d in value.Split ';' do
                            if d <> "" then defines.Add d
                    | _ -> options.Add arg
            { Directory = ""
              Options = List.ofSeq options
              Defines = List.ofSeq defines
              Sources = List.ofSeq sources
              Generated = []
              Resources = [] },
            List.ofSeq references,
            List.ofSeq analyzers

        /// The flat command line back from the structure: each marker in `Options` expands to
        /// its section -- one `/reference:<alias=>path` per reference (a path with a comma
        /// re-quoted, as msbuild quotes it), one `/analyzer:path` per analyzer, one
        /// `/define:A;B`, the sources.
        let args (compilation: Compilation) (references: Reference list) (analyzers: Hashed list) : string list =
            compilation.Options |> List.collect (fun option ->
                if option = sourcesMarker then compilation.Sources
                elif option = referencesMarker then references |> List.map formatReference
                elif option = analyzersMarker then analyzers |> List.map (fun a -> "/analyzer:" + CscArgs.quoteIfNeeded a.Path)
                elif option = definesMarker then
                    if List.isEmpty compilation.Defines then [] else [ "/define:" + String.concat ";" compilation.Defines ]
                else [ option ])

    /// One project's compilation, as recorded in a lock.
    type Entry = {
        /// AssemblyName
        Name: string
        /// The target framework this compilation is for (`netstandard2.0`, `net472`, ...).
        /// One lock holds every framework of the project set, so `(Name, Framework)` -- not
        /// `Name` alone -- identifies an entry. It sits here rather than in `Evaluation`
        /// because it is identity: it is what a build script looks an entry up by
        /// (`Lock.entryFor`), it is set for a composed `csc {}` compilation too (from
        /// `targetfwk`, which has no msbuild evaluation behind it at all), and `Evaluation`
        /// is by contract empty in that case.
        Framework: string
        Evaluation: Evaluation
        Compilation: Compilation
        Dependencies: Dependencies
    } with
        /// The exact command line, paths absolute (rebuilt from `Compilation` and
        /// `Dependencies`; the import verifies it equals what msbuild reported)
        member this.Args = Compilation.args this.Compilation this.Dependencies.References this.Dependencies.Analyzers
        member this.Sources = this.Compilation.Sources
        member this.Output = CscArgs.switchValues "out" this.Compilation.Options |> List.tryHead

    /// One lock file: every project of one variant, for every target framework it was
    /// imported for. The framework is a property of the `Entry`, not of the file: a
    /// multi-targeted project set is one import, one restore per project and one lock.
    type Document = {
        Configuration: string
        /// The properties the import ran with, e.g. `Brand`
        Properties: (string * string) list
        Entries: Entry list
    }

    let internal sha256 (path: string) =
        if not (System.IO.File.Exists path) then "" else
        use stream = System.IO.File.OpenRead path
        use algo = System.Security.Cryptography.SHA256.Create()
        algo.ComputeHash stream |> Array.map (sprintf "%02x") |> String.concat ""

    let hashed path = { Path = path; Sha256 = sha256 path }

    /// A compiler's own version: `ProductVersion` from its file version resource, cut at the
    /// first `+` (the commit suffix) or space; "" when the file does not exist. A native
    /// apphost launcher (the SDK's `csc` next to `csc.dll`, what `DotNetFwk.locateFramework`
    /// returns on macOS/Linux) carries no version of its own, so the managed assembly of the
    /// same name next to it is read instead -- that is the compiler the launcher runs.
    let compilerVersion (path: string) =
        let productVersion (file: string) =
            if not (System.IO.File.Exists file) then "" else
            match System.Diagnostics.FileVersionInfo.GetVersionInfo(file).ProductVersion with
            | null -> ""
            | v ->
                match v.IndexOfAny [| '+'; ' ' |] with
                | -1 -> v
                | i -> v.Substring (0, i)
        match productVersion path with
        | "" when System.IO.File.Exists path && not (path.EndsWith (".dll", System.StringComparison.OrdinalIgnoreCase)) ->
            productVersion (System.IO.Path.ChangeExtension (path, ".dll"))
        | v -> v

    /// Fills `Sha256` for every hashed entry (`References`, `Analyzers`, `Imports`) and for
    /// `Compiler` (its `Version` too, when empty), from what is on disk right now; leaves `""`
    /// where the file does not exist (`sha256` already does that per entry). For `resolve`'s
    /// composed-mode entries, which leave every hash empty, this is the "record time" step
    /// `lock-from-settings.md` recommendation 5 asks for -- run once by the lock-recording
    /// rule, not on every compile.
    let rehash (entry: Entry) : Entry =
        let rehashOne (h: Hashed) = { h with Sha256 = sha256 h.Path }
        let compiler = entry.Dependencies.Compiler
        { entry with
            Evaluation = { entry.Evaluation with Imports = entry.Evaluation.Imports |> List.map rehashOne }
            Dependencies =
                { entry.Dependencies with
                    References = entry.Dependencies.References |> List.map (fun r -> { r with Sha256 = sha256 r.Path })
                    Analyzers = entry.Dependencies.Analyzers |> List.map rehashOne
                    Compiler =
                        { compiler with
                            Sha256 = sha256 compiler.Path
                            Version = if compiler.Version = "" then compilerVersion compiler.Path else compiler.Version } } }

    /// Rewrites every path the entry's options, sources, references, analyzers, generated
    /// files and resources carry, through `f`. A build script uses this to point a project
    /// reference (the lock has it unhashed, at the referenced project's own
    /// `bin/Release/.../X.dll`) at the path the script itself produces that output at -- the
    /// rest of the lock, including the hashes that identify what was actually compiled
    /// against, stays as imported. A rewritten hashed entry loses its hash: the hash on record
    /// was computed for the old path, and it would otherwise be checked against a different
    /// file. Section markers are left alone.
    let mapPaths (f: string -> string) (entry: Entry) : Entry =
        let rewriteHashed (h: Hashed) =
            let path = f h.Path
            if path = h.Path then h else { Path = path; Sha256 = "" }
        let rewriteReference (r: Reference) =
            let path = f r.Path
            if path = r.Path then r else { r with Path = path; Sha256 = "" }
        let c = entry.Compilation
        { entry with
            Compilation =
                { c with
                    Options = c.Options |> List.map (fun o -> if isMarker o then o else CscArgs.parse o |> CscArgs.mapPaths f |> CscArgs.format)
                    Sources = c.Sources |> List.map f
                    Generated = c.Generated |> List.map (fun (path, content) -> f path, content)
                    Resources = c.Resources |> List.map (fun (resx, resources) -> f resx, f resources) }
            Dependencies =
                { entry.Dependencies with
                    References = entry.Dependencies.References |> List.map rewriteReference
                    Analyzers = entry.Dependencies.Analyzers |> List.map rewriteHashed } }

    /// Applies `f` to every piece of text that may embed a value rather than name a file:
    /// `Generated` content, `Options`, `Defines`, and the evaluation's property values. Used to
    /// tokenize the commit sha out of a lock (`Project.tokenizeRevision`) and to resolve it
    /// back at compile time (`run`).
    let mapText (f: string -> string) (entry: Entry) : Entry =
        let c = entry.Compilation
        { entry with
            Compilation =
                { c with
                    Generated = c.Generated |> List.map (fun (path, content) -> path, f content)
                    Options = c.Options |> List.map f
                    Defines = c.Defines |> List.map f }
            Evaluation = { entry.Evaluation with Properties = entry.Evaluation.Properties |> Map.map (fun _ v -> f v) } }

    /// A plain ordered-list diff (Myers/LCS): `- x` for an element only on the left, `+ x` for
    /// one only on the right. A moved element shows as both -- removed from its old position,
    /// added at its new one -- there being no separate "moved" marker in an ordered diff.
    let diffList (a: string list) (b: string list) : string list =
        let arrA, arrB = List.toArray a, List.toArray b
        let la, lb = arrA.Length, arrB.Length
        let lcs = Array2D.create (la + 1) (lb + 1) 0
        for i in la - 1 .. -1 .. 0 do
            for j in lb - 1 .. -1 .. 0 do
                lcs.[i, j] <-
                    if arrA.[i] = arrB.[j] then lcs.[i + 1, j + 1] + 1
                    else max lcs.[i + 1, j] lcs.[i, j + 1]
        let rec walk i j =
            if i = la && j = lb then []
            elif i = la then sprintf "+ %s" arrB.[j] :: walk i (j + 1)
            elif j = lb then sprintf "- %s" arrA.[i] :: walk (i + 1) j
            elif arrA.[i] = arrB.[j] then walk (i + 1) (j + 1)
            elif lcs.[i + 1, j] >= lcs.[i, j + 1] then sprintf "- %s" arrA.[i] :: walk (i + 1) j
            else sprintf "+ %s" arrB.[j] :: walk i (j + 1)
        walk 0 0

    /// `label`-prefixed added/removed/hash-changed lines for a `Hashed` list, keyed by path,
    /// sorted for determinism. A hash-changed line is only reported when both sides have a
    /// (non-empty) hash to compare -- an empty hash means "not computed", not "zero bytes".
    let private diffHashed (label: string) (a: Hashed list) (b: Hashed list) : string list =
        let ofList items = items |> List.map (fun (h: Hashed) -> h.Path, h.Sha256) |> Map.ofList
        let mapA, mapB = ofList a, ofList b
        let allPaths = (a |> List.map (fun h -> h.Path)) @ (b |> List.map (fun h -> h.Path)) |> List.distinct |> List.sort
        allPaths |> List.choose (fun path ->
            match Map.tryFind path mapA, Map.tryFind path mapB with
            | Some _, None -> Some (sprintf "- %s %s" label path)
            | None, Some _ -> Some (sprintf "+ %s %s" label path)
            | Some shaA, Some shaB when shaA <> shaB && shaA <> "" && shaB <> "" ->
                Some (sprintf "~ %s %s: %s -> %s" label path shaA shaB)
            | _ -> None)

    /// `label`-prefixed added/removed/content-changed lines for a `(path * content)` list
    /// (`Generated`, `Resources`), keyed by the first element, sorted for determinism.
    let private diffPairs (label: string) (a: (string * string) list) (b: (string * string) list) : string list =
        let mapA, mapB = Map.ofList a, Map.ofList b
        let allKeys = (a |> List.map fst) @ (b |> List.map fst) |> List.distinct |> List.sort
        allKeys |> List.choose (fun key ->
            match Map.tryFind key mapA, Map.tryFind key mapB with
            | Some _, None -> Some (sprintf "- %s %s" label key)
            | None, Some _ -> Some (sprintf "+ %s %s" label key)
            | Some va, Some vb when va <> vb -> Some (sprintf "~ %s %s: content changed" label key)
            | _ -> None)

    /// `label`-prefixed added/removed lines for a plain string list compared as a set
    /// (`Defines`, `ProjectRefs`), sorted for determinism.
    let private diffStringSet (label: string) (a: string list) (b: string list) : string list =
        let setA, setB = Set.ofList a, Set.ofList b
        [ for p in Set.difference setA setB |> Set.toList |> List.sort -> sprintf "- %s %s" label p
          for p in Set.difference setB setA |> Set.toList |> List.sort -> sprintf "+ %s %s" label p ]

    /// `Sha256` follows the same rule as `diffHashed`: an empty hash means "not computed"
    /// (`resolve` never hashes), not "zero bytes", so it is only compared when both sides
    /// carry one -- otherwise diffing a recorded lock against freshly resolved settings would
    /// report the missing hash as a difference on every build.
    let private diffCompiler (a: Compiler) (b: Compiler) : string list =
        [ if a.Path <> b.Path then sprintf "~ Compiler.Path: %s -> %s" a.Path b.Path
          if a.Sha256 <> b.Sha256 && a.Sha256 <> "" && b.Sha256 <> "" then sprintf "~ Compiler.Sha256: %s -> %s" a.Sha256 b.Sha256
          if a.Version <> b.Version then sprintf "~ Compiler.Version: %s -> %s" a.Version b.Version ]

    /// Packages by id (case-insensitive): added, removed, version changed, or -- same version
    /// -- sha512 changed. Sorted by id for determinism.
    let private diffPackages (a: Package list) (b: Package list) : string list =
        let key (p: Package) = p.Id.ToLowerInvariant ()
        let mapA = a |> List.map (fun p -> key p, p) |> Map.ofList
        let mapB = b |> List.map (fun p -> key p, p) |> Map.ofList
        let allIds = (a @ b) |> List.map key |> List.distinct |> List.sort
        allIds |> List.choose (fun id ->
            match Map.tryFind id mapA, Map.tryFind id mapB with
            | Some p, None -> Some (sprintf "- Package %s@%s" p.Id p.Version)
            | None, Some p -> Some (sprintf "+ Package %s@%s" p.Id p.Version)
            | Some pa, Some pb when pa.Version <> pb.Version -> Some (sprintf "~ Package %s: %s -> %s" pa.Id pa.Version pb.Version)
            | Some pa, Some pb when pa.Sha512 <> pb.Sha512 && pa.Sha512 <> "" && pb.Sha512 <> "" ->
                Some (sprintf "~ Package %s@%s: sha512 changed" pa.Id pa.Version)
            | _ -> None)

    let private toHashed (r: Reference) : Hashed = { Path = r.Path; Sha256 = r.Sha256 }

    /// Human-readable differences between two lock entries of the same project: `Framework`,
    /// `Options` and
    /// `Sources` as ordered lists (`diffList`, bare `+`/`-` lines), `Defines` as a set,
    /// `Compiler` (path, hash, version), `Evaluation.Sdk`, each hashed list (`References`,
    /// `Analyzers`, `Imports`) by path, `Generated`/`Resources` by key, `ProjectRefs` as a
    /// set, `Packages` by id. Empty list means identical. Pure, deterministic order (fixed
    /// section order, sorted within each section save the two ordered ones).
    let diff (a: Entry) (b: Entry) : string list =
        [ if a.Framework <> b.Framework then yield sprintf "~ Framework: %s -> %s" a.Framework b.Framework
          yield! diffList a.Compilation.Options b.Compilation.Options
          yield! diffList a.Compilation.Sources b.Compilation.Sources
          yield! diffStringSet "Define" a.Compilation.Defines b.Compilation.Defines
          yield! diffCompiler a.Dependencies.Compiler b.Dependencies.Compiler
          if a.Evaluation.Sdk <> b.Evaluation.Sdk then yield sprintf "~ Evaluation.Sdk: %s -> %s" a.Evaluation.Sdk b.Evaluation.Sdk
          yield! diffHashed "Reference" (a.Dependencies.References |> List.map toHashed) (b.Dependencies.References |> List.map toHashed)
          yield! diffHashed "Analyzer" a.Dependencies.Analyzers b.Dependencies.Analyzers
          yield! diffHashed "Import" a.Evaluation.Imports b.Evaluation.Imports
          yield! diffPairs "Generated" a.Compilation.Generated b.Compilation.Generated
          yield! diffPairs "Resources" a.Compilation.Resources b.Compilation.Resources
          yield! diffStringSet "ProjectRef" a.Evaluation.ProjectRefs b.Evaluation.ProjectRefs
          yield! diffPackages a.Dependencies.Packages b.Dependencies.Packages ]

    open Json

    let private writeEntry roots (entry: Entry) =
        let str = Roots.tokenizeAll roots >> escape
        let indent = "        "
        let strings name (items: string list) =
            items |> List.map (str >> sprintf "%s  %s" indent) |> String.concat ",\n"
            |> fun body -> sprintf "%s%s: [\n%s\n%s]" indent (escape name) body indent
        let hashedList name (items: Hashed list) =
            items
            |> List.map (fun h -> sprintf "%s  { \"Path\": %s, \"Sha256\": %s }" indent (str h.Path) (escape h.Sha256))
            |> String.concat ",\n"
            |> fun body -> sprintf "%s%s: [\n%s\n%s]" indent (escape name) body indent
        let referenceList name (items: Reference list) =
            items
            |> List.map (fun r ->
                if r.Alias = "" then sprintf "%s  { \"Path\": %s, \"Sha256\": %s }" indent (str r.Path) (escape r.Sha256)
                else sprintf "%s  { \"Path\": %s, \"Sha256\": %s, \"Alias\": %s }" indent (str r.Path) (escape r.Sha256) (escape r.Alias))
            |> String.concat ",\n"
            |> fun body -> sprintf "%s%s: [\n%s\n%s]" indent (escape name) body indent
        let packageList name (items: Package list) =
            items
            |> List.map (fun p ->
                sprintf "%s  { \"Id\": %s, \"Version\": %s, \"Sha512\": %s, \"Direct\": %s, \"DependsOn\": [%s] }"
                    indent (escape p.Id) (escape p.Version) (escape p.Sha512) (if p.Direct then "true" else "false")
                    (p.DependsOn |> List.map escape |> String.concat ", "))
            |> String.concat ",\n"
            |> fun body -> sprintf "%s%s: [\n%s\n%s]" indent (escape name) body indent
        let pairs name (items: (string * string) list) =
            items |> List.map (fun (k, v) -> sprintf "%s  %s: %s" indent (str k) (str v)) |> String.concat ",\n"
            |> fun body -> sprintf "%s%s: {\n%s\n%s}" indent (escape name) body indent
        let section name (fields: string list) =
            fields |> String.concat ",\n" |> sprintf "      %s: {\n%s\n      }" (escape name)

        let e, c, d = entry.Evaluation, entry.Compilation, entry.Dependencies
        [ sprintf "      \"Name\": %s" (escape entry.Name)
          sprintf "      \"Framework\": %s" (escape entry.Framework)
          section "Evaluation"
            [ sprintf "%s\"Project\": %s" indent (str e.Project)
              strings "ProjectRefs" e.ProjectRefs
              hashedList "Imports" e.Imports
              sprintf "%s\"Sdk\": %s" indent (escape e.Sdk)
              sprintf "%s\"SdkPin\": %s" indent (escape (e.SdkPin |> Option.map sdkPinText |> Option.defaultValue ""))
              pairs "Properties" (e.Properties |> Map.toList) ]
          section "Compilation"
            [ sprintf "%s\"Directory\": %s" indent (str c.Directory)
              strings "Options" c.Options
              strings "Defines" c.Defines
              strings "Sources" c.Sources
              pairs "Generated" c.Generated
              pairs "Resources" c.Resources ]
          section "Dependencies"
            [ sprintf "%s\"Compiler\": { \"Tool\": %s, \"Path\": %s, \"Sha256\": %s, \"Version\": %s }" indent
                (escape d.Compiler.Tool) (str d.Compiler.Path) (escape d.Compiler.Sha256) (escape d.Compiler.Version)
              referenceList "References" d.References
              hashedList "Analyzers" d.Analyzers
              packageList "Packages" d.Packages ] ]
        |> String.concat ",\n" |> sprintf "    {\n%s\n    }"

    /// The lock as text: paths tokenized against the given roots, one line per item, so the
    /// file is the same on every machine and a diff of two locks is the difference between two
    /// compilations. `roots` is the full list (built-in plus any extra a script declared, e.g.
    /// via `Roots.withExtra`), longest root first.
    let writeWith roots (lock: Document) =
        [ sprintf "  \"Configuration\": %s" (escape lock.Configuration)
          sprintf "  \"Properties\": {\n%s\n  }"
            (lock.Properties |> List.map (fun (k, v) -> sprintf "    %s: %s" (escape k) (escape v)) |> String.concat ",\n")
          sprintf "  \"Entries\": [\n%s\n  ]" (lock.Entries |> List.map (writeEntry roots) |> String.concat ",\n") ]
        |> String.concat ",\n" |> sprintf "{\n%s\n}\n"

    /// `documentFramework` is the file-level `"Framework"` of a lock written before the
    /// framework moved into the entry: an entry without one of its own inherits it, which is
    /// exactly right -- such a file held one framework for all of its entries.
    let private readEntry roots (documentFramework: string) value =
        let expand = Roots.expand roots
        let str name value = field name value |> Option.bind asString |> Option.defaultValue ""
        let strings name value = field name value |> Option.map asArray |> Option.defaultValue [] |> List.choose asString |> List.map expand
        let hashedList name value =
            field name value |> Option.map asArray |> Option.defaultValue []
            |> List.map (fun h -> { Path = str "Path" h |> expand; Sha256 = str "Sha256" h })
        let referenceList name value =
            field name value |> Option.map asArray |> Option.defaultValue []
            |> List.map (fun r -> { Path = str "Path" r |> expand; Sha256 = str "Sha256" r; Alias = str "Alias" r })
        let packageList name value =
            field name value |> Option.map asArray |> Option.defaultValue []
            |> List.map (fun p ->
                { Id = str "Id" p; Version = str "Version" p; Sha512 = str "Sha512" p
                  Direct = field "Direct" p |> Option.bind asBool |> Option.defaultValue false
                  DependsOn = field "DependsOn" p |> Option.map asArray |> Option.defaultValue [] |> List.choose asString })
        let pairs name value =
            match field name value with
            | Some (JObject members) -> members |> List.choose (fun (k, v) -> asString v |> Option.map (fun v -> expand k, expand v))
            | _ -> []
        let sectionOf name =
            match field name value with
            | Some (JObject _ as section) -> section
            | _ -> failwithf "lock entry '%s' has no '%s' section: lock written by an older Xake; re-import" (str "Name" value) name
        let e, c, d = sectionOf "Evaluation", sectionOf "Compilation", sectionOf "Dependencies"
        let compiler = field "Compiler" d |> Option.defaultValue (JObject [])
        {
            Name = str "Name" value
            Framework = field "Framework" value |> Option.bind asString |> Option.defaultValue documentFramework
            Evaluation =
                { Project = str "Project" e |> expand
                  ProjectRefs = strings "ProjectRefs" e
                  Imports = hashedList "Imports" e
                  Sdk = str "Sdk" e
                  SdkPin = str "SdkPin" e |> parseSdkPin
                  Properties = pairs "Properties" e |> Map.ofList }
            Compilation =
                { Directory = str "Directory" c |> expand
                  Options = strings "Options" c
                  Defines = strings "Defines" c
                  Sources = strings "Sources" c
                  Generated = pairs "Generated" c
                  Resources = pairs "Resources" c }
            Dependencies =
                { Compiler = { Tool = str "Tool" compiler; Path = str "Path" compiler |> expand; Sha256 = str "Sha256" compiler; Version = str "Version" compiler }
                  References = referenceList "References" d
                  Analyzers = hashedList "Analyzers" d
                  Packages = packageList "Packages" d }
        }

    /// Reads a lock `writeWith` produced, paths expanded for this machine. `roots` must be the
    /// same list (or a superset) used to write it, or a token stays untranslated. A lock in
    /// the flat, pre-split format (`Projects` with a verbatim `Args`) is refused with a message
    /// saying to re-import. A lock written when the framework was a property of the *file* (a
    /// document-level `"Framework"`, one framework per lock) still reads: the value is
    /// distributed into every entry that does not carry its own. Only the new shape is ever
    /// written.
    let parseWith roots (text: string) =
        let root = Json.parse text
        let str name = field name root |> Option.bind asString |> Option.defaultValue ""
        if (field "Projects" root).IsSome && (field "Entries" root).IsNone then
            failwith "lock written by an older Xake (flat format with 'Projects'/'Args'); re-import"
        {
            Configuration = str "Configuration"
            Properties =
                match field "Properties" root with
                | Some (JObject members) -> members |> List.choose (fun (k, v) -> asString v |> Option.map (fun v -> k, v))
                | _ -> []
            Entries = field "Entries" root |> Option.map asArray |> Option.defaultValue [] |> List.map (readEntry roots (str "Framework"))
        }

    let readWith roots (path: string) = System.IO.File.ReadAllText path |> parseWith roots

    /// A lock path resolved the way the engine resolves a target: against the build's project
    /// root (`ExecOptions.ProjectRoot`), not the process's current directory.
    let private fullPath (path: string) : Recipe<ExecContext, string> =
        recipe {
            let! options = getCtxOptions()
            return if System.IO.Path.IsPathRooted path then path else options.ProjectRoot </> path
        }

    /// `load` with extra roots declared by the script (the ones its `Project.import` used, see
    /// `ImportOptions.Roots`).
    ///
    /// Reading a lock **is** depending on it: `loadWith` `needFiles` the lock itself, so a
    /// recipe that reads one records the `FileDep` -- and, when a rule produces the lock (the
    /// import rule of every script here), builds it first. That used to be an explicit
    /// `do! need [lockFile ...]` in front of every `Lock.load`, easy to forget and impossible
    /// to see the absence of.
    let loadWith (extraRoots: (string * string) list) (path: string) : Recipe<ExecContext, Document> =
        recipe {
            let! full = fullPath path
            do! needFiles (Filelist [ File.make full ])
            let! roots = Roots.currentWith extraRoots
            return readWith roots full
        }

    /// Reads a lock against the roots of this build: the built-in three, with the project root
    /// taken from the engine (`ExecOptions.ProjectRoot`) rather than from the process's current
    /// directory -- which is why this is a recipe and `readWith` is not. Depends on the lock
    /// file (see `loadWith`).
    let load (path: string) : Recipe<ExecContext, Document> = loadWith [] path

    /// `save` with extra roots declared by the script.
    ///
    /// Deliberately *not* symmetric with `loadWith`: writing a file is not depending on it.
    /// A `need` here would be wrong in both of `save`'s uses -- a rule writing the lock as its
    /// own target would depend on itself, and `csc { lock }` writes its lock from inside the
    /// compile recipe, where the lock is not a target at all.
    let saveWith (extraRoots: (string * string) list) (path: string) (lock: Document) : Recipe<ExecContext, unit> =
        recipe {
            let! full = fullPath path
            let! roots = Roots.currentWith extraRoots
            System.IO.File.WriteAllText (full, writeWith roots lock)
        }

    /// Writes a lock against this build's roots (see `load`).
    let save (path: string) (lock: Document) : Recipe<ExecContext, unit> = saveWith [] path lock

    let private named (name: string) (e: Entry) =
        e.Name = name || System.IO.Path.GetFileNameWithoutExtension e.Evaluation.Project = name

    let private known (lock: Document) =
        lock.Entries |> List.map (fun e -> if e.Framework = "" then e.Name else e.Name + " (" + e.Framework + ")") |> String.concat ", "

    /// The entry for one project, by assembly name or by project file name. A lock now holds
    /// every target framework of the project set, so a name can match more than one entry:
    /// that is an error naming the frameworks, and `entryFor` is the way to say which one.
    let entry (name: string) (lock: Document) =
        match lock.Entries |> List.filter (named name) with
        | [ e ] -> e
        | [] -> failwithf "project '%s' is not in the lock (%s)" name (known lock)
        | many ->
            failwithf "project '%s' is in the lock for %d frameworks (%s); ask for one with Lock.entryFor"
                name (List.length many) (many |> List.map (fun e -> e.Framework) |> String.concat ", ")

    /// The entry for one project *and* one target framework -- the unambiguous lookup, and
    /// the one a build script whose rules carry the framework should use.
    let entryFor (framework: string) (name: string) (lock: Document) =
        match lock.Entries |> List.filter (fun e -> e.Framework = framework && named name e) with
        | [ e ] -> e
        | [] -> failwithf "project '%s' for '%s' is not in the lock (%s)" name framework (known lock)
        | _ :: _ -> failwithf "project '%s' for '%s' appears more than once in the lock" name framework
