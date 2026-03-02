namespace Xake

[<AutoOpen>]
module XakeScript =

    /// <summary>Creates a conditional file rule that executes only when the condition function returns true.</summary>
    /// <param name="fn">The condition function that determines whether the rule should execute</param>
    /// <param name="fnRule">The rule function to execute when the condition is met</param>
    /// <returns>A FileConditionRule that conditionally processes files</returns>
    let ( ..?> ) fn fnRule = FileConditionRule (fn, fnRule)

    /// <summary>Creates a file rule that matches a single file pattern and executes the specified action.</summary>
    /// <param name="pattern">The file pattern or path to match (supports wildcards)</param>
    /// <param name="actionBody">The action to execute when the pattern matches</param>
    /// <returns>A FileRule for processing matching files</returns>
    let ( ..> ) pattern actionBody = FileRule (pattern, actionBody)

    /// <summary>Creates a multi-file rule that matches multiple patterns and executes the same action for all.</summary>
    /// <param name="patterns">A sequence of file patterns to match</param>
    /// <param name="recipe">The action to execute for all matching patterns</param>
    /// <returns>A MultiFileRule for processing multiple file patterns</returns>
    let ( *..> ) (patterns: #seq<string>) recipe =
        MultiFileRule (patterns |> List.ofSeq, recipe)

    /// <summary>Creates a phony rule (a rule that doesn't correspond to an actual file).</summary>
    /// <param name="name">The name of the phony target</param>
    /// <param name="action">The recipe to execute when this phony target is requested</param>
    /// <returns>A PhonyRule for executing named actions</returns>
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

        // // [<CustomOperation("addRule")>] member this.AddRule(script, pattern, action)
        // //     = updRules script (pattern *> action |> addRule)

        [<CustomOperation("phony'")>]
        member __.Phony(script, name, action) =

            updRules script (PhonyRule(name, action) |> addRule)

        [<CustomOperation("rules")>]
        member __.Rules(script, rules: #seq<ExecContext Rule>) =
        
            rules |> Seq.map addRule |> Seq.fold (>>) id |> updRules script

        [<CustomOperation("want")>]
        member __.Want(script, targets) =
        
            updTargets script (function |[] -> targets |x -> x)    // Options override script!

        [<CustomOperation("wantOverride")>]
        member __.WantOverride(script,targets) =
        
            updTargets script (fun _ -> targets)
