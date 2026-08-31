namespace Tests

open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

[<TestFixture>]
type ``Various tests``() =
    inherit XakeTestBase("misc")

    let taskReturn n = recipe {
        return n
    }

    [<Test>]
    member x.``runs csc task (full test)``() =

        let needExecuteCount = ref 0
        
        do xake {x.TestOptions with FileLog="skipbuild.log"; ConLogLevel = Verbosity.Diag} {  // one thread to avoid simultaneous access to 'wasExecuted'
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
                "hello.cs" ..> action {
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
