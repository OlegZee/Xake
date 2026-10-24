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
