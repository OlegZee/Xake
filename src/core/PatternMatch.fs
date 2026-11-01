/// The matcher stage of the pattern module (docs/features/pattern-module/spec.md sections 5, 7.3
/// and 9; decisions D11, D12, D18, D34, D37 in brief.md): a pattern parsed by `PatternAst` is
/// translated to a .NET regex, cached per (context, text, anchor, case mode), and matched against
/// one path. Internal: the engine keeps using `Path.matchGroups` / `Path.matches` until Phase 4,
/// when they become adapters over `matchRule` / `matchMask`. The regex is an implementation detail
/// (D3); nothing here is public.
module internal Xake.PatternMatch

open System
open System.Text
open System.Text.RegularExpressions
open System.Collections.Concurrent

open Xake.PatternAst

/// The result of a rule match (spec 5.6, 7.3).
type RuleMatch =
    { /// every named group (`""` when it took no part), every positional number "1", "2", ...
      /// of a `*`, `**`, `?` at any depth, and "0", the whole path
      Captures: Map<string, string>
      /// evidence warning codes of this match (XP119)
      Evidence: string list }

/// A token with its capture numbers: wildcards numbered in document order at any depth (5.6),
/// groups numbered for their regex names (user names never reach the regex).
type private A =
    | ALit of string
    | ASep
    | AWild of Token * int
    | AClass of negated: bool * items: ClassItem list
    | AAlt of A list list
    | AGroup of name: string * index: int * body: A list

let private annotate (tokens: Token list) : A list * int * (string * int) list =
    let mutable wild = 0
    let groups = ResizeArray<string * int>()
    let rec go (ts: Token list) : A list =
        [ for t in ts do
            match t with
            | Lit s -> yield ALit s
            | Sep -> yield ASep
            | Star | AnyChar | Globstar | DoubleStar ->
                wild <- wild + 1
                yield AWild (t, wild)
            | Class (negated, items) -> yield AClass (negated, items)
            | Alt alts -> yield AAlt (alts |> List.map go)
            | Group (name, body) ->
                let k = groups.Count + 1
                groups.Add((name, k))
                yield AGroup (name, k, go body) ]
    let annotated = go tokens
    annotated, wild, List.ofSeq groups

let private isSep = function ASep -> true | _ -> false

/// A globstar, a group whose body is one such element, or braces with one such alternative: an
/// element that may take zero segments and so owns one adjacent separator (9.3, M1).
let rec private zeroSegments (a: A) =
    match a with
    | AWild (Globstar, _) -> true
    | AGroup (_, _, [ x ]) -> zeroSegments x
    | AAlt alts -> alts |> List.exists (function [ x ] -> zeroSegments x | _ -> false)
    | _ -> false

let private appendClass (sb: StringBuilder) (negated: bool) (items: ClassItem list) =
    // every item as \uXXXX: ']' '\' '^' '-' '[' keep no special meaning, no '-[' subtraction (M2)
    let u (c: char) = sb.Append("\\u").Append((int c).ToString("X4")) |> ignore
    sb.Append(if negated then "[^/" else "[") |> ignore
    for item in items do
        match item with
        | One c -> u c
        | Range (a, b) ->
            u a
            sb.Append('-') |> ignore
            u b
    sb.Append(']') |> ignore

/// Emits a token list. `atEnd`: nothing follows this list in the whole pattern; `dirMode`: the
/// pattern is a directory mask whose trailing separator was dropped.
let rec private emit (sb: StringBuilder) (dirMode: bool) (atEnd: bool) (items: A list) =
    let a = Array.ofList items
    let last = a.Length - 1
    let owned = Array.create a.Length false
    // 0: plain; 1: owns the separator before it, (?:/X)?; 2: owns the one after it, (?:X/)?
    let mode = Array.create a.Length 0
    for i in 0 .. last do
        if zeroSegments a.[i] then
            let prevFree = i > 0 && isSep a.[i - 1] && not owned.[i - 1]
            if atEnd && i = last then
                // 'a/**' needs a segment below 'a'; the directory mask 'a/**/' also matches 'a' (5.5)
                if dirMode && prevFree then
                    mode.[i] <- 1
                    owned.[i - 1] <- true
            elif prevFree then
                mode.[i] <- 1
                owned.[i - 1] <- true
            elif i < last && isSep a.[i + 1] then
                mode.[i] <- 2
                owned.[i + 1] <- true
    for i in 0 .. last do
        if not owned.[i] then
            let one () = emitOne sb dirMode (atEnd && i = last) a.[i]
            match mode.[i] with
            | 1 ->
                sb.Append("(?:/") |> ignore
                one ()
                sb.Append(")?") |> ignore
            | 2 ->
                sb.Append("(?:") |> ignore
                one ()
                sb.Append("/)?") |> ignore
            | _ -> one ()

and private emitOne (sb: StringBuilder) (dirMode: bool) (atEnd: bool) (a: A) =
    match a with
    | ALit s -> sb.Append(Regex.Escape s) |> ignore
    | ASep -> sb.Append('/') |> ignore
    | AWild (Globstar, n) -> sb.Append("(?<w").Append(n).Append(">.+)") |> ignore
    | AWild (AnyChar, n) -> sb.Append("(?<w").Append(n).Append(">[^/])") |> ignore
    | AWild (_, n) -> sb.Append("(?<w").Append(n).Append(">[^/]*)") |> ignore
    | AClass (negated, items) -> appendClass sb negated items
    | AAlt alts ->
        sb.Append("(?:") |> ignore
        alts |> List.iteri (fun k alt ->
            if k > 0 then sb.Append('|') |> ignore
            emit sb dirMode atEnd alt)
        sb.Append(')') |> ignore
    | AGroup (_, k, body) ->
        sb.Append("(?<g").Append(k).Append('>') |> ignore
        emit sb dirMode atEnd body
        sb.Append(')') |> ignore

/// The globstars that report XP119 when they take zero directories (7.3, D37): a globstar before
/// a separator (group delimiters transparent), not inside braces, not right after a `*` segment
/// (the one-or-more idiom `*/**/b`, D12).
let private xp119Candidates (annotated: A list) : int list =
    let rec flat (items: A list) = items |> List.collect (function AGroup (_, _, body) -> flat body | x -> [ x ])
    let f = flat annotated |> Array.ofList
    [ for k in 0 .. f.Length - 2 do
        match f.[k] with
        | AWild (Globstar, n) ->
            let afterStarSegment =
                k >= 2 && isSep f.[k - 1]
                && (match f.[k - 2] with AWild (Star, _) -> true | _ -> false)
                && (k = 2 || isSep f.[k - 3])
            if not afterStarSegment then yield n
        | _ -> () ]

type private Compiled =
    { Regex: Regex
      /// a directory mask (trailing '/', D9)
      IsDirectory: bool
      Wildcards: int
      Groups: (string * int) list
      Xp119: int list }

/// '\' in a path is a separator in Windows mode only (D37).
let private normalize (windows: bool) (path: string) = if windows then path.Replace('\\', '/') else path

let private isAbsolute (tokens: Token list) =
    match tokens with
    | Sep :: _ -> true
    | Lit d :: Sep :: _ when d.Length = 2 && d.[1] = ':' && Char.IsLetter d.[0] -> true
    | _ -> false

/// Joins a relative pattern to its anchor; leading '..' segments step up the anchor (D34, 9.2).
let private anchorPrefix (windows: bool) (anchor: string) (tokens: Token list) : string * Token list =
    if anchor = "" || isAbsolute tokens then "", tokens
    else
        let mutable a = (normalize windows anchor).TrimEnd('/')
        let mutable ts = tokens
        let mutable stepping = true
        while stepping do
            match ts with
            | Lit ".." :: Sep :: rest ->
                let idx = a.LastIndexOf '/'
                let lastSeg = if idx >= 0 then a.Substring(idx + 1) else ""
                if idx >= 0 && lastSeg <> "" && lastSeg <> ".." then
                    a <- a.Substring(0, idx)
                    ts <- rest
                else stepping <- false
            | _ -> stepping <- false
        Regex.Escape a + "/", ts

let private compile (ctx: PatternContext) (text: string) (anchor: string) (windows: bool) : Compiled option =
    let tokens = match ctx with PatternContext.Rule -> Rule.parse text | PatternContext.Mask -> Mask.parse text
    match tokens with
    | Sep :: Sep :: _ when not windows -> None       // a UNC root matches only on Windows (D24)
    | _ ->
    let isDirectory = ctx = PatternContext.Mask && List.tryLast tokens = Some Sep
    // the trailing separator of a directory mask is not emitted (9.2); '/' alone keeps it
    let body = if isDirectory && List.length tokens > 1 then List.take (List.length tokens - 1) tokens else tokens
    let prefix, body = anchorPrefix windows anchor body
    let annotated, wildcards, groups = annotate body
    let sb = StringBuilder("^")
    sb.Append(prefix) |> ignore
    emit sb isDirectory true annotated
    sb.Append(@"\z") |> ignore
    let options =
        RegexOptions.CultureInvariant ||| RegexOptions.Singleline ||| RegexOptions.ExplicitCapture
        ||| (if windows then RegexOptions.IgnoreCase else RegexOptions.None)
        // rule patterns are few and matched against every target; masks are compiled by the
        // walker's choice (PR 4), interpreted here (9.4, D18)
        ||| (if ctx = PatternContext.Rule then RegexOptions.Compiled else RegexOptions.None)
    Some
        { Regex = Regex(sb.ToString(), options)
          IsDirectory = isDirectory
          Wildcards = wildcards
          Groups = groups
          Xp119 = if ctx = PatternContext.Rule then xp119Candidates annotated else [] }

let private cache = ConcurrentDictionary<PatternContext * string * string * bool, Lazy<Compiled option>>()

/// The cached matcher of a pattern (9.4): key (context, text, anchor, case mode).
let private get (ctx: PatternContext) (text: string) (anchor: string) (windows: bool) : Compiled option =
    cache.GetOrAdd((ctx, text, anchor, windows), fun _ -> lazy (compile ctx text anchor windows)).Value

/// The regex text a pattern compiles to (diagnostics and tests only).
let regexText (ctx: PatternContext) (text: string) (anchor: string) (windows: bool) : string option =
    get ctx text anchor windows |> Option.map (fun c -> c.Regex.ToString())

/// Matches a rule pattern anchored at `root` (`""` for phony) against a full target path
/// (`Path.matchGroups`, 8.3). `windows`: the comparer of D11 (ignore-case, `\` in the path is a
/// separator). Raises PatternException for an invalid pattern.
let matchRule (windows: bool) (root: string) (pattern: string) (target: string) : RuleMatch option =
    match get PatternContext.Rule pattern root windows with
    | None -> None
    | Some c ->
        let path = normalize windows target
        let m = c.Regex.Match path
        if not m.Success then None
        else
            let value (name: string) = let g = m.Groups.[name] in if g.Success then g.Value else ""
            let captures =
                [ yield "0", path
                  for name, k in c.Groups do yield name, value ("g" + string k)
                  for n in 1 .. c.Wildcards do yield string n, value ("w" + string n) ]
                |> Map.ofList
            let evidence =
                if c.Xp119 |> List.exists (fun n -> value ("w" + string n) = "") then [ "XP119" ] else []
            Some { Captures = captures; Evidence = evidence }

/// Matches a mask anchored at `baseDir` against one path (`Path.matches`, the walker's per-entry
/// test). A mask with a trailing '/' matches directories only, otherwise files only (D9).
let matchMask (windows: bool) (baseDir: string) (mask: string) (path: string) (isDirectory: bool) : bool =
    match get PatternContext.Mask mask baseDir windows with
    | None -> false
    | Some c -> c.IsDirectory = isDirectory && c.Regex.IsMatch(normalize windows path)
