namespace Tests

open System
open System.IO
open NUnit.Framework

open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// Line-end neutral hashes of a lock's text inputs (`FileHash.textSha256`/`textMatches`): an
/// import's msbuild files and an application's recorded files are current on a checkout whose
/// line ends differ from the recording machine's (`core.autocrlf`); a binary is still compared
/// byte for byte. Pure: temporary files, no msbuild.
[<TestFixture>]
type ``Line-end neutral hashes``() =

    let mutable dir = ""
    let mutable tick = 0

    let write (name: string) (text: string) =
        let path = Path.Combine (dir, name)
        Directory.CreateDirectory (Path.GetDirectoryName path) |> ignore
        File.WriteAllBytes (path, Text.Encoding.UTF8.GetBytes text)
        // a rewrite within one clock tick must still get a new memo key
        tick <- tick + 1
        File.SetLastWriteTimeUtc (path, DateTime.UtcNow.AddMinutes (float tick))
        path.Replace ('\\', '/')

    let crlf (text: string) = text.Replace ("\n", "\r\n")

    let props = "<Project>\n  <PropertyGroup>\n    <LangVersion>latest</LangVersion>\n  </PropertyGroup>\n</Project>\n"

    /// An entry whose import read `imports` (path, recorded hash).
    let entryOf (imports: (string * string) list) : Lock.Entry =
        let c = { Csc.ofArgs [ "/noconfig"; "/r/proj/A.cs" ] with Name = "App"; Framework = "net8.0"; Directory = "/r/proj" }
        { Lock.ofCsc c with
            Evaluation = { Lock.Evaluation.Empty with Project = "/r/proj/App.csproj"; Sdk = "8.0.425"
                                                      Imports = [ for p, sha in imports -> ({ Path = p; Sha256 = sha } : Lock.Hashed) ] } }

    [<SetUp>]
    member _.SetUp () =
        dir <- Path.Combine (Path.GetTempPath (), "xake-line-ends-" + Guid.NewGuid().ToString "N")
        Directory.CreateDirectory dir |> ignore

    [<TearDown>]
    member _.TearDown () = if Directory.Exists dir then Directory.Delete (dir, true)

    [<Test>]
    member _.``CRLF reads as LF; a lone CR, a lone LF and a BOM stay``() =
        let bytes (s: string) = Text.Encoding.UTF8.GetBytes s
        Assert.That(FileHash.normalizeLineEnds (bytes "a\r\nb\r\n"), Is.EqualTo (bytes "a\nb\n"))
        Assert.That(FileHash.normalizeLineEnds (bytes "a\rb\n\r"), Is.EqualTo (bytes "a\rb\n\r"))
        Assert.That(FileHash.normalizeLineEnds (bytes "a\r\r\nb"), Is.EqualTo (bytes "a\r\nb"))
        let bom = Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (bytes "x\r\n")
        Assert.That(FileHash.normalizeLineEnds bom, Is.EqualTo (Array.append [| 0xEFuy; 0xBBuy; 0xBFuy |] (bytes "x\n")))
        Assert.That(FileHash.normalizeLineEnds [||], Is.Empty)

    [<Test>]
    member _.``the raw and the text hash of one path, in one process, are each its own``() =
        let path = write "Directory.Build.props" (crlf props)
        Assert.That(FileHash.sha256 path, Is.EqualTo (Hash.sha256 path))
        let lf = write "lf.props" props
        Assert.That(FileHash.textSha256 path, Is.EqualTo (Hash.sha256 lf))
        Assert.That(FileHash.textSha256 path, Is.Not.EqualTo (FileHash.sha256 path))
        // asked again (memoised): still each its own
        Assert.That(FileHash.sha256 path, Is.EqualTo (Hash.sha256 path))
        Assert.That(FileHash.textSha256 lf, Is.EqualTo (FileHash.sha256 lf))
        Assert.That(FileHash.textSha256 (Path.Combine (dir, "missing.props")), Is.EqualTo "")
        Assert.That(FileHash.textMatches path "", Is.False)

    [<Test>]
    member _.``a lock recorded over LF is current on a CRLF checkout, and the reverse``() =
        let path = write "Directory.Build.props" props
        let recorded = FileHash.textSha256 path
        Assert.That(recorded, Is.EqualTo (Hash.sha256 path))
        write "Directory.Build.props" (crlf props) |> ignore
        Assert.That(Locks.staleOf (entryOf [ path, recorded ]), Is.Empty)
        // recorded over CRLF (the hash a new import writes there) and checked out with LF
        let recordedCrlf = FileHash.textSha256 path
        Assert.That(recordedCrlf, Is.EqualTo recorded)
        write "Directory.Build.props" props |> ignore
        Assert.That(Locks.staleOf (entryOf [ path, recordedCrlf ]), Is.Empty)

    [<Test>]
    member _.``a content change is stale whatever the line ends; a lone CR counts``() =
        let path = write "Directory.Build.props" props
        let recorded = FileHash.textSha256 path
        write "Directory.Build.props" (crlf (props.Replace ("latest", "12"))) |> ignore
        Assert.That(Locks.staleOf (entryOf [ path, recorded ]), Is.EqualTo [ path ])
        write "Directory.Build.props" (props.Replace ("\n", "\r")) |> ignore
        Assert.That(Locks.staleOf (entryOf [ path, recorded ]), Is.EqualTo [ path ])
        File.Delete path
        Assert.That(Locks.staleOf (entryOf [ path, recorded ]), Is.EqualTo [ path ])

    [<Test>]
    member _.``a raw hash an older package recorded is still current on the same line ends``() =
        // recorded over CRLF by the raw hash (a Windows import before line-end neutral hashes)
        let path = write "App.csproj" (crlf props)
        let raw = Hash.sha256 path
        Assert.That(Locks.staleOf (entryOf [ path, raw ]), Is.Empty)
        Assert.That(FileHash.textMatches path raw, Is.True)
        // an LF checkout of it needs the import again (its hash is not the line-end neutral one)
        write "App.csproj" props |> ignore
        Assert.That(Locks.staleOf (entryOf [ path, raw ]), Is.EqualTo [ path ])

    [<Test>]
    member _.``a binary is compared byte for byte: a CR LF change is a mismatch``() =
        let pkgs = Path.Combine(dir, "pkgs").Replace ('\\', '/')
        let package = write "pkgs/microsoft.netcore.app.ref/8.0.11/ref/net8.0/X.dll" "MZ\n\u0000binary\n"
        let recorded = Hash.sha256 package
        let c = { Csc.ofArgs [ "/noconfig"; "/reference:" + package; "/r/proj/A.cs" ] with Name = "App"; Framework = "net8.0"; Directory = "/r/proj" }
        let c = { c with Dependencies = { c.Dependencies with References = c.Dependencies.References |> List.map (fun r -> { r with Sha256 = recorded }) } }
        let entry = { Lock.ofCsc c with Recorded = Some { Projects = []; Masks = []; Placed = None; App = None } }
        let noDotnet = Path.Combine(dir, "no-dotnet").Replace ('\\', '/')
        Assert.That(Lock.packMismatches (Some noDotnet) pkgs entry, Is.Empty)
        write "pkgs/microsoft.netcore.app.ref/8.0.11/ref/net8.0/X.dll" "MZ\r\n\u0000binary\r\n" |> ignore
        Assert.That(Lock.packMismatches (Some noDotnet) pkgs entry, Has.Length.EqualTo 1)
