namespace Xake.Hermetic.Dotnet

open System.IO

open Xake
open Xake.Dotnet

/// The locks of described projects (`xake.project.json`): one lock per project and
/// configuration, written by the script's lock rule (`generate`, one lock per call; the
/// script orders projects by `need` and runs it from an explicit `update-locks` target). A lock
/// records what the compiler would be handed -- nothing is compiled here -- in the existing
/// lock format: one entry per target framework, the packages msbuild restore resolved
/// (`Described.restore`), a project reference as the referenced project's output path in the
/// script's layout, unhashed.
module Locks =

    let private same (a: string) (b: string) = System.String.Equals (a, b, System.StringComparison.OrdinalIgnoreCase)
    let private normal (path: string) = path.Replace ('\\', '/')

    /// <summary>
    /// The framework of `available` a project targeting `target` references, msbuild's
    /// "nearest" in its simplest form: `target` itself; else the highest of the same family
    /// not above it (`net8.0` takes `net6.0`, `net472` takes `net462`); else the highest
    /// `netstandard` the target implements (.NET and .NET Core: up to 2.1; .NET Framework
    /// 4.6.1 and later: up to 2.0; `netstandard` itself: not above it). `None` when nothing fits.
    /// </summary>
    let nearestFramework (target: string) (available: string list) : string option =
        Described.nearestFramework target available

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

    /// The description `generate` restores for `d` and `fwk`: its packages for `fwk` at the
    /// version the current lock (`current`) holds unless `refresh` names them, then the
    /// packages the project references bring (`refFlowing`, (id, resolved version), first
    /// declaration of an id winning, not one `d` declares) as exact versions. Pure.
    let private restoreFor (refresh: string option) (current: Lock.Document option) (d: Description.Project) (fwk: string)
                           (refFlowing: (string * string) list) : Description.Project =
        let assemblyName = d.AssemblyName |> Option.defaultValue d.Name
        let own =
            d.Packages
            @ (d.ByFramework |> List.filter (fst >> same fwk) |> List.collect (fun (_, i) -> i.Packages))
        let flowing =
            refFlowing
            |> List.filter (fun (id, _) -> not (own |> List.exists (fun o -> same o.Id id)))
            |> List.distinctBy (fun (id, _) -> id.ToLowerInvariant ())
            |> List.map (fun (id, v) -> ({ Id = id; Version = Some ("[" + v + "]"); Private = false } : Description.Package))
        let kept =
            current
            |> Option.bind (fun doc -> doc.Entries |> List.tryFind (fun e -> e.Framework = fwk && e.Name = assemblyName))
            |> Option.map (fun e -> e.Packages) |> Option.defaultValue []
        { d with
            Packages = keptVersions refresh kept own @ flowing
            ByFramework = d.ByFramework |> List.map (fun (t, i) -> t, { i with Packages = [] }) }

    /// The value of `LOCK_REFRESH`: the script variable, else the environment variable.
    let private refreshValue =
        recipe {
            let! refreshVar = getVar "LOCK_REFRESH"
            let! refreshEnv = getEnv "LOCK_REFRESH"
            return refreshVar |> Option.orElse refreshEnv |> Option.filter (fun s -> s.Trim () <> "")
        }

    /// The script's extra roots (`Roots.make`: `$(Name)`, a path relative to the project root
    /// or absolute) as `(absolute root, Name)`, longest first, the built-in tokens left out.
    let private pathmaps (projectRoot: string) (extraRoots: (string * string) list) : (string * string) list =
        Roots.make projectRoot extraRoots
        |> List.filter (fun (token, _) -> extraRoots |> List.exists (fst >> (=) token) && not (List.contains token Roots.builtinTokens))
        |> List.map (fun (token, path) -> path, token.Substring (2, token.Length - 3))

    /// `options` with one `pathmap` per extra root (`<root>/=/<Name>/`, `prefix` is `--` for
    /// fsc, `/` for csc) in front of the project root's, so a source under a sibling checkout
    /// does not leave its absolute path in the assembly (pdb, `CallerFilePath`).
    let private withPathmaps (prefix: string) (mapped: (string * string) list) (options: string list) =
        if List.isEmpty mapped then options else
        let extra = mapped |> List.map (fun (path, name) -> sprintf "%spathmap:%s/=/%s/" prefix path name)
        match options |> List.tryFindIndex (fun o -> o.StartsWith (prefix + "pathmap:")) with
        | Some i -> List.take i options @ extra @ List.skip i options
        | None -> options @ extra

    /// <summary>
    /// Generates the lock of ONE described project for ONE configuration: the description at
    /// `descriptionPath`, configuration `config`, written to `lockOf name config` (relative to
    /// the project root; `name` is the description's). Nothing is compiled; the lock is what
    /// the compiler would be handed:
    /// <list type="bullet">
    /// <item>one entry per framework of the description, composed as `fsc/csc { project }`
    /// with `config` (the SDK preset), `--deterministic+`, the tokenized `--pathmap`, and no
    /// `-o` (the output path is the build's);</item>
    /// <item>packages through `Described.restore` (msbuild restore). A version the current
    /// lock holds is passed as exact, so it stays; the script variable (or environment
    /// variable) `LOCK_REFRESH=&lt;ids|*&gt;` lets the named packages resolve as declared again;</item>
    /// <item>a project reference to B: B's configuration `refConfig` (this one, else Release,
    /// else Debug -- whichever B has a lock for at `lockOf B c`) and framework
    /// `nearestFramework`, recorded as B's output path `outOf cfgB fwkB B` + `/&lt;assembly&gt;.dll`
    /// with no hash, and B's name in `Evaluation.ProjectRefs`; B's direct packages flow into
    /// the restore. B's own project references (the unhashed references of B's lock, so the
    /// closure) are references of this one too, after the direct ones, each once: msbuild
    /// hands csc/fsc the transitive project references.</item>
    /// <item>`extraRoots`: the script's extra roots (`Roots.make`, e.g.
    /// `[ "$(Sibling)", "../sibling-repo" ]`), as for `Lock.loadWith`: a path under one is
    /// written `$(Sibling)/...` (sources, masks, key file, `Directory`), the compilation gets
    /// one `pathmap` `&lt;root&gt;/=/Sibling/` per root before the project root's, and under
    /// `HERMETIC=on` such a path counts as under the project root. The build passes the same
    /// roots (`compileAsWith`, `needAllWith`, `restoreAllWith`).</item>
    /// </list>
    /// B's lock must exist: this does not order anything. The script's lock rule `need`s the
    /// referenced projects' lock rules first (a missing one fails, naming B). The lock is
    /// written only when its text changes. The script's rule (docs/hermetic/lock.md):
    /// <code>
    /// "lock::(name:*)::(cfg:*)" => recipe {
    ///     let! name = getRuleMatch "name"
    ///     let! cfg  = getRuleMatch "cfg"
    ///     let! d = Description.load (descriptionOf name)
    ///     do! need [ for r in d.ProjectRefs -> $"lock::{r}::{cfg}" ]
    ///     do! alwaysRerun ()
    ///     do! Locks.generate lockOf outOf cfg (descriptionOf name) }
    /// "update-locks" &lt;== [ for n in projects -> $"lock::{n}::Release" ]
    /// </code>
    /// </summary>
    let generateWith (extraRoots: (string * string) list) (lockOf: string -> string -> string) (outOf: string -> string -> string -> string)
                     (config: string) (descriptionPath: string) : Recipe<ExecContext, unit> =
        recipe {
            let descPath = descriptionPath
            let! d = Description.load descriptionPath
            let! options = getCtxOptions ()
            let root = options.ProjectRoot
            let full (p: string) = normal (Path.GetFullPath (if Path.IsPathRooted p then p else Path.Combine (root, p)))
            let! roots = Roots.currentWith extraRoots
            let mapped = pathmaps root extraRoots
            let configName = config
            let lockPath = lockOf d.Name config
            let lockFull = full lockPath
            let current = if File.Exists lockFull then Some (Lock.read roots lockFull) else None
            let! refresh = refreshValue
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
                                    failwithf "'%s': projectRef '%s' has no lock for %s (nor Release, Debug): '%s'; generate the lock of '%s' first (the lock rule needs the referenced projects' lock rules, e.g. via `update-locks`)"
                                        d.Name name config (lockOf name config) name
                            let! refLock = Lock.loadWith extraRoots (lockOf name cfg)
                            let frameworks = refLock.Entries |> List.map (fun e -> e.Framework) |> List.distinct
                            let refFwk =
                                match nearestFramework fwk frameworks with
                                | Some f -> f
                                | None ->
                                    failwithf "'%s' (%s): projectRef '%s' has no framework compatible with it in '%s' (%s)"
                                        d.Name fwk name (lockOf name cfg) (String.concat ", " frameworks)
                            let e = refLock.Entries |> List.find (fun e -> e.Framework = refFwk)
                            let output = full (Path.Combine (outOf cfg refFwk name, e.Name + ".dll"))
                            // the description names its references; the slot holds that name
                            let projectFile = name
                            // the referenced project's direct packages flow into this one's restore,
                            // at the version its lock holds, as msbuild flows them
                            let flowing = e.Packages |> List.filter (fun p -> p.Direct)
                            // B's own project references (unhashed in its lock, already B's
                            // closure): csc gets them too, as msbuild passes transitive references
                            let transitive = e.Dependencies.References |> List.filter (fun r -> r.Sha256 = "") |> List.map (fun r -> normal r.Path)
                            return (output: string), (projectFile: string), flowing, transitive })
                        |> sequence

                    // packages: the description's for this framework plus the refs' flowing ones,
                    // a version the current lock holds kept unless LOCK_REFRESH names it
                    let refFlowing = refs |> List.collect (fun (_, _, ps, _) -> ps |> List.map (fun (p: Package) -> p.Id, p.Version))
                    let forRestore = restoreFor refresh current d fwk refFlowing
                    let! restored = Described.restore forRestore fwk

                    // the direct references' outputs, then theirs (the closure), each once, in order
                    let outputs =
                        (refs |> List.map (fun (o, _, _, _) -> o)) @ (refs |> List.collect (fun (_, _, _, t) -> t))
                        |> List.distinct
                    let refFiles = outputs |> List.fold (fun fs o -> fs + (!! o)) Fileset.Empty
                    let outFile = File.make (full (Path.Combine (outOf config fwk d.Name, assemblyName + ".dll")))
                    let! compilation =
                        match d.Language with
                        | Description.FSharp -> recipe {
                            let! f = fsc { targetfwk fwk; out outFile; ref refFiles; project descPath; config configName; packages restored; resolve }
                            // the output path is the build's (`-o` added when compiling from the lock)
                            let keep o = match FscArgs.parse o with FscArgs.Switch (_, n, _) -> FscArgs.canonical n <> "out" | _ -> true
                            return Lock.Compilation.Fsc { f with Options = f.Options |> List.filter keep |> withPathmaps "--" mapped } }
                        | Description.CSharp -> recipe {
                            let! c = csc { targetfwk fwk; out outFile; ref refFiles; project descPath; config configName; packages restored; resolve }
                            let keep o = match CscArgs.parse o with CscArgs.Switch (n, _) -> CscArgs.canonical n <> "out" | _ -> true
                            return Lock.Compilation.Csc { c with Options = c.Options |> List.filter keep |> withPathmaps "/" mapped } }
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
                            Evaluation = { Lock.Evaluation.Empty with ProjectRefs = refs |> List.map (fun (_, p, _, _) -> p) }
                            Packages = restored.Packages }
                    let unhashed = compilation.Dependencies.References |> List.map (fun r -> r.Path) |> List.filter (fun p -> List.contains (normal p) outputs)
                    let! recorded = Lock.recordedWith extraRoots lockPath unhashed entry
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

    /// `generateWith` without extra roots: every path the lock names outside `$(ProjectRoot)`,
    /// `$(NuGetPackageRoot)` and `$(DotnetRoot)` stays absolute.
    let generate (lockOf: string -> string -> string) (outOf: string -> string -> string -> string)
                 (config: string) (descriptionPath: string) : Recipe<ExecContext, unit> =
        generateWith [] lockOf outOf config descriptionPath

    /// A declared version as the version restore resolves it to, when that is known without
    /// resolving: `[1.2.3]` and `1.2.3` (the lowest applicable, the version itself when it
    /// exists); `None` for a range or a floating version.
    let private resolvedVersion (version: string option) =
        let plain (v: string) = v <> "" && v |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '.' || c = '-' || c = '+')
        match version with
        | Some v when v.StartsWith "[" && v.EndsWith "]" && plain (v.Substring (1, v.Length - 2)) -> Some (v.Substring (1, v.Length - 2))
        | Some v when plain v -> Some v
        | _ -> None

    /// <summary>
    /// Restores, in ONE `dotnet restore` (`Described.prefetch`), the packages `generate` is
    /// about to restore for every description of `descriptions` (a fileset of
    /// `xake.project.json` files) and each of its frameworks in configuration `cfg`, so that the
    /// lock rules find them in the build's cache instead of starting msbuild per project and
    /// framework. The sets are computed as `generate` computes them: the versions the current
    /// locks hold (`LOCK_REFRESH` applies), and the packages of a project reference from its lock
    /// (`lockOf`), or -- before it has one -- from its description (`descriptionOf name`) at
    /// the declared exact version. A set that cannot be known ahead (a reference's floating
    /// version without a lock), or that the generation changes (a reference whose lock this run
    /// rewrites), is restored by its lock rule alone, as before. The script's `update-locks`:
    /// <code>
    /// command "update-locks" {
    ///     do! Locks.restoreAll (!! "src/*/xake.project.json") "Release" lockOf descriptionOf
    ///     do! need [ for n in projects -> $"lock::{n}::Release" ] }
    /// </code>
    /// </summary>
    let restoreAllWith (extraRoots: (string * string) list) (descriptions: Fileset) (cfg: string) (lockOf: string -> string -> string) (descriptionOf: string -> string)
            : Recipe<ExecContext, unit> =
        recipe {
            let! options = getCtxOptions ()
            let full (p: string) = normal (Path.GetFullPath (if Path.IsPathRooted p then p else Path.Combine (options.ProjectRoot, p)))
            let! roots = Roots.currentWith extraRoots
            let! refresh = refreshValue
            let lockAt (path: string) = let f = full path in if File.Exists f then Some (Lock.read roots f) else None
            let memo = System.Collections.Generic.Dictionary<string, (string * string) list option> ()
            // the restore input of `d` for `fwk`, or None when it cannot be known ahead
            let rec inputOf (d: Description.Project) (fwk: string) : Recipe<ExecContext, Description.Project option> =
                recipe {
                    let! flowing = flowingOf d.ProjectRefs fwk []
                    return flowing |> Option.map (restoreFor refresh (lockAt (lockOf d.Name cfg)) d fwk)
                }
            and flowingOf (names: string list) (fwk: string) (acc: (string * string) list) : Recipe<ExecContext, (string * string) list option> =
                recipe {
                    match names with
                    | [] -> return Some acc
                    | name :: rest ->
                        let! direct = directOf name fwk
                        match direct with
                        | None -> return None
                        | Some ps ->
                            let! result = flowingOf rest fwk (acc @ ps)
                            return result
                }
            // the direct packages (id, resolved version) of the referenced project `name` for a
            // referrer on `fwk`, as `generate` reads them from its lock
            and directOf (name: string) (fwk: string) : Recipe<ExecContext, (string * string) list option> =
                recipe {
                    let key = name + "|" + fwk
                    match memo.TryGetValue key with
                    | true, known -> return known
                    | _ ->
                        let! known =
                            match refConfig cfg (fun c -> File.Exists (full (lockOf name c))) with
                            | Some c -> recipe {
                                let doc = (lockAt (lockOf name c)).Value
                                let frameworks = doc.Entries |> List.map (fun e -> e.Framework) |> List.distinct
                                return
                                    nearestFramework fwk frameworks
                                    |> Option.map (fun f ->
                                        doc.Entries |> List.find (fun e -> e.Framework = f)
                                        |> fun e -> e.Packages |> List.filter (fun p -> p.Direct) |> List.map (fun p -> p.Id, p.Version)) }
                            | None -> recipe {
                                let! r = Description.load (descriptionOf name)
                                match nearestFramework fwk r.Frameworks with
                                | None -> return None
                                | Some rf ->
                                    let! input = inputOf r rf
                                    return
                                        input |> Option.bind (fun forRestore ->
                                            let packages = Described.restoreInput forRestore rf
                                            let resolved = packages |> List.map (fun p -> p.Id, resolvedVersion p.Version)
                                            if resolved |> List.forall (snd >> Option.isSome) then Some (resolved |> List.map (fun (id, v) -> id, v.Value))
                                            else None) }
                        memo.[key] <- known
                        return known
                }
            let! (Filelist files) = getFiles descriptions
            let rec gather (files: File list) (acc: (string * Description.Package list) list) : Recipe<ExecContext, (string * Description.Package list) list> =
                recipe {
                    match files with
                    | [] -> return acc
                    | f :: rest ->
                        let! d = Description.load f.FullName
                        let rec each (fwks: string list) (acc: (string * Description.Package list) list) : Recipe<ExecContext, (string * Description.Package list) list> =
                            recipe {
                                match fwks with
                                | [] -> return acc
                                | fwk :: more ->
                                    let! input = inputOf d fwk
                                    let acc = match input with Some forRestore -> acc @ [ fwk, Described.restoreInput forRestore fwk ] | None -> acc
                                    let! result = each more acc
                                    return result
                            }
                        let! acc = each d.Frameworks acc
                        let! result = gather rest acc
                        return result
                }
            let! requests = gather files []
            do! Described.prefetch requests
        }

    /// `restoreAllWith` without extra roots.
    let restoreAll (descriptions: Fileset) (cfg: string) (lockOf: string -> string -> string) (descriptionOf: string -> string)
            : Recipe<ExecContext, unit> =
        restoreAllWith [] descriptions cfg lockOf descriptionOf

    /// The file name of an entry's output: the one msbuild gives it (`TargetFileName`, recorded:
    /// `X.exe` for a .NET Framework application), else `<assembly>.dll`.
    let outputName (entry: Lock.Entry) : string =
        match entry.Evaluation.Properties |> Map.tryFind "TargetFileName" with
        | Some name when name <> "" -> name
        | _ -> entry.Name + ".dll"

    /// The token a lock's `Generated` sources carry where the assembly version goes; the lock
    /// does not depend on the version, the build substitutes it (`compileAs`).
    let VersionToken = "$(Version)"

    /// The output switches of a lock entry made the build's: the output at `target` (the `-o`
    /// a Xake-generated lock lacks), inserted where `--doc:` is or before the first section
    /// marker, and a `--doc:` (when the lock asks for doc) re-targeted beside it. Pure.
    let private outputAt (target: string) (c: Lock.Compilation) : Lock.Compilation =
        let xml = normal (Path.ChangeExtension (target, ".xml"))
        let place (isOut: string -> bool) (isDoc: string -> bool) (doc: string) (out: string) (options: string list) =
            let options = options |> List.filter (isOut >> not) |> List.map (fun o -> if isDoc o then doc else o)
            let at =
                options |> List.tryFindIndex isDoc
                |> Option.orElse (options |> List.tryFindIndex (fun o -> o.StartsWith "@"))
                |> Option.defaultValue options.Length
            List.take at options @ [ out ] @ List.skip at options
        match c with
        | Lock.Compilation.Fsc f ->
            let is name o = match FscArgs.parse o with FscArgs.Switch (_, n, _) -> FscArgs.canonical n = name | _ -> false
            Lock.Compilation.Fsc { f with Options = place (is "out") (is "doc") ("--doc:" + xml) ("--out:" + normal target) f.Options }
        | Lock.Compilation.Csc c ->
            let is name (o: string) = not (o.StartsWith "@") && (match CscArgs.parse o with CscArgs.Switch (n, _) -> CscArgs.canonical n = name | _ -> false)
            Lock.Compilation.Csc { c with Options = place (is "out") (is "doc") ("/doc:" + xml) ("/out:" + normal target) c.Options }

    /// <summary>
    /// The version for a project from the script's `versions` (Ant-style mask -> version
    /// variable): the first mask matching `projectPath` (relative to the project root, '/'),
    /// `None` when none does. A mask ending in `**` covers every file below its folder.
    /// </summary>
    let versionFor (versions: (string * 'v) list) (projectPath: string) : 'v option =
        let covers (mask: string) =
            let mask = normal mask
            let mask = if mask.EndsWith "**" then mask + "/*" else mask
            Path.matchesPattern (Path.parse mask) (normal projectPath)
        versions |> List.tryFind (fst >> covers) |> Option.map snd

    /// The files an entry's import read (`Evaluation.Imports`: the project file,
    /// `Directory.Build.props`, an application's `App.config`, ...) that are missing or no
    /// longer have the hash the lock records. Pure (reads the files).
    let staleOf (entry: Lock.Entry) : string list =
        entry.Evaluation.Imports
        |> List.filter (fun h -> h.Sha256 <> "" && (not (File.Exists h.Path) || FileHash.sha256 h.Path <> h.Sha256))
        |> List.map (fun h -> h.Path)

    let private staleMessage (lockPath: string) (entry: Lock.Entry) (changed: string list) =
        sprintf "'%s' (%s): the lock '%s' is stale: %s changed since the import that wrote it; run the import again"
            entry.Name entry.Framework lockPath (changed |> List.map (sprintf "'%s'") |> String.concat ", ")

    /// <summary>
    /// The stale locks among `locks` (lock paths, relative to the project root or absolute):
    /// for each lock with an entry whose imports changed (`staleOf`), the lock and the
    /// messages naming the changed files ("run the import again"). Depends on the files it
    /// reads; does not fail -- what a script's `check` target reports (`check`).
    /// </summary>
    let staleWith (extraRoots: (string * string) list) (locks: string list) : Recipe<ExecContext, (string * string list) list> =
        recipe {
            let! docs = locks |> List.map (Lock.loadWith extraRoots) |> sequence
            let imports = [ for d in docs do for e in d.Entries do for h in e.Evaluation.Imports do if File.Exists h.Path then yield h.Path ] |> List.distinct
            do! needFiles (Filelist (imports |> List.map File.make))
            return
                [ for lockPath, d in List.zip locks docs do
                    match [ for e in d.Entries do match staleOf e with [] -> () | changed -> yield staleMessage lockPath e changed ] with
                    | [] -> ()
                    | messages -> yield lockPath, messages ]
        }

    /// `staleWith` that fails naming every stale lock and its changed files ("run the import
    /// again"); nothing when every lock is current.
    let checkWith (extraRoots: (string * string) list) (locks: string list) : Recipe<ExecContext, unit> =
        recipe {
            let! stale = staleWith extraRoots locks
            if not (List.isEmpty stale) then
                let messages = stale |> List.collect snd
                for m in messages do do! trace Error "%s" m
                failwith (String.concat "\n" messages)
        }

    /// `checkWith` without extra roots.
    let check (locks: string list) : Recipe<ExecContext, unit> = checkWith [] locks

    /// <summary>
    /// Checks the msbuild files a recorded entry's import read (`Evaluation.Imports`: the
    /// project file, `Directory.Build.props`, ...) against the hashes the lock records, and
    /// depends on them. A changed or missing one fails the build naming it, "run the import
    /// again" -- the lock is msbuild's resolution of what those files said. With the variable
    /// `LOCK_STALE=warn` it is a warning instead, but never under CI (`Lock.underCi`).
    /// </summary>
    let private staleImports (lockPath: string) (entry: Lock.Entry) : Recipe<ExecContext, unit> =
        recipe {
            let imports = entry.Evaluation.Imports
            do! needFiles (Filelist (imports |> List.filter (fun h -> File.Exists h.Path) |> List.map (fun h -> File.make h.Path)))
            match staleOf entry with
            | [] -> ()
            | changed ->
                let msg = staleMessage lockPath entry changed
                let! stale = getVar "LOCK_STALE"
                let! ci = Lock.underCi
                match stale |> Option.map (fun v -> v.Trim().ToLowerInvariant ()) with
                | Some "warn" when not ci -> do! trace Warning "%s" msg
                | Some ("warn" | "fail") | None ->
                    do! trace Error "%s" msg
                    failwith msg
                | Some other -> failwithf "LOCK_STALE='%s': expected fail or warn" other
        }

    /// `compileAs` for a `.dll` target.
    let private compileDll (extraRoots: (string * string) list) (lockPath: string) (fwk: string) (versions: (string * RequiredVar<string>) list) : Recipe<ExecContext, unit> =
        recipe {
            let! options = getCtxOptions ()
            let root = normal (Path.GetFullPath options.ProjectRoot)
            let! target = getTargetFile ()
            let target = normal target.FullName
            let! doc = Lock.loadWith extraRoots lockPath
            let entry =
                match doc.Entries |> List.filter (fun e -> same e.Framework fwk) with
                | [ e ] -> e
                | [] ->
                    failwithf "the lock '%s' has no entry for %s (%s)" lockPath fwk
                        (doc.Entries |> List.map (fun e -> e.Framework) |> String.concat ", ")
                | many ->
                    failwithf "the lock '%s' has %d entries for %s (%s); one per framework is expected" lockPath many.Length fwk
                        (many |> List.map (fun e -> e.Name) |> String.concat ", ")
            let asm = Path.GetFileNameWithoutExtension target
            if entry.Name <> asm then
                failwithf "'%s': the target '%s' is not the assembly of the lock '%s' (%s)" entry.Name target lockPath (outputName entry)

            // a recorded entry is msbuild's resolution of the files its import read: one changed
            // since (a PackageReference added to the csproj) makes the lock stale
            if entry.Recorded.IsSome then
                do! staleImports lockPath entry

            // project-reference outputs first (unhashed in the lock): another rule's targets. The
            // hashed ones are packages, which may not be fetched yet: the runner needs them after
            // `compileFrom`'s restore
            do! needFiles (Filelist (entry.Dependencies.References |> List.filter (fun r -> r.Sha256 = "") |> List.map (fun r -> File.make r.Path)))
            // an application copies its referenced projects' outputs too
            let app = entry.Recorded |> Option.bind (fun r -> r.App)
            match app |> Option.map Apps.projectCopies with
            | Some (_ :: _ as outputs) -> do! needFiles (Filelist (outputs |> List.distinct |> List.map File.make))
            | _ -> ()

            let c = entry.Compilation
            let! c =
                match c, entry.Recorded with
                // recorded from msbuild: msbuild's files in its order, those the masks no longer
                // name dropped, a file the masks name since appended
                // read from a lock: the masks' files in their order, the placed ones (generated,
                // msbuild's exceptions) where msbuild had them
                | Lock.Compilation.Csc recorded, Some ({ Placed = Some placed } as r) when not (List.isEmpty r.Masks) -> recipe {
                    let! files = Described.expand r.Masks
                    let generated = recorded.Generated |> List.map fst |> set
                    let named = set files
                    let keep (f: string) = generated.Contains f || named.Contains (normal f)
                    return Lock.Compilation.Csc { recorded with Sources = Lock.reconstructSources r.Masks placed keep files } }
                | Lock.Compilation.Csc recorded, Some r when not (List.isEmpty r.Masks) -> recipe {
                    let! files = Described.expand r.Masks
                    let generated = recorded.Generated |> List.map fst
                    let isGenerated s = List.contains s generated
                    let named = set files
                    let kept = recorded.Sources |> List.filter (fun s -> isGenerated s || named.Contains (normal s))
                    let added = files |> List.filter (fun f -> not (recorded.Sources |> List.exists (fun s -> normal s = f)))
                    let firstGenerated = kept |> List.tryFindIndex isGenerated |> Option.defaultValue kept.Length
                    return Lock.Compilation.Csc { recorded with Sources = List.take firstGenerated kept @ added @ List.skip firstGenerated kept } }
                | Lock.Compilation.Csc _, Some _ -> recipe { return c }
                | Lock.Compilation.Csc masked, None -> recipe {
                    let! expanded = Described.expandCsc masked
                    return Lock.Compilation.Csc expanded }
                | Lock.Compilation.Fsc _, _ -> recipe { return c }

            let! c =
                if not (c.Generated |> List.exists (fun (_, text) -> text.Contains VersionToken)) then recipe { return c } else
                recipe {
                    let projectDir =
                        c.Sources |> List.tryFind (fun s -> not (c.Generated |> List.exists (fst >> (=) s)) && not (s.StartsWith "!"))
                        |> Option.map (fun s ->
                            let parts = (normal s).Split '/' |> List.ofArray
                            let fixedParts = parts |> List.takeWhile (fun p -> not (p.Contains "*" || p.Contains "?"))
                            String.concat "/" (if fixedParts.Length = parts.Length then List.truncate (parts.Length - 1) parts else fixedParts))
                        |> Option.defaultValue root
                    let relative = if projectDir.StartsWith (root + "/") then projectDir.Substring (root.Length + 1) else projectDir
                    let projectPath = relative + "/" + Description.FileName
                    match versionFor versions projectPath with
                    | None ->
                        return failwithf "'%s': the lock '%s' needs a version (%s) and no mask of `versions` matches '%s' (%s)"
                                    entry.Name lockPath VersionToken projectPath (versions |> List.map fst |> String.concat ", ")
                    | Some var ->
                        let! version = var
                        let subst (path, (text: string)) = path, text.Replace (VersionToken, version)
                        return
                            match c with
                            | Lock.Compilation.Fsc f -> Lock.Compilation.Fsc { f with Generated = f.Generated |> List.map subst }
                            | Lock.Compilation.Csc x -> Lock.Compilation.Csc { x with Generated = x.Generated |> List.map subst } }

            // a `.resx` edited since its `.resources` was compiled (Xake 3.8's runner still compiles a missing one only)
            Described.refreshResources c.Resources
            let! server = CompilerServer.resolve None
            let lockOptions = { Lock.Options.Default with Run = { Lock.Options.Default.Run with Server = server } }
            do! Lock.compileFromWith extraRoots lockPath lockOptions { entry with Compilation = outputAt target c }

            // an application: what msbuild writes beside the assembly, from the lock (`Apps.write`)
            match app with
            | None -> ()
            | Some app ->
                let! roots = Roots.currentWith extraRoots
                let dotnetRoot = roots |> List.tryFind (fst >> (=) "$(DotnetRoot)") |> Option.map snd |> Option.defaultValue ""
                let! packageRoot = DotNetFwk.packageRoot ()
                let! warnings = Apps.write dotnetRoot packageRoot lockPath entry.Name entry.Framework entry.Evaluation.Sdk app target
                for w in warnings do do! trace Warning "%s" w
        }

    /// Whether the compilation asks for an XML doc file (`/doc:`, `--doc:`).
    let private asksDoc (c: Lock.Compilation) =
        match c with
        | Lock.Compilation.Fsc f -> f.Options |> List.exists (fun o -> match FscArgs.parse o with FscArgs.Switch (_, n, _) -> FscArgs.canonical n = "doc" | _ -> false)
        | Lock.Compilation.Csc c -> c.Options |> List.exists (fun o -> not (o.StartsWith "@") && (match CscArgs.parse o with CscArgs.Switch (n, _) -> CscArgs.canonical n = "doc" | _ -> false))

    /// The `.xml` beside a `.dll` target: the dll rule writes it (the side output of the
    /// compile); this needs the dll and checks the file is there.
    let private docOf (extraRoots: (string * string) list) (lockPath: string) (fwk: string) (xml: string) : Recipe<ExecContext, unit> =
        recipe {
            let! doc = Lock.loadWith extraRoots lockPath
            let asm = Path.GetFileNameWithoutExtension xml
            let entry = doc.Entries |> List.tryFind (fun e -> same e.Framework fwk && e.Name = asm && asksDoc e.Compilation)
            if entry.IsNone then
                failwithf "'%s': the lock '%s' asks for no XML doc for %s: nothing writes '%s' (set `options.doc` in the description and update the lock)"
                    asm lockPath fwk xml
            let dll = Path.Combine (Path.GetDirectoryName xml, outputName entry.Value)
            do! needFiles (Filelist [ File.make dll ])
            if not (File.Exists xml) then
                failwithf "'%s': '%s' is missing while '%s' is up to date; delete the dll to compile it again" asm xml dll
        }

    /// <summary>
    /// Compiles the lock entry for `fwk` of the lock at `lockPath` into the target of the
    /// calling file rule -- the `.dll` at the script's layout path:
    /// <code>
    /// target "out/(cfg:*)/(fwk:*)/(name:*).dll" {
    ///     let! cfg = getRuleMatch "cfg"
    ///     let! fwk = getRuleMatch "fwk"
    ///     let! name = getRuleMatch "name"
    ///     do! Locks.compileAs (lockOf name cfg) fwk versions }
    /// </code>
    /// <list type="bullet">
    /// <item>reads the lock (a dependency on it, `Lock.load`); the target's file name must be
    /// the entry's assembly name;</item>
    /// <item>the output: `-o`/`/out:` at the target (a Xake-generated lock has none); a
    /// `--doc:`/`/doc:` the lock carries is re-targeted beside it -- the `.xml` is a side
    /// output of the dll's rule;</item>
    /// <item>a target `.xml` (a rule over the `.xml` with the same body, for a script that
    /// needs the doc file before or without the dll, e.g. to pack it) needs the `.dll` beside
    /// it, whose rule writes it; it fails when the lock asks for no doc. A rule declaring both
    /// (`targets [dll; xml]`) is not the form: a lock without doc never writes the `.xml`, so
    /// such a rule would run on every build;</item>
    /// <item>depends on the unhashed outputs of project references before compiling, so a
    /// referenced project's `.dll` is built first by its own rule and a change to it rebuilds
    /// this one; the hashed references (packages) are fetched by the restore below when
    /// missing, then needed by the runner;</item>
    /// <item>C# source masks are expanded through `getFiles` (`Described.expandCsc`), so an
    /// added or removed `.cs` rebuilds;</item>
    /// <item>a `.resx` resource is compiled by the runner into the `.resources` the lock names
    /// (under `obj/xake/`); the resx is an input;</item>
    /// <item>a `$(Version)` token in `Generated` is replaced by the version of the first
    /// `versions` mask matching the project (its folder, where its first source lies, as
    /// `&lt;folder&gt;/xake.project.json` relative to the project root); the variable is read
    /// (a dependency) only when the token is there;</item>
    /// <item>then `Lock.compileFrom`: prerequisites, the `HERMETIC=on` gate (message 10),
    /// restore of what the lock names, the compiler (csc's server per `CSC_SERVER`) in the
    /// project's folder (the lock's `Directory`).</item>
    /// </list>
    /// </summary>
    let compileAsWith (extraRoots: (string * string) list) (lockPath: string) (fwk: string) (versions: (string * RequiredVar<string>) list) : Recipe<ExecContext, unit> =
        recipe {
            let! target = getTargetFile ()
            if target.FullName.EndsWith (".xml", System.StringComparison.OrdinalIgnoreCase) then
                do! docOf extraRoots lockPath fwk (normal target.FullName)
            else
                do! compileDll extraRoots lockPath fwk versions
        }

    /// `compileAsWith` without extra roots.
    let compileAs (lockPath: string) (fwk: string) (versions: (string * RequiredVar<string>) list) : Recipe<ExecContext, unit> =
        compileAsWith [] lockPath fwk versions

    /// <summary>
    /// Needs the outputs of every described project of `descriptions` (a fileset of
    /// `xake.project.json` files) in configuration `cfg`: for each framework of a
    /// description, `outOf cfg fwk name` + `/&lt;assembly&gt;.dll` -- the targets the script's
    /// compile rule (`compileAs`) builds, all at once so independent ones run in parallel. A
    /// described project without a lock (`lockOf name cfg`) fails, naming the `update-locks` rule.
    /// The packages the locks name that the folder lacks are fetched first in one restore; when
    /// that fails it is a warning, and each compile restores (and reports) its own.
    /// The script's `build` command:
    /// <code>
    /// command "build" { do! Locks.needAll (!! "src/*/xake.project.json") "Release" lockOf outOf }
    /// </code>
    /// </summary>
    let needAllWith (extraRoots: (string * string) list) (descriptions: Fileset) (cfg: string)
                    (lockOf: string -> string -> string) (outOf: string -> string -> string -> string) : Recipe<ExecContext, unit> =
        recipe {
            let! options = getCtxOptions ()
            let! (Filelist files) = getFiles descriptions
            let! loaded =
                files |> List.map (fun f -> Description.load f.FullName) |> sequence
            let missing =
                loaded |> List.filter (fun d ->
                    let lockPath = lockOf d.Name cfg
                    not (File.Exists (if Path.IsPathRooted lockPath then lockPath else Path.Combine (options.ProjectRoot, lockPath))))
            if not (List.isEmpty missing) then
                failwithf "no %s lock for %s; generate the locks first (e.g. the script's `update-locks`, over Locks.generate)"
                    cfg (missing |> List.map (fun d -> sprintf "'%s' ('%s')" d.Name (lockOf d.Name cfg)) |> String.concat ", ")
            let! docs = loaded |> List.map (fun d -> Lock.loadWith extraRoots (lockOf d.Name cfg)) |> sequence
            // the output of each framework: the name msbuild gives it when the lock records one
            let targets =
                [ for d, doc in List.zip loaded docs do
                    let asm = d.AssemblyName |> Option.defaultValue d.Name
                    for fwk in d.Frameworks ->
                        let name =
                            match doc.Entries |> List.tryFind (fun e -> same e.Framework fwk) with
                            | Some e -> outputName e
                            | None -> asm + ".dll"
                        normal (Path.Combine (outOf cfg fwk d.Name, name)) ]
            // the packages every lock names that the folder lacks, fetched in one restore before
            // the compiles (each would otherwise start its own); nothing missing costs a
            // `File.Exists` per path. What is still wrong is reported by each compile
            // A failing restore (a bad feed, a missing version in one lock) does not fail the
            // build here: each compile restores what it misses again and fails, or not, alone
            let! problems =
                recipe {
                    try
                        let! problems = Restore.ensure Restore.Options.Default (Lock.restoreRequest (docs |> List.collect (fun doc -> doc.Entries)))
                        return problems
                    with e -> return [ e.Message ]
                }
            if not (List.isEmpty problems) then
                do! trace Warning "fetching the packages of the %s locks at once failed; each compile restores its own:\n%s" cfg (String.concat "\n" problems)
            do! need targets
        }

    /// `needAllWith` without extra roots.
    let needAll (descriptions: Fileset) (cfg: string)
                (lockOf: string -> string -> string) (outOf: string -> string -> string -> string) : Recipe<ExecContext, unit> =
        needAllWith [] descriptions cfg lockOf outOf

    /// The prototype's flat output layout: every project of a configuration and framework in one
    /// folder, `out/<cfg>/<fwk>` (a script's `outOf`; `Import.RecordOptions.OutOf` the same).
    let flatOutOf (cfg: string) (fwk: string) (_: string) : string = sprintf "out/%s/%s" cfg fwk

    /// The prototype's run folders: `run/<cfg>/<fwk>/<name>` (a script's `runOf`).
    let runOf (cfg: string) (fwk: string) (name: string) : string = sprintf "run/%s/%s/%s" cfg fwk name

    /// Whether a lock entry runs (a run folder is built for it): an application or a test
    /// project, as msbuild recorded it (`App` with an `Exe`/`WinExe` output type or generated
    /// runtime files, or `IsTestProject`).
    let runs (entry: Lock.Entry) : bool =
        let app = entry.Recorded |> Option.bind (fun r -> r.App)
        let isTest = entry.Evaluation.Properties |> Map.tryFind "IsTestProject" |> Option.exists (fun v -> v.Trim().ToLowerInvariant () = "true")
        isTest
        || (app |> Option.exists (fun a -> a.OutputType = "Exe" || a.OutputType = "WinExe" || not (List.isEmpty a.Files)))

    /// The files of an entry's compile beside its output (`dir`): the assembly (the compile
    /// rule's target), then its side outputs that exist (the pdb, the XML doc, the application's
    /// files `withFiles` selects, the apphost when `withHost`).
    let private outputsOf (dir: string) (e: Lock.Entry) (withFiles: Lock.AppFile -> bool) (withHost: bool) : string * (string * string) list =
        let app = e.Recorded |> Option.bind (fun r -> r.App)
        let asm = outputName e
        let side =
            [ yield e.Name + ".pdb"
              if asksDoc e.Compilation then yield e.Name + ".xml"
              match app with
              | Some a ->
                  for f in a.Files do if withFiles f then yield f.Name
                  if withHost && a.Host.IsSome then
                      for h in [ e.Name; e.Name + ".exe" ] do if h <> asm then yield h
              | None -> () ]
        normal (Path.Combine (dir, asm)),
        [ for f in side do
            let p = normal (Path.Combine (dir, f))
            if File.Exists p then yield f, p ]

    /// <summary>
    /// Builds the run folder of project `name` (configuration `cfg`, framework `fwk`) -- the
    /// folder of the calling rule's target, the manifest `.xake-run.txt` -- from that project's
    /// lock closure only, everything as msbuild recorded it:
    /// <code>
    /// rule ("run/(cfg:*)/(fwk:*)/(name:*)/.xake-run.txt" ..> recipe {
    ///     let! cfg = getRuleMatch "cfg"
    ///     let! fwk = getRuleMatch "fwk"
    ///     let! name = getRuleMatch "name"
    ///     do! Locks.runFolder lockOf outOf cfg fwk name })
    /// </code>
    /// <list type="number">
    /// <item>the root's own files, never replaced: its compile outputs in `outOf cfg fwk name`
    /// (the assembly -- a dependency on the compile rule's target --, pdb, XML doc,
    /// runtimeconfig/deps.json/config, apphost), its copy-to-output items (`App.Content`), its
    /// copy-local files (`App.Copy`: the package runtime assets at the versions its own restore
    /// resolved);</item>
    /// <item>then the closure of project references in dependency order (a recorded entry's
    /// `Recorded.Projects`, at the configuration and framework msbuild chose; else
    /// `refConfig`/`nearestFramework`): each one's assembly in `outOf` with the files beside it
    /// that have the root's `Related` extensions, a referenced application's runtimeconfig and
    /// deps.json and apphost (the SDK's items for referencing projects), its copy-to-output
    /// items, and its copy-local files that are not package files (a package's files are the
    /// versions the root's own restore resolved: msbuild never copies a referenced project's);</item>
    /// <item>one source per path: the first, except that in the closure a higher assembly
    /// version (not the package version) replaces a lower one; every dropped source with other
    /// content is written to `.xake-run.conflicts.txt` beside the manifest (a warning);</item>
    /// <item>placed by `mode` (`RunFolder.Clone`: clone, else copy; `RunFolder.Link`: hard link,
    /// else clone, else copy; `RunFolder.apply`): an unchanged file is kept, a changed one
    /// unlinked and placed again, a file of the previous run no longer planned removed -- each
    /// only when it is still what a run placed; every path stays inside the folder; a no-op
    /// touches nothing, a recompiled source rewrites the manifest (its stamp).</item>
    /// </list>
    /// The rule depends on the locks it reads, the closure's assemblies (rule targets, needed
    /// before anything beside them is looked at: a `deploy` on an unbuilt tree builds first; a
    /// recompile, in this run or an earlier one, re-runs it), the package files and items; the
    /// side outputs (pdb, xml, runtime files) are not needed through the compile rule -- they
    /// are written with their assembly.
    /// </summary>
    let runFolderWith (extraRoots: (string * string) list) (mode: RunFolder.Mode) (lockOf: string -> string -> string)
                      (outOf: string -> string -> string -> string) (cfg: string) (fwk: string) (name: string) : Recipe<ExecContext, unit> =
        recipe {
            let! options = getCtxOptions ()
            let full (p: string) = normal (Path.GetFullPath (if Path.IsPathRooted p then p else Path.Combine (options.ProjectRoot, p)))
            let! target = getTargetFile ()
            let manifest = target.FullName
            let what = sprintf "run folder %s/%s/%s" cfg fwk name
            let entryOf (lockPath: string) (doc: Lock.Document) (f: string) =
                match doc.Entries |> List.filter (fun e -> same e.Framework f) with
                | [ e ] -> e
                | [] -> failwithf "%s: the lock '%s' has no entry for %s (%s)" what lockPath f (doc.Entries |> List.map (fun e -> e.Framework) |> String.concat ", ")
                | _ -> failwithf "%s: the lock '%s' has more than one entry for %s" what lockPath f
            let rootLock = lockOf name cfg
            let! doc = Lock.loadWith extraRoots rootLock
            let root = entryOf rootLock doc fwk
            let rootApp = root.Recorded |> Option.bind (fun r -> r.App)
            let related = rootApp |> Option.map (fun a -> a.Related) |> Option.defaultValue [ ".pdb"; ".xml" ]
            let candidates = ResizeArray<RunFolder.Candidate> ()
            let assemblies = ResizeArray<string> ()
            let hashes = System.Collections.Generic.Dictionary<string, string> ()
            let itemSources = System.Collections.Generic.HashSet<string> ()
            let entries = ResizeArray<Lock.Entry> [ root ]
            let add own (src: string) (To: string) = candidates.Add { To = To; Source = src; Own = own }
            // the package folder and the .NET installation: a package's (or pack's) file in a
            // referenced project's copy-local list is that project's restore, not this one's
            let! packageRoot = DotNetFwk.packageRoot ()
            let! roots = Roots.currentWith extraRoots
            let packageDirs =
                [ packageRoot
                  roots |> List.tryFind (fst >> (=) "$(DotnetRoot)") |> Option.map snd |> Option.defaultValue "" ]
                |> List.filter ((<>) "") |> List.map (fun d -> (normal (Path.GetFullPath d)).TrimEnd '/' + "/")
            let isPackageFile (path: string) = packageDirs |> List.exists (fun d -> (normal path).StartsWith (d, System.StringComparison.OrdinalIgnoreCase))
            // a file of the package folder: its package id (the folder's first name), lower case
            let packageRootDir = (normal (Path.GetFullPath packageRoot)).TrimEnd '/' + "/"
            let packageIdOf (path: string) =
                let path = normal path
                if path.StartsWith (packageRootDir, System.StringComparison.OrdinalIgnoreCase) then
                    match path.Substring(packageRootDir.Length).Split '/' with
                    | parts when parts.Length > 2 -> Some (parts.[0].ToLowerInvariant ())
                    | _ -> None
                else None
            let rootPackages = System.Collections.Generic.HashSet<string> (root.Packages |> List.map (fun p -> p.Id.ToLowerInvariant ()))
            let notTaken = ResizeArray<string> ()
            let items own (framework: string) (app: Lock.App option) =
                match app with
                | None -> ()
                | Some a ->
                    // msbuild copies the items after the copy-local files: an item wins its path
                    for c in a.Content do
                        if c.Sha256 <> "" then hashes.[c.Path] <- c.Sha256
                        itemSources.Add c.Path |> ignore
                        add own c.Path c.To
                    for c in a.Copy do
                        // a referenced project's output comes with the closure (its related files
                        // too); the package files are the root's own restore's only -- a referenced
                        // project's are the versions its restore resolved, which msbuild never
                        // copies here (its other copy-local files, found beside its output, it does)
                        if c.Sha256 <> "" && (own || not (isPackageFile c.Path)) then
                            hashes.[c.Path] <- c.Sha256
                            itemSources.Add c.Path |> ignore
                            add own c.Path c.To
                        elif c.Sha256 <> "" then
                            // the rule holds under PackageReference transitivity: the root's restore
                            // has the referenced project's packages. One it lacks in a .NET Framework
                            // library's list (`PrivateAssets`, `ExcludeAssets`; such a library copies
                            // its packages into its `bin/`, whence msbuild's RAR copies them) may
                            // still be copied by msbuild: said, not placed. Elsewhere a package the
                            // root's graph lacks is one its framework does not need (a netstandard
                            // library's System.Memory under net8.0), which msbuild drops too
                            let netFramework = System.Text.RegularExpressions.Regex.IsMatch (framework, "^net[1-4][0-9]*$")
                            match packageIdOf c.Path with
                            | Some id when netFramework && not (rootPackages.Contains id) ->
                                notTaken.Add (sprintf "%s: '%s' (package %s) is not placed: the root's restore lacks that package (PrivateAssets/ExcludeAssets in a referenced project?); msbuild may copy it from the referenced project's output" what c.To id)
                            | _ -> ()
            // the closure first, breadth first in msbuild's reference order: (entry, output folder)
            let rootDir = full (outOf cfg fwk name)
            let closure = ResizeArray<Lock.Entry * string> ()
            let seen = System.Collections.Generic.HashSet<string> ([ name ], System.StringComparer.OrdinalIgnoreCase)
            let queue = System.Collections.Generic.Queue<string * string * string option> ()
            let enqueue (e: Lock.Entry) (c: string) =
                match e.Recorded with
                | Some r -> for p in r.Projects do queue.Enqueue (p.Name, p.Configuration, Some p.Framework)
                | None -> for p in e.Evaluation.ProjectRefs do queue.Enqueue (p, c, None)
            enqueue root cfg
            while queue.Count > 0 do
                let r, c, f = queue.Dequeue ()
                if seen.Add r then
                    let c =
                        if File.Exists (full (lockOf r c)) then c
                        else
                            match refConfig c (fun x -> File.Exists (full (lockOf r x))) with
                            | Some x -> x
                            | None -> failwithf "%s: the referenced project '%s' has no lock ('%s')" what r (lockOf r c)
                    let! l = Lock.loadWith extraRoots (lockOf r c)
                    let f =
                        match f with
                        | Some f -> f
                        | None ->
                            match nearestFramework fwk (l.Entries |> List.map (fun e -> e.Framework) |> List.distinct) with
                            | Some f -> f
                            | None -> failwithf "%s: the referenced project '%s' has no framework for %s" what r fwk
                    let e = entryOf (lockOf r c) l f
                    entries.Add e
                    closure.Add ((e, full (outOf c f r)))
                    enqueue e c
            // the assemblies are the compile rules' targets: built before anything beside them is
            // looked at (the pdb, xml, runtime files and apphost exist once they are), so a
            // `deploy` on an unbuilt tree builds first and places the whole folder; a recompile
            // re-runs this rule
            let rootAsm = normal (Path.Combine (rootDir, outputName root))
            assemblies.Add rootAsm
            for e, dir in closure do assemblies.Add (normal (Path.Combine (dir, outputName e)))
            do! needFiles (Filelist (assemblies |> Seq.map File.make |> List.ofSeq))
            // 1. the root's own
            let asm, side = outputsOf rootDir root (fun _ -> true) true
            add true asm (Path.GetFileName asm)
            for f, p in side do add true p f
            items true fwk rootApp
            // 2. the closure
            for e, dir in closure do
                // a referenced application's runtime files and apphost: the SDK's items for
                // referencing projects (`AddDepsJsonAndRuntimeConfigToCopyItemsForReferencingProjects`,
                // the `None` item of `AppHostIntermediatePath`)
                let isRuntimeFile (f: Lock.AppFile) =
                    f.Name.EndsWith (".runtimeconfig.json", System.StringComparison.OrdinalIgnoreCase)
                    || f.Name.EndsWith (".deps.json", System.StringComparison.OrdinalIgnoreCase)
                let asm, side = outputsOf dir e isRuntimeFile true
                add false asm (Path.GetFileName asm)
                let stem = Path.GetFileNameWithoutExtension asm
                for ext in related do
                    let p = normal (Path.Combine (dir, stem + ext))
                    if File.Exists p then add false p (stem + ext)
                // pdb and xml come by the root's related extensions above
                for f, p in side do
                    if not (f.EndsWith ".pdb" || f.EndsWith ".xml") then add false p f
                items false e.Framework (e.Recorded |> Option.bind (fun x -> x.App))
            for w in Seq.distinct notTaken do do! trace Warning "%s" w
            // the package files the folder lacks, fetched (the versions the locks resolved)
            let missing = candidates |> Seq.map (fun c -> c.Source) |> Seq.filter (fun s -> hashes.ContainsKey s && not (File.Exists s)) |> Seq.distinct |> List.ofSeq
            if not (List.isEmpty missing) then
                let! problems = Restore.ensure Restore.Options.Default { Packages = entries |> Seq.collect (fun e -> e.Packages) |> List.ofSeq; Paths = missing }
                if not (List.isEmpty problems) then failwithf "%s: fetching the copy-local packages failed:\n%s" what (String.concat "\n" problems)
            let files, conflicts = RunFolder.plan RunFolder.assemblyVersion RunFolder.sameContent (List.ofSeq candidates)
            let absent = files |> List.filter (fun (_, s) -> not (File.Exists s)) |> List.map snd
            if not (List.isEmpty absent) then
                failwithf "%s: missing %s; a copy item deleted or renamed since the import (a wildcard's files are the import's expansion): run the import again"
                    what (absent |> List.map (sprintf "'%s'") |> String.concat ", ")
            // the inputs no rule builds: package files and items (the outputs beside the
            // assemblies change with them)
            do! needFiles (Filelist [ for _, s in files do if itemSources.Contains s then yield File.make s ])
            let check (src: string) =
                match hashes.TryGetValue src with
                | true, sha when sha <> "" && FileHash.sha256 src <> sha ->
                    failwithf "%s: '%s' does not have the hash its lock records; run the import again" what src
                | _ -> ()
            let header = [ sprintf "# %s: lock %s; mode<TAB>path<TAB>source" what (normal rootLock) ]
            let summary = RunFolder.apply mode manifest header check files
            // the conflicts report beside the manifest, written when its text changes
            let report = Path.Combine (Path.GetDirectoryName manifest, RunFolder.ConflictsReport)
            let lines =
                [ for c in conflicts -> sprintf "%s\tkept %s\tdropped %s\t(%s)" c.Path c.Kept c.Dropped c.Reason ]
                @ [ for p in summary.Foreign -> sprintf "%s\tkept the file the folder had\tdropped the planned source\t(not placed by a run, or changed since: never overwritten)" p ]
                @ [ for p in summary.Modified -> sprintf "%s\tkept the file the folder had\tno longer planned\t(changed since a run placed it: never removed)" p ]
            if List.isEmpty lines then
                if File.Exists report then File.Delete report
            else
                let text = String.concat "\n" (sprintf "# %s: path<TAB>kept<TAB>dropped<TAB>(why)" what :: lines) + "\n"
                if not (File.Exists report) || File.ReadAllText report <> text then File.WriteAllText (report, text)
                do! trace Warning "%s: %d conflicts (%s)" what lines.Length report
            let modes = summary.Modes |> List.map (fun (m, n) -> sprintf "%d %s" n m) |> String.concat ", "
            do! trace Message "%s: %d files (%s), %d placed, %d unchanged, %d removed" what summary.Files modes summary.Placed summary.Kept summary.Removed
        }

    /// `runFolderWith` without extra roots, by clones where the file system has them (APFS,
    /// btrfs/XFS reflink; on Windows the copy clones on ReFS and Dev Drive by itself), else
    /// copies. Hard links (`RunFolder.Link`) are the explicit opt-in: no copy at all, but on
    /// Windows a process running from the folder locks the compile's output.
    let runFolder (lockOf: string -> string -> string) (outOf: string -> string -> string -> string) (cfg: string) (fwk: string) (name: string) : Recipe<ExecContext, unit> =
        runFolderWith [] RunFolder.Clone lockOf outOf cfg fwk name

    /// <summary>
    /// The run folder manifests (`runOf cfg fwk name` + `/.xake-run.txt`) of every project of
    /// `descriptions` (a fileset of `xake.project.json`) whose lock entry runs (`runs`: an
    /// application or a test project), in each of its frameworks -- what a script's `deploy`
    /// needs.
    /// </summary>
    let runFoldersWith (extraRoots: (string * string) list) (descriptions: Fileset) (cfg: string) (lockOf: string -> string -> string)
                       (runOf: string -> string -> string -> string) : Recipe<ExecContext, string list> =
        recipe {
            let! options = getCtxOptions ()
            let! (Filelist files) = getFiles descriptions
            let! loaded = files |> List.map (fun f -> Description.load f.FullName) |> sequence
            let withLock =
                loaded |> List.filter (fun d ->
                    let p = lockOf d.Name cfg
                    File.Exists (if Path.IsPathRooted p then p else Path.Combine (options.ProjectRoot, p)))
            let! docs = withLock |> List.map (fun d -> Lock.loadWith extraRoots (lockOf d.Name cfg)) |> sequence
            return
                [ for d, doc in List.zip withLock docs do
                    for e in doc.Entries do
                        if runs e then yield normal (Path.Combine (runOf cfg e.Framework d.Name, RunFolder.Manifest)) ]
        }
