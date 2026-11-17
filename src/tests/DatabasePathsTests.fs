namespace Tests

open System
open System.IO
open NUnit.Framework

open Xake
open Xake.BuildLog
open Xake.Storage
open Xake.Tasks

/// Database 0.7: path tables, roots, relocation, recovery (docs/database.md, docs/features/db-paths).
module DbPathsHelpers =
    let timeoutMs = 10000
    let quiet = CustomLogger (fun _ -> false) ignore
    let collecting (log: Collections.Concurrent.ConcurrentQueue<string>) = CustomLogger (fun _ -> true) log.Enqueue

    let get (db: Agent<DatabaseApi>) t = db.PostAndReply((fun ch -> GetResult(t, ch)), timeoutMs)
    let close (db: Agent<DatabaseApi>) = db.PostAndReply(CloseWait, timeoutMs)

    let freshDir (name: string) =
        let dir = __SOURCE_DIRECTORY__ </> "~testout~" </> "dbpaths" </> name
        if Directory.Exists dir then Directory.Delete(dir, true)
        Directory.CreateDirectory dir |> ignore
        dir

    /// Copies a folder with its files' write times.
    let rec copyTree (src: string) (dst: string) =
        Directory.CreateDirectory dst |> ignore
        for f in Directory.GetFiles src do
            let target = dst </> Path.GetFileName f
            File.Copy(f, target, true)
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc f)
        for d in Directory.GetDirectories src do
            copyTree d (dst </> Path.GetFileName d)

    let withCwd (dir: string) f =
        let saved = Directory.GetCurrentDirectory()
        Directory.SetCurrentDirectory dir
        try f () finally Directory.SetCurrentDirectory saved

    let version (path: string) =
        use r = new BinaryReader(File.OpenRead path)
        let (_, ver, _) = (Pickler.triple Pickler.str Pickler.str Pickler.date).unpickle r
        ver

    let opts root = { DbOpenOptions.Default with ProjectRoot = root }

open DbPathsHelpers

[<TestFixture>]
type ``Database paths: unit``() =

    let root name path = { PathTable.Root.Name = name; PathTable.Root.Path = path; PathTable.Root.Recorded = path }

    [<Test>]
    member _.``split picks the longest root on a separator boundary``() =
        let sep = string Path.DirectorySeparatorChar
        let baseDir = Path.GetFullPath(Path.GetTempPath()) |> PathTable.Roots.trimSep
        let a = baseDir + sep + "a"
        let ab = a + sep + "b"
        let roots = PathTable.Roots.sorted [ root "ProjectRoot" a; root "B" ab ]
        Assert.AreEqual(("$(B)", ["c"]), PathTable.Roots.split roots (ab + sep + "c"))
        Assert.AreEqual(("$(B)", ([]: string list)), PathTable.Roots.split roots ab)
        // "a/bc" is not under "a/b"
        Assert.AreEqual(("$(ProjectRoot)", ["bc"]), PathTable.Roots.split roots (a + sep + "bc"))
        // outside every root: split at the file-system root
        let k, segs = PathTable.Roots.split roots (baseDir + sep + "other")
        Assert.AreEqual(Path.GetPathRoot baseDir, k)
        Assert.AreEqual("other", List.last segs)

    [<Test>]
    member _.``a file-system root as a root``() =
        let fsRoot = Path.GetPathRoot(Path.GetTempPath())
        let roots = [ root "ProjectRoot" fsRoot ]
        Assert.IsTrue(PathTable.Roots.isUnder fsRoot (fsRoot + "x"))
        Assert.AreEqual(("$(ProjectRoot)", ["x"]), PathTable.Roots.split roots (fsRoot + "x"))
        Assert.AreEqual(fsRoot + "x", PathTable.Roots.join fsRoot "x")

    [<Test>]
    member _.``trimSep keeps a file-system root``() =
        let fsRoot = Path.GetPathRoot(Path.GetTempPath())
        Assert.AreEqual(fsRoot, PathTable.Roots.trimSep fsRoot)
        Assert.AreEqual(fsRoot + "a", PathTable.Roots.trimSep (fsRoot + "a" + string Path.DirectorySeparatorChar))

    [<Test>]
    member _.``Windows roots: drive and UNC, case-insensitive``() =
        if not Env.isWindows then Assert.Ignore "Windows path rules"
        let roots = PathTable.Roots.sorted [ root "ProjectRoot" @"C:\Work\Proj"; root "SHARE" @"\\server\share\pkgs" ]
        Assert.AreEqual(("$(ProjectRoot)", ["src"]), PathTable.Roots.split roots @"c:\work\proj\src")
        Assert.AreEqual(("$(SHARE)", ["a"]), PathTable.Roots.split roots @"\\server\share\pkgs\a")
        Assert.AreEqual((@"D:\", ["x"]), PathTable.Roots.split roots @"D:\x")

    [<Test>]
    member _.``varint round trip``() =
        for v in [ 0; 1; 127; 128; 300; 16383; 16384; Int32.MaxValue ] do
            use ms = new MemoryStream()
            let w = new BinaryWriter(ms)
            PathTable.Varint.write w v
            w.Flush()
            ms.Position <- 0L
            Assert.AreEqual(v, PathTable.Varint.read (new BinaryReader(ms)))

[<TestFixture>]
type ``Database paths: storage``() =

    let mkResult (target: string) (deps: string list) =
        { makeResult [ FileTarget (File.make target) ] with
            Depends = deps |> List.map (fun d -> FileDep (File.make d, DateTime(2020, 1, 1))) }

    [<Test>]
    member _.``results round trip and every distinct path is one File value``() =
        let dir = freshDir "intern"
        let dbpath = dir </> ".xake"
        let p n = dir </> "src" </> n
        let r1 = mkResult (dir </> "out" </> "a.dll") [ p "x.cs"; p "y.cs" ]
        let r2 = { mkResult (dir </> "out" </> "b.dll") [ p "x.cs" ] with
                     Depends = [ FileDep (File.make (p "x.cs"), DateTime(2020, 1, 1))
                                 GetFiles (Fileset.Empty, Fileset.Filelist [ File.make (p "x.cs"); File.make (p "y.cs") ])
                                 ArtifactDep2 (FileTarget (File.make (dir </> "out" </> "a.dll")), DateTime(2020, 1, 2))
                                 Var ("V", Some "1") ] }
        let db, _ = openDbWith (opts dir) dbpath quiet
        Store r1 |> db.Post
        Store r2 |> db.Post
        close db
        Assert.AreEqual("0.7", version dbpath)

        let db, _ = openDbWith (opts dir) dbpath quiet
        let l1 = (get db r1.Targets.Head).Value
        let l2 = (get db r2.Targets.Head).Value
        close db
        Assert.AreEqual(r1, l1)
        Assert.AreEqual(r2, l2)
        let fileOf = function FileDep (f, _) -> f | _ -> failwith "not a FileDep"
        Assert.IsTrue(obj.ReferenceEquals(fileOf l1.Depends.Head, fileOf l2.Depends.Head), "one File per distinct path")
        Assert.AreEqual(p "x.cs", (fileOf l1.Depends.Head).Name, "a loaded File's Name is its full path")

    [<Test>]
    member _.``each path is written once``() =
        let dir = freshDir "once"
        let dbpath = dir </> ".xake"
        let deps = [ for i in 1 .. 50 -> dir </> "src" </> sprintf "a-rather-long-source-file-name-%03d.cs" i ]
        let db, _ = openDbWith (opts dir) dbpath quiet
        for t in 1 .. 20 do Store (mkResult (dir </> "out" </> sprintf "t%d.dll" t) deps) |> db.Post
        close db
        let text = Text.Encoding.UTF8.GetString(File.ReadAllBytes dbpath)
        let count (s: string) = (text.Length - text.Replace(s, "").Length) / s.Length
        Assert.AreEqual(1, count "a-rather-long-source-file-name-007.cs")
        Assert.AreEqual(1, count dir, "the full project path only in its RootRec: paths are relative to $(ProjectRoot)")

    [<Test>]
    member _.``a failed store commits no ids, the next store writes the paths again``() =
        let dir = freshDir "failedstore"
        let dbpath = dir </> ".xake"
        let log = Collections.Concurrent.ConcurrentQueue<string>()
        let shared = dir </> "src" </> "shared.cs"
        // a null variable name makes the pickler throw after the paths got tentative ids
        let bad = { mkResult (dir </> "out" </> "bad.dll") [ shared ] with
                      Depends = [ FileDep (File.make shared, DateTime(2020, 1, 1)); Var (null, None) ] }
        let good = mkResult (dir </> "out" </> "good.dll") [ shared ]
        let db, _ = openDbWith (opts dir) dbpath (collecting log)
        Store bad |> db.Post
        Store good |> db.Post
        close db
        Assert.That(log, Has.Some.Contains "failed to store the result of")
        let log2 = Collections.Concurrent.ConcurrentQueue<string>()
        let db, _ = openDbWith (opts dir) dbpath (collecting log2)
        Assert.AreEqual(Some good, get db good.Targets.Head)
        Assert.AreEqual(None, get db bad.Targets.Head)
        close db
        Assert.That(log2, Has.None.Contains "damaged")
        Assert.That(log2, Has.None.Contains "Failed to read database")

    [<Test>]
    member _.``a torn tail is dropped and the whole records kept``() =
        let dir = freshDir "torn"
        let dbpath = dir </> ".xake"
        let r1 = mkResult (dir </> "out" </> "a.dll") [ dir </> "src" </> "a.cs" ]
        let r2 = mkResult (dir </> "out" </> "b.dll") [ dir </> "src" </> "b.cs" ]
        let db, _ = openDbWith (opts dir) dbpath quiet
        Store r1 |> db.Post
        Store r2 |> db.Post
        close db
        // cut the last record in half (the crash case), then add a frame that claims too much
        let bytes = File.ReadAllBytes dbpath
        File.WriteAllBytes(dbpath, Array.append (Array.sub bytes 0 (bytes.Length - 5)) [| 0x7Fuy |])
        let log = Collections.Concurrent.ConcurrentQueue<string>()
        let db, _ = openDbWith (opts dir) dbpath (collecting log)
        Assert.AreEqual(Some r1, get db r1.Targets.Head)
        Assert.AreEqual(None, get db r2.Targets.Head)
        Store r2 |> db.Post
        close db
        Assert.That(log, Has.Some.Contains "Recovering database")
        // the rewritten file reads cleanly, with the result stored after the recovery
        let log2 = Collections.Concurrent.ConcurrentQueue<string>()
        let db, _ = openDbWith (opts dir) dbpath (collecting log2)
        Assert.AreEqual(Some r1, get db r1.Targets.Head)
        Assert.AreEqual(Some r2, get db r2.Targets.Head)
        close db
        Assert.That(log2, Has.None.Contains "Recovering")

    [<Test>]
    member _.``concurrent stores read back identically whatever the order``() =
        let dir = freshDir "concurrent"
        let shared = [ for i in 1 .. 30 -> dir </> "src" </> sprintf "s%d.cs" i ]
        let results =
            [ for t in 1 .. 40 ->
                mkResult (dir </> "out" </> sprintf "t%d.dll" t)
                    (shared @ [ dir </> "src" </> sprintf "own%d.cs" t; dir </> "gen" </> sprintf "g%d" (t % 7) </> "x.cs" ]) ]
        let storeAll (dbpath: string) (order: BuildResult list) =
            let db, _ = openDbWith (opts dir) dbpath quiet
            order |> List.map (fun r -> async { Store r |> db.Post }) |> Async.Parallel |> Async.RunSynchronously |> ignore
            close db
        storeAll (dir </> "one.xake") results
        storeAll (dir </> "two.xake") (List.rev results)
        let read p = readDatabase (opts dir) p |> snd |> List.sortBy (fun r -> r.Targets.Head.FullName)
        let one, two = read (dir </> "one.xake"), read (dir </> "two.xake")
        Assert.AreEqual(40, one.Length)
        Assert.AreEqual(one, two)
        Assert.AreEqual(results |> List.sortBy (fun r -> r.Targets.Head.FullName), one)

    [<Test>]
    member _.``a 0.6 database upgrades to 0.7 with the same results``() =
        let dir = freshDir "upgrade"
        let dbpath = dir </> ".xake"
        let r1 = mkResult (dir </> "out" </> "a.dll") [ dir </> "src" </> "a.cs" ]
        let r2 = { makeResult [ PhonyAction "main" ] with Depends = [ ArtifactDep2 (r1.Targets.Head, DateTime(2020, 1, 3)) ] }
        OldDatabase.write dbpath "0.6" [ r1; r2 ]
        let log = Collections.Concurrent.ConcurrentQueue<string>()
        let db, _ = openDbWith (opts dir) dbpath (collecting log)
        Assert.AreEqual(Some r1, get db r1.Targets.Head)
        Assert.AreEqual(Some r2, get db r2.Targets.Head)
        close db
        Assert.That(log, Has.Some.Contains "Upgrading database to version 0.7")
        Assert.AreEqual("0.7", version dbpath)
        let _, results = readDatabase (opts dir) dbpath
        Assert.AreEqual(2, results.Length)

    [<Test>]
    member _.``read-only open neither upgrades nor appends``() =
        let dir = freshDir "readonly"
        let dbpath = dir </> ".xake"
        let r1 = mkResult (dir </> "out" </> "a.dll") [ dir </> "src" </> "a.cs" ]
        OldDatabase.write dbpath "0.6" [ r1 ]
        let before = File.ReadAllBytes dbpath
        let db, _ = openDbWith { opts dir with ReadOnly = true } dbpath quiet
        Assert.AreEqual(Some r1, get db r1.Targets.Head)
        Store (mkResult (dir </> "out" </> "b.dll") []) |> db.Post
        close db
        CollectionAssert.AreEqual(before, File.ReadAllBytes dbpath)

    [<Test>]
    member _.``a database newer than this Xake is not read``() =
        let dir = freshDir "newer"
        let dbpath = dir </> ".xake"
        OldDatabase.write dbpath "0.9" []
        let log = Collections.Concurrent.ConcurrentQueue<string>()
        let db, _ = openDbWith (opts dir) dbpath (collecting log)
        close db
        Assert.That(log, Has.Some.Contains "newer than this Xake reads")

/// Size: a synthetic 0.6 database shaped like the measured one, scaled down 10x (paths of 114 bytes,
/// about 20 references per distinct path, one file list per result), upgraded to 0.7.
[<TestFixture>]
type ``Database paths: size``() =

    /// results x references, distinct files in directories; mean path length 114 bytes
    static member Generate (dir: string) (results: int) (refsPerResult: int) (distinctFiles: int) (dirs: int) =
        let rnd = Random(42)
        let root = dir </> "pkgs"
        let pad (s: string) (n: int) = if s.Length >= n then s.Substring(0, n) else s + String('x', n - s.Length)
        let dirNames = [| for d in 0 .. dirs - 1 -> root </> sprintf "package.%04d" d </> "1.0.0" </> "lib" </> "netstandard2.0" |]
        let files =
            [| for i in 0 .. distinctFiles - 1 ->
                let d = dirNames.[i % dirs]
                let stem = sprintf "Assembly.Name.%05d" i
                let len = max (stem.Length + 4) (114 - d.Length - 1)
                d </> pad stem (len - 4) + ".dll" |]
        [ for r in 0 .. results - 1 ->
            let picked = [ for _ in 1 .. refsPerResult -> files.[rnd.Next files.Length] ]
            let half = refsPerResult / 2
            { makeResult [ FileTarget (File.make (dir </> "out" </> sprintf "cfg%d" (r % 4) </> sprintf "Project%03d.dll" r)) ] with
                Depends =
                    (picked |> List.take half |> List.map (fun f -> FileDep (File.make f, DateTime(2020, 1, 1))))
                    @ [ GetFiles (Fileset.Empty, Fileset.Filelist (picked |> List.skip half |> List.map File.make)) ] } ]

    [<Test>]
    member _.``the 0.7 file is at least 5x smaller than 0.6``() =
        let dir = freshDir "size"
        let dbpath = dir </> ".xake"
        let results = ``Database paths: size``.Generate dir 60 1200 3600 350
        OldDatabase.write dbpath "0.6" results
        let before = FileInfo(dbpath).Length
        let db, _ = openDbWith (opts dir) dbpath quiet
        close db
        let after = FileInfo(dbpath).Length
        TestContext.Progress.WriteLine(sprintf "0.6: %d bytes, 0.7: %d bytes, %.1fx" before after (float before / float after))
        Assert.AreEqual("0.7", version dbpath)
        Assert.GreaterOrEqual(float before / float after, 5.0)
        let _, loaded = readDatabase (opts dir) dbpath
        Assert.AreEqual(results.Length, loaded.Length)

    /// The measurement of the brief: set XAKE_DB_MEASURE to a copy of a real database (0.6; and
    /// XAKE_DB_MEASURE_ROOT to its project root) and run explicitly; without it a full-scale synthetic
    /// database is used. Prints sizes and load times (cold: first open in the process, warm: best of 3).
    [<Test; Explicit>]
    member _.``measure a real database``() =
        let dir = freshDir "measure"
        let projectRoot = match Environment.GetEnvironmentVariable "XAKE_DB_MEASURE_ROOT" with null | "" -> dir | r -> r
        let dbpath = dir </> ".xake"
        match Environment.GetEnvironmentVariable "XAKE_DB_MEASURE" with
        | null | "" ->
            // no real database given: a full-scale synthetic one (600 results x 1 200 references, 36 000 files)
            OldDatabase.write dbpath "0.6" (``Database paths: size``.Generate dir 600 1200 36000 3500)
        | source -> File.Copy(source, dbpath, true)
        let time f =
            let sw = Diagnostics.Stopwatch.StartNew()
            f ()
            sw.Elapsed.TotalMilliseconds
        let openClose () = let db, _ = openDbWith { opts projectRoot with ReadOnly = true } dbpath quiet in close db
        let before = FileInfo(dbpath).Length
        let cold06 = time openClose
        let warm06 = [ for _ in 1 .. 3 -> time openClose ] |> List.min
        (let db, _ = openDbWith (opts projectRoot) dbpath quiet in close db)
        let after = FileInfo(dbpath).Length
        let warm07 = [ for _ in 1 .. 3 -> time openClose ] |> List.min
        TestContext.Progress.WriteLine(
            sprintf "size %d -> %d bytes (%.1fx); load 0.6 cold %.0f ms, warm %.0f ms; 0.7 warm %.0f ms"
                before after (float before / float after) cold06 warm06 warm07)

/// Roots and relocation through scripts.
[<TestFixture>]
type ``Database paths: relocation``() =

    let baseOpts root =
        { ExecOptions.Default with
            Threads = 1; Targets = ["main"]; ConLogLevel = Silent; FileLog = ""; FileLogLevel = Silent
            ProjectRoot = root; ResetDb = false; ThrowOnError = true; Nologo = true }

    /// main -> out/a.txt (needs src/a.txt) and out/list.txt (a file list of src/*.txt); `ext` adds a
    /// dependency on a file of an external folder when given.
    let script (o: ExecOptions) (runs: int ref) (ext: string option) =
        xake o {
            rules [
                "main" <== [ "out/a.txt"; "out/list.txt" ]
                "out/a.txt" ..> recipe {
                    do! need [ "src/a.txt" ]
                    match ext with
                    | Some e -> do! need [ e </> "tool.txt" ]
                    | None -> ()
                    runs.Value <- runs.Value + 1
                    do! writeText "a"
                }
                "out/list.txt" ..> recipe {
                    let! files = !!"src/*.txt" |> getFiles
                    runs.Value <- runs.Value + 1
                    do! writeText (string (Seq.length (let (Fileset.Filelist l) = files in l)))
                }
            ]
        }

    let makeProject (dir: string) =
        Directory.CreateDirectory(dir </> "src") |> ignore
        File.WriteAllText(dir </> "src" </> "a.txt", "a")
        File.WriteAllText(dir </> "src" </> "b.txt", "b")

    let makeExt (dir: string) =
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText(dir </> "tool.txt", "tool")

    [<Test>]
    member _.``a project built with a 0.6 database rebuilds nothing after the upgrade``() =
        let dir = freshDir "upgrade-script"
        makeProject dir
        let runs = ref 0
        withCwd dir (fun () -> script (baseOpts dir) runs None)
        Assert.AreEqual(2, runs.Value)
        // the same records as a 0.6 file (paths as strings)
        let _, results = readDatabase (opts dir) (dir </> ".xake")
        OldDatabase.write (dir </> ".xake") "0.6" results
        let log = Collections.Concurrent.ConcurrentQueue<string>()
        withCwd dir (fun () -> script { baseOpts dir with CustomLogger = collecting log } runs None)
        Assert.That(log, Has.Some.Contains "Upgrading database to version 0.7")
        Assert.AreEqual(2, runs.Value, "nothing rebuilt after the upgrade")
        Assert.AreEqual("0.7", version (dir </> ".xake"))

    [<Test>]
    member _.``a moved working copy rebuilds nothing``() =
        let top = freshDir "move"
        let a, b = top </> "A", top </> "B"
        makeProject a
        let runs = ref 0
        withCwd a (fun () -> script (baseOpts a) runs None)
        Assert.AreEqual(2, runs.Value)
        copyTree a b
        let log = Collections.Concurrent.ConcurrentQueue<string>()
        withCwd b (fun () -> script { baseOpts b with CustomLogger = collecting log } runs None)
        Assert.AreEqual(2, runs.Value, "nothing rebuilt after the move")
        Assert.That(log, Has.Some.Contains "root $(ProjectRoot) moved")

    [<Test>]
    member _.``a declared root pointed at a copied folder rebuilds nothing``() =
        let top = freshDir "rootmove"
        let proj, e1, e2 = top </> "proj", top </> "ext1", top </> "ext2"
        makeProject proj
        makeExt e1
        let runs = ref 0
        withCwd proj (fun () -> script { baseOpts proj with PathRoots = [ "EXT", RootPath e1 ] } runs (Some e1))
        Assert.AreEqual(2, runs.Value)
        copyTree e1 e2
        withCwd proj (fun () -> script { baseOpts proj with PathRoots = [ "EXT", RootPath e2 ] } runs (Some e2))
        Assert.AreEqual(2, runs.Value, "nothing rebuilt after the root moved")

    [<Test>]
    member _.``a relative root moves with the project``() =
        let top = freshDir "relroot"
        let a, b = top </> "A", top </> "B"
        // the script lives in build/, a tool in ../src: a relative root ".."
        makeProject (a </> "build")
        Directory.CreateDirectory(a </> "src") |> ignore
        let runs = ref 0
        let run (dir: string) =
            withCwd (dir </> "build") (fun () ->
                script { baseOpts (dir </> "build") with PathRoots = [ "REPO", RootPath ".." ] } runs (Some (dir </> "src")))
        File.WriteAllText(a </> "src" </> "tool.txt", "tool")
        run a
        Assert.AreEqual(2, runs.Value)
        copyTree a b
        run b
        Assert.AreEqual(2, runs.Value, "nothing rebuilt: ../src is under $(REPO)")

    [<Test>]
    member _.``a root from a variable``() =
        let top = freshDir "varroot"
        let proj, e1 = top </> "proj", top </> "ext1"
        makeProject proj
        makeExt e1
        let runs = ref 0
        let o = { baseOpts proj with PathRoots = [ "EXT", fromVar "EXT_DIR" ]; ScriptVars = [ "EXT_DIR", e1 ] }
        withCwd proj (fun () -> script o runs (Some e1))
        withCwd proj (fun () -> script o runs (Some e1))
        Assert.AreEqual(2, runs.Value)
        let text = Text.Encoding.UTF8.GetString(File.ReadAllBytes(proj </> ".xake"))
        Assert.IsTrue(text.Contains "$(EXT)", "the external file is stored under $(EXT)")

    [<Test>]
    member _.``an undeclared root is an error, its targets rebuild, its records come back``() =
        let top = freshDir "undeclared"
        let proj, e1 = top </> "proj", top </> "ext1"
        makeProject proj
        makeExt e1
        let runs = ref 0
        let declared = { baseOpts proj with PathRoots = [ "EXT", RootPath e1 ] }
        withCwd proj (fun () -> script declared runs (Some e1))
        Assert.AreEqual(2, runs.Value)

        // run 2: the root is not declared; damage the tail so the file is rewritten in this run
        File.AppendAllText(proj </> ".xake", "\u007F")
        let log = Collections.Concurrent.ConcurrentQueue<string>()
        withCwd proj (fun () ->
            script { baseOpts proj with Targets = [ "out/list.txt" ]; CustomLogger = collecting log } runs (Some e1))
        Assert.That(log, Has.Some.Contains "[ERROR]")
        Assert.That(log, Has.Some.Contains "records name the root $(EXT)")
        Assert.That(log, Has.Some.Contains "Recovering database")
        Assert.AreEqual(2, runs.Value, "out/list.txt does not name $(EXT): still up to date")

        // run 3: declared again, the carried record of out/a.txt is back: nothing rebuilds
        withCwd proj (fun () -> script declared runs (Some e1))
        Assert.AreEqual(2, runs.Value)

        // run 4: undeclared and requested: out/a.txt rebuilds, the run succeeds
        withCwd proj (fun () -> script (baseOpts proj) runs (Some e1))
        Assert.AreEqual(3, runs.Value)

    [<Test>]
    member _.``a root declared later re-roots the stored paths``() =
        let top = freshDir "reroot"
        let proj, e1, e2 = top </> "proj", top </> "ext1", top </> "ext2"
        makeProject proj
        makeExt e1
        let runs = ref 0
        withCwd proj (fun () -> script (baseOpts proj) runs (Some e1))
        let log = Collections.Concurrent.ConcurrentQueue<string>()
        withCwd proj (fun () ->
            script { baseOpts proj with PathRoots = [ "EXT", RootPath e1 ]; CustomLogger = collecting log } runs (Some e1))
        Assert.AreEqual(2, runs.Value)
        Assert.That(log, Has.Some.Contains "Re-rooting database paths")
        copyTree e1 e2
        withCwd proj (fun () -> script { baseOpts proj with PathRoots = [ "EXT", RootPath e2 ] } runs (Some e2))
        Assert.AreEqual(2, runs.Value, "relocatable after the re-root")

    [<Test>]
    member _.``files outside all roots are counted with a hint``() =
        let top = freshDir "outside"
        let proj, e1 = top </> "proj", top </> "ext1"
        makeProject proj
        makeExt e1
        let runs = ref 0
        withCwd proj (fun () -> script (baseOpts proj) runs (Some e1))
        let log = Collections.Concurrent.ConcurrentQueue<string>()
        withCwd proj (fun () -> script { baseOpts proj with CustomLogger = collecting log } runs (Some e1))
        Assert.That(log, Has.Some.Contains "1 files outside all roots")
        Assert.That(log, Has.Some.Contains "pathRoot")

    [<Test>]
    member _.``invalid roots are launch errors and open no database``() =
        let top = freshDir "badroots"
        makeProject top
        let runs = ref 0
        let fails (roots: (string * RootValue) list) (expected: string) =
            let ex = Assert.Throws<XakeException>(fun () ->
                withCwd top (fun () -> script { baseOpts top with PathRoots = roots } runs None))
            StringAssert.Contains(expected, ex.Message)
        fails [ "A", RootPath (top </> "x"); "B", RootPath "x" ] "name the same folder"
        fails [ "A", RootPath "." ] "is the project root"
        fails [ "ProjectRoot", RootPath "/x" ] "taken by the project root"
        fails [ "A-B", RootPath "/x" ] "letters, digits"
        fails [ "A", RootPath "/x"; "A", RootPath "/y" ] "more than once"
        Assert.IsFalse(File.Exists(top </> ".xake"), "no database opened")
        Assert.AreEqual(0, runs.Value)

    [<Test>]
    member _.``a secret variable cannot be a root``() =
        let top = freshDir "secretroot"
        makeProject top
        let runs = ref 0
        let ex = Assert.Throws<XakeException>(fun () ->
            withCwd top (fun () ->
                xake { baseOpts top with PathRoots = [ "S", fromVar "SECRET_DIR" ] } {
                    var "SECRET_DIR" [ Default "/tmp/x"; Secret ]
                    phony "main" (recipe { runs.Value <- runs.Value + 1 })
                }))
        StringAssert.Contains("is secret", ex.Message)

    [<Test>]
    member _.``an unset variable root is undeclared, not an error``() =
        let top = freshDir "unsetroot"
        makeProject top
        let runs = ref 0
        withCwd top (fun () -> script { baseOpts top with PathRoots = [ "EXT", fromVar "XAKE_TEST_NOT_SET_DIR" ] } runs None)
        Assert.AreEqual(2, runs.Value)
