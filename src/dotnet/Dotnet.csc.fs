namespace Xake.Dotnet

[<AutoOpen>]
module CscImpl =

    open System.IO
    open Xake
    open Xake.Tasks

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
        }

    /// Default settings for the CSC task, so that you could only override required settings.
    let CscSettings = CscSettingsType.Default

    /// <summary>
    /// C# compiler task. Compiles the source fileset into the target assembly.
    /// </summary>
    /// <param name="settings">Compiler settings</param>
    /// <returns>Recipe compiling the target</returns>
    let Csc (settings:CscSettingsType) =

        recipe {
            do! trace Level.Debug "Csc: settings=%A" settings

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
                | s, _ when not <| System.String.IsNullOrWhiteSpace(s) -> s
                | _, Some s when s <> "" -> s
                | _ -> null

            let (globalRefs,nostdlib,noconfig) =
                match targetFramework with
                | null ->
                    let mapfn = (+) "/r:"
                    // TODO provide an option for user to explicitly specify all grefs (currently csc.rsp is used)
                    (settings.RefGlobal |> List.map mapfn), false, false
                | tgt ->
                    let fwk = Some tgt |> DotNetFwk.locateFramework in
                    let lookup = DotNetFwk.locateAssembly fwk
                    let mapfn = lookup >> ((+) "/r:")

                    ("mscorlib.dll" :: settings.RefGlobal |> List.map mapfn), true, true

            let args =
                seq {
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

                    yield! refs |> List.map ((fun f -> f.FullName) >> (+) "/r:")
                    yield! globalRefs

                    yield! resinfos |> List.map (fun(name,file,_) -> sprintf "/res:%s,%s" file.FullName name)
                    yield! settings.CommandArgs
                }

            let! netfxVar = getVar "NETFX"
            // the compiler is taken from the framework being targeted, unless NETFX says otherwise
            let dotnetFwk = match netfxVar with | Some _ -> netfxVar | None -> Option.ofObj targetFramework
            let fwkInfo = DotNetFwk.locateFramework dotnetFwk

// TODO for short args this is ok, otherwise use rsp file --    let commandLine = args |> escapeAndJoinArgs
            let rspFile = Path.GetTempFileName()
            File.WriteAllLines(rspFile, args |> Seq.map Impl.escapeArgument |> List.ofSeq)
            let commandLineArgs =
                seq {
                    if noconfig then
                        yield "/noconfig"
                    yield "@" + rspFile
                    }
            let cscTool = settings.CscPath |> function | Some v -> v | _ -> fwkInfo.CscTool

            // the response file and the resx files compiled to a temporary location have to go
            // regardless of how the compilation ends
            let tempFiles =
                rspFile :: (resinfos |> List.choose (fun (_, file, istemp) -> if istemp then Some file.FullName else None))
            let deleteTempFiles () =
                tempFiles |> List.iter (fun file -> try System.IO.File.Delete file with _ -> ())

            do! trace Info "compiling '%s' using framework '%s'" outFile.Name fwkInfo.Version
            do! trace Debug "Command line: '%s %s'" cscTool (args |> Seq.map Impl.escapeArgument |> String.concat "\r\n\t")

            try
                let! exitCode =
                    shell {
                        cmd cscTool
                        args commandLineArgs
                        envs fwkInfo.EnvVars
                        logprefix "[csc]"
                        stdoutlevel (Impl.levelFromString Level.Verbose)
                        erroutlevel (Impl.levelFromString Level.Verbose)
                    }

                do! Impl.failOnExitCode settings.FailOnError outFile.Name exitCode
            finally
                deleteTempFiles ()
        }

    /// Computation expression builder for the csc task.
    type CscSettingsBuilder() =

        [<CustomOperation("platform")>]  member __.Platform(s:CscSettingsType, value) =    {s with Platform = value}
        [<CustomOperation("target")>]    member __.Target(s:CscSettingsType, value) =    {s with Target = value}
        [<CustomOperation("targetfwk")>] member __.TargetFwk(s:CscSettingsType, value) = {s with TargetFramework = value}
        [<CustomOperation("out")>]       member __.OutFile(s:CscSettingsType, value) =   {s with Out = value}
        [<CustomOperation("src")>]       member __.SrcFiles(s:CscSettingsType, value) =  {s with Src = value}

        [<CustomOperation("ref")>]       member __.Ref(s:CscSettingsType, value) =         {s with Ref = s.Ref + value}
        [<CustomOperation("refif")>]     member __.Refif(s:CscSettingsType, cond, (value:Fileset)) = {s with Ref = s.Ref +? (cond,value)}

        [<CustomOperation("refs")>]      member __.Refs(s:CscSettingsType, value) =        {s with Ref = value}
        [<CustomOperation("grefs")>]     member __.RefGlobal(s:CscSettingsType, value) =   {s with RefGlobal = value}
        [<CustomOperation("resources")>] member __.Resources(s:CscSettingsType, value) =   {s with CscSettingsType.Resources = value :: s.Resources}
        [<CustomOperation("resourceslist")>] member __.ResourcesList(s:CscSettingsType, values) = {s with CscSettingsType.Resources = values @ s.Resources}

        [<CustomOperation("define")>]    member __.Define(s:CscSettingsType, value) =      {s with Define = value}
        [<CustomOperation("unsafe")>]    member __.Unsafe(s:CscSettingsType, value) =      {s with Unsafe = value}
        [<CustomOperation("cscpath")>]       member __.CscPath(s:CscSettingsType, value) =   {s with CscPath = Some value}

        /// <summary>Passes custom arguments to the compiler</summary>
        [<CustomOperation("args")>]       member __.Args(s:CscSettingsType, args) =   {s with CommandArgs = args}
        /// <summary>Does not fail the build on a compile error</summary>
        [<CustomOperation("nofailonerror")>] member __.NoFailOnError(s:CscSettingsType) = {s with FailOnError = false}

        member __.Bind(x, f) = f x
        member __.Yield(()) = CscSettingsType.Default
        member __.For(x, f) = f x

        member __.Zero() = CscSettingsType.Default
        member __.Run(s:CscSettingsType) = Csc s

    /// The csc task builder instance.
    let csc = CscSettingsBuilder()
