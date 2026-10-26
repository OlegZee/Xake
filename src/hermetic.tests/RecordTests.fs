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
            Recorded = Some { Projects = [ lib ]; Masks = [ "/r/proj/src/**/*.cs"; "!/r/proj/src/obj/**/*" ]; Placed = None; App = None } }

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
        Assert.That(e.Recorded.Value.Projects, Is.EqualTo entry.Recorded.Value.Projects)
        Assert.That(e.Recorded.Value.Masks, Is.EqualTo entry.Recorded.Value.Masks)
        // one source, in the masks' place: nothing is placed
        Assert.That(e.Recorded.Value.Placed, Is.EqualTo (Some ([]: (string * string) list)))
        Assert.That(e.Dependencies.References |> List.map (fun r -> r.Path), Is.EqualTo [ lib.Path; "/r/pkgs/system.memory/4.5.5/lib/net461/System.Memory.dll" ])
        Assert.That(e.Dependencies.Compiler.Path, Is.EqualTo "/r/dotnet/sdk/8.0.425/Roslyn/bincore/csc.dll")
        // the pack is not installed here: it reads as the package
        Assert.That(e.Dependencies.Analyzers.Head.Path, Is.EqualTo "/r/pkgs/microsoft.netcore.app.ref/8.0.11/analyzers/dotnet/cs/Gen.dll")
        // read on a machine without the pack, written again: the same text
        Assert.That(Lock.format roots back, Is.EqualTo text)

    [<Test>]
    member x.``a recorded entry's SDK prerequisite follows its content``() =
        let sdk : Lock.Prerequisite = { Kind = Lock.DotnetSdk; Version = "8.0.425"; Pin = "$(ProjectRoot)/global.json" }
        let derive (e: Lock.Entry) = (Lock.withRecordedPrerequisites "/r/dotnet" "$(ProjectRoot)/global.json" e).Prerequisites
        // the SDK's compiler: the SDK that recorded it, the pin kept
        let onSdk = x.Recorded [ "/r/pkgs/system.memory/4.5.5/lib/net461/System.Memory.dll" ]
        Assert.That(derive { onSdk with Prerequisites = [] }, Is.EqualTo [ sdk ])
        Assert.That((Lock.withRecordedPrerequisites "/r/dotnet" "other" onSdk).Prerequisites, Is.EqualTo [ sdk ])
        // the toolset compiler and package analyzers: nothing under sdk/, no prerequisite
        let onPackages =
            { onSdk with
                Compilation =
                    match onSdk.Compilation with
                    | Lock.Compilation.Csc c ->
                        Lock.Compilation.Csc
                            { c with
                                Dependencies =
                                    { c.Dependencies with
                                        Compiler = { c.Dependencies.Compiler with Path = "/r/pkgs/microsoft.net.compilers.toolset/4.11.0/tasks/netcore/bincore/csc.dll"; Sha256 = "ef" }
                                        Analyzers = [ { Path = "/r/pkgs/microsoft.codeanalysis.netanalyzers/8.0.0/analyzers/dotnet/cs/Microsoft.CodeAnalysis.NetAnalyzers.dll"; Sha256 = "01" } ] } }
                    | other -> other }
        Assert.That(derive onPackages, Is.Empty)
        // a file of the SDK anywhere on the command line (an analyzer config) brings it back
        let withConfig =
            { onPackages with
                Compilation =
                    match onPackages.Compilation with
                    | Lock.Compilation.Csc c -> Lock.Compilation.Csc { c with Options = c.Options @ [ "/analyzerconfig:/r/dotnet/sdk/8.0.425/Sdks/Microsoft.NET.Sdk/analyzers/build/config/analysislevel_8_default.globalconfig" ] }
                    | other -> other }
        Assert.That(derive withConfig, Is.EqualTo [ sdk ])
        // the SDK's apphost (no runtime identifier): a build takes it from that SDK
        let app : Lock.App = { OutputType = "Exe"; Host = Some Lock.SdkHost; Files = []; Copy = []; Content = []; Related = []; BindingRedirects = false }
        let withHost = { onPackages with Recorded = onPackages.Recorded |> Option.map (fun r -> { r with App = Some app }) }
        Assert.That(derive withHost, Is.EqualTo [ sdk ])
        // a pack's apphost (an explicit runtime identifier) is written through the SDK's HostModel too
        let packHost = { app with Host = Some (Lock.PackHost ("win-x64", { Path = "/r/pkgs/microsoft.netcore.app.host.win-x64/8.0.11/runtimes/win-x64/native/apphost.exe"; Sha256 = "aa" })) }
        let withPackHost = { onPackages with Recorded = onPackages.Recorded |> Option.map (fun r -> { r with App = Some packHost }) }
        Assert.That(derive withPackHost, Is.EqualTo [ sdk ])
        // an application without an apphost does not
        let noHost = { onPackages with Recorded = onPackages.Recorded |> Option.map (fun r -> { r with App = Some { app with Host = None } }) }
        Assert.That(derive noHost, Is.Empty)
        // a composed entry (not recorded) is left as it is
        let composed = { onPackages with Recorded = None; Prerequisites = [ sdk ] }
        Assert.That(derive composed, Is.EqualTo [ sdk ])
        // the probe: a .NET root without that SDK fails the replay, one without a prerequisite passes
        let empty = Path.Combine (Path.GetTempPath (), "xake-empty-dotnet-" + string (Guid.NewGuid ()))
        Directory.CreateDirectory (Path.Combine (empty, "sdk")) |> ignore
        try
            Assert.That(Lock.unmetPrerequisitesAt (Some empty) onSdk, Has.Length.EqualTo 1)
            Assert.That((Lock.unmetPrerequisitesAt (Some empty) onSdk).Head, Does.Contain "the lock needs the .NET SDK 8.0.425")
            Assert.That(Lock.unmetPrerequisitesAt (Some empty) { onPackages with Prerequisites = derive onPackages }, Is.Empty)
        finally
            Directory.Delete (empty, true)

    [<Test>]
    member x.``packs from NuGet by default: a pack root that does not exist; Installed passes none``() =
        let options = { Import.RecordOptions.Default with Properties = [ "Brand", "Globex" ] }
        Assert.That(options.PacksFrom, Is.EqualTo Import.NuGet)
        let globals = Import.recordGlobals "/r/proj" options
        Assert.That(globals, Is.EqualTo [ "NuGetAudit", "false"; "NetCoreTargetingPackRoot", "/r/proj/obj/xake/no-installed-packs"; "Brand", "Globex" ])
        Assert.That(Import.recordGlobals "/r/proj/" options, Is.EqualTo globals)
        Assert.That(Import.recordGlobals "/r/proj" { options with PacksFrom = Import.Installed }, Is.EqualTo [ "NuGetAudit", "false"; "Brand", "Globex" ])
        // the script's own value wins
        let own = { options with Properties = [ "netcoretargetingpackroot", "/opt/packs" ] }
        Assert.That(Import.recordGlobals "/r/proj" own, Is.EqualTo [ "NuGetAudit", "false"; "netcoretargetingpackroot", "/opt/packs" ])

    [<Test>]
    member x.``a pack identity is the copy whose hash the lock recorded, installed or the package's``() =
        let dir = Path.Combine (Path.GetTempPath (), "xake-pack-candidates-" + string (Guid.NewGuid ()))
        let write (path: string) (text: string) =
            Directory.CreateDirectory (Path.GetDirectoryName path) |> ignore
            File.WriteAllText (path, text)
            path.Replace ('\\', '/')
        try
            let dotnet, pkgs = (Path.Combine (dir, "dotnet")).Replace ('\\', '/'), (Path.Combine (dir, "pkgs")).Replace ('\\', '/')
            let installed = write (Path.Combine (dotnet, "packs", "Microsoft.NETCore.App.Ref", "8.0.11", "ref", "net8.0", "X.dll")) "installed, unsigned"
            let package = write (Path.Combine (pkgs, "microsoft.netcore.app.ref", "8.0.11", "ref", "net8.0", "X.dll")) "the package's, signed"
            let context : Lock.Identity.Context = { Roots = [ "$(DotnetRoot)", dotnet; "$(NuGetPackageRoot)", pkgs ]; Sdk = ""; Projects = [] }
            let identity = "pack:microsoft.netcore.app.ref/8.0.11/ref/net8.0/X.dll"
            let resolve = Lock.Identity.toPathHashed context identity
            let shaOf = Xake.Dotnet.Hash.sha256
            // the lock recorded the package's copy while packs/ holds another file of that name
            Assert.That(resolve (shaOf package), Is.EqualTo package)
            // and the reverse: the installed pack's
            Assert.That(resolve (shaOf installed), Is.EqualTo installed)
            // no hash: the installed pack, as toPath
            Assert.That(resolve "", Is.EqualTo installed)
            Assert.That(Lock.Identity.toPath context identity, Is.EqualTo installed)
            // neither: the package's copy (the restore fetches it), and the replay names both
            Assert.That(resolve "00", Is.EqualTo package)
            let c = { Csc.ofArgs [ "/noconfig"; "/reference:" + package; "/r/proj/A.cs" ] with Name = "App"; Framework = "net8.0"; Directory = "/r/proj" }
            let c = { c with Dependencies = { c.Dependencies with References = c.Dependencies.References |> List.map (fun r -> { r with Sha256 = "00" }) } }
            let entry = { Lock.ofCsc c with Recorded = Some { Projects = []; Masks = []; Placed = None; App = None } }
            match Lock.packMismatches (Some dotnet) pkgs entry with
            | [ message ] ->
                Assert.That(message, Does.Contain identity)
                Assert.That(message, Does.Contain (sprintf "'%s' (%s)" installed (shaOf installed)))
                Assert.That(message, Does.Contain (sprintf "'%s' (%s)" package (shaOf package)))
            | other -> Assert.Fail (sprintf "expected one message, got %A" other)
            // the recorded copy: nothing to say
            let matching = { entry with Compilation = Lock.Compilation.Csc { c with Dependencies = { c.Dependencies with References = c.Dependencies.References |> List.map (fun r -> { r with Sha256 = shaOf package }) } } }
            Assert.That(Lock.packMismatches (Some dotnet) pkgs matching, Is.Empty)
        finally
            Directory.Delete (dir, true)

    [<Test>]
    member x.``with masks, a lock writes only the sources the masks do not put in place, and a build puts them back``() =
        let masks = [ "/r/proj/**/*.cs"; "/r/shared/Link.cs"; "!/r/proj/obj/**/*" ]
        let gen = "/r/proj/obj/xake/App.AssemblyInfo.cs"
        // msbuild's order: the masks' (per mask, ordinal ignoring case) but Z.cs, compiled first,
        // and the generated file last
        let sources = [ "/r/proj/Z.cs"; "/r/proj/a.cs"; "/r/proj/B.cs"; "/r/proj/sub/C.cs"; "/r/shared/Link.cs"; gen ]
        Assert.That(Lock.canonicalSources masks (List.take 5 sources),
                    Is.EqualTo [ "/r/proj/a.cs"; "/r/proj/B.cs"; "/r/proj/sub/C.cs"; "/r/proj/Z.cs"; "/r/shared/Link.cs" ])
        let placed = Lock.placedSources masks ((=) gen) sources
        Assert.That(placed, Is.EqualTo [ "/r/proj/Z.cs", ""; gen, "/r/shared/Link.cs" ])
        let files = List.take 5 sources |> List.sort
        Assert.That(Lock.reconstructSources masks placed (fun _ -> true) files, Is.EqualTo sources)
        // a file added since takes its place in the masks' order; a deleted one is left out
        let now = files |> List.filter ((<>) "/r/proj/B.cs") |> List.append [ "/r/proj/b2.cs" ]
        Assert.That(Lock.reconstructSources masks placed (fun f -> f = gen || List.contains f now) now,
                    Is.EqualTo [ "/r/proj/Z.cs"; "/r/proj/a.cs"; "/r/proj/b2.cs"; "/r/proj/sub/C.cs"; "/r/shared/Link.cs"; gen ])
        // written and read back: the same text, the placed files only in Sources
        let c = { Csc.ofArgs ([ "/noconfig" ] @ sources) with Name = "App"; Framework = "net8.0"; Directory = "/r/proj"; Generated = [ gen, "// g" ] }
        let c = { c with Dependencies = { c.Dependencies with Compiler = { Tool = "csc"; Path = "/r/dotnet/sdk/8.0.425/Roslyn/bincore/csc.dll"; Sha256 = ""; Version = "" } } }
        let entry =
            { Lock.ofCsc c with
                Evaluation = { Lock.Evaluation.Empty with Project = "/r/proj/App.csproj"; Sdk = "8.0.425" }
                Recorded = Some { Projects = []; Masks = masks; Placed = None; App = None } }
        let rootsHere = [ "$(ProjectRoot)", "/r/proj"; "$(Shared)", "/r/shared"; "$(DotnetRoot)", "/r/dotnet"; "$(NuGetPackageRoot)", "/r/pkgs" ]
        let text = Lock.format rootsHere { Configuration = "Release"; Properties = []; Entries = [ entry ] }
        Assert.That(text, Does.Contain "\"Sources\": [\n          \"$(ProjectRoot)/Z.cs\",\n          \"$(ProjectRoot)/obj/xake/App.AssemblyInfo.cs\"\n        ]")
        Assert.That(text, Does.Contain "\"Placed\": {\n          \"$(ProjectRoot)/Z.cs\": \"\",\n          \"$(ProjectRoot)/obj/xake/App.AssemblyInfo.cs\": \"$(Shared)/Link.cs\"\n        }")
        let back = Lock.parse rootsHere text
        Assert.That(back.Entries.Head.Recorded.Value.Placed, Is.EqualTo (Some placed))
        Assert.That(Lock.format rootsHere back, Is.EqualTo text)

    [<Test>]
    member x.``document properties are tokenized like paths``() =
        let text = Lock.format roots { Configuration = "Release"; Properties = [ "Brand", "X"; "RestoreConfigFile", "/r/proj/NuGet.config" ]; Entries = [] }
        Assert.That(text, Does.Contain "\"RestoreConfigFile\": \"$(ProjectRoot)/NuGet.config\"")
        Assert.That(text, Does.Contain "\"Brand\": \"X\"")
        Assert.That((Lock.parse roots text).Properties, Is.EqualTo [ "Brand", "X"; "RestoreConfigFile", "/r/proj/NuGet.config" ])

    [<Test>]
    member x.``msbuild's quotes are taken off a path, but not off a path list item with a comma``() =
        Assert.That(Import.unquote "/analyzerconfig:\"/a/~b/x.editorconfig\"", Is.EqualTo "/analyzerconfig:/a/~b/x.editorconfig")
        Assert.That(Import.unquote "\"/a b/c.cs\"", Is.EqualTo "/a b/c.cs")
        Assert.That(Import.unquote "/reference:\"/a,b.dll\",alias", Is.EqualTo "/reference:\"/a,b.dll\",alias")
        Assert.That(Import.unquote "/reference:\"/a~/b.dll\",alias", Is.EqualTo "/reference:/a~/b.dll,alias")
        Assert.That(Import.unquote "/define:\"A;B\"", Is.EqualTo "/define:A;B")
        Assert.That(Import.unquote "/nowarn:1591", Is.EqualTo "/nowarn:1591")
        // a path with a space, unquoted, is quoted again in the response file the runner writes
        let c = Csc.ofArgs [ Import.unquote "\"/a b/c.cs\"" ]
        Assert.That(c.Sources, Is.EqualTo [ "/a b/c.cs" ])

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
        Assert.That(e.Message, Does.Contain "Xake's own resolution of the project, not msbuild's")
        let newer = Assert.Throws<exn>(fun () -> Lock.parse roots """{ "Format": 3, "Configuration": "", "Properties": {}, "Entries": [] }""" |> ignore)
        Assert.That(newer.Message, Does.Contain "newer")
        // what Project.import writes is marked: format 1, legacy, read as before
        let legacy = sprintf """{ "Format": 1, "Legacy": "Project.import", "Configuration": "Release", "Properties": {}, "Entries": [ %s ] }""" (entry "App" "$(ProjectRoot)/App.csproj")
        let doc = Lock.parse roots legacy
        Assert.That(doc.Entries.Head.Name, Is.EqualTo "App")
        Assert.That(Lock.format roots doc, Does.StartWith "{\n  \"Format\": 1,\n  \"Legacy\": \"Project.import\",\n")
        // and a format-2 lock never holds such an entry
        let unrecorded = Assert.Throws<exn>(fun () -> Lock.parse roots (sprintf """{ "Format": 2, "Configuration": "Release", "Properties": {}, "Entries": [ %s ] }""" (entry "App" "$(ProjectRoot)/App.csproj")) |> ignore)
        Assert.That(unrecorded.Message, Does.Contain "msbuild did not record")

    [<Test>]
    member x.``host strings: a root, the home folder or a runtime identifier left in a lock text``() =
        let home = (Environment.GetFolderPath Environment.SpecialFolder.UserProfile).Replace ('\\', '/')
        Assert.That(Import.hostStrings roots [] "\"$(ProjectRoot)/a.cs\",\n\"pkg:x/1.0/lib/x.dll\"", Is.Empty)
        Assert.That(Import.hostStrings roots [] "\"/r/proj/a.cs\"", Is.EqualTo [ "\"/r/proj/a.cs\"" ])
        Assert.That(Import.hostStrings roots [] (sprintf "\"%s/.nuget/x.dll\"" home), Has.Length.EqualTo 1)
        Assert.That(Import.hostStrings roots [] "\"pack:Microsoft.NETCore.App.Host.osx-arm64/8.0.11/x\"", Is.Empty)
        Assert.That(Import.hostStrings roots [] "\"$(DotnetRoot)/packs/Host/8.0.11/runtimes/osx-arm64/native/apphost\"", Has.Length.EqualTo 1)


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
        // no msbuild node left holding the redirected output (ReadToEnd would wait for it)
        psi.Environment.["MSBUILDDISABLENODEREUSE"] <- "1"
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
        let doc = recipe {
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            do! Locks.doc Locks.Settings.Default (lockOf name cfg) fwk }
        x.Run label
            [ command "build" { do! Locks.needAll (!! "*/xake.project.json") "Release" lockOf outOf }
              "out/(cfg:*)/(fwk:*)/(name:*)/*.dll" ..> compile
              "out/(cfg:*)/(fwk:*)/(name:*)/*.exe" ..> compile
              "out/(cfg:*)/(fwk:*)/(name:*)/*.xml" ..> doc ]
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
        // the targeting pack is a package row too (the SBOM's component)
        Assert.That(app, Does.Contain "{ \"Id\": \"microsoft.netcore.app.ref\", \"Version\": \"8.0.")
        Assert.That(app, Does.Not.Contain "/out:")
        Assert.That(app, Does.Not.Contain "/refout:")

        // Xake builds every framework of both projects from the locks
        x.Build "build-1"
        let produced =
            [ "out/Release/netstandard2.0/lib/lib.dll"; "out/Release/net8.0/lib/lib.dll"
              "out/Release/net472/app/app.exe"; "out/Release/net8.0/app/app.dll" ]
        for f in produced do Assert.That(File.Exists f, f)
        Assert.That(File.Exists "out/Release/net8.0/lib/lib.xml")

        // the application's output is `app.exe` (msbuild's `TargetFileName`): asked as `app.dll` is not it
        let compile = recipe {
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            do! Locks.compileAs (lockOf name cfg) fwk [] }
        let asDll = Assert.Throws<XakeException>(fun () ->
            x.Run "build-asdll" [ "out/(cfg:*)/(fwk:*)/(name:*)/*.dll" ..> compile ] [ "out/Release/net472/app/app.dll" ])
        Assert.That(asDll.Data0, Does.Contain "is not the output of the lock")
        Assert.That(asDll.Data0, Does.Contain "(app.exe)")
        Assert.That(File.Exists "out/Release/net472/app/app.dll", Is.False)

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

        // the lock keeps the description's masks and only the sources they do not put in place
        // (the generated ones): a source added since is compiled without a new import
        Assert.That(app, Does.Contain "\"Masks\": [")
        Assert.That(app, Does.Contain "\"Placed\": {")
        // P.cs only as what the first generated source follows, once per framework
        Assert.That(app.Split("\"$(ProjectRoot)/app/P.cs\"").Length - 1, Is.EqualTo 2)
        Assert.That(app, Does.Contain "\"$(ProjectRoot)/obj/xake/app/net8.0/Release/app.GlobalUsings.g.cs\": \"$(ProjectRoot)/app/P.cs\"")
        let types, _, _ = metadata "out/Release/net8.0/app/app.dll"
        File.WriteAllText ("app/Extra.cs", "public class Extra { }\n")
        x.Build "build-2"
        let typesAfter, _, _ = metadata "out/Release/net8.0/app/app.dll"
        Assert.That(typesAfter, Is.EqualTo (types + 1))
        Assert.That(x.Locks (), Is.EqualTo locks)


/// What the `Import.record` end-to-end fixtures share: running dotnet, reading an assembly's
/// metadata, a resx text, and an import or a build in a Xake run of the fixture's folder.
module internal RecordKit =

    let runDotnet (workDir: string) (args: string list) =
        let psi = ProcessStartInfo ("dotnet")
        // no msbuild node left holding the redirected output (ReadToEnd would wait for it)
        psi.Environment.["MSBUILDDISABLENODEREUSE"] <- "1"
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

    let lockOf name cfg = sprintf "locks/%s/%s.json" name cfg
    let outOf cfg fwk name = sprintf "out/%s/%s/%s" cfg fwk name

    /// Deletes the folders and writes the files (path, text) of a fixture.
    let write (folders: string list) (files: (string * string) list) =
        for d in folders do if Directory.Exists d then Directory.Delete (d, true)
        for path, text in files do
            match Path.GetDirectoryName path with "" -> () | dir -> Directory.CreateDirectory dir |> ignore
            File.WriteAllText (path, text)

    let run (options: ExecOptions) (label: string) (rules': Rule<ExecContext> list) (targets: string list) =
        xake {options with FileLog = label + ".log"; ThrowOnError = true; Vars = options.Vars @ [ "CI", "off"; "CSC_SERVER", "off" ]; FileLogLevel = Loud} {
            wantOverride targets
            rules rules'
        }

    /// `Import.record` of `projects` with `options` (the fixture's lock and output layout by
    /// default), in a run of its own.
    let import (options: ExecOptions) (label: string) (record: Import.RecordOptions) (projects: string list) =
        let result = ref []
        run options label [ label => recipe {
                                do! alwaysRerun ()
                                let! i = Import.record record projects
                                result.Value <- i } ] [ label ]
        result.Value

    let recordDefault = { Import.RecordOptions.Default with LockOf = lockOf; OutOf = outOf }

    /// The fixture's outputs of configuration `cfg`, built from the locks.
    let build (options: ExecOptions) (label: string) (cfg: string) =
        let compile = recipe {
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            do! Locks.compileAs (lockOf name cfg) fwk [] }
        let doc = recipe {
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            do! Locks.doc Locks.Settings.Default (lockOf name cfg) fwk }
        run options label
            [ command "build" { do! Locks.needAll (!! "*/xake.project.json") cfg lockOf outOf }
              "out/(cfg:*)/(fwk:*)/(name:*)/*.dll" ..> compile
              "out/(cfg:*)/(fwk:*)/(name:*)/*.exe" ..> compile
              "out/(cfg:*)/(fwk:*)/(name:*)/*.xml" ..> doc ]
            [ "build" ]

    /// The run folders (`run/<cfg>/<fwk>/<name>/`, `Locks.runFolderWith` by `mode`) of every
    /// application and test project of configuration `cfg`, over the fixture's per-project
    /// outputs (the compile rules of `build`).
    let deploy (options: ExecOptions) (label: string) (cfg: string) (mode: RunFolder.Mode) =
        let compile = recipe {
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            do! Locks.compileAs (lockOf name cfg) fwk [] }
        let doc = recipe {
            let! cfg = getRuleMatch "cfg"
            let! fwk = getRuleMatch "fwk"
            let! name = getRuleMatch "name"
            do! Locks.doc Locks.Settings.Default (lockOf name cfg) fwk }
        run options label
            [ command "deploy" {
                let! folders = Locks.runFoldersWith [] (!! "*/xake.project.json") cfg lockOf Locks.runOf
                do! need folders }
              "run/(cfg:*)/(fwk:*)/(name:*)/.xake-run.txt" ..> recipe {
                let! cfg = getRuleMatch "cfg"
                let! fwk = getRuleMatch "fwk"
                let! name = getRuleMatch "name"
                do! Locks.runFolderWith [] mode lockOf outOf cfg fwk name }
              "out/(cfg:*)/(fwk:*)/(name:*)/*.dll" ..> compile
              "out/(cfg:*)/(fwk:*)/(name:*)/*.exe" ..> compile
              "out/(cfg:*)/(fwk:*)/(name:*)/*.xml" ..> doc ]
            [ "deploy" ]

    let sdkProject (properties: string) (items: string) =
        sprintf "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>%s</PropertyGroup>\n  <ItemGroup>%s</ItemGroup>\n</Project>\n" properties items


/// `Import.record` over several configurations and a platform: the restore solution declares
/// every configuration|platform asked for, and each configuration is restored for itself (a
/// package may depend on it).
[<TestFixture>]
type ``Import record configurations``() =
    inherit XakeTestBase("import-record-cfg")

    let fixture () =
        RecordKit.write [ "one"; "out"; "locks"; "obj" ]
            [ "one/one.csproj",
                RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework>"
                    "<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" Condition=\"'$(Configuration)' == 'Debug'\" />"
              "one/C.cs",
                "namespace One { public class C {\n#if DEBUG\n  public static string S() => Newtonsoft.Json.JsonConvert.SerializeObject (1);\n#else\n  public static string S() => \"1\";\n#endif\n} }\n" ]

    [<Test>]
    member x.``Debug and Release are recorded, each with its own restore, and both build``() =
        fixture ()
        let imported =
            RecordKit.import x.TestOptions "import-cfg" { RecordKit.recordDefault with Configurations = [ "Debug"; "Release" ] } [ "one/one.csproj" ]
        Assert.That(imported |> List.collect (fun i -> i.Locks), Is.EqualTo [ RecordKit.lockOf "one" "Debug"; RecordKit.lockOf "one" "Release" ])
        let debug, release = File.ReadAllText (RecordKit.lockOf "one" "Debug"), File.ReadAllText (RecordKit.lockOf "one" "Release")
        Assert.That(debug, Does.Contain "\"DEBUG\"")
        Assert.That(release, Does.Not.Contain "\"DEBUG\"")
        // the package of the Debug configuration only
        Assert.That(debug, Does.Contain "pkg:newtonsoft.json/13.0.3/")
        Assert.That(release, Does.Not.Contain "newtonsoft")
        RecordKit.build x.TestOptions "build-debug" "Debug"
        RecordKit.build x.TestOptions "build-release" "Release"
        Assert.That(File.Exists "out/Debug/net8.0/one/one.dll")
        Assert.That(File.Exists "out/Release/net8.0/one/one.dll")

    [<Test>]
    member x.``a Platform property is a platform of the restore solution``() =
        fixture ()
        RecordKit.import x.TestOptions "import-x64" { RecordKit.recordDefault with Properties = [ "Platform", "x64" ] } [ "one/one.csproj" ] |> ignore
        let release = File.ReadAllText (RecordKit.lockOf "one" "Release")
        Assert.That(release, Does.Contain "/platform:x64")
        Assert.That(release, Does.Contain "\"Platform\": \"x64\"")


/// Two imports of one project at once, with different properties (two brands): each records
/// its own AssemblyInfo, since each design-time build writes into an intermediate folder of
/// its own.
[<TestFixture>]
type ``Import record concurrent brands``() =
    inherit XakeTestBase("import-record-brands")

    [<Test>]
    member x.``two brands imported at once record their own generated texts``() =
        RecordKit.write [ "b"; "out"; "locks"; "obj"; "desc" ]
            [ "b/b.csproj", RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework><AssemblyTitle>Brand-$(Brand)</AssemblyTitle>" ""
              "b/C.cs", "namespace B { public class C { } }\n" ]
        let options brand =
            { RecordKit.recordDefault with
                Properties = [ "Brand", brand ]
                DescriptionOf = (fun _ -> sprintf "desc/%s/xake.project.json" brand)
                LockOf = (fun name cfg -> sprintf "locks/%s/%s/%s.json" brand name cfg) }
        let importOf brand = sprintf "import-%s" brand => recipe {
                                 do! alwaysRerun ()
                                 let! _ = Import.record (options brand) [ "b/b.csproj" ]
                                 () }
        let brands = [ "Acme"; "Globex"; "Third" ]
        // one `need` of all: the targets of a command line run one after the other
        let all = "import-all" => recipe { do! need (brands |> List.map (sprintf "import-%s")) }
        RecordKit.run { x.TestOptions with Threads = 4 } "import-brands" (all :: (brands |> List.map importOf)) [ "import-all" ]
        // nothing in msbuild's own intermediate folder, which an IDE or `dotnet build` writes too
        Assert.That(File.Exists "b/obj/Release/net8.0/b.AssemblyInfo.cs", Is.False)
        Assert.That(Directory.GetDirectories ("b/obj/xake-record"), Has.Length.EqualTo brands.Length)
        for brand in brands do
            let text = File.ReadAllText (sprintf "locks/%s/b/Release.json" brand)
            Assert.That(text, Does.Contain (sprintf "AssemblyTitleAttribute(\\\"Brand-%s\\\")" brand), brand)
            for other in brands do
                if other <> brand then Assert.That(text, Does.Not.Contain ("Brand-" + other), brand)

    /// The restore output too (`project.assets.json`, `nuget.g.props`/`.targets`): each import
    /// restores into its own `obj/xake-record/<key>/` (`MSBuildProjectExtensionsPath`), so a
    /// brand's lock never holds the packages of a brand restored at the same time.
    [<Test>]
    member x.``two brands imported at once record their own packages``() =
        let packages =
            "<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" Condition=\"'$(Brand)' == 'Acme'\" />"
            + "<PackageReference Include=\"NUnit\" Version=\"3.13.3\" Condition=\"'$(Brand)' == 'Globex'\" />"
        RecordKit.write [ "lib"; "app"; "out"; "locks"; "obj"; "desc" ]
            [ "lib/lib.csproj", RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework>" packages
              "lib/L.cs", "namespace L { public class C { } }\n"
              "app/app.csproj", RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework>" "<ProjectReference Include=\"../lib/lib.csproj\" />"
              "app/A.cs", "namespace A { public class C { } }\n" ]
        let options brand =
            { RecordKit.recordDefault with
                Properties = [ "Brand", brand ]
                DescriptionOf = (fun pf -> sprintf "desc/%s/%s/xake.project.json" brand (Path.GetFileNameWithoutExtension pf))
                LockOf = (fun name cfg -> sprintf "locks/%s/%s/%s.json" brand name cfg) }
        let importOf brand = sprintf "import-%s" brand => recipe {
                                 do! alwaysRerun ()
                                 let! _ = Import.record (options brand) [ "app/app.csproj"; "lib/lib.csproj" ]
                                 () }
        let brands = [ "Acme"; "Globex" ]
        // its own (NUnit brings NETStandard.Library for net8.0) and the other brand's packages
        let own = dict [ "Acme", [ "\"Newtonsoft.Json\"" ]; "Globex", [ "\"NUnit\""; "\"NETStandard.Library\"" ] ]
        let all = "import-all" => recipe { do! need (brands |> List.map (sprintf "import-%s")) }
        for round in 1 .. 3 do
            RecordKit.run { x.TestOptions with Threads = 4 } (sprintf "import-packages-%d" round) (all :: (brands |> List.map importOf)) [ "import-all" ]
            for brand in brands do
                for name in [ "lib"; "app" ] do
                    let text = File.ReadAllText (sprintf "locks/%s/%s/Release.json" brand name)
                    let what = sprintf "round %d, %s, %s" round brand name
                    for p in own.[brand] do Assert.That(text, Does.Contain p, what)
                    for other in brands do
                        if other <> brand then
                            for p in own.[other] do Assert.That(text, Does.Not.Contain p, what)
        // msbuild's own restore output is not written: an IDE or `dotnet build` keeps its own
        Assert.That(File.Exists "lib/obj/project.assets.json", Is.False)
        Assert.That(Directory.GetFiles ("lib/obj/xake-record", "project.assets.json", SearchOption.AllDirectories), Has.Length.EqualTo brands.Length)


/// `Import.record` against what a repository sets for itself: its own
/// `CustomAfterMicrosoftCommonTargets` (chained, not overridden), a shared resource outside
/// the project (its name from msbuild's `Link`), its `global.json` (the prerequisite's pin,
/// tokenized; a warning that the lock pins the SDK more strictly).
[<TestFixture>]
type ``Import record repository settings``() =
    inherit XakeTestBase("import-record-repo")

    let fixture () =
        RecordKit.write [ "app"; "shared"; "out"; "locks"; "obj" ]
            [ "Directory.Build.props",
                "<Project><PropertyGroup><CustomAfterMicrosoftCommonTargets>$(MSBuildThisFileDirectory)custom.targets</CustomAfterMicrosoftCommonTargets></PropertyGroup></Project>\n"
              "custom.targets", "<Project><PropertyGroup><DefineConstants>$(DefineConstants);CUSTOM_AFTER</DefineConstants></PropertyGroup></Project>\n"
              "global.json", "{ \"sdk\": { \"rollForward\": \"latestMajor\", \"version\": \"8.0.0\" } }\n"
              // a binary msbuild generates in its intermediate folder and hands the compiler (as
              // WPF does with g.resources): copied beside the lock byte for byte
              "app/app.csproj",
                RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework>" "<EmbeddedResource Include=\"../shared/Shared.resx\" />"
                |> fun t -> t.Replace ("</Project>", "  <Target Name=\"Blob\" BeforeTargets=\"CoreCompile\">\n    <Copy SourceFiles=\"blob.bin\" DestinationFiles=\"$(IntermediateOutputPath)app.g.resources\" />\n    <ItemGroup><_CoreCompileResourceInputs Include=\"$(IntermediateOutputPath)app.g.resources\" LogicalName=\"app.g.resources\" /></ItemGroup>\n  </Target>\n</Project>")
              "app/C.cs", "namespace App { public class C {\n#if CUSTOM_AFTER\n  public static int X = 1;\n#endif\n} }\n"
              "shared/Shared.resx", RecordKit.resx [ "A", "a" ] ]

    [<Test>]
    member x.``the repository's targets, a linked resource and the global.json pin are recorded``() =
        fixture ()
        let blob = Array.init 512 (fun i -> byte (i * 7 % 256)) |> Array.append "\r\n\xff\xfe"B
        File.WriteAllBytes ("app/blob.bin", blob)
        RecordKit.import x.TestOptions "import-repo" RecordKit.recordDefault [ "app/app.csproj" ] |> ignore
        let lock = File.ReadAllText (RecordKit.lockOf "app" "Release")
        // the repository's CustomAfterMicrosoftCommonTargets ran in the design-time build
        Assert.That(lock, Does.Contain "\"CUSTOM_AFTER\"")
        // a resource outside the project is named by its link (PrepareForBuild's AssignLinkMetadata)
        Assert.That(lock, Does.Contain "/obj/xake/app/net8.0/Release/app.Shared.resources\"")
        Assert.That(lock, Does.Contain "\"$(ProjectRoot)/shared/Shared.resx\"")
        // the prerequisite names the global.json by its tokenized path, and the import says the
        // lock pins the SDK more strictly than it
        Assert.That(lock, Does.Contain "\"Pin\": \"$(ProjectRoot)/global.json\"")
        let log = File.ReadAllText "import-repo.log"
        Assert.That(log, Does.Contain "pins the .NET SDK")
        RecordKit.build x.TestOptions "build-repo" "Release"
        let _, _, resources = RecordKit.metadata "out/Release/net8.0/app/app.dll"
        Assert.That(resources, Is.EqualTo [ "app.Shared.resources"; "app.g.resources" ])
        // the generated binary: beside the lock, named there, embedded unchanged
        Assert.That(lock, Does.Contain "\"/resource:$(ProjectRoot)/locks/app/Release.files/net8.0/app.g.resources,app.g.resources\"")
        Assert.That(File.ReadAllBytes "locks/app/Release.files/net8.0/app.g.resources", Is.EqualTo blob)
        let asm = Reflection.Assembly.Load (File.ReadAllBytes "out/Release/net8.0/app/app.dll")
        use stream = asm.GetManifestResourceStream "app.g.resources"
        use copy = new MemoryStream ()
        stream.CopyTo copy
        Assert.That(copy.ToArray (), Is.EqualTo blob)


/// `Import.record` of a chain of project references (c -> b -> a): the transitive reference
/// is a `project:` identity too, and a referenced project outside the import fails it.
[<TestFixture>]
type ``Import record project chain``() =
    inherit XakeTestBase("import-record-chain")

    let fixture () =

        RecordKit.write [ "a"; "b"; "c"; "out"; "locks"; "obj" ]
            [ "a/a.csproj", RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework>" ""
              "a/A.cs", "namespace A { public class X { } }\n"
              "b/b.csproj", RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework>" "<ProjectReference Include=\"../a/a.csproj\" />"
              "b/B.cs", "namespace B { public class Y : A.X { } }\n"
              "c/c.csproj", RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework>" "<ProjectReference Include=\"../b/b.csproj\" />"
              "c/C.cs", "namespace C { public class Z : B.Y { A.X x; } }\n" ]

    [<Test>]
    member x.``a transitive project reference is a project identity, built from its own lock``() =
        fixture ()
        RecordKit.import x.TestOptions "import-chain" RecordKit.recordDefault [ "a/a.csproj"; "b/b.csproj"; "c/c.csproj" ] |> ignore
        // the SDK pin warning, when there is one: once for the import, not once per project
        let pinWarnings = Text.RegularExpressions.Regex.Matches(File.ReadAllText "import-chain.log", "pin the \\.NET SDK|pins the \\.NET SDK").Count
        Assert.That(pinWarnings, Is.LessThanOrEqualTo 1)
        if pinWarnings = 1 then Assert.That(File.ReadAllText "import-chain.log", Does.Contain "3 projects ('a', 'b', 'c'): their locks pin")
        let c = File.ReadAllText (RecordKit.lockOf "c" "Release")
        Assert.That(c, Does.Contain "\"Id\": \"project:a\"")
        Assert.That(c, Does.Contain "\"Id\": \"project:b\"")
        Assert.That(c, Does.Contain "{ \"Name\": \"a\", \"Configuration\": \"Release\", \"Framework\": \"net8.0\", \"Path\": \"$(ProjectRoot)/out/Release/net8.0/a/a.dll\" }")
        RecordKit.build x.TestOptions "build-chain" "Release"
        for p in [ "a"; "b"; "c" ] do Assert.That(File.Exists (sprintf "out/Release/net8.0/%s/%s.dll" p p), p)

    [<Test>]
    member x.``a project file edited since the import fails the build: run the import again``() =
        fixture ()
        RecordKit.import x.TestOptions "import-stale" RecordKit.recordDefault [ "a/a.csproj"; "b/b.csproj"; "c/c.csproj" ] |> ignore
        RecordKit.build x.TestOptions "build-stale-1" "Release"
        File.AppendAllText ("b/b.csproj", "<!-- a PackageReference would go here -->\n")
        let e = Assert.Catch(fun () -> RecordKit.build x.TestOptions "build-stale-2" "Release")
        Assert.That(string e, Does.Contain "/b/b.csproj' changed since the import that wrote it; run the import again")
        // what a script's check target asks (`Locks.staleWith`, `Locks.check`)
        let locks = [ for p in [ "a"; "b"; "c" ] -> RecordKit.lockOf p "Release" ]
        let stale = ref []
        RecordKit.run x.TestOptions "stale-with" [ "stale-with" => recipe { let! s = Locks.staleWith [] locks in stale.Value <- s } ] [ "stale-with" ]
        Assert.That(stale.Value |> List.map fst, Is.EqualTo [ RecordKit.lockOf "b" "Release" ])
        Assert.That(stale.Value |> List.collect snd |> String.concat "\n", Does.Contain "/b/b.csproj' changed since the import that wrote it; run the import again")
        let e2 = Assert.Catch(fun () -> RecordKit.run x.TestOptions "check" [ "check" => Locks.check locks ] [ "check" ])
        Assert.That(string e2, Does.Contain "/b/b.csproj' changed since the import")
        // a warning only, when asked for (not under CI)
        RecordKit.build { x.TestOptions with Vars = [ "LOCK_STALE", "warn" ] } "build-stale-3" "Release"
        Assert.That(File.ReadAllText "build-stale-3.log", Does.Contain "is stale")
        // imported again: the build is quiet
        RecordKit.import x.TestOptions "import-stale-2" RecordKit.recordDefault [ "a/a.csproj"; "b/b.csproj"; "c/c.csproj" ] |> ignore
        RecordKit.build x.TestOptions "build-stale-4" "Release"
        Assert.That(File.ReadAllText "build-stale-4.log", Does.Not.Contain "is stale")

    [<Test>]
    member x.``a referenced project the import does not record fails it, naming both``() =
        fixture ()
        let e = Assert.Catch(fun () -> RecordKit.import x.TestOptions "import-partial" RecordKit.recordDefault [ "b/b.csproj"; "c/c.csproj" ] |> ignore)
        let message = string e
        Assert.That(message, Does.Contain "/a/a.csproj', which this import does not record")
        Assert.That(message, Does.Contain "/b/b.csproj' (Release|net8.0) references")
        Assert.That(File.Exists (RecordKit.lockOf "c" "Release"), Is.False)


/// `Import.record` refuses a WPF project up front (S69) and writes no host path into a
/// description whose output folder lies outside the project (S70).
[<TestFixture>]
type ``Import record refusals and descriptions``() =
    inherit XakeTestBase("import-record-refusals")

    [<Test>]
    member x.``a WPF project is refused before msbuild's markup compiler runs; Page items or UseWPF alone are not WPF``() =
        RecordKit.write [ "wpf"; "pages"; "wpfcode"; "out"; "locks"; "obj" ]
            [ "wpf/wpf.csproj", RecordKit.sdkProject "<TargetFramework>net472</TargetFramework><UseWPF>true</UseWPF>" "<Page Include=\"MainWindow.xaml\" />"
              "pages/pages.csproj", RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework>" "<Page Include=\"X.xaml\" />"
              "pages/X.xaml", "<x />\n"
              "pages/C.cs", "class C { }\n"
              "wpfcode/wpfcode.csproj", RecordKit.sdkProject "<TargetFramework>net472</TargetFramework><UseWPF>true</UseWPF>" ""
              "wpfcode/C.cs", "class C { }\n"
              "wpf/MainWindow.xaml", "<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />\n"
              "wpf/C.cs", "class C { }\n" ]
        let e = Assert.Catch(fun () -> RecordKit.import x.TestOptions "import-wpf" RecordKit.recordDefault [ "wpf/wpf.csproj" ] |> ignore)
        Assert.That(string e, Does.Contain "WPF is not supported by the import yet")
        Assert.That(string e, Does.Contain "/wpf/wpf.csproj'")
        Assert.That(string e, Does.Not.Contain "MC3050")
        Assert.That(string e, Does.Not.Contain "pages.csproj")
        RecordKit.import x.TestOptions "import-pages" RecordKit.recordDefault [ "pages/pages.csproj"; "wpfcode/wpfcode.csproj" ] |> ignore
        Assert.That(File.Exists (RecordKit.lockOf "pages" "Release"))
        Assert.That(File.Exists (RecordKit.lockOf "wpfcode" "Release"))

    [<Test>]
    member x.``an output folder outside the project or named by the configuration is no host path in the description``() =
        let outside = (Path.GetFullPath "elsewhere").Replace ('\\', '/')
        RecordKit.write [ "p"; "q"; "elsewhere"; "out"; "locks"; "obj" ]
            [ "p/p.csproj", RecordKit.sdkProject (sprintf "<TargetFramework>net8.0</TargetFramework><OutputPath>%s/bin/$(Configuration)/</OutputPath>" outside) ""
              "p/C.cs", "class C { }\n"
              "q/q.csproj", RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework><OutputPath>build/$(Configuration)/</OutputPath>" ""
              "q/C.cs", "class C { }\n" ]
        RecordKit.import x.TestOptions "import-excludes" RecordKit.recordDefault [ "p/p.csproj"; "q/q.csproj" ] |> ignore
        let p, q = File.ReadAllText "p/xake.project.json", File.ReadAllText "q/xake.project.json"
        Assert.That(p, Does.Not.Contain outside)
        Assert.That(p, Does.Not.Contain (x.TestOptions.ProjectRoot.Replace('\\', '/').TrimEnd('/')))
        Assert.That(q, Does.Contain "\"!build/*/**/*\"")
        Assert.That(q, Does.Not.Contain "Debug")


/// `Import.record` checks the files the compiler's switches name, not only the references:
/// an `.editorconfig` above the repository fails the import; a key file in a declared root is
/// written by its token.
[<TestFixture>]
type ``Import record switch files``() =
    inherit XakeTestBase("import-record-switch")

    let here () = (Directory.GetCurrentDirectory ()).Replace ('\\', '/')
    let fixture () =
        for f in [ ".editorconfig" ] do if File.Exists f then File.Delete f
        RecordKit.write [ "repo"; "keys" ]
            [ "repo/app/app.csproj",
                RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework><SignAssembly>true</SignAssembly><AssemblyOriginatorKeyFile>../../keys/k.snk</AssemblyOriginatorKeyFile>" ""
              "repo/app/C.cs", "namespace App { public class C { } }\n"
              "keys/k.snk", "not a key: the design-time build does not read it\n" ]
    member private x.Options = { x.TestOptions with ProjectRoot = here () + "/repo" }
    member private x.Record = { RecordKit.recordDefault with Roots = [ "$(Keys)", here () + "/keys" ] }

    [<Test>]
    member x.``an editorconfig above the repository fails the import, naming it``() =
        fixture ()
        File.WriteAllText (".editorconfig", "root = true\n[*.cs]\nindent_size = 4\n")
        try
            let e = Assert.Catch(fun () -> RecordKit.import x.Options "import-editorconfig" x.Record [ "app/app.csproj" ] |> ignore)
            Assert.That(string e, Does.Contain (here () + "/.editorconfig', which cannot be named portably"))
            Assert.That(File.Exists "repo/locks/app/Release.json", Is.False)
        finally File.Delete ".editorconfig"

    [<Test>]
    member x.``a key file in a declared root is written by its token``() =
        fixture ()
        RecordKit.import x.Options "import-keyfile" x.Record [ "app/app.csproj" ] |> ignore
        let lock = File.ReadAllText "repo/locks/app/Release.json"
        Assert.That(lock, Does.Contain "\"/keyfile:$(Keys)/k.snk\"")
        // without the root, the key file cannot be named
        let e = Assert.Catch(fun () -> RecordKit.import x.Options "import-keyfile-2" RecordKit.recordDefault [ "app/app.csproj" ] |> ignore)
        Assert.That(string e, Does.Contain (here () + "/keys/k.snk', which cannot be named portably"))

    [<Test>]
    member x.``a pathmap outside every root, or a switch file outside, is reported; tokens and identities are not``() =
        let roots = [ "$(NuGetPackageRoot)", "/r/pkgs"; "$(ProjectRoot)", "/r/proj"; "$(DotnetRoot)", "/r/dotnet" ]
        let c =
            { Csc.ofArgs [ "/noconfig"; "/keyfile:/opt/keys/k.snk"; "/analyzerconfig:/r/dotnet/sdk/8.0.425/x.globalconfig"; "/ruleset:/r/proj/a.ruleset"
                           "/pathmap:/r/proj/=/_/,/opt/src/=/_2/"; "/r/proj/A.cs" ] with Name = "A"; Framework = "net8.0" }
        let entry = { Lock.ofCsc c with Evaluation = { Lock.Evaluation.Empty with Project = "/r/proj/A.csproj"; Sdk = "8.0.425" }; Recorded = Some { Projects = []; Masks = []; Placed = None; App = None } }
        let problems = Import.unportablePaths roots entry
        Assert.That(problems, Has.Length.EqualTo 2)
        Assert.That(problems.[0], Does.Contain "'/opt/keys/k.snk'")
        Assert.That(problems.[1], Does.Contain "/pathmap: maps '/opt/src/'")


/// `Import.entryOf` on a hand-made design-time build (no msbuild): the guard on preserialized
/// resources reads the recorded references, a description whose masks do not name what msbuild
/// compiles falls back to msbuild's files, and generated files are classified by kind.
[<TestFixture>]
type ``Record entry``() =
    inherit XakeTestBase("record-entry")

    let here () = (Directory.GetCurrentDirectory ()).Replace ('\\', '/')
    let sre () = here () + "/pkgs/system.resources.extensions/8.0.0/lib/net462/System.Resources.Extensions.dll"
    let setup () =
        RecordKit.write [ "proj"; "pkgs"; "dotnet" ]
            [ "proj/A.cs", "class A { }\n"
              "proj/Sub/B.cs", "class B { }\n"
              "proj/R.resx", RecordKit.resx [ "A", "a" ]
              "proj/obj/s/X.AssemblyInfo.cs", "[assembly: System.Reflection.AssemblyTitle(\"X\")]\r\n"
              "proj/obj/s/X.R.resources", "compiled"
              "pkgs/system.resources.extensions/8.0.0/lib/net462/System.Resources.Extensions.dll", "dll" ]
    let context (masks: string list) : Import.RecordContext =
        let dir = here () + "/proj"
        let description =
            Description.parse (sprintf "{ \"name\": \"X\", \"project\": \"X.csproj\", \"language\": \"csharp\", \"frameworks\": [ \"net472\" ], \"sources\": [ %s ] }"
                                   (masks |> List.map (sprintf "\"%s\"") |> String.concat ", "))
        { Root = here (); ProjectFile = dir + "/X.csproj"; ProjectDir = dir; Description = description
          DescriptionFile = dir + "/xake.project.json"; PackageRoot = here () + "/pkgs"
          Pin = Lock.NoGlobalJson; PinFile = None; Pin' = ""; Inputs = []; OutOf = RecordKit.outOf; Imported = [ dir + "/X.csproj" ] }
    let dump (references: string list) (preserialized: bool) : Import.Dump =
        let dir = here () + "/proj"
        let args =
            [ "/noconfig"; "/target:library"; "/out:obj/s/X.dll" ]
            @ (references |> List.map (sprintf "/reference:%s"))
            @ [ "/resource:obj/s/X.R.resources"; "/resource:obj/s/X.exe.licenses"; "A.cs"; "Sub/B.cs"; "obj/s/X.AssemblyInfo.cs" ]
        { Props =
            Map [ "AssemblyName", "X"; "TargetFramework", "net472"; "TargetFrameworkIdentifier", ".NETFramework"
                  "NETCoreSdkVersion", "8.0.425"; "NetCoreRoot", here () + "/dotnet"; "RoslynTargetsPath", here () + "/dotnet/sdk/8.0.425/Roslyn"
                  "MSBuildProjectFullPath", dir + "/X.csproj"
                  "GenerateResourceUsePreserializedResources", (if preserialized then "true" else "false") ]
          Items =
            Map [ "CscCommandLineArgs", args |> List.map (fun a -> a, Map.empty)
                  "EmbeddedResource", [ dir + "/R.resx", Map [ "OutputResource", "obj/s/X.R.resources" ] ] ] }

    [<Test>]
    member x.``generated files: known text kinds in the lock, any other kind copied, a compiled resx compiled again``() =
        setup ()
        let licenses = [| 0uy; 255uy; 13uy; 10uy; 0xC3uy; 0x28uy |]
        File.WriteAllBytes ("proj/obj/s/X.exe.licenses", licenses)
        let binaryDir = here () + "/locks/X/Release.files/net472"
        let entry, _, binaries = Import.entryOf (context [ "**/*.cs"; "!obj/**/*" ]) "Release" "net472" (here () + "/proj/obj/s") binaryDir (dump [] false)
        Assert.That(binaries, Is.EqualTo [ binaryDir + "/X.exe.licenses", licenses ])
        let c = entry.Compilation
        Assert.That(c.Options, Does.Contain ("/resource:" + binaryDir + "/X.exe.licenses"))
        Assert.That(c.Generated |> List.map fst, Is.EqualTo [ here () + "/obj/xake/X/net472/Release/X.AssemblyInfo.cs" ])
        Assert.That(snd c.Generated.Head, Does.Not.Contain "\r")
        Assert.That(c.Resources, Is.EqualTo [ here () + "/proj/R.resx", here () + "/obj/xake/X/net472/Release/X.R.resources" ])
        Assert.That(Import.isRecordedText "a/X.GlobalUsings.g.cs" && Import.isRecordedText "x.editorconfig", Is.True)
        Assert.That(Import.isRecordedText "X.g.resources" || Import.isRecordedText "X.exe.licenses" || Import.isRecordedText "X.res", Is.False)

    [<Test>]
    member x.``preserialized resources on .NET Framework need System.Resources.Extensions among the recorded references``() =
        setup ()
        File.WriteAllText ("proj/obj/s/X.exe.licenses", "")
        let entryOf refs preserialized = Import.entryOf (context [ "**/*.cs"; "!obj/**/*" ]) "Release" "net472" (here () + "/proj/obj/s") (here () + "/b") (dump refs preserialized)
        let e = Assert.Catch(fun () -> entryOf [] true |> ignore)
        Assert.That(e.Message, Does.Contain "reference the package System.Resources.Extensions")
        let entry, _, _ = entryOf [ sre () ] true
        Assert.That(entry.Dependencies.References |> List.map (fun r -> r.Path), Is.EqualTo [ sre () ])
        // not asked for: no guard
        entryOf [] false |> ignore

    [<Test>]
    member x.``masks that do not name msbuild's files: a warning, and the lock keeps msbuild's files only``() =
        setup ()
        File.WriteAllText ("proj/obj/s/X.exe.licenses", "")
        File.WriteAllText ("proj/Extra.cs", "class Extra { }\n")
        let entry, warnings, _ = Import.entryOf (context [ "**/*.cs"; "!obj/**/*" ]) "Release" "net472" (here () + "/proj/obj/s") (here () + "/b") (dump [] false)
        Assert.That(entry.Recorded.Value.Masks, Is.Empty)
        Assert.That(warnings |> String.concat "\n", Does.Contain "extra: Extra.cs")
        Assert.That(entry.Compilation.Sources |> List.filter (fun s -> s.EndsWith ".cs" && not (s.Contains "/obj/")),
                    Is.EqualTo [ here () + "/proj/A.cs"; here () + "/proj/Sub/B.cs" ])
        // the masks that do name them are kept, absolute
        File.Delete "proj/Extra.cs"
        let entry, warnings, _ = Import.entryOf (context [ "**/*.cs"; "!obj/**/*" ]) "Release" "net472" (here () + "/proj/obj/s") (here () + "/b") (dump [] false)
        Assert.That(warnings, Is.Empty)
        Assert.That(entry.Recorded.Value.Masks, Is.EqualTo [ here () + "/proj/**/*.cs"; "!" + here () + "/proj/obj/**/*" ])


/// One synthesized restore downloads one version of an id: the fetch of a recorded lock set
/// (each project's own restore graph) splits the packages into rounds of distinct ids.
[<TestFixture>]
type ``Restore rounds``() =

    [<Test>]
    member x.``packages of one id in several versions go into separate rounds``() =
        let rounds = Restore.rounds [ "Azure.Core", "1.47.1"; "Castle.Core", "4.4.0"; "azure.core", "1.51.1"; "Azure.Core", "1.47.1"; "Azure.Core", "2.0.0" ]
        Assert.That(rounds, Is.EqualTo [ [ "Azure.Core", "1.47.1"; "Castle.Core", "4.4.0" ]; [ "azure.core", "1.51.1" ]; [ "Azure.Core", "2.0.0" ] ])
        Assert.That(Restore.rounds [], Is.Empty)


/// A compile under the compiler server whose analyzer crashes (AD0001, S72) is reported with
/// the analyzer and compiled once more in a fresh compiler process.
[<TestFixture>]
type ``Compile under the compiler server``() =
    inherit XakeTestBase("compile-server")

    /// throws in a compilation action when it runs inside VBCSCompiler, never in a csc process
    let crash =
        "using System;\nusing System.Collections.Immutable;\nusing Microsoft.CodeAnalysis;\nusing Microsoft.CodeAnalysis.Diagnostics;\n"
        + "namespace Crash {\n  [DiagnosticAnalyzer(LanguageNames.CSharp)]\n  public class UnderServer : DiagnosticAnalyzer {\n"
        + "    static readonly DiagnosticDescriptor D = new DiagnosticDescriptor(\"CR1\", \"t\", \"m\", \"c\", DiagnosticSeverity.Warning, true);\n"
        + "    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(D);\n"
        + "    public override void Initialize(AnalysisContext c) {\n      c.EnableConcurrentExecution();\n      c.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);\n"
        + "      c.RegisterCompilationAction(_ => { if (string.Join(\" \", Environment.GetCommandLineArgs()).Contains(\"VBCSCompiler\")) throw new InvalidOperationException(\"under the server\"); });\n"
        + "    }\n  }\n}\n"

    [<Test>]
    member x.``an analyzer crash under the server is retried once without it``() =
        RecordKit.write [ "analyzer"; "app"; "out"; "locks"; "obj" ]
            [ "analyzer/analyzer.csproj",
                RecordKit.sdkProject "<TargetFramework>netstandard2.0</TargetFramework><EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules><NoWarn>RS1036;RS2008</NoWarn>"
                    "<PackageReference Include=\"Microsoft.CodeAnalysis.Common\" Version=\"4.5.0\" PrivateAssets=\"all\" />"
              "analyzer/Crash.cs", crash
              "app/app.csproj",
                RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework><TreatWarningsAsErrors>true</TreatWarningsAsErrors>"
                    "<Analyzer Include=\"../analyzer/bin/Release/netstandard2.0/analyzer.dll\" />"
              "app/C.cs", "namespace App { public class C { } }\n" ]
        RecordKit.runDotnet "analyzer" [ "build"; "-c"; "Release" ]
        RecordKit.import x.TestOptions "import-crash" RecordKit.recordDefault [ "app/app.csproj" ] |> ignore
        Assert.That(File.ReadAllText (RecordKit.lockOf "app" "Release"), Does.Contain "analyzer.dll")
        // a fresh server: one left running (an earlier run's, holding the previous analyzer.dll of
        // this path, or another fixture's dying on its keepalive) rejects the compile, and the
        // client falls back to a csc process silently, where the analyzer does not crash
        RecordKit.runDotnet "." [ "build-server"; "shutdown"; "--vbcscompiler" ]
        RecordKit.build { x.TestOptions with Vars = [ "CSC_SERVER", "on" ] } "build-crash" "Release"
        let log = File.ReadAllText "build-crash.log"
        Assert.That(log, Does.Contain "an analyzer crashed under the compiler server (AD0001): Crash.UnderServer (System.InvalidOperationException); compiling once more without the server")
        Assert.That(File.Exists "out/Release/net8.0/app/app.dll", Is.True)
        // without the server, the analyzer does not crash: no retry, nothing said
        Directory.Delete ("out", true)
        RecordKit.build x.TestOptions "build-crash-2" "Release"
        Assert.That(File.ReadAllText "build-crash-2.log", Does.Not.Contain "AD0001")

    [<Test; Platform(Exclude = "Win")>]
    member x.``a compile under the server carries no /errorlog``() =
        // `/errorlog:` makes csc report every diagnostic, hidden and suppressed ones included:
        // a quarter to a half more compiler CPU on each compile (0.4.2). A stand-in compiler
        // (`CscPath`) keeps the response file it is given.
        RecordKit.write [ "app"; "out"; "locks"; "obj" ]
            [ "app/app.csproj", RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework>" ""
              "app/C.cs", "namespace App { public class C { } }\n" ]
        RecordKit.import x.TestOptions "import-plain" RecordKit.recordDefault [ "app/app.csproj" ] |> ignore
        let seen = Path.GetFullPath "seen.rsp"
        let fake = Path.GetFullPath "fake-csc.sh"
        File.WriteAllText (fake, sprintf "#!/bin/sh\nfor a in \"$@\"; do case \"$a\" in @*) cat \"${a#@}\" > '%s';; esac; done\n" seen)
        File.SetUnixFileMode (fake, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)
        let entry = (Lock.read (Roots.builtin (Directory.GetCurrentDirectory ())) (RecordKit.lockOf "app" "Release")).Entries.Head
        let options = { Lock.Options.Default with Run = { RunOptions.Default with Server = Shared None; CscPath = Some fake } }
        let compile = recipe {
            do! alwaysRerun ()
            do! Lock.compileWith options entry }
        RecordKit.run x.TestOptions "build-plain" [ "plain" => compile ] [ "plain" ]
        let rsp = File.ReadAllText seen
        Assert.That(rsp, Does.Contain "/analyzerconfig:")
        Assert.That(rsp, Does.Not.Contain "/errorlog:")

    [<Test>]
    member x.``the crashed analyzers are read from a SARIF error log``() =
        let sarif =
            "{ \"version\": \"2.1.0\", \"runs\": [ { \"results\": [ { \"ruleId\": \"AD0001\", \"level\": \"error\", \"message\": { \"text\": "
            + "\"Analyzer 'A.B' threw an exception of type 'System.NullReferenceException' with message 'x'.\" } }, "
            + "{ \"ruleId\": \"CS0168\", \"message\": { \"text\": \"unused\" } } ] } ] }"
        Assert.That(Lock.analyzerCrashes sarif, Is.EqualTo [ "A.B (System.NullReferenceException)" ])
        Assert.That(Lock.analyzerCrashes "{ \"runs\": [ { \"results\": [ { \"ruleId\": \"CS0168\" } ] } ] }", Is.Empty)
        // and from what csc printed
        Assert.That(Lock.analyzerCrashes "CSC : error AD0001: Analyzer 'A.B' threw an exception of type 'System.NullReferenceException' with message 'x'.",
                    Is.EqualTo [ "A.B (System.NullReferenceException)" ])
        Assert.That(Lock.analyzerCrashes "C.cs(1,1): error CS0168: Analyzer 'A.B' threw an exception of type 'X'", Is.Empty)



/// Binding redirects recorded from msbuild's app run (`Import.record`, referenced projects as
/// stand-ins): a .NET Framework WinExe whose binary resources need `System.Resources.Extensions`
/// (its resources name 4.0.0.0, the package brings 8.0.0.0: without the redirect it fails to
/// start), a .NET Framework test library (`.dll.config`, Test.Sdk), a .NET application asking
/// for redirects -- each against `dotnet build`, in the compile output and the run folder.
[<TestFixture>]
type ``Import record binding redirects``() =
    inherit XakeTestBase("import-record-redirects")

    let projects = [ "lib/lib.csproj"; "resapp/resapp.csproj"; "nfxtests/nfxtests.csproj"; "winapp/winapp.csproj" ]
    let msbuilt = [ "resapp"; "nfxtests"; "winapp" ]

    let fixture () =
        RecordKit.write [ "lib"; "resapp"; "nfxtests"; "winapp"; "out"; "run"; "locks"; "obj" ]
            [ "lib/lib.csproj", RecordKit.sdkProject "<TargetFramework>netstandard2.0</TargetFramework>" ""
              "lib/C.cs", "namespace Lib { public class C { public static string S() => \"lib\"; } }\n"
              "resapp/resapp.csproj",
                RecordKit.sdkProject
                    "<TargetFramework>net472</TargetFramework><OutputType>WinExe</OutputType><GenerateResourceUsePreserializedResources>true</GenerateResourceUsePreserializedResources>"
                    "<ProjectReference Include=\"../lib/lib.csproj\" /><PackageReference Include=\"System.Resources.Extensions\" Version=\"8.0.0\" />"
              "resapp/P.cs", "class P { static void Main() { System.Console.WriteLine(Lib.C.S() + new System.Resources.ResourceManager(\"resapp.R\", typeof(P).Assembly).GetObject(\"B\")); } }\n"
              // a binary resource: the compiled .resources name System.Resources.Extensions' reader
              "resapp/R.resx",
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n"
                + "  <resheader name=\"resmimetype\"><value>text/microsoft-resx</value></resheader>\n"
                + "  <resheader name=\"version\"><value>2.0</value></resheader>\n"
                + "  <data name=\"B\" type=\"System.Byte[], mscorlib\"><value>AQID</value></data>\n"
                + "</root>\n"
              "nfxtests/nfxtests.csproj",
                RecordKit.sdkProject "<TargetFramework>net472</TargetFramework><IsPackable>false</IsPackable>"
                    ("<ProjectReference Include=\"../lib/lib.csproj\" />"
                     + "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.6.0\" /><PackageReference Include=\"NUnit\" Version=\"3.13.3\" />"
                     + "<PackageReference Include=\"NUnit3TestAdapter\" Version=\"4.2.1\" />"
                     // a package conflict: System.Memory 4.5.5 references an older Unsafe
                     + "<PackageReference Include=\"System.Memory\" Version=\"4.5.5\" /><PackageReference Include=\"System.Runtime.CompilerServices.Unsafe\" Version=\"6.0.0\" />")
              "nfxtests/T.cs", "namespace T { [NUnit.Framework.TestFixture] public class A { [NUnit.Framework.Test] public void B() => NUnit.Framework.Assert.AreEqual(\"lib\", Lib.C.S()); } }\n"
              "winapp/winapp.csproj",
                RecordKit.sdkProject
                    "<TargetFramework>net8.0-windows</TargetFramework><EnableWindowsTargeting>true</EnableWindowsTargeting><OutputType>Exe</OutputType><AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>"
                    "<ProjectReference Include=\"../lib/lib.csproj\" /><PackageReference Include=\"System.Memory\" Version=\"4.5.5\" />"
              "winapp/P.cs", "class P { static void Main() { System.Console.WriteLine(Lib.C.S() + new System.Memory<int>(new int[1]).Length); } }\n" ]

    /// The lock texts and the files beside them, by path.
    let recorded () =
        [ for f in Directory.GetFiles ("locks", "*", SearchOption.AllDirectories) |> Array.sort -> f.Replace ('\\', '/'), File.ReadAllText f ]

    /// The `<dependentAssembly>` blocks of a config text, in order.
    let redirectsOf (text: string) =
        [ for m in Text.RegularExpressions.Regex.Matches (text, "<dependentAssembly>[\\s\\S]*?</dependentAssembly>") -> m.Value ]

    let mutable prepared = false
    let mutable firstImport : (string * string) list = []

    member private x.Prepare () =
        if not prepared then
            fixture ()
            RecordKit.import x.TestOptions "import-1" RecordKit.recordDefault projects |> ignore
            firstImport <- recorded ()
            RecordKit.build x.TestOptions "build-1" "Release"
            RecordKit.deploy x.TestOptions "deploy-1" "Release" RunFolder.Link
            for a in msbuilt do RecordKit.runDotnet (Path.GetFullPath a) [ "build"; a + ".csproj"; "-c"; "Release"; "-p:NuGetAudit=false" ]
            prepared <- true

    /// (the recorded config, the compile output's, the run folder's, msbuild's) of a project.
    member private x.Configs (name: string) (fwk: string) (file: string) =
        x.Prepare ()
        sprintf "locks/%s/Release.files/%s/app/%s" name fwk file, sprintf "out/Release/%s/%s/%s" fwk name file,
        sprintf "run/Release/%s/%s/%s" fwk name file, sprintf "%s/bin/Release/%s/%s" name fwk file

    [<Test>]
    member x.``a .NET Framework app with binary resources: its exe.config has msbuild's redirects, byte for byte``() =
        let lockFile, out, run, theirs = x.Configs "resapp" "net472" "resapp.exe.config"
        let lock = File.ReadAllText (RecordKit.lockOf "resapp" "Release")
        Assert.That(lock, Does.Contain "{ \"Name\": \"resapp.exe.config\", \"Path\": \"$(ProjectRoot)/locks/resapp/Release.files/net472/app/resapp.exe.config\"")
        Assert.That(lock, Does.Contain "\"BindingRedirects\": true")
        let text = File.ReadAllText lockFile
        Assert.That(text, Does.Contain "<assemblyIdentity name=\"System.Resources.Extensions\" publicKeyToken=\"cc7b13ffcd2ddd51\" culture=\"neutral\" />")
        Assert.That(text, Does.Contain "<bindingRedirect oldVersion=\"0.0.0.0-8.0.0.0\" newVersion=\"8.0.0.0\" />")
        Assert.That(text, Does.Contain "<supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.7.2\" />")
        for f in [ lockFile; out; run ] do
            Assert.That(File.ReadAllBytes f, Is.EqualTo (File.ReadAllBytes theirs), f)

    [<Test>]
    member x.``a .NET Framework test library: its dll.config with redirects recorded, beside it and in its run folder``() =
        let lockFile, out, run, theirs = x.Configs "nfxtests" "net472" "nfxtests.dll.config"
        let lock = File.ReadAllText (RecordKit.lockOf "nfxtests" "Release")
        Assert.That(lock, Does.Contain "\"OutputType\": \"Library\"")
        Assert.That(lock, Does.Contain "{ \"Name\": \"nfxtests.dll.config\", \"Path\": \"$(ProjectRoot)/locks/nfxtests/Release.files/net472/app/nfxtests.dll.config\"")
        Assert.That(File.ReadAllText lockFile, Does.Contain "<bindingRedirect oldVersion=\"0.0.0.0-6.0.0.0\" newVersion=\"6.0.0.0\" />")
        for f in [ lockFile; out; run ] do
            Assert.That(File.ReadAllBytes f, Is.EqualTo (File.ReadAllBytes theirs), f)

    [<Test>]
    member x.``a .NET app asking for redirects: its dll.config recorded with msbuild's redirects over the packages``() =
        let lockFile, out, run, theirs = x.Configs "winapp" "net8.0-windows" "winapp.dll.config"
        Assert.That(File.ReadAllText (RecordKit.lockOf "winapp" "Release"),
                    Does.Contain "{ \"Name\": \"winapp.dll.config\", \"Path\": \"$(ProjectRoot)/locks/winapp/Release.files/net8.0-windows/app/winapp.dll.config\"")
        let ours, msbuild = redirectsOf (File.ReadAllText lockFile), redirectsOf (File.ReadAllText theirs)
        Assert.That(ours, Is.Not.Empty)
        // msbuild may have more: a conflict seen only through a referenced project's built
        // assembly (the stand-in has no references); never fewer, in the same order
        let rec subsequence (a: string list) (b: string list) =
            match a, b with
            | [], _ -> true
            | _, [] -> false
            | x :: a', y :: b' -> if x = y then subsequence a' b' else subsequence a b'
        Assert.That(subsequence ours msbuild, sprintf "%d of ours, %d of msbuild's" ours.Length msbuild.Length)
        for f in [ out; run ] do
            Assert.That(File.ReadAllBytes f, Is.EqualTo (File.ReadAllBytes lockFile), f)

    [<Test>]
    member x.``an import after both builds records the same configs: the redirects do not depend on what is built``() =
        x.Prepare ()
        RecordKit.import x.TestOptions "import-2" RecordKit.recordDefault projects |> ignore
        Assert.That(recorded (), Is.EqualTo firstImport)
