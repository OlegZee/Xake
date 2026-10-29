namespace Xake.Hermetic.Dotnet

open System.IO

open Xake
open Xake.Tasks
open Xake.Dotnet

/// The SHA-256 of a file, memoised for the process by (full path, length, write time): an
/// import hashes the same package files for every entry that copies them (on the trial product
/// 13,638 copy-local items, mostly the same files in 72 test projects), a run folder checks
/// them again. A file rewritten in place gets a new write time, hence a new key.
///
/// Text inputs (an import's msbuild files, an application's recorded files) are recorded by
/// `textSha256`, the hash of their content with CRLF line ends read as LF, and checked by
/// `textMatches`, which also accepts the raw hash (a lock an older package recorded): a lock
/// set is then the same whichever line ends a checkout gives its files (`core.autocrlf`).
module FileHash =

    // the key carries the mode (0 raw, 1 text): one path has a hash of each kind
    let private memo = System.Collections.Concurrent.ConcurrentDictionary<struct (int * string * int64 * int64), string> ()

    let private memoised (mode: int) (compute: string -> string) (path: string) : string =
        let info = FileInfo path
        if not info.Exists then compute path
        else memo.GetOrAdd (struct (mode, info.FullName, info.Length, info.LastWriteTimeUtc.Ticks), fun _ -> compute path)

    /// `bytes` with every CRLF pair read as LF; a lone CR (and a lone LF) kept, nothing else
    /// changed (a BOM, the encoding: a UTF-16 file is hashed as its bytes are, CR NUL LF NUL).
    let normalizeLineEnds (bytes: byte[]) : byte[] =
        let result = ResizeArray<byte> (bytes.Length)
        for i in 0 .. bytes.Length - 1 do
            if not (bytes.[i] = 13uy && i + 1 < bytes.Length && bytes.[i + 1] = 10uy) then result.Add bytes.[i]
        result.ToArray ()

    /// The SHA-256 (lowercase hex, as `Csc.sha256`) of a file's content after `normalizeLineEnds`;
    /// "" when it does not exist, as `Csc.sha256`.
    let private textHash (path: string) : string =
        if not (File.Exists path) then "" else
        use algo = System.Security.Cryptography.SHA256.Create ()
        algo.ComputeHash (normalizeLineEnds (File.ReadAllBytes path)) |> Array.map (sprintf "%02x") |> String.concat ""

    /// `Csc.sha256 path`, memoised; a missing file is hashed (and fails) as `Csc.sha256` does.
    let sha256 (path: string) : string = memoised 0 Csc.sha256 path

    /// The hash a lock records for a text input: of the content with CRLF read as LF
    /// (`normalizeLineEnds`), memoised apart from `sha256`; "" for a missing file, as `sha256`.
    let textSha256 (path: string) : string = memoised 1 textHash path

    /// Whether the text file `path` has the hash `sha` a lock records: the line-end neutral one
    /// (`textSha256`) or, for a lock recorded by an older package, the raw one (`sha256`).
    /// "" never matches: it is the lock's "not hashed", which the caller handles.
    let textMatches (path: string) (sha: string) : bool =
        sha <> "" && (textSha256 path = sha || sha256 path = sha)

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

    /// The base's reading of a `global.json` pin (`DotNetFwk.GlobalJsonPin`) as the lock writes
    /// it: the file that states it (`None` with `NoGlobalJson`) and the `SdkPin`.
    let ofGlobalJsonPin (pin: DotNetFwk.GlobalJsonPin) : string option * SdkPin =
        match pin with
        | DotNetFwk.NoGlobalJson -> None, NoGlobalJson
        | DotNetFwk.Exact (version, file) -> Some file, Pinned version
        | DotNetFwk.RollsForward (version, policy, file) -> Some file, RollsForward (version, policy)
        | DotNetFwk.NoVersion file -> Some file, NoVersion file

    /// <summary>
    /// The SDK pin in effect for `dir` and the `global.json` that states it (`None` with
    /// `NoGlobalJson`): the `global.json` found searching upwards from `dir`, as the .NET host
    /// does, read by the base (`DotNetFwk.globalJsonPin`, the same reader `HERMETIC=on` uses)
    /// and mapped to the lock's `SdkPin` (`ofGlobalJsonPin`). Pure but for reading the file.
    /// </summary>
    let sdkPinAt (dir: string) : string option * SdkPin =
        DotNetFwk.globalJsonPin dir |> ofGlobalJsonPin

    /// <summary>
    /// An input the environment must provide by itself -- no restore step fetches it -- and
    /// which a replay of the lock checks before doing any work (`compileWith`). One kind
    /// today: `"dotnet-sdk"`, the .NET SDK at exactly `Version`, which covers the paths
    /// under <c>$(DotnetRoot)/sdk/&lt;Version&gt;/</c> (the SDK's fsc, an imported project's
    /// compiler and analyzers, and every file of a recorded entry under it). A composed or
    /// `Project.import` entry gets it only when `global.json` pins that version exactly
    /// (`rollForward: "disable"`); an entry recorded from msbuild (`Import.record`) has it when
    /// it names a file under the SDK that recorded it (`withRecordedPrerequisites`: its
    /// compiler, analyzers, shims, the SDK's apphost), which may be stricter than `global.json`
    /// (the import warns). `Pin` names that `global.json` ("" when there is none).
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

    /// The SDK versions a list of paths depends on: every path under
    /// <c>&lt;root&gt;/sdk/&lt;v&gt;/</c>, where the root is the `$(DotnetRoot)` of `roots`
    /// (`DotNetFwk.sdkVersionOf`). Distinct, in order of first appearance. Pure.
    let sdkVersionsOf roots (paths: string list) : string list =
        match roots |> List.tryFind (fst >> (=) "$(DotnetRoot)") with
        | None -> []
        | Some (_, dotnetRoot) -> paths |> List.choose (DotNetFwk.sdkVersionOf dotnetRoot) |> List.distinct

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
        /// The `.runtimeconfig.json` the rule declares next to the output, if any
        member this.RuntimeConfig = match this with Compilation.Csc c -> c.RuntimeConfig | Compilation.Fsc f -> f.RuntimeConfig
        member this.Dependencies = match this with Compilation.Csc c -> c.Dependencies | Compilation.Fsc f -> f.Dependencies
        /// The exact command line, in the compiler's own dialect
        member this.Args = match this with Compilation.Csc c -> c.Args | Compilation.Fsc f -> f.Args
        /// The output assembly (`/out:` or `-o:`/`--out:`), if the options carry one
        member this.Output = match this with Compilation.Csc c -> c.Output | Compilation.Fsc f -> f.Output

    /// The output of a project another one references, as an entry recorded from msbuild
    /// names it: the reference is `project:<Name>` in the lock, and `Path` (the build's own
    /// layout, `outOf Configuration Framework Name`) is where the build puts it. `Framework` is
    /// the one msbuild chose (`NearestTargetFramework`).
    type ProjectOutput = { Name: string; Configuration: string; Framework: string; Path: string }

    /// <summary>
    /// The apphost of an application (`App.Host`), as `Import.record` writes it down (decision
    /// 3, "the apphost is part of the SDK"):
    /// <list type="bullet">
    /// <item>`SdkHost` -- the project names no `RuntimeIdentifier`: msbuild takes the template
    /// of the SDK's own runtime identifier, so the lock records only that; a build takes the
    /// template of the SDK it builds with (`Evaluation.Sdk`: its `NETCoreSdkRuntimeIdentifier`
    /// and `KnownAppHostPack`), identified by the SDK version and not hashed;</item>
    /// <item>`PackHost` -- an explicit `RuntimeIdentifier`: the template file msbuild resolved
    /// (`ResolvedAppHostPack`, a `pack:microsoft.netcore.app.host.&lt;rid&gt;/...` identity) with
    /// its sha256.</item>
    /// </list>
    /// </summary>
    type AppHost =
        | SdkHost
        | PackHost of rid: string * template: Hashed

    /// A file msbuild writes for an application (`runtimeconfig.json`, `deps.json`, the
    /// `<exe>.config` or a test library's `<name>.dll.config`, binding redirects included), recorded beside the lock (`Path`, `<lock>.files/<framework>/app/`,
    /// with its sha256) -- or the project's own config file msbuild copies as it is (a .NET
    /// application's `App.config`), named where it is -- and written into the output as `Name`.
    type AppFile = { Name: string; Path: string; Sha256: string }

    /// A file msbuild copies into an application's output (`ReferenceCopyLocalPaths`): the
    /// file (`pkg:`/`pack:` identity, or `project:` for a referenced project's output, whose
    /// `Sha256` is empty) and where it goes, relative to the output (`DestinationSubDirectory`
    /// and the file name: `runtimes/win-x64/native/e_sqlite3.dll`).
    type CopyItem = { Path: string; Sha256: string; To: string }

    /// <summary>
    /// What msbuild makes of a project besides the compile, as `Import.record` records it --
    /// for a project with runtime output (`HasRuntimeOutput`: an exe, a winexe, a .NET test
    /// project) always, for a library when msbuild copies something into its output or
    /// generates binding redirects for it (`OutputType` `Library`, no `Host`, `Files` the config
    /// file only: a .NET Framework test library's copy-local packages and `.dll.config`, a
    /// library's content items). The compile writes `Files` and the
    /// apphost beside the assembly; a run folder (`Locks.runFolderWith`) places the rest,
    /// deriving nothing:
    /// <list type="bullet">
    /// <item>`Host`: the apphost (`UseAppHost`), `None` without one;</item>
    /// <item>`Files`: the `runtimeconfig.json` and `deps.json` msbuild generated
    /// (`GenerateBuildRuntimeConfigurationFiles`, `GenerateBuildDependencyFile`) and the config
    /// file: msbuild's finished one with its binding redirects (against the referenced projects'
    /// stand-ins), else the one before redirects (for .NET Framework the SDK's `supportedRuntime`
    /// added), else the project's own `App.config`; a referenced project's versions in `deps.json` are the
    /// tokens `$(AssemblyVersion:&lt;file&gt;)`/`$(FileVersion:&lt;file&gt;)`, read from the built file
    /// at build time, as msbuild reads them;</item>
    /// <item>`Copy`: msbuild's copy-local files, packages and referenced projects;</item>
    /// <item>`Content`: the project's own items msbuild copies to the output
    /// (`_GetCopyToOutputDirectoryItemsFromThisProject`: `None`, `Content`, `EmbeddedResource`,
    /// `Compile` with `CopyToOutputDirectory`), each at its `TargetPath` -- a file of the
    /// repository by its path (unhashed: an input of the run folder, like a source), a package's
    /// by identity with its hash. A referencing project's run folder takes them too, as
    /// msbuild's transitive `GetCopyToOutputDirectoryItems` does;</item>
    /// <item>`Related`: the extensions of the files copied beside a referenced project's
    /// output when they exist (`AllowedReferenceRelatedFileExtensions`: `.pdb`, `.xml`, ...);</item>
    /// <item>`BindingRedirects`: msbuild adds binding redirects to the config
    /// (`AutoGenerateBindingRedirects` and `GenerateBindingRedirectsOutputType`, .NET Framework
    /// and .NET alike); the recorded config is msbuild's finished one, redirects included (since
    /// 0.5.1; an older lock has the config before redirects).</item>
    /// </list>
    /// </summary>
    type App = {
        OutputType: string
        Host: AppHost option
        Files: AppFile list
        Copy: CopyItem list
        Content: CopyItem list
        Related: string list
        BindingRedirects: bool
    }

    /// What only an entry recorded from msbuild (`Import.record`) carries. Its presence is the
    /// marker the SBOM and a staleness check read ("msbuild" against "composed"): such an
    /// entry is msbuild's resolution written down, never Xake's. On disk its references,
    /// analyzers and compiler are identities (`Identity`), not paths.
    type Recorded = {
        /// The referenced projects (`project:<name>` references), in msbuild's order
        Projects: ProjectOutput list
        /// The description's source masks (absolute; `!` an exclude) when they name exactly the
        /// sources msbuild compiles, else empty. `Sources` holds msbuild's files in msbuild's
        /// order; a build compiles the files the masks name then -- msbuild's order first, a file
        /// added since after them (`Locks.compileAs`)
        Masks: string list
        /// With masks, how `Sources` is written: only the files the masks do not put in place
        /// -- the generated ones and msbuild's exceptions to the masks' order
        /// (`canonicalSources`) -- each with the file msbuild compiles before it ("" first).
        /// `None` in an entry an import just made (its `Sources` is msbuild's whole list, and
        /// `format` computes this, `placedSources`); `Some` in an entry read from a lock, whose
        /// `Sources` then holds the placed files only and a build reconstructs the rest from
        /// the masks (`reconstructSources`)
        Placed: (string * string) list option
        /// What msbuild makes of a project with runtime output besides the compile (`App`);
        /// `None` for a library
        App: App option
    }

    /// <summary>
    /// The order a recorded entry's masks put `files` in, as msbuild's globbing does: for each
    /// include mask in turn, the files it names that no earlier one did, sorted ordinally
    /// ignoring case; a file no include names last, in the given order. Pure.
    /// </summary>
    let canonicalSources (masks: string list) (files: string list) : string list =
        let remaining = System.Collections.Generic.HashSet<string> (files)
        [ for m in masks do
            if not (m.StartsWith "!") then
                let pattern = Xake.Path.parse (if m.EndsWith "**" then m + "/*" else m)
                let named =
                    files |> List.filter (fun f -> remaining.Contains f && Xake.Path.matchesPattern pattern f)
                    |> List.distinct |> List.sortWith (fun a b -> System.String.Compare (a, b, System.StringComparison.OrdinalIgnoreCase))
                for f in named do
                    remaining.Remove f |> ignore
                    yield f
          yield! files |> List.filter remaining.Contains |> List.distinct ]

    /// <summary>
    /// The `Placed` of a recorded entry whose `sources` are msbuild's whole list: the longest
    /// run of user sources already in the masks' order (`canonicalSources`) is left to the
    /// masks; every other source -- a generated one (`isGenerated`), or one msbuild puts
    /// elsewhere -- is placed after the source msbuild compiles before it ("" for the first).
    /// `reconstructSources` gives `sources` back. Pure.
    /// </summary>
    let placedSources (masks: string list) (isGenerated: string -> bool) (sources: string list) : (string * string) list =
        let user = sources |> List.filter (isGenerated >> not)
        let rank = canonicalSources masks user |> List.mapi (fun i f -> f, i) |> dict
        // the longest increasing run of ranks (patience sorting), as the set of its files
        let kept =
            let ranks = user |> List.map (fun f -> rank.[f]) |> Array.ofList
            let tails = ResizeArray<int> ()          // index into `ranks` of the tail of each length
            let previous = Array.create ranks.Length -1
            for i in 0 .. ranks.Length - 1 do
                let mutable lo, hi = 0, tails.Count
                while lo < hi do
                    let mid = (lo + hi) / 2
                    if ranks.[tails.[mid]] < ranks.[i] then lo <- mid + 1 else hi <- mid
                if lo > 0 then previous.[i] <- tails.[lo - 1]
                if lo = tails.Count then tails.Add i else tails.[lo] <- i
            let set = System.Collections.Generic.HashSet<string> ()
            let mutable i = if tails.Count = 0 then -1 else tails.[tails.Count - 1]
            while i >= 0 do
                set.Add (List.item i user) |> ignore
                i <- previous.[i]
            set
        sources |> List.mapi (fun i f -> f, (if i = 0 then "" else List.item (i - 1) sources))
        |> List.filter (fun (f, _) -> isGenerated f || not (kept.Contains f))

    /// <summary>
    /// The sources of a recorded entry from its masks' expansion now (`files`) and its `Placed`:
    /// the files no `placed` entry names, in the masks' order, then each placed file `keep`
    /// accepts (a generated one, one the masks still name) after the one msbuild compiled
    /// before it, at the end when that one is gone. A file added since takes its place in the
    /// masks' order; a deleted one is left out. Pure.
    /// </summary>
    let reconstructSources (masks: string list) (placed: (string * string) list) (keep: string -> bool) (files: string list) : string list =
        let placedFiles = placed |> List.map fst |> set
        let result = ResizeArray<string> (canonicalSources masks (files |> List.filter (placedFiles.Contains >> not)))
        for f, after in placed do
            if keep f then
                match after with
                | "" -> result.Insert (0, f)
                | a ->
                    match result.IndexOf a with
                    | -1 -> result.Add f
                    | i -> result.Insert (i + 1, f)
        List.ofSeq result

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
        /// `Some` for an entry recorded from msbuild (`Import.record`), `None` for one composed
        /// by Xake (`csc {}`, `Locks.generate`) or imported by `Project.import`
        Recorded: Recorded option
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
        { Compilation = c; Evaluation = Evaluation.Empty; Packages = []; Prerequisites = []; Recorded = None }

    /// A composed C# compilation as a lock entry (`ofCompilation`).
    let ofCsc (c: Csc) : Entry = ofCompilation (Compilation.Csc c)

    /// A composed F# compilation as a lock entry (`ofCompilation`).
    let ofFsc (f: Fsc) : Entry = ofCompilation (Compilation.Fsc f)

    /// `f` or `g` applied to the compilation, keeping its case.
    let private mapCompilation (fc: Csc -> Csc) (ff: Fsc -> Fsc) = function
        | Compilation.Csc c -> Compilation.Csc (fc c)
        | Compilation.Fsc f -> Compilation.Fsc (ff f)

    /// <summary>
    /// Whether `path` is a compiler of the .NET SDK itself: under
    /// <c>&lt;DotnetRoot&gt;/sdk/&lt;v&gt;/</c> (`Roots.dotnetRoot`), as the SDK's `fsc.dll` and
    /// an imported C# project's Roslyn `csc.dll` are. Those files are ReadyToRun images compiled
    /// for one OS and architecture, so the same SDK version has different bytes on macOS arm64
    /// and on linux x64. Their identity in a lock is the SDK version -- the version in the path
    /// and the `dotnet-sdk` prerequisite (`withPrerequisites`) -- not a hash: the lock records
    /// no hash for them (`rehash`) and a replay does not check one (`compileWith`).
    /// </summary>
    let isSdkCompiler (path: string) : bool =
        path <> "" && Roots.dotnetRoot () |> Option.exists (fun root -> (DotNetFwk.sdkVersionOf root path).IsSome)

    /// `entry` with the hash of an SDK compiler (`isSdkCompiler`) cleared; a compiler from a
    /// package (`Microsoft.Net.Compilers.Toolset`, `CSC_TOOLSET`) or any other path keeps its
    /// hash. Applied when recording (`rehash`) and before a replay, so a lock written before
    /// this rule (whose SDK compiler carries a hash) builds wherever the SDK version is present.
    let withoutSdkCompilerHash (entry: Entry) : Entry =
        let clear (d: Dependencies) =
            if d.Compiler.Sha256 <> "" && isSdkCompiler d.Compiler.Path then { d with Compiler = { d.Compiler with Sha256 = "" } }
            else d
        { entry with
            Compilation =
                entry.Compilation
                |> mapCompilation (fun c -> { c with Dependencies = clear c.Dependencies })
                                  (fun f -> { f with Fsc.Dependencies = clear f.Dependencies }) }

    /// <summary>
    /// The SDK versions a recorded entry (`Entry.Recorded`) depends on, read off its content:
    /// the version of every path under <c>&lt;dotnetRoot&gt;/sdk/&lt;v&gt;/</c> it names -- the
    /// compiler, a reference, an analyzer, any other file of the command line, a file an
    /// application copies -- and the entry's own SDK (`Evaluation.Sdk`) when it has an apphost:
    /// the SDK's template (`SdkHost`) or a pack's (`PackHost`), both written through the SDK's
    /// `HostModel` (without it the apphost lacks the version resource and the icon). A project on the toolset compiler
    /// (`Microsoft.Net.Compilers.Toolset`) and package analyzers
    /// (`Microsoft.CodeAnalysis.NetAnalyzers`) names nothing there: no version. Distinct, in
    /// order of first appearance. Pure.
    /// </summary>
    let recordedSdkVersions (dotnetRoot: string) (entry: Entry) : string list =
        let inputs =
            match entry.Compilation with
            | Compilation.Csc c -> Csc.hermeticInputs c
            | Compilation.Fsc f -> Fsc.hermeticInputs f
        let app = entry.Recorded |> Option.bind (fun r -> r.App)
        let paths =
            (inputs |> List.map (fun i -> i.Path))
            @ [ match app with
                | Some a ->
                    for c in a.Copy do yield c.Path
                    for c in a.Content do yield c.Path
                    for f in a.Files do yield f.Path
                    match a.Host with
                    | Some (PackHost (_, t)) -> yield t.Path
                    | _ -> ()
                | None -> () ]
        // an apphost, the SDK's template or a pack's, is written through the SDK's HostModel
        // (`Apps.createAppHost`: the version resource, the icon): the entry depends on that SDK
        let host =
            match app with
            | Some { Host = Some _ } when entry.Evaluation.Sdk <> "" -> [ entry.Evaluation.Sdk ]
            | _ -> []
        (paths |> List.filter ((<>) "") |> List.choose (DotNetFwk.sdkVersionOf dotnetRoot)) @ host |> List.distinct

    /// <summary>
    /// A recorded entry (`Entry.Recorded`) with its `dotnet-sdk` prerequisites derived from its
    /// content (`recordedSdkVersions` under `dotnetRoot`): one per SDK version it names, none
    /// when it names nothing under <c>sdk/</c>. A prerequisite the entry already has keeps its
    /// `Pin`; a new one gets `pin` (the tokenized `global.json`, "" when there is none).
    /// Other kinds of prerequisite are kept; an entry that is not recorded is returned as it
    /// is (a composed entry's prerequisite follows the `global.json` pin, `withPrerequisites`).
    /// Pure.
    /// </summary>
    let withRecordedPrerequisites (dotnetRoot: string) (pin: string) (entry: Entry) : Entry =
        match entry.Recorded with
        | None -> entry
        | Some _ ->
            let pinOf v =
                entry.Prerequisites
                |> List.tryFind (fun p -> p.Kind = DotnetSdk && p.Version = v)
                |> Option.map (fun p -> p.Pin)
                |> Option.defaultValue pin
            { entry with
                Prerequisites =
                    (entry.Prerequisites |> List.filter (fun p -> p.Kind <> DotnetSdk))
                    @ [ for v in recordedSdkVersions dotnetRoot entry -> { Kind = DotnetSdk; Version = v; Pin = pinOf v } ] }

    /// Fills `Sha256` for every hashed item (`References`, `Analyzers`, the compiler via
    /// `Csc.rehash`/`Fsc.rehash`, and `Imports`) from what is on disk right now; leaves `""` where the file
    /// does not exist, and for a compiler of the .NET SDK (`isSdkCompiler`, identified by the SDK
    /// version instead). For a composed entry, which leaves every hash empty, this is the
    /// "record time" step `lock-from-settings.md` recommendation 5 asks for -- run once by the
    /// lock-recording rule, not on every compile. A recorded entry's `dotnet-sdk` prerequisites
    /// are derived again from its paths (`withRecordedPrerequisites`, under this machine's .NET
    /// root): the prerequisite follows the content.
    let rehash (entry: Entry) : Entry =
        { entry with
            Compilation = entry.Compilation |> mapCompilation Csc.rehash Fsc.rehash
            Evaluation = { entry.Evaluation with Imports = entry.Evaluation.Imports |> List.map (fun h -> { h with Sha256 = FileHash.textSha256 h.Path }) } }
        |> withoutSdkCompilerHash
        |> fun e ->
            match e.Recorded, Roots.dotnetRoot () with
            | Some _, Some root -> withRecordedPrerequisites root "" e
            | _ -> e

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
          if ca.RuntimeConfig <> cb.RuntimeConfig then
              let text = Option.defaultValue "none"
              yield sprintf "~ RuntimeConfig: %s -> %s" (text ca.RuntimeConfig) (text cb.RuntimeConfig)
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

    /// <summary>
    /// The portable names a recorded entry (`Entry.Recorded`) gives the files msbuild resolved,
    /// in place of their paths on the machine that imported:
    /// <list type="bullet">
    /// <item><c>pkg:&lt;id&gt;/&lt;version&gt;/&lt;path&gt;</c> -- a file of a package in the
    /// package folder (`$(NuGetPackageRoot)`), id as the folder spells it (lower case);</item>
    /// <item><c>pack:&lt;pack&gt;/&lt;version&gt;/&lt;path&gt;</c> -- a file of a targeting
    /// pack under <c>$(DotnetRoot)/packs</c>; on a machine whose .NET has not that pack
    /// version it is the same file of the package of that name in the package folder (a
    /// targeting pack is a NuGet package, fetched like any other);</item>
    /// <item><c>sdk:&lt;path&gt;</c> -- a file of the .NET SDK the entry was recorded with
    /// (`Evaluation.Sdk`, a prerequisite of the entry): the compiler, the SDK's analyzers and
    /// shims;</item>
    /// <item><c>project:&lt;name&gt;</c> -- the output of a referenced project
    /// (`Recorded.Projects`).</item>
    /// </list>
    /// A path under one of the lock's roots (`$(ProjectRoot)`, a script's extra root) is the
    /// tokenized path, as in every lock. Anything else cannot be named portably and fails the
    /// import (`ofPath` gives `None`).
    /// </summary>
    module Identity =

        /// What naming and resolving needs: the roots of the lock (`$(NuGetPackageRoot)`,
        /// `$(DotnetRoot)`, `$(ProjectRoot)` and the extra ones), the SDK version of the entry
        /// and its referenced projects' outputs.
        type Context = { Roots: (string * string) list; Sdk: string; Projects: ProjectOutput list }

        let private comparison = if Env.isUnix then System.StringComparison.Ordinal else System.StringComparison.OrdinalIgnoreCase
        let private slash (p: string) = p.Replace ('\\', '/')
        let private rootOf (ctx: Context) token = ctx.Roots |> List.tryFind (fst >> (=) token) |> Option.map snd
        let private under (root: string) (path: string) =
            if path.StartsWith (root.TrimEnd '/' + "/", comparison) then Some (path.Substring (root.TrimEnd('/').Length + 1)) else None
        /// `<a>/<b>/<rest>` as (a, b, rest), when all three are non-empty.
        let private threeParts (rel: string) =
            match rel.Split ([| '/' |], 3) with
            | [| a; b; rest |] when a <> "" && b <> "" && rest <> "" -> Some (a, b, rest)
            | _ -> None

        /// <summary>
        /// Whether a package id names a pack of the .NET installation: a targeting pack
        /// (`Microsoft.<X>.App.Ref`), an apphost pack (`Microsoft.NETCore.App.Host.<rid>`) or a
        /// runtime pack (`Microsoft.<X>.App.Runtime.<rid>`), which an explicit runtime
        /// identifier brings. Its files get the same `pack:` identity whether this .NET has the
        /// pack under `packs/` or msbuild downloaded it into the package folder (the id in lower
        /// case, as the package folder spells it); the two copies need not have the same bytes
        /// (`toPathHashed`). Pure.
        /// </summary>
        let isPack (id: string) =
            let id = id.ToLowerInvariant ()
            id.EndsWith ".app.ref" || id.Contains ".app.host." || id.Contains ".app.runtime."

        /// The portable name of `path`: `project:`, `pack:` (a targeting pack under the .NET root
        /// or in the package folder), `pkg:`, `sdk:` (in that order),
        /// else the tokenized path when it lies under a root other than `$(DotnetRoot)`, else
        /// `None`. Pure.
        let ofPath (ctx: Context) (path: string) : string option =
            let path = slash path
            let project = ctx.Projects |> List.tryFind (fun p -> System.String.Equals (slash p.Path, path, comparison))
            let dotnet = rootOf ctx "$(DotnetRoot)"
            let inPacks = dotnet |> Option.bind (fun d -> under (d + "/packs") path) |> Option.bind threeParts
            let inPackages = rootOf ctx Roots.nugetPackageRootToken |> Option.bind (fun r -> under r path) |> Option.bind threeParts
            let inSdk =
                dotnet |> Option.bind (fun d -> under (d + "/sdk") path)
                |> Option.bind (fun rel ->
                    match rel.Split ([| '/' |], 2) with
                    | [| v; rest |] when v = ctx.Sdk && rest <> "" -> Some rest
                    | _ -> None)
            match project, inPacks, inPackages, inSdk with
            | Some p, _, _, _ -> Some ("project:" + p.Name)
            | _, Some (pack, version, rest), _, _ -> Some (sprintf "pack:%s/%s/%s" (pack.ToLowerInvariant ()) version rest)
            | _, _, Some (id, version, rest), _ when isPack id -> Some (sprintf "pack:%s/%s/%s" (id.ToLowerInvariant ()) version rest)
            | _, _, Some (id, version, rest), _ -> Some (sprintf "pkg:%s/%s/%s" id version rest)
            | _, _, _, Some rest -> Some ("sdk:" + rest)
            | _ ->
                let tokenized = Roots.tokenize ctx.Roots path
                if tokenized.StartsWith "$(" && not (tokenized.StartsWith "$(DotnetRoot)") then Some tokenized else None

        /// The folders of a .NET's `packs` (one listing per change of the folder, not one per
        /// `pack:` identity of every lock read).
        let private packListings = System.Collections.Concurrent.ConcurrentDictionary<string * System.DateTime, string[]> ()
        let private packFolders (packs: string) =
            packListings.GetOrAdd ((packs, Directory.GetLastWriteTimeUtc packs), fun _ -> Directory.GetDirectories packs)

        /// Whether `text` is a portable name `ofPath` writes for a file outside the roots.
        let isIdentity (text: string) =
            text.StartsWith "pkg:" || text.StartsWith "pack:" || text.StartsWith "sdk:" || text.StartsWith "project:"

        /// <summary>
        /// The two files a `pack:` identity can name on this machine: the file of the pack under
        /// `$(DotnetRoot)/packs` (`Some` when this .NET has it, whatever the folder's case) and
        /// the file of the package of that id and version in the package folder (which a restore
        /// fetches; it may not exist yet). Pure but for listing `packs/` and one `File.Exists`.
        /// </summary>
        let packCandidates (ctx: Context) (identity: string) : string option * string =
            let root token = match rootOf ctx token with Some r -> r | None -> failwithf "'%s': no %s on this machine" identity token
            match threeParts (identity.Substring "pack:".Length) with
            | Some (pack, version, file) ->
                let packs = root "$(DotnetRoot)" + "/packs"
                let installed =
                    if not (Directory.Exists packs) then None
                    else
                        packFolders packs
                        |> Array.tryFind (fun d -> System.String.Equals (Path.GetFileName d, pack, System.StringComparison.OrdinalIgnoreCase))
                        |> Option.map (fun d -> slash d + "/" + version + "/" + file)
                        |> Option.filter File.Exists
                installed, root Roots.nugetPackageRootToken + "/" + pack.ToLowerInvariant () + "/" + version.ToLowerInvariant () + "/" + file
            | None -> failwithf "'%s' is not pack:<pack>/<version>/<path>" identity

        /// The file `identity` names on this machine; a tokenized path is expanded. A `pack:`
        /// is the targeting pack under `$(DotnetRoot)/packs` when this .NET has that version,
        /// else the package of that name in the package folder (a restore fetches it). Pure but
        /// for that one `File.Exists`.
        let toPath (ctx: Context) (identity: string) : string =
            let root token = match rootOf ctx token with Some r -> r | None -> failwithf "'%s': no %s on this machine" identity token
            let rest (prefix: string) = identity.Substring prefix.Length
            if identity.StartsWith "pkg:" then
                root Roots.nugetPackageRootToken + "/" + rest "pkg:"
            elif identity.StartsWith "pack:" then
                match packCandidates ctx identity with
                | Some installed, _ -> installed
                | None, package -> package
            elif identity.StartsWith "sdk:" then
                if ctx.Sdk = "" then failwithf "'%s': the entry records no SDK version (Evaluation.Sdk)" identity
                root "$(DotnetRoot)" + "/sdk/" + ctx.Sdk + "/" + rest "sdk:"
            elif identity.StartsWith "project:" then
                let name = rest "project:"
                match ctx.Projects |> List.tryFind (fun p -> p.Name = name) with
                | Some p -> p.Path
                | None -> failwithf "'%s': the entry's Recorded.Projects has no '%s'" identity name
            else Roots.expand ctx.Roots identity

        /// <summary>
        /// `toPath` of an identity the lock records with the SHA-256 `sha`. A `pack:` is the
        /// candidate (`packCandidates`) whose content the lock recorded: the installed pack's
        /// file when this .NET has it and its hash is `sha` (or `sha` is ""), else the package
        /// folder's copy -- the installed pack and the nuget.org package of the same version are
        /// not byte-identical (the package signs its assemblies; facades and source generators
        /// are other builds), so a lock recorded from the package folder names the package's
        /// copy, which the lock's restore fetches when it is missing. When that copy does not
        /// match either, the replay names both (`packMismatches`). Hashes through `FileHash`
        /// (memoised by path, length and write time), so a set of locks naming one pack hashes
        /// each file once. Any other identity is `toPath`.
        /// </summary>
        let toPathHashed (ctx: Context) (identity: string) (sha: string) : string =
            if identity.StartsWith "pack:" then
                match packCandidates ctx identity with
                | Some installed, _ when sha = "" || FileHash.sha256 installed = sha -> installed
                | _, package -> package
            else toPath ctx identity

    /// The version of the lock format `format` writes. 2: an entry recorded from msbuild
    /// (`Recorded`) names its references, analyzers and compiler by identity (`Identity`).
    /// A lock without the field is format 1.
    [<Literal>]
    let FormatVersion = 2

    /// The `"Legacy"` value of a lock `Project.import` writes: format 1, its imported entries
    /// Xake's own resolution of the project (not msbuild's, as `Import.record`'s are). Read as
    /// before; a format-1 lock of an imported project without it (an older preview's) is not.
    [<Literal>]
    let LegacyMarker = "Project.import"

    open ToolJson

    /// The context `Identity` names a recorded entry's files with, under `roots`.
    let private identityContext roots (entry: Entry) : Identity.Context =
        { Roots = roots; Sdk = entry.Evaluation.Sdk
          Projects = entry.Recorded |> Option.map (fun r -> r.Projects) |> Option.defaultValue [] }

    /// The identity of a file a recorded entry names, or a failure saying the import cannot
    /// make it portable.
    let private identityOf roots (entry: Entry) (role: string) (path: string) =
        match Identity.ofPath (identityContext roots entry) path with
        | Some id -> id
        | None ->
            failwithf "'%s' (%s): the %s '%s' cannot be named portably: it is not in the package folder, the .NET packs or the SDK %s, nor under $(ProjectRoot) or a root the script declares; declare its folder as a root (Roots.make) or fix the project"
                entry.Name entry.Framework role path entry.Evaluation.Sdk

    let private writeEntry roots (entry: Entry) =
        let str = Roots.tokenizeAll roots >> escape
        let recorded = entry.Recorded.IsSome
        // a file of a recorded entry is named by its identity, any other entry's by its path
        let fileField role (path: string) =
            if recorded then sprintf "\"Id\": %s" (escape (identityOf roots entry role path))
            else sprintf "\"Path\": %s" (str path)
        let indent = "        "
        let strings name (items: string list) =
            items |> List.map (str >> sprintf "%s  %s" indent) |> String.concat ",\n"
            |> fun body -> sprintf "%s%s: [\n%s\n%s]" indent (escape name) body indent
        let hashedList name (items: Hashed list) =
            items
            |> List.map (fun h -> sprintf "%s  { \"Path\": %s, \"Sha256\": %s }" indent (str h.Path) (escape h.Sha256))
            |> String.concat ",\n"
            |> fun body -> sprintf "%s%s: [\n%s\n%s]" indent (escape name) body indent
        let analyzerList name (items: Hashed list) =
            items
            |> List.map (fun h -> sprintf "%s  { %s, \"Sha256\": %s }" indent (fileField "analyzer" h.Path) (escape h.Sha256))
            |> String.concat ",\n"
            |> fun body -> sprintf "%s%s: [\n%s\n%s]" indent (escape name) body indent
        let referenceList name (items: Reference list) =
            items
            |> List.map (fun r ->
                if r.Alias = "" then sprintf "%s  { %s, \"Sha256\": %s }" indent (fileField "reference" r.Path) (escape r.Sha256)
                else sprintf "%s  { %s, \"Sha256\": %s, \"Alias\": %s }" indent (fileField "reference" r.Path) (escape r.Sha256) (escape r.Alias))
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
        let pairsWith value name (items: (string * string) list) =
            items |> List.map (fun (k, v) -> sprintf "%s  %s: %s" indent (str k) (value v)) |> String.concat ",\n"
            |> fun body -> sprintf "%s%s: {\n%s\n%s}" indent (escape name) body indent
        let pairs = pairsWith str
        let section name (fields: string list) =
            fields |> String.concat ",\n" |> sprintf "      %s: {\n%s\n      }" (escape name)

        // the file keeps the sectioned shape: `Name`/`Framework`, then `Evaluation`, then the
        // compilation's fields as `Compilation`, and its dependencies together with the
        // entry's package graph as `Dependencies`
        let e, c, d = entry.Evaluation, entry.Compilation, entry.Compilation.Dependencies
        let placed =
            match entry.Recorded with
            | Some r when not (List.isEmpty r.Masks) ->
                match r.Placed with
                | Some p -> Some p
                | None ->
                    let generated = c.Generated |> List.map fst |> set
                    Some (placedSources r.Masks generated.Contains c.Sources)
            | _ -> None
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
              // with masks, only what the masks do not put in place (`Recorded.Placed`)
              strings "Sources" (match placed with Some p -> p |> List.map fst | None -> c.Sources)
              // the content of a generated source is text, not a path: its `\` stay
              pairsWith (Roots.tokenizeText roots >> escape) "Generated" c.Generated
              pairs "Resources" c.Resources
              // only when the rule declares one: every other lock stays byte-identical to what
              // was written before the key existed
              if c.RuntimeConfig.IsSome then
                  sprintf "%s\"RuntimeConfig\": %s" indent (str c.RuntimeConfig.Value) ]
          section "Dependencies"
            [ sprintf "%s\"Compiler\": { \"Tool\": %s, %s, \"Sha256\": %s, \"Version\": %s }" indent
                (escape d.Compiler.Tool) (fileField "compiler" d.Compiler.Path) (escape d.Compiler.Sha256) (escape d.Compiler.Version)
              referenceList "References" d.References
              analyzerList "Analyzers" d.Analyzers
              packageList "Packages" entry.Packages
              // only when there is one: every lock without prerequisites stays byte-identical
              // to what was written before the key existed
              if not (List.isEmpty entry.Prerequisites) then
                  entry.Prerequisites
                  |> List.map (fun p ->
                      sprintf "%s  { \"Kind\": %s, \"Version\": %s, \"Pin\": %s }" indent (escape p.Kind) (escape p.Version) (escape p.Pin))
                  |> String.concat ",\n"
                  |> fun body -> sprintf "%s\"Prerequisites\": [\n%s\n%s]" indent body indent ]
          // the marker of an entry recorded from msbuild, and where its project references are
          match entry.Recorded with
          | None -> ()
          | Some r ->
              section "Recorded"
                [ yield sprintf "%s\"By\": \"msbuild\"" indent
                  yield strings "Masks" r.Masks
                  match placed with
                  | Some p when not (List.isEmpty p) -> yield pairs "Placed" p
                  | Some _ -> yield sprintf "%s\"Placed\": {}" indent
                  | None -> ()
                  let projects =
                      r.Projects
                      |> List.map (fun p ->
                          sprintf "%s  { \"Name\": %s, \"Configuration\": %s, \"Framework\": %s, \"Path\": %s }"
                              indent (escape p.Name) (escape p.Configuration) (escape p.Framework) (str p.Path))
                      |> String.concat ",\n"
                  yield if projects = "" then sprintf "%s\"Projects\": []" indent else sprintf "%s\"Projects\": [\n%s\n%s]" indent projects indent ]
          // what msbuild makes of a project with runtime output besides the compile
          match entry.Recorded |> Option.bind (fun r -> r.App) with
          | None -> ()
          | Some a ->
              let listOf name (lines: string list) =
                  if List.isEmpty lines then sprintf "%s%s: []" indent (escape name)
                  else sprintf "%s%s: [\n%s\n%s]" indent (escape name) (lines |> List.map (sprintf "%s  %s" indent) |> String.concat ",\n") indent
              section "App"
                [ yield sprintf "%s\"OutputType\": %s" indent (escape a.OutputType)
                  match a.Host with
                  | None -> ()
                  | Some SdkHost -> yield sprintf "%s\"Host\": { \"Rid\": \"\" }" indent
                  | Some (PackHost (rid, t)) ->
                      yield sprintf "%s\"Host\": { \"Rid\": %s, %s, \"Sha256\": %s }" indent (escape rid) (fileField "apphost template" t.Path) (escape t.Sha256)
                  yield listOf "Files" [ for f in a.Files -> sprintf "{ \"Name\": %s, \"Path\": %s, \"Sha256\": %s }" (escape f.Name) (str f.Path) (escape f.Sha256) ]
                  yield listOf "Copy" [ for c in a.Copy -> sprintf "{ %s, \"Sha256\": %s, \"To\": %s }" (fileField "copy-local file" c.Path) (escape c.Sha256) (escape c.To) ]
                  yield listOf "Content" [ for c in a.Content -> sprintf "{ %s, \"Sha256\": %s, \"To\": %s }" (fileField "copy item" c.Path) (escape c.Sha256) (escape c.To) ]
                  yield listOf "Related" [ for r in a.Related -> escape r ]
                  yield sprintf "%s\"BindingRedirects\": %s" indent (if a.BindingRedirects then "true" else "false") ] ]
        |> String.concat ",\n" |> sprintf "    {\n%s\n    }"

    /// The lock as text: paths tokenized against the given roots, one line per item, so the
    /// file is the same on every machine and a diff of two locks is the difference between two
    /// compilations. `roots` is the full list (built-in plus any extra a script declared, e.g.
    /// via `Roots.make`), longest root first. Pure; `save` is the recipe that takes the roots
    /// from the build.
    let format roots (lock: Document) =
        // an entry of an imported project that msbuild did not record (`Project.import`: Xake's
        // own resolution of the project) is format 1, marked as such: never a format-2 lock
        let legacy = lock.Entries |> List.exists (fun e -> e.Evaluation.Project <> "" && e.Recorded.IsNone)
        [ if legacy then
              yield "  \"Format\": 1"
              yield sprintf "  \"Legacy\": %s" (escape LegacyMarker)
          else yield sprintf "  \"Format\": %d" FormatVersion
          yield sprintf "  \"Configuration\": %s" (escape lock.Configuration)
          yield sprintf "  \"Properties\": {\n%s\n  }"
                    // a value may be a path (`RestoreConfigFile`): tokenized as every path
                    (lock.Properties |> List.map (fun (k, v) -> sprintf "    %s: %s" (escape k) (escape (Roots.tokenizeAll roots v))) |> String.concat ",\n")
          yield sprintf "  \"Entries\": [\n%s\n  ]" (lock.Entries |> List.map (writeEntry roots) |> String.concat ",\n") ]
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
        // an entry recorded from msbuild: its files are identities, resolved for this machine
        let recorded =
            match field "Recorded" value with
            | Some (JObject _ as r) ->
                Some { Projects =
                         field "Projects" r |> Option.map asArray |> Option.defaultValue []
                         |> List.map (fun p ->
                             { Name = str "Name" p; Configuration = str "Configuration" p; Framework = str "Framework" p
                               Path = str "Path" p |> expand })
                       Masks = strings "Masks" r
                       Placed =
                         match field "Placed" r with
                         | Some (JObject members) -> Some (members |> List.choose (fun (k, v) -> asString v |> Option.map (fun v -> expand k, expand v)))
                         | _ -> None
                       App = None }
            | _ -> None
        let context : Identity.Context =
            { Roots = roots; Sdk = str "Sdk" e; Projects = recorded |> Option.map (fun r -> r.Projects) |> Option.defaultValue [] }
        // `Id` (an identity) or `Path` (tokenized), whichever the item carries
        let fileOf item =
            match field "Id" item |> Option.bind asString with
            | Some id when id <> "" -> Identity.toPathHashed context id (str "Sha256" item)
            | _ -> str "Path" item |> expand
        let recorded =
            match recorded, field "App" value with
            | Some r, Some (JObject _ as a) ->
                let items name = field name a |> Option.map asArray |> Option.defaultValue []
                let app =
                    { OutputType = str "OutputType" a
                      Host =
                        match field "Host" a with
                        | Some (JObject _ as h) ->
                            match str "Rid" h with
                            | "" -> Some SdkHost
                            | rid -> Some (PackHost (rid, { Path = fileOf h; Sha256 = str "Sha256" h }))
                        | _ -> None
                      Files = items "Files" |> List.map (fun f -> { Name = str "Name" f; Path = str "Path" f |> expand; Sha256 = str "Sha256" f })
                      Copy = items "Copy" |> List.map (fun c -> { Path = fileOf c; Sha256 = str "Sha256" c; To = str "To" c })
                      Content = items "Content" |> List.map (fun c -> { Path = fileOf c; Sha256 = str "Sha256" c; To = str "To" c })
                      Related = items "Related" |> List.choose asString
                      BindingRedirects = field "BindingRedirects" a |> Option.bind asBool |> Option.defaultValue false }
                Some { r with App = Some app }
            | _ -> recorded
        let hashedFiles name value =
            field name value |> Option.map asArray |> Option.defaultValue []
            |> List.map (fun h -> ({ Path = fileOf h; Sha256 = str "Sha256" h } : Hashed))
        let referenceList name value =
            field name value |> Option.map asArray |> Option.defaultValue []
            |> List.map (fun r -> { Path = fileOf r; Sha256 = str "Sha256" r; Alias = str "Alias" r })
        let compiler = field "Compiler" d |> Option.defaultValue (JObject [])
        let name = str "Name" value
        let framework = field "Framework" value |> Option.bind asString |> Option.defaultValue documentFramework
        let dependencies : Dependencies =
            { Compiler = { Tool = str "Tool" compiler; Path = fileOf compiler; Sha256 = str "Sha256" compiler; Version = str "Version" compiler }
              References = referenceList "References" d
              Analyzers = hashedFiles "Analyzers" d }
        // absent in every lock written before the key existed, and whenever the rule declares
        // no runtimeconfig
        let runtimeConfig = field "RuntimeConfig" c |> Option.bind asString |> Option.filter ((<>) "") |> Option.map expand
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
                      Fsc.RuntimeConfig = runtimeConfig
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
                      RuntimeConfig = runtimeConfig
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
            Recorded = recorded
        }

    /// Reads a lock `format` produced, paths expanded for this machine. `roots` must be the
    /// same list (or a superset) used to write it, or a token stays untranslated. A lock in
    /// the flat, pre-split format (`Projects` with a verbatim `Args`) is refused with a message
    /// saying to re-import. A lock written when the framework was a property of the *file* (a
    /// document-level `"Framework"`, one framework per lock) still reads: the value is
    /// distributed into every entry that does not carry its own. Only the new shape is ever
    /// written. Pure; `load` is the recipe that takes the roots from the build.
    let parse roots (text: string) =
        let root = ToolJson.parse text
        let str name = field name root |> Option.bind asString |> Option.defaultValue ""
        if (field "Projects" root).IsSome && (field "Entries" root).IsNone then
            failwith "lock written by an older Xake (flat format with 'Projects'/'Args'); re-import"
        let format =
            match field "Format" root with
            | Some (JOther n) -> (match System.Int32.TryParse n with | true, v -> v | _ -> failwithf "lock format '%s' is not a number" n)
            | None -> 1
            | Some _ -> failwith "lock format: expected a number"
        if format > FormatVersion then
            failwithf "lock format %d is newer than this Xake.Hermetic.Dotnet reads (%d); update the package" format FormatVersion
        // an imported project's entry in format 1 is Xake's own resolution of the project, not
        // msbuild's: read only from a lock `Project.import` marks as such (`LegacyMarker`); the
        // entries composed by Xake, which no import wrote, are read in any format
        let entries = field "Entries" root |> Option.map asArray |> Option.defaultValue []
        let isImported e = field "Evaluation" e |> Option.bind (field "Project") |> Option.bind asString |> Option.exists ((<>) "")
        let legacy = field "Legacy" root |> Option.bind asString |> Option.exists ((=) LegacyMarker)
        if format < FormatVersion && not legacy && entries |> List.exists isImported then
            failwithf "lock format %d is older than %d: its imported entries are Xake's own resolution of the project, not msbuild's; regenerate the lock (run the import again: Import.record)" format FormatVersion
        if format = FormatVersion && entries |> List.exists (fun e -> isImported e && (field "Recorded" e).IsNone) then
            failwithf "lock format %d holds an imported entry msbuild did not record (no 'Recorded'); regenerate the lock (run the import again: Import.record)" format
        {
            Configuration = str "Configuration"
            Properties =
                match field "Properties" root with
                | Some (JObject members) -> members |> List.choose (fun (k, v) -> asString v |> Option.map (fun v -> k, Roots.expand roots v))
                | _ -> []
            Entries = field "Entries" root |> Option.map asArray |> Option.defaultValue [] |> List.map (readEntry roots (str "Framework"))
        }

    let private parsedLocks =
        System.Collections.Concurrent.ConcurrentDictionary<string * int64 * System.DateTime * (string * string) list, Document> ()

    /// `parse` of the file at `path` (taken as given, not against the project root), with
    /// the roots passed explicitly -- for code outside a recipe. Inside one use `load`, which
    /// also records the lock as a dependency.
    let read roots (path: string) =
        try
            // a lock is read by several rules of one build (needAll, each compile, the doc
            // rule): parsed once per content and roots
            let info = FileInfo path
            let key = (info.FullName, info.Length, info.LastWriteTimeUtc, roots)
            match parsedLocks.TryGetValue key with
            | true, doc -> doc
            | _ ->
                let doc = System.IO.File.ReadAllText path |> parse roots
                parsedLocks.[key] <- doc
                doc
        with e when e.Message.StartsWith "lock format" -> failwithf "'%s': %s" path e.Message

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
    /// fetched). `Restore` is applied as the build says (`Restore.resolve`): with
    /// `Options.Default` the packages live in the build's package folder (the script variable
    /// `NUGET_PACKAGES` first) and are fetched unless `NUGET_FETCH` turns fetching off.
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
    /// Whether this build runs under CI: the build variable `CI` (read, and so a dependency,
    /// here): `on`/`true`/`yes`/`1` is CI, `off`/`false`/`no`/`0`, empty or unset is not,
    /// case-insensitive; anything else fails. Same convention as `HERMETIC` and `NUGET_FETCH`.
    /// The environment variable `CI` that GitHub Actions, GitLab CI, Azure Pipelines and most
    /// other services set counts only through the script: <c>var "CI" [Env "CI"]</c>, or
    /// `-d CI=on`. Under CI, `buildWith` (and so `build` and `csc { lock }`) fails on a missing
    /// lock instead of recording it.
    /// </summary>
    let underCi : Recipe<ExecContext, bool> =
        recipe {
            let! variable = Var.read<string> "CI"
            match variable |> Option.map (fun v -> v.Trim ()) with
            | None | Some "" -> return false
            | Some v ->
                match v.ToLowerInvariant () with
                | "on" | "true" | "yes" | "1" -> return true
                | "off" | "false" | "no" | "0" -> return false
                | _ -> return failwithf "CI='%s': expected on or off" v
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
                for a in d.Analyzers do yield a.Path
                // an application's copy-local packages and its apphost template (explicit RID)
                match e.Recorded |> Option.bind (fun r -> r.App) with
                | Some app ->
                    for c in app.Copy do if c.Sha256 <> "" then yield c.Path
                    for c in app.Content do if c.Sha256 <> "" then yield c.Path
                    match app.Host with
                    | Some (PackHost (_, t)) -> yield t.Path
                    | _ -> ()
                | None -> () ] }

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

    /// The prerequisites of `entry` the .NET installation at `root` does not meet, as
    /// messages: a `dotnet-sdk` one whose <c>&lt;root&gt;/sdk/&lt;Version&gt;</c> folder is
    /// missing. `root` is the `$(DotnetRoot)` the lock is read against (a script's extra root
    /// of that name, else `Roots.dotnetRoot ()`). Reads the disk, nothing else.
    let internal unmetPrerequisitesAt (root: string option) (entry: Entry) : string list =
        [ for p in entry.Prerequisites do
            if p.Kind = DotnetSdk then
                let installed = root |> Option.exists (fun r -> Directory.Exists (Path.Combine (r, "sdk", p.Version)))
                if not installed then
                    let rootText = root |> Option.map (fun r -> r.Replace('\\', '/')) |> Option.defaultValue "<no .NET installation found>"
                    yield sprintf "'%s': the lock needs the .NET SDK %s (prerequisite from %s), which is not installed under '%s'; install it (dotnet-install --version %s --install-dir %s)"
                            entry.Name p.Version (if p.Pin = "" then "the import" else p.Pin) rootText p.Version rootText ]

    /// The compilation's inputs as `HERMETIC=on` sees them (`Csc.hermeticInputs`,
    /// `Fsc.hermeticInputs`): the compiler, references, analyzers and every other path.
    let hermeticInputs (c: Compilation) : HermeticMode.Input list =
        match c with
        | Compilation.Csc c -> Csc.hermeticInputs c
        | Compilation.Fsc f -> Fsc.hermeticInputs f

    /// <summary>
    /// The paths of `entry`, tokenized against `roots` (the roots its lock is written with),
    /// that break the `HERMETIC=on` invariant on the lock side: outside `$(ProjectRoot)` (and
    /// any extra root a script declared, `Roots.make`) and `$(NuGetPackageRoot)`, and not
    /// covered by one of the entry's prerequisites. A <c>dotnet-sdk &lt;v&gt;</c> prerequisite
    /// covers <c>$(DotnetRoot)/sdk/&lt;v&gt;/</c> -- fsc's compiler always, an imported
    /// entry's compiler and analyzers (msbuild chose them), and for an entry recorded from
    /// msbuild (`Recorded`) every file under it, references included. A file under
    /// <c>$(DotnetRoot)/packs/</c> is accepted only in a recorded entry, by its hash (a
    /// `pack:` identity). The comparison itself is `HermeticMode.check` over the tokenized
    /// roots. Pure.
    /// </summary>
    let hermeticViolations roots (entry: Entry) : string list =
        let builtinOther = set [ Roots.nugetPackageRootToken; "$(DotnetRoot)" ]
        let hermeticRoots : HermeticMode.Roots = {
            ProjectRoots = "$(ProjectRoot)" :: (roots |> List.map fst |> List.filter (fun t -> t <> "$(ProjectRoot)" && not (builtinOther.Contains t)))
            PackageRoot = Roots.nugetPackageRootToken
            // the package folder's own requirement (message 2) is the caller's
            PackageRootDeclared = true
            DotnetRoot = Some "$(DotnetRoot)"
            // what covers an SDK path is the entry's prerequisites, not the pin found now
            SdkPin = DotNetFwk.NoGlobalJson
            SdkInstalled = fun _ -> true
            TargetingPackVersion = fun _ -> None
        }
        let imported = entry.Evaluation.Project <> ""
        let sdks = entry.Prerequisites |> List.filter (fun p -> p.Kind = DotnetSdk) |> List.map (fun p -> p.Version) |> Set.ofList
        let recorded = entry.Recorded.IsSome
        // a recorded entry names a targeting pack by identity and hash (`pack:`): the hash,
        // not the folder, is what pins it
        let hashedPack (path: string) =
            let hashed =
                [ for r in entry.Dependencies.References do if r.Sha256 <> "" then yield r.Path
                  for a in entry.Dependencies.Analyzers do if a.Sha256 <> "" then yield a.Path ]
            recorded && path.StartsWith "$(DotnetRoot)/packs/"
            && (hashed |> List.exists (fun p -> Roots.tokenize roots p = path))
        let covered (input: HermeticMode.Input) =
            let role =
                match input.Role with
                | HermeticMode.Compiler "fsc" -> true
                | HermeticMode.Compiler _ | HermeticMode.Analyzer -> imported
                // msbuild chose every file of a recorded entry: the SDK's shims and
                // configuration files are the SDK prerequisite's as much as its compiler is
                | _ -> recorded
            (role && (DotNetFwk.sdkVersionOf "$(DotnetRoot)" input.Path |> Option.exists sdks.Contains))
            || hashedPack input.Path
        hermeticInputs entry.Compilation
        |> List.map (fun i -> { i with Path = Roots.tokenize roots i.Path })
        |> List.filter (fun i -> not (covered i))
        |> List.filter (fun i -> not (List.isEmpty (HermeticMode.check entry.Name hermeticRoots [ i ])))
        |> List.map (fun i -> i.Path)
        |> List.distinct

    /// Message 9 of the hermetic mode: refusing to write a lock that names `path` (tokenized).
    let hermeticRecordMessage (name: string) (lockPath: string) (path: string) =
        sprintf "'%s': HERMETIC=on: refusing to write the lock '%s': %s is outside $(ProjectRoot) and $(NuGetPackageRoot), and no prerequisite covers it"
            name lockPath path

    /// Message 10 of the hermetic mode: a lock (at `lockPath`, when known) that names `path`
    /// (tokenized) is not replayed.
    let hermeticReplayMessage (name: string) (lockPath: string option) (path: string) =
        let lock = match lockPath with Some p -> sprintf "the lock '%s'" p | None -> "the lock"
        sprintf "'%s': HERMETIC=on: %s names %s, outside $(ProjectRoot) and $(NuGetPackageRoot) and not covered by a prerequisite; re-record it with HERMETIC=on"
            name lock path

    /// <summary>
    /// The record-time gate (`record`, the recording branch of `buildWith`, `Project.import`):
    /// under `HERMETIC=on`, every reason not to write the lock at `lockPath` with `entries`,
    /// tokenized against `roots` -- the package folder not being the build's own (message 2,
    /// `DotNetFwk.packageRootWithSource`), then one line per offending path (message 9,
    /// `hermeticViolations`). Empty, and reading nothing but `HERMETIC`, with the mode off.
    /// </summary>
    let hermeticRecordGate (lockPath: string) roots (entries: Entry list) : Recipe<ExecContext, string list> =
        recipe {
            let! on = HermeticMode.enabled ()
            match on, entries with
            | false, _ | _, [] -> return []
            | true, first :: _ ->
                let! _, source = DotNetFwk.packageRootWithSource ()
                let package =
                    if source = DotNetFwk.ScriptVariable then []
                    else
                        // the base's own message 2, by asking its check with nothing but an
                        // undeclared folder
                        HermeticMode.check first.Name
                            { ProjectRoots = []; PackageRoot = ""; PackageRootDeclared = false; DotnetRoot = None
                              SdkPin = DotNetFwk.NoGlobalJson; SdkInstalled = (fun _ -> true); TargetingPackVersion = (fun _ -> None) }
                            []
                return
                    package
                    @ [ for e in entries do
                          for path in hermeticViolations roots e -> hermeticRecordMessage e.Name lockPath path ]
        }

    /// <summary>
    /// The analyzers a compile's diagnostics report as crashed (`AD0001`: "Analyzer 'X' threw
    /// an exception of type 'Y'"), as `X (Y)`; empty when there is none. `text` is what csc
    /// printed (`error AD0001: Analyzer ...`) or a SARIF error log (`"ruleId": "AD0001"`).
    /// </summary>
    let internal analyzerCrashes (text: string) : string list =
        if not (text.Contains "AD0001") then []
        else
            System.Text.RegularExpressions.Regex.Matches (text, "Analyzer '([^']+)' threw an exception of type '([^']+)'")
            |> Seq.cast<System.Text.RegularExpressions.Match>
            |> Seq.map (fun m -> sprintf "%s (%s)" m.Groups.[1].Value m.Groups.[2].Value)
            |> Seq.distinct
            |> List.ofSeq

    /// <summary>
    /// `Csc.run` under the compiler server, retried once without it when the compile fails
    /// with an analyzer crash (`AD0001`, S72: seen once in a large solution's clean build, an
    /// analyzer's NullReferenceException in a shared `VBCSCompiler`; the next build of the
    /// same project passed). The crash is named from what the failed compile printed (a
    /// crash that fails the compile is an error, and csc prints every error, at any
    /// verbosity), so the compile
    /// itself carries no `/errorlog:`: an error log makes csc report every diagnostic,
    /// hidden and suppressed ones included, and costs a quarter to a half more compiler CPU
    /// on each compile (0.4.2). A failure that names a crashed analyzer is reported with the
    /// analyzer and compiled again in a fresh compiler process. Any other failure fails as
    /// before, and so does a crash without the server.
    /// </summary>
    let private runCsc (run: RunOptions) (c: Csc) : Recipe<ExecContext, unit> =
        // the lines the compile logs that name AD0001 (the compiler's output goes to the
        // target's logger, `shell`), collected on the side; the log itself is unchanged
        let crashLines = ResizeArray<string> ()
        let collecting (Recipe inner: Recipe<ExecContext, unit>) =
            Recipe (fun (r, ctx: ExecContext) ->
                let tee = Logging.CustomLogger (fun _ -> true) (fun line -> if line.Contains "AD0001" then lock crashLines (fun () -> crashLines.Add line))
                inner (r, { ctx with Logger = Logging.CombineLogger ctx.Logger tee }))
        recipe {
            match run.Server with
            | InProcess -> do! Csc.run run c
            | Shared _ when not run.FailOnError -> do! Csc.run run c
            | Shared _ ->
                let crashed = ref []
                try
                    do! collecting (Csc.run run c)
                with e ->
                    crashed.Value <- lock crashLines (fun () -> analyzerCrashes (String.concat "\n" crashLines))
                    if List.isEmpty crashed.Value then raise e
                if not (List.isEmpty crashed.Value) then
                    do! trace Warning "('%s') an analyzer crashed under the compiler server (AD0001): %s; compiling once more without the server"
                            c.Name (String.concat ", " crashed.Value)
                    do! Csc.run { run with Server = InProcess } c
        }

    /// <summary>
    /// The pack files (`Identity.isPack`) of a recorded entry's references and analyzers that
    /// the replay takes from the package folder `packageRoot` and whose SHA-256 is not the
    /// recorded one, as messages naming both candidates of the `pack:` identity and their hashes:
    /// the installed pack under `dotnetRoot` (when this .NET has the file) and the package's
    /// copy. Run after the restore, so the package's copy is there. Reads the disk.
    /// </summary>
    let internal packMismatches (dotnetRoot: string option) (packageRoot: string) (entry: Entry) : string list =
        if entry.Recorded.IsNone then [] else
        let packageRoot = packageRoot.Replace('\\', '/').TrimEnd '/'
        let context : Identity.Context =
            { Roots = [ yield Roots.nugetPackageRootToken, packageRoot
                        match dotnetRoot with Some r -> yield "$(DotnetRoot)", r.Replace('\\', '/').TrimEnd '/' | None -> () ]
              Sdk = ""; Projects = [] }
        let d = entry.Dependencies
        [ for path, sha in [ for r in d.References -> r.Path, r.Sha256 ] @ [ for a in d.Analyzers -> a.Path, a.Sha256 ] do
            if sha <> "" && File.Exists path then
                match Nuget.packageOf packageRoot path with
                | Some (id, _) when Identity.isPack id ->
                    let actual = FileHash.sha256 path
                    if actual <> sha then
                        let identity = "pack:" + (path.Replace('\\', '/').Substring (packageRoot.Length + 1))
                        let installed =
                            match dotnetRoot with
                            | Some _ ->
                                match Identity.packCandidates context identity with
                                | Some i, _ -> sprintf "'%s' (%s)" i (FileHash.sha256 i)
                                | None, _ -> "not installed under packs/"
                            | None -> "no .NET installation"
                        yield sprintf "'%s': %s matches neither copy the lock may name (recorded %s): the installed pack %s, the package '%s' (%s)"
                                entry.Name identity sha installed path actual
                | _ -> () ]

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
    ///     an F# one; the runner's hash check makes the replay trustworthy. A compiler of the
    ///     .NET SDK is not hash-checked (`withoutSdkCompilerHash`): step 0 and its path pin
    ///     its version.
    /// </summary>
    let private compileEntry (extraRoots: (string * string) list) (lockPath: string option) (options: Options) (entry: Entry) : Recipe<ExecContext, unit> =
        recipe {
            // an SDK compiler is identified by the SDK version (the path, the prerequisite),
            // never by its bytes: a hash an older lock carries for it is ignored
            let entry = withoutSdkCompilerHash entry
            let c = entry.Compilation
            // the build's package folder and NUGET_FETCH, for `Options.Default` (`Restore.resolve`)
            let! restore = Restore.resolve options.Restore
            let options = { options with Restore = restore }

            // the prerequisites first: nothing a restore fetches can stand in for a missing SDK;
            // the .NET installation is the one the lock is read against (an extra root
            // `$(DotnetRoot)` of the script, else this machine's)
            let! readRoots = Roots.currentWith (extraRoots |> List.filter (fst >> (=) "$(DotnetRoot)"))
            let dotnetRoot =
                match extraRoots |> List.exists (fst >> (=) "$(DotnetRoot)"), readRoots |> List.tryFind (fst >> (=) "$(DotnetRoot)") with
                | true, Some (_, root) -> Some root
                | _ -> Roots.dotnetRoot ()
            match unmetPrerequisitesAt dotnetRoot entry with
            | [] -> ()
            | unmet -> do! failStep options c (unmet |> String.concat "\n")

            // HERMETIC=on: a lock naming a path outside the roots, and not covered by a
            // prerequisite, is not replayed (message 10) -- a hard failure, like the base's gate
            let! hermetic = HermeticMode.enabled ()
            if hermetic then
                // the script's extra roots (sibling checkouts) count as project roots; the
                // package folder is the one the restore uses
                let extra = extraRoots |> List.filter (fst >> (<>) Roots.nugetPackageRootToken)
                let! roots = Roots.currentWith (extra @ Roots.packageRootOverride (Restore.packageRoot restore))
                match hermeticViolations roots entry with
                | [] -> ()
                | paths ->
                    let msg = paths |> List.map (hermeticReplayMessage c.Name lockPath) |> String.concat "\n"
                    do! trace Error "%s" msg
                    failwith msg

            let! restoreProblems = Restore.ensure options.Restore (restoreRequest [entry])
            if not (List.isEmpty restoreProblems) then
                do! failStep options c
                        (sprintf "('%s') restoring the packages the lock names failed:\n%s"
                            c.Name (restoreProblems |> String.concat "\n"))

            // a pack file whose copy in the package folder (fetched now if it was missing) is not
            // the recorded one either: both candidates named
            match packMismatches dotnetRoot (Restore.packageRoot options.Restore) entry with
            | [] -> ()
            | mismatches -> do! failStep options c (mismatches |> String.concat "\n")

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
            | Compilation.Csc c -> do! runCsc options.Run c
            | Compilation.Fsc f -> do! Fsc.run options.FscRun f
        }

    /// <summary>
    /// `compileEntry` of an entry whose lock file is not known here; see the steps above.
    /// Under `HERMETIC=on` an entry naming a path outside `$(ProjectRoot)` and
    /// `$(NuGetPackageRoot)` that no prerequisite covers fails before anything else is done
    /// (`hermeticViolations`, message 10). `options.Restore` is applied as the build says
    /// (`Restore.resolve`: the build's package folder, `NUGET_FETCH`).
    /// </summary>
    let compileWith (options: Options) (entry: Entry) : Recipe<ExecContext, unit> =
        compileEntry [] None options entry

    /// `compileWith` of an entry read from the lock at `lockPath`, the path the `HERMETIC=on`
    /// refusal (message 10) names -- what `buildWith` does with a matching lock, for a caller
    /// that read the lock itself (`Locks.compileAs`).
    let compileFrom (lockPath: string) (options: Options) (entry: Entry) : Recipe<ExecContext, unit> =
        compileEntry [] (Some lockPath) options entry

    /// `compileFrom` of a lock read with the script's extra roots (`loadWith extraRoots`): a
    /// path under one of them counts as under the project root for `HERMETIC=on` (message 10).
    let compileFromWith (extraRoots: (string * string) list) (lockPath: string) (options: Options) (entry: Entry) : Recipe<ExecContext, unit> =
        compileEntry extraRoots (Some lockPath) options entry

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

    /// <summary>
    /// The record-time step of `record` without the write: `entry` hashed (`rehash`) and
    /// with its prerequisites (`withPrerequisites`), refused under `HERMETIC=on` when
    /// incomplete (`hermeticRecordGate`, messages 2 and 9; `path` is the lock it is meant for,
    /// quoted in the messages). The references whose path is in `unhashed` -- the outputs of
    /// project references, which a lock keeps without a hash (`Locks.generate`) -- are
    /// neither depended on nor hashed, whether or not a build left them on disk. `extraRoots`
    /// are the script's (`Roots.make`): a path under one counts as under the project root for
    /// the gate.
    /// </summary>
    let recordedWith (extraRoots: (string * string) list) (path: string) (unhashed: string list) (entry: Entry) : Recipe<ExecContext, Entry> =
        recipe {
            let isUnhashed (p: string) = List.contains p unhashed
            // what gets hashed is what the recorded lock depends on: a target that only
            // records (no `Csc.run`, which holds the compile-side `needFiles`) must still
            // rerun when a reference, analyzer, compiler or import changes
            let hashedPaths =
                [ for r in entry.Dependencies.References do if not (isUnhashed r.Path) then yield r.Path
                  for a in entry.Dependencies.Analyzers -> a.Path
                  yield entry.Dependencies.Compiler.Path
                  for i in entry.Evaluation.Imports -> i.Path ]
                |> List.filter File.Exists
            do! needFiles (Filelist (hashedPaths |> List.map File.make))
            let! entry = withPrerequisites true entry
            // HERMETIC=on: a lock that is not complete is not written (messages 2 and 9); a
            // path under an extra root of the script counts as under the project root
            let! roots = Roots.currentWith extraRoots
            let! refused = hermeticRecordGate path roots [ entry ]
            if not (List.isEmpty refused) then
                let msg = refused |> String.concat "\n"
                do! trace Error "%s" msg
                failwith msg
            let rehashed = rehash entry
            if List.isEmpty unhashed then return rehashed else
            let clear (r: Reference) = if isUnhashed r.Path then { r with Sha256 = "" } else r
            let deps (d: Dependencies) = { d with References = d.References |> List.map clear }
            return
                { rehashed with
                    Compilation =
                        rehashed.Compilation
                        |> mapCompilation (fun c -> { c with Dependencies = deps c.Dependencies })
                                          (fun f -> { f with Fsc.Dependencies = deps f.Dependencies }) }
        }

    /// `recordedWith` without extra roots.
    let recorded (path: string) (unhashed: string list) (entry: Entry) : Recipe<ExecContext, Entry> =
        recordedWith [] path unhashed entry

    /// Writes `entry`, hashed (`rehash`, the record-time step) and with its prerequisites
    /// (`withPrerequisites`), as a one-entry lock at `path` and returns what was written. There
    /// is no msbuild configuration or property set behind a composed compilation, so those stay
    /// empty in the document. Under `HERMETIC=on` an incomplete entry is not written
    /// (`hermeticRecordGate`, messages 2 and 9).
    let private recordEntry (path: string) (entry: Entry) =
        recipe {
            let! full = fullPath path
            let dir = Path.GetDirectoryName full
            if dir <> "" then Directory.CreateDirectory dir |> ignore
            let! rehashed = recorded path [] entry
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
    /// Nothing is compiled here. Under `HERMETIC=on` a lock naming a path outside
    /// `$(ProjectRoot)` and `$(NuGetPackageRoot)` that no prerequisite covers, or recorded
    /// without the script variable `NUGET_PACKAGES`, is refused (`hermeticRecordGate`).
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
    /// Under `HERMETIC=on` recording refuses an incomplete lock (`hermeticRecordGate`) and
    /// compiling refuses a lock that names a path outside the roots (`compileWith`).
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
                do! compileEntry [] (Some path) options (forCompile recorded)
            else
                let! doc = load full
                let recorded = entry c.Name doc
                let! resolved = withPrerequisites false (ofCompilation c)
                match diffText roots recorded resolved with
                | [] ->
                    // compile the recorded entry, not the resolved one: its hashes are what
                    // gate the build
                    do! compileEntry [] (Some path) options (forCompile recorded)
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
