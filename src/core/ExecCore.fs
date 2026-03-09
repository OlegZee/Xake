module internal Xake.ExecCore

open System.Text.RegularExpressions
open DependencyAnalysis

open Storage
open WorkerPool

/// Returns the current recipe execution context as a tuple of build result and execution context.
let getCtx() = Recipe (fun (r,c) -> async {return (r,c)})

/// Writes a formatted message to the build log at the specified logging level.
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

/// Substitutes wildcard and named capture group matches into a file pattern.
/// Positional wildcards (*, **, ?) are replaced by index, named groups by tag.
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

/// Finds the first rule whose pattern matches the given target.
/// Returns the matched rule, captured groups, and the full list of targets it produces.
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

/// Global counter for assigning ordinals to tasks submitted to the worker pool.
let refTaskOrdinal = ref 0

/// Creates a new execution context for a task, assigning it a unique ordinal and a prefixed logger.
let newTaskContext targets matches ctx =
    let ordinal = System.Threading.Interlocked.Increment(refTaskOrdinal)
    let prefix = ordinal |> sprintf "%i> "
    in
    {ctx with
        Ordinal = ordinal; Logger = PrefixLogger prefix ctx.Engine.RootLogger
        Targets = targets
        RuleMatches = matches
    }

/// Executes a single target: locates the matching rule, submits the work to the
/// scheduler, and returns the target, its execution status, and a dependency record.
/// If no rule matches but a corresponding file exists, returns JustFile status.
/// Raises XakeException when neither a rule nor a file is found.
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

/// Executes multiple targets in parallel and collects their results.
and execParallel ctx = List.map (execOne ctx) >> Seq.ofList >> Async.Parallel

/// Executes dependency targets in parallel, yielding the current scheduler slot while waiting.
/// Returns Succeed if at least one dependency was rebuilt, Skipped otherwise,
/// along with the list of recorded dependencies.
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

/// Resolves a target name to a PhonyAction if a matching phony rule exists, otherwise to a FileTarget.
/// Phony actions take precedence over files with the same name.
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

/// Recursively flattens an AggregateException into its leaf exceptions.
let rec unwindAggEx (e:System.Exception) = seq {
    match e with
        | :? System.AggregateException as a -> yield! a.InnerExceptions |> Seq.collect unwindAggEx
        | a -> yield a
    }

/// Executes a list of async computations sequentially, collecting results in order.
let rec runSeq<'r> :Async<'r> list -> Async<'r list> =
    List.fold
        (fun rest i -> async {
            let! tail = rest
            let! head = i
            return head::tail
        })
        (async {return []})

/// Maps a function over the result of an async computation.
let asyncMap f c = async.Bind(c, f >> async.Return)

/// Runs the full build pipeline for the given target groups.
/// Each group is executed sequentially; targets within a group run in parallel.
/// Returns the combined list of target/status/dependency results.
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

/// Creates the shared execution context and returns it together with a finalize callback.
/// The finalize callback closes the database, disposes the scheduler, and flushes logs.
let createContextCore (options: EngineOptions) (db: Agent<Storage.DatabaseApi>) vars showProgress =
    let logger = options.Logger
    let scheduler = Scheduler.create logger options.Threads

    let finalize () =
        db.PostAndReply Storage.CloseWait
        (scheduler :> System.IDisposable).Dispose()
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

/// Creates an execution context for engine/library mode with no persistence and no progress display.
let createContext (options: EngineOptions) vars =
    let db = Storage.noopDb ()
    createContextCore options db vars false

/// Builds a single target by name and returns its execution status.
/// Sets up dependency analysis, progress tracking, and scheduler context for the build.
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

/// Recipe action that declares dependencies on the given targets.
/// Executes them in parallel, records their dependencies, and updates step wait time.
let need targets = recipe {
    let startTime = System.DateTime.Now

    let! ctx = getCtx()
    let! _,deps = targets |> execNeed ctx

    let totalDuration = int (System.DateTime.Now - startTime).TotalMilliseconds * 1<ms>
    let! result = getResult()
    let result' = {result with Depends = result.Depends @ deps} |> (Step.updateWaitTime totalDuration)
    do! setResult result'
}
