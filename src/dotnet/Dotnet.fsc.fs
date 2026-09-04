namespace Xake.Dotnet

[<AutoOpen>]
module FscImpl =

    open System.IO
    open Xake
    open Xake.Tasks

    /// <summary>
    /// Fsc (F# compiler) task settings.
    /// </summary>
    type FscSettingsType = {
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
        /// Target .NET framework
        TargetFramework: string
        /// Use specific FSC compiler version (only dotnet)
        FscVersion: string option
        /// Custom command-line arguments
        CommandArgs: string list
        /// Build fails on compile error.
        FailOnError: bool
        /// Do not reference the default CLI assemblies by default
        NoFramework: bool

        /// Generate tailcalls where possible.
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
            CommandArgs = []
            FailOnError = true
            NoFramework = false
            Tailcalls = true
        }

    /// <summary>
    /// Default settings for the Fsc task, so that you could only override required settings.
    /// </summary>
    let FscSettings = FscSettingsType.Default

    /// <summary>
    /// F# compiler task. Compiles the source fileset into the target assembly.
    /// </summary>
    /// <param name="settings">Compiler settings</param>
    /// <returns>Recipe compiling the target</returns>
    let Fsc (settings:FscSettingsType) =

        recipe {
            do! trace Level.Debug "Fsc: settings=%A" settings

            let! options = getCtxOptions()
            let getFiles = toFileList options.ProjectRoot

            let! outFile =
                if settings.Out = File.undefined then
                    getTargetFile()
                else
                    settings.Out |> recipe.Return

            let resinfos = settings.Resources |> List.collect (Impl.collectResInfo options.ProjectRoot) |> List.map Impl.compileResxFiles
            let resfiles = resinfos |> List.choose (fun (_, file, istemp) -> if istemp then None else Some file)

            let (Filelist src)  = settings.Src |> getFiles
            let (Filelist refs) = settings.Ref |> getFiles

            do! needFiles (Filelist (src @ refs @ resfiles))

            let! globalTargetFwk = getVar "NETFX-TARGET"
            let targetFramework =
                match settings.TargetFramework, globalTargetFwk with
                | s, _ when not (System.String.IsNullOrWhiteSpace s) -> s
                | _, Some s when s <> "" -> s
                | _ -> null

            do! trace Debug "targetFramework: %s" targetFramework

            // fsc reads a leading '/' as a path on Unix, so long options must use '--' there;
            // '-r:' is understood on both platforms
            let opt (name: string) = (if Env.isWindows then "/" else "--") + name
            let refOpt = if Env.isWindows then "/r:" else "-r:"

            let (globalRefs,noframework) =
                let mapfn = (+) refOpt
                match targetFramework with
                | null ->
                    // TODO provide an option for user to explicitly specify all grefs (currently csc.rsp is used)
                    (settings.RefGlobal |> List.map mapfn), false
                | tgt ->
                    let fwk = Some tgt |> DotNetFwk.locateFramework in
                    let lookup = DotNetFwk.locateAssembly fwk
                    ("mscorlib.dll" :: settings.RefGlobal |> List.map (lookup >> mapfn)), true

            let args =
                seq {
                    yield opt "nologo"

                    yield opt "target:" + Impl.targetStr outFile.Name settings.Target
                    //yield opt "platform:" + Impl.platformStr settings.Platform

                    if settings.NoFramework || noframework then
                        yield opt "noframework"

                    if outFile <> File.undefined then
                        yield sprintf "%sout:%s" (opt "") (File.getFullName outFile)

                    if not (List.isEmpty settings.Define) then
                        yield opt "define:" + (settings.Define |> String.concat ";")

                    yield! src |> List.map (fun f -> f.FullName)

                    yield! refs |> List.map ((fun f -> f.FullName) >> (+) refOpt)
                    yield! globalRefs

                    yield! resinfos |> List.map (fun(name,file,_) -> sprintf "%sresource:%s,%s" (opt "") file.FullName name)
                    yield! settings.CommandArgs
                }

            let! netfxVar = getVar "NETFX"
            // the compiler is taken from the framework being targeted, unless NETFX says otherwise
            let dotnetFwk = match netfxVar with | Some _ -> netfxVar | None -> Option.ofObj targetFramework
            let fwkInfo = DotNetFwk.locateFramework dotnetFwk

            let! fscVer = getVar "FSCVER"
            let fsc = 
                match fwkInfo.FscTool ([settings.FscVersion; fscVer] |> Impl.coalesce) with
                | Some tool -> tool
                | None -> ""
            if fsc = "" then
                do! trace Error "('%s') failed: F# compiler not found" outFile.Name
                if settings.FailOnError then failwithf "Exiting due to FailOnError set on '%s'" outFile.Name

            let commandLineArgs = args |> Seq.map Impl.escapeArgument

            // the resx files compiled to a temporary location have to go regardless of how the
            // compilation ends
            let deleteTempFiles () =
                resinfos
                |> List.choose (fun (_, file, istemp) -> if istemp then Some file.FullName else None)
                |> List.iter (fun file -> try System.IO.File.Delete file with _ -> ())

            do! trace Info "compiling '%s' using framework '%s'" outFile.Name fwkInfo.Version
            do! trace Debug "Command line: '%s %s'" fsc (commandLineArgs |> String.concat "\r\n\t")

            try
                let! exitCode =
                    shell {
                        cmd fsc
                        args commandLineArgs
                        envs fwkInfo.EnvVars
                        logprefix "[fsc]"
                        stdoutlevel (Impl.levelFromString Level.Verbose)
                        erroutlevel (Impl.levelFromString Level.Verbose)
                    }

                do! Impl.failOnExitCode settings.FailOnError outFile.Name exitCode
            finally
                deleteTempFiles ()
        }

    /// Computation expression builder for the fsc task.
    type FscSettingsBuilder() =

        /// <summary>Limits which platforms the code can run on</summary>
        [<CustomOperation("platform")>]  member __.Platform(s:FscSettingsType, value) =  {s with Platform = value}
        /// <summary>Specifies the format of the output file</summary>
        [<CustomOperation("target")>]    member __.Target(s:FscSettingsType, value) =    {s with Target = value}
        /// <summary>Specifies the .NET framework to compile against</summary>
        [<CustomOperation("targetfwk")>] member __.TargetFwk(s:FscSettingsType, value) = {s with TargetFramework = value}
        /// <summary>Specifies the output file name</summary>
        [<CustomOperation("out")>]       member __.OutFile(s:FscSettingsType, value) =   {s with Out = value}
        /// <summary>Sets the source fileset</summary>
        [<CustomOperation("src")>]       member __.SrcFiles(s:FscSettingsType, value) =  {s with Src = value}

        /// <summary>Adds assemblies to reference</summary>
        [<CustomOperation("ref")>]       member __.Ref(s:FscSettingsType, value) =       {s with Ref = s.Ref + value}
        /// <summary>Adds assemblies to reference when the condition holds</summary>
        [<CustomOperation("refif")>]     member __.Refif(s:FscSettingsType, cond, (value:Fileset)) = {s with Ref = s.Ref +? (cond,value)}
        /// <summary>Sets the assemblies to reference</summary>
        [<CustomOperation("refs")>]      member __.Refs(s:FscSettingsType, value) =      {s with Ref = value}
        /// <summary>References the specified assemblies from GAC</summary>
        [<CustomOperation("grefs")>]     member __.RefGlobal(s:FscSettingsType, value) = {s with RefGlobal = value}

        /// <summary>Embeds a resource set</summary>
        [<CustomOperation("resources")>] member __.Resources(s:FscSettingsType, value) = {s with FscSettingsType.Resources = value :: s.Resources}
        /// <summary>Embeds several resource sets</summary>
        [<CustomOperation("resourceslist")>] member __.ResourcesList(s:FscSettingsType, values) = {s with FscSettingsType.Resources = values @ s.Resources}

        /// <summary>Defines conditional compilation symbols</summary>
        [<CustomOperation("define")>]    member __.Define(s:FscSettingsType, value) =    {s with Define = value}
        /// <summary>Uses a specific F# compiler version</summary>
        [<CustomOperation("fscver")>]    member __.FscVer(s:FscSettingsType, value) =    {s with FscVersion = Some value}
        /// <summary>Does not reference the default CLI assemblies</summary>
        [<CustomOperation("noframework")>] member __.NoFramework(s:FscSettingsType) =   {s with NoFramework = true}
        /// <summary>Turns tailcall generation off</summary>
        [<CustomOperation("notailcalls")>] member __.NoTailcalls(s:FscSettingsType) =   {s with Tailcalls = false}
        /// <summary>Passes custom arguments to the compiler</summary>
        [<CustomOperation("args")>]      member __.Args(s:FscSettingsType, args) =       {s with CommandArgs = args}
        /// <summary>Does not fail the build on a compile error</summary>
        [<CustomOperation("nofailonerror")>] member __.NoFailOnError(s:FscSettingsType) = {s with FailOnError = false}

        member __.Bind(x, f) = f x
        member __.Yield(()) = FscSettingsType.Default
        member __.For(x, f) = f x

        member __.Zero() = FscSettingsType.Default
        member __.Run(s:FscSettingsType) = Fsc s

    /// The fsc task builder instance.
    let fsc = FscSettingsBuilder()
