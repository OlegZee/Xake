namespace Tests

open System.IO
open System.Collections.Concurrent
open NUnit.Framework

open Xake
open Xake.Tasks

/// `let! m = matched` in every recipe (docs/features/match-operator/brief.md): the groups of the
/// rule being run as a `RuleMatch`, with a strict indexer.
[<TestFixture>]
type ``Matched in a recipe``() =
    inherit XakeTestBase("matched")

    /// Runs a script with these rules and targets; the error message when it failed.
    member private x.Build (targets: string list) (rules': ExecContext Rule list) : string option =
        for d in [ "out"; "obj" ] do try Directory.Delete(d, true) with _ -> ()
        try
            xake { x.TestOptions with Targets = targets; ThrowOnError = true } {
                rules rules'
            }
            None
        with e -> Some e.Message

    [<Test>]
    member x.``a file rule reads its groups by name``() =
        let failed =
            x.Build [ "out/dbg/core.txt" ] [
                "out/(cfg:*)/(n:*).txt" ..> recipe {
                    let! m = matched
                    do! writeText (m["cfg"] + " " + m.["n"])
                }
            ]
        Assert.That(failed, Is.EqualTo None)
        Assert.That(File.ReadAllText "out/dbg/core.txt", Is.EqualTo "dbg core")

    [<Test>]
    member x.``matched comes after a rule header``() =
        let failed =
            x.Build [ "out/core.txt" ] [
                "obj/(n:*).rsp" ..> recipe { do! writeText "rsp" }
                "out/(n:*).txt" ..> recipe {
                    needs "obj/(n:*).rsp"
                    let! m = matched
                    do! writeText (File.ReadAllText ("obj/" + m["n"] + ".rsp") + " " + m["n"])
                }
            ]
        Assert.That(failed, Is.EqualTo None)
        Assert.That(File.ReadAllText "out/core.txt", Is.EqualTo "rsp core")

    [<Test>]
    member x.``target and targets builders take matched``() =
        let failed =
            x.Build [ "out/a.one"; "out/b.two" ] [
                target "out/(n:*).one" {
                    let! m = matched
                    do! writeText ("one " + m["n"])
                }
                targets [ "out/(n:*).two"; "out/(n:*).two.log" ] {
                    noContentCutoff
                    let! m = matched
                    do! writeText ("two " + m["n"])
                    File.WriteAllText("out/" + m["n"] + ".two.log", "log")
                }
            ]
        Assert.That(failed, Is.EqualTo None)
        Assert.That(File.ReadAllText "out/a.one", Is.EqualTo "one a")
        Assert.That(File.ReadAllText "out/b.two", Is.EqualTo "two b")

    [<Test>]
    member x.``a phony rule reads the groups of its name``() =
        let seen = ConcurrentQueue<string>()
        let failed =
            x.Build [ "deploy-staging" ] [
                "deploy-(env:*)" => recipe {
                    let! m = matched
                    seen.Enqueue m["env"]
                }
            ]
        Assert.That(failed, Is.EqualTo None)
        Assert.That(List.ofSeq seen, Is.EqualTo [ "staging" ])

    [<Test>]
    member x.``an unknown group fails the task and names the groups``() =
        let failed =
            x.Build [ "out/dbg/core.txt" ] [
                "out/(cfg:*)/(n:*).txt" ..> recipe {
                    let! m = matched
                    do! writeText m["name"]
                }
            ]
        Assert.That(failed, Is.Not.EqualTo None)
        Assert.That(failed.Value, Does.Contain "no group 'name' in the rule match (groups: cfg, n)")

    [<Test>]
    member x.``m?n reads a group, also inside an interpolated string``() =
        let failed =
            x.Build [ "out/dbg/core.txt" ] [
                "out/(cfg:*)/(n:*).txt" ..> recipe {
                    let! m = matched
                    do! writeText $"{m?cfg} {m?n}"
                }
            ]
        Assert.That(failed, Is.EqualTo None)
        Assert.That(File.ReadAllText "out/dbg/core.txt", Is.EqualTo "dbg core")

    [<Test>]
    member x.``m?name is as strict as m["name"]``() =
        let failed =
            x.Build [ "out/dbg/core.txt" ] [
                "out/(cfg:*)/(n:*).txt" ..> recipe {
                    let! m = matched
                    do! writeText m?name
                }
            ]
        Assert.That(failed, Is.Not.EqualTo None)
        Assert.That(failed.Value, Does.Contain "no group 'name' in the rule match (groups: cfg, n)")

    [<Test>]
    member x.``TryFind and Names``() =
        let seen = ConcurrentQueue<string>()
        let failed =
            x.Build [ "out/dbg/core.txt" ] [
                "out/(cfg:*)/(n:*).txt" ..> recipe {
                    let! m = matched
                    seen.Enqueue (sprintf "%A %A %A" (m.TryFind "cfg") (m.TryFind "name") m.Names)
                    do! writeText ""
                }
            ]
        Assert.That(failed, Is.EqualTo None)
        Assert.That(List.ofSeq seen, Is.EqualTo [ """Some "dbg" None ["cfg"; "n"]""" ])

    [<Test>]
    member x.``a rule with no groups has an empty match``() =
        let seen = ConcurrentQueue<string list>()
        let failed =
            x.Build [ "main" ] [
                "main" => recipe {
                    let! m = matched
                    seen.Enqueue m.Names
                }
            ]
        Assert.That(failed, Is.EqualTo None)
        Assert.That(List.ofSeq seen, Is.EqualTo [ List.empty<string> ])
