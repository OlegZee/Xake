namespace Xake.Experimental

open Xake

[<AutoOpen>]
module RuleBuilder =

    type RuleBuilder(ruleType: string, name: string, ?patterns: string list) =
        inherit RecipeBuilder()
        member __.Run(expr: Recipe<ExecContext, unit>) =
            match ruleType with
            | "command" -> PhonyRule (name, recipe {
                do! expr
                let! result = getResult()
                let depends =
                    if List.contains AlwaysRerun result.Depends then
                        result.Depends
                    else
                        AlwaysRerun :: result.Depends
                do! setResult { result with Depends = depends }
              })
            | "target"  -> FileRule (name, expr)
            | "targets" -> MultiFileRule (patterns.Value, expr)
            | _ -> failwith "Invalid rule type"

    let command name = RuleBuilder("command", name)
    let target pattern = RuleBuilder("target", pattern)
    let targets patterns = RuleBuilder("targets", "", patterns)
