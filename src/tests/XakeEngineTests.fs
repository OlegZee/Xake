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
    member x.``Demand deduplication returns same Task for concurrent calls``() =
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

        Assert.AreSame(t1, t2, "second Demand must return the same Task")
        Assert.AreEqual(1, !count, "recipe must run exactly once within a session")

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

