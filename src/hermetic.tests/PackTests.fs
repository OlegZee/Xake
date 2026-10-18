namespace Tests

open System
open System.IO
open NUnit.Framework
open Xake.Dotnet
open Xake.Hermetic.Dotnet

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
        Pack.zip out1 entries Pack.Options.Default
        Pack.zip out2 entries Pack.Options.Default

        CollectionAssert.AreEqual (File.ReadAllBytes out1, File.ReadAllBytes out2)

        let listed = Pack.list out1
        let paths = listed |> List.map (fun (p, _, _, _) -> p)
        Assert.That (paths, Is.EqualTo (paths |> List.sortWith (fun x y -> String.CompareOrdinal (x, y))))
        for (_, _, _, dt) in listed do
            Assert.That (dt, Is.EqualTo Pack.Options.Default.Timestamp)

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
        Pack.zip out1 forward Pack.Options.Default
        Pack.zip out2 reversed Pack.Options.Default

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
        Pack.nupkg out1 nuspec files Pack.Options.Default
        Pack.nupkg out2 nuspec files Pack.Options.Default

        let paths = Pack.list out1 |> List.map (fun (p, _, _, _) -> p)
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
        Pack.nupkg out3 nuspec files Pack.Options.Default
        let psmdcpPath3 = Pack.list out3 |> List.map (fun (p, _, _, _) -> p) |> List.find (fun p -> p.EndsWith ".psmdcp")
        Assert.That (psmdcpPath3, Is.Not.EqualTo psmdcpPath)

    [<Test; Category "Integration">]
    member this.``dotnet can read it`` () =
        let a, b, c = this.MakeFiles ()
        let entries =
            [ { Pack.Path = "lib/a.txt"; Pack.Source = a }
              { Pack.Path = "lib/b.txt"; Pack.Source = b }
              { Pack.Path = "content/c.bin"; Pack.Source = c } ]
        let out = Path.Combine (testDir, "roundtrip.zip")
        Pack.zip out entries Pack.Options.Default

        let expected = Pack.list out |> List.map (fun (p, _, _, _) -> p) |> List.sortWith (fun x y -> String.CompareOrdinal (x, y))

        use fs = File.OpenRead out
        use archive = new System.IO.Compression.ZipArchive (fs, System.IO.Compression.ZipArchiveMode.Read)
        let actual =
            archive.Entries
            |> Seq.map (fun e -> e.FullName)
            |> Seq.sortWith (fun x y -> String.CompareOrdinal (x, y))
            |> List.ofSeq

        Assert.That (actual, Is.EqualTo expected)


/// The minimal pack from a project's own nuspec (S68): tokens from the description, `lib/<tfm>`
/// from flat output folders, the nuspec's `<files>`.
[<TestFixture>]
type ``Pack from a nuspec``() =

    let testDir = Path.Combine (Path.GetTempPath (), "xake-nuspec-tests-" + Guid.NewGuid().ToString "N")
    let at (p: string) = Path.Combine (testDir, p)
    let write (p: string) (text: string) =
        Directory.CreateDirectory (Path.GetDirectoryName (at p)) |> ignore
        File.WriteAllText (at p, text)
    let description =
        Description.parse "{ \"name\": \"Acme.Core\", \"project\": \"Acme.Core.csproj\", \"language\": \"csharp\", \"frameworks\": [ \"net8.0\" ], \"sources\": [ \"**/*.cs\" ], \"package\": { \"id\": \"Acme.Core\", \"authors\": \"Acme & Co\", \"description\": \"Core\" } }"
    let entryText (zip: string) (name: string) =
        use z = Compression.ZipFile.OpenRead zip
        use r = new StreamReader ((z.GetEntry name).Open ())
        r.ReadToEnd ()

    [<SetUp>]
    member _.Setup () = Directory.CreateDirectory testDir |> ignore

    [<TearDown>]
    member _.Teardown () =
        if Directory.Exists testDir then Directory.Delete (testDir, true)

    [<Test>]
    member _.``tokens come from the description and the script; lib from flat output folders`` () =
        let tokens = Pack.tokensOf description "1.2.3"
        Assert.That(tokens.["id"], Is.EqualTo "Acme.Core")
        Assert.That(tokens.["version"], Is.EqualTo "1.2.3")
        Assert.That(tokens.["authors"], Is.EqualTo "Acme & Co")
        Assert.That(tokens.["assemblyName"], Is.EqualTo "Acme.Core")
        write "out/net8.0/Acme.Core.dll" "dll8"
        write "out/net8.0/Acme.Core.xml" "<doc/>"
        write "out/net462/Acme.Core.dll" "dll4"
        write "pkg/Acme.Core.nuspec"
            ("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">\n  <metadata>\n"
             + "    <id>$id$$suffix$</id>\n    <version>$version$</version>\n    <authors>$Authors$</authors>\n    <description>$description$</description>\n"
             + "  </metadata>\n</package>\n")
        let lib = Pack.libOf "Acme.Core" [ "net8.0", at "out/net8.0"; "net462", at "out/net462" ]
        Assert.That(lib |> List.map (fun e -> e.Path), Is.EqualTo [ "lib/net8.0/Acme.Core.dll"; "lib/net8.0/Acme.Core.xml"; "lib/net462/Acme.Core.dll" ])
        // a token without a value fails, naming it
        let e = Assert.Catch(fun () -> Pack.fromNuspec (at "a.nupkg") (at "pkg/Acme.Core.nuspec") tokens lib Pack.Options.Default)
        Assert.That(e.Message, Does.Contain "$suffix$")
        let tokens = tokens |> Map.add "suffix" ".Globex"
        Pack.fromNuspec (at "a.nupkg") (at "pkg/Acme.Core.nuspec") tokens lib Pack.Options.Default
        Pack.fromNuspec (at "b.nupkg") (at "pkg/Acme.Core.nuspec") tokens (List.rev lib) Pack.Options.Default
        Assert.That(File.ReadAllBytes (at "a.nupkg"), Is.EqualTo (File.ReadAllBytes (at "b.nupkg")))
        let names = Pack.list (at "a.nupkg") |> List.map (fun (n, _, _, _) -> n)
        Assert.That(names, Does.Contain "Acme.Core.Globex.nuspec")
        Assert.That(names, Does.Contain "lib/net462/Acme.Core.dll")
        let nuspec = entryText (at "a.nupkg") "Acme.Core.Globex.nuspec"
        Assert.That(nuspec, Does.Contain "<version>1.2.3</version>")
        Assert.That(nuspec, Does.Contain "<authors>Acme &amp; Co</authors>")
        // a folder without the assembly
        let e2 = Assert.Catch(fun () -> Pack.libOf "Acme.Core" [ "net6.0", at "out/net6.0" ] |> ignore)
        Assert.That(e2.Message, Does.Contain "no Acme.Core.dll")

    [<Test>]
    member _.``the nuspec's files are packed as listed and left out of the packed manifest`` () =
        write "bin/Acme.Core.dll" "dll"
        write "bin/Acme.Core.Extra.dll" "extra"
        write "bin/readme.txt" "read me"
        write "pkg/p.nuspec"
            ("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<package>\n  <metadata>\n    <id>$id$</id>\n    <version>$version$</version>\n  </metadata>\n"
             + "  <files>\n    <file src=\"$bin$\\Acme.Core*.dll\" target=\"lib\\netstandard2.0\" />\n    <file src=\"../bin/readme.txt\" target=\"docs/readme.txt\" />\n  </files>\n</package>\n")
        let tokens = Pack.tokensOf description "2.0.0" |> Map.add "bin" "../bin"
        Pack.fromNuspec (at "p.nupkg") (at "pkg/p.nuspec") tokens [] Pack.Options.Default
        let names = Pack.list (at "p.nupkg") |> List.map (fun (n, _, _, _) -> n)
        Assert.That(names, Is.SupersetOf [ "lib/netstandard2.0/Acme.Core.dll"; "lib/netstandard2.0/Acme.Core.Extra.dll"; "docs/readme.txt"; "Acme.Core.nuspec" ])
        Assert.That(entryText (at "p.nupkg") "Acme.Core.nuspec", Does.Not.Contain "<files>")

