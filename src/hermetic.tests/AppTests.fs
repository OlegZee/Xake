namespace Tests

open System
open System.IO
open System.Diagnostics
open NUnit.Framework

open Xake
open Xake.Hermetic.Dotnet

/// The `deps.json` tokens of a referenced project's versions (`Import.depsTokens`) and the
/// host-string check over an application's lock text and files. Pure.
[<TestFixture>]
type ``App recording``() =

    let marker = Import.stubVersion

    /// A minimal PE "apphost": `MZ`, the PE header at 0x80, the application placeholder at 0x100.
    let fakePeAppHost () =
        let bytes = Array.zeroCreate<byte> 0x200
        bytes.[0] <- byte 'M'; bytes.[1] <- byte 'Z'
        BitConverter.GetBytes(0x80).CopyTo (bytes, 0x3C)
        Text.Encoding.UTF8.GetBytes("c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2").CopyTo (bytes, 0x100)
        bytes

    let scratch () =
        let dir = Path.Combine (Path.GetTempPath (), "xake-apps-" + Guid.NewGuid().ToString "N")
        Directory.CreateDirectory dir |> ignore
        dir

    [<Test>]
    member _.``the versions msbuild read from a stand-in become tokens, the rest stays``() =
        let deps =
            String.concat "\n"
                [ "{"
                  "  \"targets\": {"
                  "    \".NETCoreApp,Version=v8.0\": {"
                  "      \"app/1.0.0\": {"
                  "        \"dependencies\": {"
                  "          \"Lib.Core\": \"2.3.4\","
                  sprintf "          \"Lib.Core.Reference\": \"%s\"" marker
                  "        },"
                  "        \"runtime\": {"
                  "          \"app.dll\": {}"
                  "        }"
                  "      },"
                  "      \"Lib.Core/2.3.4\": {"
                  "        \"runtime\": {"
                  "          \"Lib.Core.dll\": {"
                  "            \"assemblyVersion\": \"2.3.4\","
                  "            \"fileVersion\": \"\""
                  "          }"
                  "        }"
                  "      },"
                  sprintf "      \"Lib.Core.Reference/%s\": {" marker
                  "        \"runtime\": {"
                  "          \"Lib.Core.dll\": {"
                  sprintf "            \"assemblyVersion\": \"%s\"," marker
                  sprintf "            \"fileVersion\": \"%s\"" marker
                  "          }"
                  "        }"
                  "      }"
                  "    }"
                  "  },"
                  "  \"libraries\": {"
                  sprintf "    \"Lib.Core.Reference/%s\": {" marker
                  "      \"type\": \"reference\""
                  "    }"
                  "  }"
                  "}" ]
        let tokens = Import.depsTokens "app.deps.json" deps
        Assert.That(tokens, Does.Not.Contain marker)
        Assert.That(tokens, Does.Contain "\"Lib.Core.Reference\": \"$(AssemblyVersion:Lib.Core.dll)\"")
        Assert.That(tokens.Split("\"Lib.Core.Reference/$(AssemblyVersion:Lib.Core.dll)\": {").Length - 1, Is.EqualTo 2)
        Assert.That(tokens, Does.Contain "\"assemblyVersion\": \"$(AssemblyVersion:Lib.Core.dll)\",\n            \"fileVersion\": \"$(FileVersion:Lib.Core.dll)\"")
        // what msbuild did not read from the file: as written
        Assert.That(tokens, Does.Contain "\"assemblyVersion\": \"2.3.4\",\n            \"fileVersion\": \"\"")
        Assert.That(Import.depsTokens "app.deps.json" tokens, Is.EqualTo tokens)

    [<Test>]
    member _.``a stand-in version left in a deps.json fails the import, naming the file``() =
        // another writer's layout (indents this code does not read): the marker would stay
        let deps =
            String.concat "\n"
                [ "{"
                  "  \"targets\": {"
                  "   \".NETCoreApp,Version=v8.0\": {"
                  sprintf "     \"Lib.Core.Reference/%s\": {" marker
                  "       \"runtime\": {"
                  "         \"Lib.Core.dll\": {"
                  sprintf "           \"assemblyVersion\": \"%s\"" marker
                  "         }"
                  "       }"
                  "     }"
                  "   }"
                  "  }"
                  "}" ]
        let e = Assert.Catch(fun () -> Import.depsTokens "obj/xake/app.deps.json" deps |> ignore)
        Assert.That(e.Message, Does.Contain "'obj/xake/app.deps.json'")
        Assert.That(e.Message, Does.Contain "deps.json layout is not known")
        // a version in the text that is not the marker passes
        Assert.That(Import.depsTokens "x.deps.json" "{ \"a\": \"1.2.3\" }", Is.EqualTo "{ \"a\": \"1.2.3\" }")

    [<Test>]
    member _.``the apphost written without the HostModel has the GUI subsystem of a WinExe on every host``() =
        let dir = scratch ()
        try
            let template = Path.Combine (dir, "apphost.exe")
            File.WriteAllBytes (template, fakePeAppHost ())
            let subsystem = 0x80 + 4 + 20 + 68
            Apps.createAppHostHere template (Path.Combine (dir, "gui.exe")) "gui.dll" true "win-x64"
            Apps.createAppHostHere template (Path.Combine (dir, "cui.exe")) "cui.dll" false "win-x64"
            let gui, cui = File.ReadAllBytes (Path.Combine (dir, "gui.exe")), File.ReadAllBytes (Path.Combine (dir, "cui.exe"))
            Assert.That(BitConverter.ToUInt16 (gui, subsystem), Is.EqualTo 2us)
            Assert.That(BitConverter.ToUInt16 (cui, subsystem), Is.EqualTo 0us)
            Assert.That(Text.Encoding.UTF8.GetString (gui, 0x100, 7), Is.EqualTo "gui.dll")
        finally Directory.Delete (dir, true)

    [<Test>]
    member _.``a HostModel that fails to load falls back to the byte-patch writer with a reason``() =
        let dir = scratch ()
        try
            // an SDK folder of this runtime's major version whose HostModel is not an assembly
            let sdkDir = Path.Combine (dir, sprintf "%d.0.100" Environment.Version.Major)
            Directory.CreateDirectory sdkDir |> ignore
            File.WriteAllText (Path.Combine (sdkDir, "Microsoft.NET.HostModel.dll"), "not an assembly")
            Assert.That(Apps.hostModelUsable sdkDir, Is.True)
            let template = Path.Combine (dir, "apphost.exe")
            File.WriteAllBytes (template, fakePeAppHost ())
            let reason = Apps.createAppHost sdkDir template (Path.Combine (dir, "app.exe")) "app.dll" true (Path.Combine (dir, "app.dll")) "win-x64"
            Assert.That(reason, Is.Not.EqualTo None)
            Assert.That(BitConverter.ToUInt16 (File.ReadAllBytes (Path.Combine (dir, "app.exe")), 0x80 + 92), Is.EqualTo 2us)
        finally Directory.Delete (dir, true)

    [<Test>]
    member _.``an SDK without its own apphost pack is a broken prerequisite: nothing is fetched``() =
        let dir = scratch ()
        try
            let sdkDir = Path.Combine (dir, "dotnet", "sdk", "8.0.425")
            Directory.CreateDirectory sdkDir |> ignore
            File.WriteAllText (Path.Combine (sdkDir, "Microsoft.NETCoreSdk.BundledVersions.props"),
                               "<Project><PropertyGroup><NETCoreSdkRuntimeIdentifier>osx-arm64</NETCoreSdkRuntimeIdentifier></PropertyGroup>"
                               + "<ItemGroup><KnownAppHostPack Include=\"Microsoft.NETCore.App\" TargetFramework=\"net8.0\" AppHostPackVersion=\"8.0.31\" /></ItemGroup></Project>")
            let e = Assert.Catch(fun () -> Apps.sdkAppHost (Path.Combine (dir, "dotnet")) sdkDir "net8.0-windows" (Path.Combine (dir, "packages")) |> ignore)
            Assert.That(e.Message, Does.StartWith "broken prerequisite")
            Assert.That(e.Message, Does.Contain "packs/Microsoft.NETCore.App.Host.osx-arm64/8.0.31")
            // the installation's pack: found
            let installed = Apps.appHostInstalled (Path.Combine (dir, "dotnet")) "osx-arm64" "8.0.31"
            Directory.CreateDirectory (Path.GetDirectoryName installed) |> ignore
            File.WriteAllText (installed, "")
            Assert.That(Apps.sdkAppHost (Path.Combine (dir, "dotnet")) sdkDir "net8.0" (Path.Combine (dir, "packages")), Is.EqualTo ((installed, "osx-arm64")))
        finally Directory.Delete (dir, true)

    [<Test>]
    member _.``a package's runtimes folder and the declared RID are not host strings``() =
        let roots = [ "$(ProjectRoot)", "/r/proj" ]
        Assert.That(Import.hostStrings roots [] "{ \"Id\": \"pkg:sqlitepclraw.lib.e_sqlite3/2.1.6/runtimes/osx-arm64/native/libe_sqlite3.dylib\", \"To\": \"runtimes/osx-arm64/native/libe_sqlite3.dylib\" }", Is.Empty)
        Assert.That(Import.hostStrings roots [] "\"rid\": \"osx-arm64\",", Is.Empty)
        Assert.That(Import.hostStrings roots [] "\"Host\": { \"Rid\": \"osx-arm64\" }", Has.Length.EqualTo 1)
        Assert.That(Import.hostStrings roots [ "win-x64" ] "\"Host\": { \"Rid\": \"win-x64\", \"Id\": \"pack:microsoft.netcore.app.host.win-x64/8.0.31/runtimes/win-x64/native/apphost.exe\" }", Is.Empty)
        Assert.That(Import.hostStrings roots [ "win-x64" ] "\"x\": \"osx-arm64\"", Has.Length.EqualTo 1)
        // the .NET installation's folder names this machine's
        Assert.That(Import.hostStrings roots [] "\"$(DotnetRoot)/packs/Host/8.0.11/runtimes/osx-arm64/native/apphost\"", Has.Length.EqualTo 1)

    [<Test>]
    member _.``an App section reads back as written``() =
        let roots = [ "$(ProjectRoot)", "/r/proj"; "$(NuGetPackageRoot)", "/r/pkgs"; "$(DotnetRoot)", "/r/dotnet" ]
        let lib : Lock.ProjectOutput = { Name = "lib"; Configuration = "Release"; Framework = "net8.0"; Path = "/r/proj/out/Release/net8.0/lib/lib.dll" }
        let app : Lock.App =
            { OutputType = "Exe"
              Host = Some (Lock.PackHost ("win-x64", { Path = "/r/pkgs/microsoft.netcore.app.host.win-x64/8.0.31/runtimes/win-x64/native/apphost.exe"; Sha256 = "ab" }))
              Files = [ { Name = "app.deps.json"; Path = "/r/proj/locks/app/Release.files/net8.0/app/app.deps.json"; Sha256 = "cd" } ]
              Copy = [ { Path = "/r/pkgs/newtonsoft.json/13.0.3/lib/net6.0/Newtonsoft.Json.dll"; Sha256 = "ef"; To = "Newtonsoft.Json.dll" }
                       { Path = lib.Path; Sha256 = ""; To = "lib.dll" } ]
              Content = [ { Path = "/r/proj/app/data/a.txt"; Sha256 = ""; To = "data/a.txt" }
                          { Path = "/r/pkgs/nunit3testadapter/4.2.1/build/netcoreapp3.1/NUnit3.TestAdapter.dll"; Sha256 = "01"; To = "NUnit3.TestAdapter.dll" } ]
              Related = [ ".pdb"; ".xml" ]
              BindingRedirects = false }
        let c = { Xake.Dotnet.Csc.ofArgs [ "/noconfig"; "/r/proj/app/P.cs" ] with Name = "app"; Framework = "net8.0"; Directory = "/r/proj/app" }
        let c = { c with Dependencies = { c.Dependencies with Compiler = { Tool = "csc"; Path = "/r/dotnet/sdk/8.0.425/Roslyn/bincore/csc.dll"; Sha256 = ""; Version = "" } } }
        let entry =
            { Lock.ofCsc c with
                Evaluation = { Lock.Evaluation.Empty with Project = "/r/proj/app/app.csproj"; Sdk = "8.0.425" }
                Recorded = Some { Projects = [ lib ]; Masks = []; Placed = None; App = Some app } }
        let text = Lock.format roots { Configuration = "Release"; Properties = []; Entries = [ entry ] }
        Assert.That(text, Does.Contain "\"Host\": { \"Rid\": \"win-x64\", \"Id\": \"pack:microsoft.netcore.app.host.win-x64/8.0.31/runtimes/win-x64/native/apphost.exe\", \"Sha256\": \"ab\" }")
        Assert.That(text, Does.Contain "{ \"Id\": \"project:lib\", \"Sha256\": \"\", \"To\": \"lib.dll\" }")
        Assert.That(text, Does.Contain "\"Path\": \"$(ProjectRoot)/locks/app/Release.files/net8.0/app/app.deps.json\"")
        Assert.That(text, Does.Contain "{ \"Id\": \"$(ProjectRoot)/app/data/a.txt\", \"Sha256\": \"\", \"To\": \"data/a.txt\" }")
        Assert.That(text, Does.Contain "{ \"Id\": \"pkg:nunit3testadapter/4.2.1/build/netcoreapp3.1/NUnit3.TestAdapter.dll\", \"Sha256\": \"01\", \"To\": \"NUnit3.TestAdapter.dll\" }")
        let back = Lock.parse roots text
        Assert.That(back.Entries.Head.Recorded.Value.App, Is.EqualTo (Some app))
        Assert.That(Lock.format roots back, Is.EqualTo text)


/// Applications recorded from msbuild and built from their locks (`Import.record`,
/// `Locks.compileAs`): a console app (net8.0, no RID: the SDK's apphost, copy-local packages
/// with `runtimes/<rid>/` assets), a WinExe (net472: `.exe`, `.exe.config`), an app with an
/// explicit RID (win-x64: the apphost pack pinned with its hash), a Test.Sdk test project --
/// each against `dotnet build`: the same files, the same `deps.json` and `runtimeconfig.json`,
/// and the app runs.
[<TestFixture>]
type ``Import record applications``() =
    inherit XakeTestBase("import-record-apps")

    let projects = [ "lib/lib.csproj"; "console/console.csproj"; "winexe/winexe.csproj"; "ridapp/ridapp.csproj"; "tests/tests.csproj" ]
    let apps = [ "console"; "winexe"; "ridapp"; "tests" ]
    let main = "class P { static void Main() { System.Console.WriteLine(Lib.C.S()); } }\n"

    let fixture () =
        RecordKit.write [ "lib"; "console"; "winexe"; "ridapp"; "tests"; "out"; "run"; "locks"; "obj" ]
            [ "lib/lib.csproj",
                RecordKit.sdkProject "<TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks><AssemblyName>Lib.Core</AssemblyName><Version>2.3.4</Version><GenerateDocumentationFile>true</GenerateDocumentationFile><NoWarn>$(NoWarn);1591</NoWarn>"
                    "<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />"
              "lib/C.cs", "namespace Lib { public class C { public static string S() => Newtonsoft.Json.JsonConvert.SerializeObject (new [] { 1, 2 }); } }\n"
              "console/console.csproj",
                RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType>"
                    "<ProjectReference Include=\"../lib/lib.csproj\" /><PackageReference Include=\"SQLitePCLRaw.lib.e_sqlite3\" Version=\"2.1.6\" />"
              "console/P.cs", main
              // the project's own config: msbuild copies it as it is (BOM, CRLF)
              "console/App.config", "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<configuration>\r\n</configuration>\r\n"
              // the source of its runtimeconfig.json's configProperties
              "console/runtimeconfig.template.json", "{ \"configProperties\": { \"System.Globalization.Invariant\": true } }\n"
              "winexe/winexe.csproj",
                RecordKit.sdkProject "<TargetFramework>net472</TargetFramework><OutputType>WinExe</OutputType>"
                    "<ProjectReference Include=\"../lib/lib.csproj\" />"
              "winexe/P.cs", main
              "winexe/App.config", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <appSettings>\n    <add key=\"a\" value=\"b\" />\n  </appSettings>\n</configuration>\n"
              "ridapp/ridapp.csproj",
                RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><RuntimeIdentifier>win-x64</RuntimeIdentifier>"
                    "<ProjectReference Include=\"../lib/lib.csproj\" />"
              "ridapp/P.cs", main
              "tests/tests.csproj",
                RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework><IsPackable>false</IsPackable>"
                    ("<ProjectReference Include=\"../lib/lib.csproj\" />"
                     + "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.6.0\" /><PackageReference Include=\"NUnit\" Version=\"3.13.3\" />"
                     + "<PackageReference Include=\"NUnit3TestAdapter\" Version=\"4.2.1\" />")
              "tests/T.cs", "namespace T { [NUnit.Framework.TestFixture] public class A { [NUnit.Framework.Test] public void B() => NUnit.Framework.Assert.AreEqual(\"[1,2]\", Lib.C.S()); } }\n" ]

    /// stdout of `exe args` in `dir`; fails on a non-zero exit.
    let runProcess (exe: string) (args: string list) (dir: string) =
        let psi = ProcessStartInfo (exe)
        // no msbuild node left holding the redirected output (ReadToEnd would wait for it)
        psi.Environment.["MSBUILDDISABLENODEREUSE"] <- "1"
        psi.WorkingDirectory <- dir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        for a in args do psi.ArgumentList.Add a
        use p = Process.Start psi
        let out = p.StandardOutput.ReadToEnd ()
        let err = p.StandardError.ReadToEnd ()
        p.WaitForExit ()
        if p.ExitCode <> 0 then failwithf "'%s %s' failed (exit %d):\n%s\n%s" exe (String.Join (" ", args)) p.ExitCode out err
        out

    /// The files under `dir`, relative, '/', sorted.
    let filesOf (dir: string) =
        Directory.GetFiles (dir, "*", SearchOption.AllDirectories)
        |> Array.filter (fun f -> not (Path.GetFileName(f).StartsWith ".xake-run"))
        |> Array.map (fun f -> Path.GetRelativePath(dir, f).Replace ('\\', '/')) |> Array.sort |> List.ofArray

    /// The lock texts and the files beside them, by path.
    let recorded () =
        [ for f in Directory.GetFiles ("locks", "*", SearchOption.AllDirectories) |> Array.sort -> f, File.ReadAllText f ]

    let mutable prepared = false
    let mutable firstImport : (string * string) list = []

    member private x.Prepare () =
        if not prepared then
            fixture ()
            RecordKit.import x.TestOptions "import-1" RecordKit.recordDefault projects |> ignore
            firstImport <- recorded ()
            RecordKit.build x.TestOptions "build-1" "Release"
            RecordKit.deploy x.TestOptions "deploy-1" "Release" RunFolder.Link
            for a in apps do RecordKit.runDotnet (Path.GetFullPath a) [ "build"; a + ".csproj"; "-c"; "Release"; "-p:NuGetAudit=false" ]
            prepared <- true

    /// (Xake's run folder, msbuild's output folder) of an application.
    member private x.Outputs (name: string) (msbuildDir: string) =
        x.Prepare ()
        sprintf "run/Release/%s/%s" (if name = "winexe" then "net472" else "net8.0") name, name + "/bin/Release/" + msbuildDir

    [<Test>]
    member x.``the locks hold no host string and an import after both builds records the same``() =
        x.Prepare ()
        let here = (Directory.GetCurrentDirectory ()).Replace ('\\', '/')
        let home = (Environment.GetFolderPath Environment.SpecialFolder.UserProfile).Replace ('\\', '/')
        let rid = Text.RegularExpressions.Regex "(?<!runtimes/|\"rid\": \")osx-arm64"
        for path, text in firstImport do
            Assert.That(text, Does.Not.Contain here, path)
            Assert.That(text, Does.Not.Contain home, path)
            Assert.That(text, Does.Not.Contain "\r", path)
            Assert.That(rid.IsMatch text, Is.False, path)
        // after Xake's build and msbuild's (the referenced project built: deps.json versions read
        // from it), the import writes the same locks and files
        RecordKit.import x.TestOptions "import-2" RecordKit.recordDefault projects |> ignore
        Assert.That(recorded (), Is.EqualTo firstImport)

    [<Test>]
    member x.``a console app: the SDK's apphost, copy-local runtimes, deps.json as msbuild's, and it runs``() =
        let ours, theirs = x.Outputs "console" "net8.0"
        let lock = File.ReadAllText (RecordKit.lockOf "console" "Release")
        Assert.That(lock, Does.Contain "\"Host\": { \"Rid\": \"\" }")
        Assert.That(lock, Does.Contain "\"To\": \"runtimes/linux-x64/native/libe_sqlite3.so\"")
        Assert.That(lock, Does.Contain "{ \"Id\": \"project:lib\", \"Sha256\": \"\", \"To\": \"Lib.Core.dll\" }")
        Assert.That(filesOf ours, Is.EqualTo (filesOf theirs))
        for f in [ "console.deps.json"; "console.runtimeconfig.json" ] do
            Assert.That(File.ReadAllText (ours </> f), Is.EqualTo (File.ReadAllText (theirs </> f)), f)
        // named where it is, by its hash, and copied byte for byte
        Assert.That(lock, Does.Contain "{ \"Name\": \"console.dll.config\", \"Path\": \"$(ProjectRoot)/console/App.config\"")
        Assert.That(File.ReadAllBytes (ours </> "console.dll.config"), Is.EqualTo (File.ReadAllBytes (theirs </> "console.dll.config")))
        Assert.That(runProcess "dotnet" [ "console.dll" ] (Path.GetFullPath ours), Does.StartWith "[1,2]")
        let apphost = Path.GetFullPath (ours </> (if Env.isWindows then "console.exe" else "console"))
        Assert.That(runProcess apphost [] (Path.GetFullPath ours), Does.StartWith "[1,2]")

    [<Test>]
    member x.``a .NET Framework WinExe: the exe and its config as msbuild's``() =
        let ours, theirs = x.Outputs "winexe" "net472"
        let lock = File.ReadAllText (RecordKit.lockOf "winexe" "Release")
        Assert.That(lock, Does.Contain "\"OutputType\": \"WinExe\"")
        Assert.That(lock, Does.Not.Contain "\"Host\"")
        Assert.That(lock, Does.Contain "\"BindingRedirects\": true")
        Assert.That(filesOf ours, Is.EqualTo (filesOf theirs))
        Assert.That(File.ReadAllText (ours </> "winexe.exe.config"), Is.EqualTo (File.ReadAllText (theirs </> "winexe.exe.config")))
        Assert.That(File.ReadAllText (ours </> "winexe.exe.config"), Does.Contain "<supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.7.2\" />")

    [<Test>]
    member x.``an edited App.config or runtimeconfig template fails the build: run the import again``() =
        x.Prepare ()
        Assert.That(File.ReadAllText (RecordKit.lockOf "winexe" "Release"), Does.Contain "\"Path\": \"$(ProjectRoot)/winexe/App.config\"")
        Assert.That(File.ReadAllText (RecordKit.lockOf "console" "Release"), Does.Contain "\"Path\": \"$(ProjectRoot)/console/runtimeconfig.template.json\"")
        for file in [ "winexe/App.config"; "console/runtimeconfig.template.json" ] do
            let text = File.ReadAllText file
            try
                File.AppendAllText (file, "\n")
                let e = Assert.Catch(fun () -> RecordKit.build x.TestOptions "build-app-stale" "Release")
                Assert.That(string e, Does.Contain (sprintf "/%s' changed since the import that wrote it; run the import again" file))
            finally File.WriteAllText (file, text)
        RecordKit.build x.TestOptions "build-app-stale-2" "Release"

    [<Test>]
    member x.``an explicit RID: the apphost pack pinned with its hash, its bytes in the output``() =
        let ours, theirs = x.Outputs "ridapp" "net8.0/win-x64"
        let lock = File.ReadAllText (RecordKit.lockOf "ridapp" "Release")
        let host = Text.RegularExpressions.Regex.Match (lock, "\"Host\": \\{ \"Rid\": \"win-x64\", \"Id\": \"pack:microsoft\\.netcore\\.app\\.host\\.win-x64/[^/]+/runtimes/win-x64/native/apphost\\.exe\", \"Sha256\": \"([0-9a-f]{64})\" \\}")
        Assert.That(host.Success, lock)
        Assert.That(lock, Does.Contain "\"RuntimeIdentifier\": \"win-x64\"")
        Assert.That(filesOf ours, Is.EqualTo (filesOf theirs))
        Assert.That(File.ReadAllText (ours </> "ridapp.deps.json"), Is.EqualTo (File.ReadAllText (theirs </> "ridapp.deps.json")))
        // the apphost from the pinned template: the SDK's HostWriter when it loads here (then as
        // msbuild's), else the template with the application's name and no resources
        let packages = match Environment.GetEnvironmentVariable "NUGET_PACKAGES" with null | "" -> Environment.GetFolderPath Environment.SpecialFolder.UserProfile </> ".nuget/packages" | p -> p
        let template =
            let id = Text.RegularExpressions.Regex.Match(lock, "\"Id\": \"pack:([^\"]+apphost\\.exe)\"").Groups.[1].Value
            File.ReadAllBytes (packages </> id)
        let exe = File.ReadAllBytes (ours </> "ridapp.exe")
        let sdk = Text.RegularExpressions.Regex.Match(lock, "\"Sdk\": \"([^\"]+)\"").Groups.[1].Value
        let sdkDir =
            runProcess "dotnet" [ "--list-sdks" ] "." |> fun t -> t.Split '\n'
            |> Array.pick (fun l -> if l.StartsWith (sdk + " [") then Some (l.Substring(sdk.Length + 2).TrimEnd(']', '\r') </> sdk) else None)
        if Apps.hostModelUsable sdkDir then
            Assert.That(exe, Is.EqualTo (File.ReadAllBytes (theirs </> "ridapp.exe")))
        else
            Assert.That(exe.Length, Is.EqualTo template.Length)
            let differing = [ for i in 0 .. exe.Length - 1 do if exe.[i] <> template.[i] then yield i ]
            Assert.That(differing.Length, Is.GreaterThan 0)
            Assert.That(List.last differing - List.head differing, Is.LessThan 64)
        // not run: a win-x64 application does not start on another OS (msbuild's neither)

    [<Test>]
    member x.``a Test.Sdk project: copy-local, the adapter's items, deps.json and runtimeconfig as msbuild's``() =
        let ours, theirs = x.Outputs "tests" "net8.0"
        Assert.That(filesOf ours, Is.EqualTo (filesOf theirs))
        // the adapter's files come from `None` items of the package's build props (`Content`)
        let lock = File.ReadAllText (RecordKit.lockOf "tests" "Release")
        Assert.That(lock, Does.Match "\"Id\": \"pkg:nunit3testadapter/4\\.2\\.1/build/netcoreapp[0-9.]+/NUnit3\\.TestAdapter\\.dll\", \"Sha256\": \"[0-9a-f]{64}\", \"To\": \"NUnit3\\.TestAdapter\\.dll\"")
        Assert.That(lock, Does.Contain "\"IsTestProject\": \"true\"")
        for f in [ "tests.deps.json"; "tests.runtimeconfig.json" ] do
            Assert.That(File.ReadAllText (ours </> f), Is.EqualTo (File.ReadAllText (theirs </> f)), f)
        // its generated entry point runs (the runtime finds the framework and the copy-local files)
        runProcess "dotnet" [ "tests.dll" ] (Path.GetFullPath ours) |> ignore
