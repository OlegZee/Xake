namespace Xake.Hermetic.Dotnet

open System.IO

open Xake
open Xake.Tasks
open Xake.Dotnet

/// The imported compilation of a project: exactly what `dotnet build` would have handed the
/// compiler, plus the identity (path and SHA-256) of everything the compiler would read that
/// is not in the repository -- packages, analyzers, the compiler itself, the msbuild files
/// that produced the answer. One lock file (`Document`) per variant holds one `Entry` per
/// (project, framework), and an entry is the `Csc` the compiler is handed (the structured
/// command line, the generated inputs, the resx pairs, and the hashed compiler, references and
/// analyzers) plus its provenance: `Evaluation` (where the answer came from -- the project
/// file, its imports, the SDK; empty-valued for a compilation composed from `csc {}`
/// settings) and `Packages` (the restore graph). On disk the entry keeps the sectioned shape
/// `Name`, `Framework`, `Evaluation`, `Compilation`, `Dependencies` (with `Packages` inside).
///
/// The recipes at the end (`build`, `compile`, `record`, `verify`) are the lock's side of a
/// build: gate a compilation against the lock, obtain what it names (packages, the revision
/// token, an explanation for a missing compiler), then hand it to `Csc.run`.
module Lock =

    /// Abbreviations of the types that moved to `Csc.fs` / `Restore.fs`, kept so that
    /// `Lock.Hashed`, `Lock.Reference`, `Lock.Compiler` and `Lock.Package` still name them.
    type Hashed = Xake.Dotnet.Hashed
    type Reference = Xake.Dotnet.Reference
    type Compiler = Xake.Dotnet.Compiler
    type Package = Xake.Hermetic.Dotnet.Package

    /// The restore graph of one target, as the lock records it (`Entry.Packages`, what
    /// `Project.import` writes): every package of `assets.Packages` with its cache sha512
    /// (`Nuget.readCache`, "" when the cache has no `.nupkg.metadata`), whether it is a direct
    /// `PackageReference`, and the ids it depends on (edges of `assets.Graph` whose source is
    /// this package). Pure but for reading the cache's metadata files.
    let packagesOf (cacheRoot: string) (assets: Nuget.Assets) : Package list =
        let direct = assets.Direct |> List.map (fun s -> s.ToLowerInvariant ()) |> Set.ofList
        let same (a: string) (b: string) = System.String.Equals (a, b, System.StringComparison.OrdinalIgnoreCase)
        assets.Packages |> List.map (fun (id, version) ->
            { Id = id
              Version = version
              Sha512 = (Nuget.readCache cacheRoot id version).Sha512
              Direct = direct.Contains (id.ToLowerInvariant ())
              DependsOn =
                assets.Graph
                |> List.choose (fun ((fromId, fromVersion), (toId, _)) ->
                    if same fromId id && same fromVersion version then Some toId else None)
                |> List.distinct })

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

    /// The `global.json` the .NET host would use for `dir`: searched upwards from it, the
    /// first one found.
    let internal findGlobalJson (dir: string) : string option =
        let rec find (dir: string) =
            let path = Path.Combine (dir, "global.json")
            if File.Exists path then Some path
            else
                match Path.GetDirectoryName (dir: string) with
                | null | "" -> None
                | parent when parent = dir -> None
                | parent -> find parent
        find (Path.GetFullPath dir)

    /// The pin a `global.json` file states: `sdk.version` plus `sdk.rollForward` (default
    /// `latestPatch` when a version is set and the policy is absent); only `"disable"` is
    /// `Pinned`.
    let internal readSdkPin (file: string) : SdkPin =
        let root = File.ReadAllText file |> Json.parse
        let sdk = Json.field "sdk" root
        match sdk |> Option.bind (Json.field "version") |> Option.bind Json.asString with
        | None -> NoVersion file
        | Some version ->
            match sdk |> Option.bind (Json.field "rollForward") |> Option.bind Json.asString with
            | Some "disable" -> Pinned version
            | Some policy -> RollsForward (version, policy)
            | None -> RollsForward (version, "latestPatch")

    /// <summary>
    /// The SDK pin in effect for `dir` and the `global.json` that states it (`None` with
    /// `NoGlobalJson`): `findGlobalJson`, then `readSdkPin`. Pure but for reading the file.
    /// </summary>
    /// <remarks>
    /// The exact-pin reader of this package. Xake 3.6 exposes one in the base
    /// (`DotNetFwk`); once this package builds on 3.6 this one is to be replaced by it.
    /// </remarks>
    let sdkPinAt (dir: string) : string option * SdkPin =
        match findGlobalJson dir with
        | None -> None, NoGlobalJson
        | Some file -> Some file, readSdkPin file

    /// <summary>
    /// An input the environment must provide by itself -- no restore step fetches it -- and
    /// which a replay of the lock checks before doing any work (`compileWith`). One kind
    /// today: `"dotnet-sdk"`, the .NET SDK at exactly `Version`, which covers the paths
    /// under <c>$(DotnetRoot)/sdk/&lt;Version&gt;/</c> (the SDK's fsc, an imported project's
    /// compiler and analyzers). It is written only when `global.json` pins that version
    /// exactly (`rollForward: "disable"`); `Pin` names that file.
    /// </summary>
    /// <remarks>
    /// `Pin` is kept tokenized (<c>$(ProjectRoot)/global.json</c>) in memory as well as on
    /// disk: it is where the requirement comes from, quoted in messages, never opened by a
    /// replay. So `mapPaths` and `rehash` leave it alone and `mapText` applies to it.
    /// </remarks>
    type Prerequisite = {
        /// `"dotnet-sdk"`
        Kind: string
        /// The exact version (`10.0.401`)
        Version: string
        /// Where the requirement comes from, tokenized (`$(ProjectRoot)/global.json`)
        Pin: string
    }

    /// The `Kind` of the .NET SDK prerequisite.
    [<Literal>]
    let DotnetSdk = "dotnet-sdk"

    /// The SDK versions a list of paths depends on: every path that, tokenized against
    /// `roots`, starts with <c>$(DotnetRoot)/sdk/&lt;v&gt;/</c>. Distinct, in order of first
    /// appearance. Pure.
    let sdkVersionsOf roots (paths: string list) : string list =
        let prefix = "$(DotnetRoot)/sdk/"
        paths
        |> List.map (Roots.tokenize roots)
        |> List.choose (fun p ->
            if p.StartsWith prefix then
                match p.Substring(prefix.Length).Split('/') with
                | [| _ |] -> None
                | parts when parts.[0] <> "" -> Some parts.[0]
                | _ -> None
            else None)
        |> List.distinct

    /// The prerequisites an entry naming the SDK `versions` gets under `pin` (read from
    /// `pinFile`), and the versions left without one -- each of those is a warning for the
    /// caller to trace (`sdkUnpinnedWarning`). A version is a prerequisite only when the pin is
    /// exactly it. Pure.
    let prerequisitesFor roots (pinFile: string option) (pin: SdkPin) (versions: string list) : Prerequisite list * string list =
        let pinned, unpinned =
            versions |> List.partition (fun v -> match pin, pinFile with Pinned p, Some _ -> p = v | _ -> false)
        let pinText = pinFile |> Option.map (Roots.tokenize roots) |> Option.defaultValue ""
        [ for v in pinned -> { Kind = DotnetSdk; Version = v; Pin = pinText } ], unpinned

    /// The warning for an entry that depends on an SDK `global.json` does not pin exactly.
    let sdkUnpinnedWarning (name: string) (version: string) =
        sprintf "'%s': depends on the .NET SDK %s but global.json does not pin it exactly; the lock will only build where that SDK is installed" name version

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
    } with
        /// The evaluation of a composed compilation: no project, no imports, no SDK, no pin.
        static member Empty =
            { Project = ""; ProjectRefs = []; Imports = []; Sdk = ""; SdkPin = None; Properties = Map.empty }

    /// What the compiler of one entry is handed: a C# compilation (`Csc`) or an F# one
    /// (`Fsc`). The two records have the same fields with the same meaning and differ only in
    /// the argument dialect (`CscArgs`/`FscArgs`) and the runner options; the members below
    /// are the common view. On disk the case is `Dependencies.Compiler.Tool`: `"fsc"` is F#,
    /// anything else C#. Qualified (`Compilation.Csc c`), so the case never shadows the
    /// module `Csc`.
    [<RequireQualifiedAccess>]
    type Compilation =
        | Csc of Csc
        | Fsc of Fsc
        member this.Name = match this with Compilation.Csc c -> c.Name | Compilation.Fsc f -> f.Name
        member this.Framework = match this with Compilation.Csc c -> c.Framework | Compilation.Fsc f -> f.Framework
        member this.Directory = match this with Compilation.Csc c -> c.Directory | Compilation.Fsc f -> f.Directory
        member this.Options = match this with Compilation.Csc c -> c.Options | Compilation.Fsc f -> f.Options
        member this.Defines = match this with Compilation.Csc c -> c.Defines | Compilation.Fsc f -> f.Defines
        member this.Sources = match this with Compilation.Csc c -> c.Sources | Compilation.Fsc f -> f.Sources
        member this.Generated = match this with Compilation.Csc c -> c.Generated | Compilation.Fsc f -> f.Generated
        member this.Resources = match this with Compilation.Csc c -> c.Resources | Compilation.Fsc f -> f.Resources
        member this.Dependencies = match this with Compilation.Csc c -> c.Dependencies | Compilation.Fsc f -> f.Dependencies
        /// The exact command line, in the compiler's own dialect
        member this.Args = match this with Compilation.Csc c -> c.Args | Compilation.Fsc f -> f.Args
        /// The output assembly (`/out:` or `-o:`/`--out:`), if the options carry one
        member this.Output = match this with Compilation.Csc c -> c.Output | Compilation.Fsc f -> f.Output

    /// One project's compilation, as recorded in a lock: what the compiler is handed
    /// (`Compilation`, which carries the entry's identity, `Name` and `Framework`) plus the
    /// provenance the runner never reads -- the msbuild evaluation and the restore graph.
    ///
    /// `(Name, Framework)` -- not the name alone -- identifies an entry: one lock holds every
    /// framework of the project set (`Lock.entryFor`).
    type Entry = {
        Compilation: Compilation
        Evaluation: Evaluation
        /// The restore graph (`project.assets.json`) at import time; empty when composed
        Packages: Package list
        /// What the environment must provide by itself (`Prerequisite`), checked before a
        /// replay; written at record time when the entry depends on an exactly pinned SDK,
        /// empty otherwise
        Prerequisites: Prerequisite list
    } with
        member this.Name = this.Compilation.Name
        member this.Framework = this.Compilation.Framework
        member this.Dependencies = this.Compilation.Dependencies
        member this.Args = this.Compilation.Args
        member this.Output = this.Compilation.Output
        /// The C# compilation; fails for an F# entry (match on `Compilation` instead).
        member this.Csc =
            match this.Compilation with
            | Compilation.Csc c -> c
            | Compilation.Fsc f -> failwithf "'%s' is an F# entry; use entry.Compilation" f.Name
        /// The F# compilation; fails for a C# entry (match on `Compilation` instead).
        member this.Fsc =
            match this.Compilation with
            | Compilation.Fsc f -> f
            | Compilation.Csc c -> failwithf "'%s' is a C# entry; use entry.Compilation" c.Name

    /// One lock file: every project of one variant, for every target framework it was
    /// imported for. The framework is a property of the `Entry`, not of the file: a
    /// multi-targeted project set is one import, one restore per project and one lock.
    type Document = {
        Configuration: string
        /// The properties the import ran with, e.g. `Brand`
        Properties: (string * string) list
        Entries: Entry list
    }

    /// A composed compilation as a lock entry: no evaluation, no package graph.
    let ofCompilation (c: Compilation) : Entry =
        { Compilation = c; Evaluation = Evaluation.Empty; Packages = []; Prerequisites = [] }

    /// A composed C# compilation as a lock entry (`ofCompilation`).
    let ofCsc (c: Csc) : Entry = ofCompilation (Compilation.Csc c)

    /// A composed F# compilation as a lock entry (`ofCompilation`).
    let ofFsc (f: Fsc) : Entry = ofCompilation (Compilation.Fsc f)

    /// `f` or `g` applied to the compilation, keeping its case.
    let private mapCompilation (fc: Csc -> Csc) (ff: Fsc -> Fsc) = function
        | Compilation.Csc c -> Compilation.Csc (fc c)
        | Compilation.Fsc f -> Compilation.Fsc (ff f)

    /// Fills `Sha256` for every hashed item (`References`, `Analyzers`, the compiler via
    /// `Csc.rehash`/`Fsc.rehash`, and `Imports`) from what is on disk right now; leaves `""` where the file
    /// does not exist. For a composed entry, which leaves every hash empty, this is the
    /// "record time" step `lock-from-settings.md` recommendation 5 asks for -- run once by the
    /// lock-recording rule, not on every compile.
    let rehash (entry: Entry) : Entry =
        { entry with
            Compilation = entry.Compilation |> mapCompilation Csc.rehash Fsc.rehash
            Evaluation = { entry.Evaluation with Imports = entry.Evaluation.Imports |> List.map (fun h -> { h with Sha256 = Csc.sha256 h.Path }) } }

    /// Rewrites every path the entry's compilation carries, through `f` (`Csc.mapPaths`,
    /// `Fsc.mapPaths`). A
    /// build script uses this to point a project reference (the lock has it unhashed, at the
    /// referenced project's own `bin/Release/.../X.dll`) at the path the script itself
    /// produces that output at -- the rest of the lock, including the hashes that identify
    /// what was actually compiled against, stays as imported. A rewritten hashed entry loses
    /// its hash. Section markers are left alone.
    let mapPaths (f: string -> string) (entry: Entry) : Entry =
        { entry with Compilation = entry.Compilation |> mapCompilation (Csc.mapPaths f) (Fsc.mapPaths f) }

    /// Applies `f` to every piece of text that may embed a value rather than name a file:
    /// the compilation's (`Csc.mapText`, `Fsc.mapText`), the evaluation's property values and
    /// each prerequisite's (tokenized) `Pin`. Used to
    /// tokenize the commit sha out of a lock (`Git.tokenize`) and to resolve it
    /// back at compile time (`compile`).
    let mapText (f: string -> string) (entry: Entry) : Entry =
        { entry with
            Compilation = entry.Compilation |> mapCompilation (Csc.mapText f) (Fsc.mapText f)
            Evaluation = { entry.Evaluation with Properties = entry.Evaluation.Properties |> Map.map (fun _ v -> f v) }
            Prerequisites = entry.Prerequisites |> List.map (fun p -> { p with Pin = f p.Pin }) }

    /// See `Csc.diffList`.
    let diffList (a: string list) (b: string list) : string list = Csc.diffList a b

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
        [ if a.Tool <> b.Tool then sprintf "~ Compiler.Tool: %s -> %s" a.Tool b.Tool
          if a.Path <> b.Path then sprintf "~ Compiler.Path: %s -> %s" a.Path b.Path
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

    /// Prerequisites by (kind, version): added, removed, or the pin moved. Sorted.
    let private diffPrerequisites (a: Prerequisite list) (b: Prerequisite list) : string list =
        let ofList items = items |> List.map (fun (p: Prerequisite) -> (p.Kind, p.Version), p) |> Map.ofList
        let mapA, mapB = ofList a, ofList b
        let keys = (a @ b) |> List.map (fun p -> p.Kind, p.Version) |> List.distinct |> List.sort
        keys |> List.choose (fun ((kind, version) as key) ->
            match Map.tryFind key mapA, Map.tryFind key mapB with
            | Some p, None -> Some (sprintf "- Prerequisite %s %s (%s)" kind version p.Pin)
            | None, Some p -> Some (sprintf "+ Prerequisite %s %s (%s)" kind version p.Pin)
            | Some pa, Some pb when pa.Pin <> pb.Pin -> Some (sprintf "~ Prerequisite %s %s: %s -> %s" kind version pa.Pin pb.Pin)
            | _ -> None)

    /// Human-readable differences between two lock entries of the same project: `Framework`,
    /// `Options` and
    /// `Sources` as ordered lists (`diffList`, bare `+`/`-` lines), `Defines` as a set,
    /// `Compiler` (path, hash, version), `Evaluation.Sdk`, each hashed list (`References`,
    /// `Analyzers`, `Imports`) by path, `Generated`/`Resources` by key, `ProjectRefs` as a
    /// set, `Packages` by id, `Prerequisites` by kind and version. Empty list means identical. Pure, deterministic order (fixed
    /// section order, sorted within each section save the two ordered ones).
    let diff (a: Entry) (b: Entry) : string list =
        let ca, cb = a.Compilation, b.Compilation
        [ if ca.Framework <> cb.Framework then yield sprintf "~ Framework: %s -> %s" ca.Framework cb.Framework
          yield! Csc.diffList ca.Options cb.Options
          yield! Csc.diffList ca.Sources cb.Sources
          yield! diffStringSet "Define" ca.Defines cb.Defines
          yield! diffCompiler ca.Dependencies.Compiler cb.Dependencies.Compiler
          if a.Evaluation.Sdk <> b.Evaluation.Sdk then yield sprintf "~ Evaluation.Sdk: %s -> %s" a.Evaluation.Sdk b.Evaluation.Sdk
          yield! diffHashed "Reference" (ca.Dependencies.References |> List.map toHashed) (cb.Dependencies.References |> List.map toHashed)
          yield! diffHashed "Analyzer" ca.Dependencies.Analyzers cb.Dependencies.Analyzers
          yield! diffHashed "Import" a.Evaluation.Imports b.Evaluation.Imports
          yield! diffPairs "Generated" ca.Generated cb.Generated
          yield! diffPairs "Resources" ca.Resources cb.Resources
          yield! diffStringSet "ProjectRef" a.Evaluation.ProjectRefs b.Evaluation.ProjectRefs
          yield! diffPackages a.Packages b.Packages
          yield! diffPrerequisites a.Prerequisites b.Prerequisites ]

    /// `diff` as it is printed: every path in every line tokenized against `roots` (the
    /// same tokens the lock file is written with, `format`), so a drift message reads
    /// `$(ProjectRoot)/src/Extra.cs` on every machine instead of naming this checkout.
    /// `buildWith` and `verify` report the differences this way; `diff` itself keeps the
    /// expanded paths.
    let diffText roots (a: Entry) (b: Entry) : string list =
        diff a b |> List.map (Roots.tokenizeAll roots)

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

        // the file keeps the sectioned shape: `Name`/`Framework`, then `Evaluation`, then the
        // compilation's fields as `Compilation`, and its dependencies together with the
        // entry's package graph as `Dependencies`
        let e, c, d = entry.Evaluation, entry.Compilation, entry.Compilation.Dependencies
        [ sprintf "      \"Name\": %s" (escape c.Name)
          sprintf "      \"Framework\": %s" (escape c.Framework)
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
              packageList "Packages" entry.Packages
              // only when there is one: every lock without prerequisites stays byte-identical
              // to what was written before the key existed
              if not (List.isEmpty entry.Prerequisites) then
                  entry.Prerequisites
                  |> List.map (fun p ->
                      sprintf "%s  { \"Kind\": %s, \"Version\": %s, \"Pin\": %s }" indent (escape p.Kind) (escape p.Version) (escape p.Pin))
                  |> String.concat ",\n"
                  |> fun body -> sprintf "%s\"Prerequisites\": [\n%s\n%s]" indent body indent ] ]
        |> String.concat ",\n" |> sprintf "    {\n%s\n    }"

    /// The lock as text: paths tokenized against the given roots, one line per item, so the
    /// file is the same on every machine and a diff of two locks is the difference between two
    /// compilations. `roots` is the full list (built-in plus any extra a script declared, e.g.
    /// via `Roots.make`), longest root first. Pure; `save` is the recipe that takes the roots
    /// from the build.
    let format roots (lock: Document) =
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
        let name = str "Name" value
        let framework = field "Framework" value |> Option.bind asString |> Option.defaultValue documentFramework
        let dependencies : Dependencies =
            { Compiler = { Tool = str "Tool" compiler; Path = str "Path" compiler |> expand; Sha256 = str "Sha256" compiler; Version = str "Version" compiler }
              References = referenceList "References" d
              Analyzers = hashedList "Analyzers" d }
        // the case is the compiler's tool: "fsc" is F#, anything else (every lock written
        // before F# entries existed says "csc") is C#
        let compilation =
            match dependencies.Compiler.Tool with
            | "fsc" ->
                if not (List.isEmpty dependencies.Analyzers) then
                    failwithf "lock entry '%s' is an F# entry with analyzers; fsc has none" name
                Compilation.Fsc
                    { Fsc.Name = name
                      Fsc.Framework = framework
                      Fsc.Directory = str "Directory" c |> expand
                      Fsc.Options = strings "Options" c
                      Fsc.Defines = strings "Defines" c
                      Fsc.Sources = strings "Sources" c
                      Fsc.Generated = pairs "Generated" c
                      Fsc.Resources = pairs "Resources" c
                      Fsc.Dependencies = dependencies }
            | _ ->
                Compilation.Csc
                    { Name = name
                      Framework = framework
                      Directory = str "Directory" c |> expand
                      Options = strings "Options" c
                      Defines = strings "Defines" c
                      Sources = strings "Sources" c
                      Generated = pairs "Generated" c
                      Resources = pairs "Resources" c
                      Dependencies = dependencies }
        {
            Compilation = compilation
            Evaluation =
                { Project = str "Project" e |> expand
                  ProjectRefs = strings "ProjectRefs" e
                  Imports = hashedList "Imports" e
                  Sdk = str "Sdk" e
                  SdkPin = str "SdkPin" e |> parseSdkPin
                  Properties = pairs "Properties" e |> Map.ofList }
            Packages = packageList "Packages" d
            // absent in every lock written before the key existed, and whenever the entry
            // has none; `Pin` is kept tokenized, as written
            Prerequisites =
                field "Prerequisites" d |> Option.map asArray |> Option.defaultValue []
                |> List.map (fun p -> { Kind = str "Kind" p; Version = str "Version" p; Pin = str "Pin" p })
        }

    /// Reads a lock `format` produced, paths expanded for this machine. `roots` must be the
    /// same list (or a superset) used to write it, or a token stays untranslated. A lock in
    /// the flat, pre-split format (`Projects` with a verbatim `Args`) is refused with a message
    /// saying to re-import. A lock written when the framework was a property of the *file* (a
    /// document-level `"Framework"`, one framework per lock) still reads: the value is
    /// distributed into every entry that does not carry its own. Only the new shape is ever
    /// written. Pure; `load` is the recipe that takes the roots from the build.
    let parse roots (text: string) =
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

    /// `parse` of the file at `path` (taken as given, not against the project root), with
    /// the roots passed explicitly -- for code outside a recipe. Inside one use `load`, which
    /// also records the lock as a dependency.
    let read roots (path: string) = System.IO.File.ReadAllText path |> parse roots

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
            return read roots full
        }

    /// Reads a lock against the roots of this build: the built-in three, with the project root
    /// taken from the engine (`ExecOptions.ProjectRoot`) rather than from the process's current
    /// directory -- which is why this is a recipe and `read` is not. Depends on the lock
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
            System.IO.File.WriteAllText (full, format roots lock)
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

    /// What a lock-gated build needs besides the compilation: the runner's options, one per
    /// compiler (`Run` for a C# entry, `FscRun` for an F# one; each is read only for its own
    /// case), and where the packages the lock names live (and whether a missing one may be
    /// fetched).
    type Options = {
        /// The csc runner's options (`Csc.run`)
        Run: RunOptions
        /// The fsc runner's options (`Fsc.run`)
        FscRun: FscRunOptions
        Restore: Restore.Options
    } with static member Default = {
            Run = RunOptions.Default
            FscRun = FscRunOptions.Default
            Restore = Restore.Options.Default
        }

    /// Whether a failure fails the build, for this compilation: the `FailOnError` of the
    /// runner options of its own compiler.
    let private failOnError (options: Options) (c: Compilation) =
        match c with
        | Compilation.Csc _ -> options.Run.FailOnError
        | Compilation.Fsc _ -> options.FscRun.FailOnError

    /// <summary>
    /// Whether this build runs under CI, decided once per build: the script variable `CI`
    /// when set (`on`/`true`/`yes`/`1` is CI, `off`/`false`/`no`/`0` is not, case-insensitive,
    /// anything else fails), otherwise the environment variable `CI` that GitHub Actions,
    /// GitLab CI, Azure Pipelines and most other services set (non-empty and not `0`/`false`
    /// is CI). Same convention as `NETFX` and `CSC_SERVER`: `-d CI=off` or
    /// <c>xakeScript { var "CI" "on" }</c> forces either way. Reading it (`getVar`, then
    /// `getEnv`) makes it a tracked dependency of the target, so changing it reruns the target.
    /// Under CI, `buildWith` (and so `build` and `csc { lock }`) fails on a missing lock
    /// instead of recording it.
    /// </summary>
    let underCi : Recipe<ExecContext, bool> =
        recipe {
            let! variable = getVar "CI"
            match variable |> Option.map (fun v -> v.Trim().ToLowerInvariant ()) with
            | Some ("on" | "true" | "yes" | "1") -> return true
            | Some ("off" | "false" | "no" | "0") -> return false
            | Some other -> return failwithf "CI='%s': expected on or off" other
            | None ->
                let! env = getEnv "CI"
                match env |> Option.map (fun v -> v.Trim().ToLowerInvariant ()) with
                | None | Some ("" | "0" | "false") -> return false
                | Some _ -> return true
        }

    /// The extra roots that read a lock against the package folder the options restore into
    /// (`Roots.packageRootOverride`); none for the machine's own cache.
    let private packageRoots (options: Options) =
        match options.Restore.PackageRoot with
        | Some dir -> Roots.packageRootOverride dir
        | None -> []

    /// What a restore of `entries` has to provide: every compiler, reference and analyzer
    /// path they name, looked up in their combined package graph.
    let restoreRequest (entries: Entry list) : Restore.Request =
        { Packages = entries |> List.collect (fun e -> e.Packages)
          Paths =
            [ for e in entries do
                let d = e.Dependencies
                yield d.Compiler.Path
                for r in d.References do yield r.Path
                for a in d.Analyzers do yield a.Path ] }

    /// Fills the package folder from a whole lock in one step -- a script's "restore the
    /// build's dependencies" target, so a build agent can populate (and then cache) the folder
    /// before any compile runs. Fails the build on the first problem; `Restore.ensure` is the
    /// variant that reports instead, for a caller with its own failure policy.
    let restore (options: Restore.Options) (document: Document) : Recipe<ExecContext, unit> =
        recipe {
            let! problems = Restore.ensure options (restoreRequest document.Entries)
            if not (List.isEmpty problems) then
                failwithf "restoring the packages of the lock failed:\n%s" (problems |> String.concat "\n")
        }

    /// Traces `msg` as an error and, when the options say so for this compilation's compiler,
    /// fails the build with it -- the same shape as `Tool.failOnExitCode` and the runner's
    /// hash-mismatch check.
    let private failStep (options: Options) (c: Compilation) (msg: string) =
        recipe {
            do! trace Error "%s" msg
            if failOnError options c then failwith msg
        }

    /// Accounts for a compiler the lock names that this machine still does not have, once the
    /// restore step (`Restore.ensure`, which treats the compiler as one package among the
    /// entry's references and analyzers) has had its chance -- all that is left here is the
    /// part that is specific to the compiler, saying what went wrong:
    ///  - already on disk: nothing to do.
    ///  - under the package folder: a `Microsoft.Net.Compilers.Toolset`-shaped package (see
    ///    `csc { toolset }`) that the restore did not, or was not allowed to, provide. A
    ///    hash mismatch after a successful restore is a different package build, not a missing
    ///    one, and is left to the runner's check.
    ///  - under `$(DotnetRoot)/sdk/<version>/`: an SDK this machine does not have; nothing to
    ///    restore, so this fails immediately naming the SDK version.
    ///  - anywhere else: the path simply does not exist.
    let private ensureCompilerAvailable (options: Options) (c: Compilation) =
        recipe {
            let compiler = c.Dependencies.Compiler
            if File.Exists compiler.Path then
                ()
            else
                let path = compiler.Path.Replace('\\', '/')
                let comparer = if Env.isUnix then System.StringComparison.Ordinal else System.StringComparison.OrdinalIgnoreCase
                let normalize (r: string) = r.Replace('\\', '/').TrimEnd '/'
                let under (r: string) = path.StartsWith(r + "/", comparer)

                match Restore.packageRoot options.Restore |> normalize |> Some |> Option.filter under with
                | Some packageRoot ->
                    let rest = path.Substring(packageRoot.Length + 1).Split('/')
                    let packageId, version = rest.[0], rest.[1]
                    do! failStep options c
                            (sprintf "'%s': the compiler %s is not available and restoring %s %s did not provide it"
                                c.Name compiler.Path packageId version)
                | None ->
                    match Roots.dotnetRoot () |> Option.map normalize |> Option.filter under with
                    | Some dotnetRoot ->
                        let sdkPrefix = dotnetRoot + "/sdk/"
                        let msg =
                            if path.StartsWith(sdkPrefix, comparer) then
                                let version = path.Substring(sdkPrefix.Length).Split('/').[0]
                                sprintf "'%s': the lock names the compiler of SDK %s (%s), which is not installed; install that SDK or re-import with the installed one"
                                    c.Name version compiler.Path
                            else
                                sprintf "'%s': the compiler %s named by the lock is not installed" c.Name compiler.Path
                        do! failStep options c msg
                    | None ->
                        do! failStep options c
                                (sprintf "'%s': the compiler %s named by the lock does not exist" c.Name compiler.Path)
        }

    /// The prerequisites of `entry` this machine does not meet, as messages: a `dotnet-sdk`
    /// one whose <c>&lt;DotnetRoot&gt;/sdk/&lt;Version&gt;</c> folder is missing. Reads the
    /// disk, nothing else.
    let internal unmetPrerequisites (entry: Entry) : string list =
        let root = Roots.dotnetRoot ()
        [ for p in entry.Prerequisites do
            if p.Kind = DotnetSdk then
                let installed = root |> Option.exists (fun r -> Directory.Exists (Path.Combine (r, "sdk", p.Version)))
                if not installed then
                    let rootText = root |> Option.map (fun r -> r.Replace('\\', '/')) |> Option.defaultValue "<no .NET installation found>"
                    yield sprintf "'%s': the lock needs the .NET SDK %s (prerequisite from %s), which is not installed under '%s'; install it (dotnet-install --version %s --install-dir %s)"
                            entry.Name p.Version p.Pin rootText p.Version rootText ]

    /// <summary>
    /// Replays a lock entry -- one imported by `Project.import`, read back from a lock, or
    /// recorded from composed settings -- exactly as recorded, with the runner and restore
    /// options given:
    ///  0. the entry's prerequisites (`Entry.Prerequisites`): a `dotnet-sdk` one whose SDK is
    ///     not installed under the .NET root fails before anything else, saying what to
    ///     install.
    ///  1. `Restore.ensure`: a lock built on another machine names packages this one may not
    ///     have yet -- a compiler in a toolset package, and every reference and analyzer under
    ///     the package folder. The whole missing set is fetched in one restore; with nothing
    ///     missing (the normal case) this is a `File.Exists` per path and no process at all.
    ///  2. a compiler that is still missing is explained (an SDK that is not installed is not
    ///     a package).
    ///  3. the token `$(SourceRevisionId)` in `Generated`, `Options` or `Defines` (the lock
    ///     never carries the commit sha itself, `Git.tokenize`) is resolved from
    ///     the project's own repository.
    ///  4. `Csc.run` with `options.Run` for a C# entry, `Fsc.run` with `options.FscRun` for
    ///     an F# one; the runner's hash check makes the replay trustworthy.
    /// </summary>
    let compileWith (options: Options) (entry: Entry) : Recipe<ExecContext, unit> =
        recipe {
            let c = entry.Compilation

            // the prerequisites first: nothing a restore fetches can stand in for a missing SDK
            match unmetPrerequisites entry with
            | [] -> ()
            | unmet -> do! failStep options c (unmet |> String.concat "\n")

            // TODO(Xake 3.6): the HERMETIC=on gate of the lock side (hermetic-mode.md, messages
            // 9-11: refuse to record, or to replay, an entry naming a path outside
            // $(ProjectRoot) and $(NuGetPackageRoot) not covered by a prerequisite) goes here and
            // in `recordEntry`, reading the base's `HERMETIC` variable; this package does not
            // read the variable itself

            let! restoreProblems = Restore.ensure options.Restore (restoreRequest [entry])
            if not (List.isEmpty restoreProblems) then
                do! failStep options c
                        (sprintf "('%s') restoring the packages the lock names failed:\n%s"
                            c.Name (restoreProblems |> String.concat "\n"))

            // whatever the restore could not provide, the compiler's own absence is worth an
            // explanation of its own (an SDK that is not installed is not a package)
            do! ensureCompilerAvailable options c

            // when `Generated`, `Options` or `Defines` carries the token `$(SourceRevisionId)`
            // (from a project whose SourceLink writes it into `sourcelink.json`), resolve it
            // here, from the project's own repository, right before it is used -- a lock that
            // needs a revision has to be compiled in a repository, or this fails with a clear
            // message
            let sourceRevisionToken = Git.revisionToken
            let containsToken (s: string) = s.Contains sourceRevisionToken
            let needsRevision =
                (c.Generated |> List.exists (snd >> containsToken))
                || (c.Args |> List.exists containsToken)
            let! c =
                if not needsRevision then
                    recipe.Return c
                else
                    match Git.headSha c.Directory with
                    | Some sha ->
                        let resolve (s: string) = if containsToken s then s.Replace (sourceRevisionToken, sha) else s
                        recipe.Return (c |> mapCompilation (Csc.mapText resolve) (Fsc.mapText resolve))
                    | None ->
                        recipe {
                            let msg =
                                sprintf "'%s': the lock needs %s but no git repository was found at or above '%s' -- a lock that needs a revision must be compiled in a repository"
                                    c.Name sourceRevisionToken c.Directory
                            do! trace Error "%s" msg
                            if failOnError options c then failwith msg
                            return c
                        }

            match c with
            | Compilation.Csc c -> do! Csc.run options.Run c
            | Compilation.Fsc f -> do! Fsc.run options.FscRun f
        }

    /// `compileWith` with the default options, the compiler server resolved for this build
    /// (`CompilerServer.resolve`: `CSC_SERVER`, then `XAKE_CSC_SERVER`; csc only, fsc has no
    /// server).
    let compile (entry: Entry) : Recipe<ExecContext, unit> =
        recipe {
            let! server = CompilerServer.resolve None
            do! compileWith { Options.Default with Run = { Options.Default.Run with Server = server } } entry
        }

    /// <summary>
    /// `entry` with its prerequisites (`Entry.Prerequisites`) derived from this build: when
    /// its compiler, tokenized against the build's roots, lies under
    /// <c>$(DotnetRoot)/sdk/&lt;v&gt;/</c> and the `global.json` found from the project root
    /// upwards pins exactly `v` (`rollForward: "disable"`), a `dotnet-sdk v` prerequisite
    /// naming that file. Otherwise none; with `warn`, each SDK version left without one is
    /// traced as a warning (`sdkUnpinnedWarning`). The record-time step, and the same
    /// derivation for the resolved side of a comparison (`buildWith`, `verify`), so a lock
    /// whose pin changed differs from the configuration.
    /// </summary>
    let private withPrerequisites (warn: bool) (entry: Entry) : Recipe<ExecContext, Entry> =
        recipe {
            let! roots = Roots.current
            match sdkVersionsOf roots [ entry.Dependencies.Compiler.Path ] with
            | [] -> return entry
            | versions ->
                let! options = getCtxOptions ()
                let projectRoot = match options.ProjectRoot with null | "" -> Directory.GetCurrentDirectory () | dir -> dir
                let pinFile, pin = sdkPinAt projectRoot
                let prerequisites, unpinned = prerequisitesFor roots pinFile pin versions
                if warn then
                    for v in unpinned do
                        do! trace Warning "%s" (sdkUnpinnedWarning entry.Name v)
                return { entry with Prerequisites = prerequisites }
        }

    /// Writes `entry`, hashed (`rehash`, the record-time step) and with its prerequisites
    /// (`withPrerequisites`), as a one-entry lock at `path` and returns what was written. There is no msbuild configuration or property set behind
    /// a composed compilation, so those stay empty in the document.
    let private recordEntry (path: string) (entry: Entry) =
        recipe {
            let! full = fullPath path
            let dir = Path.GetDirectoryName full
            if dir <> "" then Directory.CreateDirectory dir |> ignore
            // what gets hashed is what the recorded lock depends on: a target that only
            // records (no `Csc.run`, which holds the compile-side `needFiles`) must still
            // rerun when a reference, analyzer, compiler or import changes
            let hashedPaths =
                [ for r in entry.Dependencies.References -> r.Path
                  for a in entry.Dependencies.Analyzers -> a.Path
                  yield entry.Dependencies.Compiler.Path
                  for i in entry.Evaluation.Imports -> i.Path ]
                |> List.filter File.Exists
            do! needFiles (Filelist (hashedPaths |> List.map File.make))
            let! entry = withPrerequisites true entry
            let rehashed = rehash entry
            do! save full { Configuration = ""; Properties = []; Entries = [ rehashed ] }
            return rehashed
        }

    /// <summary>
    /// Hashes `c` and writes (overwriting) the one-entry lock at `path` -- the deliberate
    /// update step `build` refuses to take on its own. A script declares it as a target of
    /// its own:
    /// <code>
    /// "update-locks" => recipe {
    ///     let! c = csc { src !!"*.cs"; out "app.dll"; resolve }
    ///     do! Lock.record "locks/app.json" (Lock.Compilation.Csc c) }
    /// </code>
    /// Nothing is compiled here.
    /// </summary>
    let record (path: string) (c: Compilation) : Recipe<ExecContext, unit> =
        recipe {
            let! _ = recordEntry path (ofCompilation c)
            return ()
        }

    /// <summary>
    /// The differences between the lock at `path` and `c` -- `[]` means the lock is current.
    /// This is what `build` fails on, without compiling and without writing anything
    /// (`lock-from-settings.md` scenario 3). Hashes are not compared when `c` has none (a
    /// composed compilation); the recorded hashes are verified against disk by the runner
    /// when a lock is actually compiled. The lines are `diffText`: paths tokenized the way
    /// the lock file writes them. A lock that does not exist fails, naming the path and
    /// `record`.
    /// </summary>
    let verify (path: string) (c: Compilation) : Recipe<ExecContext, string list> =
        recipe {
            let! full = fullPath path
            if not (File.Exists full) then
                failwithf "'%s': the lock '%s' does not exist, so there is nothing to verify. Record it (the target that calls Lock.record \"%s\", e.g. update-locks) and commit it."
                    c.Name path path
            let! doc = load full
            let! roots = Roots.current
            let! resolved = withPrerequisites false (ofCompilation c)
            return diffText roots (entry c.Name doc) resolved
        }

    /// <summary>
    /// Builds `c` gated by the lock at `path` (relative to the project root, or absolute).
    /// Strict, `npm ci`-like semantics: missing -- record it and compile, or, under CI
    /// (`underCi`, read here, so every caller gets it), fail naming the lock before anything
    /// is recorded or compiled; present -- `c` must match it or the build fails with the
    /// differences (`diffText`, tokenized paths); matching -- compile the recorded entry,
    /// whose hashes then gate the build. Compiling is `compileWith` (restore, revision,
    /// `Csc.run` or `Fsc.run`); with a package folder of the build's own (`Restore.PackageRoot`) the
    /// recorded entry is re-rooted at that folder, so the packages it names are restored
    /// there. Updating is explicit: delete the file, or call `record` from a target of the
    /// script's own. With `FailOnError = false` a mismatch is a traced error and `c` itself is
    /// compiled; a missing lock under CI fails regardless, or CI could pass without a lock.
    /// </summary>
    let buildWith (options: Options) (path: string) (c: Compilation) : Recipe<ExecContext, unit> =
        recipe {
            let! full = fullPath path
            // the roots the lock is written with (`save`), which are also the ones its
            // differences are printed with
            let! roots = Roots.current
            let! compileRoots = Roots.currentWith (packageRoots options)
            // the recorded entry as it is compiled: re-rooted in memory at the package folder
            // the options restore into, when that is not the machine's cache -- written with
            // the lock's own roots, read back with the build's
            let forCompile (recorded: Entry) =
                match options.Restore.PackageRoot with
                | None -> recorded
                | Some _ ->
                    format roots { Configuration = ""; Properties = []; Entries = [ recorded ] }
                    |> parse compileRoots
                    |> entry recorded.Name
            if not (File.Exists full) then
                let! ci = underCi
                if ci then
                    let msg =
                        sprintf "'%s': the lock '%s' is not there. Under CI a lock is never recorded: record it on a developer machine (build once, or run the target that calls Lock.record \"%s\", e.g. update-locks) and commit it."
                            c.Name path path
                    do! trace Error "%s" msg
                    // not `failStep`: `FailOnError = false` must not let CI pass without a lock
                    failwith msg
                // no lock yet: record what was just resolved and compile that -- the hashes
                // `Csc.run` verifies are the ones taken a moment ago
                let! recorded = recordEntry path (ofCompilation c)
                do! compileWith options (forCompile recorded)
            else
                let! doc = load full
                let recorded = entry c.Name doc
                let! resolved = withPrerequisites false (ofCompilation c)
                match diffText roots recorded resolved with
                | [] ->
                    // compile the recorded entry, not the resolved one: its hashes are what
                    // gate the build
                    do! compileWith options (forCompile recorded)
                | differences ->
                    do! failStep options c
                            (sprintf "'%s': the resolved compilation differs from the lock '%s':\n%s\nUpdate the lock deliberately: delete '%s', or run the target that calls Lock.record \"%s\"."
                                c.Name path (differences |> String.concat "\n") path path)
                    // FailOnError = false turned the failure into a warning: the resolved
                    // compilation is the source of truth, so compile what it says
                    do! compileWith options (ofCompilation c)
        }

    /// `buildWith` with the default options and the compiler server resolved for this build
    /// (`CompilerServer.resolve`); under CI a missing lock fails (`underCi`).
    let build (path: string) (c: Compilation) : Recipe<ExecContext, unit> =
        recipe {
            let! server = CompilerServer.resolve None
            do! buildWith { Options.Default with Run = { Options.Default.Run with Server = server } } path c
        }

/// The state of a `csc {}` block after `lock "path"`: the lock path, the settings, and
/// whether a package the lock names may be fetched (`nofetch` turns it off).
type CscLocked = CscLocked of path: string * settings: CscSettingsType * fetch: bool

/// `csc { ...; lock "path" }`: the settings resolved, then built gated by that lock
/// (`Lock.buildWith`, so under CI a missing lock fails) with the run options the settings
/// imply (`Csc.runOptions`). `nofetch` comes *after* `lock` -- an operation on the locked
/// state, not on the settings:
/// <code>
/// csc { src !!"*.cs"; out (File.make "app.dll"); lock "locks/app.json"; nofetch }
/// </code>
/// The packages are read from and restored into the build's package folder (the
/// `NUGET_PACKAGES` environment variable, else `~/.nuget/packages`); there is no per-target
/// folder (`packageroot` was removed in 0.2). A script that reads a lock against another
/// folder uses `Lock.loadWith (Roots.packageRootOverride dir)` and `Restore.into dir`.
[<AutoOpen>]
module CscLockBuilder =
    type CscSettingsBuilder with
        /// <summary>Records this compilation in the lock file at the given path (relative to the
        /// project root, or absolute) and, once it exists, refuses to compile anything else:
        /// the resolved settings must match the lock, or the build fails with the differences.
        /// A matching lock is what gets compiled, so its recorded hashes are verified against
        /// disk. To update it, delete the file or call `Lock.record` from a target of the
        /// script's own. A missing lock is recorded, except under CI (`Lock.underCi`), where
        /// it fails the build. Only `nofetch` may follow it.</summary>
        [<CustomOperation("lock")>]
        member _.Lock(s: CscSettingsType, path: string) = CscLocked (path, s, true)

        /// <summary>After `lock`: never fetch a package the lock names but the package folder
        /// does not have (`Restore.Options.Enabled = false`, the per-target form of
        /// `NUGET_FETCH=off`); the missing files are reported by the hash check before
        /// anything is compiled.</summary>
        [<CustomOperation("nofetch")>]
        member _.NoFetch(CscLocked (path, s, _)) = CscLocked (path, s, false)

        member _.Run(CscLocked (path, s, fetch)) =
            recipe {
                let! c = Csc.ofSettings s
                let! run = Csc.runOptions s
                do! Lock.buildWith
                        { Run = run; FscRun = FscRunOptions.Default; Restore = { Restore.Options.Default with Enabled = fetch } }
                        path (Lock.Compilation.Csc c)
            }

/// The state of an `fsc {}` block after `lock "path"`: the lock path, the settings, and
/// whether a package the lock names may be fetched (`nofetch` turns it off).
type FscLocked = FscLocked of path: string * settings: FscSettingsType * fetch: bool

/// `fsc { ...; lock "path" }`: the twin of `csc { lock }`. The settings resolved
/// (`Fsc.ofSettings`), then built gated by that lock (`Lock.buildWith`, so under CI a missing
/// lock fails) with the run options the settings imply (`Fsc.runOptions`); `nofetch` may come
/// *after* `lock`:
/// <code>
/// fsc { targetfwk "net8.0"; src !!"*.fs"; out (File.make "lib.dll"); lock "locks/lib.json" }
/// </code>
/// The F# compiler is the SDK's own `fsc.dll`: pin the SDK (`global.json`, `rollForward:
/// disable`) and the lock records it as a `dotnet-sdk` prerequisite (`Lock.Prerequisite`),
/// checked before every replay; without the pin the recording warns, and the lock only
/// builds where that SDK is installed.
[<AutoOpen>]
module FscLockBuilder =
    type FscSettingsBuilder with
        /// <summary>Records this compilation in the lock file at the given path (relative to the
        /// project root, or absolute) and, once it exists, refuses to compile anything else:
        /// the resolved settings must match the lock, or the build fails with the differences.
        /// A matching lock is what gets compiled, so its recorded hashes are verified against
        /// disk. To update it, delete the file or call `Lock.record` from a target of the
        /// script's own. A missing lock is recorded, except under CI (`Lock.underCi`), where
        /// it fails the build. Only `nofetch` may follow it.</summary>
        [<CustomOperation("lock")>]
        member _.Lock(s: FscSettingsType, path: string) = FscLocked (path, s, true)

        /// <summary>After `lock`: never fetch a package the lock names but the package folder
        /// does not have (`Restore.Options.Enabled = false`, the per-target form of
        /// `NUGET_FETCH=off`); the missing files are reported by the hash check before
        /// anything is compiled.</summary>
        [<CustomOperation("nofetch")>]
        member _.NoFetch(FscLocked (path, s, _)) = FscLocked (path, s, false)

        member _.Run(FscLocked (path, s, fetch)) =
            recipe {
                let! f = Fsc.ofSettings s
                let! run = Fsc.runOptions s
                do! Lock.buildWith
                        { Run = RunOptions.Default; FscRun = run; Restore = { Restore.Options.Default with Enabled = fetch } }
                        path (Lock.Compilation.Fsc f)
            }
