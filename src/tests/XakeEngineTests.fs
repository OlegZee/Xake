namespace Tests

open System.IO
open NUnit.Framework
open Xake

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

