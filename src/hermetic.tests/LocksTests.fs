namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Hermetic.Dotnet

/// `Locks.generateAll`: the locks of described projects, generated without compiling. The
/// fixtures `fixtures/src/{core,dotnet,hermetic}` describe Xake's three projects (placed as
/// in `DescribedTests`: a copy whose `project` points at the real project file).
[<TestFixture>]
type ``Lock generation``() =
    inherit XakeTestBase("locks")

    let fixtures = __SOURCE_DIRECTORY__ </> "fixtures" </> "src"
    let srcRoot = Path.GetFullPath (__SOURCE_DIRECTORY__ </> "..")
    let lockOf name cfg = sprintf "locks/%s/%s.json" name cfg
    let outOf cfg fwk (_: string) = sprintf "out/%s/%s" cfg fwk
    let roots () = Roots.builtin (Directory.GetCurrentDirectory ())

    let place (dir: string) =
        let d = Description.read (fixtures </> dir </> Description.FileName)
        let target = Path.GetFullPath ("desc" </> dir)
        Directory.CreateDirectory target |> ignore
        let real = srcRoot </> dir </> d.Project
        Description.write (target </> Description.FileName) { d with Project = Path.GetRelativePath (target, real) } |> ignore
        "desc/" + dir + "/" + Description.FileName

    /// A small F# library description `name` under `tiny/<name>/`, one source file.
    let tiny (name: string) (frameworks: string list) (refs: string list) (packages: Description.Package list) =
        let dir = Path.GetFullPath ("tiny" </> name)
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText (dir </> "Lib.fs", sprintf "module %s.Lib\nlet answer = 42\n" name)
        let d : Description.Project =
            { Schema = None; Name = name; Project = name + ".fsproj"; AssemblyName = None; RootNamespace = None
              Language = Description.FSharp; Kind = Description.Library; Frameworks = frameworks; Sources = [ "Lib.fs" ]
              Resources = []; Defines = []; References = []; ProjectRefs = refs; Packages = packages
              Options = Description.Options.Default; InternalsVisibleTo = []; ByFramework = []; Package = None }
        Description.write (dir </> Description.FileName) d |> ignore
        "tiny/" + name + "/" + Description.FileName

    member private x.Run (label: string) (vars: (string * string) list) (body: Recipe<ExecContext, unit>) =
        xake {x.TestOptions with FileLog = label + ".log"; ThrowOnError = true; Vars = ("CI", "off") :: vars; FileLogLevel = Loud} {
            wantOverride ([label])
            rules [ label => body ]
        }

    [<Test>]
    member x.``the nearest framework``() =
        Assert.That(Locks.nearestFramework "net462" [ "net462"; "netstandard2.0" ], Is.EqualTo (Some "net462"))
        Assert.That(Locks.nearestFramework "net472" [ "net462"; "netstandard2.0" ], Is.EqualTo (Some "net462"))
        Assert.That(Locks.nearestFramework "net462" [ "netstandard2.0"; "netstandard1.6" ], Is.EqualTo (Some "netstandard2.0"))
        Assert.That(Locks.nearestFramework "net8.0" [ "net6.0"; "netstandard2.1" ], Is.EqualTo (Some "net6.0"))
        Assert.That(Locks.nearestFramework "net8.0" [ "net462"; "netstandard2.1"; "netstandard2.0" ], Is.EqualTo (Some "netstandard2.1"))
        Assert.That(Locks.nearestFramework "netstandard2.0" [ "netstandard2.1"; "net462" ], Is.EqualTo None)
        Assert.That(Locks.nearestFramework "net45" [ "netstandard2.0" ], Is.EqualTo None)

    [<Test>]
    member x.``a referenced project's configuration: the same, else Release, else Debug``() =
        Assert.That(Locks.refConfig "Debug" (fun c -> c = "Release"), Is.EqualTo (Some "Release"))
        Assert.That(Locks.refConfig "Debug" (fun _ -> true), Is.EqualTo (Some "Debug"))
        Assert.That(Locks.refConfig "Release" (fun c -> c = "Debug"), Is.EqualTo (Some "Debug"))
        Assert.That(Locks.refConfig "Release" (fun _ -> false), Is.EqualTo None)

    [<Test>]
    member x.``a version the lock holds is kept unless LOCK_REFRESH names it``() =
        let declared : Description.Package list =
            [ { Id = "FSharp.Core"; Version = Some "8.0.*"; Private = false }; { Id = "New"; Version = Some "1.0"; Private = false } ]
        let kept : Lock.Package list = [ { Id = "fsharp.core"; Version = "8.0.100"; Sha512 = ""; Direct = true; DependsOn = [] } ]
        let versions refresh = Locks.keptVersions refresh kept declared |> List.map (fun p -> p.Version.Value)
        Assert.That(versions None, Is.EqualTo [ "[8.0.100]"; "1.0" ])
        Assert.That(versions (Some "Other"), Is.EqualTo [ "[8.0.100]"; "1.0" ])
        Assert.That(versions (Some "Other, FSharp.Core"), Is.EqualTo [ "8.0.*"; "1.0" ])
        Assert.That(versions (Some "*"), Is.EqualTo [ "8.0.*"; "1.0" ])

    [<Test>]
    member x.``referenced projects come first``() =
        let d name refs : string * Description.Project =
            name, { Description.parse (sprintf """{ "name": "%s", "project": "%s.fsproj", "language": "fsharp", "kind": "library", "frameworks": ["netstandard2.0"], "sources": ["a.fs"] }""" name name)
                    with ProjectRefs = refs }
        let order = Locks.ordered [ d "A" [ "B"; "C" ]; d "B" [ "C"; "Outside" ]; d "C" [] ] |> List.map fst
        Assert.That(order, Is.EqualTo [ "C"; "B"; "A" ])
        let e = Assert.Throws<exn>(fun () -> Locks.ordered [ d "A" [ "B" ]; d "B" [ "A" ] ] |> ignore)
        Assert.That(e.Message, Does.Contain "A -> B -> A")

    [<Test>]
    member x.``the hermetic fixture describes src/hermetic``() =
        let path = fixtures </> "hermetic" </> Description.FileName
        let text = File.ReadAllText path
        let d = Description.parse text
        Assert.That(Description.format d, Is.EqualTo text, "the fixture is not in the writer's format")
        let fsproj = File.ReadAllText (srcRoot </> "hermetic" </> d.Project)
        let compiled =
            System.Text.RegularExpressions.Regex.Matches (fsproj, "<Compile Include=\"([^\"]+)\"")
            |> Seq.map (fun m -> m.Groups.[1].Value) |> List.ofSeq
        Assert.That(d.Sources, Is.EqualTo compiled)

    /// The acceptance of the PR: Xake's three projects get Release locks the 0.2 reader takes
    /// unchanged; a second generation writes nothing; the project reference is the layout
    /// path, unhashed even when an output is on disk; `Options` has no `-o`.
    [<Test; Category("Integration")>]
    member x.``Xake's three projects get Release locks, regenerated byte-identically``() =
        for dir in [ "core"; "dotnet"; "hermetic" ] do place dir |> ignore
        if Directory.Exists "locks" then Directory.Delete ("locks", true)
        let generate label = x.Run label [] (Locks.generateAll (!! "desc/*/xake.project.json") [ "Release" ] lockOf outOf)
        generate "generate-1"

        let locks = [ "Xake"; "Xake.Dotnet"; "Xake.Hermetic.Dotnet" ] |> List.map (fun n -> lockOf n "Release")
        let texts = locks |> List.map File.ReadAllText
        for path, text in List.zip locks texts do
            let doc = Lock.parse (roots ()) text
            Assert.That(Lock.format (roots ()) doc, Is.EqualTo text, path + ": the reader does not take it unchanged")
            Assert.That(doc.Configuration, Is.EqualTo "Release")
            Assert.That(doc.Entries |> List.map (fun e -> e.Framework), Is.EqualTo [ "net462"; "netstandard2.0" ], path)
            for e in doc.Entries do
                Assert.That(e.Compilation.Output, Is.EqualTo None, path + ": an output in Options")
                Assert.That(e.Compilation.Options |> List.exists (fun o -> o.StartsWith "-o:" || o.StartsWith "--out:"), Is.False)
                Assert.That(e.Compilation.Options, Does.Contain "--optimize+")
            Assert.That(text, Does.Contain "\"--pathmap:$(ProjectRoot)/=/_/\"")
            Assert.That(text, Does.Not.Contain (Directory.GetCurrentDirectory ()), path + ": a machine path")

        let dotnet = Lock.parse (roots ()) texts.[1]
        for e in dotnet.Entries do
            let xake = e.Dependencies.References |> List.filter (fun r -> r.Path.EndsWith "/Xake.dll")
            Assert.That(xake |> List.map (fun r -> r.Sha256), Is.EqualTo [ "" ], "the project reference is unhashed")
            Assert.That(texts.[1], Does.Contain (sprintf "\"$(ProjectRoot)/out/Release/%s/Xake.dll\"" e.Framework))
            Assert.That(e.Evaluation.ProjectRefs |> List.map Path.GetFileName, Is.EqualTo [ "Xake.fsproj" ])
            Assert.That(e.Packages |> List.exists (fun p -> p.Id = "FSharp.Core" && p.Version = "8.0.100" && p.Direct), Is.True)
        let hermetic = Lock.parse (roots ()) texts.[2]
        Assert.That(hermetic.Entries.Head.Packages |> List.exists (fun p -> p.Id = "Xake" && p.Direct), Is.True)

        // an output left on disk by a build changes nothing; nothing is rewritten
        for fwk in [ "net462"; "netstandard2.0" ] do
            Directory.CreateDirectory ("out/Release/" + fwk) |> ignore
            File.WriteAllText (sprintf "out/Release/%s/Xake.dll" fwk, "not an assembly")
        let stamps = locks |> List.map File.GetLastWriteTimeUtc
        System.Threading.Thread.Sleep 1100
        generate "generate-2"
        Assert.That(locks |> List.map File.ReadAllText, Is.EqualTo texts)
        Assert.That(locks |> List.map File.GetLastWriteTimeUtc, Is.EqualTo stamps, "a lock was rewritten")

    /// A Debug project over a Release-only library gets the library's Release output; a
    /// net462 project over a netstandard2.0-only library gets netstandard2.0.
    [<Test; Category("Integration")>]
    member x.``a Debug project over a Release-only library on another framework``() =
        let lib = tiny "Lib" [ "netstandard2.0" ] [] []
        let app = tiny "App" [ "net462" ] [ "Lib" ] []
        for n in [ "Lib"; "App" ] do if Directory.Exists ("locks/" + n) then Directory.Delete ("locks/" + n, true)

        let e = Assert.Throws<XakeException>(fun () -> x.Run "app-without-lib" [] (Locks.generateAll (!! app) [ "Debug" ] lockOf outOf))
        Assert.That(e.Data0, Does.Contain "generate the locks of 'Lib' first")

        x.Run "lib-release" [] (Locks.generateAll (!! lib) [ "Release" ] lockOf outOf)
        x.Run "app-debug" [] (Locks.generateAll (!! app) [ "Debug" ] lockOf outOf)
        let text = File.ReadAllText (lockOf "App" "Debug")
        let doc = Lock.parse (roots ()) text
        let entry = doc.Entries |> List.exactlyOne
        Assert.That(entry.Framework, Is.EqualTo "net462")
        Assert.That(entry.Compilation.Options, Does.Contain "--optimize-")
        Assert.That(text, Does.Contain "{ \"Path\": \"$(ProjectRoot)/out/Release/netstandard2.0/Lib.dll\", \"Sha256\": \"\" }")
        Assert.That(entry.Evaluation.ProjectRefs, Is.EqualTo [ "Lib" ])

    /// A floating version stays at what the lock holds until LOCK_REFRESH names the package.
    [<Test; Category("Integration")>]
    member x.``a floating version stays until LOCK_REFRESH names it``() =
        let pinned : Description.Package = { Id = "FSharp.Core"; Version = Some "8.0.100"; Private = false }
        let path = tiny "Floating" [ "netstandard2.0" ] [] [ pinned ]
        if Directory.Exists "locks/Floating" then Directory.Delete ("locks/Floating", true)
        let lock = lockOf "Floating" "Release"
        let fsharpCore () =
            (Lock.read (roots ()) lock).Entries.Head.Packages |> List.find (fun p -> p.Id = "FSharp.Core") |> fun p -> p.Version
        let generate label vars = x.Run label vars (Locks.generateAll (!! path) [ "Release" ] lockOf outOf)
        generate "floating-1" []
        Assert.That(fsharpCore (), Is.EqualTo "8.0.100")

        // the description floats now; the lock keeps its version and is not rewritten
        let d = Description.read path
        Description.write path { d with Packages = [ { pinned with Version = Some "8.0.*" } ] } |> ignore
        let stamp = File.GetLastWriteTimeUtc lock
        System.Threading.Thread.Sleep 1100
        generate "floating-2" []
        generate "floating-3" [ "LOCK_REFRESH", "Other.Package" ]
        Assert.That(fsharpCore (), Is.EqualTo "8.0.100")
        Assert.That(File.GetLastWriteTimeUtc lock, Is.EqualTo stamp)

        generate "floating-4" [ "LOCK_REFRESH", "FSharp.Core" ]
        Assert.That(fsharpCore (), Does.StartWith "8.0.")
        Assert.That(fsharpCore (), Is.Not.EqualTo "8.0.100", "the refreshed version floats to the latest 8.0.x")
