namespace Tests

open System.IO
open System.IO.Compression
open NUnit.Framework

open Xake
open Xake.Tasks

/// Reads and writes `.xake` files the way an older Xake did (database 0.4: no ArtifactDep2).
module OldDatabase =
    let private headerPU = Pickler.triple Pickler.str Pickler.str Pickler.date

    /// header version and all records in file order (later records win when loaded)
    let read (path: string) =
        use r = new BinaryReader(File.OpenRead path)
        let (_, ver, _) = headerPU.unpickle r
        ver, [ while r.BaseStream.Position < r.BaseStream.Length do yield Storage.resultPU.unpickle r ]

    let write (path: string) (ver: string) (results: BuildResult list) =
        use w = new BinaryWriter(File.Create path)
        headerPU.pickle ("XAKE", ver, System.DateTime.Now) w
        results |> List.iter (fun r -> Storage.resultPU.pickle r w)

    /// rewrites a database as a 0.4 one: every ArtifactDep2 becomes the stampless ArtifactDep
    let rewriteAs04 (path: string) =
        let downgrade = function ArtifactDep2 (t, _) -> ArtifactDep t | d -> d
        let _, results = read path
        results
        |> List.map (fun r -> { r with Depends = r.Depends |> List.map downgrade })
        |> write path "0.4"

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

    /// A database written by an older Xake (0.4, stampless ArtifactDep) loads; the dependent
    /// rebuilds once, records ArtifactDep2, and the file is rewritten as the current version.
    [<Test>]
    member x.``database 0.4 with ArtifactDep loads and is upgraded``() =
        let aRuns, bRuns = ref 0, ref 0
        prepare "up_"
        do script x.TestOptions "up_" aRuns bRuns ["deploy"]
        Assert.AreEqual((1, 1), (aRuns.Value, bRuns.Value))

        OldDatabase.rewriteAs04 ".xake"
        let ver, results = OldDatabase.read ".xake"
        Assert.AreEqual("0.4", ver)
        Assert.IsTrue(results |> List.exists (fun r -> r.Depends |> List.exists (function ArtifactDep _ -> true | _ -> false)))

        do script x.TestOptions "up_" aRuns bRuns ["deploy"]
        Assert.AreEqual((1, 2), (aRuns.Value, bRuns.Value), "old ArtifactDep counts as changed once; a is not rebuilt")

        let ver, results = OldDatabase.read ".xake"
        Assert.AreEqual(BuildLog.XakeDbVersion, ver)
        let bDeps =
            results |> List.filter (fun r -> r.Targets |> List.exists (fun t -> t.ShortName.EndsWith "up_b.txt"))
            |> List.last |> fun r -> r.Depends
        Assert.IsTrue(bDeps |> List.exists (function ArtifactDep2 (FileTarget f, _) -> f.Name.EndsWith "up_a.txt" | _ -> false), sprintf "%A" bDeps)

        do script x.TestOptions "up_" aRuns bRuns ["deploy"]
        Assert.AreEqual((1, 2), (aRuns.Value, bRuns.Value), "no further rebuilds")
