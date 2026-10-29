module internal Xake.ScriptRunner

open DependencyAnalysis

/// Converts a value to the type of a declaration (the 3.13 conversion); the error text of a
/// secret does not show the value.
let convertVar (decl: VarDecl) (value: string) : Result<obj, string> =
    try Ok (System.Convert.ChangeType(value, decl.ValueType))
    with
    | :? System.FormatException | :? System.InvalidCastException | :? System.OverflowException ->
        let typeName = decl.ValueType.FullName
        if VarDecl.isSecret decl then Result.Error (sprintf "Failed to convert the value of secret variable '%s' to type '%s'." decl.Key typeName)
        else Result.Error (sprintf "Failed to convert value '%s' for variable '%s' to type '%s'." value decl.Key typeName)

let private secretGiven (name: string) (where: string) =
    sprintf "Variable '%s' is secret: its value comes only from its declaration's sources, not from %s" name where

let private envOnlyGiven (name: string) (env: string) (where: string) =
    sprintf "Variable '%s' comes only from the environment variable %s, not from %s" name env where

let private isSecretEntry (name: string) (entry: VarEntry) =
    Secrets.isSecret name || entry.Decl |> Option.exists VarDecl.isSecret

/// Builds the variable set at launch: evaluates every declaration once (the environment is
/// read here and nowhere else), overlays `-d` and the script's values (first defined wins:
/// `-d` > the script's value > the declaration's sources), and checks second definitions,
/// secrets, scopes, required fields and typed values. Secret names are declared and their
/// values registered for masking before anything is logged. Returns the set and the errors.
let launchVariables (opts: ExecOptions) : Map<string, VarEntry> * string list =
    let decls = opts.VarDecls
    for d in decls do
        if VarDecl.isSecret d then
            Secrets.declare d.Key d.Secret
            VarDecl.envNames d |> List.iter (fun e -> Secrets.declare e d.Secret)
    for (name, value) in opts.CliVars @ opts.Vars do
        if Secrets.isSecret name then SecretMask.register value

    let errors = ResizeArray<string>()

    // one script definition per key: the ExecOptions.Vars entries first, then the ops in script order
    let named key = List.filter (fst >> (=) key)
    for key in (opts.Vars |> List.map fst) @ (opts.VarDefinedBy |> List.map fst) |> List.distinct do
        let byOps = opts.VarDefinedBy |> named key |> List.map snd
        let constants = byOps |> List.filter ((=) (VarDecl.constantLabel key)) |> List.length
        let fromOptions = (opts.Vars |> named key |> List.length) - constants
        match List.replicate (max 0 fromOptions) "ExecOptions.Vars" @ byOps with
        | a :: b :: _ -> errors.Add (sprintf "Variable '%s' is defined twice in the script: by %s and by %s; define it once" key a b)
        | _ -> ()

    // a secret takes nothing from -d, neither under its name nor under an env name of its sources
    for d in decls do
        if VarDecl.isSecret d then
            match VarDecl.envNames d with
            | env :: _ as envs ->
                for given in d.Key :: envs |> List.distinct do
                    if Util.getVar opts.CliVars given |> Option.isSome then errors.Add (Secrets.fromEnvOnly given env)
            | [] ->
                if Util.getVar opts.CliVars d.Key |> Option.isSome then errors.Add (secretGiven d.Key (sprintf "-d %s=..." d.Key))

    let declared = decls |> List.rev |> List.map (fun d -> d.Key, d) |> Map.ofList     // the first declaration of a key
    let ownValue (d: VarDecl) =
        d.Sources |> List.tryPick (function
            | Env e -> Util.getEnvVar e |> Option.map (fun v -> v, VarOrigin.Env e)
            | Default v -> Some (v, VarOrigin.Default)
            | Secret | SecretHashed -> None)
    let keys = (opts.CliVars |> List.map fst) @ (opts.Vars |> List.map fst) @ (decls |> List.map (fun d -> d.Key)) |> List.distinct
    let variables =
        [ for key in keys do
            let decl = Map.tryFind key declared
            let cli = Util.getVar opts.CliVars key
            let value =
                match decl, cli with
                | Some d, _ when VarDecl.isSecret d -> ownValue d       // -d refused above
                | Some d, Some _ when d.Scope = EnvOnly ->
                    let env = VarDecl.envNames d |> List.tryHead |> Option.defaultValue key
                    errors.Add (envOnlyGiven key env (sprintf "-d %s=..." key))
                    ownValue d
                | _, Some v -> Some (v, VarOrigin.Cli)
                | _, None ->
                    match Util.getVar opts.Vars key, decl with
                    | Some v, _ -> Some (v, VarOrigin.Script)
                    | None, Some d -> ownValue d
                    | None, None -> None
            match decl, value with
            | Some d, None when d.Required -> errors.Add (sprintf "Required variable '%s' is not provided" key)
            | Some d, Some (v, origin) when d.ValueType <> typeof<string> && origin <> VarOrigin.Default ->
                match convertVar d v with
                | Result.Error e -> errors.Add e
                | Ok _ -> ()
            | _ -> ()
            let entry = { Value = Option.map fst value; Origin = Option.map snd value; Decl = decl }
            if isSecretEntry key entry then entry.Value |> Option.iter SecretMask.register
            yield key, entry ]
        |> Map.ofList
    variables, List.ofSeq errors

/// The variable set flattened to the context's `Vars`: one pair per key that has a value.
let flatVars (variables: Map<string, VarEntry>) =
    [ for KeyValue (name, entry) in variables do
        match entry.Value with
        | Some value -> yield name, value
        | None -> () ]

/// Checks the engine's per-demand `?vars` against the declarations: a secret or an
/// environment-only variable takes no request value, a typed one must convert. Returns the errors.
let checkRequestVars (variables: Map<string, VarEntry>) (pairs: (string * string) list) =
    let where = "the ?vars of Demand"
    [ for (name, value) in pairs do
        match variables |> Map.tryFind name |> Option.bind (fun e -> e.Decl) with
        | Some d when VarDecl.isSecret d ->
            SecretMask.register value
            yield secretGiven name where
        | Some d when d.Scope = EnvOnly ->
            yield envOnlyGiven name (VarDecl.envNames d |> List.tryHead |> Option.defaultValue name) where
        | Some d when d.ValueType <> typeof<string> ->
            match convertVar d value with
            | Result.Error e -> yield e
            | Ok _ -> ()
        | _ -> () ]

/// Converts CLI ExecOptions into engine-level EngineOptions, constructing the logger.
let toEngineOptions (opts: ExecOptions) (rules: Rules<ExecContext>) (variables: Map<string, VarEntry>) : EngineOptions =
    let logger = CombineLogger (ConsoleLogger opts.ConLogLevel) opts.CustomLogger
    let logger =
        match opts.FileLog, opts.FileLogLevel with
        | null,_ | "",_
        | _, Silent -> logger
        | logFileName,level -> CombineLogger logger (FileLogger logFileName level)
    { ProjectRoot = opts.ProjectRoot; Threads = opts.Threads
      Logger = MaskingLogger logger; Teardown = opts.Teardown; Rules = rules; RuleMatch = opts.RuleMatch
      Variables = variables }

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
    let variables, launchErrors = launchVariables opts
    let engineOpts = toEngineOptions opts rules variables
    let db =
        if opts.NoPersist then
            Storage.noopDb ()
        else
            let dbPath = engineOpts.ProjectRoot </> opts.DbFileName
            if opts.ResetDb then
                Storage.cleanupDb dbPath engineOpts.Logger
            Storage.openDb dbPath engineOpts.Logger
    let ctx, finalize = ExecCore.createContextCore engineOpts db (flatVars variables) opts.Progress
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

let private originText = function
    | VarOrigin.Request -> "request"
    | VarOrigin.Cli -> "cli"
    | VarOrigin.Script -> "script"
    | VarOrigin.Env name -> "env:" + name
    | VarOrigin.Default -> "default"

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
    let vars =
        [ for KeyValue (name, entry) in variables do
            match entry.Value, entry.Origin with
            | Some value, Some origin ->
                let shown = if isSecretEntry name entry || SecretMask.apply value <> value then "***" else value
                yield sprintf "    %s=%s (%s)" name shown (originText origin)
            | _ -> yield sprintf "    %s (not set)" name ]
    [ yield sprintf "Xake %s" version
      yield sprintf "  mode:         %s" mode
      yield sprintf "  targets:      %s" targets
      yield sprintf "  project root: %s" options.ProjectRoot
      yield sprintf "  database:     %s" db
      yield sprintf "  threads:      %d" options.Threads
      yield sprintf "  rule match:   %A" options.RuleMatch
      yield sprintf "  console log:  %A" options.ConLogLevel
      yield sprintf "  file log:     %s" fileLog
      yield sprintf "  setup:        %s" (list options.Setup)
      yield sprintf "  teardown:     %s" (list options.Teardown)
      if List.isEmpty vars then yield "  vars:         (none)"
      else
        yield "  vars:"
        yield! vars ]

/// Performs a dry run: analyzes dependencies and logs what would be rebuilt without executing anything.
/// Displays estimated build time and parallelism degree.
let dryRun (ctx: ExecContext) (setup: string list) (groups: string list list) =
    let options = ctx.Options
    let getDeps = getChangeReasons ctx |> memoizeRec

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
    let hideSecretDefaults = List.map (fun (name, help: VarHelp) -> name, if help.IsSecret then { help with DefaultStr = None } else help)
    let hideValues = List.map (fun (name, value) -> name, if Secrets.isSecret name then "***" else value)
    let hideDecl (d: VarDecl) =
        if VarDecl.isSecret d then { d with Sources = d.Sources |> List.filter (function Default _ -> false | _ -> true); DefaultValue = None }
        else d
    logger.Log Level.Debug "Options: %A" { options with Vars = hideValues options.Vars; CliVars = hideValues options.CliVars
                                                        VarDecls = List.map hideDecl options.VarDecls
                                                        VarSchema = hideSecretDefaults options.VarSchema }
    logger.Log Level.Debug "Variables: %A"
        (ctx.Options.Variables |> Map.map (fun name entry ->
            { entry with Value = if isSecretEntry name entry then Option.map (fun _ -> "***") entry.Value else entry.Value
                         Decl = Option.map hideDecl entry.Decl }))

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
            do logger.Log Level.Command "Dumping dependencies for targets %A" targetLists
            targetLists |> List.iter (List.map (ExecCore.makeTarget ctx) >> (dumpDeps ctx))
        | Dryrun ->
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
