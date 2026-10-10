namespace Xake.Hermetic.Dotnet

open System.IO

open Xake
open Xake.Tasks
open Xake.Dotnet

/// Imports a C# or F# project the way Visual Studio learns a project's compiler switches: a
/// design-time build in which the compiler task (`Csc` or `Fsc`) is asked to report its
/// command line instead of running (`ProvideCommandLineArgs`, `SkipCompilerExecution`).
/// Nothing about the compilation is reconstructed; what msbuild would have run is what the
/// lock holds.
module Project =

    open Lock

    /// Walks up from `projectDir` looking for `global.json` and reads its `sdk.version` /
    /// `sdk.rollForward` into a `Lock.SdkPin` (`Lock.sdkPinAt`). Pure: no msbuild involved.
    let sdkPin (projectDir: string) : SdkPin = Lock.sdkPinAt projectDir |> snd

    type ImportOptions = {
        /// The project files. All of them land in one lock: what varies between projects of
        /// one framework and variant is small next to what they share
        Projects: string list
        /// The target frameworks to import each project for. **All of them land in one lock**,
        /// one `Lock.Entry` per (project, framework): a multi-targeted project is restored
        /// once, without `TargetFramework`, so its `project.assets.json` holds every target,
        /// and the design-time build then runs per framework against that one restore. One
        /// lock per framework was what made two frameworks of one project impossible to
        /// import concurrently (verify-dataengine.md §6)
        Frameworks: string list
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
        /// `Roots.make`.
        Roots: (string * string) list
    } with static member Default = {
            Projects = []
            Frameworks = []
            Configuration = "Release"
            Properties = []
            Variant = ""
            Output = ""
            Roots = []
        }

    /// Serializes msbuild runs of one project file: two concurrent imports (different lock
    /// outputs, e.g. one rule per brand) of the *same* project both restore
    /// into that project's shared `obj/project.assets.json` (and `obj/*.nuget.g.*`) -- only
    /// `IntermediateOutputPath` is per-variant above, not `BaseIntermediateOutputPath` -- so
    /// when the package set depends on a property like `Brand`, one import can read the
    /// other's restore output and record the wrong references. A process-wide `Resource` of
    /// quantity 1 per normalized project path serializes the whole import of that project
    /// (the restore, then every framework's design-time build and `-pp`, and the reads of the
    /// assets file in between); a different project path gets its own
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

    /// Which of the `requested` frameworks this project actually has, and which it does not,
    /// in the order they were requested. `declared` is msbuild's `TargetFrameworks` (or the
    /// single `TargetFramework`) -- the authority on which legs of the project exist. It
    /// matters because one `Project.import` now covers a whole framework matrix for every
    /// project it is given, and a repository rarely multi-targets every project the same way:
    /// asking msbuild for a leg the project never declared makes it build one that was never
    /// configured, against a restore (which ran without the property) that has no target for
    /// it -- `NETSDK1005`, halfway through an import. An empty `declared` is "msbuild said
    /// nothing", not "nothing is declared": everything requested is kept. Pure.
    let internal frameworksToImport (declared: string list) (requested: string list) =
        match declared |> List.map (fun (s: string) -> s.Trim ()) |> List.filter ((<>) "") with
        | [] -> requested, []
        | declared ->
            let has f = declared |> List.exists (fun d -> System.String.Equals (d, f, System.StringComparison.OrdinalIgnoreCase))
            requested |> List.partition has

    /// `PrepareResources` runs resgen so the `/resource:` switches name real files;
    /// `Compile` (not `CoreCompile`) so that everything hooked before it -- generated
    /// assembly attributes, `BeforeCompile` extensions -- has run.
    let internal targets = "PrepareResources;Compile"
    /// Both compilers' command-line items: a csproj fills `CscCommandLineArgs`, an fsproj
    /// `FscCommandLineArgs` (the F# targets' `Fsc` task honours the same
    /// `ProvideCommandLineArgs`/`SkipCompilerExecution` contract); the other one is empty.
    let internal items = "CscCommandLineArgs,FscCommandLineArgs,ReferencePath,Analyzer,ProjectReference,EmbeddedResource"
    let internal wantedProperties =
        "AssemblyName,MSBuildProjectFullPath,MSBuildProjectDirectory,IntermediateOutputPath,BaseIntermediateOutputPath,TargetPath," +
        "CscToolPath,CscToolExe,CSharpCoreTargetsPath,RoslynTargetsPath,DotnetFscCompilerPath,NETCoreSdkVersion,NetCoreRoot,NuGetPackageRoot,ProjectAssetsFile," +
        "TargetFrameworkMoniker,LangVersion,Version,InformationalVersion,SignAssembly,AssemblyOriginatorKeyFile,Deterministic,SourceRevisionId"

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
    /// `sdkPin`) and `packages` the restore graph (from `Lock.packagesOf`), both computed separately
    /// so this function does no file walking of its own beyond the msbuild result and the
    /// files the command line names. Fails when the command line rebuilt from the structured
    /// entry (`Entry.Args`) is not exactly msbuild's -- the fidelity guarantee of brief §8c,
    /// checked here rather than trusted.
    let internal parseImport (framework: string) (resultFile: string) (imports: string list) (pin: SdkPin) (packages: Lock.Package list) =
        let root = File.ReadAllText resultFile |> Json.parse
        let items name =
            Json.field "Items" root |> Option.bind (Json.field name)
            |> Option.map Json.asArray |> Option.defaultValue []
        let identity item = Json.field "Identity" item |> Option.bind Json.asString |> Option.defaultValue ""
        let metadata name item = Json.field name item |> Option.bind Json.asString |> Option.defaultValue ""
        let properties =
            match Json.field "Properties" root with
            | Some (Json.JObject members) ->
                members |> List.choose (fun (name, value) -> Json.asString value |> Option.map (fun v -> name, v)) |> Map.ofList
            | _ -> Map.empty
        let prop name = properties |> Map.tryFind name |> Option.defaultValue ""

        let directory = (prop "MSBuildProjectDirectory").Replace ('\\', '/')
        // the compiler is whichever of the two items came back: an fsproj reports
        // `FscCommandLineArgs`, a csproj `CscCommandLineArgs`
        let fscArgs = items "FscCommandLineArgs" |> List.map identity
        let isFSharp = not (List.isEmpty fscArgs)
        let args =
            if isFSharp then fscArgs |> FscArgs.absolutize directory
            else items "CscCommandLineArgs" |> List.map identity |> CscArgs.absolutize directory
        if List.isEmpty args then
            failwithf "'%s': the design-time build reported no compiler command line -- CoreCompile did not run (skipped as up to date, or the project has no C# or F# compile step)" (prop "MSBuildProjectFullPath")
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
        //
        // fsc: `DotnetFscCompilerPath` is the SDK's `fsc.dll`, as the F# targets hand it to
        // `dotnet` -- quoted (`"<sdk>/FSharp/fsc.dll"`), so the quotes are stripped.
        let fscCompilerPath () =
            match (prop "DotnetFscCompilerPath").Trim().Trim '"' with
            | "" -> failwithf "'%s': the design-time build reported no DotnetFscCompilerPath -- not an SDK-style F# project?" (prop "MSBuildProjectFullPath")
            | path -> Path.GetFullPath path |> slash
        let cscCompilerPath () =
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
        let compilerPath = if isFSharp then fscCompilerPath () else cscCompilerPath ()

        let absoluteDir (dir: string) =
            let dir = slash dir
            (if Path.IsPathRooted dir then dir else Path.GetFullPath (Path.Combine (directory, dir)) |> slash).TrimEnd '/' + "/"
        let intermediate = prop "IntermediateOutputPath" |> absoluteDir
        // where restore writes its props and targets
        let baseIntermediate = (match prop "BaseIntermediateOutputPath" with | "" -> "obj/" | dir -> dir) |> absoluteDir

        // `FullPath`, a well-known item metadata, is unreliable here under the msbuild CLI's
        // `-getItem`: seen live on an `<EmbeddedResource Update="...">` item (page's
        // `Properties\Resources.resx`, resolved via the SDK's default-items glob) and on a
        // relative `<ProjectReference Include="..\..\X\X.csproj">`, its `FullPath` came back
        // resolved against this *process's* current directory instead of the project's own
        // directory -- `RootDir` and `Directory` in the same metadata bag were equally off.
        // `Identity` combined with `directory` (a *property*, not well-known item metadata, and
        // not subject to this) is reliable.
        let resolveAgainstProject (path: string) =
            let path = slash path
            if Path.IsPathRooted path then path
            else Path.GetFullPath (Path.Combine (directory, path)) |> slash

        // `PrepareResources` compiles every resx `EmbeddedResource` and records where: prefer
        // `OutputResource` (`GenerateResource`'s own, exact, relative to the project directory)
        // and fall back to `IntermediateOutputPath + ManifestResourceName + ".resources"` for
        // an older SDK that does not set it. Non-resx embedded resources are passed as plain
        // files on the `/resource:` switch and need nothing recorded here.
        let resources =
            items "EmbeddedResource"
            |> List.filter (fun item -> (identity item).ToLowerInvariant().EndsWith ".resx")
            |> List.map (fun item ->
                let resx = identity item |> resolveAgainstProject
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
            (if isFSharp then FscArgs.inputs args else CscArgs.inputs args)
            |> List.filter (fun path -> path.StartsWith intermediate && File.Exists path && not (resourceOutputs.Contains path))
            |> List.map (fun path -> path, File.ReadAllText path)

        // an import under the SDK is the SDK version, recorded with the compiler; one under
        // obj is restore's, regenerated by the import itself. The rest are the evaluation's
        // inputs: the project, the Directory.Build files, package build files
        let sdkRoot = (prop "NetCoreRoot" |> slash).TrimEnd '/'
        let imports =
            imports |> List.map slash
            |> List.filter (fun path -> not (sdkRoot <> "" && path.StartsWith (sdkRoot + "/")) && not (path.StartsWith baseIntermediate))

        let dependencies tool (parsed: Dependencies) : Dependencies =
            { Compiler = { Tool = tool; Path = compilerPath; Sha256 = Csc.sha256 compilerPath; Version = Csc.compilerVersion compilerPath }
              References = parsed.References |> List.map (fun r -> { r with Sha256 = Csc.sha256 r.Path })
              Analyzers = parsed.Analyzers |> List.map (fun a -> Csc.hashed a.Path) }
        let compilation =
            if isFSharp then
                let composed = Fsc.ofArgs args
                Compilation.Fsc
                    { composed with
                        Fsc.Name = prop "AssemblyName"
                        Fsc.Framework = framework
                        Fsc.Directory = directory
                        Fsc.Generated = generated
                        Fsc.Resources = resources
                        Fsc.Dependencies = dependencies "fsc" composed.Dependencies }
            else
                let composed = Csc.ofArgs args
                Compilation.Csc
                    { composed with
                        Name = prop "AssemblyName"
                        Framework = framework
                        Directory = directory
                        Generated = generated
                        Resources = resources
                        Dependencies = dependencies "csc" composed.Dependencies }
        let entry : Lock.Entry = {
            Compilation = compilation
            Evaluation =
                { Project = prop "MSBuildProjectFullPath" |> slash
                  ProjectRefs = items "ProjectReference" |> List.map (identity >> resolveAgainstProject)
                  Imports = imports |> List.map Csc.hashed
                  Sdk = prop "NETCoreSdkVersion"
                  SdkPin = Some pin
                  Properties =
                    properties |> Map.filter (fun name _ ->
                        List.contains name [ "AssemblyName"; "TargetFrameworkMoniker"; "LangVersion"; "Version"; "InformationalVersion"
                                             "SignAssembly"; "AssemblyOriginatorKeyFile"; "Deterministic"; "TargetPath"; "IntermediateOutputPath" ]) }
            Packages = packages
            // derived by `import`, which knows the roots and the pin's file
            Prerequisites = []
        }
        // the round trip: the structured entry must give back msbuild's command line exactly,
        // or the lock would describe a compilation other than the one msbuild ran
        let rebuilt = entry.Args
        if rebuilt <> args then
            failwithf "'%s': the command line rebuilt from the lock entry differs from msbuild's (structured form lost fidelity):\n%s"
                entry.Name (Csc.diffList args rebuilt |> String.concat "\n")
        // SourceLink's `sourcelink.json` (captured above, now that `sourcelink` is an input
        // switch) embeds the commit msbuild resolved via `SourceRevisionId` -- tokenize it out
        // so the lock's content, hence the lock file, does not change on every commit
        match prop "SourceRevisionId" with
        | "" -> entry
        | sha -> entry |> Lock.mapText (Git.tokenize sha)

    /// <summary>
    /// Imports the projects, for every framework in `options.Frameworks`, and writes the one
    /// lock. Make it the recipe of a file rule over the lock: msbuild then runs only when a
    /// project file or one of the files it imports changed.
    /// </summary>
    /// <remarks>
    /// Three msbuild phases per project, all inside that project's lock
    /// (`withProjectLock`), so a concurrent import of the same project for another variant
    /// cannot land between them:
    ///
    /// 1. **One restore, with no `TargetFramework`** (`-t:Restore`, plus
    ///    `-p:RestoreRecursive=false`). Without the property NuGet resolves every target of a
    ///    multi-targeted project into one `project.assets.json`; with `RestoreRecursive=false`
    ///    it stops walking the project graph, so a project's restore no longer rewrites the
    ///    assets files of the projects it references -- which is the half `withProjectLock`
    ///    never covered and what made two frameworks of one project impossible to import
    ///    concurrently (verify-dataengine.md §6). Verified on the dataengine fixture: with the
    ///    flag, only the restored project's own assets file is written.
    /// 2. **A design-time build per framework**, `-p:TargetFramework=&lt;f&gt;` and **no**
    ///    `-restore` -- the restore above already produced everything it reads.
    /// 3. **A `-pp` preprocess per framework** for the evaluation's import list.
    /// </remarks>
    let import (options: ImportOptions) =

        recipe {
            if List.isEmpty options.Projects then
                failwith "ImportOptions.Projects is empty: there is nothing to import, and an empty lock would be written without a word. A script that filters its project list by File.Exists silently comes to this when it runs from the wrong directory"
            if List.isEmpty options.Frameworks then
                failwith "ImportOptions.Frameworks is empty: name at least one target framework to import for"

            let variantDir = if options.Variant = "" then "" else options.Variant + "/"

            // what selects the package set and the compilation alike; the script's own
            // properties come last so they can override any of these
            let common = [ "Configuration", options.Configuration
                           // the audit talks to the feeds and its warnings turn fatal under
                           // TreatWarningsAsErrors; it is not the import's business
                           "NuGetAudit", "false" ]

            let switchesOf properties = [for name, value in properties -> sprintf "-p:%s=%s" name value]

            // the restore runs *without* TargetFramework, so every target of a multi-targeted
            // project lands in the one project.assets.json, and with RestoreRecursive=false,
            // so it writes that project's assets file and nobody else's
            let restoreSwitches = switchesOf (common @ [ "RestoreRecursive", "false" ] @ options.Properties)

            let buildSwitches framework =
                switchesOf (
                    common
                    @ [ "TargetFramework", framework
                        // the compiler reports its command line and does not run
                        "ProvideCommandLineArgs", "true"
                        "SkipCompilerExecution", "true"
                        // resolving a project reference must not build it
                        "BuildProjectReferences", "false"
                        // the generated files are per (framework, variant): brands sharing one
                        // obj overwrite each other's assembly attributes
                        "IntermediateOutputPath", sprintf "obj/xake/%s/%s" framework variantDir
                        // CoreCompile lists this property among its Outputs (Visual Studio's
                        // own design-time trick): a file that never exists keeps the target
                        // from being skipped as up to date when the assembly in obj/xake is
                        // newer than the sources -- skipped, it reports no command line at all
                        "NonExistentFile", "__NonExistentSubDir__/__NonExistentFile__" ]
                    @ options.Properties)

            do! needFiles (Filelist (options.Projects |> List.map File.make))
            Directory.CreateDirectory (Path.GetDirectoryName (Path.GetFullPath options.Output)) |> ignore

            let msbuild (arguments: string list) name =
                recipe {
                    let! exitCode =
                        shell {
                            cmd "dotnet"
                            args ("msbuild" :: arguments)
                            logprefix "[msbuild]"
                            stdoutlevel (Tool.diagnosticLevel Level.Verbose)
                            erroutlevel (Tool.diagnosticLevel Level.Error)
                        }
                    do! Tool.failOnExitCode true name exitCode
                }

            // the roots the lock is written with; the SDK an entry depends on is read off its
            // tokenized paths
            let! roots = Roots.currentWith options.Roots

            let entries = ResizeArray<Lock.Entry>()
            for project in options.Projects do
                let projectDir = Path.GetDirectoryName (Path.GetFullPath project)
                let pinFile, pin = Lock.sdkPinAt projectDir

                // one hold of the project's lock for the whole project: the restore and every
                // framework's design-time build (which reads what that restore wrote) have to
                // be one atomic unit with respect to another variant's import of the same
                // project -- MESCIUS's and GCCN's restores write the same
                // obj/project.assets.json
                let! imported = withProjectLock project (recipe {
                    do! trace Info "restoring '%s'%s" project (if options.Variant = "" then "" else " (" + options.Variant + ")")

                    // the same run reports the project's own framework list: the restore is
                    // the one msbuild run here that is not per framework, so asking it costs
                    // nothing and tells us which legs of the matrix this project has
                    let restoreDump = sprintf "%s.%s.restore.msbuild" options.Output (Path.GetFileNameWithoutExtension project)
                    do! msbuild
                            ([ project; "-nologo"; "-verbosity:quiet"; "-t:Restore" ] @ restoreSwitches
                             @ [ "-getProperty:TargetFrameworks,TargetFramework"
                                 sprintf "-getResultOutputFile:%s" restoreDump ]) project

                    let declared =
                        match readDumpProperty restoreDump "TargetFrameworks" with
                        | Some list -> list.Split ';' |> List.ofArray
                        | None -> readDumpProperty restoreDump "TargetFramework" |> Option.toList
                    File.Delete restoreDump

                    let frameworks, absent = frameworksToImport declared options.Frameworks
                    for framework in absent do
                        do! trace Info "'%s' does not target '%s' -- no lock entry for it" project framework
                    if List.isEmpty frameworks then
                        do! trace Warning "'%s' targets %s and none of them was asked for: it contributes no entry to the lock"
                                project (String.concat ", " declared)

                    let ofProject = ResizeArray<Lock.Entry>()
                    for framework in frameworks do
                        do! trace Info "importing '%s' for '%s'%s" project framework (if options.Variant = "" then "" else " (" + options.Variant + ")")

                        let dump = sprintf "%s.%s.%s.msbuild" options.Output (Path.GetFileNameWithoutExtension project) framework
                        let preprocessed = dump + ".pp"
                        let switches = buildSwitches framework

                        // the design-time build; the compiler's command line comes back as an
                        // item list. No `-restore`: the one above already ran, for every target
                        do! msbuild
                                ([ project; "-nologo"; "-verbosity:quiet" ] @ switches
                                 @ [ sprintf "-t:%s" targets
                                     sprintf "-getItem:%s" items
                                     sprintf "-getProperty:%s" wantedProperties
                                     sprintf "-getResultOutputFile:%s" dump ]) project

                        // the restore graph this import saw, for this framework's target
                        let graph =
                            match readDumpProperty dump "ProjectAssetsFile" with
                            | Some assetsFile when File.Exists assetsFile ->
                                let cacheRoot =
                                    readDumpProperty dump "NuGetPackageRoot" |> Option.defaultWith Roots.nugetRoot
                                Nuget.readAssets assetsFile framework |> Lock.packagesOf cacheRoot
                            | _ -> []

                        // the files that took part in the evaluation; MSBuildAllProjects no
                        // longer tells, the preprocessed project does
                        do! msbuild ([ project; "-nologo" ] @ switches @ [ sprintf "-pp:%s" preprocessed ]) project

                        let imports = File.ReadAllText preprocessed |> parseImports
                        let entry = parseImport framework dump imports pin graph
                        File.Delete dump
                        File.Delete preprocessed
                        ofProject.Add entry
                    return List.ofSeq ofProject
                })

                for importedEntry in imported do
                    let sdk = importedEntry.Evaluation.Sdk
                    // the SDK this entry depends on: its compiler and analyzers under
                    // $(DotnetRoot)/sdk/<v>/. Exactly pinned (the evaluation's `SdkPin` is
                    // `Pinned v`, and so msbuild ran `v`), that SDK is the entry's prerequisite;
                    // otherwise a warning, and none
                    let sdkVersions =
                        Lock.sdkVersionsOf roots
                            (importedEntry.Dependencies.Compiler.Path :: (importedEntry.Dependencies.Analyzers |> List.map (fun a -> a.Path)))
                    let prerequisites, unpinned =
                        Lock.prerequisitesFor roots pinFile (importedEntry.Evaluation.SdkPin |> Option.defaultValue NoGlobalJson) sdkVersions
                    let entry = { importedEntry with Prerequisites = prerequisites }
                    match pin with
                    | Pinned v when sdk <> "" && sdk <> v ->
                        do! trace Warning "'%s': the SDK is pinned to %s but msbuild ran %s -- the pinned SDK is not installed on this machine" entry.Name v sdk
                    | Pinned _ -> ()
                    | other when List.isEmpty sdkVersions ->
                        do! trace Warning "'%s': the SDK is not pinned (%s) -- the lock's compiler (%s, SDK %s) will drift with every SDK the machine picks; pin it with global.json { sdk: { version, rollForward: \"disable\" } }" entry.Name (sdkPinText other) entry.Dependencies.Compiler.Version sdk
                    | _ -> ()
                    for v in unpinned do
                        do! trace Warning "%s" (Lock.sdkUnpinnedWarning entry.Name v)

                    // the evaluation's inputs, so that a Directory.Build.props edit re-imports
                    // and nothing else does
                    do! needFiles (Filelist (entry.Evaluation.Imports |> List.map (fun (h: Lock.Hashed) -> File.make h.Path)))

                    // every resx output has to be named by a resource switch, or `run` would
                    // regenerate a file the compiler never reads
                    let resourceInputs =
                        match entry.Compilation with
                        | Compilation.Csc c -> CscArgs.switchValues "resource" c.Options
                        | Compilation.Fsc f -> FscArgs.switchValues "resource" f.Options
                    for (resx, resourcesFile) in entry.Compilation.Resources do
                        if not (List.contains resourcesFile resourceInputs) then
                            do! trace Warning "'%s' compiles to '%s' but no resource switch names that path" resx resourcesFile

                    entries.Add entry

                // when `Generated` carries a tokenized `$(SourceRevisionId)`, a new commit does
                // not touch any tracked input above and would leave the lock stale (the token
                // makes its *content* commit-independent, but the project still has to be
                // re-imported once there is a new commit to resolve at compile time) -- `HEAD`
                // and the ref file (or `packed-refs`) it resolves through are the files that
                // change when the commit does
                do! needFiles (Filelist (Git.headFiles projectDir |> List.map File.make))

            let lock : Lock.Document = {
                Configuration = options.Configuration
                Properties = options.Properties
                Entries = List.ofSeq entries
            }
            File.WriteAllText (options.Output, Lock.format roots lock)
        }
