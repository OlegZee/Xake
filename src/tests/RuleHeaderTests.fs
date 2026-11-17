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
    member x.``requiring on a file rule is requires in its header``() =
        let lock = Resource.newResource "requiring" 1
        match "out/a.txt" ..> recipe { do! writeText "a" } |> requiring lock with
        | FileRule (_, header, _) -> Assert.That(header.Locks |> List.map (fun (r, n) -> r.ResourceName, n), Is.EqualTo [ "requiring", 1 ])
        | other -> Assert.Fail (sprintf "expected a file rule, got %A" other)

    [<Test>]
    member x.``a resource named twice on one rule is a registration error, whatever named it``() =
        let lock = Resource.newResource "twice" 1
        let other = Resource.newResource "twice" 1          // another resource with the same name
        let shared = ruleHeader [] { requires lock }
        let loads (r: ExecContext Rule) =
            try xake { x.TestOptions with Targets = [ "none" ]; ThrowOnError = true } { rules [ r; "none" => recipe { () } ] } ; ""
            with e -> e.Message
        let expected = "rule \"out/a.txt\" requires the resource twice more than once"
        for rule in [ "out/a.txt" ..> recipe { requires lock; do! writeText "a" } |> requiring lock
                      "out/a.txt" ..> recipe { requires lock; requires (lock, 2); do! writeText "a" }
                      "out/a.txt" ..> recipe { including shared; including shared; do! writeText "a" }
                      "out/a.txt" ..> recipe { requires lock; requires other; do! writeText "a" } ] do
            Assert.That(loads rule, Does.Contain expected)

    [<Test>]
    member x.``locks are taken by name, then by creation, whatever the order they are written in``() =
        let b = Resource.newResource "b-lock" 1
        let a1 = Resource.newResource "a-lock" 1
        let a2 = Resource.newResource "a-lock-2" 1
        let header = { RuleHeader.Empty with Requires = [ b, 1; a2, 1; a1, 1 ] }
        Assert.That(header.Locks |> List.map (fun (r, _) -> r.ResourceName), Is.EqualTo [ "a-lock"; "a-lock-2"; "b-lock" ])

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
    member x.``a header-only file rule is a registration error``() =
        let loads (r: ExecContext Rule) =
            try xake { x.TestOptions with Targets = [ "none" ]; ThrowOnError = true } { rules [ r; "none" => recipe { () } ] } ; ""
            with e -> e.Message
        Assert.That(loads ("out/h.txt" ..> recipe { needs "dep" }), Does.Contain "rule \"out/h.txt\" has a header and no body")
        Assert.That(loads (target "out/h.txt" { needs "dep" }), Does.Contain "rule \"out/h.txt\" has a header and no body")
        Assert.That(loads ((fun _ -> true) ..?> recipe { needs "dep" }), Does.Contain "has a header and no body")
        // a body, even an empty one, is a body
        Assert.That(loads ("out/h.txt" ..> recipe { needs "dep"; () }), Is.Empty)

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
        Assert.That(loads ((fun _ -> true) ..?> recipe { including (ruleHeader [] { needs "obj/common.rsp" }); () }), Is.Empty)

    [<Test>]
    member x.``X.RuleWith gives the typed function form a header, checked against the pattern``() =
        x.Fresh ()
        File.WriteAllText("src/A.rsp", "r")
        let Asm = Xake.Typed.Target<{| n: string |}> "out/(n:*).dll"
        let events = ConcurrentQueue<string>()
        let shared = ruleHeader ["n"] { needs "src/(n:*).rsp"; needs "prep" }
        let rule = Asm.RuleWith shared <| fun m -> recipe { events.Enqueue ("body " + m.n); do! writeText "dll" }
        let failed = x.Build [ "out/A.dll" ] [ "prep" => recipe { events.Enqueue "prep" }; rule ]
        Assert.That(failed, Is.False)
        Assert.That(List.ofSeq events, Is.EqualTo [ "prep"; "body A" ])
        let Other = Xake.Typed.Target<{| name: string |}> "out/(name:*).lib"
        let message =
            try xake { x.TestOptions with Targets = [ "none" ]; ThrowOnError = true } {
                    rules [ (Other.RuleWith shared <| fun _ -> recipe { () }); "none" => recipe { () } ] } ; ""
            with e -> e.Message
        Assert.That(message, Does.Contain "rule \"out/(name:*).lib\" includes ruleHeader [n]")

    [<Test>]
    member x.``a delegated rule holds the lock of its header around the body``() =
        x.Fresh ()
        let lock = Resource.newResource "delegated-guard" 1
        let executor : DelegatedExecutor<ExecContext> = fun _ _ body -> body ()
        let failed =
            x.Build [ "out/a.txt" ] [
                "gen/a.txt" ..> recipe { do! writeText "g" }
                "out/a.txt" ..> recipe {
                    requires lock
                    do! need [ "gen/a.txt" ]       // a rule target under the held lock: the guard fails it
                    do! writeText "a" } |> delegated executor ]
        Assert.That(failed, Is.True, "the lock is held in the delegated body, so the need is refused")

/// The run summary (item 1h): counters per run, the log line, `onSummary`, `XakeEngine.LastSummary`.
[<TestFixture>]
type ``Run summary``() =
    inherit XakeTestBase("run-summary")

    [<Test>]
    member x.``a run counts the targets it ran and skipped, and reports to onSummary``() =
        for d in [ "out" ] do try Directory.Delete(d, true) with _ -> ()
        try File.Delete ".xake" with _ -> ()
        File.WriteAllText("c.in", "c")
        let summaries = ResizeArray<RunSummary>()
        let run () =
            xake { x.TestOptions with Targets = [ "out/a.txt"; "out/b.txt" ]; ResetDb = false; ThrowOnError = true } {
                onSummary summaries.Add
                rules [
                    "out/a.txt" ..> recipe {
                        do! need [ "out/c.txt" ]
                        do! writeText "a" }
                    "out/b.txt" ..> recipe {
                        do! need [ "out/c.txt" ]
                        do! writeText "b" }
                    "out/c.txt" ..> recipe {
                        do! need [ "c.in" ]
                        do! writeText "c" } ]
            }
        run ()
        run ()
        Assert.That(summaries.Count, Is.EqualTo 2)
        let first, second = summaries.[0], summaries.[1]
        // two command-line targets are two groups, run one after the other: b's group finds c up to date
        Assert.That((first.TargetsRun, first.TargetsSkipped, first.TasksScheduled), Is.EqualTo((3, 1, 4)))
        Assert.That((second.TargetsRun, second.TargetsSkipped, second.TasksScheduled), Is.EqualTo((0, 2, 2)), "a and b are decided up to date without scheduling c")
        Assert.That((second.CutOff, second.FilesHashed, second.HashTime), Is.EqualTo((0, 0, System.TimeSpan.Zero)))

    [<Test>]
    member x.``an engine keeps the summary of the last demand``() =
        let eng = xakeEngine {
            phony "build" (recipe { () })
            start
        }
        Assert.That(eng.LastSummary, Is.EqualTo None)
        eng.Demand("build").Wait()
        Assert.That(eng.LastSummary |> Option.map (fun s -> s.TargetsRun), Is.EqualTo (Some 1))
        eng.StopAsync().Wait()

