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
    let generate (lockOf: string -> string -> string) (outOf: string -> string -> string -> string)
                 (config: string) (descriptionPath: string) : Recipe<ExecContext, unit> =
        recipe {
            let descPath = descriptionPath
            let! d = Description.load descriptionPath
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
                                    failwithf "'%s': projectRef '%s' has no lock for %s (nor Release, Debug): '%s'; generate the lock of '%s' first (the lock rule needs the referenced projects' lock rules, e.g. via `update-locks`)"
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
                    let own =
                        d.Packages
                        @ (d.ByFramework |> List.filter (fst >> same fwk) |> List.collect (fun (_, i) -> i.Packages))
                    let flowing =
                        refs |> List.collect (fun (_, _, ps, _) -> ps)
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

                    // the direct references' outputs, then theirs (the closure), each once, in order
                    let outputs =
                        (refs |> List.map (fun (o, _, _, _) -> o)) @ (refs |> List.collect (fun (_, _, _, t) -> t))
                        |> List.distinct
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
                            Evaluation = { Lock.Evaluation.Empty with ProjectRefs = refs |> List.map (fun (_, p, _, _) -> p) }
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
    /// output, not a target, so a lock without doc expects none;</item>
    /// <item>depends on the unhashed outputs of project references before compiling, so a
    /// referenced project's `.dll` is built first by its own rule and a change to it rebuilds
    /// this one; the hashed references (packages) are fetched by the restore below when
    /// missing, then needed by the runner;</item>
    /// <item>C# source masks are expanded through `getFiles` (`Described.expandCsc`), so an
    /// added or removed `.cs` rebuilds;</item>
    /// <item>a `.resx` resource is compiled into the `.resources` the lock names (under
    /// `obj/xake/`) when that is missing or older than the resx; the resx is an input;</item>
    /// <item>a `$(Version)` token in `Generated` is replaced by the version of the first
    /// `versions` mask matching the project (its folder, where its first source lies, as
    /// `&lt;folder&gt;/xake.project.json` relative to the project root); the variable is read
    /// (a dependency) only when the token is there;</item>
    /// <item>then `Lock.compileFrom`: prerequisites, the `HERMETIC=on` gate (message 10),
    /// restore of what the lock names, the compiler (csc's server per `CSC_SERVER`).</item>
    /// </list>
    /// </summary>
    let compileAs (lockPath: string) (fwk: string) (versions: (string * RequiredVar<string>) list) : Recipe<ExecContext, unit> =
        recipe {
            let! options = getCtxOptions ()
            let root = normal (Path.GetFullPath options.ProjectRoot)
            let! target = getTargetFile ()
            let target = normal target.FullName
            let! doc = Lock.load lockPath
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
                failwithf "'%s': the target '%s' is not the assembly of the lock '%s' (%s.dll)" entry.Name target lockPath entry.Name

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

            // a `.resx` edited since its `.resources` was compiled: compiled again by the runner
            Described.refreshResources c.Resources
            let! server = CompilerServer.resolve None
            let lockOptions = { Lock.Options.Default with Run = { Lock.Options.Default.Run with Server = server } }
            do! Lock.compileFrom lockPath lockOptions { entry with Compilation = outputAt target c }
        }

    /// <summary>
    /// Needs the outputs of every described project of `descriptions` (a fileset of
    /// `xake.project.json` files) in configuration `cfg`: for each framework of a
    /// description, `outOf cfg fwk name` + `/&lt;assembly&gt;.dll` -- the targets the script's
    /// compile rule (`compileAs`) builds, all at once so independent ones run in parallel. A
    /// described project without a lock (`lockOf name cfg`) fails, naming the `update-locks` rule.
    /// The script's `build` command:
    /// <code>
    /// command "build" { do! Locks.needAll (!! "src/*/xake.project.json") "Release" lockOf outOf }
    /// </code>
    /// </summary>
    let needAll (descriptions: Fileset) (cfg: string)
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
            let targets =
                [ for d in loaded do
                    let asm = d.AssemblyName |> Option.defaultValue d.Name
                    for fwk in d.Frameworks -> normal (Path.Combine (outOf cfg fwk d.Name, asm + ".dll")) ]
            do! need targets
        }
