namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks

/// The order of the queues by key (docs/implnotes.md, "Critical path").
[<TestFixture>]
type ``Critical path queues``() =

    /// Grants of waiters queued in order with these keys, each releasing at once: their indices.
    let grants (acquire: int -> Async<unit>) (release: unit -> unit) (keys: int list) =
        let order = System.Collections.Concurrent.ConcurrentQueue<int>()
        acquire 0 |> Async.RunSynchronously   // the holder
        let waiters =
            keys |> List.mapi (fun i k ->
                Async.StartImmediateAsTask (async {
                    do! acquire k
                    order.Enqueue i
                    release () }))
        release ()
        System.Threading.Tasks.Task.WaitAll(waiters |> List.map (fun t -> t :> System.Threading.Tasks.Task) |> Array.ofList, 5000) |> ignore
        List.ofSeq order

    [<Test>]
    member _.``the gate gives a slot to the largest key, equal keys in arrival order``() =
        let gate = WorkerPool.WorkerPool.Gate 1
        Assert.AreEqual([1; 3; 2; 0; 4], grants gate.Acquire gate.Release [1; 5; 3; 5; 0])
        Assert.AreEqual([0; 1; 2; 3], grants gate.Acquire gate.Release [0; 0; 0; 0], "every key 0: FIFO")

    [<Test>]
    member _.``a resource grants the largest key first, equal keys in arrival order``() =
        let r = Resource.newResource "cp-order" 1
        let acquire k = Resource.acquireAt k r 1
        Assert.AreEqual([1; 3; 2; 0; 4], grants acquire (fun () -> Resource.release r 1) [1; 5; 3; 5; 0])
        Assert.AreEqual([0; 1; 2], grants acquire (fun () -> Resource.release r 1) [0; 0; 0], "every key 0: FIFO")

/// The acceptance graph of the brief, at 1 s = 40 ms: 40 tasks of one unit and a chain a (2) -> b
/// (20) with b requested last.
[<TestFixture>]
type ``Critical path``() =
    inherit XakeTestBase("critical-path")

    let unit = 40
    let shorts = [ for i in 0 .. 39 -> sprintf "s%02d" i ]
    let started = System.Collections.Concurrent.ConcurrentQueue<string>()
    let mark (name: string) = started.Enqueue name

    /// Runs the targets and returns the order in which bodies started, and the wall time.
    member private x.Run threads schedule (rs: ExecContext Rule list) =
        started.Clear()
        let watch = System.Diagnostics.Stopwatch.StartNew()
        xake { x.TestOptions with Threads = threads; Schedule = schedule; ThrowOnError = true } { rules rs }
        List.ofSeq started, watch.ElapsedMilliseconds

    member private x.Records () =
        (Storage.DbReader.readDatabase x.TestOptions.ProjectRoot ".xake").Status
        |> Map.toList |> List.map (fun (t, r) -> t.Name, (r.Targets, r.Depends, r.StaticCount))

    /// Phony tasks under the pool alone, `-t 4`.
    member private x.Phony : ExecContext Rule list = [
        yield "main" <== shorts @ [ "b" ]
        for s in shorts do
            yield s => recipe {
                mark s
                do! Async.Sleep unit }
        yield "a" => recipe {
            mark "a"
            do! Async.Sleep (2 * unit) }
        yield "b" => recipe {
            do! need [ "a" ]
            mark "b"
            do! Async.Sleep (20 * unit) } ]

    /// File tasks under one resource of 4, `-t 16`; outputs are removed before each run.
    member private x.Locked (lock: Resource) : ExecContext Rule list = [
        "main" <== (shorts |> List.map (sprintf "out/%s.txt")) @ [ "out/b.txt" ]
        "out/(n:s*).txt" ..> recipe {
            requires lock
            let! m = matched
            mark m?n
            do! Async.Sleep unit
            do! writeText "s" }
        "out/a.txt" ..> recipe {
            requires lock
            mark "a"
            do! Async.Sleep (2 * unit)
            do! writeText "a" }
        "out/b.txt" ..> recipe {
            needs "out/a.txt"
            requires lock
            mark "b"
            do! Async.Sleep (20 * unit)
            do! writeText "b" } ]

    member private x.Clean () = try Directory.Delete("out", true) with _ -> ()

    [<Test>]
    member x.``with history the chain starts in the first wave``() =
        let first, _ = x.Run 4 CriticalPath x.Phony
        Assert.That(List.findIndex ((=) "a") first, Is.GreaterThan 30, "no history: the order of request")
        let order, cp = x.Run 4 CriticalPath x.Phony
        // a starts in the first waves (as-requested: after 30); a slow runner may let a few more
        // shorts start before b's `need` queues it, so the bound leaves room (CI saw 8)
        Assert.That(List.findIndex ((=) "a") order, Is.LessThan 16, "a as soon as b asks for it")
        let order, asRequested = x.Run 4 AsRequested x.Phony
        Assert.That(List.findIndex ((=) "a") order, Is.GreaterThan 30, "as-requested")
        TestContext.WriteLine("critical-path {0} ms, as-requested {1} ms", cp, asRequested)
        Assert.That(cp, Is.LessThan asRequested, "the run is shorter")

    [<Test>]
    member x.``with history the chain gets the resource first``() =
        let lock = Resource.newResource "cp-lock" 4
        x.Clean ()
        x.Run 16 CriticalPath (x.Locked lock) |> ignore
        x.Clean ()
        let order, cp = x.Run 16 CriticalPath (x.Locked lock)
        let ia = List.findIndex ((=) "a") order
        // as above: the first waves, with room for a slow runner (as-requested: after 20)
        Assert.That(ia, Is.LessThan 16, "a among the first holders")
        // b waits for a (2 units); meanwhile the other 3 units serve about 6 short tasks
        Assert.That(List.findIndex ((=) "b") order, Is.LessThan (ia + 13), "b on the first unit after a")
        x.Clean ()
        let order, asRequested = x.Run 16 AsRequested (x.Locked lock)
        Assert.That(List.findIndex ((=) "a") order, Is.GreaterThan 20, "as-requested")
        TestContext.WriteLine("critical-path {0} ms, as-requested {1} ms", cp, asRequested)
        Assert.That(cp, Is.LessThan asRequested, "the run is shorter")

    [<Test>]
    member x.``the database records are the same in both modes``() =
        let lock = Resource.newResource "cp-records" 4
        let records schedule =
            try File.Delete ".xake" with _ -> ()
            x.Clean ()
            x.Run 16 schedule (x.Locked lock) |> ignore
            x.Clean ()
            x.Run 16 schedule (x.Locked lock) |> ignore
            x.Records ()
        Assert.AreEqual(records AsRequested, records CriticalPath)
