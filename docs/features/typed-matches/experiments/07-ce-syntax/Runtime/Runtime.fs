namespace Xake.Patterns

open Xake
open Xake.Fileset

/// Experiment 07 (copied from 05), the runtime half. In the real design this is a file in Xake.dll (after
/// ScriptFuncs.fs); here it is a stand-in dll so that src/ stays untouched. Every provided (erased)
/// member compiles to a call into this module, with the pattern strings and keys baked in as
/// constants. A typed match is, at run time, the `Map<string,string>` of `getRuleMatches`: named
/// groups under their names, bare wildcards under their regex group numbers ("1", "2", ...).
module PatternRuntime =

    let private cache = System.Collections.Concurrent.ConcurrentDictionary<string, PatternAst.Token list>()
    let private tokens (p: string) = cache.GetOrAdd(p, System.Func<_, _> PatternAst.parse)

    /// Path of one pattern for a typed match; one AST walk, shared with the engine's parser.
    let format (pattern: string) (m: Map<string, string>) =
        let toks = tokens pattern
        let nums = PatternAstGen.wildcards toks |> List.map snd |> Array.ofList
        PatternAstGen.format (fun n -> m.[n]) (fun i -> m.[string nums.[i - 1]]) toks

    /// One value of a typed match (a group name or a wildcard's number).
    let group (m: Map<string, string>) (key: string) = m.[key]

    /// Inverse of matching: keys/values -> typed match. Each pattern's path is re-matched with the
    /// engine's own `Path.matchGroups` and must give the same values back: this is where a
    /// constraint such as `(ext:c*)`, or a '/' in a `*` value, is rejected.
    let make (patterns: string[]) (keys: string[]) (values: string[]) =
        let m = Array.zip keys values |> Map.ofArray
        for p in patterns do
            let path = format p m
            match Path.matchGroups p "" path with
            | Some found when keys |> Array.forall (fun k -> found |> List.forall (fun (k', v) -> k' <> k || v = m.[k])) -> ()
            | _ -> failwithf "Pattern '%s': %A do not give a path the pattern matches (got '%s')" p (Map.toList m) path
        m

    /// Cross product of value lists, every pattern's path per combination (pattern order).
    let all (patterns: string[]) (keys: string[]) (values: string list[]) =
        let rec cross = function
            | [] -> [ [] ]
            | vs :: rest -> [ for v in vs do for tail in cross rest -> v :: tail ]
        [ for combo in cross (List.ofArray values) do
            let m = make patterns keys (Array.ofList combo)
            for p in patterns -> format p m ]

    /// Partial binding -> Ant masks: null = unbound; unbound groups become their own sub-masks,
    /// unbound wildcards stay wildcards. One mask per pattern.
    let globMasks (patterns: string[]) (keys: string[]) (values: string[]) =
        let bound = Array.zip keys values |> Array.filter (fun (_, v) -> not (isNull v)) |> Map.ofArray
        [ for p in patterns ->
            let toks = tokens p
            let nums = PatternAstGen.wildcards toks |> List.map snd |> Array.ofList
            PatternAstGen.glob bound.TryFind (fun i -> bound.TryFind (string nums.[i - 1])) toks ]

    /// The same as a Fileset (brief decision 8), one `includes` per pattern.
    let glob (patterns: string[]) (keys: string[]) (values: string[]) : Fileset =
        Fileset (DefaultOptions, [ for m in globMasks patterns keys values -> Includes (Path.parse m) ])

    let get () : Recipe<ExecContext, Map<string, string>> = getRuleMatches ()

    let rule (patterns: string[]) (body: Map<string, string> -> Recipe<ExecContext, unit>) : ExecContext Rule =
        let r = recipe {
            let! m = getRuleMatches ()
            do! body m
        }
        match List.ofArray patterns with
        | [ p ] -> FileRule (p, r)
        | ps -> MultiFileRule (ps, r)

    // ---------------------------------------------------------------- experiment 07: rule syntax

    /// Checked `Get` (form A): the current rule's matches must hold every key of the typed
    /// pattern. `Dll.Pattern ..> recipe { let! m = Lib.Get ... }` compiles, so a Get of another
    /// pattern can only be caught here, at run time.
    let getFor (patterns: string[]) (keys: string[]) : Recipe<ExecContext, Map<string, string>> = recipe {
        let! m = getRuleMatches ()
        let missing = keys |> Array.filter (m.ContainsKey >> not)
        if missing.Length > 0 then
            failwithf "%A.Get: the rule being run has no groups %A (it has %A); a Get of another pattern than the rule's?" patterns missing (m |> Map.toList |> List.map fst)
        return m
    }

    /// Phony rule with `=>` semantics (experiment 04), for `Command<>.Rule(fun m -> ...)`.
    let phony (name: string) (body: Map<string, string> -> Recipe<ExecContext, unit>) : ExecContext Rule =
        PhonyRule (name, recipe {
            let! m = getRuleMatches ()
            do! body m
        })

    /// The matched action's own name (the engine's whole-match group "0").
    let name (text: string) (m: Map<string, string>) =
        match m.TryFind "0" with
        | Some n -> n
        | None -> format text m

// in the real design these live in Xake.dll's `Xake` namespace; here too, so that a script's
// `open Xake` brings `matched` in
namespace Xake

/// Experiment 07, form B: the marker bound by `let! m = matched` inside a typed rule builder.
type Matched = Matched

/// Experiment 07, form B: the builder a provided type exposes as `Dll.rule`. Everything of
/// `recipe { }` (it inherits RecipeBuilder, as `target`/`targets`/`command` do) plus one Bind:
/// `let! m = matched` gives the typed match, its type fixed by the builder's 'M (the provided,
/// erased type, i.e. Map<string,string> at run time). Run turns the body into the rule.
type TypedRuleBuilder<'M>(make: Recipe<ExecContext, unit> -> ExecContext Rule) =
    inherit RecipeBuilder()
    member this.Bind(_: Matched, f: 'M -> Recipe<ExecContext, 'b>) : Recipe<ExecContext, 'b> =
        this.Bind(getRuleMatches (), fun (m: Map<string, string>) -> f (unbox<'M> (box m)))
    member _.Run(body: Recipe<ExecContext, unit>) : ExecContext Rule = make body

[<AutoOpen>]
module MatchedValue =
    /// `let! m = matched` inside `Dll.rule { ... }`.
    let matched = Matched

/// Experiment 07, form B: what the provided `rule` properties erase to.
module PatternBuilders =
    let rule (patterns: string[]) : TypedRuleBuilder<Map<string, string>> =
        TypedRuleBuilder (fun body ->
            match List.ofArray patterns with
            | [ p ] -> FileRule (p, body)
            | ps -> MultiFileRule (ps, body))

    let command (name: string) : TypedRuleBuilder<Map<string, string>> =
        TypedRuleBuilder (fun body -> PhonyRule (name, body))
