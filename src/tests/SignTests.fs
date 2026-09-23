namespace Tests

open NUnit.Framework
open Xake
open Xake.Dotnet

open System
open System.IO
open System.Threading

/// The six acceptance criteria of docs/features/hermetic-build/signing.md section 5. Every
/// fixture starts from a real managed dll -- this test assembly's own `Xake.Dotnet.dll`, as
/// `VerifyTests` does -- and from a real (deterministic) nupkg built by `Pack.nupkg`. The
/// signer is always `Sign.fakeSigner`: a genuine `WIN_CERTIFICATE` / `.signature.p7s` around a
/// fake payload, so the rule, the executor, the store and the verification step are exercised
/// end to end without a certificate, a Windows agent or the network.
[<TestFixture>]
type ``Sign delegated``() =

    let testDir = Path.Combine (Path.GetTempPath (), "xake-sign-tests-" + Guid.NewGuid().ToString "N")
    let outDir = Path.Combine (testDir, "out")
    let storeDir = Path.Combine (testDir, "signstore")

    let sampleDll = typeof<Verify.Difference>.Assembly.Location

    /// A copy of the sample dll under `out/`. `tweak` patches one byte of the first section,
    /// which changes the image (and so the identity) while keeping a parseable PE.
    let input (name: string) (tweak: byte option) =
        let path = Path.Combine (outDir, name)
        let bytes = File.ReadAllBytes sampleDll
        match tweak with
        | None -> ()
        | Some b ->
            let (sectionOffset, _) = (Verify.layout bytes).Sections |> List.head
            bytes.[sectionOffset] <- b
        File.WriteAllBytes (path, bytes)
        path

    /// The settings under test: a mask over `signed/`, mapping each target back to the
    /// same-named file in `out/`.
    // `Sign.sign`, not a bare `sign`: FSharp.Core already defines `sign`.
    let settings =
        Sign.sign {
            target "signed/(name:*).(ext:dll|exe|nupkg)"
            input (fun t -> Path.Combine (outDir, Path.GetFileName t))
            certificate (Sign.Thumbprint "cc4967777c49a3ff")
            timestamp "http://timestamp.invalid/rfc3161"
            hashalg Sign.Sha256
        }

    /// `fakeSigner` plus a call counter, optionally slowed down and metered so a test can watch
    /// how many signer calls overlap.
    let countingSigner (counter: int ref) (enter: (unit -> unit) option) (leave: (unit -> unit) option) (delay: int) : Sign.Signer =
        fun request ->
            async {
                Interlocked.Increment counter |> ignore
                enter |> Option.iter (fun f -> f ())
                if delay > 0 then do! Async.Sleep delay
                let! bytes = Sign.fakeSigner request
                leave |> Option.iter (fun f -> f ())
                return bytes
            }

    /// The meter of `DelegatedTests`: enter/leave bracket a region, `peak` records the maximum
    /// overlap.
    let meter () =
        let current = ref 0
        let peak = ref 0
        let gate = obj ()
        let enter () =
            let n = Interlocked.Increment current
            lock gate (fun () -> if n > peak.Value then peak.Value <- n)
        let leave () = Interlocked.Decrement current |> ignore
        enter, leave, peak

    /// One engine run over `targets`, with the signing rule delegated to `Sign.executor`.
    /// `threads` is the CPU-slot count and `budget` the dispatch resource -- deliberately
    /// independent, which is what criterion 4 measures.
    let runScript (signer: Sign.Signer) (budget: Resource) (threads: int) (targets: string list) =
        let store = Sign.directoryStore storeDir
        let options =
            { ExecOptions.Default with
                Threads = threads
                // one group, so the engine builds them in parallel: separate entries in
                // `Targets` are groups and run one after another (ScriptRunner.targetLists)
                Targets = [ String.concat ";" targets ]
                ProjectRoot = testDir
                IgnoreCommandLine = true
                NoPersist = true
                Progress = false
                ConLogLevel = Silent
                FileLogLevel = Silent }
        RulesBuilder options {
            rules [ (Sign.rule settings signer) |> delegated (Sign.executor settings signer store budget) ]
        }

    let signedPath (name: string) = Path.Combine (testDir, "signed", name)

    [<SetUp>]
    member _.Setup () =
        Directory.CreateDirectory outDir |> ignore
        Directory.CreateDirectory (Path.Combine (testDir, "signed")) |> ignore

    [<TearDown>]
    member _.Teardown () =
        if Directory.Exists testDir then Directory.Delete (testDir, true)

    // --- 1 -------------------------------------------------------------------------------

    [<Test>]
    member _.``the fake signer leaves the authenticode hash alone and adds a certificate table`` () =
        let unsigned = input "A.dll" None
        let expectedHash = Verify.authenticodeHash unsigned
        let expectedSha = Verify.sha256 unsigned

        let signed = signedPath "A.dll"
        Sign.fakeSigner (Sign.request settings unsigned) |> Async.RunSynchronously
        |> fun bytes -> File.WriteAllBytes (signed, bytes)

        Assert.That (Verify.authenticodeHash signed, Is.EqualTo expectedHash,
            "signing must not change the image the Authenticode hash covers")
        Assert.That (Verify.sha256 signed, Is.Not.EqualTo expectedSha, "the file's raw bytes do change")
        Assert.That (Sign.isSigned signed, Is.True)
        Assert.That (Sign.verifySameImage unsigned signed, Is.True)

        // the appended blob is a genuine WIN_CERTIFICATE: 8-byte aligned, dwLength covering the
        // whole structure, revision 0x0200, type 0x0002 (PKCS#7)
        let bytes = File.ReadAllBytes signed
        let layout = Verify.layout bytes
        match layout.CertTable with
        | None -> Assert.Fail "the signed file must report a certificate table"
        | Some (offset, size) ->
            Assert.That (offset % 8, Is.EqualTo 0, "the certificate table must start 8-byte aligned")
            Assert.That (offset + size, Is.EqualTo bytes.Length)
            // BitConverter, not shifts: `open Xake` rebinds `<<<` as the phony-rule operator
            let u16 o = int (BitConverter.ToUInt16 (bytes, o))
            let i32 o = BitConverter.ToInt32 (bytes, o)
            Assert.That (i32 offset, Is.EqualTo size, "dwLength must cover the whole structure")
            Assert.That (u16 (offset + 4), Is.EqualTo 0x0200, "wRevision")
            Assert.That (u16 (offset + 6), Is.EqualTo 0x0002, "wCertificateType (PKCS#7)")
            let body = Text.Encoding.ASCII.GetString (bytes, offset + 8, size - 8)
            Assert.That (body, Does.Contain expectedHash, "the payload records the image it signed")

        // the unsigned/signed pair is exactly the difference `Verify.compare` is expected to
        // label: the refreshed checksum and the appended certificate table, nothing else.
        // (Only when the unsigned file is already 8-byte aligned -- otherwise the alignment
        // padding the signer inserts is an unlabelled gap between the two, which is the
        // `Verify.verdict` gap signing.md section 3 records as still open.)
        if (FileInfo unsigned).Length % 8L = 0L then
            let fields = Verify.compare unsigned signed |> List.map (fun d -> d.Field) |> List.distinct
            Assert.That (fields, Is.EqualTo [ "CheckSum"; "CertificateTable" ])

    // --- 2 -------------------------------------------------------------------------------

    [<Test>]
    member _.``two targets sharing one image are signed once`` () =
        let a = input "A.dll" None
        let b = input "B.dll" None       // byte-identical copy: the same image, a different file
        Assert.That (Verify.authenticodeHash a, Is.EqualTo (Verify.authenticodeHash b))
        Assert.That (Sign.identity settings a, Is.EqualTo (Sign.identity settings b))

        let calls = ref 0
        let budget = Resource.newResource "sign" 4
        runScript (countingSigner calls None None 60) budget 4 [ "signed/A.dll"; "signed/B.dll" ]

        Assert.That (calls.Value, Is.EqualTo 1,
            "two targets that share one image share one signature -- in-flight dedup or a store hit")
        for name in [ "A.dll"; "B.dll" ] do
            Assert.That (File.Exists (signedPath name), Is.True, name + " must be written")
            Assert.That (Sign.verifySameImage (Path.Combine (outDir, name)) (signedPath name), Is.True)
        CollectionAssert.AreEqual (File.ReadAllBytes (signedPath "A.dll"), File.ReadAllBytes (signedPath "B.dll"),
            "both targets are served from the one signature")

        // the signature went into the store, keyed by identity
        Assert.That (File.Exists (Path.Combine (storeDir, Sign.identity settings a + ".signed")), Is.True)

    // --- 3 -------------------------------------------------------------------------------

    [<Test>]
    member _.``a second run serves every target from the store with no signer call`` () =
        let a = input "A.dll" None
        let b = input "B.dll" (Some 0x42uy)

        let firstCalls = ref 0
        let budget = Resource.newResource "sign" 4
        runScript (countingSigner firstCalls None None 0) budget 4 [ "signed/A.dll"; "signed/B.dll" ]
        Assert.That (firstCalls.Value, Is.EqualTo 2, "two different images need two signatures")

        let firstBytes = [ for n in [ "A.dll"; "B.dll" ] -> File.ReadAllBytes (signedPath n) ]
        for n in [ "A.dll"; "B.dll" ] do File.Delete (signedPath n)

        // a second run in the same folder, against the same store: no signer call at all
        let secondCalls = ref 0
        runScript (countingSigner secondCalls None None 0) budget 4 [ "signed/A.dll"; "signed/B.dll" ]

        Assert.That (secondCalls.Value, Is.EqualTo 0, "every target must come from the store")
        List.iter2
            (fun (expected: byte[]) name -> CollectionAssert.AreEqual (expected, File.ReadAllBytes (signedPath name)))
            firstBytes [ "A.dll"; "B.dll" ]
        Assert.That (Sign.verifySameImage a (signedPath "A.dll"), Is.True)
        Assert.That (Sign.verifySameImage b (signedPath "B.dll"), Is.True)

    // --- 4 -------------------------------------------------------------------------------

    [<Test>]
    member _.``the dispatch budget caps concurrent signer calls independently of Threads`` () =
        // 8 distinct images, only 2 CPU slots, a dispatch budget of 4. A delegated rule never
        // holds a CPU slot (docs/delegated.md), so the observed peak must be the budget, not
        // the thread count.
        let names = [ for i in 1 .. 8 -> sprintf "T%d.dll" i ]
        names |> List.iteri (fun i n -> input n (Some (byte (0x40 + i))) |> ignore)

        let enter, leave, peak = meter ()
        let calls = ref 0
        let budget = Resource.newResource "sign" 4
        runScript (countingSigner calls (Some enter) (Some leave) 200) budget 2 (names |> List.map (fun n -> "signed/" + n))

        Assert.That (calls.Value, Is.EqualTo 8, "eight distinct images need eight signatures")
        Assert.That (peak.Value, Is.LessThanOrEqualTo 4, "the dispatch budget must cap concurrent signer calls")
        Assert.That (peak.Value, Is.GreaterThan 2,
            sprintf "the CPU-slot count (2) must not be the limit -- peak was %d" peak.Value)
        for n in names do
            Assert.That (Sign.verifySameImage (Path.Combine (outDir, n)) (signedPath n), Is.True)

    // --- 5 -------------------------------------------------------------------------------

    [<Test>]
    member _.``a signed nupkg gains the signature entry and keeps every other entry`` () =
        let nuspec = Path.Combine (outDir, "Test.Pkg.nuspec")
        File.WriteAllText (
            nuspec,
            "<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">\
             <metadata><id>Test.Pkg</id><version>1.2.3</version><authors>Xake</authors>\
             <description>test</description></metadata></package>")
        let payload = input "A.dll" None
        let unsigned = Path.Combine (outDir, "Test.Pkg.1.2.3.nupkg")
        Pack.nupkg unsigned nuspec [ { Pack.Path = "lib/netstandard2.0/A.dll"; Pack.Source = payload } ] Pack.defaultOptions

        Assert.That (Sign.isSigned unsigned, Is.False, "Pack.nupkg writes no signature")

        let signed = signedPath "Test.Pkg.1.2.3.nupkg"
        Sign.fakeSigner (Sign.request settings unsigned) |> Async.RunSynchronously
        |> fun bytes -> File.WriteAllBytes (signed, bytes)

        let names = Pack.entries signed |> List.map (fun (n, _, _, _) -> n)
        Assert.That (names, Does.Contain Sign.SignatureEntry)
        Assert.That (Sign.isSigned signed, Is.True)

        // entry-level identity: everything but the signature is byte-identical (name, size,
        // crc32 and timestamp all unchanged)
        let others (path: string) = Pack.entries path |> List.filter (fun (n, _, _, _) -> n <> Sign.SignatureEntry)
        CollectionAssert.AreEqual (others unsigned, others signed)
        Assert.That (Sign.verifySameImage unsigned signed, Is.True)

        // two signings of the same package agree on the identity key, though not on the bytes
        let signedAgain = signedPath "again.nupkg"
        Sign.fakeSigner (Sign.request settings unsigned) |> Async.RunSynchronously
        |> fun bytes -> File.WriteAllBytes (signedAgain, bytes)
        Assert.That (Sign.identity settings unsigned, Is.EqualTo (Sign.identity settings unsigned))
        CollectionAssert.AreEqual (others signed, others signedAgain)

    // --- 6 -------------------------------------------------------------------------------

    [<Test>]
    member _.``the identity key follows the image and the policy, never the signing moment`` () =
        let a = input "A.dll" None
        let baseKey = Sign.identity settings a

        let changed (f: Sign.Settings -> Sign.Settings) = Sign.identity (f settings) a

        Assert.That (changed (fun s -> { s with Certificate = Sign.Thumbprint "0000" }), Is.Not.EqualTo baseKey, "certificate")
        Assert.That (changed (fun s -> { s with Certificate = Sign.KeyId "cc4967777c49a3ff" }), Is.Not.EqualTo baseKey,
            "the certificate KIND is part of the key, not just its text")
        Assert.That (changed (fun s -> { s with TimestampServer = None }), Is.Not.EqualTo baseKey, "timestamp server")
        Assert.That (changed (fun s -> { s with Hash = Sign.Sha512 }), Is.Not.EqualTo baseKey, "hash algorithm")

        // cosmetic metadata is deliberately not in the key
        Assert.That (changed (fun s -> { s with Description = Some "Xake" }), Is.EqualTo baseKey, "description")
        // neither is the target the signature will be written to
        Assert.That (changed (fun s -> { s with Target = "elsewhere/*.dll" }), Is.EqualTo baseKey, "target mask")

        // a different image is a different key
        let b = input "B.dll" (Some 0x7Fuy)
        Assert.That (Sign.identity settings b, Is.Not.EqualTo baseKey, "a changed image")

        // and the key does NOT follow the signing moment: two signings give two different files
        // whose Authenticode hash -- and so whose identity -- is one and the same
        let one = signedPath "one.dll"
        let two = signedPath "two.dll"
        for path in [ one; two ] do
            Sign.fakeSigner (Sign.request settings a) |> Async.RunSynchronously
            |> fun bytes -> File.WriteAllBytes (path, bytes)
        Assert.That (Verify.authenticodeHash one, Is.EqualTo (Verify.authenticodeHash two))
        Assert.That (Sign.identity settings a, Is.EqualTo baseKey)
