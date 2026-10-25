/// The rule pattern grammar, parsed once into tokens: `*`, `**`, `?`, named groups `(name:...)`
/// and literals. Everything that reads the grammar derives from these tokens -- the matching regex
/// (`toRegex`), the specificity of a rule (`specificity`) and the inverse of matching, generating a
/// path from group values (`format`, `glob`). Public, so that typed patterns can build on it.
///
/// `( ) [ ] { } |` are reserved for future pattern elements (outside the named-group structure).
/// `\` before a reserved character makes it a literal (`\(`, `\|`, ...). An unescaped reserved
/// character keeps its former meaning for now -- `( ) [ ] |` reach the matching regex as is, `{ }`
/// are literals -- and is reported once per pattern (see `Warnings`); a later release makes it an
/// error. `[c]`, a one-character class, is read as the literal `c` (the former escape idiom), and a
/// `\` that escapes nothing is a path separator; both are reported as deprecated too.
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
    /// Literal text, matched as is (escapes resolved: `\(` is the literal "(").
    | Lit of string
    /// `*`  — a run of non-separator characters.
    | Star
    /// `**` — anything, including '/'.
    | DoubleStar
    /// `?`  — a single non-separator character.
    | AnyChar
    /// `(name:...)` — a named capture around its body.
    | Group of string * Token list
    /// An unescaped `(`, `)`, `[`, `]` or `|` outside the named-group structure. It reaches the
    /// matching regex as is (a group, a character class, an alternation), as it always did.
    /// Deprecated: a later release makes it an error.
    | Reserved of char

/// A deprecated construct found while parsing a pattern.
type Issue =
    /// An unescaped reserved character (`( ) [ ] { } |`).
    | ReservedChar of char
    /// `[c]`, read as the literal `c`.
    | ClassLiteral of char
    /// A `\` used as a path separator.
    | BackslashSeparator
    /// Named groups (their names) in a fileset mask, where they are not groups (see `Warnings.checkMask`).
    | NamedGroupInMask of string list

/// The characters reserved for future pattern elements.
let reservedChars = "()[]{}|"
let isReserved (c: char) = reservedChars.IndexOf c >= 0

/// Puts a `\` before every reserved character of a literal text, so that it parses back to itself.
let escape (s: string) =
    if s.IndexOfAny(reservedChars.ToCharArray()) < 0 then s
    else s |> String.collect (fun c -> if isReserved c then "\\" + string c else string c)

let private isWord c = System.Char.IsLetterOrDigit c || c = '_'

// Parses tokens from s starting at i. When inGroup, a top-level ')' ends the run and is left
// for the caller to consume; otherwise ')' is a reserved character.
let rec private parseAt (s: string) (issues: ResizeArray<Issue>) (inGroup: bool) (i0: int) : Token list * int =
    let sb = System.Text.StringBuilder()
    let tokens = System.Collections.Generic.List<Token>()
    let flush () = if sb.Length > 0 then tokens.Add(Lit (sb.ToString())); sb.Clear() |> ignore
    let issue i = if not (issues.Contains i) then issues.Add i
    let mutable i = i0
    let mutable stop = false
    while not stop && i < s.Length do
        let c = s.[i]
        if c = '\\' then
            if i + 1 < s.Length && isReserved s.[i + 1] then
                sb.Append s.[i + 1] |> ignore; i <- i + 2      // an escaped reserved character
            else
                issue BackslashSeparator
                sb.Append '/' |> ignore; i <- i + 1
        elif inGroup && c = ')' then
            stop <- true                                    // caller consumes the ')'
        elif c = '*' && i + 1 < s.Length && s.[i + 1] = '*' then
            flush (); tokens.Add DoubleStar; i <- i + 2
        elif c = '*' then
            flush (); tokens.Add Star; i <- i + 1
        elif c = '?' then
            flush (); tokens.Add AnyChar; i <- i + 1
        elif c = '(' then
            // a group opener is `(` <word+> `:` ; anything else is a reserved '('
            let mutable j = i + 1
            while j < s.Length && isWord s.[j] do j <- j + 1
            if j > i + 1 && j < s.Length && s.[j] = ':' then
                flush ()
                let name = s.Substring(i + 1, j - (i + 1))
                let inner, next = parseAt s issues true (j + 1)
                tokens.Add(Group(name, inner))
                i <- if next < s.Length && s.[next] = ')' then next + 1 else next
            else
                issue (ReservedChar c)
                flush (); tokens.Add(Reserved c); i <- i + 1
        elif c = '[' && i + 2 < s.Length && s.[i + 2] = ']' && "]\\/*?".IndexOf s.[i + 1] < 0 then
            // `[c]`: a one-character class is the literal c, as the regex read it
            issue (ClassLiteral s.[i + 1])
            sb.Append s.[i + 1] |> ignore; i <- i + 3
        elif c = '{' || c = '}' then
            issue (ReservedChar c)
            sb.Append c |> ignore; i <- i + 1               // `{ }` were always literal
        elif isReserved c then
            issue (ReservedChar c)
            flush (); tokens.Add(Reserved c); i <- i + 1
        else
            sb.Append c |> ignore; i <- i + 1
    flush ()
    List.ofSeq tokens, i

/// Parses an Ant-style pattern into tokens, with the deprecated constructs it uses. Never fails:
/// a `\` that escapes nothing is a '/', an unclosed group ends the pattern.
let parseWithIssues (pattern: string) : Token list * Issue list =
    let issues = ResizeArray()
    let tokens = parseAt pattern issues false 0 |> fst
    tokens, List.ofSeq issues

/// Parses an Ant-style pattern into tokens (see `parseWithIssues`).
let parse (pattern: string) : Token list = parseWithIssues pattern |> fst

/// The warning for the deprecated constructs of a pattern, or None when it uses none.
let warningText (pattern: string) (issues: Issue list) : string option =
    let quote (c: char) = "'" + string c + "'"
    let reserved = issues |> List.choose (function ReservedChar c -> Some c | _ -> None)
    let parts =
        [ if not reserved.IsEmpty then
            yield sprintf "the reserved character%s %s will be an error in a future release; write %s for a literal"
                    (if reserved.Length > 1 then "s" else "")
                    (reserved |> List.map quote |> String.concat ", ")
                    (reserved |> List.map (fun c -> "\\" + string c) |> String.concat " ")
          for i in issues do
            match i with
            | ClassLiteral c ->
                yield sprintf "'[%c]' is read as the literal %s for compatibility; write %s instead, or %s for the brackets themselves"
                        c (quote c) (escape (string c)) (escape ("[" + string c + "]"))
            | _ -> ()
          for i in issues do
            match i with
            | NamedGroupInMask names ->
                yield sprintf "named groups (%s) are only meaningful in rule patterns; in a mask they are not groups: an include reads '(name:' and ')' as literal text (a segment with a wildcard then finds no files) and an exclude segment with a wildcard matches the group's body -- this will be an error in a future release; write \\( \\) for literal parentheses"
                        (names |> List.map (fun n -> "'(" + n + ":...)'") |> String.concat ", ")
            | _ -> ()
          if issues |> List.contains BackslashSeparator then
            yield "'\\' as a path separator is deprecated; use '/'" ]
    match parts with
    | [] -> None
    | _ -> Some (sprintf "warning: pattern '%s': %s." pattern (String.concat "; " parts))

/// An invalid pattern (an unbalanced reserved `(`, `)` or `[`): the message names the pattern.
exception PatternException of string
    with override this.Message = match this :> exn with PatternException m -> m | _ -> "invalid pattern"

// Regex fragments. Triple-quoted so the `\\` reaches the regex as a literal backslash inside the
// character class.
let private starRx  = """([^/\\]*)"""
let private anyRx   = """([^/\\])"""
let private dstarRx = "(.*)"
// Literal text is matched literally: every regex metacharacter a path can hold is escaped (a
// literal comes from the pattern or from the project root a rule pattern is joined to).
let private metachars = Regex(@"[.$^+!={}()\[\]|\\*?]", RegexOptions.Compiled)
let private escapeLiteral (s: string) = metachars.Replace(s, @"\$0")

/// The inner (unanchored) regex for a token list. Every wildcard is an unnamed capture group, so
/// the regex numbers ("1", "2", ...) of `getRuleMatches` count the wildcards left to right, at any
/// depth (an unescaped reserved '(' opens an unnamed capture too and takes a number).
let rec toRegex (tokens: Token list) : string =
    tokens
    |> List.map (function
        | Lit s -> escapeLiteral s
        | Star -> starRx
        | DoubleStar -> dstarRx
        | AnyChar -> anyRx
        | Reserved c -> string c
        | Group (name, inner) -> "(?<" + name + ">" + toRegex inner + ")")
    |> String.concat ""

/// Specificity read straight off the tokens (see the `Specificity` type).
let specificity (tokens: Token list) : Specificity =
    let rec go (lit, dstar, wild) toks =
        toks |> List.fold (fun (lit, dstar, wild) t ->
            match t with
            | Lit s -> lit + s.Length, dstar, wild
            | Reserved _ -> lit + 1, dstar, wild
            | Star -> lit, dstar, wild + 2
            | AnyChar -> lit, dstar, wild + 1
            | DoubleStar -> lit, dstar + 1, wild
            | Group (_, inner) -> go (lit, dstar, wild) inner) (lit, dstar, wild)
    let lit, dstar, wild = go (0, 0, 0) tokens
    lit, -dstar, -wild

// ---------------------------------------------------------------- generation (inverse of matching)

/// Prints tokens back as pattern text (the inverse of `parse`): literals with their reserved
/// characters escaped, separators as '/'.
let rec unparse (tokens: Token list) : string =
    tokens
    |> List.map (function
        | Lit s -> escape s
        | Star -> "*"
        | DoubleStar -> "**"
        | AnyChar -> "?"
        | Reserved c -> string c
        | Group (name, inner) -> "(" + name + ":" + unparse inner + ")")
    |> String.concat ""

/// The text of the tokens with every escape resolved and everything else as written: what an OS
/// file-name glob gets for a fileset mask (it knows only `*` and `?`; the rest is literal there).
let rec plainText (tokens: Token list) : string =
    tokens
    |> List.map (function
        | Lit s -> s
        | Star -> "*"
        | DoubleStar -> "**"
        | AnyChar -> "?"
        | Reserved c -> string c
        | Group (name, inner) -> "(" + name + ":" + plainText inner + ")")
    |> String.concat ""

/// Named groups in order of first appearance, nested ones included.
let groupNames (tokens: Token list) : string list =
    let rec go toks = toks |> List.collect (function Group (n, inner) -> n :: go inner | _ -> [])
    go tokens |> List.distinct

/// Top-level bare wildcards (a wildcard inside a named group belongs to the group), in order, each
/// with the regex group number it is captured under -- the key `getRuleMatches` holds it under.
/// Numbers count every unnamed capture before it: wildcards at any depth and the captures an
/// unescaped reserved '(' opens (outside a reserved `[...]` class).
let wildcards (tokens: Token list) : (Token * int) list =
    let mutable n = 0
    let mutable inClass = false
    let rec walk top (toks: Token list) =
        [ for t in toks do
            match t with
            | Star | DoubleStar | AnyChar ->
                n <- n + 1
                if top then yield t, n
            | Reserved '[' -> inClass <- true
            | Reserved ']' -> inClass <- false
            | Reserved '(' when not inClass -> n <- n + 1
            | Group (_, inner) -> yield! walk false inner
            | Lit _ | Reserved _ -> () ]
    walk true tokens

/// The path for given values: a named group takes `named name`, the i-th top-level bare wildcard
/// (1-based) takes `positional i`, escapes are resolved. No check: a caller that needs one
/// re-matches the result. A pattern made only of literals formats to its literal text.
let format (named: string -> string) (positional: int -> string) (tokens: Token list) : string =
    let mutable i = 0
    tokens
    |> List.map (function
        | Lit s -> s
        | Reserved c -> string c
        | Group (name, _) -> named name
        | Star | DoubleStar | AnyChar -> i <- i + 1; positional i)
    |> String.concat ""

/// A plain Ant mask (no named groups) for partially bound values: a bound group is replaced by its
/// value, an unbound one by its own body (recursively, so nested groups unwrap); an unbound
/// wildcard stays a wildcard. Literals and values have their reserved characters escaped. The
/// result is what `Fileset`'s `includes` takes.
let rec glob (named: string -> string option) (positional: int -> string option) (tokens: Token list) : string =
    let mutable i = 0
    tokens
    |> List.map (function
        | Lit s -> escape s
        | Reserved c -> string c
        | Group (name, inner) ->
            match named name with
            | Some v -> escape v
            | None -> glob named (fun _ -> None) inner
        | Star | DoubleStar | AnyChar as w ->
            i <- i + 1
            match positional i with
            | Some v -> escape v
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

/// Deprecation warnings about the pattern syntax, reported once per pattern text. A build attaches
/// its log while it runs; warnings raised while nothing is attached (e.g. by filesets built when a
/// script is loaded) wait for the next attached log.
module Warnings =

    let private checkedPatterns = System.Collections.Concurrent.ConcurrentDictionary<string, bool>()
    let private gate = obj ()
    let mutable private sinks : (obj * (string -> unit)) list = []
    let private pending = ResizeArray<string>()
    let private maxPending = 1000

    let private deliver (message: string) =
        lock gate (fun () ->
            match sinks with
            | [] -> if pending.Count < maxPending then pending.Add message
            | _ -> for _, sink in sinks do sink message)

    let private report (key: string) (pattern: string) (issues: unit -> Issue list) =
        if not (checkedPatterns.ContainsKey key) && checkedPatterns.TryAdd(key, true) then
            warningText pattern (issues ()) |> Option.iter deliver

    /// Reports the deprecated constructs of a rule pattern, once per pattern text.
    let check (pattern: string) = report pattern pattern (fun () -> parseWithIssues pattern |> snd)

    /// Reports the deprecated constructs of a fileset mask, once per mask text. A mask without named
    /// groups shares its report with the same text used as a rule pattern; a mask with groups is
    /// reported apart, since the groups are deprecated only in a mask.
    let checkMask (mask: string) =
        let tokens, issues = parseWithIssues mask
        match groupNames tokens with
        | [] -> report mask mask (fun () -> issues)
        | names -> report ("\u0000mask:" + mask) mask (fun () -> issues @ [ NamedGroupInMask names ])

    /// Sends the warnings to `sink` until disposed, starting with those waiting for a log.
    let internal attach (sink: string -> unit) : System.IDisposable =
        let key = obj ()
        let waiting =
            lock gate (fun () ->
                sinks <- (key, sink) :: sinks
                let w = List.ofSeq pending in pending.Clear(); w)
        waiting |> List.iter sink
        { new System.IDisposable with
            member _.Dispose() = lock gate (fun () -> sinks <- sinks |> List.filter (fun (k, _) -> not (obj.ReferenceEquals(k, key)))) }
