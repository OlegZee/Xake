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
    /// reported as one final "Content" range.
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

        if ba.Length <> bb.Length then
            labelled @ [ { Offset = minLen; Length = (max ba.Length bb.Length) - minLen; Field = "Content" } ]
        else
            labelled

    /// A one-line verdict from `compare`'s result.
    let verdict (diffs: Difference list) : string =
        match diffs with
        | [] -> "identical"
        | _ ->
            let fields = diffs |> List.map (fun d -> d.Field) |> List.distinct
            if List.contains "Content" fields then
                sprintf "content differs (%d ranges)" (List.length diffs)
            else
                "identical except: " + String.concat ", " fields
