namespace Tests

open System
open System.IO
open System.Diagnostics
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// `.resx` -> `.resources`, without `System.Windows.Forms`'s `ResXResourceReader`: the pure
/// reader/writer in `Xake.Dotnet.Resx`, byte-identical to msbuild's output (see
/// `docs/csc-syntax.md`).
[<TestFixture>]
type ``Resx resources``() =
    inherit XakeTestBase("resx")

    let writeTemp (content: string) =
        let path = Path.Combine (Path.GetTempPath(), "xake-resx-test-" + string (Guid.NewGuid()) + ".resx")
        File.WriteAllText (path, content)
        path

    /// Runs `dotnet <args>` in `workDir`, failing with both streams on a non-zero exit.
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

    /// A folder beside this fixture's, not in it: the msbuild tests glob the fixture's folder
    /// for `*.resx`.
    let formatDir () = Path.GetDirectoryName (Directory.GetCurrentDirectory ()) </> "resx-format"

    [<Test>]
    member x.``reads string entries in document order``() =
        let resx =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<root>\n" +
            "  <resheader name=\"resmimetype\"><value>text/microsoft-resx</value></resheader>\n" +
            "  <resheader name=\"version\"><value>2.0</value></resheader>\n" +
            "  <data name=\"Greeting\" xml:space=\"preserve\">\n" +
            "    <value>Hello,\n   World</value>\n" +
            "    <comment>a friendly one</comment>\n" +
            "  </data>\n" +
            "  <data name=\"Copyright\" xml:space=\"preserve\"><value>© 2026 Xüke 🎉</value></data>\n" +
            "  <data name=\"Empty\" xml:space=\"preserve\"><value></value></data>\n" +
            "</root>\n"
        let path = writeTemp resx
        try
            Assert.That(Resx.read path, Is.EqualTo [
                "Greeting", "Hello,\n   World"
                "Copyright", "© 2026 Xüke 🎉"
                "Empty", "" ])
        finally File.Delete path

    [<Test>]
    member x.``refuses typed and file-ref entries``() =
        let resx =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<root>\n" +
            "  <data name=\"Color1\" type=\"System.Drawing.Color, System.Drawing\">\n" +
            "    <value>Red</value>\n" +
            "  </data>\n" +
            "</root>\n"
        let path = writeTemp resx
        try
            let ex = Assert.Throws<exn> (fun () -> Resx.read path |> ignore)
            Assert.That(ex.Message, Does.Contain "Color1")
            Assert.That(ex.Message, Does.Contain "not supported")
        finally File.Delete path

    [<Test; Category("Integration")>]
    member x.``Resx.compile writes the same bytes as msbuild``() =
        let dir = Directory.GetCurrentDirectory ()

        let csproj = """<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <Deterministic>true</Deterministic>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>

</Project>
"""
        let helloCs = "public class Hello {}\n"
        // The real projects this feature targets only ever have string entries: a multi-line
        // value with leading spaces, a unicode value and an empty value, same as the ones
        // `reads string entries in document order` exercises against the reader alone.
        let stringsResx =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<root>\n" +
            "  <resheader name=\"resmimetype\"><value>text/microsoft-resx</value></resheader>\n" +
            "  <resheader name=\"version\"><value>2.0</value></resheader>\n" +
            "  <resheader name=\"reader\"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\n" +
            "  <resheader name=\"writer\"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\n" +
            "  <data name=\"Greeting\" xml:space=\"preserve\">\n" +
            "    <value>Hello,\n   World</value>\n" +
            "  </data>\n" +
            "  <data name=\"Copyright\" xml:space=\"preserve\"><value>© 2026 Xüke 🎉</value></data>\n" +
            "  <data name=\"Empty\" xml:space=\"preserve\"><value></value></data>\n" +
            "</root>\n"

        let projectFile = dir </> "Sample.csproj"
        File.WriteAllText (projectFile, csproj)
        File.WriteAllText (dir </> "Hello.cs", helloCs)
        let resxFile = dir </> "Strings.resx"
        File.WriteAllText (resxFile, stringsResx)

        // The baseline: what msbuild itself compiles the resx to, into its own obj so the
        // xake-driven import (a different IntermediateOutputPath) never touches it.
        runDotnet dir [ "build"; "Sample.csproj"; "-c"; "Release"; "-p:NuGetAudit=false"; "-p:IntermediateOutputPath=obj/ref/" ]

        let refResources =
            Directory.GetFiles (dir </> "obj" </> "ref", "*.resources", SearchOption.AllDirectories)
        Assert.That(refResources, Has.Length.EqualTo 1, "expected exactly one .resources under obj/ref")
        let baselineDir = dir </> "baseline"
        Directory.CreateDirectory baselineDir |> ignore
        let baselineResources = baselineDir </> "Sample.Strings.resources"
        File.Copy (refResources.[0], baselineResources, true)

        // the pure writer, no lock and no import involved
        let resourcesOutput = dir </> "out" </> "Sample.Strings.resources"
        Resx.compile resxFile resourcesOutput
        Assert.That(File.Exists resourcesOutput, Is.True, "the .resources file was not written")

        Assert.That(File.ReadAllBytes resourcesOutput, Is.EqualTo (File.ReadAllBytes baselineResources),
            "Resx.compile's .resources differs from msbuild's byte for byte")

    /// The kinds of entries a WinForms resx holds (typed values converted from a string, a
    /// null, bitmaps and icons as bytes, a BinaryFormatter payload, `ResXFileRef` to a text
    /// and a binary file, aliases completing the type names, a repeated name), compiled by
    /// `Resx.compile` and by the SDK's `GenerateResource` with preserialized resources (net8.0
    /// and System.Resources.Extensions): the same bytes.
    [<Test; Category("Integration")>]
    member x.``Resx.compile writes the same bytes as msbuild for typed and file-ref entries``() =
        // beside this fixture's folder, not in it: the other msbuild test globs that folder
        let dir = Path.GetDirectoryName (Directory.GetCurrentDirectory ()) </> "resx-typed"
        if Directory.Exists dir then Directory.Delete (dir, true)
        Directory.CreateDirectory (dir </> "Resources") |> ignore
        let typedProject = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <GenerateResourceUsePreserializedResources>true</GenerateResourceUsePreserializedResources>
    <EnableSourceLink>false</EnableSourceLink>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.Resources.Extensions" Version="8.0.0" />
  </ItemGroup>
</Project>
"""
        File.WriteAllText (dir </> "Typed.csproj", typedProject)
        File.WriteAllText (dir </> "Hello.cs", "public class Hello {}\n")
        File.WriteAllText (dir </> "Resources" </> "Prompt.txt", "﻿Line one\nLine two: ü\n")
        File.WriteAllBytes (dir </> "Resources" </> "Blob.bin", [| 0uy; 1uy; 127uy; 255uy |])
        let b64 = Convert.ToBase64String [| 1uy; 2uy; 3uy; 4uy; 5uy; 6uy |]
        let resx =
            String.concat "\n"
                [ "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                  "<root>"
                  "  <resheader name=\"resmimetype\"><value>text/microsoft-resx</value></resheader>"
                  "  <assembly alias=\"System.Windows.Forms\" name=\"System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\" />"
                  "  <assembly alias=\"System.Drawing\" name=\"System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a\" />"
                  "  <data name=\"Text\" xml:space=\"preserve\"><value>plain</value></data>"
                  "  <data name=\"Blank\"><value>   </value></data>"
                  "  <data name=\"Size\" type=\"System.Drawing.Size, System.Drawing\"><value>10, 20</value></data>"
                  "  <data name=\"Count\" type=\"System.Int32, mscorlib\"><value>7</value></data>"
                  "  <data name=\"Flag\" type=\"System.Boolean, mscorlib\"><value>True</value></data>"
                  "  <data name=\"Anchor\" type=\"System.Windows.Forms.AnchorStyles, System.Windows.Forms\"><value>Top, Left</value></data>"
                  "  <data name=\"Nothing\" type=\"System.Resources.ResXNullRef, System.Windows.Forms\"><value /></data>"
                  "  <data name=\"Bytes\" type=\"System.Byte[], mscorlib\"><value>" + b64 + "</value></data>"
                  "  <data name=\"Image\" type=\"System.Drawing.Bitmap, System.Drawing\" mimetype=\"application/x-microsoft.net.object.bytearray.base64\"><value>" + b64 + "</value></data>"
                  "  <data name=\"Stream\" mimetype=\"application/x-microsoft.net.object.binary.base64\"><value>" + b64 + "</value></data>"
                  "  <data name=\"Prompt\" type=\"System.Resources.ResXFileRef, System.Windows.Forms\"><value>Resources\\Prompt.txt;System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;utf-8</value></data>"
                  "  <data name=\"Blob\" type=\"System.Resources.ResXFileRef, System.Windows.Forms\"><value>Resources\\Blob.bin;System.Byte[], mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></data>"
                  "  <data name=\"Text\"><value>repeated: the first wins</value></data>"
                  "</root>"
                  "" ]
        File.WriteAllText (dir </> "Typed.resx", resx)
        runDotnet dir [ "build"; "Typed.csproj"; "-c"; "Release"; "-p:NuGetAudit=false" ]
        let reference = Directory.GetFiles (dir </> "obj", "Typed.Typed.resources", SearchOption.AllDirectories)
        Assert.That(reference, Has.Length.EqualTo 1)
        let ours = dir </> "ours" </> "Typed.Typed.resources"
        Resx.compile (dir </> "Typed.resx") ours
        Assert.That(File.ReadAllBytes ours, Is.EqualTo (File.ReadAllBytes reference.[0]), "the .resources differs from msbuild's byte for byte")

    [<Test>]
    member x.``a ResXFileRef value splits as msbuild splits it``() =
        Assert.That(ResxFormat.parseFileRef "Resources\\T.txt;System.String, mscorlib, Version=4.0.0.0;utf-8",
            Is.EqualTo (("Resources\\T.txt", "System.String, mscorlib, Version=4.0.0.0", Some "utf-8")))
        Assert.That(ResxFormat.parseFileRef "\"a b.bin\";System.Byte[], mscorlib",
            Is.EqualTo (("a b.bin", "System.Byte[], mscorlib", None)))

    [<Test>]
    member x.``a ResXFileRef in a code page reads as msbuild reads it``() =
        let d = formatDir () </> "codepage"
        Directory.CreateDirectory d |> ignore
        // "Привет" in windows-1251
        File.WriteAllBytes (d </> "Text.txt", [| 0xCFuy; 0xF0uy; 0xE8uy; 0xE2uy; 0xE5uy; 0xF2uy |])
        File.WriteAllText (d </> "R.resx",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n"
            + "  <resheader name=\"resmimetype\"><value>text/microsoft-resx</value></resheader>\n"
            + "  <data name=\"Text\" type=\"System.Resources.ResXFileRef, System.Windows.Forms\"><value>Text.txt;System.String, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089;windows-1251</value></data>\n"
            + "</root>\n")
        Assert.That(ResxFormat.read (d </> "R.resx"), Is.EqualTo [ ResxFormat.Text ("Text", "Привет") ])

    [<Test>]
    member x.``a resx of strings compiles to the classic format, a typed one to the preserialized``() =
        let d = formatDir () </> "format"
        if Directory.Exists d then Directory.Delete (d, true)
        Directory.CreateDirectory d |> ignore
        let resx (body: string) =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n"
            + "  <resheader name=\"resmimetype\"><value>text/microsoft-resx</value></resheader>\n"
            + "  <data name=\"Hello\" xml:space=\"preserve\"><value>Hello</value></data>\n"
            + body + "</root>\n"
        File.WriteAllText (d </> "Strings.resx", resx "")
        File.WriteAllText (d </> "Typed.resx",
            resx "  <data name=\"Size\" type=\"System.Drawing.Size, System.Drawing\"><value>10, 20</value></data>\n")
        let strings = ResxFormat.compileBytes (d </> "Strings.resx")
        let typed = ResxFormat.compileBytes (d </> "Typed.resx")
        Assert.That(ResxFormat.readerTypeOf strings, Does.StartWith "System.Resources.ResourceReader")
        Assert.That(ResxFormat.needsExtensions strings, Is.False)
        Assert.That(ResxFormat.needsExtensions typed, Is.True)
        Assert.That(ResxFormat.needsExtensions [| 1uy; 2uy; 3uy |], Is.False)
        // Resx.refresh writes once, then keeps an unchanged file
        let out = d </> "out" </> "Typed.resources"
        Assert.That(Resx.refresh (d </> "Typed.resx") out, Is.True)
        Assert.That(Resx.refresh (d </> "Typed.resx") out, Is.False)
        Assert.That(File.ReadAllBytes out, Is.EqualTo typed)
