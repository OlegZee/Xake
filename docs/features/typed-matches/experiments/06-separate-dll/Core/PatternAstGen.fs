/// Experiment 05: the generation side proposed for `PatternAst` (would sit in the same file as
/// parse/toRegex/specificity). Pure functions over `Token list`; no Xake types, no I/O.
module Xake.PatternAstGen

open Xake.PatternAst

/// Prints tokens back as pattern text (the inverse of `parse`; slashes come back as '/').
let rec unparse (tokens: Token list) : string =
    tokens
    |> List.map (function
        | Lit s -> s
        | Star -> "*"
        | DoubleStar -> "**"
        | AnyChar -> "?"
        | Group (name, inner) -> "(" + name + ":" + unparse inner + ")")
    |> String.concat ""

/// Named groups in order of first appearance, nested ones included.
let groupNames (tokens: Token list) : string list =
    let rec go toks = toks |> List.collect (function Group (n, inner) -> n :: go inner | _ -> [])
    go tokens |> List.distinct

/// How many unnamed capturing groups `toRegex` emits for these tokens: one per wildcard at any
/// depth, plus one per '(' left in a literal (escapeLiteral does not escape parentheses, so a
/// literal "(x)" is a regex group too).
let rec private unnamedCount (tokens: Token list) =
    tokens |> List.sumBy (function
        | Star | DoubleStar | AnyChar -> 1
        | Lit s -> s |> Seq.filter ((=) '(') |> Seq.length
        | Group (_, inner) -> unnamedCount inner)

/// Top-level bare wildcards (a wildcard inside a named group belongs to the group), in order,
/// each with the .NET regex group number it is captured under. .NET numbers unnamed groups first,
/// left to right, so that number is the key `getRuleMatches` uses for it ("1", "2", ...).
let wildcards (tokens: Token list) : (Token * int) list =
    let mutable before = 0
    [ for t in tokens do
        match t with
        | Star | DoubleStar | AnyChar ->
            before <- before + 1
            yield t, before
        | other -> before <- before + unnamedCount [ other ] ]

/// The path for given values: a named group takes `named name`, the i-th top-level bare wildcard
/// (1-based) takes `positional i`. No check here; the caller re-matches the result.
let format (named: string -> string) (positional: int -> string) (tokens: Token list) : string =
    let mutable i = 0
    tokens
    |> List.map (function
        | Lit s -> s
        | Group (name, _) -> named name
        | Star | DoubleStar | AnyChar -> i <- i + 1; positional i)
    |> String.concat ""

/// A plain Ant mask (no named groups) for partially bound values: a bound group is replaced by its
/// value, an unbound one by its own body (recursively, so nested groups unwrap); an unbound
/// wildcard stays a wildcard. The result is what `Fileset`'s `includes` takes.
let rec glob (named: string -> string option) (positional: int -> string option) (tokens: Token list) : string =
    let mutable i = 0
    tokens
    |> List.map (function
        | Lit s -> s
        | Group (name, inner) ->
            match named name with
            | Some v -> v
            | None -> glob named (fun _ -> None) inner
        | Star | DoubleStar | AnyChar as w ->
            i <- i + 1
            match positional i with
            | Some v -> v
            | None -> unparse [ w ])
    |> String.concat ""
