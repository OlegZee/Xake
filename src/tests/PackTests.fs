namespace Tests

open System
open System.IO
open NUnit.Framework
open Xake.Dotnet

[<TestFixture>]
type ``Deterministic pack``() =

    let testDir = Path.Combine (Path.GetTempPath (), "xake-pack-tests-" + Guid.NewGuid().ToString "N")

    [<SetUp>]
    member _.Setup () = Directory.CreateDirectory testDir |> ignore

    [<TearDown>]
    member _.Teardown () =
        if Directory.Exists testDir then Directory.Delete (testDir, true)

    /// three small files: two text, one binary with random-but-seeded bytes
    member private _.MakeFiles () =
        let a = Path.Combine (testDir, "a.txt")
        let b = Path.Combine (testDir, "b.txt")
        let c = Path.Combine (testDir, "c.bin")
        File.WriteAllText (a, "hello from a")
        File.WriteAllText (b, "hello from b, a bit longer so deflate has something to do")
        let rng = Random 42
        let bytes = Array.zeroCreate<byte> 256
        rng.NextBytes bytes
        File.WriteAllBytes (c, bytes)
        a, b, c

    [<Test>]
    member this.``two packs of the same inputs are byte-identical`` () =
        let a, b, c = this.MakeFiles ()
        let entries =
            [ { Pack.Path = "lib/a.txt"; Pack.Source = a }
              { Pack.Path = "lib/b.txt"; Pack.Source = b }
              { Pack.Path = "content/c.bin"; Pack.Source = c } ]
        let out1 = Path.Combine (testDir, "one.zip")
        let out2 = Path.Combine (testDir, "two.zip")
        Pack.zip out1 entries Pack.defaultOptions
        Pack.zip out2 entries Pack.defaultOptions

        CollectionAssert.AreEqual (File.ReadAllBytes out1, File.ReadAllBytes out2)

        let listed = Pack.entries out1
        let paths = listed |> List.map (fun (p, _, _, _) -> p)
        Assert.That (paths, Is.EqualTo (paths |> List.sortWith (fun x y -> String.CompareOrdinal (x, y))))
        for (_, _, _, dt) in listed do
            Assert.That (dt, Is.EqualTo Pack.defaultOptions.Timestamp)

    [<Test>]
    member this.``entry order and timestamps do not depend on the input order or file mtimes`` () =
        let a, b, c = this.MakeFiles ()
        let forward =
            [ { Pack.Path = "lib/a.txt"; Pack.Source = a }
              { Pack.Path = "lib/b.txt"; Pack.Source = b }
              { Pack.Path = "content/c.bin"; Pack.Source = c } ]
        let reversed = List.rev forward

        // perturb mtimes
        File.SetLastWriteTimeUtc (a, DateTime (2001, 2, 3, 4, 5, 6, DateTimeKind.Utc))
        File.SetLastWriteTimeUtc (b, DateTime (2020, 12, 31, 23, 59, 58, DateTimeKind.Utc))
        File.SetLastWriteTimeUtc (c, DateTime (1999, 1, 1, 0, 0, 0, DateTimeKind.Utc))

        let out1 = Path.Combine (testDir, "fwd.zip")
        let out2 = Path.Combine (testDir, "rev.zip")
        Pack.zip out1 forward Pack.defaultOptions
        Pack.zip out2 reversed Pack.defaultOptions

        CollectionAssert.AreEqual (File.ReadAllBytes out1, File.ReadAllBytes out2)

    [<Test>]
    member _.``nupkg has the OPC parts and a content-derived psmdcp GUID`` () =
        let nuspec = Path.Combine (testDir, "Test.Pkg.nuspec")
        File.WriteAllText (
            nuspec,
            "<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">\
             <metadata><id>Test.Pkg</id><version>1.2.3</version><authors>Xake</authors>\
             <description>test</description></metadata></package>")

        let dll = Path.Combine (testDir, "A.dll")
        File.WriteAllBytes (dll, [| 1uy; 2uy; 3uy; 4uy |])
        let files = [ { Pack.Path = "lib/netstandard2.0/A.dll"; Pack.Source = dll } ]

        let out1 = Path.Combine (testDir, "one.nupkg")
        let out2 = Path.Combine (testDir, "two.nupkg")
        Pack.nupkg out1 nuspec files Pack.defaultOptions
        Pack.nupkg out2 nuspec files Pack.defaultOptions

        let paths = Pack.entries out1 |> List.map (fun (p, _, _, _) -> p)
        Assert.That (paths, Does.Contain "Test.Pkg.nuspec")
        Assert.That (paths, Does.Contain "[Content_Types].xml")
        Assert.That (paths, Does.Contain "_rels/.rels")
        let psmdcpPath = paths |> List.find (fun p -> p.StartsWith "package/services/metadata/core-properties/" && p.EndsWith ".psmdcp")

        CollectionAssert.AreEqual (File.ReadAllBytes out1, File.ReadAllBytes out2)

        // read the psmdcp text back out of the zip
        let readEntryBytes (zipPath: string) (path: string) =
            use fs = File.OpenRead zipPath
            use archive = new System.IO.Compression.ZipArchive (fs, System.IO.Compression.ZipArchiveMode.Read)
            let entry = archive.GetEntry path
            use es = entry.Open ()
            use ms = new MemoryStream ()
            es.CopyTo ms
            ms.ToArray ()

        let psmdcpText = Text.Encoding.UTF8.GetString (readEntryBytes out1 psmdcpPath)
        Assert.That (psmdcpText, Does.Not.Contain "dcterms:created")

        // change one byte of A.dll -> the derived GUID changes
        File.WriteAllBytes (dll, [| 1uy; 2uy; 3uy; 5uy |])
        let out3 = Path.Combine (testDir, "three.nupkg")
        Pack.nupkg out3 nuspec files Pack.defaultOptions
        let psmdcpPath3 = Pack.entries out3 |> List.map (fun (p, _, _, _) -> p) |> List.find (fun p -> p.EndsWith ".psmdcp")
        Assert.That (psmdcpPath3, Is.Not.EqualTo psmdcpPath)

    [<Test; Category "Integration">]
    member this.``dotnet can read it`` () =
        let a, b, c = this.MakeFiles ()
        let entries =
            [ { Pack.Path = "lib/a.txt"; Pack.Source = a }
              { Pack.Path = "lib/b.txt"; Pack.Source = b }
              { Pack.Path = "content/c.bin"; Pack.Source = c } ]
        let out = Path.Combine (testDir, "roundtrip.zip")
        Pack.zip out entries Pack.defaultOptions

        let expected = Pack.entries out |> List.map (fun (p, _, _, _) -> p) |> List.sortWith (fun x y -> String.CompareOrdinal (x, y))

        use fs = File.OpenRead out
        use archive = new System.IO.Compression.ZipArchive (fs, System.IO.Compression.ZipArchiveMode.Read)
        let actual =
            archive.Entries
            |> Seq.map (fun e -> e.FullName)
            |> Seq.sortWith (fun x y -> String.CompareOrdinal (x, y))
            |> List.ofSeq

        Assert.That (actual, Is.EqualTo expected)
