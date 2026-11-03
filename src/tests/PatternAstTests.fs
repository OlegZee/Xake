namespace Tests

open System.Text.RegularExpressions
open NUnit.Framework

open Xake

/// Golden test for the pattern AST (PR "B"): the specificity read off it must match the previous
/// regex tokenizer, except for the patterns 4.0 reads differently (pinned below). The reference
/// function is a verbatim copy of the code the AST replaced. (The regex golden test went with the
/// 3.x part matcher in Phase 4b.)
[<TestFixture>]
type PatternAstTests() =

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

    // Rewritten for 4.0 (Phase 4a): `Path.specificity` is `PatternAst.specificity` of the pattern
    // module (spec 5.8). Patterns whose 4.0 reading is unchanged score as the 3.x tokenizer did;
    // the others are pinned below with the spec section-3 item that changes them.
    static let specificityChanged =
        [ "a/**/x.dll",     (6, -1, 0)    // item 33 (D5): a '/' next to '**' counts 0 (3.x: 8)
          "**/*.cs",        (3, -1, -2)   // item 33 (3.x: 4)
          "src/**/bin/*.o", (9, -1, -2)   // item 33 (3.x: 11)
          "lib[x].dll",     (7, 0, -1)    // item 10: '[x]' is the class of 'x' (XP105), weight 1 (3.x: 10 literals)
          "a***b",          (2, 0, -4) ]  // item 13: segment-local '**' is '*' (XP112) (3.x: (2, -1, -2))
    // item 7: '{1}' without a comma is XP005; item 3: '(' outside a group is XP003
    static let specificityErrors = [ "x.y+z!=w{1}$^", "XP005"; "fun(ction).o", "XP003" ]

    [<Test>]
    member _.``AST specificity matches the former tokenizer``() =
        let changed = specificityChanged |> List.map fst
        let errors = specificityErrors |> List.map fst
        for p in corpus |> List.filter (fun p -> not (List.contains p changed || List.contains p errors)) do
            Assert.AreEqual(oldSpecificity p, Path.specificity p, sprintf "specificity for '%s'" p)
        for p, expected in specificityChanged do
            Assert.AreEqual(expected, Path.specificity p, sprintf "4.0 specificity for '%s'" p)
        for p, code in specificityErrors do
            let e = Assert.Throws<PatternAst.PatternException>(fun () -> Path.specificity p |> ignore)
            StringAssert.Contains(code, e.Message, p)

    // --- glob (spec 5.7), used by the typed layer's Glob ------------------------------------------

    [<Test>]
    member _.``glob: unbound groups become their bodies, bound values are escaped``() =
        let parse = PatternAst.Rule.parse
        Assert.AreEqual("*/*-a.ss", PatternAst.glob (fun _ -> None) (fun _ -> None) (parse "(root:*/(leaf:*-a)).ss"))
        Assert.AreEqual("src/core/**/*.fs", PatternAst.glob (Map.ofList [ "dir", "core" ]).TryFind (fun _ -> None) (parse "src/(dir:*)/**/*.fs"))
        // spec 11, example 16
        let lib v = PatternAst.glob (function "lib" -> Some v | _ -> None) (fun _ -> None) (parse "src/(lib:*)/(name:*).cs")
        Assert.AreEqual("src/core/*.cs", lib "core")
        Assert.AreEqual(@"src/a\(b/*.cs", lib "a(b")
        // bound wildcards take their value, unbound ones stay; literals keep their escapes
        let toks = parse @"\(x\)/\[y\]\|*/(n:*)-?.txt"
        Assert.AreEqual(@"\(x\)/\[y\]\|*/core-?.txt", PatternAst.glob (Map.ofList [ "n", "core" ]).TryFind (fun _ -> None) toks)
        Assert.AreEqual(@"\(x\)/\[y\]\|a/*-\{7\}.txt", PatternAst.glob (fun _ -> None) (function 1 -> Some "a" | _ -> Some "{7}") toks)

    [<Test>]
    member _.``glob: positional numbers count top-level wildcards only; classes and braces stay``() =
        let parse = PatternAst.Rule.parse
        // the group's own '*' is not a positional slot: 1 is '**', 2 the last '*'
        let toks = parse "gen/(dir:*)/**/*.txt"
        Assert.AreEqual("gen/*/a/b/file.txt", PatternAst.glob (fun _ -> None) (function 1 -> Some "a/b" | 2 -> Some "file" | _ -> None) toks)
        // an empty globstar slot drops one '/', as format does
        Assert.AreEqual("gen/d/file.txt", PatternAst.glob (fun _ -> Some "d") (function 1 -> Some "" | _ -> Some "file") toks)
        Assert.AreEqual("out/x/*.{dll,pdb}", PatternAst.glob (fun _ -> Some "x") (fun _ -> None) (parse "out/(n:*)/*.{dll,pdb}"))
        Assert.AreEqual("[ab]-core.txt", PatternAst.glob (fun _ -> Some "core") (fun _ -> None) (parse "[ab]-(n:*).txt"))

    [<Test>]
    member _.``a PatternException prints as its message alone``() =
        let e = PatternAst.PatternException "Invalid pattern 'x'"
        Assert.AreEqual("Invalid pattern 'x'", e.ToString())
        Assert.AreEqual("Invalid pattern 'x'", e.Message)
