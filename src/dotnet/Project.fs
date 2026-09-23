namespace Xake.Dotnet

open System.IO

open Xake
open Xake.Tasks

/// The C# compiler's command line, as data. The import takes the exact invocation msbuild
/// would have made and the compile step replays it, so both sides need to know which of the
/// arguments name files: those get made absolute and tokenized when the lock is written, and
/// they are the inputs the compile step depends on.
module CscArgs =

    /// One argument: a source file, or a switch with its (possibly empty) value.
    type Arg =
        | Source of string
        | Switch of name: string * value: string

    /// `/reference:a.dll` -> Switch ("reference", "a.dll"); the name is lowercased and the
    /// `+`/`-` of a boolean switch stays with it (`/optimize+` -> Switch ("optimize+", "")).
    let parse (arg: string) =
        // an absolute Unix path also starts with '/': it has a second slash before any colon
        let head = match arg.IndexOf ':' with | -1 -> arg | i -> arg.Substring (0, i)
        if arg.Length > 1 && arg.[0] = '/' && head.IndexOf ('/', 1) < 0 then
            match arg.IndexOf ':' with
            | -1 -> Switch (arg.Substring(1).ToLowerInvariant(), "")
            | i -> Switch (arg.Substring(1, i - 1).ToLowerInvariant(), arg.Substring (i + 1))
        else Source arg

    let private aliases =
        [ "r", "reference"; "a", "analyzer"; "res", "resource"; "linkres", "linkresource"
          "l", "link"; "d", "define"; "lib", "lib" ] |> Map.ofList

    /// Switch names with their aliases folded.
    let canonical name = aliases |> Map.tryFind name |> Option.defaultValue name

    /// How a switch's value is shaped, for the ones whose value names files.
    type private Shape =
        /// one path
        | Path
        /// comma-separated paths; a reference may carry an `alias=` prefix
        | PathList
        /// `path,name,...` -- only the first element is a path
        | PathFirst

    let private shapes =
        [ "out", Path; "doc", Path; "keyfile", Path; "ruleset", Path; "sourcelink", Path
          "pdb", Path; "refout", Path; "win32icon", Path; "win32res", Path; "win32manifest", Path
          "appconfig", Path; "generatedfilesout", Path; "touchedfiles", Path; "analyzerconfig", Path
          "reference", PathList; "analyzer", PathList; "additionalfile", PathList; "embed", PathList
          "link", PathList; "addmodule", PathList; "lib", PathList
          "resource", PathFirst; "linkresource", PathFirst; "errorlog", PathFirst ]
        |> Map.ofList

    /// Switches that name the compiler's inputs, with the value's file path(s).
    let private inputSwitches =
        set [ "reference"; "analyzer"; "additionalfile"; "embed"; "link"; "addmodule"; "keyfile"
              "ruleset"; "analyzerconfig"; "resource"; "linkresource"; "win32icon"; "win32res"
              "win32manifest"; "appconfig"; "sourcelink" ]

    let private outputSwitches = set [ "out"; "doc"; "refout"; "pdb"; "errorlog"; "generatedfilesout"; "touchedfiles" ]

    /// Splits a switch value on ',' the way csc itself does: a comma inside a `"..."` quoted
    /// segment does not split (msbuild quotes a path in a list when the path itself contains a
    /// comma, e.g. the generated `.NETStandard,Version=vX.Y.AssemblyAttributes.cs` a netstandard
    /// project embeds); the quotes themselves are not part of the path and are dropped.
    let internal splitList (value: string) =
        let items = ResizeArray<string>()
        let current = System.Text.StringBuilder()
        let mutable inQuotes = false
        for ch in value do
            match ch with
            | '"' -> inQuotes <- not inQuotes
            | ',' when not inQuotes -> items.Add (current.ToString()); current.Clear() |> ignore
            | c -> current.Append c |> ignore
        items.Add (current.ToString())
        List.ofSeq items

    /// Re-quotes a path list item if joining it back with ',' would make it split again where
    /// it did not before -- the inverse of `splitList`.
    let internal quoteIfNeeded (s: string) = if s.Contains "," then "\"" + s + "\"" else s

    /// A `/reference:` (etc.) list item may be `alias=path`: the alias is a short identifier
    /// with no path separator in it, always at the very start, so an `=` is only the alias
    /// marker when it comes before the first `/` -- a bare path may itself contain an `=` after
    /// one (e.g. the generated `.NETStandard,Version=vX.Y.AssemblyAttributes.cs`, once its
    /// comma-hiding quotes are gone).
    let internal aliasSplitIndex (p: string) =
        match p.IndexOf '=' with
        | -1 -> -1
        | i -> match p.IndexOf '/' with | slash when slash >= 0 && slash < i -> -1 | _ -> i

    /// Paths named by one argument.
    let paths arg =
        match arg with
        | Source path -> [path]
        | Switch (name, value) ->
            match shapes |> Map.tryFind (canonical name) with
            | Some Path -> [value]
            | Some PathList ->
                splitList value |> List.filter ((<>) "")
                |> List.map (fun p -> match aliasSplitIndex p with | -1 -> p | i -> p.Substring (i + 1))
            | Some PathFirst -> [(splitList value).[0]]
            | None -> []

    /// Rewrites every path an argument names, leaving the rest of it as it is.
    let mapPaths (f: string -> string) arg =
        match arg with
        | Source path -> Source (f path)
        | Switch (name, value) ->
            match shapes |> Map.tryFind (canonical name) with
            | Some Path -> Switch (name, f value)
            | Some PathList ->
                let mapped =
                    splitList value |> List.map (fun p ->
                        if p = "" then p else
                        (match aliasSplitIndex p with
                         | -1 -> f p
                         | i -> p.Substring (0, i + 1) + f (p.Substring (i + 1)))
                        |> quoteIfNeeded)
                Switch (name, System.String.Join (",", mapped))
            | Some PathFirst ->
                let parts = splitList value |> List.mapi (fun i p -> if i = 0 then quoteIfNeeded (f p) else p)
                Switch (name, System.String.Join (",", parts))
            | None -> arg

    let format = function
        | Source path -> path
        | Switch (name, "") -> "/" + name
        | Switch (name, value) -> sprintf "/%s:%s" name value

    /// The files a command line reads: the sources and everything an input switch names.
    let inputs (args: string list) =
        args |> List.map parse |> List.collect (function
            | Source path -> [path]
            | Switch (name, _) as arg when inputSwitches.Contains (canonical name) -> paths arg
            | _ -> [])

    /// The files a command line writes.
    let outputs (args: string list) =
        args |> List.map parse |> List.collect (function
            | Switch (name, _) as arg when outputSwitches.Contains (canonical name) -> paths arg
            | _ -> [])

    let sources (args: string list) =
        args |> List.choose (parse >> function | Source path -> Some path | _ -> None)

    let switchValues name (args: string list) =
        args |> List.choose (parse >> function
            | Switch (n, _) as arg when canonical n = name -> Some (paths arg)
            | _ -> None) |> List.concat

    /// Makes every path relative to `dir` absolute, with forward slashes.
    let absolutize (dir: string) (args: string list) =
        let absolute (p: string) =
            let p = p.Replace ('\\', '/')
            if p = "" then p
            else System.IO.Path.GetFullPath (System.IO.Path.Combine (dir, p)) |> fun p -> p.Replace ('\\', '/')
        args |> List.map (parse >> mapPaths absolute >> format)

/// The current commit of a project's repository, read directly from `.git` -- no `git`
/// executable. Two uses: `Project.import` needs the files that name the current commit as
/// inputs (so a new commit re-imports), and `run` (`Dotnet.csc.fs`) needs the sha itself, to
/// resolve `$(SourceRevisionId)` at compile time.
module Git =

    /// The `.git` entry above (or at) `dir`, walking up -- stops at the filesystem root. A
    /// normal checkout has a `.git` directory; a linked worktree has a `.git` file instead
    /// (`gitdir: <path>`).
    let rec private findGitEntry (dir: string) : string option =
        let candidate = Path.Combine (dir, ".git")
        if Directory.Exists candidate || File.Exists candidate then Some candidate
        else
            match Path.GetDirectoryName (dir: string) with
            | null | "" -> None
            | parent when parent = dir -> None
            | parent -> findGitEntry parent

    /// The directory that holds `HEAD` (`gitDir`) and the one refs are resolved against
    /// (`commonDir`) -- the same directory for an ordinary repository, but for a linked
    /// worktree `gitDir` is the worktree's own private directory
    /// (`<repo>/.git/worktrees/<name>`) while `commonDir` -- named by its `commondir` file --
    /// is the main repository's `.git`, where `refs/` and `packed-refs` actually live.
    let private resolveGitDir (gitEntry: string) : (string * string) option =
        if Directory.Exists gitEntry then
            let commondirFile = Path.Combine (gitEntry, "commondir")
            let common =
                if File.Exists commondirFile then
                    let rel = (File.ReadAllText commondirFile).Trim()
                    if rel = "" then gitEntry else Path.GetFullPath (Path.Combine (gitEntry, rel))
                else gitEntry
            Some (gitEntry, common)
        elif File.Exists gitEntry then
            let content = (File.ReadAllText gitEntry).Trim()
            let prefix = "gitdir:"
            if content.StartsWith prefix then
                let target = content.Substring(prefix.Length).Trim()
                let gitDir =
                    if Path.IsPathRooted target then target
                    else Path.GetFullPath (Path.Combine (Path.GetDirectoryName (gitEntry: string), target))
                let commondirFile = Path.Combine (gitDir, "commondir")
                let common =
                    if File.Exists commondirFile then
                        let rel = (File.ReadAllText commondirFile).Trim()
                        if rel = "" then gitDir else Path.GetFullPath (Path.Combine (gitDir, rel))
                    else gitDir
                Some (gitDir, common)
            else None
        else None

    /// A ref name (`refs/heads/main`) resolved against `packed-refs`, when it is not (or no
    /// longer) a loose ref file. Lines are `<sha> <refname>`; a `#`-prefixed line is the
    /// header, and a following `^<sha>` line (a peeled annotated tag) never matches a ref name
    /// so it is skipped implicitly.
    let private fromPackedRefs (commonDir: string) (refName: string) : string option =
        let packedRefs = Path.Combine (commonDir, "packed-refs")
        if not (File.Exists packedRefs) then None
        else
            File.ReadAllLines packedRefs
            |> Array.tryPick (fun line ->
                let line = line.Trim()
                if line = "" || line.StartsWith "#" || line.StartsWith "^" then None
                else
                    match line.Split ' ' with
                    | [| sha; name |] when name = refName -> Some sha
                    | _ -> None)

    /// The files that name the repository's current commit: `HEAD`, and -- when `HEAD` is a
    /// symbolic ref -- the loose ref file it points to when that exists, or `packed-refs` when
    /// the ref is only there. Empty when `dir` is not inside a git repository. These are the
    /// files `Project.import` `needFiles`s so that a new commit re-imports the project.
    let headFiles (dir: string) : string list =
        match findGitEntry dir |> Option.bind resolveGitDir with
        | None -> []
        | Some (gitDir, commonDir) ->
            let headPath = Path.Combine (gitDir, "HEAD")
            if not (File.Exists headPath) then [] else
            let headContent = (File.ReadAllText headPath).Trim()
            let prefix = "ref:"
            if headContent.StartsWith prefix then
                let refName = headContent.Substring(prefix.Length).Trim()
                let refPath = Path.Combine (commonDir, refName.Replace ('/', Path.DirectorySeparatorChar))
                if File.Exists refPath then [ headPath; refPath ]
                else
                    let packedRefs = Path.Combine (commonDir, "packed-refs")
                    if File.Exists packedRefs then [ headPath; packedRefs ] else [ headPath ]
            else [ headPath ]

    /// The repository's current commit sha, or `None` when `dir` is not inside a git
    /// repository (or `HEAD` cannot be resolved). Pure filesystem reads, no `git` executable.
    let headSha (dir: string) : string option =
        match findGitEntry dir |> Option.bind resolveGitDir with
        | None -> None
        | Some (gitDir, commonDir) ->
            let headPath = Path.Combine (gitDir, "HEAD")
            if not (File.Exists headPath) then None else
            let headContent = (File.ReadAllText headPath).Trim()
            let prefix = "ref:"
            if headContent.StartsWith prefix then
                let refName = headContent.Substring(prefix.Length).Trim()
                let refPath = Path.Combine (commonDir, refName.Replace ('/', Path.DirectorySeparatorChar))
                if File.Exists refPath then
                    match (File.ReadAllText refPath).Trim() with
                    | "" -> None
                    | sha -> Some sha
                else fromPackedRefs commonDir refName
            else
                match headContent with
                | "" -> None
                | sha -> Some sha

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
        Evaluation: Evaluation
        Compilation: Compilation
        Dependencies: Dependencies
    } with
        /// The exact command line, paths absolute (rebuilt from `Compilation` and
        /// `Dependencies`; the import verifies it equals what msbuild reported)
        member this.Args = Compilation.args this.Compilation this.Dependencies.References this.Dependencies.Analyzers
        member this.Sources = this.Compilation.Sources
        member this.Output = CscArgs.switchValues "out" this.Compilation.Options |> List.tryHead

    /// One lock file: every project of one (framework, variant).
    type Document = {
        Framework: string
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

    let private diffCompiler (a: Compiler) (b: Compiler) : string list =
        [ if a.Path <> b.Path then sprintf "~ Compiler.Path: %s -> %s" a.Path b.Path
          if a.Sha256 <> b.Sha256 then sprintf "~ Compiler.Sha256: %s -> %s" a.Sha256 b.Sha256
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

    /// Human-readable differences between two lock entries of the same project: `Options` and
    /// `Sources` as ordered lists (`diffList`, bare `+`/`-` lines), `Defines` as a set,
    /// `Compiler` (path, hash, version), `Evaluation.Sdk`, each hashed list (`References`,
    /// `Analyzers`, `Imports`) by path, `Generated`/`Resources` by key, `ProjectRefs` as a
    /// set, `Packages` by id. Empty list means identical. Pure, deterministic order (fixed
    /// section order, sorted within each section save the two ordered ones).
    let diff (a: Entry) (b: Entry) : string list =
        [ yield! diffList a.Compilation.Options b.Compilation.Options
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
        [ sprintf "  \"Framework\": %s" (escape lock.Framework)
          sprintf "  \"Configuration\": %s" (escape lock.Configuration)
          sprintf "  \"Properties\": {\n%s\n  }"
            (lock.Properties |> List.map (fun (k, v) -> sprintf "    %s: %s" (escape k) (escape v)) |> String.concat ",\n")
          sprintf "  \"Entries\": [\n%s\n  ]" (lock.Entries |> List.map (writeEntry roots) |> String.concat ",\n") ]
        |> String.concat ",\n" |> sprintf "{\n%s\n}\n"

    let private readEntry roots value =
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
    /// saying to re-import.
    let parseWith roots (text: string) =
        let root = Json.parse text
        let str name = field name root |> Option.bind asString |> Option.defaultValue ""
        if (field "Projects" root).IsSome && (field "Entries" root).IsNone then
            failwith "lock written by an older Xake (flat format with 'Projects'/'Args'); re-import"
        {
            Framework = str "Framework"
            Configuration = str "Configuration"
            Properties =
                match field "Properties" root with
                | Some (JObject members) -> members |> List.choose (fun (k, v) -> asString v |> Option.map (fun v -> k, v))
                | _ -> []
            Entries = field "Entries" root |> Option.map asArray |> Option.defaultValue [] |> List.map (readEntry roots)
        }

    let readWith roots (path: string) = System.IO.File.ReadAllText path |> parseWith roots

    /// Reads a lock against the roots of this build: the built-in three, with the project root
    /// taken from the engine (`ExecOptions.ProjectRoot`) rather than from the process's current
    /// directory -- which is why this is a recipe and `readWith` is not.
    let load (path: string) : Recipe<ExecContext, Document> =
        recipe {
            let! roots = Roots.current
            return readWith roots path
        }

    /// `load` with extra roots declared by the script (the ones its `Project.import` used, see
    /// `ImportOptions.Roots`).
    let loadWith (extraRoots: (string * string) list) (path: string) : Recipe<ExecContext, Document> =
        recipe {
            let! roots = Roots.currentWith extraRoots
            return readWith roots path
        }

    /// Writes a lock against this build's roots (see `load`).
    let save (path: string) (lock: Document) : Recipe<ExecContext, unit> =
        recipe {
            let! roots = Roots.current
            System.IO.File.WriteAllText (path, writeWith roots lock)
        }

    /// `save` with extra roots declared by the script.
    let saveWith (extraRoots: (string * string) list) (path: string) (lock: Document) : Recipe<ExecContext, unit> =
        recipe {
            let! roots = Roots.currentWith extraRoots
            System.IO.File.WriteAllText (path, writeWith roots lock)
        }

    /// The entry for one project, by assembly name or by project file name.
    let entry (name: string) (lock: Document) =
        lock.Entries |> List.tryFind (fun e ->
            e.Name = name || System.IO.Path.GetFileNameWithoutExtension e.Evaluation.Project = name)
        |> function
            | Some e -> e
            | None -> failwithf "project '%s' is not in the lock (%s)" name (lock.Entries |> List.map (fun e -> e.Name) |> String.concat ", ")

/// Imports a C# project the way Visual Studio learns a project's compiler switches: a
/// design-time build in which the compiler task is asked to report its command line instead
/// of running (`ProvideCommandLineArgs`, `SkipCompilerExecution`). Nothing about the
/// compilation is reconstructed; what msbuild would have run is what the lock holds.
module Project =

    open Lock

    /// Walks up from `projectDir` looking for `global.json` and reads its `sdk.version` /
    /// `sdk.rollForward` into a `Lock.SdkPin`. Pure: no msbuild involved.
    let sdkPin (projectDir: string) : SdkPin =
        let rec findGlobalJson (dir: string) =
            let path = Path.Combine (dir, "global.json")
            if File.Exists path then Some path
            else
                match Path.GetDirectoryName (dir: string) with
                | null | "" -> None
                | parent when parent = dir -> None
                | parent -> findGlobalJson parent
        match findGlobalJson (Path.GetFullPath projectDir) with
        | None -> NoGlobalJson
        | Some file ->
            let root = File.ReadAllText file |> Json.parse
            let sdk = Json.field "sdk" root
            match sdk |> Option.bind (Json.field "version") |> Option.bind Json.asString with
            | None -> NoVersion file
            | Some version ->
                match sdk |> Option.bind (Json.field "rollForward") |> Option.bind Json.asString with
                | Some "disable" -> Pinned version
                | Some policy -> RollsForward (version, policy)
                | None -> RollsForward (version, "latestPatch")

    type ImportOptions = {
        /// The project files. All of them land in one lock: what varies between projects of
        /// one framework and variant is small next to what they share
        Projects: string list
        /// Target framework the import runs for
        Framework: string
        Configuration: string
        /// What else selects the compilation, e.g. `["Brand", "MESCIUS"]`. Each distinct set
        /// needs its own `Variant` so the generated files do not overwrite each other's
        Properties: (string * string) list
        /// Names the obj subtree the import generates into (`obj/xake/<framework>/<variant>/`);
        /// the brand, typically. Empty when the framework alone selects the compilation
        Variant: string
        /// The lock file to write
        Output: string
        /// Extra roots to tokenize paths against, beyond the built-in three (`$(NuGetPackageRoot)`,
        /// `$(ProjectRoot)`, `$(DotnetRoot)`) -- one token per sibling repository, e.g.
        /// `["$(DataEngineRoot)", "/abs/path/to/dataengine"]` when a `Projects` entry or a
        /// project reference resolves outside `$(ProjectRoot)` (the current directory). See
        /// `Roots.withExtra`.
        Roots: (string * string) list
    } with static member Default = {
            Projects = []
            Framework = ""
            Configuration = "Release"
            Properties = []
            Variant = ""
            Output = ""
            Roots = []
        }

    /// Serializes msbuild runs of one project file: two concurrent imports (different lock
    /// outputs, e.g. one rule per (framework, brand)) of the *same* project both `-restore`
    /// into that project's shared `obj/project.assets.json` (and `obj/*.nuget.g.*`) -- only
    /// `IntermediateOutputPath` is per-variant above, not `BaseIntermediateOutputPath` -- so
    /// when the package set depends on a property like `Brand`, one import can read the
    /// other's restore output and record the wrong references. A process-wide `Resource` of
    /// quantity 1 per normalized project path serializes the two msbuild runs of that project
    /// (`-restore` design-time build, then `-pp`); a different project path gets its own
    /// `Resource`, so unrelated projects still import in parallel. This is `Resource` /
    /// `withResource` (`docs/delegated.md`) used exactly as documented for a script -- a
    /// `Resource` is just a value and `withResource` is the recipe-level bracket, so a library
    /// recipe can create and use one without any script-side declaration. The wait yields the
    /// CPU slot (`withResource` does this already), so a blocked import never pins a worker
    /// thread.
    module private ProjectLocks =
        let private comparer = if Env.isUnix then System.StringComparer.Ordinal else System.StringComparer.OrdinalIgnoreCase
        let private locks = System.Collections.Concurrent.ConcurrentDictionary<string, Resource> (comparer)
        let private key (project: string) = (Path.GetFullPath project).Replace ('\\', '/')
        let resourceFor (project: string) : Resource =
            locks.GetOrAdd (key project, fun k -> Resource.newResource k 1)

    /// Runs `body` exclusively with respect to every other import of the same project file
    /// (by full path, OS-appropriate comparison); a different project file imports
    /// concurrently. `internal` so `ProjectImportTests.fs` can drive it directly, without a
    /// real msbuild, through a small `xake {}` engine.
    let internal withProjectLock (project: string) (body: Recipe<ExecContext, 'a>) : Recipe<ExecContext, 'a> =
        withResource (ProjectLocks.resourceFor project) 1 body

    /// One property from a design-time build's result dump (the `-getResultOutputFile` json
    /// `wantedProperties` asks for), read directly rather than through `parseImport`'s full
    /// parse -- `ProjectAssetsFile` and `NuGetPackageRoot` are needed early, inside the
    /// project lock, right after the design-time build, so the restore graph is read before a
    /// concurrent import of the same project (once the lock is released) restores over the
    /// shared `obj/project.assets.json`.
    let internal readDumpProperty (dumpFile: string) (name: string) : string option =
        let root = File.ReadAllText dumpFile |> Json.parse
        Json.field "Properties" root
        |> Option.bind (Json.field name)
        |> Option.bind Json.asString
        |> Option.filter ((<>) "")

    /// The restore graph of one target, as the lock records it: every package of
    /// `assets.Packages` with its cache sha512 (`Nuget.readCache`, "" when the cache has no
    /// `.nupkg.metadata`), whether it is a direct `PackageReference`, and the ids it depends
    /// on (edges of `assets.Graph` whose source is this package). Pure but for reading the
    /// cache's metadata files.
    let packages (cacheRoot: string) (assets: Nuget.Assets) : Lock.Package list =
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

    /// `PrepareResources` runs resgen so the `/resource:` switches name real files;
    /// `Compile` (not `CoreCompile`) so that everything hooked before it -- generated
    /// assembly attributes, `BeforeCompile` extensions -- has run.
    let internal targets = "PrepareResources;Compile"
    let internal items = "CscCommandLineArgs,ReferencePath,Analyzer,ProjectReference,EmbeddedResource"
    let internal wantedProperties =
        "AssemblyName,MSBuildProjectFullPath,MSBuildProjectDirectory,IntermediateOutputPath,BaseIntermediateOutputPath,TargetPath," +
        "CscToolPath,CscToolExe,CSharpCoreTargetsPath,RoslynTargetsPath,NETCoreSdkVersion,NetCoreRoot,NuGetPackageRoot,ProjectAssetsFile," +
        "TargetFrameworkMoniker,LangVersion,Version,InformationalVersion,SignAssembly,AssemblyOriginatorKeyFile,Deterministic,SourceRevisionId"

    /// Replaces every occurrence of `sha` in the entry's `Generated` content, `Options`,
    /// `Defines` and evaluation `Properties` values with the literal token
    /// `$(SourceRevisionId)`. `sourcelink.json` (and, in principle, any other generated text)
    /// embeds the commit that produced it -- SourceLink's own doing, not this tool's -- which
    /// would otherwise make the lock's content, and so the lock file itself, change on every
    /// commit even though the compilation it describes did not. The sha itself is
    /// deliberately not recorded anywhere in the lock; `run` (`Dotnet.csc.fs`) resolves the
    /// token back from the project's repository right before it would be used. Pure; a no-op
    /// when `sha` is empty.
    let tokenizeRevision (sha: string) (entry: Lock.Entry) : Lock.Entry =
        if sha = "" then entry else
        let token = "$(SourceRevisionId)"
        entry |> Lock.mapText (fun s -> if s.Contains sha then s.Replace (sha, token) else s)

    /// Every file msbuild imported, from a preprocessed project (`-pp`): each import is
    /// announced by a banner with the file's path on the line above a rule of `=`.
    let internal parseImports (preprocessed: string) =
        let lines = preprocessed.Split '\n' |> Array.map (fun l -> l.TrimEnd '\r')
        [ for i in 0 .. lines.Length - 2 do
            let line = lines.[i].Trim()
            let next = lines.[i + 1].Trim()
            if line <> "" && next.Length > 10 && next |> Seq.forall ((=) '=')
               && not (line.StartsWith "<") && Path.IsPathRooted line then
                yield line ]
        |> List.distinct

    /// Builds the lock entry from what msbuild wrote. `pin` is the project's SDK pin (from
    /// `sdkPin`) and `packages` the restore graph (from `packages`), both computed separately
    /// so this function does no file walking of its own beyond the msbuild result and the
    /// files the command line names. Fails when the command line rebuilt from the structured
    /// entry (`Entry.Args`) is not exactly msbuild's -- the fidelity guarantee of brief §8c,
    /// checked here rather than trusted.
    let internal parseImport (resultFile: string) (imports: string list) (pin: SdkPin) (packages: Lock.Package list) =
        let root = File.ReadAllText resultFile |> Json.parse
        let items name =
            Json.field "Items" root |> Option.bind (Json.field name)
            |> Option.map Json.asArray |> Option.defaultValue []
        let identity item = Json.field "Identity" item |> Option.bind Json.asString |> Option.defaultValue ""
        let fullPath item =
            Json.field "FullPath" item |> Option.bind Json.asString |> Option.defaultValue (identity item)
        let metadata name item = Json.field name item |> Option.bind Json.asString |> Option.defaultValue ""
        let properties =
            match Json.field "Properties" root with
            | Some (Json.JObject members) ->
                members |> List.choose (fun (name, value) -> Json.asString value |> Option.map (fun v -> name, v)) |> Map.ofList
            | _ -> Map.empty
        let prop name = properties |> Map.tryFind name |> Option.defaultValue ""

        let directory = (prop "MSBuildProjectDirectory").Replace ('\\', '/')
        let args = items "CscCommandLineArgs" |> List.map identity |> CscArgs.absolutize directory
        if List.isEmpty args then
            failwithf "'%s': the design-time build reported no compiler command line -- CoreCompile did not run (skipped as up to date, or the project has no C# compile step)" (prop "MSBuildProjectFullPath")
        let slash (p: string) = p.Replace ('\\', '/')

        // A project that pins the compiler via the `Microsoft.Net.Compilers.Toolset` package
        // does not set `CscToolPath`/`CscToolExe` -- that package only redirects
        // `CSharpCoreTargetsPath` (and the `Csc` task's assembly) to its own `tasks/<tfm>/`
        // directory, and the task's `ToolPath` defaults to a `bincore` folder next to whatever
        // targets file is driving it. So `CSharpCoreTargetsPath`'s own directory (not
        // `RoslynTargetsPath`, which the SDK always reports as its own Roslyn regardless of a
        // toolset override) is what actually tells the SDK csc.dll from the package's: for an
        // unpinned project it is `<sdk>/Roslyn/Microsoft.CSharp.Core.targets`, so this produces
        // the exact same path the old `RoslynTargetsPath </> "bincore" </> "csc.dll"` fallback
        // did; for a pinned one it is
        // `<nuget>/microsoft.net.compilers.toolset/<version>/build/../tasks/netcore/Microsoft.CSharp.Core.targets`,
        // so this resolves to the package's own compiler.
        let compilerPath =
            match prop "CscToolPath" with
            | "" ->
                match prop "CSharpCoreTargetsPath" with
                | "" -> prop "RoslynTargetsPath" </> "bincore" </> "csc.dll"
                | csTargets ->
                    // the package's path goes through `build/../tasks`: folded, so the lock
                    // names one spelling of the file
                    Path.GetFullPath (Path.GetDirectoryName (slash csTargets) </> "bincore" </> "csc.dll")
            | toolPath -> toolPath </> (match prop "CscToolExe" with | "" -> "csc.dll" | exe -> exe)
            |> slash

        let absoluteDir (dir: string) =
            let dir = slash dir
            (if Path.IsPathRooted dir then dir else Path.GetFullPath (Path.Combine (directory, dir)) |> slash).TrimEnd '/' + "/"
        let intermediate = prop "IntermediateOutputPath" |> absoluteDir
        // where restore writes its props and targets
        let baseIntermediate = (match prop "BaseIntermediateOutputPath" with | "" -> "obj/" | dir -> dir) |> absoluteDir

        // `PrepareResources` compiles every resx `EmbeddedResource` and records where: prefer
        // `OutputResource` (`GenerateResource`'s own, exact, relative to the project directory)
        // and fall back to `IntermediateOutputPath + ManifestResourceName + ".resources"` for
        // an older SDK that does not set it. Non-resx embedded resources are passed as plain
        // files on the `/resource:` switch and need nothing recorded here.
        let resources =
            items "EmbeddedResource"
            |> List.filter (fun item -> (identity item).ToLowerInvariant().EndsWith ".resx")
            |> List.map (fun item ->
                // `FullPath`, a well-known item metadata, is unreliable here under the msbuild
                // CLI's `-getItem`: seen live on an `<EmbeddedResource Update="...">` item
                // (page's `Properties\Resources.resx`, resolved via the SDK's default-items
                // glob), its `FullPath` came back resolved against this *process's* current
                // directory instead of the project's own directory -- `RootDir` and `Directory`
                // in the same metadata bag were equally off. `Identity` combined with
                // `directory` (a *property*, not well-known item metadata, and not subject to
                // this) is reliable, the same combine `OutputResource` already gets below.
                let identityPath = identity item |> slash
                let resx =
                    if Path.IsPathRooted identityPath then identityPath
                    else Path.GetFullPath (Path.Combine (directory, identityPath)) |> slash
                let output =
                    match metadata "OutputResource" item with
                    | "" -> intermediate + metadata "ManifestResourceName" item + ".resources"
                    | outputResource ->
                        let outputResource = slash outputResource
                        if Path.IsPathRooted outputResource then outputResource
                        else Path.GetFullPath (Path.Combine (directory, outputResource)) |> slash
                resx, output)
        let resourceOutputs = resources |> List.map snd |> Set.ofList

        // msbuild-generated *text* inputs (assembly attributes, the derived .editorconfig):
        // the compiled .resources files are binary and already tracked, separately, in
        // `resources` -- reading one with `File.ReadAllText` here would corrupt it (and `run`
        // would then write the mangled text back over the real file).
        let generated =
            CscArgs.inputs args
            |> List.filter (fun path -> path.StartsWith intermediate && File.Exists path && not (resourceOutputs.Contains path))
            |> List.map (fun path -> path, File.ReadAllText path)

        // an import under the SDK is the SDK version, recorded with the compiler; one under
        // obj is restore's, regenerated by the import itself. The rest are the evaluation's
        // inputs: the project, the Directory.Build files, package build files
        let sdkRoot = (prop "NetCoreRoot" |> slash).TrimEnd '/'
        let imports =
            imports |> List.map slash
            |> List.filter (fun path -> not (sdkRoot <> "" && path.StartsWith (sdkRoot + "/")) && not (path.StartsWith baseIntermediate))

        let compilation, references, analyzers = Lock.Compilation.ofArgs args
        let entry : Lock.Entry = {
            Name = prop "AssemblyName"
            Evaluation =
                { Project = prop "MSBuildProjectFullPath" |> slash
                  ProjectRefs = items "ProjectReference" |> List.map (fullPath >> slash)
                  Imports = imports |> List.map Lock.hashed
                  Sdk = prop "NETCoreSdkVersion"
                  SdkPin = Some pin
                  Properties =
                    properties |> Map.filter (fun name _ ->
                        List.contains name [ "AssemblyName"; "TargetFrameworkMoniker"; "LangVersion"; "Version"; "InformationalVersion"
                                             "SignAssembly"; "AssemblyOriginatorKeyFile"; "Deterministic"; "TargetPath"; "IntermediateOutputPath" ]) }
            Compilation =
                { compilation with
                    Directory = directory
                    Generated = generated
                    Resources = resources }
            Dependencies =
                { Compiler = { Tool = "csc"; Path = compilerPath; Sha256 = Lock.sha256 compilerPath; Version = Lock.compilerVersion compilerPath }
                  References = references |> List.map (fun r -> { r with Sha256 = Lock.sha256 r.Path })
                  Analyzers = analyzers |> List.map (fun a -> Lock.hashed a.Path)
                  Packages = packages }
        }
        // the round trip: the structured entry must give back msbuild's command line exactly,
        // or the lock would describe a compilation other than the one msbuild ran
        let rebuilt = entry.Args
        if rebuilt <> args then
            failwithf "'%s': the command line rebuilt from the lock entry differs from msbuild's (structured form lost fidelity):\n%s"
                entry.Name (Lock.diffList args rebuilt |> String.concat "\n")
        // SourceLink's `sourcelink.json` (captured above, now that `sourcelink` is an input
        // switch) embeds the commit msbuild resolved via `SourceRevisionId` -- tokenize it out
        // so the lock's content, hence the lock file, does not change on every commit
        match prop "SourceRevisionId" with
        | "" -> entry
        | sha -> tokenizeRevision sha entry

    /// <summary>
    /// Imports the projects and writes the lock. Make it the recipe of a file rule over the
    /// lock: msbuild then runs only when a project file or one of the files it imports
    /// changed.
    /// </summary>
    let import (options: ImportOptions) =

        recipe {
            let variantDir = if options.Variant = "" then "" else options.Variant + "/"
            let properties =
                [ "Configuration", options.Configuration
                  "TargetFramework", options.Framework
                  // the compiler reports its command line and does not run
                  "ProvideCommandLineArgs", "true"
                  "SkipCompilerExecution", "true"
                  // resolving a project reference must not build it
                  "BuildProjectReferences", "false"
                  // the generated files are per (framework, variant): brands sharing one obj
                  // overwrite each other's assembly attributes
                  "IntermediateOutputPath", sprintf "obj/xake/%s/%s" options.Framework variantDir
                  // the audit talks to the feeds and its warnings turn fatal under
                  // TreatWarningsAsErrors; it is not the import's business
                  "NuGetAudit", "false"
                  // CoreCompile lists this property among its Outputs (Visual Studio's own
                  // design-time trick): a file that never exists keeps the target from being
                  // skipped as up to date when the assembly in obj/xake is newer than the
                  // sources -- skipped, it reports no command line at all
                  "NonExistentFile", "__NonExistentSubDir__/__NonExistentFile__" ]
                @ options.Properties

            let switches = [for name, value in properties -> sprintf "-p:%s=%s" name value]

            do! needFiles (Filelist (options.Projects |> List.map File.make))
            Directory.CreateDirectory (Path.GetDirectoryName (Path.GetFullPath options.Output)) |> ignore

            let msbuild (arguments: string list) name =
                recipe {
                    let! exitCode =
                        shell {
                            cmd "dotnet"
                            args ("msbuild" :: arguments)
                            logprefix "[msbuild]"
                            stdoutlevel (Impl.levelFromString Level.Verbose)
                            erroutlevel (Impl.levelFromString Level.Error)
                        }
                    do! Impl.failOnExitCode true name exitCode
                }

            let entries = ResizeArray<Lock.Entry>()
            for project in options.Projects do
                do! trace Info "importing '%s' for '%s' %s" project options.Framework (if options.Variant = "" then "" else "(" + options.Variant + ")")

                let dump = options.Output + "." + Path.GetFileNameWithoutExtension project + ".msbuild"
                let preprocessed = dump + ".pp"

                let! graph = withProjectLock project (recipe {
                    // the design-time build; the compiler's command line comes back as an item list
                    do! msbuild
                            ([ project; "-restore"; "-nologo"; "-verbosity:quiet" ] @ switches
                             @ [ sprintf "-t:%s" targets
                                 sprintf "-getItem:%s" items
                                 sprintf "-getProperty:%s" wantedProperties
                                 sprintf "-getResultOutputFile:%s" dump ]) project

                    // `-restore` just wrote (or overwrote) the project's shared
                    // obj/project.assets.json; read the graph *this* import saw into the lock
                    // now, before a concurrent import of the same project (a different
                    // variant, next in line for the lock) restores over it
                    let graph =
                        match readDumpProperty dump "ProjectAssetsFile" with
                        | Some assetsFile when File.Exists assetsFile ->
                            let cacheRoot =
                                readDumpProperty dump "NuGetPackageRoot" |> Option.defaultWith Roots.nugetRoot
                            Nuget.readAssets assetsFile options.Framework |> packages cacheRoot
                        | _ -> []

                    // the files that took part in the evaluation; MSBuildAllProjects no longer
                    // tells, the preprocessed project does
                    do! msbuild ([ project; "-nologo" ] @ switches @ [ sprintf "-pp:%s" preprocessed ]) project
                    return graph
                })

                let imports = File.ReadAllText preprocessed |> parseImports
                let pin = sdkPin (Path.GetDirectoryName (Path.GetFullPath project))
                let entry = parseImport dump imports pin graph
                File.Delete dump
                File.Delete preprocessed

                let sdk = entry.Evaluation.Sdk
                match pin with
                | Pinned v when sdk <> "" && sdk <> v ->
                    do! trace Warning "'%s': the SDK is pinned to %s but msbuild ran %s -- the pinned SDK is not installed on this machine" entry.Name v sdk
                | Pinned _ -> ()
                | other ->
                    do! trace Warning "'%s': the SDK is not pinned (%s) -- the lock's compiler (%s, SDK %s) will drift with every SDK the machine picks; pin it with global.json { sdk: { version, rollForward: \"disable\" } }" entry.Name (sdkPinText other) entry.Dependencies.Compiler.Version sdk

                // the evaluation's inputs, so that a Directory.Build.props edit re-imports
                // and nothing else does
                do! needFiles (Filelist (entry.Evaluation.Imports |> List.map (fun (h: Lock.Hashed) -> File.make h.Path)))

                // when `Generated` carries a tokenized `$(SourceRevisionId)`, a new commit does
                // not touch any tracked input above and would leave the lock stale (the token
                // makes its *content* commit-independent, but the project still has to be
                // re-imported once there is a new commit to resolve at compile time) -- `HEAD`
                // and the ref file (or `packed-refs`) it resolves through are the files that
                // change when the commit does
                do! needFiles (Filelist (Git.headFiles (Path.GetDirectoryName (Path.GetFullPath project)) |> List.map File.make))

                // every resx output has to be named by a /resource: switch, or `run` would
                // regenerate a file the compiler never reads
                let resourceInputs = CscArgs.switchValues "resource" entry.Compilation.Options
                for (resx, resourcesFile) in entry.Compilation.Resources do
                    if not (List.contains resourcesFile resourceInputs) then
                        do! trace Warning "'%s' compiles to '%s' but no /resource: switch names that path" resx resourcesFile

                entries.Add entry

            let lock : Lock.Document = {
                Framework = options.Framework
                Configuration = options.Configuration
                Properties = options.Properties
                Entries = List.ofSeq entries
            }
            let! roots = Roots.currentWith options.Roots
            File.WriteAllText (options.Output, Lock.writeWith roots lock)
        }
