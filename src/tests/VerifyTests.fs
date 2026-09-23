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

    let sampleDll = typeof<Verify.Difference>.Assembly.Location

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
        Assert.That(Verify.verdict headerOnly, Is.EqualTo "identical except: TimeDateStamp, CheckSum, 8 bytes")

        // one byte inside the first section's data
        Assert.That(sectionSize, Is.GreaterThan 0)
        bytesB.[sectionOffset] <- bytesB.[sectionOffset] ^^^ 0xFFuy
        File.WriteAllBytes(pathB, bytesB)

        let diffs = Verify.compare pathA pathB
        Assert.That(diffs |> List.map (fun d -> d.Field), Is.EqualTo ["TimeDateStamp"; "CheckSum"; "Content"])
        Assert.That(Verify.verdict diffs, Does.StartWith "content differs:")

    [<Test>]
    member x.``verdict reports total differing bytes and the largest range``() =
        let pathA = copyTo "verdictA.dll"
        let pathB = copyTo "verdictB.dll"

        let bytesB = File.ReadAllBytes pathB
        let layout = Verify.layout bytesB
        let (timeDateStampOffset, _) = layout.TimeDateStamp
        let (sectionOffset, sectionSize) = layout.Sections |> List.head

        // a small header field difference plus one large content block, so the largest range
        // (not the header field) must be the one `verdict` reports
        for i in 0 .. 3 do bytesB.[timeDateStampOffset + i] <- bytesB.[timeDateStampOffset + i] ^^^ 0xFFuy
        Assert.That(sectionSize, Is.GreaterThanOrEqualTo 64)
        for i in 0 .. 63 do bytesB.[sectionOffset + i] <- bytesB.[sectionOffset + i] ^^^ 0xFFuy
        File.WriteAllBytes(pathB, bytesB)

        let diffs = Verify.compare pathA pathB
        let totalBytes = diffs |> List.sumBy (fun d -> d.Length)
        let largest = diffs |> List.maxBy (fun d -> d.Length)

        Assert.That(largest.Field, Is.EqualTo "Content")
        Assert.That(largest.Length, Is.EqualTo 64)
        Assert.That(
            Verify.verdict diffs,
            Is.EqualTo (sprintf "content differs: %d ranges, %d bytes, largest 64 bytes at 0x%x (Content)" (List.length diffs) totalBytes largest.Offset))

    [<Test>]
    member x.``compare labels a signed copy's appended certificate table from its own layout``() =
        // an honest signed-vs-unsigned fixture (docs/features/hermetic-build/signing.md): a
        // fake WIN_CERTIFICATE appended after the unsigned bytes, 8-byte aligned, with the
        // certificate-table directory entry (4) pointed at it and CheckSum refreshed -- exactly
        // the shape `Sign.fakeSigner` produces, minus the real cryptography.
        let unsigned = copyTo "sign-unsigned.dll"
        let signed = copyTo "sign-signed.dll"

        // align the unsigned file to an 8-byte boundary first, so the certificate table below
        // can be appended immediately with no padding gap to mislabel
        let unaligned = File.ReadAllBytes unsigned
        let alignPad = (8 - (unaligned.Length % 8)) % 8
        let unsignedBytes = if alignPad = 0 then unaligned else Array.append unaligned (Array.create alignPad 0uy)
        File.WriteAllBytes(unsigned, unsignedBytes)

        let oldLen = unsignedBytes.Length
        let certOffset = oldLen
        let certBody = Array.create 128 0xCDuy
        let signedBytes = Array.append unsignedBytes certBody

        let (certDirOffset, _) = (Verify.layout unsignedBytes).CertTableDirEntry
        writeI32 signedBytes certDirOffset certOffset
        writeI32 signedBytes (certDirOffset + 4) certBody.Length

        let (checkSumOffset, _) = (Verify.layout unsignedBytes).CheckSum
        for i in 0 .. 3 do signedBytes.[checkSumOffset + i] <- signedBytes.[checkSumOffset + i] ^^^ 0xFFuy

        File.WriteAllBytes(signed, signedBytes)

        let diffs = Verify.compare unsigned signed
        let fields = diffs |> List.map (fun d -> d.Field) |> List.distinct
        Assert.That(fields, Is.EqualTo ["CheckSum"; "CertificateTable"])
        Assert.That(Verify.verdict diffs, Is.EqualTo (sprintf "identical except: CheckSum, CertificateTable, %d bytes" (diffs |> List.sumBy (fun d -> d.Length))))

        // the appended tail is labelled from the SIGNED file's own layout -- the unsigned file
        // (`a`) has no certificate table to name it with
        let tail = diffs |> List.last
        Assert.That(tail.Field, Is.EqualTo "CertificateTable")
        Assert.That(tail.Offset, Is.EqualTo certOffset)
        Assert.That(tail.Length, Is.EqualTo certBody.Length)

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
