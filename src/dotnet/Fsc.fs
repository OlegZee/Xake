namespace Xake.Dotnet

open System.IO

open Xake
open Xake.Tasks

/// The settings of the `fsc {}` task. Auto-opened, so `open Xake.Dotnet` exposes
/// `FscSettingsType`/`FscSettings` exactly as the 3.3 API did.
[<AutoOpen>]
module FscTypes =

    /// <summary>
    /// Fsc (F# compiler) task settings.
    /// </summary>
    type FscSettingsType = {
        /// Limits which platforms this code can run on. Not passed to fsc (it never was).
        Platform: TargetPlatform
        /// Specifies the format of the output file.
        Target: TargetType
        /// Specifies the output file name (default: the target of the rule).
        Out: File
        /// Source files, in compile order.
        Src: Fileset
        /// References metadata from the specified assembly files.
        Ref: Fileset
        /// Assemblies looked up in the target framework's reference assemblies.
        RefGlobal: string list
        /// Embeds the specified resource.
        Resources: ResourceFileset list
        /// Defines conditional compilation symbols.
        Define: string list
        /// Target .NET framework; optional: `NETFX-TARGET` when not set, else the .NET framework
        /// of the SDK the build runs on (`net10.0` for SDK 10.0.x).
        TargetFramework: string
        /// Use a specific F# compiler version (the Windows registry provider only).
        FscVersion: string option
        /// Xml documentation file to produce alongside the assembly.
        Doc: File
        /// Custom command-line arguments
        CommandArgs: string list
        /// Build fails on compile error.
        FailOnError: bool
        /// Do not reference the default CLI assemblies. Always in effect: the target
        /// framework's reference assemblies are passed explicitly.
        NoFramework: bool

        /// Generate tailcalls where possible (`--tailcalls-` when false).
        Tailcalls: bool
    } with static member Default = {
            Platform = AnyCpu
            Target = Auto
            Out = File.undefined
            Src = Fileset.Empty
            Ref = Fileset.Empty
            RefGlobal = []
            Resources = []
            Define = []
            TargetFramework = null
            FscVersion = None
            Doc = File.undefined
            CommandArgs = []
            FailOnError = true
            NoFramework = false
            Tailcalls = true
        }

    /// <summary>
    /// Default settings for the Fsc task, so that you could only override required settings.
    /// </summary>
    let FscSettings = FscSettingsType.Default

    /// What the fsc runner needs, as opposed to what is being compiled. fsc's own type: there
    /// is no compiler server, so nothing here would be ignored. Its labels overlap
    /// `RunOptions`', so it requires qualified construction (`{ FscRunOptions.FailOnError =
    /// ... }`); `{ FscRunOptions.Default with ... }` needs none.
    [<RequireQualifiedAccess>]
    type FscRunOptions = {
        /// Build fails on compile error.
        FailOnError: bool
        /// Path to the fsc executable, overriding the compiler the compilation names; it is
        /// run directly (not through `dotnet`).
        FscPath: string option
        /// Environment variables the compiler process is started with: the framework's
        /// (`DotNetFwk.FrameworkInfo.EnvVars`), empty on the SDK path. `Fsc.runOptions`
        /// fills it for composed settings.
        Environment: (string * string) list
    } with static member Default = {
            FscRunOptions.FailOnError = true
            FscRunOptions.FscPath = None
            FscRunOptions.Environment = []
        }

/// The section markers of `Fsc.Options` and the way back to the flat command line.
module internal FscSections =
    let sourcesMarker = "@Sources"
    let referencesMarker = "@References"
    let definesMarker = "@Defines"

    let isMarker (option: string) =
        option = sourcesMarker || option = referencesMarker || option = definesMarker

    let args (options: string list) (defines: string list) (sources: string list) (references: Reference list) =
        options |> List.collect (fun option ->
            if option = sourcesMarker then sources
            elif option = referencesMarker then references |> List.map (fun r -> "-r:" + r.Path)
            elif option = definesMarker then defines |> List.map (fun d -> "--define:" + d)
            else [ option ])

/// <summary>
/// A resolved F# compilation: exactly what the compiler is handed, the twin of `Csc` in fsc's
/// own argument dialect (`FscArgs`). `Options` holds every argument that is not a source,
/// reference or define, in the original order and spelling (`--optimize+`, `-o:...`), with a
/// marker -- `"@Sources"`, `"@References"`, `"@Defines"` -- at the position each section
/// occupied; `Args` rebuilds the command line, references as `-r:path` and defines as
/// `--define:X`, one per switch. The source order is the F# compile order. fsc has no
/// analyzers: `Dependencies.Analyzers` is always empty.
///
/// Obtained from `fsc { ...; resolve }` (or `Fsc.ofSettings`) or built by hand (`Fsc.ofArgs`).
/// The labels are the same as `Csc`'s, so the record requires qualified construction
/// (`{ Fsc.Name = ... }`) and never shadows `Csc` in a record expression.
/// </summary>
[<RequireQualifiedAccess>]
type Fsc = {
    /// AssemblyName
    Name: string
    /// The target framework this compilation is for (`netstandard2.0`, `net472`, ...)
    Framework: string
    /// The compiler's working directory, and what relative arguments were resolved against
    Directory: string
    /// Every switch that is not a reference or define, paths absolute, with the three section
    /// markers in place
    Options: string list
    /// Conditional compilation symbols, one per `--define:`
    Defines: string list
    /// Source files, absolute, in compile order
    Sources: string list
    /// Inputs generated ahead of the compile, by path, with their content; the runner writes
    /// each back when it is missing or differs
    Generated: (string * string) list
    /// `.resx` files this compilation embeds: (resx path, `.resources` output path), both
    /// absolute; the runner compiles the output whenever it is missing
    Resources: (string * string) list
    Dependencies: Dependencies
} with
    /// The exact command line, paths absolute (rebuilt from `Options` and the sections)
    member this.Args =
        FscSections.args this.Options this.Defines this.Sources this.Dependencies.References
    /// The `--out:`/`-o:` path, if the options carry one
    member this.Output = FscArgs.switchValues "out" this.Options |> List.tryHead

/// Resolving and running an `Fsc`.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Fsc =

    /// Whether an `Options` element is one of the three section markers.
    let isMarker (option: string) = FscSections.isMarker option

    /// Factors a command line into the structured form: every `-r:`/`--reference:` into the
    /// references, every `--define:`/`-d:` into `Defines`, every source into `Sources` (one
    /// marker at the first of each kind). Everything else stays in `Options`, in order and as
    /// spelled. `Args` writes references back as `-r:` and defines as `--define:`, the
    /// spelling msbuild uses. `Name`, `Framework`, `Directory`, `Generated` and `Resources`
    /// are empty (the caller knows them), every hash is empty, and the compiler is an empty
    /// `fsc` entry.
    let ofArgs (args: string list) : Fsc =
        let options = ResizeArray<string>()
        let defines = ResizeArray<string>()
        let sources = ResizeArray<string>()
        let references = ResizeArray<Reference>()
        let marker (m: string) = if not (options.Contains m) then options.Add m
        for arg in args do
            match FscArgs.parse arg with
            | FscArgs.Source path ->
                marker FscSections.sourcesMarker
                sources.Add path
            | FscArgs.Switch (_, name, value) ->
                match FscArgs.canonical name with
                | "reference" when value <> "" ->
                    marker FscSections.referencesMarker
                    references.Add { Path = value; Sha256 = ""; Alias = "" }
                | "define" when value <> "" ->
                    marker FscSections.definesMarker
                    defines.Add value
                | _ -> options.Add arg
        { Fsc.Name = ""
          Fsc.Framework = ""
          Fsc.Directory = ""
          Fsc.Options = List.ofSeq options
          Fsc.Defines = List.ofSeq defines
          Fsc.Sources = List.ofSeq sources
          Fsc.Generated = []
          Fsc.Resources = []
          Fsc.Dependencies =
            { Compiler = { Tool = "fsc"; Path = ""; Sha256 = ""; Version = "" }
              References = List.ofSeq references
              Analyzers = [] } }

    /// The flat command line back from the structure (`f.Args`).
    let args (f: Fsc) : string list = f.Args

    /// Fills `Sha256` for every reference and for the compiler (its `Version` too, when
    /// empty), from what is on disk right now; leaves `""` where the file does not exist.
    let rehash (f: Fsc) : Fsc =
        let compiler = f.Dependencies.Compiler
        { f with
            Fsc.Dependencies =
                { f.Dependencies with
                    References = f.Dependencies.References |> List.map (fun r -> { r with Sha256 = Csc.sha256 r.Path })
                    Compiler =
                        { compiler with
                            Sha256 = Csc.sha256 compiler.Path
                            Version = if compiler.Version = "" then Csc.compilerVersion compiler.Path else compiler.Version } } }

    /// Rewrites every path the options, sources, references, generated files and resources
    /// carry, through `f`. A rewritten reference loses its hash. Section markers are left
    /// alone.
    let mapPaths (f: string -> string) (c: Fsc) : Fsc =
        let rewriteReference (r: Reference) =
            let path = f r.Path
            if path = r.Path then r else { r with Path = path; Sha256 = "" }
        { c with
            Fsc.Options = c.Options |> List.map (fun o -> if isMarker o then o else FscArgs.parse o |> FscArgs.mapPaths f |> FscArgs.format)
            Fsc.Sources = c.Sources |> List.map f
            Fsc.Generated = c.Generated |> List.map (fun (path, content) -> f path, content)
            Fsc.Resources = c.Resources |> List.map (fun (resx, resources) -> f resx, f resources)
            Fsc.Dependencies =
                { c.Dependencies with References = c.Dependencies.References |> List.map rewriteReference } }

    /// Applies `f` to every piece of text that may embed a value rather than name a file:
    /// `Generated` content, `Options`, `Defines`.
    let mapText (f: string -> string) (c: Fsc) : Fsc =
        { c with
            Fsc.Generated = c.Generated |> List.map (fun (path, content) -> path, f content)
            Fsc.Options = c.Options |> List.map f
            Fsc.Defines = c.Defines |> List.map f }

    /// <summary>
    /// Composes an `Fsc` from the settings at recipe time, without running the compiler (what
    /// `fsc { ...; resolve }` returns). The argument list, in order: `--nologo`, `--target:`,
    /// `--noframework`, `--targetprofile:netstandard` (netstandard) or `--targetprofile:netcore`
    /// (.NET), `--tailcalls-` (when `Tailcalls` is off), `--out:`, `--doc:`, one `--define:` per
    /// symbol, the sources, `-r:` per reference, the default `FSharp.Core.dll` (when no
    /// reference is an `FSharp.Core.dll`), the framework's references (the whole targeting pack
    /// for .NET; `netstandard.dll` for netstandard; `mscorlib.dll` and a forwarding
    /// `netstandard.dll` facade for .NET Framework, `DotNetFwk.netstandardFacade`, unless a
    /// reference is a `netstandard.dll`; then `RefGlobal`), `--resource:path,name`,
    /// `CommandArgs`. Always the `--` form, on every OS. The list goes through `ofArgs` and the
    /// same round-trip check as `Csc.ofSettings`.
    ///
    /// The target framework is `targetfwk`, else `NETFX-TARGET`, else the SDK's own .NET
    /// framework (`net10.0` for SDK 10.0.x). The default `FSharp.Core` is the SDK's own (next
    /// to `fsc.dll`) for .NET, and the `FSharp.Core` package at `FSHARP_CORE_VERSION`, else
    /// `DotNetFwk.fsharpCoreVersion`, for netstandard and .NET Framework
    /// (`DotNetFwk.fsharpCoreReference`). Every reference, the defaulted ones included, is
    /// recorded like any other. The compiler is the SDK's `fsc.dll` (`DotNetFwk.fscCompiler`);
    /// on the other providers, `FscTool` with `fscver`/`FSCVER`. A `.resx` resource becomes
    /// a permanent `(resx, .resources)` pair under `obj/xake/<name>/`, as for csc. With no
    /// `Out`, the output is the target of the rule this runs in.
    /// </summary>
    let ofSettings (settings: FscSettingsType) : Recipe<ExecContext, Fsc> =
        recipe {
            let! options = getCtxOptions()
            let getFiles = toFileList options.ProjectRoot

            let! outFile =
                if settings.Out = File.undefined then
                    getTargetFile()
                else
                    settings.Out |> recipe.Return

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

            let! targetFramework, fwkInfo = Csc.frameworkFor settings.TargetFramework
            match DotNetFwk.sdkProbeWarning options.ProjectRoot with
            | Some warning -> do! trace Warning "%s" warning
            | None -> ()

            let! targetFwkInfo =
                match targetFramework with
                | null ->
                    failwithf "'%s': fsc needs a target framework: set targetfwk in the fsc block or the NETFX-TARGET script variable (e.g. targetfwk \"netstandard2.0\"); the SDK's own framework could not be determined" assemblyName
                | tgt -> DotNetFwk.resolveFramework (Some tgt)

            // netstandard is a profile rather than a framework version: its whole surface
            // lives in netstandard.dll, and fsc has to be told about it explicitly. .NET
            // (net5.0 and later) is the `netcore` profile, referenced as a whole targeting pack.
            // Everything else is .NET Framework.
            let isNetstandard = targetFramework.StartsWith ("netstandard", System.StringComparison.OrdinalIgnoreCase)
            let isNetcore = Option.isSome (DotNetFwk.netcoreMoniker targetFramework)
            let isNetFramework = not isNetstandard && not isNetcore
            let fileNamed name (path: string) =
                System.String.Equals (Path.GetFileName path, name, System.StringComparison.OrdinalIgnoreCase)
            let refsNamed name = refs |> List.exists (fun f -> fileNamed name f.FullName)
            let globalRefs =
                let lookup = DotNetFwk.locateAssembly targetFwkInfo
                let frameworkRefs =
                    match DotNetFwk.frameworkReferences targetFwkInfo with
                    | [] when isNetstandard -> [ lookup "netstandard.dll" ]
                    | [] ->
                        // FSharp.Core (any of them: the SDK's, the package's netstandard
                        // builds) references netstandard 2.0.0.0, which .NET Framework
                        // reference assemblies below 4.7.1 lack: a forwarding facade
                        // makes it resolve, unless the settings reference one themselves
                        let facade =
                            if isNetFramework && not (refsNamed "netstandard.dll") then
                                DotNetFwk.netstandardFacade targetFwkInfo [ targetFwkInfo.InstallPath; fwkInfo.InstallPath ]
                                |> Option.toList
                            else []
                        lookup "mscorlib.dll" :: facade
                    | all -> all
                frameworkRefs @ (settings.RefGlobal |> List.map lookup |> List.filter (fun r -> not (List.contains r frameworkRefs)))

            // FSharp.Core unless the settings reference one: for .NET the SDK's own (next to
            // fsc.dll, the version the compiler is built with and what `dotnet build` picks by
            // default); for netstandard and .NET Framework the FSharp.Core package at a pinned
            // version (`DotNetFwk.fsharpCoreReference`), restored into the machine's cache
            let hasFSharpCore =
                refsNamed "FSharp.Core.dll" || (globalRefs |> List.exists (fileNamed "FSharp.Core.dll"))
            let! defaultFSharpCore =
                if hasFSharpCore then recipe { return [] }
                elif isNetcore then recipe { return DotNetFwk.sdkFSharpCore targetFwkInfo |> Option.toList }
                else
                    recipe {
                        let! dll = DotNetFwk.fsharpCoreReference targetFramework
                        return [ dll ]
                    }

            let args =
                seq {
                    yield "--nologo"
                    yield "--target:" + Impl.targetStr outFile.Name settings.Target
                    // the framework's references are passed explicitly, below
                    yield "--noframework"
                    if isNetstandard then
                        yield "--targetprofile:netstandard"
                    elif isNetcore then
                        yield "--targetprofile:netcore"
                    if not settings.Tailcalls then
                        yield "--tailcalls-"
                    if outFile <> File.undefined then
                        yield "--out:" + File.getFullName outFile
                    if settings.Doc <> File.undefined then
                        yield "--doc:" + File.getFullName settings.Doc
                    // one symbol per switch: fsc, unlike csc, takes '--define:A;B' as a
                    // single (and useless) symbol named "A;B"
                    yield! settings.Define |> List.map (fun symbol -> "--define:" + symbol)
                    yield! src |> List.map (fun f -> f.FullName)
                    yield! refs |> List.map (fun f -> "-r:" + f.FullName)
                    yield! defaultFSharpCore |> List.map ((+) "-r:")
                    yield! globalRefs |> List.map ((+) "-r:")
                    yield! resArgs |> List.map (fun (name, path) -> sprintf "--resource:%s,%s" path name)
                    yield! settings.CommandArgs
                } |> List.ofSeq

            // the SDK's fsc.dll is what gets recorded and hashed; `fscver`/`FSCVER` matter
            // only to the other providers (the registry's fsc.exe), so the variable is read
            // -- and becomes a dependency -- only there
            let! compilerPath =
                match DotNetFwk.fscCompiler fwkInfo with
                | Some dll -> recipe { return dll }
                | None ->
                    recipe {
                        let! fscVer = getVar "FSCVER"
                        match fwkInfo.FscTool ([settings.FscVersion; fscVer] |> Impl.coalesce) with
                        | Some tool -> return tool
                        | None -> return failwithf "'%s': F# compiler not found (framework '%s')" assemblyName fwkInfo.Version
                    }

            let composed = ofArgs args
            let f =
                { composed with
                    Fsc.Name = assemblyName
                    Fsc.Framework = targetFramework
                    Fsc.Directory = options.ProjectRoot
                    Fsc.Resources = resources
                    Fsc.Generated = composed.Generated @ Impl.runtimeConfig targetFramework (File.getFullName outFile) settings.Target
                    Fsc.Dependencies =
                        { composed.Dependencies with
                            Compiler = { Tool = "fsc"; Path = compilerPath; Sha256 = ""; Version = Csc.compilerVersion compilerPath } } }
            let rebuilt = f.Args
            if rebuilt <> args then
                failwithf "'%s': the command line rebuilt from the resolved compilation differs from the composed one:\n%s"
                    f.Name (Csc.diffList args rebuilt |> String.concat "\n")

            return f
        }

    /// The runner's options for composed settings: `FailOnError` from the settings and the
    /// environment of the framework they target. What `compile` runs with.
    let runOptions (settings: FscSettingsType) : Recipe<ExecContext, FscRunOptions> =
        recipe {
            let! _, fwkInfo = Csc.frameworkFor settings.TargetFramework
            return { FscRunOptions.FailOnError = settings.FailOnError
                     FscRunOptions.FscPath = None
                     FscRunOptions.Environment = fwkInfo.EnvVars }
        }

    /// <summary>
    /// Runs the compiler over a resolved compilation -- composed by `ofSettings`, or built by
    /// hand -- through the same runner as `Csc.run`: writes back missing or changed
    /// `Generated` files, creates the output directories (`--doc:` included), compiles each
    /// `.resx` whose `.resources` is missing, `needFiles` every input the command line names,
    /// and verifies the SHA-256 of every hashed reference and of the compiler before starting
    /// it; a mismatch fails the build. An empty hash (a composed compilation has none) is not
    /// checked. All arguments go into a response file; a `.dll` compiler (the SDK's
    /// `fsc.dll`) is started as `dotnet fsc.dll`.
    /// </summary>
    let run (options: FscRunOptions) (f: Fsc) : Recipe<ExecContext, unit> =
        recipe {
            let compiler = f.Dependencies.Compiler
            let args = f.Args
            let plan : CompilerRunner.RunPlan =
                { Name = f.Name
                  Tool = compiler.Tool
                  LogPrefix = "[fsc]"
                  CompilerPath = compiler.Path
                  CompilerVersion = compiler.Version
                  RequireCompiler = options.FscPath.IsNone
                  Launch =
                    match options.FscPath with
                    | Some tool -> CompilerRunner.Native tool
                    | None -> CompilerRunner.launchOf compiler.Path
                  Leading = []
                  ResponseArgs = args
                  ClientSwitches = []
                  ClientSwitchesNote = None
                  Inputs = FscArgs.inputs args
                  Outputs = FscArgs.outputs args
                  Hashed =
                    [ for r in f.Dependencies.References do yield r.Path, r.Sha256
                      yield compiler.Path, compiler.Sha256 ]
                  Generated = f.Generated
                  Resources = f.Resources
                  Environment = options.Environment
                  Directory = f.Directory
                  FailOnError = options.FailOnError
                  Diagnostics = Tool.diagnosticLevel Level.Verbose }
            do! CompilerRunner.run plan
        }

    /// <summary>
    /// F# compiler task in record syntax: `ofSettings`, then `run` with the options the
    /// settings imply (`runOptions`). What `fsc { ... }` runs; replaces the 3.4 function
    /// `Fsc settings`.
    /// </summary>
    /// <param name="settings">Compiler settings</param>
    /// <returns>Recipe compiling the target</returns>
    let compile (settings: FscSettingsType) : Recipe<ExecContext, unit> =
        recipe {
            do! trace Level.Debug "Fsc: settings=%A" settings
            let! f = ofSettings settings
            let! options = runOptions settings
            do! run options f
        }
