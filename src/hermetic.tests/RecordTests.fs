namespace Tests

open System
open System.IO
open System.Diagnostics
open NUnit.Framework

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// The portable names of a recorded entry (`Lock.Identity`), the lock format 2 that writes
/// them, and the host-string check of `Import.record`. Pure: no msbuild.
[<TestFixture>]
type ``Recorded lock identities``() =

    let roots =
        [ "$(NuGetPackageRoot)", "/r/pkgs"; "$(ProjectRoot)", "/r/proj"; "$(DotnetRoot)", "/r/dotnet"; "$(Sibling)", "/r/sibling" ]
    let lib : Lock.ProjectOutput = { Name = "Lib"; Configuration = "Release"; Framework = "netstandard2.0"; Path = "/r/proj/out/Release/netstandard2.0/Lib/Lib.dll" }
    let context : Lock.Identity.Context = { Roots = roots; Sdk = "8.0.425"; Projects = [ lib ] }

    [<Test>]
    member x.``a file is named by its package, pack, SDK or project, else by its root``() =
        let name = Lock.Identity.ofPath context
        Assert.That(name "/r/pkgs/system.memory/4.5.5/lib/net461/System.Memory.dll", Is.EqualTo (Some "pkg:system.memory/4.5.5/lib/net461/System.Memory.dll"))
        Assert.That(name "/r/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.11/ref/net8.0/System.Runtime.dll",
                    Is.EqualTo (Some "pack:microsoft.netcore.app.ref/8.0.11/ref/net8.0/System.Runtime.dll"))
        // the same pack downloaded into the package folder: the same identity
        Assert.That(name "/r/pkgs/microsoft.netcore.app.ref/8.0.11/ref/net8.0/System.Runtime.dll",
                    Is.EqualTo (Some "pack:microsoft.netcore.app.ref/8.0.11/ref/net8.0/System.Runtime.dll"))
        Assert.That(name "/r/dotnet/sdk/8.0.425/Roslyn/bincore/csc.dll", Is.EqualTo (Some "sdk:Roslyn/bincore/csc.dll"))
        Assert.That(name "/r/proj/out/Release/netstandard2.0/Lib/Lib.dll", Is.EqualTo (Some "project:Lib"))
        Assert.That(name "/r/proj/lib/Native.dll", Is.EqualTo (Some "$(ProjectRoot)/lib/Native.dll"))
        Assert.That(name "/r/sibling/bin/Tool.dll", Is.EqualTo (Some "$(Sibling)/bin/Tool.dll"))
        // another SDK than the entry's, a file of the .NET root outside packs and sdk, a file anywhere else
        Assert.That(name "/r/dotnet/sdk/9.0.100/Roslyn/bincore/csc.dll", Is.EqualTo None)
        Assert.That(name "/r/dotnet/shared/Microsoft.NETCore.App/8.0.11/System.Runtime.dll", Is.EqualTo None)
        Assert.That(name "/opt/elsewhere/X.dll", Is.EqualTo None)

    [<Test>]
    member x.``an identity is the file on this machine; a pack missing from the .NET root is the package``() =
        let path = Lock.Identity.toPath context
        Assert.That(path "pkg:system.memory/4.5.5/lib/net461/System.Memory.dll", Is.EqualTo "/r/pkgs/system.memory/4.5.5/lib/net461/System.Memory.dll")
        // /r/dotnet/packs does not exist here: the package of that name in the package folder
        Assert.That(path "pack:microsoft.netcore.app.ref/8.0.11/ref/net8.0/System.Runtime.dll",
                    Is.EqualTo "/r/pkgs/microsoft.netcore.app.ref/8.0.11/ref/net8.0/System.Runtime.dll")
        Assert.That(path "sdk:Roslyn/bincore/csc.dll", Is.EqualTo "/r/dotnet/sdk/8.0.425/Roslyn/bincore/csc.dll")
        Assert.That(path "project:Lib", Is.EqualTo lib.Path)
        Assert.That(path "$(ProjectRoot)/lib/Native.dll", Is.EqualTo "/r/proj/lib/Native.dll")

    member private x.Recorded (references: string list) : Lock.Entry =
        let c =
            { Csc.ofArgs ([ "/noconfig"; "/target:library" ] @ (references |> List.map (fun r -> "/reference:" + r)) @ [ "/r/proj/src/A.cs" ]) with
                Name = "App"; Framework = "net8.0"; Directory = "/r/proj/src" }
        let c =
            { c with
                Dependencies =
                    { c.Dependencies with
                        Compiler = { Tool = "csc"; Path = "/r/dotnet/sdk/8.0.425/Roslyn/bincore/csc.dll"; Sha256 = ""; Version = "4.8.0" }
                        References = c.Dependencies.References |> List.map (fun r -> if r.Path = lib.Path then r else { r with Sha256 = "ab" })
                        Analyzers = [ { Path = "/r/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.11/analyzers/dotnet/cs/Gen.dll"; Sha256 = "cd" } ] } }
        { Lock.ofCsc c with
            Evaluation = { Lock.Evaluation.Empty with Project = "/r/proj/src/App.csproj"; ProjectRefs = [ "Lib" ]; Sdk = "8.0.425" }
            Prerequisites = [ { Kind = Lock.DotnetSdk; Version = "8.0.425"; Pin = "$(ProjectRoot)/global.json" } ]
            Recorded = Some { Projects = [ lib ]; Masks = [ "/r/proj/src/**/*.cs"; "!/r/proj/src/obj/**/*" ] } }

    [<Test>]
    member x.``a recorded entry is written with identities and the Recorded section, and reads back to paths``() =
        let entry = x.Recorded [ lib.Path; "/r/pkgs/system.memory/4.5.5/lib/net461/System.Memory.dll" ]
        let text = Lock.format roots { Configuration = "Release"; Properties = []; Entries = [ entry ] }
        Assert.That(text, Does.StartWith "{\n  \"Format\": 2,\n")
        Assert.That(text, Does.Contain "{ \"Id\": \"project:Lib\", \"Sha256\": \"\" }")
        Assert.That(text, Does.Contain "{ \"Id\": \"pkg:system.memory/4.5.5/lib/net461/System.Memory.dll\", \"Sha256\": \"ab\" }")
        Assert.That(text, Does.Contain "\"Compiler\": { \"Tool\": \"csc\", \"Id\": \"sdk:Roslyn/bincore/csc.dll\"")
        Assert.That(text, Does.Contain "{ \"Id\": \"pack:microsoft.netcore.app.ref/8.0.11/analyzers/dotnet/cs/Gen.dll\", \"Sha256\": \"cd\" }")
        Assert.That(text, Does.Contain "\"By\": \"msbuild\"")
        Assert.That(text, Does.Contain "\"$(ProjectRoot)/src/**/*.cs\"")
        Assert.That(text, Does.Contain "{ \"Name\": \"Lib\", \"Configuration\": \"Release\", \"Framework\": \"netstandard2.0\", \"Path\": \"$(ProjectRoot)/out/Release/netstandard2.0/Lib/Lib.dll\" }")
        Assert.That(text, Does.Not.Contain "/r/")
        let back = Lock.parse roots text
        let e = back.Entries.Head
        Assert.That(e.Recorded, Is.EqualTo entry.Recorded)
        Assert.That(e.Dependencies.References |> List.map (fun r -> r.Path), Is.EqualTo [ lib.Path; "/r/pkgs/system.memory/4.5.5/lib/net461/System.Memory.dll" ])
        Assert.That(e.Dependencies.Compiler.Path, Is.EqualTo "/r/dotnet/sdk/8.0.425/Roslyn/bincore/csc.dll")
        // the pack is not installed here: it reads as the package
        Assert.That(e.Dependencies.Analyzers.Head.Path, Is.EqualTo "/r/pkgs/microsoft.netcore.app.ref/8.0.11/analyzers/dotnet/cs/Gen.dll")
        // read on a machine without the pack, written again: the same text
        Assert.That(Lock.format roots back, Is.EqualTo text)

    [<Test>]
    member x.``a recorded file outside every root and package folder is refused, naming it``() =
        let entry = x.Recorded [ "/opt/elsewhere/X.dll" ]
        let e = Assert.Throws<exn>(fun () -> Lock.format roots { Configuration = "Release"; Properties = []; Entries = [ entry ] } |> ignore)
        Assert.That(e.Message, Does.Contain "/opt/elsewhere/X.dll")
        Assert.That(e.Message, Does.Contain "cannot be named portably")

    [<Test>]
    member x.``a format 1 lock of an imported project is refused; a composed one still reads``() =
        let entry name project =
            sprintf """{ "Name": "%s", "Framework": "net8.0",
      "Evaluation": { "Project": "%s", "ProjectRefs": [], "Imports": [], "Sdk": "", "SdkPin": "", "Properties": {} },
      "Compilation": { "Directory": "/r/proj", "Options": [], "Defines": [], "Sources": [], "Generated": {}, "Resources": {} },
      "Dependencies": { "Compiler": { "Tool": "csc", "Path": "", "Sha256": "", "Version": "" }, "References": [], "Analyzers": [], "Packages": [] } }""" name project
        let lockOf (e: string) = sprintf """{ "Configuration": "Release", "Properties": {}, "Entries": [ %s ] }""" e
        let e = Assert.Throws<exn>(fun () -> Lock.parse roots (lockOf (entry "App" "$(ProjectRoot)/App.csproj")) |> ignore)
        Assert.That(e.Message, Does.Contain "lock format 1 is older than 2")
        Assert.That(e.Message, Does.Contain "run the import again")
        Assert.That((Lock.parse roots (lockOf (entry "Composed" ""))).Entries.Head.Name, Is.EqualTo "Composed")
        let newer = Assert.Throws<exn>(fun () -> Lock.parse roots """{ "Format": 3, "Configuration": "", "Properties": {}, "Entries": [] }""" |> ignore)
        Assert.That(newer.Message, Does.Contain "newer")

    [<Test>]
    member x.``host strings: a root, the home folder or a runtime identifier left in a lock text``() =
        let home = (Environment.GetFolderPath Environment.SpecialFolder.UserProfile).Replace ('\\', '/')
        Assert.That(Import.hostStrings roots "\"$(ProjectRoot)/a.cs\",\n\"pkg:x/1.0/lib/x.dll\"", Is.Empty)
        Assert.That(Import.hostStrings roots "\"/r/proj/a.cs\"", Is.EqualTo [ "\"/r/proj/a.cs\"" ])
        Assert.That(Import.hostStrings roots (sprintf "\"%s/.nuget/x.dll\"" home), Has.Length.EqualTo 1)
        Assert.That(Import.hostStrings roots "\"pack:Microsoft.NETCore.App.Host.osx-arm64/8.0.11/x\"", Is.Empty)
        Assert.That(Import.hostStrings roots "\"$(DotnetRoot)/packs/Host/8.0.11/runtimes/osx-arm64/native/apphost\"", Has.Length.EqualTo 1)


/// `Import.record` end to end on a two-project solution: a library (netstandard2.0 and net8.0,
/// a package) and an application (net472 and net8.0, an exe, a project reference, a resx, a
/// named resource, implicit usings). The locks hold no string of this machine; Xake builds
/// them into the same files as `dotnet build`, with the same types and methods; importing
/// again -- after Xake's build and after `dotnet build` -- writes the same locks (no facade or
/// reference depends on what is already built).
[<TestFixture>]
type ``Import record``() =
    inherit XakeTestBase("import-record")

    let runDotnet (workDir: string) (args: string list) =
        let psi = ProcessStartInfo ("dotnet")
        psi.WorkingDirectory <- workDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        for a in args do psi.ArgumentList.Add a
        use p = Process.Start psi
        let out = p.StandardOutput.ReadToEnd ()
        let err = p.StandardError.ReadToEnd ()
        p.WaitForExit ()
        if p.ExitCode <> 0 then
            failwithf "'dotnet %s' failed (exit %d):\n%s\n%s" (String.Join (" ", args)) p.ExitCode out err

    /// (types, methods, manifest resources) of an assembly.
    let metadata (path: string) =
        use stream = File.OpenRead path
        use pe = new System.Reflection.PortableExecutable.PEReader (stream)
        let md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader pe
        md.TypeDefinitions.Count, md.MethodDefinitions.Count,
        [ for h in md.ManifestResources -> md.GetString (md.GetManifestResource h).Name ] |> List.sort

    let resx (entries: (string * string) list) =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n"
        + "  <resheader name=\"resmimetype\"><value>text/microsoft-resx</value></resheader>\n"
        + "  <resheader name=\"version\"><value>2.0</value></resheader>\n"
        + (entries |> List.map (fun (k, v) -> sprintf "  <data name=\"%s\" xml:space=\"preserve\"><value>%s</value></data>\n" k v) |> String.concat "")
        + "</root>\n"

    let projects = [ "lib/lib.csproj"; "app/app.csproj" ]
    let lockOf name cfg = sprintf "locks/%s/%s.json" name cfg
    let outOf cfg fwk name = sprintf "out/%s/%s/%s" cfg fwk name

    let fixture () =
        for d in [ "lib"; "app"; "out"; "locks"; "obj" ] do if Directory.Exists d then Directory.Delete (d, true)
        for d in [ "lib"; "app/Data" ] do Directory.CreateDirectory d |> ignore
        let libProject = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks><GenerateDocumentationFile>true</GenerateDocumentationFile><NoWarn>$(NoWarn);1591</NoWarn></PropertyGroup>
  <ItemGroup><PackageReference Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup>
</Project>
"""
        File.WriteAllText ("lib/lib.csproj", libProject)
        File.WriteAllText ("lib/C.cs", "namespace Lib { public class C { public static string S() => Newtonsoft.Json.JsonConvert.SerializeObject (new [] { 1, 2 }); } }\n")
        let appProject = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFrameworks>net472;net8.0</TargetFrameworks><OutputType>Exe</OutputType><LangVersion>latest</LangVersion><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><ProjectReference Include="../lib/lib.csproj" /><EmbeddedResource Include="Data/t.txt" LogicalName="X.txt" /></ItemGroup>
</Project>
"""
        File.WriteAllText ("app/app.csproj", appProject)
        File.WriteAllText ("app/P.cs", "class P { static void Main() { Console.WriteLine(Lib.C.S() + new System.Resources.ResourceManager(\"app.R\", typeof(P).Assembly).GetString(\"A\")); } }\n")
        File.WriteAllText ("app/R.resx", resx [ "A", "a" ])
        File.WriteAllText ("app/Data/t.txt", "t\n")

    member private x.Run (label: string) (rules': Rule<ExecContext> list) (targets: string list) =
        xake {x.TestOptions with FileLog = label + ".log"; ThrowOnError = true; Vars = [ "CI", "off"; "CSC_SERVER", "off" ]; FileLogLevel = Loud} {
            wantOverride targets
            rules rules'
        }

    member private x.Import (label: string) =
        let result = ref []
        x.Run label [ label => recipe {
                          do! alwaysRerun ()
                          let! i = Import.record { Import.RecordOptions.Default with LockOf = lockOf; OutOf = outOf } projects
                          result.Value <- i } ] [ label ]
        result.Value

    member private x.Build (label: string) =
        let compile = recipe {
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            do! Locks.compileAs (lockOf name cfg) fwk [] }
        x.Run label
            [ command "build" { do! Locks.needAll (!! "*/xake.project.json") "Release" lockOf outOf }
              "out/(cfg:*)/(fwk:*)/(name:*)/*.dll" ..> compile
              "out/(cfg:*)/(fwk:*)/(name:*)/*.exe" ..> compile
              "out/(cfg:*)/(fwk:*)/(name:*)/*.xml" ..> compile ]
            [ "build" ]

    member private x.Locks () = [ for p in [ "lib"; "app" ] -> File.ReadAllText (lockOf p "Release") ]

    [<Test>]
    member x.``a two-project solution records, builds as dotnet build does, and records the same again``() =
        fixture ()
        let imported = x.Import "import-1"
        Assert.That(imported |> List.collect (fun i -> i.Locks), Is.EqualTo [ lockOf "lib" "Release"; lockOf "app" "Release" ])
        let locks = x.Locks ()
        let here = (Directory.GetCurrentDirectory ()).Replace ('\\', '/')
        let home = (Environment.GetFolderPath Environment.SpecialFolder.UserProfile).Replace ('\\', '/')
        for text in locks do
            Assert.That(text, Does.Not.Contain here)
            Assert.That(text, Does.Not.Contain home)
            Assert.That(text, Does.Not.Contain "\r")
            Assert.That(text, Does.StartWith "{\n  \"Format\": 2,")
        let app = locks.[1]
        Assert.That(app, Does.Contain "\"Id\": \"project:lib\"")
        Assert.That(app, Does.Contain "\"Id\": \"pkg:newtonsoft.json/13.0.3/lib/")
        Assert.That(app, Does.Contain "\"Id\": \"pkg:microsoft.netframework.referenceassemblies.net472/")
        Assert.That(app, Does.Contain "\"Id\": \"sdk:Roslyn/bincore/csc.dll\"")
        Assert.That(app, Does.Contain "\"By\": \"msbuild\"")
        Assert.That(app, Does.Contain "/nullable:enable")
        Assert.That(app, Does.Contain "/resource:$(ProjectRoot)/obj/xake/app/net8.0/Release/app.R.resources")
        Assert.That(app, Does.Contain "/resource:$(ProjectRoot)/app/Data/t.txt,X.txt")
        Assert.That(app, Does.Contain "app.GlobalUsings.g.cs")
        Assert.That(app, Does.Contain "\"TargetFileName\": \"app.exe\"")
        Assert.That(app, Does.Not.Contain "/out:")
        Assert.That(app, Does.Not.Contain "/refout:")

        // Xake builds every framework of both projects from the locks
        x.Build "build-1"
        let produced =
            [ "out/Release/netstandard2.0/lib/lib.dll"; "out/Release/net8.0/lib/lib.dll"
              "out/Release/net472/app/app.exe"; "out/Release/net8.0/app/app.dll" ]
        for f in produced do Assert.That(File.Exists f, f)
        Assert.That(File.Exists "out/Release/net8.0/lib/lib.xml")

        // the same again: after Xake's build nothing changes
        let again = x.Import "import-2"
        Assert.That(again |> List.exists (fun i -> i.Written), Is.False)
        Assert.That(x.Locks (), Is.EqualTo locks)

        // msbuild's own build: the same compile outputs, the same types, methods and resources
        let here = Directory.GetCurrentDirectory ()
        runDotnet (here </> "app") [ "build"; "app.csproj"; "-c"; "Release"; "-p:NuGetAudit=false" ]
        let msbuildOf =
            [ "out/Release/netstandard2.0/lib/lib.dll", "lib/bin/Release/netstandard2.0/lib.dll"
              "out/Release/net8.0/lib/lib.dll", "lib/bin/Release/net8.0/lib.dll"
              "out/Release/net472/app/app.exe", "app/bin/Release/net472/app.exe"
              "out/Release/net8.0/app/app.dll", "app/bin/Release/net8.0/app.dll" ]
        for ours, theirs in msbuildOf do
            Assert.That(File.Exists theirs, theirs)
            Assert.That(metadata ours, Is.EqualTo (metadata theirs), ours)
            Assert.That(File.Exists (Path.ChangeExtension (ours, ".pdb")), Is.EqualTo (File.Exists (Path.ChangeExtension (theirs, ".pdb"))), ours)
        let _, _, resources = metadata "out/Release/net8.0/app/app.dll"
        Assert.That(resources, Is.EqualTo [ "X.txt"; "app.R.resources" ])

        // and after msbuild's build (bin/ and obj/ full of outputs): still the same locks
        let third = x.Import "import-3"
        Assert.That(third |> List.exists (fun i -> i.Written), Is.False)
        Assert.That(x.Locks (), Is.EqualTo locks)

        // the lock keeps msbuild's file order and the description's masks: a source added since
        // is compiled without a new import
        Assert.That(app, Does.Contain "\"Masks\": [")
        let types, _, _ = metadata "out/Release/net8.0/app/app.dll"
        File.WriteAllText ("app/Extra.cs", "public class Extra { }\n")
        x.Build "build-2"
        let typesAfter, _, _ = metadata "out/Release/net8.0/app/app.dll"
        Assert.That(typesAfter, Is.EqualTo (types + 1))
        Assert.That(x.Locks (), Is.EqualTo locks)


/// One synthesized restore downloads one version of an id: the fetch of a recorded lock set
/// (each project's own restore graph) splits the packages into rounds of distinct ids.
[<TestFixture>]
type ``Restore rounds``() =

    [<Test>]
    member x.``packages of one id in several versions go into separate rounds``() =
        let rounds = Restore.rounds [ "Azure.Core", "1.47.1"; "Castle.Core", "4.4.0"; "azure.core", "1.51.1"; "Azure.Core", "1.47.1"; "Azure.Core", "2.0.0" ]
        Assert.That(rounds, Is.EqualTo [ [ "Azure.Core", "1.47.1"; "Castle.Core", "4.4.0" ]; [ "azure.core", "1.51.1" ]; [ "Azure.Core", "2.0.0" ] ])
        Assert.That(Restore.rounds [], Is.Empty)
