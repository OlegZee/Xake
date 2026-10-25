namespace Xake.Patterns

open System.ComponentModel
open Xake
open Xake.Fileset

/// What the erased members of `Target<...>` and `Command<...>` compile to: direct calls from the
/// script's own code, the pattern strings and keys baked in as constants. Public because erased code
/// calls it; hidden from completion. A typed match is, at run time, the `Map<string,string>` of
/// `getRuleMatches`: named groups under their names, bare wildcards under their regex numbers.
[<EditorBrowsable(EditorBrowsableState.Never)>]
module PatternRuntime =

    let private cache = System.Collections.Concurrent.ConcurrentDictionary<string, PatternAst.Token list>()
    let private tokens (p: string) = cache.GetOrAdd(p, System.Func<_, _> PatternAst.parse)

    /// One value of a typed match (a group name or a wildcard's regex number).
    let group (m: Map<string, string>) (key: string) =
        match m.TryFind key with
        | Some v -> v
        | None -> failwithf "typed match has no value for '%s' (it has %A)" key (m |> Map.toList |> List.map fst)

    /// The path (or action name) of one pattern for a typed match.
    let format (pattern: string) (m: Map<string, string>) = PatternAst.formatMatches m pattern

    /// Inverse of matching: values -> typed match. Every pattern's path is re-matched with the
    /// engine's own `Path.matchGroups` and must give the same values back; this is where a
    /// constraint such as `(ext:c*)`, or a '/' in a `*` value, is rejected.
    let make (patterns: string[]) (keys: string[]) (values: string[]) : Map<string, string> =
        let m = Array.zip keys values |> Map.ofArray
        for p in patterns do
            let path = format p m
            let back = Path.matchGroups p "" path |> Option.map Map.ofList
            match back with
            | Some found when keys |> Array.forall (fun k -> found.TryFind k = m.TryFind k) -> ()
            | _ -> failwithf "Pattern '%s': %A do not give a name the pattern matches back (got '%s')" p (Map.toList m) path
        m

    /// Cross product of value lists; every pattern's path per combination, in pattern order.
    let forAll (patterns: string[]) (keys: string[]) (values: string list[]) : string list =
        let rec cross = function
            | [] -> [ [] ]
            | vs :: rest -> [ for v in vs do for tail in cross rest -> v :: tail ]
        [ for combo in cross (List.ofArray values) do
            let m = make patterns keys (Array.ofList combo)
            for p in patterns -> format p m ]

    /// Partial binding (null = unbound) -> a Fileset with one `includes` per pattern: an unbound
    /// group becomes its own sub-mask, an unbound wildcard stays a wildcard (brief decision 8).
    let glob (patterns: string[]) (keys: string[]) (values: string[]) : Fileset =
        let bound = Array.zip keys values |> Array.filter (fun (_, v) -> not (isNull v)) |> Map.ofArray
        let masks =
            [ for p in patterns ->
                let toks = tokens p
                let nums = PatternAst.wildcards toks |> List.map snd |> Array.ofList
                PatternAst.glob bound.TryFind (fun i -> bound.TryFind (string nums.[i - 1])) toks ]
        Fileset (DefaultOptions, [ for m in masks -> Includes (Path.parse m) ])

    /// The matched action's own name (the engine's whole-match group "0").
    let name (pattern: string) (m: Map<string, string>) =
        match m.TryFind "0" with
        | Some n -> n
        | None -> format pattern m

    let private withMatches (body: Map<string, string> -> Recipe<ExecContext, unit>) = recipe {
        let! m = getRuleMatches ()
        do! body m
    }

    /// `Target<...>.Rule`: a FileRule, or a MultiFileRule for several outputs.
    let fileRule (patterns: string[]) (r: Recipe<ExecContext, unit>) : ExecContext Rule =
        match List.ofArray patterns with
        | [ p ] -> FileRule (p, r)
        | ps -> MultiFileRule (ps, r)

    /// `Command<...>.Rule`: exactly today's `command "..." { }` (a PhonyRule adding AlwaysRerun,
    /// brief decision 21) -- it is the `command` builder's own Run.
    let commandRule (name: string) (r: Recipe<ExecContext, unit>) : ExecContext Rule =
        (RuleBuilder.command name).Run r

    let rule (patterns: string[]) body = fileRule patterns (withMatches body)
    let command (name: string) body = commandRule name (withMatches body)
    /// A command without groups: the body takes unit (brief decision 23).
    let command0 (name: string) (body: unit -> Recipe<ExecContext, unit>) = commandRule name (recipe { do! body () })

/// Form B: `let! m = matched` inside `X.rule { ... }`.
type Matched = Matched

/// Form B: the builder a provided type exposes as `X.rule`. Everything of `recipe { }` (it inherits
/// RecipeBuilder, as `target`/`targets`/`command` do) plus one Bind: `let! m = matched` gives the typed
/// match, its type fixed by 'M -- the provided (erased) type, Map<string,string> at run time. Run
/// turns the body into the rule.
type TypedRuleBuilder<'M>(make: Recipe<ExecContext, unit> -> ExecContext Rule) =
    inherit RecipeBuilder()
    member this.Bind(_: Matched, f: 'M -> Recipe<ExecContext, 'b>) : Recipe<ExecContext, 'b> =
        this.Bind(getRuleMatches (), fun (m: Map<string, string>) -> f (unbox<'M> (box m)))
    member _.Run(body: Recipe<ExecContext, unit>) : ExecContext Rule = make body

[<AutoOpen>]
module MatchedValue =
    /// `let! m = matched` inside `X.rule { ... }`: the typed match of the rule being run.
    let matched = Matched

/// What the provided `rule` properties erase to.
[<EditorBrowsable(EditorBrowsableState.Never)>]
module PatternBuilders =
    let rule (patterns: string[]) : TypedRuleBuilder<Map<string, string>> =
        TypedRuleBuilder (PatternRuntime.fileRule patterns)
    let command (name: string) : TypedRuleBuilder<Map<string, string>> =
        TypedRuleBuilder (PatternRuntime.commandRule name)
    /// A command without groups: today's `command "name" { }` builder itself.
    let command0 (name: string) : RuleBuilder.RuleBuilder = Xake.RuleBuilder.command name
