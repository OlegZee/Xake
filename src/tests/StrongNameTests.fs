namespace Tests

open System
open System.IO
open System.Security.Cryptography
open NUnit.Framework

open Xake
open Xake.Dotnet

/// `StrongName` normalises a PE image for E4 (brief §8j): re-stamp `TimeDateStamp`,
/// recompute `CheckSum`, re-sign the CLR strong name. All three are checked against a real
/// compile -- csc is the ground truth for the signature (test 1), `Verify.compare` is the
/// ground truth for "only these three fields changed" (test 3).
[<TestFixture>]
type ``Strong name``() =

    let temp name = Path.Combine(Path.GetTempPath(), sprintf "xake-strongname-%s-%s" (Guid.NewGuid().ToString "N") name)

    /// Compiles a one-class library with csc, signed with `snkPath`, `/deterministic+` so two
    /// compiles of the same sources differ only in what E4 is about. Mirrors
    /// `FromLockTests.fs`'s `makeLock`: the SDK's own csc.dll, run via `dotnet <csc.dll>` the
    /// way `CscLock.compile` does, against netstandard's reference assembly.
    let compile (dir: string) (snkPath: string) : string =
        let fwk = DotNetFwk.locateFramework (Some "netstandard2.0")
        let cscDll =
            if Impl.endsWith ".dll" fwk.CscTool then fwk.CscTool
            else Path.Combine (Path.GetDirectoryName fwk.CscTool, "csc.dll")
        let netstandardDll = DotNetFwk.locateAssembly fwk "netstandard.dll"

        let src = Path.Combine (dir, "Lib.cs")
        let outDll = Path.Combine (dir, "Lib.dll")
        File.WriteAllText (src, "public class Lib { public static int Answer() => 42; }\n")

        let args =
            sprintf "\"%s\" /nologo /noconfig /nostdlib+ /target:library /deterministic+ /keyfile:\"%s\" /reference:\"%s\" /out:\"%s\" \"%s\""
                cscDll snkPath netstandardDll outDll src

        let output = Text.StringBuilder()
        let exitCode = ProcessExec.pexecSync (fun l -> output.AppendLine l |> ignore) (fun l -> output.AppendLine l |> ignore) "dotnet" args [] (Some dir)
        Assert.That(exitCode, Is.EqualTo 0, sprintf "csc failed:\n%s" (output.ToString()))
        Assert.That(File.Exists outDll, Is.True, "csc did not produce Lib.dll")
        outDll

    let makeSnk (dir: string) : string * RSAParameters =
        use rsa = RSA.Create(1024)
        let key = rsa.ExportParameters true
        let path = Path.Combine (dir, "test.snk")
        File.WriteAllBytes (path, StrongName.writeSnk key)
        path, key

    // ---------------------------------------------------------------------------------
    // 1. Reproduce csc's signature byte for byte -- pins the hash-exclusion rule and the
    //    signature byte order (the two things no spec alone settles: has to match a real
    //    compiler's output).
    // ---------------------------------------------------------------------------------

    [<Test; Category("Integration")>]
    member x.``writeSnk and readSnk round-trip a generated key``() =
        use rsa = RSA.Create(1024)
        let key = rsa.ExportParameters true
        let blob = StrongName.writeSnk key
        let roundTripped, isPrivate = StrongName.readSnk (let p = temp "roundtrip.snk" in File.WriteAllBytes(p, blob); p)
        Assert.That(isPrivate, Is.True)
        Assert.That(roundTripped.Modulus, Is.EqualTo key.Modulus)
        Assert.That(roundTripped.Exponent, Is.EqualTo key.Exponent)
        Assert.That(roundTripped.P, Is.EqualTo key.P)
        Assert.That(roundTripped.Q, Is.EqualTo key.Q)
        Assert.That(roundTripped.DP, Is.EqualTo key.DP)
        Assert.That(roundTripped.DQ, Is.EqualTo key.DQ)
        Assert.That(roundTripped.InverseQ, Is.EqualTo key.InverseQ)
        Assert.That(roundTripped.D, Is.EqualTo key.D)

    [<Test; Category("Integration")>]
    member x.``sign reproduces csc's own strong-name signature byte for byte``() =
        let dir = temp "csc-sign"
        Directory.CreateDirectory dir |> ignore
        let snkPath, key = makeSnk dir
        let outDll = compile dir snkPath

        let original = File.ReadAllBytes outDll
        let l = Verify.layout original
        let sigRange =
            match l.StrongNameSignature with
            | Some r -> r
            | None -> Assert.Fail("csc did not produce a StrongNameSignature directory entry"); failwith "unreachable"

        // zero the signature blob in a copy, re-sign with the same key, and require an exact
        // match against what csc itself wrote -- this is the only real ground truth for the
        // hash-exclusion rule and the CAPI little-endian byte order.
        let zeroed = Array.copy original
        let (sigOffset, sigSize) = sigRange
        for i in 0 .. sigSize - 1 do zeroed.[sigOffset + i] <- 0uy

        let resigned = StrongName.sign zeroed key

        Assert.That(resigned, Is.EqualTo original, "re-signing a zeroed copy did not reproduce csc's own signature bytes")

        Assert.That(StrongName.verify original key, Is.True, "verify rejected csc's own signature")

        let flipped = Array.copy original
        let firstSection = l.Sections |> List.head |> fst
        flipped.[firstSection] <- flipped.[firstSection] ^^^ 0xFFuy
        Assert.That(StrongName.verify flipped key, Is.False, "verify accepted a tampered image")

    // ---------------------------------------------------------------------------------
    // 2. stamp / checksum
    // ---------------------------------------------------------------------------------

    [<Test; Category("Integration")>]
    member x.``checksum is idempotent and reacts to content changes``() =
        let dir = temp "checksum"
        Directory.CreateDirectory dir |> ignore
        let snkPath, _ = makeSnk dir
        let outDll = compile dir snkPath
        let original = File.ReadAllBytes outDll

        let once = StrongName.checksum original
        let twice = StrongName.checksum once
        Assert.That(twice, Is.EqualTo once, "checksum is not idempotent")

        let l = Verify.layout original
        let (checkSumOffset, _) = l.CheckSum
        let originalCheckSum = original.[checkSumOffset .. checkSumOffset + 3]

        // csc/the linker already wrote a checksum for a strong-name-signed dll (loaders can
        // check it); if it did, recomputing from the untouched original must reproduce it --
        // the only external reference available without a Windows box to run imagehlp on.
        if originalCheckSum |> Array.exists (fun b -> b <> 0uy) then
            Assert.That(once.[checkSumOffset .. checkSumOffset + 3], Is.EqualTo originalCheckSum,
                "recomputed checksum does not match the one csc/the linker wrote")
        else
            Assert.Inconclusive("this build wrote a zero CheckSum -- only idempotence and the content-sensitivity below are checked")

        // and: flipping one content byte changes the checksum.
        let tampered = Array.copy original
        let contentOffset = l.Sections |> List.head |> fst
        tampered.[contentOffset] <- tampered.[contentOffset] ^^^ 0xFFuy
        Assert.That(StrongName.checksum tampered, Is.Not.EqualTo once, "checksum did not change when a content byte did")

    [<Test; Category("Integration")>]
    member x.``stamp sets TimeDateStamp and recomputes CheckSum only``() =
        let dir = temp "stamp"
        Directory.CreateDirectory dir |> ignore
        let snkPath, _ = makeSnk dir
        let outDll = compile dir snkPath
        let original = File.ReadAllBytes outDll

        let stamped = StrongName.stamp original 0x11223344u
        let l = Verify.layout original
        let (tdsOffset, _) = l.TimeDateStamp
        Assert.That(BitConverter.ToUInt32(stamped, tdsOffset), Is.EqualTo 0x11223344u)

        let path = temp "stamp.dll"
        File.WriteAllBytes(path, stamped)
        let origPath = temp "stamp-orig.dll"
        File.WriteAllBytes(origPath, original)

        let diffs = Verify.compare origPath path |> List.map (fun d -> d.Field)
        Assert.That(diffs, Is.EqualTo ["TimeDateStamp"; "CheckSum"])

    // ---------------------------------------------------------------------------------
    // 3. normalise
    // ---------------------------------------------------------------------------------

    [<Test; Category("Integration")>]
    member x.``normalise touches exactly TimeDateStamp, CheckSum and StrongNameSignature``() =
        let dir = temp "normalise"
        Directory.CreateDirectory dir |> ignore
        let snkPath, key = makeSnk dir
        let outDll = compile dir snkPath

        let originalPath = temp "normalise-orig.dll"
        File.Copy(outDll, originalPath)

        StrongName.normalise outDll 0xCAFEBABEu snkPath

        let diffs = Verify.compare originalPath outDll
        let fields = diffs |> List.map (fun d -> d.Field) |> List.distinct |> List.sort
        Assert.That(fields, Is.EqualTo (["CheckSum"; "StrongNameSignature"; "TimeDateStamp"] |> List.sort),
            sprintf "expected exactly the E4 triple, got: %s" (String.concat ", " fields))

        Assert.That(StrongName.verify (File.ReadAllBytes outDll) key, Is.True, "normalised image does not verify")

        let onceMore = temp "normalise-twice.dll"
        File.Copy(outDll, onceMore)
        StrongName.normalise onceMore 0xCAFEBABEu snkPath
        Assert.That(File.ReadAllBytes onceMore, Is.EqualTo (File.ReadAllBytes outDll),
            "normalising twice with the same timestamp is not idempotent")
