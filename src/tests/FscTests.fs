namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

[<TestFixture>]
type ``Fsc record tests``() =
    inherit XakeTestBase("fsc")

    // what `dotnet build` hands fsc for a netstandard library (the design note's probe), cut
    // down to one item of each kind
    let probeLine = [
        "-o:obj/Release/netstandard2.0/Lib.dll"
        "-g"
        "--debug:portable"
        "--noframework"
        "--define:TRACE"
        "--define:NETSTANDARD"
        "--doc:obj/Release/netstandard2.0/Lib.xml"
        "--optimize+"
        "--tailcalls-"
        "-r:/nuget/fsharp.core/8.0.100/lib/netstandard2.0/FSharp.Core.dll"
        "-r:/nuget/netstandard.library/2.0.3/build/netstandard2.0/ref/netstandard.dll"
        "--target:library"
        "--warn:3"
        "--warnaserror:3239"
        "--targetprofile:netstandard"
        "--nocopyfsharpcore"
        "--deterministic+"
        "--resource:r.txt,Lib.r.txt"
        "obj/Release/netstandard2.0/Lib.AssemblyInfo.fs"
        "A.fs"
        "B.fs"
    ]

    [<Test>]
    member __.``FscArgs parses fsc's own switch forms``() =
        Assert.That(FscArgs.parse "--out:a.dll", Is.EqualTo (FscArgs.Switch ("--", "out", "a.dll")))
        Assert.That(FscArgs.parse "-o:a.dll", Is.EqualTo (FscArgs.Switch ("-", "o", "a.dll")))
        Assert.That(FscArgs.parse "--optimize+", Is.EqualTo (FscArgs.Switch ("--", "optimize+", "")))
        Assert.That(FscArgs.parse "-r:C:\\lib\\a.dll", Is.EqualTo (FscArgs.Switch ("-", "r", "C:\\lib\\a.dll")))
        // a leading '/' is a path for fsc, not a switch
        Assert.That(FscArgs.parse "/src/a.fs", Is.EqualTo (FscArgs.Source "/src/a.fs"))
        Assert.That(FscArgs.canonical "o", Is.EqualTo "out")
        Assert.That(FscArgs.canonical "r", Is.EqualTo "reference")
        Assert.That(FscArgs.canonical "I", Is.EqualTo "lib")

    [<Test>]
    member __.``FscArgs formats back what it parsed``() =
        let roundTrip = probeLine |> List.map (FscArgs.parse >> FscArgs.format)
        Assert.That(roundTrip, Is.EqualTo probeLine)

    [<Test>]
    member __.``FscArgs knows inputs and outputs``() =
        Assert.That(FscArgs.outputs probeLine,
                    Is.EqualTo [ "obj/Release/netstandard2.0/Lib.dll"; "obj/Release/netstandard2.0/Lib.xml" ])
        Assert.That(FscArgs.inputs probeLine,
                    Is.EqualTo [ "/nuget/fsharp.core/8.0.100/lib/netstandard2.0/FSharp.Core.dll"
                                 "/nuget/netstandard.library/2.0.3/build/netstandard2.0/ref/netstandard.dll"
                                 "r.txt"
                                 "obj/Release/netstandard2.0/Lib.AssemblyInfo.fs"; "A.fs"; "B.fs" ])
        Assert.That(FscArgs.sources probeLine,
                    Is.EqualTo [ "obj/Release/netstandard2.0/Lib.AssemblyInfo.fs"; "A.fs"; "B.fs" ])
        Assert.That(FscArgs.switchValues "out" probeLine, Is.EqualTo [ "obj/Release/netstandard2.0/Lib.dll" ])

    [<Test>]
    member __.``FscArgs absolutizes paths and leaves the rest``() =
        let dir = Path.GetFullPath "/proj"
        let abs = FscArgs.absolutize dir probeLine
        let expected (p: string) = (Path.GetFullPath (Path.Combine (dir, p))).Replace('\\', '/')
        Assert.That(abs, Does.Contain ("-o:" + expected "obj/Release/netstandard2.0/Lib.dll"))
        Assert.That(abs, Does.Contain ("--resource:" + expected "r.txt" + ",Lib.r.txt"))
        Assert.That(abs, Does.Contain (expected "A.fs"))
        Assert.That(abs, Does.Contain "--optimize+")
        Assert.That(abs, Does.Contain "--define:TRACE")

    [<Test>]
    member __.``Fsc.ofArgs factors the sections and Args rebuilds the line``() =
        let f = Fsc.ofArgs probeLine
        Assert.That(f.Defines, Is.EqualTo [ "TRACE"; "NETSTANDARD" ])
        Assert.That(f.Sources, Is.EqualTo [ "obj/Release/netstandard2.0/Lib.AssemblyInfo.fs"; "A.fs"; "B.fs" ])
        Assert.That(f.Dependencies.References |> List.map (fun r -> r.Path),
                    Is.EqualTo [ "/nuget/fsharp.core/8.0.100/lib/netstandard2.0/FSharp.Core.dll"
                                 "/nuget/netstandard.library/2.0.3/build/netstandard2.0/ref/netstandard.dll" ])
        Assert.That(f.Dependencies.Analyzers, Is.Empty)
        Assert.That(f.Dependencies.Compiler.Tool, Is.EqualTo "fsc")
        Assert.That(f.Options |> List.filter Fsc.isMarker, Is.EqualTo [ "@Defines"; "@References"; "@Sources" ])
        Assert.That(f.Args, Is.EqualTo probeLine)
        Assert.That(f.Output, Is.EqualTo (Some "obj/Release/netstandard2.0/Lib.dll"))

    [<Test>]
    member __.``Fsc.mapPaths rewrites paths and drops a moved reference's hash``() =
        let f = Fsc.ofArgs probeLine
        let f = { f with Fsc.Dependencies = { f.Dependencies with References = f.Dependencies.References |> List.map (fun r -> { r with Sha256 = "x" }) } }
        let moved = f |> Fsc.mapPaths (fun p -> if p.StartsWith "/nuget/" then "/cache/" + p.Substring 7 else p)
        Assert.That(moved.Dependencies.References |> List.map (fun r -> r.Path, r.Sha256),
                    Is.EqualTo [ "/cache/fsharp.core/8.0.100/lib/netstandard2.0/FSharp.Core.dll", ""
                                 "/cache/netstandard.library/2.0.3/build/netstandard2.0/ref/netstandard.dll", "" ])
        Assert.That(moved.Options, Is.EqualTo f.Options)

    [<Test>]
    member x.``fsc without a target framework fails asking for one``() =
        let build () =
            xake {x.TestOptions with FileLog="fsc-nofwk.log"; ThrowOnError = true} {
                wantOverride (["nofwk"])
                rules [
                    "nofwk" => recipe {
                        let! _ = fsc { src !!"a.fs"; out (File.make "nofwk.dll"); resolve }
                        ()
                    }
                ]
            }
        let ex = Assert.Throws<XakeException> (fun () -> build () |> ignore)
        Assert.That(ex.ToString(), Does.Contain "fsc needs a target framework: set targetfwk in the fsc block or the NETFX-TARGET script variable")
        Assert.That(ex.ToString(), Does.Contain "targetfwk \"netstandard2.0\"")

    // `resolve` describes the compilation and stops: the SDK's fsc.dll is the compiler, the
    // sources keep their order, nothing is compiled.
    [<Test; Category("Integration")>]
    member x.``fsc resolve returns the Fsc without compiling``() =
        File.WriteAllText ("b.fs", "module B\nlet b = 1\n")
        File.WriteAllText ("a.fs", "module A\nlet a = B.b\n")
        let fsharpCore = System.Reflection.Assembly.GetAssembly(typeof<option<int>>).Location
        let resolved = ref None
        do xake {x.TestOptions with FileLog="fsc-resolve.log"; ThrowOnError = true} {
            wantOverride (["resolve"])
            rules [
                "resolve" => recipe {
                    let! f = fsc {
                        targetfwk "netstandard2.0"
                        src (Fileset.Empty ++ "b.fs" ++ "a.fs")
                        ref (Fileset.Empty ++ fsharpCore)
                        out (File.make "resolved/r.dll")
                        define ["A"; "B"]
                        notailcalls
                        resolve
                    }
                    resolved.Value <- Some f
                }
            ]
        }
        let f = resolved.Value |> Option.get
        let norm (p: string) = p.Replace('\\', '/')
        Assert.That(f.Name, Is.EqualTo "r")
        Assert.That(f.Framework, Is.EqualTo "netstandard2.0")
        Assert.That(f.Sources |> List.map (norm >> Path.GetFileName), Is.EqualTo [ "b.fs"; "a.fs" ])
        Assert.That(f.Defines, Is.EqualTo [ "A"; "B" ])
        Assert.That(f.Dependencies.Compiler.Tool, Is.EqualTo "fsc")
        Assert.That(f.Dependencies.Compiler.Path, Does.EndWith "fsc.dll")
        Assert.That(File.Exists f.Dependencies.Compiler.Path, Is.True)
        Assert.That(f.Dependencies.References |> List.map (fun r -> Path.GetFileName r.Path), Does.Contain "FSharp.Core.dll")
        Assert.That(f.Dependencies.References |> List.map (fun r -> Path.GetFileName r.Path), Does.Contain "netstandard.dll")
        Assert.That(f.Args, Does.Contain "--noframework")
        Assert.That(f.Args, Does.Contain "--targetprofile:netstandard")
        Assert.That(f.Args, Does.Contain "--tailcalls-")
        Assert.That(f.Args |> List.forall (fun a -> not (a.StartsWith "/") || File.Exists a), Is.True, "no '/' switches")
        Assert.That(f.Output |> Option.map norm, Is.EqualTo (Some (norm (Path.GetFullPath "resolved/r.dll"))))
        Assert.That(File.Exists "resolved/r.dll", Is.False, "resolve must not compile")

    // `Fsc.run` end to end on the SDK's fsc.dll: netstandard2.0 against the FSharp.Core the
    // tests run on (the SDK has no net4x FSharp.Core, so this is the one target that works on
    // every OS). A source directory with a space checks the response file's quoting, a doc file
    // in a missing directory the runner's output step, and two clean builds have to produce
    // the same bytes.
    [<Test; Category("Integration")>]
    member x.``Fsc.run compiles netstandard2.0 deterministically``() =
        Directory.CreateDirectory "src dir" |> ignore
        File.WriteAllText ("src dir/b.fs", "module B\n/// doc\nlet b = 1\n")
        File.WriteAllText ("src dir/a.fs", "module A\nlet a = B.b + 1\n")
        let fsharpCore = System.Reflection.Assembly.GetAssembly(typeof<option<int>>).Location
        let built = ref 0
        let build () =
            xake {x.TestOptions with FileLog="fsc-run.log"; ThrowOnError = true} {
                wantOverride (["e2e/e2e.dll"])
                rules [
                    "e2e/e2e.dll" ..> recipe {
                        built.Value <- built.Value + 1
                        do! fsc {
                            targetfwk "netstandard2.0"
                            src (Fileset.Empty ++ "src dir/b.fs" ++ "src dir/a.fs")
                            ref (Fileset.Empty ++ fsharpCore)
                            doc (File.make "e2e/docs/e2e.xml")
                            args ["--deterministic+"; "--nocopyfsharpcore"]
                        }
                    }
                ]
            }
        let clean () =
            for p in ["e2e"; "src dir/../e2e"] do
                if Directory.Exists p then Directory.Delete (p, true)
            try File.Delete ".xake" with _ -> ()

        clean ()
        build ()
        Assert.That(File.Exists "e2e/e2e.dll", Is.True, "fsc did not produce e2e.dll")
        Assert.That(File.Exists "e2e/docs/e2e.xml", Is.True, "the doc directory was not created")
        let first = File.ReadAllBytes "e2e/e2e.dll"

        clean ()
        build ()
        Assert.That(built.Value, Is.EqualTo 2)
        Assert.That(File.ReadAllBytes "e2e/e2e.dll", Is.EqualTo first, "two clean builds differ")

    // a recorded hash that no longer matches the file fails the build before the compiler
    // starts: no output appears
    [<Test; Category("Integration")>]
    member x.``Fsc.run fails on a reference hash mismatch before compiling``() =
        File.WriteAllText ("hm.fs", "module Hm\nlet x = 1\n")
        let fsharpCore = System.Reflection.Assembly.GetAssembly(typeof<option<int>>).Location
        let tampered = String.replicate 64 "0"
        if File.Exists "hm/hm.dll" then File.Delete "hm/hm.dll"
        let build () =
            xake {x.TestOptions with FileLog="fsc-hash.log"; ThrowOnError = true} {
                wantOverride (["hm"])
                rules [
                    "hm" => recipe {
                        let! f = fsc {
                            targetfwk "netstandard2.0"
                            src !!"hm.fs"
                            ref (Fileset.Empty ++ fsharpCore)
                            out (File.make "hm/hm.dll")
                            resolve
                        }
                        let f = Fsc.rehash f
                        Assert.That(f.Dependencies.Compiler.Sha256, Is.Not.Empty, "rehash hashes fsc.dll")
                        let refs =
                            f.Dependencies.References |> List.map (fun r ->
                                if r.Path.EndsWith "FSharp.Core.dll" then { r with Sha256 = tampered } else r)
                        let f = { f with Fsc.Dependencies = { f.Dependencies with References = refs } }
                        do! Fsc.run FscRunOptions.Default f
                    }
                ]
            }
        let ex = Assert.Throws<XakeException> (fun () -> build () |> ignore)
        Assert.That(ex.ToString(), Does.Contain "hash mismatch")
        Assert.That(ex.ToString(), Does.Contain (sprintf "FSharp.Core.dll: expected %s, got " tampered))
        Assert.That(File.Exists "hm/hm.dll", Is.False, "the compiler must not have run")
