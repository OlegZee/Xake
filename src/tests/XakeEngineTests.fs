namespace Tests

open System.IO
open NUnit.Framework
open Xake
open Xake.Tasks

[<TestFixture>]
type ``XakeEngine tests``() =
    inherit XakeTestBase("engine")

    [<Test>]
    member x.``noPersist always rebuilds across engine instances``() =
        let count = ref 0

        let runOnce () =
            let eng = xakeEngine {
                phony "build" (recipe { count := !count + 1 })
                start
            }
            eng.Demand("build").Wait()
            eng.StopAsync().Wait()

        runOnce ()
        runOnce ()

        Assert.AreEqual(2, !count)

    [<Test>]
    member x.``Concurrent Demands for same target are serialized, not deduplicated``() =
        let count = ref 0

        let eng = xakeEngine {
            phony "build" (recipe {
                do! Async.Sleep 100
                count := !count + 1
            })
            start
        }

        let t1 = eng.Demand "build"
        let t2 = eng.Demand "build"

        eng.StopAsync().Wait()

        Assert.AreNotSame(t1, t2, "second Demand must return a different Task")
        Assert.AreEqual(1, !count, "recipe must run exactly once (second call skipped by DB)")

    [<Test>]
    member x.``Concurrent Demands are executed sequentially not concurrently``() =
        let maxConcurrent = ref 0
        let current = ref 0

        let eng = xakeEngine {
            phony "build" (recipe {
                let n = System.Threading.Interlocked.Increment current
                if n > !maxConcurrent then maxConcurrent := n
                do! Async.Sleep 50
                System.Threading.Interlocked.Decrement current |> ignore
            })
            start
        }

        let t1 = eng.Demand "build"
        let t2 = eng.Demand "build"
        System.Threading.Tasks.Task.WaitAll(t1, t2)
        eng.StopAsync().Wait()

        Assert.AreEqual(1, !maxConcurrent, "never more than one execution at a time")

    [<Test>]
    member x.``Second Demand waits for first before starting``() =
        let log = System.Collections.Generic.List<string> ()

        let eng = xakeEngine {
            phony "build" (recipe {
                lock log (fun () -> log.Add "start")
                do! Async.Sleep 50
                lock log (fun () -> log.Add "end")
            })
            start
        }

        let t1 = eng.Demand "build"
        System.Threading.Thread.Sleep 10
        let t2 = eng.Demand "build"
        System.Threading.Tasks.Task.WaitAll(t1, t2)
        eng.StopAsync().Wait()

        Assert.AreEqual(["start"; "end"; "start"; "end"], log |> Seq.toList, "executions must be sequential")

    [<Test>]
    member x.``Demand after StopAsync raises InvalidOperationException``() =

        let eng = xakeEngine {
            phony "build" (recipe { () })
            start
        }

        eng.StopAsync().Wait()

        Assert.Throws<System.InvalidOperationException>(fun () ->
            eng.Demand "build" |> ignore) |> ignore

    [<Test>]
    member x.``StopAsync drains in-flight tasks before running teardown``() =
        let log = System.Collections.Generic.List<string> ()

        let eng = xakeEngine {
            teardown ["cleanup"]
            rules[
                "build" => recipe {
                    do! Async.Sleep 100
                    lock log (fun () -> log.Add "build")
                }
                "cleanup" => recipe {
                    lock log (fun () -> log.Add "cleanup")
                }
            ]
            start
        }

        eng.Demand "build" |> ignore
        eng.StopAsync().Wait()

        let result = log |> Seq.toList
        Assert.AreEqual(["build"; "cleanup"], result, "teardown must run after all in-flight demands")

    [<Test>]
    member x.``noPersist does not create db file``() =
        let dbFile = x.TestOptions.ProjectRoot </> ".xake"
        try File.Delete dbFile with _ -> ()

        let eng = xakeEngine {
            phony "build" (recipe { () })
            start
        }

        eng.Demand("build").Wait()
        eng.StopAsync().Wait()

        Assert.IsFalse(File.Exists dbFile, "db file must not be created when NoPersist = true")

    [<Test>]
    member x.``Sequential Demand skips rebuild when var dependency is unchanged``() =
        let count = ref 0

        let eng = xakeEngine {
            phony "build" (recipe {
                let! _ = getVar "VERSION"
                count := !count + 1
            })
            start
        }

        eng.Demand("build", ["VERSION", "1.0"]).Wait()
        eng.Demand("build", ["VERSION", "1.0"]).Wait()
        eng.StopAsync().Wait()

        Assert.AreEqual(1, !count, "recipe must run exactly once; second demand skipped because var is unchanged")

    [<Test>]
    member x.``Sequential Demand rebuilds target when dependent var changes``() =
        let count = ref 0

        let eng = xakeEngine {
            phony "build" (recipe {
                let! _ = getVar "VERSION"
                count := !count + 1
            })
            start
        }

        eng.Demand("build", ["VERSION", "1.0"]).Wait()
        eng.Demand("build", ["VERSION", "2.0"]).Wait()
        eng.StopAsync().Wait()

        Assert.AreEqual(2, !count, "recipe must run twice; second demand triggered by changed var")

    /// A dependent's recipe, after its `need`, sees the dependee's result in the database with
    /// this run's epoch: the result is stored before the dependee's task is done (C2).
    [<Test>]
    member x.``a dependent sees its dependee's result once need returns``() =
        let seen = ref None
        let eng = xakeEngine {
            phony "c2a" (recipe { do () })
            phony "c2b" (recipe {
                do! need [ "c2a" ]
                let! ctx = getCtx ()
                seen.Value <- Some (Storage.Db.tryGetResult ctx.Db (PhonyAction "c2a") |> Option.map (fun r -> r.Epoch), ctx.Run.RunEpoch) })
            start
        }
        eng.Demand("c2b").Wait()
        eng.StopAsync().Wait()
        match seen.Value with
        | Some (Some epoch, run) -> Assert.AreEqual(run, epoch)
        | other -> Assert.Fail(sprintf "no result of c2a: %A" other)

    /// Two Demands of different targets at once are two overlapping runs: each builds, each result
    /// carries its own run's epoch, and the demands settle: at most one more build each, then
    /// none (C13; EpochTests checks the
    /// epochs of two overlapping phony runs).
    [<Test>]
    member x.``overlapping demands of different targets keep their own runs``() =
        let root = x.TestOptions.ProjectRoot
        File.WriteAllText(root </> "c13.c", "s")
        let runs = ref 0
        let current = ref 0
        let overlap = ref 0
        let body name = recipe {
            do! need [ "c13.c" ]
            System.Threading.Interlocked.Increment runs |> ignore
            let n = System.Threading.Interlocked.Increment current
            lock overlap (fun () -> if n > overlap.Value then overlap.Value <- n)
            do! Async.Sleep 200
            System.Threading.Interlocked.Decrement current |> ignore
            do! writeText name }
        let eng =
            RulesBuilder { x.TestOptions with IgnoreCommandLine = true; Progress = false; Threads = 4 } {
                rules [ "c13a.txt" ..> body "a"
                        "c13b.txt" ..> body "b" ]
                start
            }
        let demandBoth () = System.Threading.Tasks.Task.WaitAll(eng.Demand "c13a.txt", eng.Demand "c13b.txt")
        demandBoth ()
        Assert.AreEqual(2, runs.Value, "each target built once")
        Assert.AreEqual(2, overlap.Value, "the two runs overlapped")
        // an observation is stamped with the newest epoch handed out, so the run with the older
        // epoch may see its shared source as changed after its result, and build once more
        // (never a missed change; docs/implnotes.md, "Epochs")
        demandBoth ()
        Assert.That(runs.Value, Is.InRange(2, 4))
        let settled = runs.Value
        demandBoth ()
        eng.StopAsync().Wait()
        Assert.AreEqual(settled, runs.Value, "then both are up to date")
        let db = Storage.DbReader.readDatabase root (root </> x.TestOptions.DbFileName)
        let epochOf name = db.Status.[FileTarget (File.make (root </> name))].Epoch
        Assert.AreNotEqual(epochOf "c13a.txt", epochOf "c13b.txt")

