namespace Xake.Hermetic.Dotnet

open System.IO

open Xake
open Xake.Dotnet

/// The locks of described projects (`xake.project.json`): one lock per project and
/// configuration, written by the script's explicit `locks` command (`generateAll`). A lock
/// records what the compiler would be handed -- nothing is compiled here -- in the existing
/// lock format: one entry per target framework, the packages msbuild restore resolved
/// (`Described.restore`), a project reference as the referenced project's output path in the
/// script's layout, unhashed.
module Locks =

    let private same (a: string) (b: string) = System.String.Equals (a, b, System.StringComparison.OrdinalIgnoreCase)
    let private normal (path: string) = path.Replace ('\\', '/')

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
    /// 4.6.1 and later: up to 2.0; `netstandard` itself: not above it). `None` when nothing fits.
    /// </summary>
    let nearestFramework (target: string) (available: string list) : string option =
        match available |> List.tryFind (same target), moniker target with
        | Some exact, _ -> Some exact
        | None, None -> None
        | None, Some (family, version) ->
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

    /// The configuration of a referenced project a project built as `config` gets, from the
    /// configurations the referenced one has a lock for (`hasLock`): `config` itself, else
    /// `Release`, else `Debug` (the two the presets know). Provisional (brief §6, gaps.md
    /// "ref-config fallback order"): the simplest fixed, deterministic order.
    let refConfig (config: string) (hasLock: string -> bool) : string option =
        [ config; "Release"; "Debug" ] |> List.distinctBy (fun s -> s.ToLowerInvariant ()) |> List.tryFind hasLock

    /// The ids `LOCK_REFRESH` names (`*` is every package): comma, semicolon or space separated.
    let private refreshed (value: string option) (id: string) =
        match value with
        | None -> false
        | Some v ->
            v.Split ([| ','; ';'; ' ' |], System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.exists (fun s -> s.Trim () = "*" || same (s.Trim ()) id)

    /// <summary>
    /// The packages handed to restore: `declared` with the version a lock already holds
    /// (`kept`, from the current lock's entry) as exact (`[v]`), unless `refresh` (the value of
    /// `LOCK_REFRESH`) names the id or is `*` -- then as declared, so a floating version floats
    /// again. A package the lock does not hold is passed as declared. Pure.
    /// </summary>
    let keptVersions (refresh: string option) (kept: Lock.Package list) (declared: Description.Package list) =
        declared |> List.map (fun p ->
            match kept |> List.tryFind (fun k -> same k.Id p.Id) with
            | Some k when not (refreshed refresh p.Id) -> { p with Version = Some ("[" + k.Version + "]") }
            | _ -> p)

    /// The recipes one after another, their results in order.
    let private sequence (recipes: Recipe<ExecContext, 'a> list) : Recipe<ExecContext, 'a list> =
        recipes |> List.fold (fun (acc: Recipe<ExecContext, 'a list>) r ->
            recipe {
                let! xs = acc
                let! x = r
                return xs @ [ x ] }) (recipe { return [] })

    /// The descriptions in build order: every project after the projects it references
    /// (`projectRefs`, by name); a reference outside the set is not ordered (its lock must
    /// exist). A cycle fails, naming it.
    let ordered (descriptions: (string * Description.Project) list) =
        let byName name = descriptions |> List.tryFind (fun (_, d) -> d.Name = name)
        let rec visit (path: string list) (acc: (string * Description.Project) list) (item: string * Description.Project) =
            let d = snd item
            if acc |> List.exists (fun (_, x) -> x.Name = d.Name) then acc
            elif List.contains d.Name path then
                failwithf "projectRefs form a cycle: %s" (List.rev (d.Name :: path) |> String.concat " -> ")
            else
                let acc = d.ProjectRefs |> List.choose byName |> List.fold (visit (d.Name :: path)) acc
                acc @ [ item ]
        descriptions |> List.fold (visit []) []

    /// One lock per described project and configuration; see `generateAll`.
    let private generate (all: (string * Description.Project) list) (descPath: string) (d: Description.Project) (config: string)
                         (lockOf: string -> string -> string) (outOf: string -> string -> string -> string) =
        recipe {
            let! options = getCtxOptions ()
            let root = options.ProjectRoot
            let full (p: string) = normal (Path.GetFullPath (if Path.IsPathRooted p then p else Path.Combine (root, p)))
            let! roots = Roots.current
            let configName = config
            let lockPath = lockOf d.Name config
            let lockFull = full lockPath
            let current = if File.Exists lockFull then Some (Lock.read roots lockFull) else None
            let! refreshVar = getVar "LOCK_REFRESH"
            let! refreshEnv = getEnv "LOCK_REFRESH"
            let refresh = refreshVar |> Option.orElse refreshEnv |> Option.filter (fun s -> s.Trim () <> "")
            let assemblyName = d.AssemblyName |> Option.defaultValue d.Name

            let! entries =
                d.Frameworks |> List.map (fun fwk -> recipe {
                    // each project reference: its configuration and framework, chosen from its locks
                    let! refs =
                        d.ProjectRefs |> List.map (fun name -> recipe {
                            let cfg =
                                match refConfig config (fun c -> File.Exists (full (lockOf name c))) with
                                | Some c -> c
                                | None ->
                                    failwithf "'%s': projectRef '%s' has no lock for %s (nor Release, Debug): '%s'; generate the locks of '%s' first"
                                        d.Name name config (lockOf name config) name
                            let! refLock = Lock.load (lockOf name cfg)
                            let frameworks = refLock.Entries |> List.map (fun e -> e.Framework) |> List.distinct
                            let refFwk =
                                match nearestFramework fwk frameworks with
                                | Some f -> f
                                | None ->
                                    failwithf "'%s' (%s): projectRef '%s' has no framework compatible with it in '%s' (%s)"
                                        d.Name fwk name (lockOf name cfg) (String.concat ", " frameworks)
                            let e = refLock.Entries |> List.find (fun e -> e.Framework = refFwk)
                            let output = full (Path.Combine (outOf cfg refFwk name, e.Name + ".dll"))
                            let projectFile =
                                match all |> List.tryFind (fun (_, x) -> x.Name = name) with
                                | Some (p, x) -> full (Path.Combine (Path.GetDirectoryName (full p), x.Project))
                                | None -> name
                            // the referenced project's direct packages flow into this one's restore,
                            // at the version its lock holds, as msbuild flows them
                            let flowing = e.Packages |> List.filter (fun p -> p.Direct)
                            return (output: string), (projectFile: string), flowing })
                        |> sequence

                    // packages: the description's for this framework plus the refs' flowing ones,
                    // a version the current lock holds kept unless LOCK_REFRESH names it
                    let own =
                        d.Packages
                        @ (d.ByFramework |> List.filter (fst >> same fwk) |> List.collect (fun (_, i) -> i.Packages))
                    let flowing =
                        refs |> List.collect (fun (_, _, ps) -> ps)
                        |> List.filter (fun p -> not (own |> List.exists (fun o -> same o.Id p.Id)))
                        |> List.distinctBy (fun p -> p.Id.ToLowerInvariant ())
                        |> List.map (fun p -> ({ Id = p.Id; Version = Some ("[" + p.Version + "]"); Private = false } : Description.Package))
                    let kept =
                        current
                        |> Option.bind (fun doc -> doc.Entries |> List.tryFind (fun e -> e.Framework = fwk && e.Name = assemblyName))
                        |> Option.map (fun e -> e.Packages) |> Option.defaultValue []
                    let forRestore =
                        { d with
                            Packages = keptVersions refresh kept own @ flowing
                            ByFramework = d.ByFramework |> List.map (fun (t, i) -> t, { i with Packages = [] }) }
                    let! restored = Described.restore forRestore fwk

                    let outputs = refs |> List.map (fun (o, _, _) -> o)
                    let refFiles = outputs |> List.fold (fun fs o -> fs + (!! o)) Fileset.Empty
                    // the description's own packages go through `packages`; flowing ones alone through `ref`
                    let restoredRefs = if List.isEmpty own then restored.References |> List.fold (fun fs o -> fs + (!! o)) Fileset.Empty else Fileset.Empty
                    let outFile = File.make (full (Path.Combine (outOf config fwk d.Name, assemblyName + ".dll")))
                    let! compilation =
                        match d.Language with
                        | Description.FSharp -> recipe {
                            let! f = fsc { targetfwk fwk; out outFile; ref (refFiles + restoredRefs); project descPath; config configName; packages restored; resolve }
                            // the output path is the build's (`-o` added when compiling from the lock)
                            let keep o = match FscArgs.parse o with FscArgs.Switch (_, n, _) -> FscArgs.canonical n <> "out" | _ -> true
                            return Lock.Compilation.Fsc { f with Options = f.Options |> List.filter keep } }
                        | Description.CSharp -> recipe {
                            let! c = csc { targetfwk fwk; out outFile; ref (refFiles + restoredRefs); project descPath; config configName; packages restored; resolve }
                            let keep o = match CscArgs.parse o with CscArgs.Switch (n, _) -> CscArgs.canonical n <> "out" | _ -> true
                            return Lock.Compilation.Csc { c with Options = c.Options |> List.filter keep } }
                    // a project reference must be among the compilation's references even when
                    // its output is not built yet
                    let compilation =
                        let missing = outputs |> List.filter (fun o -> not (compilation.Dependencies.References |> List.exists (fun r -> normal r.Path = o)))
                        if List.isEmpty missing then compilation else
                        let add (deps: Dependencies) =
                            { deps with References = (missing |> List.map (fun o -> ({ Path = o; Sha256 = ""; Alias = "" } : Reference))) @ deps.References }
                        match compilation with
                        | Lock.Compilation.Fsc f -> Lock.Compilation.Fsc { f with Dependencies = add f.Dependencies }
                        | Lock.Compilation.Csc c -> Lock.Compilation.Csc { c with Dependencies = add c.Dependencies }
                    let entry : Lock.Entry =
                        { Lock.ofCompilation compilation with
                            Evaluation = { Lock.Evaluation.Empty with ProjectRefs = refs |> List.map (fun (_, p, _) -> p) }
                            Packages = restored.Packages }
                    let unhashed = compilation.Dependencies.References |> List.map (fun r -> r.Path) |> List.filter (fun p -> List.contains (normal p) outputs)
                    let! recorded = Lock.recorded lockPath unhashed entry
                    return recorded })
                |> sequence

            let text = Lock.format roots { Configuration = config; Properties = []; Entries = entries }
            let existing = if File.Exists lockFull then Some ((File.ReadAllText lockFull).Replace ("\r\n", "\n")) else None
            if existing = Some text then
                do! trace Info "'%s': the lock '%s' is unchanged" d.Name lockPath
            else
                Directory.CreateDirectory (Path.GetDirectoryName lockFull) |> ignore
                File.WriteAllText (lockFull, text)
                do! trace Info "'%s': wrote the lock '%s'" d.Name lockPath
        }

    /// <summary>
    /// Generates the locks of the project descriptions `descriptions` (a fileset of
    /// `xake.project.json` files) for each configuration of `configs`: one lock per project and
    /// configuration at `lockOf name config` (relative to the project root), referenced
    /// projects first. Nothing is compiled; a lock is what the compiler would be handed:
    /// <list type="bullet">
    /// <item>one entry per framework of the description, composed as `fsc/csc { project }`
    /// with `config` (the SDK preset), `--deterministic+`, the tokenized `--pathmap`, and no
    /// `-o` (the output path is the build's);</item>
    /// <item>packages through `Described.restore` (msbuild restore). A version the current
    /// lock holds is passed as exact, so it stays; the script variable (or environment
    /// variable) `LOCK_REFRESH=<ids|*>` lets the named packages resolve as declared again;</item>
    /// <item>a project reference to B: B's configuration `refConfig` (this one, else Release,
    /// else Debug -- whichever B has a lock for) and framework `nearestFramework`, recorded as
    /// B's output path `outOf cfgB fwkB B` + `/<assembly>.dll` with no hash, and B's project
    /// file in `Evaluation.ProjectRefs`; B's direct packages flow into the restore.</item>
    /// </list>
    /// A lock is written only when its text changes: regenerating with nothing changed writes
    /// nothing. The script's `locks` command:
    /// <code>
    /// command "locks" { do! Locks.generateAll (!! "src/*/xake.project.json") ["Release"] lockOf outOf }
    /// </code>
    /// </summary>
    let generateAll (descriptions: Fileset) (configs: string list)
                    (lockOf: string -> string -> string) (outOf: string -> string -> string -> string) : Recipe<ExecContext, unit> =
        recipe {
            let! (Filelist files) = getFiles descriptions
            let! loaded =
                files |> List.map (fun f -> recipe { let! d = Description.load f.FullName in return f.FullName, d })
                |> sequence
            let all = ordered (loaded |> List.sortBy (fun (_, d) -> d.Name))
            for (path, d) in all do
                for config in configs do
                    do! generate all path d config lockOf outOf
        }
