namespace Xake

open Xake

[<AutoOpen>]
module RuleBuilder =

    /// `target "p" { .. }` and `targets ["p"; "q"] { .. }`: a file rule; the body may start with a
    /// header (`also`, `needs`, `requires`, `noContentCutoff`).
    type FileRuleBuilder(patterns: string list) =
        inherit RuleRecipeBuilder()
        member __.Run(body: Recipe<ExecContext, unit>) : ExecContext Rule = FileRule (patterns, RuleHeader.Empty, body)
        member __.Run(Headed (header, body): Headed<ExecContext>) : ExecContext Rule = FileRule (patterns, header, body)

    /// `command "name" { .. }`: a phony rule that always runs; no header.
    type CommandBuilder(name: string) =
        inherit RuleRecipeBuilder()
        member __.Run(expr: Recipe<ExecContext, unit>) : ExecContext Rule =
            PhonyRule (name, recipe {
                do! expr
                let! result = getResult()
                let depends =
                    if List.contains AlwaysRerun result.Depends then
                        result.Depends
                    else
                        AlwaysRerun :: result.Depends
                do! setResult { result with Depends = depends }
              })

    let command name = CommandBuilder(name)
    let target pattern = FileRuleBuilder([ pattern ])
    let targets patterns = FileRuleBuilder(patterns)
