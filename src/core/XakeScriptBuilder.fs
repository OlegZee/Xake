namespace Xake

module ParseArgs =

    type 't ParseMode =
        | TopLevel
        | Number of string*('t -> int -> 't)
        | String of string*('t -> string -> 't)
        | KeyValue of string*('t -> string -> string -> 't)

    let getArgs = System.Environment.GetCommandLineArgs >> List.ofArray

    let rec getScriptArgs = function
    | [] -> []
    | "--" :: rest -> rest
    | _ :: tail -> getScriptArgs tail

    let parseTopLevel (arg:string) optionsSoFar = 
        match arg.ToLowerInvariant() with 

        | "-h" | "/h" | "--help" | "/help" | "/?" | "-?" ->
            printf """
Usage:
 dotnet fsi <script file> [-- -- options target..]

Options:
  -h                  - displays help screen
  -t <task count>     - parallelize execution to <task count> processes. Defaults to CPU cores
  -r <root path>      - override the root path. Default is current directory
  -ll <log level>     - console log level (Silent | Quiet | Normal | Loud | Chatty | Diag)
  -fl <file log path> - specifies the name of the log file
  -fll <log level>    - specifies the logging level to a log file
  target1 .. targetN  - define the list of targets to be executed sequentially
  target1;target2;..targetN -- execute the targets simultaneously
  -d <name>=<value>   - defines a script variable value
  --dryrun            - prints dependency graph and estimates the build duration
  --dump              - prints dependency graph based on the last run
  --resetdb           - reset the database before starting the build
  --progress, -p      - display progress indicator
  --noprogress, -p-   - do not display progress

            """
            { optionsSoFar with ShowHelp = true; Nologo = true }, TopLevel
        | "-t" | "/t" -> 
            optionsSoFar, Number ("thread count", fun o v -> {o with ExecOptions.Threads = v})
        | "-r" | "/r" -> 
            optionsSoFar, String ("root folder", fun o v -> {o with ExecOptions.ProjectRoot = v})
        | "-fl" | "/fl" -> 
            optionsSoFar, String ("file log filename", fun o v -> {o with ExecOptions.FileLog = v})
        | "-d" | "/d" -> 
            optionsSoFar, KeyValue ("variable", fun o k v -> {o with Vars = (k,v) :: o.Vars })  // last -d first: getVar is first match
        | "-ll" | "/ll" -> 
            optionsSoFar, String ("console verbosity", fun o s -> {o with ConLogLevel = s |> parseVerbosity })
        | "-fll" | "/fll" -> 
            optionsSoFar, String ("filelog verbosity", fun o s -> {o with FileLogLevel = s |> parseVerbosity })
        | "-nologo" -> 
            {optionsSoFar with Nologo = true}, TopLevel
        | "--dryrun" | "--dry-run" -> 
            {optionsSoFar with DryRun = true}, TopLevel
        | "--dump" -> 
            {optionsSoFar with DumpDeps = true}, TopLevel
        | "--resetdb" -> 
            {optionsSoFar with ResetDb = true}, TopLevel
        | "--progress" | "-p" -> 
            {optionsSoFar with Progress = true}, TopLevel
        | "--noprogress" | "-p-" -> 
            {optionsSoFar with Progress = false}, TopLevel

        | x when x.StartsWith("-") || x.StartsWith("/") ->
            printfn "Option '%s' is unrecognized" x
            optionsSoFar, TopLevel
        | _ -> 
            {optionsSoFar with Targets = optionsSoFar.Targets @ [arg]}, TopLevel

    let (|NumberVar|_|) str =
        match System.Int32.TryParse str with
        | (true, n) -> Some (NumberVar n)
        | _ -> None

    let readNumber name arg optionsSoFar fn = 
        match arg with
        | NumberVar v -> (fn optionsSoFar v, TopLevel)
        | _ -> 
            printfn "%s needs a second argument" name
            optionsSoFar, TopLevel

    let readString name arg optionsSoFar fn =
        fn optionsSoFar arg, TopLevel

    let readKeyValue name (arg:string) optionsSoFar fn =
        let k,v =
            match arg.IndexOfAny([|':'; '='|], 1) with
            | -1 -> arg,""
            | n -> arg.Substring(0, n), arg.Substring(n+1)

        fn optionsSoFar k v, TopLevel

    let foldFunction state element =
        try
            match state with
            | optionsSoFar, TopLevel ->
                parseTopLevel element optionsSoFar
            | optionsSoFar, Number (name, fn) ->
                readNumber name element optionsSoFar fn
            | optionsSoFar, String (name, fn) ->
                readString name element optionsSoFar fn
            | optionsSoFar, KeyValue (name, fn) ->
                readKeyValue name element optionsSoFar fn
        with e ->
            let argName = match state with | _, Number (name, _) | _, KeyValue (name, _) | _, String (name, _) -> name | _ -> "switch"
            printfn "Failed to parse '%s' due to %s" argName e.Message
            fst state, TopLevel

[<AutoOpen>]
module XakeScriptBuilder =

    open System.Collections.Concurrent
    open System.Threading.Tasks

    /// The `--help` lines of `var "N" [...]` rules: name, env names, the first default; a secret
    /// is marked "(secret)" and shows no default. Constant `var` ops are not listed.
    let private ruleHelpLines (decls: VarDecl list) =
        [ for d in decls do
            if d.DeclaredBy.StartsWith "var " then
                let envs = d.Sources |> List.choose (function Env e -> Some e | _ -> None)
                let secret = if d.Secret <> NotSecret then " (secret)" else ""
                let envStr = if envs.IsEmpty then "" else ", env: " + String.concat ", " envs
                let defaultStr =
                    match d.Secret, d.Sources |> List.tryPick (function Default v -> Some v | _ -> None) with
                    | NotSecret, Some v -> sprintf ", default: %s" v
                    | _ -> ""
                yield sprintf "  -d %s=<value> [string]%s%s%s" d.Key secret envStr defaultStr ]

    /// The "Script variables" section of --help: the `varschema` fields (names, types, env names,
    /// defaults and descriptions), then the `var "N" [...]` rules; a secret variable is marked
    /// "(secret)" and shows no default.
    let private formatHelpLines (schema: (string * VarHelp) list) (decls: VarDecl list) =
        let rules = ruleHelpLines decls
        if schema = [] && rules = [] then []
        else
            "\nScript variables:" ::
            [ for (name, help) in schema do
                let header =
                    match help.Scope with
                    | EnvOnly ->
                        let envName = help.EnvVarName |> Option.defaultValue name
                        sprintf "  $%s [%s]" envName help.TypeName
                    | _ ->
                        sprintf "  -d %s=<value> [%s]" name help.TypeName
                let required = if help.IsRequired then " (required)" else ""
                let secret = if help.IsSecret then " (secret)" else ""
                let envStr =
                    match help.Scope with
                    | EnvOnly -> ""  // env name already shown in header
                    | _ -> help.EnvVarName |> Option.map (sprintf ", env: %s") |> Option.defaultValue ""
                let defaultStr =
                    if help.IsSecret then ""
                    else help.DefaultStr |> Option.map (sprintf ", default: %s") |> Option.defaultValue ""
                let descStr = help.Description |> Option.map (sprintf " — %s") |> Option.defaultValue ""
                yield sprintf "%s%s%s%s%s%s" header required secret envStr defaultStr descStr
              yield! rules ]

    /// The "Script variables" section of --help for a `varschema` alone.
    let internal formatVarsHelp (schema: (string * VarHelp) list) = formatHelpLines schema []

    /// The "Script variables" section of --help for the script's options. It is built from the
    /// declarations only: nothing is evaluated, no environment variable is read.
    let internal formatHelp (options: ExecOptions) = formatHelpLines options.VarSchema options.VarDecls

    let private printVarsHelp (options: ExecOptions) =
        formatHelp options |> List.iter (printfn "%s")

    /// Script builder; `args` are the command line (None: not from xakeArgs, no logo either).
    type RulesBuilder internal (options: ExecOptions, args: string list option) =

        let updRules (XakeScript (options, rules)) f = XakeScript (options, f(rules))
        let updTargets (XakeScript (options, rules)) f = XakeScript ({options with Targets = f(options.Targets)}, rules)
        let addRule rule (Rules rules) :Rules<_> =    Rules (rule :: rules)

        new (options: ExecOptions) = RulesBuilder(options, None)

        /// Applies the command line over the script's options: only the fields whose flags are given
        /// change (`-d` is prepended, so it wins over `var`); command-line targets replace `want`'s.
        member internal __.ApplyCommandLine(XakeScript (options, rules) as script) =
            match args with
            | None -> script
            | Some args ->
                let options =
                    if options.IgnoreCommandLine then options
                    else
                        let cli = args |> List.fold ParseArgs.foldFunction ({ options with Targets = [] }, ParseArgs.TopLevel) |> fst
                        if cli.Targets = [] then { cli with Targets = options.Targets } else cli
                if not options.Nologo then
                    printf "XAKE build tool %s\n\n" (typeof<ExecOptions>.Assembly.GetName().Version.ToString())
                XakeScript (options, rules)

        member __.Zero() = XakeScript (options, Rules [])
        member this.Yield(()) = this.Zero()

        // --- flat script body: rules may be written directly in the script, no `rules [..]` needed ---

        /// Yields a single rule into the script body.
        member __.Yield(rule: ExecContext Rule) = XakeScript (options, Rules [rule])

        /// Yields a list of rules into the script body (`yield! rules` or a bare list).
        member __.Yield(rules: ExecContext Rule list) =
            XakeScript (options, Rules (List.rev rules))

        member this.YieldFrom(rules: #seq<ExecContext Rule>) =
            this.Yield(rules |> List.ofSeq)

        /// Rules accumulate in reverse declaration order (last matching rule wins),
        /// which is what the `rules [..]` operation has always done.
        member __.Combine(XakeScript (opts, Rules head), XakeScript (_, Rules tail)) =
            XakeScript (opts, Rules (tail @ head))

        member __.Delay(f: unit -> XakeScript) = f ()
        member __.Delay(f: unit -> XakeEngine) = f ()

        /// Continuation of the script body after a custom operation (`filelog`, `want`, ...).
        member this.For(script: XakeScript, f: unit -> XakeScript) = this.Combine(script, f ())

        /// `for x in xs do ...` inside a script body, generating rules.
        member this.For(items: #seq<'T>, f: 'T -> XakeScript) =
            items |> Seq.fold (fun acc x -> this.Combine(acc, f x)) (XakeScript (options, Rules []))

        member this.Run(script: XakeScript) =
            let (XakeScript (options, rules)) = this.ApplyCommandLine script
            if options.ShowHelp then
                printVarsHelp options
                exit 0
            ScriptRunner.runScript options rules

        [<CustomOperation("dryrun")>]
        member __.DryRun(XakeScript (options, rules)) =
            XakeScript ({options with DryRun = true}, rules)

        /// Sets the project root directory for relative paths in rules. Defaults to current directory.        
        [<CustomOperation("rootdir")>]
        member __.RootDir(XakeScript (options, rules), dir) =
            XakeScript ({options with ProjectRoot = dir}, rules)

        /// A script value of a variable (`-d` and the engine's per-demand vars win over it).
        [<CustomOperation("var")>]
        member __.AddVar(XakeScript (options, rules), name: string, value: string) =
            XakeScript ({ options with Vars = options.Vars @ [name, value]
                                       VarDefinedBy = options.VarDefinedBy @ [name, sprintf "var \"%s\" \"...\"" name] }, rules)

        /// A variable rule: at launch its sources are tried in list order (`Env "X"`, `Default "v"`),
        /// unless `-d` or a script value defines the name; `Secret`/`SecretHashed` make it a secret.
        /// The op only records the declaration.
        [<CustomOperation("var")>]
        member __.AddVar(XakeScript (options, rules), name: string, sources: VarSource list) =
            let decl = VarDecl.ofRule name sources
            XakeScript ({ options with VarDecls = options.VarDecls @ [decl]
                                       VarDefinedBy = options.VarDefinedBy @ [name, decl.DeclaredBy] }, rules)

        [<CustomOperation("filelog")>]
        member __.FileLog(XakeScript (options, rules), filename, ?loglevel) =

            let loglevel = defaultArg loglevel Verbosity.Chatty in
            XakeScript ({options with FileLog = filename; FileLogLevel = loglevel}, rules)

        [<CustomOperation("consolelog")>]
        member __.ConLog(XakeScript (options, rules), ?loglevel) =

            let loglevel = defaultArg loglevel Verbosity.Chatty in
            XakeScript ({options with ConLogLevel =loglevel}, rules)

        [<CustomOperation("ruleMatch")>]
        member __.RuleMatch(XakeScript (options, rules), mode: RuleMatchMode) =
            XakeScript ({options with RuleMatch = mode}, rules)

        [<CustomOperation("rule")>]
        member __.Rule(script, rule) =

            updRules script (addRule rule)

        // [<CustomOperation("addRule")>] member this.AddRule(script, pattern, action)
        //     = updRules script (pattern *> action |> addRule)

        [<CustomOperation("phony")>]
        member __.Phony(script, name, action) =

            updRules script (name => action |> addRule)

        [<CustomOperation("rules")>]
        member __.Rules(script, rules: #seq<ExecContext Rule>) =

            (rules |> Seq.map addRule |> Seq.fold (>>) id) |> updRules script

        [<CustomOperation("want")>]
        member __.Want(script, targets) =
            updTargets script (fun _ -> targets)    // over ExecOptions.Targets; CLI targets win in Run

        [<CustomOperation("noPersist")>]
        member __.NoPersist(XakeScript (options, rules)) =
            XakeScript ({options with NoPersist = true}, rules)

        [<CustomOperation("teardown")>]
        member __.Teardown(XakeScript (options, rules), targets: string list) =
            XakeScript ({options with Teardown = targets}, rules)

        /// Targets run sequentially before the build, once options and variables are final.
        /// Unlike `teardown` (a later op replaces the earlier list), several `setup` ops accumulate.
        [<CustomOperation("setup")>]
        member __.Setup(XakeScript (options, rules), targets: string list) =
            XakeScript ({options with Setup = options.Setup @ targets}, rules)

        /// Starts the engine, returning a long-lived XakeEngine.
        [<CustomOperation("start")>]
        member this.Start(script: XakeScript) : XakeEngine =
            let (XakeScript (options, rules)) = this.ApplyCommandLine script
            // explicitly disable progress for long-lived engine
            XakeEngine.Start (XakeScript ({ options with Progress = false }, rules))

        [<CustomOperation("engine")>]
        member this.Engine script : XakeEngine = this.Start script

        member __.Run(e: XakeEngine) = e

    /// Long-lived engine for watch/LSP-style callers. Start once, demand targets on demand, stop cleanly.
    and XakeEngine private (engine: EngineState, vars: (string * string) list, showProgress: bool, finalize: unit -> unit) =
        [<VolatileField>]
        let mutable stopped = false
        let inFlight = ConcurrentDictionary<string, Lazy<Task<ExecStatus>>> ()

        let makeCtx extraVars = {
            Engine = engine
            Vars = vars @ defaultArg extraVars []
            Progress = Progress.emptyProgress()
            NeedRebuild = fun _ -> false
            ShowProgress = showProgress
            Targets = []
            RuleMatches = Map.empty
            Ordinal = 0
            Logger = engine.RootLogger
            HeldResources = []
        }

        /// Starts a XakeEngine from a fully assembled XakeScript.
        static member Start(XakeScript (options, rules)) =
            let ctx, finalize = ScriptRunner.createScriptContext options rules
            try ScriptRunner.runSetupAsync ctx options.Setup |> Async.RunSynchronously
            with _ -> finalize (); reraise ()
            XakeEngine (ctx.Engine, ctx.Vars, ctx.ShowProgress, finalize)

        /// Starts a XakeEngine from raw EngineOptions without a script.
        static member Start(options: EngineOptions) =
            let ctx, finalize = ExecCore.createContext options []
            XakeEngine (ctx.Engine, ctx.Vars, ctx.ShowProgress, finalize)

        /// Builds a single target by name. Serializes concurrent requests for the same target:
        /// if a demand is in-flight, waits for it to complete then re-evaluates with the new context.
        member _.Demand(targetName: string, ?vars: (string * string) list) : Task =
            if stopped then raise (System.InvalidOperationException "XakeEngine has been stopped")
            let demandCtx = makeCtx vars
            let rec tryDemand () : Task =
                let newLazy = Lazy<_>(fun () ->
                    let task = ExecCore.demandTarget demandCtx targetName |> Async.StartAsTask
                    task.ContinueWith(fun (_: Task<ExecStatus>) -> inFlight.TryRemove targetName |> ignore) |> ignore
                    task
                )
                let existing = inFlight.GetOrAdd(targetName, newLazy)
                if obj.ReferenceEquals(existing, newLazy) then
                    newLazy.Value :> Task
                else
                    async {
                        try do! existing.Value |> Async.AwaitTask |> Async.Ignore
                        with _ -> ()
                        inFlight.TryRemove targetName |> ignore
                        return! tryDemand () |> Async.AwaitTask
                    } |> Async.StartAsTask :> Task
            tryDemand ()

        /// Stops the engine: waits for in-flight tasks, runs teardown targets, then releases resources.
        member this.StopAsync() : Task =
            async {
                stopped <- true
                try
                    let snapshot = inFlight.Values |> Seq.map (fun l -> l.Value) |> Seq.toArray
                    do! snapshot |> Array.map Async.AwaitTask |> Async.Parallel |> Async.Ignore
                    do! makeCtx None |> ScriptRunner.runTeardownAsync
                finally
                    finalize ()
            } |> Async.StartAsTask :> Task

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        interface System.IAsyncDisposable with
            member this.DisposeAsync() = System.Threading.Tasks.ValueTask (this.StopAsync ())
#endif
