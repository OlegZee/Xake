namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Dotnet

/// The pieces of `Project.import` that do not need msbuild: reading the compiler's command
/// line, the lock's round trip, the import list of a preprocessed project.
[<TestFixture>]
type ``Project import``() =

    let temp name = Path.Combine (Path.GetTempPath(), "xake-test-" + name)

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
        let packages = Fsproj.roots () |> List.find (fst >> (=) "$(NuGetPackageRoot)") |> snd
        let root = Directory.GetCurrentDirectory().Replace ('\\', '/')

        let project : Lock.Project = {
            Name = "Sample.Lib"
            Project = root + "/src/Sample/Sample.csproj"
            Directory = root + "/src/Sample"
            Compiler = { Tool = "csc"; Path = "/dotnet/sdk/8.0.425/Roslyn/bincore/csc.dll"; Sha256 = "ab01"; Sdk = "8.0.425" }
            Args =
                [ "/noconfig"; "/define:TRACE;RELEASE"
                  "/reference:" + packages + "/netstandard.library/2.0.3/build/netstandard2.0/ref/netstandard.dll"
                  "/keyfile:" + root + "/.keys/sample.snk"
                  sprintf "/pathmap:%s=/_/" root
                  "/out:" + root + "/src/Sample/obj/xake/net8.0/Sample.Lib.dll"
                  root + "/src/Sample/A.cs" ]
            References = [ { Path = packages + "/netstandard.library/2.0.3/build/netstandard2.0/ref/netstandard.dll"; Sha256 = "cd02" }
                           { Path = root + "/src/Other/bin/Release/Other.dll"; Sha256 = "" } ]
            Analyzers = [ { Path = "/dotnet/sdk/8.0.425/Sdks/Microsoft.NET.Sdk/analyzers/an.dll"; Sha256 = "ef03" } ]
            ProjectRefs = [ root + "/src/Other/Other.csproj" ]
            Imports = [ { Path = root + "/Directory.Build.props"; Sha256 = "0a0b" } ]
            Generated = [ root + "/src/Sample/obj/xake/net8.0/Sample.AssemblyInfo.cs", "// <autogenerated />\r\n[assembly: A(\"x\")]\n" ]
            Resources = [ root + "/src/Sample/Strings.resx", root + "/src/Sample/obj/xake/net8.0/Sample.Strings.resources" ]
            Properties = Map.ofList [ "AssemblyName", "Sample.Lib"; "TargetPath", root + "/src/Sample/bin/Sample.Lib.dll" ]
        }
        let lock : Lock.File = {
            Framework = "net8.0"; Configuration = "Release"; Properties = [ "Brand", "X" ]; Projects = [ project ]
        }

        let text = Lock.write lock
        Assert.That(Lock.parse text, Is.EqualTo lock)

        // nothing machine-specific survives in the file: the checkout and the package cache
        // are tokens, and a root inside a value (`/pathmap:`) is one too
        Assert.That(text, Does.Not.Contain root)
        Assert.That(text, Does.Not.Contain packages)
        Assert.That(text, Does.Contain "/pathmap:$(ProjectRoot)=/_/")
        Assert.That(text, Does.Contain "\"/keyfile:$(ProjectRoot)/.keys/sample.snk\"")
        Assert.That(text, Does.Contain "$(NuGetPackageRoot)/netstandard.library/2.0.3")

        // and the same text again from the parsed lock: the file is deterministic
        Assert.That(Lock.parse text |> Lock.write, Is.EqualTo text)

        Assert.That((Lock.project "Sample" lock).Name, Is.EqualTo "Sample.Lib")
        Assert.That((Lock.project "Sample.Lib" lock).Sources, Is.EqualTo [ root + "/src/Sample/A.cs" ])
        Assert.That((Lock.project "Sample.Lib" lock).Output, Is.EqualTo (Some (root + "/src/Sample/obj/xake/net8.0/Sample.Lib.dll")))

    [<Test>]
    member x.``an extra root tokenizes a sibling repository's paths and round-trips``() =
        let root = Directory.GetCurrentDirectory().Replace ('\\', '/')
        let extraRoots = [ "$(DataEngineRoot)", "/x/dataengine" ]
        let roots = Fsproj.withRoots extraRoots

        let project : Lock.Project = {
            Name = "Sample.Lib"
            Project = root + "/src/Sample/Sample.csproj"
            Directory = root + "/src/Sample"
            Compiler = { Tool = "csc"; Path = "/dotnet/sdk/8.0.425/Roslyn/bincore/csc.dll"; Sha256 = "ab01"; Sdk = "8.0.425" }
            Args = [ "/noconfig"; "/out:" + root + "/src/Sample/obj/xake/net8.0/Sample.Lib.dll"; "/x/dataengine/src/A.cs" ]
            References = []
            Analyzers = []
            ProjectRefs = []
            Imports = []
            Generated = []
            Resources = []
            Properties = Map.ofList [ "AssemblyName", "Sample.Lib" ]
        }
        let lock : Lock.File = {
            Framework = "net8.0"; Configuration = "Release"; Properties = []; Projects = [ project ]
        }

        let text = Lock.writeWith roots lock
        Assert.That(text, Does.Contain "$(DataEngineRoot)/src/A.cs")
        Assert.That(text, Does.Not.Contain "/x/dataengine")

        Assert.That(Lock.parseWith roots text, Is.EqualTo lock)
        Assert.That((Lock.parseWith roots text).Projects.[0].Sources, Is.EqualTo [ "/x/dataengine/src/A.cs" ])

    [<Test>]
    member x.``a declared root must be a well-formed, non-built-in, absolute token``() =
        Assert.Throws<System.Exception>(fun () -> Fsproj.withRoots [ "DataEngineRoot", "/x/dataengine" ] |> ignore) |> ignore
        Assert.Throws<System.Exception>(fun () -> Fsproj.withRoots [ "$(ProjectRoot)", "/x/dataengine" ] |> ignore) |> ignore
        Assert.Throws<System.Exception>(fun () -> Fsproj.withRoots [ "$(DataEngineRoot)", "relative/dataengine" ] |> ignore) |> ignore

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
              { "Identity": "../Other/Other.csproj", "FullPath": "%s/../Other/Other.csproj" }
            ],
            "EmbeddedResource": [
              { "Identity": "Strings.resx", "FullPath": "%s/Strings.resx", "OutputResource": "obj/xake/net8.0/X/Sample.Strings.resources", "ManifestResourceName": "Sample.Strings" },
              { "Identity": "logo.png", "FullPath": "%s/logo.png" }
            ]
          }
        }""" dirSlash dirSlash key dirSlash dirSlash dirSlash dirSlash)

        let imports =
            [ dirSlash + "/Sample.csproj"
              "/dotnet/sdk/8.0.425/Sdks/Microsoft.NET.Sdk/Sdk/Sdk.props"
              dirSlash + "/obj/Sample.csproj.nuget.g.props"
              dirSlash + "/Directory.Build.props" ]
        let entry = Project.parseImport result imports (Project.Pinned "8.0.425")

        Assert.That(entry.Name, Is.EqualTo "Sample.Lib")
        Assert.That(entry.Directory, Is.EqualTo dirSlash)
        Assert.That(entry.Compiler.Path, Is.EqualTo "/dotnet/sdk/8.0.425/Roslyn/bincore/csc.dll")
        Assert.That(entry.Compiler.Sdk, Is.EqualTo "8.0.425")
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
        // a reference that does not exist yet (a project reference's output) has no hash
        Assert.That(entry.References |> List.map (fun r -> r.Sha256 = ""), Is.EqualTo [ true; true ])
        Assert.That(entry.ProjectRefs, Is.EqualTo [ dirSlash + "/../Other/Other.csproj" ])
        // the SDK's own files are the SDK version; restore's are the import's own
        Assert.That(entry.Imports |> List.map (fun i -> i.Path), Is.EqualTo [ dirSlash + "/Sample.csproj"; dirSlash + "/Directory.Build.props" ])
        // what msbuild generated into obj travels with the lock
        Assert.That(entry.Generated, Is.EqualTo [ dirSlash + "/obj/xake/net8.0/X/Sample.AssemblyInfo.cs", "[assembly: A]" ])
        Assert.That(entry.Properties.["LangVersion"], Is.EqualTo "12")
        // the resx compiles to what OutputResource named, absolute; the non-resx
        // EmbeddedResource (logo.png) needs nothing recorded
        Assert.That(entry.Resources, Is.EqualTo [
            dirSlash + "/Strings.resx", dirSlash + "/obj/xake/net8.0/X/Sample.Strings.resources" ])
        Assert.That(entry.Properties.["SdkPin"], Is.EqualTo "exact 8.0.425")

    [<Test>]
    member x.``sdkPin finds no global.json``() =
        let dir = temp "sdkpin-none"
        Directory.CreateDirectory dir |> ignore
        try Assert.That(Project.sdkPin dir, Is.EqualTo Project.NoGlobalJson)
        finally Directory.Delete (dir, true)

    [<Test>]
    member x.``sdkPin reads rollForward from a global.json two levels up``() =
        let root = temp "sdkpin-rollforward"
        let dir = Path.Combine (root, "a", "b")
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText (Path.Combine (root, "global.json"), """{"sdk":{"version":"8.0.100","rollForward":"latestFeature"}}""")
        try Assert.That(Project.sdkPin dir, Is.EqualTo (Project.RollsForward ("8.0.100", "latestFeature")))
        finally Directory.Delete (root, true)

    [<Test>]
    member x.``sdkPin recognizes rollForward disable as pinned``() =
        let dir = temp "sdkpin-pinned"
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText (Path.Combine (dir, "global.json"), """{"sdk":{"version":"8.0.425","rollForward":"disable"}}""")
        try Assert.That(Project.sdkPin dir, Is.EqualTo (Project.Pinned "8.0.425"))
        finally Directory.Delete (dir, true)

    [<Test>]
    member x.``sdkPin without rollForward defaults to latestPatch``() =
        let dir = temp "sdkpin-defaultpolicy"
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText (Path.Combine (dir, "global.json"), """{"sdk":{"version":"8.0.425"}}""")
        try Assert.That(Project.sdkPin dir, Is.EqualTo (Project.RollsForward ("8.0.425", "latestPatch")))
        finally Directory.Delete (dir, true)

    [<Test>]
    member x.``sdkPin with no version``() =
        let dir = temp "sdkpin-noversion"
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText (Path.Combine (dir, "global.json"), """{"sdk":{}}""")
        try
            match Project.sdkPin dir with
            | Project.NoVersion _ -> ()
            | other -> Assert.Fail (sprintf "expected NoVersion, got %A" other)
        finally Directory.Delete (dir, true)

    [<Test>]
    member x.``tokenizeRevision replaces the sha in Generated content, Args and Properties, and leaves other text``() =
        let sha = "abc123def456abc123def456abc123def456abc"
        let project : Lock.Project = {
            Name = "Sample"
            Project = "/a/Sample.csproj"
            Directory = "/a"
            Compiler = { Tool = "csc"; Path = "/dotnet/csc.dll"; Sha256 = ""; Sdk = "8.0.0" }
            Args = [ "/sourcelink:/a/obj/sourcelink.json"; sprintf "/define:VERSION_%s" sha ]
            References = []
            Analyzers = []
            ProjectRefs = []
            Imports = []
            Generated = [ "/a/obj/sourcelink.json", sprintf "{\"documents\":{\"/x/*\":\"https://h/src/%s/*\"}}" sha ]
            Resources = []
            Properties = Map.ofList [ "Version", sprintf "1.0.0+%s" sha; "AssemblyName", "Sample" ]
        }

        let tokenized = Project.tokenizeRevision sha project

        Assert.That(tokenized.Generated, Is.EqualTo [
            "/a/obj/sourcelink.json", "{\"documents\":{\"/x/*\":\"https://h/src/$(SourceRevisionId)/*\"}}" ])
        Assert.That(tokenized.Args, Is.EqualTo [ "/sourcelink:/a/obj/sourcelink.json"; "/define:VERSION_$(SourceRevisionId)" ])
        Assert.That(tokenized.Properties.["Version"], Is.EqualTo "1.0.0+$(SourceRevisionId)")
        Assert.That(tokenized.Properties.["AssemblyName"], Is.EqualTo "Sample")

    [<Test>]
    member x.``tokenizeRevision is a no-op when the sha is empty``() =
        let project : Lock.Project = {
            Name = "Sample"; Project = "/a/Sample.csproj"; Directory = "/a"
            Compiler = { Tool = "csc"; Path = "/dotnet/csc.dll"; Sha256 = ""; Sdk = "8.0.0" }
            Args = [ "/define:X" ]; References = []; Analyzers = []; ProjectRefs = []
            Imports = []; Generated = []; Resources = []; Properties = Map.ofList [ "Version", "1.0.0" ]
        }
        Assert.That(Project.tokenizeRevision "" project, Is.EqualTo project)

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
