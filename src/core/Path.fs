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

/// How specific a rule pattern is, higher = more specific (see `PatternAst.Specificity`).
type Specificity = PatternAst.Specificity

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

    /// Parses a path (a file or directory name, e.g. read from disk): '\' and '/' both separate.
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

    /// Splits a mask at '/' and at a '\' that escapes nothing; an escape (`\(`) stays in its segment.
    let splitMask (pattern: string) =
        let segments = ResizeArray<string>()
        let sb = System.Text.StringBuilder()
        let mutable i = 0
        while i < pattern.Length do
            let c = pattern.[i]
            if c = '\\' && i + 1 < pattern.Length && PatternAst.isReserved pattern.[i + 1] then
                sb.Append(c).Append(pattern.[i + 1]) |> ignore; i <- i + 2
            elif c = '\\' || c = '/' then
                if sb.Length > 0 then segments.Add(sb.ToString()); sb.Clear() |> ignore
                i <- i + 1
            else
                sb.Append c |> ignore; i <- i + 1
        if sb.Length > 0 then segments.Add(sb.ToString())
        segments.ToArray()

    /// Parses a fileset mask (user-written): escapes and the deprecated constructs as in rule
    /// patterns (reported once per mask). A mask segment keeps its pattern text (it goes to the mask
    /// regex for excludes and, escapes resolved, to the OS glob for includes); a literal segment
    /// holds its plain text.
    let parseMask isLastPart (pattern: string) =
        if notNullOrEmpty pattern then
            PatternAst.Warnings.checkMask pattern
            let parts = splitMask pattern
            let rooted = pattern.[0] = '/' || (pattern.[0] = '\\' && not (pattern.Length > 1 && PatternAst.isReserved pattern.[1]))
            let fsroot = if rooted then [FsRoot] else []
            let isLast = isLastPart parts
            let mapMaskPart idx (seg: string) =
                match mapPart (isLast idx) seg with
                | Directory d -> Directory (d |> PatternAst.parse |> PatternAst.plainText)
                | FileName f -> FileName (f |> PatternAst.parse |> PatternAst.plainText)
                | part -> part
            fsroot @ (parts |> Array.mapi mapMaskPart |> List.ofArray)
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

    /// Whether a rule pattern is rooted (`Path.Combine` would drop the project root for it): it
    /// starts with '/' (or '\', parsed to '/'), or with a drive on Windows.
    let isRootedPattern (tokens: PatternAst.Token list) =
        match tokens with
        | PatternAst.Lit s :: _ ->
            s.StartsWith "/" || (not Env.isUnix && s.Length >= 2 && s.[1] = ':' && System.Char.IsLetter s.[0])
        | _ -> false

    /// The tokens of a rule pattern joined to the project root, the root being a literal (it is a
    /// path, not a pattern). ".." / "." are collapsed only within the literal directory prefix (up
    /// to, but not including, the segment where the first wildcard, group or reserved character
    /// appears); the rest is left as given -- a named group can contain '/' (e.g. "(root:*/*-a).ss").
    let joinRoot (rootPath: string) (tokens: PatternAst.Token list) =
        let tokens =
            if System.String.IsNullOrEmpty rootPath || isRootedPattern tokens then tokens
            else
                let root = rootPath.Replace('\\', '/')
                PatternAst.Lit (if root.EndsWith "/" then root else root + "/") :: tokens
        let rec splitLits acc = function
            | PatternAst.Lit s :: rest -> splitLits (acc + s) rest
            | rest -> acc, rest
        match splitLits "" tokens with
        | "", _ -> tokens
        | text, rest ->
            let segments = text.Split('/') |> List.ofArray
            let prefix, partial =
                if List.isEmpty rest then segments, []
                else List.take (segments.Length - 1) segments, [ List.last segments ]
            PatternAst.Lit (System.String.Join("/", collapseDotSegments [] prefix @ partial)) :: rest

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

module internal matchImpl =

    let eq s1 s2 = System.StringComparer.OrdinalIgnoreCase.Equals(s1, s2)

    let normalizeSlashes (pat: string) =
        pat.Replace('\\', '/')

    let private ignoreCase = if Env.isUnix then RegexOptions.None else RegexOptions.IgnoreCase

    let private compile (pattern: string) (regex: string) =
        try Regex(@"^" + regex + "$", RegexOptions.Compiled + ignoreCase)
        with :? System.ArgumentException as e ->
            raise (PatternAst.PatternException (
                    sprintf "Invalid pattern '%s': %s (an unbalanced '(', ')' or '['?). Write \\( \\) \\[ \\] for a literal."
                        pattern e.Message))

    // Building a compiled Regex costs about a millisecond; the masks are few (a script's
    // patterns and their segments) and matched against every file or target, so each is
    // built once. A Regex is safe to share between threads for matching.
    let private regexCache = System.Collections.Concurrent.ConcurrentDictionary<string, Regex> ()
    let private ruleCache = System.Collections.Concurrent.ConcurrentDictionary<struct (string * string), Regex> ()

    /// The anchored regex for an Ant-style mask, built once per distinct mask.
    let maskToRegex (pattern:string) =
        regexCache.GetOrAdd (pattern, fun p -> p |> PatternAst.parse |> PatternAst.toRegex |> compile p)

    /// The anchored regex for a rule pattern under a project root, built once per pair. The
    /// pattern's deprecated constructs are reported here (once per pattern).
    let ruleRegex (pattern: string) (rootPath: string) =
        ruleCache.GetOrAdd (struct (pattern, rootPath), fun (struct (p, root)) ->
            PatternAst.Warnings.check p
            p |> PatternAst.parse |> impl.joinRoot root |> PatternAst.toRegex |> compile p)

    /// What the OS glob gets for a mask segment of an include: escapes resolved, `[c]` read as c.
    let maskToGlob (mask: string) = mask |> PatternAst.parse |> PatternAst.plainText

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
let parseDir = impl.parseMask impl.isLastPartForDir

/// <summary>
/// Converts Ant-style file pattern to a PathMask.
/// </summary>
let parse = impl.parseMask impl.isLastPartForFile

/// Parses a directory path (not a mask: no escapes, '\' and '/' both separate, no warnings).
let internal parseDirPath = impl.parse impl.isLastPartForDir

(*
/// <summary>
/// Returns true if a file name (parsed to p) matches specific file mask.         
/// </summary>
/// <param name="mask"></param>
/// <param name="file"></param>
let matchesPattern (pattern:string) =

    let regex = matchImpl.maskToRegex pattern
    fun file -> regex.Match(matchImpl.normalizeSlashes file).Success
*)
/// Returns true if the parsed file path matches the given PathMask.
let matchesPattern (PathMask mask) file =
    let (PathMask fileParts) = file |> impl.parse impl.isLastPartForFile in
    matchImpl.matchPaths mask fileParts

/// Returns true if the file matches the Ant-style pattern relative to the given root.
let matches filePattern rootPath =
    // IDEA: make relative path then match to pattern?
    // matches "src/**/*.cs" "c:\!\src\a\b\c.cs" -> true

    matchesPattern <| join (parseDirPath rootPath) (parse filePattern)

/// Matches a file against a pattern and returns captured named groups, or None if no match.
let matchGroups (pattern:string) rootPath =

    // The root is a literal (escaped in the regex); ".." / "." are collapsed in the literal
    // directory prefix, since an actual target file's path (File.make -> FileInfo ->
    // GetFullPath) has them resolved away (see `impl.joinRoot`).
    let regex = matchImpl.ruleRegex pattern rootPath
    fun file ->
        let m = regex.Match(matchImpl.normalizeSlashes file)
        if m.Success then
            [for groupName in regex.GetGroupNames()  do
                let group = m.Groups.[groupName]
                yield groupName, group.Value] |> Some
        else
            None

module private SpecImpl =
    let cache = System.Collections.Concurrent.ConcurrentDictionary<string, Specificity> ()
    let compute (pattern: string) : Specificity = pattern |> PatternAst.parse |> PatternAst.specificity

/// Specificity of a rule pattern (see the `Specificity` type), memoized per pattern string.
let specificity (pattern: string) : Specificity =
    SpecImpl.cache.GetOrAdd(pattern, System.Func<_,_> SpecImpl.compute)

