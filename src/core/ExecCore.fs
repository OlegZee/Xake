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

        |FileConditionRule (meetCondition,_,_), FileTarget file when file |> File.getSysFileName |> meetCondition ->
            // TODO let condition rule extracting named groups
            Some { Rule = rule; Groups = []; Targets = [target]; Pattern = None; Specificity = conditionSpecificity; Index = index; Evidence = [] }

        |FileRule ([pattern], { Also = [] }, _), FileTarget file ->
            file.Name
            |> matchRulePattern pattern projectRoot
            |> Option.map (fun (groups, evidence) -> { Rule = rule; Groups = groups; Targets = [target]; Pattern = Some pattern; Specificity = specificityOf pattern; Index = index; Evidence = evidence })

        |FileRule (patterns, header, _), FileTarget file ->
            // several outputs (`*..>` or `also`): the match of any names them all, main first
            let patterns = Rule.outputs patterns header
            let fname = file.Name
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

/// The candidates for a target and the rule selected among them by the script's `ruleMatch`.
let selectRule (ctx: ExecContext) target =
    let candidates = explainMatch ctx.Options.Rules ctx.Options.ProjectRoot target
    candidates,
    match ctx.Options.RuleMatch with
    | MostSpecific -> candidates |> List.tryHead
    | LastDeclared -> candidates |> List.sortBy (fun m -> m.Index) |> List.tryHead

/// A rule's header and recipe.
let rec ruleAction = function
    | FileRule (_, h, a) | FileConditionRule (_, h, a) -> h, a
    | PhonyRule (_, a) -> RuleHeader.Empty, a
    | DelegatedRule (inner, _) -> ruleAction inner

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

/// Re-observes (and hashes, with `cutoff`) the file outputs of a rule that just finished; true when
/// every output kept the content it had (content cut-off).
let private observeOutputs (ctx: ExecContext) (cutoff: bool) (targets: Target list) = async {
    let files = targets |> List.choose (function FileTarget f -> Some f | PhonyAction _ -> None)
    // changed in this run, or not: one observation for the task's outputs
    let! states = observeMany ctx (Output cutoff) (ctx.RunEpoch - 1) files
    let phony = targets |> List.exists (function PhonyAction _ -> true | _ -> false)
    return not phony && states |> Array.forall (function Unchanged _ -> true | Changed _ | Absent -> false) }

/// Hashes the outputs of a rule about to run that have no hash and were not touched since they
/// were recorded, so that cut-off works from their first rebuild.
let private preHash (ctx: ExecContext) (targets: Target list) =
    observe ctx PreHash (targets |> List.choose (function FileTarget f -> Some f | PhonyAction _ -> None))

/// Stores a task's result and waits until the snapshot holds it (docs/features/idle-perf, L2);
/// then its dependency files without a record (a delegated result, A3) are observed, without
/// hashing, as the agent did on `Store` before.
let private storeResult (ctx: ExecContext) (result: BuildResult) = async {
    do! Storage.storeResult ctx.Db result
    let files = ctx.Db.Snapshot.Files
    let missing =
        result.Depends
        |> List.choose (function FileDep f | TargetDep (FileTarget f) -> Some f | _ -> None)
        |> List.filter (files.ContainsKey >> not) |> List.distinct
    if not missing.IsEmpty then
        do! Storage.observeFiles { ctx.Observe with Hashing = false } Verdict System.Int32.MaxValue missing |> Async.Ignore }

/// Whether a rule's outputs take part in content cut-off (`noContentCutoff` opts out).
let private ruleCutoff (header: RuleHeader) = header.ContentCutoff <> Some false

/// A new table for the two-spellings check of one run.
let newSpellings () = System.Collections.Concurrent.ConcurrentDictionary<string, File * bool>(System.StringComparer.Ordinal)

/// The two-spellings check (5.0): a file named in this run under another spelling of the same
/// path (compared ignoring case). Two targets: an error (the target fails); otherwise a warning,
/// once per pair. The case on disk is not checked.
let checkSpelling (ctx: ExecContext) (isTarget: bool) (file: File) =
    let first, firstTarget = ctx.Spellings.GetOrAdd(file.Name.ToUpperInvariant(), (file, isTarget))
    if first <> file then
        let show = File.display ctx.Options.ProjectRoot
        if isTarget && firstTarget then
            let errorText = sprintf "target '%s' is also named '%s'" (show first) (show file)
            do ctx.Logger.Log Error "%s" errorText
            raise (XakeException errorText)
        elif ctx.Spellings.TryAdd("\u0000" + first.Name + "\u0000" + file.Name, (file, isTarget)) then
            ctx.Logger.Log Warning "file '%s' is also named '%s' in this run" (show first) (show file)

/// Executes a single target: locates the matching rule, submits the work to the
/// scheduler, and returns the target, its execution status, and a dependency record.
/// If no rule matches but a corresponding file exists, returns JustFile status.
/// Raises XakeException when neither a rule nor a file is found.
let rec prepareOne (ctx: ExecContext) target : Choice<File, Async<Target * ExecStatus * Dependency>> =

    // a rule with several outputs (`*..>`, `also`) must write every one of them (M2): a missing
    // one would make the rule rerun forever. Nothing is recorded, so the next run retries.
    let checkOutputsWritten (targets: Target list) =
        match targets |> List.filter (function FileTarget f -> not (File.exists f) | PhonyAction _ -> false) with
        | missing when targets.Length > 1 && not missing.IsEmpty ->
            let errorText =
                sprintf "The rule for '%s' did not write its declared output%s %s; a rule writes every output it declares (also, *..>): write it, or remove it from the rule"
                    ((List.head targets).Display ctx.Options.ProjectRoot) (if missing.Length > 1 then "s" else "")
                    (missing |> List.map (fun t -> "'" + t.Display ctx.Options.ProjectRoot + "'") |> String.concat ", ")
            ctx.Logger.Log Error "%s" errorText
            raise (XakeException errorText)
        | _ -> ()

    // A task: its static `needs`, the verdict (which may wait for running dependees), then the
    // pre-hash of its outputs, its locks and its body (docs/content-cutoff.md).
    let run ruleMatches header action targets =
        let primaryTarget = targets |> List.head
        async {
            ctx.Counters.TaskScheduled ()
            let taskContext = newTaskContext targets ruleMatches ctx
            let! headerDeps, step = headerNeeds taskContext header
            let verdictStarted = System.DateTime.Now
            let! rebuild = ctx.NeedRebuild targets
            // the verdict may wait for a running dependee: the step's wait, as the needs
            let needs = headerDeps, { step with WaitTime = step.WaitTime + Step.msSince verdictStarted }
            match rebuild with
            | true ->
                do ctx.Logger.Log Command "Started %s as task %i" (primaryTarget.Display ctx.Options.ProjectRoot) taskContext.Ordinal

                do Progress.TaskStart primaryTarget |> ctx.Progress.Post

                let cutoff = ruleCutoff header
                if cutoff then do! preHash ctx targets
                let! result = runBody taskContext header action targets needs
                // Epoch is the run the task started in (A2)
                let result = { result with Epoch = ctx.RunEpoch }

                checkOutputsWritten targets
                let! same = observeOutputs ctx cutoff targets
                do! storeResult ctx result
                ctx.Counters.TargetRun ()
                if same && cutoff && ctx.Options.ContentCutoff then
                    ctx.Counters.TargetCutOff ()
                    do ctx.Logger.Log Info "cut-off: %s rebuilt with the same content" (primaryTarget.Display ctx.Options.ProjectRoot)

                do Progress.TaskComplete primaryTarget |> ctx.Progress.Post
                do ctx.Logger.Log Command "Completed %s in %A ms (wait %A ms)" (primaryTarget.Display ctx.Options.ProjectRoot) (Step.lastStep result).OwnTime  (Step.lastStep result).WaitTime
                return Succeed
            | false ->
                ctx.Counters.TargetSkipped ()
                // its predicted duration leaves the estimate
                do Progress.TaskComplete primaryTarget |> ctx.Progress.Post
                do ctx.Logger.Log Command "Skipped %s (up to date)" (primaryTarget.Display ctx.Options.ProjectRoot)
                return Skipped
        }

    // Runs a delegated rule: the up-to-date check and execution are delegated to the
    // caller-supplied executor. The ENTIRE delegated task — executor (store lookup /
    // dedup / remote dispatch) AND the recipe body — runs detached: it holds no CPU slot,
    // so a delegated rule is never bounded by the local thread pool. Its concurrency is
    // governed solely by whatever dispatch Resource the executor applies (or is unbounded).
    // `runDetached` inside a delegated body is therefore redundant. The result is still
    // stored locally so downstream targets resolve as built.
    let runDelegated ruleMatches header action (executor: DelegatedExecutor<ExecContext>) targets =
        let primaryTarget = targets |> List.head
        async {
            let taskContext = newTaskContext targets ruleMatches ctx
            do ctx.Logger.Log Command "Started %s as delegated task %i" (primaryTarget.Display ctx.Options.ProjectRoot) taskContext.Ordinal
            do Progress.TaskStart primaryTarget |> ctx.Progress.Post

            let bodyRan = ref false
            let bodyThunk () = async {
                bodyRan.Value <- true
                return! runHeaded taskContext header action targets
            }

            ctx.Counters.TaskScheduled ()
            ctx.Counters.TargetRun ()
            let! result =
                Scheduler.withYieldedSlot ctx.Engine.Scheduler targets (executor taskContext targets bodyThunk)
            // the body's result has the run's epoch; one returned without the body keeps the epoch
            // of the local result (A3): a foreign epoch is never stored
            let! epoch =
                if bodyRan.Value then async.Return ctx.RunEpoch
                else async {
                    let local = Storage.tryGetResult ctx.Db primaryTarget
                    return local |> Option.map (fun r -> r.Epoch) |> Option.defaultValue ctx.RunEpoch }
            let result = { result with Epoch = epoch }

            checkOutputsWritten targets
            let! _ = observeOutputs ctx (ruleCutoff header) targets
            do! storeResult ctx result

            do Progress.TaskComplete primaryTarget |> ctx.Progress.Post
            do ctx.Logger.Log Command "Completed delegated %s" (primaryTarget.Display ctx.Options.ProjectRoot)
            return Succeed
        }

    let candidates, selected = selectRule ctx target
    let ruleMatchMode = ctx.Options.RuleMatch

    // -d EXPLAIN_RULE=<target>: dump the matching rules and the winner for a matching target.
    ctx.Vars
    |> List.tryPick (fun (k, v) -> if System.String.Equals(k, "EXPLAIN_RULE", System.StringComparison.OrdinalIgnoreCase) then Some v else None)
    |> Option.iter (fun wanted ->
        if target.Name = wanted || target.Display ctx.Options.ProjectRoot = wanted || target.Name.EndsWith wanted then
            ctx.Logger.Log Message "Rule candidates for '%s' (%s):" (target.Display ctx.Options.ProjectRoot)
                (match ruleMatchMode with MostSpecific -> "most specific wins" | LastDeclared -> "last declared wins")
            candidates |> List.iteri (fun i m ->
                let (l, d, w) = m.Specificity
                ctx.Logger.Log Message "  %s %-48s {lit=%d, **=%d, wc=%d}"
                    (if i = 0 then "->" else "  ") (m.Pattern |> Option.defaultValue "<condition>") l (-d) (-w)))

    // one file has one rule (M1): every other output of the selected rule must select it back
    match selected with
    | Some best when best.Targets.Length > 1 ->
        let ruleName (m: RuleMatchInfo<_>) = m.Pattern |> Option.defaultValue "<condition>"
        for output in best.Targets do
            match output <> target, selectRule ctx output with
            | true, (_, Some other) when other.Index <> best.Index ->
                let errorText =
                    sprintf "Outputs overlap: '%s' is an output of the rule \"%s\" (building '%s') and is built by the rule \"%s\"; a file has one rule: narrow one of the patterns, or remove the output from the first rule"
                        (output.Display ctx.Options.ProjectRoot) (ruleName best) (target.Display ctx.Options.ProjectRoot) (ruleName other)
                ctx.Logger.Log Error "%s" errorText
                raise (XakeException errorText)
            | _ -> ()
    | _ -> ()

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
                    (target.Display ctx.Options.ProjectRoot) bestPat (String.concat "', '" ties) l (-d) (-w) bestPat
        | None -> ()
    | _ -> ()

    // a legacy reading the match revealed (XP119), once per pattern (spec 7.3)
    match selected with
    | Some { Pattern = Some pattern; Evidence = evidence } ->
        for code in evidence do
            PatternAst.Warnings.reportEvidence "rule" pattern (Path.ruleEvidence code target.Name)
    | _ -> ()

    match target, selected with
    | FileTarget file, _ -> checkSpelling ctx selected.IsSome file
    | _ -> ()

    match selected with
    | Some {Rule = rule; Groups = groups; Targets = targets} ->
        let groupsMap = groups |> Map.ofSeq
        let header, Recipe action = ruleAction rule
        let taskAction =
            match rule with
            | DelegatedRule (_, executor) -> runDelegated groupsMap header action executor targets
            | _ -> run groupsMap header action targets
        // the fast path: a target not started in this run and up to date without building or
        // waiting is not scheduled at all
        Choice2Of2 <| async {
            let! fast =
                match rule, Scheduler.query ctx.Engine.Scheduler target with
                | DelegatedRule _, _ -> async.Return false
                | _, NotStarted -> async {
                    do! ctx.Counters.FastGate.WaitAsync() |> Async.AwaitTask
                    try return! ctx.UpToDate target
                    finally ctx.Counters.FastGate.Release() |> ignore }
                | _ -> async.Return false
            let! status =
                if fast then
                    ctx.Counters.TargetSkipped ()
                    do ctx.Logger.Log Command "Skipped %s (up to date)" (target.Display ctx.Options.ProjectRoot)
                    async.Return Skipped
                else async {
                    let! waitTask = (fun channel -> Run(target, targets, taskAction, channel)) |> (Scheduler.pool ctx.Engine.Scheduler).PostAndAsyncReply
                    return! waitTask }
            // the dependee is built or up to date now: its file is observed (a built one already
            // was, right after its rule) and the dependency recorded by id (decision 1c)
            match target with
            | FileTarget file -> do! observe ctx Verdict [ file ]
            | PhonyAction _ -> ()
            return target, status, TargetDep target
        }
    | None ->
        target |> function
        | FileTarget file when File.exists file -> Choice1Of2 file
        | _ ->
            let errorText = sprintf "Neither rule nor file is found for '%s'" (target.Display ctx.Options.ProjectRoot)
            do ctx.Logger.Log Error "%s" errorText
            raise (XakeException errorText)

/// Executes a single target: its task (or the fast path), or the record of a plain file.
and execOne (ctx: ExecContext) target =
    match prepareOne ctx target with
    | Choice2Of2 task -> task
    | Choice1Of2 file -> async {
        do! observe ctx Recording [ file ]
        return target, ExecStatus.JustFile, FileDep file }

/// Executes multiple targets in parallel and collects their results.
/// The targets are requested in the order of their critical-path keys (equal keys as given); the
/// results come back in the order given, so the recorded dependencies do not depend on the keys.
/// Every target is resolved first (the rule, or the error for neither rule nor file); the plain
/// files are then recorded in one observation, next to the rule targets (docs/features/idle-perf).
and execParallel ctx targets = async {
    let order = targets |> List.indexed |> List.sortByDescending (fun (_, t) -> Scheduler.key ctx.Engine.Scheduler [t])
    let prepared = order |> List.map (fun (i, t) -> i, t, prepareOne ctx t)
    let plain = prepared |> List.choose (function i, t, Choice1Of2 f -> Some (i, t, f) | _ -> None)
    let plainBatch = async {
        if not plain.IsEmpty then do! observe ctx Recording (plain |> List.map (fun (_, _, f) -> f))
        return [| for i, t, f in plain -> i, (t, ExecStatus.JustFile, FileDep f) |] }
    let tasks = prepared |> List.choose (function i, _, Choice2Of2 task -> Some (async { let! r = task in return [| i, r |] }) | _ -> None)
    let! results = plainBatch :: tasks |> Async.Parallel
    return results |> Array.concat |> Array.sortBy fst |> Array.map snd }

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
                    | t :: _ -> sprintf "target '%s'" (t.Display ctx.Options.ProjectRoot)
                    | [] -> "the script"
                let resources = held |> List.map (sprintf "'%s'") |> String.concat ", "
                let errorText =
                    sprintf "need inside withResource: %s needs '%s' while it holds the resource %s. A dependency built under a held resource can deadlock the build. Move the need before withResource (or acquire the resource around the command only), or wrap it in allowNeed if the dependency never takes the resource."
                        needing (needed.Display ctx.Options.ProjectRoot) resources
                do ctx.Logger.Log Error "%s" errorText
                raise (XakeException errorText)

        let primaryTarget = ctx.Targets |> List.head
        primaryTarget |> (Progress.TaskSuspend >> ctx.Progress.Post)

        // the wait is known to the verdicts' cycle check (WaitGraph)
        let unregister = ctx.Counters.Waits.Need (ctx.Targets |> List.map (fun t -> t.Name)) (targets |> List.map (fun t -> t.Name))
        let! statuses =
            try Scheduler.withYieldedSlot ctx.Engine.Scheduler ctx.Targets (targets |> execParallel ctx)
            finally unregister ()

        primaryTarget |> (Progress.TaskResume >> ctx.Progress.Post)

        let dependencies = statuses |> Array.map (fun (_,_,x) -> x) |> List.ofArray in
        return
            (match statuses |> Array.exists (fun (_,x,_) -> x = ExecStatus.Succeed) with
                |true -> Succeed
                |false -> Skipped), dependencies
    }

/// The static `needs` of a header for one match (groups filled in): a pattern without wildcards
/// is one target, resolved as `need` resolves a name; with wildcards (or braces) it is a fileset of
/// the existing files, recorded as `GetFiles`. Returns the targets and the `GetFiles` entries.
and resolveNeeds (ctx: ExecContext) (groups: Map<string, string>) (patterns: string list) =
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
            let fileset = Fileset.ls (PatternAst.glob (fun name -> Map.tryFind name groups) (fun _ -> None) tokens)
            let (Filelist files) as list = fileset |> Fileset.toFileList ctx.Options.ProjectRoot
            files |> List.map FileTarget, [ GetFiles (fileset, list) ]
    let resolved = patterns |> List.map resolve
    (resolved |> List.collect fst |> List.distinct), (resolved |> List.collect snd)

/// The header's static `needs` of a task, requested together (before its verdict): the
/// dependencies recorded and the task's step, started before them, their time as its wait.
and private headerNeeds (ctx: ExecContext) (header: RuleHeader) : Async<Dependency list * StepInfo> = async {
    let step = Step.start "all"
    let! headerDeps = async {
        match resolveNeeds ctx ctx.RuleMatches header.Needs with
        | [], filesetDeps -> return filesetDeps
        | targets, filesetDeps ->
            let! _, deps = execNeed ctx targets
            return filesetDeps @ deps }
    return headerDeps, { step with WaitTime = Step.msSince step.Start } }

/// Acquires `units` of `resource`, yielding the CPU slot meanwhile; the time it took (the slot
/// taken back included), counted as the step's wait.
and acquireTimed (ctx: ExecContext) (resource: Resource) units : Async<int<ms>> = async {
    let started = System.DateTime.Now
    do! Scheduler.withYieldedSlot ctx.Engine.Scheduler ctx.Targets (Resource.acquireAt (Scheduler.key ctx.Engine.Scheduler ctx.Targets) resource units)
    return Step.msSince started }

/// A task's body (docs/rule-header.md) after its `needs`: its locks in `Locks` order, yielding the
/// CPU slot while waiting; then the body on a fresh result, holding them. The header's entries go
/// first (`StaticCount`), whatever the body records.
and private runBody (ctx: ExecContext) (header: RuleHeader) action targets (headerDeps: Dependency list, step: StepInfo) : Async<BuildResult> = async {
    let rec holding (start: BuildResult) (held: string list) rest = async {
        match rest with
        | [] -> return! action (start, { ctx with HeldResources = held @ ctx.HeldResources })
        | (resource: Resource, units) :: rest ->
            let! waited = acquireTimed ctx resource units
            try return! holding (Step.updateWaitTime waited start) (held @ [ resource.ResourceName ]) rest
            finally Resource.release resource units }
    let! (result, _) = holding { BuildLog.makeResult targets with Steps = [step] } [] header.Locks
    return { Step.updateTotalDuration result with
                Built = System.DateTime.Now; Depends = headerDeps @ result.Depends; StaticCount = headerDeps.Length } }

/// A task's work: the header's `needs`, then the body (a delegated rule's body thunk).
and private runHeaded (ctx: ExecContext) (header: RuleHeader) action targets : Async<BuildResult> = async {
    let! needs = headerNeeds ctx header
    return! runBody ctx header action targets needs }

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

/// The rule of a target now, for the verdict (M9): its outputs and static set; None without a rule.
let currentRule (ctx: ExecContext) target =
    match selectRule ctx target with
    | _, Some m ->
        let targets, filesetDeps = resolveNeeds ctx (Map.ofList m.Groups) (fst (ruleAction m.Rule)).Needs
        Some (m.Targets, staticKeys (filesetDeps @ List.map TargetDep targets))
    | _ -> None

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

/// The verdict of a task's targets (content cut-off): after its static `needs`, it waits for the
/// running dependees recorded by its last result (unless the wait would close a cycle), then
/// decides with `getDeps` (the run's memo) for the dependees.
let private verdict (ctx: ExecContext) (verdicts: Verdicts) (targets: Target list) = async {
    let names = targets |> List.map (fun t -> t.Name)
    let primary = List.head targets
    if ctx.Options.ContentCutoff then
        let last = Storage.tryGetResult ctx.Db primary
        let dependees = last |> Option.map (fun r -> r.Depends |> List.choose (function TargetDep t -> Some t | _ -> None)) |> Option.defaultValue []
        for d in dependees do
            match Scheduler.query ctx.Engine.Scheduler d with
            | Running task ->
                match ctx.Counters.Waits.Verdict names d.Name with
                | None ->
                    do ctx.Logger.Log Info "%s: not waiting for %s, which waits for it" (primary.Display ctx.Options.ProjectRoot) (d.Display ctx.Options.ProjectRoot)
                | Some (cancel, unregister) ->
                    try
                        do! Scheduler.withYieldedSlot ctx.Engine.Scheduler targets (async {
                                let! _ = System.Threading.Tasks.Task.WhenAny(Async.StartAsTask task :> System.Threading.Tasks.Task, cancel.Task :> System.Threading.Tasks.Task) |> Async.AwaitTask
                                return () })
                    finally unregister ()
                    if cancel.Task.IsCompleted then
                        do ctx.Logger.Log Info "%s: stopped waiting for %s, which waits for it" (primary.Display ctx.Options.ProjectRoot) (d.Display ctx.Options.ProjectRoot)
                    else
                        // a failed dependee fails the dependent, as its body would
                        do! task |> Async.Ignore
            | _ -> ()
    let rebuild (target: Target) = async {
        let! reasons = verdicts.Fresh target
        match reasons with
        | [] -> return false
        | reasons ->
            let reason =
                match reasons with
                | Other reason :: _ -> reason
                | Depends t :: _ -> "Depends on target " + t.Display ctx.Options.ProjectRoot
                | DependsMissingTarget t :: _ -> sprintf "Depends on target %s (missing)" (t.Display ctx.Options.ProjectRoot)
                | FilesChanged (file :: _) :: _ -> "File(s) changed " + file
                | reasons -> sprintf "Some reason %A" reasons
            do ctx.Logger.Log Info "Rebuild %A: %s" (target.Display ctx.Options.ProjectRoot) reason
            return true }
    let rec anyRebuild = function
        | [] -> async.Return false
        | t :: rest -> async { let! r = rebuild t in if r then return true else return! anyRebuild rest }
    return! anyRebuild targets }

/// Starts a run (B2): the pool's run boundary, the next epoch, the verdict memo of the run and
/// its table of spellings; the context's verdicts use the memo. Every target group of the command
/// line and every demand is a run.
let beginRun (ctx: ExecContext) = async {
    do Scheduler.newRun ctx.Engine.Scheduler
    let! epoch = ctx.Db.PostAndAsyncReply Storage.NewRun
    let ctx = { ctx with Spellings = newSpellings (); RunFiles = Storage.RunFiles (); RunEpoch = epoch }
    let verdicts = Verdicts(ctx, currentRule ctx)
    let upToDate t = async { let! reasons = verdicts.Of t in return List.isEmpty reasons }
    return { ctx with NeedRebuild = verdict ctx verdicts; UpToDate = upToDate }, verdicts }

/// Decides, in parallel, the verdicts the progress estimate and the critical path read (whatever
/// the schedule), then the critical-path keys of a run's targets, before any task starts; with
/// `AsRequested` none, so every key is 0.
let private planRun (ctx: ExecContext) (verdicts: Verdicts) targets = async {
    do! verdicts.Warm targets
    if ctx.Options.Schedule = CriticalPath then
        criticalPath verdicts.Estimate targets |> Scheduler.setKeys ctx.Engine.Scheduler }

/// Runs the full build pipeline for the given target groups.
/// Each group is executed sequentially; targets within a group run in parallel.
/// Returns the combined list of target/status/dependency results.
let runBuild (ctx: ExecContext) groups =
    let options = ctx.Options

    let count = List.length groups
    // each group is estimated when it starts: an earlier one can change what it finds (`clean`)
    let runTargets ctx index (names: string list) =
        async {
            let targets = names |> List.map (makeTarget ctx)
            do ctx.Logger.Log Info "Build target list %A" targets
            let name = String.concat ";" names
            let label = if count > 1 then sprintf "[%d/%d %s] " (index + 1) count name else ""
            let started = System.DateTime.Now

            // the change analysis is memoized for the whole group, so the group is one run
            let! ctx, verdicts = beginRun ctx
            do! planRun ctx verdicts targets
            let progressSink = Progress.openProgress verdicts.Estimate options.Threads targets label ctx.ShowProgress
            let stepCtx = {ctx with Progress = progressSink}

            try
                let! results = targets |> execParallel stepCtx
                if count > 1 then
                    do ctx.Logger.Log Message "group '%s' done in %.1f s" name (System.DateTime.Now - started).TotalSeconds
                return results
            finally
                do Progress.Finish |> progressSink.Post
        }

    groups |> List.mapi (runTargets ctx)
    |> runSeq
    |> asyncMap (Array.concat >> List.ofArray)

/// Creates the shared execution context and returns it together with a finalize callback.
/// The finalize callback closes the database, disposes the scheduler, and flushes logs.
let createContextCore (options: EngineOptions) (db: Storage.Db) vars showProgress =
    // the root every path is shown and stored against, normalised once (A6, R3)
    let options = { options with ProjectRoot = (File.make options.ProjectRoot).Name }
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
        NeedRebuild = fun _ -> async.Return false
        UpToDate = fun _ -> async.Return false
        Targets = []
        RuleMatches = Map.empty
        Vars = vars
        ShowProgress = showProgress
        HeldResources = []
        Spellings = newSpellings ()
        RunFiles = Storage.RunFiles ()
        RunEpoch = 0
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
        let! ctx, verdicts = beginRun ctx
        do! planRun ctx verdicts [target]
        let progressSink = Progress.openProgress verdicts.Estimate ctx.Engine.Options.Threads [target] "" ctx.ShowProgress
        let stepCtx = {ctx with Progress = progressSink}
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

    let! result = getResult()
    let result' = {result with Depends = result.Depends @ deps} |> (Step.updateWaitTime (Step.msSince startTime))
    do! setResult result'
}
