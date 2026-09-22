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
        /// A project imported by `Project.import`: when set, the task replays that project's
        /// command line verbatim instead of composing one from `Src`/`Ref`/`Define`/... (those
        /// and `Target`/`Platform`/`Out`/`TargetFramework` are ignored in this mode). See
        /// `compileFromLock`.
        Invocation: Lock.Project option
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
            Invocation = None
        }

    /// Default settings for the CSC task, so that you could only override required settings.
    let CscSettings = CscSettingsType.Default

    /// <summary>
    /// Compiles a project imported by `Project.import`, replaying its command line exactly as
    /// msbuild would have run it. Nothing is composed: the sources, references, defines and
    /// output are whatever `project.Args` says. Before running the compiler this:
    /// writes back any `Generated` file that is missing or whose content changed (the lock is
    /// the source of truth for msbuild-generated inputs like AssemblyInfo.cs), creates the
    /// output directories, and verifies the SHA-256 of every hashed reference/analyzer and of
    /// the compiler itself against what is on disk -- a mismatch fails the build rather than
    /// silently compiling against something other than what was imported.
    /// </summary>
    let private compileFromLock (settings: CscSettingsType) (project: Lock.Project) =
        recipe {
            do! trace Info "compiling '%s' from the lock (%s %s)" project.Name project.Compiler.Tool project.Compiler.Sdk

            // the lock is the source of truth for what msbuild generated (assembly attributes,
            // TFM defines): write it back whenever it is missing or someone touched it
            for (path, content) in project.Generated do
                let upToDate = File.Exists path && File.ReadAllText path = content
                if not upToDate then
                    let dir = Path.GetDirectoryName path
                    if not (Impl.isEmpty dir) then Directory.CreateDirectory dir |> ignore
                    File.WriteAllText (path, content)

            for path in CscArgs.outputs project.Args do
                let dir = Path.GetDirectoryName path
                if not (Impl.isEmpty dir) then Directory.CreateDirectory dir |> ignore

            // everything that carries a hash has to be exactly what was imported, or the
            // compilation is not the one the lock describes
            let mismatches =
                let check (path: string) (expected: string) =
                    if expected = "" then None
                    else
                        let actual = if File.Exists path then Lock.sha256 path else "missing"
                        if actual = expected then None else Some (path, expected, actual)
                [ for r in project.References do yield check r.Path r.Sha256
                  for a in project.Analyzers do yield check a.Path a.Sha256
                  yield check project.Compiler.Path project.Compiler.Sha256 ]
                |> List.choose id

            if not (List.isEmpty mismatches) then
                let detail =
                    mismatches
                    |> List.map (fun (path, expected, actual) -> sprintf "%s: expected %s, got %s" path expected actual)
                    |> String.concat "\n"
                do! trace Error "('%s') hash mismatch:\n%s" project.Name detail
                if settings.FailOnError then
                    failwithf "('%s') hash mismatch:\n%s" project.Name detail

            // the generated files have to exist before the inputs are demanded
            do! needFiles (Filelist (CscArgs.inputs project.Args |> List.map File.make))

            // csc warns CS2023 and ignores /noconfig when it is inside the response file, so
            // it has to stay on the command line and everything else goes into the rsp
            let args = project.Args
            let noconfig = args |> List.contains "/noconfig"
            let rspArgs = args |> List.filter ((<>) "/noconfig")

            let rspFile = Path.GetTempFileName()
            File.WriteAllLines (rspFile, rspArgs |> List.map Impl.escapeArgument)
            let commandLineArgs =
                seq {
                    if noconfig then yield "/noconfig"
                    yield "@" + rspFile
                }

            let deleteTempFiles () = try System.IO.File.Delete rspFile with _ -> ()

            let cscTool, extraArgs =
                match settings.CscPath with
                | Some tool -> tool, []
                | None when Impl.endsWith ".dll" project.Compiler.Path -> "dotnet", [project.Compiler.Path]
                | None -> project.Compiler.Path, []

            do! trace Debug "Command line: '%s %s'" cscTool
                    ((extraArgs @ List.ofSeq commandLineArgs) |> String.concat " ")

            try
                let! exitCode =
                    shell {
                        cmd cscTool
                        args (Seq.append extraArgs commandLineArgs)
                        logprefix "[csc]"
                        stdoutlevel (Impl.levelFromString Level.Verbose)
                        erroutlevel (Impl.levelFromString Level.Verbose)
                    }

                do! Impl.failOnExitCode settings.FailOnError project.Name exitCode
            finally
                deleteTempFiles ()
        }

    /// <summary>
    /// C# compiler task. Compiles the source fileset into the target assembly.
    /// </summary>
    /// <param name="settings">Compiler settings</param>
    /// <returns>Recipe compiling the target</returns>
    let Csc (settings:CscSettingsType) =

      match settings.Invocation with
      | Some project -> compileFromLock settings project
      | None ->

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
        /// <summary>Replays a project imported by `Project.import` verbatim; see `Invocation`</summary>
        [<CustomOperation("invocation")>]    member __.Invocation(s:CscSettingsType, project) = {s with Invocation = Some project}

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
