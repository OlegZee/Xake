namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Dotnet

/// `Description` (Xake.Dotnet): the project description (`xake.project.json`) -- parse and
/// validation, the writer's exact format, write-on-change, the recipe helpers and the schema.
[<TestFixture>]
type ``Project description``() =
    inherit XakeTestBase("description")

    let fixtures = __SOURCE_DIRECTORY__ </> "fixtures" </> "description"

    let minimal name =
        sprintf "{\n  \"name\": \"%s\",\n  \"project\": \"%s.fsproj\",\n  \"language\": \"fsharp\",\n  \"kind\": \"library\",\n  \"frameworks\": [\"net8.0\"],\n  \"sources\": [\"Lib.fs\"]\n}\n" name name

    let failure (text: string) =
        let e = Assert.Throws<exn>(fun () -> Description.parse text |> ignore)
        e.Message

    [<Test>]
    member x.``the hermetic example round-trips byte-identically``() =
        let text = File.ReadAllText (fixtures </> "xake.project.json")
        let d = Description.parse text
        Assert.That(Description.format d, Is.EqualTo text)
        Assert.That(d.Name, Is.EqualTo "Xake.Dotnet")
        Assert.That(d.Language, Is.EqualTo Description.FSharp)
        Assert.That(d.Frameworks, Is.EqualTo ["netstandard2.0"; "net462"])
        Assert.That(d.Sources.Length, Is.EqualTo 16)
        Assert.That(d.Options.Doc, Is.True)
        Assert.That(d.ByFramework |> List.map fst, Is.EqualTo ["net462"; "netstandard2.0"])
        Assert.That(d.Package.Value.Bundle, Is.EqualTo ["Xake"])

    [<Test>]
    member x.``every field of the schema round-trips byte-identically``() =
        let text = File.ReadAllText (fixtures </> "full.xake.project.json")
        let d = Description.parse text
        Assert.That(Description.format d, Is.EqualTo text)
        Assert.That(d.AssemblyName, Is.EqualTo (Some "Sample.Web.Api"))
        Assert.That(d.Kind, Is.EqualTo Description.Exe)
        Assert.That(d.FrameworkReferences, Is.EqualTo ["Microsoft.AspNetCore.App"])
        Assert.That(d.Resources.[1], Is.EqualTo ({ File = "data/logo.png"; Name = Some "Sample.Logo" } : Description.Resource))
        Assert.That(d.Options.NoWarn, Is.EqualTo ["CS1591"; "CS0618"])
        Assert.That(d.Options.Nullable, Is.EqualTo (Some "enable"))
        Assert.That(d.InternalsVisibleTo.[1].Key, Is.EqualTo (Some "0024000004800000"))
        Assert.That(d.AssemblyAttributes.[1].Args, Is.EqualTo ["\"Channel\""; "\"preview\""])
        let windows = d.ByFramework |> List.find (fst >> (=) "net8.0-windows") |> snd
        Assert.That(windows.FrameworkReferences, Is.EqualTo ["Microsoft.WindowsDesktop.App"])
        let ns = d.ByFramework |> List.find (fst >> (=) "netstandard2.0") |> snd
        Assert.That(ns.Resources, Is.EqualTo ([ { File = "legacy/strings.txt"; Name = None } ] : Description.Resource list))
        Assert.That(ns.Packages.[0].Version, Is.EqualTo (Some "8.0.5"))

    [<Test>]
    member x.``defaults are filled and empty fields are not written``() =
        let text = minimal "Lib"
        let d = Description.parse (text.Replace ("  \"kind\": \"library\",\n", ""))
        Assert.That(d.Kind, Is.EqualTo Description.Library)
        Assert.That(d.Options, Is.EqualTo Description.Options.Default)
        Assert.That(Description.format d, Is.EqualTo text)

    [<Test>]
    member x.``a missing required field is named``() =
        Assert.That(failure ((minimal "Lib").Replace ("  \"project\": \"Lib.fsproj\",\n", "")), Does.StartWith "'project': required")

    [<Test>]
    member x.``a nested invalid field is named with its path``() =
        let text = (minimal "Lib").Replace ("\"sources\"", "\"packages\": [{ \"version\": \"1.0\" }],\n  \"sources\"")
        Assert.That(failure text, Does.StartWith "'packages[0].id': required")
        let text = (minimal "Lib").Replace ("\"sources\"", "\"options\": { \"doc\": \"yes\" },\n  \"sources\"")
        Assert.That(failure text, Does.StartWith "'options.doc': expected true or false")
        let text = (minimal "Lib").Replace ("\"sources\"", "\"options\": { \"nullable\": \"on\" },\n  \"sources\"")
        Assert.That(failure text, Does.StartWith "'options.nullable': 'on' is not one of")

    [<Test>]
    member x.``an unknown field, a bad enum value and an unknown framework key are named``() =
        Assert.That(failure ((minimal "Lib").Replace ("\"kind\"", "\"knid\"")), Does.StartWith "'knid': unknown field")
        Assert.That(failure ((minimal "Lib").Replace ("\"fsharp\"", "\"vb\"")), Does.StartWith "'language': 'vb' is not one of")
        let text = (minimal "Lib").Replace ("\"sources\"", "\"byFramework\": { \"net462\": { \"defines\": [\"X\"] } },\n  \"sources\"")
        Assert.That(failure text, Does.StartWith "'byFramework.net462': not one of the frameworks")
        Assert.That(failure ((minimal "Lib").Replace ("[\"net8.0\"]", "[]")), Does.StartWith "'frameworks': needs at least one")
        Assert.That(failure ((minimal "Lib").Replace ("\"kind\"", "\"name\"")), Does.StartWith "'name': repeated field")
        Assert.That(failure "{ \"name\": ", Does.StartWith "malformed json")

    [<Test>]
    member x.``an unknown shared framework is named``() =
        let text = (minimal "Lib").Replace ("\"sources\"", "\"frameworkReferences\": [\"Microsoft.AspNetCore.App\", \"Microsoft.Maui\"],\n  \"sources\"")
        Assert.That(failure text, Does.StartWith "'frameworkReferences[1]': 'Microsoft.Maui' is not one of Microsoft.NETCore.App")
        let text = (minimal "Lib").Replace ("\"sources\"", "\"byFramework\": { \"net8.0\": { \"frameworkReferences\": [\"Web\"] } },\n  \"sources\"")
        Assert.That(failure text, Does.StartWith "'byFramework.net8.0.frameworkReferences[0]': 'Web' is not one of")
        // the name is kept in its canonical spelling
        let text = (minimal "Lib").Replace ("\"sources\"", "\"frameworkReferences\": [\"microsoft.aspnetcore.app\"],\n  \"sources\"")
        Assert.That((Description.parse text).FrameworkReferences, Is.EqualTo ["Microsoft.AspNetCore.App"])

    [<Test>]
    member x.``read prefixes a failure with the file``() =
        let path = x.TestOptions.ProjectRoot </> "broken.json"
        File.WriteAllText (path, "{ \"name\": \"Lib\" }")
        let e = Assert.Throws<exn>(fun () -> Description.read path |> ignore)
        Assert.That(e.Message, Does.StartWith (path + ": 'project': required"))

    [<Test>]
    member x.``write leaves an unchanged file untouched``() =
        let path = x.TestOptions.ProjectRoot </> "unchanged" </> Description.FileName
        let d = Description.parse (File.ReadAllText (fixtures </> "full.xake.project.json"))
        Assert.That(Description.write path d, Is.True, "first write creates the file")
        let stamp = System.DateTime (2001, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)
        File.SetLastWriteTimeUtc (path, stamp)
        Assert.That(Description.write path d, Is.False)
        Assert.That(File.GetLastWriteTimeUtc path, Is.EqualTo stamp)
        Assert.That(Description.write path { d with Sources = d.Sources @ ["New.cs"] }, Is.True)
        Assert.That(File.GetLastWriteTimeUtc path, Is.Not.EqualTo stamp)

    [<Test>]
    member x.``save and load go through the project root, load records the file``() =
        let loaded = ref None
        let depends = ref []
        do xake {x.TestOptions with ThrowOnError = true} {
            wantOverride ["roundtrip"]
            rules [
                "roundtrip" => recipe {
                    do! Description.save ("saved" </> Description.FileName) (Description.parse (minimal "Saved"))
                    let! d = Description.load ("saved" </> Description.FileName)
                    let! result = getResult ()
                    loaded.Value <- Some d
                    depends.Value <- result.Depends
                }
            ]
        }
        Assert.That(loaded.Value.Value.Name, Is.EqualTo "Saved")
        let files = depends.Value |> List.choose (function FileDep (f, _) -> Some f.FullName | _ -> None)
        Assert.That(files, Is.EqualTo [ Path.GetFullPath (x.TestOptions.ProjectRoot </> "saved" </> Description.FileName) ])

    [<Test>]
    member x.``find records exactly one input, the matching file``() =
        let root = x.TestOptions.ProjectRoot
        for name in ["A"; "B"; "C"] do
            Directory.CreateDirectory (root </> "find" </> name) |> ignore
            File.WriteAllText (root </> "find" </> name </> Description.FileName, minimal name)
        let found = ref None
        let depends = ref []
        do xake {x.TestOptions with ThrowOnError = true} {
            wantOverride ["find-b"]
            rules [
                "find-b" => recipe {
                    let! d = Description.find (!! "find/*/xake.project.json") "B"
                    let! result = getResult ()
                    found.Value <- Some d
                    depends.Value <- result.Depends
                }
            ]
        }
        Assert.That(found.Value.Value.Name, Is.EqualTo "B")
        let inputs =
            depends.Value |> List.choose (function
                | FileDep (f, _) -> Some f.FullName
                | GetFiles _ -> Some "GetFiles"
                | _ -> None)
        Assert.That(inputs, Is.EqualTo [ Path.GetFullPath (root </> "find" </> "B" </> Description.FileName) ])

    [<Test>]
    member x.``beside and names``() =
        Assert.That(Description.beside ("src" </> "core" </> "Xake.fsproj"), Is.EqualTo ("src" </> "core" </> "xake.project.json"))
        Assert.That(Description.beside "Lib.fsproj", Is.EqualTo "xake.project.json")
        let ds = ["A"; "B"] |> List.map (minimal >> Description.parse)
        Assert.That(Description.names ds, Is.EqualTo ["A"; "B"])

    [<Test>]
    member x.``the committed schema lists the fields the reader knows, in the writer's order``() =
        let schema = Json.parse (File.ReadAllText (__SOURCE_DIRECTORY__ </> ".." </> ".." </> "docs" </> "schemas" </> "xake.project.schema.json"))
        let rec get (path: string list) value =
            match path with
            | [] -> value
            | p :: rest ->
                match System.Int32.TryParse p, value with
                | (true, i), Json.JArray items -> get rest items.[i]
                | _ -> get rest (Json.field p value |> Option.get)
        let names path = match get path schema with Json.JObject ms -> List.map fst ms | _ -> []
        let check kind path = Assert.That(names path, Is.EqualTo (Description.fields.[kind]), kind)
        check "project" ["properties"]
        for kind in ["package"; "items"; "resource"; "options"; "attribute"; "metadata"; "repository"] do
            check kind ["$defs"; kind; "properties"]
        check "friend" ["$defs"; "friend"; "oneOf"; "1"; "properties"]
        let enum = get ["$defs"; "items"; "properties"; "frameworkReferences"; "items"; "enum"] schema
        Assert.That(enum |> Json.asArray |> List.choose Json.asString, Is.EqualTo DotNetFwk.sharedFrameworks)
