namespace Xake

[<AutoOpen>]
module XakeScriptBuilder =

    /// Script builder.
    type RulesBuilder(options) =

        let updRules (XakeScript (options,rules)) f = XakeScript (options, f(rules))
        let updTargets (XakeScript (options,rules)) f = XakeScript ({options with Targets = f(options.Targets)}, rules)
        let addRule rule (Rules rules) :Rules<_> =    Rules (rule :: rules)

        let updateVar (key: string) (value: string) =
            List.filter(fst >> ((<>) key)) >> ((@) [key, value])

        member __.Zero() = XakeScript (options, Rules [])
        member this.Yield(()) = this.Zero()

        member __.Run(XakeScript (options,rules)) =
            ExecCore.runScript options rules

        [<CustomOperation("dryrun")>]
        member __.DryRun(XakeScript (options, rules)) =

            XakeScript ({options with DryRun = true}, rules)

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

        [<CustomOperation("start")>]
        member __.Start(script: XakeScript) : XakeEngine =
            XakeEngine.Start script

        member __.Run(e: XakeEngine) = e

    /// Long-lived engine for watch/LSP-style callers. Start once, demand targets on demand, stop cleanly.
    and XakeEngine private (engine: EngineState, vars: (string * string) list, showProgress: bool, finalize: unit -> unit) =
        [<VolatileField>]
        let mutable stopped = false
        let inFlight = System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<System.Threading.Tasks.Task<ExecStatus>>> ()

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

        static member Start(XakeScript (options, rules)) =
            let ctx, finalize = ExecCore.createScriptContext options rules
            XakeEngine (ctx.Engine, ctx.Vars, ctx.ShowProgress, finalize)

        static member Start(options: EngineOptions) =
            let ctx, finalize = ExecCore.createContext options []
            XakeEngine (ctx.Engine, ctx.Vars, ctx.ShowProgress, finalize)

        member _.Demand(targetName: string, ?vars: (string * string) list) : System.Threading.Tasks.Task =
            if stopped then raise (System.InvalidOperationException "XakeEngine has been stopped")
            let demandCtx = makeCtx vars
            inFlight.GetOrAdd(targetName, Lazy<_>(fun () ->
                ExecCore.demandTarget demandCtx targetName |> Async.StartAsTask
            )).Value :> System.Threading.Tasks.Task

        member this.StopAsync() : System.Threading.Tasks.Task =
            async {
                stopped <- true
                try
                    let snapshot = inFlight.Values |> Seq.map (fun l -> l.Value) |> Seq.toArray
                    do! snapshot |> Array.map Async.AwaitTask |> Async.Parallel |> Async.Ignore
                    for name in engine.Options.Teardown do
                        let ctx = makeCtx None
                        do! ExecCore.demandTarget ctx name |> Async.Ignore
                finally
                    finalize ()
            } |> Async.StartAsTask :> System.Threading.Tasks.Task

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        interface System.IAsyncDisposable with
            member this.DisposeAsync() = System.Threading.Tasks.ValueTask (this.StopAsync ())
#endif
