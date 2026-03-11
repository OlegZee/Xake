namespace Tests

open System.IO
open NUnit.Framework
open Xake
open Xake.Experimental

[<TestFixture>]
type ``Command tests``() =
    inherit XakeTestBase("command")

    [<Test>]
    member x.``phony with deps skips on second run``() =
        let count = ref 0
        File.WriteAllText("src.txt", "content")

        let build () = xake {x.TestOptions with Targets = ["build"]; ResetDb = false} {
            rules [
                "build" => recipe {
                    do! need ["src.txt"]
                    count := !count + 1
                }
            ]
        }

        build ()
        build ()

        Assert.AreEqual(1, !count, "phony with file deps should skip on second run")

    [<Test>]
    member x.``command always executes across script runs``() =
        let count = ref 0
        File.WriteAllText("src2.txt", "content")

        let build () = xake {x.TestOptions with Targets = ["build"]; ResetDb = false} {
            rules [
                command "build" {
                    do! need ["src2.txt"]
                    count := !count + 1
                }
            ]
        }

        build ()
        build ()

        Assert.AreEqual(2, !count, "command should always execute even when deps unchanged")

    [<Test>]
    member x.``command deduplicates within single build``() =
        let count = ref 0

        do xake x.TestOptions {
            rules [
                "main" <== ["test"; "deploy"]
                command "test" {
                    do! need ["build"]
                }
                command "deploy" {
                    do! need ["build"]
                }
                command "build" {
                    count := !count + 1
                }
            ]
        }

        Assert.AreEqual(1, !count, "command should run only once within a single build via WorkerPool dedup")

    [<Test>]
    member x.``command re-executes on each Demand``() =
        let count = ref 0

        let eng = xakeEngine {
            rules [
                command "build" {
                    count := !count + 1
                }
            ]
            start
        }

        eng.Demand("build").Wait()
        eng.Demand("build").Wait()
        eng.StopAsync().Wait()

        Assert.AreEqual(2, !count, "command should re-execute on each Demand")

    [<Test>]
    member x.``command with deps still checks deps``() =
        let deployCount = ref 0
        let buildCount = ref 0

        let eng = xakeEngine {
            rules [
                command "deploy" {
                    do! need ["build"]
                    deployCount := !deployCount + 1
                }
            ]
            phony "build" (recipe {
                let! _ = getVar "V"
                buildCount := !buildCount + 1
            })
            start
        }

        eng.Demand("deploy", ["V", "1.0"]).Wait()
        eng.Demand("deploy", ["V", "1.0"]).Wait()
        eng.StopAsync().Wait()

        Assert.AreEqual(2, !deployCount, "command deploy should run twice")
        Assert.AreEqual(1, !buildCount, "phony build should run once since var dep unchanged")

    [<Test>]
    member x.``phony with deps skips on second Demand when deps unchanged``() =
        let count = ref 0

        let eng = xakeEngine {
            phony "build" (recipe {
                let! _ = getVar "V"
                count := !count + 1
            })
            start
        }

        eng.Demand("build", ["V", "1.0"]).Wait()
        eng.Demand("build", ["V", "1.0"]).Wait()
        eng.StopAsync().Wait()

        Assert.AreEqual(1, !count, "phony should skip when var dep unchanged")

    [<Test>]
    member x.``concurrent Demands for command are serialized``() =
        let count = ref 0

        let eng = xakeEngine {
            rules [
                command "build" {
                    do! Async.Sleep 50
                    count := !count + 1
                }
            ]
            start
        }

        let t1 = eng.Demand "build"
        System.Threading.Thread.Sleep 10
        let t2 = eng.Demand "build"
        System.Threading.Tasks.Task.WaitAll(t1, t2)
        eng.StopAsync().Wait()

        Assert.AreEqual(2, !count, "both Demands should execute for commands")

    [<Test>]
    member x.``no-dep phony and no-dep command both always execute``() =
        let phonyCount = ref 0
        let commandCount = ref 0

        let buildPhony () = xake {x.TestOptions with Targets = ["act"]; ResetDb = false} {
            rules [
                "act" => recipe {
                    phonyCount := !phonyCount + 1
                }
            ]
        }

        let buildCommand () = xake {x.TestOptions with Targets = ["act2"]; ResetDb = false} {
            rules [
                command "act2" {
                    commandCount := !commandCount + 1
                }
            ]
        }

        buildPhony ()
        buildPhony ()
        buildCommand ()
        buildCommand ()

        Assert.AreEqual(2, !phonyCount, "no-dep phony is non-pure and always executes")
        Assert.AreEqual(2, !commandCount, "no-dep command always executes")

    [<Test>]
    member x.``command with explicit alwaysRerun is not double-applied``() =
        let count = ref 0
        File.WriteAllText("src5.txt", "content")

        let build () = xake {x.TestOptions with Targets = ["build"]; ResetDb = false} {
            rules [
                command "build" {
                    do! need ["src5.txt"]
                    do! alwaysRerun()
                    count := !count + 1
                }
            ]
        }

        build ()
        build ()

        Assert.AreEqual(2, !count, "double alwaysRerun should be harmless")
