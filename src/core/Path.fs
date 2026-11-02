module Xake.Path

open System.IO
open System.Text.RegularExpressions

/// A single segment of a parsed file path or pattern.
type Part =
    | FsRoot
    | Parent
    | CurrentDir
    | Disk of string
    | DirectoryMask of string
    | Directory of string
    | Recurse
    | FileMask of string
    | FileName of string

/// A parsed Ant-style path pattern consisting of part segments.
type PathMask = PathMask of Part list

/// Result of matching a file path against a pattern.
type MatchResult =
    | Matches of (string*string) list
    | Nope

/// How specific a rule pattern is, higher = more specific (spec 5.8; `PatternAst.specificity`).
/// Compared as a tuple, lexicographically:
///   1. literal character count  (a `/` next to a `**` counts 0, D5)
///   2. fewer `**` globstars     (a globstar spans segments, so it is the least specific wildcard)
///   3. lighter wildcard weight  (`?` and a class = 1, `*` = 2; braces count as their least
///      specific alternative)
/// A named group `(name:*)` contributes no literals for its `(name:`/`)` structure, so `(cfg:*)`
/// scores exactly like a bare `*`.
type Specificity = int * int * int

module private impl =

    let notNullOrEmpty = System.String.IsNullOrEmpty >> not

    let driveRegex = Regex(@"^[A-Za-z]:$", RegexOptions.Compiled)
    let isMask (a:string) = a.IndexOfAny([|'*';'?'|]) >= 0
    let iif fn b c a = match fn a with | true -> b a | _ -> c a
    
    let isRoot = function | FsRoot::_ | Disk _::_ -> true | _ -> false

    /// <summary>
    /// Normalizes the pattern by resolving parent references and removing \.\
    /// </summary>
    let rec normalize = function
        | [] -> []
        | [x] -> [x]
        | x::tail ->               
            match x::(normalize tail) with
            | Directory _::Parent::t -> t
            | CurrentDir::t -> t
            | rest -> rest

    /// <summary>
    /// Maps part of file path to a path part.
    /// </summary>
    /// <param name="mapPart"></param>
    let mapPart isLast = function
        | "**" -> Recurse
        | "." -> CurrentDir
        | ".." -> Parent (* works well now with Path.Combine() *)
        | a when a.EndsWith(":") && driveRegex.IsMatch(a) -> Disk(a)
        | a when not isLast -> a |> iif isMask DirectoryMask Directory
        | a -> a |> iif isMask FileMask FileName

    let parse isLastPart pattern =
     
        if notNullOrEmpty pattern then
            let parts = pattern.Split([|'\\'; '/'|], System.StringSplitOptions.RemoveEmptyEntries)
            let fsroot = pattern.[0] |> function | '\\' | '/' -> [FsRoot] | _ -> []
            in
            let isLast = isLastPart parts
            fsroot @ (parts |> Array.mapi (isLast >> mapPart) |> List.ofArray)
                |> normalize |> PathMask
        else
            PathMask []

    /// <summary>
    /// supplementary function for parsing directory
    /// </summary>
    let isLastPartForDir _ _ = false
    /// <summary>
    /// supplementary function for parsing file
    /// </summary>
    let isLastPartForFile (parts:_ array) = (=) (parts.Length-1)

    let dirSeparator = string Path.DirectorySeparatorChar
    let partToString =
        function
        | Directory s
        | FileName s
        | DirectoryMask s
        | FileMask s
            -> s
        | Parent -> ".."
        | Part.CurrentDir -> "."
        | Part.Disk d -> d + dirSeparator
        | Part.Recurse -> "**"
        | Part.FsRoot -> dirSeparator

    // A segment is considered the start of a wildcard/named-group portion of a
    // pattern once it contains any of these characters. Everything from that segment
    // on is left untouched - it may legitimately contain '/' inside a named group
    // (e.g. "(root:*/*-a).ss"), so it must not be split and reassembled.
    let isSpecialSegment (s: string) = s.IndexOfAny([| '*'; '?'; '(' |]) >= 0

    // A segment that must never be popped by a ".." above it: the empty segment
    // produced by a leading '/' (unix/UNC root), or a drive letter like "C:".
    let isRootMarkerSegment (s: string) = s = "" || (s.Length = 2 && s.[1] = ':')

    /// Collapses "." and ".." segments against the preceding literal segment,
    /// refusing to climb past a root marker segment.
    let rec collapseDotSegments (stack: string list) = function
        | [] -> List.rev stack
        | "." :: rest -> collapseDotSegments stack rest
        | ".." :: rest ->
            match stack with
            | top :: restStack when not (isRootMarkerSegment top) -> collapseDotSegments restStack rest
            | _ -> collapseDotSegments stack rest
        | s :: rest -> collapseDotSegments (s :: stack) rest

    /// Normalizes ".." / "." only within the literal directory prefix of a combined
    /// root+pattern path string (i.e. up to, but not including, the first segment
    /// that contains a wildcard or named-group marker). The wildcard/named-group
    /// suffix is left byte-for-byte as given.
    let normalizeLiteralPrefix (combined: string) =
        let segments = combined.Replace('\\', '/').Split('/') |> List.ofArray
        let idx = segments |> List.tryFindIndex isSpecialSegment |> Option.defaultValue segments.Length
        let prefix, suffix = segments |> List.splitAt idx
        let normalizedPrefix = collapseDotSegments [] prefix
        System.String.Join("/", normalizedPrefix @ suffix)


module private PicklerImpl =

    open Pickler

    let patternpart=
        alt(function
            | FsRoot -> 0
            | Parent -> 1
            | Disk _ -> 2
            | DirectoryMask _ -> 3
            | Directory _ -> 4
            | Recurse -> 5
            | FileMask _ -> 6
            | FileName _ -> 7
            | CurrentDir -> 8
            )
          [|
            wrap0 FsRoot
            wrap0 Parent
            wrap (Disk, fun (Disk d | OtherwiseFail d) -> d) str
            wrap (DirectoryMask, fun (DirectoryMask d | OtherwiseFail d) -> d) str
            wrap (Directory, fun (Directory d | OtherwiseFail d) -> d) str
            wrap0 Recurse
            wrap (FileMask, fun (FileMask m | OtherwiseFail m) -> m) str
            wrap (FileName, fun (FileName m | OtherwiseFail m) -> m) str
            wrap0 CurrentDir
          |]

    let pattern = wrap(PathMask, fun(PathMask pp) -> pp) (list patternpart)

/// The 3.x segment matcher of `Fileset` scanning and `matchesPattern` (a `PathMask` segment with
/// `*`/`?` becomes a regex). Rule matching, `matches` and `matchGroups` use the pattern module
/// (`Xake.PatternAst`, `Xake.PatternMatch`) since Phase 4a; this stays until the scanner moves
/// onto the module (Phase 4b), then it is deleted.
module internal LegacyMask =

    type Token =
        | Lit of string
        | Star            // `*`  — a run of non-separator characters
        | DoubleStar      // `**` — anything, including '/'
        | AnyChar         // `?`  — a single non-separator character
        | Group of string * Token list   // `(name:...)` — a named capture around its body

    let private isWord c = System.Char.IsLetterOrDigit c || c = '_'

    // Parses tokens from s starting at i. When inGroup, a top-level ')' ends the run and is left
    // for the caller to consume; otherwise ')' is an ordinary literal (as the old regex treated it).
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

    /// Parses an Ant-style pattern into tokens (slashes normalized to '/').
    let parse (pattern: string) : Token list =
        parseAt (pattern.Replace('\\', '/')) false 0 |> fst

    // Regex fragments, byte-identical to the former string-substitution map. Triple-quoted so the
    // `\\` reaches the regex as a literal backslash inside the character class.
    let private starRx  = """([^/\\]*)"""
    let private anyRx   = """([^/\\])"""
    let private dstarRx = "(.*)"
    // The former substitution escaped only these metacharacters; '[' and ']' are deliberately
    // absent, so they pass through unescaped exactly as before (no behavior change).
    let private metachars = Regex(@"[.$^+!={}]", RegexOptions.Compiled)
    let private escapeLiteral (s: string) = metachars.Replace(s, @"\$0")

    /// The inner (unanchored) regex for a token list.
    let rec toRegex (tokens: Token list) : string =
        tokens
        |> List.map (function
            | Lit s -> escapeLiteral s
            | Star -> starRx
            | DoubleStar -> dstarRx
            | AnyChar -> anyRx
            | Group (name, inner) -> "(?<" + name + ">" + toRegex inner + ")")
        |> String.concat ""

module internal matchImpl =

    let eq s1 s2 = System.StringComparer.OrdinalIgnoreCase.Equals(s1, s2)

    let normalizeSlashes (pat: string) =
        pat.Replace('\\', '/')

    let private buildRegex (pattern:string) =
        let pat = pattern |> LegacyMask.parse |> LegacyMask.toRegex
        let ignoreCase = if Env.isUnix then RegexOptions.None else RegexOptions.IgnoreCase
        in
        Regex(@"^" + pat + "$", RegexOptions.Compiled + ignoreCase)

    // Building a compiled Regex costs about a millisecond; the masks are few (a script's
    // patterns and their segments) and matched against every file or target, so each is
    // built once. A Regex is safe to share between threads for matching.
    let private regexCache = System.Collections.Concurrent.ConcurrentDictionary<string, Regex> ()

    /// The anchored regex for an Ant-style mask, built once per distinct mask.
    let maskToRegex (pattern:string) = regexCache.GetOrAdd (pattern, fun p -> buildRegex p)

    let matchPart (mask:Part) (path:Part) =
        let matchByMask (rx:Regex) value = rx.Match(value).Success
        match mask,path with
        | (FsRoot, FsRoot) -> true
        | (Disk mask, Disk d) | (Directory mask, Directory d) | FileName mask, FileName d when eq mask d -> true

        | DirectoryMask mask, Directory d | FileMask mask, FileName d ->
            matchByMask (maskToRegex mask) d

        | _ -> false

    let rec matchPaths (mask:Part list) (p:Part list) =
        match mask,p with
        | [], [] -> true
        | [], _ | _, [] -> false

        | Directory _::Recurse::Parent::ms, _ -> matchPaths (Recurse::ms) p
        | Recurse::Parent::ms, _              -> matchPaths (Recurse::ms) p    // ignore parent ref

        | Recurse::ms, (FileName _)::_ -> matchPaths ms p
        | Recurse::ms, Directory _::xs -> (matchPaths mask xs) || (matchPaths ms p)
        | m::ms, x::xs ->
            matchPart m x && matchPaths ms xs

/// Pickler for PathMask serialization.
let pickler = PicklerImpl.pattern

/// <summary>
/// Converts path to string representation (platform specific).
/// </summary>
let toString =
    List.map impl.partToString
    >> List.fold (fun s ps -> Path.Combine (s, ps)) ""

/// <summary>
/// Joins two patterns.
/// </summary>
/// <param name="p1"></param>
/// <param name="p2"></param>
let join (PathMask p1) (PathMask p2) =
    match impl.isRoot p2 with
    | true -> PathMask p2
    | _ -> p1 @ p2 |> impl.normalize |> PathMask

/// <summary>
/// Converts Ant-style file pattern to a list of parts. Assumes the path specified
/// </summary>
let parseDir = impl.parse impl.isLastPartForDir

/// <summary>
/// Converts Ant-style file pattern to a PathMask.
/// </summary>
let parse = impl.parse impl.isLastPartForFile

/// Returns true if the parsed file path matches the given PathMask (the 3.x part matcher, still
/// behind `Fileset` scanning and hermetic's lock masks until Phase 4b).
let matchesPattern (PathMask mask) file =
    let (PathMask fileParts) = file |> impl.parse impl.isLastPartForFile in
    matchImpl.matchPaths mask fileParts

module private RuleImpl =

    /// The comparer of D11: ignore-case and `\` a path separator on Windows, ordinal elsewhere.
    let windows = not Env.isUnix

    /// Collapses "." and ".." against the preceding name and runs of separators, as `File.make`
    /// does for a target path; a root ("", "C:") is never popped. `\` is a separator on Windows.
    let collapsePath (path: string) =
        let p = if windows then path.Replace('\\', '/') else path
        if p.IndexOf("/.", System.StringComparison.Ordinal) < 0 && not (p.StartsWith "./")
           && (p.Length < 2 || p.IndexOf("//", 1, System.StringComparison.Ordinal) < 0) then p
        else
            let segs = p.Split('/')
            let out = System.Collections.Generic.List<string>()
            segs |> Array.iteri (fun i s ->
                match s with
                | "" when i > 0 && i < segs.Length - 1 -> ()
                | "." when segs.Length > 1 -> ()
                | ".." when out.Count > 0 && (let top = out.[out.Count - 1] in top <> ".." && top <> "" && not (top.Length = 2 && top.[1] = ':')) ->
                    out.RemoveAt(out.Count - 1)
                | s -> out.Add s)
            System.String.Join("/", out)

    let private isMeta (s: string) = s.IndexOfAny([| '*'; '?'; '['; '{'; '('; '\\' |]) >= 0

    /// Collapses `x/..` and `.` in the literal prefix of a rule pattern (the segments before the
    /// first one holding a metacharacter), so `a/../mid/(name:*).txt` names `mid/...` as the
    /// normalized target path does. Leading `..` stay: the matcher steps them up the root (D34).
    let collapsePrefix (pattern: string) =
        if pattern.IndexOf("..", System.StringComparison.Ordinal) < 0 && pattern.IndexOf("./", System.StringComparison.Ordinal) < 0 then pattern
        else
            let segs = pattern.Split('/') |> List.ofArray
            let idx = segs |> List.tryFindIndex isMeta |> Option.defaultValue segs.Length
            let prefix, suffix = List.splitAt idx segs
            let folded =
                let stack = System.Collections.Generic.List<string>()
                for s in prefix do
                    match s with
                    | "." when stack.Count > 0 -> ()
                    | ".." when stack.Count > 0 && (let top = stack.[stack.Count - 1] in top <> ".." && top <> "" && top <> "." && not (top.Length = 2 && top.[1] = ':')) ->
                        stack.RemoveAt(stack.Count - 1)
                    | s -> stack.Add s
                List.ofSeq stack
            let result = System.String.Join("/", folded @ suffix)
            if result = "" then pattern else result

    /// Named groups of a rule pattern in first-appearance order, per pattern text.
    let private namesCache = System.Collections.Concurrent.ConcurrentDictionary<string, string list>()
    let groupNames (pattern: string) =
        namesCache.GetOrAdd(pattern, fun p -> Xake.PatternAst.Rule.parse p |> Xake.PatternAst.groupNames)

    let specCache = System.Collections.Concurrent.ConcurrentDictionary<string, Specificity>()

/// Matches a target against a rule pattern anchored at `rootPath` (`""` for a phony name) through
/// the pattern module (spec 8.3): the captures in 3.x order ("0", the positional numbers, then the
/// named groups in pattern order) and the evidence codes of the match (XP119). Raises
/// `PatternAst.PatternException` for an invalid pattern.
let internal matchRule (pattern: string) (rootPath: string) : string -> ((string * string) list * string list) option =
    let pattern = RuleImpl.collapsePrefix pattern
    fun file ->
        match PatternMatch.matchRule RuleImpl.windows rootPath pattern (RuleImpl.collapsePath file) with
        | None -> None
        | Some m ->
            let positional =
                m.Captures |> Map.toList
                |> List.choose (fun (k, v) -> match System.Int32.TryParse k with | true, n when n > 0 -> Some (n, (k, v)) | _ -> None)
                |> List.sortBy fst |> List.map snd
            let named = RuleImpl.groupNames pattern |> List.map (fun n -> n, defaultArg (Map.tryFind n m.Captures) "")
            Some ((("0", m.Captures.["0"]) :: positional) @ named, m.Evidence)

/// Matches a file against a rule pattern and returns the captured groups (named, positional and
/// "0"), or None if no match. Raises `PatternAst.PatternException` for an invalid pattern.
let matchGroups (pattern:string) rootPath =
    let m = matchRule pattern rootPath
    fun file -> m file |> Option.map fst

/// Returns true if the file matches the mask relative to the given root (the pattern module's
/// mask semantics, spec 5.1; a mask with a trailing '/' names directories and matches no file).
/// `.` and `..` in the file path are resolved first. Raises `PatternAst.PatternException` for an
/// invalid mask.
let matches filePattern rootPath =
    fun file -> PatternMatch.matchMask RuleImpl.windows rootPath filePattern (RuleImpl.collapsePath file) false

/// Specificity of a rule pattern (spec 5.8, see the `Specificity` type), memoized per pattern
/// string. Raises `PatternAst.PatternException` for an invalid pattern.
let specificity (pattern: string) : Specificity =
    RuleImpl.specCache.GetOrAdd(pattern, fun p -> Xake.PatternAst.Rule.parse p |> Xake.PatternAst.specificity)

/// The text of an evidence warning found by a rule match (spec 7.3).
let internal ruleEvidence (code: string) (target: string) : Xake.PatternAst.Issue =
    match code with
    | "XP119" ->
        { Code = code
          Text = sprintf "'**/' matched zero directories for '%s'; in Xake 3.x '**/' in a rule needed at least one directory, so this rule did not match it; to require one or more directories write '*/**/' (with a capture: '(path:*/**)/')" target }
    | _ -> { Code = code; Text = code }
