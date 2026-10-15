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

            // project-reference outputs first (unhashed in the lock): another rule's targets. The
            // hashed ones are packages, which may not be fetched yet: the runner needs them after
            // `compileFrom`'s restore
            do! needFiles (Filelist (entry.Dependencies.References |> List.filter (fun r -> r.Sha256 = "") |> List.map (fun r -> File.make r.Path)))

            let c = entry.Compilation
            let! c =
                match c with
                | Lock.Compilation.Csc masked -> recipe {
                    let! expanded = Described.expandCsc masked
                    return Lock.Compilation.Csc expanded }
                | Lock.Compilation.Fsc _ -> recipe { return c }

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

            // a `.resx` edited since its `.resources` was compiled (Xake 3.7's runner compiles a missing one only)
            Described.refreshResources c.Resources
            let! server = CompilerServer.resolve None
            let lockOptions = { Lock.Options.Default with Run = { Lock.Options.Default.Run with Server = server } }
            do! Lock.compileFromWith extraRoots lockPath lockOptions { entry with Compilation = outputAt target c }
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
