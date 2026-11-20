module internal Xake.ScriptRunner

open DependencyAnalysis

/// Converts CLI ExecOptions into engine-level EngineOptions, constructing the logger.
/// The scheduling mode as the banner and the run summary name it.
let scheduleName = function CriticalPath -> "critical-path" | AsRequested -> "as-requested"

let toEngineOptions (opts: ExecOptions) (rules: Rules<ExecContext>) (variables: Map<string, VarEntry>) : EngineOptions =
    let logger = CombineLogger (ConsoleLogger opts.ConLogLevel) opts.CustomLogger
    let logger =
        match opts.FileLog, opts.FileLogLevel with
        | null,_ | "",_
        | _, Silent -> logger
        | logFileName,level -> CombineLogger logger (FileLogger logFileName level)
    { ProjectRoot = opts.ProjectRoot; Threads = opts.Threads
      Logger = MaskingLogger logger; Teardown = opts.Teardown; Rules = rules; RuleMatch = opts.RuleMatch
      Variables = variables; ContentCutoff = opts.ContentCutoff; Schedule = opts.Schedule; SummarySink = opts.SummarySink }

/// Active pattern that classifies ExecOptions into Dump, Dryrun, or Run execution mode.
let (|Dump|Dryrun|Run|) (opts:ExecOptions) =
    match opts with
    | _ when opts.DumpDeps -> Dump
    | _ when opts.DryRun -> Dryrun
    | _ -> Run

/// Creates execution context from CLI ExecOptions, handling ResetDb and logger construction.
/// This is the launch (run, dry-run, dump and engine `Start`): the variable set is built here,
/// once, and the launch errors are returned with the context; the caller reports them.
let createScriptContext (opts: ExecOptions) rules =
    let variables, launchErrors = VarEvaluation.launch opts
    let engineOpts = toEngineOptions opts rules variables
    let db =
        if opts.NoPersist then
            Storage.noopDb ()
        else
            let dbPath = engineOpts.ProjectRoot </> opts.DbFileName
            // the lock first: --resetdb runs under it (A5)
            Storage.openDbWith (System.TimeSpan.FromSeconds 30.0) opts.ResetDb engineOpts.ProjectRoot dbPath engineOpts.Logger
    let ctx, finalize = ExecCore.createContextCore engineOpts db (VarEvaluation.flatVars variables) opts.Progress
    ctx, finalize, launchErrors

let runTeardownAsync (ctx: ExecContext) =
    async {
        for targetName in ctx.Engine.Options.Teardown do
            do! ExecCore.demandTarget ctx targetName |> Async.Ignore
    }

/// Runs the setup targets sequentially, in the order given, through the normal demand path.
let runSetupAsync (ctx: ExecContext) (setup: string list) =
    async {
        for targetName in setup do
            do! ExecCore.demandTarget ctx targetName |> Async.Ignore
    }

/// The compact run banner, one line per item in a stable order. The variables are listed once
/// each, sorted, with their source; values of secret variables are shown as `***`.
let bannerLines (options: ExecOptions) (variables: Map<string, VarEntry>) (targetLists: string list list) =
    let version =
        let asm = typeof<ExecOptions>.Assembly
        match asm.GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false) with
        | [| :? System.Reflection.AssemblyInformationalVersionAttribute as a |] -> a.InformationalVersion
        | _ -> asm.GetName().Version.ToString()
    let mode =
        match options with
        | Dump -> "dump"
        | Dryrun -> "dry-run"
        | _ -> "run"
    let list = function [] -> "(none)" | xs -> String.concat ", " xs
    let targets =
        if List.isEmpty options.Targets then "main (default)"
        else targetLists |> List.map (String.concat "|") |> String.concat ", "
    let db = if options.NoPersist then "(no persist)" else options.ProjectRoot </> options.DbFileName
    let fileLog =
        match options.FileLog, options.FileLogLevel with
        | null,_ | "",_ | _, Silent -> "(off)"
        | name, level -> sprintf "%s (%A)" (System.IO.Path.GetFullPath(options.ProjectRoot </> name)) level
    let vars = VarEvaluation.bannerLines variables
    [ yield sprintf "Xake %s" version
      yield sprintf "  mode:         %s" mode
      yield sprintf "  targets:      %s" targets
      yield sprintf "  project root: %s" options.ProjectRoot
      yield sprintf "  database:     %s" db
      yield sprintf "  threads:      %d" options.Threads
      yield sprintf "  rule match:   %A" options.RuleMatch
      yield sprintf "  schedule:     %s" (scheduleName options.Schedule)
      yield sprintf "  console log:  %A" options.ConLogLevel
      yield sprintf "  file log:     %s" fileLog
      yield sprintf "  setup:        %s" (list options.Setup)
      yield sprintf "  teardown:     %s" (list options.Teardown)
      if options.IgnoreEnvironment then yield "  environment:  ignored (IgnoreEnvironment)"
      if List.isEmpty vars then yield "  vars:         (none)"
      else
        yield "  vars:"
        yield! vars ]

/// Performs a dry run: analyzes dependencies and logs what would be rebuilt without executing anything.
/// Displays estimated build time and parallelism degree.
let dryRun (ctx: ExecContext) (setup: string list) (groups: string list list) =
    let options = ctx.Options
    let getDeps = getChangeReasons ctx (ExecCore.currentRule ctx) |> memoizeRec

    // getPlainDeps getDeps (getExecTime ctx)
    if not setup.IsEmpty then
        do ctx.Logger.Log Command "Setup targets (not run in dry mode) %A" setup
    do ctx.Logger.Log Command "Running (dry) targets %A" groups
    let doneTargets = System.Collections.Hashtable()

    let print f = ctx.Logger.Log Info f
    let indent i = String.replicate i "  "

    let rec showDepStatus ii reasons =
        reasons |> function
        | Other reason ->
            print "%sReason: %s" (indent ii) reason
        | Depends t ->
            print "%sDepends '%s' - changed target" (indent ii) (t.Display options.ProjectRoot)
        | DependsMissingTarget t ->
            print "%sDepends on '%s' - missing target" (indent ii) (t.Display options.ProjectRoot)
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
                do ctx.Logger.Log Command "%sRebuild %A (~%Ams)" (indent ii) (target.Display options.ProjectRoot) execTimeEstimate
                deps |> List.iter (showDepStatus (ii+1))
                deps |> List.iter (displayNestedDeps (ii+1))

    let targetGroups = ExecCore.makeTarget ctx |> List.map |> List.map <| groups
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

/// Main entry point for running a build script.
/// Parses target lists, sets up cancellation handling, and dispatches to dump, dry-run, or full build mode.
let runScript options rules =
    let ctx, finalize, launchErrors = createScriptContext options rules
    let logger = ctx.Logger
    let cts = new System.Threading.CancellationTokenSource()

    // Only cancel the token — do not touch teardown or call exit here.
    // The main thread observes the cancellation via Async.RunSynchronously,
    // exits the build loop cleanly, then runs teardown before exiting.
    // Double Ctrl+C support: first press cancels gracefully, second press allows OS termination.
    let mutable cancelledOnce = false
    let handler =
        new System.ConsoleCancelEventHandler(fun _ args ->
            if not cancelledOnce then
                args.Cancel <- true  // suppress default process termination
                cancelledOnce <- true
                logger.Log Error "Build interrupted by user. Press Ctrl+C again to force exit"
                cts.Cancel()
            else
                args.Cancel <- false  // allow default termination on second press
                logger.Log Error "Force exiting build process...")
    System.Console.CancelKeyPress.AddHandler(handler)

    // the values and defaults of secret variables are hidden; the masking logger hides registered values
    logger.Log Level.Debug "Options: %A" (VarEvaluation.maskOptions options)
    logger.Log Level.Debug "Variables: %A" (VarEvaluation.maskVariables ctx.Options.Variables)

    let targetLists =
        options.Targets |>
        function
        | [] ->
            do logger.Log Level.Message "No target(s) specified. Defaulting to 'main'"
            [["main"]]
        | tt ->
            tt |> List.map (fun (s: string) -> s.Split(';', '|') |> List.ofArray)

    let mutable exitCode = 0
    bannerLines options ctx.Options.Variables targetLists |> List.iter (logger.Log Info "%s")
    try
        match options with
        | _ when not launchErrors.IsEmpty ->
            let errorText = launchErrors |> String.concat "\r\n"
            do logger.Log Error "%s" errorText
            exitCode <- 2
            if options.ThrowOnError then raise (XakeException errorText)
        | Dump ->
            Storage.InMemory |> ctx.Db.Post
            ctx.Db.PostAndReply Storage.NewRun |> ignore
            do logger.Log Level.Command "Dumping dependencies for targets %A" targetLists
            targetLists |> List.iter (List.map (ExecCore.makeTarget ctx) >> (dumpDeps ctx (ExecCore.currentRule ctx)))
        | Dryrun ->
            Storage.InMemory |> ctx.Db.Post
            ctx.Db.PostAndReply Storage.NewRun |> ignore
            targetLists |> dryRun ctx options.Setup
        | _ ->
            let mutable reraisedError = None
            let setupOk =
                try
                    runSetupAsync ctx options.Setup |> fun a -> Async.RunSynchronously(a, cancellationToken = cts.Token)
                    true
                with
                | :? System.OperationCanceledException ->
                    ctx.Logger.Log Message "\n\n\tBuild interrupted during setup\n"
                    exitCode <- 1
                    false
                | exn ->
                    let errorText = exn |> ExecCore.unwindAggEx |> Seq.map (fun e -> e.Message) |> String.concat "\r\n"
                    ctx.Logger.Log Error "Setup failed: %s. The build is skipped" errorText
                    ctx.Logger.Log Verbose "Setup error details are:\n%A\n\n" exn
                    if options.ThrowOnError then
                        reraisedError <- Some (XakeException (sprintf "Setup failure: %s" errorText))
                    exitCode <- 2
                    false
            if setupOk then
                let start = System.DateTime.Now
                // one summary per script run (docs/content-cutoff.md), also when the build fails
                let reportSummary () =
                    let summary = ctx.Counters.Summary
                    ctx.Logger.Log Message "%s, schedule %s" (RunSummary.format summary) (scheduleName options.Schedule)
                    ctx.Options.SummarySink |> Option.iter (fun sink -> sink.Report summary)
                try
                  try
                    targetLists |> ExecCore.runBuild ctx |> fun a -> Async.RunSynchronously(a, cancellationToken = cts.Token) |> ignore
                    ctx.Logger.Log Message "\n\n    Build completed in %A\n" (System.DateTime.Now - start)
                  finally
                    try reportSummary () with ex -> ctx.Logger.Log Error "The run summary failed: %s" ex.Message
                with
                | :? System.OperationCanceledException ->
                    // Ctrl+C: build async workflow was cancelled; fall through to teardown on main thread
                    ctx.Logger.Log Message "\n\n\tBuild interrupted after running for %A\n" (System.DateTime.Now - start)
                    exitCode <- 1
                | exn ->
                    let exceptions = exn |> ExecCore.unwindAggEx
                    let errors = exceptions |> Seq.map (fun e -> e.Message) in
                    let details = exceptions |> Seq.last |> fun e -> e.ToString()
                    let errorText = errors |> String.concat "\r\n"

                    do ctx.Logger.Log Error "Error '%s'. See build.log for more details" errorText
                    do ctx.Logger.Log Verbose "Error details are:\n%A\n\n" details
                    do ctx.Logger.Log Message "\n\n\tBuild failed after running for %A\n" (System.DateTime.Now - start)

                    if options.ThrowOnError then
                        reraisedError <- Some (XakeException errorText)
                    exitCode <- 2

            try
                ctx |> runTeardownAsync |> Async.RunSynchronously
            with teardownExn ->
                ctx.Logger.Log Error "Teardown failed: %s" teardownExn.Message
                ctx.Logger.Log Verbose "Teardown error details are:\n%A\n\n" teardownExn
                if exitCode = 0 then
                    // build succeeded but teardown failed — teardown is the only failure
                    exitCode <- 2
                    if options.ThrowOnError then
                        reraisedError <- Some (XakeException (sprintf "Teardown failure: %s" teardownExn.Message))
                // else: build already failed — log teardown failure but preserve the
                // original build error in reraisedError and exitCode so it is reported

            match reraisedError with
            | Some exn -> raise exn
            | None -> ()
    finally
        System.Console.CancelKeyPress.RemoveHandler(handler)
        cts.Dispose()
        finalize()
    if exitCode <> 0 then exit exitCode
