namespace Xake.Dotnet

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Security.Cryptography

/// A deterministic zip/nupkg writer (docs/features/hermetic-build/pack.md). `dotnet pack`'s
/// output is a zip whose entry order, timestamps and OPC metadata are not reproducible across
/// runs; this module writes the zip format by hand -- sorted entries, a fixed timestamp, no
/// extra fields, no comments -- so that packing the same inputs twice gives byte-identical
/// bytes. Hand-rolled rather than `System.IO.Compression.ZipArchive`: `ZipArchiveEntry.Crc32`
/// (needed by `entries` below) is not part of the netstandard2.0/net462 API surface that this
/// assembly targets -- confirmed by compiling a throwaway project against both TFMs, where
/// `entry.Crc32` fails with FS0039 on each. `ZipArchive` itself was never exercised for
/// determinism because of this: reading the central directory back needs a hand parser either
/// way, so the writer is hand-rolled too, for one code path and no platform-dependent external
/// attributes (Unix permission bits, "version made by" host byte) that `ZipArchive` would embed.
module Pack =

    /// One file to place in the archive: `Path` is the path inside the zip (forward slashes),
    /// `Source` is the file on disk to read its bytes from.
    type Entry = { Path: string; Source: string }

    /// A file already read into memory, used internally so generated OPC parts (content types,
    /// rels, psmdcp) do not need a temp file on disk to be zipped alongside real files.
    type private RawEntry = { RPath: string; Bytes: byte[] }

    type Options = {
        /// entry timestamps (zip DOS time, 2-second resolution); default 1980-01-01 00:00:00 --
        /// or from SOURCE_DATE_EPOCH when set
        Timestamp: DateTime
        /// compression level
        Level: CompressionLevel
    }

    /// 1980-01-01 (the DOS epoch, and the oldest date the zip format can represent) unless
    /// `SOURCE_DATE_EPOCH` (unix seconds, https://reproducible-builds.org/specs/source-date-epoch/)
    /// names a later one.
    let defaultOptions : Options =
        let epoch = DateTime (1980, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        let fromEnv =
            match Environment.GetEnvironmentVariable "SOURCE_DATE_EPOCH" with
            | null | "" -> None
            | v ->
                match Int64.TryParse v with
                | true, seconds ->
                    let dt = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                    Some (if dt < epoch then epoch else dt)
                | false, _ -> None
        { Timestamp = fromEnv |> Option.defaultValue epoch; Level = CompressionLevel.Optimal }

    // ---- CRC-32 (ISO 3309 / zip's polynomial), a table-driven implementation: ZipArchiveEntry
    // does not expose Crc32 on netstandard2.0/net462 (see the module comment), so both the
    // writer and `entries` compute it themselves. ----

    let private crcTable : uint32[] =
        Array.init 256 (fun i ->
            let mutable c = uint32 i
            for _ in 0 .. 7 do
                c <- if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1
            c)

    let private crc32 (bytes: byte[]) : uint32 =
        let mutable crc = 0xFFFFFFFFu
        for b in bytes do
            crc <- crcTable.[int ((crc ^^^ uint32 b) &&& 0xFFu)] ^^^ (crc >>> 8)
        crc ^^^ 0xFFFFFFFFu

    let private deflate (level: CompressionLevel) (bytes: byte[]) : byte[] =
        use ms = new MemoryStream()
        (
            use ds = new DeflateStream (ms, level, true)
            ds.Write (bytes, 0, bytes.Length)
        )
        ms.ToArray ()

    /// zip DOS time/date (2-second resolution, no timezone: the clock value of `dt` is used
    /// literally, so the caller decides what "the fixed timestamp" means).
    let private dosDateTime (dt: DateTime) : uint16 * uint16 =
        let time = (uint16 dt.Hour <<< 11) ||| (uint16 dt.Minute <<< 5) ||| (uint16 (dt.Second / 2))
        let date = (uint16 (max 0 (dt.Year - 1980)) <<< 9) ||| (uint16 dt.Month <<< 5) ||| (uint16 dt.Day)
        time, date

    let private dosToDateTime (date: int) (time: int) : DateTime =
        let year = 1980 + ((date >>> 9) &&& 0x7F)
        let month = max 1 ((date >>> 5) &&& 0xF)
        let day = max 1 (date &&& 0x1F)
        let hour = (time >>> 11) &&& 0x1F
        let minute = (time >>> 5) &&& 0x3F
        let second = (time &&& 0x1F) * 2
        DateTime (year, month, day, hour, minute, second)

    let private writeU16 (s: Stream) (v: uint16) =
        s.WriteByte (byte (v &&& 0xFFus))
        s.WriteByte (byte (v >>> 8))

    let private writeU32 (s: Stream) (v: uint32) =
        s.WriteByte (byte (v &&& 0xFFu))
        s.WriteByte (byte ((v >>> 8) &&& 0xFFu))
        s.WriteByte (byte ((v >>> 16) &&& 0xFFu))
        s.WriteByte (byte ((v >>> 24) &&& 0xFFu))

    let private readU16 (bytes: byte[]) (o: int) : int = int bytes.[o] ||| (int bytes.[o + 1] <<< 8)

    let private readU32 (bytes: byte[]) (o: int) : uint32 =
        uint32 bytes.[o] ||| (uint32 bytes.[o + 1] <<< 8) ||| (uint32 bytes.[o + 2] <<< 16) ||| (uint32 bytes.[o + 3] <<< 24)

    /// language-encoding flag (bit 11): file names are written as UTF-8.
    let private generalPurposeFlag = 0x0800us
    let private versionNeeded = 20us
    /// "version made by": low byte 20 (2.0), high byte 0 (MS-DOS/FAT, i.e. no host platform) --
    /// picked precisely so the central directory carries no platform-specific bit, unlike
    /// `ZipArchive`, which stamps the running OS.
    let private versionMadeBy = 0x0014us

    type private WrittenEntry = {
        WPath: string
        Crc: uint32
        CompressedSize: uint32
        UncompressedSize: uint32
        LocalHeaderOffset: uint32
        Time: uint16
        Date: uint16
        Method: uint16
    }

    let private writeZip (output: string) (raw: RawEntry list) (options: Options) : unit =
        let sorted = raw |> List.sortWith (fun a b -> String.CompareOrdinal (a.RPath, b.RPath))
        let time, date = dosDateTime options.Timestamp
        if File.Exists output then File.Delete output
        use fs = new FileStream (output, FileMode.Create, FileAccess.Write)

        let written =
            sorted
            |> List.map (fun e ->
                let path = e.RPath.Replace ('\\', '/')
                let nameBytes = Encoding.UTF8.GetBytes path
                let crc = crc32 e.Bytes
                let method, data =
                    if options.Level = CompressionLevel.NoCompression then 0us, e.Bytes
                    else 8us, deflate options.Level e.Bytes
                let offset = uint32 fs.Position
                writeU32 fs 0x04034b50u
                writeU16 fs versionNeeded
                writeU16 fs generalPurposeFlag
                writeU16 fs method
                writeU16 fs time
                writeU16 fs date
                writeU32 fs crc
                writeU32 fs (uint32 data.Length)
                writeU32 fs (uint32 e.Bytes.Length)
                writeU16 fs (uint16 nameBytes.Length)
                writeU16 fs 0us // extra field length
                fs.Write (nameBytes, 0, nameBytes.Length)
                fs.Write (data, 0, data.Length)
                { WPath = path; Crc = crc; CompressedSize = uint32 data.Length; UncompressedSize = uint32 e.Bytes.Length
                  LocalHeaderOffset = offset; Time = time; Date = date; Method = method })

        let cdStart = uint32 fs.Position
        for w in written do
            let nameBytes = Encoding.UTF8.GetBytes w.WPath
            writeU32 fs 0x02014b50u
            writeU16 fs versionMadeBy
            writeU16 fs versionNeeded
            writeU16 fs generalPurposeFlag
            writeU16 fs w.Method
            writeU16 fs w.Time
            writeU16 fs w.Date
            writeU32 fs w.Crc
            writeU32 fs w.CompressedSize
            writeU32 fs w.UncompressedSize
            writeU16 fs (uint16 nameBytes.Length)
            writeU16 fs 0us // extra field length
            writeU16 fs 0us // comment length
            writeU16 fs 0us // disk number start
            writeU16 fs 0us // internal file attributes
            writeU32 fs 0u  // external file attributes -- deliberately platform-neutral (no unix perm bits)
            writeU32 fs w.LocalHeaderOffset
            fs.Write (nameBytes, 0, nameBytes.Length)
        let cdSize = uint32 fs.Position - cdStart

        writeU32 fs 0x06054b50u
        writeU16 fs 0us // disk number
        writeU16 fs 0us // disk with central directory
        writeU16 fs (uint16 written.Length)
        writeU16 fs (uint16 written.Length)
        writeU32 fs cdSize
        writeU32 fs cdStart
        writeU16 fs 0us // comment length

    /// Writes the entries sorted by path (ordinal), each with the fixed timestamp, no extra
    /// fields, no comments; returns the output path.
    let zip (output: string) (entries: Entry list) (options: Options) : unit =
        entries
        |> List.map (fun e -> { RPath = e.Path; Bytes = File.ReadAllBytes e.Source })
        |> fun raw -> writeZip output raw options

    // ---- nupkg: OPC (Open Packaging Conventions) parts around the plain zip above ----

    let private xmlChild (name: string) (el: System.Xml.XmlElement) : System.Xml.XmlElement option =
        el.ChildNodes
        |> Seq.cast<System.Xml.XmlNode>
        |> Seq.tryPick (function :? System.Xml.XmlElement as e when e.LocalName = name -> Some e | _ -> None)

    let private xmlText (el: System.Xml.XmlElement option) : string =
        el |> Option.map (fun e -> e.InnerText.Trim ()) |> Option.defaultValue ""

    /// Reads `id`/`version` from a nuspec the way `Nuget.readCache` reads a cached one:
    /// namespace-agnostic, by local name, with `System.Xml.XmlDocument`.
    let private readNuspecIdVersion (nuspecPath: string) : string * string =
        let doc = System.Xml.XmlDocument ()
        doc.Load nuspecPath
        let metadata = doc.DocumentElement |> xmlChild "metadata"
        let id = metadata |> Option.bind (xmlChild "id") |> xmlText
        let version = metadata |> Option.bind (xmlChild "version") |> xmlText
        id, version

    let private sha256HexBytes (bytes: byte[]) : string =
        use algo = SHA256.Create ()
        algo.ComputeHash bytes |> Array.map (sprintf "%02x") |> String.concat ""

    /// A GUID derived from sha256(id, version, sorted "path:hash" pairs) instead of a random
    /// one, so the psmdcp part -- and so the whole nupkg -- is a pure function of its inputs:
    /// change one byte of one file and the GUID (and everything after it) changes too.
    let private deriveGuid (id: string) (version: string) (fileHashes: (string * string) list) : Guid =
        let sorted = fileHashes |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal (a, b))
        let material =
            id + "|" + version + "|" + (sorted |> List.map (fun (p, h) -> p + ":" + h) |> String.concat "|")
        use algo = SHA256.Create ()
        let hash = algo.ComputeHash (Encoding.UTF8.GetBytes material)
        Guid (hash |> Array.take 16)

    let private buildContentTypes (extensions: string list) : string =
        let defaults =
            [ "rels", "application/vnd.openxmlformats-package.relationships+xml"
              "psmdcp", "application/vnd.openxmlformats-package.core-properties+xml" ]
            @ (extensions |> List.filter (fun e -> e <> "rels" && e <> "psmdcp") |> List.map (fun e -> e, "application/octet"))
        let items =
            defaults
            |> List.map (fun (ext, ct) -> sprintf "<Default Extension=\"%s\" ContentType=\"%s\" />" ext ct)
            |> String.concat ""
        sprintf "<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">%s</Types>" items

    let private buildRels (nuspecName: string) (psmdcpName: string) : string =
        let relId (target: string) = "R" + (sha256HexBytes (Encoding.UTF8.GetBytes target)).Substring (0, 16)
        let manifestTarget = "/" + nuspecName
        let coreTarget = "/package/services/metadata/core-properties/" + psmdcpName
        sprintf
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Type=\"http://schemas.microsoft.com/packaging/2010/07/manifest\" Target=\"%s\" Id=\"%s\" /><Relationship Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"%s\" Id=\"%s\" /></Relationships>"
            manifestTarget (relId manifestTarget) coreTarget (relId coreTarget)

    let private buildPsmdcp (id: string) (version: string) : string =
        sprintf
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><coreProperties xmlns=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><dc:creator>%s</dc:creator><dc:identifier>%s</dc:identifier><version>%s</version><keywords /><lastModifiedBy>Xake.Dotnet.Pack</lastModifiedBy></coreProperties>"
            id id version

    /// A nupkg from a nuspec plus files: adds the nuspec at the root, `[Content_Types].xml`,
    /// `_rels/.rels`, and `package/services/metadata/core-properties/<id>.psmdcp` with a GUID
    /// derived from SHA-256 of (id, version, sorted file hashes) instead of a random one and no
    /// creation date -- so two packs of the same inputs are byte-identical. `files` are (zip
    /// path, source) pairs (e.g. `lib/netstandard2.0/X.dll`).
    let nupkg (output: string) (nuspec: string) (files: Entry list) (options: Options) : unit =
        let id, version = readNuspecIdVersion nuspec
        let nuspecName = id + ".nuspec"
        let nuspecBytes = File.ReadAllBytes nuspec
        let fileEntries = files |> List.map (fun e -> { RPath = e.Path; Bytes = File.ReadAllBytes e.Source })
        let allContent = { RPath = nuspecName; Bytes = nuspecBytes } :: fileEntries

        let fileHashes = allContent |> List.map (fun r -> r.RPath, sha256HexBytes r.Bytes)
        let guidName = (deriveGuid id version fileHashes).ToString "N"
        let psmdcpPath = sprintf "package/services/metadata/core-properties/%s.psmdcp" guidName

        let extensions =
            (nuspecName :: (files |> List.map (fun e -> e.Path)))
            |> List.map (fun p -> Path.GetExtension(p).TrimStart('.').ToLowerInvariant ())
            |> List.filter (fun e -> e <> "")
            |> List.distinct
            |> List.sortWith (fun a b -> String.CompareOrdinal (a, b))

        let generated =
            [ { RPath = "[Content_Types].xml"; Bytes = Encoding.UTF8.GetBytes (buildContentTypes extensions) }
              { RPath = "_rels/.rels"; Bytes = Encoding.UTF8.GetBytes (buildRels nuspecName guidName) }
              { RPath = psmdcpPath; Bytes = Encoding.UTF8.GetBytes (buildPsmdcp id version) } ]

        writeZip output (allContent @ generated) options

    /// Lists a zip: (path, size, crc32, dos time) in central-directory order -- for tests and
    /// for `Verify`-style comparison of two packages. `size` is the uncompressed length.
    /// Assumes no zip-file comment (this module never writes one), so the end-of-central-
    /// directory record is the file's last 22 bytes.
    let entries (zipPath: string) : (string * int64 * uint32 * DateTime) list =
        let bytes = File.ReadAllBytes zipPath
        let eocd = bytes.Length - 22
        let count = readU16 bytes (eocd + 10)
        let cdOffset = int (readU32 bytes (eocd + 16))

        let rec loop offset i acc =
            if i >= count then List.rev acc
            else
                let time = readU16 bytes (offset + 12)
                let date = readU16 bytes (offset + 14)
                let crc = readU32 bytes (offset + 16)
                let uncompSize = int64 (readU32 bytes (offset + 24))
                let nameLen = readU16 bytes (offset + 28)
                let extraLen = readU16 bytes (offset + 30)
                let commentLen = readU16 bytes (offset + 32)
                let name = Encoding.UTF8.GetString (bytes, offset + 46, nameLen)
                let entry = (name, uncompSize, crc, dosToDateTime date time)
                loop (offset + 46 + nameLen + extraLen + commentLen) (i + 1) (entry :: acc)

        loop cdOffset 0 []
