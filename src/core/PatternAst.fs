/// The rule pattern grammar, parsed once into tokens: `*`, `**`, `?`, named groups `(name:...)`
/// and literals. Everything that reads the grammar derives from these tokens -- the matching regex
/// (`toRegex`), the specificity of a rule (`specificity`) and the inverse of matching, generating a
/// path from group values (`format`, `glob`). Public, so that typed patterns can build on it.
module Xake.PatternAst

open System.Text.RegularExpressions

/// How specific a rule pattern is, higher = more specific. Compared as a tuple, lexicographically:
///   1. literal character count  (chars that are neither a wildcard nor named-group structure)
///   2. fewer `**` spans         (`**` crosses '/', so it is the least specific wildcard)
///   3. lighter wildcard weight  (`?` = 1, `*`/named-group `*` = 2; fewer/tighter slots wins)
/// A named group `(name:*)` contributes no literals for its `(name:`/`)` structure, while the
/// `*`/`**`/`?` inside it count like any other wildcard, so `(cfg:*)` scores exactly like a bare
/// `*`. Literal text, including '/' and text inside a group (e.g. `(root:*/*-a)`), does count.
type Specificity = int * int * int

/// A pattern token. A named group spans to its closing ')'; a nested opener parses as a nested `Group`.
type Token =
    /// Literal text.
    | Lit of string
    /// `*`  — a run of non-separator characters.
    | Star
    /// `**` — anything, including '/'.
    | DoubleStar
    /// `?`  — a single non-separator character.
    | AnyChar
    /// `(name:...)` — a named capture around its body.
    | Group of string * Token list

let private isWord c = System.Char.IsLetterOrDigit c || c = '_'

// Parses tokens from s starting at i. When inGroup, a top-level ')' ends the run and is left
// for the caller to consume; otherwise ')' is an ordinary literal.
let rec private parseAt (s: string) (inGroup: bool) (i0: int) : Token list * int =
    let sb = System.Text.StringBuilder()
    let tokens = System.Collections.Generic.List<Token>()
    let flush () = if sb.Length > 0 then tokens.Add(Lit (sb.ToString())); sb.Clear() |> ignore
    let mutable i = i0
    let mutable stop = false
    while not stop && i < s.Length do
        let c = s.[i]
        if inGroup && c = ')' then
            stop <- true                                    // caller consumes the ')'
        elif c = '*' && i + 1 < s.Length && s.[i + 1] = '*' then
            flush (); tokens.Add DoubleStar; i <- i + 2
        elif c = '*' then
            flush (); tokens.Add Star; i <- i + 1
        elif c = '?' then
            flush (); tokens.Add AnyChar; i <- i + 1
        elif c = '(' then
            // a group opener is `(` <word+> `:` ; anything else is a literal '('
            let mutable j = i + 1
            while j < s.Length && isWord s.[j] do j <- j + 1
            if j > i + 1 && j < s.Length && s.[j] = ':' then
                flush ()
                let name = s.Substring(i + 1, j - (i + 1))
                let inner, next = parseAt s true (j + 1)
                tokens.Add(Group(name, inner))
                i <- if next < s.Length && s.[next] = ')' then next + 1 else next
            else
                sb.Append c |> ignore; i <- i + 1
        else
            sb.Append c |> ignore; i <- i + 1
    flush ()
    List.ofSeq tokens, i

/// Parses an Ant-style pattern into tokens (slashes normalized to '/'). Never fails: a '(' that
/// does not open `(name:` and a ')' outside a group are literals; an unclosed group ends the pattern.
let parse (pattern: string) : Token list =
    parseAt (pattern.Replace('\\', '/')) false 0 |> fst

// Regex fragments. Triple-quoted so the `\\` reaches the regex as a literal backslash inside the
// character class.
let private starRx  = """([^/\\]*)"""
let private anyRx   = """([^/\\])"""
let private dstarRx = "(.*)"
// The former substitution escaped only these metacharacters; '[' and ']' are deliberately
// absent, so they pass through unescaped exactly as before (no behavior change).
let private metachars = Regex(@"[.$^+!={}]", RegexOptions.Compiled)
let private escapeLiteral (s: string) = metachars.Replace(s, @"\$0")

/// The inner (unanchored) regex for a token list. Every wildcard is an unnamed capture group, so
/// the regex numbers ("1", "2", ...) of `getRuleMatches` count the wildcards left to right, at any depth.
let rec toRegex (tokens: Token list) : string =
    tokens
    |> List.map (function
        | Lit s -> escapeLiteral s
        | Star -> starRx
        | DoubleStar -> dstarRx
        | AnyChar -> anyRx
        | Group (name, inner) -> "(?<" + name + ">" + toRegex inner + ")")
    |> String.concat ""

/// Specificity read straight off the tokens (see the `Specificity` type).
let specificity (tokens: Token list) : Specificity =
    let rec go (lit, dstar, wild) toks =
        toks |> List.fold (fun (lit, dstar, wild) t ->
            match t with
            | Lit s -> lit + s.Length, dstar, wild
            | Star -> lit, dstar, wild + 2
            | AnyChar -> lit, dstar, wild + 1
            | DoubleStar -> lit, dstar + 1, wild
            | Group (_, inner) -> go (lit, dstar, wild) inner) (lit, dstar, wild)
    let lit, dstar, wild = go (0, 0, 0) tokens
    lit, -dstar, -wild

// ---------------------------------------------------------------- generation (inverse of matching)

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

let rec private wildcardCount (tokens: Token list) =
    tokens |> List.sumBy (function
        | Star | DoubleStar | AnyChar -> 1
        | Lit _ -> 0
        | Group (_, inner) -> wildcardCount inner)

/// Top-level bare wildcards (a wildcard inside a named group belongs to the group), in order, each
/// with the regex group number it is captured under -- the key `getRuleMatches` holds it under.
let wildcards (tokens: Token list) : (Token * int) list =
    let mutable before = 0
    [ for t in tokens do
        match t with
        | Star | DoubleStar | AnyChar ->
            before <- before + 1
            yield t, before
        | other -> before <- before + wildcardCount [ other ] ]

/// The path for given values: a named group takes `named name`, the i-th top-level bare wildcard
/// (1-based) takes `positional i`. No check: a caller that needs one re-matches the result.
/// A pattern made only of literals formats to itself.
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

/// The path a pattern gives for the groups of a rule match (as `getRuleMatches` holds them: named
/// groups by name, wildcards by regex number). A missing value gives "" (as `getRuleMatch` does).
/// This is how a multi-file rule names its other targets.
let formatMatches (matches: Map<string, string>) (pattern: string) : string =
    let tokens = parse pattern
    let nums = wildcards tokens |> List.map snd |> Array.ofList
    let find key = matches |> Map.tryFind key |> Option.defaultValue ""
    format find (fun i -> find (string nums.[i - 1])) tokens
