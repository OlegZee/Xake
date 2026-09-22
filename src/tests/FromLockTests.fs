namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// The `csc { fromlock project }` mode: the compiler runs exactly the command line a
/// `Lock.Project` carries, with no composition. The lock is built by hand here, the way
/// `Project.import` would have produced it for a trivial project, so the tests do not need
/// msbuild -- only the compiler the lock names.
[<TestFixture>]
type ``Csc fromlock``() =
    inherit XakeTestBase("csc-fromlock")

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
            Resources = []
            Properties = Map.empty
        }
        project, outDll, assemblyInfoCs, assemblyInfoContent

    [<Test; Category("Integration")>]
    member x.``compiles from the lock and writes the generated inputs``() =

        let dir = Directory.GetCurrentDirectory()
        let project, outDll, assemblyInfoCs, assemblyInfoContent = makeLock dir

        do xake {x.TestOptions with FileLog="csc-fromlock.log"; ThrowOnError = true} {
            wantOverride (["hello"])

            rules [
                "hello" => recipe {
                    do! Csc {CscSettingsType.Default with FromLock = Some project}
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
            xake {x.TestOptions with FileLog="csc-fromlock-tamper.log"; ThrowOnError = true} {
                wantOverride (["hello-tamper"])

                rules [
                    "hello-tamper" => recipe {
                        do! Csc {CscSettingsType.Default with FromLock = Some tampered}
                    }
                ]
            }

        let ex = Assert.Throws<XakeException> (fun () -> build () |> ignore)
        Assert.That(ex.Data0, Does.Contain (Path.GetFileName (tampered.References.Head.Path)))

    /// "make the compiler available" (`ensureCompilerAvailable` in `Dotnet.csc.fs`), exercised
    /// through the public `Csc { fromlock ... }` entry point rather than calling the private
    /// helper directly.
    [<Test; Category("Integration")>]
    member x.``restores the toolset compiler named by the lock``() =

        // exercises the restore path for real, without touching the user's own NuGet cache:
        // point NUGET_PACKAGES at a scratch directory for the duration of this test.
        // `Fsproj.roots ()` and `DotNetFwk.sdkImpl.nugetRoot ()` both read it directly (see
        // Fsproj.fs / DotNetFwk.fs), and `restorePackage` shells out to `dotnet restore`, which
        // inherits it like any other environment variable -- confirmed against this package
        // before writing the test.
        let originalNugetPackages = System.Environment.GetEnvironmentVariable "NUGET_PACKAGES"
        let scratchNuget = Path.Combine (Path.GetTempPath(), "xake-fromlock-restore-" + System.Guid.NewGuid().ToString("N"))

        try
            System.Environment.SetEnvironmentVariable ("NUGET_PACKAGES", scratchNuget)

            let dir = Directory.GetCurrentDirectory()
            let project, outDll, _, _ = makeLock dir

            // the compiler this test names is the toolset package restored into the scratch
            // cache, at the version already present in the user's own cache on this machine
            // (so the hash the lock records is real): `~/.nuget/packages/microsoft.net.compilers.toolset/`
            let version = "4.12.0"
            let userNugetRoot =
                match originalNugetPackages with
                | null | "" -> Path.Combine (System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile, ".nuget", "packages")
                | dir -> dir
            let userCscDll = Path.Combine (userNugetRoot, "microsoft.net.compilers.toolset", version, "tasks", "netcore", "bincore", "csc.dll")
            Assume.That(File.Exists userCscDll, Is.True,
                sprintf "microsoft.net.compilers.toolset %s is not restored on this machine (%s) -- nothing to compare the restore against" version userCscDll)

            let scratchCscDll = Path.Combine (scratchNuget, "microsoft.net.compilers.toolset", version, "tasks", "netcore", "bincore", "csc.dll")
            let project =
                { project with
                    Compiler = { project.Compiler with Path = scratchCscDll; Sha256 = Lock.sha256 userCscDll } }

            Assert.That(File.Exists scratchCscDll, Is.False, "the scratch NuGet cache already has the package -- test setup is wrong")

            do xake {x.TestOptions with FileLog="csc-fromlock-restore.log"; ThrowOnError = true} {
                wantOverride (["hello-restore"])

                rules [
                    "hello-restore" => recipe {
                        do! Csc {CscSettingsType.Default with FromLock = Some project}
                    }
                ]
            }

            Assert.That(File.Exists scratchCscDll, Is.True, "the toolset package was not restored into the scratch NuGet cache")
            Assert.That(File.Exists outDll, Is.True, "csc did not produce Hello.dll after restoring the compiler")
        finally
            System.Environment.SetEnvironmentVariable ("NUGET_PACKAGES", originalNugetPackages)
            try Directory.Delete (scratchNuget, true) with _ -> ()

    [<Test>]
    member x.``explains a missing SDK compiler``() =

        let dir = Directory.GetCurrentDirectory()
        let project, _, _, _ = makeLock dir
        let dotnetRoot =
            match DotNetFwk.sdkImpl.dotnetRoot () with
            | Some root -> root
            | None -> Assert.Ignore("no .NET SDK root found on this machine"); failwith "unreachable"
        let sdkCompilerPath = Path.Combine (dotnetRoot, "sdk", "0.0.1", "Roslyn", "bincore", "csc.dll")
        let project = { project with Compiler = { project.Compiler with Path = sdkCompilerPath; Sha256 = "" } }

        let build () =
            xake {x.TestOptions with FileLog="csc-fromlock-missing-sdk.log"; ThrowOnError = true} {
                wantOverride (["hello-missing-sdk"])

                rules [
                    "hello-missing-sdk" => recipe {
                        do! Csc {CscSettingsType.Default with FromLock = Some project}
                    }
                ]
            }

        let ex = Assert.Throws<XakeException> (fun () -> build () |> ignore)
        Assert.That(ex.Data0, Does.Contain "SDK 0.0.1")

    [<Test>]
    member x.``explains a compiler that does not exist anywhere``() =

        let dir = Directory.GetCurrentDirectory()
        let project, _, _, _ = makeLock dir
        let nowhere = if Env.isUnix then "/nonexistent/csc.dll" else "C:\\nonexistent\\csc.dll"
        let project = { project with Compiler = { project.Compiler with Path = nowhere; Sha256 = "" } }

        let build () =
            xake {x.TestOptions with FileLog="csc-fromlock-missing-anywhere.log"; ThrowOnError = true} {
                wantOverride (["hello-missing-anywhere"])

                rules [
                    "hello-missing-anywhere" => recipe {
                        do! Csc {CscSettingsType.Default with FromLock = Some project}
                    }
                ]
            }

        let ex = Assert.Throws<XakeException> (fun () -> build () |> ignore)
        Assert.That(ex.Data0, Does.Contain nowhere)

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
            Resources = []
            Properties = Map.empty
        }

        let mapped = project |> Lock.mapPaths (fun p -> if p = "/a/Old.dll" then "/b/New.dll" else p)

        Assert.That(mapped.Args, Is.EqualTo [ "/reference:/b/New.dll"; "/a/A.cs" ])
        let expected : Lock.Hashed list = [ { Path = "/b/New.dll"; Sha256 = "" } ]
        Assert.That(mapped.References, Is.EqualTo expected)
