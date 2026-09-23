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
        /// Lock file recording this compilation, relative to the project root (or absolute).
        /// Strict, `npm ci`-like semantics: missing -- record it and compile; present -- the
        /// resolved settings must match it or the build fails; matching -- compile from the
        /// recorded entry, whose hashes then gate the build. Updating is explicit: delete the
        /// file, or call `CscLock.record` from a target of the script's own.
        Lock: string option
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
            Lock = None
        }

    /// Default settings for the CSC task, so that you could only override required settings.
    let CscSettings = CscSettingsType.Default

    /// What the runner itself needs, as opposed to what is being compiled: how to react to a
    /// compile error, and which executable to run. Everything else about a compilation lives in
    /// the `Lock.Entry` handed to the runner. Kept apart from `CscSettingsType` so that
    /// replaying a lock does not go through a settings record whose other fields would be
    /// silently ignored (conceptual-review.md 2.2).
    type RunOptions = {
        /// Build fails on compile error.
        FailOnError: bool
        /// Path to the csc executable, overriding the compiler the project names.
        CscPath: string option
        /// Where the packages the entry names live, and whether one that is missing may be
        /// fetched. The default is the machine's own NuGet cache with restore on -- what the
        /// compiler restore has always done, now for references and analyzers too.
        Restore: Restore.Options
    } with static member Default = {
            FailOnError = true
            CscPath = None
            Restore = Restore.Options.Default
        }

    /// <summary>
    /// Runs the compiler over an already-resolved compilation: `entry` is exactly what would
    /// go into a lock file, whether it came from `Project.import`, from a hand-built
    /// `Lock.Entry`, or from `resolve` composing one from `Src`/`Ref`/... at recipe time. This
    /// is the only place that shells out to csc; both `CscLock.compile` and the composed
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
    /// resolved via `DotNetFwk`) -- `Lock.Entry` is serialized as the lock file format and has
    /// no room for it, so it travels alongside instead.
    /// </summary>
    /// The path `csc.dll` would have under a `Microsoft.Net.Compilers.Toolset`-shaped package
    /// (`<packageRoot>/<packageId>/<version>/tasks/netcore/bincore/csc.dll`), fetching the
    /// package into the folder first when it is not there yet. This is `resolve`'s `toolset`
    /// operation, which composes a lock and therefore has no lock to read the package out of.
    /// Replaying a lock does not come here: `run`'s restore step treats the compiler as one
    /// package among the entry's others (`Restore.ensure`).
    let private restoreToolsetCompiler (options: Restore.Options) (packageId: string) (version: string) =
        recipe {
            let cscDll =
                Restore.packageRoot options </> packageId.ToLowerInvariant() </> version
                </> "tasks" </> "netcore" </> "bincore" </> "csc.dll"
            if not (File.Exists cscDll) then
                do! Restore.download options [ packageId, version ]
            return cscDll
        }

    /// Traces `msg` as an error and, when the options say so, fails the build with it -- the
    /// same shape as `Impl.failOnExitCode` and the hash-mismatch check below.
    let private failStep (options: RunOptions) (msg: string) =
        recipe {
            do! trace Error "%s" msg
            if options.FailOnError then failwith msg
        }

    /// Accounts for a compiler the lock names that this machine still does not have, once the
    /// restore step (`Restore.ensure`, which treats the compiler as one package among the
    /// entry's references and analyzers) has had its chance. It used to restore the compiler
    /// itself; that was the one case of the general mechanism this module implemented on its
    /// own, and all that is left here is the part that is specific to the compiler -- saying
    /// what went wrong:
    ///  - already on disk: nothing to do.
    ///  - under the package folder: a `Microsoft.Net.Compilers.Toolset`-shaped package (see
    ///    `resolve`'s `toolset`) that the restore did not, or was not allowed to, provide. A
    ///    hash mismatch after a successful restore is a different package build, not a missing
    ///    one, and is left to the check that follows.
    ///  - under `$(DotnetRoot)/sdk/<version>/`: an SDK this machine does not have; nothing to
    ///    restore, so this fails immediately naming the SDK version.
    ///  - anywhere else: the path simply does not exist.
    let private ensureCompilerAvailable (options: RunOptions) (entry: Lock.Entry) =
        recipe {
            let compiler = entry.Dependencies.Compiler
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
                    do! failStep options
                            (sprintf "'%s': the compiler %s is not available and restoring %s %s did not provide it"
                                entry.Name compiler.Path packageId version)
                | None ->
                    match Roots.dotnetRoot () |> Option.map normalize |> Option.filter under with
                    | Some dotnetRoot ->
                        let sdkPrefix = dotnetRoot + "/sdk/"
                        let msg =
                            if path.StartsWith(sdkPrefix, comparer) then
                                let version = path.Substring(sdkPrefix.Length).Split('/').[0]
                                sprintf "'%s': the lock names the compiler of SDK %s (%s), which is not installed; install that SDK or re-import with the installed one"
                                    entry.Name version compiler.Path
                            else
                                sprintf "'%s': the compiler %s named by the lock is not installed" entry.Name compiler.Path
                        do! failStep options msg
                    | None ->
                        do! failStep options
                                (sprintf "'%s': the compiler %s named by the lock does not exist" entry.Name compiler.Path)
        }

    let private run (options: RunOptions) (entry: Lock.Entry) (envVars: (string * string) list) =
        recipe {
            let compiler = entry.Dependencies.Compiler
            do! trace Info "compiling '%s' (%s %s)" entry.Name compiler.Tool compiler.Version

            // a lock built on another machine names packages this one may not have yet -- a
            // compiler in a toolset package, and every reference and analyzer under the
            // package folder. Fetch the whole missing set in one restore before the hash
            // check below looks at any of it; with nothing missing (the normal case) this is
            // a `File.Exists` per path and no process at all.
            let! restoreProblems = Restore.ensure options.Restore [entry]
            if not (List.isEmpty restoreProblems) then
                do! failStep options
                        (sprintf "('%s') restoring the packages the lock names failed:\n%s"
                            entry.Name (restoreProblems |> String.concat "\n"))

            // whatever the restore could not provide, the compiler's own absence is worth an
            // explanation of its own (an SDK that is not installed is not a package)
            do! ensureCompilerAvailable options entry

            // the compiler is hashed (below) but was never a tracked dependency, so an SDK or
            // toolset update that changes csc.dll's bytes left the target looking up to date
            // and the hash check never ran (conceptual-review.md 2.4)
            do! needFiles (Filelist [File.make compiler.Path])

            // the lock never carries the commit sha itself (`Project.tokenizeRevision`): when
            // `Generated`, `Options` or `Defines` carries the token `$(SourceRevisionId)` (from
            // a project whose SourceLink writes it into `sourcelink.json`), resolve it here,
            // from the project's own repository, right before it is used -- a lock that needs
            // a revision has to be compiled in a repository, or this fails with a clear message
            let sourceRevisionToken = "$(SourceRevisionId)"
            let containsToken (s: string) = s.Contains sourceRevisionToken
            let needsRevision =
                (entry.Compilation.Generated |> List.exists (snd >> containsToken))
                || (entry.Args |> List.exists containsToken)
            let! entry =
                if not needsRevision then
                    recipe.Return entry
                else
                    match Git.headSha entry.Compilation.Directory with
                    | Some sha ->
                        recipe.Return (entry |> Lock.mapText (fun s -> if containsToken s then s.Replace (sourceRevisionToken, sha) else s))
                    | None ->
                        recipe {
                            let msg =
                                sprintf "'%s': the lock needs %s but no git repository was found at or above '%s' -- a lock that needs a revision must be compiled in a repository"
                                    entry.Name sourceRevisionToken entry.Compilation.Directory
                            do! trace Error "%s" msg
                            if options.FailOnError then failwith msg
                            return entry
                        }

            let compilation = entry.Compilation
            let args = entry.Args

            // the resolved entry is the source of truth for what msbuild (or the composed
            // front end) generated (assembly attributes, TFM defines): write it back whenever
            // it is missing or someone touched it
            for (path, content) in compilation.Generated do
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
            // from being a second rebuilder next to the engine's (conceptual-review.md 2.3) --
            // the gate is "does it exist", staleness is the engine's call, not this recipe's.
            // This is the same step for both modes now: the composed mode's `resolve` records
            // its `.resx` resources here too, at a permanent path under `obj/xake/<name>/`,
            // instead of compiling them itself into a temp file at recipe time.
            do! needFiles (Filelist (compilation.Resources |> List.map (fst >> File.make)))
            for (resx, resourcesFile) in compilation.Resources do
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
                [ for r in entry.Dependencies.References do yield check r.Path r.Sha256
                  for a in entry.Dependencies.Analyzers do yield check a.Path a.Sha256
                  yield check compiler.Path compiler.Sha256 ]
                |> List.choose id

            if not (List.isEmpty mismatches) then
                let detail =
                    mismatches
                    |> List.map (fun (path, expected, actual) -> sprintf "%s: expected %s, got %s" path expected actual)
                    |> String.concat "\n"
                do! trace Error "('%s') hash mismatch:\n%s" entry.Name detail
                if options.FailOnError then
                    failwithf "('%s') hash mismatch:\n%s" entry.Name detail

            // the generated files have to exist before the inputs are demanded. Note: for the
            // composed mode this now needs everything the args name -- including the
            // framework's global references (mscorlib.dll etc) -- rather than only the sources,
            // refs and resource files it used to `needFiles` directly. That is intended.
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

            let cscTool, extraArgs =
                match options.CscPath with
                | Some tool -> tool, []
                // the compiler path from `DotNetFwk` may be a native launcher rather than a
                // managed dll (the "run directly" branch below covers that case too)
                | None when Impl.endsWith ".dll" compiler.Path -> "dotnet", [compiler.Path]
                | None -> compiler.Path, []

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
                        // per source file. `Compilation.Directory` records exactly that (see its
                        // doc comment on `Lock.Compilation`); without it, a project compiled from a
                        // different cwd than its own directory can fail with CS1589 even though
                        // every file the args name is absolute and present. Args, `/out:` etc.
                        // are already absolute, so this only affects paths that never made it
                        // onto the command line.
                        workdir compilation.Directory
                        logprefix "[csc]"
                        stdoutlevel (Impl.levelFromString Level.Verbose)
                        erroutlevel (Impl.levelFromString Level.Verbose)
                    }

                do! Impl.failOnExitCode options.FailOnError entry.Name exitCode
            finally
                deleteTempFiles ()
        }

    /// <summary>
    /// Composes a `Lock.Entry` from `Src`/`Ref`/`RefGlobal`/`Resources`/`Define`/`Target`/
    /// `Platform`/`Unsafe`/`TargetFramework`/`CommandArgs` at recipe time -- the same work the
    /// composed mode always did, just stopping short of running the compiler. The argument list
    /// it produces is, in order: `/noconfig` (first, when the target framework requires it),
    /// `/nologo`, `/target:`, `/platform:`, `/unsafe`, `/nostdlib+`, `/out:`, `/define:`,
    /// sources, `/reference:` refs (one per reference; was `/r:` before the structured lock),
    /// global refs, `/res:`, `CommandArgs`. The list goes through `Lock.Compilation.ofArgs`
    /// like an imported one and gets the same round-trip check.
    ///
    /// Returns the resolved entry together with the framework's environment variables (see
    /// `run`).
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

            // a `.resx` resource is not compiled here: unlike an ordinary embedded-resource
            // file (already the file the compiler reads), a resx needs turning into a
            // `.resources` first, and doing that eagerly into a random temp file left nothing
            // for a lock recorded from these settings (`CscLock.resolve`) to compile against
            // once resolve's caller deleted it. Instead this records a permanent
            // `(resx, .resources)` pair in `Resources`, the same shape `Project.import`
            // already produces for an imported project -- `run`'s existing resource step
            // compiles it when the output is missing and `needFiles` the resx itself, so the
            // engine (not this recipe) decides when a resx edit reruns the compile.
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

            let resfiles = plainEntries |> List.map snd

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

            let! netfxVar = getVar "NETFX"
            // the compiler is taken from the framework being targeted, unless NETFX says otherwise
            let dotnetFwk = match netfxVar with | Some _ -> netfxVar | None -> Option.ofObj targetFramework
            let fwkInfo = DotNetFwk.locateFramework dotnetFwk

            // references and env vars always come from the targeted framework -- `toolset`
            // only replaces the compiler executable, fetching the package into the package
            // folder first when it is not there yet. Composed settings name no package folder
            // of their own (a lock does, through `RunOptions.Restore`), so this is the
            // machine's cache.
            let! compilerPath =
                match settings.Toolset with
                | None -> recipe { return fwkInfo.CscTool }
                | Some version ->
                    recipe {
                        let! cscDll = restoreToolsetCompiler Restore.Options.Default "Microsoft.Net.Compilers.Toolset" version
                        if not (File.Exists cscDll) then
                            failwithf "compiler package Microsoft.Net.Compilers.Toolset %s could not be restored (expected '%s')" version cscDll
                        return cscDll
                    }

            let compilation, references, analyzers = Lock.Compilation.ofArgs args
            let entry : Lock.Entry = {
                Name = Path.GetFileNameWithoutExtension outFile.Name
                Framework = (match targetFramework with null -> "" | fwk -> fwk)
                // no msbuild evaluation behind a composed compilation: no project, no imports,
                // no SDK, no pin
                Evaluation = { Project = ""; ProjectRefs = []; Imports = []; Sdk = ""; SdkPin = None; Properties = Map.empty }
                Compilation = { compilation with Directory = options.ProjectRoot; Resources = resources }
                Dependencies =
                    { Compiler = { Tool = "csc"; Path = compilerPath; Sha256 = ""; Version = Lock.compilerVersion compilerPath }
                      References = references
                      Analyzers = analyzers
                      Packages = [] }
            }
            // the same fidelity check the import makes: one item per switch here, so it holds
            let rebuilt = entry.Args
            if rebuilt <> args then
                failwithf "'%s': the command line rebuilt from the resolved entry differs from the composed one:\n%s"
                    entry.Name (Lock.diffList args rebuilt |> String.concat "\n")

            return entry, fwkInfo.EnvVars
        }

    /// A lock path as written in a script: relative to the build's project root, like every
    /// other target path, or absolute.
    let private lockPath (path: string) =
        recipe {
            let! options = getCtxOptions()
            return if Path.IsPathRooted path then path else options.ProjectRoot </> path
        }

    /// The document a composed compilation is recorded in: one entry, which carries the
    /// framework itself (`resolve` sets `Entry.Framework` from `targetfwk`/`NETFX-TARGET`).
    /// There is no msbuild configuration or property set behind composed settings, so those
    /// two stay empty.
    let private lockDocument (entry: Lock.Entry) : Lock.Document =
        { Configuration = ""
          Properties = []
          Entries = [ entry ] }

    /// Hashes the resolved entry (`Lock.rehash` -- the record-time step, see
    /// `lock-from-settings.md` recommendation 5) and writes it as a one-entry lock, returning
    /// what was written. The lock file is not a target of the engine on this path: it is
    /// written from inside the recipe that compiles, which is what lets `lock "path"` keep a
    /// tuned `csc { }` block in place (`lock-from-settings.md` §9, migration path A).
    let private recordLock (path: string) (entry: Lock.Entry) =
        recipe {
            let! full = lockPath path
            let dir = Path.GetDirectoryName full
            if dir <> "" then Directory.CreateDirectory dir |> ignore
            let rehashed = Lock.rehash entry
            do! Lock.save full (lockDocument rehashed)
            return rehashed
        }

    /// <summary>
    /// Resolves composed `csc {}` settings into a `Lock.Entry` without compiling -- the
    /// smallest piece `lock-from-settings.md` recommends (1b) so a lock-recording rule can
    /// write out what a compilation would look like, the way `Project.import` does for an
    /// msbuild project. Both feed `CscLock.compile`.
    ///
    /// `resolve` no longer produces any temp files of its own -- a `.resx` resource is now
    /// recorded as a permanent `(resx, .resources)` pair in `Resources`, compiled by `run`'s
    /// resource step the same way an imported project's is, so the returned project is
    /// compilable and recordable as is, `.resx` resources included.
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
                let! entry, _ = resolve settings
                return entry
            }

        /// <summary>
        /// Replays a resolved compilation -- one imported by `Project.import`, read back from a
        /// lock, or produced by `resolve` -- exactly as recorded: the entry's own `Args` is
        /// the whole compilation (see `run`). This is the replay entry point; it is not a mode
        /// of `csc {}`, because a settings record carrying a lock would let every other setting
        /// on it be silently ignored (conceptual-review.md 2.2).
        /// </summary>
        let compile (entry: Lock.Entry) = run RunOptions.Default entry []

        /// `compile` with the runner's own options (fail-on-error, an overriding `cscpath`).
        let compileWith (options: RunOptions) (entry: Lock.Entry) = run options entry []

        /// <summary>
        /// Resolves `settings`, hashes the result and writes (overwriting) the one-entry lock
        /// at `path` -- the deliberate update step `csc { lock "path" }` refuses to take on
        /// its own. A script declares it as a target of its own:
        /// <code>
        /// "update-locks" => recipe { do! CscLock.record "locks/app.json" settings }
        /// </code>
        /// with `settings` shared in record syntax (`{ CscSettings with Src = ...; Lock =
        /// Some "locks/app.json" }`) between that target and the rule that compiles.
        /// Nothing is compiled here.
        /// </summary>
        let record (path: string) (settings: CscSettingsType) =
            recipe {
                // `resolve` here is `CscLock.resolve` above (the entry alone), not the outer
                // private one that also returns the framework's env vars -- nothing is run.
                let! entry = resolve settings
                let! _ = recordLock path entry
                return ()
            }

        /// <summary>
        /// The differences between the lock at `path` and what `settings` resolve to right
        /// now -- `[]` means the lock is current. This is what `csc { lock "path" }` fails on,
        /// without compiling and without writing anything (`lock-from-settings.md` scenario 3).
        /// Hashes are not compared (the resolved side has none); the recorded hashes are
        /// verified against disk by the runner when a lock is actually compiled.
        /// </summary>
        let verify (path: string) (settings: CscSettingsType) =
            recipe {
                let! entry = resolve settings
                let! full = lockPath path
                let! doc = Lock.load full
                return Lock.diff (Lock.entry entry.Name doc) entry
            }

    /// <summary>
    /// C# compiler task. Compiles the source fileset into the target assembly: `resolve` turns
    /// the settings into a `Lock.Entry` and the one runner compiles it. To replay a lock
    /// instead, call `CscLock.compile` -- there is one resolved form, a `Lock.Entry`, and one
    /// runner; settings are intent.
    /// </summary>
    /// <param name="settings">Compiler settings</param>
    /// <returns>Recipe compiling the target</returns>
    let Csc (settings:CscSettingsType) =

        recipe {
            do! trace Level.Debug "Csc: settings=%A" settings
            let! (entry, envVars) = resolve settings
            let options = { RunOptions.Default with FailOnError = settings.FailOnError; CscPath = settings.CscPath }

            // `lock "path"`: settings stay the source of truth (resolve has already run), the
            // lock decides whether this compilation is the one that was recorded. Strict by
            // default, like `npm ci` -- an update is an explicit act, never a side effect of
            // building (the user's decision, 2026-09-24; there is no engine mode and no
            // global variable).
            let! toCompile =
                match settings.Lock with
                | None -> entry |> recipe.Return
                | Some path ->
                    recipe {
                        let! full = lockPath path
                        if not (File.Exists full) then
                            // no lock yet: record what was just resolved and compile that --
                            // the hashes `run` verifies are the ones taken a moment ago
                            let! recorded = recordLock path entry
                            return recorded
                        else
                            let! doc = Lock.load full
                            let recorded = Lock.entry entry.Name doc
                            match Lock.diff recorded entry with
                            | [] ->
                                // compile the recorded entry, not the resolved one: its
                                // hashes are what gate the build
                                return recorded
                            | differences ->
                                do! failStep options
                                        (sprintf "'%s': the resolved compilation differs from the lock '%s':\n%s\nUpdate the lock deliberately: delete '%s', or run the target that calls CscLock.record \"%s\"."
                                            entry.Name path (differences |> String.concat "\n") path path)
                                // FailOnError = false turned the failure into a warning: the
                                // settings are the source of truth, so compile what they say
                                return entry
                    }

            do! run options toCompile envVars
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

        /// <summary>Records this compilation in the lock file at the given path (relative to the
        /// project root, or absolute) and, once it exists, refuses to compile anything else:
        /// the resolved settings must match the lock, or the build fails with the differences.
        /// A matching lock is what gets compiled, so its recorded hashes are verified against
        /// disk. To update it, delete the file or call `CscLock.record` from a target of the
        /// script's own.</summary>
        [<CustomOperation("lock")>]       member __.Lock(s:CscSettingsType, path: string) = {s with Lock = Some path}

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
