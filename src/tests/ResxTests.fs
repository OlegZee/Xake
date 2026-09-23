namespace Tests

open System
open System.IO
open System.Diagnostics
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// `.resx` -> `.resources`, without `System.Windows.Forms`'s `ResXResourceReader`: the pure
/// reader/writer in `Xake.Dotnet.Resx`, and the lock/`run` plumbing that regenerates a
/// project's `.resources` files from their resx on a machine that only has the lock (see
/// `docs/features/hermetic-build/csc-syntax.md`).
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
    member x.``writes the same bytes as msbuild``() =
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

        let lockFile = dir </> "resx.lock.json"

        do xake {x.TestOptions with FileLog="resx-import.log"; ThrowOnError = true} {
            wantOverride (["build"])

            rules [
                "build" => recipe {
                    do! Project.import {
                        Project.ImportOptions.Default with
                            Projects = [ projectFile ]
                            Framework = "netstandard2.0"
                            Configuration = "Release"
                            Variant = "x"
                            Output = lockFile
                    }
                }
            ]
        }

        let lock = Lock.readWith (Roots.builtin (Directory.GetCurrentDirectory())) lockFile
        let project = Lock.project "Sample" lock

        Assert.That(project.Resources, Has.Length.EqualTo 1, "expected exactly one resx recorded in the lock")
        let (importedResx, resourcesOutput) = project.Resources.Head
        Assert.That(importedResx, Is.EqualTo (resxFile.Replace ('\\', '/')))

        // clean slate: the import already ran PrepareResources so the .resources exists; make
        // sure `run` is the one that (re)writes it, not a leftover from the import
        File.Delete resourcesOutput
        Assert.That(File.Exists resourcesOutput, Is.False)

        do xake {x.TestOptions with FileLog="resx-compile.log"; ThrowOnError = true} {
            wantOverride (["compile"])

            rules [
                "compile" => recipe {
                    do! CscLock.compile project
                }
            ]
        }

        Assert.That(File.Exists resourcesOutput, Is.True, "the .resources file was not regenerated")

        let ours = File.ReadAllBytes resourcesOutput
        let baseline = File.ReadAllBytes baselineResources
        Assert.That(ours, Is.EqualTo baseline,
            "the regenerated .resources differs from msbuild's byte for byte")
