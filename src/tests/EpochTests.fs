namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks

/// The verdict by epochs (decision 1c): a run number per run, a file's `changed`, a result's `Epoch`.
[<TestFixture>]
type ``Epoch tests``() =
    inherit XakeTestBase("epochs")

    /// stamps are compared with a 10 ms tolerance: keep edits well apart
    let edit (path: string) (text: string) =
        System.Threading.Thread.Sleep 100
        File.WriteAllText(path, text)

    let count (c: int ref) = c.Value <- c.Value + 1

    member x.Run (targets: string list) (rs: Rule<ExecContext> list) =
        xake { x.TestOptions with Targets = targets; ResetDb = false } { rules rs }

    member x.Results () =
        Storage.readDatabase x.TestOptions.ProjectRoot ".xake"

    [<Test>]
    member x.``a no-change run rebuilds nothing and appends nothing; a source edit rebuilds the chain``() =
        let oRuns, aRuns = ref 0, ref 0
        File.WriteAllText("nc.c", "v1")
        let rs =
            [ "out/nc.o" ..> recipe {
                    do! need [ "nc.c" ]
                    count oRuns
                    do! writeText (File.ReadAllText "nc.c")
                }
              "out/nc.app" ..> recipe {
                    do! need [ "out/nc.o" ]
                    count aRuns
                    do! writeText (File.ReadAllText "out/nc.o")
                } ]
        x.Run [ "out/nc.app" ] rs
        let length = (FileInfo ".xake").Length
        x.Run [ "out/nc.app" ] rs
        Assert.AreEqual((1, 1), (oRuns.Value, aRuns.Value))
        Assert.AreEqual(length, (FileInfo ".xake").Length, "a no-change run appends nothing")
        edit "nc.c" "v2"
        x.Run [ "out/nc.app" ] rs
        Assert.AreEqual((2, 2), (oRuns.Value, aRuns.Value))
        Assert.AreEqual("v2", File.ReadAllText "out/nc.app")
        let db = x.Results ()
        let last = db.Epoch
        let app = db.Status.[FileTarget (File.make "out/nc.app")]
        Assert.AreEqual(last, app.Epoch, "the rebuild has the run's epoch")

    [<Test>]
    member x.``a dynamic need of a rebuilt target and of a phony does not rerun the dependent next time``() =
        let kRuns, dRuns, pRuns = ref 0, ref 0, ref 0
        File.WriteAllText("dyn.c", "v1")
        File.WriteAllText("dyn.p", "p1")
        let rs =
            [ "out/dyn.k" ..> recipe {
                count kRuns
                do! need [ "out/dyn.d" ]
                do! need [ "gen" ]
                do! writeText (File.ReadAllText "out/dyn.d") }
              "out/dyn.d" ..> recipe {
                    do! need [ "dyn.c" ]
                    count dRuns
                    do! writeText (File.ReadAllText "dyn.c")
                }
              "gen" => recipe {
                    do! need [ "dyn.p" ]
                    count pRuns
                } ]
        x.Run [ "out/dyn.k" ] rs
        edit "dyn.c" "v2"
        edit "dyn.p" "p2"
        x.Run [ "out/dyn.k" ] rs
        Assert.AreEqual((2, 2, 2), (kRuns.Value, dRuns.Value, pRuns.Value))
        x.Run [ "out/dyn.k" ] rs
        x.Run [ "out/dyn.k" ] rs
        Assert.AreEqual((2, 2, 2), (kRuns.Value, dRuns.Value, pRuns.Value), "no rerun after a dynamic need")

    [<Test>]
    member x.``a source saved during the build after its observation reruns its dependents next time``() =
        let runs = ref 0
        File.WriteAllText("during.c", "v1")
        let rs =
            [ "out/during.o" ..> recipe {
                do! need [ "during.c" ]
                count runs
                let text = File.ReadAllText "during.c"
                if runs.Value = 1 then edit "during.c" "v2"      // saved while the rule runs
                do! writeText text } ]
        x.Run [ "out/during.o" ] rs
        Assert.AreEqual("v1", File.ReadAllText "out/during.o")
        x.Run [ "out/during.o" ] rs
        Assert.AreEqual(2, runs.Value)
        Assert.AreEqual("v2", File.ReadAllText "out/during.o")
        x.Run [ "out/during.o" ] rs
        Assert.AreEqual(2, runs.Value)

    [<Test>]
    member x.``engine mode: two runs get consecutive epochs``() =
        let engine =
            RulesBuilder { x.TestOptions with IgnoreCommandLine = true; ResetDb = false; Progress = false } {
                phony "always" (recipe { do! alwaysRerun () })
                start
            }
        engine.Demand("always").Wait()
        let first = (x.Results ()).Status.[PhonyAction "always"].Epoch
        engine.Demand("always").Wait()
        let second = (x.Results ()).Status.[PhonyAction "always"].Epoch
        engine.StopAsync().Wait()
        Assert.AreEqual(first + 1L, second)

    [<Test>]
    member x.``a moved project keeps its database: nothing is rebuilt``() =
        let runs = ref 0
        let src = Path.Combine(Path.GetTempPath(), "xake-reloc-" + string (System.Guid.NewGuid()))
        let dst = src + "-moved"
        let rs =
            [ "out/r.o" ..> recipe {
                    do! need [ "r.c" ]
                    count runs
                    do! writeText "o"
                } ]
        let run root =
            xake { x.TestOptions with ProjectRoot = root; Targets = [ "out/r.o" ]; ResetDb = false } { rules rs }
        try
            Directory.CreateDirectory src |> ignore
            File.WriteAllText(src </> "r.c", "c")
            run src
            Assert.AreEqual(1, runs.Value)
            for f in Directory.GetFiles(src, "*", SearchOption.AllDirectories) do
                if not (f.EndsWith ".lock") then
                    let target = dst </> Path.GetRelativePath(src, f)
                    Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
                    File.Copy(f, target)
                    File.SetLastWriteTime(target, File.GetLastWriteTime f)
            run dst
            Assert.AreEqual(1, runs.Value, "nothing rebuilt in the copy")
        finally
            for d in [ src; dst ] do try Directory.Delete(d, true) with _ -> ()

    /// A8: a dry run observes in memory and appends nothing.
    [<Test>]
    member x.``a dry run appends nothing``() =
        let runs = ref 0
        File.WriteAllText("dry.c", "v1")
        let rs = [ "out/dry.o" ..> recipe {
                        do! need [ "dry.c" ]
                        count runs
                        do! writeText "o" } ]
        x.Run [ "out/dry.o" ] rs
        edit "dry.c" "v2"
        let length = (FileInfo ".xake").Length
        xake { x.TestOptions with Targets = [ "out/dry.o" ]; ResetDb = false; DryRun = true } { rules rs }
        Assert.AreEqual(length, (FileInfo ".xake").Length)
        x.Run [ "out/dry.o" ] rs
        Assert.AreEqual(2, runs.Value)

    /// A3: a delegated result returned without its body keeps the local epoch, never a foreign one.
    [<Test>]
    member x.``a delegated result never stores a foreign epoch``() =
        let executor : DelegatedExecutor<ExecContext> =
            fun _ targets _ -> async {
                Directory.CreateDirectory "out" |> ignore
                File.WriteAllText("out/del.txt", "x")
                return { BuildLog.makeResult targets with Epoch = 999L } }
        let rs = [ DelegatedRule (FileRule ("out/del.txt", recipe { () }), executor) ]
        x.Run [ "out/del.txt" ] rs
        x.Run [ "out/del.txt" ] rs
        let db = x.Results ()
        let last = db.Epoch
        let epoch = db.Status.[FileTarget (File.make "out/del.txt")].Epoch
        Assert.That(epoch, Is.LessThanOrEqualTo last)
        Assert.AreNotEqual(999L, epoch)
