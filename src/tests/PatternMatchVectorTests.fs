namespace Tests

open System
open NUnit.Framework

open PatternMatchVectors
open Xake.PatternAst

/// The matcher contract for PR 3 (spec 8.1-8.3, 9; brief D16, D18, D31). This module is the
/// shim the conformance tables drive; each body calls into the module (`Xake.PatternMatch` for
/// matching, `Xake.PatternAst` for generation):
///
/// - `matchRule windows root pattern target : RuleMatch option` -- the matcher behind
///   `Path.matchGroups pattern root target` (8.3: its public signature does not change; it becomes
///   an adapter that passes `not Env.isUnix` and returns `Captures` as a list). `root` is the
///   anchor (project root, `""` for phony); `target` the full target path. `Captures` holds every
///   named group, every positional number `"1"`, `"2"`, ... and `"0"` (5.6, C7). `Evidence` holds
///   the evidence warning codes of this match (XP119, 7.3); the engine reports them once per
///   pattern through `Warnings`. Raises `PatternException` for an invalid pattern.
/// - `matchMask windows baseDir mask path isDirectory : bool` -- the matcher side of an include
///   or an exclude, one path, no tree (the walker of PR 4 calls it per entry; `Path.matches`
///   becomes an adapter). A mask with a trailing `/` matches directories only, otherwise files.
/// - `format named positional tokens : string` -- public `PatternAst.format` (8.2); `positional i`
///   is asked for the i-th top-level bare wildcard (5.7). Raises `PatternException` (XP014) for a
///   class or braces outside a group.
/// - `formatMatches matches pattern : string` -- public `PatternAst.formatMatches` (8.2).
/// - `checkOutputs patterns : unit` -- the multi-output check made when a `*..>` / `targets` rule
///   is created (internal `checkSameGroups` of 8.2, plus XP014): raises `PatternException` whose
///   message names XP013 or XP014.
///
/// `windows` is the comparer of D11 made explicit (true: ordinal-ignore-case, and `\` in a path is
/// a separator; false: ordinal), so the vectors of both OSes run on every host.
module PatternMatcherUnderTest =

    type RuleMatch =
        { Captures: Map<string, string>
          Evidence: string list }

    let matchRule (windows: bool) (root: string) (pattern: string) (target: string) : RuleMatch option =
        Xake.PatternMatch.matchRule windows root pattern target
        |> Option.map (fun r -> { Captures = r.Captures; Evidence = r.Evidence })

    let matchMask (windows: bool) (baseDir: string) (mask: string) (path: string) (isDirectory: bool) : bool =
        Xake.PatternMatch.matchMask windows baseDir mask path isDirectory

    let format (named: string -> string) (positional: int -> string) (tokens: Token list) : string =
        Xake.PatternAst.format named positional tokens

    let formatMatches (matches: Map<string, string>) (pattern: string) : string =
        Xake.PatternAst.formatMatches matches pattern

    let checkOutputs (patterns: string list) : unit =
        Xake.PatternAst.checkOutputs patterns

module private MatchDrive =
    let modes = function
        | Os.Unix -> [ false ]
        | Os.Windows -> [ true ]
        | Os.Any -> [ false; true ]

    let modeName windows = if windows then "Windows" else "Unix"

    let isAbsolute (p: string) =
        p.StartsWith "/" || p.StartsWith "\\" || (p.Length >= 2 && p.[1] = ':')

    /// A path relative to its anchor joined with '/'; an absolute path or an empty anchor as is.
    let join (anchor: string) (p: string) =
        if anchor = "" || isAbsolute p then p else anchor.TrimEnd('/', '\\') + "/" + p

    /// The "0" capture: the full path, '\' read as '/' in Windows mode.
    let zero windows (full: string) = if windows then full.Replace('\\', '/') else full

    let lookup (pairs: ('k * string) list) (key: 'k) =
        pairs |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd |> Option.defaultValue ""

    /// Runs `f`; returns the PatternException message it raised, if any (other exceptions pass).
    let patternError (f: unit -> unit) =
        try f (); None with PatternException m -> Some m

    let cases (vectors: 'v list) (id: 'v -> string) =
        vectors |> List.map (fun v -> TestCaseData(box v).SetName(sprintf "{m}(%s)" (id v)))

    // ---- reading the AST (table checks) ------------------------------------------------------------

    /// every token at any depth (inside braces and groups)
    let rec allTokens (tokens: Token list) : Token list =
        tokens |> List.collect (fun t ->
            match t with
            | Alt alts -> t :: (alts |> List.collect allTokens)
            | Group (_, body) -> t :: allTokens body
            | _ -> [ t ])

    let wildcardCount (tokens: Token list) =
        allTokens tokens |> List.filter (function Star | AnyChar | Globstar | DoubleStar -> true | _ -> false) |> List.length

    /// a class or braces outside any named group (XP014, 5.7)
    let rec unformattable (tokens: Token list) =
        tokens |> List.exists (function
            | Class _ | Alt _ -> true
            | _ -> false)

module M = PatternMatcherUnderTest

/// The matcher-stage conformance vectors (spec 5, 7.3 XP119, 8, 9, 11; brief D31, PR 3).
[<TestFixture>]
type ``Pattern module conformance: matcher (PR 3)``() =

    static member RuleCases = MatchDrive.cases ruleMatches (fun v -> v.Id)
    static member MaskCases = MatchDrive.cases maskMatches (fun v -> v.Id)
    static member FormatCases = MatchDrive.cases formats (fun v -> v.Id)
    static member FormatMatchesCases = MatchDrive.cases formatMatchesV (fun v -> v.Id)
    static member MultiCases = MatchDrive.cases multiOutputs (fun v -> v.Id)
    static member SelectionCases = MatchDrive.cases selections (fun v -> v.Id)

    [<TestCaseSource("RuleCases")>]
    member _.``rule match``(v: RuleVector) =
        for windows in MatchDrive.modes v.Os do
            let full = MatchDrive.join v.Root v.Target
            let label = sprintf "%s [%s] '%s' root '%s' vs '%s' (%s)" v.Id (MatchDrive.modeName windows) v.Pattern v.Root full v.Note
            match v.Expect, M.matchRule windows v.Root v.Pattern full with
            | NoMatch, None -> ()
            | Match captures, Some r ->
                let expected = ("0", MatchDrive.zero windows full) :: captures |> Map.ofList
                Assert.AreEqual(expected, r.Captures, label)
                Assert.AreEqual(List.sort v.Warnings, List.sort r.Evidence, label + " (evidence)")
            | NoMatch, Some r -> Assert.Fail(sprintf "%s: expected no match, got %A" label r)
            | Match captures, None -> Assert.Fail(sprintf "%s: expected %A, got no match" label captures)

    [<TestCaseSource("MaskCases")>]
    member _.``mask match``(v: MaskVector) =
        for windows in MatchDrive.modes v.Os do
            let full = MatchDrive.join v.Base v.Path
            let label = sprintf "%s [%s] '%s' base '%s' vs '%s' (%s) (%s)" v.Id (MatchDrive.modeName windows) v.Mask v.Base full (if v.IsDirectory then "directory" else "file") v.Note
            Assert.AreEqual(v.Expect, M.matchMask windows v.Base v.Mask full v.IsDirectory, label)

    [<TestCaseSource("FormatCases")>]
    member _.format(v: FormatVector) =
        let tokens = Rule.parse v.Pattern
        let run () = M.format (MatchDrive.lookup v.Named) (MatchDrive.lookup v.Positional) tokens
        match v.Expect with
        | Formats expected -> Assert.AreEqual(expected, run (), v.Id)
        | FormatFails code ->
            match MatchDrive.patternError (run >> ignore) with
            | Some message -> StringAssert.Contains(sprintf "[%s]" code, message, v.Id)
            | None -> Assert.Fail(sprintf "%s: expected %s" v.Id code)

    [<TestCaseSource("FormatMatchesCases")>]
    member _.formatMatches(v: FormatMatchesVector) =
        let run () = M.formatMatches (Map.ofList v.Matches) v.Pattern
        match v.Expect with
        | Formats expected -> Assert.AreEqual(expected, run (), v.Id)
        | FormatFails code ->
            match MatchDrive.patternError (run >> ignore) with
            | Some message -> StringAssert.Contains(sprintf "[%s]" code, message, v.Id)
            | None -> Assert.Fail(sprintf "%s: expected %s" v.Id code)

    [<TestCaseSource("MultiCases")>]
    member _.``multi-output rule``(v: MultiOutputVector) =
        match v.Expect with
        | OutputsFail code ->
            match MatchDrive.patternError (fun () -> M.checkOutputs v.Patterns) with
            | Some message -> StringAssert.Contains(code, message, v.Id)
            | None -> Assert.Fail(sprintf "%s: expected %s when the rule is created" v.Id code)
        | _ ->
            M.checkOutputs v.Patterns
            for windows in [ false; true ] do
                let full = MatchDrive.join v.Root v.Target
                let label = sprintf "%s [%s]" v.Id (MatchDrive.modeName windows)
                let matched = v.Patterns |> List.tryPick (fun p -> M.matchRule windows v.Root p full)
                match v.Expect, matched with
                | NoOutputs, None -> ()
                | Outputs expected, Some r ->
                    Assert.AreEqual(expected, v.Patterns |> List.map (M.formatMatches r.Captures), label)
                | _, other -> Assert.Fail(sprintf "%s: expected %A, got %A" label v.Expect other)

    [<TestCaseSource("SelectionCases")>]
    member _.``rule selection``(v: SelectionVector) =
        for windows in MatchDrive.modes v.Os do
            let full = MatchDrive.join v.Root v.Target
            let winner =
                v.Rules
                |> List.mapi (fun i p -> i, p)
                |> List.filter (fun (_, p) -> (M.matchRule windows v.Root p full).IsSome)
                // most specific first; within equal specificity the last declared first
                |> List.sortWith (fun (i1, p1) (i2, p2) ->
                    match compare (specificity (Rule.parse p2)) (specificity (Rule.parse p1)) with
                    | 0 -> compare i2 i1
                    | c -> c)
                |> List.tryHead
                |> Option.map snd
            Assert.AreEqual(v.Winner, winner, sprintf "%s [%s] (%s)" v.Id (MatchDrive.modeName windows) v.Note)

/// Checks of the matcher tables themselves (not of the matcher): ids, that every pattern parses
/// with the PR 2 parser, capture keys against the AST, and coverage. These run now.
[<TestFixture>]
type ``Pattern module conformance: matcher vector tables``() =

    let tables =
        [ "RM-", ruleMatches |> List.map (fun v -> v.Id)
          "MM-", maskMatches |> List.map (fun v -> v.Id)
          "FO-", formats |> List.map (fun v -> v.Id)
          "FM-", formatMatchesV |> List.map (fun v -> v.Id)
          "MO-", multiOutputs |> List.map (fun v -> v.Id)
          "RS-", selections |> List.map (fun v -> v.Id) ]

    let parsesAsRule id (pattern: string) =
        match Rule.tryParse pattern with
        | Ok (tokens, _) -> tokens
        | Error e -> failwithf "%s: rule pattern '%s' does not parse: %O" id pattern e

    [<Test>]
    member _.``ids are unique and carry their table's prefix``() =
        let all = tables |> List.collect snd
        let dups = all |> List.countBy id |> List.filter (fun (_, n) -> n > 1)
        Assert.IsEmpty(dups, sprintf "%A" dups)
        for prefix, ids in tables do
            for i in ids do StringAssert.StartsWith(prefix, i)
        TestContext.WriteLine(tables |> List.map (fun (p, ids) -> sprintf "%s%d" p ids.Length) |> String.concat " ")

    [<Test>]
    member _.``every rule pattern parses``() =
        for v in ruleMatches do parsesAsRule v.Id v.Pattern |> ignore
        for v in formats do parsesAsRule v.Id v.Pattern |> ignore
        for v in multiOutputs do for p in v.Patterns do parsesAsRule v.Id p |> ignore
        for v in selections do for p in v.Rules do parsesAsRule v.Id p |> ignore
        for v in formatMatchesV do
            match v.Expect, Rule.tryParse v.Pattern with
            | FormatFails code, Error e when code <> "XP014" -> Assert.AreEqual(code, e.Code, v.Id)
            | FormatFails code, Error e -> Assert.Fail(sprintf "%s: expected %s, the pattern fails to parse: %O" v.Id code e)
            | _, Error e -> Assert.Fail(sprintf "%s: '%s' does not parse: %O" v.Id v.Pattern e)
            | FormatFails code, Ok _ -> Assert.AreEqual("XP014", code, v.Id + ": a parse-level code needs an unparsable pattern")
            | Formats _, Ok _ -> ()

    [<Test>]
    member _.``every mask parses``() =
        for v in maskMatches do
            match Mask.tryParse v.Mask with
            | Ok _ -> ()
            | Error e -> Assert.Fail(sprintf "%s: mask '%s' does not parse: %O" v.Id v.Mask e)

    [<Test>]
    member _.``rule captures name every group and every wildcard, never "0"``() =
        for v in ruleMatches do
            match v.Expect with
            | NoMatch -> Assert.IsEmpty(v.Warnings, v.Id + ": evidence warnings need a match")
            | Match captures ->
                let tokens = parsesAsRule v.Id v.Pattern
                let keys = captures |> List.map fst
                Assert.AreEqual(List.length keys, keys |> List.distinct |> List.length, v.Id + ": repeated key")
                let positional, named = keys |> List.partition (fun k -> k.Length > 0 && Char.IsDigit k.[0])
                Assert.AreEqual(Set.ofList (groupNames tokens), Set.ofList named, v.Id + ": named keys = the group names")
                let expected = [ 1 .. MatchDrive.wildcardCount tokens ] |> List.map string
                Assert.AreEqual(Set.ofList expected, Set.ofList positional, v.Id + ": positional keys = 1..wildcards (5.6), no \"0\"")

    [<Test>]
    member _.``XP119 only where a globstar can take zero directories``() =
        for v in ruleMatches do
            for w in v.Warnings do Assert.AreEqual("XP119", w, v.Id + ": only evidence codes belong here")
            if not (List.isEmpty v.Warnings) then
                let tokens = parsesAsRule v.Id v.Pattern
                Assert.That(MatchDrive.allTokens tokens |> List.contains Globstar, v.Id + ": XP119 without a globstar")

    [<Test>]
    member _.``XP014 exactly where a class or braces lie outside a group``() =
        for v in formats do
            let tokens = parsesAsRule v.Id v.Pattern
            Assert.AreEqual(MatchDrive.unformattable tokens, (v.Expect = FormatFails "XP014"), v.Id)
            let top = wildcards tokens |> List.length
            for i, _ in v.Positional do
                Assert.That(i >= 1 && i <= top, sprintf "%s: positional %d outside 1..%d (top-level wildcards)" v.Id i top)
        for v in formatMatchesV do
            match Rule.tryParse v.Pattern with
            | Ok (tokens, _) -> Assert.AreEqual(MatchDrive.unformattable tokens, (v.Expect = FormatFails "XP014"), v.Id)
            | Error _ -> ()
        for v in multiOutputs do
            let bad = v.Patterns |> List.exists (fun p -> MatchDrive.unformattable (parsesAsRule v.Id p))
            Assert.AreEqual(bad, (v.Expect = OutputsFail "XP014"), v.Id)

    [<Test>]
    member _.``a directory mask expects directories, a file mask files``() =
        for v in maskMatches do
            let dirMask = (Mask.parse v.Mask |> List.tryLast) = Some Sep
            if dirMask <> v.IsDirectory then
                Assert.IsFalse(v.Expect, sprintf "%s: '%s' against a %s cannot match" v.Id v.Mask (if v.IsDirectory then "directory" else "file"))

    [<Test>]
    member _.``a backslash in a path is Windows mode or flagged SPEC?``() =
        let check id (os: Os) (path: string) (note: string) =
            if path.Contains "\\" && os <> Os.Windows then
                StringAssert.StartsWith("SPEC?", note, id + ": a '\\' in a path outside Windows mode")
        for v in ruleMatches do check v.Id v.Os v.Target v.Note
        for v in maskMatches do check v.Id v.Os v.Path v.Note

    [<Test>]
    member _.``selection winners are among the rules``() =
        for v in selections do
            match v.Winner with
            | Some w -> CollectionAssert.Contains(v.Rules, w, v.Id)
            | None -> ()

    [<Test>]
    member _.``every element is covered in rule and mask matching``() =
        let kinds (patterns: Token list list) =
            patterns
            |> List.collect MatchDrive.allTokens
            |> List.map (function
                | Lit _ -> "Lit" | Sep -> "Sep" | Star -> "Star" | AnyChar -> "AnyChar"
                | Globstar -> "Globstar" | DoubleStar -> "DoubleStar"
                | Class (false, _) -> "Class" | Class (true, _) -> "NegatedClass"
                | Alt _ -> "Alt" | Group _ -> "Group")
            |> set
        let ruleKinds = kinds (ruleMatches |> List.map (fun v -> Rule.parse v.Pattern))
        let maskKinds = kinds (maskMatches |> List.map (fun v -> Mask.parse v.Mask))
        for k in [ "Lit"; "Sep"; "Star"; "AnyChar"; "Globstar"; "DoubleStar"; "Class"; "NegatedClass"; "Alt"; "Group" ] do
            Assert.That(ruleKinds.Contains k, "rule: " + k)
            if k <> "Group" then Assert.That(maskKinds.Contains k, "mask: " + k)
        for os in [ Os.Unix; Os.Windows; Os.Any ] do
            Assert.That(ruleMatches |> List.exists (fun v -> v.Os = os), sprintf "rule: %A" os)
            Assert.That(maskMatches |> List.exists (fun v -> v.Os = os), sprintf "mask: %A" os)
        Assert.That(ruleMatches |> List.exists (fun v -> v.Warnings = [ "XP119" ]), "XP119")
