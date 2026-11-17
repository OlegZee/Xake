namespace Tests

open System.IO
open System.Collections.Concurrent
open NUnit.Framework

open Xake
open Xake.Tasks

/// `lock'`: the F# `lock`, under a name the tests' `lock` resources do not shadow.
[<AutoOpen>]
module internal HeaderTestUtil =
    let lock' (gate: obj) (f: unit -> unit) = lock gate f

/// The rule header (Xake 5.0, docs/rule-header.md): `needs`, `requires`, registration and the
/// rule forms that take a header. Compile-time cases are in RuleHeaderCompileTests.fs.
[<TestFixture>]
type ``Rule header``() =
    inherit XakeTestBase("rule-header")

    /// Runs a script with these rules and targets on the shared database; true when it failed.
    member private x.Build (targets: string list) (rules': ExecContext Rule list) =
        try
            xake { x.TestOptions with Targets = targets; ResetDb = false; ThrowOnError = true } {
                rules rules'
            }
            false
        with _ -> true

    member private x.Fresh () =
        for d in [ "out"; "src"; "gen" ] do try Directory.Delete(d, true) with _ -> ()
        try File.Delete ".xake" with _ -> ()
        Directory.CreateDirectory "src" |> ignore

    [<Test>]
    member x.``loading a script runs no body, even a statement before the first bind``() =
        x.Fresh ()
        let failed =
            x.Build [ "other" ] [
                "out/(n:*).txt" ..> recipe {
                    needs "src/(n:*).in"
                    failwith "the body ran at registration"
                    do! writeText "never"
                }
                "other" => recipe { () }
            ]
        Assert.That(failed, Is.False)

    [<Test>]
    member x.``on an empty database every needs is built before the body's first step``() =
        x.Fresh ()
        let events = ConcurrentQueue<string>()
        let failed =
            x.Build [ "out/a.txt" ] [
                "dep1" => recipe { System.Threading.Thread.Sleep 50; events.Enqueue "dep1" }
                "dep2" => recipe { events.Enqueue "dep2" }
                "gen/(n:*).txt" ..> recipe { events.Enqueue "gen"; do! writeText "g" }
                "out/(n:*).txt" ..> recipe {
                    needs "dep1"
                    needs [ "dep2"; "gen/(n:*).txt" ]
                    events.Enqueue "body"
                    do! writeText "a"
                }
            ]
        Assert.That(failed, Is.False)
        let order = List.ofSeq events
        Assert.That(order |> List.last, Is.EqualTo "body", sprintf "%A" order)
        Assert.That(order |> List.sort, Is.EqualTo [ "body"; "dep1"; "dep2"; "gen" ])

    [<Test>]
    member x.``a needs fills in the groups and is recorded: an edit of the input rebuilds``() =
        x.Fresh ()
        File.WriteAllText("src/A.in", "v1")
        let runs = ref 0
        let rules () = [
            "out/(n:*).txt" ..> recipe {
                needs "src/(n:*).in"
                runs.Value <- runs.Value + 1
                let! m = getRuleMatches ()
                do! writeText (File.ReadAllText ("src/" + m.["n"] + ".in"))
            } ]
        Assert.That(x.Build [ "out/A.txt" ] (rules ()), Is.False)
        Assert.That(x.Build [ "out/A.txt" ] (rules ()), Is.False)
        Assert.That(runs.Value, Is.EqualTo 1, "up to date")
        System.Threading.Thread.Sleep 100
        File.WriteAllText("src/A.in", "v2")
        Assert.That(x.Build [ "out/A.txt" ] (rules ()), Is.False)
        Assert.That(runs.Value, Is.EqualTo 2, "the static input changed")
        Assert.That(File.ReadAllText "out/A.txt", Is.EqualTo "v2")

    [<Test>]
    member x.``a captured value is substituted literally, not read as pattern syntax``() =
        x.Fresh ()
        File.WriteAllText("src/a[1].in", "x")
        let failed =
            x.Build [ "out/a[1].txt" ] [
                "out/(n:*).txt" ..> recipe { needs "src/(n:*).in"; do! writeText "ok" } ]
        Assert.That(failed, Is.False)
        Assert.That(File.Exists "out/a[1].txt", Is.True)

    [<Test>]
    member x.``a single needs that resolves to nothing fails before the body runs``() =
        x.Fresh ()
        let bodyRan = ref false
        let failed =
            x.Build [ "out/a.txt" ] [
                "out/(n:*).txt" ..> recipe { needs "src/(n:*).missing"; bodyRan.Value <- true; do! writeText "a" } ]
        Assert.That(failed, Is.True)
        Assert.That(bodyRan.Value, Is.False)

    [<Test>]
    member x.``an empty fileset needs does not fail; a new matching file rebuilds``() =
        x.Fresh ()
        let runs = ref 0
        let rules () = [
            "out/all.txt" ..> recipe { needs "src/*.in"; runs.Value <- runs.Value + 1; do! writeText "all" } ]
        Assert.That(x.Build [ "out/all.txt" ] (rules ()), Is.False)
        Assert.That(runs.Value, Is.EqualTo 1)
        Assert.That(x.Build [ "out/all.txt" ] (rules ()), Is.False)
        Assert.That(runs.Value, Is.EqualTo 1, "up to date")
        File.WriteAllText("src/new.in", "n")
        Assert.That(x.Build [ "out/all.txt" ] (rules ()), Is.False)
        Assert.That(runs.Value, Is.EqualTo 2, "the fileset changed")

    [<Test>]
    member x.``a needs may name a phony target``() =
        x.Fresh ()
        let ran = ref false
        let failed =
            x.Build [ "out/a.txt" ] [
                "prepare" => recipe { ran.Value <- true }
                "out/a.txt" ..> recipe { needs "prepare"; do! writeText "a" } ]
        Assert.That(failed, Is.False)
        Assert.That(ran.Value, Is.True)

    [<Test>]
    member x.``requires takes the lock around the body only: static needs of locked rules may be rule targets``() =
        x.Fresh ()
        let lock = Resource.newResource "header-lock" 1
        let inside, most = ref 0, ref 0
        let gate = obj ()
        let body = recipe {
            lock' gate (fun () -> inside.Value <- inside.Value + 1; most.Value <- max most.Value inside.Value)
            System.Threading.Thread.Sleep 30
            lock' gate (fun () -> inside.Value <- inside.Value - 1)
            do! writeText "x"
        }
        let failed =
            xake { x.TestOptions with Targets = [ "all" ]; Threads = 4; ThrowOnError = true } {
                rules [
                    "all" <== [ "out/a.txt"; "out/b.txt"; "out/c.txt" ]
                    "gen/(n:*).txt" ..> recipe { do! writeText "g" }
                    "out/(n:*).txt" ..> recipe {
                        needs "gen/(n:*).txt"      // a rule target: built before the lock, so allowed
                        requires lock
                        do! body
                    } ]
            } |> ignore
            false
        Assert.That(failed, Is.False)
        Assert.That(most.Value, Is.EqualTo 1, "the lock admits one body at a time")

    [<Test>]
    member x.``a dynamic need of a rule target under requires still fails, as under withResource``() =
        x.Fresh ()
        let lock = Resource.newResource "header-guard" 1
        let failed =
            x.Build [ "out/a.txt" ] [
                "gen/a.txt" ..> recipe { do! writeText "g" }
                "out/a.txt" ..> recipe {
                    requires lock
                    do! need [ "gen/a.txt" ]
                    do! writeText "a" } ]
        Assert.That(failed, Is.True)

    [<Test>]
    member x.``requiring on a file rule is requires in its header; naming the lock twice takes it once``() =
        x.Fresh ()
        let lock = Resource.newResource "twice" 1
        let rule = "out/a.txt" ..> recipe { requires lock; do! writeText "a" } |> requiring lock
        match rule with
        | FileRule (_, header, _) -> Assert.That(header.Locks |> List.map snd, Is.EqualTo [ 1 ])
        | other -> Assert.Fail (sprintf "expected a file rule, got %A" other)
        Assert.That(x.Build [ "out/a.txt" ] [ rule ], Is.False, "a lock held twice by one rule would hang")

    [<Test>]
    member x.``the header of a delegated rule applies: needs built, lock taken``() =
        x.Fresh ()
        let lock = Resource.newResource "delegated-lock" 1
        let built = ref false
        let executor : DelegatedExecutor<ExecContext> = fun _ _ body -> body ()
        let failed =
            x.Build [ "out/a.txt" ] [
                "dep" => recipe { built.Value <- true }
                "out/a.txt" ..> recipe { needs "dep"; requires lock; do! writeText "a" } |> delegated executor ]
        Assert.That(failed, Is.False)
        Assert.That(built.Value, Is.True)

    [<Test>]
    member x.``every file-rule form takes a header``() =
        x.Fresh ()
        File.WriteAllText("src/in.txt", "i")
        let count = ref 0
        let note = recipe { count.Value <- count.Value + 1 }
        let failed =
            x.Build [ "out/1.txt"; "out/2a.txt"; "out/3.txt"; "out/4a.txt"; "out/5.cond" ] [
                "out/1.txt" ..> recipe {
                    needs "src/in.txt"
                    do! note
                    do! writeText "1" }
                [ "out/2a.txt"; "out/2b.txt" ] *..> recipe {
                    needs "src/in.txt"
                    do! note
                    do! writeText "2"
                    File.WriteAllText("out/2b.txt", "2b") }
                target "out/3.txt" {
                    needs "src/in.txt"
                    do! note
                    do! writeText "3" }
                targets [ "out/4a.txt"; "out/4b.txt" ] {
                    needs "src/in.txt"
                    do! note
                    do! writeText "4"
                    File.WriteAllText("out/4b.txt", "4b") }
                (fun (f: string) -> f.EndsWith ".cond") ..?> recipe {
                    needs "src/in.txt"
                    do! note
                    do! writeText "5" } ]
        Assert.That(failed, Is.False)
        Assert.That(count.Value, Is.EqualTo 5)

    [<Test>]
    member x.``a header-only rule has needs and an empty body``() =
        x.Fresh ()
        let ran = ref false
        let failed =
            x.Build [ "stamp" ] [
                "dep" => recipe { ran.Value <- true }
                "out/h.txt" ..> recipe { needs "dep" }
                "stamp" <== [ "out/h.txt" ] ]
        // the body writes nothing, so the target file is missing afterwards: that is the rule's business
        ignore failed
        Assert.That(ran.Value, Is.True)

    [<Test>]
    member x.``registration errors: an unknown group in needs, an also or a group on a condition rule``() =
        let loads (r: ExecContext Rule) =
            try xake { x.TestOptions with Targets = [ "none" ]; ThrowOnError = true } { rules [ r; "none" => recipe { () } ] } ; None
            with e -> Some e.Message
        Assert.That(loads ("out/(n:*).txt" ..> recipe { needs "src/(m:*).in" }) |> Option.defaultValue "",
                    Does.Contain "needs \"src/(m:*).in\" uses the group [m], which the rule's pattern does not have (its groups: [n])")
        Assert.That(loads ((fun _ -> true) ..?> recipe { also "x.pdb" }) |> Option.defaultValue "",
                    Does.Contain "a condition rule (..?>) cannot have an also")
        Assert.That(loads ((fun _ -> true) ..?> recipe { needs "src/(n:*).in" }) |> Option.defaultValue "",
                    Does.Contain "a condition rule has no pattern and no groups")
        Assert.That(loads ("out/(n:*).txt" ..> recipe { also "out/(m:*).pdb" }) |> Option.defaultValue "", Does.Contain "XP013")

    [<Test>]
    member x.``including merges a shared header: its needs are built first, its lock taken``() =
        x.Fresh ()
        File.WriteAllText("src/A.rsp", "r")
        let lock = Resource.newResource "shared-lock" 1
        let shared = ruleHeader ["n"] { needs "src/(n:*).rsp"; requires lock }
        let events = ConcurrentQueue<string>()
        let rule =
            "out/(n:*).dll" ..> recipe {
                including shared
                needs "prep"
                events.Enqueue "body"
                do! writeText "dll"
            }
        match rule with
        | FileRule (_, h, _) ->
            Assert.That(h.Needs, Is.EqualTo [ "src/(n:*).rsp"; "prep" ])
            Assert.That(h.Locks |> List.map (fun (r, _) -> r.ResourceName), Is.EqualTo [ "shared-lock" ])
        | other -> Assert.Fail (sprintf "expected a file rule, got %A" other)
        let failed = x.Build [ "out/A.dll" ] [ "prep" => recipe { events.Enqueue "prep" }; rule ]
        Assert.That(failed, Is.False)
        Assert.That(List.ofSeq events, Is.EqualTo [ "prep"; "body" ])

    [<Test>]
    member x.``ruleHeader checks its groups when it is made``() =
        let made (f: unit -> SharedHeader) = try f () |> ignore; "" with e -> e.Message
        Assert.That(made (fun () -> ruleHeader ["n"] { needs "obj/(m:*).rsp" }),
                    Does.Contain "ruleHeader [n] (needs \"obj/(m:*).rsp\"): needs \"obj/(m:*).rsp\" uses the group m, which is not declared; declared: [n]")
        Assert.That(made (fun () -> ruleHeader ["n"; "cfg"] { needs "obj/(n:*).rsp" }),
                    Does.Contain "the group cfg is declared but no pattern uses it")
        Assert.That(made (fun () -> ruleHeader [] { needs "obj/common.rsp" }), Is.Empty)

    [<Test>]
    member x.``including a header whose group the rule lacks fails, naming the rule, the header and the group``() =
        let shared = ruleHeader ["n"] { needs "obj/(n:*).rsp" }
        let loads (r: ExecContext Rule) =
            try xake { x.TestOptions with Targets = [ "none" ]; ThrowOnError = true } { rules [ r; "none" => recipe { () } ] } ; ""
            with e -> e.Message
        Assert.That(loads ("out/(name:*).dll" ..> recipe { including shared }),
                    Does.Contain "rule \"out/(name:*).dll\" includes ruleHeader [n] (needs \"obj/(n:*).rsp\"), which uses the group n; the rule has the groups [name]")
        Assert.That(loads ((fun _ -> true) ..?> recipe { including shared }),
                    Does.Contain "includes ruleHeader [n] (needs \"obj/(n:*).rsp\"), which uses the group n; a condition rule has no pattern and no groups")
        Assert.That(loads ((fun _ -> true) ..?> recipe { including (ruleHeader [] { needs "obj/common.rsp" }) }), Is.Empty)
