namespace Tests

open System.Collections.Generic
open NUnit.Framework

open Xake

/// "The most specific rule wins": among the rules that match a target, the one with the most
/// literal text (then fewest `**`, then fewest/tightest wildcards) is chosen; ties fall back to
/// the last declared rule and log a warning.
[<TestFixture>]
type RuleSpecificityTests() =
    inherit XakeTestBase("rulespec")

    let noop = Recipe (fun (r, _) -> async { return r, () })
    let file (root: string) (p: string) = root </> p |> File.make |> FileTarget
    let patternOf = function
        | Some (FileRule (p, _), _, _) | Some (PhonyRule (p, _), _, _) -> p
        | other -> failwithf "expected a file/phony rule, got %A" other

    // --- the specificity metric itself --------------------------------------------------------

    [<Test>]
    member _.``specificity: more literals beats fewer``() =
        Assert.Greater(Path.specificity "out/(b:*)/obf/(fwk:*)/(asm:*).dll",
                       Path.specificity "out/(b:*)/(cfg:*)/(fwk:*)/(asm:*).dll")

    [<Test>]
    member _.``specificity: * is more specific than ** at equal literals``() =
        Assert.Greater(Path.specificity "a/*/x.dll", Path.specificity "a/**/x.dll")

    [<Test>]
    member _.``specificity: ? is more specific than * at equal literals``() =
        Assert.Greater(Path.specificity "a/?.dll", Path.specificity "a/*.dll")

    [<Test>]
    member _.``specificity: a named group scores like a bare wildcard``() =
        Assert.AreEqual(Path.specificity "*/x.dll", Path.specificity "(cfg:*)/x.dll")

    // --- selection through locateRule (MostSpecific is the default) ---------------------------

    [<Test>]
    member x.``the literal rule wins over a general one regardless of declaration order``() =
        let root = x.TestOptions.ProjectRoot
        // general declared AFTER the specific one: last-declared order would pick the general.
        let rules =
            Rules [ FileRule ("out/(b:*)/(cfg:*)/(fwk:*)/(asm:*).dll", noop)   // index 0 = last declared
                    FileRule ("out/(b:*)/obf/(fwk:*)/(asm:*).dll", noop) ]
        let target = file root "out/acme/obf/net472/A.B.dll"
        Assert.AreEqual("out/(b:*)/obf/(fwk:*)/(asm:*).dll",
                        ExecCore.locateRule rules root target |> patternOf)

    [<Test>]
    member x.``clean::b::cfg (two groups) wins for a two-part phony target``() =
        let rules =
            Rules [ PhonyRule ("clean::(b:*)::(cfg:*)", noop)
                    PhonyRule ("clean::(b:*)", noop) ]
        Assert.AreEqual("clean::(b:*)::(cfg:*)",
                        ExecCore.locateRule rules "" (PhonyAction "clean::acme::Release") |> patternOf)
        // a one-part target matches only the one-group rule
        Assert.AreEqual("clean::(b:*)",
                        ExecCore.locateRule rules "" (PhonyAction "clean::acme") |> patternOf)

    [<Test>]
    member x.``locateRule prefers a file rule over a condition rule``() =
        let root = x.TestOptions.ProjectRoot
        let rules =
            Rules [ FileRule ("src/(name:*).cs", noop)
                    FileConditionRule ((fun _ -> true), noop) ]
        match ExecCore.locateRule rules root (file root "src/a.cs") with
        | Some (FileRule (p, _), _, _) -> Assert.AreEqual("src/(name:*).cs", p)
        | other -> Assert.Fail(sprintf "expected the file rule, got %A" other)

    // --- end to end: the recipe that actually runs --------------------------------------------

    [<Test>]
    member x.``the specific phony rule runs even when declared first``() =
        let ran = ref ""
        do xake {x.TestOptions with Targets = ["build::special"]} {
            "build::special" => recipe { ran := "specific" }
            "build::(x:*)"   => recipe { ran := "general" }
        }
        Assert.AreEqual("specific", !ran)

    [<Test>]
    member x.``ruleMatch LastDeclared restores the legacy last-declared order``() =
        let ran = ref ""
        do xake {x.TestOptions with Targets = ["build::special"]} {
            ruleMatch LastDeclared
            "build::special" => recipe { ran := "specific" }
            "build::(x:*)"   => recipe { ran := "general" }
        }
        Assert.AreEqual("general", !ran)

    // --- ambiguity warning --------------------------------------------------------------------

    [<Test>]
    member x.``equal-specificity different patterns warn and pick last declared``() =
        let ran = ref ""
        let warnings = List<string>()
        do xake {x.TestOptions with Targets = ["pre::common"]
                                    CustomLogger = CustomLogger ((=) Level.Warning) warnings.Add
                                    ConLogLevel = Silent} {
            "(a:*)::common" => recipe { ran := "a" }
            "(b:*)::common" => recipe { ran := "b" }     // last declared -> wins the tie
        }
        Assert.AreEqual("b", !ran)
        Assert.AreEqual(1, warnings |> Seq.filter (fun w -> w.Contains "Ambiguous rules") |> Seq.length)

    [<Test>]
    member x.``identical patterns do not warn (the override idiom)``() =
        let ran = ref ""
        let warnings = List<string>()
        do xake {x.TestOptions with Targets = ["main"]
                                    CustomLogger = CustomLogger ((=) Level.Warning) warnings.Add
                                    ConLogLevel = Silent} {
            "main" => recipe { ran := "first" }
            "main" => recipe { ran := "second" }
        }
        Assert.AreEqual("second", !ran)
        Assert.AreEqual(0, warnings |> Seq.filter (fun w -> w.Contains "Ambiguous rules") |> Seq.length)

/// Rule patterns on the pattern module (Phase 4a, spec 7 and 8.3): parsed when the rule is
/// created, multi-output checks, the XP119 evidence warning, specificity per D5.
[<TestFixture>]
type RulePatternTests() =
    inherit XakeTestBase("rulepattern")

    let noop = Recipe (fun (r, _) -> async { return r, () })
    let file (root: string) (p: string) = root </> p |> File.make |> FileTarget
    let patternOf = function
        | Some (FileRule (p, _), _, _) | Some (PhonyRule (p, _), _, _) -> p
        | other -> failwithf "expected a file/phony rule, got %A" other
    let throwsCode (code: string) (f: unit -> obj) =
        let e = Assert.Throws<PatternAst.PatternException>(fun () -> f () |> ignore)
        StringAssert.Contains(code, e.Message)
        e.Message

    // the check sits in XakeScriptBuilder.Yield: a rule is validated when it enters a script
    let yielded (x: XakeTestBase) (r: ExecContext Rule) () = box (xake x.TestOptions { yield r })

    [<Test>]
    member x.``an invalid rule pattern fails when the rule enters the script``() =
        // section 3 item 18: a group name starting with a digit
        throwsCode "XP007" (yielded x ("src/(1:*).cs" ..> recipe { return () })) |> ignore
        // item 22: a trailing '/'
        throwsCode "XP011" (yielded x ("out/" ..> recipe { return () })) |> ignore
        // item 3: '|' outside a group, phony too
        throwsCode "XP003" (yielded x ("a|b" => recipe { return () })) |> ignore
        throwsCode "XP003" (yielded x (target "x/(y" { return () })) |> ignore
        throwsCode "XP003" (yielded x (command "cmd(1)" { return () })) |> ignore

    [<Test>]
    member x.``a rule built from the DU cases is checked when yielded``() =
        throwsCode "XP004" (yielded x (FileRule ("{x", noop))) |> ignore
        // the `rules` operation goes through the same check
        throwsCode "XP004" (fun () -> box (xake x.TestOptions { rules [ FileRule ("{x", noop) ] })) |> ignore

    [<Test>]
    member x.``a regex alternation in a group points to braces``() =
        // item 4 (C3): the hermetic signing pattern of 3.x
        let msg = throwsCode "XP003" (yielded x ("signed/(name:*).(ext:dll|exe|nupkg)" ..> recipe { return () }))
        StringAssert.Contains("(ext:{dll,exe,nupkg})", msg)
        // ... and the 4.0 spelling is accepted
        "signed/(name:*).(ext:{dll,exe,nupkg})" ..> recipe { return () } |> ignore

    [<Test>]
    member x.``multi-output rules check their named groups when they enter the script``() =
        // item 31: outputs with different named groups (XP013)
        throwsCode "XP013" (yielded x (["out/(cfg:*)/(name:*).dll"; "out/(cfg:*)/x.xml"] *..> recipe { return () })) |> ignore
        throwsCode "XP013" (yielded x (targets ["out/(cfg:*)/(name:*).dll"; "out/(name:*).xml"] { return () })) |> ignore
        // item 32: braces outside a group cannot be named from a match (XP014)
        throwsCode "XP014" (yielded x (["out/(name:*).{dll,pdb}"; "out/(name:*).xml"] *..> recipe { return () })) |> ignore
        // positional bare wildcards pair by position (unchanged)
        ["dd/*.dll"; "dd/*.xml"] *..> recipe { return () } |> ignore

    [<Test>]
    member x.``an invalid pattern in a rule built from the DU fails the build with its message``() =
        let root = x.TestOptions.ProjectRoot
        let rules = Rules [ FileRule ("src/(a:*)/(a:*).cs", noop) ]
        let e = Assert.Throws<XakeException>(fun () -> ExecCore.locateRule rules root (file root "src/x/y.cs") |> ignore)
        StringAssert.Contains("XP015", e.Message)

    [<Test>]
    member x.``a globstar rule loses to a single-segment rule for a root file``() =
        // D5 / spec example 9: '*.dll' (4, 0, -2) beats '**/*.dll' (4, -1, -2) for 'a.dll'
        let root = x.TestOptions.ProjectRoot
        let rules = Rules [ FileRule ("**/*.dll", noop); FileRule ("*.dll", noop) ]
        Assert.AreEqual("*.dll", ExecCore.locateRule rules root (file root "a.dll") |> patternOf)
        Assert.AreEqual("**/*.dll", ExecCore.locateRule rules root (file root "a/b.dll") |> patternOf)

    [<Test>]
    member x.``item 33: a '/' next to '**' counts no literal``() =
        // 3.x picked '**/x.dll' (5 literals with the '/') over '*/?.dll' (5); 4.0 counts '**/' 0
        let root = x.TestOptions.ProjectRoot
        let rules = Rules [ FileRule ("*/?.dll", noop); FileRule ("**/x.dll", noop) ]
        Assert.AreEqual("*/?.dll", ExecCore.locateRule rules root (file root "a/x.dll") |> patternOf)

    [<Test>]
    member x.``XP119 is logged once when a selected rule's '**/' took zero directories``() =
        let warnings = List<string>()
        let ran = ref 0
        do xake {x.TestOptions with Targets = ["xp119root.dll"; "sub/xp119b.dll"]
                                    CustomLogger = CustomLogger ((=) Level.Warning) warnings.Add
                                    ConLogLevel = Silent} {
            "**/xp119*.dll" ..> recipe { ran.Value <- ran.Value + 1 }
        }
        Assert.AreEqual(2, ran.Value)
        let xp119 = warnings |> Seq.filter (fun w -> w.Contains "[XP119]" && w.Contains "**/xp119*.dll") |> List.ofSeq
        Assert.AreEqual(1, xp119.Length, sprintf "%A" (List.ofSeq warnings))
        StringAssert.Contains("xp119root.dll", xp119.Head)
        StringAssert.Contains("*/**/", xp119.Head)

    [<Test>]
    member x.``a legacy rule spelling warns once at creation``() =
        let warnings = List<string>()
        // XP105: '[(]' is the literal '(' as in 3.x, '\(' is the 4.0 spelling
        do xake {x.TestOptions with Targets = ["(xp105.ss"]
                                    CustomLogger = CustomLogger ((=) Level.Warning) warnings.Add
                                    ConLogLevel = Silent} {
            "[(]xp105*.ss" ..> recipe { return () }
        }
        let lines = warnings |> Seq.filter (fun w -> w.Contains "'[(]xp105*.ss'") |> List.ofSeq
        Assert.AreEqual(1, lines.Length, sprintf "%A" (List.ofSeq warnings))
        StringAssert.Contains("XP105", lines.Head)
