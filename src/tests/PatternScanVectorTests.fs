namespace Tests

open System
open System.IO
open NUnit.Framework

open Xake.PatternAst
open PatternMatchVectors
open PatternScanVectors

/// The scanner contract for PR 4 (spec 6, 7.3; brief D9, D13, D17, D25, D29, D31). The walker
/// lands behind this shim:
///
/// `scan windows baseDir fileset : ScanResult` evaluates a fileset as `Fileset.toFileList` will
/// (6.3): `fileset` is the ordered list of elements, an include appends what it finds, an exclude
/// removes from what was found so far. `baseDir` is an absolute directory (it may not exist).
/// `windows` is the comparer of D11 made explicit (true: ordinal-ignore-case; false: ordinal); it
/// decides matching and duplicates, never the file system calls (a literal probe asks the volume).
///
/// - `Files`: the full paths found, in result order (6.6), each spelled as the walker builds it:
///   `baseDir` (as passed) or the absolute mask's literal prefix, then the probed segments in the
///   mask's spelling (D25) and the listed names in the disk's; `.`/`..` of a literal prefix
///   resolved; `/` or the OS separator. Directories (a trailing-`/` include) without a trailing
///   separator.
/// - `Warnings`: the evidence and evaluation warnings of this one evaluation (7.3: XP104, XP113,
///   XP114, XP120) as `(code, use, pattern)`, use = "include" | "exclude", each at most once per
///   (code, use, pattern). The process-wide "once per pattern" registry of 7.3 must not hide a
///   warning from a later call: the shim reports what this evaluation would report on a fresh
///   registry. Parse-time parts (XP105, XP106, XP110, XP117) are not included.
/// - `Log`: the build-log notes of the walk with full directory paths, once per directory:
///   `LinkSkipped` (6.5, Message) and `Unreadable` (6.6, Warning).
/// - Raises `PatternException` (its message ends in `[XPnnn]`) when a mask does not parse.
module PatternScannerUnderTest =

    type ScanResult =
        { Files: string list
          Warnings: (string * string * string) list
          Log: ScanLog list }

    let scan (windows: bool) (baseDir: string) (fileset: (MaskRole * string) list) : ScanResult =
        ignore (windows, baseDir, fileset)
        raise (NotImplementedException "pattern module PR 4: the walker has not landed")

module private ScanDrive =

    let isUnix = not (OperatingSystem.IsWindows())

    let modes = function
        | Os.Unix -> [ false ]
        | Os.Windows -> [ true ]
        | Os.Any -> [ false; true ]

    let modeName windows = if windows then "Windows" else "Unix"

    type Entry =
        | EFile of string
        | EDir of string
        | ELink of name: string * target: string

    let parseEntry (e: string) =
        match e.IndexOf " -> " with
        | i when i >= 0 -> ELink (e.Substring(0, i), e.Substring(i + 4))
        | _ when e.EndsWith "/" -> EDir (e.TrimEnd '/')
        | _ -> EFile e

    let entryPath = function EFile p | EDir p | ELink (p, _) -> p

    let tempRoot () = Path.Combine(Path.GetTempPath(), "xake-scan-" + Guid.NewGuid().ToString "N")

    /// Runs `probe` in a fresh temp directory, then removes it.
    let inTemp (probe: string -> bool) =
        let dir = tempRoot ()
        Directory.CreateDirectory dir |> ignore
        try probe dir finally Directory.Delete(dir, true)

    let caseInsensitiveVolume =
        lazy (inTemp (fun d ->
            File.WriteAllText(Path.Combine(d, "probe-a"), "")
            File.Exists(Path.Combine(d, "PROBE-A"))))

    let normalizingVolume =
        lazy (inTemp (fun d ->
            Directory.CreateDirectory(Path.Combine(d, "café")) |> ignore
            Directory.Exists(Path.Combine(d, "café"))))

    /// Ignores the running test when the host cannot give a need (symlinks and permissions are
    /// checked when the tree is made).
    let requireHost (need: Need) =
        let fail why = Assert.Ignore(sprintf "host: %s (%A)" why need)
        match need with
        | Need.CaseSensitiveVolume -> if caseInsensitiveVolume.Value then fail "the temp volume is case-insensitive"
        | Need.CaseInsensitiveVolume -> if not caseInsensitiveVolume.Value then fail "the temp volume is case-sensitive"
        | Need.NormalizingVolume -> if not normalizingVolume.Value then fail "the temp volume does not normalize names"
        | Need.NonNormalizingVolume -> if normalizingVolume.Value then fail "the temp volume normalizes names"
        | Need.UnixPermissions -> if not isUnix then fail "chmod is Unix only"
        | Need.Symlinks -> ()

    let os (p: string) = p.Replace('/', Path.DirectorySeparatorChar)

    /// Creates the tree under a fresh root; `created` registers the root and `locked` each
    /// unreadable directory before anything can fail, so that TearDown cleans up.
    let materialize (t: Tree) (created: string -> unit) (locked: string -> unit) : string =
        let root = tempRoot ()
        Directory.CreateDirectory root |> ignore
        created root
        let entries = t.Entries |> List.map parseEntry
        for e in entries do
            match e with
            | EFile p ->
                let full = Path.Combine(root, os p)
                Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
                File.WriteAllText(full, "")
            | EDir p -> Directory.CreateDirectory(Path.Combine(root, os p)) |> ignore
            | ELink _ -> ()
        for e in entries do
            match e with
            | ELink (name, target) ->
                let full = Path.Combine(root, os name)
                Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
                let resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName full, os target))
                try
                    if Directory.Exists resolved then Directory.CreateSymbolicLink(full, os target) |> ignore
                    else File.CreateSymbolicLink(full, os target) |> ignore
                with ex -> Assert.Ignore(sprintf "host: cannot create the symlink %s -> %s (%s)" name target ex.Message)
            | _ -> ()
        for d in t.Unreadable do
            let full = Path.Combine(root, os d)
            locked full
            File.SetUnixFileMode(full, UnixFileMode.None)
            let readable = try Directory.EnumerateFileSystemEntries full |> Seq.length |> ignore; true with _ -> false
            if readable then Assert.Ignore("host: chmod 000 does not block listing (running as root?)")
        root

    let unlock (dir: string) =
        if isUnix then
            try File.SetUnixFileMode(dir, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute) with _ -> ()

    /// `<root>` in a mask or an expected pattern: the tree root, written with escapePath.
    let substitute (root: string) (text: string) = text.Replace("<root>", escapePath root)

    let norm (p: string) = if isUnix then p else p.Replace('\\', '/')

    /// A full path from the walker as a vector spells it (see PatternScanVectors' header).
    let relative (root: string) (baseDir: string) (full: string) =
        let f = norm full
        let b = (norm baseDir).TrimEnd '/'
        let r = (norm root).TrimEnd '/'
        if f.StartsWith(b + "/", StringComparison.Ordinal) then f.Substring(b.Length + 1)
        elif f.StartsWith(r + "/", StringComparison.Ordinal) then "<root>/" + f.Substring(r.Length + 1)
        elif f = r then "<root>"
        else f

    let relativeLog root baseDir = function
        | LinkSkipped d -> LinkSkipped (relative root baseDir d)
        | Unreadable d -> Unreadable (relative root baseDir d)

    let baseOf (root: string) = function
        | At "" -> root
        | At p -> Path.Combine(root, os p)
        | Missing -> Path.Combine(root, "missing-base")

    let roleName = function MaskRole.Include -> "include" | MaskRole.Exclude -> "exclude"

    // ---- reading the tables (the live checks) -----------------------------------------------------------

    /// The pre-order of 6.6: names compared ordinally per directory, the files of a directory before
    /// its subdirectories, a directory before its contents. `dirs`: the paths name directories.
    let treeOrder (dirs: bool) (a: string) (b: string) =
        let sa = a.Split '/'
        let sb = b.Split '/'
        let kind (s: string[]) i = if i = s.Length - 1 && not dirs then 0 else 1
        let rec go i =
            if i = sa.Length && i = sb.Length then 0
            elif i = sa.Length then -1
            elif i = sb.Length then 1
            elif sa.[i] = sb.[i] && kind sa i = kind sb i then go (i + 1)
            else
                match compare (kind sa i) (kind sb i) with
                | 0 -> String.CompareOrdinal(sa.[i], sb.[i])
                | c -> c
        go 0

    let maskOf (fileset: (MaskRole * string) list) = fileset |> List.map snd

    let sampleRoot = "/tmp/xake-root"

module P = PatternScannerUnderTest

/// The scanner-stage conformance vectors (spec 6, 7.3, 11; brief D31, PR 4).
[<TestFixture; Ignore("pattern module: vectors only until the walker lands (PR 4)")>]
type ``Pattern module conformance: scanner (PR 4)``() =

    let roots = ResizeArray<string>()
    let locked = ResizeArray<string>()

    static member ScanCases =
        scanVectors |> List.map (fun v -> TestCaseData(box v).SetName(sprintf "{m}(%s)" v.Id))

    [<TearDown>]
    member _.Cleanup() =
        for d in locked do ScanDrive.unlock d
        locked.Clear()
        for r in roots do
            try if Directory.Exists r then Directory.Delete(r, true) with _ -> ()
        roots.Clear()

    [<TestCaseSource("ScanCases")>]
    member _.scan(v: ScanVector) =
        let tree = trees |> List.find (fun t -> t.Id = v.Tree)
        for n in tree.Needs @ v.Needs do ScanDrive.requireHost n
        let root = ScanDrive.materialize tree roots.Add locked.Add
        let baseDir = ScanDrive.baseOf root v.Base
        let fileset = v.Fileset |> List.map (fun (role, m) -> role, ScanDrive.substitute root m)
        for windows in ScanDrive.modes v.Os do
            let label = sprintf "%s [%s] %A (%s)" v.Id (ScanDrive.modeName windows) v.Fileset v.Note
            match v.Expect with
            | Raises code ->
                let raised =
                    try P.scan windows baseDir fileset |> ignore; None
                    with PatternException m -> Some m
                match raised with
                | Some m -> StringAssert.Contains(sprintf "[%s]" code, m, label)
                | None -> Assert.Fail(sprintf "%s: expected %s" label code)
            | Lists (files, warnings, log) ->
                let r = P.scan windows baseDir fileset
                Assert.AreEqual(files, r.Files |> List.map (ScanDrive.relative root baseDir), label)
                let expectedWarnings = warnings |> List.map (fun (c, u, p) -> c, u, ScanDrive.substitute root p) |> List.sort
                Assert.AreEqual(expectedWarnings, List.sort r.Warnings, label + " (warnings)")
                Assert.AreEqual(List.sort log, r.Log |> List.map (ScanDrive.relativeLog root baseDir) |> List.sort, label + " (log)")

/// Checks of the scanner tables themselves (not of the walker): ids, trees, that every mask
/// parses with the PR 2 parser, the order and uniqueness of the expected paths, and coverage.
/// These run now.
[<TestFixture>]
type ``Pattern module conformance: scanner vector tables``() =

    let evidenceCodes = set [ "XP104"; "XP113"; "XP114"; "XP120" ]

    [<Test>]
    member _.``ids are unique and carry their table's prefix``() =
        let ids = scanVectors |> List.map (fun v -> v.Id)
        let treeIds = trees |> List.map (fun t -> t.Id)
        for all in [ ids; treeIds ] do
            let dups = all |> List.countBy id |> List.filter (fun (_, n) -> n > 1)
            Assert.IsEmpty(dups, sprintf "%A" dups)
        for i in ids do StringAssert.StartsWith("SC-", i)
        for i in treeIds do StringAssert.StartsWith("T-", i)
        let specNotes = scanVectors |> List.filter (fun v -> v.Note.StartsWith "SPEC?") |> List.length
        TestContext.WriteLine(sprintf "SC-%d T-%d SPEC?-%d" ids.Length treeIds.Length specNotes)

    [<Test>]
    member _.``every vector names an existing tree, every tree is used``() =
        let treeIds = trees |> List.map (fun t -> t.Id) |> set
        for v in scanVectors do Assert.That(treeIds.Contains v.Tree, v.Id + ": unknown tree " + v.Tree)
        let used = scanVectors |> List.map (fun v -> v.Tree) |> set
        for t in trees do Assert.That(used.Contains t.Id, t.Id + ": no vector uses it")

    [<Test>]
    member _.``every tree path is well formed``() =
        for t in trees do
            let entries = t.Entries |> List.map ScanDrive.parseEntry
            let paths = entries |> List.map ScanDrive.entryPath
            let dups = paths |> List.countBy id |> List.filter (fun (_, n) -> n > 1)
            Assert.IsEmpty(dups, sprintf "%s: %A" t.Id dups)
            for p in paths do
                let label = sprintf "%s: '%s'" t.Id p
                Assert.That(p <> "" && not (p.StartsWith "/") && not (p.Contains "\\"), label)
                for s in p.Split '/' do
                    Assert.That(s <> "" && s <> "." && s <> "..", label + ": a segment is empty, '.' or '..'")
            // a file or a link is never the parent of another entry
            for e in entries do
                match e with
                | ScanDrive.EFile p | ScanDrive.ELink (p, _) ->
                    for q in paths do Assert.That(not (q.StartsWith(p + "/")), sprintf "%s: '%s' lies below the file '%s'" t.Id q p)
                | ScanDrive.EDir _ -> ()
            for e in entries do
                match e with
                | ScanDrive.ELink (name, target) ->
                    Assert.That(target <> "" && not (target.StartsWith "/") && not (target.Contains "\\"), sprintf "%s: link %s -> '%s'" t.Id name target)
                | _ -> ()
            // an unreadable directory is a directory of the tree
            for d in t.Unreadable do
                Assert.That(paths |> List.exists (fun p -> p.StartsWith(d + "/") || p = d), sprintf "%s: unreadable '%s' is not in the tree" t.Id d)
            if not t.Unreadable.IsEmpty then
                CollectionAssert.Contains(t.Needs, Need.UnixPermissions, t.Id)
            if entries |> List.exists (function ScanDrive.ELink _ -> true | _ -> false) then
                CollectionAssert.Contains(t.Needs, Need.Symlinks, t.Id)
            // names that differ only in case need a case-sensitive volume
            let folded = paths |> List.map (fun p -> p.ToUpperInvariant()) |> List.countBy id |> List.filter (fun (_, n) -> n > 1)
            if not folded.IsEmpty then CollectionAssert.Contains(t.Needs, Need.CaseSensitiveVolume, t.Id)

    [<Test>]
    member _.``every mask parses, a Raises vector fails with its code``() =
        for v in scanVectors do
            let results = v.Fileset |> List.map (fun (_, m) -> m, Mask.tryParse (ScanDrive.substitute ScanDrive.sampleRoot m))
            match v.Expect with
            | Lists _ ->
                for m, r in results do
                    match r with
                    | Ok _ -> ()
                    | Error e -> Assert.Fail(sprintf "%s: mask '%s' does not parse: %O" v.Id m e)
            | Raises code ->
                match results |> List.tryPick (fun (_, r) -> match r with Error e -> Some e | Ok _ -> None) with
                | Some e -> Assert.AreEqual(code, e.Code, sprintf "%s: %O" v.Id e)
                | None -> Assert.Fail(sprintf "%s: expected %s, every mask parses" v.Id code)

    [<Test>]
    member _.``expected paths are well formed, duplicate-free and in walk order``() =
        for v in scanVectors do
            match v.Expect with
            | Raises _ -> ()
            | Lists (files, _, log) ->
                let all = files @ (log |> List.map (function LinkSkipped d | Unreadable d -> d))
                for p in all do
                    let body = if p.StartsWith "<root>/" then p.Substring 7 else p
                    Assert.That(body <> "" && not (body.StartsWith "/") && not (body.Contains "\\") && not (body.Contains "<root>"),
                                sprintf "%s: '%s'" v.Id p)
                    for s in body.Split '/' do Assert.That(s <> "" && s <> "." && s <> "..", sprintf "%s: '%s'" v.Id p)
                let distinct (cmp: StringComparer) = (Collections.Generic.HashSet<string>(files, cmp)).Count = files.Length
                Assert.That(distinct StringComparer.Ordinal, v.Id + ": a duplicate")
                if v.Os <> Os.Unix then Assert.That(distinct StringComparer.OrdinalIgnoreCase, v.Id + ": a duplicate under the Windows comparer")
                let includes = v.Fileset |> List.filter (fun (r, _) -> r = MaskRole.Include)
                match includes with
                | [ _, m ] when not (m.Contains "<root>") || files |> List.forall (fun f -> f.StartsWith "<root>/") ->
                    let dirs = m.EndsWith "/"
                    let sorted = files |> List.sortWith (ScanDrive.treeOrder dirs)
                    Assert.AreEqual(sorted, files, v.Id + ": not in walk order (6.6)")
                | _ -> ()

    [<Test>]
    member _.``expected warnings are evidence codes of a mask of the fileset``() =
        for v in scanVectors do
            match v.Expect with
            | Raises _ -> ()
            | Lists (_, warnings, _) ->
                Assert.AreEqual(List.length warnings, warnings |> List.distinct |> List.length, v.Id + ": a warning twice")
                for code, using, pattern in warnings do
                    Assert.That(evidenceCodes.Contains code, sprintf "%s: %s is not an evidence code" v.Id code)
                    let uses = v.Fileset |> List.filter (fun (_, m) -> m = pattern) |> List.map (fst >> ScanDrive.roleName)
                    CollectionAssert.Contains(uses, using, sprintf "%s: no %s '%s' in the fileset" v.Id using pattern)
                    if code = "XP114" then Assert.AreEqual("include", using, v.Id)
                    if code = "XP120" then
                        Assert.AreEqual("exclude", using, v.Id)
                        Assert.That(pattern.StartsWith "/" || pattern.StartsWith "<root>", v.Id + ": XP120 needs an absolute exclude")
                    if code = "XP113" then StringAssert.EndsWith("/**", pattern, v.Id)

    [<Test>]
    member _.``SPEC? notes and 3.x notes``() =
        for v in scanVectors do
            Assert.That(v.Note <> "", v.Id + ": a note")
            Assert.That(v.Section <> "", v.Id + ": a section")

    [<Test>]
    member _.``coverage: modes, needs, codes, log notes, roles``() =
        for os in [ Os.Unix; Os.Windows; Os.Any ] do
            Assert.That(scanVectors |> List.exists (fun v -> v.Os = os), sprintf "%A" os)
        let needs = (trees |> List.collect (fun t -> t.Needs)) @ (scanVectors |> List.collect (fun v -> v.Needs)) |> set
        for n in [ Need.Symlinks; Need.CaseSensitiveVolume; Need.CaseInsensitiveVolume; Need.UnixPermissions; Need.NormalizingVolume; Need.NonNormalizingVolume ] do
            Assert.That(needs.Contains n, sprintf "%A" n)
        let lists = scanVectors |> List.choose (fun v -> match v.Expect with Lists (f, w, l) -> Some (f, w, l) | Raises _ -> None)
        let codes = lists |> List.collect (fun (_, w, _) -> w |> List.map (fun (c, _, _) -> c)) |> set
        for c in evidenceCodes do Assert.That(codes.Contains c, c)
        let logs = lists |> List.collect (fun (_, _, l) -> l)
        Assert.That(logs |> List.exists (function LinkSkipped _ -> true | _ -> false), "LinkSkipped")
        Assert.That(logs |> List.exists (function Unreadable _ -> true | _ -> false), "Unreadable")
        Assert.That(scanVectors |> List.exists (fun v -> v.Base = Missing), "a missing base")
        Assert.That(scanVectors |> List.exists (fun v -> match v.Expect with Raises _ -> true | _ -> false), "Raises")
        Assert.That(scanVectors |> List.exists (fun v -> v.Fileset |> List.exists (fun (r, _) -> r = MaskRole.Exclude)), "excludes")
