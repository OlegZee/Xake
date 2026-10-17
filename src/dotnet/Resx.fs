namespace Xake.Dotnet

open System.IO
open System.Xml

/// Reads `.resx` string resources and compiles `.resx` files without `System.Windows.Forms`'s
/// `ResXResourceReader`, which is only available under the full framework: `compile` and
/// `refresh` follow the .NET SDK's `GenerateResource` (`ResxFormat`), every entry kind it
/// supports; `read` is the plain-strings view.
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

    /// Compiles a `.resx` to a `.resources` file the way the .NET SDK's `GenerateResource`
    /// does, byte for byte, typed values and `ResXFileRef` entries included (`ResxFormat`).
    /// Creates the output directory if needed.
    let compile (resxFile: string) (resourcesFile: string) =
        let dir = Path.GetDirectoryName resourcesFile
        if not (System.String.IsNullOrWhiteSpace dir) then Directory.CreateDirectory dir |> ignore
        File.WriteAllBytes (resourcesFile, ResxFormat.compileBytes resxFile)

    /// `compile`, but the `.resources` file is written only when it is missing or its bytes
    /// differ from what the resx compiles to, so an unchanged one keeps its timestamp.
    /// Returns whether it wrote.
    let refresh (resxFile: string) (resourcesFile: string) : bool =
        ResxFormat.refresh resxFile resourcesFile
