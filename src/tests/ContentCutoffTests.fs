namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks

/// Content cut-off (docs/content-cutoff.md), measured by counters, not timings.
[<TestFixture>]
type ``Content cut-off tests``() =
    inherit XakeTestBase("cutoff")

    /// stamps are compared with a 10 ms tolerance: keep edits well apart
    let edit (path: string) (text: string) =
        System.Threading.Thread.Sleep 100
        File.WriteAllText(path, text)

    /// a new write time, the same content
    let touch (path: string) =
        System.Threading.Thread.Sleep 100
        File.SetLastWriteTime(path, System.DateTime.Now)

    let count (c: int ref) = c.Value <- c.Value + 1

    /// the content without comment lines: a deterministic "compiler"
    let strip (path: string) =
        File.ReadAllLines path |> Array.filter (fun l -> not (l.StartsWith "//")) |> String.concat "\n"

    member val Last : RunSummary option = None with get, set

    member x.Run (targets: string list) (rs: Rule<ExecContext> list) =
        x.RunWith id targets rs

    member x.RunWith (f: ExecOptions -> ExecOptions) (targets: string list) (rs: Rule<ExecContext> list) =
        let sink = { new IRunSummarySink with member _.Report s = x.Last <- Some s }
        xake (f { x.TestOptions with Targets = targets; ResetDb = false; ThrowOnError = true; SummarySink = Some sink }) { rules rs }
        x.Last.Value

    member x.Files () = (Storage.Db.readDatabase x.TestOptions.ProjectRoot ".xake").Files

    /// src -> out/X.o (comments stripped) -> out/X.app (needs the .o in its header)
    member x.Chain (name: string) (oRuns: int ref) (aRuns: int ref) =
        [ ("out/" + name + ".o") ..> recipe {
                do! need [ name + ".c" ]
                count oRuns
                do! writeText (strip (name + ".c"))
            }
          ("out/" + name + ".app") ..> recipe {
                needs ("out/" + name + ".o")
                count aRuns
                do! writeText (File.ReadAllText ("out/" + name + ".o"))
            } ]

    [<Test>]
    member x.``an output rebuilt with the same bytes does not rerun its dependent``() =
        let oRuns, aRuns = ref 0, ref 0
        File.WriteAllText("same.c", "// v1\nint x;")
        let rs = x.Chain "same" oRuns aRuns
        let first = x.Run [ "out/same.app" ] rs
        Assert.AreEqual(0, first.CutOff)
        edit "same.c" "// v2, a comment only\nint x;"
        let s = x.Run [ "out/same.app" ] rs
        Assert.AreEqual((2, 1), (oRuns.Value, aRuns.Value))
        Assert.AreEqual(1, s.CutOff)
        edit "same.c" "// v2\nint y;"
        let s = x.Run [ "out/same.app" ] rs
        Assert.AreEqual((3, 2), (oRuns.Value, aRuns.Value), "different bytes rerun the dependent")
        Assert.AreEqual(0, s.CutOff)

    [<Test>]
    member x.``a touched source reruns its dependent once, then never; a no-change run hashes nothing``() =
        let oRuns, aRuns = ref 0, ref 0
        File.WriteAllText("touch.c", "int x;")
        let rs = x.Chain "touch" oRuns aRuns
        let clean = x.Run [ "out/touch.app" ] rs
        Assert.AreEqual(2, clean.FilesHashed, "a clean build hashes its outputs only")
        touch "touch.c"
        x.Run [ "out/touch.app" ] rs |> ignore
        Assert.AreEqual((2, 1), (oRuns.Value, aRuns.Value), "first move: no hash yet; the .o is rebuilt to the same bytes")
        touch "touch.c"
        let s = x.Run [ "out/touch.app" ] rs
        Assert.AreEqual((2, 1), (oRuns.Value, aRuns.Value), "second move: the same hash, nothing reruns")
        Assert.AreEqual(0, s.TargetsRun)
        let s = x.Run [ "out/touch.app" ] rs
        Assert.AreEqual(0, s.FilesHashed)
        Assert.AreEqual(0, s.TasksScheduled, "a run where nothing moved schedules no task")

    [<Test>]
    member x.``a restored output with a fresh time and a known hash reruns nothing``() =
        let oRuns, aRuns = ref 0, ref 0
        File.WriteAllText("rest.c", "int x;")
        let rs = x.Chain "rest" oRuns aRuns
        x.Run [ "out/rest.app" ] rs |> ignore
        let saved = File.ReadAllText "out/rest.o"
        File.Delete "out/rest.o"
        edit "out/rest.o" saved
        let s = x.Run [ "out/rest.app" ] rs
        Assert.AreEqual((1, 1), (oRuns.Value, aRuns.Value))
        Assert.AreEqual(0, s.TargetsRun)

    [<Test>]
    member x.``a hand-edited output reruns its dependents, not its producer``() =
        let oRuns, aRuns = ref 0, ref 0
        File.WriteAllText("hand.c", "int x;")
        let rs = x.Chain "hand" oRuns aRuns
        x.Run [ "out/hand.app" ] rs |> ignore
        edit "out/hand.o" "edited by hand"
        x.Run [ "out/hand.app" ] rs |> ignore
        Assert.AreEqual((1, 2), (oRuns.Value, aRuns.Value))
        Assert.AreEqual("edited by hand", File.ReadAllText "out/hand.app")

    [<Test>]
    member x.``a dynamic need of an out-of-date target reruns the dependent; nothing is built to decide``() =
        let oRuns, aRuns = ref 0, ref 0
        File.WriteAllText("dyn.c", "// v1\nint x;")
        let rs =
            [ "out/dyn.o" ..> recipe {
                    do! need [ "dyn.c" ]
                    count oRuns
                    do! writeText (strip "dyn.c")
                }
              "out/dyn.app" ..> recipe {
                    do! need [ "out/dyn.o" ]
                    count aRuns
                    do! writeText (File.ReadAllText "out/dyn.o")
                } ]
        x.Run [ "out/dyn.app" ] rs |> ignore
        edit "dyn.c" "// v2\nint x;"
        x.Run [ "out/dyn.app" ] rs |> ignore
        Assert.AreEqual((2, 2), (oRuns.Value, aRuns.Value), "option B: the dependent reruns and its body builds the .o")

    [<Test>]
    member x.``with --content-cutoff=off a touched source reruns everything and nothing is hashed``() =
        let oRuns, aRuns = ref 0, ref 0
        File.WriteAllText("off.c", "int x;")
        let rs = x.Chain "off" oRuns aRuns
        let off (o: ExecOptions) = { o with ContentCutoff = false }
        let s = x.RunWith off [ "out/off.app" ] rs
        Assert.AreEqual(0, s.FilesHashed)
        touch "off.c"
        x.RunWith off [ "out/off.app" ] rs |> ignore
        touch "off.c"
        let s = x.RunWith off [ "out/off.app" ] rs
        Assert.AreEqual((3, 3), (oRuns.Value, aRuns.Value))
        Assert.AreEqual(0, s.FilesHashed)
        Assert.IsTrue(x.Files () |> Map.forall (fun _ st -> st.Hash.IsNone), "an off run stores no hash")
        // the next `on` run starts from there: a no-change run rebuilds nothing
        let s = x.Run [ "out/off.app" ] rs
        Assert.AreEqual(0, s.TargetsRun)

    [<Test>]
    member x.``an off run clears the hash of a moved file``() =
        let oRuns, aRuns = ref 0, ref 0
        File.WriteAllText("clear.c", "int x;")
        let rs = x.Chain "clear" oRuns aRuns
        x.Run [ "out/clear.app" ] rs |> ignore
        touch "clear.c"
        x.Run [ "out/clear.app" ] rs |> ignore
        Assert.IsTrue((x.Files ()).[File.make "clear.c"].Hash.IsSome)
        touch "clear.c"
        x.RunWith (fun (o: ExecOptions) -> { o with ContentCutoff = false }) [ "out/clear.app" ] rs |> ignore
        Assert.IsTrue((x.Files ()).[File.make "clear.c"].Hash.IsNone)

    [<Test>]
    member x.``noContentCutoff: the output is never hashed and every rebuild reruns the dependents``() =
        let oRuns, aRuns = ref 0, ref 0
        File.WriteAllText("nc.c", "// v1\nint x;")
        let rs =
            [ "out/nc.o" ..> recipe {
                    noContentCutoff
                    do! need [ "nc.c" ]
                    count oRuns
                    do! writeText (strip "nc.c")
                }
              "out/nc.app" ..> recipe {
                    needs "out/nc.o"
                    count aRuns
                    do! writeText (File.ReadAllText "out/nc.o")
                } ]
        x.Run [ "out/nc.app" ] rs |> ignore
        edit "nc.c" "// v2\nint x;"
        x.Run [ "out/nc.app" ] rs |> ignore
        Assert.AreEqual((2, 2), (oRuns.Value, aRuns.Value))
        Assert.IsTrue((x.Files ()).[File.make "out/nc.o"].Hash.IsNone)

    [<Test>]
    member x.``a database without hashes (an off run) gets cut-off from the first rebuild (pre-hash)``() =
        let oRuns, aRuns = ref 0, ref 0
        File.WriteAllText("pre.c", "// v1\nint x;")
        let rs = x.Chain "pre" oRuns aRuns
        x.RunWith (fun (o: ExecOptions) -> { o with ContentCutoff = false }) [ "out/pre.app" ] rs |> ignore
        Assert.IsTrue((x.Files ()).[File.make "out/pre.o"].Hash.IsNone)
        edit "pre.c" "// v2\nint x;"
        let s = x.Run [ "out/pre.app" ] rs
        Assert.AreEqual((2, 1), (oRuns.Value, aRuns.Value))
        Assert.AreEqual(1, s.CutOff)

    [<Test>]
    member x.``a dependee being built is waited for and compared``() =
        let dRuns, kRuns = ref 0, ref 0
        File.WriteAllText("wait.c", "// v1\nint x;")
        let rs =
            [ "out/wait.d" ..> recipe {
                    do! need [ "wait.c" ]
                    count dRuns
                    System.Threading.Thread.Sleep 1000
                    do! writeText (strip "wait.c")
                }
              "out/wait.slow" ..> recipe {
                    System.Threading.Thread.Sleep 300
                    do! writeText "slow"
                }
              "out/wait.k" ..> recipe {
                    needs "out/wait.slow"
                    count kRuns
                    do! need [ "out/wait.d" ]
                    do! writeText (File.ReadAllText "out/wait.d")
                } ]
        let threads4 (o: ExecOptions) = { o with Threads = 4 }
        x.RunWith threads4 [ "out/wait.d;out/wait.k" ] rs |> ignore
        Assert.AreEqual((1, 1), (dRuns.Value, kRuns.Value))
        edit "wait.c" "// v2\nint x;"
        File.Delete "out/wait.slow"
        x.RunWith threads4 [ "out/wait.d;out/wait.k" ] rs |> ignore
        Assert.AreEqual((2, 1), (dRuns.Value, kRuns.Value), "k waited for d and saw the same content")

    [<Test>]
    member x.``a dependency reversed since the last run does not hang the verdict wait``() =
        let dRuns, kRuns = ref 0, ref 0
        File.WriteAllText("rev.c", "v1")
        let first =
            [ "out/rev.d" ..> recipe {
                    do! need [ "rev.c" ]
                    count dRuns
                    do! writeText (File.ReadAllText "rev.c")
                }
              "out/rev.k" ..> recipe {
                    count kRuns
                    do! need [ "out/rev.d" ]
                    do! writeText "k"
                } ]
        x.RunWith (fun (o: ExecOptions) -> { o with Threads = 4 }) [ "out/rev.k" ] first |> ignore
        // now d needs k, and k no longer needs d
        let second =
            [ "out/rev.d" ..> recipe {
                    do! need [ "rev.c" ]
                    System.Threading.Thread.Sleep 300
                    do! need [ "out/rev.k" ]
                    count dRuns
                    do! writeText (File.ReadAllText "rev.c")
                }
              "out/rev.k" ..> recipe {
                    count kRuns
                    do! writeText "k2"
                } ]
        edit "rev.c" "v2"
        let run = System.Threading.Tasks.Task.Run(fun () -> x.RunWith (fun (o: ExecOptions) -> { o with Threads = 4 }) [ "out/rev.d;out/rev.k" ] second |> ignore)
        Assert.IsTrue(run.Wait(System.TimeSpan.FromSeconds 30.0), "the run finished")
        Assert.AreEqual((2, 2), (dRuns.Value, kRuns.Value))

    [<Test>]
    member x.``onSummary receives the counters of the run``() =
        let oRuns, aRuns = ref 0, ref 0
        File.WriteAllText("sum.c", "int x;")
        let rs = x.Chain "sum" oRuns aRuns
        let s = x.Run [ "out/sum.app" ] rs
        Assert.AreEqual(2, s.TargetsRun)
        Assert.AreEqual(2, s.TasksScheduled)
        let s = x.Run [ "out/sum.app" ] rs
        Assert.AreEqual((0, 1, 0), (s.TargetsRun, s.TargetsSkipped, s.TasksScheduled))
