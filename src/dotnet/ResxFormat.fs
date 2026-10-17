namespace Xake.Dotnet

#nowarn "44" // AddBinaryFormattedResource stores a payload as is; msbuild calls it the same way

open System
open System.IO
open System.Text
open System.Xml
open System.Xml.Linq

/// <summary>
/// `.resx` to `.resources` the way the .NET SDK's msbuild does it (`GenerateResource` on .NET:
/// `MSBuildResXReader`, then `PreserializedResourceWriter` of System.Resources.Extensions),
/// byte for byte: strings, `ResXNullRef`, `ResXFileRef` (a text file as a string in the
/// named encoding, else UTF-8 with BOM detection; `System.Byte[]` and `System.IO.MemoryStream`
/// as bytes; any other type as an activator stream of the file), base64 `byte[]`, values
/// converted from a string by the type's converter (`System.Drawing.Size`, `System.Int32`, an
/// enum: stored as the string and the type name; a primitive type converted on the spot),
/// `bytearray.base64` values (a converter from bytes: bitmaps, icons) and `binary.base64`
/// values (a BinaryFormatter payload, stored as is). Aliases (`&lt;assembly alias=...&gt;`)
/// complete the type names as msbuild does; a repeated name keeps the first entry.
///
/// One writer for every file: msbuild writes a resx whose entries are all strings (or
/// primitives) with `ResourceWriter` when `GenerateResourceUsePreserializedResources` is off,
/// and that output is the same bytes `PreserializedResourceWriter` writes for those entries
/// (checked on 22 such files of ActiveReports); a resx with anything else only builds with
/// the property on, and then msbuild uses `PreserializedResourceWriter` too.
/// </summary>
module ResxFormat =

    /// One `data` entry as msbuild's reader classifies it.
    type Entry =
        /// a string (no type, `System.String`, or a text `ResXFileRef`)
        | Text of name: string * value: string
        /// `ResXNullRef`
        | Null of name: string
        /// `System.Byte[]`: base64 in the resx, or a binary `ResXFileRef`
        | Bytes of name: string * value: byte[]
        /// a `System.IO.MemoryStream` `ResXFileRef`
        | MemoryStream of name: string * value: byte[]
        /// a value its type converts from a string (`type` without `mimetype`)
        | ConvertedString of name: string * typeName: string * value: string
        /// `application/x-microsoft.net.object.bytearray.base64`: converted from bytes
        | ConvertedBytes of name: string * typeName: string * value: byte[]
        /// `application/x-microsoft.net.object.binary.base64` (and two older mime types)
        | BinaryFormatted of name: string * value: byte[]
        /// a `ResXFileRef` of another type: the file, handed to the type's constructor at run time
        | FileStream of name: string * typeName: string * path: string

    let entryName = function
        | Text (n, _) | Null n | Bytes (n, _) | MemoryStream (n, _) | ConvertedString (n, _, _)
        | ConvertedBytes (n, _, _) | BinaryFormatted (n, _) | FileStream (n, _, _) -> n

    let private stringTypeNamePrefix = "System.String, mscorlib,"
    let private stringTypeName40 = "System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"
    let private memoryStreamTypeNamePrefix = "System.IO.MemoryStream, mscorlib,"

    let private isString (t: string) = t.StartsWith (stringTypeNamePrefix, StringComparison.Ordinal)
    let private isByteArray (t: string) = t.IndexOf "System.Byte[]" <> -1 && t.IndexOf "mscorlib" <> -1
    let private isMemoryStream (t: string) = t.StartsWith (memoryStreamTypeNamePrefix, StringComparison.Ordinal)

    /// `GetFullTypeNameFromAlias`: the assembly part after `, ` replaced by the full name an
    /// `assembly` element gives that alias.
    let private fullTypeName (aliases: Collections.Generic.Dictionary<string, string>) (typeName: string) =
        match typeName with
        | null -> stringTypeName40
        | t ->
            let i = t.IndexOf ','
            let key = if i + 2 <= t.Length then t.Substring (i + 2) else ""
            match aliases.TryGetValue key with
            | true, full -> t.Substring (0, i + 2) + full
            | _ when t.Equals ("System.String", StringComparison.Ordinal) -> stringTypeName40
            | _ -> t

    /// The code-page encodings (`windows-1251`, ...) registered once, as msbuild registers
    /// `CodePagesEncodingProvider` at startup: .NET knows only the Unicode, ASCII and Latin-1
    /// encodings without it. Through reflection, so that this assembly needs no package for it
    /// (the provider is in the shared framework of .NET Core 3.0+; .NET Framework knows the
    /// code pages itself).
    let private codePages =
        lazy (
            match Type.GetType "System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages" with
            | null -> ()
            | t ->
                match t.GetProperty "Instance" with
                | null -> ()
                | p ->
                    match p.GetValue null with
                    | :? EncodingProvider as provider -> Encoding.RegisterProvider provider
                    | _ -> ())

    /// The encoding a `ResXFileRef` names (`Encoding.GetEncoding`, code pages included).
    let internal encodingOf (name: string) : Encoding =
        codePages.Force ()
        Encoding.GetEncoding name

    /// `ParseResxFileRefString`: (file, type, encoding option).
    let internal parseFileRef (value: string) : string * string * string option =
        let v = value.Trim ()
        let fileName, rest =
            if v.StartsWith "\"" then
                let last = v.LastIndexOf "\""
                if last - 1 < 0 || last + 2 > v.Length then failwithf "malformed ResXFileRef '%s'" value
                v.Substring (1, last - 1), v.Substring (last + 2)
            else
                match v.IndexOf ";" with
                | -1 -> failwithf "malformed ResXFileRef '%s'" value
                | i -> v.Substring (0, i), v.Substring (i + 1)
        match rest.Split ';' with
        | parts when parts.Length > 1 -> fileName, parts.[0], Some parts.[1]
        | parts -> fileName, parts.[0], None

    /// The entries of a `.resx`, in document order, a repeated name keeping the first. A
    /// `ResXFileRef` names a file relative to the resx's folder; it is read here.
    let read (resxFile: string) : Entry list =
        let aliases = Collections.Generic.Dictionary<string, string> ()
        let doc =
            use stream = new IO.FileStream (resxFile, FileMode.Open, FileAccess.Read, FileShare.Read)
            use reader = new XmlTextReader (stream)
            reader.WhitespaceHandling <- WhitespaceHandling.All
            XDocument.Load (reader, LoadOptions.PreserveWhitespace)
        let attr (e: XElement) (name: string) = match e.Attribute (XName.Get name) with null -> null | a -> a.Value
        let baseDir = Path.GetDirectoryName (Path.GetFullPath resxFile)
        let entries = ResizeArray<Entry> ()
        let seen = Collections.Generic.HashSet<string> ()
        let add (e: Entry) = if seen.Add (entryName e) then entries.Add e
        for elem in doc.Element(XName.Get "root").Elements () do
            match elem.Name.LocalName with
            | "assembly" ->
                let name = attr elem "name"
                let alias = match attr elem "alias" with null | "" -> Reflection.AssemblyName(name).Name | a -> a
                aliases.[alias] <- name
            | "data" ->
                let name = attr elem "name"
                let preserve =
                    match elem.Attribute (XName.Get ("space", "http://www.w3.org/XML/1998/namespace")) with
                    | null -> false
                    | a -> a.Value = "preserve"
                let value =
                    match elem.Element (XName.Get "value") with
                    | null ->
                        if elem.HasElements then failwithf "%s: entry '%s' has child elements but no 'value'" resxFile name
                        elem.Value
                    | v -> if not preserve && String.IsNullOrWhiteSpace v.Value then "" else v.Value
                let typeName = fullTypeName aliases (attr elem "type")
                let mimeType = attr elem "mimetype"
                if isString typeName && isNull mimeType then add (Text (name, value))
                elif typeName.StartsWith ("System.Resources.ResXFileRef", StringComparison.Ordinal) then
                    let file, refType, encoding = parseFileRef value
                    let path = Path.Combine (baseDir, file.Replace ('\\', '/'))
                    if isString refType then
                        let enc = match encoding with Some e -> encodingOf e | None -> (UTF8Encoding (false) :> Encoding)
                        use sr = new StreamReader (path, enc)
                        add (Text (name, sr.ReadToEnd ()))
                    elif isByteArray refType then add (Bytes (name, File.ReadAllBytes path))
                    elif isMemoryStream refType then add (MemoryStream (name, File.ReadAllBytes path))
                    else add (FileStream (name, refType, path))
                elif typeName.StartsWith ("System.Resources.ResXNullRef", StringComparison.Ordinal) then add (Null name)
                elif isNull mimeType then
                    if isByteArray typeName then add (Bytes (name, Convert.FromBase64String value))
                    else add (ConvertedString (name, typeName, value))
                else
                    match mimeType with
                    | "application/x-microsoft.net.object.bytearray.base64" ->
                        add (ConvertedBytes (name, typeName, Convert.FromBase64String value))
                    | "application/x-microsoft.net.object.binary.base64"
                    | "text/microsoft-urt/psuedoml-serialized/base64"
                    | "text/microsoft-urt/binary-serialized/base64" ->
                        add (BinaryFormatted (name, Convert.FromBase64String value))
                    | m -> failwithf "%s: entry '%s' has mimetype '%s', which the .NET SDK's GenerateResource does not support" resxFile name m
            | _ -> ()
        List.ofSeq entries

    /// The `.resources` bytes of `entries`, as `PreserializedResourceWriter` writes them.
    let generate (entries: Entry list) : byte[] =
        use stream = new IO.MemoryStream ()
        (
            use writer = new System.Resources.Extensions.PreserializedResourceWriter (stream)
            let opened = ResizeArray<IDisposable> ()
            for e in entries do
                match e with
                | Text (n, v) -> writer.AddResource (n, v)
                | Null n -> writer.AddResource (n, (null: obj))
                | Bytes (n, v) -> writer.AddResource (n, box v)
                | MemoryStream (n, v) -> writer.AddResource (n, box (new IO.MemoryStream (v)))
                | ConvertedString (n, t, v) -> writer.AddResource (n, v, t)
                | ConvertedBytes (n, t, v) -> writer.AddTypeConverterResource (n, v, t)
                | BinaryFormatted (n, v) -> writer.AddBinaryFormattedResource (n, v)
                | FileStream (n, t, path) ->
                    let s = new IO.FileStream (path, FileMode.Open, FileAccess.Read, FileShare.Read)
                    opened.Add s
                    writer.AddActivatorResource (n, s, t, true)
            writer.Generate ()
        )
        stream.ToArray ()

    /// The reader a `.resources` file names in its header (`System.Resources.ResourceReader`
    /// for the classic format; `System.Resources.Extensions.DeserializingResourceReader` for
    /// the preserialized one); `""` when the bytes are not a `.resources` file.
    let readerTypeOf (resources: byte[]) : string =
        try
            use reader = new BinaryReader (new IO.MemoryStream (resources))
            if resources.Length < 12 || reader.ReadInt32 () <> 0xBEEFCACE then ""
            else
                reader.ReadInt32 () |> ignore // the resource manager header version
                reader.ReadInt32 () |> ignore // the bytes to skip
                reader.ReadString ()
        with _ -> ""

    /// Whether a `.resources` file is in the preserialized format: `generate` writes it so when
    /// an entry is not a string (or another primitive `ResourceWriter` stores itself), and then
    /// reading it needs `System.Resources.Extensions` at run time -- part of .NET (5+)'s
    /// shared framework, a package (`System.Resources.Extensions.dll` beside the assembly) on
    /// .NET Framework.
    let needsExtensions (resources: byte[]) : bool =
        (readerTypeOf resources).StartsWith ("System.Resources.Extensions.", StringComparison.Ordinal)

    /// The `.resources` bytes of a `.resx` (`read`, then `generate`).
    let compileBytes (resxFile: string) : byte[] = generate (read resxFile)

    /// Writes the `.resources` of `resxFile` to `resourcesFile` when it is missing or its bytes
    /// differ, so an unchanged one keeps its timestamp; creates the folder. Whether it wrote.
    let refresh (resxFile: string) (resourcesFile: string) : bool =
        let bytes = compileBytes resxFile
        if File.Exists resourcesFile && File.ReadAllBytes resourcesFile = bytes then false
        else
            let dir = Path.GetDirectoryName resourcesFile
            if not (String.IsNullOrWhiteSpace dir) then Directory.CreateDirectory dir |> ignore
            File.WriteAllBytes (resourcesFile, bytes)
            true

    /// The files a `.resx` reads besides itself: its `ResXFileRef` targets (absolute), in order.
    let fileRefs (resxFile: string) : string list =
        try
            let doc = XDocument.Load resxFile
            let baseDir = Path.GetDirectoryName (Path.GetFullPath resxFile)
            [ for elem in doc.Root.Elements (XName.Get "data") do
                match elem.Attribute (XName.Get "type"), elem.Element (XName.Get "value") with
                | null, _ | _, null -> ()
                | t, v when t.Value.StartsWith ("System.Resources.ResXFileRef", StringComparison.Ordinal) ->
                    let file, _, _ = parseFileRef v.Value
                    yield Path.GetFullPath (Path.Combine (baseDir, file.Replace ('\\', '/')))
                | _ -> () ]
        with _ -> []
