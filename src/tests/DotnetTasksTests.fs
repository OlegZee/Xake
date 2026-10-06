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
                    do! Csc.compile {
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

    // Composed mode with a `.resx` resource: `resolve` no longer compiles it into a random
    // temp file (deleted after the compile, and unusable if the settings were instead
    // recorded as a lock -- see `Csc.ofSettings` in Csc.fs and the csc-syntax.md
    // "composed mode resx" paragraph). It records a permanent `(resx, .resources)` pair in
    // `Csc.Resources` and `run`'s existing resource step compiles it, exactly as it
    // already does for an imported project.
    [<Test; Category("Integration")>]
    member x.``runs csc task with a composed resx resource``() =

        let compileCount = ref 0

        let resx =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<root>\n" +
            "  <data name=\"Greeting\" xml:space=\"preserve\"><value>Hello, resx!</value></data>\n" +
            "</root>\n"
        File.WriteAllText ("Strings.resx", resx)

        // both inputs are plain files on disk, not rules -- a rule with no dependencies of
        // its own reruns on every build (there is nothing to check staleness against), which
        // would make `helloresx.exe` rerun too since it `need`s it. Writing them once, like
        // `CommandLineTests`' "input.txt", is what lets the second build be a genuine no-op.
        File.WriteAllText ("helloresx.cs", """class Program
        {
            public static void Main()
            {
                System.Console.WriteLine("Hello world!");
            }
        }""")

        // a file target, not a phony one: phony actions always rerun in Xake (like a `.PHONY`
        // make target), so only a file rule's own up-to-date check can show the second build
        // was a no-op
        let runBuild () =
            xake {x.TestOptions with FileLog="skipbuild.log"; ConLogLevel = Verbosity.Diag; ThrowOnError = true} {
                wantOverride (["helloresx.exe"])

                rules [
                    "helloresx.exe" ..> recipe {
                        do! need ["helloresx.cs"]
                        compileCount.Value <- compileCount.Value + 1
                        do! Csc.compile {
                        CscSettingsType.Default with
                            Src = !!"helloresx.cs"
                            Out = File.make "helloresx.exe"
                            TargetFramework = "net-4.6.2"
                            RefGlobal = ["System.dll"]
                            Resources = [
                                resourceset {
                                    prefix "Sample.Application"
                                    files (fileset { includes "Strings.resx" })
                                }
                            ]
                        }
                    }
                ]
            }

        runBuild ()

        let expectedResources = "obj" </> "xake" </> "helloresx" </> "Sample.Application.Strings.resources"
        Assert.That(File.Exists "helloresx.exe", Is.True, "csc did not produce helloresx.exe")
        Assert.That(File.Exists expectedResources, Is.True, sprintf "expected '%s' to exist" expectedResources)
        Assert.That(compileCount.Value, Is.EqualTo 1)

        // a second build with nothing changed must not recompile: the .resources file, the
        // resx and the dll are all still up to date as far as the engine's dependency
        // tracking is concerned
        runBuild ()
        Assert.That(compileCount.Value, Is.EqualTo 1, "second build should not rerun the rule")

    // A reference produced by another rule of the same script has to be (re)built before
    // `Csc.run` checks its hash, not after: the check is meant to verify what the compiler is
    // about to read. The consuming rule records its compilation once, hashed (`rehash`, the
    // step a lock makes), and replays that record on later builds -- base API only, no lock.
    // The library compiles with /deterministic, so rebuilding it from the same source yields
    // the same bytes and the recorded hash stays valid.
    [<Test; Category("Integration")>]
    member x.``csc builds a referenced library before checking its hash``() =

        File.WriteAllText ("hashlib.cs", "public class Lib { public static string Name = \"lib\"; }")
        File.WriteAllText ("hashapp.cs", "class Program { static void Main() { System.Console.WriteLine(Lib.Name); } }")
        for f in ["hashlib.dll"; "hashapp.exe"] do try File.Delete f with _ -> ()

        let recorded : Csc option ref = ref None
        let libCompiles = ref 0
        let appCompiles = ref 0

        let libSettings =
            { CscSettingsType.Default with
                Src = !!"hashlib.cs"
                Out = File.make "hashlib.dll"
                TargetFramework = "net-4.6.2"
                RefGlobal = ["System.dll"]
                CommandArgs = ["/deterministic"] }
        let appSettings =
            { CscSettingsType.Default with
                Src = !!"hashapp.cs"
                Ref = !!"hashlib.dll"
                Out = File.make "hashapp.exe"
                TargetFramework = "net-4.6.2"
                RefGlobal = ["System.dll"] }

        let runBuild () =
            xake {x.TestOptions with FileLog="csc-refhash.log"; ConLogLevel = Verbosity.Diag; ThrowOnError = true} {
                wantOverride (["hashapp.exe"])

                rules [
                    "hashlib.dll" ..> recipe {
                        do! need ["hashlib.cs"]
                        libCompiles.Value <- libCompiles.Value + 1
                        do! Csc.compile libSettings
                    }
                    "hashapp.exe" ..> recipe {
                        do! need ["hashapp.cs"]
                        appCompiles.Value <- appCompiles.Value + 1
                        let! options = Csc.runOptions appSettings
                        let! c =
                            match recorded.Value with
                            | Some c -> recipe { return c }
                            | None ->
                                recipe {
                                    do! need ["hashlib.dll"]
                                    let! c = Csc.ofSettings appSettings
                                    let c = Csc.rehash c
                                    recorded.Value <- Some c
                                    return c
                                }
                        do! Csc.run options c
                    }
                ]
            }

        runBuild ()
        Assert.That(File.Exists "hashapp.exe", Is.True, "csc did not produce hashapp.exe")
        let libRef = recorded.Value.Value.Dependencies.References |> List.find (fun r -> r.Path.EndsWith "hashlib.dll")
        Assert.That(libRef.Sha256, Is.Not.Empty, "the recorded compilation hashes the library")
        Assert.That((libCompiles.Value, appCompiles.Value), Is.EqualTo ((1, 1)))

        // the library source changes (timestamp): the library rebuilds and the application,
        // which references it, recompiles against it
        File.SetLastWriteTimeUtc ("hashlib.cs", System.DateTime.UtcNow.AddSeconds 2.0)
        runBuild ()
        Assert.That((libCompiles.Value, appCompiles.Value), Is.EqualTo ((2, 2)), "touching the library source rebuilds both")

        // the library output is gone: checking its hash before building it would see it missing
        File.Delete "hashlib.dll"
        runBuild ()
        Assert.That((libCompiles.Value, appCompiles.Value), Is.EqualTo ((3, 3)), "a missing library is rebuilt, then verified")
        Assert.That(File.Exists "hashlib.dll", Is.True)

    // The counterpart of the ordering above: a hashed reference that is missing and that no
    // rule produces cannot be obtained by `needFiles`, so `run` reports every such file with
    // the hash it was expected to have, before `needFiles` would stop at the first one.
    [<Test; Category("Integration")>]
    member x.``csc reports every missing hashed reference that no rule produces``() =

        File.WriteAllText ("missingref.cs", "class C {}")
        let expected = String.replicate 64 "a"
        let missing = ["nothere1.dll"; "nothere2.dll"] |> List.map Path.GetFullPath
        let settings =
            { CscSettingsType.Default with
                Src = !!"missingref.cs"
                Out = File.make "missingref.dll"
                TargetFramework = "net-4.6.2" }

        let build () =
            xake {x.TestOptions with FileLog="csc-missingref.log"; ThrowOnError = true} {
                wantOverride (["missingref"])
                rules [
                    "missingref" => recipe {
                        let! options = Csc.runOptions settings
                        let! c = Csc.ofSettings settings
                        let refs = missing |> List.map (fun p -> ({ Path = p; Sha256 = expected; Alias = "" } : Reference))
                        let c = { c with Dependencies = { c.Dependencies with References = c.Dependencies.References @ refs } }
                        do! Csc.run options c
                    }
                ]
            }

        let ex = Assert.Throws<XakeException> (fun () -> build () |> ignore)
        for p in missing do
            Assert.That(ex.ToString(), Does.Contain (sprintf "%s: expected %s, got missing" p expected))

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
                    do! Fsc.compile {
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

    // The SDK's `Roslyn/bincore` has a `csc` (`csc.exe` on Windows) apphost next to `csc.dll`;
    // a composed compilation records the dll, the compiler an imported lock hashes. Where the
    // probe finds no dll beside the compiler (the .NET Framework's own `csc.exe`, mono) the
    // test is ignored: the path is kept as it is there.
    [<Test; Category("Integration")>]
    member x.``composed compilation records the SDK's csc.dll as the compiler``() =

        let probed = (DotNetFwk.locateFramework (Some "netstandard2.0")).CscTool
        if not (File.Exists (Path.GetDirectoryName probed </> "csc.dll")) then
            Assert.Ignore(sprintf "no csc.dll next to '%s' (not the SDK's compiler)" probed)

        File.WriteAllText ("managedcsc.cs", "public class C {}")
        let resolved = ref ""
        do xake {x.TestOptions with FileLog="csc-managed.log"; ThrowOnError = true} {
            wantOverride (["managedcsc"])
            rules [
                "managedcsc" => recipe {
                    let! c = Csc.ofSettings { CscSettingsType.Default with
                                                Src = !!"managedcsc.cs"
                                                Out = File.make "managedcsc.dll"
                                                TargetFramework = "netstandard2.0" }
                    resolved.Value <- c.Dependencies.Compiler.Path
                }
            ]
        }
        Assert.That(resolved.Value, Does.EndWith "csc.dll")
        Assert.That(Path.GetDirectoryName resolved.Value, Is.EqualTo (Path.GetDirectoryName probed))

    // netstandard2.0's reference assemblies come from the NETStandard.Library package at the
    // exact version DotNetFwk pins (no "newest in the cache"), fetched through the restore
    // mechanism (`restorePackage`) when absent, and end up in the resolved compilation as
    // ordinary references under the package folder.
    [<Test; Category("Integration")>]
    member x.``composed netstandard2.0 references NETStandard.Library at the pinned version``() =

        let version = DotNetFwk.defaultReferencePackVersions.NetStandardLibrary
        Assert.That(DotNetFwk.referencePackage DotNetFwk.defaultReferencePackVersions "netstandard2.0",
                    Is.EqualTo (Some ("NETStandard.Library", version)))
        Assert.That(DotNetFwk.referencePackage DotNetFwk.defaultReferencePackVersions "netstandard2.1", Is.EqualTo None)
        Assert.That(DotNetFwk.referencePackage DotNetFwk.defaultReferencePackVersions "net-4.6.2",
                    Is.EqualTo (Some ("Microsoft.NETFramework.ReferenceAssemblies.net462", DotNetFwk.defaultReferencePackVersions.ReferenceAssemblies)))

        File.WriteAllText ("refpack.cs", "public class C {}")
        let resolved = ref None
        do xake {x.TestOptions with FileLog="refpack.log"; ThrowOnError = true} {
            wantOverride (["refpack"])
            rules [
                "refpack" => recipe {
                    let! c = Csc.ofSettings { CscSettingsType.Default with
                                                Src = !!"refpack.cs"
                                                Out = File.make "refpack.dll"
                                                TargetFramework = "netstandard2.0" }
                    resolved.Value <- Some c
                }
            ]
        }
        let c = resolved.Value |> Option.get
        let norm (p: string) = p.Replace('\\', '/')
        let refDir = norm (DotNetFwk.nugetRoot () </> "netstandard.library" </> version </> "build" </> "netstandard2.0" </> "ref")
        let refs = c.Dependencies.References |> List.map (fun r -> norm r.Path)
        Assert.That(refs, Does.Contain (refDir + "/mscorlib.dll"))
        Assert.That(c.Args, Does.Contain ("/reference:" + DotNetFwk.nugetRoot () </> "netstandard.library" </> version </> "build" </> "netstandard2.0" </> "ref" </> "mscorlib.dll"))

    // A reference-pack version that does not exist fails the build, naming the package and
    // the version, instead of being swallowed.
    [<Test; Category("Integration")>]
    member x.``a reference pack that cannot be restored fails the build naming it``() =

        File.WriteAllText ("refpackbad.cs", "public class C {}")
        let ex =
            Assert.Catch(fun () ->
                do xake {x.TestOptions with FileLog="refpackbad.log"; ThrowOnError = true
                                            Vars = ["NETSTANDARD_LIBRARY_VERSION", "0.0.1-xake-missing"]} {
                    wantOverride (["refpackbad"])
                    rules [
                        "refpackbad" => recipe {
                            let! _ = Csc.ofSettings { CscSettingsType.Default with
                                                        Src = !!"refpackbad.cs"
                                                        Out = File.make "refpackbad.dll"
                                                        TargetFramework = "netstandard2.0" }
                            ()
                        }
                    ]
                })
        let rec messages (e: exn) = if isNull e then "" else e.Message + "\n" + messages e.InnerException
        Assert.That(messages ex, Does.Contain "NETStandard.Library 0.0.1-xake-missing")

    [<Test>]
    member __.``managedCompiler replaces only a csc launcher that has csc.dll beside it``() =
        let dir = Path.GetFullPath "managedcompiler"
        Directory.CreateDirectory dir |> ignore
        let launcher = dir </> "csc"
        File.WriteAllText (launcher, "")
        try File.Delete (dir </> "csc.dll") with _ -> ()
        Assert.That(Csc.managedCompiler launcher, Is.EqualTo launcher, "no dll beside it")
        File.WriteAllText (dir </> "csc.dll", "")
        Assert.That(Csc.managedCompiler launcher, Is.EqualTo (dir </> "csc.dll"))
        Assert.That(Csc.managedCompiler (dir </> "csc.exe"), Is.EqualTo (dir </> "csc.dll"))
        Assert.That(Csc.managedCompiler (dir </> "csc.dll"), Is.EqualTo (dir </> "csc.dll"))
        Assert.That(Csc.managedCompiler (dir </> "other"), Is.EqualTo (dir </> "other"))
        Assert.That(Csc.managedCompiler "mcs", Is.EqualTo "mcs")

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

    // no targetfwk and no NETFX-TARGET: the SDK's own .NET framework, its targeting pack
    // passed whole, as the SDK does
    [<Test; Category("Integration")>]
    member x.``csc without a target framework defaults to the SDK framework``() =
        File.WriteAllText ("a.cs", "public class A { public static string Now() => System.DateTime.Now.ToString(); }")
        let resolved = ref None
        do xake {x.TestOptions with FileLog="csc-nofwk.log"; ThrowOnError = true} {
            wantOverride (["nofwk"])
            rules [
                "nofwk" => recipe {
                    let! c = Csc.ofSettings { CscSettingsType.Default with Src = !!"a.cs"; Out = File.make "nofwk/nofwk.dll" }
                    resolved.Value <- Some c
                    do! Csc.compile { CscSettingsType.Default with Src = !!"a.cs"; Out = File.make "nofwk/nofwk.dll" }
                }
            ]
        }
        let c = resolved.Value |> Option.get
        let sdkFwk = DotNetFwk.sdkFramework x.TestOptions.ProjectRoot |> Option.get
        Assert.That(c.Framework, Is.EqualTo sdkFwk)
        let refs = c.Dependencies.References |> List.map (fun r -> r.Path.Replace('\\', '/'))
        Assert.That(refs, Is.Not.Empty)
        Assert.That(refs |> List.forall (fun r -> r.Contains "/packs/Microsoft.NETCore.App.Ref/"), Is.True, "references come from the targeting pack")
        Assert.That(refs |> List.map Path.GetFileName, Does.Contain "System.Runtime.dll")
        Assert.That(File.Exists "nofwk/nofwk.dll", Is.True, "csc did not produce nofwk/nofwk.dll")

    // `dotnet <out>.dll` needs `<out>.runtimeconfig.json`, which csc does not write: it is
    // written when the rule declares it as one of its targets, and only then
    member private x.CompileHw (dir: string) (targets: string list) (fwk: string option) =
        if Directory.Exists dir then Directory.Delete (dir, true)
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText (dir </> "hw.cs", "class P { static void Main() { System.Console.WriteLine(\"Hello world!\"); } }")
        let targets = targets |> List.map (fun t -> dir </> t)
        let body = recipe {
            match fwk with
            | Some f -> do! csc { src !!(dir </> "hw.cs"); targetfwk f; grefs ["System.dll"; "mscorlib.dll"] }
            | None -> do! csc { src !!(dir </> "hw.cs") }
        }
        do xake {x.TestOptions with FileLog= dir + ".log"; ThrowOnError = true} {
            wantOverride [List.head targets]
            rules [ match targets with [ t ] -> t ..> body | ts -> ts *..> body ]
        }

    [<Test; Category("Integration")>]
    member x.``csc app declaring its runtimeconfig.json as a target writes it and runs``() =
        x.CompileHw "rc-app" ["hw.dll"; "hw.runtimeconfig.json"] None
        let sdkFwk = DotNetFwk.sdkFramework x.TestOptions.ProjectRoot |> Option.get
        let major = sdkFwk.Substring(3).Split('.').[0]
        let expected =
            "{\n  \"runtimeOptions\": {\n    \"tfm\": \"" + sdkFwk + "\",\n    \"framework\": {\n      \"name\": \"Microsoft.NETCore.App\",\n      \"version\": \"" + major + ".0.0\"\n    },\n    \"configProperties\": {\n      \"System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization\": false\n    }\n  }\n}"
        Assert.That(File.ReadAllText "rc-app/hw.runtimeconfig.json", Is.EqualTo expected)
        let psi = System.Diagnostics.ProcessStartInfo ("dotnet", "rc-app/hw.dll", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
        use p = System.Diagnostics.Process.Start psi
        let out = p.StandardOutput.ReadToEnd()
        let err = p.StandardError.ReadToEnd()
        p.WaitForExit()
        Assert.That(out.Trim(), Is.EqualTo "Hello world!", err)

    [<Test; Category("Integration")>]
    member x.``csc single target writes no runtimeconfig.json``() =
        x.CompileHw "rc-single" ["hw.exe"] None
        Assert.That(File.Exists "rc-single/hw.exe", Is.True)
        Assert.That(File.Exists "rc-single/hw.runtimeconfig.json", Is.False)

    [<Test; Category("Integration")>]
    member x.``csc runtimeconfig.json declared for .NET Framework fails``() =
        let ex = Assert.Catch(fun () -> x.CompileHw "rc-462" ["hw.exe"; "hw.runtimeconfig.json"] (Some "net-4.6.2"))
        let rec messages (e: exn) = if isNull e then "" else e.Message + "\n" + messages e.InnerException
        Assert.That(messages ex, Does.Contain "'hw.runtimeconfig.json' is declared as a target, but net-4.6.2 applications do not use one")
        Assert.That(File.Exists "rc-462/hw.runtimeconfig.json", Is.False)

    [<Test; Category("Integration")>]
    member x.``csc runtimeconfig.json named for another output fails naming both``() =
        let ex = Assert.Catch(fun () -> x.CompileHw "rc-other" ["app.dll"; "other.runtimeconfig.json"] None)
        let rec messages (e: exn) = if isNull e then "" else e.Message + "\n" + messages e.InnerException
        Assert.That(messages ex, Does.Contain "'other.runtimeconfig.json' is declared as a target, but the output is 'app.dll', whose runtimeconfig is 'app.runtimeconfig.json'")

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

