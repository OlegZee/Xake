namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// The compiler-as-a-package path in the composed `csc {}` mode: the `toolset` operation names a
/// `Microsoft.Net.Compilers.Toolset` version and the compiler is restored from that package; the
/// script variable `CSC_TOOLSET` does the same for every block that names none.
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

    [<Test; Category("Integration")>]
    member x.``CSC_TOOLSET takes the compiler from the toolset package when the block names none``() =

        let srcFile = Directory.GetCurrentDirectory() </> "VarToolset.cs"
        File.WriteAllText (srcFile, "public class VarToolset {}\n")

        do xake {x.TestOptions with FileLog="toolset-var.log"; FileLogLevel = Verbosity.Diag; ThrowOnError = true
                                    Vars = ["CSC_TOOLSET", toolsetVersion]} {
            wantOverride (["VarToolset.dll"])

            rules [
                "VarToolset.dll" ..> csc {
                    targetfwk "net-4.6.2"
                    src !!"VarToolset.cs"
                    grefs ["System.dll"]
                }
            ]
        }

        Assert.That(File.Exists "VarToolset.dll", Is.True, "csc did not produce VarToolset.dll")

        let logText = (File.ReadAllText (Directory.GetCurrentDirectory() </> "toolset-var.log")).Replace('\\', '/')
        Assert.That(logText, Does.Contain ("microsoft.net.compilers.toolset/" + toolsetVersion),
            "the compiler command line in the log does not show the toolset package CSC_TOOLSET names")

    [<Test; Category("Integration")>]
    member x.``the block's toolset wins over CSC_TOOLSET``() =

        let blockVersion = "4.11.0"
        File.WriteAllText ("BlockToolset.cs", "public class BlockToolset {}\n")
        let resolved = ref ""

        do xake {x.TestOptions with FileLog="toolset-block.log"; ThrowOnError = true
                                    Vars = ["CSC_TOOLSET", toolsetVersion]} {
            wantOverride (["blocktoolset"])

            rules [
                "blocktoolset" => recipe {
                    let! c = csc {
                        targetfwk "net-4.6.2"
                        out (File.make "BlockToolset.dll")
                        src !!"BlockToolset.cs"
                        toolset blockVersion
                        resolve
                    }
                    resolved.Value <- c.Dependencies.Compiler.Path
                }
            ]
        }

        let path = resolved.Value.Replace('\\', '/').ToLowerInvariant()
        Assert.That(path, Does.Contain ("microsoft.net.compilers.toolset/" + blockVersion + "/"), "the block's toolset")
        Assert.That(path, Does.Not.Contain ("/" + toolsetVersion + "/"), "CSC_TOOLSET must not win")
