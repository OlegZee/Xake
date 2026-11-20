module ``Verdicts of a run``

// The async verdict and its memo per run (docs/features/idle-perf/design.md, L6).

open System.IO
open NUnit.Framework

open Xake
open Xake.Storage
open Xake.FileProbe
open Xake.FileTracker
open Xake.DependencyAnalysis

let private newContext () =
    let options = { EngineOptions.Default with ProjectRoot = Directory.GetCurrentDirectory() }
    ExecCore.createContext options []

let private store (ctx: ExecContext) result = ctx.Db.PostAndReply(fun ch -> Store (result, ch))

let private phony name deps =
    { makeResult [ PhonyAction name ] with Depends = deps; Epoch = 1 }

/// Two entries into one cycle of records at once: neither waits for the other; the edge that
/// would close the cycle counts as a change.
[<Test>]
let ``two entries into one cycle finish``() =
    let ctx, finalize = newContext ()
    try
        store ctx (phony "a" [ TargetDep (PhonyAction "b") ])
        store ctx (phony "b" [ TargetDep (PhonyAction "a") ])
        for _ in 1 .. 20 do
            let verdicts = Verdicts(ctx, fun _ -> None)
            let warm = verdicts.Warm [ PhonyAction "a"; PhonyAction "b" ] |> Async.StartAsTask
            Assert.IsTrue(warm.Wait 10000, "the warm walk finished")
            let a, b = verdicts.Ready (PhonyAction "a"), verdicts.Ready (PhonyAction "b")
            Assert.IsTrue(a.IsSome && b.IsSome)
            Assert.IsTrue(not a.Value.IsEmpty || not b.Value.IsEmpty, "the refused edge counts as a change")
    finally finalize ()

/// A target that depends on itself through its record does not wait for itself.
[<Test>]
let ``a target depending on itself finishes``() =
    let ctx, finalize = newContext ()
    try
        store ctx (phony "self" [ TargetDep (PhonyAction "self") ])
        let verdicts = Verdicts(ctx, fun _ -> None)
        let task = verdicts.Of (PhonyAction "self") |> Async.StartAsTask
        Assert.IsTrue(task.Wait 10000)
    finally finalize ()

/// `Estimate` reads the verdicts `Warm` decided: the last own time of a stale target, nothing for
/// one not warmed or up to date (whatever the schedule: `planRun` warms in both).
[<Test>]
let ``the estimate reads the warmed verdicts``() =
    let ctx, finalize = newContext ()
    try
        let out = FileTarget (File.make "est.o")
        let step = { StepInfo.Empty with Name = "all"; OwnTime = 300<ms> }
        store ctx { makeResult [ out ] with Depends = [ FileDep (File.make "no-such-source.c") ]; Epoch = 1; Steps = [ step ] }
        let verdicts = Verdicts(ctx, fun _ -> None)
        Assert.AreEqual((0<ms>, ([]: Target list)), verdicts.Estimate out, "not warmed")
        verdicts.Warm [ out ] |> Async.RunSynchronously
        Assert.AreEqual((300<ms>, ([]: Target list)), verdicts.Estimate out)
    finally finalize ()

/// The wait-for primitive both maps use (docs/features/arch-review/wait-for.md).
[<Test>]
let ``a wait-for edge closing a cycle is refused``() =
    let g = WaitFor<unit>()
    Assert.IsTrue(g.TryAdd [ "a" ] "b" ())
    Assert.IsTrue(g.TryAdd [ "b" ] "c" ())
    Assert.IsFalse(g.TryAdd [ "c" ] "a" (), "c -> a closes a -> b -> c")
    Assert.IsFalse(g.TryAdd [ "x"; "c" ] "a" (), "one waiter closing a cycle refuses all")
    Assert.IsFalse(g.TryAdd [ "a" ] "a" (), "a self-wait is a cycle")
    Assert.IsTrue(g.TryAdd [ "a" ] "c" ())

[<Test>]
let ``a removed wait-for edge no longer closes a cycle``() =
    let g = WaitFor<unit>()
    g.Add [ "a" ] "b" ()
    g.Add [ "a" ] "b" ()
    g.Remove [ "a" ] "b"
    Assert.IsFalse(g.TryAdd [ "b" ] "a" (), "one of two equal edges is left")
    g.Remove [ "a" ] "b"
    Assert.IsTrue(g.TryAdd [ "b" ] "a" ())
    Assert.AreEqual([ "b", "a", () ], g.Edges |> List.ofSeq)
