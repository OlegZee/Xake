namespace Tests

open System.IO
open System.Threading.Tasks
open NUnit.Framework

open Xake

/// `need` of a target that has a rule while a resource is held fails at once (hold-and-wait
/// can deadlock the build, docs/resources.md); plain files and `allowNeed` are allowed.
[<TestFixture>]
type ``Resource need guard``() =
    inherit XakeTestBase("resource-guard")

    /// Runs the script under a timeout, so a regression fails instead of hanging; returns
    /// whether it failed and the log.
    member private x.Run (label: string) (threads: int) (rules': Rule<ExecContext> list) =
        let log = label + ".log"
        let build =
            Task.Run (fun () ->
                try
                    xake {x.TestOptions with FileLog = log; FileLogLevel = Verbosity.Diag; ThrowOnError = true; Threads = threads} {
                        wantOverride ["main"]
                        rules rules'
                    }
                    false
                with _ -> true)
        if not (build.Wait 60000) then Assert.Fail (sprintf "'%s' hung" label)
        build.Result, File.ReadAllText log

    [<Test>]
    member x.``need of a rule target inside withResource fails, naming the resource and both targets``() =
        let r = Resource.newResource "guarded" 1
        let failed, log =
            x.Run "inside" 2 [
                "dep" => recipe { () }
                "main" => withResource r 1 (recipe { do! need ["dep"] })
            ]
        Assert.That(failed, Is.True)
        Assert.That(log, Does.Contain "need inside withResource: target 'main' needs 'dep' while it holds the resource 'guarded'")
        Assert.That(log, Does.Contain "allowNeed")

    [<Test>]
    member x.``need of a plain file inside withResource is allowed``() =
        File.WriteAllText ("plain.txt", "x")
        let r = Resource.newResource "plain" 1
        let failed, _ = x.Run "plain" 2 [ "main" => withResource r 1 (recipe { do! need ["plain.txt"] }) ]
        Assert.That(failed, Is.False)

    [<Test>]
    member x.``need before withResource and need under allowNeed pass``() =
        let r = Resource.newResource "allowed" 1
        let ran = ref 0
        let failed, _ =
            x.Run "allowed" 2 [
                "dep1" => recipe { ran.Value <- ran.Value + 1 }
                "dep2" => recipe { ran.Value <- ran.Value + 1 }
                "main" => recipe {
                    do! need ["dep1"]
                    do! withResource r 1 (allowNeed (recipe { do! need ["dep2"] }))
                }
            ]
        Assert.That(failed, Is.False)
        Assert.That(ran.Value, Is.EqualTo 2)

    [<Test>]
    member x.``a requiring rule that needs a rule target fails``() =
        let r = Resource.newResource "lock" 1
        let failed, log =
            x.Run "requiring" 2 [
                "dep" => recipe { () }
                "main" => recipe { do! need ["dep"] } |> requiring r
            ]
        Assert.That(failed, Is.True)
        Assert.That(log, Does.Contain "target 'main' needs 'dep' while it holds the resource 'lock'")

    [<Test>]
    member x.``nested withResource names every held resource``() =
        let outer = Resource.newResource "outer" 1
        let inner = Resource.newResource "inner" 1
        let failed, log =
            x.Run "nested" 2 [
                "dep" => recipe { () }
                "main" => withResource outer 1 (withResource inner 1 (recipe { do! need ["dep"] }))
            ]
        Assert.That(failed, Is.True)
        Assert.That(log, Does.Contain "holds the resource 'inner', 'outer'")

    [<Test>]
    member x.``a task started under allowNeed does not inherit the held resource``() =
        // allowNeed clears the guard for the needing recipe only; the needed task starts with
        // nothing held, so its own need of a rule target is fine
        let r = Resource.newResource "parent" 1
        let failed, _ =
            x.Run "child" 2 [
                "leaf" => recipe { () }
                "dep" => recipe { do! need ["leaf"] }
                "main" => withResource r 1 (allowNeed (recipe { do! need ["dep"] }))
            ]
        Assert.That(failed, Is.False)

    [<Test>]
    member x.``the incident shape fails fast instead of hanging``() =
        // a chain of compiles, each needing the previous one inside a resource of 2: hung
        // with every unit held by a recipe waiting for a queued dependency
        let r = Resource.newResource "chain" 2
        let failed, log =
            x.Run "chain" 4 [
                "c0" => withResource r 1 (recipe { () })
                for i in 1..10 do
                    sprintf "c%d" i => withResource r 1 (recipe { do! need [sprintf "c%d" (i - 1)] })
                "main" => recipe { do! need ["c10"] }
            ]
        Assert.That(failed, Is.True)
        Assert.That(log, Does.Contain "need inside withResource")
