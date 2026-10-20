namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// `Description`: the project description (`xake.project.json`) -- parse and validation, the
/// writer's exact format, write-on-change, and the recipe helpers.
[<TestFixture>]
type ``Project description``() =
    inherit XakeTestBase("hermetic-description")   // not "description": src/tests' fixture of that name shares ~testout~

    let fixture = __SOURCE_DIRECTORY__ </> "fixtures" </> "xake.project.json"

    let minimal name =
        sprintf "{\n  \"name\": \"%s\",\n  \"project\": \"%s.fsproj\",\n  \"language\": \"fsharp\",\n  \"kind\": \"library\",\n  \"frameworks\": [\"net8.0\"],\n  \"sources\": [\"Lib.fs\"]\n}\n" name name

    let failure (text: string) =
        let e = Assert.Throws<exn>(fun () -> Description.parse text |> ignore)
        e.Message

    [<Test>]
    member x.``the example round-trips byte-identically``() =
        let text = File.ReadAllText fixture
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

    [<Test>]
    member x.``an unknown field, a bad enum value and an unknown framework key are named``() =
        Assert.That(failure ((minimal "Lib").Replace ("\"kind\"", "\"knid\"")), Does.StartWith "'knid': unknown field")
        Assert.That(failure ((minimal "Lib").Replace ("\"fsharp\"", "\"vb\"")), Does.StartWith "'language': 'vb' is not one of")
        let text = (minimal "Lib").Replace ("\"sources\"", "\"byFramework\": { \"net462\": { \"defines\": [\"X\"] } },\n  \"sources\"")
        Assert.That(failure text, Does.StartWith "'byFramework.net462': not one of the frameworks")
        Assert.That(failure ((minimal "Lib").Replace ("[\"net8.0\"]", "[]")), Does.StartWith "'frameworks': needs at least one")

    [<Test>]
    member x.``read prefixes a failure with the file``() =
        let path = x.TestOptions.ProjectRoot </> "broken.json"
        File.WriteAllText (path, "{ \"name\": \"Lib\" }")
        let e = Assert.Throws<exn>(fun () -> Description.read path |> ignore)
        Assert.That(e.Message, Does.StartWith (path + ": 'project': required"))

    [<Test>]
    member x.``write leaves an unchanged file untouched``() =
        let path = x.TestOptions.ProjectRoot </> "unchanged" </> Description.FileName
        let d = Description.parse (File.ReadAllText fixture)
        Assert.That(Description.write path d, Is.True, "first write creates the file")
        let stamp = System.DateTime (2001, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)
        File.SetLastWriteTimeUtc (path, stamp)
        Assert.That(Description.write path d, Is.False)
        Assert.That(File.GetLastWriteTimeUtc path, Is.EqualTo stamp)
        Assert.That(Description.write path { d with Sources = d.Sources @ ["New.fs"] }, Is.True)
        Assert.That(File.GetLastWriteTimeUtc path, Is.Not.EqualTo stamp)

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
                | ArtifactDep (FileTarget f) -> Some f.FullName
                | ArtifactDep2 (FileTarget f, _) -> Some f.FullName
                | GetFiles _ -> Some "GetFiles"
                | _ -> None)
        Assert.That(inputs, Is.EqualTo [ Path.GetFullPath (root </> "find" </> "B" </> Description.FileName) ])

    [<Test>]
    member x.``find fails naming the missing project``() =
        let e = Assert.Catch(fun () ->
            do xake {x.TestOptions with ThrowOnError = true} {
                wantOverride ["find-none"]
                rules [ "find-none" => recipe { let! _ = Description.find (!! "nowhere/*/xake.project.json") "Nope" in () } ]
            })
        Assert.That(e.ToString(), Does.Contain "no description named 'Nope'")

    [<Test>]
    member x.``beside and names``() =
        Assert.That(Description.beside ("src" </> "core" </> "Xake.fsproj"), Is.EqualTo ("src" </> "core" </> "xake.project.json"))
        Assert.That(Description.beside "Lib.fsproj", Is.EqualTo "xake.project.json")
        let ds = ["A"; "B"] |> List.map (minimal >> Description.parse)
        Assert.That(Description.names ds, Is.EqualTo ["A"; "B"])

    [<Test>]
    member x.``the description the import writes uses the core schema's fields, in its order``() =
        // the schema is the core's (docs/schemas/, Xake.Dotnet.Description since Xake 3.9); its
        // own test checks it against the reader. Here: what this package writes stays inside it
        let schema = ToolJson.parse (File.ReadAllText (__SOURCE_DIRECTORY__ </> ".." </> ".." </> "docs" </> "schemas" </> "xake.project.schema.json"))
        let keys value = match value with ToolJson.JObject ms -> List.map fst ms | _ -> []
        let properties = ToolJson.field "properties" schema |> Option.get |> keys
        let written = Description.read fixture |> Description.format |> ToolJson.parse |> keys
        Assert.That(written, Is.Not.Empty)
        Assert.That(properties, Is.SupersetOf written)
        Assert.That(properties |> List.filter (fun p -> List.contains p written), Is.EqualTo written)
