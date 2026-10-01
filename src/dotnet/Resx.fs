namespace Xake.Dotnet

open System.IO
open System.Xml

/// Reads and compiles `.resx` string resources without `System.Windows.Forms`'s
/// `ResXResourceReader`, which is only available under the full framework.
/// `System.Resources.ResourceWriter` is available on netstandard2.0 already (it is used
/// elsewhere with no `#if`), so only the reading side needed a replacement.
module Resx =

    /// Parses a `.resx` file: every `/root/data` element, in document order, as
    /// (name, value). `xml:space="preserve"` values keep their whitespace exactly, since the
    /// `value` element's text content already carries it verbatim -- there is nothing extra to
    /// do to honor it. `resheader`, `metadata`, `assembly` and comments are ignored.
    ///
    /// Fails with a message naming the file and the entry when a `data` element carries a
    /// `type` or `mimetype` attribute, or has no `value` child: that is a typed value or a
    /// `ResXFileRef`, neither of which is supported yet -- only plain string entries are.
    let read (path: string) : (string * string) list =
        let doc = XmlDocument ()
        doc.Load path
        [ for node in doc.DocumentElement.ChildNodes do
            match node with
            | :? XmlElement as el when el.Name = "data" ->
                let name = el.GetAttribute "name"
                if el.GetAttribute "type" <> "" then
                    failwithf "%s: entry '%s' has a 'type' attribute -- typed values are not supported yet, only plain strings are" path name
                elif el.GetAttribute "mimetype" <> "" then
                    failwithf "%s: entry '%s' has a 'mimetype' attribute -- ResXFileRef/binary values are not supported yet, only plain strings are" path name
                else
                    match el.SelectSingleNode "value" with
                    | null -> failwithf "%s: entry '%s' has no 'value' element -- ResXFileRef/typed values are not supported yet, only plain strings are" path name
                    | valueNode -> yield name, valueNode.InnerText
            | _ -> () ]

    /// Compiles a `.resx` to a `.resources` file: entries added to a
    /// `System.Resources.ResourceWriter` in document order, the same format
    /// `GenerateResource`/`ResourceWriter` produces for a resx with only string entries, so the
    /// output is byte-identical to what msbuild wrote. Creates the output directory if needed.
    let compile (resxFile: string) (resourcesFile: string) =
        let dir = Path.GetDirectoryName resourcesFile
        if not (System.String.IsNullOrWhiteSpace dir) then Directory.CreateDirectory dir |> ignore
        use writer = new System.Resources.ResourceWriter (resourcesFile)
        for (name, value) in read resxFile do
            writer.AddResource (name, value)
        writer.Generate ()
