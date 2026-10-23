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
