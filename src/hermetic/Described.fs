namespace Xake.Hermetic.Dotnet

open System.IO

open Xake
open Xake.Tasks
open Xake.Dotnet

/// A project description (`Description`) turned into a compilation: what `fsc { project
/// "path" }` / `csc { project "path" }` do, and the package step a lock generation runs before
/// them (`restore`). Nothing here resolves a package inside the compile: the description's
/// packages come from `restore` (an msbuild restore of a synthesized project) and are handed
/// to the block with `packages`.
module Described =

    /// What `restore` resolved for one description and framework: the compile-time assemblies
    /// of every package in the restore graph (absolute paths, sorted) and the graph itself, as
    /// a lock entry records it (`Lock.Entry.Packages`).
    type Restored =
        { Framework: string; References: string list; Packages: Package list
          /// the .NET Framework assemblies the packages ask for (a nuspec's
          /// `frameworkAssemblies`, `Nuget.Assets.FrameworkAssemblies`), by name: `project`
          /// references them for a .NET Framework target the way it does the implicit ones
          FrameworkReferences: string list }

    /// What this package adds to a block that names a description (the core's `project`
    /// setting, `CscSettingsType.Project`): the configuration (`config`) and the restored
    /// packages (`packages`).
    type Spec =
        { Config: string option; Restored: Restored option }
        static member None = { Config = None; Restored = None }

    let private same (a: string) (b: string) = System.String.Equals (a, b, System.StringComparison.OrdinalIgnoreCase)

    /// <summary>
    /// A platform framework of .NET as (base framework, platform, platform version): the
    /// version msbuild gives it (`TargetPlatformVersion`), the SDK's default `7.0` for Windows
    /// when the moniker names none -- `net8.0-windows` -> (`net8.0`, `Windows`, `7.0`),
    /// `net8.0-windows10.0.19041.0` -> (`net8.0`, `Windows`, `10.0.19041.0`). `None` for a
    /// framework without a platform.
    /// </summary>
    let platformOf (framework: string) : (string * string * string) option =
        let m = System.Text.RegularExpressions.Regex.Match (framework, @"^([A-Za-z]+\d+(?:\.\d+)*)-([A-Za-z]+)(\d+(?:\.\d+)*)?$")
        if not m.Success then None
        else
            let name = m.Groups.[2].Value.ToLowerInvariant ()
            let identifier =
                match name with
                | "windows" -> "Windows"
                | "ios" -> "iOS" | "maccatalyst" -> "MacCatalyst" | "macos" -> "macOS" | "tvos" -> "tvOS"
                | "android" -> "Android" | "browser" -> "Browser"
                | n -> string (System.Char.ToUpperInvariant n.[0]) + n.Substring 1
            let version =
                match m.Groups.[3].Value with
                | "" -> if identifier = "Windows" then "7.0" else ""
                | v -> if v.Contains "." then v else v + ".0"
            Some (m.Groups.[1].Value.ToLowerInvariant (), identifier, version)

    /// A dotted version as numbers, for ordering (`10.0.19041.0` -> [10; 0; 19041; 0]).
    let private versionKey (v: string) =
        v.Split ([| '.' |], System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun p -> match System.Int32.TryParse p with | true, n -> n | _ -> 0) |> List.ofArray


    /// The configuration preset the .NET SDK applies (SDK 10.0.401: `Microsoft.NET.Sdk.props`,
    /// `Microsoft.FSharp.NetSdk.props`, the `ImplicitConfigurationDefine` of
    /// `Microsoft.NET.Sdk.CSharp.targets`/`FSharpTargetsShim.targets`; checked against the
    /// compiler command lines of `dotnet build -c Debug|Release`): the defines and the
    /// switches, and for F# whether tail calls stay on.
    ///
    /// | | F# | C# |
    /// |---|---|---|
    /// | Debug | `--debug+ --debug:portable --optimize- --tailcalls-`, TRACE;DEBUG | `/debug+ /debug:portable /optimize-`, TRACE;DEBUG |
    /// | Release | `--debug:portable --optimize+`, TRACE;RELEASE | `/debug- /debug:portable /optimize+`, TRACE;RELEASE |
    let preset (language: Description.Language) (config: string) : string list * string list * bool =
        match language, config.ToLowerInvariant () with
        | Description.FSharp, "debug" -> [ "TRACE"; "DEBUG" ], [ "--debug+"; "--debug:portable"; "--optimize-" ], false
        | Description.FSharp, "release" -> [ "TRACE"; "RELEASE" ], [ "--debug:portable"; "--optimize+" ], true
        | Description.CSharp, "debug" -> [ "TRACE"; "DEBUG" ], [ "/debug+"; "/debug:portable"; "/optimize-" ], true
        | Description.CSharp, "release" -> [ "TRACE"; "RELEASE" ], [ "/debug-"; "/debug:portable"; "/optimize+" ], true
        | _ -> failwithf "config '%s': only Debug and Release are known (the SDK's own presets)" config

    /// The package the F# SDK references implicitly (`Microsoft.FSharp.NetSdk.props`): `System.ValueTuple`
    /// 4.6.2 for .NET Framework 4.0 to 4.7, unless the description names it.
    let implicitPackages (d: Description.Project) (framework: string) : Description.Package list =
        let f = framework.ToLowerInvariant ()
        let old = [ "net40"; "net45"; "net451"; "net452"; "net46"; "net461"; "net462"; "net47" ]
        let named = (d.Packages @ (d.ByFramework |> List.collect (fun (_, i) -> i.Packages))) |> List.exists (fun p -> p.Id = "System.ValueTuple")
        if d.Language = Description.FSharp && List.contains f old && not named then
            [ { Id = "System.ValueTuple"; Version = Some "4.6.2"; Private = false } ]
        else []

    let private normal (path: string) = path.Replace ('\\', '/')

    /// <summary>
    /// Deletes each compiled `.resources` of `resources` ((resx, .resources) pairs) that is older
    /// than its `.resx`. The runner of Xake 3.7 to 3.9 (`Xake.Dotnet`, the floor of this
    /// package) compiles a `.resources` only when it is missing, so an edited resx (which reruns
    /// the compile: the runner depends on it) is compiled again. Remove this when a Xake runner
    /// that compiles it on every run is the package floor.
    /// </summary>
    let refreshResources (resources: (string * string) list) : unit =
        for resx, compiled in resources do
            if File.Exists compiled && File.Exists resx && File.GetLastWriteTimeUtc compiled < File.GetLastWriteTimeUtc resx then
                File.Delete compiled

    /// A target framework moniker as (family, version): `netstandard2.0` -> ("netstandard",
    /// [2; 0]), `net8.0`/`netcoreapp3.1` -> ("netcoreapp", ...), `net462` -> ("netfx", [4; 6; 2]).
    let private moniker (tfm: string) =
        let t = tfm.ToLowerInvariant ()
        let dotted (s: string) = s.Split '.' |> Array.map int |> List.ofArray
        let m = System.Text.RegularExpressions.Regex.Match (t, @"^(netstandard|netcoreapp|net)(\d+(\.\d+)+)$")
        if m.Success then
            Some ((if m.Groups.[1].Value = "netstandard" then "netstandard" else "netcoreapp"), dotted m.Groups.[2].Value)
        elif System.Text.RegularExpressions.Regex.IsMatch (t, @"^net\d{2,3}$") then
            Some ("netfx", t.Substring 3 |> Seq.map (fun c -> int c - int '0') |> List.ofSeq)
        else None

    /// <summary>
    /// The framework of `available` a project targeting `target` references, msbuild's
    /// "nearest" in its simplest form: `target` itself; else the highest of the same family
    /// not above it (`net8.0` takes `net6.0`, `net472` takes `net462`); else the highest
    /// `netstandard` the target implements (.NET and .NET Core: up to 2.1; .NET Framework
    /// 4.6.1 and later: up to 2.0; `netstandard` itself: not above it). A platform framework
    /// (`net8.0-windows`) takes the same platform first (`net8.0-windows`, `net6.0-windows`),
    /// then a framework without a platform by those rules; a framework without a platform never
    /// takes a platform one. `None` when nothing fits.
    /// </summary>
    let rec nearestFramework (target: string) (available: string list) : string option =
        match available |> List.tryFind (same target), platformOf target with
        | Some exact, _ -> Some exact
        | None, Some (baseTarget, identifier, version) ->
            // a platform framework (`net8.0-windows`): the same platform at a version not above
            // it on the nearest base, else a framework without a platform by the rules below
            // (NuGet's FrameworkReducer)
            let samePlatform =
                available |> List.choose (fun a ->
                    match platformOf a with
                    | Some (b, i, v) when same i identifier && versionKey v <= versionKey version -> Some (a, b, v)
                    | _ -> None)
            let viaPlatform =
                nearestFramework baseTarget (samePlatform |> List.map (fun (_, b, _) -> b) |> List.distinct)
                |> Option.bind (fun b ->
                    samePlatform |> List.filter (fun (_, b2, _) -> same b b2)
                    |> List.sortBy (fun (_, _, v) -> versionKey v) |> List.tryLast |> Option.map (fun (a, _, _) -> a))
            viaPlatform |> Option.orElse (nearestFramework baseTarget (available |> List.filter (platformOf >> Option.isNone)))
        | None, None ->
        match moniker target with
        | None -> None
        | Some (family, version) ->
            let available = available |> List.filter (platformOf >> Option.isNone)
            let candidates = available |> List.choose (fun a -> moniker a |> Option.map (fun m -> a, m))
            let highest pick = candidates |> List.filter pick |> List.sortBy (snd >> snd) |> List.tryLast |> Option.map fst
            let standardUpTo =
                match family with
                | "netstandard" -> Some version
                | "netcoreapp" -> Some [ 2; 1 ]
                | _ when version >= [ 4; 6; 1 ] -> Some [ 2; 0 ]
                | _ -> None
            highest (fun (_, (f, v)) -> f = family && v <= version)
            |> Option.orElse (
                match standardUpTo with
                | Some top -> highest (fun (_, (f, v)) -> f = "netstandard" && v <= top)
                | None -> None)

    /// The synthesized project `restore` runs `dotnet restore` on: one `PackageReference`
    /// per package of the description for `framework`, the version as the description states
    /// it (exact, a range, or none -- floating is left to msbuild), and no implicit framework
    /// package (`NETStandard.Library`, the .NET Framework reference assemblies), which the
    /// compile takes from its target framework instead.
    let restoreProjectText (framework: string) (packages: Description.Package list) =
        [ yield "<Project Sdk=\"Microsoft.NET.Sdk\">"
          yield "  <PropertyGroup>"
          yield sprintf "    <TargetFramework>%s</TargetFramework>" framework
          yield "    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>"
          yield "    <AutomaticallyUseReferenceAssemblyPackages>false</AutomaticallyUseReferenceAssemblyPackages>"
          yield "  </PropertyGroup>"
          yield "  <ItemGroup>"
          for p in packages do
              let version = p.Version |> Option.map (sprintf " Version=\"%s\"") |> Option.defaultValue ""
              let privateAssets = if p.Private then " PrivateAssets=\"all\"" else ""
              yield sprintf "    <PackageReference Include=\"%s\"%s%s />" p.Id version privateAssets
          yield "  </ItemGroup>"
          yield "</Project>"
          yield "" ]
        |> String.concat "\n"

    /// The restores of one build (one engine run): the `Restored` of each restore key, so
    /// that every target asking for the same package set and framework restores once.
    let private caches =
        System.Runtime.CompilerServices.ConditionalWeakTable<obj, System.Collections.Concurrent.ConcurrentDictionary<string, Restored>> ()
    let private cacheOf (ctx: ExecContext) =
        caches.GetValue (box ctx.Engine, fun _ -> System.Collections.Concurrent.ConcurrentDictionary<string, Restored> ())
    /// One `Resource` per restore key, so two targets missing the same key run one restore.
    let private keyLocks = System.Collections.Concurrent.ConcurrentDictionary<string, Resource> ()
    let mutable private processes = 0

    /// The number of `dotnet restore` processes `restore`, `restoreWith` and `restoreAll` have
    /// started in this process (a diagnostic: measurements and tests).
    let restoreProcesses () = processes

    /// The restore of a package set for a framework: its key (`<framework>-<hash>`, the hash of
    /// the package folder and the restore project with the packages sorted, so the order of the
    /// declarations does not matter) and the project text.
    let private keyOf (root: string) (framework: string) (packages: Description.Package list) =
        let sorted = packages |> List.sortBy (fun p -> p.Id.ToLowerInvariant (), defaultArg p.Version "", p.Private)
        let text = restoreProjectText framework sorted
        use sha = System.Security.Cryptography.SHA256.Create ()
        let hash = sha.ComputeHash (System.Text.Encoding.UTF8.GetBytes (root + "\n" + text))
        let hex = System.BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant().Substring (0, 16)
        framework.ToLowerInvariant () + "-" + hex, text

    /// The folder all synthesized restore projects live in: `obj/xake/restore/<key>/restore.csproj`.
    let private restoreDir (projectRoot: string) = Path.Combine (projectRoot, "obj", "xake", "restore")

    /// Runs `dotnet restore` on `project` (one synthesized project or the batch project).
    let private runRestore (root: string) (project: string) : Recipe<ExecContext, int> =
        recipe {
            System.Threading.Interlocked.Increment &processes |> ignore
            let! exitCode =
                shell {
                    cmd "dotnet"
                    args ([ "restore"; project; "-v:quiet"; "-nologo"; "-p:NuGetAudit=false"
                            "-p:ImportDirectoryBuildProps=false"; "-p:ImportDirectoryBuildTargets=false"
                            "-p:ImportDirectoryPackagesProps=false"; "-p:ManagePackageVersionsCentrally=false" ]
                         |> List.map Tool.quoteArgument)
                    env ("NUGET_PACKAGES", root)
                    // no msbuild worker nodes left behind: a batch restore starts nodes, and a
                    // reused node inherits this process's stdout and keeps the pipe open after
                    // `dotnet restore` exits, so reading its output to the end waits for the
                    // node's idle timeout (the Linux CI hang of 0.4.0)
                    env ("MSBUILDDISABLENODEREUSE", "1")
                    workdir (Path.GetDirectoryName project)
                    logprefix "[restore]"
                    stdoutlevel (Tool.diagnosticLevel Level.Verbose)
                    erroutlevel (Tool.diagnosticLevel Level.Verbose)
                }
            return exitCode
        }

    /// The `Restored` of a restore project whose `obj/project.assets.json` is written.
    let private readRestored (root: string) (dir: string) (framework: string) : Restored =
        let assets = Path.Combine (dir, "obj", "project.assets.json")
        let graph = Nuget.readAssets assets framework
        let references =
            Nuget.compileItems assets framework
            |> List.map (fun (id, version, item) ->
                normal (Path.Combine (root, id.ToLowerInvariant (), version.ToLowerInvariant (), item)))
            |> List.distinct
            |> List.sortWith (fun a b -> System.String.CompareOrdinal (a, b))
        { Framework = framework; References = references; Packages = Lock.packagesOf root graph
          FrameworkReferences = graph.FrameworkAssemblies }

    /// The package folder of this build, normalized.
    let private packageFolder () =
        recipe {
            let! root = DotNetFwk.packageRoot ()
            return normal root |> fun r -> r.TrimEnd '/'
        }

    /// The restore of `packages` for description `d` and `framework` (see `restore`): from
    /// this build's cache when a target (or `restoreAll`) restored the same set already, else
    /// one `dotnet restore` of its own.
    let private restorePackages (d: Description.Project) (framework: string) (packages: Description.Package list) : Recipe<ExecContext, Restored> =
        recipe {
            if List.isEmpty packages then
                return { Framework = framework; References = []; Packages = []; FrameworkReferences = [] }
            else
                let! options = getCtxOptions ()
                let! ctx = getCtx ()
                let! root = packageFolder ()
                let key, text = keyOf root framework packages
                let cache = cacheOf ctx
                match cache.TryGetValue key with
                | true, restored -> return restored
                | _ ->
                    let lock = keyLocks.GetOrAdd (key, fun k -> Resource.newResource ("described-restore:" + k) 1)
                    let! restored =
                        withResource lock 1 (recipe {
                            match cache.TryGetValue key with
                            | true, restored -> return restored
                            | _ ->
                                let dir = Path.Combine (restoreDir options.ProjectRoot, key)
                                Directory.CreateDirectory dir |> ignore
                                let project = Path.Combine (dir, "restore.csproj")
                                File.WriteAllText (project, text)
                                let! exitCode = runRestore root project
                                if exitCode <> 0 then
                                    failwithf "'%s': restoring the packages of the description for %s failed with exit code %d (see '%s'): %s"
                                        d.Name framework exitCode project
                                        (packages |> List.map (fun p -> p.Id + " " + defaultArg p.Version "*") |> String.concat ", ")
                                let restored = readRestored root dir framework
                                cache.[key] <- restored
                                return restored })
                    return restored
        }

    /// <summary>
    /// Restores the package sets `requests` ((framework, packages) pairs) with ONE `dotnet
    /// restore` and keeps each result in this build's cache, so the targets asking for them
    /// afterwards (`restore`, `restoreWith`, `Locks.generate`) do not start msbuild again. Sets
    /// already in the cache and empty ones are skipped; identical sets are restored once. The
    /// synthesized projects are `obj/xake/restore/&lt;key&gt;/restore.csproj`, the batch is a plain
    /// msbuild project referencing them (`obj/xake/restore/batch-&lt;hash&gt;.proj`): restore
    /// evaluates them in one process and resolves each graph on its own, in parallel. When
    /// the batch fails nothing is cached and a warning says so: each target then restores
    /// alone and reports its own error.
    /// </summary>
    let prefetch (requests: (string * Description.Package list) list) : Recipe<ExecContext, unit> =
        recipe {
            let! options = getCtxOptions ()
            let! ctx = getCtx ()
            let! root = packageFolder ()
            let cache = cacheOf ctx
            let wanted =
                requests
                |> List.filter (snd >> List.isEmpty >> not)
                |> List.map (fun (framework, packages) -> let key, text = keyOf root framework packages in key, (framework, text))
                |> List.distinctBy fst
                |> List.filter (fun (key, _) -> not (cache.ContainsKey key))
            // the per-key locks single restores take (`restorePackages`), in key order, so a
            // target restoring one of these keys meanwhile does not run a second msbuild on its `obj`
            let rec holding (keys: string list) (body: Recipe<ExecContext, unit>) : Recipe<ExecContext, unit> =
                match keys with
                | [] -> body
                | key :: rest ->
                    let lock = keyLocks.GetOrAdd (key, fun k -> Resource.newResource ("described-restore:" + k) 1)
                    withResource lock 1 (holding rest body)
            do! holding (wanted |> List.map fst |> List.sort) (recipe {
                // what another target restored while this waited is not restored again
                let wanted = wanted |> List.filter (fun (key, _) -> not (cache.ContainsKey key))
                if not (List.isEmpty wanted) then
                    let dir = restoreDir options.ProjectRoot
                    for key, (_, text) in wanted do
                        let d = Path.Combine (dir, key)
                        Directory.CreateDirectory d |> ignore
                        let project = Path.Combine (d, "restore.csproj")
                        // unchanged text keeps the file (and msbuild's no-op restore) as it is
                        if not (File.Exists project) || File.ReadAllText project <> text then File.WriteAllText (project, text)
                    let keys = wanted |> List.map fst
                    let batch =
                        use sha = System.Security.Cryptography.SHA256.Create ()
                        let hash = sha.ComputeHash (System.Text.Encoding.UTF8.GetBytes (String.concat "\n" keys))
                        Path.Combine (dir, "batch-" + System.BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant().Substring (0, 16) + ".proj")
                    File.WriteAllText (batch,
                        [ yield "<Project>"
                          yield "  <ItemGroup>"
                          for key in keys do yield sprintf "    <ProjectReference Include=\"%s/restore.csproj\" />" key
                          yield "  </ItemGroup>"
                          yield "  <Import Project=\"$(MSBuildToolsPath)/Microsoft.Common.targets\" />"
                          yield "</Project>"
                          yield "" ] |> String.concat "\n")
                    do! trace Info "restoring %d package set(s) in one restore ('%s')" keys.Length batch
                    let! exitCode = runRestore root batch
                    if exitCode <> 0 then
                        do! trace Warning "the batch restore '%s' failed with exit code %d; each target restores its packages alone" batch exitCode
                    else
                        for key, (framework, _) in wanted do
                            cache.[key] <- readRestored root (Path.Combine (dir, key)) framework })
        }

    /// The packages `restore` restores for description `d` and `framework`: its own (project-wide
    /// and `byFramework`) and the implicit ones (`implicitPackages`). Pure.
    let restoreInput (d: Description.Project) (framework: string) : Description.Package list =
        (ProjectPlan.itemsFor d framework).Packages @ implicitPackages d framework

    /// <summary>
    /// Resolves the packages of description `d` for `framework` the way msbuild does: writes
    /// `restoreProjectText` to `obj/xake/restore/&lt;framework&gt;-&lt;hash&gt;/restore.csproj` under
    /// the project root (the hash is of the package set: one folder per distinct set), runs `dotnet restore` on it into the build's package folder
    /// (`DotNetFwk.packageRoot`: `NUGET_PACKAGES`), then reads its `project.assets.json`
    /// (`Nuget.readAssets`, `Nuget.compileItems`; the packages' `frameworkAssemblies` become
    /// `FrameworkReferences`). The repository's `nuget.config` applies; its
    /// `Directory.Build.props`/`Directory.Packages.props` do not. This is the lock-generation
    /// step: `fsc`/`csc` never call it, they take its result through `packages`. A description
    /// without packages for `framework` gives an empty result without running anything. A set
    /// this build restored already (by another target, or by `restoreAll` / `prefetch` in one
    /// batch) comes from the build's cache without running anything either. Only
    /// the description's own packages: a project compiled without a lock whose project
    /// references declare packages takes `restoreWith`.
    /// </summary>
    let restore (d: Description.Project) (framework: string) : Recipe<ExecContext, Restored> =
        restorePackages d framework (restoreInput d framework)

    /// <summary>
    /// The packages description `d`'s project references bring for `framework`, as msbuild
    /// flows them to the referring project: for each project of the closure of `projectRefs`
    /// (breadth first, each once) -- its description at `descriptionOf name`, read and depended
    /// on -- the packages it declares for its nearest framework (`nearestFramework`), at the
    /// version it declares; not the `private` ones (`PrivateAssets="all"` does not flow), not
    /// one `d` itself declares, the first declaration of an id winning. Fails when a
    /// referenced project has no framework compatible with the referring one.
    /// </summary>
    let flowingPackages (descriptionOf: string -> string) (d: Description.Project) (framework: string)
            : Recipe<ExecContext, Description.Package list> =
        let declared = restoreInput d framework
        let rec walk (seen: string list) (queue: (string * string * string) list) (acc: Description.Package list) =
            recipe {
                match queue with
                | [] -> return acc
                | (name, _, _) :: rest when List.contains name seen ->
                    let! result = walk seen rest acc
                    return result
                | (name, referrer, fwk) :: rest ->
                    let! r = Description.load (descriptionOf name)
                    let rf =
                        match nearestFramework fwk r.Frameworks with
                        | Some f -> f
                        | None ->
                            failwithf "'%s' (%s): projectRef '%s' has no framework compatible with it (%s)"
                                referrer fwk name (String.concat ", " r.Frameworks)
                    // with the implicit ones (F#'s System.ValueTuple), as the lock route and msbuild flow them
                    let packages = restoreInput r rf
                    let known = declared @ acc
                    let adds =
                        packages
                        |> List.filter (fun p -> not p.Private && not (known |> List.exists (fun k -> same k.Id p.Id)))
                        |> List.distinctBy (fun p -> p.Id.ToLowerInvariant ())
                    let! result = walk (seen @ [ name ]) (rest @ [ for n in r.ProjectRefs -> n, r.Name, rf ]) (acc @ adds)
                    return result
            }
        walk [] [ for n in d.ProjectRefs -> n, d.Name, framework ] []

    /// The packages `restoreWith` restores: the description's for `framework`, the implicit
    /// ones, and those its project references bring (`flowingPackages`).
    let private withFlowing (descriptionOf: string -> string) (d: Description.Project) (framework: string) =
        recipe {
            let! flowing = flowingPackages descriptionOf d framework
            return (ProjectPlan.itemsFor d framework).Packages @ implicitPackages d framework @ flowing
        }

    /// <summary>
    /// `restore` for a project compiled without a lock (`csc/fsc { project }` straight from the
    /// description): the description's packages plus those its project references bring
    /// (`flowingPackages`; a referenced project's description is at `descriptionOf name`, the
    /// script's own mapping, the one its lock rule hands `Locks.generate`). Lock generation
    /// flows the referenced projects' packages from their locks instead.
    /// <code>
    /// let! pkgs = Described.restoreWith descOf d fwk
    /// do! csc { targetfwk fwk; ref refs; project (descOf name); config cfg; packages pkgs }
    /// </code>
    /// </summary>
    let restoreWith (descriptionOf: string -> string) (d: Description.Project) (framework: string) : Recipe<ExecContext, Restored> =
        recipe {
            let! packages = withFlowing descriptionOf d framework
            let! restored = restorePackages d framework packages
            return restored
        }

    /// <summary>
    /// Fills this build's restore cache for the direct route: for each description of
    /// `descriptions` (paths) and each of its frameworks that `frameworks` names (an empty list:
    /// all of its frameworks), the packages `restoreWith descriptionOf` restores, all in ONE
    /// `dotnet restore` (`prefetch`). The `restoreWith` of each target then reads the cache.
    /// Call it before needing the targets; a target reached without it restores alone, as before:
    /// <code>
    /// "main" => recipe {
    ///     do! Described.restoreAll descOf [ for n in names -> descOf n ] fwks
    ///     do! need [ for n in names do for f in fwks -> dllOf "Release" f n ] }
    /// </code>
    /// </summary>
    let restoreAll (descriptionOf: string -> string) (descriptions: string list) (frameworks: string list) : Recipe<ExecContext, unit> =
        recipe {
            let rec gather (paths: string list) (acc: (string * Description.Package list) list) =
                recipe {
                    match paths with
                    | [] -> return acc
                    | path :: rest ->
                        let! d = Description.load path
                        let fwks = if List.isEmpty frameworks then d.Frameworks else d.Frameworks |> List.filter (fun f -> frameworks |> List.exists (same f))
                        let rec each (fs: string list) (acc: (string * Description.Package list) list) =
                            recipe {
                                match fs with
                                | [] -> return acc
                                | f :: more ->
                                    let! packages = withFlowing descriptionOf d f
                                    let! result = each more (acc @ [ f, packages ])
                                    return result
                            }
                        let! acc = each fwks acc
                        let! result = gather rest acc
                        return result
                }
            let! requests = gather descriptions []
            do! prefetch requests
        }

    /// <summary>
    /// The transitive project references of description `d` (the closure of `projectRefs`, each
    /// description read -- a dependency -- at `descriptionOf name`), each once, a project after
    /// the projects it references: what msbuild hands the compiler for `d`'s project references.
    /// A project compiled straight from its description (no lock) passes their outputs with `ref`:
    /// <code>
    /// let! refs = Described.closure descOf d
    /// do! need [ for r in refs -> dllOf cfg fwk r ]
    /// do! csc { targetfwk fwk; ref (refs |> List.fold (fun fs r -> fs ++ dllOf cfg fwk r) Fileset.Empty)
    ///           project (descOf name); config cfg; packages pkgs }
    /// </code>
    /// Fails on a cycle, naming it.
    /// </summary>
    let closure (descriptionOf: string -> string) (d: Description.Project) : Recipe<ExecContext, string list> =
        // depth first, a project emitted after its references
        let rec visit (path: string list) (name: string) (acc: string list) : Recipe<ExecContext, string list> =
            recipe {
                if List.contains name acc then return acc
                elif List.contains name path then
                    return failwithf "'%s': the project references form a cycle: %s" d.Name (String.concat " -> " (List.rev (name :: path)))
                else
                    let! r = Description.load (descriptionOf name)
                    let! acc = visitAll (name :: path) r.ProjectRefs acc
                    return acc @ [ name ]
            }
        and visitAll (path: string list) (names: string list) (acc: string list) : Recipe<ExecContext, string list> =
            recipe {
                match names with
                | [] -> return acc
                | n :: rest ->
                    let! acc = visit path n acc
                    let! result = visitAll path rest acc
                    return result
            }
        visitAll [ d.Name ] d.ProjectRefs []

    let private hasWildcard (s: string) = s.Contains "*" || s.Contains "?"

    /// <summary>
    /// The files absolute C# source masks name, read through `getFiles` so that the target
    /// depends on the listing (an added `.cs` reruns it). The convention is the import's
    /// (`Import.csharpMasks`): an entry is an include, a `!` prefix makes it an exclude, applied in
    /// order (`["**/*.cs", "!obj/**/*", "!bin/**/*"]`), and `dir/**/*` is every file below
    /// `dir`. The masks become one Xake fileset whose base is the leading wildcard-free folder
    /// of the first include; the files come out sorted (ordinal).
    /// </summary>
    let expand (masks: string list) : Recipe<ExecContext, string list> =
        recipe {
            let split (mask: string) =
                let isExclude = mask.StartsWith "!"
                isExclude, normal (if isExclude then mask.Substring 1 else mask)
            let entries = masks |> List.map split
            match entries |> List.tryFind (fst >> not) with
            | None -> return []
            | Some (_, first) ->
                let fixedOf (mask: string) =
                    let parts = mask.Split '/' |> List.ofArray
                    let fixedParts = parts |> List.takeWhile (hasWildcard >> not)
                    String.concat "/" fixedParts, parts |> List.skip fixedParts.Length |> String.concat "/"
                let baseDir =
                    match fixedOf first with
                    | dir, "" -> normal (Path.GetDirectoryName dir)
                    | dir, _ -> dir
                let relative (mask: string) =
                    let dir, rest = fixedOf mask
                    let rel =
                        if dir = baseDir then "."
                        elif dir.StartsWith (baseDir + "/") then dir.Substring (baseDir.Length + 1)
                        else
                            System.Uri.UnescapeDataString (
                                (System.Uri (baseDir.TrimEnd '/' + "/")).MakeRelativeUri(System.Uri dir).ToString ())
                    match rel, rest with
                    | ".", r -> r
                    | r, "" -> r
                    | r, rest -> r + "/" + rest
                let fs =
                    entries |> List.fold (fun (fs: Fileset) (isExclude, mask) ->
                        if isExclude then fs - relative mask else fs + relative mask) (Fileset.(~+) baseDir)
                let! (Filelist found) = getFiles fs
                return
                    found |> List.map (File.getFullName >> normal) |> List.distinct
                    |> List.sortWith (fun a b -> System.String.CompareOrdinal (a, b))
        }

    /// The generated folder of a described compilation: `obj/xake/<key>/<framework>[/<config>]`
    /// under the build's project root (`ProjectPlan.generatedKey`).
    let generatedDirOf (root: string) (key: string) (framework: string) (config: string option) : string =
        normal (Path.Combine (Array.append [| root; "obj"; "xake" |] (Array.append (key.Split '/') (Array.append [| framework |] (Option.toArray config)))))

    /// What `project` contributes to a compilation, resolved at recipe time.
    type internal Plan = {
        Project: Description.Project
        Framework: string
        /// the project file's folder, absolute, '/'
        ProjectDir: string
        Defines: string list
        Args: string list
        GlobalRefs: string list
        PackageRefs: string list
        /// sources (F#) or absolute masks (C#)
        Sources: string list
        Out: File
        Doc: string option
        Target: TargetType
        Tailcalls: bool
        /// the generated assembly-attributes source (`InternalsVisibleTo` and the
        /// description's `assemblyAttributes`), when it names any
        Attributes: (string * string) option
        /// the `.resx` resources: (resx, compiled `.resources`), absolute
        Resx: (string * string) list }

    let private fullOf (root: string) (path: string) =
        normal (Path.GetFullPath (if Path.IsPathRooted path then path else Path.Combine (root, path)))

    /// The description's plan for one framework, from the settings `project` follows.
    let internal plan (language: Description.Language) (path: string) (spec: Spec) (targetFramework: string) (out: File) (refs: Fileset) =
        recipe {
            let! options = getCtxOptions ()
            let root = options.ProjectRoot
            let descriptionPath = fullOf root path
            let! d = Description.load descriptionPath
            if d.Language <> language then
                failwithf "'%s': the description '%s' is %s, not for this compiler" d.Name path
                    (match d.Language with Description.FSharp -> "F#" | Description.CSharp -> "C#")
            let projectDir = fullOf (Path.GetDirectoryName descriptionPath) d.Project |> Path.GetDirectoryName |> normal
            let inProject = fullOf projectDir

            // the framework: the block's `targetfwk`, else the description's only one, else NETFX-TARGET
            let! netfxTarget = getVar "NETFX-TARGET"
            let framework = ProjectPlan.pickFramework d targetFramework netfxTarget
            let frameworkDefines = ProjectPlan.frameworkDefines framework

            let items = ProjectPlan.itemsFor d framework
            let packages, defines, references, extraArgs = items.Packages, items.Defines, items.References, items.Args
            let packageRefs =
                // the restore may hold more than the description declares (the packages its
                // project references bring, `restoreWith`): all of it is referenced
                match packages, spec.Restored with
                | _, Some r when not (same r.Framework framework) ->
                    failwithf "'%s': the packages were restored for %s, the compilation is for %s" d.Name r.Framework framework
                | _, Some r -> r.References
                | [], None -> []
                | _, None ->
                    failwithf "'%s': the description references packages (%s); resolve them with Described.restore and pass the result with `packages` -- fsc and csc do not resolve packages"
                        d.Name (packages |> List.map (fun p -> p.Id) |> String.concat ", ")
            // the packages' .NET Framework assemblies (`frameworkAssemblies`), as msbuild references them
            let packageFrameworkRefs =
                match spec.Restored with
                | Some r when same r.Framework framework && List.contains "NETFRAMEWORK" frameworkDefines -> r.FrameworkReferences
                | _ -> []

            // a projectRef is passed as a reference by the script (its output path is the script's layout)
            let (Filelist given) = toFileList root refs
            for name in d.ProjectRefs do
                if not (given |> List.exists (fun f -> same (Path.GetFileNameWithoutExtension f.FullName) name)) then
                    do! trace Warning "'%s': projectRef '%s' is not among the references; pass its output with `ref`" d.Name name

            // the shared frameworks (`frameworkReferences`) from the SDK's targeting packs, as the
            // core composes them (`DotNetFwk.sharedFrameworkReferences`); Microsoft.NETCore.App
            // is the target framework's own, referenced anyway
            let shared = ResizeArray<string> ()
            for f in items.FrameworkReferences |> List.filter (same "Microsoft.NETCore.App" >> not) |> List.distinctBy (fun f -> f.ToLowerInvariant ()) do
                let! dlls = DotNetFwk.sharedFrameworkReferences f framework
                shared.AddRange dlls

            let! out =
                if out <> File.undefined then recipe { return out } else
                recipe {
                    let! ctx = getCtx ()
                    let files =
                        ctx.Targets |> List.choose (function FileTarget f -> Some f | _ -> None)
                        |> List.filter (fun f -> not (f.Name.EndsWith ".runtimeconfig.json"))
                    match files with
                    | f :: _ -> return f
                    | [] ->
                        let! f = getTargetFile ()
                        return f
                }

            let configDefines, configArgs, tailcalls =
                match spec.Config with
                | Some cfg -> preset language cfg
                | None -> [], [], true
            let traceDefine, configName = (configDefines |> List.truncate 1), (configDefines |> List.skip (min 1 configDefines.Length))
            let o = d.Options
            let fs = language = Description.FSharp
            let switch (name: string) value = if fs then sprintf "--%s:%s" name value else sprintf "/%s:%s" name value
            let assemblyName = Path.GetFileNameWithoutExtension out.Name
            // embedded resources: the manifest name as given, else msbuild's default
            // (`ProjectPlan.manifestName`); a `.resx` is embedded as the `.resources` it compiles to, under
            // obj/xake/<assembly>/<framework>[/<config>]/ (a build product, compiled by the runner)
            let resxDir =
                normal (Path.Combine (Array.append [| root; "obj"; "xake"; assemblyName; framework |] (Option.toArray spec.Config)))
            // the project-wide resources, then those of this framework only (`byFramework`)
            let frameworkResources =
                d.ByFramework |> List.tryPick (fun (tfm, items) -> if same tfm framework then Some items.Resources else None)
                |> Option.defaultValue []
            let resources =
                d.Resources @ frameworkResources |> List.map (fun r ->
                    let name = r.Name |> Option.defaultValue (ProjectPlan.manifestName (ProjectPlan.rootNamespace d) r.File)
                    if r.File.EndsWith (".resx", System.StringComparison.OrdinalIgnoreCase) then
                        let compiled = resxDir + "/" + name
                        Some (inProject r.File, compiled), switch "resource" (compiled + "," + name)
                    else None, switch "resource" (inProject r.File + "," + name))
            let nowarn = ProjectPlan.sdkNoWarn language @ o.NoWarn |> List.distinct
            let pathmap = (root |> normal |> fun r -> r.TrimEnd '/') + "/=/_/"
            let args =
                configArgs
                @ [ (if fs then "--deterministic+" else "/deterministic+"); switch "pathmap" pathmap ]
                @ (o.LangVersion |> Option.map (switch "langversion") |> Option.toList)
                @ (if o.WarningsAsErrors then [ (if fs then "--warnaserror+" else "/warnaserror+") ] else [])
                @ (if List.isEmpty nowarn then [] else [ switch "nowarn" (String.concat "," nowarn) ])
                @ (match o.Nullable with
                   | Some n when not fs -> [ switch "nullable" n ]
                   | Some "enable" -> [ "--checknulls+" ]
                   | _ -> [])
                @ (o.KeyFile |> Option.map (inProject >> switch "keyfile") |> Option.toList)
                @ (resources |> List.map snd)
                @ extraArgs

            // one generated source per compile: the friends, then the other assembly attributes,
            // in the description's order. Named `<assembly>.InternalsVisibleTo` while it holds
            // only friends (as before), `<assembly>.AssemblyAttributes` otherwise
            let attributes =
                match d.InternalsVisibleTo, d.AssemblyAttributes with
                | [], [] -> None
                | friends, others ->
                    let line (text: string) = if fs then sprintf "[<assembly: %s>]" text else sprintf "[assembly: %s]" text
                    let friend (f: Description.Friend) =
                        let value = match f.Key with Some key -> sprintf "%s, PublicKey=%s" f.Name key | None -> f.Name
                        line (sprintf "System.Runtime.CompilerServices.InternalsVisibleTo(\"%s\")" value)
                    let other (a: Description.Attribute) = line (sprintf "%s(%s)" a.Type (String.concat ", " a.Args))
                    let kind = if List.isEmpty others then ".InternalsVisibleTo" else ".AssemblyAttributes"
                    let path =
                        normal (Path.Combine (root, "obj", "xake", assemblyName, framework,
                                              assemblyName + kind + (if fs then ".fs" else ".cs")))
                    let body =
                        [ yield "// <auto-generated/> by Xake from the project description"
                          if fs then yield "namespace FSharp"; yield ""
                          yield! friends |> List.map friend
                          yield! others |> List.map other
                          if fs then yield "do ()"
                          yield "" ]
                    Some (path, String.concat "\n" body)

            return
                { Project = d
                  Framework = framework
                  ProjectDir = projectDir
                  Defines = (traceDefine @ defines @ configName @ frameworkDefines) |> List.distinct
                  Args = args
                  GlobalRefs =
                    (if List.contains "NETFRAMEWORK" frameworkDefines then ProjectPlan.implicitFrameworkReferences
                     // composed csc references only mscorlib for netstandard (fsc adds netstandard.dll itself)
                     elif not fs && List.contains "NETSTANDARD" frameworkDefines then [ "netstandard" ]
                     else []) @ references @ packageFrameworkRefs
                    |> List.map (fun r -> if r.EndsWith ".dll" then r else r + ".dll")
                    // an assembly name is case-insensitive (`WindowsBase`, `windowsbase`): the first spelling
                    |> List.distinctBy (fun r -> r.ToLowerInvariant ())
                  PackageRefs = packageRefs @ List.ofSeq shared |> List.distinct
                  Sources =
                    d.Sources |> List.map (fun s -> if s.StartsWith "!" then "!" + inProject (s.Substring 1) else inProject s)
                  Out = out
                  Doc = if o.Doc then Some (normal (Path.ChangeExtension (out.FullName, ".xml"))) else None
                  Target = (match d.Kind with Description.Library -> TargetType.Library | Description.Exe -> TargetType.Exe)
                  Tailcalls = tailcalls
                  Attributes = attributes
                  Resx = resources |> List.choose fst } : Plan
        }

    let private filesOf (paths: string list) =
        paths |> List.fold (fun fs p -> fs + (!! p)) Fileset.Empty

    /// The description a block names (`project`), or a failure saying which operation needs it.
    let private projectOf (settings: string option) =
        match settings with
        | Some path -> path
        | None -> failwith "config, packages and a lock on a described compilation need `project \"path\"` in the block (Xake.Hermetic.Dotnet adds them to the core's `project`)"

    /// The resolved `Fsc` of `fsc { ...; project path; config c; packages p }`: the settings
    /// with this package's plan applied (`config`, the restored packages, `--pathmap`), then
    /// `Fsc.ofSettings` of the result (without `Project`, so the core does not compose the
    /// description a second time), and the generated attribute source first.
    let fsc (settings: FscSettingsType) (spec: Spec) : Recipe<ExecContext, Fsc> =
        recipe {
            let path = projectOf settings.Project
            let! p = plan Description.FSharp path spec settings.TargetFramework settings.Out settings.Ref
            let s =
                { settings with
                    Project = None
                    TargetFramework = p.Framework
                    Out = p.Out
                    Target = p.Target
                    Src = settings.Src + filesOf p.Sources
                    Ref = settings.Ref + filesOf p.PackageRefs
                    RefGlobal = settings.RefGlobal @ p.GlobalRefs
                    Define = settings.Define @ p.Defines |> List.distinct
                    Doc = (match p.Doc with Some doc -> File.make doc | None -> settings.Doc)
                    Tailcalls = settings.Tailcalls && p.Tailcalls
                    CommandArgs = settings.CommandArgs @ p.Args }
            let! f = Fsc.ofSettings s
            // the compiler runs in the project's folder, as under `dotnet build` (a relative
            // `<include file=...>` of a doc comment resolves against it)
            let f = { f with Fsc.Resources = f.Resources @ p.Resx; Fsc.Directory = p.ProjectDir }
            match p.Attributes with
            | None -> return f
            | Some (path, content) ->
                return { f with Fsc.Sources = path :: f.Sources; Fsc.Generated = (path, content) :: f.Generated }
        }

    /// The resolved `Csc` of `csc { ...; project path; config c; ... }`, its `Sources` the description's
    /// masks (absolute), as a lock records them; the files they name are expanded (`expand`,
    /// a `getFiles` dependency) for the composition and by `run`. The generated
    /// `InternalsVisibleTo` source comes first.
    let csc (settings: CscSettingsType) (spec: Spec) : Recipe<ExecContext, Csc> =
        recipe {
            let path = projectOf settings.Project
            let! p = plan Description.CSharp path spec settings.TargetFramework settings.Out settings.Ref
            let! files = expand p.Sources
            let s =
                { settings with
                    Project = None
                    TargetFramework = p.Framework
                    Out = p.Out
                    Target = p.Target
                    Src = settings.Src + filesOf files
                    Ref = settings.Ref + filesOf p.PackageRefs
                    RefGlobal = settings.RefGlobal @ p.GlobalRefs
                    Define = settings.Define @ p.Defines |> List.distinct
                    CommandArgs = settings.CommandArgs @ p.Args @ (p.Doc |> Option.map ((+) "/doc:") |> Option.toList) }
            let! c = Csc.ofSettings s
            let generated = p.Attributes |> Option.toList
            return { c with Sources = (generated |> List.map fst) @ p.Sources; Generated = generated @ c.Generated
                            Resources = c.Resources @ p.Resx; Directory = p.ProjectDir }
        }

    /// `c` with every source mask replaced by the files it names now (`expand`). The generated
    /// sources are files and keep their place: those before the first mask stay in front (a
    /// composed compilation's), those after the masks stay after the files (a recorded one's,
    /// in msbuild's order).
    let expandCsc (c: Csc) : Recipe<ExecContext, Csc> =
        recipe {
            let generated = c.Generated |> List.map fst
            let isGenerated s = List.contains s generated
            let before = c.Sources |> List.takeWhile isGenerated
            let rest = c.Sources |> List.skip before.Length
            let! files = expand (rest |> List.filter (isGenerated >> not))
            return { c with Sources = before @ files @ (rest |> List.filter isGenerated) }
        }

    /// <summary>
    /// Builds the C# compilation `masked` (its `Sources` the description's masks) gated by
    /// the lock at `path`, the way `Lock.buildWith` gates a composed one: a missing lock is
    /// recorded (under CI it fails), a present one must equal `masked` (masks compared as
    /// written), and the recorded entry is compiled with its masks expanded (`expand`), so
    /// the lock keeps the masks and an added `.cs` is compiled without touching it.
    /// </summary>
    let buildCsc (options: Lock.Options) (path: string) (masked: Csc) : Recipe<ExecContext, unit> =
        recipe {
            let! ctxOptions = getCtxOptions ()
            let full = fullOf ctxOptions.ProjectRoot path
            if not (File.Exists full) then
                let! ci = Lock.underCi
                if ci then
                    let msg = sprintf "'%s': the lock '%s' is not there. Under CI a lock is never recorded: record it on a developer machine and commit it." masked.Name path
                    do! trace Error "%s" msg
                    failwith msg
                do! Lock.record path (Lock.Compilation.Csc masked)
            else
                let! differences = Lock.verify path (Lock.Compilation.Csc masked)
                if not (List.isEmpty differences) then
                    let msg =
                        sprintf "'%s': the resolved compilation differs from the lock '%s':\n%s\nUpdate the lock deliberately: delete '%s', or run the target that calls Lock.record \"%s\"."
                            masked.Name path (differences |> String.concat "\n") path path
                    do! trace Error "%s" msg
                    if options.Run.FailOnError then failwith msg
            let! doc = Lock.load path
            let recorded = Lock.entry masked.Name doc
            let! expanded = expandCsc recorded.Csc
            do! Lock.compileWith options { recorded with Compilation = Lock.Compilation.Csc expanded }
        }


/// The state of an `fsc {}` block after `config` or `packages`: the settings (with the core's
/// `project`), what `config` and `packages` added, and the lock (`lock`, `nofetch`).
type FscDescribed = { FscSettings: FscSettingsType; FscSpec: Described.Spec; FscLock: (string * bool) option }

/// `fsc { ...; project "path"; config c; ...; resolve }`: the resolved `Fsc`.
type FscDescribedRequest = FscDescribedRequest of FscDescribed

/// The C# twin of `FscDescribed`.
type CscDescribed = { CscSettings: CscSettingsType; CscSpec: Described.Spec; CscLock: (string * bool) option }

/// `csc { ...; project "path"; config c; ...; resolve }`: the resolved `Csc`, sources as masks.
type CscDescribedRequest = CscDescribedRequest of CscDescribed

/// The state of a `csc {}` block after `lock "path"`: the lock path, the settings, and
/// whether a package the lock names may be fetched (`nofetch` turns it off).
type CscLocked = CscLocked of path: string * settings: CscSettingsType * fetch: bool

/// The state of an `fsc {}` block after `lock "path"`: the lock path, the settings, and
/// whether a package the lock names may be fetched (`nofetch` turns it off).
type FscLocked = FscLocked of path: string * settings: FscSettingsType * fetch: bool

/// What the builders below run: a described compilation (`project` with `config`, `packages`
/// or `lock`) composed by `Described.fsc`/`Described.csc`, a plain one by the core.
module internal DescribedRun =

    let fsc (d: FscDescribed) =
        recipe {
            let! f = Described.fsc d.FscSettings d.FscSpec
            Described.refreshResources f.Resources
            let! run = Fsc.runOptions { d.FscSettings with TargetFramework = f.Framework; Project = None }
            match d.FscLock with
            | None -> do! Fsc.run run f
            | Some (path, fetch) ->
                do! Lock.buildWith
                        { Lock.Options.Default with FscRun = run; Restore = { Restore.Options.Default with Enabled = fetch } }
                        path (Lock.Compilation.Fsc f)
        }

    let csc (d: CscDescribed) =
        recipe {
            let! masked = Described.csc d.CscSettings d.CscSpec
            Described.refreshResources masked.Resources
            let! run = Csc.runOptions { d.CscSettings with TargetFramework = masked.Framework; Project = None }
            match d.CscLock with
            | None ->
                let! c = Described.expandCsc masked
                do! Csc.run run c
            | Some (path, fetch) ->
                do! Described.buildCsc
                        { Lock.Options.Default with Run = run; Restore = { Restore.Options.Default with Enabled = fetch } }
                        path masked
        }

/// <summary>
/// `fsc { project "path"; config "Release"; packages p; lock "path" }`: what this package adds
/// to the core's `project` (the description, `Xake.Dotnet.Description`). The core composes a
/// description by itself (`fsc { project "path" }`: no packages, no lock); `config` (the SDK's
/// preset), `packages` (what `Described.restore` resolved) and `lock` make it this package's
/// composition (`Described.fsc`: the description's packages, `--pathmap` of the project root)
/// and, with `lock`, gate it by the lock. `config`/`packages` come after `project`; `lock` and
/// `nofetch` after them, or right after `project`:
/// <code>
/// "out/Release/netstandard2.0/Xake.dll" ..> recipe {
///     let! d = Description.load "src/core/xake.project.json"
///     let! pkgs = Described.restore d "netstandard2.0"          // lock generation's step
///     do! fsc { targetfwk "netstandard2.0"; project "src/core/xake.project.json"; config "Release"; packages pkgs }
/// }
/// </code>
/// </summary>
[<AutoOpen>]
module FscDescribedBuilder =
    type FscSettingsBuilder with
        /// <summary>The SDK's configuration preset, `Debug` or `Release` (`Described.preset`), for
        /// the description `project` names.</summary>
        [<CustomOperation("config")>]
        member _.Config(s: FscSettingsType, config: string) =
            { FscSettings = s; FscSpec = { Described.Spec.None with Config = Some config }; FscLock = None }

        /// <summary>The description's packages as `Described.restore` resolved them; required
        /// when it has any.</summary>
        [<CustomOperation("packages")>]
        member _.Packages(s: FscSettingsType, restored: Described.Restored) =
            { FscSettings = s; FscSpec = { Described.Spec.None with Restored = Some restored }; FscLock = None }

        [<CustomOperation("config")>]
        member _.Config(d: FscDescribed, config: string) = { d with FscSpec = { d.FscSpec with Config = Some config } }

        [<CustomOperation("packages")>]
        member _.Packages(d: FscDescribed, restored: Described.Restored) = { d with FscSpec = { d.FscSpec with Restored = Some restored } }

        /// <summary>Gates the compilation by the lock at `path`, as `lock` does for settings.</summary>
        [<CustomOperation("lock")>]
        member _.Lock(d: FscDescribed, path: string) = { d with FscLock = Some (path, true) }

        /// <summary>After `lock`: never fetch a package the lock names.</summary>
        [<CustomOperation("nofetch")>]
        member _.NoFetch(d: FscDescribed) = { d with FscLock = d.FscLock |> Option.map (fun (p, _) -> p, false) }

        /// <summary>Returns the resolved `Fsc` instead of compiling; must be the last operation.</summary>
        [<CustomOperation("resolve")>]
        member _.Resolve(d: FscDescribed) = FscDescribedRequest d

        member _.Run(FscDescribedRequest d) = Described.fsc d.FscSettings d.FscSpec

        member _.Run(d: FscDescribed) = DescribedRun.fsc d

/// `csc { project "path" }` with `config`, `packages` and `lock`: the C# twin of
/// `FscDescribedBuilder`. The description's sources are masks; they stay masks in the
/// resolved `Csc` and in its lock, and are expanded when compiling (`Described.expand`, a
/// `getFiles` dependency).
[<AutoOpen>]
module CscDescribedBuilder =
    type CscSettingsBuilder with
        /// <summary>The SDK's configuration preset, `Debug` or `Release` (`Described.preset`), for
        /// the description `project` names.</summary>
        [<CustomOperation("config")>]
        member _.Config(s: CscSettingsType, config: string) =
            { CscSettings = s; CscSpec = { Described.Spec.None with Config = Some config }; CscLock = None }

        /// <summary>The description's packages as `Described.restore` resolved them.</summary>
        [<CustomOperation("packages")>]
        member _.Packages(s: CscSettingsType, restored: Described.Restored) =
            { CscSettings = s; CscSpec = { Described.Spec.None with Restored = Some restored }; CscLock = None }

        [<CustomOperation("config")>]
        member _.Config(d: CscDescribed, config: string) = { d with CscSpec = { d.CscSpec with Config = Some config } }

        [<CustomOperation("packages")>]
        member _.Packages(d: CscDescribed, restored: Described.Restored) = { d with CscSpec = { d.CscSpec with Restored = Some restored } }

        /// <summary>Gates the compilation by the lock at `path`; the lock keeps the masks
        /// (`Described.buildCsc`).</summary>
        [<CustomOperation("lock")>]
        member _.Lock(d: CscDescribed, path: string) = { d with CscLock = Some (path, true) }

        /// <summary>After `lock`: never fetch a package the lock names.</summary>
        [<CustomOperation("nofetch")>]
        member _.NoFetch(d: CscDescribed) = { d with CscLock = d.CscLock |> Option.map (fun (p, _) -> p, false) }

        /// <summary>Returns the resolved `Csc` (sources as masks) instead of compiling.</summary>
        [<CustomOperation("resolve")>]
        member _.Resolve(d: CscDescribed) = CscDescribedRequest d

        member _.Run(CscDescribedRequest d) = Described.csc d.CscSettings d.CscSpec

        member _.Run(d: CscDescribed) = DescribedRun.csc d

/// `csc { ...; lock "path" }`: the settings resolved, then built gated by that lock
/// (`Lock.buildWith`, so under CI a missing lock fails) with the run options the settings
/// imply (`Csc.runOptions`). With the core's `project` in the block, the compilation is this
/// package's composition of the description (`Described.csc`: masks kept in the lock), as
/// after `config`/`packages`. `nofetch` comes *after* `lock` -- an operation on the locked
/// state, not on the settings:
/// <code>
/// csc { src !!"*.cs"; out (File.make "app.dll"); lock "locks/app.json"; nofetch }
/// </code>
/// The packages are read from and restored into the build's package folder (the script
/// variable `NUGET_PACKAGES`, else the environment variable, else `~/.nuget/packages`), and
/// fetched unless `NUGET_FETCH` (or `nofetch`) turns fetching off; there is no per-target
/// folder (`packageroot` was removed in 0.2). A script that reads a lock against another
/// folder uses `Lock.loadWith (Roots.packageRootOverride dir)` and `Restore.into dir`.
[<AutoOpen>]
module CscLockBuilder =
    type CscSettingsBuilder with
        /// <summary>Records this compilation in the lock file at the given path (relative to the
        /// project root, or absolute) and, once it exists, refuses to compile anything else:
        /// the resolved settings must match the lock, or the build fails with the differences.
        /// A matching lock is what gets compiled, so its recorded hashes are verified against
        /// disk. To update it, delete the file or call `Lock.record` from a target of the
        /// script's own. A missing lock is recorded, except under CI (`Lock.underCi`), where
        /// it fails the build. Only `nofetch` may follow it.</summary>
        [<CustomOperation("lock")>]
        member _.Lock(s: CscSettingsType, path: string) = CscLocked (path, s, true)

        /// <summary>After `lock`: never fetch a package the lock names but the package folder
        /// does not have (`Restore.Options.Enabled = false`, the per-target form of
        /// `NUGET_FETCH=off`); the missing files are reported by the hash check before
        /// anything is compiled.</summary>
        [<CustomOperation("nofetch")>]
        member _.NoFetch(CscLocked (path, s, _)) = CscLocked (path, s, false)

        member _.Run(CscLocked (path, s, fetch)) =
            match s.Project with
            | Some _ -> DescribedRun.csc { CscSettings = s; CscSpec = Described.Spec.None; CscLock = Some (path, fetch) }
            | None ->
                recipe {
                    let! c = Csc.ofSettings s
                    let! run = Csc.runOptions s
                    do! Lock.buildWith
                            { Run = run; FscRun = FscRunOptions.Default; Restore = { Restore.Options.Default with Enabled = fetch } }
                            path (Lock.Compilation.Csc c)
                }

/// `fsc { ...; lock "path" }`: the twin of `csc { lock }`. The settings resolved
/// (`Fsc.ofSettings`; with `project`, `Described.fsc`), then built gated by that lock
/// (`Lock.buildWith`, so under CI a missing lock fails) with the run options the settings
/// imply (`Fsc.runOptions`); `nofetch` may come *after* `lock`:
/// <code>
/// fsc { targetfwk "net8.0"; src !!"*.fs"; out (File.make "lib.dll"); lock "locks/lib.json" }
/// </code>
/// The F# compiler is the SDK's own `fsc.dll`: pin the SDK (`global.json`, `rollForward:
/// disable`) and the lock records it as a `dotnet-sdk` prerequisite (`Lock.Prerequisite`),
/// checked before every replay; without the pin the recording warns, and the lock only
/// builds where that SDK is installed.
[<AutoOpen>]
module FscLockBuilder =
    type FscSettingsBuilder with
        /// <summary>Records this compilation in the lock file at the given path (relative to the
        /// project root, or absolute) and, once it exists, refuses to compile anything else:
        /// the resolved settings must match the lock, or the build fails with the differences.
        /// A matching lock is what gets compiled, so its recorded hashes are verified against
        /// disk. To update it, delete the file or call `Lock.record` from a target of the
        /// script's own. A missing lock is recorded, except under CI (`Lock.underCi`), where
        /// it fails the build. Only `nofetch` may follow it.</summary>
        [<CustomOperation("lock")>]
        member _.Lock(s: FscSettingsType, path: string) = FscLocked (path, s, true)

        /// <summary>After `lock`: never fetch a package the lock names but the package folder
        /// does not have (`Restore.Options.Enabled = false`, the per-target form of
        /// `NUGET_FETCH=off`); the missing files are reported by the hash check before
        /// anything is compiled.</summary>
        [<CustomOperation("nofetch")>]
        member _.NoFetch(FscLocked (path, s, _)) = FscLocked (path, s, false)

        member _.Run(FscLocked (path, s, fetch)) =
            match s.Project with
            | Some _ -> DescribedRun.fsc { FscSettings = s; FscSpec = Described.Spec.None; FscLock = Some (path, fetch) }
            | None ->
                recipe {
                    let! f = Fsc.ofSettings s
                    let! run = Fsc.runOptions s
                    do! Lock.buildWith
                            { Run = RunOptions.Default; FscRun = run; Restore = { Restore.Options.Default with Enabled = fetch } }
                            path (Lock.Compilation.Fsc f)
                }
