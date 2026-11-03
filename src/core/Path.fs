module Xake.Path

open System.IO

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

    let private reserved = "()[]{}|"

    /// The end of the class starting at `i` (a `[`), the index of its closing `]`, or -1
    /// (spec 4.1 class mode: a `]` right after `[`, `[!`, `[^` is an item, `\` escapes).
    let private classEnd (s: string) (i: int) =
        let n = s.Length
        let mutable j = i + 1
        if j < n && (s.[j] = '!' || s.[j] = '^') then j <- j + 1
        if j < n && s.[j] = ']' then j <- j + 1
        while j < n && s.[j] <> ']' do
            j <- if s.[j] = '\\' then j + 2 else j + 1
        if j < n then j else -1

    /// Splits a mask at its separators with the lexer's rules (spec 6.1, 4.1): at `/` and at a
    /// `\` that is a legacy separator, never inside `[...]` or `{...}` and never at a `\` that
    /// escapes a reserved character. The segment texts keep their escapes; empty strings mark a
    /// leading separator, a trailing one and a repeated one.
    let split (s: string) : string list =
        let segs = ResizeArray<string>()
        let sb = System.Text.StringBuilder()
        let n = s.Length
        let mutable i = 0
        let mutable braces = 0
        while i < n do
            let c = s.[i]
            if c = '\\' && i + 1 < n && reserved.IndexOf s.[i + 1] >= 0 then
                sb.Append(c).Append(s.[i + 1]) |> ignore
                i <- i + 2
            elif (c = '/' || c = '\\') && braces = 0 then
                segs.Add(sb.ToString())
                sb.Clear() |> ignore
                i <- i + 1
            elif c = '[' then
                match classEnd s i with
                | -1 -> sb.Append c |> ignore; i <- i + 1
                | e -> sb.Append(s, i, e - i + 1) |> ignore; i <- e + 1
            else
                if c = '{' then braces <- braces + 1
                elif c = '}' && braces > 0 then braces <- braces - 1
                sb.Append c |> ignore
                i <- i + 1
        segs.Add(sb.ToString())
        List.ofSeq segs

    /// A segment holding a wildcard, a class or braces (an unescaped `* ? [ {`).
    let isMask (a: string) =
        let mutable i = 0
        let mutable found = false
        while not found && i < a.Length do
            match a.[i] with
            | '\\' -> i <- i + 2
            | '*' | '?' | '[' | '{' -> found <- true
            | _ -> i <- i + 1
        found

    let private isDrive (a: string) = a.Length = 2 && a.[1] = ':' && System.Char.IsLetter a.[0]

    /// Parses a mask into parts (spec 6.1). A mask ending in a separator, or any mask when
    /// `dirMode`, names directories: its last part is `Directory`, `DirectoryMask` or `Recurse`;
    /// otherwise the last part is `FileName` or `FileMask` (a last `**` is `FileMask "**"`), so
    /// `maskText` gives the text back. A leading `//` is two `FsRoot` (a UNC root, D24). `.` and
    /// `x/..` are resolved in the literal prefix only (D34: after a wildcard they are XP012).
    let parse (dirMode: bool) (pattern: string) =
        if System.String.IsNullOrEmpty pattern then PathMask []
        else
            let raw = split pattern
            let leading = raw |> List.takeWhile ((=) "") |> List.length
            let body = raw |> List.filter ((<>) "")
            let dirMode = dirMode || (body <> [] && List.last raw = "")
            let root = match leading with 0 -> [] | 1 -> [ FsRoot ] | _ -> [ FsRoot; FsRoot ]
            let lastIndex = body.Length - 1
            let parts =
                body |> List.mapi (fun i a ->
                    let isLast = i = lastIndex
                    match a with
                    | _ when i = 0 && leading = 0 && isDrive a -> Disk a
                    | "**" -> if isLast && not dirMode then FileMask a else Recurse
                    | "." when not (isLast && dirMode) -> CurrentDir
                    | ".." when not (isLast && dirMode) -> Parent
                    | a when not isLast || dirMode -> if isMask a then DirectoryMask a else Directory a
                    | a -> if isMask a then FileMask a else FileName a)
            let isWild = function DirectoryMask _ | FileMask _ | Recurse -> true | _ -> false
            let prefix = parts |> List.takeWhile (isWild >> not)
            let rest = parts |> List.skip prefix.Length
            PathMask (root @ normalize prefix @ rest)

    /// The mask text of parts made by `parse` (spec 6.1): segments joined with `/`, a trailing
    /// `/` when the last part names directories.
    let maskText (PathMask parts) =
        let text = function
            | FsRoot -> "" | Disk d | Directory d | DirectoryMask d | FileName d | FileMask d -> d
            | Parent -> ".." | CurrentDir -> "." | Recurse -> "**"
        let roots = parts |> List.takeWhile ((=) FsRoot) |> List.length
        let body = parts |> List.skip roots
        let head = match roots with 0 -> "" | 1 -> "/" | _ -> "//"
        let joined = body |> List.map text |> String.concat "/"
        let trailing =
            match List.tryLast body with
            | Some (Directory _ | DirectoryMask _ | Recurse) -> "/"
            | Some (Disk _) when body.Length = 1 -> "/"
            | _ -> ""
        head + joined + trailing

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
/// Converts a mask naming directories to a PathMask (as if it ended in '/'). The parts keep
/// the segment text of the mask (spec 6.1); the mask is parsed by the pattern module when a
/// fileset is evaluated.
/// </summary>
let parseDir = impl.parse true

/// <summary>
/// Converts a mask to a PathMask: split at '/' (and at a '\' that is a legacy separator), not
/// inside '[...]' or '{...}'; a mask ending in a separator names directories (spec 6.1).
/// </summary>
let parse = impl.parse false

/// The mask text of a PathMask made by `parse` or `parseDir` (spec 6.1): what the pattern module
/// parses when a fileset is evaluated.
let maskText = impl.maskText

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
