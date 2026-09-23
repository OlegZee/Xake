namespace Xake.Dotnet

open System
open System.IO
open System.Security.Cryptography

/// PE timestamp normalisation and CLR strong-name (re-)signing, used to make ring-2
/// (obfuscator) output byte-identical across runs (brief §8j "E4"; see
/// docs/features/hermetic-build/strongname.md). Pure -- reads/writes bytes handed to it,
/// touches the filesystem only in `signFile`/`normalise`. Reuses `Verify.layout`
/// (`InternalsVisibleTo` already covers this assembly's own modules) for the PE/CLR offsets.
module StrongName =

    let private u16 (b: byte[]) (o: int) = int b.[o] ||| (int b.[o + 1] <<< 8)
    let private i32 (b: byte[]) (o: int) =
        int b.[o] ||| (int b.[o + 1] <<< 8) ||| (int b.[o + 2] <<< 16) ||| (int b.[o + 3] <<< 24)

    let private writeU32 (bytes: byte[]) (offset: int) (value: uint32) =
        bytes.[offset] <- byte value
        bytes.[offset + 1] <- byte (value >>> 8)
        bytes.[offset + 2] <- byte (value >>> 16)
        bytes.[offset + 3] <- byte (value >>> 24)

    // ---------------------------------------------------------------------------
    // CheckSum -- the standard "MapFileAndCheckSum" algorithm: a 16-bit ones-complement
    // sum over the whole file (as a stream of 32-bit words, folded down to 16 bits, which
    // is arithmetically the same running sum imagehlp computes), the CheckSum field itself
    // treated as zero, plus the file length. This dword-at-a-time formulation is the one
    // widely reproduced (dnlib, pefile, LIEF) as bit-for-bit compatible with Windows'
    // CheckSumMappedFile / signtool.
    // ---------------------------------------------------------------------------

    let private computeChecksumValue (image: byte[]) (checksumOffset: int) : uint32 =
        let len = image.Length
        let paddedLen = if len % 4 = 0 then len else len + (4 - len % 4)
        let byteAt i = if i < len then uint64 image.[i] else 0UL
        let dwordAt o = byteAt o ||| (byteAt (o + 1) <<< 8) ||| (byteAt (o + 2) <<< 16) ||| (byteAt (o + 3) <<< 24)
        let checksumDword = checksumOffset / 4
        let mutable checksum = 0UL
        for i in 0 .. (paddedLen / 4) - 1 do
            if i <> checksumDword then
                checksum <- (checksum &&& 0xFFFFFFFFUL) + dwordAt (i * 4) + (checksum >>> 32)
                if checksum > 0xFFFFFFFFUL then
                    checksum <- (checksum &&& 0xFFFFFFFFUL) + (checksum >>> 32)
        checksum <- (checksum &&& 0xFFFFUL) + (checksum >>> 16)
        checksum <- checksum + (checksum >>> 16)
        checksum <- checksum &&& 0xFFFFUL
        checksum <- checksum + uint64 len
        uint32 checksum

    /// Recomputes only the PE `CheckSum` field. Returns the new bytes.
    let checksum (image: byte[]) : byte[] =
        let result = Array.copy image
        let l = Verify.layout result
        let (checkSumOffset, _) = l.CheckSum
        writeU32 result checkSumOffset 0u
        let value = computeChecksumValue result checkSumOffset
        writeU32 result checkSumOffset value
        result

    /// Sets the COFF `TimeDateStamp` field (leaves `CheckSum` untouched -- the caller
    /// recomputes it, see `checksum`/`stamp`).
    let private setTimeDateStamp (image: byte[]) (timeDateStamp: uint32) : byte[] =
        let result = Array.copy image
        let l = Verify.layout result
        let (tdsOffset, _) = l.TimeDateStamp
        writeU32 result tdsOffset timeDateStamp
        result

    /// Sets the COFF `TimeDateStamp` and recomputes the PE `CheckSum`. Returns the new bytes.
    let stamp (image: byte[]) (timeDateStamp: uint32) : byte[] =
        setTimeDateStamp image timeDateStamp |> checksum

    // ---------------------------------------------------------------------------
    // .snk (CAPI PRIVATEKEYBLOB / PUBLICKEYBLOB) <-> RSAParameters.
    //
    // BLOBHEADER (8 bytes): bType (1: 0x06 PUBLICKEYBLOB / 0x07 PRIVATEKEYBLOB), bVersion (1),
    // reserved (2), aiKeyAlg (4, CALG_RSA_SIGN = 0x00002400 for a strong-name key).
    // RSAPUBKEY (12 bytes): magic ("RSA1" public-only / "RSA2" with private parts), bitlen (4),
    // pubexp (4). Followed, all little-endian (CAPI convention -- the reverse of .NET's
    // RSAParameters, which are big-endian): modulus (bitlen/8); for a private key, prime1 (P),
    // prime2 (Q), exponent1 (DP), exponent2 (DQ), coefficient (InverseQ) (each bitlen/16), then
    // the private exponent D (bitlen/8).
    // ---------------------------------------------------------------------------

    let private toFixedLen (len: int) (b: byte[]) : byte[] =
        if isNull b then Array.zeroCreate len
        elif b.Length = len then b
        elif b.Length < len then Array.append (Array.zeroCreate (len - b.Length)) b
        else b.[(b.Length - len) ..]

    /// Reads an .snk (CAPI PRIVATEKEYBLOB or PUBLICKEYBLOB) into RSAParameters, and whether it
    /// carried the private key. Fails clearly on anything else -- no .pfx support.
    let readSnk (path: string) : RSAParameters * bool =
        let blob = File.ReadAllBytes path
        if blob.Length < 20 then failwithf "%s is too short to be a CAPI RSA key blob" path
        let bType = blob.[0]
        let magic = Text.Encoding.ASCII.GetString(blob, 8, 4)
        let bitLen = i32 blob 12
        let pubExpDword = uint32 (i32 blob 16)
        let byteLen = bitLen / 8
        let halfLen = bitLen / 16
        let mutable offset = 20
        let readBlock len =
            let b = blob.[offset .. offset + len - 1]
            offset <- offset + len
            Array.rev b
        let exponent =
            let e = BitConverter.GetBytes(pubExpDword) |> Array.rev // -> big-endian
            match e |> Array.tryFindIndex (fun x -> x <> 0uy) with
            | Some i -> e.[i ..]
            | None -> [| 0uy |]
        match bType, magic with
        | 0x06uy, "RSA1" ->
            let modulus = readBlock byteLen
            let mutable p = RSAParameters()
            p.Modulus <- modulus
            p.Exponent <- exponent
            p, false
        | 0x07uy, "RSA2" ->
            let modulus = readBlock byteLen
            let pp = readBlock halfLen
            let q = readBlock halfLen
            let dp = readBlock halfLen
            let dq = readBlock halfLen
            let iq = readBlock halfLen
            let d = readBlock byteLen
            let mutable key = RSAParameters()
            key.Modulus <- modulus
            key.Exponent <- exponent
            key.P <- pp
            key.Q <- q
            key.DP <- dp
            key.DQ <- dq
            key.InverseQ <- iq
            key.D <- d
            key, true
        | _ -> failwithf "%s is not a recognised CAPI RSA key blob (type=0x%02x, magic=%s) -- only .snk is supported, not .pfx" path bType magic

    /// Writes an RSAParameters (private key required) as a CAPI PRIVATEKEYBLOB, the format an
    /// .snk file holds. `internal`: a test-only round-trip helper (`readSnk (writeSnk k) = k`)
    /// used to fabricate a throwaway signing key without shelling out to `sn`/`dotnet dev-certs`.
    let internal writeSnk (key: RSAParameters) : byte[] =
        if isNull key.D then failwith "writeSnk needs a private key (RSAParameters.D is null)"
        let byteLen = key.Modulus.Length
        let bitLen = byteLen * 8
        let halfLen = byteLen / 2
        let header = [| 0x07uy; 0x02uy; 0x00uy; 0x00uy; 0x00uy; 0x24uy; 0x00uy; 0x00uy |]
        let magic = Text.Encoding.ASCII.GetBytes "RSA2"
        let bitLenBytes = BitConverter.GetBytes(bitLen)
        let pubExpBytes = toFixedLen 4 key.Exponent |> Array.rev
        let block len (b: byte[]) = toFixedLen len b |> Array.rev
        Array.concat
            [ header; magic; bitLenBytes; pubExpBytes
              block byteLen key.Modulus
              block halfLen key.P
              block halfLen key.Q
              block halfLen key.DP
              block halfLen key.DQ
              block halfLen key.InverseQ
              block byteLen key.D ]

    // ---------------------------------------------------------------------------
    // Strong-name signature.
    //
    // Hash-exclusion rule (verified against csc's own output byte for byte, see
    // `StrongNameTests.fs` and `hashForSigning` below): the signed content is the PE header up
    // to but excluding its file-alignment padding, with `CheckSum` and the Certificate Table
    // directory entry zeroed, followed by every section (with their padding) with the
    // StrongNameSignature blob itself cut out of the stream entirely (not zeroed).
    //
    // Hash algorithm: ECMA-335 leaves it to the assembly's Assembly-table `HashAlgId` (default
    // 0x8004 = SHA-1). This module hard-codes SHA-1, which is csc's default and what the E4
    // Babel key uses; see strongname.md for the SHA-256 caveat.
    // ---------------------------------------------------------------------------

    let private cliHeaderOffset (bytes: byte[]) : int =
        let l = Verify.layout bytes
        // Verify.Layout does not expose the CLI header's own file offset (only the signature
        // blob it points into), so its RVA is re-resolved here the same way `Verify.layout`
        // does internally: data directory entry 14 -> section table -> file offset.
        let peOffset = i32 bytes 0x3C
        let coffOffset = peOffset + 4
        let numberOfSections = u16 bytes (coffOffset + 2)
        let sizeOfOptionalHeader = u16 bytes (coffOffset + 16)
        let optionalOffset = coffOffset + 20
        let dataDirOffset = optionalOffset + (if l.Is64 then 112 else 96)
        let clrRva = i32 bytes (dataDirOffset + 14 * 8)
        let sectionHeadersOffset = optionalOffset + sizeOfOptionalHeader
        let sections =
            [ for s in 0 .. numberOfSections - 1 ->
                let o = sectionHeadersOffset + s * 40
                (i32 bytes (o + 12), i32 bytes (o + 8), i32 bytes (o + 20), i32 bytes (o + 16)) ]
        match sections |> List.tryFind (fun (va, vsize, _, rawSize) -> clrRva >= va && clrRva < va + (max vsize rawSize)) with
        | Some (va, _, ptr, _) -> ptr + (clrRva - va)
        | None -> failwith "could not locate the CLI header (no section covers its RVA)"

    /// Sets COMIMAGE_FLAGS_STRONGNAMESIGNED (0x8) in the CLI header Flags (IMAGE_COR20_HEADER
    /// + 16, right before EntryPointToken; StrongNameSignature is at +32, per the CLI header).
    let private setStrongNameFlag (bytes: byte[]) =
        let flagsOffset = cliHeaderOffset bytes + 16
        let flags = uint32 (i32 bytes flagsOffset)
        writeU32 bytes flagsOffset (flags ||| 0x8u)

    let private align (value: int) (alignment: int) : int = ((value + alignment - 1) / alignment) * alignment

    /// The exact bytes the CLR strong-name signature is computed over -- verified byte-for-byte
    /// against csc's own signature in `StrongNameTests.fs`, by decoding csc's signature with
    /// the public key and reading off the SHA-1 it actually signed (this is not documented
    /// anywhere as cleanly as the code itself: `System.Reflection.Metadata`'s
    /// `PEBuilder.GetContentToSign`, called from Roslyn's `SigningUtilities.CalculateRsaSignature`
    /// via `DesktopStrongNameProvider.SignBuilder`). The signed content is:
    ///   - the PE header (DOS/COFF/optional headers + section table) up to but EXCLUDING its
    ///     file-alignment padding -- with `CheckSum` and the Certificate Table directory entry
    ///     zeroed, because neither has been written yet when signing happens (`CheckSum` is
    ///     computed only afterwards -- see `sign`/`normalise` -- and a Certificate Table, if
    ///     any, is added later still by Authenticode signing, deliberately outside strong-name's
    ///     hash so that a strong name survives being Authenticode-signed afterwards);
    ///   - then every section verbatim, including inter-section alignment padding, with the
    ///     StrongNameSignature blob itself cut out of the byte stream entirely (not zeroed --
    ///     it sits inside section data, not a fixed header field, so the algorithm just skips
    ///     over it rather than hashing a same-sized zero block).
    /// The earlier "exclude checksum + cert entry + cert table + signature, uniformly, from an
    /// otherwise-untouched file" reading of the brief did not reproduce csc's bytes; zeroing
    /// (not excluding) the two header fields, and excluding the header's own alignment padding,
    /// is what does.
    let private hashForSigning (image: byte[]) (l: Verify.Layout) (sigRange: int * int) : byte[] =
        let fileAlignment = i32 image (l.OptionalOffset + 36)
        let peHeadersSize = l.SectionHeadersOffset + l.NumberOfSections * 40
        let alignedHeaderEnd = align peHeadersSize fileAlignment
        let (sigOffset, sigSize) = sigRange

        let header = Array.sub image 0 peHeadersSize
        let (checkSumOffset, _) = l.CheckSum
        let (certDirOffset, _) = l.CertTableDirEntry
        for i in 0 .. 3 do header.[checkSumOffset + i] <- 0uy
        for i in 0 .. 7 do header.[certDirOffset + i] <- 0uy

        use sha1 = SHA1.Create()
        sha1.TransformBlock(header, 0, header.Length, null, 0) |> ignore
        let sectionsLen = sigOffset - alignedHeaderEnd
        if sectionsLen > 0 then sha1.TransformBlock(image, alignedHeaderEnd, sectionsLen, null, 0) |> ignore
        let tailStart = sigOffset + sigSize
        let tailLen = image.Length - tailStart
        (if tailLen > 0 then sha1.TransformFinalBlock(image, tailStart, tailLen)
         else sha1.TransformFinalBlock(Array.empty, 0, 0)) |> ignore
        sha1.Hash

    /// Re-signs the CLR strong-name signature with `key` (must carry the private key): hashes
    /// the image per the exclusion rule above, RSA/PKCS#1-v1.5-signs it, and writes the
    /// signature into the blob the CLI header's StrongNameSignature entry names -- in CAPI's
    /// little-endian byte order (.NET's `RSA.SignHash` returns the mathematical, big-endian
    /// order; it is reversed here). Also sets COMIMAGE_FLAGS_STRONGNAMESIGNED. Does *not*
    /// recompute `CheckSum` (the signature bytes just changed it) -- callers needing a
    /// consistent file call `checksum` afterwards; `normalise` does this for the E4 pipeline.
    let sign (image: byte[]) (key: RSAParameters) : byte[] =
        if isNull key.D then failwith "sign needs a private key (RSAParameters.D is null)"
        let result = Array.copy image
        let l = Verify.layout result
        match l.StrongNameSignature with
        | None -> failwith "image has no StrongNameSignature directory entry -- not a strong-name-capable assembly"
        | Some (sigOffset, sigSize) ->
            let hash = hashForSigning result l (sigOffset, sigSize)
            use rsa = RSA.Create()
            rsa.ImportParameters key
            let signatureBigEndian = rsa.SignHash(hash, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1)
            if signatureBigEndian.Length <> sigSize then
                failwithf "computed signature is %d bytes, but the image's StrongNameSignature blob is %d bytes"
                    signatureBigEndian.Length sigSize
            let signatureLittleEndian = Array.rev signatureBigEndian
            Array.blit signatureLittleEndian 0 result sigOffset sigSize
            setStrongNameFlag result
            result

    /// Verifies an existing strong-name signature against `key` (the public components are
    /// enough).
    let verify (image: byte[]) (key: RSAParameters) : bool =
        let l = Verify.layout image
        match l.StrongNameSignature with
        | None -> false
        | Some (sigOffset, sigSize) ->
            try
                let hash = hashForSigning image l (sigOffset, sigSize)
                let signatureBigEndian = image.[sigOffset .. sigOffset + sigSize - 1] |> Array.rev
                use rsa = RSA.Create()
                rsa.ImportParameters key
                rsa.VerifyHash(hash, signatureBigEndian, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1)
            with _ -> false

    /// Re-signs the file at `path` in place with the key named by `snkPath`.
    let signFile (path: string) (snkPath: string) : unit =
        let image = File.ReadAllBytes path
        let key, isPrivate = readSnk snkPath
        if not isPrivate then failwithf "%s carries only a public key -- signing needs the private key" snkPath
        File.WriteAllBytes(path, sign image key)

    /// The whole E4 normalisation: stamp `timeDateStamp`, re-sign the strong name with
    /// `snkPath`'s private key, then recompute `CheckSum` last -- the signature bytes just
    /// changed, and CheckSum (unlike the strong-name hash) covers the whole file including
    /// them, so it must be computed after signing to be self-consistent.
    let normalise (path: string) (timeDateStamp: uint32) (snkPath: string) : unit =
        let image = File.ReadAllBytes path
        let key, isPrivate = readSnk snkPath
        if not isPrivate then failwithf "%s carries only a public key -- signing needs the private key" snkPath
        let stamped = setTimeDateStamp image timeDateStamp
        let signed = sign stamped key
        File.WriteAllBytes(path, checksum signed)
