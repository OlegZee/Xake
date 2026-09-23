namespace Tests

open System
open System.IO
open NUnit.Framework

open Xake.Dotnet

/// `Verify` parses just enough of the PE/CLR headers to compute the Authenticode image hash
/// (what E5 in the hermetic-build brief uses to tell "same build, re-signed" from "different
/// build") and to label what changed between two builds. All fixtures start from a real managed
/// dll -- this test assembly's own `Xake.Dotnet.dll` -- copied to a temp file and patched byte
/// by byte using `Verify.layout`, which `InternalsVisibleTo("tests")` exposes.
[<TestFixture>]
type ``Verify PE``() =

    let temp name = Path.Combine(Path.GetTempPath(), sprintf "xake-verify-%s-%s" (Guid.NewGuid().ToString "N") name)

    let sampleDll = typeof<Lock.Project>.Assembly.Location

    let copyTo name =
        let path = temp name
        File.Copy(sampleDll, path, true)
        path

    let writeI32 (bytes: byte[]) (offset: int) (value: int) =
        bytes.[offset] <- byte value
        bytes.[offset + 1] <- byte (value >>> 8)
        bytes.[offset + 2] <- byte (value >>> 16)
        bytes.[offset + 3] <- byte (value >>> 24)

    [<Test>]
    member x.``authenticode hash ignores checksum and certificate table``() =
        let path = copyTo "auth.dll"
        let originalHash = Verify.authenticodeHash path
        let originalSha = Verify.sha256 path

        let bytes = File.ReadAllBytes path
        let layout = Verify.layout bytes
        let (checkSumOffset, _) = layout.CheckSum
        let (certDirOffset, _) = layout.CertTableDirEntry

        // scramble the checksum field
        for i in 0 .. 3 do bytes.[checkSumOffset + i] <- bytes.[checkSumOffset + i] ^^^ 0xFFuy

        // append a fake 64-byte certificate table and point the data directory entry at it
        let certOffset = bytes.Length
        let patched = Array.append bytes (Array.create 64 0xABuy)
        writeI32 patched certDirOffset certOffset
        writeI32 patched (certDirOffset + 4) 64

        File.WriteAllBytes(path, patched)

        Assert.That(Verify.authenticodeHash path, Is.EqualTo originalHash)
        Assert.That(Verify.sha256 path, Is.Not.EqualTo originalSha)

    [<Test>]
    member x.``compare labels the known fields``() =
        let pathA = copyTo "cmpA.dll"
        let pathB = copyTo "cmpB.dll"

        let bytesB = File.ReadAllBytes pathB
        let layout = Verify.layout bytesB
        let (timeDateStampOffset, _) = layout.TimeDateStamp
        let (checkSumOffset, _) = layout.CheckSum
        let (sectionOffset, sectionSize) = layout.Sections |> List.head

        for i in 0 .. 3 do bytesB.[timeDateStampOffset + i] <- bytesB.[timeDateStampOffset + i] ^^^ 0xFFuy
        for i in 0 .. 3 do bytesB.[checkSumOffset + i] <- bytesB.[checkSumOffset + i] ^^^ 0xFFuy

        // without the content patch: only the two header fields differ
        File.WriteAllBytes(pathB, bytesB)
        let headerOnly = Verify.compare pathA pathB
        Assert.That(headerOnly |> List.map (fun d -> d.Field), Is.EqualTo ["TimeDateStamp"; "CheckSum"])
        Assert.That(Verify.verdict headerOnly, Is.EqualTo "identical except: TimeDateStamp, CheckSum")

        // one byte inside the first section's data
        Assert.That(sectionSize, Is.GreaterThan 0)
        bytesB.[sectionOffset] <- bytesB.[sectionOffset] ^^^ 0xFFuy
        File.WriteAllBytes(pathB, bytesB)

        let diffs = Verify.compare pathA pathB
        Assert.That(diffs |> List.map (fun d -> d.Field), Is.EqualTo ["TimeDateStamp"; "CheckSum"; "Content"])
        Assert.That(Verify.verdict diffs, Does.StartWith "content differs")

    [<Test>]
    member x.``identical files compare empty``() =
        let pathA = copyTo "eqA.dll"
        let pathB = copyTo "eqB.dll"
        Assert.That(Verify.compare pathA pathB, Is.Empty)
        Assert.That(Verify.verdict [], Is.EqualTo "identical")

    [<Test>]
    member x.``PE32+ parses alongside PE32``() =
        // any managed dll from this test's own build is a known-good PE32 sample -- proves
        // `layout`/`authenticodeHash` handle the 32-bit optional header shape.
        let managedPath = copyTo "pe32.dll"
        let managedLayout = Verify.layout (File.ReadAllBytes managedPath)
        Assert.That(managedLayout.Is64, Is.False)
        Assert.That(Verify.authenticodeHash managedPath, Has.Length.EqualTo 64)

        let pe64Sample =
            let nugetPackages = Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".nuget", "packages")
            if Directory.Exists nugetPackages then
                Directory.GetDirectories(nugetPackages)
                |> Array.collect (fun pkg ->
                    let nativeDirs = Directory.GetDirectories(pkg, "native", SearchOption.AllDirectories)
                    nativeDirs
                    |> Array.filter (fun d -> d.Replace('\\', '/').Contains "/runtimes/win-x64/native")
                    |> Array.collect (fun d -> Directory.GetFiles(d, "*.dll")))
                |> Array.tryHead
            else None

        match pe64Sample with
        | None -> Assert.Ignore("no PE32+ sample on this machine")
        | Some path ->
            let bytes = File.ReadAllBytes path
            let layout = Verify.layout bytes
            Assert.That(layout.Is64, Is.True)
            Assert.That(Verify.authenticodeHash path, Has.Length.EqualTo 64)
