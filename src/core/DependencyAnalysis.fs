module internal Xake.DependencyAnalysis

open Xake
open Xake.WorkerPool
open Storage

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
    (fun ch -> Storage.GetResult(target, ch)) |> ctx.Db.PostAndReply
    |> Option.fold (fun _ r -> r.Steps |> List.sumBy (fun s -> s.OwnTime)) 0<ms>

/// Content hashes (docs/content-cutoff.md).
module Content =
    /// The file's stat and the first 8 bytes of the SHA-256 of its content, taken together: stat,
    /// hash, stat. `Unreadable` when the stat moved meanwhile, or the file cannot be read.
    let hash (file: File) =
        try
            let before = File.getFileInfo file
            if not before.Exists then Unreadable
            else
                let bytes =
                    use sha = System.Security.Cryptography.SHA256.Create()
                    use stream = new System.IO.FileStream(before.FullName, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                                                          System.IO.FileShare.ReadWrite ||| System.IO.FileShare.Delete)
                    sha.ComputeHash stream
                let after = File.getFileInfo file
                if after.Exists && after.LastWriteTime = before.LastWriteTime && after.Length = before.Length
                then Hashed (before.LastWriteTime, before.Length, System.BitConverter.ToUInt64(bytes, 0))
                else Unreadable
        with _ -> Unreadable

/// Observes a file through the database (decisions 1c, A1): missing, or its state and whether its
/// content changed after the run `since`. A file the agent asks to hash (`HashNeeded`) is hashed
/// here, off the agent, and settled.
let observeSince (ctx: ExecContext) (mode: ObserveMode) (since: int) (file: File) = async {
    let! o = ctx.Db.PostAndAsyncReply(fun ch -> Observe(file, mode, since, ch))
    match o with
    | HashNeeded request ->
        let watch = System.Diagnostics.Stopwatch.StartNew()
        let hashed = Content.hash request.File
        match ctx.Counters with null -> () | c -> c.FileHashed watch.Elapsed
        return! ctx.Db.PostAndAsyncReply(fun ch -> Settle(request, hashed, ch))
    | o -> return o }

/// Observes a file where only the record matters, not the reply.
let observe (ctx: ExecContext) (mode: ObserveMode) (file: File) =
    observeSince ctx mode System.Int32.MaxValue file |> Async.Ignore

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
         | Absent | HashNeeded _ -> ChangeReason.FilesChanged [show file], Some "file does not exist")

    | TargetDep (FileTarget file as t) ->
        (match observe epoch file with
         | Changed st ->
            ChangeReason.Depends t, Some (sprintf "dependee changed in run %d, after run %d" st.Changed epoch)
         | Unchanged _ -> transitive t
         | Absent | HashNeeded _ -> ChangeReason.DependsMissingTarget t, None)

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
let getChangeReasons (ctx: ExecContext) (current: Target -> (Target list * Set<StaticKey>) option) getTargetDeps target =

    // separates change reason into two lists and collabses FilesChanged all into one
    let collapseFilesChanged reasons =
        let files, other = reasons |> List.partition (fst >> function | ChangeReason.FilesChanged _ -> true | _ -> false)
        let filesChangedDbg = files |> List.collect (snd >> Option.toList)
        let filesChanged = files |> List.collect (fst >> fun (FilesChanged files | OtherwiseFail files) -> files) |> function | [] -> [] | ls -> [FilesChanged ls, Some (sprintf "%A" filesChangedDbg)]
        in
        filesChanged @ other |> List.rev


    let lastBuild = (fun ch -> GetResult(target, ch)) |> ctx.Db.PostAndReply

    match lastBuild with
    | Some {BuildResult.Depends = []} ->
        [ChangeReason.Other "No dependencies", Some "It means target is not \"pure\" and depends on something beyond our control (oracle)"]

    | Some ({BuildResult.Depends = depends; Targets = result; Epoch = epoch} as built) ->
        let getResult t = (fun ch -> GetResult(t, ch)) |> ctx.Db.PostAndReply
        // with cut-off off every dependee goes through the transitive rule, as in phase 1 (4.0)
        let taskState t = if ctx.Options.ContentCutoff then Scheduler.query ctx.Engine.Scheduler t else NotStarted
        let depState =
            getDepState (Util.getVar ctx.Vars) (toFileList ctx.Options.ProjectRoot) getTargetDeps taskState
                        (fun since f -> observeSince ctx Verdict since f |> Async.RunSynchronously) getResult (File.display ctx.Options.ProjectRoot) epoch

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
        [ChangeReason.Other "Not built yet", Some "Target was not built before or build results were cleaned so we don't know dependencies."]
    |> List.map fst

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

/// Dumps all dependencies for particular target
let dumpDeps (ctx: ExecContext) current (target: Target list) =

    let getDeps = getChangeReasons ctx current |> memoizeRec

    let doneTargets = System.Collections.Hashtable()
    let indent i = String.replicate i "  "
    // the dump goes to stdout, past the loggers: mask the values of secret variables here
    let printfn fmt = Printf.kprintf (SecretMask.apply >> System.Console.WriteLine) fmt

    let show = File.display ctx.Options.ProjectRoot
    let state (f: File) =
        match observeSince ctx Verdict System.Int32.MaxValue f |> Async.RunSynchronously with
        | Unchanged st | Changed st -> sprintf "stamp %A, size %d, changed in run %d" st.Stamp st.Size st.Changed
        | Absent | HashNeeded _ -> "NOT EXISTS"

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

            let lastResult = (fun ch -> GetResult(target, ch)) |> ctx.Db.PostAndReply
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