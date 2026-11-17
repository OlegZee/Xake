namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks

/// Content cut-off (Xake 5.0 step 3, docs/content-cutoff.md), acceptance "Step 3".
/// The content histories are not persisted in the database until the 0.7 layout lands, so these
/// tests run several demands on one engine, which keeps them in memory between demands.
[<TestFixture>]
type ``Content cut-off``() =
    inherit XakeTestBase("content-cutoff")

    /// Times closer than 10 ms are the same time: keep writes apart.
    let later () = System.Threading.Thread.Sleep 40

    member private x.Fresh () =
        for d in [ "out"; "src"; "obj" ] do try Directory.Delete(d, true) with _ -> ()
        try File.Delete ".xake" with _ -> ()
        Directory.CreateDirectory "src" |> ignore

    member private x.Engine (cutoff: bool) (rules': ExecContext Rule list) =
        xake { x.TestOptions with ContentCutoff = cutoff; ThrowOnError = true } {
            var "ROUTE" "on"
            rules rules'
            start
        }

    /// Demands a target; returns the run's summary.
    member private x.Demand (engine: XakeEngine) (target: string) (vars: (string * string) list) =
        engine.Demand(target, vars).Wait()
        engine.LastSummary |> Option.get

    /// `out/A.dll` from `src/A.cs`, deterministic (a comment does not change the output); counts runs.
    member private x.Compiler (runs: int ref) =
        "out/(n:*).dll" ..> recipe {
            let! m = getRuleMatches ()
            let src = "src/" + m.["n"] + ".cs"
            do! need [ src ]
            runs.Value <- runs.Value + 1
            let code = File.ReadAllLines src |> Array.filter (fun l -> not (l.StartsWith "//")) |> String.concat "\n"
            do! writeText ("compiled: " + code)
        }

    /// `out/App.exe` names `out/A.dll` statically (`needs`); counts runs.
    member private x.App (runs: int ref) =
        "out/App.exe" ..> recipe {
            needs "out/A.dll"
            runs.Value <- runs.Value + 1
            do! writeText ("app " + File.ReadAllText "out/A.dll")
        }

    [<Test>]
    member x.``control file A to B to A: the dependents do not rerun when the route comes back``() =
        x.Fresh ()
        let routeRuns, built = ref 0, ref 0
        let rules = [
            "obj/layer.route" ..> recipe {
                let! route = getVar "ROUTE"
                routeRuns.Value <- routeRuns.Value + 1
                do! writeText (defaultArg route "on")
            }
            "out/(n:*).txt" ..> recipe {
                needs "obj/layer.route"
                built.Value <- built.Value + 1
                do! writeText "t"
            }
            "all" => recipe { do! need [ for i in 1 .. 100 -> sprintf "out/%d.txt" i ] } ]
        let engine = x.Engine true rules
        let first = x.Demand engine "all" [ "ROUTE", "on" ]
        Assert.That(built.Value, Is.EqualTo 100)
        Assert.That(first.FilesHashed, Is.EqualTo 0, "a clean build hashes nothing")
        later ()
        x.Demand engine "obj/layer.route" [ "ROUTE", "off" ] |> ignore      // the 100 are not requested
        Assert.That(File.ReadAllText "obj/layer.route", Is.EqualTo "off")
        later ()
        let back = x.Demand engine "all" [ "ROUTE", "on" ]
        Assert.That(routeRuns.Value, Is.EqualTo 3)
        Assert.That(built.Value, Is.EqualTo 100, "the route returned to the content the 100 were built from")
        Assert.That(back.TargetsSkipped, Is.GreaterThanOrEqualTo 100)
        let again = x.Demand engine "all" [ "ROUTE", "on" ]
        Assert.That(again.FilesHashed, Is.EqualTo 0, "a further run with no change hashes nothing")
        Assert.That(built.Value, Is.EqualTo 100)
        engine.StopAsync().Wait()

    [<Test>]
    member x.``control file, control: requested while the route is off, the dependents rebuild both times``() =
        x.Fresh ()
        let built = ref 0
        let rules = [
            "obj/layer.route" ..> recipe {
                let! route = getVar "ROUTE"
                do! writeText (defaultArg route "on")
            }
            "out/(n:*).txt" ..> recipe {
                needs "obj/layer.route"
                built.Value <- built.Value + 1
                do! writeText "t"
            }
            "all" => recipe { do! need [ for i in 1 .. 10 -> sprintf "out/%d.txt" i ] } ]
        let engine = x.Engine true rules
        x.Demand engine "all" [ "ROUTE", "on" ] |> ignore
        later ()
        x.Demand engine "all" [ "ROUTE", "off" ] |> ignore
        later ()
        x.Demand engine "all" [ "ROUTE", "on" ] |> ignore
        Assert.That(built.Value, Is.EqualTo 30)
        engine.StopAsync().Wait()

    [<Test>]
    member x.``an output rebuilt with the same bytes does not rerun its dependents, from the first rebuild``() =
        x.Fresh ()
        File.WriteAllText("src/A.cs", "class A {}")
        let compiled, linked = ref 0, ref 0
        let engine = x.Engine true [ x.Compiler compiled; x.App linked ]
        x.Demand engine "out/App.exe" [] |> ignore
        later ()
        File.WriteAllText("src/A.cs", "// a comment\nclass A {}")
        let summary = x.Demand engine "out/App.exe" []
        Assert.That((compiled.Value, linked.Value), Is.EqualTo((2, 1)))
        Assert.That(summary.CutOff, Is.EqualTo 1)
        later ()
        File.WriteAllText("src/A.cs", "class A { int x; }")
        x.Demand engine "out/App.exe" [] |> ignore
        Assert.That((compiled.Value, linked.Value), Is.EqualTo((3, 2)), "different bytes rerun the dependent")
        engine.StopAsync().Wait()

    [<Test>]
    member x.``a dynamic need of an out-of-date rule target reruns the dependent; nothing is built to decide it``() =
        x.Fresh ()
        File.WriteAllText("src/A.cs", "class A {}")
        let compiled, used = ref 0, ref 0
        let wantA = ref true
        let user =
            "out/User.exe" ..> recipe {
                if wantA.Value then do! need [ "out/A.dll" ]   // dynamic, and untracked: the case a speculation would build
                used.Value <- used.Value + 1
                do! writeText "user"
            }
        let engine = x.Engine true [ x.Compiler compiled; user ]
        x.Demand engine "out/User.exe" [] |> ignore
        later ()
        File.WriteAllText("src/A.cs", "// comment\nclass A {}")      // A is out of date; same bytes if rebuilt
        wantA.Value <- false
        x.Demand engine "out/User.exe" [] |> ignore
        Assert.That(used.Value, Is.EqualTo 2, "the dependent reruns: A is out of date and is not built to decide it")
        Assert.That(compiled.Value, Is.EqualTo 1, "A is not built: the body no longer needs it")
        engine.StopAsync().Wait()

    [<Test>]
    member x.``an output edited by hand: without a hash the dependents rerun, with one a rebuild of the same bytes keeps them``() =
        x.Fresh ()
        File.WriteAllText("src/A.cs", "class A {}")
        let compiled, linked = ref 0, ref 0
        let engine = x.Engine true [ x.Compiler compiled; x.App linked ]
        x.Demand engine "out/App.exe" [] |> ignore
        later ()
        File.WriteAllText("out/A.dll", "hacked")                     // no hash known for A's first output
        x.Demand engine "out/App.exe" [] |> ignore
        Assert.That((compiled.Value, linked.Value), Is.EqualTo((2, 2)))
        later ()
        File.WriteAllText("out/A.dll", "hacked again")               // A's output now has a hash
        x.Demand engine "out/App.exe" [] |> ignore
        Assert.That((compiled.Value, linked.Value), Is.EqualTo((3, 2)), "the rebuild reproduced what App saw")
        engine.StopAsync().Wait()

    [<Test>]
    member x.``an output restored with a new time and its recorded hash reruns nothing``() =
        x.Fresh ()
        File.WriteAllText("src/A.cs", "class A {}")
        let compiled, linked = ref 0, ref 0
        let engine = x.Engine true [ x.Compiler compiled; x.App linked ]
        x.Demand engine "out/App.exe" [] |> ignore
        later ()
        File.WriteAllText("src/A.cs", "// c\nclass A {}")
        x.Demand engine "out/App.exe" [] |> ignore                      // A rebuilt: its output has a hash
        Assert.That((compiled.Value, linked.Value), Is.EqualTo((2, 1)))
        later ()
        File.SetLastWriteTime("out/A.dll", System.DateTime.Now.AddMinutes 5.0)   // a restore: new time, same bytes
        let summary = x.Demand engine "out/App.exe" []
        Assert.That((compiled.Value, linked.Value), Is.EqualTo((2, 1)))
        Assert.That(summary.FilesHashed, Is.EqualTo 1)
        engine.StopAsync().Wait()

    [<Test>]
    member x.``a source touched without an edit: the second time nothing reruns; then nothing is hashed``() =
        x.Fresh ()
        File.WriteAllText("src/A.cs", "class A {}")
        let compiled = ref 0
        let engine = x.Engine true [ x.Compiler compiled ]
        x.Demand engine "out/A.dll" [] |> ignore
        later ()
        File.SetLastWriteTime("src/A.cs", System.DateTime.Now)
        x.Demand engine "out/A.dll" [] |> ignore
        Assert.That(compiled.Value, Is.EqualTo 2, "the first move: the earlier content is not known")
        later ()
        File.SetLastWriteTime("src/A.cs", System.DateTime.Now)
        x.Demand engine "out/A.dll" [] |> ignore
        Assert.That(compiled.Value, Is.EqualTo 2, "the second move: same content")
        let quiet = x.Demand engine "out/A.dll" []
        Assert.That(quiet.FilesHashed, Is.EqualTo 0)
        engine.StopAsync().Wait()

    [<Test>]
    member x.``with --content-cutoff=off a touched source reruns its dependents``() =
        x.Fresh ()
        File.WriteAllText("src/A.cs", "class A {}")
        let compiled = ref 0
        let engine = x.Engine false [ x.Compiler compiled ]
        x.Demand engine "out/A.dll" [] |> ignore
        for _ in 1 .. 2 do
            later ()
            File.SetLastWriteTime("src/A.cs", System.DateTime.Now)
            let summary = x.Demand engine "out/A.dll" []
            Assert.That(summary.FilesHashed, Is.EqualTo 0, "off hashes nothing")
        Assert.That(compiled.Value, Is.EqualTo 3)
        engine.StopAsync().Wait()

    [<Test>]
    member x.``non-monotonic times: content A at T10, B at T20, A restored at T10 and touched to T30 reruns the dependent``() =
        x.Fresh ()
        let t10 = System.DateTime.Now.AddMinutes -10.0
        let t20 = System.DateTime.Now.AddMinutes -5.0
        File.WriteAllText("src/A.cs", "class A {}")
        File.SetLastWriteTime("src/A.cs", t10)
        let compiled = ref 0
        let engine = x.Engine true [ x.Compiler compiled ]
        x.Demand engine "out/A.dll" [] |> ignore
        File.WriteAllText("src/A.cs", "class B {}")
        File.SetLastWriteTime("src/A.cs", t20)
        x.Demand engine "out/A.dll" [] |> ignore                         // built from B
        File.WriteAllText("src/A.cs", "class A {}")
        File.SetLastWriteTime("src/A.cs", t10)
        File.SetLastWriteTime("src/A.cs", System.DateTime.Now)          // touched to T30
        x.Demand engine "out/A.dll" [] |> ignore
        Assert.That(compiled.Value, Is.EqualTo 3)
        Assert.That(File.ReadAllText "out/A.dll", Is.EqualTo "compiled: class A {}")
        engine.StopAsync().Wait()

    [<Test>]
    member x.``the same time with a different size reruns the dependent``() =
        x.Fresh ()
        let t = System.DateTime.Now.AddMinutes -3.0
        File.WriteAllText("src/A.cs", "class A {}")
        File.SetLastWriteTime("src/A.cs", t)
        let compiled = ref 0
        let engine = x.Engine true [ x.Compiler compiled ]
        x.Demand engine "out/A.dll" [] |> ignore
        File.WriteAllText("src/A.cs", "class A { int longer; }")
        File.SetLastWriteTime("src/A.cs", t)
        x.Demand engine "out/A.dll" [] |> ignore
        Assert.That(compiled.Value, Is.EqualTo 2)
        engine.StopAsync().Wait()

    [<Test>]
    member x.``noContentCutoff: a rebuild with the same bytes reruns the dependents``() =
        x.Fresh ()
        File.WriteAllText("src/A.cs", "class A {}")
        let linked = ref 0
        let huge =
            "out/(n:*).dll" ..> recipe {
                noContentCutoff
                let! m = getRuleMatches ()
                do! need [ "src/" + m.["n"] + ".cs" ]
                do! writeText "always the same"
            }
        let engine = x.Engine true [ huge; x.App linked ]
        x.Demand engine "out/App.exe" [] |> ignore
        later ()
        File.WriteAllText("src/A.cs", "class A { }")
        let summary = x.Demand engine "out/App.exe" []
        Assert.That(linked.Value, Is.EqualTo 2)
        Assert.That(summary.CutOff, Is.EqualTo 1, "App itself, rebuilt with the same bytes; never the noContentCutoff rule")
        engine.StopAsync().Wait()
