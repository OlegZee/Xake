module ``Storage facility``

open System.IO
open NUnit.Framework

open Xake
open Xake.BuildLog
open Xake.Storage
open Xake.LegacyRead
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
    let (<-*) (a : Db) t = Storage.tryGetResult a t
    let store (a : Db) r = a.PostAndReply(fun ch -> Store (r, ch))
    /// Starts a run: its epoch and its files (docs/features/idle-perf, L4).
    let newRun (a : Db) = a.PostAndReply NewRun, RunFiles ()
    /// Observes one file through the facade (L5).
    let observeIn (a : Db) run hashing mode since f =
        (Storage.observeFiles { Db = a; Run = run; Hashing = hashing; OnHashed = ignore } mode since [ f ] |> Async.RunSynchronously).[0]
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
          Epoch = 42
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
        [ f "src/a.cs", { Stamp = System.DateTime(2026, 1, 2, 3, 4, 5); Size = 1234L; Hash = Some 0xDEADBEEFCAFEF00DUL; Changed = 7 }
          outside, { Stamp = System.DateTime(2025, 1, 1); Size = -1L; Hash = None; Changed = 1 }
          f "top.txt", { Stamp = System.DateTime(2025, 1, 1); Size = 0L; Hash = Some 0UL; Changed = 300 } ]
    let recs = (states |> List.map DbRecord.FileState) @ [ DbRecord.Result result; DbRecord.Result { result with Epoch = 43; StaticCount = 0 } ]
    let bytes, read = DbFormat.roundTrip root recs
    let expected = (states |> List.map DbRecord.FileState) @ [ DbRecord.Result result; DbRecord.Result { result with Epoch = 43; StaticCount = 0 } ]
    Assert.AreEqual(expected, read.Records)
    Assert.IsFalse(read.Torn)
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
    let db = readDatabase (root ()) dbname
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
            Storage.openDbWith (System.TimeSpan.FromMilliseconds 200.0) false (root ()) dbname (createStrLogger msgs) |> ignore)
    StringAssert.Contains("another Xake run uses this database (.xake.lock)", ex.Message)
    Assert.AreEqual(1, msgs |> Seq.filter (fun s -> s.Contains "waiting for another Xake run on this database") |> Seq.length)
    first.PostAndReply CloseWait
    let again = Storage.openDbWith (System.TimeSpan.FromMilliseconds 200.0) false (root ()) dbname logger
    again.PostAndReply CloseWait

[<Test>]
let ``the read API reads a database whose lock is held``() =
    File.WriteAllText("src.c", "x")
    let testee = openDb () logger
    let run, files = newRun testee
    store testee { (createResult "a.exe") with Epoch = run }
    store testee { (createResult "b.exe") with Depends = [ FileDep (File.make "src.c") ]; Epoch = run }
    observeIn testee files false Verdict 0 (File.make "src.c") |> ignore
    let records = readRecords (root ()) dbname |> List.ofSeq
    match records with
    | [ DbRecord.Result a; DbRecord.Result b; DbRecord.FileState (f, st) ] ->
        Assert.AreEqual([ mkFileTarget "a.exe" ], a.Targets)
        Assert.AreEqual([ mkFileTarget "b.exe" ], b.Targets)
        Assert.AreEqual(File.make "src.c", f)
        Assert.AreEqual(1L, st.Size)
        Assert.AreEqual(run, st.Changed)
        Assert.AreEqual(run, a.Epoch)
    | other -> Assert.Fail(sprintf "unexpected %A" other)
    let db = readDatabase (root ()) dbname
    let epoch = db.Epoch
    Assert.AreEqual(2, db.Status.Count)
    Assert.AreEqual(1, db.Files.Count)
    Assert.AreEqual(run, epoch)
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
    let meanPath = results |> List.collect snd |> List.averageBy (fun f -> float f.Name.Length)
    let old =
        use ms = new MemoryStream()
        use w = new BinaryWriter(ms)
        Pickler.triple Pickler.str Pickler.str Pickler.date |> fun pu -> pu.pickle ("XAKE", "0.6", stamp) w
        let pu = LegacyRead.result root
        for r, deps in results do
            pu.pickle { OTargets = [ FileTarget (File.make (root </> sprintf "out/obj-%04d.dll" r)) ]; OBuilt = stamp
                        ODepends = deps |> List.map (fun f -> OldFile (f, stamp)); OSteps = [ newStepInfo ("all", 10) ] } w
        w.Flush()
        ms.Length
    let recs =
        [ for f in results |> List.collect snd |> List.distinct -> DbRecord.FileState (f, { Stamp = stamp; Size = 4096L; Hash = None; Changed = 1 }) ]
        @ [ for r, deps in results ->
              DbRecord.Result
                  { Targets = [ FileTarget (File.make (root </> sprintf "out/obj-%04d.dll" r)) ]; Built = stamp; Epoch = 1
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
            let state = readDatabase (Path.GetDirectoryName (Path.GetFullPath sample)) copy
            printfn "sample: %s %d B -> %s %d B (%.1fx) in %d ms; %d results, %d files"
                (OldDatabase.version sample) before (OldDatabase.version copy) after (float before / float after)
                sw.ElapsedMilliseconds state.Status.Count state.Files.Count
        finally
            try Directory.Delete(dir, true) with _ -> ()

// ---- amendments ------------------------------------------------------------------------------

/// A1: a body's recording re-stats a file the verdict saw; a second recording leaves the record.
[<Test>]
let ``recording re-stats a file once per run``() =
    File.WriteAllText("rec.c", "v1")
    let f = File.make "rec.c"
    let db = openDb () logger
    let observe files mode = match observeIn db files false mode 0 f with Unchanged s | Changed s -> s | o -> failwithf "%A" o
    let run1, files1 = newRun db
    Assert.AreEqual(run1, (observe files1 Verdict).Changed)
    let run2, files2 = newRun db
    Assert.AreEqual(run1, (observe files2 Verdict).Changed)
    System.Threading.Thread.Sleep 100
    File.WriteAllText("rec.c", "v2-longer")
    Assert.AreEqual(run1, (observe files2 Verdict).Changed, "a verdict's observation stays cached")
    let recorded = observe files2 Recording
    Assert.AreEqual(run2, recorded.Changed, "the recording sees the move")
    System.Threading.Thread.Sleep 100
    File.WriteAllText("rec.c", "v3")
    Assert.AreEqual(recorded, observe files2 Recording, "another recording in the run leaves the record")
    db.PostAndReply CloseWait
    let state = readDatabase (root ()) dbname
    Assert.AreEqual(recorded, state.Files.[f])

/// A7: the upgrade takes a file's stamp from the result with the latest Built; MinValue is no stamp.
[<Test>]
let ``the upgrade takes the stamp of the latest built result``() =
    let f = File.make "conv.c"
    let t0 = System.DateTime(2026, 1, 1)
    let res name built stamp =
        { OTargets = [ mkFileTarget name ]; OBuilt = built; ODepends = [ OldFile (f, stamp) ]; OSteps = [] }
    let files, results =
        convert [ res "late.o" (t0.AddHours 2.0) (t0.AddMinutes 1.0)
                  res "early.o" (t0.AddHours 1.0) (t0.AddMinutes 5.0)
                  { res "none.o" (t0.AddHours 3.0) System.DateTime.MinValue with ODepends = [ OldArtifact2 (mkFileTarget "x.o", System.DateTime.MinValue) ] } ]
    Assert.AreEqual([ f, { Stamp = t0.AddMinutes 1.0; Size = -1L; Hash = None; Changed = 1 } ], files)
    Assert.AreEqual([ 1; 0; 0 ], results |> List.map (fun r -> r.Epoch))

/// A5: a failed open releases the lock; a reader of a missing file gets nothing.
[<Test>]
let ``a failed open releases the lock``() =
    Directory.CreateDirectory "dbdir" |> ignore
    let path = "dbdir"     // a directory: the database file cannot be opened
    let openIt () = Storage.openDbWith (System.TimeSpan.FromMilliseconds 200.0) false (root ()) path logger |> ignore
    let first = Assert.Catch(fun () -> openIt ())
    let second = Assert.Catch(fun () -> openIt ())
    Assert.IsNotInstanceOf<XakeException>(second, "not the lock: it was released")
    Assert.AreEqual(first.GetType(), second.GetType())
    Assert.IsEmpty(readRecords (root ()) "no-such.xake")

/// A6: a project at a file-system root loads its paths back (no '//').
[<Test>]
let ``a path table at a file-system root round-trips``() =
    let root = if Env.isUnix then "/" else @"C:\"
    let f = File.make (root </> "src" </> "a.c")
    let state = { Stamp = System.DateTime(2026, 1, 1); Size = 1L; Hash = None; Changed = 1 }
    let _, read = DbFormat.roundTrip root [ DbRecord.FileState (f, state) ]
    Assert.AreEqual([ DbRecord.FileState (f, state) ], read.Records)

/// Content cut-off through the facade: a moved file is hashed; the same content keeps its run,
/// other content is a change.
[<Test>]
let ``a moved file is hashed; the same content keeps its run``() =
    File.WriteAllText("settle.c", "v1")
    let f = File.make "settle.c"
    let db = openDb () logger
    let observe files since = observeIn db files true Verdict since f
    let touch () =
        System.Threading.Thread.Sleep 100
        File.SetLastWriteTime("settle.c", System.DateTime.Now)
    let run1, files = newRun db
    match observe files 0 with
    | Changed st -> Assert.AreEqual((run1, None), (st.Changed, st.Hash), "first sight: new, not hashed")
    | o -> Assert.Fail(sprintf "first sight: %A" o)
    let run2, files = newRun db
    touch ()
    match observe files run1 with
    | Changed st -> Assert.AreEqual(run2, st.Changed); Assert.IsTrue(st.Hash.IsSome, "the move was hashed")
    | o -> Assert.Fail(sprintf "first move: %A" o)
    let _, files = newRun db
    touch ()
    match observe files run2 with
    | Unchanged st -> Assert.AreEqual(run2, st.Changed, "the same content keeps its run")
    | o -> Assert.Fail(sprintf "same content: %A" o)
    let run4, files = newRun db
    System.Threading.Thread.Sleep 100
    File.WriteAllText("settle.c", "v2-other")
    match observe files (run4 - 1) with
    | Changed st -> Assert.AreEqual(run4, st.Changed); Assert.IsTrue(st.Hash.IsSome)
    | o -> Assert.Fail(sprintf "other content: %A" o)
    db.PostAndReply CloseWait


// ---- idle performance: the layers (docs/features/idle-perf/design.md) -------------------------

let private stateAt changed = { Stamp = System.DateTime(2026, 1, 1); Size = 1L; Hash = None; Changed = changed }

/// L2: `Apply` writes a record only if it is still the one the caller decided against; a
/// conflict replies the current record and writes nothing for that file.
[<Test>]
let ``Apply is a compare-and-set per file``() =
    let db = openDb () logger
    let a, b = File.make "cas-a.c", File.make "cas-b.c"
    let apply writes = db.PostAndReply(fun ch -> Apply (writes, ch))
    Assert.IsEmpty(apply [ a, None, stateAt 1; b, None, stateAt 1 ])
    Assert.AreEqual([ b, Some (stateAt 1) ], apply [ a, Some (stateAt 1), stateAt 2; b, None, stateAt 3 ])
    Assert.AreEqual(Some (stateAt 2), db.Snapshot.Files.TryFind a)
    Assert.AreEqual(Some (stateAt 1), db.Snapshot.Files.TryFind b)
    db.PostAndReply CloseWait
    let records = readRecords (root ()) dbname |> List.ofSeq
    Assert.AreEqual(3, records.Length, "the conflicting write appended nothing")

/// L2: one `Apply` is one append, whatever the number of records.
[<Test>]
let ``Apply appends its records in one write``() =
    let db = openDb () logger
    db.PostAndReply(fun ch -> Apply ([], ch)) |> ignore
    db.PostAndReply CloseWait
    let empty = (FileInfo dbname).Length
    let db = openDb () logger
    let files = [ for i in 1 .. 50 -> File.make (sprintf "batch%d.c" i) ]
    Assert.IsEmpty(db.PostAndReply(fun ch -> Apply ([ for f in files -> f, None, stateAt 1 ], ch)))
    db.PostAndReply CloseWait
    Assert.AreEqual(50, readRecords (root ()) dbname |> Seq.length)
    Assert.Greater((FileInfo dbname).Length, empty)

/// L2: once `Store` replies, the snapshot holds the result; reads post nothing.
[<Test>]
let ``the snapshot holds a stored result once Store replies``() =
    let db = openDb () logger
    let epoch = db.PostAndReply NewRun
    Assert.AreEqual(epoch, db.Snapshot.Epoch)
    store db { createResult "snap.exe" with Epoch = epoch }
    Assert.AreEqual(Some epoch, Storage.tryGetResult db (mkFileTarget "snap.exe") |> Option.map (fun r -> r.Epoch))
    db.PostAndReply CloseWait

/// L3: two requests for one (file, stat) while it is hashed compute it once; another request
/// after it finished computes again (no cache of completed hashes).
[<Test>]
let ``the hasher computes a (file, stat) once while it runs``() =
    File.WriteAllBytes("big.bin", Array.init (16 * 1024 * 1024) byte)
    let f = File.make "big.bin"
    let stat = FileStat.take f
    let hasher = Hasher()
    let computed = ref 0
    let onComputed _ = System.Threading.Interlocked.Increment computed |> ignore
    let results = [ for _ in 1 .. 4 -> hasher.Hash f stat onComputed ] |> Async.Parallel |> Async.RunSynchronously
    Assert.AreEqual(1, computed.Value)
    Assert.That(results |> Array.distinct, Has.Length.EqualTo 1)
    match results.[0] with Hashed _ -> () | r -> Assert.Fail(sprintf "%A" r)
    hasher.Hash f stat onComputed |> Async.RunSynchronously |> ignore
    Assert.AreEqual(2, computed.Value)

/// L0: a file that cannot be hashed is `Unreadable` with the stat taken after the attempt.
[<Test>]
let ``Content.hash of a missing file is Unreadable Missing``() =
    Assert.AreEqual(Unreadable Missing, Content.hash (File.make "no-such-file.bin"))

/// L5: a batch gives the same records as single observations.
[<Test>]
let ``a batch observes like single observations``() =
    let names = [ for i in 1 .. 5 -> sprintf "same%d.c" i ]
    for n in names do File.WriteAllText(n, n)
    let files = names |> List.map File.make
    let observeAll (single: bool) =
        for f in [ dbname; dbname + ".bak" ] do try File.Delete f with _ -> ()
        let db = openDb () logger
        let epoch, run = newRun db
        let oc = { Db = db; Run = run; Hashing = false; OnHashed = ignore }
        let replies =
            if single then [ for f in files -> (Storage.observeFiles oc Recording 0 [ f ] |> Async.RunSynchronously).[0] ]
            else Storage.observeFiles oc Recording 0 files |> Async.RunSynchronously |> List.ofArray
        let state = db.Snapshot.Files
        db.PostAndReply CloseWait
        replies, [ for f in files -> state.TryFind f ], epoch
    let singles, singleStates, _ = observeAll true
    let batch, batchStates, _ = observeAll false
    Assert.AreEqual(singles, batch)
    Assert.AreEqual(singleStates, batchStates)

/// L5: callers racing on a new file leave one record; an unchanged file writes nothing.
[<Test>]
let ``racing observations of a new file write one record, and an unchanged file none``() =
    File.WriteAllText("race.c", "x")
    let f = File.make "race.c"
    let db = openDb () logger
    let _, run = newRun db
    let oc = { Db = db; Run = run; Hashing = false; OnHashed = ignore }
    [ for _ in 1 .. 16 -> Storage.observeFiles oc Recording 0 [ f ] ] |> Async.Parallel |> Async.RunSynchronously |> ignore
    let _, run2 = newRun db
    Storage.observeFiles { oc with Run = run2 } Verdict 0 [ f ] |> Async.RunSynchronously |> ignore
    db.PostAndReply CloseWait
    let fileRecords = readRecords (root ()) dbname |> Seq.filter (function DbRecord.FileState (g, _) -> g = f | _ -> false) |> Seq.length
    Assert.AreEqual(1, fileRecords)

/// L5: a verdict's later observation in the run sees a file deleted since: existence per
/// dependency.
[<Test>]
let ``a verdict's later observation sees a deleted file``() =
    File.WriteAllText("gone.c", "x")
    let f = File.make "gone.c"
    let db = openDb () logger
    let _, run = newRun db
    let observe () = observeIn db run false Verdict 0 f
    match observe () with Changed _ -> () | o -> Assert.Fail(sprintf "%A" o)
    File.Delete "gone.c"
    Assert.AreEqual(Absent, observe ())
    db.PostAndReply CloseWait
