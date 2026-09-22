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
        /// Compiler package version (`Microsoft.Net.Compilers.Toolset`): when set, `resolve`
        /// takes `csc.dll` from that package in the NuGet cache instead of the SDK's own, so
        /// the compiler is a pinned, hashed dependency in the lock rather than whatever the
        /// SDK happens to ship. `CscPath` still overrides everything, this included.
        Toolset: string option
        /// A project imported by `Project.import`, or resolved by a previous `csc {}` run: when
        /// set, the task replays that project's command line verbatim instead of composing one
        /// from `Src`/`Ref`/`Define`/... (those and `Target`/`Platform`/`Out`/`TargetFramework`
        /// are ignored in this mode). See `resolve` and `run`.
        FromLock: Lock.Project option
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
            FromLock = None
        }

    /// Default settings for the CSC task, so that you could only override required settings.
    let CscSettings = CscSettingsType.Default

    /// <summary>
    /// Runs the compiler over an already-resolved compilation: `project` is exactly what would
    /// go into a lock file, whether it came from `Project.import`, from a hand-built
    /// `Lock.Project`, or from `resolve` composing one from `Src`/`Ref`/... at recipe time. This
    /// is the only place that shells out to csc; both `csc { fromlock ... }` and the composed
    /// `csc { src ... }` end up here.
    ///
    /// Before running the compiler this: writes back any `Generated` file that is missing or
    /// whose content changed (the lock/resolved project is the source of truth for
    /// msbuild-generated inputs like AssemblyInfo.cs -- for the composed mode there simply are
    /// none), creates the output directories, and verifies the SHA-256 of every hashed
    /// reference/analyzer and of the compiler itself against what is on disk -- a mismatch fails
    /// the build rather than silently compiling against something other than what was imported.
    /// An empty hash (the composed mode never records one) skips that check.
    ///
    /// `envVars` carries the environment the compiler needs to run (e.g. the framework's,
    /// resolved via `DotNetFwk`) -- `Lock.Project` is serialized as the lock file format and has
    /// no room for it, so it travels alongside instead. `extraTempFiles` are deleted together
    /// with the response file once the compiler exits, whatever the outcome -- the composed
    /// mode's resx-compiled temporaries.
    /// </summary>
    /// The path `csc.dll` would have under a `Microsoft.Net.Compilers.Toolset`-shaped package
    /// (`<nugetRoot>/<packageId>/<version>/tasks/netcore/bincore/csc.dll`), restoring the
    /// package into the NuGet cache first when it is not there yet. Shared by `resolve`'s
    /// `toolset` operation (composing a lock) and `run`'s "make the compiler available" step
    /// (replaying a lock that names a package not yet restored on this machine).
    let private restoreToolsetCompiler (packageId: string) (version: string) =
        let dir =
            DotNetFwk.sdkImpl.nugetRoot () </> packageId.ToLowerInvariant() </> version
            </> "tasks" </> "netcore" </> "bincore"
        let cscDll = dir </> "csc.dll"
        if not (File.Exists cscDll) then
            DotNetFwk.sdkImpl.restorePackage packageId version
        cscDll

    /// Traces `msg` as an error and, when `failOnError`, fails the build with it -- the same
    /// shape as `Impl.failOnExitCode` and the hash-mismatch check below.
    let private failStep (failOnError: bool) (msg: string) =
        recipe {
            do! trace Error "%s" msg
            if failOnError then failwith msg
        }

    /// Makes `project.Compiler.Path` available on this machine before the hash check runs,
    /// for a lock imported (or resolved) somewhere else:
    ///  - already on disk: nothing to do.
    ///  - under `$(NuGetPackageRoot)`: the compiler is a `Microsoft.Net.Compilers.Toolset`-shaped
    ///    package (see `resolve`'s `toolset`) that simply is not restored yet on this machine --
    ///    restore it, the same way `toolset` does. A hash mismatch after a successful restore is
    ///    still a hard error (a different package build), left to the check that follows.
    ///  - under `$(DotnetRoot)/sdk/<version>/`: an SDK this machine does not have; nothing to
    ///    restore, so this fails immediately naming the SDK version.
    ///  - anywhere else: the path simply does not exist.
    let private ensureCompilerAvailable (settings: CscSettingsType) (project: Lock.Project) =
        recipe {
            if File.Exists project.Compiler.Path then
                ()
            else
                let path = project.Compiler.Path.Replace('\\', '/')
                let comparer = if Env.isUnix then System.StringComparison.Ordinal else System.StringComparison.OrdinalIgnoreCase
                let root token = Fsproj.roots () |> List.tryFind (fst >> (=) token) |> Option.map snd
                let under (r: string) = path.StartsWith(r + "/", comparer)

                match root "$(NuGetPackageRoot)" |> Option.filter under with
                | Some nugetRoot ->
                    let rest = path.Substring(nugetRoot.Length + 1).Split('/')
                    let packageId, version = rest.[0], rest.[1]
                    do! trace Info "restoring compiler package %s %s" packageId version
                    restoreToolsetCompiler packageId version |> ignore
                    if not (File.Exists project.Compiler.Path) then
                        do! failStep settings.FailOnError
                                (sprintf "'%s': the compiler %s is not available and restoring %s %s did not provide it"
                                    project.Name project.Compiler.Path packageId version)
                | None ->
                    match root "$(DotnetRoot)" |> Option.filter under with
                    | Some dotnetRoot ->
                        let sdkPrefix = dotnetRoot + "/sdk/"
                        let msg =
                            if path.StartsWith(sdkPrefix, comparer) then
                                let version = path.Substring(sdkPrefix.Length).Split('/').[0]
                                sprintf "'%s': the lock names the compiler of SDK %s (%s), which is not installed; install that SDK or re-import with the installed one"
                                    project.Name version project.Compiler.Path
                            else
                                sprintf "'%s': the compiler %s named by the lock is not installed" project.Name project.Compiler.Path
                        do! failStep settings.FailOnError msg
                    | None ->
                        do! failStep settings.FailOnError
                                (sprintf "'%s': the compiler %s named by the lock does not exist" project.Name project.Compiler.Path)
        }

    let private run (settings: CscSettingsType) (project: Lock.Project) (envVars: (string * string) list) (extraTempFiles: string list) =
        recipe {
            do! trace Info "compiling '%s' (%s %s)" project.Name project.Compiler.Tool project.Compiler.Sdk

            // a lock built on another machine may name a compiler this one does not have yet
            // (a toolset package not restored, an SDK not installed): make it available -- or
            // fail with a clear reason -- before the hash check below even looks at it
            do! ensureCompilerAvailable settings project

            // the compiler is hashed (below) but was never a tracked dependency, so an SDK or
            // toolset update that changes csc.dll's bytes left the target looking up to date
            // and the hash check never ran (conceptual-review.md 2.4)
            do! needFiles (Filelist [File.make project.Compiler.Path])

            // the resolved project is the source of truth for what msbuild (or the composed
            // front end) generated (assembly attributes, TFM defines): write it back whenever
            // it is missing or someone touched it
            for (path, content) in project.Generated do
                let upToDate = File.Exists path && File.ReadAllText path = content
                if not upToDate then
                    let dir = Path.GetDirectoryName path
                    if not (Impl.isEmpty dir) then Directory.CreateDirectory dir |> ignore
                    File.WriteAllText (path, content)

            for path in CscArgs.outputs project.Args do
                let dir = Path.GetDirectoryName path
                if not (Impl.isEmpty dir) then Directory.CreateDirectory dir |> ignore

            // a resx `PrepareResources` compiled is named by a `/resource:` switch as the
            // `.resources` file it produced, not the resx itself -- that file has to exist
            // before the hash check and the `needFiles` below see it. The resx is `needFiles`d
            // so the engine decides whether an edit reruns this recipe; regenerating only when
            // the `.resources` output is missing (not on a timestamp comparison) keeps `run`
            // from being a second rebuilder next to the engine's (conceptual-review.md 2.3) --
            // the gate is "does it exist", staleness is the engine's call, not this recipe's.
            do! needFiles (Filelist (project.Resources |> List.map (fst >> File.make)))
            for (resx, resourcesFile) in project.Resources do
                if not (File.Exists resourcesFile) then
                    Resx.compile resx resourcesFile

            // everything that carries a hash has to be exactly what was imported, or the
            // compilation is not the one the project describes
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

            // the generated files have to exist before the inputs are demanded. Note: for the
            // composed mode this now needs everything the args name -- including the
            // framework's global references (mscorlib.dll etc) -- rather than only the sources,
            // refs and resource files it used to `needFiles` directly. That is intended.
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

            // the response file and any temporary the front end produced (the composed mode's
            // resx-compiled resources) have to go regardless of how the compilation ends
            let tempFiles = rspFile :: extraTempFiles
            let deleteTempFiles () =
                tempFiles |> List.iter (fun file -> try System.IO.File.Delete file with _ -> ())

            let cscTool, extraArgs =
                match settings.CscPath with
                | Some tool -> tool, []
                // the compiler path from `DotNetFwk` may be a native launcher rather than a
                // managed dll (the "run directly" branch below covers that case too)
                | None when Impl.endsWith ".dll" project.Compiler.Path -> "dotnet", [project.Compiler.Path]
                | None -> project.Compiler.Path, []

            do! trace Debug "Command line: '%s %s'" cscTool
                    ((extraArgs @ List.ofSeq commandLineArgs) |> String.concat " ")

            try
                let! exitCode =
                    shell {
                        cmd cscTool
                        args (Seq.append extraArgs commandLineArgs)
                        envs envVars
                        // `dotnet build` runs csc with cwd = the project's directory; some
                        // compiler inputs are resolved against it rather than against an
                        // argument on the command line -- an XML-doc `<include file='../..'>`
                        // path is resolved relative to the *compiler's* working directory, not
                        // per source file. `project.Directory` records exactly that (see its
                        // doc comment on `Lock.Project`); without it, a project compiled from a
                        // different cwd than its own directory can fail with CS1589 even though
                        // every file the args name is absolute and present. Args, `/out:` etc.
                        // are already absolute, so this only affects paths that never made it
                        // onto the command line.
                        workdir project.Directory
                        logprefix "[csc]"
                        stdoutlevel (Impl.levelFromString Level.Verbose)
                        erroutlevel (Impl.levelFromString Level.Verbose)
                    }

                do! Impl.failOnExitCode settings.FailOnError project.Name exitCode
            finally
                deleteTempFiles ()
        }

    /// <summary>
    /// Composes a `Lock.Project` from `Src`/`Ref`/`RefGlobal`/`Resources`/`Define`/`Target`/
    /// `Platform`/`Unsafe`/`TargetFramework`/`CommandArgs` at recipe time -- the same work the
    /// composed mode always did, just stopping short of running the compiler. The argument list
    /// it produces is identical, in the same order, to what the old composed mode ran: `/noconfig`
    /// (first, when the target framework requires it), `/nologo`, `/target:`, `/platform:`,
    /// `/unsafe`, `/nostdlib+`, `/out:`, `/define:`, sources, `/r:` refs, global refs, `/res:`,
    /// `CommandArgs`.
    ///
    /// Returns the resolved project together with the framework's environment variables (see
    /// `run`) and the resx-compiled temporary files the caller has to delete once the
    /// compilation is done.
    /// </summary>
    let private resolve (settings: CscSettingsType) =
        recipe {
            let! options = getCtxOptions()
            let getFiles = toFileList options.ProjectRoot

            let! outFile =
                if settings.Out = File.undefined then
                    getTargetFile()
                else
                    settings.Out |> recipe.Return

            let resinfos = settings.Resources |> List.collect (Impl.collectResInfo options.ProjectRoot) |> List.map Impl.compileResxFiles
            let resfiles = resinfos |> List.choose (fun (_, file, istemp) -> if istemp then None else Some file)
            let tempFiles = resinfos |> List.choose (fun (_, file, istemp) -> if istemp then Some file.FullName else None)

            let (Filelist src)  = settings.Src |> getFiles
            let (Filelist refs) = settings.Ref |> getFiles

            do! needFiles (Filelist (src @ refs @ resfiles))

            let! globalTargetFwk = getVar "NETFX-TARGET"
            let targetFramework =
                match settings.TargetFramework, globalTargetFwk with
                | s, _ when not <| System.String.IsNullOrWhiteSpace(s) -> s
                | _, Some s when s <> "" -> s
                | _ -> null

            let (globalRefPaths, nostdlib, noconfig) =
                match targetFramework with
                | null ->
                    // TODO provide an option for user to explicitly specify all grefs (currently csc.rsp is used)
                    settings.RefGlobal, false, false
                | tgt ->
                    let fwk = Some tgt |> DotNetFwk.locateFramework in
                    let lookup = DotNetFwk.locateAssembly fwk
                    (("mscorlib.dll" :: settings.RefGlobal) |> List.map lookup), true, true

            let globalRefs = globalRefPaths |> List.map ((+) "/r:")

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

                    yield! refs |> List.map ((fun f -> f.FullName) >> (+) "/r:")
                    yield! globalRefs

                    yield! resinfos |> List.map (fun(name,file,_) -> sprintf "/res:%s,%s" file.FullName name)
                    yield! settings.CommandArgs
                } |> List.ofSeq

            let! netfxVar = getVar "NETFX"
            // the compiler is taken from the framework being targeted, unless NETFX says otherwise
            let dotnetFwk = match netfxVar with | Some _ -> netfxVar | None -> Option.ofObj targetFramework
            let fwkInfo = DotNetFwk.locateFramework dotnetFwk

            // references and env vars always come from the targeted framework -- `toolset`
            // only replaces the compiler executable, restoring the package into the NuGet
            // cache first when it is not there yet (the same mechanism `DotNetFwk.sdkImpl`
            // uses for the reference-assemblies packages)
            let compilerPath =
                match settings.Toolset with
                | None -> fwkInfo.CscTool
                | Some version ->
                    let cscDll = restoreToolsetCompiler "Microsoft.Net.Compilers.Toolset" version
                    if not (File.Exists cscDll) then
                        failwithf "compiler package Microsoft.Net.Compilers.Toolset %s could not be restored (expected '%s')" version cscDll
                    cscDll

            let references =
                (refs |> List.map (fun f -> f.FullName)) @ globalRefPaths
                |> List.map (fun path -> { Lock.Path = path; Lock.Sha256 = "" })

            let project : Lock.Project = {
                Name = Path.GetFileNameWithoutExtension outFile.Name
                Project = ""
                Directory = options.ProjectRoot
                // `Sdk` names the reference-assembly framework, as everywhere else in this
                // record -- the package version (when `Toolset` is set) is in `Path` instead,
                // there being nowhere else in `Compiler` for it
                Compiler = { Tool = "csc"; Path = compilerPath; Sha256 = ""; Sdk = fwkInfo.Version }
                Args = args
                References = references
                Analyzers = []
                ProjectRefs = []
                Imports = []
                Generated = []
                Resources = []
                Properties = Map.empty
            }

            return project, fwkInfo.EnvVars, tempFiles
        }

    /// <summary>
    /// Resolves composed `csc {}` settings into a `Lock.Project` without compiling -- the
    /// smallest piece `lock-from-settings.md` recommends (1b) so a lock-recording rule can
    /// write out what a compilation would look like, the way `Project.import` does for an
    /// msbuild project. Both feed `Csc { fromlock = Some project }`.
    ///
    /// Cleans up the resx-compiled temp files `resolve` creates before returning, since there
    /// is nothing here to run the compiler against them for. Consequence: when the settings
    /// carry `.resx` resources, the returned project's `/res:` arguments name files that no
    /// longer exist -- it is NOT compilable as is (recording/diffing only) until `resolve`
    /// routes resx through `Resources` with permanent outputs (tracker: "Lock from composed
    /// csc settings" / lock-from-settings.md scenario 9's common trap). Does not change the
    /// private `resolve`'s own behaviour, or how `Csc` uses it.
    ///
    /// Named `CscLock.resolve`, not `Csc.resolve`: F# does not let a module and a `let`-bound
    /// function share one name in a namespace the way it lets a `type` and a `module` share
    /// one (`[&lt;CompilationRepresentation(ModuleSuffix)&gt;]`) -- verified by compiling a
    /// minimal repro (`let Csc x = ...` alongside `module Csc = ...` leaves `Csc.resolve`
    /// unresolved, FS0039, in both definition orders). `Csc` stays the function it always was.
    /// </summary>
    module CscLock =
        let resolve (settings: CscSettingsType) =
            recipe {
                let! project, _, tempFiles = resolve settings
                tempFiles |> List.iter (fun file -> try System.IO.File.Delete file with _ -> ())
                return project
            }

    /// <summary>
    /// C# compiler task. Compiles the source fileset into the target assembly.
    ///
    /// With `fromlock` set, replays that `Lock.Project`'s command line exactly (see `run`).
    /// Otherwise composes one from the settings (see `resolve`) and runs it the same way. Either
    /// way there is one resolved form -- a `Lock.Project` -- and one runner: settings are intent,
    /// `Lock.Project` is the resolved compilation.
    /// </summary>
    /// <param name="settings">Compiler settings</param>
    /// <returns>Recipe compiling the target</returns>
    let Csc (settings:CscSettingsType) =

      match settings.FromLock with
      | Some project -> run settings project [] []
      | None ->

        recipe {
            do! trace Level.Debug "Csc: settings=%A" settings
            let! (project, envVars, tempFiles) = resolve settings
            do! run settings project envVars tempFiles
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
        /// <summary>Takes `csc.dll` from `Microsoft.Net.Compilers.Toolset/&lt;version&gt;` in the
        /// NuGet cache (restoring the package if it is missing) instead of the SDK's own, so the
        /// compiler is a pinned, hashed dependency in the lock. `cscpath` still overrides this.</summary>
        [<CustomOperation("toolset")>]       member __.Toolset(s:CscSettingsType, version: string) = {s with Toolset = Some version}
        /// <summary>Replays a project imported by `Project.import` (or resolved by an earlier
        /// `csc {}`) verbatim; see `FromLock`</summary>
        [<CustomOperation("fromlock")>]    member __.FromLock(s:CscSettingsType, project) = {s with FromLock = Some project}

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
