namespace Tests

open System.IO
open System.Threading
open System.Threading.Tasks
open NUnit.Framework

open Xake
open Xake.Dotnet

/// The pieces of `Project.import` that do not need msbuild: reading the compiler's command
/// line, the lock's round trip, the import list of a preprocessed project.
[<TestFixture>]
type ``Project import``() =

    let temp name = Path.Combine (Path.GetTempPath(), "xake-test-" + name)

    // `withProjectLock` needs a real ExecContext (it goes through `withResource`, which
    // touches the scheduler's CPU slot), so it is driven through a small long-lived engine --
    // the same pattern `DelegatedTests.fs` uses for `withResource`/`runDetached` -- rather than
    // called directly. Two rules "importing" the same fake project path must never overlap;
    // two rules on different paths must be free to.
    let engineBuilder threads =
        RulesBuilder
            { ExecOptions.Default with
                Threads = threads
                IgnoreCommandLine = true
                NoPersist = true
                Progress = false
                ConLogLevel = Silent
                FileLogLevel = Silent }

    // A concurrency meter: enter()/leave() bracket a region; peak records the max overlap.
    let meter () =
        let current = ref 0
        let peak = ref 0
        let gate = obj ()
        let enter () =
            let n = Interlocked.Increment current
            lock gate (fun () -> if n > !peak then peak := n)
        let leave () = Interlocked.Decrement current |> ignore
        enter, leave, peak

    /// A hand-made entry in the new three-section shape, built from a flat argument list the
    /// way `parseImport` builds one.
    let entryOf name (project: string) (directory: string) (args: string list) : Lock.Entry =
        let compilation, references, analyzers = Lock.Compilation.ofArgs args
        { Name = name
          Evaluation = { Project = project; ProjectRefs = []; Imports = []; Sdk = "8.0.425"; SdkPin = Some (Lock.Pinned "8.0.425"); Properties = Map.empty }
          Compilation = { compilation with Directory = directory }
          Dependencies =
            { Compiler = { Tool = "csc"; Path = "/dotnet/sdk/8.0.425/Roslyn/bincore/csc.dll"; Sha256 = "ab01"; Version = "4.11.0-3.25569.22" }
              References = references
              Analyzers = analyzers
              Packages = [] } }

    [<Test>]
    member x.``tells switches from sources``() =
        Assert.That(CscArgs.parse "/noconfig", Is.EqualTo (CscArgs.Switch ("noconfig", "")))
        Assert.That(CscArgs.parse "/optimize+", Is.EqualTo (CscArgs.Switch ("optimize+", "")))
        Assert.That(CscArgs.parse "/reference:/pkgs/a.dll", Is.EqualTo (CscArgs.Switch ("reference", "/pkgs/a.dll")))
        Assert.That(CscArgs.parse "/define:A;B", Is.EqualTo (CscArgs.Switch ("define", "A;B")))
        // an absolute Unix path starts with '/' as well
        Assert.That(CscArgs.parse "/Users/me/proj/A.cs", Is.EqualTo (CscArgs.Source "/Users/me/proj/A.cs"))
        Assert.That(CscArgs.parse "Aggregates/Aggregate.cs", Is.EqualTo (CscArgs.Source "Aggregates/Aggregate.cs"))
        Assert.That(CscArgs.parse @"C:\proj\A.cs", Is.EqualTo (CscArgs.Source @"C:\proj\A.cs"))

    [<Test>]
    member x.``knows which arguments name files``() =
        let args =
            [ "/noconfig"; "/nowarn:1701,1702"; "/define:TRACE;RELEASE"
              "/reference:/pkgs/a.dll"; "/r:ext=/pkgs/b.dll,/pkgs/c.dll"
              "/analyzer:/sdk/an.dll"; "/keyfile:/proj/key.snk"
              "/resource:/proj/obj/X.resources,Ns.X.resources,public"
              "/additionalfile:/proj/a.txt"; "/embed:/proj/obj/AssemblyInfo.cs"
              "/analyzerconfig:/proj/.editorconfig"
              "/sourcelink:/proj/obj/sourcelink.json"
              "/out:/proj/obj/X.dll"; "/doc:/proj/obj/X.xml"; "/refout:/proj/obj/ref/X.dll"
              "/errorlog:/proj/obj/log.sarif,version=2"
              "/pathmap:/proj=/_/"
              "A.cs"; "sub/B.cs" ]

        Assert.That(CscArgs.sources args, Is.EqualTo ["A.cs"; "sub/B.cs"])
        Assert.That(CscArgs.inputs args, Is.EqualTo [
            "/pkgs/a.dll"; "/pkgs/b.dll"; "/pkgs/c.dll"; "/sdk/an.dll"; "/proj/key.snk"
            "/proj/obj/X.resources"; "/proj/a.txt"; "/proj/obj/AssemblyInfo.cs"; "/proj/.editorconfig"
            "/proj/obj/sourcelink.json"
            "A.cs"; "sub/B.cs" ])
        Assert.That(CscArgs.outputs args, Is.EqualTo [
            "/proj/obj/X.dll"; "/proj/obj/X.xml"; "/proj/obj/ref/X.dll"; "/proj/obj/log.sarif" ])
        Assert.That(CscArgs.switchValues "reference" args, Is.EqualTo ["/pkgs/a.dll"; "/pkgs/b.dll"; "/pkgs/c.dll"])

    [<Test>]
    member x.``makes the paths absolute and leaves the rest alone``() =
        let args =
            [ "/nowarn:1701,1702"; "/define:TRACE;RELEASE"
              "/r:ext=../pkgs/b.dll,/pkgs/c.dll"
              "/resource:obj/X.resources,Ns.X.resources"
              "/analyzer:/sdk/targets/../analyzers/an.dll"
              "/out:obj/X.dll"; "/optimize+"; "A.cs"; "sub/B.cs" ]

        Assert.That(CscArgs.absolutize "/proj/src" args, Is.EqualTo [
            "/nowarn:1701,1702"; "/define:TRACE;RELEASE"
            "/r:ext=/proj/pkgs/b.dll,/pkgs/c.dll"
            "/resource:/proj/src/obj/X.resources,Ns.X.resources"
            // `..` folded: two spellings of one file are one input
            "/analyzer:/sdk/analyzers/an.dll"
            "/out:/proj/src/obj/X.dll"; "/optimize+"; "/proj/src/A.cs"; "/proj/src/sub/B.cs" ])

    [<Test>]
    member x.``the lock reads back what was written, with roots tokenized``() =
        let packages = (Roots.nugetRoot()).Replace('\\', '/').TrimEnd '/'
        let root = Directory.GetCurrentDirectory().Replace ('\\', '/')
        let roots = Roots.builtin (Directory.GetCurrentDirectory())

        let args =
            [ "/noconfig"; "/define:TRACE;RELEASE"
              "/reference:" + packages + "/netstandard.library/2.0.3/build/netstandard2.0/ref/netstandard.dll"
              "/reference:ext=" + root + "/src/Other/bin/Release/Other.dll"
              "/analyzer:/dotnet/sdk/8.0.425/Sdks/Microsoft.NET.Sdk/analyzers/an.dll"
              "/keyfile:" + root + "/.keys/sample.snk"
              sprintf "/pathmap:%s=/_/" root
              "/out:" + root + "/src/Sample/obj/xake/net8.0/Sample.Lib.dll"
              root + "/src/Sample/A.cs"
              "/warnaserror+:NU1605" ]
        let plain = entryOf "Sample.Lib" (root + "/src/Sample/Sample.csproj") (root + "/src/Sample") args
        let entry : Lock.Entry =
            { plain with
                Evaluation =
                    { plain.Evaluation with
                        ProjectRefs = [ root + "/src/Other/Other.csproj" ]
                        Imports = [ { Path = root + "/Directory.Build.props"; Sha256 = "0a0b" } ]
                        SdkPin = Some (Lock.RollsForward ("8.0.100", "latestFeature"))
                        Properties = Map.ofList [ "AssemblyName", "Sample.Lib"; "TargetPath", root + "/src/Sample/bin/Sample.Lib.dll" ] }
                Compilation =
                    { plain.Compilation with
                        Generated = [ root + "/src/Sample/obj/xake/net8.0/Sample.AssemblyInfo.cs", "// <autogenerated />\r\n[assembly: A(\"x\")]\n" ]
                        Resources = [ root + "/src/Sample/Strings.resx", root + "/src/Sample/obj/xake/net8.0/Sample.Strings.resources" ] }
                Dependencies =
                    { plain.Dependencies with
                        References =
                            plain.Dependencies.References |> List.mapi (fun i r -> if i = 0 then { r with Sha256 = "cd02" } else r)
                        Analyzers = plain.Dependencies.Analyzers |> List.map (fun a -> { a with Sha256 = "ef03" })
                        Packages =
                            [ { Id = "Foo.Bar"; Version = "1.2.3"; Sha512 = "AAAA=="; Direct = true; DependsOn = [ "Baz.Qux" ] }
                              { Id = "Baz.Qux"; Version = "4.5.6"; Sha512 = ""; Direct = false; DependsOn = [] } ] } }
        let lock : Lock.Document = {
            Framework = "net8.0"; Configuration = "Release"; Properties = [ "Brand", "X" ]; Entries = [ entry ]
        }

        let text = Lock.writeWith roots lock
        Assert.That(Lock.parseWith roots text, Is.EqualTo lock)

        // nothing machine-specific survives in the file: the checkout and the package cache
        // are tokens, and a root inside a value (`/pathmap:`) is one too
        Assert.That(text, Does.Not.Contain root)
        Assert.That(text, Does.Not.Contain packages)
        Assert.That(text, Does.Contain "/pathmap:$(ProjectRoot)=/_/")
        Assert.That(text, Does.Contain "\"/keyfile:$(ProjectRoot)/.keys/sample.snk\"")
        Assert.That(text, Does.Contain "$(NuGetPackageRoot)/netstandard.library/2.0.3")
        // the three sections, the markers, the typed pin, the alias and the packages
        Assert.That(text, Does.Contain "\"Evaluation\": {")
        Assert.That(text, Does.Contain "\"Compilation\": {")
        Assert.That(text, Does.Contain "\"Dependencies\": {")
        Assert.That(text, Does.Contain "\"@References\"")
        Assert.That(text, Does.Contain "\"SdkPin\": \"8.0.100 rollForward:latestFeature\"")
        Assert.That(text, Does.Contain "\"Alias\": \"ext\"")
        Assert.That(text, Does.Contain "\"Direct\": true, \"DependsOn\": [\"Baz.Qux\"]")
        Assert.That(text, Does.Not.Contain "\"Args\"")

        // and the same text again from the parsed lock: the file is deterministic
        Assert.That(Lock.parseWith roots text |> Lock.writeWith roots, Is.EqualTo text)

        Assert.That((Lock.entry "Sample" lock).Name, Is.EqualTo "Sample.Lib")
        Assert.That((Lock.entry "Sample.Lib" lock).Sources, Is.EqualTo [ root + "/src/Sample/A.cs" ])
        Assert.That((Lock.entry "Sample.Lib" lock).Output, Is.EqualTo (Some (root + "/src/Sample/obj/xake/net8.0/Sample.Lib.dll")))
        // the flat command line comes back exactly, alias and trailing switch included
        Assert.That((Lock.parseWith roots text |> Lock.entry "Sample.Lib").Args, Is.EqualTo args)

    [<Test>]
    member x.``a flat lock from an older Xake is refused with a clear message``() =
        let roots = Roots.builtin (Directory.GetCurrentDirectory())
        let flat = """{ "Framework": "net8.0", "Configuration": "Release", "Properties": {}, "Projects": [ { "Name": "X", "Args": [] } ] }"""
        let ex = Assert.Throws<System.Exception>(fun () -> Lock.parseWith roots flat |> ignore)
        Assert.That(ex.Message, Does.Contain "re-import")

    [<Test>]
    member x.``ofArgs factors a realistic msbuild command line and Args rebuilds it exactly``() =
        // the shape of a real page/dataengine lock: references and analyzers in the middle,
        // sources after them, a switch after the sources; an alias reference; a quoted list
        // item with a comma inside (netstandard's generated AssemblyAttributes.cs on /embed)
        let args =
            [ "/noconfig"; "/unsafe-"; "/nowarn:1701,1702"; "/fullpaths"; "/nostdlib+"
              "/define:TRACE;RELEASE;NETSTANDARD;NETSTANDARD2_0"
              "/highentropyva+"
              "/reference:/pkgs/a/1.0/lib/a.dll"
              "/reference:ext=/pkgs/b/1.0/lib/b.dll"
              "/reference:\"/pkgs/odd,name/1.0/lib/c.dll\""
              "/debug:portable"; "/out:/proj/obj/X.dll"; "/target:library"
              "/embed:\"/proj/obj/.NETStandard,Version=v2.0.AssemblyAttributes.cs\",/proj/obj/X.AssemblyInfo.cs"
              "/analyzerconfig:/proj/.editorconfig"
              "/analyzer:/sdk/analyzers/an1.dll"
              "/analyzer:/sdk/analyzers/an2.dll"
              "/proj/A.cs"; "/proj/obj/X.AssemblyInfo.cs"
              "/warnaserror+:NU1605" ]

        let compilation, references, analyzers = Lock.Compilation.ofArgs args

        Assert.That(compilation.Options, Is.EqualTo [
            "/noconfig"; "/unsafe-"; "/nowarn:1701,1702"; "/fullpaths"; "/nostdlib+"
            "@Defines"; "/highentropyva+"; "@References"
            "/debug:portable"; "/out:/proj/obj/X.dll"; "/target:library"
            "/embed:\"/proj/obj/.NETStandard,Version=v2.0.AssemblyAttributes.cs\",/proj/obj/X.AssemblyInfo.cs"
            "/analyzerconfig:/proj/.editorconfig"
            "@Analyzers"; "@Sources"; "/warnaserror+:NU1605" ])
        Assert.That(compilation.Defines, Is.EqualTo [ "TRACE"; "RELEASE"; "NETSTANDARD"; "NETSTANDARD2_0" ])
        Assert.That(compilation.Sources, Is.EqualTo [ "/proj/A.cs"; "/proj/obj/X.AssemblyInfo.cs" ])
        let expectedRefs : Lock.Reference list =
            [ { Path = "/pkgs/a/1.0/lib/a.dll"; Sha256 = ""; Alias = "" }
              { Path = "/pkgs/b/1.0/lib/b.dll"; Sha256 = ""; Alias = "ext" }
              { Path = "/pkgs/odd,name/1.0/lib/c.dll"; Sha256 = ""; Alias = "" } ]
        Assert.That(references, Is.EqualTo expectedRefs)
        Assert.That(analyzers |> List.map (fun a -> a.Path), Is.EqualTo [ "/sdk/analyzers/an1.dll"; "/sdk/analyzers/an2.dll" ])

        Assert.That(Lock.Compilation.args compilation references analyzers, Is.EqualTo args)

    [<Test>]
    member x.``the round-trip check fails readably when the structure cannot give the command line back``() =
        // two /define: switches are one section: the rebuild emits one switch, so the two
        // lists differ and the import must refuse the entry with a diff
        let args = [ "/define:A"; "/define:B"; "/proj/A.cs" ]
        let compilation, references, analyzers = Lock.Compilation.ofArgs args
        let rebuilt = Lock.Compilation.args compilation references analyzers
        Assert.That(rebuilt, Is.EqualTo [ "/define:A;B"; "/proj/A.cs" ])
        Assert.That(Lock.diffList args rebuilt, Is.EqualTo [ "- /define:A"; "- /define:B"; "+ /define:A;B" ])

    [<Test>]
    member x.``SdkPin round-trips through its text form``() =
        for pin in [ Lock.NoGlobalJson; Lock.Pinned "8.0.425"; Lock.RollsForward ("8.0.100", "latestFeature"); Lock.NoVersion "/repo/global.json" ] do
            Assert.That(Lock.parseSdkPin (Lock.sdkPinText pin), Is.EqualTo (Some pin))
        Assert.That(Lock.parseSdkPin "", Is.EqualTo None)

    [<Test>]
    member x.``packages are built from the restore graph with the cache's sha512``() =
        let cacheRoot = temp "packages-cache"
        let fooDir = Path.Combine (cacheRoot, "foo.bar", "1.2.3")
        Directory.CreateDirectory fooDir |> ignore
        File.WriteAllText (Path.Combine (fooDir, ".nupkg.metadata"), """{ "version": 2, "contentHash": "AAAA==", "source": "x" }""")
        try
            let assets : Nuget.Assets = {
                Packages = [ "Foo.Bar", "1.2.3"; "Baz.Qux", "4.5.6" ]
                Graph = [ ("Foo.Bar", "1.2.3"), ("Baz.Qux", "4.5.6"); ("MyProj", "1.0.0"), ("Baz.Qux", "4.5.6") ]
                Direct = [ "foo.bar" ]
                Framework = "netstandard2.0"
            }
            let expected : Lock.Package list =
                [ { Id = "Foo.Bar"; Version = "1.2.3"; Sha512 = "AAAA=="; Direct = true; DependsOn = [ "Baz.Qux" ] }
                  { Id = "Baz.Qux"; Version = "4.5.6"; Sha512 = ""; Direct = false; DependsOn = [] } ]
            Assert.That(Project.packages cacheRoot assets, Is.EqualTo expected)
        finally Directory.Delete (cacheRoot, true)

    [<Test>]
    member x.``an extra root tokenizes a sibling repository's paths and round-trips``() =
        let root = Directory.GetCurrentDirectory().Replace ('\\', '/')
        let extraRoots = [ "$(DataEngineRoot)", "/x/dataengine" ]
        let roots = Roots.withExtra (Directory.GetCurrentDirectory()) extraRoots

        let entry =
            entryOf "Sample.Lib" (root + "/src/Sample/Sample.csproj") (root + "/src/Sample")
                [ "/noconfig"; "/out:" + root + "/src/Sample/obj/xake/net8.0/Sample.Lib.dll"; "/x/dataengine/src/A.cs" ]
        let lock : Lock.Document = {
            Framework = "net8.0"; Configuration = "Release"; Properties = []; Entries = [ entry ]
        }

        let text = Lock.writeWith roots lock
        Assert.That(text, Does.Contain "$(DataEngineRoot)/src/A.cs")
        Assert.That(text, Does.Not.Contain "/x/dataengine")

        Assert.That(Lock.parseWith roots text, Is.EqualTo lock)
        Assert.That((Lock.parseWith roots text).Entries.[0].Sources, Is.EqualTo [ "/x/dataengine/src/A.cs" ])

    [<Test>]
    member x.``a declared root must be a well-formed, non-built-in, absolute token``() =
        let cwd = Directory.GetCurrentDirectory()
        Assert.Throws<System.Exception>(fun () -> Roots.withExtra cwd [ "DataEngineRoot", "/x/dataengine" ] |> ignore) |> ignore
        Assert.Throws<System.Exception>(fun () -> Roots.withExtra cwd [ "$(ProjectRoot)", "/x/dataengine" ] |> ignore) |> ignore
        Assert.Throws<System.Exception>(fun () -> Roots.withExtra cwd [ "$(DataEngineRoot)", "relative/dataengine" ] |> ignore) |> ignore

    [<Test>]
    member x.``lists the files a preprocessed project imported``() =
        let preprocessed = """<!--
============================================================================================================================================
/repo/src/DataEngine/DataEngine.csproj
============================================================================================================================================
-->
<Project ToolsVersion="15.0" DefaultTargets="Build">
  <!--
============================================================================================================================================
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk">
  This import was added implicitly because the Project element's Sdk attribute specified "Microsoft.NET.Sdk".

/dotnet/sdk/8.0.425/Sdks/Microsoft.NET.Sdk/Sdk/Sdk.props
============================================================================================================================================
-->
  <PropertyGroup>
    <_Placeholder>========</_Placeholder>
  </PropertyGroup>
  <!--
============================================================================================================================================
  <Import Project="$(DirectoryBuildPropsPath)" Condition="'$(ImportDirectoryBuildProps)' == 'true' and exists('$(DirectoryBuildPropsPath)')">

/repo/Directory.Build.props
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  <Import Project="$(Brand).props">

/repo/src/MESCIUS.props
============================================================================================================================================
-->
  <!--
============================================================================================================================================
  </Import>

/repo/src/MESCIUS.props
============================================================================================================================================
-->
</Project>
"""
        Assert.That(Project.parseImports preprocessed, Is.EqualTo [
            "/repo/src/DataEngine/DataEngine.csproj"
            "/dotnet/sdk/8.0.425/Sdks/Microsoft.NET.Sdk/Sdk/Sdk.props"
            "/repo/Directory.Build.props"
            "/repo/src/MESCIUS.props" ])

    [<Test>]
    member x.``builds the lock entry from what msbuild answered``() =
        let dir = temp "import"
        Directory.CreateDirectory (Path.Combine (dir, "obj", "xake", "net8.0", "X")) |> ignore
        let generated = Path.Combine (dir, "obj", "xake", "net8.0", "X", "Sample.AssemblyInfo.cs")
        File.WriteAllText (generated, "[assembly: A]")
        let key = Path.Combine (dir, "key.snk")
        File.WriteAllBytes (key, [| 1uy; 2uy; 3uy |])
        let dirSlash = dir.Replace ('\\', '/')

        let result = temp "import.msbuild"
        File.WriteAllText (result, sprintf """{
          "Properties": {
            "AssemblyName": "Sample.Lib",
            "MSBuildProjectFullPath": "%s/Sample.csproj",
            "MSBuildProjectDirectory": "%s",
            "IntermediateOutputPath": "obj/xake/net8.0/X/",
            "CscToolPath": "",
            "CscToolExe": "",
            "RoslynTargetsPath": "/dotnet/sdk/8.0.425/Roslyn",
            "NETCoreSdkVersion": "8.0.425",
            "NetCoreRoot": "/dotnet/",
            "LangVersion": "12"
          },
          "Items": {
            "CscCommandLineArgs": [
              { "Identity": "/noconfig" },
              { "Identity": "/keyfile:%s" },
              { "Identity": "/reference:/dotnet/packs/ref/netstandard.dll" },
              { "Identity": "/reference:%s/../Other/bin/Other.dll" },
              { "Identity": "/analyzer:/dotnet/sdk/8.0.425/Sdks/Microsoft.NET.Sdk/targets/../analyzers/an.dll" },
              { "Identity": "/out:obj/xake/net8.0/X/Sample.Lib.dll" },
              { "Identity": "/resource:obj/xake/net8.0/X/Sample.Strings.resources,Sample.Strings" },
              { "Identity": "obj/xake/net8.0/X/Sample.AssemblyInfo.cs" },
              { "Identity": "A.cs" }
            ],
            "ProjectReference": [
              { "Identity": "../Other/Other.csproj", "FullPath": "/cwd/Other/Other.csproj" }
            ],
            "EmbeddedResource": [
              { "Identity": "Strings.resx", "FullPath": "%s/Strings.resx", "OutputResource": "obj/xake/net8.0/X/Sample.Strings.resources", "ManifestResourceName": "Sample.Strings" },
              { "Identity": "logo.png", "FullPath": "%s/logo.png" }
            ]
          }
        }""" dirSlash dirSlash key dirSlash dirSlash dirSlash)

        let imports =
            [ dirSlash + "/Sample.csproj"
              "/dotnet/sdk/8.0.425/Sdks/Microsoft.NET.Sdk/Sdk/Sdk.props"
              dirSlash + "/obj/Sample.csproj.nuget.g.props"
              dirSlash + "/Directory.Build.props" ]
        let packages : Lock.Package list = [ { Id = "Foo.Bar"; Version = "1.2.3"; Sha512 = "AAAA=="; Direct = true; DependsOn = [] } ]
        let entry = Project.parseImport result imports (Lock.Pinned "8.0.425") packages

        Assert.That(entry.Name, Is.EqualTo "Sample.Lib")
        Assert.That(entry.Compilation.Directory, Is.EqualTo dirSlash)
        Assert.That(entry.Dependencies.Compiler.Path, Is.EqualTo "/dotnet/sdk/8.0.425/Roslyn/bincore/csc.dll")
        // a compiler that does not exist on this machine has no version to read
        Assert.That(entry.Dependencies.Compiler.Version, Is.EqualTo "")
        Assert.That(entry.Evaluation.Sdk, Is.EqualTo "8.0.425")
        // relative arguments resolved against the project directory, `..` folded
        Assert.That(entry.Args, Is.EqualTo [
            "/noconfig"
            "/keyfile:" + key.Replace ('\\', '/')
            "/reference:/dotnet/packs/ref/netstandard.dll"
            "/reference:" + (Path.GetFullPath (Path.Combine (dir, "..", "Other", "bin", "Other.dll"))).Replace ('\\', '/')
            "/analyzer:/dotnet/sdk/8.0.425/Sdks/Microsoft.NET.Sdk/analyzers/an.dll"
            "/out:" + dirSlash + "/obj/xake/net8.0/X/Sample.Lib.dll"
            "/resource:" + dirSlash + "/obj/xake/net8.0/X/Sample.Strings.resources,Sample.Strings"
            dirSlash + "/obj/xake/net8.0/X/Sample.AssemblyInfo.cs"
            dirSlash + "/A.cs" ])
        Assert.That(entry.Sources, Is.EqualTo [ dirSlash + "/obj/xake/net8.0/X/Sample.AssemblyInfo.cs"; dirSlash + "/A.cs" ])
        // the structured form: the references and the analyzer are factored out of the options
        Assert.That(entry.Compilation.Options, Is.EqualTo [
            "/noconfig"
            "/keyfile:" + key.Replace ('\\', '/')
            "@References"
            "@Analyzers"
            "/out:" + dirSlash + "/obj/xake/net8.0/X/Sample.Lib.dll"
            "/resource:" + dirSlash + "/obj/xake/net8.0/X/Sample.Strings.resources,Sample.Strings"
            "@Sources" ])
        Assert.That(entry.Compilation.Defines, Is.Empty)
        // a reference that does not exist yet (a project reference's output) has no hash
        Assert.That(entry.Dependencies.References |> List.map (fun r -> r.Sha256 = ""), Is.EqualTo [ true; true ])
        Assert.That(entry.Dependencies.Analyzers |> List.map (fun a -> a.Path), Is.EqualTo [ "/dotnet/sdk/8.0.425/Sdks/Microsoft.NET.Sdk/analyzers/an.dll" ])
        Assert.That(entry.Dependencies.Packages, Is.EqualTo packages)
        // `FullPath` is deliberately the wrong (cwd-resolved) spelling: `Identity` combined with
        // the project directory wins, `..` folded -- the same fix already applied to resx above
        Assert.That(entry.Evaluation.ProjectRefs,
                    Is.EqualTo [ (Path.GetFullPath (Path.Combine (dir, "..", "Other", "Other.csproj"))).Replace ('\\', '/') ])
        // the SDK's own files are the SDK version; restore's are the import's own
        Assert.That(entry.Evaluation.Imports |> List.map (fun i -> i.Path), Is.EqualTo [ dirSlash + "/Sample.csproj"; dirSlash + "/Directory.Build.props" ])
        // what msbuild generated into obj travels with the lock
        Assert.That(entry.Compilation.Generated, Is.EqualTo [ dirSlash + "/obj/xake/net8.0/X/Sample.AssemblyInfo.cs", "[assembly: A]" ])
        Assert.That(entry.Evaluation.Properties.["LangVersion"], Is.EqualTo "12")
        // the resx compiles to what OutputResource named, absolute; the non-resx
        // EmbeddedResource (logo.png) needs nothing recorded
        Assert.That(entry.Compilation.Resources, Is.EqualTo [
            dirSlash + "/Strings.resx", dirSlash + "/obj/xake/net8.0/X/Sample.Strings.resources" ])
        // the pin is typed now, not a Properties string
        Assert.That(entry.Evaluation.SdkPin, Is.EqualTo (Some (Lock.Pinned "8.0.425")))
        Assert.That(entry.Evaluation.Properties.ContainsKey "SdkPin", Is.False)
        Assert.That(entry.Evaluation.Properties.ContainsKey "NETCoreSdkVersion", Is.False)

    [<Test>]
    member x.``ProjectReference is resolved from Identity, not the cwd-resolved FullPath``() =
        let dir = temp "import-projectref"
        Directory.CreateDirectory (Path.Combine (dir, "obj", "xake", "net8.0", "X")) |> ignore
        let dirSlash = dir.Replace ('\\', '/')

        let result = temp "import-projectref.msbuild"
        File.WriteAllText (result, sprintf """{
          "Properties": {
            "AssemblyName": "Sample.Lib",
            "MSBuildProjectFullPath": "%s/Sample.csproj",
            "MSBuildProjectDirectory": "%s",
            "IntermediateOutputPath": "obj/xake/net8.0/X/",
            "NETCoreSdkVersion": "8.0.425",
            "NetCoreRoot": "/dotnet/"
          },
          "Items": {
            "CscCommandLineArgs": [
              { "Identity": "/noconfig" },
              { "Identity": "/out:obj/xake/net8.0/X/Sample.Lib.dll" }
            ],
            "ProjectReference": [
              { "Identity": "../../Rendering/Rendering.csproj", "FullPath": "/wherever/the/process/cwd/put/it/Rendering.csproj" }
            ]
          }
        }""" dirSlash dirSlash)

        let entry = Project.parseImport result [] (Lock.Pinned "8.0.425") []

        // `FullPath` here is the wrong, cwd-resolved spelling -- `Identity` combined with the
        // project directory must win, `..` folded
        Assert.That(entry.Evaluation.ProjectRefs,
                    Is.EqualTo [ (Path.GetFullPath (Path.Combine (dir, "..", "..", "Rendering", "Rendering.csproj"))).Replace ('\\', '/') ])

    [<Test>]
    member x.``sdkPin finds no global.json``() =
        let dir = temp "sdkpin-none"
        Directory.CreateDirectory dir |> ignore
        try Assert.That(Project.sdkPin dir, Is.EqualTo Lock.NoGlobalJson)
        finally Directory.Delete (dir, true)

    [<Test>]
    member x.``sdkPin reads rollForward from a global.json two levels up``() =
        let root = temp "sdkpin-rollforward"
        let dir = Path.Combine (root, "a", "b")
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText (Path.Combine (root, "global.json"), """{"sdk":{"version":"8.0.100","rollForward":"latestFeature"}}""")
        try Assert.That(Project.sdkPin dir, Is.EqualTo (Lock.RollsForward ("8.0.100", "latestFeature")))
        finally Directory.Delete (root, true)

    [<Test>]
    member x.``sdkPin recognizes rollForward disable as pinned``() =
        let dir = temp "sdkpin-pinned"
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText (Path.Combine (dir, "global.json"), """{"sdk":{"version":"8.0.425","rollForward":"disable"}}""")
        try Assert.That(Project.sdkPin dir, Is.EqualTo (Lock.Pinned "8.0.425"))
        finally Directory.Delete (dir, true)

    [<Test>]
    member x.``sdkPin without rollForward defaults to latestPatch``() =
        let dir = temp "sdkpin-defaultpolicy"
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText (Path.Combine (dir, "global.json"), """{"sdk":{"version":"8.0.425"}}""")
        try Assert.That(Project.sdkPin dir, Is.EqualTo (Lock.RollsForward ("8.0.425", "latestPatch")))
        finally Directory.Delete (dir, true)

    [<Test>]
    member x.``sdkPin with no version``() =
        let dir = temp "sdkpin-noversion"
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText (Path.Combine (dir, "global.json"), """{"sdk":{}}""")
        try
            match Project.sdkPin dir with
            | Lock.NoVersion _ -> ()
            | other -> Assert.Fail (sprintf "expected NoVersion, got %A" other)
        finally Directory.Delete (dir, true)

    [<Test>]
    member x.``tokenizeRevision replaces the sha in Generated content, Options, Defines and Properties, and leaves other text``() =
        let sha = "abc123def456abc123def456abc123def456abc"
        let plain = entryOf "Sample" "/a/Sample.csproj" "/a" [ "/sourcelink:/a/obj/sourcelink.json"; sprintf "/define:VERSION_%s" sha; sprintf "/pathmap:/%s=/_/" sha ]
        let entry =
            { plain with
                Evaluation = { plain.Evaluation with Properties = Map.ofList [ "Version", sprintf "1.0.0+%s" sha; "AssemblyName", "Sample" ] }
                Compilation = { plain.Compilation with Generated = [ "/a/obj/sourcelink.json", sprintf "{\"documents\":{\"/x/*\":\"https://h/src/%s/*\"}}" sha ] } }

        let tokenized = Project.tokenizeRevision sha entry

        Assert.That(tokenized.Compilation.Generated, Is.EqualTo [
            "/a/obj/sourcelink.json", "{\"documents\":{\"/x/*\":\"https://h/src/$(SourceRevisionId)/*\"}}" ])
        Assert.That(tokenized.Compilation.Options, Is.EqualTo [ "/sourcelink:/a/obj/sourcelink.json"; "@Defines"; "/pathmap:/$(SourceRevisionId)=/_/" ])
        Assert.That(tokenized.Compilation.Defines, Is.EqualTo [ "VERSION_$(SourceRevisionId)" ])
        Assert.That(tokenized.Args, Is.EqualTo [ "/sourcelink:/a/obj/sourcelink.json"; "/define:VERSION_$(SourceRevisionId)"; "/pathmap:/$(SourceRevisionId)=/_/" ])
        Assert.That(tokenized.Evaluation.Properties.["Version"], Is.EqualTo "1.0.0+$(SourceRevisionId)")
        Assert.That(tokenized.Evaluation.Properties.["AssemblyName"], Is.EqualTo "Sample")

    [<Test>]
    member x.``tokenizeRevision is a no-op when the sha is empty``() =
        let entry = entryOf "Sample" "/a/Sample.csproj" "/a" [ "/define:X" ]
        Assert.That(Project.tokenizeRevision "" entry, Is.EqualTo entry)

    [<Test>]
    member x.``Git headSha resolves a symbolic HEAD via a loose ref``() =
        let dir = temp "git-loose"
        let gitDir = Path.Combine (dir, ".git")
        Directory.CreateDirectory (Path.Combine (gitDir, "refs", "heads")) |> ignore
        File.WriteAllText (Path.Combine (gitDir, "HEAD"), "ref: refs/heads/main\n")
        let sha = "1111111111111111111111111111111111111111"
        File.WriteAllText (Path.Combine (gitDir, "refs", "heads", "main"), sha + "\n")
        try
            Assert.That(Git.headSha dir, Is.EqualTo (Some sha))
            Assert.That(Git.headFiles dir, Is.EquivalentTo [
                Path.Combine (gitDir, "HEAD"); Path.Combine (gitDir, "refs", "heads", "main") ])
        finally Directory.Delete (dir, true)

    [<Test>]
    member x.``Git headSha resolves a symbolic HEAD via packed-refs when there is no loose ref``() =
        let dir = temp "git-packed"
        let gitDir = Path.Combine (dir, ".git")
        Directory.CreateDirectory gitDir |> ignore
        File.WriteAllText (Path.Combine (gitDir, "HEAD"), "ref: refs/heads/main\n")
        let sha = "2222222222222222222222222222222222222222"
        File.WriteAllText (Path.Combine (gitDir, "packed-refs"),
            sprintf "# pack-refs with: peeled fully-peeled sorted\n%s refs/heads/main\n" sha)
        try
            Assert.That(Git.headSha dir, Is.EqualTo (Some sha))
            Assert.That(Git.headFiles dir, Is.EquivalentTo [
                Path.Combine (gitDir, "HEAD"); Path.Combine (gitDir, "packed-refs") ])
        finally Directory.Delete (dir, true)

    [<Test>]
    member x.``Git headSha reads a detached HEAD directly``() =
        let dir = temp "git-detached"
        let gitDir = Path.Combine (dir, ".git")
        Directory.CreateDirectory gitDir |> ignore
        let sha = "3333333333333333333333333333333333333333"
        File.WriteAllText (Path.Combine (gitDir, "HEAD"), sha + "\n")
        try
            Assert.That(Git.headSha dir, Is.EqualTo (Some sha))
            Assert.That(Git.headFiles dir, Is.EqualTo [ Path.Combine (gitDir, "HEAD") ])
        finally Directory.Delete (dir, true)

    [<Test>]
    member x.``Git headSha resolves a linked worktree's .git file``() =
        let repo = temp "git-worktree-main"
        let wtCheckout = temp "git-worktree-checkout"
        let mainGitDir = Path.Combine (repo, ".git")
        let wtGitDir = Path.Combine (mainGitDir, "worktrees", "wt")
        Directory.CreateDirectory (Path.Combine (mainGitDir, "refs", "heads")) |> ignore
        Directory.CreateDirectory wtGitDir |> ignore
        Directory.CreateDirectory wtCheckout |> ignore
        let sha = "4444444444444444444444444444444444444444"
        File.WriteAllText (Path.Combine (mainGitDir, "refs", "heads", "main"), sha + "\n")
        File.WriteAllText (Path.Combine (wtGitDir, "HEAD"), "ref: refs/heads/main\n")
        File.WriteAllText (Path.Combine (wtGitDir, "commondir"), "../..\n")
        File.WriteAllText (Path.Combine (wtCheckout, ".git"), sprintf "gitdir: %s\n" wtGitDir)
        try
            Assert.That(Git.headSha wtCheckout, Is.EqualTo (Some sha))
            Assert.That(Git.headFiles wtCheckout, Is.EquivalentTo [
                Path.Combine (wtGitDir, "HEAD"); Path.Combine (mainGitDir, "refs", "heads", "main") ])
        finally
            Directory.Delete (repo, true)
            Directory.Delete (wtCheckout, true)

    [<Test>]
    member x.``Git headSha and headFiles find nothing outside a repository``() =
        let dir = temp "git-none"
        Directory.CreateDirectory dir |> ignore
        try
            Assert.That(Git.headSha dir, Is.EqualTo None)
            Assert.That(Git.headFiles dir, Is.Empty)
        finally Directory.Delete (dir, true)

    [<Test>]
    member x.``withProjectLock serializes two imports of the same project path``() =
        let enter, leave, peak = meter ()
        let runCount = ref 0
        let sameProject = temp "lock-same" </> "Shared.csproj"

        let body = recipe {
            enter ()
            do! Async.Sleep 80
            leave ()
            Interlocked.Increment runCount |> ignore
        }

        let b = engineBuilder 8   // plenty of CPU slots; the lock, not the pool, must serialize
        let eng = b {
            rules [ for i in 1..2 -> (sprintf "t%d" i) => Project.withProjectLock sameProject body ]
            start
        }

        let tasks = [| for i in 1..2 -> eng.Demand (sprintf "t%d" i) |]
        Task.WaitAll tasks
        eng.StopAsync().Wait()

        Assert.AreEqual(1, !peak, "two imports of the same project path must never overlap")
        Assert.AreEqual(2, !runCount, "both must still run")

    [<Test>]
    member x.``withProjectLock lets two different project paths import concurrently``() =
        let enter, leave, peak = meter ()
        let projectFor i = temp (sprintf "lock-distinct-%d" i) </> "Distinct.csproj"

        let bodyFor i = recipe {
            enter ()
            do! Async.Sleep 150
            leave ()
        }

        let b = engineBuilder 8
        let eng = b {
            rules [ for i in 1..2 -> (sprintf "t%d" i) => Project.withProjectLock (projectFor i) (bodyFor i) ]
            start
        }

        let tasks = [| for i in 1..2 -> eng.Demand (sprintf "t%d" i) |]
        Task.WaitAll tasks
        eng.StopAsync().Wait()

        Assert.AreEqual(2, !peak, "different project paths must not serialize against each other")

    [<Test>]
    member x.``withProjectLock keys the same path the same way regardless of case on Windows, or exactly on Unix``() =
        let baseDir = temp "lock-case"
        let lower = baseDir </> "same.csproj"
        let upper = baseDir </> "SAME.csproj"

        let enter, leave, peak = meter ()

        let body = recipe {
            enter ()
            do! Async.Sleep 80
            leave ()
        }

        let b = engineBuilder 8
        let eng = b {
            rules [
                "t1" => Project.withProjectLock lower body
                "t2" => Project.withProjectLock upper body
            ]
            start
        }

        let tasks = [| eng.Demand "t1"; eng.Demand "t2" |]
        Task.WaitAll tasks
        eng.StopAsync().Wait()

        let expectedPeak = if Env.isUnix then 2 else 1
        Assert.AreEqual(expectedPeak, !peak,
            "on Unix paths differing only by case are different projects; on Windows they are the same one")
