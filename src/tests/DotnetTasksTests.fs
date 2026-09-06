namespace Tests

open System.IO
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

    // The compilers come from the .NET SDK and the reference assemblies from a NuGet package
    // (DotNetFwk.sdkImpl), so this needs an SDK and, on a cold package cache, network access --
    // hence the Integration category. It is pinned to net-4.6.2, the configuration
    // samples/fullframework.fsx exercises.
    // ThrowOnError keeps a failure inside the test: without it Xake terminates the process
    // and takes the whole test host down with it.
    [<Test; Category("Integration")>]
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
                    CscSettingsType.Default with
                        Src = !!"hello.cs"
                        Out = File.make "hello.exe"
                        TargetFramework = "net-4.6.2"
                        RefGlobal = ["System.dll"]
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
                    needExecuteCount.Value <- needExecuteCount.Value + 1
                }
            ]
        }

        // the source was generated and the compiler turned it into an assembly
        Assert.That(needExecuteCount.Value, Is.GreaterThanOrEqualTo 1)
        Assert.That(File.Exists "hello.exe", Is.True, "csc did not produce hello.exe")

    // Same discovery, but for a profile rather than a framework version: the reference
    // assembly comes from the SDK's netstandard pack or from the NETStandard.Library
    // package. FSharp.Core is referenced explicitly since --noframework is in effect.
    [<Test; Category("Integration")>]
    member x.``runs fsc task targeting netstandard``() =

        let fsharpCore = System.Reflection.Assembly.GetAssembly(typeof<option<int>>).Location

        do xake {x.TestOptions with FileLog="fsc-netstandard.log"; ThrowOnError = true} {
            wantOverride (["hi.dll"])

            rules [
                "hi.dll" ..> recipe {
                    do! need ["hi.fs"]
                    do! Fsc {
                    FscSettingsType.Default with
                        Src = !!"hi.fs"
                        Out = File.make "hi.dll"
                        Ref = Fileset.Empty ++ fsharpCore
                        TargetFramework = "netstandard2.0"
                    }
                }
                "hi.fs" ..> writeText """module Hi
let greet name = sprintf "Hello, %s" name
"""
            ]
        }

        Assert.That(File.Exists "hi.dll", Is.True, "fsc did not produce hi.dll")

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
        File.WriteAllText(kept, Fsproj.write project)
        Assert.That(Fsproj.parse kept, Is.EqualTo project)

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

    [<Test>]
    member __.``locates the .NET Framework through the SDK``() =

        let fwk = DotNetFwk.locateFramework (Some "net-4.6.2")

        Assert.That(fwk.CscTool, Is.Not.Empty)
        if not <| File.Exists fwk.CscTool then
            Assert.Ignore(sprintf "no C# compiler at '%s' -- .NET SDK not available?" fwk.CscTool)
        Assert.That(fwk.AssemblyDirs, Is.Not.Empty, "reference assembly directories")

    [<Test>]
    member __.``escapes compiler arguments``() =

        // plain arguments are passed through untouched
        Assert.AreEqual("simple", Impl.escapeArgument "simple")
        Assert.AreEqual("/r:System.dll", Impl.escapeArgument "/r:System.dll")

        // a space or a quote forces quoting, and inner quotes are backslash-escaped
        Assert.AreEqual("\"with space\"", Impl.escapeArgument "with space")
        Assert.AreEqual("\"say \\\"hi\\\"\"", Impl.escapeArgument "say \"hi\"")

    [<Test>]
    member __.``resolves the output target type``() =

        Assert.AreEqual("library", Impl.targetStr "a.dll" Auto)
        Assert.AreEqual("exe", Impl.targetStr "a.exe" Auto)
        // an unrecognized extension defaults to a library
        Assert.AreEqual("library", Impl.targetStr "a.out" Auto)
        // an explicit target wins over the file name
        Assert.AreEqual("winexe", Impl.targetStr "a.dll" WinExe)

    [<Test>]
    member __.``classifies compiler output by log level``() =

        let classify = Impl.levelFromString Level.Verbose

        Assert.AreEqual(Level.Warning, classify "a.cs(1,1): warning CS0168: unused variable")
        Assert.AreEqual(Level.Error, classify "a.cs(1,1): error CS0103: undefined name")
        // fsc reports whole-compilation problems without a source position
        Assert.AreEqual(Level.Error, classify "error FS0084: Assembly reference 'x' was not found")
        Assert.AreEqual(Level.Warning, classify "warning FS0064: this construct is deprecated")

        // anything else keeps the level the caller asked for
        Assert.AreEqual(Level.Verbose, classify "Microsoft (R) Visual C# Compiler")

    [<Test>]
    member __.``builds resource names``() =

        let dynamic = {ResourceSetOptions.Default with DynamicPrefix = false}

        Assert.AreEqual("Strings.resx", Impl.makeResourceName dynamic None "sub/Strings.resx")
        Assert.AreEqual(
            "Sample.App.Strings.resx",
            Impl.makeResourceName {dynamic with Prefix = Some "Sample.App"} None "sub/Strings.resx")

    [<Test>]
    member __.``task builders produce recipes``() =

        // the CEs have to compile and yield a recipe; running them needs a compiler
        let recipes: Recipe<ExecContext,unit> list = [
            csc { targetfwk "net-4.6.2"; src !!"a.cs"; grefs ["System.dll"]; nofailonerror }
            fsc { targetfwk "net-4.6.2"; src !!"a.fs"; noframework; nofailonerror }
            msbuild { buildfile "a.sln"; target "Build"; prop ("Configuration", "Release"); maxcpu 0; verbosity Minimal; nofailonerror }
            resgen { resources (resourceset { prefix "P" }); targetdir "out"; nosourcepath }
        ]

        Assert.AreEqual(4, recipes |> List.length)

