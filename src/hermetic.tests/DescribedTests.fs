namespace Tests

open System.IO
open System.Xml
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// `fsc { project }` / `csc { project }`, `config`, `packages` and `Described.restore`: a
/// project description compiled through the lock machinery. The fixtures under
/// `fixtures/src/core` and `fixtures/src/dotnet` are what `xake.project.json` beside
/// `src/core/Xake.fsproj` and `src/dotnet/Xake.Dotnet.fsproj` would hold; a test checks them
/// against the project files, and the integration tests compile the real sources from them
/// (a copy whose `project` points at the real project file).
///
/// Every test that records a lock runs with the script variable `CI=off`.
[<TestFixture>]
type ``Described compilation``() =
    inherit XakeTestBase("described")

    let fixtures = __SOURCE_DIRECTORY__ </> "fixtures" </> "src"
    let srcRoot = Path.GetFullPath (__SOURCE_DIRECTORY__ </> "..")

    let typeNames (path: string) =
        use stream = File.OpenRead path
        use pe = new System.Reflection.PortableExecutable.PEReader (stream)
        let md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader pe
        [ for h in md.TypeDefinitions -> md.GetString (md.GetTypeDefinition h).Name ]

    let readLock (path: string) = Lock.read (Roots.builtin (Directory.GetCurrentDirectory())) path

    /// The fixture `dir` copied to `desc/<dir>/xake.project.json`, its `project` pointing at
    /// the real project file under src/.
    let place (dir: string) =
        let d = Description.read (fixtures </> dir </> Description.FileName)
        let target = Path.GetFullPath ("desc" </> dir)
        Directory.CreateDirectory target |> ignore
        let real = srcRoot </> dir </> d.Project
        let relative = Path.GetRelativePath (target, real)
        Description.write (target </> Description.FileName) { d with Project = relative } |> ignore
        "desc/" + dir + "/" + Description.FileName

    member private x.Run (label: string) (body: Recipe<ExecContext, unit>) =
        xake {x.TestOptions with FileLog = label + ".log"; ThrowOnError = true; Vars = [ "CI", "off" ]; FileLogLevel = Loud} {
            wantOverride ([label])
            rules [ label => body ]
        }

    [<Test>]
    member x.``framework defines are the SDK's``() =
        // `dotnet build -c Release` of SDK 10.0.401, DefineConstants after TRACE;RELEASE
        Assert.That(ProjectPlan.frameworkDefines "netstandard2.0" |> String.concat ";",
            Is.EqualTo "NETSTANDARD;NETSTANDARD2_0;NETSTANDARD1_0_OR_GREATER;NETSTANDARD1_1_OR_GREATER;NETSTANDARD1_2_OR_GREATER;NETSTANDARD1_3_OR_GREATER;NETSTANDARD1_4_OR_GREATER;NETSTANDARD1_5_OR_GREATER;NETSTANDARD1_6_OR_GREATER;NETSTANDARD2_0_OR_GREATER")
        Assert.That(ProjectPlan.frameworkDefines "net462" |> String.concat ";",
            Is.EqualTo "NETFRAMEWORK;NET462;NET20_OR_GREATER;NET30_OR_GREATER;NET35_OR_GREATER;NET40_OR_GREATER;NET45_OR_GREATER;NET451_OR_GREATER;NET452_OR_GREATER;NET46_OR_GREATER;NET461_OR_GREATER;NET462_OR_GREATER")
        Assert.That(ProjectPlan.frameworkDefines "net8.0" |> String.concat ";",
            Is.EqualTo "NET;NET8_0;NETCOREAPP;NET5_0_OR_GREATER;NET6_0_OR_GREATER;NET7_0_OR_GREATER;NET8_0_OR_GREATER;NETCOREAPP1_0_OR_GREATER;NETCOREAPP1_1_OR_GREATER;NETCOREAPP2_0_OR_GREATER;NETCOREAPP2_1_OR_GREATER;NETCOREAPP2_2_OR_GREATER;NETCOREAPP3_0_OR_GREATER;NETCOREAPP3_1_OR_GREATER")

    [<Test>]
    member x.``Debug and Release presets are the SDK's``() =
        Assert.That(Described.preset Description.FSharp "Debug",
            Is.EqualTo (["TRACE"; "DEBUG"], ["--debug+"; "--debug:portable"; "--optimize-"], false))
        Assert.That(Described.preset Description.FSharp "Release",
            Is.EqualTo (["TRACE"; "RELEASE"], ["--debug:portable"; "--optimize+"], true))
        Assert.That(Described.preset Description.CSharp "Debug",
            Is.EqualTo (["TRACE"; "DEBUG"], ["/debug+"; "/debug:portable"; "/optimize-"], true))
        Assert.That(Described.preset Description.CSharp "release",
            Is.EqualTo (["TRACE"; "RELEASE"], ["/debug-"; "/debug:portable"; "/optimize+"], true))
        let e = Assert.Throws<exn>(fun () -> Described.preset Description.FSharp "Staging" |> ignore)
        Assert.That(e.Message, Does.Contain "only Debug and Release")

    [<Test>]
    member x.``the restore project references the packages as declared``() =
        let text =
            Described.restoreProjectText "net462"
                [ { Id = "FSharp.Core"; Version = Some "8.0.100"; Private = false }
                  { Id = "Floating"; Version = None; Private = true } ]
        Assert.That(text, Does.Contain "<TargetFramework>net462</TargetFramework>")
        Assert.That(text, Does.Contain "<PackageReference Include=\"FSharp.Core\" Version=\"8.0.100\" />")
        Assert.That(text, Does.Contain "<PackageReference Include=\"Floating\" PrivateAssets=\"all\" />")
        Assert.That(text, Does.Contain "<DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>")

    /// The fixtures are faithful to the project files: frameworks, compile items in order,
    /// package references (per framework), framework references, the doc switch.
    [<Test>]
    member x.``the fixtures describe src/core and src/dotnet``() =
        for dir, projectFile in [ "core", "Xake.fsproj"; "dotnet", "Xake.Dotnet.fsproj" ] do
            let path = fixtures </> dir </> Description.FileName
            let text = File.ReadAllText path
            let d = Description.parse text
            Assert.That(Description.format d, Is.EqualTo text, "the fixture is not in the writer's format")
            Assert.That(d.Project, Is.EqualTo projectFile)
            let doc = XmlDocument ()
            doc.Load (srcRoot </> dir </> projectFile)
            let items (xpath: string) (attr: string) =
                [ for n in doc.SelectNodes xpath |> Seq.cast<XmlElement> -> n.GetAttribute attr ]
            let property name = (doc.SelectSingleNode (sprintf "//PropertyGroup/%s" name)).InnerText
            Assert.That(d.Sources, Is.EqualTo (items "//Compile" "Include" |> List.map (fun s -> s.Replace ('\\', '/'))))
            Assert.That(d.Frameworks, Is.EqualTo (property "TargetFrameworks" |> fun s -> s.Split ';' |> List.ofArray))
            Assert.That(d.Options.Doc, Is.EqualTo (property "GenerateDocumentationFile" = "true"))
            Assert.That(d.Packages |> List.map (fun p -> p.Id, p.Version.Value),
                Is.EqualTo (items "/Project/ItemGroup[not(@Condition)]/PackageReference" "Include"
                            |> List.map (fun id -> id, (doc.SelectSingleNode (sprintf "/Project/ItemGroup[not(@Condition)]/PackageReference[@Include='%s']" id) :?> XmlElement).GetAttribute "Version")))
            for tfm, items' in d.ByFramework do
                let cond = sprintf "/Project/ItemGroup[@Condition=\"'$(TargetFramework)' == '%s'\"]" tfm
                Assert.That(items'.References, Is.EqualTo (items (cond + "/Reference") "Include"), tfm)
                Assert.That(items'.Packages |> List.map (fun p -> p.Id), Is.EqualTo (items (cond + "/PackageReference") "Include"), tfm)
            let friends = [ for n in doc.SelectNodes "//AssemblyAttribute/_Parameter1" -> n.InnerText ]
            Assert.That(d.InternalsVisibleTo |> List.map (fun f -> f.Name), Is.EqualTo friends)
            Assert.That(d.ProjectRefs.Length, Is.EqualTo (items "//ProjectReference" "Include").Length)

    /// The 0.2 lock reader takes a mask in `Sources` as it takes a path: written, read back
    /// unchanged (no format change).
    [<Test>]
    member x.``a mask in a lock's Sources reads back unchanged``() =
        let roots = Roots.builtin "/repo"
        let c =
            { Csc.ofArgs [ "/nologo"; "/out:/repo/out/a.dll"; "/repo/src/a/**/*.cs" ] with
                Name = "a"; Framework = "netstandard2.0"; Directory = "/repo" }
        let c = { c with Sources = [ "/repo/src/a/**/*.cs"; "!/repo/src/a/obj/**/*" ] }
        let doc : Lock.Document = { Configuration = ""; Properties = []; Entries = [ Lock.ofCsc c ] }
        let text = Lock.format roots doc
        Assert.That(text, Does.Contain "\"$(ProjectRoot)/src/a/**/*.cs\"")
        let back = Lock.parse roots text
        Assert.That(text, Does.Contain "\"!$(ProjectRoot)/src/a/obj/**/*\"")
        Assert.That(back.Entries.Head.Compilation.Sources, Is.EqualTo [ "/repo/src/a/**/*.cs"; "!/repo/src/a/obj/**/*" ])
        Assert.That(Lock.format roots back, Is.EqualTo text)

    [<Test>]
    member x.``a description with packages needs them passed with packages``() =
        let core = place "core"
        let e = Assert.Throws<XakeException>(fun () ->
            x.Run "no-packages" (recipe {
                // `config` makes it this package's composition (the core's alone composes FSharp.Core itself)
                let! _ = fsc { targetfwk "netstandard2.0"; out (File.make "out/nopkg/Xake.dll"); project core; config "Release"; resolve }
                return () }))
        Assert.That(e.Data0, Does.Contain "resolve them with Described.restore")

    [<Test; Category("Integration")>]
    member x.``restore resolves the packages through msbuild``() =
        let core = place "core"
        let mutable restored = None
        x.Run "restore-core" (recipe {
            let! d = Description.load core
            let! r = Described.restore d "netstandard2.0"
            restored <- Some r })
        let r = restored.Value
        Assert.That(r.References |> List.exists (fun p -> p.EndsWith "/fsharp.core/8.0.100/lib/netstandard2.0/FSharp.Core.dll"), Is.True, sprintf "%A" r.References)
        let fsharpCore = r.Packages |> List.find (fun p -> p.Id = "FSharp.Core")
        Assert.That((fsharpCore.Version, fsharpCore.Direct), Is.EqualTo (("8.0.100", true)))
        Assert.That(Directory.GetFiles ("obj/xake/restore", "project.assets.json", SearchOption.AllDirectories)
                    |> Array.exists (fun f -> f.Replace('\\', '/').Contains "/netstandard2.0-"), Is.True)

    /// The acceptance of the PR: src/core and src/dotnet from their descriptions, Release,
    /// both frameworks, through `Lock.record` then `Lock.build`, into assemblies that load.
    [<Test; Category("Integration")>]
    member x.``src/core and src/dotnet compile from their descriptions through the lock``() =
        let core, dotnet = place "core", place "dotnet"
        let lockOf name fwk = sprintf "locks/%s/Release-%s.json" name fwk
        let outOf fwk name = sprintf "out/Release/%s/%s.dll" fwk name
        for f in Directory.GetFiles (".", "*.json", SearchOption.AllDirectories) |> Array.filter (fun f -> f.Contains "locks") do File.Delete f
        let mutable resolved : Fsc list = []
        for fwk in [ "netstandard2.0"; "net462" ] do
            x.Run ("described-" + fwk) (recipe {
                let! dc = Description.load core
                let! pc = Described.restore dc fwk
                let! c = fsc { targetfwk fwk; out (File.make (outOf fwk "Xake")); project core; config "Release"; packages pc; resolve }
                do! Lock.record (lockOf "Xake" fwk) (Lock.Compilation.Fsc c)
                do! Lock.build (lockOf "Xake" fwk) (Lock.Compilation.Fsc c)

                let! dd = Description.load dotnet
                let! pd = Described.restore dd fwk
                let! f = fsc { targetfwk fwk; out (File.make (outOf fwk "Xake.Dotnet")); ref !!(outOf fwk "Xake")
                               project dotnet; config "Release"; packages pd; resolve }
                do! Lock.record (lockOf "Xake.Dotnet" fwk) (Lock.Compilation.Fsc f)
                do! Lock.build (lockOf "Xake.Dotnet" fwk) (Lock.Compilation.Fsc f)
                resolved <- resolved @ [ c; f ] })

        for f in resolved do
            Assert.That(f.Options, Does.Contain "--deterministic+")
            Assert.That(f.Options, Does.Contain "--optimize+")
            Assert.That(f.Options |> List.exists (fun o -> o.StartsWith "--pathmap:" && o.EndsWith "/=/_/"), Is.True)
            Assert.That(f.Defines, Does.Contain "RELEASE")
            Assert.That(f.Defines, Does.Contain "TRACE")
            Assert.That(f.Sources.Head, Does.EndWith ".InternalsVisibleTo.fs")
            Assert.That(f.Options |> List.exists (fun o -> o.StartsWith "--doc:"), Is.True)
            // packages from the restore, the framework from targetfwk; nothing else from NuGet
            let fsharpCore = f.Dependencies.References |> List.filter (fun r -> r.Path.EndsWith "FSharp.Core.dll")
            Assert.That(fsharpCore |> List.map (fun r -> r.Path), Has.All.Contains "/fsharp.core/8.0.100/lib/netstandard2.0/")
        Assert.That(resolved.[1].Defines, Does.Contain "NETSTANDARD2_0")
        Assert.That(resolved.[3].Defines, Does.Contain "NETFRAMEWORK")
        Assert.That(resolved.[1].Dependencies.References |> List.exists (fun r -> r.Path.EndsWith "Microsoft.Win32.Registry.dll"), Is.True)
        Assert.That(resolved.[3].Dependencies.References |> List.exists (fun r -> r.Path.EndsWith "System.Windows.Forms.dll"), Is.True)

        let entry = (readLock (lockOf "Xake.Dotnet" "netstandard2.0")).Entries.Head
        Assert.That(entry.Compilation.Options |> List.exists (fun o -> o = "--pathmap:$(ProjectRoot)/=/_/"), Is.False,
            "the lock is read back expanded")
        Assert.That(File.ReadAllText (lockOf "Xake.Dotnet" "netstandard2.0"), Does.Contain "\"--pathmap:$(ProjectRoot)/=/_/\"")

        // loadable: the netstandard2.0 pair in a context of its own
        let alc = System.Runtime.Loader.AssemblyLoadContext ("described", true)
        alc.add_Resolving (fun ctx name ->
            let candidate = Path.GetFullPath (outOf "netstandard2.0" name.Name)
            if File.Exists candidate then ctx.LoadFromAssemblyPath candidate else null)
        try
            for name in [ "Xake"; "Xake.Dotnet" ] do
                let a = alc.LoadFromAssemblyPath (Path.GetFullPath (outOf "netstandard2.0" name))
                Assert.That(a.GetExportedTypes (), Is.Not.Empty, name)
            Assert.That(File.Exists (outOf "net462" "Xake.Dotnet"), Is.True)
            Assert.That(File.Exists (outOf "net462" "Xake.Dotnet" |> fun p -> Path.ChangeExtension (p, ".xml")), Is.True)
        finally alc.Unload ()

    [<Test; Category("Integration")>]
    member x.``Debug is the SDK's Debug preset``() =
        let core = place "core"
        let mutable options = []
        let mutable defines = []
        x.Run "described-debug" (recipe {
            let! d = Description.load core
            let! p = Described.restore d "netstandard2.0"
            let! c = fsc { targetfwk "netstandard2.0"; out (File.make "out/Debug/Xake.dll"); project core; config "Debug"; packages p; resolve }
            options <- c.Options
            defines <- c.Defines })
        Assert.That(options, Is.SupersetOf [ "--debug+"; "--debug:portable"; "--optimize-"; "--tailcalls-"; "--deterministic+" ])
        Assert.That(options, Has.None.EqualTo "--optimize+")
        Assert.That(defines |> List.take 3, Is.EqualTo [ "TRACE"; "DEBUG"; "NETSTANDARD" ])

    /// `project` is the core's (Xake 3.9): alone it is the core's composition (sources expanded,
    /// no pathmap); `config`, `packages` or `lock` after it make it this package's (masks kept,
    /// `/pathmap`), `lock` right after `project` included.
    [<Test; Category("Integration")>]
    member x.``the core's project alone, this package's composition after config or lock``() =
        if Directory.Exists "route" then Directory.Delete ("route", true)
        Directory.CreateDirectory "route" |> ignore
        File.WriteAllText ("route/A.cs", "namespace R { public class A {} }\n")
        File.WriteAllText ("route/xake.project.json",
            "{\n  \"name\": \"R\",\n  \"project\": \"R.csproj\",\n  \"language\": \"csharp\",\n  \"kind\": \"library\",\n  \"frameworks\": [\"netstandard2.0\"],\n  \"sources\": [\"**/*.cs\"]\n}\n")
        let lockPath = "locks/R.json"
        if File.Exists lockPath then File.Delete lockPath
        let mutable core, described = None, None
        x.Run "route-resolve" (recipe {
            let! c = csc { out (File.make "out/R.dll"); project "route/xake.project.json"; resolve }
            core <- Some c
            let! d = csc { out (File.make "out/R.dll"); project "route/xake.project.json"; config "Release"; resolve }
            described <- Some d })
        Assert.That(core.Value.Sources |> List.map Path.GetFileName, Is.EqualTo [ "A.cs" ])
        Assert.That(core.Value.Options |> List.exists (fun o -> o.StartsWith "/pathmap"), Is.False)
        Assert.That(described.Value.Sources |> List.exists (fun s -> s.EndsWith "/route/**/*.cs"), Is.True)
        Assert.That(described.Value.Options |> List.exists (fun o -> o.StartsWith "/pathmap"), Is.True)
        do xake {x.TestOptions with FileLog = "route-lock.log"; ThrowOnError = true; Vars = [ "CI", "off" ]} {
            wantOverride [ "out/R.dll" ]
            rules [ "out/R.dll" ..> csc { project "route/xake.project.json"; lock lockPath } ]
        }
        Assert.That(File.Exists "out/R.dll", Is.True)
        Assert.That(File.ReadAllText lockPath, Does.Contain "\"$(ProjectRoot)/route/**/*.cs\"")

    [<Test>]
    member x.``config needs project``() =
        let e = Assert.Throws<XakeException>(fun () ->
            x.Run "config-no-project" (recipe {
                let! _ = csc { out (File.make "out/N.dll"); src !!"none/*.cs"; config "Release"; resolve }
                return () }))
        Assert.That(e.Data0, Does.Contain "need `project")

    /// A C# description keeps its masks: in the resolved `Csc`, in the lock; an added `.cs` is
    /// compiled on the next build without a lock change; `obj/` is not a source.
    [<Test; Category("Integration")>]
    member x.``a C# description keeps its masks``() =
        if Directory.Exists "cs" then Directory.Delete ("cs", true)
        Directory.CreateDirectory "cs/Sub" |> ignore
        Directory.CreateDirectory "cs/obj" |> ignore
        File.WriteAllText ("cs/A.cs", "namespace Cs { public class A {} }\n")
        File.WriteAllText ("cs/Sub/B.cs", "namespace Cs { public class B { internal int X; } }\n")
        File.WriteAllText ("cs/obj/Broken.cs", "this is not C#\n")
        File.WriteAllText ("cs/xake.project.json",
            "{\n  \"name\": \"Cs\",\n  \"project\": \"Cs.csproj\",\n  \"language\": \"csharp\",\n  \"kind\": \"library\",\n  \"frameworks\": [\"netstandard2.0\"],\n  \"sources\": [\"**/*.cs\", \"!obj/**/*\", \"!bin/**/*\"],\n  \"internalsVisibleTo\": [\"Cs.Tests\"]\n}\n")
        let lockPath = "locks/Cs.json"
        if File.Exists lockPath then File.Delete lockPath
        if File.Exists "out/Cs.dll" then File.Delete "out/Cs.dll"
        let build label =
            xake {x.TestOptions with FileLog = label + ".log"; ThrowOnError = true; Vars = [ "CI", "off" ]; FileLogLevel = Loud} {
                wantOverride ([ "out/Cs.dll" ])
                rules [ "out/Cs.dll" ..> csc { project "cs/xake.project.json"; config "Release"; lock lockPath } ]
            }
        let mutable masked = None
        x.Run "cs-resolve" (recipe {
            let! c = csc { out (File.make "out/Cs.dll"); project "cs/xake.project.json"; config "Release"; resolve }
            masked <- Some c })
        let c = masked.Value
        Assert.That(c.Sources |> List.skip 1 |> List.map (fun s -> s.Substring (s.IndexOf "/cs/")),
            Is.EqualTo [ "/cs/**/*.cs"; "/cs/obj/**/*"; "/cs/bin/**/*" ])
        Assert.That(c.Sources.[2], Does.StartWith "!")
        Assert.That(c.Sources.Head, Does.EndWith "Cs.InternalsVisibleTo.cs")
        Assert.That(c.Options, Is.SupersetOf [ "/deterministic+"; "/optimize+"; "/debug-" ])

        build "cs-1"
        Assert.That(File.Exists "out/Cs.dll", Is.True)
        let lockText = File.ReadAllText lockPath
        Assert.That(lockText, Does.Contain "\"$(ProjectRoot)/cs/**/*.cs\"")
        Assert.That(lockText, Does.Contain "\"!$(ProjectRoot)/cs/obj/**/*\"")
        Assert.That(lockText, Does.Not.Contain "B.cs")
        let first = File.GetLastWriteTimeUtc "out/Cs.dll"

        build "cs-2"
        Assert.That(File.GetLastWriteTimeUtc "out/Cs.dll", Is.EqualTo first, "nothing changed, nothing rebuilt")

        File.WriteAllText ("cs/Sub/C.cs", "namespace Cs { public class C {} }\n")
        build "cs-3"
        Assert.That(File.ReadAllText lockPath, Is.EqualTo lockText, "an added file changes no lock")
        Assert.That(typeNames "out/Cs.dll", Is.SupersetOf [ "A"; "B"; "C" ], "class C is in the rebuilt assembly")
        Assert.That(System.Text.Encoding.UTF8.GetString (File.ReadAllBytes "out/Cs.dll"), Does.Contain "Cs.Tests")

    /// A C# library description under `<dir>/<name>/`, written as JSON.
    member private x.CsDescription (dir: string) (name: string) (frameworks: string list) (refs: string list) (packages: (string * string) list) =
        let quoted (xs: string list) = xs |> List.map (sprintf "\"%s\"") |> String.concat ", "
        let pkgs = packages |> List.map (fun (id, v) -> sprintf "{ \"id\": \"%s\", \"version\": \"%s\" }" id v) |> String.concat ", "
        File.WriteAllText (dir </> name </> Description.FileName,
            sprintf "{\n  \"name\": \"%s\",\n  \"project\": \"%s.csproj\",\n  \"language\": \"csharp\",\n  \"kind\": \"library\",\n  \"frameworks\": [%s],\n  \"sources\": [\"*.cs\"],\n  \"projectRefs\": [%s],\n  \"packages\": [%s]\n}\n"
                name name (quoted frameworks) (quoted refs) pkgs)

    /// G14: compiled straight from the descriptions (no lock), A -> B where only B declares the
    /// package A's source uses: `restore` gives A nothing, `restoreWith` brings B's package.
    [<Test; Category("Integration")>]
    member x.``restoreWith brings the packages of the project references``() =
        if Directory.Exists "g14" then Directory.Delete ("g14", true)
        for d in [ "g14/A"; "g14/B" ] do Directory.CreateDirectory d |> ignore
        File.WriteAllText ("g14/B/B.cs", "namespace B { public class Buf { public System.Span<byte> Of (byte[] b) { return b; } } }\n")
        File.WriteAllText ("g14/A/A.cs", "namespace A { public class User { public int Length (byte[] b) { return new B.Buf ().Of (b).Slice (1).Length; } } }\n")
        x.CsDescription "g14" "B" [ "netstandard2.0" ] [] [ "System.Memory", "4.6.0" ]
        x.CsDescription "g14" "A" [ "netstandard2.0" ] [ "B" ] []
        let descOf name = sprintf "g14/%s/%s" name Description.FileName
        let mutable own = None
        let mutable flowing = None
        x.Run "g14" (recipe {
            let! b = Description.load (descOf "B")
            let! pb = Described.restoreWith descOf b "netstandard2.0"
            do! csc { targetfwk "netstandard2.0"; out (File.make "g14/out/B.dll"); project (descOf "B"); config "Release"; packages pb }
            let! a = Description.load (descOf "A")
            let! pa = Described.restore a "netstandard2.0"
            own <- Some pa
            let! pa = Described.restoreWith descOf a "netstandard2.0"
            flowing <- Some pa
            do! csc { targetfwk "netstandard2.0"; out (File.make "g14/out/A.dll"); ref !!"g14/out/B.dll"; project (descOf "A"); config "Release"; packages pa } })
        Assert.That(own.Value.Packages, Is.Empty, "A declares no package")
        Assert.That(flowing.Value.Packages |> List.exists (fun p -> p.Id = "System.Memory" && p.Version = "4.6.0" && p.Direct), Is.True, sprintf "%A" flowing.Value.Packages)
        Assert.That(File.Exists "g14/out/A.dll", Is.True)

    /// The direct route flows a referenced F# project's implicit `System.ValueTuple` (net462),
    /// as the lock route and msbuild do; a referrer that has it implicitly too gets it once.
    [<Test>]
    member x.``flowingPackages takes a reference's implicit packages``() =
        if Directory.Exists "flowimplicit" then Directory.Delete ("flowimplicit", true)
        for d in [ "flowimplicit/A"; "flowimplicit/F"; "flowimplicit/G" ] do Directory.CreateDirectory d |> ignore
        let fs (name: string) (refs: string list) =
            File.WriteAllText (sprintf "flowimplicit/%s/%s" name Description.FileName,
                sprintf "{\n  \"name\": \"%s\",\n  \"project\": \"%s.fsproj\",\n  \"language\": \"fsharp\",\n  \"frameworks\": [\"net462\"],\n  \"sources\": [\"Lib.fs\"],\n  \"projectRefs\": [%s]\n}\n"
                    name name (refs |> List.map (sprintf "\"%s\"") |> String.concat ", "))
        fs "F" []
        fs "G" [ "F" ]
        x.CsDescription "flowimplicit" "A" [ "net462" ] [ "F" ] []
        let descOf name = sprintf "flowimplicit/%s/%s" name Description.FileName
        let mutable csharp = []
        let mutable fsharp = []
        x.Run "flowimplicit" (recipe {
            let! a = Description.load (descOf "A")
            let! fa = Described.flowingPackages descOf a "net462"
            csharp <- fa
            let! g = Description.load (descOf "G")
            let! fg = Described.flowingPackages descOf g "net462"
            fsharp <- fg })
        Assert.That(csharp |> List.map (fun p -> p.Id, p.Version), Is.EqualTo [ "System.ValueTuple", Some "4.6.2" ])
        Assert.That(fsharp, Is.Empty, "G has System.ValueTuple implicitly itself")

    /// G6: a package's `frameworkAssemblies` (DocumentFormat.OpenXml: WindowsBase for .NET
    /// Framework) are referenced by name on net472, as msbuild does; without them the use of
    /// the package fails (CS0012).
    [<Test; Category("Integration")>]
    member x.``a package's framework assemblies are referenced on .NET Framework``() =
        if Directory.Exists "g6" then Directory.Delete ("g6", true)
        Directory.CreateDirectory "g6/X" |> ignore
        File.WriteAllText ("g6/X/X.cs",
            "namespace X { public static class Doc { public static bool Main (System.IO.Stream s) { using (var d = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open (s, false)) { return d.MainDocumentPart != null; } } } }\n")
        x.CsDescription "g6" "X" [ "net472" ] [] [ "DocumentFormat.OpenXml", "3.2.0" ]
        let path = sprintf "g6/X/%s" Description.FileName
        let mutable restored = None
        let mutable resolved = None
        x.Run "g6" (recipe {
            let! d = Description.load path
            let! r = Described.restore d "net472"
            restored <- Some r
            let! c = csc { targetfwk "net472"; out (File.make "g6/out/X.dll"); project path; config "Release"; packages r; resolve }
            resolved <- Some c
            do! csc { targetfwk "net472"; out (File.make "g6/out/X.dll"); project path; config "Release"; packages r } })
        Assert.That(restored.Value.FrameworkReferences, Does.Contain "WindowsBase")
        Assert.That(resolved.Value.Dependencies.References |> List.exists (fun r -> r.Path.EndsWith "/WindowsBase.dll"), Is.True)
        Assert.That(File.Exists "g6/out/X.dll", Is.True)
        let e = Assert.Throws<XakeException>(fun () ->
            x.Run "g6-without" (recipe {
                let! d = Description.load path
                let! r = Described.restore d "net472"
                do! csc { targetfwk "net472"; out (File.make "g6/out/Without.dll"); project path; config "Release"; packages { r with FrameworkReferences = [] } } }))
        Assert.That(e.Data0, Is.Not.Empty)
        Assert.That(File.ReadAllText "g6-without.log", Does.Contain "CS0012")

    /// The descriptions `names` (C#, under `<dir>/`), fresh: each folder emptied and given a source.
    member private x.CsFolder (dir: string) (names: string list) =
        if Directory.Exists dir then Directory.Delete (dir, true)
        for n in names do
            Directory.CreateDirectory (dir </> n) |> ignore
            File.WriteAllText (dir </> n </> (n + ".cs"), sprintf "namespace %s { public class T {} }\n" n)

    /// B2.1: two targets restoring the same package set in one build start one `dotnet restore`
    /// (the build's cache), and get the same result.
    [<Test; Category("Integration")>]
    member x.``two targets with the same package set restore once``() =
        x.CsFolder "same" [ "X"; "Y" ]
        x.CsDescription "same" "X" [ "netstandard2.0" ] [] [ "System.Memory", "4.6.0" ]
        x.CsDescription "same" "Y" [ "netstandard2.0" ] [] [ "System.Memory", "4.6.0" ]
        let results = System.Collections.Concurrent.ConcurrentBag<Described.Restored> ()
        let restoreOf name = recipe {
            let! d = Description.load (sprintf "same/%s/%s" name Description.FileName)
            let! r = Described.restore d "netstandard2.0"
            results.Add r }
        let before = Described.restoreProcesses ()
        xake {x.TestOptions with FileLog = "same.log"; ThrowOnError = true; Vars = [ "CI", "off" ]; FileLogLevel = Loud} {
            wantOverride [ "both" ]
            rules [ "both" <== [ "x"; "y" ]; "x" => restoreOf "X"; "y" => restoreOf "Y" ]
        }
        Assert.That(Described.restoreProcesses () - before, Is.EqualTo 1)
        let all = results.ToArray ()
        Assert.That(all.Length, Is.EqualTo 2)
        Assert.That(all.[0], Is.EqualTo all.[1])
        Assert.That(all.[0].Packages |> List.exists (fun p -> p.Id = "System.Memory" && p.Direct), Is.True)

    /// B2.1: `restoreAll` over three descriptions x two frameworks is one msbuild process; the
    /// targets' `restoreWith` afterwards start none, and get what they would alone.
    [<Test; Category("Integration")>]
    member x.``restoreAll restores three descriptions for two frameworks in one process``() =
        x.CsFolder "batch" [ "A"; "B"; "C" ]
        let fwks = [ "netstandard2.0"; "net472" ]
        x.CsDescription "batch" "A" fwks [] [ "System.Memory", "4.6.0" ]
        x.CsDescription "batch" "B" fwks [] [ "System.Buffers", "4.6.0" ]
        x.CsDescription "batch" "C" fwks [ "A" ] []        // C gets A's package: the same set as A
        let descOf name = sprintf "batch/%s/%s" name Description.FileName
        let names = [ "A"; "B"; "C" ]
        let batched = System.Collections.Generic.List<string * Described.Restored> ()
        let mutable afterBatch = 0
        let before = Described.restoreProcesses ()
        x.Run "batch" (recipe {
            do! Described.restoreAll descOf [ for n in names -> descOf n ] fwks
            afterBatch <- Described.restoreProcesses ()
            for n in names do
                let! d = Description.load (descOf n)
                for f in fwks do
                    let! r = Described.restoreWith descOf d f
                    batched.Add ((n + "/" + f), r) })
        Assert.That(afterBatch - before, Is.EqualTo 1, "one dotnet restore for the batch")
        Assert.That(Described.restoreProcesses () - afterBatch, Is.EqualTo 0, "the targets read the cache")
        // the same results as restoring alone (a new build: an empty cache)
        let alone = System.Collections.Generic.List<string * Described.Restored> ()
        x.Run "alone" (recipe {
            for n in names do
                let! d = Description.load (descOf n)
                for f in fwks do
                    let! r = Described.restoreWith descOf d f
                    alone.Add ((n + "/" + f), r) })
        Assert.That(List.ofSeq batched, Is.EqualTo (List.ofSeq alone))
        let c472 = batched |> Seq.find (fst >> (=) "C/net472") |> snd
        Assert.That(c472.Packages |> List.exists (fun p -> p.Id = "System.Memory" && p.Direct), Is.True)

    /// B2.1: the transitive project references of a description, a project after the ones it
    /// references, each once; a cycle fails naming it.
    [<Test>]
    member x.``closure lists the transitive project references in dependency order``() =
        x.CsFolder "closure" [ "A"; "B"; "C"; "D" ]
        x.CsDescription "closure" "A" [ "netstandard2.0" ] [ "B"; "C" ] []
        x.CsDescription "closure" "B" [ "netstandard2.0" ] [ "C" ] []
        x.CsDescription "closure" "C" [ "netstandard2.0" ] [] []
        x.CsDescription "closure" "D" [ "netstandard2.0" ] [ "A" ] []
        let descOf name = sprintf "closure/%s/%s" name Description.FileName
        let mutable ofA = []
        let mutable ofD = []
        x.Run "closure" (recipe {
            let! a = Description.load (descOf "A")
            let! refs = Described.closure descOf a
            ofA <- refs
            let! d = Description.load (descOf "D")
            let! refs = Described.closure descOf d
            ofD <- refs })
        Assert.That(ofA, Is.EqualTo [ "C"; "B" ])
        Assert.That(ofD, Is.EqualTo [ "C"; "B"; "A" ])
        x.CsDescription "closure" "C" [ "netstandard2.0" ] [ "A" ] []
        let e = Assert.Throws<XakeException>(fun () ->
            x.Run "closure-cycle" (recipe {
                let! d = Description.load (descOf "D")
                let! _ = Described.closure descOf d
                () }))
        Assert.That(File.ReadAllText "closure-cycle.log", Does.Contain "cycle: D -> A -> B -> C -> A")
