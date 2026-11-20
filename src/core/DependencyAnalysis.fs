module internal Xake.DependencyAnalysis

open Xake
open Xake.WorkerPool
open Storage
open FileTracker

/// <summary>
/// Dependency state.
/// </summary>
type ChangeReason =
    | NotChanged
    | Depends of Target
    | DependsMissingTarget of Target
    | FilesChanged of string list
    | Other of string


/// <summary>
/// Gets target execution time in the last run
/// </summary>
/// <param name="ctx"></param>
/// <param name="target"></param>
let getExecTime (ctx: ExecContext) target =
    Storage.tryGetResult ctx.Db target
    |> Option.fold (fun _ r -> r.Steps |> List.sumBy (fun s -> s.OwnTime)) 0<ms>

/// Observes a group of files (FileTracker.observeFiles) in the context's run: per file, in the
/// order given, missing or whether its content changed after the run `since`.
let observeMany (ctx: ExecContext) (mode: ObserveMode) (since: int) (files: File list) =
    if files.IsEmpty then async.Return [||] else FileTracker.observeFiles ctx.Observe mode since files

/// Observes one file (docs/features/idle-perf, L5).
let observeSince (ctx: ExecContext) (mode: ObserveMode) (since: int) (file: File) = async {
    let! o = observeMany ctx mode since [ file ]
    return o.[0] }

/// Observes files where only the record matters, not the reply.
let observe (ctx: ExecContext) (mode: ObserveMode) (files: File list) =
    observeMany ctx mode System.Int32.MaxValue files |> Async.Ignore

/// Gets single dependency state and reason of a change, for a dependent built in run `epoch`
/// (decision 1c): a file or target dependency changed when it was seen to change after that run.
/// A dependee target not changed after that run (content cut-off): finished in this run, it is
/// compared exactly; running, it is a change (only a task's own verdict waits for it, before
/// this); not started, it is a change when it is itself out of date (the 4.0 transitive rule).
let rec getDepState getVar getFileList (getChangedDeps: Target -> ChangeReason list)
                    (taskState: Target -> TaskState<ExecStatus>)
                    (observe: int -> File -> Observation) (getResult: Target -> BuildResult option)
                    (show: File -> string) (epoch: int) x =
    let recurse = getDepState getVar getFileList getChangedDeps taskState observe getResult show epoch
    let transitive dependeeTarget =
        match taskState dependeeTarget with
        | Finished -> NotChanged, None
        | Running _ -> ChangeReason.Depends dependeeTarget, Some "the dependee is being built"
        | NotStarted ->
            dependeeTarget |> getChangedDeps |> List.filter ((<>) ChangeReason.NotChanged)
            |> function
                |  [] -> NotChanged, None
                |item::_ ->
                    ChangeReason.Depends dependeeTarget, Some (sprintf "E.g. %A..." item)
    match x with
    | FileDep file ->
        (match observe epoch file with
         | Changed st ->
            ChangeReason.FilesChanged [show file], Some (sprintf "changed in run %d, after run %d (stamp %A, size %d)" st.Changed epoch st.Stamp st.Size)
         | Unchanged _ -> NotChanged, None
         | Absent -> ChangeReason.FilesChanged [show file], Some "file does not exist")

    | TargetDep (FileTarget file as t) ->
        (match observe epoch file with
         | Changed st ->
            ChangeReason.Depends t, Some (sprintf "dependee changed in run %d, after run %d" st.Changed epoch)
         | Unchanged _ -> transitive t
         | Absent -> ChangeReason.DependsMissingTarget t, None)

    | TargetDep (PhonyAction _ as t) ->
        (match getResult t with
         | None -> ChangeReason.Depends t, Some "the dependee has no result"
         | Some r when r.Epoch > epoch ->
            ChangeReason.Depends t, Some (sprintf "dependee built in run %d, after run %d" r.Epoch epoch)
         | Some _ -> transitive t)

    // a clear-value record of a name declared secret is judged as its digest, never printed
    | EnvVar (name, value) when Secrets.isSecret name ->
        recurse (Secrets.scrub x)

    | Var (name, value) when Secrets.isSecret name ->
        recurse (Secrets.scrub x)

    | SecretDep (name, source, digest) ->
        let current =
            match source with
            | SecretEnv -> Util.getEnvVar name
            | SecretArg -> getVar name
        current |> Option.iter SecretMask.register
        match Secrets.changeReason name digest current with
        | Some reason -> ChangeReason.Other reason, None
        | None -> NotChanged, None

    | EnvVar (name,value) when value <> Util.getEnvVar name ->
        ChangeReason.Other <| sprintf "Environment variable %s was changed from '%A' to '%A'" name value (Util.getEnvVar name), None

    | Var (name,value) when value <> getVar name ->
        ChangeReason.Other <| sprintf "Global script variable %s was changed '%A'->'%A'" name value (getVar name), None

    | AlwaysRerun ->
        ChangeReason.Other <| "AlwaysRerun rule", Some "Rule indicating target has to be run regardless dependencies state"

    | GetFiles (fileset,files) ->
        let newfiles = getFileList fileset
        let diff = compareFileList files newfiles

        if List.isEmpty diff then
            NotChanged, None
        else
            Other <| sprintf "File list is changed for fileset %A" fileset, Some (sprintf "The diff list is %A" diff)

    | EnvVar _ | Var _ -> NotChanged, None


/// What a static set is compared by (M9): a file or target by name, a fileset by itself.
type StaticKey = StaticName of string | StaticFiles of Fileset

/// The static set of header dependencies, as keys.
let staticKeys (deps: Dependency list) =
    deps |> List.choose (function
        | FileDep f -> Some (StaticName f.Name)
        | TargetDep t -> Some (StaticName t.Name)
        | GetFiles (fs, _) -> Some (StaticFiles fs)
        | _ -> None) |> Set.ofList

/// <summary>
/// Gets the list of reasons to rebuilt the target. Empty list means target is not changed.
/// </summary>
/// <param name="ctx"></param>
/// <param name="getTargetDeps">gets state for nested dependency</param>
/// <param name="target">The target to analyze</param>
/// `current` gives the target's rule now: its outputs and static set (None without a rule).
let getChangeReasons (ctx: ExecContext) (current: Target -> (Target list * Set<StaticKey>) option)
                     (getTargetDeps: Target -> Async<ChangeReason list>) target : Async<ChangeReason list> = async {

    // separates change reason into two lists and collabses FilesChanged all into one
    let collapseFilesChanged reasons =
        let files, other = reasons |> List.partition (fst >> function | ChangeReason.FilesChanged _ -> true | _ -> false)
        let filesChangedDbg = files |> List.collect (snd >> Option.toList)
        let filesChanged = files |> List.collect (fst >> fun (FilesChanged files | OtherwiseFail files) -> files) |> function | [] -> [] | ls -> [FilesChanged ls, Some (sprintf "%A" filesChangedDbg)]
        in
        filesChanged @ other |> List.rev


    let lastBuild = Storage.tryGetResult ctx.Db target

    let! reasons = async {
      match lastBuild with
      | Some {BuildResult.Depends = []} ->
        return [ChangeReason.Other "No dependencies", Some "It means target is not \"pure\" and depends on something beyond our control (oracle)"]

      | Some ({BuildResult.Depends = depends; Targets = result; Epoch = epoch} as built) ->
        let getResult t = Storage.tryGetResult ctx.Db t
        // with cut-off off every dependee goes through the transitive rule, as in phase 1 (4.0);
        // each dependee's state is taken once, so both phases below see the same one
        let states = System.Collections.Generic.Dictionary<Target, TaskState<ExecStatus>>()
        let taskState t =
            match states.TryGetValue t with
            | true, st -> st
            | _ ->
                let st = if ctx.Options.ContentCutoff then Scheduler.query ctx.Engine.Scheduler t else NotStarted
                states.[t] <- st
                st
        // the first phase gathers what the rule needs: the target's files in one observation
        // (docs/features/idle-perf, L5), then the dependees' own verdicts that the transitive rule
        // asks for, in parallel
        let files = depends |> List.choose (function FileDep f | TargetDep (FileTarget f) -> Some f | _ -> None) |> List.distinct
        let! replies = observeMany ctx Verdict epoch files
        let observed = Seq.zip files replies |> dict
        let transitive =
            depends |> List.choose (function
                | TargetDep (FileTarget f as t) when (match observed.[f] with Unchanged _ -> true | _ -> false) && (match taskState t with NotStarted -> true | _ -> false) -> Some t
                | TargetDep (PhonyAction _ as t) when (match getResult t with Some r -> r.Epoch <= epoch | None -> false) && (match taskState t with NotStarted -> true | _ -> false) -> Some t
                | _ -> None)
            |> List.distinct
        let! dependees = transitive |> List.map (fun t -> async { let! r = getTargetDeps t in return t, r }) |> Async.Parallel
        let byDependee = dict dependees
        // the second phase is the rule itself, pure over what was gathered
        let depState =
            getDepState (Util.getVar ctx.Vars) (toFileList ctx.Options.ProjectRoot) (fun t -> byDependee.[t]) taskState
                        (fun _ f -> observed.[f]) getResult (File.display ctx.Options.ProjectRoot) epoch

        // the rule now (M9): a current output missing (an `also` added since included), or a static
        // set other than the recorded one
        let ruleReasons =
            match current target with
            | None -> []
            | Some (outputs, keys) ->
                [ match outputs |> List.tryFind (function FileTarget f -> not (File.exists f) | _ -> false) with
                  | Some t -> yield ChangeReason.Other (sprintf "output %s is missing" (t.Display ctx.Options.ProjectRoot)), None
                  | None -> ()
                  if keys <> staticKeys (List.truncate built.StaticCount depends) then
                      yield ChangeReason.Other "its static needs changed", None ]
        return
            depends
            |> List.map depState
            |> List.filter (fst >> (<>) ChangeReason.NotChanged)
            |> collapseFilesChanged
            |> List.append ruleReasons
            |> function
            | [] ->
                match result with
                | targetList when targetList |> List.exists (function | FileTarget file when not (File.exists file) -> true | _ -> false) ->
                    [ChangeReason.Other "target file does not exist", Some "The file has to be rebuilt regardless all its dependencies were not changed"]
                | _ -> []
            | ls -> ls

      | _ ->
        return [ChangeReason.Other "Not built yet", Some "Target was not built before or build results were cleaned so we don't know dependencies."] }
    return reasons |> List.map fst }

// gets task duration and list of targets it depends on. No clue why one method does both.
let getDurationDeps ctx getDeps t =
    match getDeps t with
    | [] -> 0<ms>, []
    | deps ->
        let targets = deps |> List.collect (function |Depends t |DependsMissingTarget t -> [t] | _ -> [])
        (getExecTime ctx t, targets)
//    |> fun (tt,dd) ->
//        printfn "For task %A duration:%A deps:%A" t tt dd
//        (tt,dd)

/// The verdicts of one run (docs/features/idle-perf, L6): each target's no-build verdict is
/// decided once, and everyone who asks awaits the same task. A verdict that waits for a
/// dependee's verdict still in progress registers the edge; an edge that would close a cycle is
/// refused and the dependee counts as changed (two entries into one cycle cannot wait for each
/// other).
[<Sealed>]
type Verdicts(ctx: ExecContext, current: Target -> (Target list * Set<StaticKey>) option) =
    let memo = System.Collections.Concurrent.ConcurrentDictionary<Target, Lazy<System.Threading.Tasks.Task<ChangeReason list>>>()
    let waits = System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>()
    let reaches (src: string) (dst: string) =
        let seen = System.Collections.Generic.HashSet<string>()
        let rec go k = k = dst || (seen.Add k && (match waits.TryGetValue k with | true, es -> es |> Seq.exists go | _ -> false))
        go src
    let rec taskOf (t: Target) =
        memo.GetOrAdd(t, fun t -> lazy (Async.StartAsTask (getChangeReasons ctx current (dependee t) t))).Value
    and dependee (waiter: Target) (d: Target) = async {
        let task = taskOf d
        if task.IsCompleted then return! Async.AwaitTask task
        else
            let edge =
                lock waits (fun () ->
                    if reaches d.Name waiter.Name then false
                    else
                        match waits.TryGetValue waiter.Name with
                        | true, es -> es.Add d.Name |> ignore
                        | _ -> waits.[waiter.Name] <- System.Collections.Generic.HashSet [ d.Name ]
                        true)
            if not edge then return [ ChangeReason.Depends d ]
            else
                try return! Async.AwaitTask task
                finally lock waits (fun () -> match waits.TryGetValue waiter.Name with | true, es -> es.Remove d.Name |> ignore | _ -> ()) }
    /// The run's no-build verdict of a target (memoised).
    member _.Of (target: Target) = Async.AwaitTask (taskOf target)
    /// The verdict decided afresh (a task's own verdict, after waiting for its running dependees),
    /// its dependees through the memo.
    member _.Fresh (target: Target) = getChangeReasons ctx current (dependee target) target
    /// The verdict when it is decided already.
    member _.Ready (target: Target) =
        match memo.TryGetValue target with
        | true, lz when lz.IsValueCreated && lz.Value.Status = System.Threading.Tasks.TaskStatus.RanToCompletion -> Some lz.Value.Result
        | _ -> None
    /// Decides, in parallel, the verdicts of the targets the estimate visits from `goals`: each
    /// target, then the dependees named by its reasons (as `getDurationDeps`).
    member this.Warm (goals: Target list) = async {
        let visited = System.Collections.Concurrent.ConcurrentDictionary<Target, byte>()
        let rec visit t = async {
            if visited.TryAdd(t, 0uy) then
                let! reasons = this.Of t
                let dependees = reasons |> List.collect (function Depends d | DependsMissingTarget d -> [d] | _ -> [])
                do! dependees |> List.map visit |> Async.Parallel |> Async.Ignore }
        do! goals |> List.map visit |> Async.Parallel |> Async.Ignore }
    /// The estimate's duration and dependees of a target, from the verdicts `Warm` decided (a
    /// target not warmed counts as up to date).
    member this.Estimate (target: Target) =
        getDurationDeps ctx (fun t -> this.Ready t |> Option.defaultValue []) target

/// The critical-path key (ms) of every target the run's estimate reaches from `goals`: its own
/// time plus the largest key of the targets that depend on it in this run (docs/implnotes.md).
let criticalPath (getDurationDeps: Target -> int<ms> * Target list) (goals: Target list) =
    let own = System.Collections.Generic.Dictionary<string, int>()
    let dependents = System.Collections.Generic.Dictionary<string, ResizeArray<string>>()
    let rec visit (t: Target) =
        if not (own.ContainsKey t.Name) then
            let duration, dependees = getDurationDeps t
            own.[t.Name] <- int duration
            for d in dependees do
                match dependents.TryGetValue d.Name with
                | true, ds -> ds.Add t.Name
                | _ -> dependents.[d.Name] <- ResizeArray [t.Name]
                visit d
    goals |> List.iter visit
    let keys = System.Collections.Generic.Dictionary<string, int>()
    // a cycle is impossible in a finished run's records; an edge closing one counts 0
    let rec key visiting name =
        match keys.TryGetValue name with
        | true, k -> k
        | _ when Set.contains name visiting -> 0
        | _ ->
            let up =
                match dependents.TryGetValue name with
                | true, ds -> ds |> Seq.fold (fun m d -> max m (key (Set.add name visiting) d)) 0
                | _ -> 0
            let k = own.[name] + up
            keys.[name] <- k
            k
    own.Keys |> List.ofSeq |> List.map (fun n -> n, key Set.empty n)

/// Dumps all dependencies for particular target
let dumpDeps (ctx: ExecContext) current (target: Target list) =

    let doneTargets = System.Collections.Hashtable()
    let indent i = String.replicate i "  "
    // the dump goes to stdout, past the loggers: mask the values of secret variables here
    let printfn fmt = Printf.kprintf (SecretMask.apply >> System.Console.WriteLine) fmt

    let show = File.display ctx.Options.ProjectRoot
    let state (f: File) =
        match observeSince ctx Verdict System.Int32.MaxValue f |> Async.RunSynchronously with
        | Unchanged st | Changed st -> sprintf "stamp %A, size %d, changed in run %d" st.Stamp st.Size st.Changed
        | Absent -> "NOT EXISTS"

    let rec displayNestedDeps ii =
        function
        | TargetDep dependeeTarget ->
            showTargetStatus (ii+1) dependeeTarget
        | _ -> ()
    and showDepStatus ii (d: Dependency) =
        match d with
        | AlwaysRerun ->
            printfn "%sAlways Rerun" (indent ii)
        | FileDep file ->
            printfn "%sFile '%s' %s" (indent ii) (show file) (state file)
        | TargetDep (FileTarget file) ->
            printfn "%sTarget file '%s' %s" (indent ii) (show file) (state file)
        | TargetDep (PhonyAction name) ->
            printfn "%sTarget '%s'" (indent ii) name

        | EnvVar (name, _) | Var (name, _) when Secrets.isSecret name ->
            showDepStatus ii (Secrets.scrub d)

        | SecretDep (name, source, digest) ->
            let current = match source with SecretEnv -> Util.getEnvVar name | SecretArg -> Util.getVar ctx.Vars name
            let state =
                match digest with
                | SecretAbsent -> "unset"
                | SecretPresent -> "set"
                | SecretHmac _ -> "set (hashed)"
            let changed = Secrets.changeReason name digest current |> Option.map (sprintf " CHANGED: %s") |> Option.defaultValue ""
            let kind = match source with SecretEnv -> "ENV Var" | SecretArg -> "Script var"
            printfn "%sSecret %s: '%s' = %s%s" (indent ii) kind name state changed

        | EnvVar (name,value) ->
            let newValue = Util.getEnvVar name
            let changed = if value <> newValue then sprintf "CHANGED %A => %A" value newValue else ""
            printfn "%sENV Var: '%s' = %A %s" (indent ii) name value changed

        | Var (name,value) ->
            printfn "%sScript var: '%s' = %A" (indent ii) name value

        | GetFiles (fileset, _) ->
            printfn "%sGetFiles: %A" (indent ii) fileset
    and showTargetStatus ii (target: Target) =
        if not <| doneTargets.ContainsKey(target) then
            doneTargets.Add(target, 1)

            let lastResult = Storage.tryGetResult ctx.Db target
            let built = match lastResult with Some r -> sprintf " (built in run %d)" r.Epoch | None -> ""
            printfn "%sTarget %A%s" (indent ii) (target.Display ctx.Options.ProjectRoot) built

            match lastResult with
            | Some {BuildResult.Depends = []} ->
                printfn "%sno dependencies" (indent ii)
            | Some {BuildResult.Depends = deps} ->
                deps |> List.iter (showDepStatus (ii+1))
                deps |> List.iter (displayNestedDeps (ii+1))
            | None ->
                printfn "%sno built yet (no stats)" (indent ii)

    target |> List.iter (showTargetStatus 0)