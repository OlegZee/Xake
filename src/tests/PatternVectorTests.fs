namespace Tests

open System
open NUnit.Framework

open PatternVectors

// The module under test (spec 8.2). Opened after PatternVectors so that `unparse`, `escape`, ...
// name the module's functions, not the vector tables (those are written `PatternVectors.unparse`).
open Xake.PatternAst

/// The parse-time warning parts of a mask for a role: `Mask.warnings` (D34, implemented form).
module Adapter =
    let maskIssues (role: MaskRole) (pattern: string) : Issue list = Mask.warnings role pattern

/// The textual rendering of a `Token list` described at the top of PatternVectors.fs, and its
/// reader (used only to check that the expected shapes in the tables are well formed).
module Shape =

    let private classSpecials = set [ '\\'; ']'; '-'; '^'; '!'; ' ' ]

    let private renderClassChar (c: char) =
        if classSpecials.Contains c then "\\" + string c else string c

    let private renderLit (s: string) =
        "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'"

    let rec render (tokens: Token list) : string =
        match tokens with
        | [] -> "<>"
        | _ -> tokens |> List.map renderToken |> String.concat " "

    and renderToken (token: Token) : string =
        match token with
        | Lit s -> renderLit s
        | Sep -> "/"
        | Star -> "*"
        | AnyChar -> "?"
        | Globstar -> "**"
        | DoubleStar -> "**~"
        | Class (negated, items) ->
            let item = function
                | One c -> renderClassChar c
                | Range (a, b) -> renderClassChar a + "-" + renderClassChar b
            "[" + (if negated then "^" else "") + (items |> List.map item |> String.concat " ") + "]"
        | Alt alts -> "{" + (alts |> List.map render |> String.concat " , ") + "}"
        | Group (name, body) -> "(" + name + ": " + render body + ")"

    /// Reads a shape back into tokens; fails with a message naming the position on malformed input.
    let read (shape: string) : Token list =
        let mutable i = 0
        let fail what = failwithf "shape '%s' at %d: %s" shape i what
        let peek () = if i < shape.Length then Some shape.[i] else None
        let at (s: string) = i + s.Length <= shape.Length && shape.Substring(i, s.Length) = s
        let expect (s: string) = if at s then i <- i + s.Length else fail (sprintf "expected '%s'" s)
        let escapedChar () =
            match peek () with
            | Some '\\' ->
                i <- i + 1
                match peek () with
                | Some c -> i <- i + 1; c
                | None -> fail "dangling '\\'"
            | Some c -> i <- i + 1; c
            | None -> fail "unexpected end"
        let readLit () =
            expect "'"
            let sb = Text.StringBuilder()
            while peek () <> Some '\'' do
                if peek () = None then fail "unclosed literal"
                sb.Append(escapedChar ()) |> ignore
            expect "'"
            Lit (sb.ToString())
        let readClass () =
            expect "["
            let negated = if at "^" then (i <- i + 1; true) else false
            let items = ResizeArray<ClassItem>()
            while peek () <> Some ']' do
                match peek () with
                | None -> fail "unclosed class"
                | Some ' ' -> i <- i + 1
                | Some _ ->
                    let a = escapedChar ()
                    if at "-" then
                        i <- i + 1
                        items.Add(Range (a, escapedChar ()))
                    else items.Add(One a)
            expect "]"
            Class (negated, List.ofSeq items)
        let rec readSeq () : Token list =
            let tokens = ResizeArray<Token>()
            let mutable stop = false
            while not stop do
                let mutable j = i
                while j < shape.Length && shape.[j] = ' ' do j <- j + 1
                match (if j < shape.Length then Some shape.[j] else None) with
                | None | Some ',' | Some '}' | Some ')' -> stop <- true
                | _ -> i <- j
                if not stop then
                    match peek () with
                    | None -> stop <- true
                    | Some '<' -> expect "<>"; stop <- true
                    | Some '\'' -> tokens.Add(readLit ())
                    | Some '/' -> i <- i + 1; tokens.Add Sep
                    | Some '*' when at "**~" -> i <- i + 3; tokens.Add DoubleStar
                    | Some '*' when at "**" -> i <- i + 2; tokens.Add Globstar
                    | Some '*' -> i <- i + 1; tokens.Add Star
                    | Some '?' -> i <- i + 1; tokens.Add AnyChar
                    | Some '[' -> tokens.Add(readClass ())
                    | Some '{' ->
                        i <- i + 1
                        let alts = ResizeArray<Token list>([ readSeq () ])
                        while at " , " do
                            i <- i + 3
                            alts.Add(readSeq ())
                        expect "}"
                        tokens.Add(Alt (List.ofSeq alts))
                    | Some '(' ->
                        i <- i + 1
                        let colon = shape.IndexOf(':', i)
                        if colon < 0 then fail "group without ':'"
                        let name = shape.Substring(i, colon - i)
                        i <- colon
                        expect ": "
                        let body = readSeq ()
                        expect ")"
                        tokens.Add(Group (name, body))
                    | Some c -> fail (sprintf "unexpected '%c'" c)
            List.ofSeq tokens
        let tokens = readSeq ()
        if i <> shape.Length then fail "trailing text"
        tokens

module private Drive =
    let context = function
        | Ctx.Rule -> PatternContext.Rule
        | Ctx.Mask -> PatternContext.Mask

    let tryParse ctx (text: string) =
        match ctx with
        | Ctx.Rule -> Rule.tryParse text
        | Ctx.Mask -> Mask.tryParse text

    let parse ctx (text: string) =
        match ctx with
        | Ctx.Rule -> Rule.parse text
        | Ctx.Mask -> Mask.parse text

    /// the parse-time warning parts for the vector's use (7.3)
    let issues ctx role (text: string) : Issue list =
        match ctx, role with
        | Ctx.Rule, _ ->
            match Rule.tryParse text with
            | Ok (_, issues) -> issues
            | Error e -> failwithf "unexpected error %O" e
        | Ctx.Mask, Role.Exclude -> Adapter.maskIssues MaskRole.Exclude text
        | Ctx.Mask, _ -> Adapter.maskIssues MaskRole.Include text

    let codes (issues: Issue list) = issues |> List.map (fun i -> i.Code) |> List.distinct |> List.sort

    let cases (vectors: 'v list) (id: 'v -> string) =
        vectors |> List.map (fun v -> TestCaseData(box v).SetName(sprintf "{m}(%s)" (id v)))

/// The parser-stage conformance vectors (spec sections 4, 5.8, 7, 8; brief D31, PR 2).
[<TestFixture>]
type ``Pattern module conformance: parser (PR 2)``() =

    static member ParseCases = Drive.cases PatternVectors.parse (fun v -> v.Id)
    static member MessageCases = Drive.cases PatternVectors.messages (fun v -> v.Id)
    static member IssueTextCases = Drive.cases PatternVectors.issueTexts (fun v -> v.Id)
    static member UnparseCases = Drive.cases PatternVectors.unparse (fun v -> v.Id)
    static member EscapeCases = Drive.cases PatternVectors.escape (fun v -> v.Id)
    static member EscapePathCases = Drive.cases PatternVectors.escapePath (fun v -> v.Id)
    static member GroupNamesCases = Drive.cases PatternVectors.groupNames (fun v -> v.Id)
    static member WildcardsCases = Drive.cases PatternVectors.wildcards (fun v -> v.Id)
    static member SpecificityCases = Drive.cases PatternVectors.specificity (fun v -> v.Id)
    static member ComparisonCases = Drive.cases PatternVectors.specificityComparisons (fun v -> v.Id)

    [<TestCaseSource("ParseCases")>]
    member _.parse(v: ParseVector) =
        let label = sprintf "%s %A '%s' (%s)" v.Id v.Context v.Input v.Note
        match v.Expect, Drive.tryParse v.Context v.Input with
        | Parses shape, Ok (tokens, _) ->
            Assert.AreEqual(shape, Shape.render tokens, label)
            Assert.AreEqual(shape, Shape.render (Drive.parse v.Context v.Input), label + " (parse)")
            Assert.AreEqual(List.sort v.Warnings, Drive.codes (Drive.issues v.Context v.Role v.Input), label + " (warnings)")
        | Fails (code, at), Error e ->
            Assert.AreEqual(code, e.Code, label)
            Assert.AreEqual(v.Input, e.Pattern, label)
            Assert.AreEqual(Drive.context v.Context, e.Context, label)
            match at with
            | At n -> Assert.AreEqual(Some n, e.Position, label)
            | NoPosition -> Assert.AreEqual(None, e.Position, label)
            | AnyPosition -> ()
            let message =
                try Drive.parse v.Context v.Input |> ignore; None
                with PatternException m -> Some m
            Assert.AreEqual(Some (e.ToString()), message, label + " (PatternException)")
            StringAssert.StartsWith(sprintf "Invalid pattern '%s' (" v.Input, e.ToString(), label)
            StringAssert.EndsWith(sprintf "[%s]" code, e.ToString(), label)
        | Parses shape, Error e -> Assert.Fail(sprintf "%s: expected %s, got %O" label shape e)
        | Fails (code, _), Ok (tokens, _) -> Assert.Fail(sprintf "%s: expected %s, got %s" label code (Shape.render tokens))

    [<TestCaseSource("MessageCases")>]
    member _.message(v: MessageVector) =
        match Drive.tryParse v.Context v.Input with
        | Error e -> Assert.AreEqual(v.Message, e.ToString(), v.Id)
        | Ok (tokens, _) -> Assert.Fail(sprintf "%s: expected an error, got %s" v.Id (Shape.render tokens))
        let message =
            try Drive.parse v.Context v.Input |> ignore; None
            with PatternException m -> Some m
        Assert.AreEqual(Some v.Message, message, v.Id + " (PatternException)")

    [<TestCaseSource("IssueTextCases")>]
    member _.``issue text``(v: IssueTextVector) =
        let parts = Drive.issues v.Context v.Role v.Input |> List.filter (fun i -> i.Code = v.Code)
        Assert.AreEqual([ v.Text ], parts |> List.map (fun i -> i.Text), v.Id)

    [<TestCaseSource("UnparseCases")>]
    member _.unparse(v: UnparseVector) =
        let ctx, tokens =
            match v.Input with
            | FromShape shape -> Ctx.Rule, Shape.read shape
            | FromText (ctx, text) -> ctx, Drive.parse ctx text
        let text = unparse tokens
        match v.Expect with
        | Text expected -> Assert.AreEqual(expected, text, v.Id)
        | RoundTrip -> ()
        // the inverse of parse: the canonical text parses to the same tokens (a mask for a trailing Sep)
        let ctx = if List.tryLast tokens = Some Sep then Ctx.Mask else ctx
        Assert.AreEqual(Shape.render tokens, Shape.render (Drive.parse ctx text), v.Id + " (round trip)")

    [<TestCaseSource("EscapeCases")>]
    member _.escape(v: EscapeVector) =
        Assert.AreEqual(v.Expected, escape v.Input, v.Id)

    [<TestCaseSource("EscapePathCases")>]
    member _.escapePath(v: EscapeVector) =
        let mask = escapePath v.Input
        Assert.AreEqual(v.Expected, mask, v.Id)
        // 7.3: escapePath output never warns
        Assert.IsEmpty(Adapter.maskIssues MaskRole.Include mask, v.Id + " (no warning)")

    [<TestCaseSource("GroupNamesCases")>]
    member _.groupNames(v: GroupNamesVector) =
        Assert.AreEqual(v.Expected, groupNames (Rule.parse v.Input), v.Id)

    [<TestCaseSource("WildcardsCases")>]
    member _.wildcards(v: WildcardsVector) =
        let actual =
            wildcards (Rule.parse v.Input)
            |> List.map (fun (t, n) -> sprintf "%s %d" (Shape.renderToken t) n)
            |> String.concat "; "
        Assert.AreEqual(v.Expected, actual, v.Id)

    [<TestCaseSource("SpecificityCases")>]
    member _.specificity(v: SpecificityVector) =
        Assert.AreEqual(v.Expected, specificity (Rule.parse v.Input), v.Id)

    [<TestCaseSource("ComparisonCases")>]
    member _.``specificity order``(v: SpecificityComparison) =
        let first = specificity (Rule.parse v.First)
        let second = specificity (Rule.parse v.Second)
        match v.Order with
        | FirstWins -> Assert.That(compare first second > 0, sprintf "%s: %A should beat %A" v.Id first second)
        | Tie -> Assert.AreEqual(second, first, v.Id)

/// Checks of the vector tables themselves (not of the module): ids, shapes, positions, coverage.
/// These run now, so a malformed table fails before the parser exists.
[<TestFixture>]
type ``Pattern module conformance: vector tables``() =

    let allIds =
        [ yield! PatternVectors.parse |> List.map (fun v -> v.Id)
          yield! PatternVectors.messages |> List.map (fun v -> v.Id)
          yield! PatternVectors.issueTexts |> List.map (fun v -> v.Id)
          yield! PatternVectors.unparse |> List.map (fun v -> v.Id)
          yield! PatternVectors.escape |> List.map (fun v -> v.Id)
          yield! PatternVectors.escapePath |> List.map (fun v -> v.Id)
          yield! PatternVectors.groupNames |> List.map (fun v -> v.Id)
          yield! PatternVectors.wildcards |> List.map (fun v -> v.Id)
          yield! PatternVectors.specificity |> List.map (fun v -> v.Id)
          yield! PatternVectors.specificityComparisons |> List.map (fun v -> v.Id) ]

    [<Test>]
    member _.``ids are unique``() =
        let dups = allIds |> List.countBy id |> List.filter (fun (_, n) -> n > 1)
        Assert.IsEmpty(dups, sprintf "%A" dups)

    [<Test>]
    member _.``every expected shape is canonical``() =
        let shapes =
            [ for v in PatternVectors.parse do
                match v.Expect with
                | Parses s -> yield v.Id, s
                | Fails _ -> ()
              for v in PatternVectors.unparse do
                match v.Input with
                | FromShape s -> yield v.Id, s
                | FromText _ -> () ]
        for id, s in shapes do
            Assert.AreEqual(s, Shape.render (Shape.read s), id)

    [<Test>]
    member _.``error positions point at the offending character``() =
        let starts =
            dict [ "XP002", "["; "XP003", "()|"; "XP004", "{}]"; "XP005", "{"; "XP006", "("; "XP007", "("
                   "XP008", "("; "XP010", "["; "XP011", @"/\"; "XP012", "."; "XP015", "("; "XP017", @"\"
                   "XP019", "{" ]
        for v in PatternVectors.parse do
            match v.Expect with
            | Fails (code, At n) ->
                Assert.That(n >= 1 && n <= v.Input.Length, sprintf "%s: position %d outside '%s'" v.Id n v.Input)
                match starts.TryGetValue code with
                | true, allowed -> StringAssert.Contains(string v.Input.[n - 1], allowed, sprintf "%s: '%s' at %d" v.Id v.Input n)
                | _ -> ()
            | Fails (code, NoPosition) -> Assert.AreEqual("XP018", code, v.Id + ": only XP018 has no position")
            | _ -> ()

    [<Test>]
    member _.``rule vectors have no role, mask vectors have one``() =
        for v in PatternVectors.parse do
            match v.Context, v.Role with
            | Ctx.Rule, Role.NoRole | Ctx.Mask, (Role.Include | Role.Exclude) -> ()
            | _ -> Assert.Fail(sprintf "%s: %A with %A" v.Id v.Context v.Role)

    [<Test>]
    member _.``every 4.0 parse error and parse-time warning is covered``() =
        let errors =
            PatternVectors.parse
            |> List.choose (fun v -> match v.Expect with Fails (c, _) -> Some c | Parses _ -> None)
            |> set
        let warnings = PatternVectors.parse |> List.collect (fun v -> v.Warnings) |> set
        // XP009 is 5.0 only; XP013 (multi-output) and XP014 (format) are not raised by parsing
        for code in [ "XP002"; "XP003"; "XP004"; "XP005"; "XP006"; "XP007"; "XP008"; "XP010"; "XP011"
                      "XP012"; "XP015"; "XP016"; "XP017"; "XP018"; "XP019" ] do
            Assert.That(errors.Contains code, code)
        for code in [ "XP105"; "XP106"; "XP110"; "XP112"; "XP117" ] do
            Assert.That(warnings.Contains code, code)
        // warnings that are never parse-time must not appear in the parse tables
        for code in [ "XP104"; "XP113"; "XP114"; "XP119"; "XP120" ] do
            Assert.That(not (warnings.Contains code), code)
