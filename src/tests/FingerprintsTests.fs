module ``Fingerprints feature``

open System
open System.IO
open NUnit.Framework
open Xake

let private newRoot () =
    let d = Path.Combine(Path.GetTempPath(), "xake-fp-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory d |> ignore
    d

let private write root (rel: string) (text: string) =
    let p = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName p) |> ignore
    File.WriteAllText(p, text)

let private run root (logged: Collections.Generic.List<string>) (body: Recipe<ExecContext, unit>) =
    do xake { ExecOptions.Default with
                ProjectRoot = root; Targets = ["main"]; ThrowOnError = true; FileLog = ""
                NoPersist = true; Nologo = true; ConLogLevel = Silent
                CustomLogger = CustomLogger ((=) Level.Warning) logged.Add } {
        phony "main" body
    }

/// A root with a generated target "desc/a.json" from inputs "a.fsproj" and "Directory.Build.props".
let private recorded () =
    let root = newRoot ()
    write root "a.fsproj" "<Project/>\n"
    write root "Directory.Build.props" "<Project>\n</Project>\n"
    write root "desc/a.json" "{ \"name\": \"a\" }\n"
    Fingerprints.recordIn root "xake.fingerprints" "desc/a.json" ["a.fsproj"; "Directory.Build.props"] |> ignore
    root

[<Test>]
let ``format sorts by target and round-trips``() =
    let l t : Fingerprints.Line = { Target = t; TargetHash = "th"; InputsHash = "ih"; Inputs = ["x/y.props"; "z.fsproj"] }
    let text = Fingerprints.format [l "locks/b/Release.json"; l "desc/a.json"; l "locks/a/Release.json"]
    Assert.AreEqual(
        "desc/a.json\tth\tih\tx/y.props\tz.fsproj\nlocks/a/Release.json\tth\tih\tx/y.props\tz.fsproj\nlocks/b/Release.json\tth\tih\tx/y.props\tz.fsproj\n",
        text)
    Assert.AreEqual(text, text |> Fingerprints.parse |> Fingerprints.format)
    Assert.AreEqual(text, text.Replace("\n", "\r\n") |> Fingerprints.parse |> Fingerprints.format)

[<Test>]
let ``CRLF and LF contents hash equal``() =
    let lf = Text.Encoding.UTF8.GetBytes "a\nb\n"
    let crlf = Text.Encoding.UTF8.GetBytes "a\r\nb\r\n"
    Assert.AreEqual(Fingerprints.hashBytes lf, Fingerprints.hashBytes crlf)
    Assert.AreNotEqual(Fingerprints.hashBytes lf, Fingerprints.hashBytes (Text.Encoding.UTF8.GetBytes "a\nc\n"))
    Assert.AreEqual(64, (Fingerprints.hashBytes lf).Length)

[<Test>]
let ``record writes one relative sorted line and only on change``() =
    let root = recorded ()
    let file = Path.Combine(root, "xake.fingerprints")
    let text = File.ReadAllText file
    let line = text |> Fingerprints.parse |> List.exactlyOne
    Assert.AreEqual("desc/a.json", line.Target)
    Assert.AreEqual(["Directory.Build.props"; "a.fsproj"], line.Inputs)
    Assert.IsTrue(text.EndsWith "\n" && not (text.Contains "\r"))

    let stamp = DateTime(2020, 1, 1)
    File.SetLastWriteTimeUtc(file, stamp)
    Assert.IsFalse(Fingerprints.recordIn root "xake.fingerprints" (Path.Combine(root, "desc", "a.json")) ["a.fsproj"; "Directory.Build.props"])
    Assert.AreEqual(stamp, File.GetLastWriteTimeUtc file)

    write root "desc/a.json" "{ \"name\": \"a2\" }\n"
    Assert.IsTrue(Fingerprints.recordIn root "xake.fingerprints" "desc/a.json" ["a.fsproj"; "Directory.Build.props"])
    write root "locks/a/Release.json" "{}\n"
    Fingerprints.recordIn root "xake.fingerprints" "locks/a/Release.json" ["desc/a.json"] |> ignore
    let targets = File.ReadAllText file |> Fingerprints.parse |> List.map (fun l -> l.Target)
    Assert.AreEqual(["desc/a.json"; "locks/a/Release.json"], targets)

[<Test>]
let ``record recipe writes relative to the project root``() =
    let root = newRoot ()
    write root "a.fsproj" "<Project/>\n"
    write root "desc/a.json" "{}\n"
    run root (Collections.Generic.List()) (Fingerprints.record "xake.fingerprints" "desc/a.json" ["a.fsproj"])
    let line = Fingerprints.load root "xake.fingerprints" |> List.exactlyOne
    Assert.AreEqual(Fingerprints.Fresh, Fingerprints.status root line)

[<Test>]
let ``fresh fingerprints give no warning and check passes without a database``() =
    let root = recorded ()
    let logged = Collections.Generic.List()
    run root logged (Fingerprints.warn "xake.fingerprints")
    run root logged (Fingerprints.check "xake.fingerprints")
    CollectionAssert.IsEmpty logged
    Assert.IsFalse(File.Exists(Path.Combine(root, ".xake")))

[<Test>]
let ``CRLF checkout of target and inputs is fresh``() =
    let root = recorded ()
    for f in ["a.fsproj"; "Directory.Build.props"; "desc/a.json"] do
        let p = Path.Combine(root, f)
        File.WriteAllText(p, File.ReadAllText(p).Replace("\n", "\r\n"))
    CollectionAssert.IsEmpty(Fingerprints.verify root "xake.fingerprints")

[<Test>]
let ``changed input warns once and leaves the target untouched``() =
    let root = recorded ()
    let target = Path.Combine(root, "desc/a.json")
    let before = File.ReadAllText target
    write root "Directory.Build.props" "<Project><PropertyGroup/></Project>\n"
    let logged = Collections.Generic.List()
    run root logged (Fingerprints.warn "xake.fingerprints")
    Assert.AreEqual(1, logged.Count)
    StringAssert.Contains("desc/a.json is stale", logged.[0])
    Assert.AreEqual(before, File.ReadAllText target)

[<Test>]
let ``hand edit and missing target are reported``() =
    let root = recorded ()
    write root "b.fsproj" "<Project/>\n"
    write root "desc/b.json" "{}\n"
    Fingerprints.recordIn root "xake.fingerprints" "desc/b.json" ["b.fsproj"] |> ignore
    write root "desc/a.json" "{ \"name\": \"edited\" }\n"
    File.Delete(Path.Combine(root, "desc/b.json"))
    let problems = Fingerprints.verify root "xake.fingerprints" |> List.map (fun (l, s) -> l.Target, s)
    Assert.AreEqual([ "desc/a.json", Fingerprints.HandEdited; "desc/b.json", Fingerprints.MissingTarget ], problems)

[<Test>]
let ``check fails naming the stale and hand-edited targets``() =
    let root = recorded ()
    write root "b.fsproj" "<Project/>\n"
    write root "desc/b.json" "{}\n"
    Fingerprints.recordIn root "xake.fingerprints" "desc/b.json" ["b.fsproj"] |> ignore
    write root "a.fsproj" "<Project Sdk=\"x\"/>\n"
    write root "desc/b.json" "{ }\n"
    let ex = Assert.Catch(fun () -> run root (Collections.Generic.List()) (Fingerprints.check "xake.fingerprints"))
    let msg = ex.ToString()
    StringAssert.Contains("desc/a.json is stale", msg)
    StringAssert.Contains("desc/b.json was edited by hand", msg)
