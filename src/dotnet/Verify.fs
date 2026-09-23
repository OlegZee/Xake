namespace Xake.Dotnet

open System
open System.IO
open System.Security.Cryptography

/// PE/Authenticode inspection, used to compare what shipped against what a local build
/// produces (see docs/features/hermetic-build/verify.md). Pure -- nothing here touches the
/// network or a signing tool; it neither creates nor validates a signature, only measures.
module Verify =

    /// lowercase hex sha256 of a file
    let sha256 (path: string) : string =
        use stream = File.OpenRead path
        use algo = SHA256.Create()
        algo.ComputeHash stream |> Array.map (sprintf "%02x") |> String.concat ""

    let private u16 (b: byte[]) (o: int) = int b.[o] ||| (int b.[o + 1] <<< 8)
    let private i32 (b: byte[]) (o: int) =
        int b.[o] ||| (int b.[o + 1] <<< 8) ||| (int b.[o + 2] <<< 16) ||| (int b.[o + 3] <<< 24)

    /// The byte ranges of the PE/CLR structures `authenticodeHash` excludes and `compare`
    /// labels. `internal` (`InternalsVisibleTo("tests")` on this assembly) so tests can corrupt
    /// exactly the bytes it names.
    type internal Layout = {
        Is64: bool
        CoffOffset: int
        OptionalOffset: int
        SectionHeadersOffset: int
        NumberOfSections: int
        /// (offset, length) of the COFF `TimeDateStamp` field
        TimeDateStamp: int * int
        /// (offset, length) of the optional header `CheckSum` field
        CheckSum: int * int
        /// (offset, length) of the Certificate Table data-directory entry itself (8 bytes)
        CertTableDirEntry: int * int
        /// (fileOffset, size) of the certificate table blob, when the entry is non-empty
        CertTable: (int * int) option
        /// (PointerToRawData, SizeOfRawData) of every non-empty section, in file-offset order
        Sections: (int * int) list
        /// (fileOffset, size) of the debug directory table, when present
        DebugDirectory: (int * int) option
        /// (fileOffset, 16) of the CodeView entry's MVID/GUID, when a CodeView (type 2) entry exists
        CodeViewGuid: (int * int) option
        /// (fileOffset, size) of the CLR strong-name signature, when the assembly has a CLR header
        StrongNameSignature: (int * int) option
        FileLength: int
    }

    /// RVA -> file offset via the section that contains it.
    let private rvaToFile (sections: (int * int * int * int) list) (rva: int) : int option =
        sections
        |> List.tryFind (fun (va, vsize, _, rawSize) -> rva >= va && rva < va + (max vsize rawSize))
        |> Option.map (fun (va, _, ptr, _) -> ptr + (rva - va))

    /// Parses the DOS/COFF/optional headers, the section table, the debug directory and the
    /// CLR header far enough to locate the fields below. Works for PE32 and PE32+ alike.
    let internal layout (bytes: byte[]) : Layout =
        let peOffset = i32 bytes 0x3C
        let coffOffset = peOffset + 4
        let numberOfSections = u16 bytes (coffOffset + 2)
        let timeDateStampOffset = coffOffset + 4
        let sizeOfOptionalHeader = u16 bytes (coffOffset + 16)
        let optionalOffset = coffOffset + 20
        let magic = u16 bytes optionalOffset
        let is64 = magic = 0x20b
        let checkSumOffset = optionalOffset + 64
        let dataDirOffset = optionalOffset + (if is64 then 112 else 96)
        let dirEntry i =
            let o = dataDirOffset + i * 8
            (o, i32 bytes o, i32 bytes (o + 4))
        let sectionHeadersOffset = optionalOffset + sizeOfOptionalHeader

        let sectionsRaw =
            [ for s in 0 .. numberOfSections - 1 ->
                let o = sectionHeadersOffset + s * 40
                let va = i32 bytes (o + 12)
                let vsize = i32 bytes (o + 8)
                let rawSize = i32 bytes (o + 16)
                let ptr = i32 bytes (o + 20)
                (va, vsize, ptr, rawSize) ]

        let sections =
            sectionsRaw
            |> List.map (fun (_, _, ptr, rawSize) -> (ptr, rawSize))
            |> List.filter (fun (_, size) -> size > 0)
            |> List.sortBy fst

        // this one entry's "VirtualAddress" is documented as a file offset, not an RVA
        let (certDirOffset, certFileOffset, certSize) = dirEntry 4
        let certTable = if certFileOffset > 0 && certSize > 0 then Some (certFileOffset, certSize) else None

        let (_, debugRva, debugSize) = dirEntry 6
        let debugDirectory =
            if debugRva > 0 && debugSize > 0 then
                rvaToFile sectionsRaw debugRva |> Option.map (fun o -> (o, debugSize))
            else None

        // IMAGE_DEBUG_DIRECTORY is 28 bytes: Type at +12, SizeOfData at +16, PointerToRawData at +24.
        // Type 2 (CodeView) data starts with an "RSDS" magic, then a 16-byte MVID/GUID.
        let codeViewGuid =
            debugDirectory
            |> Option.bind (fun (dirOffset, dirSize) ->
                [ 0 .. 28 .. dirSize - 28 ]
                |> List.tryPick (fun i ->
                    let entry = dirOffset + i
                    if entry + 28 > bytes.Length then None else
                    let debugType = i32 bytes (entry + 12)
                    let dataSize = i32 bytes (entry + 16)
                    let dataPtr = i32 bytes (entry + 24)
                    if debugType = 2 && dataSize >= 24 && dataPtr > 0 && dataPtr + 24 <= bytes.Length
                       && bytes.[dataPtr] = 0x52uy && bytes.[dataPtr + 1] = 0x53uy
                       && bytes.[dataPtr + 2] = 0x44uy && bytes.[dataPtr + 3] = 0x53uy then
                        Some (dataPtr + 4, 16)
                    else None))

        // CLR header (data directory entry 14) -> IMAGE_COR20_HEADER; StrongNameSignature (RVA, Size)
        // sits at offset 32 of that 72-byte structure.
        let (_, clrRva, clrSize) = dirEntry 14
        let strongNameSignature =
            if clrRva > 0 && clrSize > 0 then
                rvaToFile sectionsRaw clrRva
                |> Option.bind (fun clrOffset ->
                    if clrOffset + 40 > bytes.Length then None else
                    let snRva = i32 bytes (clrOffset + 32)
                    let snSize = i32 bytes (clrOffset + 36)
                    if snRva > 0 && snSize > 0 then rvaToFile sectionsRaw snRva |> Option.map (fun o -> (o, snSize))
                    else None)
            else None

        { Is64 = is64
          CoffOffset = coffOffset
          OptionalOffset = optionalOffset
          SectionHeadersOffset = sectionHeadersOffset
          NumberOfSections = numberOfSections
          TimeDateStamp = (timeDateStampOffset, 4)
          CheckSum = (checkSumOffset, 4)
          CertTableDirEntry = (certDirOffset, 8)
          CertTable = certTable
          Sections = sections
          DebugDirectory = debugDirectory
          CodeViewGuid = codeViewGuid
          StrongNameSignature = strongNameSignature
          FileLength = bytes.Length }

    /// The ranges Authenticode excludes from the hash, in file order: `CheckSum`, the
    /// Certificate Table directory entry, and the certificate table blob itself.
    let private excludedRanges (l: Layout) : (int * int) list =
        [ yield l.CheckSum
          yield l.CertTableDirEntry
          match l.CertTable with
          | Some r -> yield r
          | None -> () ]
        |> List.sortBy fst

    /// The Authenticode PE image hash: SHA-256 over the whole file, sections hashed in
    /// file-offset order, with `CheckSum`, the Certificate Table directory entry and the
    /// certificate table itself excluded. Two files with the same `authenticodeHash` are the
    /// same image modulo signature -- that is what makes it useful for "is the shipped,
    /// signed dll the same as this local, unsigned build" (see verify.md, E5).
    let authenticodeHash (path: string) : string =
        let bytes = File.ReadAllBytes path
        let l = layout bytes
        let excluded = excludedRanges l
        use algo = SHA256.Create()
        let feed from len = if len > 0 then algo.TransformBlock(bytes, from, len, null, 0) |> ignore
        let mutable pos = 0
        for (o, len) in excluded do
            if o > pos then feed pos (o - pos)
            pos <- max pos (o + len)
        let tail = bytes.Length - pos
        (if tail > 0 then algo.TransformFinalBlock(bytes, pos, tail)
         else algo.TransformFinalBlock(Array.empty, 0, 0)) |> ignore
        algo.Hash |> Array.map (sprintf "%02x") |> String.concat ""

    /// One byte range that differs between two files, labelled with the structure it falls in
    /// (per file A's layout) or "Content" when it is not a field `layout` recognises.
    type Difference = { Offset: int; Length: int; Field: string }

    let private fieldAt (l: Layout) (offset: int) : string =
        let inRange (o, len) = offset >= o && offset < o + len
        let inOpt r = match r with Some x -> inRange x | None -> false
        if inRange l.TimeDateStamp then "TimeDateStamp"
        elif inRange l.CheckSum then "CheckSum"
        elif inRange l.CertTableDirEntry || inOpt l.CertTable then "CertificateTable"
        elif inOpt l.CodeViewGuid || inOpt l.DebugDirectory then "DebugDirectory/PDB id (MVID/GUID)"
        elif inOpt l.StrongNameSignature then "StrongNameSignature"
        else "Content"

    /// What differs between two PE files, for the audit report: byte-compares them, merges
    /// adjacent differing bytes (gaps under 4 bytes bridged) into ranges, and labels each range
    /// using file `a`'s layout. `[]` when the files are identical. A trailing size mismatch is
    /// reported as one or more final ranges: when `b` is the longer file, the extra bytes exist
    /// only in `b`, so they (and any recognised field inside, typically an appended
    /// `CertificateTable`) are labelled from file `b`'s own layout instead -- a signed copy
    /// appends its certificate table after everything `a`'s layout can name. Every other size
    /// mismatch (including `a` the longer file) keeps the old single trailing "Content" range.
    let compare (a: string) (b: string) : Difference list =
        let ba = File.ReadAllBytes a
        let bb = File.ReadAllBytes b
        let la = layout ba
        let minLen = min ba.Length bb.Length

        let diffOffsets = [ for i in 0 .. minLen - 1 do if ba.[i] <> bb.[i] then yield i ]

        let ranges =
            (([]: (int * int) list), diffOffsets)
            ||> List.fold (fun acc off ->
                match acc with
                | (start, last) :: rest when off - last <= 4 -> (start, off) :: rest
                | _ -> (off, off) :: acc)
            |> List.rev
            |> List.map (fun (start, last) -> (start, last - start + 1))

        let labelled = ranges |> List.map (fun (o, len) -> { Offset = o; Length = len; Field = fieldAt la o })

        if ba.Length = bb.Length then
            labelled
        elif bb.Length > ba.Length then
            let lb = layout bb
            let tailOffsets = [ minLen .. bb.Length - 1 ]
            let tailRanges =
                (([]: (int * int * string) list), tailOffsets)
                ||> List.fold (fun acc off ->
                    let f = fieldAt lb off
                    match acc with
                    | (start, last, lf) :: rest when lf = f && off = last + 1 -> (start, off, f) :: rest
                    | _ -> (off, off, f) :: acc)
                |> List.rev
                |> List.map (fun (start, last, f) -> { Offset = start; Length = last - start + 1; Field = f })
            labelled @ tailRanges
        else
            labelled @ [ { Offset = minLen; Length = (max ba.Length bb.Length) - minLen; Field = "Content" } ]

    /// Space-grouped thousands, e.g. `148213 -> "148 213"` -- readable byte counts in `verdict`.
    let private formatThousands (n: int) : string =
        let s = string n
        let len = s.Length
        if len <= 3 then s
        else
            let firstLen = match len % 3 with 0 -> 3 | r -> r
            [ s.Substring(0, firstLen) ] @ [ for i in firstLen .. 3 .. len - 3 -> s.Substring(i, 3) ]
            |> String.concat " "

    /// A one-line verdict from `compare`'s result: the field list (or range count) plus how much
    /// actually differs -- the total differing bytes across all ranges, and, when unlabelled
    /// content is involved, the single largest range (its field, offset and length), so a big
    /// block does not hide behind a range count.
    let verdict (diffs: Difference list) : string =
        match diffs with
        | [] -> "identical"
        | _ ->
            let fields = diffs |> List.map (fun d -> d.Field) |> List.distinct
            let totalBytes = diffs |> List.sumBy (fun d -> d.Length)
            if List.contains "Content" fields then
                let largest = diffs |> List.maxBy (fun d -> d.Length)
                sprintf "content differs: %d ranges, %s bytes, largest %s bytes at 0x%x (%s)"
                    (List.length diffs) (formatThousands totalBytes) (formatThousands largest.Length) largest.Offset largest.Field
            else
                sprintf "identical except: %s, %s bytes" (String.concat ", " fields) (formatThousands totalBytes)

    /// The RFC's acceptance checks for a package-scope SBOM (nuget-sbom.md "Package scope",
    /// tracker "acceptance checks 3.1-3.4"), run against the nupkg the document describes.
    /// Returns one line per finding; `[]` means the document passes. Mechanical diffs only --
    /// nupkg <-> SBOM <-> nuspec -- no policy:
    /// - 3.1 every component traces to something: each of the root's nested components names
    ///   a file shipped for the TFM (`xake:nuget:path`) whose bytes hash to the recorded
    ///   SHA-256/SHA-512; each top-level component is a nuspec dependency of that TFM's group
    /// - 3.2 every shipped assembly and native (`IsAssembly` / `IsNative`) has a nested
    ///   component with both hashes
    /// - 3.3 tier-2 names and the `DeclaredRangeProperty` equal the nuspec text (ordinal), and
    ///   every declared dependency the rule keeps (`IsTooling` drops tooling) is present
    /// - 3.4 the boundary is declared: a `complete` composition over `assemblies` naming the
    ///   root, an `incomplete` one over `dependencies` naming it, an annotation with the root as
    ///   subject; `dependencies[]` has no entry whose `ref` is a tier-2 component, and every
    ///   `ref`/`dependsOn` names a component the document has
    ///
    /// `options` must be the record the document was produced with (`Sbom.forPackageScopedWith`).
    let sbomPackageScopeWith (options: Sbom.PackageScopeOptions) (nupkgPath: string) (framework: string) (bom: Sbom.Bom) : string list =
        let entries = Sbom.nupkgEntries nupkgPath
        let nuspec = Sbom.nuspecOfEntries entries
        let shipped = Sbom.shippedPaths options framework entries
        let bytesOf = entries |> Map.ofList
        let hex (algo: HashAlgorithm) (bytes: byte[]) = algo.ComputeHash bytes |> Array.map (sprintf "%02x") |> String.concat ""
        let pathOf (c: Sbom.Component) =
            c.Properties |> List.tryPick (fun p -> if p.Name = Sbom.pathProperty then Some p.Value else None)
        let findings = ResizeArray<string> ()
        let fail (check: string) (message: string) = findings.Add (sprintf "%s: %s" check message)

        // 3.1 tier 1 traces to shipped bytes
        for c in bom.Root.Components do
            match pathOf c with
            | None -> fail "3.1" (sprintf "component '%s' names no shipped file" c.BomRef)
            | Some path when not (List.contains path shipped) -> fail "3.1" (sprintf "component '%s' names '%s', which is not shipped for %s" c.BomRef path framework)
            | Some path ->
                let bytes = bytesOf.[path]
                let expect alg (algo: unit -> HashAlgorithm) =
                    match c.Hashes |> List.tryFind (fun h -> h.Alg = alg) with
                    | None -> fail "3.1" (sprintf "'%s' has no %s hash" path alg)
                    | Some h ->
                        use a = algo ()
                        if h.Content <> hex a bytes then fail "3.1" (sprintf "'%s' %s does not match the shipped bytes" path alg)
                expect "SHA-256" (fun () -> SHA256.Create () :> HashAlgorithm)
                expect "SHA-512" (fun () -> SHA512.Create () :> HashAlgorithm)

        // 3.1 / 3.3 tier 2 traces to the nuspec, textually
        let declared = Nuget.nuspecDependenciesFor framework nuspec |> List.filter (fun d -> not (options.IsTooling d.Id))
        for c in bom.Components do
            match declared |> List.tryFind (fun d -> d.Id = c.Name) with
            | None -> fail "3.1" (sprintf "component '%s' is not a nuspec dependency of %s" c.BomRef framework)
            | Some d ->
                match c.Properties |> List.tryPick (fun p -> if p.Name = options.DeclaredRangeProperty then Some p.Value else None) with
                | Some range when range = d.Range -> ()
                | Some range -> fail "3.3" (sprintf "'%s' declares range '%s', the nuspec says '%s'" c.Name range d.Range)
                | None -> fail "3.3" (sprintf "'%s' carries no %s" c.Name options.DeclaredRangeProperty)
        for d in declared do
            if not (bom.Components |> List.exists (fun c -> c.Name = d.Id)) then
                fail "3.3" (sprintf "nuspec dependency '%s' has no component" d.Id)

        // 3.2 every shipped binary has a hashed component
        for path in shipped |> List.filter (fun p -> options.IsAssembly p || options.IsNative p) do
            match bom.Root.Components |> List.tryFind (fun c -> pathOf c = Some path) with
            | None -> fail "3.2" (sprintf "shipped binary '%s' has no component" path)
            | Some c when c.Hashes |> List.exists (fun h -> h.Alg = "SHA-256") |> not -> fail "3.2" (sprintf "shipped binary '%s' has no SHA-256" path)
            | Some _ -> ()

        // 3.4 the boundary
        let rootRef = bom.Root.BomRef
        if not (bom.Compositions |> List.exists (fun c -> c.Aggregate = "complete" && List.contains rootRef c.Assemblies)) then
            fail "3.4" "no 'complete' composition over assemblies naming the root"
        if not (bom.Compositions |> List.exists (fun c -> c.Aggregate = "incomplete" && List.contains rootRef c.Dependencies)) then
            fail "3.4" "no 'incomplete' composition over dependencies naming the root"
        if not (bom.Annotations |> List.exists (fun a -> List.contains rootRef a.Subjects && a.Text <> "")) then
            fail "3.4" "no boundary annotation on the root"
        let tier2Refs = bom.Components |> List.map (fun c -> c.BomRef) |> Set.ofList
        let known = Set.union tier2Refs (bom.Root.Components |> List.map (fun c -> c.BomRef) |> Set.ofList) |> Set.add rootRef
        for (r, dependsOn) in bom.Dependencies do
            if tier2Refs.Contains r then fail "3.4" (sprintf "dependencies[] carries tier-2 component '%s' as a ref" r)
            elif not (known.Contains r) then fail "3.4" (sprintf "dependencies[] ref '%s' is not a component of this document" r)
            for d in dependsOn do
                if not (known.Contains d) then fail "3.4" (sprintf "'%s' dependsOn '%s', which is not a component of this document" r d)

        List.ofSeq findings

    /// `sbomPackageScopeWith Sbom.defaultPackageScope`.
    let sbomPackageScope (nupkgPath: string) (framework: string) (bom: Sbom.Bom) : string list =
        sbomPackageScopeWith Sbom.defaultPackageScope nupkgPath framework bom
