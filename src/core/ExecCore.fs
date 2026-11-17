module internal Xake.ExecCore

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

/// `Path.matchRule` with an invalid pattern reported as the engine's build error (spec 7.1): a
/// rule built from the `Rule` DU cases directly is first parsed here.
let private matchRulePattern pattern root target =
    try Path.matchRule pattern root target
    with PatternAst.PatternException message -> raise (XakeException message)

let private specificityOf pattern =
    try Path.specificity pattern
    with PatternAst.PatternException message -> raise (XakeException message)

/// A rule that matches a target, with what is needed to rank and explain the choice.
/// `Pattern` is None for a condition rule (no textual pattern). `Index` is the position in the
/// stored rule list, which is in reverse declaration order, so 0 is the last declared rule.
type RuleMatchInfo<'ctx> = {
    Rule: 'ctx Rule
    Groups: (string * string) list
    Targets: Target list
    Pattern: string option
    Specificity: Path.Specificity
    Index: int
    /// evidence warning codes of the match (XP119), reported when the rule is selected
    Evidence: string list
}

/// A condition rule has no pattern to measure, so it ranks below every pattern rule (whose
/// literal count is >= 0). When two of them match, declaration order alone breaks the tie.
let conditionSpecificity : Path.Specificity = (System.Int32.MinValue, 0, 0)

/// All rules that match the target, each with its captured groups, produced targets, pattern and
/// specificity, sorted most-specific first and, within equal specificity, last-declared first
/// (the stored list is reverse-declared, so lower Index = declared later). This is the single
/// source of truth for rule selection (`locateRule`), the ambiguity warning and `EXPLAIN_RULE`.
let explainMatch (Rules rules) projectRoot target : RuleMatchInfo<_> list =
    let rec matchRule index rule =
        match rule, target with

        |FileConditionRule (meetCondition,_,_), FileTarget file when file |> File.getFullName |> meetCondition ->
            // TODO let condition rule extracting named groups
            Some { Rule = rule; Groups = []; Targets = [target]; Pattern = None; Specificity = conditionSpecificity; Index = index; Evidence = [] }

        |FileRule ([pattern], { Also = [] }, _), FileTarget file ->
            file
            |> File.getFullName
            |> matchRulePattern pattern projectRoot
            |> Option.map (fun (groups, evidence) -> { Rule = rule; Groups = groups; Targets = [target]; Pattern = Some pattern; Specificity = specificityOf pattern; Index = index; Evidence = evidence })

        |FileRule (patterns, header, _), FileTarget file ->
            // several outputs (`*..>` or `also`): the match of any names them all, main first
            let patterns = Rule.outputs patterns header
            let fname = file |> File.getFullName
            patterns
            |> List.tryPick(fun pattern ->
                matchRulePattern pattern projectRoot fname
                |> Option.map(fun (groups, evidence) -> groups, evidence, pattern)
                )
            |> Option.map (fun (groups, evidence, pattern) ->
                let generateName name =
                    try PatternAst.formatMatches (Map.ofList groups) name
                    with PatternAst.PatternException message -> raise (XakeException message)

                let targets = patterns |> List.map (generateName >> (</>) projectRoot >> File.make >> FileTarget)
                { Rule = rule; Groups = groups; Targets = targets; Pattern = Some pattern; Specificity = specificityOf pattern; Index = index; Evidence = evidence })

        |PhonyRule (pattern,_), PhonyAction phony ->
            phony
            |> matchRulePattern pattern ""
            |> Option.map (fun (groups, evidence) -> { Rule = rule; Groups = groups; Targets = [target]; Pattern = Some pattern; Specificity = specificityOf pattern; Index = index; Evidence = evidence })

        |DelegatedRule (inner, _), _ ->
            // Match using the inner rule's pattern logic, but surface the outer
            // DelegatedRule so the dispatcher knows execution is delegated.
            matchRule index inner |> Option.map (fun m -> { m with Rule = rule })

        | _ -> None

    rules
    |> List.mapi matchRule
    |> List.choose id
    |> List.sortWith (fun a b ->
        // most specific first, then (lower Index = declared later) first
        match compare b.Specificity a.Specificity with
        | 0 -> compare a.Index b.Index
        | c -> c)

/// Finds the rule that should build the given target: the most specific match, ties broken by
/// last declared. Returns the matched rule, captured groups, and the full list of targets it
/// produces. (The legacy last-declared-wins order is applied by the caller via `RuleMatch`.)
let locateRule rules projectRoot target =
    explainMatch rules projectRoot target
    |> List.tryHead
    |> Option.map (fun m -> m.Rule, m.Groups, m.Targets)

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
        // a task started by a `need` holds nothing, whatever the needing task holds
        HeldResources = []
    }

/// Executes a single target: locates the matching rule, submits the work to the
/// scheduler, and returns the target, its execution status, and a dependency record.
/// If no rule matches but a corresponding file exists, returns JustFile status.
/// Raises XakeException when neither a rule nor a file is found.
let rec execOne (ctx: ExecContext) target =

    let run ruleMatches action targets =
        let primaryTarget = targets |> List.head
        async {
            ctx.Counters.TaskScheduled ()
            match ctx.NeedRebuild targets with
            | true ->
                ctx.Counters.TargetRun ()
                let taskContext = newTaskContext targets ruleMatches ctx
                do ctx.Logger.Log Command "Started %s as task %i" primaryTarget.ShortName taskContext.Ordinal

                do Progress.TaskStart primaryTarget |> ctx.Progress.Post

                let startResult = {BuildLog.makeResult targets with Steps = [Step.start "all"]}
                let! (result,_) = action (startResult, taskContext)
                // Built is the completion time: it is the change stamp of a phony target
                // (see Dependency.ArtifactDep2 and DependencyAnalysis.getChangeStamp)
                let result = {Step.updateTotalDuration result with Built = System.DateTime.Now}

                Store result |> ctx.Db.Post

                do Progress.TaskComplete primaryTarget |> ctx.Progress.Post
                do ctx.Logger.Log Command "Completed %s in %A ms (wait %A ms)" primaryTarget.ShortName (Step.lastStep result).OwnTime  (Step.lastStep result).WaitTime
                return Succeed
            | false ->
                ctx.Counters.TargetSkipped ()
                do ctx.Logger.Log Command "Skipped %s (up to date)" primaryTarget.ShortName
                return Skipped
        }

    // Runs a delegated rule: the up-to-date check and execution are delegated to the
    // caller-supplied executor. The ENTIRE delegated task — executor (store lookup /
    // dedup / remote dispatch) AND the recipe body — runs detached: it holds no CPU slot,
    // so a delegated rule is never bounded by the local thread pool. Its concurrency is
    // governed solely by whatever dispatch Resource the executor applies (or is unbounded).
    // `runDetached` inside a delegated body is therefore redundant. The result is still
    // stored locally so downstream targets resolve as built.
    let runDelegated ruleMatches action (executor: DelegatedExecutor<ExecContext>) targets =
        let primaryTarget = targets |> List.head
        async {
            ctx.Counters.TaskScheduled ()
            ctx.Counters.TargetRun ()
            let taskContext = newTaskContext targets ruleMatches ctx
            do ctx.Logger.Log Command "Started %s as delegated task %i" primaryTarget.ShortName taskContext.Ordinal
            do Progress.TaskStart primaryTarget |> ctx.Progress.Post

            let bodyThunk () = async {
                let startResult = {BuildLog.makeResult targets with Steps = [Step.start "all"]}
                let! (result,_) = action (startResult, taskContext)
                return {Step.updateTotalDuration result with Built = System.DateTime.Now}
            }

            let! result =
                Scheduler.withYieldedSlot ctx.Engine.Scheduler (executor taskContext targets bodyThunk)

            Store result |> ctx.Db.Post

            do Progress.TaskComplete primaryTarget |> ctx.Progress.Post
            do ctx.Logger.Log Command "Completed delegated %s" primaryTarget.ShortName
            return Succeed
        }

    let rec getAction = function
        | FileRule (_, h, a)
        | FileConditionRule (_, h, a) -> h, a
        | PhonyRule (_, a) -> RuleHeader.Empty, a
        | DelegatedRule (inner, _) -> getAction inner

    // result expression is...
    let candidates = explainMatch ctx.Options.Rules ctx.Options.ProjectRoot target

    // The rule selection strategy, set in the script by the `ruleMatch` operation.
    let ruleMatchMode = ctx.Options.RuleMatch

    // -d EXPLAIN_RULE=<target>: dump the matching rules and the winner for a matching target.
    ctx.Vars
    |> List.tryPick (fun (k, v) -> if System.String.Equals(k, "EXPLAIN_RULE", System.StringComparison.OrdinalIgnoreCase) then Some v else None)
    |> Option.iter (fun wanted ->
        if target.ShortName = wanted || target.FullName = wanted || target.FullName.EndsWith wanted then
            ctx.Logger.Log Message "Rule candidates for '%s' (%s):" target.FullName
                (match ruleMatchMode with MostSpecific -> "most specific wins" | LastDeclared -> "last declared wins")
            candidates |> List.iteri (fun i m ->
                let (l, d, w) = m.Specificity
                ctx.Logger.Log Message "  %s %-48s {lit=%d, **=%d, wc=%d}"
                    (if i = 0 then "->" else "  ") (m.Pattern |> Option.defaultValue "<condition>") l (-d) (-w)))

    let selected =
        match ruleMatchMode with
        | MostSpecific -> candidates |> List.tryHead
        | LastDeclared -> candidates |> List.sortBy (fun m -> m.Index) |> List.tryHead

    // Warn when the winner ties on specificity with a different pattern (MostSpecific only): the
    // choice then rests on declaration order, which is the trap this feature exists to surface.
    match ruleMatchMode, selected with
    | MostSpecific, Some best ->
        match best.Pattern with
        | Some bestPat ->
            let ties =
                candidates |> List.choose (fun m ->
                    match m.Pattern with
                    | Some p when p <> bestPat && m.Specificity = best.Specificity -> Some p
                    | _ -> None)
            match ties with
            | [] -> ()
            | _ ->
                let (l, d, w) = best.Specificity
                ctx.Logger.Log Warning
                    "Ambiguous rules for '%s': '%s' and '%s' have equal specificity {lit=%d, **=%d, wc=%d}; last-declared '%s' chosen. Make one more specific or reorder."
                    target.FullName bestPat (String.concat "', '" ties) l (-d) (-w) bestPat
        | None -> ()
    | _ -> ()

    // a legacy reading the match revealed (XP119), once per pattern (spec 7.3)
    match selected with
    | Some { Pattern = Some pattern; Evidence = evidence } ->
        for code in evidence do
            PatternAst.Warnings.reportEvidence "rule" pattern (Path.ruleEvidence code target.FullName)
    | _ -> ()

    match selected with
    | Some {Rule = rule; Groups = groups; Targets = targets} ->
        let groupsMap = groups |> Map.ofSeq
        let header, recipe = rule |> getAction
        let (Recipe action) = withHeader header groupsMap recipe
        let taskAction =
            match rule with
            | DelegatedRule (_, executor) -> runDelegated groupsMap action executor targets
            | _ -> run groupsMap action targets
        async {
            let! waitTask = (fun channel -> Run(target, targets, taskAction, channel)) |> (Scheduler.pool ctx.Engine.Scheduler).PostAndAsyncReply
            let! status = waitTask
            // the dependee is built or up to date now: record its change stamp, so a later run
            // sees a rebuild done in between, by another run or target group (S66)
            let stamp = getChangeStamp ctx target |> Option.defaultValue System.DateTime.MinValue
            return target, status, ArtifactDep2 (target, stamp)
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
        // hold-and-wait: a dependency built while this recipe holds a resource may itself wait
        // for that resource, and every unit can end up held by recipes waiting for their
        // dependencies (an acyclic graph, a silent hang). A plain file without a rule never
        // schedules anything, so it is allowed; a rule target is refused even when it is
        // already built, so the error does not depend on timing (docs/resources.md).
        match ctx.HeldResources with
        | [] -> ()
        | held ->
            let ruleTarget =
                targets |> List.tryFind (fun t -> t |> locateRule ctx.Options.Rules ctx.Options.ProjectRoot |> Option.isSome)
            match ruleTarget with
            | None -> ()
            | Some needed ->
                let needing =
                    match ctx.Targets with
                    | t :: _ -> sprintf "target '%s'" t.FullName
                    | [] -> "the script"
                let resources = held |> List.map (sprintf "'%s'") |> String.concat ", "
                let errorText =
                    sprintf "need inside withResource: %s needs '%s' while it holds the resource %s. A dependency built under a held resource can deadlock the build. Move the need before withResource (or acquire the resource around the command only), or wrap it in allowNeed if the dependency never takes the resource."
                        needing needed.FullName resources
                do ctx.Logger.Log Error "%s" errorText
                raise (XakeException errorText)

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

/// The static `needs` of a header for one match: each pattern with the match's groups filled in.
/// Without wildcards it is one target, resolved as `need` resolves a name (a phony target first);
/// with wildcards (or braces) it is a fileset of the existing files, recorded as `GetFiles`.
and private resolveNeeds (ctx: ExecContext) (groups: Map<string, string>) (patterns: string list) =
    let resolve (pattern: string) =
        let tokens = PatternAst.Rule.parse pattern
        let single =
            if List.isEmpty (PatternAst.wildcards tokens) then
                try Some (PatternAst.formatMatches groups pattern)
                with PatternAst.PatternException _ -> None   // braces or a class outside a group
            else None
        match single with
        | Some path -> [ makeTarget ctx path ], []
        | None ->
            let mask = PatternAst.glob (fun name -> Map.tryFind name groups) (fun _ -> None) tokens
            let fileset = Fileset.ls mask
            let (Filelist list) as files = fileset |> Fileset.toFileList ctx.Options.ProjectRoot
            list |> List.map FileTarget, [ GetFiles (fileset, files) ]
    let resolved = patterns |> List.map resolve
    (resolved |> List.collect fst |> List.distinct), (resolved |> List.collect snd)

/// Applies a rule header around a rule's recipe (docs/rule-header.md): the static `needs` are
/// requested together and recorded before the body; then the `requires` resources are taken, in
/// the order of their names, yielding the CPU slot while waiting; the body runs holding them and
/// they are released also when it fails.
and private withHeader (header: RuleHeader) (groups: Map<string, string>) (body: Recipe<ExecContext, unit>) : Recipe<ExecContext, unit> =
    if header.Needs.IsEmpty && header.Requires.IsEmpty then body else
    Recipe (fun (result: BuildResult, ctx: ExecContext) -> async {
        let! result =
            match header.Needs with
            | [] -> async.Return result
            | patterns -> async {
                let startTime = System.DateTime.Now
                let targets, filesetDeps = resolveNeeds ctx groups patterns
                let! _, deps = execNeed ctx targets
                let waited = int (System.DateTime.Now - startTime).TotalMilliseconds * 1<ms>
                return { result with Depends = result.Depends @ filesetDeps @ deps } |> Step.updateWaitTime waited }
        let locks = header.Locks
        let rec holding (taken: (Resource * int) list) (rest: (Resource * int) list) = async {
            match rest with
            | [] ->
                let held = taken |> List.map (fun (r, _) -> r.ResourceName)
                return! A.runAction body (result, { ctx with HeldResources = held @ ctx.HeldResources })
            | (resource, units) :: rest ->
                do! Scheduler.withYieldedSlot ctx.Engine.Scheduler (Resource.acquire resource units)
                try
                    return! holding (taken @ [ resource, units ]) rest
                finally
                    Resource.release resource units
        }
        return! holding [] locks
    })

/// Resolves a target name to a PhonyAction if a matching phony rule exists, otherwise to a FileTarget.
/// Phony actions take precedence over files with the same name.
and makeTarget (ctx: ExecContext) name =
    let (Rules rules) = ctx.Options.Rules
    let rec isPhonyRule nm = function
        |PhonyRule (pattern,_) ->
            nm |> matchRulePattern pattern "" |> Option.isSome
        |DelegatedRule (inner, _) -> isPhonyRule nm inner
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

            // the change analysis below is memoized for the whole group, so the pool has to
            // treat the group as one run: a target it already built must not be built again
            do Scheduler.newRun ctx.Engine.Scheduler

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
    // pattern warnings (spec 7.3) and fileset scan notes (6.5, 6.6), including those buffered at
    // script load, go to this build's log
    let patternWarnings = PatternAst.Warnings.attach (fun level message -> logger.Log level "%s" message)

    let finalize () =
        patternWarnings.Dispose()
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
        HeldResources = []
        Counters = RunCounters()
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
        do Scheduler.newRun ctx.Engine.Scheduler
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
