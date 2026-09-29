namespace Tests

open System
open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// The kept form of msbuild's evaluation of a project (`Fsproj`), split out of
/// `DotnetTasksTests` when the base test project stopped referencing the hermetic package.
[<TestFixture>]
type ``Fsproj evaluation``() =
    inherit XakeTestBase("fsproj")

    [<Test>]
    member x.``reads the project msbuild evaluated``() =


        let result = Path.Combine(Path.GetTempPath(), "xake-test-eval.json")
        File.WriteAllText(result, """{
              "Properties": {
                "AssemblyName": "Sample.Lib",
                "DefineConstants": "TRACE;RELEASE;NETSTANDARD;NETSTANDARD2_0",
                "Copyright": "(c) \"nobody\" \u00a9"
              },
              "Items": {
                "CompileBefore": [
                  { "Identity": "obj/Release/netstandard2.0/Sample.AssemblyInfo.fs", "FullPath": "/proj/obj/Release/netstandard2.0/Sample.AssemblyInfo.fs" }
                ],
                "Compile": [
                  { "Identity": "First.fs", "FullPath": "/proj/First.fs" },
                  { "Identity": "Second.fs", "FullPath": "/proj/Second.fs" }
                ],
                "CompileAfter": [],
                "ReferencePath": [
                  { "Identity": "FSharp.Core", "FullPath": "/packages/FSharp.Core.dll" }
                ],
                "ProjectReference": [
                  { "Identity": "../core/Core.fsproj", "FullPath": "/core/Core.fsproj" }
                ]
              }
            }""")

        let project = Fsproj.parseEvaluation result

        Assert.That(project.AssemblyName, Is.EqualTo "Sample.Lib")
        // the generated assembly attributes are the CompileBefore item and come first
        Assert.That(project.Sources, Is.EqualTo [
            "/proj/obj/Release/netstandard2.0/Sample.AssemblyInfo.fs"; "/proj/First.fs"; "/proj/Second.fs"])
        Assert.That(project.References, Is.EqualTo ["/packages/FSharp.Core.dll"])
        Assert.That(project.ProjectRefs, Is.EqualTo ["/core/Core.fsproj"])
        Assert.That(project.Defines, Is.EqualTo ["TRACE"; "RELEASE"; "NETSTANDARD"; "NETSTANDARD2_0"])
        // escapes are the parser's own business -- there is no json library underneath
        Assert.That(project.Properties.["Copyright"], Is.EqualTo "(c) \"nobody\" \u00a9")

        // msbuild's answer is kept in the compact form, which has to read back the same
        let kept = Path.Combine(Path.GetTempPath(), "xake-test-eval-kept.json")
        let roots = Roots.builtin (Directory.GetCurrentDirectory())
        File.WriteAllText(kept, Fsproj.format roots project)
        Assert.That(Fsproj.parse roots kept, Is.EqualTo project)

        // a path under the package cache is kept as a token: the file goes into the
        // repository and the cache is somewhere else on the next machine
        let packages = (Roots.nugetRoot()).Replace('\\', '/').TrimEnd '/'
        let reference = packages + "/fsharp.core/8.0.100/lib/netstandard2.0/FSharp.Core.dll"
        File.WriteAllText(kept, Fsproj.format roots { project with References = [reference] })

        Assert.That(File.ReadAllText kept, Does.Contain "$(NuGetPackageRoot)/fsharp.core")
        Assert.That((Fsproj.parse roots kept).References, Is.EqualTo [reference])

    [<Test>]
    member x.``evaluates a project with two project references against the project directory``() =

        // a working directory other than the project's, as in a build script: with `-restore`
        // in the same msbuild call the items of such a project came back cwd-relative
        let dir = Path.Combine(Path.GetTempPath(), "xake-eval-" + Guid.NewGuid().ToString "N")
        let write (name: string) (text: string) =
            let path = Path.Combine(dir, name)
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, text)

        let library (references: string list) =
            let refs =
                references
                |> List.map (fun r -> sprintf """<ProjectReference Include="..\%s\%s.fsproj" />""" r r)
                |> String.concat ""
            sprintf """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Lib.fs" /></ItemGroup>
  <ItemGroup>%s</ItemGroup>
</Project>""" refs

        try
            write "Main/Lib.fs" "module Main.Lib\nlet a = 1\n"
            write "Main/Main.fsproj" (library ["Left"; "Right"])
            write "Left/Lib.fs" "module Left.Lib\nlet a = 1\n"
            write "Left/Left.fsproj" (library [])
            write "Right/Lib.fs" "module Right.Lib\nlet a = 1\n"
            write "Right/Right.fsproj" (library [])

            let project = Path.Combine(dir, "Main", "Main.fsproj")
            let result = Path.Combine(dir, "eval.json")
            Assert.That(Directory.GetCurrentDirectory(), Is.Not.EqualTo(Path.Combine(dir, "Main")))

            do xake {x.TestOptions with FileLog = "fsproj-eval.log"; ThrowOnError = true; ProjectRoot = dir} {
                rules [
                    "main" => recipe {
                        do! Fsproj.evaluate {
                            Fsproj.EvalOptions.Default with
                                Project = project
                                Framework = "netstandard2.0"
                                Output = result
                        }
                    }
                ]
            }

            let kept = File.ReadAllText result
            let loaded = Fsproj.parse (Roots.builtin dir) result
            let norm (p: string) = p.Replace('\\', '/')
            let under (p: string) = (norm p).StartsWith (norm dir + "/Main/")

            Assert.That(loaded.Sources, Is.Not.Empty)
            Assert.That(loaded.Sources |> List.forall under, Is.True, sprintf "%A" loaded.Sources)
            Assert.That(kept, Does.Contain "$(ProjectRoot)/Main/Lib.fs")
            Assert.That(loaded.ProjectRefs |> List.map norm,
                        Is.EqualTo [norm dir + "/Left/Left.fsproj"; norm dir + "/Right/Right.fsproj"])
            Assert.That(kept, Does.Contain "$(ProjectRoot)/Left/Left.fsproj")
            Assert.That(kept, Does.Contain "$(ProjectRoot)/Right/Right.fsproj")
        finally
            if Directory.Exists dir then Directory.Delete(dir, true)
