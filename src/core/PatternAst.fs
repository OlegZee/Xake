/// The pattern module: one grammar, one parser and one AST for rule patterns and masks
/// (docs/features/pattern-module/spec.md, sections 4, 5, 7 and 8; decisions D1-D34 in brief.md).
///
/// This file holds the parser stage (PR 2): the AST, the lexer with its class mode, the parser with
/// a context argument (`Rule` or `Mask`), errors and parse-time warnings, `unparse`, `escape`,
/// `escapePath`, `groupNames`, `wildcards` and `specificity`. The matcher, the scanner and the
/// engine wiring come in later stages; nothing in the engine uses this module yet.
///
/// Not opened by `open Xake`: scripts write `PatternAst.Rule.parse` and so on.
module Xake.PatternAst

open System
open System.Text
open System.Collections.Generic
open System.Collections.Concurrent

// =================================================================================================
// AST (spec 4.4)
// =================================================================================================

/// One item of a character class.
type ClassItem =
    | One of char
    | Range of char * char

/// One element of a parsed pattern.
type Token =
    /// literal text, escapes resolved, no separator
    | Lit of string
    /// '/' (or a deprecated '\' separator)
    | Sep
    /// '*'
    | Star
    /// '?'
    | AnyChar
    /// '**' as a whole segment (spec 4.3)
    | Globstar
    /// '**' inside a segment: matches as '*'; kept for unparse and XP112
    | DoubleStar
    /// '[...]'
    | Class of negated: bool * items: ClassItem list
    /// '{a,b,...}'; each alternative is a token list
    | Alt of Token list list
    /// '(name:body)', rule patterns only
    | Group of name: string * body: Token list

/// The two parse contexts (D9).
[<RequireQualifiedAccess>]
type PatternContext = Rule | Mask

/// The two uses of a mask; selects the 3.x meaning a warning names (spec 7.3, D24).
[<RequireQualifiedAccess>]
type MaskRole = Include | Exclude

/// One parse-time warning part (spec 7.3).
type Issue = { Code: string; Text: string }

/// A parse error (spec 7.2). `ToString()` is the exception message.
type PatternError =
    { Pattern: string
      Context: PatternContext
      /// 1-based character position, when the error has one.
      Position: int option
      Code: string
      Reason: string }
    override e.ToString() =
        let ctx = match e.Context with PatternContext.Rule -> "rule" | PatternContext.Mask -> "mask"
        let at = match e.Position with Some n -> sprintf " at character %d" n | None -> ""
        sprintf "Invalid pattern '%s' (%s)%s: %s [%s]" e.Pattern ctx at e.Reason e.Code

/// An invalid pattern, or a rule whose outputs disagree. The message names the pattern.
exception PatternException of string
    with
        override this.Message = this.Data0

/// The characters that `\` escapes in Xake 4.0 (tm 55).
let reservedChars = "()[]{}|"

// =================================================================================================
// escape, escapePath, unparse (spec 5.7)
// =================================================================================================

let private escapeCharTo (sb: StringBuilder) (inBraces: bool) (c: char) =
    if reservedChars.IndexOf c >= 0 then sb.Append('\\').Append(c) |> ignore
    elif c = '*' then sb.Append("[*]") |> ignore
    elif c = '?' then sb.Append("[?]") |> ignore
    elif c = '\\' then sb.Append(@"[\\]") |> ignore
    elif c = ',' && inBraces then sb.Append("[,]") |> ignore
    else sb.Append(c) |> ignore

/// A plain string as pattern text that matches exactly that string; '/' stays a separator.
let escape (s: string) : string =
    let sb = StringBuilder()
    for c in s do escapeCharTo sb false c
    sb.ToString()

/// A real file-system path as a mask naming exactly that path (D7): every '\' is a separator,
/// a drive or UNC root stays the root, every metacharacter of the segments is escaped.
let escapePath (p: string) : string = escape (p.Replace('\\', '/'))

let private classCharTo (sb: StringBuilder) (first: bool) (c: char) =
    match c with
    | '\\' | ']' | '-' | '^' | '!' -> sb.Append('\\').Append(c) |> ignore
    | '[' when first -> sb.Append(@"\[") |> ignore
    | _ -> sb.Append(c) |> ignore

/// The canonical text of a token list; `parse (unparse tokens)` gives the tokens back.
let unparse (tokens: Token list) : string =
    let sb = StringBuilder()
    let rec go inBraces (tokens: Token list) =
        for t in tokens do
            match t with
            | Lit s -> for c in s do escapeCharTo sb inBraces c
            | Sep -> sb.Append('/') |> ignore
            | Star -> sb.Append('*') |> ignore
            | AnyChar -> sb.Append('?') |> ignore
            | Globstar | DoubleStar -> sb.Append("**") |> ignore
            | Class (negated, items) ->
                sb.Append('[') |> ignore
                if negated then sb.Append('^') |> ignore
                items |> List.iteri (fun k item ->
                    match item with
                    | One c -> classCharTo sb (k = 0) c
                    | Range (a, b) ->
                        classCharTo sb (k = 0) a
                        sb.Append('-') |> ignore
                        classCharTo sb false b)
                sb.Append(']') |> ignore
            | Alt alts ->
                sb.Append('{') |> ignore
                alts |> List.iteri (fun k alt ->
                    if k > 0 then sb.Append(',') |> ignore
                    go true alt)
                sb.Append('}') |> ignore
            | Group (name, body) ->
                sb.Append('(').Append(name).Append(':') |> ignore
                go false body
                sb.Append(')') |> ignore
    go false tokens
    sb.ToString()

// =================================================================================================
// Error and warning texts (spec 7.2, 7.3)
// =================================================================================================

module private Reasons =
    let xp002 = @"an unescaped '[' has no matching ']' (Xake 3.x read it as a literal in masks and failed in rules); write \[ for a literal '['"
    let xp003 (ctx: PatternContext) (c: char) =
        match ctx with
        | PatternContext.Rule ->
            if c = '|' then @"'|' is reserved (Xake 3.x: a regex alternation); write \| for a literal '|', or {a,b} for alternatives"
            else sprintf @"'%c' is reserved (Xake 3.x: a regex group); write \%c for a literal '%c'" c c c
        | PatternContext.Mask ->
            sprintf @"'%c' is reserved (Xake 3.x: a literal character in masks); write \%c for a literal '%c', or build the mask from a real path with PatternAst.escapePath" c c c
    let xp003Alt (groupText: string) (name: string) (alts: string[]) =
        sprintf "'%s': '|' is reserved (Xake 3.x: a regex alternation); write (%s:{%s})" groupText name (String.Join(",", alts))
    let xp004Open = @"an unescaped '{' has no matching '}' (Xake 3.x: a literal); write \{ for a literal '{'"
    let xp004Brace = @"an unescaped '}' closes no '{' (Xake 3.x: a literal); write \} for a literal '}'"
    let xp004Bracket = @"an unescaped ']' closes no '[' (Xake 3.x: a literal); write \] for a literal ']'"
    let xp005 (text: string) =
        sprintf "'%s' is not an alternation (no ','; Xake 3.x read it as literal text); write %s for literal braces, or {a,b} for alternatives" text (escape text)
    let xp006 (name: string) =
        sprintf "the group '(%s:' is not closed (Xake 3.x: in a string rule it ran to the end of the pattern); add ')'" name
    let xp007 (name: string) =
        sprintf "the group name '%s' starts with a digit; it would collide with the positional match '%s' (Xake 3.x accepted it); rename it" name name
    let xp008 = @"named groups '(name:...)' exist only in rule patterns (Xake 3.x: an include read the group as literal text, an exclude wildcard segment matched its body); write the body alone, or \( \) for literal parentheses; PatternAst.escapePath escapes a real path"
    let xp010 (text: string) (detail: string) = sprintf "invalid character class '%s': %s" text detail
    let xp011 = "a rule pattern names a file or a phony target and cannot end with '/' (in Xake 3.x it matched nothing)"
    let xp012Parent = "'..' is allowed only before the first wildcard (Xake 3.x applied it as a parent step in masks and never matched it in rules)"
    let xp012Dot = "'.' is allowed only before the first wildcard; remove the segment"
    let xp015 (name: string) (count: int) =
        sprintf "the group name '%s' appears %d times; a name may appear once per pattern (Xake 3.x let each occurrence capture and the last one win); rename one occurrence, or put one group around the braces: (x:{a,b})" name count
    let xp016 = "a UNC root '//server/share' needs a literal server and share name"
    let xp017 = @"'\,' is not an escaped comma in Xake 4.0 (Xake 3.x: a path separator, then ','); write [,] for a literal comma"
    let xp018 = "an empty pattern names nothing; write the file or directory path, or '*' for the files of the base directory"
    let xp019 (text: string) (replacement: string) =
        sprintf "'%s' ends with '/' in one alternative only; write %s when every alternative is a directory, or two patterns" text replacement

    let xp105 (text: string) (c: char) =
        if reservedChars.IndexOf c >= 0 then sprintf @"'%s' is the literal '%c' (as in Xake 3.x); write \%c instead" text c c
        else sprintf "'%s' is the literal '%c'; write %c instead" text c c
    let xp106 (text: string) (c: char) =
        sprintf "'%s' matches the single character '%c' from Xake 4.0; Xake 3.x read the literal text '%s'; write %s for the brackets, or %s for the character"
            text c text (escape text) (escape (string c))
    let xp110 = @"'\' as a path separator is deprecated and is only an escape from Xake 5.0; use '/'"
    let xp112 (segment: string) =
        sprintf "'%s' matches within one segment from Xake 4.0, like '%s'; in Xake 3.x it crossed '/'; write '*' for one segment, or '/**/' between segments for any depth"
            segment (segment.Replace("**", "*"))
    let xp117 (text: string) =
        let body = text.Substring(2, text.Length - 3)
        sprintf "'%s' is a negated class (any character but '%s') from Xake 4.0; in Xake 3.x it was a class of '!' and '%s'; write [^%s] for \"not %s\" (same meaning in 3.x and 4.0), or [%s!] for the 3.x meaning"
            text body body body body body

// =================================================================================================
// Lexer and parser (spec 4.1, 4.2): characters to a raw tree with positions
// =================================================================================================

type private ClassInfo =
    { CPos: int; CText: string; Negated: bool; Bang: bool; Items: ClassItem list }

/// The parser's tree before the structural rules of 4.2; positions are 0-based indexes.
type private Raw =
    | RChar of char * int
    | RSep of int * backslash: bool
    | RStar of int
    | RQ of int
    | RDStar of int
    | RClass of ClassInfo
    | RAlt of pos: int * text: string * alts: (Raw list * string) list
    | RGroup of name: string * body: Raw list * pos: int

exception internal PatternFail of code: string * pos: int option * reason: string

let private failAt (code: string) (pos: int) (reason: string) : 'a =
    raise (PatternFail (code, Some (pos + 1), reason))

let private rawPos = function
    | RChar (_, p) | RSep (p, _) | RStar p | RQ p | RDStar p -> p
    | RClass c -> c.CPos
    | RAlt (p, _, _) -> p
    | RGroup (_, _, p) -> p

type private SeqLevel = Top | InBrace | InGroup

type private RawParser(ctx: PatternContext, s: string) =
    let n = s.Length
    let mutable i = 0
    let mutable groups : (int * string) list = []
    let mutable classSlash = false

    let isReserved (c: char) = reservedChars.IndexOf c >= 0
    let isNameStart (c: char) = Char.IsLetter c || c = '_'
    let isNameChar (c: char) = Char.IsLetterOrDigit c || c = '_'

    /// For a '(' at p: the index of the ':' when name characters and ':' follow.
    let groupHead (p: int) =
        let mutable j = p + 1
        while j < n && isNameChar s.[j] do j <- j + 1
        if j > p + 1 && j < n && s.[j] = ':' then Some j else None

    /// XP003 for a '|' at p: inside a group whose alternatives are literals the text suggests braces.
    let pipeReason () =
        match groups with
        | (gpos, name) :: _ ->
            let mutable depth = 0
            let mutable close = -1
            let mutable j = gpos
            while close < 0 && j < n do
                let c = s.[j]
                if c = '\\' then j <- j + 1
                elif c = '(' then depth <- depth + 1
                elif c = ')' then
                    depth <- depth - 1
                    if depth = 0 then close <- j
                j <- j + 1
            if close > 0 then
                let groupText = s.Substring(gpos, close - gpos + 1)
                let bodyStart = gpos + name.Length + 2
                let alts = s.Substring(bodyStart, close - bodyStart).Split('|')
                let plain (a: string) = a.Length > 0 && a.IndexOfAny("*?[]{}()\\/,".ToCharArray()) < 0
                if Array.forall plain alts then Reasons.xp003Alt groupText name alts
                else Reasons.xp003 ctx '|'
            else Reasons.xp003 ctx '|'
        | [] -> Reasons.xp003 ctx '|'

    /// One class character, an escape resolved; None at the end of the text.
    let readClassChar () : char option =
        if i >= n then None
        elif s.[i] = '\\' then
            if i + 1 >= n then
                i <- n
                None
            else
                let c = s.[i + 1]
                if c = '/' then classSlash <- true
                i <- i + 2
                Some c
        else
            let c = s.[i]
            if c = '/' then classSlash <- true
            i <- i + 1
            Some c

    /// Class mode (4.1): the whole '[...]' as one token.
    let readClass () : Raw =
        let start = i
        i <- i + 1
        classSlash <- false
        let mutable negated = false
        let mutable bang = false
        if i < n && (s.[i] = '!' || s.[i] = '^') then
            negated <- true
            bang <- s.[i] = '!'
            i <- i + 1
        let bodyStart = i
        let items = ResizeArray<ClassItem>()
        let mutable reversed : string option = None
        let mutable closed = false
        let mutable first = true
        while not closed && i < n do
            if s.[i] = ']' && not first then
                closed <- true
                i <- i + 1
            else
                first <- false
                match readClassChar () with
                | None -> ()
                | Some a ->
                    if i + 1 < n && s.[i] = '-' && s.[i + 1] <> ']' then
                        i <- i + 1
                        match readClassChar () with
                        | None -> ()
                        | Some b ->
                            if b < a && reversed.IsNone then reversed <- Some (sprintf "%c-%c" a b)
                            items.Add(Range (a, b))
                    else items.Add(One a)
        if not closed then failAt "XP002" start Reasons.xp002
        let text = s.Substring(start, i - start)
        if bodyStart + 1 < n && s.[bodyStart] = '[' && s.[bodyStart + 1] = ':' then
            failAt "XP010" start (Reasons.xp010 text "POSIX class names '[:...:]' are reserved")
        if classSlash then failAt "XP010" start (Reasons.xp010 text "a class never matches '/'")
        match reversed with
        | Some r -> failAt "XP010" start (Reasons.xp010 text (sprintf "the range '%s' is reversed" r))
        | None -> ()
        match negated, List.ofSeq items with
        // the 4.0 literal spelling of '*', '?', ',' and '\' (4.1, D22, D34; 5.7: parse (escape s) = Lit s)
        | false, [ One c ] when "*?,\\".IndexOf c >= 0 -> RChar (c, start)
        | _, its -> RClass { CPos = start; CText = text; Negated = negated; Bang = bang; Items = its }

    /// A sequence up to its terminator: '\000' (end of text), ',' or '}' (in braces), ')' (in a group).
    let rec parseSeq (level: SeqLevel) : Raw list * char =
        let nodes = ResizeArray<Raw>()
        let mutable term = ' '
        let mutable go = true
        while go do
            if i >= n then
                term <- '\000'
                go <- false
            else
                let c = s.[i]
                match c with
                | '\\' ->
                    if i + 1 < n && isReserved s.[i + 1] then
                        nodes.Add(RChar (s.[i + 1], i))
                        i <- i + 2
                    elif level = InBrace && i + 1 < n && s.[i + 1] = ',' then
                        failAt "XP017" i Reasons.xp017
                    else
                        nodes.Add(RSep (i, true))
                        i <- i + 1
                | '/' ->
                    nodes.Add(RSep (i, false))
                    i <- i + 1
                | '*' ->
                    if i + 1 < n && s.[i + 1] = '*' then
                        nodes.Add(RDStar i)
                        i <- i + 2
                    else
                        nodes.Add(RStar i)
                        i <- i + 1
                | '?' ->
                    nodes.Add(RQ i)
                    i <- i + 1
                | '[' -> nodes.Add(readClass ())
                | ']' -> failAt "XP004" i Reasons.xp004Bracket
                | '{' -> nodes.Add(readBraces ())
                | '}' ->
                    if level = InBrace then
                        term <- '}'
                        go <- false
                    else failAt "XP004" i Reasons.xp004Brace
                | ',' when level = InBrace ->
                    term <- ','
                    go <- false
                | '(' -> nodes.Add(readGroup ())
                | ')' ->
                    if level = InGroup then
                        term <- ')'
                        go <- false
                    else failAt "XP003" i (Reasons.xp003 ctx ')')
                | '|' -> failAt "XP003" i (pipeReason ())
                | _ ->
                    nodes.Add(RChar (c, i))
                    i <- i + 1
        List.ofSeq nodes, term

    and readBraces () : Raw =
        let start = i
        i <- i + 1
        let alts = ResizeArray<Raw list * string>()
        let mutable fin = false
        while not fin do
            let altStart = i
            let nodes, term = parseSeq InBrace
            alts.Add((nodes, s.Substring(altStart, i - altStart)))
            match term with
            | ',' -> i <- i + 1
            | '}' ->
                i <- i + 1
                fin <- true
            | _ -> failAt "XP004" start Reasons.xp004Open
        let text = s.Substring(start, i - start)
        if alts.Count < 2 then failAt "XP005" start (Reasons.xp005 text)
        RAlt (start, text, List.ofSeq alts)

    and readGroup () : Raw =
        let start = i
        match groupHead start with
        | Some colon ->
            let name = s.Substring(start + 1, colon - start - 1)
            match ctx with
            | PatternContext.Mask ->
                // no group_open token in a mask (D9, D23, D24)
                if isNameStart name.[0] then failAt "XP008" start Reasons.xp008
                else failAt "XP003" start (Reasons.xp003 ctx '(')
            | PatternContext.Rule ->
                if not (isNameStart name.[0]) then failAt "XP007" start (Reasons.xp007 name)
                i <- colon + 1
                groups <- (start, name) :: groups
                let body, term = parseSeq InGroup
                groups <- List.tail groups
                if term <> ')' then failAt "XP006" start (Reasons.xp006 name)
                i <- i + 1
                RGroup (name, body, start)
        | None -> failAt "XP003" start (Reasons.xp003 ctx '(')

    member _.Parse() : Raw list = fst (parseSeq Top)

// =================================================================================================
// Structure (spec 4.2, 4.3): roots, separators, '.' and '..', trailing '/', globstars
// =================================================================================================

let private isSep = function RSep _ -> true | _ -> false
let private isChar = function RChar _ -> true | _ -> false
let private charsText (nodes: Raw list) =
    String(nodes |> List.map (function RChar (c, _) -> c | _ -> '?') |> Array.ofList)
let rec private dropSeps = function RSep _ :: t -> dropSeps t | l -> l

/// Splits at separators; empty segments (runs of separators) are left out.
let private splitSegments (nodes: Raw list) : Raw list list =
    let segs = ResizeArray<Raw list>()
    let cur = ResizeArray<Raw>()
    for node in nodes do
        match node with
        | RSep _ ->
            if cur.Count > 0 then segs.Add(List.ofSeq cur)
            cur.Clear()
        | _ -> cur.Add node
    if cur.Count > 0 then segs.Add(List.ofSeq cur)
    List.ofSeq segs

let rec private nestedSequences (nodes: Raw list) : Raw list list =
    nodes |> List.collect (function
        | RAlt (_, _, alts) -> alts |> List.collect (fun (a, _) -> a :: nestedSequences a)
        | RGroup (_, body, _) -> body :: nestedSequences body
        | _ -> [])

let rec private groupsOf (nodes: Raw list) : (string * int) list =
    nodes |> List.collect (function
        | RGroup (name, body, p) -> (name, p) :: groupsOf body
        | RAlt (_, _, alts) -> alts |> List.collect (fst >> groupsOf)
        | _ -> [])

let rec private classesOf (nodes: Raw list) : ClassInfo list =
    nodes |> List.collect (function
        | RClass c -> [ c ]
        | RAlt (_, _, alts) -> alts |> List.collect (fst >> classesOf)
        | RGroup (_, body, _) -> classesOf body
        | _ -> [])

let rec private hasBackslashSep (nodes: Raw list) : bool =
    nodes |> List.exists (function
        | RSep (_, true) -> true
        | RAlt (_, _, alts) -> alts |> List.exists (fst >> hasBackslashSep)
        | RGroup (_, body, _) -> hasBackslashSep body
        | _ -> false)

/// Braces that end the pattern with an alternative ending in a separator (XP019).
let rec private endingSepAlt (node: Raw) : (int * string * (Raw list * string) list) option =
    match node with
    | RAlt (p, text, alts) ->
        let endsWithSep (nodes: Raw list, _) = match List.tryLast nodes with Some (RSep _) -> true | _ -> false
        if alts |> List.exists endsWithSep then Some (p, text, alts)
        else alts |> List.tryPick (fun (nodes, _) -> List.tryLast nodes |> Option.bind endingSepAlt)
    | RGroup (_, body, _) -> List.tryLast body |> Option.bind endingSepAlt
    | _ -> None

type private Flat<'t> =
    | FNode of 't
    | FOpen
    | FClose

/// The position rule for '**' (4.3, D34): group delimiters are transparent, and the alternatives of
/// braces see the braces' neighbours. Collects the positions of segment-local double stars.
let rec private classifyDStars (local: HashSet<int>) (nodes: Raw list) (leftOk: bool) (rightOk: bool) =
    let rec flatten (nodes: Raw list) =
        nodes |> List.collect (function
            | RGroup (_, body, _) -> FOpen :: flatten body @ [ FClose ]
            | n -> [ FNode n ])
    let flat = flatten nodes |> Array.ofList
    let boundary (k: int) (step: int) (atEdge: bool) =
        let mutable j = k + step
        while j >= 0 && j < flat.Length && (match flat.[j] with FOpen | FClose -> true | _ -> false) do
            j <- j + step
        if j < 0 || j >= flat.Length then atEdge
        else match flat.[j] with FNode (RSep _) -> true | _ -> false
    for k in 0 .. flat.Length - 1 do
        match flat.[k] with
        | FNode (RDStar p) ->
            if not (boundary k (-1) leftOk && boundary k 1 rightOk) then local.Add p |> ignore
        | FNode (RAlt (_, _, alts)) ->
            let l, r = boundary k (-1) leftOk, boundary k 1 rightOk
            for (alt, _) in alts do classifyDStars local alt l r
        | _ -> ()

/// Adjacent globstars collapse to one (5.5, D34).
let rec private collapseGlobstars (tokens: Token list) =
    match tokens with
    | Globstar :: Sep :: Globstar :: rest -> collapseGlobstars (Globstar :: rest)
    | t :: rest -> t :: collapseGlobstars rest
    | [] -> []

let rec private toTokens (local: HashSet<int>) (nodes: Raw list) : Token list =
    let out = ResizeArray<Token>()
    let sb = StringBuilder()
    let flush () =
        if sb.Length > 0 then
            out.Add(Lit (sb.ToString()))
            sb.Clear() |> ignore
    let mutable prevSep = false
    for node in nodes do
        match node with
        | RChar (c, _) ->
            sb.Append c |> ignore
            prevSep <- false
        | RSep _ ->
            flush ()
            if not prevSep then out.Add Sep
            prevSep <- true
        | other ->
            flush ()
            prevSep <- false
            out.Add(
                match other with
                | RStar _ -> Star
                | RQ _ -> AnyChar
                | RDStar p -> if local.Contains p then DoubleStar else Globstar
                | RClass c -> Class (c.Negated, c.Items)
                | RAlt (_, _, alts) -> Alt (alts |> List.map (fst >> toTokens local))
                | RGroup (name, body, _) -> Group (name, toTokens local body)
                | RChar _ | RSep _ -> failwith "unreachable")
    flush ()
    collapseGlobstars (List.ofSeq out)

type private Parsed =
    { Tokens: Token list
      Raw: Raw list
      /// positions of the segment-local double stars
      Local: HashSet<int> }

let private build (ctx: PatternContext) (raw: Raw list) : Parsed =
    let notSep n = not (isSep n)
    let isDriveLetter (c: char) = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')

    // ---- the root (4.2, S6, D24)
    let rootTokens, rest, unc, rootSepPos =
        match raw with
        | RChar (d, _) :: RChar (':', _) :: RSep (p, _) :: rest when isDriveLetter d ->
            [ Lit (string d + ":"); Sep ], rest, false, Some p
        | RSep (p, _) :: RSep _ :: (x :: _ as afterTwo) when notSep x ->
            let seg1 = List.takeWhile notSep afterTwo
            let seg2, r3 =
                match List.skipWhile notSep afterTwo with
                | RSep _ :: r2 -> List.takeWhile notSep r2, List.skipWhile notSep r2
                | _ -> [], []
            if List.isEmpty seg2 then [ Sep ], dropSeps raw, false, Some p     // '//a': collapsed (D34)
            else
                match seg1 @ seg2 |> List.tryFind (isChar >> not) with
                | Some bad -> failAt "XP016" (rawPos bad) Reasons.xp016
                | None -> [ Sep; Sep; Lit (charsText seg1); Sep; Lit (charsText seg2) ], r3, true, None
        | RSep (p, _) :: _ -> [ Sep ], dropSeps raw, false, Some p
        | _ -> [], raw, false, None

    // ---- segments, trailing separator
    let trailing =
        let rec lastRun (acc: int option) (nodes: Raw list) =
            match nodes with
            | RSep (p, _) :: t -> lastRun (if acc.IsNone then Some p else acc) t
            | _ :: t -> lastRun None t
            | [] -> acc
        lastRun None rest

    // ---- '.' and '..' in the literal prefix; XP012 after the first non-literal segment
    let errors = ResizeArray<string * int * string>()
    let out = ResizeArray<Raw list>()
    let mutable seenWild = false
    for seg in splitSegments rest do
        if seg |> List.forall isChar then
            let t = charsText seg
            if t = "." || t = ".." then
                if seenWild then
                    errors.Add(("XP012", rawPos seg.Head, (if t = "." then Reasons.xp012Dot else Reasons.xp012Parent)))
                elif t = "." then ()
                elif out.Count > 0 && charsText out.[out.Count - 1] <> ".." then out.RemoveAt(out.Count - 1)
                else out.Add seg     // '..' above the start of the pattern stays literal (D34)
            else out.Add seg
        else
            seenWild <- true
            out.Add seg

    if out.Count = 0 && List.isEmpty rootTokens then
        raise (PatternFail ("XP018", None, Reasons.xp018))

    // ---- '.' and '..' segments inside braces and groups are never in the literal prefix
    for sq in nestedSequences raw do
        if List.exists isSep sq then
            for seg in splitSegments sq do
                if seg |> List.forall isChar then
                    match charsText seg with
                    | "." -> errors.Add(("XP012", rawPos seg.Head, Reasons.xp012Dot))
                    | ".." -> errors.Add(("XP012", rawPos seg.Head, Reasons.xp012Parent))
                    | _ -> ()

    // ---- a repeated group name (XP015, D4)
    let groups = groupsOf raw
    let seen = HashSet<string>()
    match groups |> List.tryFind (fun (name, _) -> not (seen.Add name)) with
    | Some (name, p) ->
        let count = groups |> List.filter (fun (g, _) -> g = name) |> List.length
        errors.Add(("XP015", p, Reasons.xp015 name count))
    | None -> ()

    // ---- braces ending the pattern with an alternative ending in a separator (XP019, D29)
    if trailing.IsNone && out.Count > 0 then
        match List.tryLast out.[out.Count - 1] |> Option.bind endingSepAlt with
        | Some (p, text, alts) ->
            let trimmed = alts |> List.map (fun (_, t: string) -> t.TrimEnd('/', '\\'))
            let replacement = "{" + String.Join(",", trimmed) + "}/"
            errors.Add(("XP019", p, Reasons.xp019 text replacement))
        | None -> ()

    // ---- the tokens
    let bodyRaw =
        [ for k, seg in List.indexed (List.ofSeq out) do
            if k > 0 then yield RSep (-1, false)
            yield! seg
          match trailing with
          | Some p when out.Count > 0 -> yield RSep (p, false)
          | _ -> () ]
    let local = HashSet<int>()
    classifyDStars local bodyRaw true true
    let tokens =
        rootTokens
        @ (if unc && (out.Count > 0 || trailing.IsSome) then [ Sep ] else [])
        @ toTokens local bodyRaw

    // ---- a rule cannot end with a separator (XP011)
    if ctx = PatternContext.Rule && List.tryLast tokens = Some Sep then
        let p = match trailing, rootSepPos with Some p, _ | None, Some p -> p | None, None -> 0
        errors.Add(("XP011", p, Reasons.xp011))

    if errors.Count > 0 then
        let code, p, reason = errors |> Seq.minBy (fun (_, p, _) -> p)
        failAt code p reason

    { Tokens = tokens; Raw = raw; Local = local }

let private parseIn (ctx: PatternContext) (text: string) : Result<Parsed, PatternError> =
    let error code pos reason =
        Result.Error { Pattern = text; Context = ctx; Position = pos; Code = code; Reason = reason }
    if String.IsNullOrEmpty text then error "XP018" None Reasons.xp018
    else
        try
            Result.Ok (build ctx (RawParser(ctx, text).Parse()))
        with PatternFail (code, pos, reason) -> error code pos reason

let private raiseError (e: PatternError) : 'a = raise (PatternException (e.ToString()))

// =================================================================================================
// Parse-time warnings (spec 7.3)
// =================================================================================================

type private Use =
    | URule
    | UMask
    | UInclude
    | UExclude

/// The raw text of the segment around position p ('/' and '\' end a segment).
let private segmentAround (text: string) (p: int) (length: int) =
    let isS c = c = '/' || c = '\\'
    let mutable a = p
    while a > 0 && not (isS text.[a - 1]) do a <- a - 1
    let mutable b = min text.Length (p + length)
    while b < text.Length && not (isS text.[b]) do b <- b + 1
    text.Substring(a, b - a)

let private issuesOf (usage: Use) (text: string) (parsed: Parsed) : Issue list =
    let metachars = "*?,\\"
    [ if hasBackslashSep parsed.Raw then yield { Code = "XP110"; Text = Reasons.xp110 }
      if usage = URule then
          for p in Seq.sort parsed.Local do
              yield { Code = "XP112"; Text = Reasons.xp112 (segmentAround text p 2) }
      for c in classesOf parsed.Raw do
          let single =
              match c.Negated, c.Items with
              | false, [ One ch ] when metachars.IndexOf ch < 0 -> Some ch
              | _ -> None
          // 3.x read an exclude segment holding '*' or '?' as a regex ("wildcard segment", 5.2)
          let wildcardSegment () =
              let seg = segmentAround text c.CPos c.CText.Length
              seg.IndexOf '*' >= 0 || seg.IndexOf '?' >= 0
          let xp105 () = match single with Some ch -> [ { Code = "XP105"; Text = Reasons.xp105 c.CText ch } ] | None -> []
          let xp106 () = match single with Some ch -> [ { Code = "XP106"; Text = Reasons.xp106 c.CText ch } ] | None -> []
          let xp117 () = if c.Bang then [ { Code = "XP117"; Text = Reasons.xp117 c.CText } ] else []
          match usage with
          | URule -> yield! xp105 () @ xp117 ()
          | UInclude -> yield! xp106 ()
          | UExclude -> if wildcardSegment () then yield! xp105 () @ xp117 () else yield! xp106 ()
          | UMask -> () ]
    |> List.distinct
    |> List.sortBy (fun i -> i.Code)

/// Parse-time warnings, logged once per (use, pattern text); buffered until a log attaches.
module Warnings =
    let private gate = obj ()
    let private seen = ConcurrentDictionary<string, bool>()
    let private sinks = List<obj * (string -> unit)>()
    let private buffer = Queue<string>()

    let private emit (line: string) =
        lock gate (fun () ->
            if sinks.Count = 0 then
                if buffer.Count < 1000 then buffer.Enqueue line
            else
                for (_, sink) in sinks do sink line)

    /// Sends every warning (the buffered ones first) to the sink until disposed.
    let internal attach (sink: string -> unit) : IDisposable =
        let key = obj ()
        lock gate (fun () ->
            sinks.Add((key, sink))
            while buffer.Count > 0 do sink (buffer.Dequeue()))
        { new IDisposable with
            member _.Dispose() = lock gate (fun () -> sinks.RemoveAll(fun (k, _) -> obj.ReferenceEquals(k, key)) |> ignore) }

    /// The line of spec 7.3: `Pattern '<text>' (<use>): <part>; <part>. [<codes>]`.
    let internal format (usage: string) (pattern: string) (issues: Issue list) =
        let parts = issues |> List.map (fun i -> i.Text) |> String.concat "; "
        let codes = issues |> List.map (fun i -> i.Code) |> List.distinct |> String.concat ", "
        sprintf "Pattern '%s' (%s): %s. [%s]" pattern usage parts codes

    let internal report (usage: string) (pattern: string) (issues: Issue list) =
        if not (List.isEmpty issues) && seen.TryAdd(usage + "\n" + pattern, true) then
            emit (format usage pattern issues)

    /// An evidence or evaluation warning (XP104, XP113, XP114, XP119, XP120): one more line for
    /// the key (use, pattern), at most once per code (7.3).
    let internal reportEvidence (usage: string) (pattern: string) (issue: Issue) =
        if seen.TryAdd(usage + "\n" + pattern + "\n" + issue.Code, true) then
            emit (format usage pattern [ issue ])

// =================================================================================================
// Parsing entry points (spec 8.2, D9, D34)
// =================================================================================================

/// Rule patterns (`..>`, `*..>`, `=>`, `target(s)`, `command`, `phony`).
module Rule =
    /// Parses a rule pattern; raises PatternException. No warnings.
    let parse (pattern: string) : Token list =
        match parseIn PatternContext.Rule pattern with
        | Result.Ok p -> p.Tokens
        | Result.Error e -> raiseError e

    /// Parses a rule pattern with its parse-time warning parts (XP105, XP110, XP112, XP117).
    let tryParse (pattern: string) : Result<Token list * Issue list, PatternError> =
        parseIn PatternContext.Rule pattern
        |> Result.map (fun p -> p.Tokens, issuesOf URule pattern p)

    /// Parses, reports the parse-time warnings once, raises PatternException on an error.
    let check (pattern: string) : unit =
        match tryParse pattern with
        | Result.Ok (_, issues) -> Warnings.report "rule" pattern issues
        | Result.Error e -> raiseError e

/// Masks (`!!`, `ls`, `++`, `--`, fileset includes and excludes).
module Mask =
    /// Parses a mask; raises PatternException. No warnings.
    let parse (pattern: string) : Token list =
        match parseIn PatternContext.Mask pattern with
        | Result.Ok p -> p.Tokens
        | Result.Error e -> raiseError e

    /// Parses a mask with the warning parts that do not depend on the role (XP110).
    let tryParse (pattern: string) : Result<Token list * Issue list, PatternError> =
        parseIn PatternContext.Mask pattern
        |> Result.map (fun p -> p.Tokens, issuesOf UMask pattern p)

    /// Every parse-time warning part of a mask for its role (XP105, XP106, XP110, XP117); empty
    /// for an invalid mask. Pure. Takes the text, not the tokens: `[!a]` vs `[^a]` and a `\`
    /// separator are not in the AST (D34, implemented form; spec 8.2 "PR 2 API notes").
    let warnings (role: MaskRole) (pattern: string) : Issue list =
        match parseIn PatternContext.Mask pattern with
        | Result.Ok p -> issuesOf (match role with MaskRole.Include -> UInclude | MaskRole.Exclude -> UExclude) pattern p
        | Result.Error _ -> []

    /// Parses, reports the warnings for the role once, raises PatternException on an error.
    let check (role: MaskRole) (pattern: string) : unit =
        match parseIn PatternContext.Mask pattern with
        | Result.Ok _ ->
            let usage = match role with MaskRole.Include -> "include" | MaskRole.Exclude -> "exclude"
            Warnings.report usage pattern (warnings role pattern)
        | Result.Error e -> raiseError e

// =================================================================================================
// Reading the AST (spec 5.6, 5.8, 8.2)
// =================================================================================================

/// The named groups in first-appearance order, nested groups included.
let rec groupNames (tokens: Token list) : string list =
    [ for t in tokens do
        match t with
        | Group (name, body) ->
            yield name
            yield! groupNames body
        | Alt alts -> for a in alts do yield! groupNames a
        | _ -> () ]

/// The wildcards outside named groups with their positional numbers (5.6): every `*`, `**`, `?`
/// is numbered left to right at any depth; braces are transparent (D34), groups hide theirs.
let wildcards (tokens: Token list) : (Token * int) list =
    let result = ResizeArray<Token * int>()
    let counter = ref 0
    let rec go inGroup (tokens: Token list) =
        for t in tokens do
            match t with
            | Star | AnyChar | Globstar | DoubleStar ->
                counter.Value <- counter.Value + 1
                if not inGroup then result.Add((t, counter.Value))
            | Group (_, body) -> go true body
            | Alt alts -> for a in alts do go inGroup a
            | _ -> ()
    go false tokens
    List.ofSeq result

/// (literal characters, -globstars, -wildcard weight), compared lexicographically, larger wins
/// (5.8, D5): a `Sep` next to a globstar counts 0 (group delimiters transparent, D34); `*` and a
/// segment-local `**` weigh 2, `?` and a class 1; braces count as their least specific alternative.
let rec specificity (tokens: Token list) : int * int * int =
    let rec flatten (tokens: Token list) =
        tokens |> List.collect (function
            | Group (_, body) -> FOpen :: flatten body @ [ FClose ]
            | t -> [ FNode t ])
    let flat = flatten tokens |> Array.ofList
    let globstarAt (k: int) (step: int) =
        let mutable j = k + step
        while j >= 0 && j < flat.Length && (match flat.[j] with FOpen | FClose -> true | _ -> false) do
            j <- j + step
        j >= 0 && j < flat.Length && flat.[j] = FNode Globstar
    let mutable lit, globstars, weight = 0, 0, 0
    for k in 0 .. flat.Length - 1 do
        match flat.[k] with
        | FNode (Lit s) -> lit <- lit + s.Length
        | FNode Sep -> if not (globstarAt k (-1) || globstarAt k 1) then lit <- lit + 1
        | FNode Globstar -> globstars <- globstars + 1
        | FNode (Star | DoubleStar) -> weight <- weight + 2
        | FNode (AnyChar | Class _) -> weight <- weight + 1
        | FNode (Alt alts) ->
            let a, b, c = alts |> List.map specificity |> List.min
            lit <- lit + a
            globstars <- globstars - b
            weight <- weight - c
        | FNode (Group _) | FOpen | FClose -> ()
    lit, -globstars, -weight

// =================================================================================================
// Generation (spec 5.5, 5.7)
// =================================================================================================

/// A globstar, or a group whose body is one: the slot whose empty value drops a '/' (5.5, D37).
let rec private globstarSlot (t: Token) =
    match t with
    | Globstar -> true
    | Group (_, [ x ]) -> globstarSlot x
    | _ -> false

type private Piece =
    | Text of string
    | Slash
    | EmptySlot

/// XP014: a class or braces outside a group cannot be filled in from a match (5.7).
let private unformattable (text: string) (tokens: Token list) =
    match tokens |> List.tryFind (function Class _ | Alt _ -> true | _ -> false) with
    | Some bad ->
        let t = unparse [ bad ]
        let reason = sprintf "'%s' cannot be filled in from a match; put it in a named group: (name:%s)" t t
        Some { Pattern = text; Context = PatternContext.Rule; Position = None; Code = "XP014"; Reason = reason }
    | None -> None

let private formatText (text: string) (named: string -> string) (positional: int -> string) (tokens: Token list) =
    match unformattable text tokens with
    | Some e -> raiseError e
    | None ->
        let mutable k = 0
        let pieces =
            [| for t in tokens do
                 match t with
                 | Lit s -> yield Text s
                 | Sep -> yield Slash
                 | Star | AnyChar | DoubleStar | Globstar ->
                     k <- k + 1
                     let v = positional k
                     yield (if v = "" && t = Globstar then EmptySlot else Text v)
                 | Group (name, _) ->
                     let v = named name
                     yield (if v = "" && globstarSlot t then EmptySlot else Text v)
                 | Class _ | Alt _ -> () |]
        // an empty globstar slot drops one adjacent '/', the one after it first (5.5)
        let dropped = Array.create pieces.Length false
        for i in 0 .. pieces.Length - 1 do
            if pieces.[i] = EmptySlot then
                if i + 1 < pieces.Length && pieces.[i + 1] = Slash && not dropped.[i + 1] then dropped.[i + 1] <- true
                elif i > 0 && pieces.[i - 1] = Slash && not dropped.[i - 1] then dropped.[i - 1] <- true
        let sb = StringBuilder()
        pieces |> Array.iteri (fun i p ->
            if not dropped.[i] then
                match p with
                | Text s -> sb.Append(s) |> ignore
                | Slash -> sb.Append('/') |> ignore
                | EmptySlot -> ())
        sb.ToString()

/// Fills a pattern in (5.7): a named group becomes `named name`, the i-th top-level bare wildcard
/// (1-based, wildcards inside groups do not count) `positional i`; literals print with escapes
/// resolved, `Sep` as '/'. A globstar slot (or a group around one) with the value "" drops one
/// adjacent '/'. Raises PatternException (XP014) for a class or braces outside a group.
let format (named: string -> string) (positional: int -> string) (tokens: Token list) : string =
    formatText (unparse tokens) named positional tokens

/// `format` fed from a rule match (5.7; replaces ExecCore.applyWildcards): named groups by name,
/// the i-th top-level bare wildcard by its positional number in `pattern` (`wildcards`). A missing
/// key gives "". Raises PatternException for an invalid pattern and for XP014.
let formatMatches (matches: Map<string, string>) (pattern: string) : string =
    let tokens = Rule.parse pattern
    let numbers = wildcards tokens |> List.map snd |> Array.ofList
    let get (key: string) = match Map.tryFind key matches with Some v -> v | None -> ""
    let positional i = if i >= 1 && i <= numbers.Length then get (string numbers.[i - 1]) else ""
    formatText pattern get positional tokens

/// The check made when a multi-output rule (`*..>`, `targets`) is created (5.6): every output
/// parses, has no class or braces outside a group (XP014) and the same set of named groups as the
/// first output (XP013). Raises PatternException.
let internal checkOutputs (patterns: string list) : unit =
    let parsed = patterns |> List.map (fun p -> p, Rule.parse p)
    for p, tokens in parsed do
        match unformattable p tokens with
        | Some e -> raiseError e
        | None -> ()
    match parsed with
    | [] -> ()
    | (_, first) :: rest ->
        let firstSet = groupNames first |> Set.ofList
        let show (s: string seq) = "[" + String.Join(", ", s) + "]"
        for p, tokens in rest do
            let names = groupNames tokens
            let set = Set.ofList names
            if set <> firstSet then
                let reason =
                    sprintf "output \"%s\" has groups %s, the first output %s (missing %s, extra %s); every output needs the same named groups, or the others cannot be named from a match"
                        p (show names) (show (groupNames first)) (show (firstSet - set)) (show (set - firstSet))
                raiseError { Pattern = p; Context = PatternContext.Rule; Position = None; Code = "XP013"; Reason = reason }
