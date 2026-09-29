namespace Xake.Dotnet

open System.IO

open Xake
open Xake.Tasks

/// The settings of the `csc {}` task and the options of its runner. Auto-opened, so `open
/// Xake.Dotnet` exposes `CscSettingsType`/`CscSettings` exactly as the 3.3 API did.
[<AutoOpen>]
module CscTypes =

    /// Whether the runner compiles through the Roslyn compiler server (`VBCSCompiler`) or in
    /// a fresh compiler process each time. A client-side matter only: `/shared` and
    /// `/keepalive` are parsed out by `csc` itself before anything reaches the server, so
    /// neither ever enters a compilation's argument list (`Csc.Args`) -- like the framework's
    /// env vars, they are parameters of `Csc.run`, not of the compilation.
    type CompilerServer =
        /// `/shared` (and `/keepalive:<seconds>` when given): csc.dll acts as a thin client
        /// that connects to, or starts, the `VBCSCompiler` sitting next to it, and compiles
        /// in-process when no server can be reached. The keepalive is honoured only by a
        /// server this client starts; `None` leaves Roslyn's own default (10 minutes idle).
        | Shared of keepAlive: int option
        /// A fresh compiler process per compilation -- what the runner always did.
        | InProcess

    module CompilerServer =
        /// The default for a fresh `RunOptions`: the server, unless `XAKE_CSC_SERVER` says
        /// `0`, `false` or `off` -- the CI switch for a box where a lingering `VBCSCompiler`
        /// is unwelcome.
        let fromEnvironment () =
            match System.Environment.GetEnvironmentVariable "XAKE_CSC_SERVER" with
            | null -> Shared None
            | v when List.contains (v.Trim().ToLowerInvariant()) ["0"; "false"; "off"; "no"] -> InProcess
            | _ -> Shared None

        /// The server setting in effect for one compilation. `Some` (a target's own `noserver`
        /// or `keepalive`) wins; `None` inherits: the script variable `CSC_SERVER` (`on`,
        /// `off`, or a number of keepalive seconds; case-insensitive; set in `xakeScript { var
        /// ... }` or with `-d`), then the environment variable `XAKE_CSC_SERVER`
        /// (`fromEnvironment`), then `Shared None`. An unrecognized `CSC_SERVER` value is
        /// traced as a warning and taken as `on`. Reading the variable makes it a dependency
        /// of the target, like `NETFX`: changing it rebuilds.
        let resolve (setting: CompilerServer option) : Recipe<ExecContext, CompilerServer> =
            recipe {
                match setting with
                | Some server -> return server
                | None ->
                    let! var = getVar "CSC_SERVER"
                    match var with
                    | None -> return fromEnvironment ()
                    | Some value ->
                        match value.Trim().ToLowerInvariant() with
                        | "on" -> return Shared None
                        | "off" -> return InProcess
                        | v ->
                            match System.Int32.TryParse v with
                            | true, seconds -> return Shared (Some seconds)
                            | _ ->
                                do! trace Warning "CSC_SERVER='%s' is not 'on', 'off' or a number of seconds; using the compiler server" value
                                return Shared None
            }

    type CscSettingsType = {
        /// Limits which platforms this code can run on. The default is anycpu.
        Platform: TargetPlatform
        /// Specifies the format of the output file.
        Target: TargetType
        /// Specifies the output file name (default: base name of file with main class or first file).
        Out: File
        /// Source files.
        Src: Fileset
        /// References metadata from the specified assembly files.
        Ref: Fileset
        /// References the specified assemblies from GAC.
        RefGlobal: string list
        /// Embeds the specified resource.
        Resources: ResourceFileset list
        /// Defines conditional compilation symbols.
        Define: string list
        /// Allows unsafe code.
        Unsafe: bool
        /// Target .NET framework
        TargetFramework: string
        /// Custom command-line arguments
        CommandArgs: string list
        /// Build fails on compile error.
        FailOnError: bool
        /// Path to csc executable
        CscPath: string option
        /// Compiler package version (`Microsoft.Net.Compilers.Toolset`): when set,
        /// `Csc.ofSettings` takes `csc.dll` from that package in the NuGet cache instead of
        /// the SDK's own, so the compiler is a pinned dependency rather than whatever the SDK
        /// happens to ship. `CscPath` still overrides everything, this included.
        Toolset: string option
        /// Compiler server use; see `CompilerServer`. `None` (the default) inherits it from
        /// the script variable `CSC_SERVER`, then the environment variable `XAKE_CSC_SERVER`
        /// (see `CompilerServer.resolve`).
        Server: CompilerServer option
    } with static member Default = {
            Platform = AnyCpu
            Target = Auto    // try to resolve the type from name etc
            Out = File.undefined
            Src = Fileset.Empty
            Ref = Fileset.Empty
            RefGlobal = []
            Resources = []
            Define = []
            Unsafe = false
            TargetFramework = null
            CommandArgs = []
            FailOnError = true
            CscPath = None
            Toolset = None
            Server = None
        }

    /// Default settings for the CSC task, so that you could only override required settings.
    let CscSettings = CscSettingsType.Default

    /// What the runner itself needs, as opposed to what is being compiled: how to react to a
    /// compile error, which executable to run, the compiler server, and the environment the
    /// compiler runs in. Everything else about a compilation lives in the `Csc` handed to the
    /// runner. Kept apart from `CscSettingsType` so that running a resolved compilation does
    /// not go through a settings record whose other fields would be silently ignored
    /// (conceptual-review.md 2.2).
    type RunOptions = {
        /// Build fails on compile error.
        FailOnError: bool
        /// Path to the csc executable, overriding the compiler the compilation names.
        CscPath: string option
        /// Compiler server use; see `CompilerServer`. Default: `Shared None`, unless the
        /// environment variable `XAKE_CSC_SERVER` is `0`, `false` or `off`.
        Server: CompilerServer
        /// Environment variables the compiler process is started with: the framework's
        /// (`DotNetFwk.FrameworkInfo.EnvVars`) -- empty on the SDK path, set on the mono and
        /// legacy full-framework paths. `Csc.compile` fills it; a caller that runs the result
        /// of `csc { ...; resolve }` on those paths passes it itself (`Csc.runOptions`).
        Environment: (string * string) list
    } with static member Default = {
            FailOnError = true
            CscPath = None
            Server = CompilerServer.fromEnvironment ()
            Environment = []
        }

    /// The client-side switches `options.Server` asks for, given the compiler file about to
    /// be run -- `[]` whenever the server is not safe to use:
    ///  - only a Roslyn client can be a client: the test is a `VBCSCompiler.dll` next to the
    ///    compiler file (the SDK's `Roslyn/bincore`, a `Microsoft.Net.Compilers.Toolset`
    ///    package). The legacy full-framework `csc.exe`, mono's `mcs` and an arbitrary
    ///    `cscpath` have none and would reject `/shared` as an unknown switch (or, worse,
    ///    treat it as a source file), so they compile in-process as before.
    ///  - the server the client connects to (or starts) is that very `VBCSCompiler` -- Roslyn
    ///    resolves it relative to the client's own directory and derives the default pipe name
    ///    from `user.isAdmin.lowercase(clientDirectory)`, so a compiler at another path (another
    ///    SDK, another toolset version) gets a pipe, and a server, of its own; and the request
    ///    carries the client's commit hash, which the server checks before compiling
    ///    (`IncorrectHashBuildResponse` falls back to in-process). The `csc.dll` that is hashed
    ///    and the server that compiles are therefore the same build of Roslyn.
    ///  - env vars: the client passes its working directory, temp directory and `LIB` to the
    ///    server explicitly; anything else in the client's environment reaches the server only
    ///    if this client is the one starting it. The env vars the runner sets are
    ///    `options.Environment` -- empty for the SDK path, whose references are all absolute
    ///    `/reference:` arguments -- so a non-empty list means a toolchain whose compiler
    ///    needs its environment (mono's PATH, the registry `COMPLUS_VERSION`), and the server
    ///    is skipped rather than risk compiling with a server started under another
    ///    environment.
    ///  - a compiler under the temp directory -- a package restored into a throwaway package
    ///    root, as the tests do -- is not one to keep a server for: the server would outlive
    ///    the folder by its whole keepalive, holding a directory that is gone. In-process.
    let internal serverArgs (options: RunOptions) (compilerFile: string) =
        match options.Server with
        | InProcess -> []
        | Shared _ when not (List.isEmpty options.Environment) -> []
        | Shared keepAlive ->
            let dir = Path.GetDirectoryName compilerFile
            let comparer = if Env.isUnix then System.StringComparison.Ordinal else System.StringComparison.OrdinalIgnoreCase
            let underTemp =
                let temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                Path.GetFullPath(compilerFile).StartsWith(temp + string Path.DirectorySeparatorChar, comparer)
            if Impl.isEmpty dir || underTemp || not (File.Exists (dir </> "VBCSCompiler.dll")) then []
            else
                "/shared" :: (keepAlive |> Option.map (sprintf "/keepalive:%d") |> Option.toList)

/// A file the build reads that is not source: where it is and what it was.
type Hashed = {
    Path: string
    /// Lowercase hex SHA-256, or empty when not computed (a composed compilation never
    /// hashes; an imported project reference points at the referenced project's own output,
    /// not built yet)
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
    /// The compiler assembly, `csc.dll` under the SDK's Roslyn directory unless a compiler
    /// package is pinned
    Path: string
    Sha256: string
    /// The compiler's own version, from its file version resource (`ProductVersion` cut at
    /// the first `+` or space, e.g. `4.11.0-3.25569.22`); empty when the file is missing
    Version: string
}

/// What the compilation is made with and against, each hashed (an empty hash is not
/// checked).
type Dependencies = {
    Compiler: Compiler
    /// Every assembly referenced, in command-line order
    References: Reference list
    Analyzers: Hashed list
}

/// The section markers of `Csc.Options` and the way from the structured command line back to
/// the flat one; `Csc.args`/`Csc.isMarker` are the public names.
module internal CscSections =
    let sourcesMarker = "@Sources"
    let referencesMarker = "@References"
    let analyzersMarker = "@Analyzers"
    let definesMarker = "@Defines"

    let isMarker (option: string) =
        option = sourcesMarker || option = referencesMarker || option = analyzersMarker || option = definesMarker

    let formatReference (r: Reference) =
        "/reference:" + (if r.Alias = "" then "" else r.Alias + "=") + CscArgs.quoteIfNeeded r.Path

    let args (options: string list) (defines: string list) (sources: string list) (references: Reference list) (analyzers: Hashed list) =
        options |> List.collect (fun option ->
            if option = sourcesMarker then sources
            elif option = referencesMarker then references |> List.map formatReference
            elif option = analyzersMarker then analyzers |> List.map (fun a -> "/analyzer:" + CscArgs.quoteIfNeeded a.Path)
            elif option = definesMarker then
                if List.isEmpty defines then [] else [ "/define:" + String.concat ";" defines ]
            else [ option ])

/// <summary>
/// A resolved C# compilation: exactly what the compiler is handed. `Options`, `Defines`,
/// `Sources` and the references and analyzers of `Dependencies` together are the exact
/// command line (`Args` rebuilds it): `Options` holds every argument that is not one of those
/// four sections, in the original order, with a marker string -- `"@Sources"`,
/// `"@References"`, `"@Analyzers"`, `"@Defines"` -- at the position each section occupied. A
/// leading `@` in `Options` is always such a marker and never a csc response-file reference.
///
/// Obtained from `csc { ...; resolve }` (or `Csc.ofSettings`), from a lock entry
/// (`Lock.Entry.Csc`), or built by hand (`Csc.ofArgs`); run by `Csc.run`.
/// </summary>
type Csc = {
    /// AssemblyName
    Name: string
    /// The target framework this compilation is for (`netstandard2.0`, `net472`, ...; ""
    /// when a composed compilation names none)
    Framework: string
    /// The compiler's working directory -- the project's directory in `dotnet build`, the
    /// build's project root for a composed compilation -- and what relative arguments were
    /// resolved against
    Directory: string
    /// Every switch that is not a reference, analyzer or define, paths absolute, with the
    /// four section markers in place
    Options: string list
    /// Conditional compilation symbols (`/define:A;B` split)
    Defines: string list
    /// Source files, absolute
    Sources: string list
    /// Inputs generated ahead of the compile (assembly attributes, the editorconfig msbuild
    /// derives from properties), by path, with their content; `Csc.run` writes each back
    /// when it is missing or differs
    Generated: (string * string) list
    /// `.resx` files this compilation embeds: (resx path, `.resources` output path), both
    /// absolute. `Csc.run` regenerates the output from the resx (via `Xake.Dotnet.Resx`)
    /// whenever it is missing, so the exact input the `/resource:` switch names can always be
    /// reproduced.
    Resources: (string * string) list
    Dependencies: Dependencies
} with
    /// The exact command line, paths absolute (rebuilt from `Options` and the sections)
    member this.Args =
        CscSections.args this.Options this.Defines this.Sources this.Dependencies.References this.Dependencies.Analyzers
    /// The `/out:` path, if the options carry one
    member this.Output = CscArgs.switchValues "out" this.Options |> List.tryHead

/// Resolving and running a `Csc`.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Csc =

    /// Whether an `Options` element is one of the four section markers.
    let isMarker (option: string) = CscSections.isMarker option

    /// Lowercase hex SHA-256 of a file, "" when it does not exist.
    /// `Hash.sha256`, with "" for a file that does not exist: the lock's "not hashed" value.
    let sha256 (path: string) = if File.Exists path then Hash.sha256 path else ""

    let hashed path : Hashed = { Path = path; Sha256 = sha256 path }

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

    /// Factors a command line into the structured form: a `/reference:`/`/r:` switch
    /// carrying exactly one item goes into the references (its `alias=` prefix split off),
    /// a `/analyzer:`/`/a:` switch with one item into the analyzers, every `/define:`/`/d:`
    /// into `Defines` (all of them concatenated, one marker at the first), every source
    /// into `Sources` (one marker at the first). Everything else stays in `Options`, in
    /// order. A contiguous block collapses to one marker; should two non-contiguous
    /// blocks of one kind ever appear, the marker stays at the first and the caller's
    /// round-trip check (`Args` against the original) decides. `Name`, `Framework`,
    /// `Directory`, `Generated` and `Resources` are empty (the caller knows them), every hash
    /// is empty, and the compiler is an empty `csc` entry.
    let ofArgs (args: string list) : Csc =
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
                marker CscSections.sourcesMarker
                sources.Add path
            | CscArgs.Switch (name, value) ->
                match CscArgs.canonical name, singleItem value with
                | "reference", Some item ->
                    marker CscSections.referencesMarker
                    let alias, path =
                        match CscArgs.aliasSplitIndex item with
                        | -1 -> "", item
                        | i -> item.Substring (0, i), item.Substring (i + 1)
                    references.Add { Path = path; Sha256 = ""; Alias = alias }
                | "analyzer", Some item ->
                    marker CscSections.analyzersMarker
                    analyzers.Add { Path = item; Sha256 = "" }
                | "define", _ ->
                    marker CscSections.definesMarker
                    for d in value.Split ';' do
                        if d <> "" then defines.Add d
                | _ -> options.Add arg
        { Name = ""
          Framework = ""
          Directory = ""
          Options = List.ofSeq options
          Defines = List.ofSeq defines
          Sources = List.ofSeq sources
          Generated = []
          Resources = []
          Dependencies =
            { Compiler = { Tool = "csc"; Path = ""; Sha256 = ""; Version = "" }
              References = List.ofSeq references
              Analyzers = List.ofSeq analyzers } }

    /// The flat command line back from the structure (`c.Args`): each marker in `Options`
    /// expands to its section -- one `/reference:<alias=>path` per reference (a path with a
    /// comma re-quoted, as msbuild quotes it), one `/analyzer:path` per analyzer, one
    /// `/define:A;B`, the sources.
    let args (c: Csc) : string list = c.Args

    /// Fills `Sha256` for every reference and analyzer and for the compiler (its `Version`
    /// too, when empty), from what is on disk right now; leaves `""` where the file does not
    /// exist. The record-time step for a composed compilation, which never hashes.
    let rehash (c: Csc) : Csc =
        let compiler = c.Dependencies.Compiler
        { c with
            Dependencies =
                { References = c.Dependencies.References |> List.map (fun r -> { r with Sha256 = sha256 r.Path })
                  Analyzers = c.Dependencies.Analyzers |> List.map (fun h -> { h with Sha256 = sha256 h.Path })
                  Compiler =
                    { compiler with
                        Sha256 = sha256 compiler.Path
                        Version = if compiler.Version = "" then compilerVersion compiler.Path else compiler.Version } } }

    /// Rewrites every path the options, sources, references, analyzers, generated files and
    /// resources carry, through `f`. A rewritten hashed item loses its hash: the hash on
    /// record was computed for the old path, and it would otherwise be checked against a
    /// different file. Section markers are left alone.
    let mapPaths (f: string -> string) (c: Csc) : Csc =
        let rewriteHashed (h: Hashed) : Hashed =
            let path = f h.Path
            if path = h.Path then h else { Path = path; Sha256 = "" }
        let rewriteReference (r: Reference) =
            let path = f r.Path
            if path = r.Path then r else { r with Path = path; Sha256 = "" }
        { c with
            Options = c.Options |> List.map (fun o -> if isMarker o then o else CscArgs.parse o |> CscArgs.mapPaths f |> CscArgs.format)
            Sources = c.Sources |> List.map f
            Generated = c.Generated |> List.map (fun (path, content) -> f path, content)
            Resources = c.Resources |> List.map (fun (resx, resources) -> f resx, f resources)
            Dependencies =
                { c.Dependencies with
                    References = c.Dependencies.References |> List.map rewriteReference
                    Analyzers = c.Dependencies.Analyzers |> List.map rewriteHashed } }

    /// Applies `f` to every piece of text that may embed a value rather than name a file:
    /// `Generated` content, `Options`, `Defines`.
    let mapText (f: string -> string) (c: Csc) : Csc =
        { c with
            Generated = c.Generated |> List.map (fun (path, content) -> path, f content)
            Options = c.Options |> List.map f
            Defines = c.Defines |> List.map f }

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

    /// The path `csc.dll` would have under a `Microsoft.Net.Compilers.Toolset`-shaped package
    /// (`<packageRoot>/<packageId>/<version>/tasks/netcore/bincore/csc.dll`), fetching the
    /// package into the folder first when it is not there yet. This is `ofSettings`'s
    /// `toolset` operation.
    let private restoreToolsetCompiler (packageId: string) (version: string) =
        recipe {
            let! dir = DotNetFwk.restorePackage None packageId version
            let cscDll = dir </> "tasks" </> "netcore" </> "bincore" </> "csc.dll"
            if not (File.Exists cscDll) then
                failwithf "toolset %s %s: the package folder %s has no compiler at %s (a partial restore? delete the folder and rebuild)" packageId version dir cscDll
            return cscDll
        }

    /// The target framework the settings name (`targetfwk`, else the script variable
    /// `NETFX-TARGET`; null for none) and the framework the compiler and its env vars come
    /// from (that one, unless `NETFX` says otherwise).
    let private frameworkOf (settings: CscSettingsType) =
        recipe {
            let! globalTargetFwk = getVar "NETFX-TARGET"
            let targetFramework =
                match settings.TargetFramework, globalTargetFwk with
                | s, _ when not <| System.String.IsNullOrWhiteSpace(s) -> s
                | _, Some s when s <> "" -> s
                | _ -> null
            let! netfxVar = getVar "NETFX"
            let dotnetFwk = match netfxVar with | Some _ -> netfxVar | None -> Option.ofObj targetFramework
            return targetFramework, DotNetFwk.locateFramework dotnetFwk
        }

    /// The runner's options for composed settings: `FailOnError` and `CscPath` from the
    /// settings, the server resolved (`CompilerServer.resolve`), and the environment of the
    /// framework the settings target. What `compile` runs with, and what `csc { ...; lock }`
    /// hands `Lock.build`.
    let runOptions (settings: CscSettingsType) : Recipe<ExecContext, RunOptions> =
        recipe {
            let! server = CompilerServer.resolve settings.Server
            let! _, fwkInfo = frameworkOf settings
            return { FailOnError = settings.FailOnError
                     CscPath = settings.CscPath
                     Server = server
                     Environment = fwkInfo.EnvVars }
        }

    /// <summary>
    /// Composes a `Csc` from `Src`/`Ref`/`RefGlobal`/`Resources`/`Define`/`Target`/
    /// `Platform`/`Unsafe`/`TargetFramework`/`CommandArgs` at recipe time, without running
    /// the compiler (what `csc { ...; resolve }` returns). The argument list it produces is,
    /// in order: `/noconfig` (first, when the target framework requires it), `/nologo`,
    /// `/target:`, `/platform:`, `/unsafe`, `/nostdlib+`, `/out:`, `/define:`, sources,
    /// `/reference:` refs (one per reference), global refs, `/res:`, `CommandArgs`. The list
    /// goes through `ofArgs` like an imported one and gets the same round-trip check.
    ///
    /// With no `Out`, the output is the target of the rule this runs in (`getTargetFile`), so
    /// outside a file rule the settings need an explicit `out`. Hashes stay empty (`rehash`
    /// is the record-time step). A `.resx` resource is recorded as a permanent `(resx,
    /// .resources)` pair under `obj/xake/<name>/`, compiled by `run`.
    /// </summary>
    let ofSettings (settings: CscSettingsType) : Recipe<ExecContext, Csc> =
        recipe {
            let! options = getCtxOptions()
            let getFiles = toFileList options.ProjectRoot

            let! outFile =
                if settings.Out = File.undefined then
                    getTargetFile()
                else
                    settings.Out |> recipe.Return

            // a `.resx` resource is not compiled here: unlike an ordinary embedded-resource
            // file (already the file the compiler reads), a resx needs turning into a
            // `.resources` first, and doing that eagerly into a random temp file left nothing
            // for a lock recorded from these settings to compile against once the caller
            // deleted it. Instead this records a permanent `(resx, .resources)` pair in
            // `Resources`, the same shape `Project.import` already produces for an imported
            // project -- `run`'s resource step compiles it when the output is missing and
            // `needFiles` the resx itself, so the engine (not this recipe) decides when a resx
            // edit reruns the compile.
            let resNames = settings.Resources |> List.collect (Impl.collectResInfo options.ProjectRoot)
            let isResx (_, file: File) = file |> File.getFileName |> Impl.endsWith ".resx"
            let resxEntries, plainEntries = resNames |> List.partition isResx

            let assemblyName = Path.GetFileNameWithoutExtension outFile.Name
            let resxResources =
                resxEntries
                |> List.map (fun (resname, file) ->
                    let manifestName = Path.ChangeExtension(resname, ".resources")
                    let resourcesPath =
                        (options.ProjectRoot </> "obj" </> "xake" </> assemblyName </> manifestName).Replace('\\', '/')
                    manifestName, file.FullName, resourcesPath)

            let resArgs =
                (plainEntries |> List.map (fun (name, file: File) -> name, file.FullName))
                @ (resxResources |> List.map (fun (manifestName, _, resourcesPath) -> manifestName, resourcesPath))
            let resources = resxResources |> List.map (fun (_, resx, resourcesPath) -> resx, resourcesPath)

            let (Filelist src)  = settings.Src |> getFiles
            let (Filelist refs) = settings.Ref |> getFiles

            // no `needFiles` here: `resolve` only describes the compilation. `run` needs
            // everything the command line reads (`CscArgs.inputs`: sources, references,
            // resource files) once, for every producer -- this, a lock entry, `ofArgs`.

            let! targetFramework, fwkInfo = frameworkOf settings

            let (globalRefPaths, nostdlib, noconfig) =
                match targetFramework with
                | null ->
                    failwithf "'%s': csc needs a target framework: set targetfwk in the csc block or the NETFX-TARGET script variable (e.g. targetfwk \"net8.0\")" assemblyName
                | tgt ->
                    let fwk = Some tgt |> DotNetFwk.locateFramework in
                    let lookup = DotNetFwk.locateAssembly fwk
                    (("mscorlib.dll" :: settings.RefGlobal) |> List.map lookup), true, true

            let globalRefs = globalRefPaths |> List.map ((+) "/reference:")

            let args =
                seq {
                    if noconfig then
                        yield "/noconfig"

                    yield "/nologo"

                    yield "/target:" + Impl.targetStr outFile.Name settings.Target
                    yield "/platform:" + Impl.platformStr settings.Platform

                    if settings.Unsafe then
                        yield "/unsafe"

                    if nostdlib then
                        yield "/nostdlib+"

                    if outFile <> File.undefined then
                        yield sprintf "/out:%s" (File.getFullName outFile)

                    if not (List.isEmpty settings.Define) then
                        yield "/define:" + (settings.Define |> String.concat ";")

                    yield! src |> List.map (fun f -> f.FullName)

                    yield! refs |> List.map ((fun f -> f.FullName) >> (+) "/reference:")
                    yield! globalRefs

                    yield! resArgs |> List.map (fun (name, path) -> sprintf "/res:%s,%s" path name)
                    yield! settings.CommandArgs
                } |> List.ofSeq

            // references and env vars always come from the targeted framework -- `toolset`
            // only replaces the compiler executable, fetching the package into the machine's
            // package cache first when it is not there yet
            let! compilerPath =
                match settings.Toolset with
                | None -> recipe { return fwkInfo.CscTool }
                | Some version ->
                    recipe {
                        let! cscDll = restoreToolsetCompiler "Microsoft.Net.Compilers.Toolset" version
                        if not (File.Exists cscDll) then
                            failwithf "compiler package Microsoft.Net.Compilers.Toolset %s could not be restored (expected '%s')" version cscDll
                        return cscDll
                    }

            let composed = ofArgs args
            let c =
                { composed with
                    Name = Path.GetFileNameWithoutExtension outFile.Name
                    Framework = (match targetFramework with null -> "" | fwk -> fwk)
                    Directory = options.ProjectRoot
                    Resources = resources
                    Dependencies =
                        { composed.Dependencies with
                            Compiler = { Tool = "csc"; Path = compilerPath; Sha256 = ""; Version = compilerVersion compilerPath } } }
            // the same fidelity check the import makes: one item per switch here, so it holds
            let rebuilt = c.Args
            if rebuilt <> args then
                failwithf "'%s': the command line rebuilt from the resolved compilation differs from the composed one:\n%s"
                    c.Name (diffList args rebuilt |> String.concat "\n")

            return c
        }

    /// <summary>
    /// Runs the compiler over a resolved compilation -- composed by `ofSettings`, taken from
    /// a lock entry, or built by hand. This is the only place that shells out to csc.
    ///
    /// Before running the compiler this: writes back any `Generated` file that is missing or
    /// whose content changed, creates the output directories, compiles each `.resx` whose
    /// `.resources` output is missing, and verifies the SHA-256 of every hashed reference,
    /// analyzer and of the compiler itself against what is on disk -- a mismatch fails the
    /// build rather than silently compiling against something other than what was recorded.
    /// An empty hash (a composed compilation never records one) skips that check.
    ///
    /// Obtaining anything that is missing (packages, a revision token) is not this function's
    /// business; `Lock.compile` does that before handing the compilation here.
    /// </summary>
    let run (options: RunOptions) (c: Csc) : Recipe<ExecContext, unit> =
        recipe {
            let compiler = c.Dependencies.Compiler
            do! trace Info "compiling '%s' (%s %s)" c.Name compiler.Tool compiler.Version

            if options.CscPath.IsNone && not (File.Exists compiler.Path) then
                let msg = sprintf "'%s': the compiler %s does not exist" c.Name compiler.Path
                do! trace Error "%s" msg
                if options.FailOnError then failwith msg

            // the compiler is hashed (below) but was never a tracked dependency, so an SDK or
            // toolset update that changes csc.dll's bytes left the target looking up to date
            // and the hash check never ran (conceptual-review.md 2.4)
            do! needFiles (Filelist [File.make compiler.Path])

            let args = c.Args

            // the resolved compilation is the source of truth for what msbuild (or the
            // composed front end) generated (assembly attributes, TFM defines): write it back
            // whenever it is missing or someone touched it
            for (path, content) in c.Generated do
                let upToDate = File.Exists path && File.ReadAllText path = content
                if not upToDate then
                    let dir = Path.GetDirectoryName path
                    if not (Impl.isEmpty dir) then Directory.CreateDirectory dir |> ignore
                    File.WriteAllText (path, content)

            for path in CscArgs.outputs args do
                let dir = Path.GetDirectoryName path
                if not (Impl.isEmpty dir) then Directory.CreateDirectory dir |> ignore

            // a resx `PrepareResources` compiled is named by a `/resource:` switch as the
            // `.resources` file it produced, not the resx itself -- that file has to exist
            // before the hash check and the `needFiles` below see it. The resx is `needFiles`d
            // so the engine decides whether an edit reruns this recipe; regenerating only when
            // the `.resources` output is missing (not on a timestamp comparison) keeps `run`
            // from being a second rebuilder next to the engine's (conceptual-review.md 2.3).
            do! needFiles (Filelist (c.Resources |> List.map (fst >> File.make)))
            for (resx, resourcesFile) in c.Resources do
                if not (File.Exists resourcesFile) then
                    Resx.compile resx resourcesFile

            // everything that carries a hash has to be exactly what was recorded, or the
            // compilation is not the one described
            let mismatches =
                let check (path: string) (expected: string) =
                    if expected = "" then None
                    else
                        let actual = if File.Exists path then sha256 path else "missing"
                        if actual = expected then None else Some (path, expected, actual)
                [ for r in c.Dependencies.References do yield check r.Path r.Sha256
                  for a in c.Dependencies.Analyzers do yield check a.Path a.Sha256
                  yield check compiler.Path compiler.Sha256 ]
                |> List.choose id

            if not (List.isEmpty mismatches) then
                let detail =
                    mismatches
                    |> List.map (fun (path, expected, actual) -> sprintf "%s: expected %s, got %s" path expected actual)
                    |> String.concat "\n"
                do! trace Error "('%s') hash mismatch:\n%s" c.Name detail
                if options.FailOnError then
                    failwithf "('%s') hash mismatch:\n%s" c.Name detail

            // the generated files have to exist before the inputs are demanded. Note: for the
            // composed mode this needs everything the args name -- including the framework's
            // global references (mscorlib.dll etc) -- not only the sources, refs and resource
            // files. That is intended.
            do! needFiles (Filelist (CscArgs.inputs args |> List.map File.make))

            // csc warns CS2023 and ignores /noconfig when it is inside the response file, so
            // it has to stay on the command line and everything else goes into the rsp
            let noconfig = args |> List.contains "/noconfig"
            let rspArgs = args |> List.filter ((<>) "/noconfig")

            let rspFile = Path.GetTempFileName()
            File.WriteAllLines (rspFile, rspArgs |> List.map Impl.escapeArgument)
            let commandLineArgs =
                seq {
                    if noconfig then yield "/noconfig"
                    yield "@" + rspFile
                }

            // the response file has to go regardless of how the compilation ends
            let deleteTempFiles () =
                try System.IO.File.Delete rspFile with _ -> ()

            let cscTool, compilerFile, extraArgs =
                match options.CscPath with
                | Some tool -> tool, tool, []
                // the compiler path from `DotNetFwk` may be a native launcher rather than a
                // managed dll (the "run directly" branch below covers that case too)
                | None when Impl.endsWith ".dll" compiler.Path -> "dotnet", compiler.Path, [compiler.Path]
                | None -> compiler.Path, compiler.Path, []

            // client-side switches, on the command line and never in the rsp or a lock: the
            // client strips them before the arguments reach the server (`serverArgs` says when
            // they are safe to add)
            let serverSwitches = serverArgs options compilerFile
            match options.Server, serverSwitches with
            | Shared _, [] -> do! trace Debug "compiler server not used for '%s' (no VBCSCompiler next to it, under the temp directory, or env vars in play)" compilerFile
            | _ -> ()
            let commandLineArgs = Seq.append serverSwitches commandLineArgs

            do! trace Debug "Command line: '%s %s'" cscTool
                    ((extraArgs @ List.ofSeq commandLineArgs) |> String.concat " ")

            try
                let! exitCode =
                    shell {
                        cmd cscTool
                        args (Seq.append extraArgs commandLineArgs)
                        envs options.Environment
                        // `dotnet build` runs csc with cwd = the project's directory; some
                        // compiler inputs are resolved against it rather than against an
                        // argument on the command line -- an XML-doc `<include file='../..'>`
                        // path is resolved relative to the *compiler's* working directory, not
                        // per source file. `Csc.Directory` records exactly that; without it, a
                        // project compiled from a different cwd than its own directory can
                        // fail with CS1589 even though every file the args name is absolute
                        // and present.
                        workdir c.Directory
                        logprefix "[csc]"
                        stdoutlevel (Tool.diagnosticLevel Level.Verbose)
                        erroutlevel (Tool.diagnosticLevel Level.Verbose)
                    }

                do! Tool.failOnExitCode options.FailOnError c.Name exitCode
            finally
                deleteTempFiles ()
        }

    /// <summary>
    /// C# compiler task in record syntax: `ofSettings`, then `run` with the options the
    /// settings imply (`runOptions`). What `csc { ... }` runs; replaces the 3.3 function
    /// `Csc settings`.
    /// </summary>
    /// <param name="settings">Compiler settings</param>
    /// <returns>Recipe compiling the target</returns>
    let compile (settings: CscSettingsType) : Recipe<ExecContext, unit> =
        recipe {
            do! trace Level.Debug "Csc: settings=%A" settings
            let! c = ofSettings settings
            let! options = runOptions settings
            do! run options c
        }
