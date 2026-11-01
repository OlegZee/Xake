/// The scanner stage of the pattern module (docs/features/pattern-module/spec.md sections 6, 7.3
/// and 9.5; decisions D9, D10, D11, D13, D17, D25, D29, D33, D39 in brief.md): a depth-first
/// walker driven by the AST of each mask. Internal: the engine's `Fileset` keeps its 3.x scanner
/// until Phase 4, when `Fileset.toFileList` becomes an adapter over `scan` (D31).
///
/// The walk of one include, from its anchor (the base directory, or the root of an absolute mask):
/// - the mask is split into segments; braces holding `/` (or a globstar) are expanded first and the
///   alternatives are walked together, so the union comes out in tree order (6.3, D39);
/// - at each directory the walker holds the set of continuations (remaining segments) that apply
///   to it; a globstar adds its zero-segment continuation;
/// - when a continuation needs a listing (a wildcard segment or a globstar), the directory is
///   listed once (legacy `DirectoryInfo.EnumerateFileSystemInfos`, the OS filter only as a superset
///   pre-filter), sorted ordinally, and every head, literal heads included, is decided by the
///   matcher (`PatternMatch`); otherwise every literal head is probed (`File.Exists`,
///   `Directory.Exists`) and keeps the mask's spelling (D25);
/// - a directory link is entered by a one-segment pattern or a literal, never by globstar
///   recursion (logged once as `LinkSkipped`, D29); an unreadable directory is skipped and logged
///   once as `Unreadable` (6.6), so is a failed probe for lack of permission (D39);
/// - a subdirectory entirely covered by a later exclude is not entered (pruning, 6.3 step 3).
module internal Xake.PatternScan

open System
open System.IO
open System.Text
open System.Collections.Generic

open Xake.PatternAst
open Xake.PatternMatch

/// A build-log note of the walk; the path is a full directory path (6.5, 6.6).
type ScanNote =
    /// a directory link met under `**`, not entered (Message level, D29)
    | LinkSkipped of dir: string
    /// a directory that could not be listed or searched (Warning level, 6.6, D39)
    | Unreadable of dir: string

/// An evidence or evaluation warning of one evaluation (7.3): XP104, XP113, XP114, XP120.
type ScanWarning =
    { Code: string
      /// "include" | "exclude"
      Use: string
      Pattern: string
      /// the part text of 7.3
      Text: string }

/// The result of evaluating a fileset.
type ScanResult =
    { /// full paths in result order (6.6); directories (a trailing-`/` include) included
      Files: string list
      /// once per (code, use, pattern) within this evaluation
      Warnings: ScanWarning list
      /// once per directory within this evaluation
      Notes: ScanNote list }

// =================================================================================================
// Paths
// =================================================================================================

let private hostWindows = not Env.isUnix
let private osSep = Path.DirectorySeparatorChar

/// A directory or file: the path as the walker spells it (`Os`) and with `/` (`Norm`, for matching).
[<Struct>]
type private Loc = { Os: string; Norm: string }

let private toNorm (p: string) = if hostWindows then p.Replace('\\', '/') else p

let private loc (os: string) = { Os = os; Norm = toNorm os }

let private join (d: Loc) (name: string) =
    let endsWithSep (s: string) = s.Length > 0 && (s.[s.Length - 1] = '/' || s.[s.Length - 1] = osSep)
    { Os = (if endsWithSep d.Os then d.Os + name else d.Os + string osSep + name)
      Norm = (if d.Norm.EndsWith "/" then d.Norm + name else d.Norm + "/" + name) }

let private parent (d: Loc) : Loc option =
    match Path.GetDirectoryName d.Os with
    | null | "" -> None
    | p -> Some (loc p)

/// The base directory as passed, without a trailing separator (a root keeps it).
let private baseLoc (baseDir: string) =
    let t = baseDir.TrimEnd('/', osSep)
    loc (if t = "" || t.EndsWith ":" then baseDir else t)

// =================================================================================================
// Masks: root, segments, brace expansion
// =================================================================================================

type private Root =
    | Relative
    | RootSep
    | Drive of string
    | Unc of server: string * share: string

let private rootLoc (root: Root) (baseD: Loc) =
    match root with
    | Relative -> baseD
    | RootSep -> { Os = string osSep; Norm = "/" }
    | Drive d -> { Os = d + string osSep; Norm = d + "/" }
    | Unc (server, share) -> { Os = String(osSep, 2) + server + string osSep + share; Norm = "//" + server + "/" + share }

/// The root and the body of a parsed mask (4.2, D24), the body without the trailing separator.
let private splitRoot (tokens: Token list) : Root * Token list * bool =
    let root, rest =
        match tokens with
        | Sep :: Sep :: Lit server :: Sep :: Lit share :: rest -> Unc (server, share), rest
        | Lit d :: Sep :: rest when d.Length = 2 && d.[1] = ':' && Char.IsLetter d.[0] -> Drive d, rest
        | Sep :: rest -> RootSep, rest
        | _ -> Relative, tokens
    let rest = match root, rest with Unc _, Sep :: r -> r | _ -> rest
    match List.rev rest with
    | Sep :: body -> root, List.rev body, true
    | _ -> root, rest, (match root, tokens with Unc _, _ -> List.tryLast tokens = Some Sep && rest.IsEmpty | _ -> false)

let rec private hasWildcard (tokens: Token list) =
    tokens |> List.exists (function
        | Star | AnyChar | Globstar | DoubleStar | Class _ -> true
        | Alt _ -> true
        | _ -> false)

/// Braces whose alternatives hold a separator or a globstar (6.3: expanded before walking).
let rec private crossesSegments (alts: Token list list) =
    alts |> List.exists (List.exists (function Sep | Globstar -> true | Alt a -> crossesSegments a | _ -> false))

let rec private expand (tokens: Token list) : Token list list =
    match tokens |> List.tryFindIndex (function Alt a -> crossesSegments a | _ -> false) with
    | None -> [ tokens ]
    | Some i ->
        match List.item i tokens with
        | Alt alts ->
            let pre = List.take i tokens
            let post = List.skip (i + 1) tokens
            alts |> List.collect (fun a -> expand (pre @ a @ post))
        | _ -> [ tokens ]

let private mergeLits (tokens: Token list) =
    let rec go acc tokens =
        match tokens with
        | Lit a :: Lit b :: rest -> go acc (Lit (a + b) :: rest)
        | t :: rest -> go (t :: acc) rest
        | [] -> List.rev acc
    go [] tokens

/// Splits at top-level separators; empty segments (from an expansion) are dropped.
let private segmentsOf (tokens: Token list) : Token list list =
    let segs = ResizeArray<Token list>()
    let cur = ResizeArray<Token>()
    let flush () =
        if cur.Count > 0 then
            segs.Add(mergeLits (List.ofSeq cur))
            cur.Clear()
    for t in tokens do
        match t with
        | Sep -> flush ()
        | _ -> cur.Add t
    flush ()
    List.ofSeq segs

/// The literal prefix of a mask (6.3 step 1) as a location: the anchor, leading `..` resolved,
/// then the literal segments before the first non-literal one.
let private literalPrefix (baseD: Loc) (root: Root) (segments: Token list list) =
    let rec go (d: Loc) (atStart: bool) (segs: Token list list) =
        match segs with
        | [ Lit ".." ] :: rest when atStart && root = Relative ->
            match parent d with
            | Some p -> go p true rest
            | None -> go (join d "..") false rest
        | [ Lit s ] :: rest -> go (join d s) false rest
        | _ -> d, segs
    go (rootLoc root baseD) true segments

// =================================================================================================
// Segments of the walk
// =================================================================================================

type private PatSeg =
    { Text: string
      IsMatch: string -> bool
      /// the OS search pattern: the segment when it is safe (6.4), else "*"
      Filter: string
      /// the segment is `*.*` (XP114)
      StarDotStar: bool
      /// the literal readings of 3.x (XP104), empty when the segment has none
      Readings: string list
      /// the first class or brace of the segment (XP104 text)
      Construct: Token option }

type private Seg =
    | SLit of text: string * isMatch: (string -> bool)
    | SGlob
    | SPat of PatSeg

let private segKey = function
    | SLit (s, _) -> "L" + s
    | SGlob -> "G"
    | SPat p -> "P" + p.Text

let private safeFilter (windows: bool) (tokens: Token list) =
    // a case-sensitive OS filter (Linux) is not a superset of the Windows-mode comparer
    if windows && not hostWindows then "*"
    else
        let safeChar (c: char) =
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || "._-+ ".IndexOf c >= 0
        let sb = StringBuilder()
        let ok =
            tokens |> List.forall (function
                | Lit s when s |> Seq.forall safeChar -> sb.Append s |> ignore; true
                | Star | DoubleStar -> sb.Append '*' |> ignore; true
                | AnyChar -> sb.Append '?' |> ignore; true
                | _ -> false)
        if ok then sb.ToString() else "*"

let private classText (bang: string) (negated: bool) (items: ClassItem list) =
    let item = function One c -> string c | Range (a, b) -> string a + "-" + string b
    "[" + (if negated then bang else "") + String.Join("", items |> List.map item) + "]"

/// The literal text 3.x read for a segment holding a class or braces and no other wildcard
/// (XP104, D39); none for a one-character class only (`sub[1]`: XP106 at parse).
let private readings (tokens: Token list) : string list =
    let rec wild (ts: Token list) =
        ts |> List.exists (function Star | AnyChar | Globstar | DoubleStar -> true | Alt alts -> List.exists wild alts | _ -> false)
    let legacy =
        tokens |> List.exists (function
            | Alt _ -> true
            | Class (false, [ One _ ]) -> false
            | Class _ -> true
            | _ -> false)
    if wild tokens || not legacy then []
    else
        let rec text (bang: string) (ts: Token list) =
            ts |> List.map (function
                | Lit s -> s
                | Sep -> "/"
                | Class (negated, items) -> classText bang negated items
                | Alt alts -> "{" + String.Join(",", alts |> List.map (text bang)) + "}"
                | _ -> "")
            |> String.concat ""
        List.distinct [ text "^" tokens; text "!" tokens ]

let private segment (windows: bool) (tokens: Token list) : Seg =
    let matcher (text: string) = let m = maskMatcher windows "" text in fun (name: string) -> m name false
    match tokens with
    | [ Lit s ] -> SLit (s, matcher (escape s))
    | [ Globstar ] -> SGlob
    | _ ->
        let text = unparse tokens
        let starDotStar = (tokens = [ Star; Lit "."; Star ])
        SPat
            { Text = text
              IsMatch = matcher text
              Filter = (if starDotStar then "*" else safeFilter windows tokens)
              StarDotStar = starDotStar
              Readings = readings tokens
              Construct = tokens |> List.tryFind (function Class _ | Alt _ -> true | _ -> false) }

// =================================================================================================
// Warning texts (7.3)
// =================================================================================================

module private Texts =
    let xp104 (construct: Token option) (reading: string) (entry: string) =
        match construct with
        | Some (Class (negated, items)) ->
            let item = function One c -> sprintf "'%c'" c | Range (a, b) -> sprintf "'%c'-'%c'" a b
            let what =
                let list = String.Join(", ", items |> List.map item)
                if negated then sprintf "any character but %s" list else sprintf "one of %s" list
            sprintf "'%s' is a character class from Xake 4.0 (%s); Xake 3.x read the literal text '%s', and '%s' exists; write %s for the literal text"
                (classText "^" negated items) what reading entry (escape reading)
        | _ ->
            sprintf "'%s' is an alternation from Xake 4.0; Xake 3.x read the literal text '%s', and '%s' exists; write %s for the literal text"
                reading reading entry (escape reading)
    let xp113 (text: string) =
        let dir = if text.EndsWith "/**" then text.Substring(0, text.Length - 3) else text
        sprintf "'%s' is every file below '%s' from Xake 4.0; in Xake 3.x an include listed the directories below '%s' and an exclude matched nothing; write '%s/**/' for the directories"
            text dir dir dir
    let xp114 (name: string) =
        sprintf "'*.*' needs a '.' in the name from Xake 4.0; Xake 3.x also listed '%s'; write '*' for every name" name
    let xp120 (baseDir: string) =
        sprintf "the exclude is an absolute path outside the fileset's base directory '%s' and every include, so it removes nothing; a leading '/' is the file-system root in Xake (in .gitignore it is the base directory); drop the leading '/' to anchor it at the base directory"
            baseDir

// =================================================================================================
// The walker
// =================================================================================================

/// A continuation: the head segment and the id of the rest (0 = the end of the mask).
[<Struct>]
type private Node = { Head: Seg; Next: int }

type private Item = { Path: string; Norm: string; IsDir: bool }

type private Element =
    { Role: MaskRole
      Text: string
      Tokens: Token list
      Root: Root
      Body: Token list
      DirMode: bool }

let private useName = function MaskRole.Include -> "include" | MaskRole.Exclude -> "exclude"

/// `dir/**` as a file mask (XP113).
let private endsInGlobstar (e: Element) =
    not e.DirMode && (match List.rev e.Tokens with Globstar :: Sep :: _ -> true | _ -> false)

/// Evaluates a fileset (6.3): `fileset` in order, an include appends what it walks, an exclude
/// removes from what was found so far. `windows`: the comparer of D11 (ignore-case; `\` in a path
/// is a separator), it never changes the file-system calls. `baseDir`: an absolute directory,
/// which may not exist. Raises PatternException when a mask does not parse.
let scan (windows: bool) (baseDir: string) (fileset: (MaskRole * string) list) : ScanResult =
    let elements =
        fileset |> List.map (fun (role, text) ->
            let tokens = Mask.parse text
            let root, body, dirMode = splitRoot tokens
            { Role = role; Text = text; Tokens = tokens; Root = root; Body = body; DirMode = dirMode })
        |> Array.ofList
    let cmp = if windows then StringComparer.OrdinalIgnoreCase else StringComparer.Ordinal
    let cmpKind = if windows then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
    let baseD = baseLoc baseDir

    let notes = ResizeArray<ScanNote>()
    let noted = HashSet<string>(cmp)
    let note (link: bool) (d: Loc) =
        if noted.Add((if link then "L" else "U") + d.Norm) then
            notes.Add(if link then LinkSkipped d.Os else Unreadable d.Os)
    let warnings = ResizeArray<ScanWarning>()
    let warned = HashSet<string>()
    let warn (code: string) (e: Element) (text: string) =
        let usage = useName e.Role
        if warned.Add(code + "\n" + usage + "\n" + e.Text) then
            warnings.Add { Code = code; Use = usage; Pattern = e.Text; Text = text }

    let isUnder (x: string) (y: string) =
        x.Equals(y, cmpKind) || x.StartsWith(y.TrimEnd '/' + "/", cmpKind)

    /// The matcher of an exclude, anchored at the base directory (6.3 step 4).
    let excludeMatchers = elements |> Array.map (fun e -> lazy (maskMatcher windows baseD.Norm e.Text))
    /// XP113 candidates prune only once they are known to remove a file (6.3: the evidence
    /// follows the unpruned result).
    let observed = Array.create elements.Length false

    /// Pruning (6.3 step 3): `P/` for every include; `P/**` and `P/**/*` for file includes.
    let prunesAfter (k: int) (dirInclude: bool) =
        [ for j in k + 1 .. elements.Length - 1 do
            let e = elements.[j]
            if e.Role = MaskRole.Exclude then
                let covering (prefix: Token list) =
                    let _, body, _ = splitRoot (prefix @ [ Sep ])
                    if body.IsEmpty then None else Some (maskMatcher windows baseD.Norm (unparse prefix + "/"))
                let rev = List.rev e.Tokens
                if e.DirMode then yield j, false, excludeMatchers.[j].Value
                elif not dirInclude then
                    match rev with
                    | Globstar :: Sep :: p ->
                        match covering (List.rev p) with Some m -> yield j, true, m | None -> ()
                    | Star :: Sep :: Globstar :: Sep :: p ->
                        match covering (List.rev p) with Some m -> yield j, false, m | None -> ()
                    | _ -> () ]

    let deniedProbe (path: string) =
        try File.GetAttributes path |> ignore; false
        with
        | :? UnauthorizedAccessException -> true
        | _ -> false

    let listing (d: Loc) (filter: string) : (List<string> * List<struct (string * bool)>) option =
        try
            let files = List<string>()
            let dirs = List<struct (string * bool)>()
            for e in DirectoryInfo(d.Os).EnumerateFileSystemInfos(filter) do
                match e with
                | :? DirectoryInfo -> dirs.Add(struct (e.Name, (e.Attributes &&& FileAttributes.ReparsePoint) = FileAttributes.ReparsePoint))
                | _ -> files.Add e.Name
            files.Sort(StringComparer.Ordinal)
            dirs.Sort(fun x y -> let struct (a, _) = x in let struct (b, _) = y in String.CompareOrdinal(a, b))
            Some (files, dirs)
        with
        | :? DirectoryNotFoundException -> None
        | :? UnauthorizedAccessException
        | :? IOException
        | :? Security.SecurityException ->
            note false d
            None

    /// Walks one include; returns what it finds, in tree order.
    let walk (k: int) (e: Element) : ResizeArray<Item> =
        let found = ResizeArray<Item>()
        let prunes = prunesAfter k e.DirMode
        let candidates = prunes |> List.filter (fun (_, gated, _) -> gated) |> List.map (fun (j, _, _) -> j)
        let isPruned (d: Loc) =
            prunes |> List.exists (fun (j, gated, m) -> (not gated || observed.[j]) && m d.Norm true)
        let emit (d: Loc) (isDir: bool) =
            found.Add { Path = d.Os; Norm = d.Norm; IsDir = isDir }
            if not isDir then
                for j in candidates do
                    if not observed.[j] && excludeMatchers.[j].Value d.Norm false then observed.[j] <- true

        // ---- the continuations of every brace alternative, interned
        let nodes = List<Node>()
        nodes.Add { Head = SGlob; Next = -1 }      // 0: the end of the mask
        let ids = Dictionary<string, int>()
        let rec intern (segs: Seg list) : int =
            match segs with
            | [] -> 0
            | head :: tail ->
                let key = String.Join("\u0000", segs |> List.map segKey)
                match ids.TryGetValue key with
                | true, id -> id
                | _ ->
                    let next = intern tail
                    let id = nodes.Count
                    nodes.Add { Head = head; Next = next }
                    ids.[key] <- id
                    id
        // a globstar ending a file mask takes one segment or more: '*' here, '**' below (5.5)
        let starNode = if e.DirMode then 0 else intern [ segment windows [ Star ] ]
        let zero (i: int) = match nodes.[i].Next with 0 -> starNode | n -> n
        let starts = List<Loc * List<int>>()
        for alt in expand e.Body do
            let segs = segmentsOf alt
            let anchor, rest =
                let rec up (d: Loc) (segs: Token list list) =
                    match segs with
                    | [ Lit ".." ] :: tail when e.Root = Relative ->
                        match parent d with
                        | Some p -> up p tail
                        | None -> d, segs
                    | _ -> d, segs
                up (rootLoc e.Root baseD) segs
            let id = intern (rest |> List.map (segment windows))
            match starts |> Seq.tryFind (fun (a, _) -> cmp.Equals(a.Norm, anchor.Norm)) with
            | Some (_, l) -> if not (l.Contains id) then l.Add id
            | None -> starts.Add((anchor, List<int>([ id ])))

        let isFileHead (i: int) =
            let n = nodes.[i]
            n.Next = 0 && not e.DirMode && (match n.Head with SGlob -> false | _ -> true)
        let headMatches (i: int) (name: string) =
            match nodes.[i].Head with
            | SLit (_, m) -> m name
            | SPat p -> p.IsMatch name
            | SGlob -> true
        let evidence (i: int) (name: string) =
            match nodes.[i].Head with
            | SPat p ->
                if p.StarDotStar && name.IndexOf '.' < 0 then warn "XP114" e (Texts.xp114 name)
                for r in p.Readings do
                    if String.Equals(r, name, cmpKind) then warn "XP104" e (Texts.xp104 p.Construct r name)
            | _ -> ()

        let rec visit (d: Loc) (depth: int) (start: seq<int>) =
            // closure: a globstar also applies the rest of the mask here (zero segments)
            let set = List<int>()
            let seen = HashSet<int>()
            for i in start do if seen.Add i then set.Add i
            let mutable n = 0
            while n < set.Count do
                let i = set.[n]
                if i <> 0 then
                    match nodes.[i].Head with
                    | SGlob -> let z = zero i in if seen.Add z then set.Add z
                    | _ -> ()
                n <- n + 1
            if e.DirMode && depth > 0 && seen.Contains 0 then emit d true
            let active = set |> Seq.filter (fun i -> i <> 0) |> Array.ofSeq
            if active.Length > 0 then
                let needsListing = active |> Array.exists (fun i -> match nodes.[i].Head with SLit _ -> false | _ -> true)
                if needsListing then listed d depth active else probed d depth active

        and listed (d: Loc) (depth: int) (active: int[]) =
            let filter =
                match active |> Array.map (fun i -> nodes.[i].Head) |> Array.distinctBy segKey with
                | [| SPat p |] -> p.Filter
                | _ -> "*"
            match listing d filter with
            | None -> ()
            | Some (files, dirs) ->
                let fileHeads = active |> Array.filter isFileHead
                if fileHeads.Length > 0 then
                    for name in files do
                        let mutable hit = false
                        for i in fileHeads do
                            evidence i name
                            if not hit && headMatches i name then hit <- true
                        if hit then emit (join d name) false
                let dirHeads = active |> Array.filter (isFileHead >> not)
                if dirHeads.Length > 0 then
                    let globs = dirHeads |> Array.filter (fun i -> match nodes.[i].Head with SGlob -> true | _ -> false)
                    for entry in dirs do
                        let struct (name, isLink) = entry
                        let next = List<int>()
                        for i in dirHeads do
                            match nodes.[i].Head with
                            | SGlob -> ()
                            | _ ->
                                evidence i name
                                if headMatches i name then next.Add nodes.[i].Next
                        if next.Count > 0 || globs.Length > 0 then
                            let sub = join d name
                            if not (isPruned sub) then
                                if isLink then
                                    // globstar recursion does not enter a link (6.5, D17)
                                    if globs.Length > 0 then note true sub
                                    if next.Count > 0 then visit sub (depth + 1) next
                                else
                                    next.AddRange globs
                                    visit sub (depth + 1) next

        and probed (d: Loc) (depth: int) (active: int[]) =
            let litOf (i: int) = match nodes.[i].Head with SLit (s, _) -> s | _ -> ""
            let fileNames =
                active |> Array.filter isFileHead |> Array.map litOf
                |> (fun names -> let seen = HashSet<string>(cmp) in names |> Array.filter seen.Add)
                |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
            for name in fileNames do
                let f = join d name
                if File.Exists f.Os then emit f false
                elif deniedProbe f.Os then note false d
            let dirs = Dictionary<string, List<int>>(cmp)
            let order = List<string>()
            for i in active do
                if not (isFileHead i) then
                    let name = litOf i
                    match dirs.TryGetValue name with
                    | true, l -> l.Add nodes.[i].Next
                    | _ ->
                        dirs.[name] <- List<int>([ nodes.[i].Next ])
                        order.Add name
            order.Sort(fun a b -> String.CompareOrdinal(a, b))
            for name in order do
                let sub = join d name
                if not (isPruned sub) then
                    if Directory.Exists sub.Os then visit sub (depth + 1) dirs.[name]
                    elif deniedProbe sub.Os then note false d

        for anchor, ids in starts do visit anchor 0 ids
        found

    // ---- the fold (6.3)
    let results = ResizeArray<Item>()
    let present = HashSet<string>(cmp)
    let anchors = ResizeArray<string>([ baseD.Norm ])
    for k in 0 .. elements.Length - 1 do
        let e = elements.[k]
        match e.Role with
        | MaskRole.Include ->
            let found =
                if not (hasWildcard e.Body) then
                    // an explicit name is listed whether or not it exists (6.2, D13)
                    let d, _ = literalPrefix baseD e.Root (segmentsOf e.Body)
                    ResizeArray [ { Path = d.Os; Norm = d.Norm; IsDir = e.DirMode } ]
                else walk k e
            if endsInGlobstar e && found |> Seq.exists (fun i -> not i.IsDir) then warn "XP113" e (Texts.xp113 e.Text)
            for item in found do
                if present.Add item.Norm then results.Add item
            anchors.Add((fst (literalPrefix baseD e.Root (segmentsOf e.Body))).Norm)
        | MaskRole.Exclude ->
            // XP120: an absolute exclude outside the base and every earlier include's anchor (D6, D39)
            if e.Root <> Relative then
                let prefix = (fst (literalPrefix baseD e.Root (segmentsOf e.Body))).Norm
                if not (anchors |> Seq.exists (fun a -> isUnder prefix a || isUnder a prefix)) then
                    warn "XP120" e (Texts.xp120 baseDir)
            // XP104: a found path has a segment named as the 3.x literal reading
            let rs = segmentsOf e.Body |> List.collect readings
            if not rs.IsEmpty then
                let prefix = baseD.Norm.TrimEnd '/' + "/"
                for item in results do
                    let rel = if item.Norm.StartsWith(prefix, cmpKind) then item.Norm.Substring prefix.Length else item.Norm
                    for s in rel.Split '/' do
                        for r in rs do
                            if String.Equals(r, s, cmpKind) then
                                let construct = segmentsOf e.Body |> List.tryPick (fun seg -> if List.contains r (readings seg) then List.tryFind (function Class _ | Alt _ -> true | _ -> false) seg else None)
                                warn "XP104" e (Texts.xp104 construct r s)
            let m = excludeMatchers.[k].Value
            let dirVerdicts = Dictionary<string, bool>()
            let dirMatches (p: string) =
                match dirVerdicts.TryGetValue p with
                | true, v -> v
                | _ ->
                    let v = m p true
                    dirVerdicts.[p] <- v
                    v
            let removes (item: Item) =
                if e.DirMode then
                    (item.IsDir && dirMatches item.Norm)
                    || (let p = item.Norm
                        let mutable hit = false
                        let mutable i = p.IndexOf('/', 1)
                        while not hit && i > 0 do
                            hit <- dirMatches (p.Substring(0, i))
                            i <- if i + 1 < p.Length then p.IndexOf('/', i + 1) else -1
                        hit)
                else not item.IsDir && m item.Norm false
            let kept = results |> Seq.filter (removes >> not) |> Array.ofSeq
            if kept.Length < results.Count then
                if endsInGlobstar e then warn "XP113" e (Texts.xp113 e.Text)
                results.Clear()
                results.AddRange kept
                present.Clear()
                for item in kept do present.Add item.Norm |> ignore

    { Files = results |> Seq.map (fun i -> i.Path) |> List.ofSeq
      Warnings = List.ofSeq warnings
      Notes = List.ofSeq notes }

/// Sends the warnings of an evaluation to the build log through the process-wide registry of
/// 7.3 (once per (use, pattern, code)); the notes are the engine's to log (Phase 4).
let reportWarnings (result: ScanResult) =
    for w in result.Warnings do
        Warnings.reportEvidence w.Use w.Pattern { Code = w.Code; Text = w.Text }
