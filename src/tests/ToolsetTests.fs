namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// The compiler-as-a-package path: a `Toolset.csproj` (the same content as
/// `samples/hermetic/toolset/Toolset.csproj`, kept there as a sample for humans) pins its C#
/// compiler with a `Microsoft.Net.Compilers.Toolset` `PackageReference` (plus
/// `RoslynCompilerType=Toolset`, without which the SDK silently reverts to its own compiler --
/// see the comment on `Project.parseImport`'s `compilerPath`). These tests exercise both ways
/// `Lock.Project.Compiler.Path` can end up naming that package: the import reading it out of
/// the project (`CSharpCoreTargetsPath`, since the package never sets `CscToolPath`), and the
/// composed `csc {}` mode taking it directly via the `toolset` operation. The project and its
/// source are written into this fixture's own sandbox so the tests do not depend on `samples/`.
[<TestFixture>]
type ``Toolset compiler``() =
    inherit XakeTestBase("toolset")

    let toolsetVersion = "4.12.0"

    let toolsetCsproj = """<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <Deterministic>true</Deterministic>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <!-- Without this the SDK silently reverts CSharpCoreTargetsPath to its own Roslyn, even
         with the PackageReference below present: see Microsoft.NET.Sdk.BeforeCommon.targets,
         "RoslynCompilerType specified by user, do not overwrite it." -->
    <RoslynCompilerType>Toolset</RoslynCompilerType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Net.Compilers.Toolset" Version="4.12.0" PrivateAssets="all" />
  </ItemGroup>

</Project>
"""

    let helloCs = "public class Hello\n{\n    public string Greet() => \"Hello, toolset!\";\n}\n"

    /// Writes the fixture's own copy of the toolset sample project into `dir`, returning the
    /// csproj path. Rewriting the file (and `Hello.cs`) fresh at the start of each test that
    /// imports is what keeps the design-time build from considering a previous test's `obj/xake`
    /// output still current -- no per-test `Variant` needed for that.
    let writeProject (dir: string) =
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText (dir </> "Toolset.csproj", toolsetCsproj)
        File.WriteAllText (dir </> "Hello.cs", helloCs)
        dir </> "Toolset.csproj"

    [<Test; Category("Integration")>]
    member x.``imports the toolset compiler from the project``() =

        let projectDir = Directory.GetCurrentDirectory() </> "proj"
        let projectFile = writeProject projectDir
        let lockFile = Directory.GetCurrentDirectory() </> "toolset.json"

        do xake {x.TestOptions with FileLog="toolset-import.log"; ThrowOnError = true} {
            wantOverride (["import"])

            rules [
                "import" => recipe {
                    do! Project.import {
                        Project.ImportOptions.Default with
                            Projects = [ projectFile ]
                            Framework = "netstandard2.0"
                            Configuration = "Release"
                            Output = lockFile
                    }
                }
            ]
        }

        let lock = Lock.read lockFile
        let project = Lock.project "Toolset" lock

        Assert.That(project.Compiler.Path, Does.Contain "/microsoft.net.compilers.toolset/")
        Assert.That(project.Compiler.Path, Does.EndWith "csc.dll")
        Assert.That(project.Compiler.Sha256, Has.Length.EqualTo 64)
        Assert.That(project.Compiler.Sha256, Does.Match "^[0-9a-f]{64}$")

        let text = File.ReadAllText lockFile
        Assert.That(text, Does.Contain "$(NuGetPackageRoot)/microsoft.net.compilers.toolset/")

    [<Test; Category("Integration")>]
    member x.``compiles the imported project with the toolset compiler``() =

        let projectDir = Directory.GetCurrentDirectory() </> "proj"
        let projectFile = writeProject projectDir
        let lockFile = Directory.GetCurrentDirectory() </> "toolset-compile.json"

        do xake {x.TestOptions with FileLog="toolset-compile.log"; FileLogLevel = Verbosity.Diag; ThrowOnError = true} {
            wantOverride (["build"])

            rules [
                "build" => recipe {
                    do! Project.import {
                        Project.ImportOptions.Default with
                            Projects = [ projectFile ]
                            Framework = "netstandard2.0"
                            Configuration = "Release"
                            Output = lockFile
                    }
                    let lock = Lock.read lockFile
                    let project = Lock.project "Toolset" lock
                    do! csc { fromlock project }
                }
            ]
        }

        let lock = Lock.read lockFile
        let project = Lock.project "Toolset" lock
        let outDll = project.Output |> Option.defaultWith (fun () -> failwith "the lock's project has no /out:")

        Assert.That(File.Exists outDll, Is.True, "csc did not produce the toolset-compiled dll")

        let logText = File.ReadAllText (Directory.GetCurrentDirectory() </> "toolset-compile.log")
        Assert.That(logText, Does.Contain "microsoft.net.compilers.toolset",
            "the compiler command line in the log does not show the toolset package")

    [<Test; Category("Integration")>]
    member x.``composed mode takes the compiler from the toolset package``() =

        let srcFile = Directory.GetCurrentDirectory() </> "ComposedToolset.cs"
        File.WriteAllText (srcFile, "public class ComposedToolset {}\n")

        do xake {x.TestOptions with FileLog="toolset-composed.log"; FileLogLevel = Verbosity.Diag; ThrowOnError = true} {
            wantOverride (["ComposedToolset.dll"])

            rules [
                "ComposedToolset.dll" ..> csc {
                    targetfwk "net-4.6.2"
                    src !!"ComposedToolset.cs"
                    grefs ["System.dll"]
                    toolset toolsetVersion
                }
            ]
        }

        Assert.That(File.Exists "ComposedToolset.dll", Is.True, "csc did not produce ComposedToolset.dll")

        let logText = File.ReadAllText (Directory.GetCurrentDirectory() </> "toolset-composed.log")
        Assert.That(logText, Does.Contain "microsoft.net.compilers.toolset",
            "the compiler command line in the log does not show the toolset package")
