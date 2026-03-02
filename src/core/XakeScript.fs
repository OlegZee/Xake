namespace Xake

[<AutoOpen>]
module XakeScript =

    /// Creates the rule for specified file pattern.
    let ( ..?> ) fn fnRule = FileConditionRule (fn, fnRule)

    let ( ..> ) pattern actionBody = FileRule (pattern, actionBody)

    let ( *..> ) (patterns: #seq<string>) actionBody =
        MultiFileRule (patterns |> List.ofSeq, actionBody)

    /// Creates phony action (check if I can unify the operator name)
    let (=>) name action = PhonyRule (name, action)

    /// Main type.
    type XakeScript = XakeScript of ExecOptions * Rules<ExecContext>

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
    and XakeEngine private (ctx: ExecContext, finalize: unit -> unit) =
        let gate = obj ()
        let mutable stopped = false
        let inFlight = System.Collections.Generic.Dictionary<string, System.Threading.Tasks.Task<ExecStatus>> ()

        static member Start(XakeScript (options, rules)) =
            let ctx, finalize = ExecCore.createContext options rules
            XakeEngine (ctx, finalize)

        member _.Demand(targetName: string) : System.Threading.Tasks.Task =
            lock gate (fun () ->
                if stopped then raise (System.InvalidOperationException "XakeEngine has been stopped")
                match inFlight.TryGetValue targetName with
                | true, t -> t :> System.Threading.Tasks.Task
                | _ ->
                    let t = ExecCore.demandTarget ctx targetName |> Async.StartAsTask
                    inFlight.[targetName] <- t
                    t :> System.Threading.Tasks.Task)

        member this.StopAsync() : System.Threading.Tasks.Task =
            async {
                let snapshot = lock gate (fun () -> stopped <- true; inFlight.Values |> Seq.toArray)
                do! snapshot |> Array.map Async.AwaitTask |> Async.Parallel |> Async.Ignore
                for name in ctx.Options.Teardown do
                    do! ExecCore.demandTarget ctx name |> Async.Ignore
                finalize ()
            } |> Async.StartAsTask :> System.Threading.Tasks.Task

#if NETSTANDARD2_1_OR_GREATER || NET5_0_OR_GREATER
        interface System.IAsyncDisposable with
            member this.DisposeAsync() = System.Threading.Tasks.ValueTask (this.StopAsync ())
#endif
