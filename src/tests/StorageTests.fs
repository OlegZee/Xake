module ``Storage facility``

open System.IO
open NUnit.Framework

open Xake
open Xake.BuildLog
open Xake.Storage
open Xake.Storage.Persist
open Tests

module private impl =
    let dbname = "." </> ".xake"
    let root () = Directory.GetCurrentDirectory()

    let mkFileTarget = File.make >> FileTarget
    let newStepInfo (name, duration) =
        { StepInfo.Empty with Name = name
                              OwnTime = duration * 1<ms> }

    let createStrLogger (errorlist : System.Collections.Generic.List<string>) =
        CustomLogger (fun _ -> true) (fun s -> lock errorlist (fun () -> errorlist.Add s))
    let logger = ConsoleLogger Verbosity.Diag

    let createResult name =
        { (makeResult [ mkFileTarget name ]) with
            Depends = [ TargetDep (mkFileTarget "abc.c"); Var("DEBUG", Some "false") ]
            Steps = [ newStepInfo ("compile", 217) ] }

    let openDb () = Storage.openDb (root ()) dbname
    let (<-*) (a : Agent<DatabaseApi>) t = a.PostAndReply(fun ch -> GetResult(t, ch))
    let store (a : Agent<DatabaseApi>) r = Store r |> a.Post
    // Is.Any predicate for assertions
    let IsAny() = Is.Not.All.Not

open impl

[<SetUp>]
let Setup() =
    for f in [ dbname; dbname + ".bak"; dbname + ".lock" ] do
        try File.Delete f with _ -> ()

[<Test>]
let ``round-trips every record kind``() =
    let root = if Env.isUnix then "/p" else @"C:\p"
    let f (p: string) = File.make (root </> p)
    let outside = File.make (if Env.isUnix then "/q/lib/x.dll" else @"D:\q\lib\x.dll")
    let fileset = Fileset ({ DefaultOptions with CaseSensitive = true; BaseDir = Some "src" }, []) + "**/*.cs" - "obj/**"
    let result =
        { Targets = [ FileTarget (f "out/a.dll"); FileTarget (f "out/a.pdb") ]
          Built = System.DateTime(2026, 11, 18, 10, 20, 30)
          Epoch = 42L
          Depends =
            [ FileDep (f "src/a.cs"); FileDep outside; FileDep (f "top.txt")
              TargetDep (FileTarget (f "out/b.dll")); TargetDep (PhonyAction "gen")
              GetFiles (fileset, Filelist [ f "src/a.cs"; f "src/sub/b.cs" ])
              EnvVar ("SDK", Some "4.5"); EnvVar ("NONE", None); Var ("DEBUG", Some "false"); AlwaysRerun
              SecretDep ("A", SecretEnv, SecretAbsent); SecretDep ("B", SecretArg, SecretPresent)
              SecretDep ("C", SecretEnv, SecretHmac ([| 1uy; 2uy |], [| 3uy; 4uy |])) ]
          Steps = [ newStepInfo ("compile", 217); newStepInfo ("link", 471) ]
          StaticCount = 3 }
    let states =
        [ f "src/a.cs", { Stamp = System.DateTime(2026, 1, 2, 3, 4, 5); Size = 1234L; Hash = Some 0xDEADBEEFCAFEF00DUL; Changed = 7L }
          outside, { Stamp = System.DateTime(2025, 1, 1); Size = -1L; Hash = None; Changed = 1L }
          f "top.txt", { Stamp = System.DateTime(2025, 1, 1); Size = 0L; Hash = Some 0UL; Changed = 300L } ]
    let recs = (states |> List.map FileRec) @ [ ResultRec result; ResultRec { result with Epoch = 43L; StaticCount = 0 } ]
    let bytes, read = DbFormat.roundTrip root recs
    let expected = (states |> List.map DbRecord.FileState) @ [ DbRecord.Result result; DbRecord.Result { result with Epoch = 43L; StaticCount = 0 } ]
    Assert.AreEqual(expected, read.Records)
    Assert.IsFalse(read.Torn)
    Assert.AreEqual(2, read.ResultCount)
    // the root's value is never written; a path outside the root stays absolute
    let text = System.Text.Encoding.UTF8.GetString bytes
    Assert.IsFalse(text.Contains (File.make root).Name, "the root is not in the file")
    StringAssert.Contains("lib", text)
    StringAssert.Contains((File.make (if Env.isUnix then "/q/lib" else @"D:\q\lib")).Name, text)
    // a path is written once: one PathRec for a.cs although it is used four times
    Assert.AreEqual(1, (text.Split([| "a.cs" |], System.StringSplitOptions.None).Length - 1))

[<Test>]
let ``persists build data in Xake db``() =
    let testee = openDb () logger
    for n in [ "abc.exe"; "def.exe"; "fgh.exe" ] do store testee (createResult n)
    testee.PostAndReply CloseWait
    let testee = openDb () logger
    for n in [ "abc.exe"; "def.exe"; "fgh.exe" ] do
        Assert.AreEqual(Some (createResult n).Depends, testee <-* mkFileTarget n |> Option.map (fun r -> r.Depends))
    testee.PostAndReply CloseWait

[<Test>]
let ``compresses database when limit is reached``() =
    let msgs = System.Collections.Generic.List<string>()
    let logger = createStrLogger msgs
    let testee = openDb () logger
    for _ in 1..20 do
        for i in 1..20 do store testee (createResult (sprintf "a%A.exe" i))
    testee.PostAndReply CloseWait
    let oldLen = (FileInfo dbname).Length
    let testee = openDb () logger
    testee.PostAndReply CloseWait
    let newLen = (FileInfo dbname).Length
    printfn "old size: %A, new size: %A" oldLen newLen
    Assert.Less((int newLen), (int oldLen) / 3)
    Assert.That(msgs, IsAny().Contains("Compacting database"))
    let db, _ = readDatabase (root ()) dbname
    Assert.AreEqual(20, db.Status.Count)

[<Test>]
let ``updates data in file storage``() =
    let testee = openDb () logger
    let result = createResult "abc"
    store testee result
    store testee { result with Depends = [ Var("DEBUG", Some "true") ] }
    testee.PostAndReply CloseWait
    let testee = openDb () logger
    let read = testee <-* (mkFileTarget "abc")
    testee.PostAndReply CloseWait
    Assert.AreEqual(Some [ Var("DEBUG", Some "true") ], read |> Option.map (fun r -> r.Depends))

[<Test>]
let ``restores db in case write failed``() =
    let msgs = System.Collections.Generic.List<string>()
    let logger = createStrLogger msgs
    let testee = openDb () logger
    store testee (createResult "abc")
    testee.PostAndReply CloseWait
    let bkdb = "." </> ".xake" <.> "bak"
    File.Move(dbname, bkdb)
    File.WriteAllText(dbname, "dummy text")
    let testee = openDb () logger
    let read = testee <-* (mkFileTarget "abc")
    Assert.IsTrue(Option.isSome read)
    testee.PostAndReply CloseWait
    Assert.That(msgs, IsAny().Contains("restoring db"))

[<Test>]
let ``repairs (cleans) broken db``() =
    let msgs = System.Collections.Generic.List<string>()
    let logger = createStrLogger msgs
    File.WriteAllText(dbname, "dummy text")
    let testee = openDb () logger
    store testee (createResult "abc")
    let read = testee <-* mkFileTarget "abc"
    Assert.IsTrue(Option.isSome read)
    testee.PostAndReply CloseWait
    Assert.That(msgs, IsAny().Contains("Failed to read database"))

[<Test>]
let ``cleanupDb deletes database files``() =
    let msgs = System.Collections.Generic.List<string>()
    let logger = createStrLogger msgs
    let testee = openDb () logger
    store testee (createResult "abc.exe")
    testee.PostAndReply CloseWait
    Assert.IsTrue(File.Exists(dbname), "Database file should exist before cleanup")
    let bkpath = dbname + ".bak"
    File.WriteAllText(bkpath, "backup")
    Storage.cleanupDb dbname logger
    Assert.IsFalse(File.Exists(dbname), "Database file should be deleted")
    Assert.IsFalse(File.Exists(bkpath), "Backup file should be deleted")
    Assert.That(msgs, IsAny().Contains("Deleted database file"))
    let testee = openDb () logger
    let read = testee <-* mkFileTarget "abc.exe"
    Assert.IsTrue(Option.isNone read, "Fresh database should not contain old data")
    testee.PostAndReply CloseWait

[<Test>]
let ``reads a 0.4 database and rewrites it as the current version``() =
    let msgs = System.Collections.Generic.List<string>()
    let old = { (makeResult [ mkFileTarget "old.exe" ]) with Depends = [ Var("DEBUG", Some "false") ] }
    OldDatabase.write dbname "0.4" [old]
    let testee = openDb () (createStrLogger msgs)
    let read = testee <-* mkFileTarget "old.exe"
    testee.PostAndReply CloseWait
    Assert.AreEqual(Some old.Depends, read |> Option.map (fun r -> r.Depends))
    Assert.That(msgs, IsAny().Contains("Upgrading database"))
    let ver, results = OldDatabase.read dbname
    Assert.AreEqual(XakeDbVersion, ver)
    Assert.AreEqual([old.Depends], results |> List.map (fun r -> r.Depends))

[<Test>]
let ``recreates a database older than 0.4 or newer than this Xake, with a message``() =
    for ver, text in [ "0.3", "database version 0.3 is no longer read, recreating"
                       "0.9", "database written by a newer Xake (version 0.9), recreating" ] do
        let msgs = System.Collections.Generic.List<string>()
        OldDatabase.write dbname ver [ makeResult [ mkFileTarget "old.exe" ] ]
        let testee = openDb () (createStrLogger msgs)
        let read = testee <-* mkFileTarget "old.exe"
        testee.PostAndReply CloseWait
        Assert.IsTrue(Option.isNone read)
        Assert.That(msgs, IsAny().Contains(text))
        Assert.AreEqual(XakeDbVersion, OldDatabase.version dbname)

[<Test>]
let ``a torn last record is cut off and the rest kept``() =
    let msgs = System.Collections.Generic.List<string>()
    let testee = openDb () logger
    store testee (createResult "first.exe")
    testee <-* mkFileTarget "first.exe" |> ignore
    let goodLength = (FileInfo dbname).Length
    store testee (createResult "second.exe")
    testee.PostAndReply CloseWait
    let cut = (FileInfo dbname).Length - 3L
    use fs = new FileStream(dbname, FileMode.Open)
    fs.SetLength cut
    fs.Dispose()
    // the read API stops at the torn record
    Assert.AreEqual(1, readRecords (root ()) dbname |> Seq.length)
    let testee = openDb () (createStrLogger msgs)
    let first, second = testee <-* mkFileTarget "first.exe", testee <-* mkFileTarget "second.exe"
    testee.PostAndReply CloseWait
    Assert.IsTrue(first.IsSome)
    Assert.IsTrue(second.IsNone)
    Assert.That(msgs, IsAny().Contains("incomplete"))
    // cut back to the end of the last whole record (a new path record may precede the torn one)
    Assert.That((FileInfo dbname).Length, Is.InRange(goodLength, cut - 1L))

[<Test>]
let ``a second open waits for the lock, then fails``() =
    let msgs = System.Collections.Generic.List<string>()
    let first = openDb () logger
    let ex =
        Assert.Throws<XakeException>(fun () ->
            Storage.openDbWith (System.TimeSpan.FromMilliseconds 200.0) (root ()) dbname (createStrLogger msgs) |> ignore)
    StringAssert.Contains("another Xake run uses this database (.xake.lock)", ex.Message)
    Assert.AreEqual(1, msgs |> Seq.filter (fun s -> s.Contains "waiting for another Xake run on this database") |> Seq.length)
    first.PostAndReply CloseWait
    let again = Storage.openDbWith (System.TimeSpan.FromMilliseconds 200.0) (root ()) dbname logger
    again.PostAndReply CloseWait

[<Test>]
let ``the read API reads a database whose lock is held``() =
    let testee = openDb () logger
    store testee (createResult "a.exe")
    store testee { (createResult "b.exe") with Depends = [ FileDep (File.make "src.c") ] }
    File.WriteAllText("src.c", "x")
    testee.PostAndReply(fun ch -> Observe(File.make "src.c", false, ch)) |> ignore
    let records = readRecords (root ()) dbname |> List.ofSeq
    match records with
    | [ DbRecord.Result a; DbRecord.Result b; DbRecord.FileState (f, st) ] ->
        Assert.AreEqual([ mkFileTarget "a.exe" ], a.Targets)
        Assert.AreEqual([ mkFileTarget "b.exe" ], b.Targets)
        Assert.AreEqual(File.make "src.c", f)
        Assert.AreEqual(1L, st.Size)
        Assert.AreEqual(a.Epoch, st.Changed)
    | other -> Assert.Fail(sprintf "unexpected %A" other)
    let db, epoch = readDatabase (root ()) dbname
    Assert.AreEqual(2, db.Status.Count)
    Assert.AreEqual(1, db.Files.Count)
    Assert.AreEqual(1L, epoch)
    testee.PostAndReply CloseWait

// ---- size --------------------------------------------------------------------------------------

/// 1000 results x 30 file dependencies, paths of about 114 bytes: 0.6 against 0.7 bytes.
[<Test>]
let ``database 0.7 is at least 5x smaller than 0.6 on a synthetic database``() =
    let root = if Env.isUnix then "/home/build/work/products/project-root" else @"C:\build\work\products\project-root"
    let dirs = [| for i in 0 .. 129 -> sprintf "src/component-%03d/implementation/subsystem/module-%02d" i (i % 17) |]
    let path i = File.make (root </> dirs.[i % dirs.Length] </> sprintf "source-file-number-%04d.cs" i)
    let stamp = System.DateTime(2026, 11, 18, 12, 0, 0)
    let results =
        [ for r in 0 .. 999 ->
            let deps = [ for k in 0 .. 29 -> path ((r * 7 + k * 31) % 1050) ]
            r, deps ]
    let meanPath = results |> List.collect snd |> List.averageBy (fun f -> float f.FullName.Length)
    let old =
        use ms = new MemoryStream()
        use w = new BinaryWriter(ms)
        Pickler.triple Pickler.str Pickler.str Pickler.date |> fun pu -> pu.pickle ("XAKE", "0.6", stamp) w
        let pu = legacyResult root
        for r, deps in results do
            pu.pickle { OTargets = [ FileTarget (File.make (root </> sprintf "out/obj-%04d.dll" r)) ]; OBuilt = stamp
                        ODepends = deps |> List.map (fun f -> OldFile (f, stamp)); OSteps = [ newStepInfo ("all", 10) ] } w
        w.Flush()
        ms.Length
    let recs =
        [ for f in results |> List.collect snd |> List.distinct -> FileRec (f, { Stamp = stamp; Size = 4096L; Hash = None; Changed = 1L }) ]
        @ [ for r, deps in results ->
              ResultRec { Targets = [ FileTarget (File.make (root </> sprintf "out/obj-%04d.dll" r)) ]; Built = stamp; Epoch = 1L
                          Depends = deps |> List.map FileDep; Steps = [ newStepInfo ("all", 10) ]; StaticCount = 0 } ]
    let bytes, _ = DbFormat.roundTrip root recs
    let ratio = float old / float bytes.Length
    printfn "synthetic: mean path %.1f B, 0.6 %d B, 0.7 %d B, %.1fx" meanPath old bytes.Length ratio
    Assert.GreaterOrEqual(ratio, 5.0)

/// A real 0.6 database named by XAKE_DB_SAMPLE: bytes before and after the upgrade (read-only:
/// the file is copied, the copy is upgraded; the sample's folder is only the root).
[<Test; Explicit("needs XAKE_DB_SAMPLE")>]
let ``measures the upgrade of XAKE_DB_SAMPLE``() =
    match System.Environment.GetEnvironmentVariable "XAKE_DB_SAMPLE" with
    | null | "" -> Assert.Ignore "XAKE_DB_SAMPLE is not set"
    | sample ->
        let dir = Path.Combine(Path.GetTempPath(), "xake-db-sample-" + string (System.Guid.NewGuid()))
        Directory.CreateDirectory dir |> ignore
        try
            let copy = dir </> ".xake"
            File.Copy(sample, copy)
            let before = (FileInfo copy).Length
            let sw = System.Diagnostics.Stopwatch.StartNew()
            let db = Storage.openDb (Path.GetDirectoryName (Path.GetFullPath sample)) copy (CustomLogger (fun _ -> false) ignore)
            db.PostAndReply CloseWait
            let after = (FileInfo copy).Length
            let state, _ = readDatabase (Path.GetDirectoryName (Path.GetFullPath sample)) copy
            printfn "sample: %s %d B -> %s %d B (%.1fx) in %d ms; %d results, %d files"
                (OldDatabase.version sample) before (OldDatabase.version copy) after (float before / float after)
                sw.ElapsedMilliseconds state.Status.Count state.Files.Count
        finally
            try Directory.Delete(dir, true) with _ -> ()
