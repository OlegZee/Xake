namespace Tests

open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open NUnit.Framework
open Xake

[<TestFixture>]
type ``Delegated and resource tests``() =

    // Builds a long-lived engine with an explicit thread (CPU slot) limit and no persistence.
    let engineBuilder threads =
        RulesBuilder
            { ExecOptions.Default with
                Threads = threads
                IgnoreCommandLine = true
                NoPersist = true
                Progress = false
                ConLogLevel = Silent
                FileLogLevel = Silent }

    // A concurrency meter: enter()/leave() bracket a region; peak records the max overlap.
    let meter () =
        let current = ref 0
        let peak = ref 0
        let gate = obj()
        let enter () =
            let n = Interlocked.Increment current
            lock gate (fun () -> if n > !peak then peak := n)
        let leave () = Interlocked.Decrement current |> ignore
        enter, leave, peak

    let emptyResult targets : BuildResult =
        { Targets = targets; Built = System.DateTime.Now; Depends = []; Steps = [] }

    // An in-memory fake of Qualhalla's delegated executor: a run-scoped result store keyed
    // by a fixed identity, with in-flight dedup. Returns (executor, bodyRunCount, store, key).
    let fakeExecutor () =
        let key = "shared-identity"
        let store = ConcurrentDictionary<string, BuildResult>()
        let inFlight = ConcurrentDictionary<string, Lazy<Task<BuildResult>>>()
        let bodyRunCount = ref 0
        let executor : DelegatedExecutor<ExecContext> =
            fun _ctx _targets body ->
                async {
                    match store.TryGetValue key with
                    | true, result -> return result                  // cache hit: body never runs
                    | _ ->
                        let fresh = lazy ((async {
                                            let! result = body ()    // miss: run the work once
                                            store.[key] <- result
                                            return result
                                          }) |> Async.StartAsTask)
                        let task = inFlight.GetOrAdd(key, fresh).Value  // concurrent demands share it
                        let! result = task |> Async.AwaitTask
                        inFlight.TryRemove key |> ignore
                        return result
                }
        executor, bodyRunCount, store, key

    [<Test>]
    member _.``bounded resource caps concurrency``() =
        let r = Resource.newResource "db" 2
        let enter, leave, peak = meter ()
        let runCount = ref 0

        let body = recipe {
            enter ()
            do! Async.Sleep 80
            leave ()
            Interlocked.Increment runCount |> ignore
        }

        let b = engineBuilder 8   // plenty of CPU slots; the resource must be the limiter
        let eng = b {
            rules [ for i in 1..8 -> (sprintf "t%d" i) => withResource r 1 body ]
            start
        }

        let tasks = [| for i in 1..8 -> eng.Demand (sprintf "t%d" i) |]
        Task.WaitAll tasks
        eng.StopAsync().Wait()

        Assert.LessOrEqual(!peak, 2, "resource of quantity 2 must cap concurrency at 2")
        Assert.AreEqual(8, !runCount, "every target must still run")

    [<Test>]
    member _.``unbounded resource does not cap concurrency``() =
        let r = Resource.newUnbounded "db"
        let enter, leave, peak = meter ()

        let body = recipe {
            enter ()
            do! Async.Sleep 200
            leave ()
        }

        let b = engineBuilder 8
        let eng = b {
            rules [ for i in 1..8 -> (sprintf "t%d" i) => withResource r 1 body ]
            start
        }

        let tasks = [| for i in 1..8 -> eng.Demand (sprintf "t%d" i) |]
        Task.WaitAll tasks
        eng.StopAsync().Wait()

        Assert.AreEqual(8, !peak, "an unbounded resource must not throttle")

    [<Test>]
    member _.``detached rules run far beyond CPU slot count``() =
        // Only 2 CPU slots, but 40 detached (I/O-bound) rules must overlap.
        let n = 40
        let enter, leave, peak = meter ()

        let body = recipe {
            do! runDetached (recipe {
                enter ()
                do! Async.Sleep 150
                leave ()
            })
        }

        let b = engineBuilder 2
        let eng = b {
            rules [ for i in 1..n -> (sprintf "t%d" i) => body ]
            start
        }

        let tasks = [| for i in 1..n -> eng.Demand (sprintf "t%d" i) |]
        Task.WaitAll tasks
        eng.StopAsync().Wait()

        Assert.GreaterOrEqual(!peak, 20,
            sprintf "with 2 CPU slots, detached rules should still overlap heavily (peak was %d)" !peak)

    [<Test>]
    member _.``delegated rules are never bounded by the CPU thread pool``() =
        // A delegated rule must NOT consume a local CPU slot — the whole task runs
        // detached. With only 2 CPU slots and no dispatch budget, all 16 delegated
        // rules must still overlap. (No runDetached in the body — it is redundant here.)
        let n = 16
        let enter, leave, peak = meter ()

        // Trivial pass-through executor: just runs the body (always a "miss").
        let executor : DelegatedExecutor<ExecContext> = fun _ctx _targets runBody -> runBody ()

        let body = recipe {
            enter ()
            do! Async.Sleep 200
            leave ()
        }

        let b = engineBuilder 2     // only 2 CPU slots
        let eng = b {
            rules [ for i in 1..n -> (sprintf "dt%d" i => body) |> delegated executor ]
            start
        }

        let tasks = [| for i in 1..n -> eng.Demand (sprintf "dt%d" i) |]
        Task.WaitAll tasks
        eng.StopAsync().Wait()

        Assert.AreEqual(n, !peak,
            sprintf "delegated rules must not be capped by CPU slots (peak was %d, expected %d)" !peak n)

    [<Test>]
    member _.``delegated cache miss runs the body exactly once``() =
        let executor, bodyRunCount, store, key = fakeExecutor ()

        let body = recipe {
            Interlocked.Increment bodyRunCount |> ignore
            do! Async.Sleep 30
        }

        let b = engineBuilder 4
        let eng = b {
            rule (("dt" => body) |> delegated executor)
            start
        }

        eng.Demand("dt").Wait()
        eng.StopAsync().Wait()

        Assert.AreEqual(1, !bodyRunCount, "a miss must run the body once")
        Assert.IsTrue(store.ContainsKey key, "a miss must publish the result to the store")

    [<Test>]
    member _.``concurrent delegated demands for one identity deduplicate to a single execution``() =
        let executor, bodyRunCount, _store, _key = fakeExecutor ()

        // Two distinct targets that map to the SAME identity key inside the executor.
        let body = recipe {
            Interlocked.Increment bodyRunCount |> ignore
            do! Async.Sleep 150
        }

        let b = engineBuilder 4
        let eng = b {
            rules [
                ("dt1" => body) |> delegated executor
                ("dt2" => body) |> delegated executor
            ]
            start
        }

        let t1 = eng.Demand "dt1"
        let t2 = eng.Demand "dt2"
        Task.WaitAll(t1, t2)
        eng.StopAsync().Wait()

        Assert.AreEqual(1, !bodyRunCount,
            "concurrent demands sharing an identity must dedup to one body execution")

    [<Test>]
    member _.``delegated cache hit returns stored result without running the body``() =
        let executor, bodyRunCount, store, key = fakeExecutor ()
        store.[key] <- emptyResult [PhonyAction "dt"]   // pre-seed: every demand is now a hit

        let body = recipe {
            Interlocked.Increment bodyRunCount |> ignore
            do! Async.Sleep 30
        }

        let b = engineBuilder 4
        let eng = b {
            rule (("dt" => body) |> delegated executor)
            start
        }

        eng.Demand("dt").Wait()
        eng.StopAsync().Wait()

        Assert.AreEqual(0, !bodyRunCount, "a cache hit must not run the body")

    [<Test>]
    member _.``resize increases resource capacity and unblocks pending acquires``() =
        let r = Resource.newResource "resizable" 1

        // Acquire the single available unit
        Resource.acquire r 1 |> Async.RunSynchronously

        // Resize to 2 -- this should free one additional unit
        Resource.resize r 2

        // A second acquire must now succeed without blocking
        let completed =
            Resource.acquire r 1
            |> fun a -> Async.StartAsTask(a, cancellationToken = (new CancellationTokenSource(1000)).Token)
        completed.Wait()

        Assert.IsTrue(completed.IsCompletedSuccessfully, "second acquire should succeed after resize")

        // Cleanup
        Resource.release r 1
        Resource.release r 1
