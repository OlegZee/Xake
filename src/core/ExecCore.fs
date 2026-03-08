module internal Xake.ExecCore

open System.Text.RegularExpressions
open DependencyAnalysis

open Storage
open WorkerPool

/// <summary>
/// Gets action context.
/// </summary>
let getCtx() = Recipe (fun (r,c) -> async {return (r,c)})

/// Writes the message with formatting to a log
let traceLog (level:Logging.Level) fmt =
    let write s = recipe {
        let! ctx = getCtx()
        return ctx.Logger.Log level "%s" s
    }
    Printf.kprintf write fmt

let wildcardsRegex = Regex(@"\*\*|\*|\?", RegexOptions.Compiled)
let patternTagRegex = Regex(@"\((?'tag'\w+?)\:[^)]+\)", RegexOptions.Compiled)
let replace (regex:Regex) (evaluator: Match -> string) text = regex.Replace(text, evaluator)
let ifNone x = function |Some x -> x | _ -> x

/// Converts CLI ExecOptions into engine-level EngineOptions, constructing the logger.
let toEngineOptions (opts: ExecOptions) (rules: Rules<ExecContext>) : EngineOptions =
    let logger = CombineLogger (ConsoleLogger opts.ConLogLevel) opts.CustomLogger
    let logger =
        match opts.FileLog, opts.FileLogLevel with
        | null,_ | "",_
        | _, Silent -> logger
        | logFileName,level -> CombineLogger logger (FileLogger logFileName level)
    { ProjectRoot = opts.ProjectRoot; Threads = opts.Threads
      Logger = logger; Teardown = opts.Teardown; Rules = rules }

let (|Dump|Dryrun|Run|) (opts:ExecOptions) =
    match opts with
    | _ when opts.DumpDeps -> Dump
    | _ when opts.DryRun -> Dryrun
    | _ -> Run

let applyWildcards = function
    | None -> id
    | Some matches ->
        fun pat ->
            let mutable i = 0
            let evaluator m =
                i <- i + 1
                matches |> Map.tryFind (i.ToString()) |> ifNone ""
            let evaluatorTag (m: Match) =
                matches |> (Map.tryFind m.Groups.["tag"].Value) |> ifNone ""
            pat
            |> replace wildcardsRegex evaluator
            |> replace patternTagRegex evaluatorTag

// locates the rule
let locateRule (Rules rules) projectRoot target =
    let matchRule rule =
        match rule, target with

        |FileConditionRule (meetCondition,_), FileTarget file when file |> File.getFullName |> meetCondition ->
            //writeLog Level.Debug "Found conditional pattern '%s'" name
            // TODO let condition rule extracting named groups
            Some (rule,[],[target])

        |FileRule (pattern,_), FileTarget file ->
            file
            |> File.getFullName
            |> Path.matchGroups pattern projectRoot
            |> Option.map (fun groups -> rule,groups,[target])

        |MultiFileRule (patterns, _), FileTarget file ->
            let fname = file |> File.getFullName
            patterns
            |> List.tryPick(fun pattern ->
                Path.matchGroups pattern projectRoot fname
                |> Option.map(fun groups -> groups, pattern)
                )
            |> Option.map (fun (groups, _) ->
                let generateName = applyWildcards (Map.ofList groups |> Some)

                let targets = patterns |> List.map (generateName >> (</>) projectRoot >> File.make >> FileTarget)
                rule, groups, targets)

        |PhonyRule (pattern,_), PhonyAction phony ->
            // printfn $"Phony rule {phony}, pattern {pattern}"
            // Some (rule, [], [target])
            phony
            |> Path.matchGroups pattern ""
            |> Option.map (fun groups -> rule,groups,[target])

        | _ -> None

    rules |> List.tryPick matchRule

// Ordinal of the task being added to a task pool
let refTaskOrdinal = ref 0

/// <summary>
/// Creates a context for a new task
/// </summary>
let newTaskContext targets matches ctx =
    let ordinal = System.Threading.Interlocked.Increment(refTaskOrdinal)
    let prefix = ordinal |> sprintf "%i> "
    in
    {ctx with
        Ordinal = ordinal; Logger = PrefixLogger prefix ctx.Engine.RootLogger
        Targets = targets
        RuleMatches = matches
    }

// executes single artifact
let rec execOne (ctx: ExecContext) target =

    let run ruleMatches action targets =
        let primaryTarget = targets |> List.head
        async {
            match ctx.NeedRebuild targets with
            | true ->
                let taskContext = newTaskContext targets ruleMatches ctx
                do ctx.Logger.Log Command "Started %s as task %i" primaryTarget.ShortName taskContext.Ordinal

                do Progress.TaskStart primaryTarget |> ctx.Progress.Post

                let startResult = {BuildLog.makeResult targets with Steps = [Step.start "all"]}
                let! (result,_) = action (startResult, taskContext)
                let result = Step.updateTotalDuration result

                Store result |> ctx.Db.Post

                do Progress.TaskComplete primaryTarget |> ctx.Progress.Post
                do ctx.Logger.Log Command "Completed %s in %A ms (wait %A ms)" primaryTarget.ShortName (Step.lastStep result).OwnTime  (Step.lastStep result).WaitTime
                return Succeed
            | false ->
                do ctx.Logger.Log Command "Skipped %s (up to date)" primaryTarget.ShortName
                return Skipped
        }

    let getAction = function
        | FileRule (_, a)
        | FileConditionRule (_, a)
        | MultiFileRule (_, a)
        | PhonyRule (_, a) -> a

    // result expression is...
    match target |> locateRule ctx.Options.Rules ctx.Options.ProjectRoot with
    | Some(rule,groups,targets) ->
        let groupsMap = groups |> Map.ofSeq
        let (Recipe action) = rule |> getAction
        async {
            let! waitTask = (fun channel -> Run(target, targets, run groupsMap action targets, channel)) |> (Scheduler.pool ctx.Engine.Scheduler).PostAndAsyncReply
            let! status = waitTask
            return target, status, ArtifactDep target
        }
    | None ->
        target |> function
        | FileTarget file when File.exists file ->
            async.Return <| (target, ExecStatus.JustFile, FileDep (file, File.getLastWriteTime file))
        | _ ->
            let errorText = sprintf "Neither rule nor file is found for '%s'" target.FullName
            do ctx.Logger.Log Error "%s" errorText
            raise (XakeException errorText)

/// <summary>
/// Executes several artifacts in parallel.
/// </summary>
and execParallel ctx = List.map (execOne ctx) >> Seq.ofList >> Async.Parallel

/// <summary>
/// Gets the status of dependency artifacts (obtained from 'need' calls).
/// </summary>
/// <returns>
/// ExecStatus.Succeed,... in case at least one dependency was rebuilt
/// </returns>
and execNeed (ctx: ExecContext) targets : Async<ExecStatus * Dependency list> =
    async {
        let primaryTarget = ctx.Targets |> List.head
        primaryTarget |> (Progress.TaskSuspend >> ctx.Progress.Post)

        let! statuses = Scheduler.withYieldedSlot ctx.Engine.Scheduler (targets |> execParallel ctx)

        primaryTarget |> (Progress.TaskResume >> ctx.Progress.Post)

        let dependencies = statuses |> Array.map (fun (_,_,x) -> x) |> List.ofArray in
        return
            (match statuses |> Array.exists (fun (_,x,_) -> x = ExecStatus.Succeed) with
                |true -> Succeed
                |false -> Skipped), dependencies
    }

/// phony actions are detected by their name so if there's "clean" phony and file "clean" in `need` list if will choose first
let makeTarget (ctx: ExecContext) name =
    let (Rules rules) = ctx.Options.Rules
    let isPhonyRule nm = function
        |PhonyRule (pattern,_) ->
            nm |> Path.matchGroups pattern "" |> Option.isSome
        | _ -> false
    in
    match rules |> List.exists (isPhonyRule name) with
    | true -> PhonyAction name
    | _ -> ctx.Options.ProjectRoot </> name |> File.make |> FileTarget

/// Implementation of "dry run"
let dryRun (ctx: ExecContext) (groups: string list list) =
    let options = ctx.Options
    let getDeps = getChangeReasons ctx |> memoizeRec

    // getPlainDeps getDeps (getExecTime ctx)
    do ctx.Logger.Log Command "Running (dry) targets %A" groups
    let doneTargets = System.Collections.Hashtable()

    let print f = ctx.Logger.Log Info f
    let indent i = String.replicate i "  "

    let rec showDepStatus ii reasons =
        reasons |> function
        | Other reason ->
            print "%sReason: %s" (indent ii) reason
        | Depends t ->
            print "%sDepends '%s' - changed target" (indent ii) t.ShortName
        | DependsMissingTarget t ->
            print "%sDepends on '%s' - missing target" (indent ii) t.ShortName
        | FilesChanged (file:: rest) ->
            print "%sFile is changed '%s' %s" (indent ii) file (if List.isEmpty rest then "" else sprintf " and %d more file(s)" <| List.length rest)
        | reasons ->
            do print "%sSome reason %A" (indent ii) reasons
        ()
    let rec displayNestedDeps ii =
        function
        | DependsMissingTarget t
        | Depends t ->
            showTargetStatus ii t
        | _ -> ()
    and showTargetStatus ii target =
        if not <| doneTargets.ContainsKey(target) then
            doneTargets.Add(target, 1)
            let deps = getDeps target
            if not <| List.isEmpty deps then
                let execTimeEstimate = getExecTime ctx target
                do ctx.Logger.Log Command "%sRebuild %A (~%Ams)" (indent ii) target.ShortName execTimeEstimate
                deps |> List.iter (showDepStatus (ii+1))
                deps |> List.iter (displayNestedDeps (ii+1))

    let targetGroups = makeTarget ctx |> List.map |> List.map <| groups
    let toSec v = float (v / 1<ms>) * 0.001
    let endTime = Progress.estimateEndTime (getDurationDeps ctx getDeps) options.Threads targetGroups |> toSec

    targetGroups |> List.collect id |> List.iter (showTargetStatus 0)
    let alldeps = targetGroups |> List.collect id |> List.collect getDeps
    if List.isEmpty alldeps then
        ctx.Logger.Log Message "\n\n\tNo changed dependencies. Nothing to do.\n"
    else
        let parallelismMsg =
            let endTimeTotal = Progress.estimateEndTime (getDurationDeps ctx getDeps) 1 targetGroups |> toSec
            if options.Threads > 1 && endTimeTotal > endTime * 1.05 then
                sprintf "\n\tTotal tasks duration is (estimate) in %As\n\tParallelist degree: %.2f" endTimeTotal (endTimeTotal / endTime)
            else ""
        ctx.Logger.Log Message "\n\n\tBuild will be completed (estimate) in %As%s\n" endTime parallelismMsg

let rec unwindAggEx (e:System.Exception) = seq {
    match e with
        | :? System.AggregateException as a -> yield! a.InnerExceptions |> Seq.collect unwindAggEx
        | a -> yield a
    }

let rec runSeq<'r> :Async<'r> list -> Async<'r list> =
    List.fold
        (fun rest i -> async {
            let! tail = rest
            let! head = i
            return head::tail
        })
        (async {return []})

let asyncMap f c = async.Bind(c, f >> async.Return)

/// Runs the build (main function of xake)
let runBuild (ctx: ExecContext) groups =
    let options = ctx.Options

    let runTargets ctx targets =
        let getDeps = getChangeReasons ctx |> memoizeRec

        let needRebuild (target: Target) =
            getDeps >>
            function
            | [] -> false, ""
            | Other reason::_        -> true, reason
            | Depends t ::_          -> true, "Depends on target " + t.ShortName
            | DependsMissingTarget t ::_ -> true, sprintf "Depends on target %s (missing)" t.ShortName
            | FilesChanged (file::_) ::_ -> true, "File(s) changed " + file
            | reasons -> true, sprintf "Some reason %A" reasons
            >>
            function
            | false, _ -> false
            | true, reason ->
                do ctx.Logger.Log Info "Rebuild %A: %s" target.ShortName reason
                true
            <| target
            // todo improve output by printing primary target

        async {
            do ctx.Logger.Log Info "Build target list %A" targets

            let progressSink = Progress.openProgress (getDurationDeps ctx getDeps) options.Threads targets ctx.ShowProgress
            let stepCtx = {ctx with NeedRebuild = List.exists needRebuild; Progress = progressSink}

            try
                return! targets |> execParallel stepCtx
            finally
                do Progress.Finish |> progressSink.Post
        }

    groups |> List.map
        (List.map (makeTarget ctx) >> (runTargets ctx))
    |> runSeq
    |> asyncMap (Array.concat >> List.ofArray)

/// Shared context creation logic.
let private createContextCore (options: EngineOptions) (db: Agent<Storage.DatabaseApi>) vars showProgress =
    let logger = options.Logger
    let scheduler = Scheduler.create logger options.Threads

    let finalize () =
        db.PostAndReply Storage.CloseWait
        FlushLogs()

    let engineState = {
        Options = options
        Db = db
        Scheduler = scheduler
        RootLogger = logger
    }

    let ctx = {
        Ordinal = 0
        Engine = engineState
        Logger = logger
        Progress = Progress.emptyProgress()
        NeedRebuild = fun _ -> false
        Targets = []
        RuleMatches = Map.empty
        Vars = vars
        ShowProgress = showProgress
        }
    ctx, finalize

/// Creates execution context for engine mode: noPersist, no progress.
let createContext (options: EngineOptions) vars =
    let db = Storage.noopDb ()
    createContextCore options db vars false

/// Creates execution context from CLI ExecOptions, handling ResetDb and logger construction.
let createScriptContext (opts: ExecOptions) rules =
    let engineOpts = toEngineOptions opts rules
    let db =
        if opts.NoPersist then
            Storage.noopDb ()
        else
            let dbPath = engineOpts.ProjectRoot </> opts.DbFileName
            if opts.ResetDb then
                Storage.cleanupDb dbPath engineOpts.Logger
            Storage.openDb dbPath engineOpts.Logger
    createContextCore engineOpts db opts.Vars opts.Progress

/// Demand a single target be built; returns its ExecStatus.
let demandTarget (ctx: ExecContext) (targetName: string) : Async<ExecStatus> =
    async {
        let target = makeTarget ctx targetName
        let getDeps = getChangeReasons ctx |> memoizeRec
        let needRebuild (t: Target) =
            getDeps t |> function | [] -> false | _ -> true
        let progressSink = Progress.openProgress (getDurationDeps ctx getDeps) ctx.Engine.Options.Threads [target] ctx.ShowProgress
        let stepCtx = {ctx with NeedRebuild = List.exists needRebuild; Progress = progressSink}
        try
            let! (_, status, _) = execOne stepCtx target
            return status
        finally
            Progress.Finish |> progressSink.Post
    }

type ScriptResult = BuildOk | BuildFailed of exn

/// Pure build execution - no process lifecycle, no exit calls.
let runBuildScript options rules : ScriptResult =
    let ctx, finalize = createScriptContext options rules
    let logger = ctx.Logger

    logger.Log Info "Options: %A" options

    let targetLists =
        options.Targets |>
        function
        | [] ->
            do logger.Log Level.Message "No target(s) specified. Defaulting to 'main'"
            [["main"]]
        | tt ->
            tt |> List.map (fun (s: string) -> s.Split(';', '|') |> List.ofArray)

    let reportError ctx error details =
        do ctx.Logger.Log Error "Error '%s'. See build.log for details" error
        do ctx.Logger.Log Verbose "Error details are:\n%A\n\n" details

    try
        match options with
        | Dump ->
            do logger.Log Level.Command "Dumping dependencies for targets %A" targetLists
            targetLists |> List.iter (List.map (makeTarget ctx) >> (dumpDeps ctx))
            BuildOk
        | Dryrun ->
            targetLists |> (dryRun ctx)
            BuildOk
        | _ ->
            let start = System.DateTime.Now
            try
                targetLists |> (runBuild ctx) |> Async.RunSynchronously |> ignore
                ctx.Logger.Log Message "\n\n    Build completed in %A\n" (System.DateTime.Now - start)
                BuildOk
            with | exn ->
                let exceptions = exn |> unwindAggEx
                let errors = exceptions |> Seq.map (fun e -> e.Message) in
                let details = exceptions |> Seq.last |> fun e -> e.ToString()
                let errorText = errors |> String.concat "\r\n"

                do reportError ctx errorText details
                ctx.Logger.Log Message "\n\n\tBuild failed after running for %A\n" (System.DateTime.Now - start)
                BuildFailed exn
    finally
        finalize()

/// CLI entry point - adds Ctrl+C handler and exit calls.
let runScript options rules =
    System.Console.CancelKeyPress |> Event.add (fun _ -> exit 1)
    match runBuildScript options rules with
    | BuildOk -> ()
    | BuildFailed _ when options.ThrowOnError ->
        raise (XakeException "Script failure. See log file for details.")
    | BuildFailed _ -> exit 2

/// "need" implementation
let need targets = recipe {
    let startTime = System.DateTime.Now

    let! ctx = getCtx()
    let! _,deps = targets |> execNeed ctx

    let totalDuration = int (System.DateTime.Now - startTime).TotalMilliseconds * 1<ms>
    let! result = getResult()
    let result' = {result with Depends = result.Depends @ deps} |> (Step.updateWaitTime totalDuration)
    do! setResult result'
}
