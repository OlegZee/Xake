namespace Xake.Dotnet

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Security.Cryptography
open System.Collections.Concurrent
open System.Threading.Tasks

open Xake

/// Code signing as a *delegated* rule (docs/features/hermetic-build/signing.md): the two
/// signing steps of the pipeline -- Authenticode on a PE file, and the NuGet author signature
/// on a `.nupkg` -- expressed so that the private key never has to live on the build agent.
///
/// Three properties make this the one delegated rule in the pipeline:
///   * the key is elsewhere -- `Signer` is a function from a request to the signed bytes, so a
///     real deployment substitutes a function instead of redesigning the rule;
///   * it is I/O-bound waiting (HSM round trip + RFC 3161 timestamp), so the engine runs it
///     detached and its concurrency is the dispatch `Resource`, not the CPU pool;
///   * its output is *not* byte-reproducible (the timestamp countersignature embeds the signing
///     moment), so identity is computed from the Authenticode hash of the **input**, never from
///     the output bytes. A cached signature is a valid signature *of that image*, not a
///     byte-reproduction of a previous run.
///
/// Nothing here creates or validates real cryptography: `fakeSigner` builds a genuine
/// `WIN_CERTIFICATE` around a fake payload so the whole pipeline -- rule, executor, store,
/// verification -- can be exercised end to end.
module Sign =

    /// The digest algorithm named in the signing request. Carried into the identity key, so
    /// changing it re-signs.
    type Algorithm =
        | Sha256
        | Sha384
        | Sha512

    /// How the signer names the key. Never key material -- a reference the signing agent
    /// resolves on its own side.
    type Certificate =
        /// certificate store / pfx thumbprint
        | Thumbprint of string
        /// Azure Trusted Signing account and certificate profile
        | TrustedSigning of account: string * profile: string
        /// an HSM label or an agent-side alias
        | KeyId of string

    /// What is being signed. Decided from the file extension by `kindOf`.
    type Kind =
        /// a `.dll`/`.exe` -- Authenticode, a certificate table appended to the PE
        | PeImage
        /// a `.nupkg` -- a NuGet author signature, a `.signature.p7s` entry added to the zip
        | Nupkg

    /// What is handed to the signer. Everything the identity key is computed from lives here
    /// (`Description` aside -- see `identity`).
    type Request = {
        /// the file to sign (the rule's input, never the target)
        File: string
        Kind: Kind
        Certificate: Certificate
        /// RFC 3161 URL; `None` = no countersignature
        TimestampServer: string option
        Hash: Algorithm
        Description: string option }

    /// The only thing a real deployment replaces: takes a request, returns the SIGNED BYTES.
    /// Async because every real implementation is a round trip to a tool, an HSM or a service.
    type Signer = Request -> Async<byte[]>

    /// Content-addressed delivery of signed bytes, keyed by `identity`. This is how the signed
    /// artifact travels between agents: only the `BuildResult` goes into `.xake`, the bytes go
    /// through the store (`docs/delegated.md`, "artifact bytes are delivered out of band").
    type Store = {
        TryFetch: string -> Async<byte[] option>
        Publish: string -> byte[] -> Async<unit> }

    /// A signing rule: the target mask, how to name the unsigned input for a target, and the
    /// signing policy. `Input` is a *function*, not a path, because the rule is a mask: the
    /// target `signed/X.dll` has to name its unsigned source `out/X.dll`, and that mapping is
    /// the script's. Keeping it in the settings is what lets the executor derive the input the
    /// same way the body would -- it needs it *before* any body runs, to compute the identity.
    type Settings = {
        /// rule mask for the signed output, e.g. `"signed/(name:*).(ext:dll|exe|nupkg)"`
        Target: string
        /// signed target path -> the file to sign
        Input: string -> string
        Certificate: Certificate
        TimestampServer: string option
        Hash: Algorithm
        Description: string option }
        with
            /// Signs in place (`Input = id`) with an unnamed key: a script has to set at least
            /// `target`, `input` and `certificate`.
            static member Default = {
                Target = ""
                Input = id
                Certificate = Thumbprint ""
                TimestampServer = None
                Hash = Sha256
                Description = None }

    // ---------------------------------------------------------------------------
    // identity
    // ---------------------------------------------------------------------------

    let private algorithmName = function
        | Sha256 -> "sha256"
        | Sha384 -> "sha384"
        | Sha512 -> "sha512"

    let private certificateId = function
        | Thumbprint t -> "thumbprint:" + t
        | TrustedSigning (account, profile) -> "trustedsigning:" + account + "/" + profile
        | KeyId k -> "keyid:" + k

    /// PE or nupkg, by extension. Anything that is not `.nupkg` is treated as a PE image.
    let kindOf (path: string) : Kind =
        if String.Equals (Path.GetExtension path, ".nupkg", StringComparison.OrdinalIgnoreCase) then Nupkg
        else PeImage

    let private hexOf (bytes: byte[]) = bytes |> Array.map (sprintf "%02x") |> String.concat ""

    /// The hash that identifies the *image* being signed, signature aside: the Authenticode
    /// hash for a PE (it excludes the certificate table, so it survives signing unchanged), and
    /// the plain SHA-256 for a nupkg -- the input package is unsigned by construction
    /// (`Pack.nupkg` writes no `.signature.p7s`), so there is nothing to exclude.
    let imageHash (path: string) : string =
        match kindOf path with
        | PeImage -> Verify.authenticodeHash path
        | Nupkg -> Verify.sha256 path

    /// The identity key of one signing job: `sha256(image hash | certificate | timestamp
    /// server | hash algorithm)`. It says "this exact image, signed by this key, under this
    /// policy" and nothing about wall-clock time -- which is the whole point, since the signed
    /// bytes are never reproducible. Two targets holding the same image share one signature; a
    /// changed certificate, timestamp server or algorithm is a different key and re-signs.
    /// `Description` is deliberately *not* part of the key: it is cosmetic metadata, and
    /// including it would re-sign every artifact on a description edit.
    let identity (settings: Settings) (input: string) : string =
        let material =
            [ imageHash input
              certificateId settings.Certificate
              defaultArg settings.TimestampServer ""
              algorithmName settings.Hash ]
            |> String.concat "|"
        use algo = SHA256.Create ()
        algo.ComputeHash (Encoding.UTF8.GetBytes material) |> hexOf

    /// The request `rule`/`executor` hand to the signer for one input file.
    let request (settings: Settings) (input: string) : Request =
        { File = input
          Kind = kindOf input
          Certificate = settings.Certificate
          TimestampServer = settings.TimestampServer
          Hash = settings.Hash
          Description = settings.Description }

    // ---------------------------------------------------------------------------
    // verification helpers (signing.md section 3)
    // ---------------------------------------------------------------------------

    /// The zip entry a NuGet author signature lives in.
    let [<Literal>] SignatureEntry = ".signature.p7s"

    /// Whether the file carries a signature at all: a non-empty certificate table for a PE,
    /// a `.signature.p7s` entry for a nupkg. Says nothing about whether that signature is
    /// *valid* -- chain building and timestamp validation are `signtool verify`'s job, and stay
    /// out of scope here.
    let isSigned (path: string) : bool =
        match kindOf path with
        | PeImage -> (Verify.layout (File.ReadAllBytes path)).CertTable |> Option.isSome
        | Nupkg -> Pack.entries path |> List.exists (fun (name, _, _, _) -> name = SignatureEntry)

    /// The reproducibility check for a signed artifact, and the only one that means anything:
    /// the signed file is the *same image* as the input, and it does carry a signature. Byte
    /// equality is unavailable by construction (the timestamp countersignature), so this --
    /// not `sha256` -- is what ties a shipped, signed file back to a reproducible build.
    /// For a nupkg "same image" can only mean "every entry but the signature is unchanged",
    /// which is what this checks.
    let verifySameImage (input: string) (signed: string) : bool =
        isSigned signed &&
        match kindOf signed with
        | PeImage -> Verify.authenticodeHash signed = Verify.authenticodeHash input
        | Nupkg ->
            let strip = List.filter (fun (name, _, _, _) -> name <> SignatureEntry)
            strip (Pack.entries signed) = strip (Pack.entries input)

    // ---------------------------------------------------------------------------
    // the fake signer -- a genuine container around a fake payload
    // ---------------------------------------------------------------------------

    let private writeI32 (bytes: byte[]) (offset: int) (value: int) =
        bytes.[offset] <- byte value
        bytes.[offset + 1] <- byte (value >>> 8)
        bytes.[offset + 2] <- byte (value >>> 16)
        bytes.[offset + 3] <- byte (value >>> 24)

    let private writeU16 (bytes: byte[]) (offset: int) (value: int) =
        bytes.[offset] <- byte value
        bytes.[offset + 1] <- byte (value >>> 8)

    let private align8 (n: int) = (n + 7) / 8 * 8

    /// The JSON blob that stands in for a PKCS#7 `SignedData`: everything a reader would want
    /// to check a real signature against, and a `signedAt` stamp, so the fake reproduces the
    /// one property that matters to the design -- signing the same input twice gives two
    /// different files.
    let private fakePayload (req: Request) : byte[] =
        let field name value = sprintf "%s:%s" (Json.escape name) (Json.escape value)
        let fields =
            [ field "signer" "Xake.Dotnet.Sign.fakeSigner (NOT A VALID SIGNATURE)"
              field "imageHash" (imageHash req.File)
              field "certificate" (certificateId req.Certificate)
              field "timestampServer" (defaultArg req.TimestampServer "")
              field "hashAlgorithm" (algorithmName req.Hash)
              field "description" (defaultArg req.Description "")
              field "signedAt" (DateTime.UtcNow.ToString ("o", Globalization.CultureInfo.InvariantCulture)) ]
        Encoding.ASCII.GetBytes ("{" + String.concat "," fields + "}")

    /// Appends a real `WIN_CERTIFICATE` (`dwLength`, `wRevision = 0x0200`,
    /// `wCertificateType = 0x0002` PKCS#7, then the body, padded to 8 bytes) carrying `payload`,
    /// points data directory 4 at it and refreshes `CheckSum` -- exactly what an Authenticode
    /// signer does to a PE, minus the cryptography. The image is padded to an 8-byte boundary
    /// first, as the certificate table must start aligned.
    let internal appendCertificate (image: byte[]) (payload: byte[]) : byte[] =
        let pad = (8 - image.Length % 8) % 8
        let aligned = if pad = 0 then image else Array.append image (Array.create pad 0uy)
        let certLength = align8 (8 + payload.Length)
        let cert = Array.zeroCreate certLength
        writeI32 cert 0 certLength
        writeU16 cert 4 0x0200      // WIN_CERT_REVISION_2_0
        writeU16 cert 6 0x0002      // WIN_CERT_TYPE_PKCS_SIGNED_DATA
        Array.blit payload 0 cert 8 payload.Length

        let certOffset = aligned.Length
        let signed = Array.append aligned cert
        let (certDirOffset, _) = (Verify.layout aligned).CertTableDirEntry
        writeI32 signed certDirOffset certOffset
        writeI32 signed (certDirOffset + 4) certLength
        // CheckSum covers the whole file, the certificate table included, so it is refreshed
        // last -- `authenticodeHash` excludes it, which is why signing leaves that hash alone.
        StrongName.checksum signed

    /// Reads a zip into a temp directory and returns (`Pack.Entry` list, that directory).
    let private explode (zipPath: string) (into: string) : Pack.Entry list =
        use fs = File.OpenRead zipPath
        use archive = new ZipArchive (fs, ZipArchiveMode.Read)
        [ for entry in archive.Entries do
            if entry.FullName <> SignatureEntry && not (entry.FullName.EndsWith "/") then
                let dest = Path.Combine (into, entry.FullName.Replace ('/', Path.DirectorySeparatorChar))
                Path.GetDirectoryName dest |> Directory.CreateDirectory |> ignore
                (
                    use source = entry.Open ()
                    use target = File.Create dest
                    source.CopyTo target
                )
                yield { Pack.Path = entry.FullName; Pack.Source = dest } ]

    /// Rewrites the package with a `.signature.p7s` entry holding `payload`, through
    /// `Pack.zip`/`Pack.defaultOptions` so every other entry keeps the deterministic bytes
    /// `Pack.nupkg` gave it.
    let internal addSignatureEntry (nupkgPath: string) (payload: byte[]) : byte[] =
        let temp = Path.Combine (Path.GetTempPath (), "xake-sign-" + Guid.NewGuid().ToString "N")
        Directory.CreateDirectory temp |> ignore
        try
            let content = explode nupkgPath temp
            let signaturePath = Path.Combine (temp, "signature.p7s")
            File.WriteAllBytes (signaturePath, payload)
            let output = Path.Combine (temp, "signed.nupkg")
            Pack.zip output ({ Pack.Path = SignatureEntry; Pack.Source = signaturePath } :: content) Pack.defaultOptions
            File.ReadAllBytes output
        finally
            if Directory.Exists temp then Directory.Delete (temp, true)

    /// A signer for tests and dry runs. **Nothing verifies what it produces**: the container is
    /// genuine -- a real `WIN_CERTIFICATE` in the PE, a real `.signature.p7s` entry in the
    /// nupkg -- and the payload is plain JSON instead of a PKCS#7 `SignedData`. It exists so
    /// that the rule, the executor, the store and the verification step can be exercised end to
    /// end without a certificate, a Windows agent or the network.
    let fakeSigner : Signer =
        fun request ->
            async {
                let payload = fakePayload request
                match request.Kind with
                | PeImage -> return appendCertificate (File.ReadAllBytes request.File) payload
                | Nupkg -> return addSignatureEntry request.File payload
            }

    // ---------------------------------------------------------------------------
    // the store
    // ---------------------------------------------------------------------------

    /// A directory as the shared store: one file per identity key. Both agents need read and
    /// write access to it; in a real deployment this is object storage behind the same
    /// interface. Publishing writes to a temporary name first, so a concurrent reader never
    /// sees a half-written signature.
    let directoryStore (root: string) : Store =
        let pathOf (key: string) = Path.Combine (root, key + ".signed")
        { TryFetch = fun key ->
            async {
                let path = pathOf key
                return (if File.Exists path then Some (File.ReadAllBytes path) else None)
            }
          Publish = fun key bytes ->
            async {
                Directory.CreateDirectory root |> ignore
                let path = pathOf key
                let staging = path + "." + Guid.NewGuid().ToString "N" + ".tmp"
                File.WriteAllBytes (staging, bytes)
                File.Copy (staging, path, true)
                File.Delete staging
            } }

    // ---------------------------------------------------------------------------
    // the rule and the delegated executor
    // ---------------------------------------------------------------------------

    let private writeTo (path: string) (bytes: byte[]) =
        let dir = Path.GetDirectoryName path
        if not (String.IsNullOrEmpty dir) then Directory.CreateDirectory dir |> ignore
        File.WriteAllBytes (path, bytes)

    /// The plain, *local* rule: needs the input, calls the signer in-process, writes the signed
    /// bytes to the target. This is the one configuration where the body is the signer -- an
    /// agent that does hold the key. Wrapped in `delegated (executor ...)` the body is never
    /// called at all; it is kept so that one settings record drives both shapes.
    let rule (settings: Settings) (signer: Signer) : ExecContext Rule =
        settings.Target ..> recipe {
            let! target = getTargetFullName ()
            let input = settings.Input target
            do! need [ input ]
            let! bytes = signer (request settings input)
            writeTo target bytes
            do! trace Level.Info "[sign] %s -> %s" input target
        }

    /// The delegated executor: **it never calls the body thunk** -- by definition the work
    /// happens elsewhere. For each target it derives the input from the settings, computes the
    /// identity key, and then, exactly as `samples/delegated-proc.fsx` does:
    ///   * store hit -> write the stored bytes to the target, no signer call at all;
    ///   * in-flight -> join the running job for the same key (two targets sharing an image
    ///     collapse to one signature);
    ///   * miss -> take one unit of `budget` (the signing service's rate limit, *not* the core
    ///     count) and call the signer, publish the bytes, write them.
    ///
    /// `Resource.withAcquired`, never `withYieldedSlot`: the engine already runs the whole
    /// delegated task detached, so there is no CPU slot to yield.
    ///
    /// The executor records the input as a `FileDep`, and the body -- with its `need` -- does
    /// not run, so the input must be built by the time the signed target is demanded: declare
    /// it in the enclosing rule, as `docs/delegated.md` requires of any delegated body.
    let executor (settings: Settings) (signer: Signer) (store: Store) (budget: Resource) : DelegatedExecutor<ExecContext> =
        let inFlight = ConcurrentDictionary<string, Lazy<Task<byte[]>>> ()

        fun _ctx targets _body ->
            async {
                let targetPaths =
                    targets |> List.map (function
                        | FileTarget file -> File.getFullName file
                        | PhonyAction name -> failwithf "Sign.executor: '%s' is a phony target; signing produces a file" name)
                let input = settings.Input (List.head targetPaths)
                let key = identity settings input

                let! bytes =
                    async {
                        match! store.TryFetch key with
                        | Some cached -> return cached
                        | None ->
                            let fresh =
                                lazy ((Resource.withAcquired budget 1 (async {
                                          let! signed = signer (request settings input)
                                          do! store.Publish key signed
                                          return signed
                                      })) |> Async.StartAsTask)
                            let! signed = (inFlight.GetOrAdd (key, fresh)).Value |> Async.AwaitTask
                            inFlight.TryRemove key |> ignore
                            return signed
                    }

                for path in targetPaths do
                    writeTo path bytes

                return
                    { Targets = targets
                      Built = DateTime.Now
                      Depends = [ FileDep (File.make input, File.getLastWriteTime (File.make input)) ]
                      Steps = [] }
            }

    // ---------------------------------------------------------------------------
    // the settings builder
    // ---------------------------------------------------------------------------

    /// Computation expression builder for `Sign.Settings`.
    type SignSettingsBuilder () =

        /// <summary>The rule mask for the signed output.</summary>
        [<CustomOperation("target")>]      member __.Target (s: Settings, value: string) = { s with Target = value }
        /// <summary>Maps a signed target path to the unsigned file to sign.</summary>
        [<CustomOperation("input")>]       member __.Input (s: Settings, value: string -> string) = { s with Input = value }
        /// <summary>Names the key -- a thumbprint, a Trusted Signing profile or an agent-side alias. Never key material.</summary>
        [<CustomOperation("certificate")>] member __.Certificate (s: Settings, value: Certificate) = { s with Certificate = value }
        /// <summary>The RFC 3161 timestamp server URL; unset means no countersignature.</summary>
        [<CustomOperation("timestamp")>]   member __.Timestamp (s: Settings, value: string) = { s with TimestampServer = Some value }
        /// <summary>The digest algorithm (default sha256).</summary>
        [<CustomOperation("hashalg")>]     member __.HashAlg (s: Settings, value: Algorithm) = { s with Hash = value }
        /// <summary>A human-readable description carried into the signature; not part of the identity key.</summary>
        [<CustomOperation("description")>] member __.Description (s: Settings, value: string) = { s with Description = Some value }

        member __.Bind (x, f) = f x
        member __.Yield (()) = Settings.Default
        member __.For (x, f) = f x

        member __.Zero () = Settings.Default
        member __.Run (s: Settings) = s

    /// The signing settings builder instance.
    let sign = SignSettingsBuilder ()
