namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// `Locks.generate`: the lock of one described project, generated without compiling, driven
/// by the script's phony lock rule (`lock::<name>::<cfg>`, docs/hermetic/lock.md) and its
/// `update-locks` target. The fixtures `fixtures/src/{core,dotnet,hermetic}` describe Xake's
/// three projects (placed as in `DescribedTests`: a copy whose `project` points at the real
/// project file).
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
              Resources = []; Defines = []; References = []; FrameworkReferences = []; ProjectRefs = refs; Packages = packages
              Options = Description.Options.Default; InternalsVisibleTo = []; AssemblyAttributes = []; ByFramework = []; Package = None }
        Description.write (dir </> Description.FileName) d |> ignore
        "tiny/" + name + "/" + Description.FileName

    let lockRule descriptionOf refCfg ran = LockRule.lockRule lockOf outOf descriptionOf refCfg ran
    let updateLocks projects = LockRule.updateLocks [ "Release" ] projects
    let tinyOf name = "tiny/" + name + "/" + Description.FileName
    let clean (names: string list) = for n in names do if Directory.Exists ("locks/" + n) then Directory.Delete ("locks/" + n, true)

    member private x.Run (label: string) (vars: (string * string) list) (targets: string list) (rs: Rule<ExecContext> list) =
        xake {x.TestOptions with FileLog = label + ".log"; ThrowOnError = true; Vars = ("CI", "off") :: vars; FileLogLevel = Loud} {
            wantOverride targets
            rules rs
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

    /// The acceptance of the PR: `update-locks` gives Xake's three projects Release locks the
    /// 0.2 reader takes unchanged; a second run writes nothing; the project reference is the
    /// layout path, unhashed even when an output is on disk; `Options` has no `-o`.
    [<Test; Category("Integration")>]
    member x.``Xake's three projects get Release locks, regenerated byte-identically``() =
        let described = [ "core"; "dotnet"; "hermetic" ] |> List.map (fun dir -> let p = place dir in (Description.read p).Name, p) |> Map.ofList
        let names = [ "Xake"; "Xake.Dotnet"; "Xake.Hermetic.Dotnet" ]
        if Directory.Exists "locks" then Directory.Delete ("locks", true)
        let ran = ResizeArray ()
        let script = [ lockRule (fun n -> described.[n]) id ran; updateLocks names ]
        x.Run "generate-1" [] [ "update-locks" ] script
        Assert.That(List.ofSeq ran |> List.sort, Is.EqualTo (names |> List.map (fun n -> n + "::Release") |> List.sort), "each lock once")
        Assert.That(ran.[0], Is.EqualTo "Xake::Release", "the referenced project first")

        let locks = names |> List.map (fun n -> lockOf n "Release")
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
            Assert.That(e.Evaluation.ProjectRefs, Is.EqualTo [ "Xake" ])
            Assert.That(e.Packages |> List.exists (fun p -> p.Id = "FSharp.Core" && p.Version = "8.0.100" && p.Direct), Is.True)
        let hermetic = Lock.parse (roots ()) texts.[2]
        Assert.That(hermetic.Entries.Head.Packages |> List.exists (fun p -> p.Id = "Xake" && p.Direct), Is.True)

        // an output left on disk by a build changes nothing; nothing is rewritten
        for fwk in [ "net462"; "netstandard2.0" ] do
            Directory.CreateDirectory ("out/Release/" + fwk) |> ignore
            File.WriteAllText (sprintf "out/Release/%s/Xake.dll" fwk, "not an assembly")
        let stamps = locks |> List.map File.GetLastWriteTimeUtc
        System.Threading.Thread.Sleep 1100
        x.Run "generate-2" [] [ "update-locks" ] script
        Assert.That(locks |> List.map File.ReadAllText, Is.EqualTo texts)
        Assert.That(locks |> List.map File.GetLastWriteTimeUtc, Is.EqualTo stamps, "a lock was rewritten")

    /// `update-locks` run twice: each lock rule runs once per run -- the library both apps
    /// reference too -- and the second run rewrites nothing.
    [<Test; Category("Integration")>]
    member x.``update-locks generates each lock once per run, the second run writes nothing``() =
        tiny "Base" [ "netstandard2.0" ] [] [] |> ignore
        tiny "App1" [ "netstandard2.0" ] [ "Base" ] [] |> ignore
        tiny "App2" [ "netstandard2.0" ] [ "Base" ] [] |> ignore
        clean [ "Base"; "App1"; "App2" ]
        let ran = ResizeArray ()
        let script = [ lockRule tinyOf id ran; updateLocks [ "App1"; "App2" ] ]

        x.Run "twice-1" [] [ "update-locks" ] script
        Assert.That(List.ofSeq ran |> List.sort, Is.EqualTo [ "App1::Release"; "App2::Release"; "Base::Release" ], "Base once although two need it")
        Assert.That(ran.[0], Is.EqualTo "Base::Release", "the referenced project first")
        let locks = [ "Base"; "App1"; "App2" ] |> List.map (fun n -> lockOf n "Release")
        let stamps = locks |> List.map File.GetLastWriteTimeUtc
        System.Threading.Thread.Sleep 1100

        ran.Clear ()
        x.Run "twice-2" [] [ "update-locks" ] script
        Assert.That(List.ofSeq ran |> List.sort, Is.EqualTo [ "App1::Release"; "App2::Release"; "Base::Release" ], "alwaysRerun: every lock rule runs again, once")
        Assert.That(locks |> List.map File.GetLastWriteTimeUtc, Is.EqualTo stamps, "a lock was rewritten")

    /// The fresh-clone / CI property: committed locks, no `.xake` database, a script that has
    /// the lock rule -- a build reading the locks (`Lock.load`, i.e. `needFiles`) runs no lock
    /// rule and leaves the locks untouched, because no file rule matches `locks/...`.
    [<Test; Category("Integration")>]
    member x.``a build reading committed locks on a fresh clone runs no lock rule``() =
        tiny "Core" [ "netstandard2.0" ] [] [] |> ignore
        tiny "Tool" [ "netstandard2.0" ] [ "Core" ] [] |> ignore
        clean [ "Core"; "Tool" ]
        let ran = ResizeArray ()
        let locks = [ lockOf "Core" "Release"; lockOf "Tool" "Release" ]
        let read = ResizeArray ()
        let build =
            "build" => recipe {
                for l in locks do
                    let! doc = Lock.load l
                    read.Add doc.Configuration }
        let script = [ lockRule tinyOf id ran; updateLocks [ "Tool" ]; build ]
        x.Run "fresh-generate" [] [ "update-locks" ] script
        Assert.That(ran.Count, Is.EqualTo 2)
        let texts = locks |> List.map File.ReadAllText
        let stamps = locks |> List.map File.GetLastWriteTimeUtc

        // the clone: the locks are committed, the database is not
        Assert.That(File.Exists x.TestOptions.DbFileName, Is.True, "the generating run left a database")
        File.Delete x.TestOptions.DbFileName
        ran.Clear ()
        System.Threading.Thread.Sleep 1100
        x.Run "fresh-build" [] [ "build" ] script
        x.Run "fresh-build-again" [] [ "build" ] script
        Assert.That(List.ofSeq ran, Is.Empty, "a lock rule ran during build")
        Assert.That(List.ofSeq read, Is.EqualTo [ "Release"; "Release" ], "the build read the locks")
        Assert.That(locks |> List.map File.ReadAllText, Is.EqualTo texts)
        Assert.That(locks |> List.map File.GetLastWriteTimeUtc, Is.EqualTo stamps)

    /// A Debug project over a Release-only library (a script whose rule needs the referenced
    /// locks in Release) gets the library's Release output; a net462 project over a
    /// netstandard2.0-only library gets netstandard2.0. Without the library's lock the
    /// primitive fails, naming it.
    [<Test; Category("Integration")>]
    member x.``a Debug project over a Release-only library on another framework``() =
        tiny "Lib" [ "netstandard2.0" ] [] [] |> ignore
        tiny "App" [ "net462" ] [ "Lib" ] [] |> ignore
        clean [ "Lib"; "App" ]

        let alone = "app-alone" => Locks.generate lockOf outOf "Debug" (tinyOf "App")
        let e = Assert.Throws<XakeException>(fun () -> x.Run "app-without-lib" [] [ "app-alone" ] [ alone ])
        Assert.That(e.Data0, Does.Contain "generate the lock of 'Lib' first")

        let ran = ResizeArray ()
        x.Run "app-debug" [] [ "lock::App::Debug" ] [ lockRule tinyOf (fun _ -> "Release") ran ]
        Assert.That(List.ofSeq ran, Is.EqualTo [ "Lib::Release"; "App::Debug" ])
        Assert.That(File.Exists (lockOf "Lib" "Debug"), Is.False)
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
        clean [ "Floating" ]
        let lock = lockOf "Floating" "Release"
        let fsharpCore () =
            (Lock.read (roots ()) lock).Entries.Head.Packages |> List.find (fun p -> p.Id = "FSharp.Core") |> fun p -> p.Version
        let ran = ResizeArray ()
        let generate label vars = x.Run label vars [ "update-locks" ] [ lockRule tinyOf id ran; updateLocks [ "Floating" ] ]
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

    /// B2.1: `Locks.restoreAll` before the lock rules restores every set `generate` needs in one
    /// `dotnet restore`, on a first generation (references without locks yet) and on a second
    /// one (locks present); the lock rules then start none, and the locks are the ones
    /// generated without it.
    [<Test; Category("Integration")>]
    member x.``update-locks after Locks.restoreAll starts one restore and writes the same locks``() =
        let pkg id v : Description.Package = { Id = id; Version = Some v; Private = false }
        let fwks = [ "netstandard2.0"; "net472" ]
        tiny "BrC" fwks [] [ pkg "FSharp.Core" "8.0.100" ] |> ignore
        tiny "BrB" fwks [ "BrC" ] [ pkg "System.Memory" "4.6.0" ] |> ignore
        tiny "BrA" fwks [ "BrB" ] [] |> ignore
        let names = [ "BrA"; "BrB"; "BrC" ]
        let locks = names |> List.map (fun n -> lockOf n "Release")
        let ran = ResizeArray ()
        let plain = [ lockRule tinyOf id ran; updateLocks [ "BrA" ] ]
        clean names
        x.Run "batch-plain" [] [ "update-locks" ] plain
        let texts = locks |> List.map File.ReadAllText

        let batched =
            [ lockRule tinyOf id ran
              "update-locks" => recipe {
                  do! Locks.restoreAll (!! ("tiny/Br*/" + Description.FileName)) "Release" lockOf tinyOf
                  do! need [ "lock::BrA::Release" ] } ]
        clean names
        let before = Described.restoreProcesses ()
        x.Run "batch-1" [] [ "update-locks" ] batched
        Assert.That(Described.restoreProcesses () - before, Is.EqualTo 1, "first generation: one restore")
        Assert.That(locks |> List.map File.ReadAllText, Is.EqualTo texts)

        let before = Described.restoreProcesses ()
        x.Run "batch-2" [] [ "update-locks" ] batched
        Assert.That(Described.restoreProcesses () - before, Is.EqualTo 1, "with the locks present: one restore")
        Assert.That(locks |> List.map File.ReadAllText, Is.EqualTo texts)
