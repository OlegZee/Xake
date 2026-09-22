namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// The `csc { invocation project }` mode: the compiler runs exactly the command line a
/// `Lock.Project` carries, with no composition. The lock is built by hand here, the way
/// `Project.import` would have produced it for a trivial project, so the tests do not need
/// msbuild -- only the compiler the lock names.
[<TestFixture>]
type ``Csc invocation``() =
    inherit XakeTestBase("csc-invocation")

    /// A lock for a one-file "Hello" library: `netstandard.dll` as the only reference,
    /// `Hello.cs` as the only real source, and an `AssemblyInfo.cs` that only exists through
    /// `Generated` -- the test never writes it itself, the task has to.
    let makeLock (dir: string) =
        let fwk = DotNetFwk.locateFramework (Some "netstandard2.0")
        // on this OS `CscTool` may be a native launcher rather than csc.dll itself (see
        // DotNetFwk.sdkImpl.sdkFwkInfo); the lock always names the assembly, since that is
        // what msbuild would have recorded and what `dotnet <dll>` can run
        let cscDll =
            if Impl.endsWith ".dll" fwk.CscTool then fwk.CscTool
            else Path.Combine (Path.GetDirectoryName fwk.CscTool, "csc.dll")
        let netstandardDll = DotNetFwk.locateAssembly fwk "netstandard.dll"

        let objDir = Path.Combine (dir, "obj")
        let genDir = Path.Combine (objDir, "gen")
        let helloCs = Path.Combine (dir, "Hello.cs")
        let assemblyInfoCs = Path.Combine (genDir, "AssemblyInfo.cs")
        let outDll = Path.Combine (objDir, "Hello.dll")

        File.WriteAllText (helloCs, "public class Hello {}\n")

        let assemblyInfoContent = "[assembly: System.Reflection.AssemblyTitleAttribute(\"Hello\")]\n"

        let project : Lock.Project = {
            Name = "Hello"
            Project = Path.Combine (dir, "Hello.csproj")
            Directory = dir
            Compiler = { Tool = "csc"; Path = cscDll; Sha256 = Lock.sha256 cscDll; Sdk = fwk.Version }
            Args =
                [ "/noconfig"; "/nostdlib+"; "/target:library"; "/deterministic+"
                  "/reference:" + netstandardDll
                  "/out:" + outDll
                  assemblyInfoCs
                  helloCs ]
            References = [ Lock.hashed netstandardDll ]
            Analyzers = []
            ProjectRefs = []
            Imports = []
            Generated = [ assemblyInfoCs, assemblyInfoContent ]
            Properties = Map.empty
        }
        project, outDll, assemblyInfoCs, assemblyInfoContent

    [<Test; Category("Integration")>]
    member x.``compiles from the lock and writes the generated inputs``() =

        let dir = Directory.GetCurrentDirectory()
        let project, outDll, assemblyInfoCs, assemblyInfoContent = makeLock dir

        do xake {x.TestOptions with FileLog="csc-invocation.log"; ThrowOnError = true} {
            wantOverride (["hello"])

            rules [
                "hello" => recipe {
                    do! Csc {CscSettingsType.Default with Invocation = Some project}
                }
            ]
        }

        Assert.That(File.Exists outDll, Is.True, "csc did not produce Hello.dll")
        Assert.That(File.Exists assemblyInfoCs, Is.True, "the generated AssemblyInfo.cs was not written")
        Assert.That(File.ReadAllText assemblyInfoCs, Is.EqualTo assemblyInfoContent)

    [<Test; Category("Integration")>]
    member x.``refuses a reference whose hash changed``() =

        let dir = Directory.GetCurrentDirectory()
        let project, _, _, _ = makeLock dir
        let tampered =
            { project with References = project.References |> List.map (fun r -> { r with Sha256 = "0000000000000000000000000000000000000000000000000000000000000000" }) }

        let build () =
            xake {x.TestOptions with FileLog="csc-invocation-tamper.log"; ThrowOnError = true} {
                wantOverride (["hello-tamper"])

                rules [
                    "hello-tamper" => recipe {
                        do! Csc {CscSettingsType.Default with Invocation = Some tampered}
                    }
                ]
            }

        let ex = Assert.Throws<XakeException> (fun () -> build () |> ignore)
        Assert.That(ex.Data0, Does.Contain (Path.GetFileName (tampered.References.Head.Path)))

    [<Test>]
    member x.``Lock.mapPaths rewrites a reference and drops its hash``() =

        let project : Lock.Project = {
            Name = "Sample"
            Project = "/a/Sample.csproj"
            Directory = "/a"
            Compiler = { Tool = "csc"; Path = "/dotnet/csc.dll"; Sha256 = ""; Sdk = "8.0.0" }
            Args = [ "/reference:/a/Old.dll"; "/a/A.cs" ]
            References = [ { Path = "/a/Old.dll"; Sha256 = "ab" } ]
            Analyzers = []
            ProjectRefs = []
            Imports = []
            Generated = []
            Properties = Map.empty
        }

        let mapped = project |> Lock.mapPaths (fun p -> if p = "/a/Old.dll" then "/b/New.dll" else p)

        Assert.That(mapped.Args, Is.EqualTo [ "/reference:/b/New.dll"; "/a/A.cs" ])
        let expected : Lock.Hashed list = [ { Path = "/b/New.dll"; Sha256 = "" } ]
        Assert.That(mapped.References, Is.EqualTo expected)
