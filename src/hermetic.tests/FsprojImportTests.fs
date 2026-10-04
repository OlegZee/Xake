namespace Tests

open System
open System.IO
open System.Diagnostics
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// `Project.import` of an fsproj: the design-time build reports `FscCommandLineArgs`
/// (the F# targets honour `ProvideCommandLineArgs`/`SkipCompilerExecution` like the C#
/// ones), the entry is `Compilation.Fsc` with the SDK's `fsc.dll` (`DotnetFscCompilerPath`)
/// as its compiler. A two-project solution -- an F# application referencing a C# library --
/// is imported into one lock and both entries are compiled from it with `Lock.compile`; the
/// outputs are compared with what `dotnet build` writes to the same intermediate directory.
[<TestFixture>]
type ``Project import of an fsproj``() =
    inherit XakeTestBase("fsproj-import")

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

    let csproj framework = sprintf """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>%s</TargetFramework>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
"""                                framework

    let fsproj framework = sprintf """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>%s</TargetFramework>
    <Deterministic>true</Deterministic>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Greeting.fs" />
    <Compile Include="Program.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Core/Core.csproj" />
  </ItemGroup>
</Project>
"""                                framework

    [<Test; Category("Integration")>]
    member x.``imports an fsproj and a csproj into one lock and compiles both from it``() =
        let dir = Directory.GetCurrentDirectory ()
        // the SDK's own framework: its targeting pack is always there
        let framework = DotNetFwk.sdkFramework x.TestOptions.ProjectRoot |> Option.get
        let coreDir, appDir = dir </> "Core", dir </> "App"
        for d in [ coreDir; appDir ] do
            if Directory.Exists d then Directory.Delete (d, true)
            Directory.CreateDirectory d |> ignore
        File.WriteAllText (coreDir </> "Core.csproj", csproj framework)
        File.WriteAllText (coreDir </> "Names.cs", "namespace Core { public static class Names { public static string World => \"World\"; } }\n")
        File.WriteAllText (appDir </> "App.fsproj", fsproj framework)
        File.WriteAllText (appDir </> "Greeting.fs", "module Greeting\n/// Greets\nlet greet (name: string) = sprintf \"Hello, %s\" name\n")
        File.WriteAllText (appDir </> "Program.fs", "module Program\nlet message () = Greeting.greet Core.Names.World\n")

        // the baseline: `dotnet build` into the intermediate directory the import uses
        // (`obj/xake/<framework>/`), so every path compiled into the outputs is the same
        let intermediate = sprintf "obj/xake/%s/" framework
        runDotnet appDir [ "build"; "App.fsproj"; "-c"; "Release"; "-p:NuGetAudit=false"; "-p:IntermediateOutputPath=" + intermediate ]
        let appDll, coreDll = appDir </> intermediate </> "App.dll", coreDir </> intermediate </> "Core.dll"
        let baselineDir = dir </> "baseline"
        Directory.CreateDirectory baselineDir |> ignore
        File.Copy (appDll, baselineDir </> "App.dll", true)
        File.Copy (coreDll, baselineDir </> "Core.dll", true)

        let lockFile = dir </> "fsproj.lock.json"
        do xake {x.TestOptions with FileLog = "fsproj-import.log"; ThrowOnError = true; Vars = [ "CI", "off" ]} {
            wantOverride ([ "import" ])
            rules [
                "import" => Project.import {
                    Project.ImportOptions.Default with
                        Projects = [ coreDir </> "Core.csproj"; appDir </> "App.fsproj" ]
                        Frameworks = [ framework ]
                        Configuration = "Release"
                        Output = lockFile }
            ]
        }

        let lock = Lock.read (Roots.builtin dir) lockFile
        let core, app = Lock.entryFor framework "Core" lock, Lock.entryFor framework "App" lock
        Assert.That(core.Dependencies.Compiler.Tool, Is.EqualTo "csc")
        Assert.That(File.ReadAllText lockFile, Does.Contain "\"Tool\": \"fsc\"")
        match app.Compilation with
        | Lock.Compilation.Fsc f ->
            Assert.That(f.Dependencies.Compiler.Tool, Is.EqualTo "fsc")
            Assert.That(f.Dependencies.Compiler.Path, Does.EndWith "/FSharp/fsc.dll")
            Assert.That(f.Dependencies.Compiler.Sha256, Is.Not.Empty)
            Assert.That(f.Dependencies.Compiler.Version, Is.Not.Empty)
            Assert.That(f.Dependencies.Analyzers, Is.Empty)
            Assert.That(f.Options, Does.Not.Contain "@Analyzers")
            // the compile order is the project's, after msbuild's generated files
            let sources = f.Sources |> List.map Path.GetFileName
            Assert.That(sources |> List.filter (fun s -> s = "Greeting.fs" || s = "Program.fs"), Is.EqualTo [ "Greeting.fs"; "Program.fs" ])
            Assert.That(f.Generated |> List.exists (fun (p, _) -> p.EndsWith ".AssemblyInfo.fs"), Is.True)
            Assert.That(f.Dependencies.References |> List.exists (fun r -> r.Path.EndsWith "FSharp.Core.dll" && r.Sha256 <> ""), Is.True)
            Assert.That(f.Dependencies.References |> List.exists (fun r -> Path.GetFileName r.Path = "Core.dll"), Is.True)
            Assert.That(app.Evaluation.Project, Does.EndWith "App.fsproj")
            Assert.That(app.Evaluation.ProjectRefs |> List.map Path.GetFileName, Is.EqualTo [ "Core.csproj" ])
            Assert.That(app.Packages |> List.exists (fun p -> p.Id = "FSharp.Core" && p.Direct), Is.True)
            Assert.That(app.Output, Is.EqualTo (Some (appDll.Replace ('\\', '/'))))
        | Lock.Compilation.Csc _ -> Assert.Fail "the fsproj was imported as a C# entry"

        // both compiled from the lock, the library first: the application's reference to it
        // is whatever the import recorded, and nothing here rewrites it
        for f in [ appDll; coreDll ] do File.Delete f
        do xake {x.TestOptions with FileLog = "fsproj-compile.log"; ThrowOnError = true; Vars = [ "CI", "off" ]} {
            wantOverride ([ "compile" ])
            rules [
                "compile" => recipe {
                    do! Lock.compile core
                    do! Lock.compile app
                }
            ]
        }

        Assert.That(File.Exists coreDll, Is.True, "Core.dll was not compiled from the lock")
        Assert.That(File.Exists appDll, Is.True, "App.dll was not compiled from the lock")
        Assert.That(File.ReadAllBytes coreDll, Is.EqualTo (File.ReadAllBytes (baselineDir </> "Core.dll")),
            "the C# library compiled from the lock differs from dotnet build's")
        Assert.That(File.ReadAllBytes appDll, Is.EqualTo (File.ReadAllBytes (baselineDir </> "App.dll")),
            "the F# application compiled from the lock differs from dotnet build's")
