namespace Xake

[<AutoOpen>]
module XakeScriptBuilder =

    open System.Collections.Concurrent
    open System.Threading.Tasks

    let private printVarsHelp (schema: (string * VarHelp) list) =
        if schema <> [] then
            printfn "\nScript variables:"
            for (name, help) in schema do
                let header =
                    match help.Scope with
                    | EnvOnly ->
                        let envName = help.EnvVarName |> Option.defaultValue name
                        sprintf "  $%s [%s]" envName help.TypeName
                    | _ ->
                        sprintf "  -d %s=<value> [%s]" name help.TypeName
                let required = if help.IsRequired then " (required)" else ""
                let envStr =
                    match help.Scope with
                    | EnvOnly -> ""  // env name already shown in header
                    | _ -> help.EnvVarName |> Option.map (sprintf ", env: %s") |> Option.defaultValue ""
                let defaultStr = help.DefaultStr |> Option.map (sprintf ", default: %s") |> Option.defaultValue ""
                let descStr = help.Description |> Option.map (sprintf " — %s") |> Option.defaultValue ""
                printfn "%s%s%s%s%s" header required envStr defaultStr descStr

    /// Script builder.
    type RulesBuilder(options) =

        let updRules (XakeScript (options,rules)) f = XakeScript (options, f(rules))
        let updTargets (XakeScript (options,rules)) f = XakeScript ({options with Targets = f(options.Targets)}, rules)
        let addRule rule (Rules rules) :Rules<_> =    Rules (rule :: rules)

        let updateVar (key: string) (value: string) =
            List.filter(fst >> ((<>) key)) >> ((@) [key, value])

        member __.Zero() = XakeScript (options, Rules [])
        member this.Yield(()) = this.Zero()

        member __.Run(XakeScript (options, rules)) =
            if options.ShowHelp then
                printVarsHelp options.VarSchema
                exit 0
            ScriptRunner.runScript options rules

        [<CustomOperation("dryrun")>]
        member __.DryRun(XakeScript (options, rules)) =
            XakeScript ({options with DryRun = true}, rules)

        /// Sets the project root directory for relative paths in rules. Defaults to current directory.        
        [<CustomOperation("rootdir")>]
        member __.RootDir(XakeScript (options, rules), dir) =
            XakeScript ({options with ProjectRoot = dir}, rules)

        [<CustomOperation("var")>]
        member __.AddVar(XakeScript (options, rules), name, value) =

            XakeScript ({options with Vars = options.Vars |> updateVar name value }, rules)

        [<CustomOperation("filelog")>]
        member __.FileLog(XakeScript (options, rules), filename, ?loglevel) =

            let loglevel = defaultArg loglevel Verbosity.Chatty in
            XakeScript ({options with FileLog = filename; FileLogLevel = loglevel}, rules)

        [<CustomOperation("consolelog")>]
        member __.ConLog(XakeScript (options, rules), ?loglevel) =

            let loglevel = defaultArg loglevel Verbosity.Chatty in
            XakeScript ({options with ConLogLevel =loglevel}, rules)

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

            updTargets script (function |[] -> targets |x -> x)    // Options override script!

        [<CustomOperation("wantOverride")>]
        member __.WantOverride(script,targets) =

            updTargets script (fun _ -> targets)

        [<CustomOperation("noPersist")>]
        member __.NoPersist(XakeScript (options, rules)) =
            XakeScript ({options with NoPersist = true}, rules)

        [<CustomOperation("teardown")>]
        member __.Teardown(XakeScript (options, rules), targets: string list) =
            XakeScript ({options with Teardown = targets}, rules)

        /// Starts the engine, returning a long-lived XakeEngine.
        [<CustomOperation("start")>]
        member __.Start(script: XakeScript) : XakeEngine =
            XakeEngine.Start script

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
        }

        /// Starts a XakeEngine from a fully assembled XakeScript.
        static member Start(XakeScript (options, rules)) =
            let ctx, finalize = ScriptRunner.createScriptContext options rules
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
