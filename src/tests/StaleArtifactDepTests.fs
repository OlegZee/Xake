namespace Tests

open System.IO
open System.IO.Compression
open NUnit.Framework

open Xake
open Xake.Tasks

/// Writes `.xake` files the way Xake 4.x did (databases 0.4-0.6, the legacy pickler kept in
/// Xake for the upgrade) and reads any version through the public read API.
module internal OldDatabase =
    open Xake.Storage.Persist

    let private headerPU = Pickler.triple Pickler.str Pickler.str Pickler.date
    let private root () = Directory.GetCurrentDirectory()

    /// the version in the header
    let version (path: string) =
        use r = new BinaryReader(File.OpenRead path)
        let (_, ver, _) = headerPU.unpickle r
        ver

    /// header version and the results in file order (an older version: converted)
    let read (path: string) =
        version path,
        Storage.readRecords (root ()) path |> Seq.choose (function Storage.DbRecord.Result r -> Some r | _ -> None) |> List.ofSeq

    /// writes a 0.4-0.6 file of the given results with their old dependencies
    let writeOld (path: string) (ver: string) (results: OldResult list) =
        use w = new BinaryWriter(File.Create path)
        headerPU.pickle ("XAKE", ver, System.DateTime.Now) w
        let pu = legacyResult (root ())
        results |> List.iter (fun r -> pu.pickle r w)

    /// writes a 0.4-0.6 file of results without file or target dependencies
    let write (path: string) (ver: string) (results: BuildResult list) =
        results
        |> List.map (fun r -> { OTargets = r.Targets; OBuilt = r.Built; ODepends = r.Depends |> List.map Plain; OSteps = r.Steps })
        |> writeOld path ver

/// The 0.7 format in memory: records written by the engine's writer and read back.
module internal DbFormat =
    open Xake.Storage.Persist

    /// writes the records to a fresh 0.7 stream and reads them back (paths against `root`)
    let roundTrip (root: string) (recs: Rec list) =
        use stream = new MemoryStream()
        writeHeader stream
        let writer = Writer(PathTable root, stream, raise)
        recs |> List.iter (fun r -> writer.Append [ r ])
        let bytes = stream.ToArray()
        bytes, read root (new MemoryStream(bytes))

    /// a result written and read back
    let result (r: BuildResult) =
        match (roundTrip "/p" [ ResultRec r ] |> snd).Records with
        | [ Storage.DbRecord.Result r ] -> r
        | other -> failwithf "unexpected %A" other

/// S66: a dependent must be rebuilt when its dependee (a rule-built file) was rebuilt
/// by an earlier, separate run. Each `xake` call below is a separate session that shares
/// the same `.xake` database (ResetDb = false).
[<TestFixture>]
type ``StaleArtifactDep tests``() =
    inherit XakeTestBase("staleartifactdep")

    let copyRule (dst: string) (src: string) (counter: int ref) =
        dst ..> recipe {
            do! need [src]
            counter.Value <- counter.Value + 1
            do! writeText (File.ReadAllText src)
        }

    /// in.txt -> out/a.txt -> out/b.txt; "build" needs a only, "deploy" needs b only
    let script options (prefix: string) aRuns bRuns targets =
        let inp, a, b = prefix + "in.txt", "out" </> prefix + "a.txt", "out" </> prefix + "b.txt"
        xake { options with Targets = targets; ResetDb = false } {
            rules [
                "build" <== [a]
                "deploy" <== [b]
                copyRule a inp aRuns
                copyRule b a bRuns
            ]
        }

    /// the engine compares write times with a 10 ms tolerance: keep edits well apart
    let edit (path: string) (text: string) =
        System.Threading.Thread.Sleep 100
        File.WriteAllText(path, text)

    let prepare (prefix: string) =
        try Directory.Delete("out", true) with _ -> ()
        File.WriteAllText(prefix + "in.txt", "v1")

    [<Test>]
    member x.``two-step: dependent is rebuilt after dependee was rebuilt by an earlier run``() =
        let aRuns, bRuns = ref 0, ref 0
        prepare "s2_"
        do script x.TestOptions "s2_" aRuns bRuns ["deploy"]          // session 1
        Assert.AreEqual((1, 1), (aRuns.Value, bRuns.Value))

        edit "s2_in.txt" "v2"
        do script x.TestOptions "s2_" aRuns bRuns ["build"]           // session 2: a only
        Assert.AreEqual((2, 1), (aRuns.Value, bRuns.Value))
        Assert.AreEqual("v2", File.ReadAllText("out/s2_a.txt"))

        do script x.TestOptions "s2_" aRuns bRuns ["deploy"]          // session 3
        Assert.AreEqual("v2", File.ReadAllText("out/s2_b.txt"), "b.txt is stale (S66)")
        Assert.AreEqual(2, bRuns.Value)

        do script x.TestOptions "s2_" aRuns bRuns ["deploy"]          // session 4: nothing to do
        do script x.TestOptions "s2_" aRuns bRuns ["build"]
        Assert.AreEqual((2, 2), (aRuns.Value, bRuns.Value), "no spurious rebuilds")

    [<Test>]
    member x.``control: build and deploy in one run rebuilds the dependent``() =
        let aRuns, bRuns = ref 0, ref 0
        prepare "s1_"
        do script x.TestOptions "s1_" aRuns bRuns ["deploy"]
        edit "s1_in.txt" "v2"
        do script x.TestOptions "s1_" aRuns bRuns ["build"; "deploy"]
        Assert.AreEqual("v2", File.ReadAllText("out/s1_b.txt"))
        Assert.AreEqual((2, 2), (aRuns.Value, bRuns.Value))

    [<Test>]
    member x.``control: dependent on a plain file is rebuilt after the file changed between runs``() =
        let bRuns = ref 0
        File.WriteAllText("pf_a.txt", "v1")
        let run () =
            xake { x.TestOptions with Targets = ["deploy"]; ResetDb = false } {
                rules [
                    "deploy" <== ["out/pf_b.txt"]
                    copyRule ("out" </> "pf_b.txt") "pf_a.txt" bRuns
                ]
            }
        do run ()
        edit "pf_a.txt" "v2"        // changed by "someone else" (no rule)
        do run ()
        Assert.AreEqual("v2", File.ReadAllText("out/pf_b.txt"))
        Assert.AreEqual(2, bRuns.Value)

    /// The owner's scenario: build (dll via a rule), edit the source, build, then pack
    /// (a rule that needs the dll and zips it), each in a separate session.
    [<Test>]
    member x.``build, edit, build, pack: the package carries the new dll``() =
        let dllRuns, packRuns = ref 0, ref 0
        try Directory.Delete("out", true) with _ -> ()
        Directory.CreateDirectory "src" |> ignore
        File.WriteAllText("src/app.fs", "let v = 1")

        let readPackage () =
            use zip = System.IO.Compression.ZipFile.OpenRead "out/app.nupkg"
            use reader = new StreamReader((zip.GetEntry "lib/app.dll").Open())
            reader.ReadToEnd()

        let run target =
            xake { x.TestOptions with Targets = [target]; ResetDb = false } {
                rules [
                    "build" <== ["out/app.dll"]
                    "pack" <== ["out/app.nupkg"]
                    "out/app.dll" ..> recipe {
                        do! need ["src/app.fs"]
                        dllRuns.Value <- dllRuns.Value + 1
                        do! writeText ("compiled: " + File.ReadAllText "src/app.fs")
                    }
                    "out/app.nupkg" ..> recipe {
                        do! need ["out/app.dll"]
                        packRuns.Value <- packRuns.Value + 1
                        let! pkg = getTargetFile()
                        File.Delete pkg.FullName
                        use zip = System.IO.Compression.ZipFile.Open(pkg.FullName, System.IO.Compression.ZipArchiveMode.Create)
                        zip.CreateEntryFromFile("out/app.dll", "lib/app.dll") |> ignore
                    }
                ]
            }

        run "build"
        run "pack"
        Assert.AreEqual("compiled: let v = 1", readPackage ())

        edit "src/app.fs" "let v = 2"
        run "build"
        Assert.AreEqual((2, 1), (dllRuns.Value, packRuns.Value))
        run "pack"
        Assert.AreEqual("compiled: let v = 2", readPackage (), "the package is stale (S66)")
        Assert.AreEqual((2, 2), (dllRuns.Value, packRuns.Value))

        run "pack"
        run "build"
        Assert.AreEqual((2, 2), (dllRuns.Value, packRuns.Value), "no spurious rebuilds")

    /// A phony dependee (=>, not AlwaysRerun) re-run by an earlier session: its stamp is its completion time.
    [<Test>]
    member x.``phony dependee re-run by an earlier run triggers its dependent``() =
        let genRuns, cRuns = ref 0, ref 0
        try Directory.Delete("out", true) with _ -> ()
        File.WriteAllText("ph_in.txt", "v1")
        let run targets =
            xake { x.TestOptions with Targets = targets; ResetDb = false } {
                rules [
                    "deploy" <== ["out/ph_c.txt"]
                    "gen" => recipe {
                        do! need ["ph_in.txt"]
                        genRuns.Value <- genRuns.Value + 1
                        File.WriteAllText("ph_gen.txt", File.ReadAllText "ph_in.txt")
                    }
                    "out/ph_c.txt" ..> recipe {
                        do! need ["gen"]
                        cRuns.Value <- cRuns.Value + 1
                        do! writeText (File.ReadAllText "ph_gen.txt")
                    }
                ]
            }
        run ["deploy"]
        edit "ph_in.txt" "v2"
        run ["gen"]
        Assert.AreEqual((2, 1), (genRuns.Value, cRuns.Value))
        run ["deploy"]
        Assert.AreEqual("v2", File.ReadAllText "out/ph_c.txt")
        run ["deploy"]
        Assert.AreEqual((2, 2), (genRuns.Value, cRuns.Value), "no spurious rebuilds")

    /// A database written by Xake 4.x (0.6) loads and is rewritten as 0.7: a no-change run
    /// rebuilds nothing; a 0.4 stampless ArtifactDep reruns its dependent once.
    [<Test>]
    member x.``database 0.6 upgrades without a rebuild, a 0.4 ArtifactDep reruns once``() =
        let aRuns, bRuns = ref 0, ref 0
        prepare "up_"
        do script x.TestOptions "up_" aRuns bRuns ["deploy"]
        Assert.AreEqual((1, 1), (aRuns.Value, bRuns.Value))

        // the same state as Xake 4.x would have written it: stamps of the files as they are
        let stamp (p: string) = File.GetLastWriteTime p
        let file (p: string) = File.make (Directory.GetCurrentDirectory() </> p)
        let res targets deps = { Storage.Persist.OTargets = targets; Storage.Persist.OBuilt = System.DateTime.Now; Storage.Persist.ODepends = deps; Storage.Persist.OSteps = [] }
        let a, b = file "out/up_a.txt", file "out/up_b.txt"
        let old06 artifact =
            [ res [ PhonyAction "deploy" ] [ artifact (FileTarget b) ]
              res [ FileTarget a ] [ Storage.Persist.OldFile (file "up_in.txt", stamp "up_in.txt") ]
              res [ FileTarget b ] [ artifact (FileTarget a) ] ]
        let artifact2 t = match t with FileTarget f -> Storage.Persist.OldArtifact2 (t, stamp f.FullName) | _ -> failwith "file"
        OldDatabase.writeOld ".xake" "0.6" (old06 artifact2)
        do script x.TestOptions "up_" aRuns bRuns ["deploy"]
        Assert.AreEqual((1, 1), (aRuns.Value, bRuns.Value), "a no-change run after the upgrade rebuilds nothing")
        Assert.AreEqual(BuildLog.XakeDbVersion, OldDatabase.version ".xake")

        // a 0.6 result that saw an older stamp reruns
        let older t = match t with FileTarget f -> Storage.Persist.OldArtifact2 (t, (stamp f.FullName).AddSeconds -5.0) | _ -> failwith "file"
        OldDatabase.writeOld ".xake" "0.6" [ res [ PhonyAction "deploy" ] [ artifact2 (FileTarget b) ]
                                             res [ FileTarget a ] [ Storage.Persist.OldFile (file "up_in.txt", stamp "up_in.txt") ]
                                             res [ FileTarget b ] [ older (FileTarget a) ] ]
        do script x.TestOptions "up_" aRuns bRuns ["deploy"]
        Assert.AreEqual((1, 2), (aRuns.Value, bRuns.Value), "b saw an older a: rerun")

        // 0.4: stampless ArtifactDep, the dependent reruns once
        OldDatabase.writeOld ".xake" "0.4" (old06 Storage.Persist.OldArtifact)
        do script x.TestOptions "up_" aRuns bRuns ["deploy"]
        Assert.AreEqual((1, 3), (aRuns.Value, bRuns.Value), "old ArtifactDep counts as changed once; a is not rebuilt")
        let _, results = OldDatabase.read ".xake"
        let bDeps = results |> List.filter (fun r -> r.Targets = [ FileTarget b ]) |> List.last |> fun r -> r.Depends
        Assert.AreEqual([ TargetDep (FileTarget a) ], bDeps)
        do script x.TestOptions "up_" aRuns bRuns ["deploy"]
        Assert.AreEqual((1, 3), (aRuns.Value, bRuns.Value), "no further rebuilds")
