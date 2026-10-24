namespace Xake.Patterns

open System.Text.RegularExpressions
open Xake

/// Runtime half of the experiment: the provided (erased) members compile to calls into
/// these functions. A provided `Pattern<...>` value is, at run time, a Map<string,string>.
module Runtime =

    let private tagRegex = Regex(@"\((?'tag'\w+?)\:[^)]+\)")

    /// Substitutes named groups into a pattern ("out/(fwk:*)/(lib:*).dll" + map -> "out/net8.0/Lib.dll").
    let subst (pattern: string) (groups: Map<string, string>) =
        tagRegex.Replace(pattern, fun m -> groups.[m.Groups.["tag"].Value])

    /// Reads one group; the provider only emits names it has seen in the pattern.
    let group (groups: Map<string, string>) (name: string) = groups.[name]

    /// Builds the group map from values given positionally (in the provider's group order)
    /// and checks every resulting path matches its pattern with the same values: this is where
    /// a constraint such as `(ext:c*)` is enforced for a generated path.
    let make (patterns: string list) (names: string list) (values: string list) =
        let groups = List.zip names values |> Map.ofList
        for p in patterns do
            let path = subst p groups
            match Path.matchGroups p "" path with
            | Some found when names |> List.forall (fun n -> not (p.Contains("(" + n + ":")) || (found |> List.exists (fun (k, v) -> k = n && v = groups.[n]))) -> ()
            | _ -> failwithf "Pattern '%s': the values %A do not produce a path the pattern matches (got '%s')" p (Map.toList groups) path
        groups

    /// Vectorized generation: the cross product of the value lists, every pattern's path for
    /// each combination (multi-file outputs flattened, in pattern order per combination).
    let all (patterns: string list) (names: string list) (values: string list list) =
        let rec cross = function
            | [] -> [ [] ]
            | vs :: rest -> [ for v in vs do for tail in cross rest -> v :: tail ]
        [ for combo in cross values do
            let groups = make patterns names combo
            for p in patterns -> subst p groups ]

    /// Partial binding for globbing: bound groups are substituted, unbound ones (null) are
    /// replaced by their own sub-mask, giving a plain Ant mask for a fileset.
    let glob (patterns: string list) (names: string list) (values: string list) =
        let bound = List.zip names values |> List.filter (fun (_, v) -> not (isNull v)) |> Map.ofList
        let unbound = Regex(@"\((?'tag'\w+?)\:(?'mask'[^)]+)\)")
        [ for p in patterns ->
            unbound.Replace(p, fun m ->
                match bound.TryFind m.Groups.["tag"].Value with
                | Some v -> v
                | None -> m.Groups.["mask"].Value) ]

    /// The recipe-side accessor: the current rule's groups.
    let get () : Recipe<ExecContext, Map<string, string>> = getRuleMatches ()

    /// Registers the rule: one pattern -> FileRule, several -> MultiFileRule.
    let rule (patterns: string list) (body: Map<string, string> -> Recipe<ExecContext, unit>) : ExecContext Rule =
        let r = recipe {
            let! m = getRuleMatches ()
            do! body m
        }
        match patterns with
        | [ p ] -> FileRule (p, r)
        | ps -> MultiFileRule (ps, r)

    // ---- experiment 04: phony names (Command<...>)

    /// Registers a phony rule: exactly what `name => recipe { ... }` gives (no AlwaysRerun added).
    let phony (name: string) (body: Map<string, string> -> Recipe<ExecContext, unit>) : ExecContext Rule =
        PhonyRule (name, recipe {
            let! m = getRuleMatches ()
            do! body m
        })

    /// The action's own name: the engine's whole-match group "0" when matched (covers bare
    /// wildcards), the substituted name for a value built by `Make`.
    let name (text: string) (groups: Map<string, string>) =
        match groups.TryFind "0" with
        | Some n -> n
        | None -> subst text groups

    /// `need` of one phony name built from group values (checked like `make`).
    let needOne (name: string) (names: string list) (values: string list) : Recipe<ExecContext, unit> =
        need [ subst name (make [ name ] names values) ]
