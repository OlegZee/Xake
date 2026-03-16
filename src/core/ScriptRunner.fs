module internal Xake.ScriptRunner

open DependencyAnalysis

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

/// Active pattern that classifies ExecOptions into Dump, Dryrun, or Run execution mode.
let (|Dump|Dryrun|Run|) (opts:ExecOptions) =
    match opts with
    | _ when opts.DumpDeps -> Dump
    | _ when opts.DryRun -> Dryrun
    | _ -> Run

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
    ExecCore.createContextCore engineOpts db opts.Vars opts.Progress

let runTeardownAsync (ctx: ExecContext) =
    async {
        for targetName in ctx.Engine.Options.Teardown do
            do! ExecCore.demandTarget ctx targetName |> Async.Ignore
    }

/// Performs a dry run: analyzes dependencies and logs what would be rebuilt without executing anything.
/// Displays estimated build time and parallelism degree.
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
    let ctx, finalize = createScriptContext options rules
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

    logger.Log Level.Debug "Options: %A" { options with Vars = [] }
        // be careful with debug option as it may contain sensitive info like script variables

    let targetLists =
        options.Targets |>
        function
        | [] ->
            do logger.Log Level.Message "No target(s) specified. Defaulting to 'main'"
            [["main"]]
        | tt ->
            tt |> List.map (fun (s: string) -> s.Split(';', '|') |> List.ofArray)

    let mutable exitCode = 0
    try
        match options with
        | Dump ->
            do logger.Log Level.Command "Dumping dependencies for targets %A" targetLists
            targetLists |> List.iter (List.map (ExecCore.makeTarget ctx) >> (dumpDeps ctx))
        | Dryrun ->
            targetLists |> dryRun ctx
        | _ ->
            let start = System.DateTime.Now
            let mutable reraisedError = None
            try
                targetLists |> ExecCore.runBuild ctx |> fun a -> Async.RunSynchronously(a, cancellationToken = cts.Token) |> ignore
                ctx.Logger.Log Message "\n\n    Build completed in %A\n" (System.DateTime.Now - start)
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
