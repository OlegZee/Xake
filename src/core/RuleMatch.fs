namespace Xake

/// The marker of `let! m = matched`: the groups of the rule being run. In `recipe { }` (and
/// `target`, `targets`, `command`) `m` is a `RuleMatch`; in `X.rule { }` it is the typed record.
type Matched = Matched

/// The groups of the rule being run, from `let! m = matched` in a recipe: `m["n"]` is the value
/// of the group `n`. A rule with no groups (or a recipe run outside a rule) has an empty match.
[<Sealed>]
type RuleMatch internal (groups: Map<string, string>) =
    /// Numbered keys (`0`, `1`, ...) are the positional captures, not named groups.
    static let isNamed (key: string) = not (key |> Seq.forall System.Char.IsDigit)

    /// The value of a group; an unknown name fails the task with the names of the groups.
    member _.Item
        with get (name: string) : string =
            match Map.tryFind name groups with
            | Some value -> value
            | None ->
                let names = groups |> Map.toList |> List.map fst |> List.filter isNamed
                failwithf "no group '%s' in the rule match (groups: %s)" name
                    (match names with [] -> "none" | _ -> String.concat ", " names)

    /// The value of a group, or None.
    member _.TryFind(name: string) : string option = Map.tryFind name groups

    /// The names of the named groups, in name order.
    member _.Names : string list = groups |> Map.toList |> List.map fst |> List.filter isNamed

    override _.ToString() =
        groups |> Map.toList |> List.filter (fst >> isNamed)
        |> List.map (fun (k, v) -> sprintf "%s=%s" k v) |> String.concat ", " |> sprintf "RuleMatch(%s)"

[<AutoOpen>]
module RuleRecipe =
    /// The `recipe { }` of scripts: `RecipeBuilder` plus `let! m = matched` (a `RuleMatch`).
    /// The typed builders (`X.rule { }`) derive from `RecipeBuilder` directly and bind `matched`
    /// to their record; seeing this overload too would cost them their compile-time errors.
    type RuleRecipeBuilder() =
        inherit RecipeBuilder()
        /// `let! m = matched`: the groups of the rule being run.
        member _.Bind(_: Matched, f: RuleMatch -> Recipe<ExecContext, 'b>) : Recipe<ExecContext, 'b> =
            Recipe (fun (result, ctx: ExecContext) -> A.runAction (f (RuleMatch ctx.RuleMatches)) (result, ctx))

    /// `let! m = matched`: the groups of the rule being run, `m["n"]` (strict; see `RuleMatch`).
    let matched = Matched

    /// The recipe computation expression builder instance.
    let recipe = RuleRecipeBuilder()
    /// Backward-compatible alias for the recipe builder.
    let action = RuleRecipeBuilder()
