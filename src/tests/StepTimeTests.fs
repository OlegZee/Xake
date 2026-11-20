namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks

/// Step times (docs/implnotes.md, "Step times"): waiting for a resource or for the header's
/// `needs` is `WaitTime`, never `OwnTime`; and the target groups of the command line.
[<TestFixture>]
type ``Step times``() =
    inherit XakeTestBase("step-times")

    let sleep (ms: int) = recipe { do! Async.Sleep ms }

    member private x.Build (threads: int) (targets: string list) (rs: ExecContext Rule list) =
        xake { x.TestOptions with Targets = targets; Threads = threads; ThrowOnError = true } { rules rs }

    member private x.Step (target: Target) =
        match (Storage.Db.readDatabase x.TestOptions.ProjectRoot ".xake").Status.[target].Steps with
        | step :: _ -> step
        | [] -> failwith "no step"

    /// Two bodies of 300 ms under a one-unit lock, side by side: one waits about 300 ms, and that
    /// wait is not its own time.
    member private x.CheckQueued (a: Target, b: Target) =
        let sa, sb = x.Step a, x.Step b
        let waiting = if sa.WaitTime > sb.WaitTime then sa else sb
        Assert.That(waiting.WaitTime, Is.GreaterThanOrEqualTo 200, "the wait for the lock")
        for s in [ sa; sb ] do
            Assert.That(s.OwnTime, Is.InRange(250, 450), sprintf "%s: the body only" s.Name)

    [<Test>]
    member x.``waiting in withResource is wait time``() =
        let lock = Resource.newResource "steps-with" 1
        x.Build 2 [ "main" ] [
            "main" <== [ "a"; "b" ]
            "a" => withResource lock 1 (sleep 300)
            "b" => withResource lock 1 (sleep 300) ]
        x.CheckQueued (PhonyAction "a", PhonyAction "b")

    [<Test>]
    member x.``waiting for requiring is wait time``() =
        let lock = Resource.newResource "steps-requiring" 1
        x.Build 2 [ "main" ] [
            "main" <== [ "a"; "b" ]
            "a" => sleep 300 |> requiring lock
            "b" => sleep 300 |> requiring lock ]
        x.CheckQueued (PhonyAction "a", PhonyAction "b")

    [<Test>]
    member x.``waiting for a lock of the header is wait time``() =
        let lock = Resource.newResource "steps-requires" 1
        x.Build 2 [ "main" ] [
            "main" <== [ "out/a.txt"; "out/b.txt" ]
            "out/(n:*).txt" ..> recipe {
                requires lock
                do! Async.Sleep 300
                do! writeText "x" } ]
        x.CheckQueued (FileTarget (File.make "out/a.txt"), FileTarget (File.make "out/b.txt"))

    [<Test>]
    member x.``the header's needs are wait time, inside the step``() =
        x.Build 2 [ "out/c.txt" ] [
            "slow" => sleep 300
            "out/c.txt" ..> recipe {
                needs "slow"
                do! Async.Sleep 50
                do! writeText "c" } ]
        let s = x.Step (FileTarget (File.make "out/c.txt"))
        Assert.That(s.WaitTime, Is.GreaterThanOrEqualTo 280, "the needs")
        Assert.That(s.OwnTime, Is.InRange(40, 200), "the body only")

    [<Test>]
    member x.``each of several target groups logs its time; one group does not``() =
        let messages = System.Collections.Concurrent.ConcurrentQueue<string>()
        let run targets =
            messages.Clear()
            xake { x.TestOptions with
                    Targets = targets; ThrowOnError = true
                    CustomLogger = CustomLogger (fun l -> l = Level.Message) messages.Enqueue } {
                rules [ "a" => sleep 10; "b" => sleep 10 ] }
            messages |> Seq.filter (fun m -> m.Contains "group '") |> List.ofSeq
        let several = run [ "a"; "b" ]
        Assert.That(several.Length, Is.EqualTo 2, sprintf "%A" several)
        Assert.That(several.[0], Does.Contain "group 'a' done in")
        Assert.That(several.[1], Does.Contain "group 'b' done in")
        Assert.That(run [ "a;b" ], Is.Empty)
