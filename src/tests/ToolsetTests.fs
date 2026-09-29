namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// The compiler-as-a-package path in the composed `csc {}` mode: the `toolset` operation names a
/// `Microsoft.Net.Compilers.Toolset` version and the compiler is restored from that package.
/// (Reading the same package out of a project is a project-import concern, tested with it.)
[<TestFixture>]
type ``Toolset compiler``() =
    inherit XakeTestBase("toolset")

    let toolsetVersion = "4.12.0"

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
