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
        // the agent stores a result after the demand completes: read the file once the engine
        // is stopped (the database closed), and stop it even when a demand fails, so that its
        // lock never outlives the test
        try
            engine.Demand("always").Wait()
            engine.Demand("always").Wait()
        finally
            engine.StopAsync().Wait()
        let epochs =
            Storage.readRecords x.TestOptions.ProjectRoot ".xake"
            |> Seq.choose (function
                | Storage.DbRecord.Result r when r.Targets = [ PhonyAction "always" ] -> Some r.Epoch
                | _ -> None)
            |> List.ofSeq
        Assert.AreEqual(2, epochs.Length)
        Assert.AreEqual(epochs.[0] + 1L, epochs.[1])

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
        let epochOf () = (x.Results ()).Status.[FileTarget (File.make "out/del.txt")].Epoch
        x.Run [ "out/del.txt" ] rs
        let first = epochOf ()
        x.Run [ "out/del.txt" ] rs
        Assert.AreEqual(first, epochOf (), "the second hit keeps the local epoch")
        Assert.AreNotEqual(999L, first)

    /// A2: each target group of the command line is a run of its own; overlapping engine demands
    /// get their own epochs.
    [<Test>]
    member x.``target groups and overlapping demands take their own epochs``() =
        let rs = [ "g1" => recipe { do! alwaysRerun () }; "g2" => recipe { do! alwaysRerun () } ]
        x.Run [ "g1"; "g2" ] rs
        let db = x.Results ()
        Assert.AreEqual(db.Status.[PhonyAction "g1"].Epoch + 1L, db.Status.[PhonyAction "g2"].Epoch)
        let engine =
            RulesBuilder { x.TestOptions with IgnoreCommandLine = true; ResetDb = false; Progress = false } {
                phony "slow" (recipe {
                    do! Async.Sleep 200
                    do! alwaysRerun () })
                phony "fast" (recipe { do! alwaysRerun () })
                start
            }
        try
            let slow = engine.Demand "slow"
            let fast = engine.Demand "fast"
            System.Threading.Tasks.Task.WaitAll [| slow; fast |]
        finally
            engine.StopAsync().Wait()
        let db = x.Results ()
        Assert.AreNotEqual(db.Status.[PhonyAction "slow"].Epoch, db.Status.[PhonyAction "fast"].Epoch)

    /// A1 end to end: a file without a rule rewritten during a run by another rule is recorded
    /// with that run's change; the next run rebuilds nothing.
    [<Test>]
    member x.``a generated file without a rule rewritten in a run does not rebuild twice``() =
        let yRuns = ref 0
        File.WriteAllText("a1.src", "v1")
        let rs =
            [ "out/a1.x" ..> recipe {
                do! need [ "a1.src" ]
                File.WriteAllText("a1.gen", File.ReadAllText "a1.src")    // a side output, no rule
                do! writeText "x" }
              "out/a1.y" ..> recipe {
                do! need [ "out/a1.x" ]
                do! need [ "a1.gen" ]
                count yRuns
                do! writeText (File.ReadAllText "a1.gen") } ]
        x.Run [ "out/a1.y" ] rs
        edit "a1.src" "v2"
        x.Run [ "out/a1.y" ] rs
        Assert.AreEqual(2, yRuns.Value)
        Assert.AreEqual("v2", File.ReadAllText "out/a1.y")
        x.Run [ "out/a1.y" ] rs
        Assert.AreEqual(2, yRuns.Value, "no second rebuild")

    /// A8: --dump appends nothing; a multi-output rule re-observes all its outputs.
    [<Test>]
    member x.``dump appends nothing; a multi-output rule re-observes every output``() =
        let runs = ref 0
        File.WriteAllText("mo.src", "v1")
        let rs =
            [ [ "out/mo.dll"; "out/mo.xml" ] *..> recipe {
                do! need [ "mo.src" ]
                count runs
                File.WriteAllText("out/mo.dll", "d")
                File.WriteAllText("out/mo.xml", "x") }
              "out/mo.zip" ..> recipe {
                do! need [ "out/mo.xml" ]
                do! writeText "z" } ]
        x.Run [ "out/mo.zip" ] rs
        let db = x.Results ()
        let built = db.Status.[FileTarget (File.make "out/mo.dll")].Epoch
        for f in [ "out/mo.dll"; "out/mo.xml" ] do
            Assert.AreEqual(built, db.Files.[File.make f].Changed, f)
        let length = (FileInfo ".xake").Length
        edit "mo.src" "v2"
        xake { x.TestOptions with Targets = [ "out/mo.zip" ]; ResetDb = false; DumpDeps = true } { rules rs }
        Assert.AreEqual(length, (FileInfo ".xake").Length)

    /// A5: --resetdb runs under the lock and starts afresh.
    [<Test>]
    member x.``resetdb deletes the database and rebuilds``() =
        let runs = ref 0
        File.WriteAllText("rs.c", "c")
        let rs = [ "out/rs.o" ..> recipe {
                        do! need [ "rs.c" ]
                        count runs
                        do! writeText "o" } ]
        x.Run [ "out/rs.o" ] rs
        x.Run [ "out/rs.o" ] rs
        xake { x.TestOptions with Targets = [ "out/rs.o" ]; ResetDb = true } { rules rs }
        Assert.AreEqual(2, runs.Value)
        Assert.IsFalse(File.Exists ".xake.bak")

    /// A6: drive-relative and rooted paths without a drive (Windows), a lowercase drive.
    [<Test>]
    member _.``File.make resolves Windows partial roots``() =
        if Env.isUnix then Assert.Ignore "Windows paths"
        let cwd = Directory.GetCurrentDirectory()
        let drive = cwd.Substring(0, 2).ToUpperInvariant()
        Assert.AreEqual(drive + "/foo", (File.make @"\foo").Name)
        Assert.AreEqual(Path.collapse (cwd </> "foo"), (File.make (drive + "foo")).Name)
        Assert.AreEqual(File.make (drive.ToLowerInvariant() + @"\x"), File.make (drive + @"\x"))

    /// A7: an upgraded fileset is searched ignoring case; with names differing in case on a
    /// case-sensitive volume (Linux) its dependent reruns once.
    [<Test>]
    member x.``an upgraded fileset is re-scanned ignoring case``() =
        Directory.CreateDirectory "cs7" |> ignore
        File.WriteAllText("cs7/a.cs", "")
        File.WriteAllText("cs7/A.cs", "")
        if Directory.GetFiles "cs7" |> Array.length < 2 then Assert.Ignore "the volume ignores case"
        let runs = ref 0
        let rs = [ "out/cs7.o" ..> recipe {
                        let! _ = getFiles (fileset { includes "cs7/*.cs" })
                        count runs
                        do! writeText "o" } ]
        let fs = fileset { includes "cs7/*.cs" }
        let old =
            { LegacyRead.OTargets = [ FileTarget (File.make "out/cs7.o") ]; LegacyRead.OBuilt = System.DateTime.Now
              LegacyRead.ODepends = [ LegacyRead.Plain (GetFiles (fs, Filelist [ File.make "cs7/a.cs" ])) ]; LegacyRead.OSteps = [] }
        Directory.CreateDirectory "out" |> ignore
        File.WriteAllText("out/cs7.o", "o")
        OldDatabase.writeOld ".xake" "0.6" [ old ]
        x.Run [ "out/cs7.o" ] rs
        x.Run [ "out/cs7.o" ] rs
        Assert.AreEqual(1, runs.Value, "reruns once")
