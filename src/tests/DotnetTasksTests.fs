namespace Tests

open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

[<TestFixture>]
type ``Dotnet tasks tests``() =
    inherit XakeTestBase("dotnet")

    let taskReturn n = recipe {
        return n
    }

    // DotNetFwk locates csc by probing pkg-config/Mono prefixes and the Windows registry,
    // neither of which exists on a machine that only has the .NET SDK -- it fails with
    // "No framework found". Re-enable once DotNetFwk resolves Roslyn from the SDK.
    // ThrowOnError keeps a failure inside the test: without it Xake terminates the process
    // and takes the whole test host down with it.
    [<Test; Ignore("DotNetFwk cannot locate a C# compiler without Mono or .NET Framework")>]
    member x.``runs csc task (full test)``() =

        let needExecuteCount = ref 0
        
        do xake {x.TestOptions with FileLog="skipbuild.log"; ConLogLevel = Verbosity.Diag; ThrowOnError = true} {  // one thread to avoid simultaneous access to 'wasExecuted'
            wantOverride (["hello"])
            filelog "csc-err.log" Verbosity.Diag

            rules [
                "hello" => recipe {
                    do! trace Error "Running inside 'hello' rule"
                    do! need ["hello.cs"]

                    do! trace Error "Rebuilding..."
                    do! Csc {
                    CscSettings with
                        Src = !!"hello.cs"
                        Out = File.make "hello.exe"
                    }
                }
                "hello.cs" ..> recipe {
                    do! writeText """class Program
                    {
    	                public static void Main()
    	                {
    		                System.Console.WriteLine("Hello world!");
    	                }
                    }"""
                    let! src = getTargetFullName()
                    do! trace Error "Done building 'hello.cs' rule in %A" src
                    needExecuteCount := !needExecuteCount + 1
                }
            ]
        }

        Assert.AreEqual(1, !needExecuteCount)

    [<Test>]
    member x.``resource set instantiation``() =

        let resset = resourceset {
            prefix "Sample.Application"
            dynamic true

            files (fileset {
                includes "*.resx"
            })
        }

        let resourceSetCollection = [
            resourceset {
                prefix "Sample.Application"
                dynamic true

                files (fileset {
                    includes "*.resx"
                })
            }
            resourceset {
                prefix "Sample.Application1"
                dynamic true

                files (fileset {
                    includes "*.res"
                })
            }
        ]

        printfn "%A" resset
        ()
