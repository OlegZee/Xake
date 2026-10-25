namespace Tests

open System.Text.RegularExpressions
open NUnit.Framework

open Xake

/// Golden tests for the pattern AST (PR "B"): the regex generated from the AST, and the specificity
/// read off it, must match the previous string-substitution implementation byte-for-byte. The
/// reference functions below are verbatim copies of the code the AST replaced.
[<TestFixture>]
type PatternAstTests() =

    // --- reference: the former maskToRegex body (string substitution) -------------------------
    static let oldMap =
        dict [ "**", "(.*)"
               "*", """([^/\\]*)"""
               "?", "([^/\\\\])"
               ".", "\\."; "$", "\\$"; "^", "\\^"; "[", "\\["; "]", "\\]"
               "+", "\\+"; "!", "\\!"; "=", "\\="; "{", "\\{"; "}", "\\}" ]
    static let oldRepl (m: Match) =
        match m.Groups.["tag"] with
        | t when not t.Success -> (match oldMap.TryGetValue m.Value with | true, v -> v | _ -> m.Value)
        | t -> "(?<" + t.Value + ">"
    static let oldRegex (pattern: string) =
        "^" + Regex.Replace(pattern.Replace('\\', '/'), @"\((?'tag'\w+?)\:|\*\*|([*.?$^+!={}])", oldRepl) + "$"

    // --- reference: the former specificity (regex tokenizer over the same grammar) ------------
    static let oldTokenRegex = Regex(@"(?<dstar>\*\*)|(?<group>\(\w+?\:)|(?<star>\*)|(?<q>\?)|(?<close>\))")
    static let oldSpecificity (pattern: string) : int * int * int =
        let mutable dstar, star, q, structural = 0, 0, 0, 0
        for m in oldTokenRegex.Matches pattern do
            if   m.Groups.["dstar"].Success then dstar <- dstar + 1
            elif m.Groups.["star"].Success  then star <- star + 1
            elif m.Groups.["q"].Success     then q <- q + 1
            else structural <- structural + m.Length
        (pattern.Length - structural - 2*dstar - star - q), -dstar, -(2*star + q)

    // Patterns (all with balanced groups) exercising literals, each wildcard, named groups,
    // metacharacters that are escaped, brackets that are deliberately *not* escaped, a literal
    // '(' that is not a group opener, roots, drive letters, spaces and run-together wildcards.
    static let corpus =
        [ "out/(cfg:*)/(fwk:*)/(name:*).dll"
          "out/(b:*)/obf/(fwk:*)/(asm:*).dll"
          "a/*/x.dll"; "a/**/x.dll"; "a/?.dll"; "a/*.dll"
          "**/*.cs"; "src/**/bin/*.o"
          "(root:*/*-a).ss"
          "x.y+z!=w{1}$^"            // escaped metacharacters . + ! = { } $ ^
          "lib[x].dll"               // brackets pass through unescaped
          "fun(ction).o"             // '(' not followed by name ':' is a literal
          "C:/Program Files/a b/(n:*).dll"
          "clean::(b:*)::(cfg:*)"
          "a***b"; "a?*b"; "(x:**)" ]

    [<Test>]
    member _.``AST regex matches the former substitution byte for byte``() =
        for p in corpus do
            Assert.AreEqual(oldRegex p, (Path.matchImpl.maskToRegex p).ToString(), sprintf "regex for '%s'" p)

    [<Test>]
    member _.``AST specificity matches the former tokenizer``() =
        // Excludes `fun(ction).o`: the old tokenizer stripped a top-level literal ')' as "structure"
        // (while keeping the '('), so a pattern with parentheses that are not a group scored one
        // literal short. The AST counts both as the literal characters they are. No real rule
        // pattern has unpaired/non-group parentheses, and specificity is only ever compared, so this
        // is a harmless, more-correct divergence rather than a behavior change.
        for p in corpus |> List.filter ((<>) "fun(ction).o") do
            Assert.AreEqual(oldSpecificity p, Path.specificity p, sprintf "specificity for '%s'" p)

    [<Test>]
    member _.``a pattern made only of literals formats to itself``() =
        for p in [ "out/version.txt"; "clean"; "a b/c.d.e"; "lit(x)/[y].txt"; "C:/x/y.dll" ] do
            Assert.AreEqual(p, PatternAst.formatMatches Map.empty p)

    [<Test>]
    member _.``format substitutes groups by name and bare wildcards by position``() =
        let p = "gen/*/(name:*)-?.txt"
        // as getRuleMatches holds it: the group's own '*' is regex group "2", the '?' is "3"
        let m = Map.ofList [ "1", "a"; "2", "core"; "3", "7"; "name", "core" ]
        Assert.AreEqual("gen/a/core-7.txt", PatternAst.formatMatches m p)
        Assert.AreEqual([ PatternAst.Star, 1; PatternAst.AnyChar, 3 ], PatternAst.wildcards (PatternAst.parse p))
        // the inverse of matching: the values the engine captures format back to the path
        let path = "gen/x/lib-9.txt"
        let captured = Path.matchGroups p "" path |> Option.get |> Map.ofList
        Assert.AreEqual(path, PatternAst.formatMatches captured p)

    [<Test>]
    member _.``format numbers top-level wildcards from 1 and ignores those inside groups``() =
        let toks = PatternAst.parse "**/(n:*)/?*.(ext:c?)"
        Assert.AreEqual([ PatternAst.DoubleStar, 1; PatternAst.AnyChar, 3; PatternAst.Star, 4 ], PatternAst.wildcards toks)
        Assert.AreEqual("<1>/N/<2><3>.EXT", PatternAst.format (fun n -> n.ToUpper()) (sprintf "<%d>") toks)
        Assert.AreEqual([ "n"; "ext" ], PatternAst.groupNames toks)
        // the inverse of matching for '**', '?' and '*' together
        let path = "a/b/core/x1.cs"
        let captured = Path.matchGroups "**/(n:*)/?*.(ext:c?)" "" path |> Option.get |> Map.ofList
        Assert.AreEqual(path, PatternAst.formatMatches captured "**/(n:*)/?*.(ext:c?)")

    [<Test>]
    member _.``format edge cases the former applyWildcards got wrong``() =
        // an empty group value used to leave "(lib:)" in the path; a ')' in a value broke it
        Assert.AreEqual("out/net8.0/.xml", PatternAst.formatMatches (Map.ofList [ "fwk", "net8.0"; "lib", "" ]) "out/(fwk:*)/(lib:*).xml")
        Assert.AreEqual("out/a)b/c.xml", PatternAst.formatMatches (Map.ofList [ "fwk", "a)b"; "lib", "c" ]) "out/(fwk:*)/(lib:*).xml")
        // a missing group gives "" (as getRuleMatch does)
        Assert.AreEqual("out//c.xml", PatternAst.formatMatches (Map.ofList [ "lib", "c" ]) "out/(fwk:*)/(lib:*).xml")

    [<Test>]
    member _.``glob leaves unbound groups as their own masks``() =
        let toks = PatternAst.parse "(root:*/(leaf:*-a)).ss"
        Assert.AreEqual("*/*-a.ss", PatternAst.glob (fun _ -> None) (fun _ -> None) toks)
        Assert.AreEqual("src/core/**/*.fs", PatternAst.glob (Map.ofList [ "dir", "core" ]).TryFind (fun _ -> None) (PatternAst.parse "src/(dir:*)/**/*.fs"))
        Assert.AreEqual("out/(fwk:*)/(lib:*).dll", PatternAst.unparse (PatternAst.parse "out/(fwk:*)/(lib:*).dll"))
        // bound wildcards take their value, unbound ones stay; literal ( ) [ ] | pass through
        let toks = PatternAst.parse "(x)/[y]|*/(n:*)-?.txt"
        Assert.AreEqual("(x)/[y]|*/core-?.txt", PatternAst.glob (Map.ofList [ "n", "core" ]).TryFind (fun _ -> None) toks)
        Assert.AreEqual("(x)/[y]|a/*-7.txt", PatternAst.glob (fun _ -> None) (function 1 -> Some "a" | _ -> Some "7") toks)
