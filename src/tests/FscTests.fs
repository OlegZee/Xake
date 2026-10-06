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

    // no `targetfwk` and no NETFX-TARGET: the .NET framework of the SDK the build runs on,
    // referenced through the SDK's own targeting pack, plus the SDK's FSharp.Core
    [<Test; Category("Integration")>]
    member x.``fsc without a target framework defaults to the SDK framework``() =
        File.WriteAllText ("a.fs", "module A\nlet a = 1\n")
        let resolved = ref None
        do xake {x.TestOptions with FileLog="fsc-nofwk.log"; ThrowOnError = true} {
            wantOverride (["nofwk"])
            rules [
                "nofwk" => recipe {
                    let! f = fsc { src !!"a.fs"; out (File.make "nofwk.dll"); resolve }
                    resolved.Value <- Some f
                }
            ]
        }
        let f = resolved.Value |> Option.get
        let sdkFwk = DotNetFwk.sdkFramework x.TestOptions.ProjectRoot |> Option.get
        let norm (p: string) = p.Replace('\\', '/')
        Assert.That(sdkFwk, Does.Match @"^net\d+\.\d+$")
        Assert.That(f.Framework, Is.EqualTo sdkFwk)
        Assert.That(f.Args, Does.Contain "--noframework")
        Assert.That(f.Args, Does.Contain "--targetprofile:netcore")
        let refs = f.Dependencies.References |> List.map (fun r -> norm r.Path)
        let packRefs = refs |> List.filter (fun r -> r.Contains "/packs/Microsoft.NETCore.App.Ref/" && r.Contains ("/ref/" + sdkFwk + "/"))
        Assert.That(packRefs |> List.map Path.GetFileName, Does.Contain "System.Runtime.dll")
        Assert.That(packRefs |> List.map Path.GetFileName, Does.Contain "mscorlib.dll")
        // the SDK's FSharp.Core, the one next to fsc.dll
        let fsharpCore = refs |> List.filter (fun r -> Path.GetFileName r = "FSharp.Core.dll")
        Assert.That(fsharpCore, Is.EqualTo [ norm (Path.GetDirectoryName f.Dependencies.Compiler.Path </> "FSharp.Core.dll") ])

    // the default framework compiles with nothing but the SDK: no targetfwk, no FSharp.Core
    [<Test; Category("Integration")>]
    member x.``fsc compiles for the SDK framework with no targetfwk and no ref``() =
        try File.Delete "sdkfwk/hello.dll" with _ -> ()
        do xake {x.TestOptions with FileLog="fsc-sdkfwk.log"; ThrowOnError = true} {
            wantOverride (["sdkfwk/hello.dll"])
            rules [
                "sdkfwk/hello.dll" ..> recipe {
                    do! need ["hello.fs"]
                    do! fsc { src !!"hello.fs" }
                }
                "hello.fs" ..> writeText "module Hello\nlet greet name = sprintf \"Hello, %s\" name\nlet now () = System.DateTime.Now\n"
            ]
        }
        Assert.That(File.Exists "sdkfwk/hello.dll", Is.True, "fsc did not produce sdkfwk/hello.dll")

    [<Test; Category("Integration")>]
    member x.``fsc app declaring its runtimeconfig.json as a target writes it``() =
        if Directory.Exists "rc-fsc" then Directory.Delete ("rc-fsc", true)
        Directory.CreateDirectory "rc-fsc" |> ignore
        File.WriteAllText ("rc-fsc/hw.fs", "[<EntryPoint>]\nlet main _ = printfn \"Hello world!\"; 0\n")
        do xake {x.TestOptions with FileLog="fsc-rc.log"; ThrowOnError = true} {
            wantOverride (["rc-fsc/hw.dll"])
            rules [ ["rc-fsc/hw.dll"; "rc-fsc/hw.runtimeconfig.json"] *..> recipe { do! fsc { src !!"rc-fsc/hw.fs" } } ]
        }
        let sdkFwk = DotNetFwk.sdkFramework x.TestOptions.ProjectRoot |> Option.get
        Assert.That(File.ReadAllText "rc-fsc/hw.runtimeconfig.json", Does.Contain (sprintf "\"tfm\": \"%s\"" sdkFwk))

    // an explicit .NET moniker, in both spellings, is resolved the same way
    [<Test; Category("Integration")>]
    member x.``fsc targetfwk names a .NET framework explicitly``() =
        File.WriteAllText ("a.fs", "module A\nlet a = 1\n")
        let sdkFwk = DotNetFwk.sdkFramework x.TestOptions.ProjectRoot |> Option.get
        let resolved = ResizeArray<Fsc>()
        do xake {x.TestOptions with FileLog="fsc-netfwk.log"; ThrowOnError = true} {
            wantOverride (["explicit"])
            rules [
                "explicit" => recipe {
                    let! f1 = fsc { targetfwk sdkFwk; src !!"a.fs"; out (File.make "explicit.dll"); resolve }
                    let! f2 = fsc { targetfwk ("sdk-" + sdkFwk); src !!"a.fs"; out (File.make "explicit.dll"); resolve }
                    resolved.Add f1
                    resolved.Add f2
                }
            ]
        }
        Assert.That(resolved.[0].Framework, Is.EqualTo sdkFwk)
        Assert.That(resolved.[1].Dependencies.References, Is.EqualTo resolved.[0].Dependencies.References)
        Assert.That(resolved.[0].Args, Does.Contain "--targetprofile:netcore")

    [<Test>]
    member __.``.NET monikers are told from .NET Framework and netstandard ones``() =
        Assert.That(DotNetFwk.netcoreMoniker "net10.0", Is.EqualTo (Some "net10.0"))
        Assert.That(DotNetFwk.netcoreMoniker "sdk-net8.0", Is.EqualTo (Some "net8.0"))
        Assert.That(DotNetFwk.netcoreMoniker "NET9.0", Is.EqualTo (Some "net9.0"))
        for other in [ "net472"; "net-4.6.2"; "netstandard2.0"; "net4.8"; "4.0"; "mono-4.5"; "" ] do
            Assert.That(DotNetFwk.netcoreMoniker other, Is.EqualTo None, other)

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

    // the default framework through the runner: a rehashed record (the whole targeting pack and
    // the SDK's FSharp.Core hashed) compiles
    [<Test; Category("Integration")>]
    member x.``Fsc.run compiles a rehashed record for the SDK framework``() =
        File.WriteAllText ("rh.fs", "module Rh\nlet now () = System.DateTime.Now\n")
        if File.Exists "rh/rh.dll" then File.Delete "rh/rh.dll"
        do xake {x.TestOptions with FileLog="fsc-rehash.log"; ThrowOnError = true} {
            wantOverride (["rh"])
            rules [
                "rh" => recipe {
                    let! f = fsc { src !!"rh.fs"; out (File.make "rh/rh.dll"); resolve }
                    let f = Fsc.rehash f
                    Assert.That(f.Dependencies.References |> List.forall (fun r -> r.Sha256 <> ""), Is.True, "every reference hashed")
                    do! Fsc.run FscRunOptions.Default f
                }
            ]
        }
        Assert.That(File.Exists "rh/rh.dll", Is.True, "Fsc.run did not produce rh/rh.dll")

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

    // .NET Framework with no `ref`: the FSharp.Core package (netstandard2.0, pinned version)
    // and a netstandard facade are implicit, so the compile works on any OS from the SDK alone
    [<Test; Category("Integration")>]
    member x.``fsc compiles a net462 exe with no ref``() =
        try Directory.Delete ("fx462", true) with _ -> ()
        do xake {x.TestOptions with FileLog="fsc-net462.log"; ThrowOnError = true} {
            wantOverride (["fx462/hello.exe"])
            rules [
                "fx462/hello.exe" ..> recipe {
                    do! need ["hello462.fs"]
                    do! fsc {
                        targetfwk "net-4.6.2"
                        target Exe
                        src !!"hello462.fs"
                        args ["--nocopyfsharpcore"]
                    }
                }
                "hello462.fs" ..> writeText "module Hello\nlet lz = lazy (sprintf \"%A\" (Some 1))\n[<EntryPoint>]\nlet main _ = printfn \"%s\" lz.Value; 0\n"
            ]
        }
        Assert.That(File.Exists "fx462/hello.exe", Is.True, "fsc did not produce fx462/hello.exe")
        Assert.That(File.Exists "fx462/FSharp.Core.dll", Is.False, "--nocopyfsharpcore")

    member private x.ResolveFsc (name: string) (vars: (string * string) list) (build: unit -> Recipe<ExecContext, Fsc>) =
        File.WriteAllText ("a.fs", "module A\nlet a = 1\n")
        let resolved = ref None
        do xake {x.TestOptions with FileLog = sprintf "fsc-%s.log" name; ThrowOnError = true; Vars = vars} {
            wantOverride ([name])
            rules [
                name => recipe {
                    let! f = build ()
                    resolved.Value <- Some f
                }
            ]
        }
        let f = resolved.Value |> Option.get
        f.Dependencies.References |> List.map (fun r -> r.Path.Replace('\\', '/'))

    // what lands in the record: FSharp.Core from the package folder (`$(NuGetPackageRoot)`
    // once tokenized), the reference assemblies' mscorlib, and the netstandard facade -- the
    // SDK's below 4.7.1, the reference assemblies' own from 4.7.1
    [<Test; Category("Integration")>]
    member x.``fsc resolve for .NET Framework lists the implicit FSharp.Core and netstandard``() =
        let nuget = (DotNetFwk.nugetRoot ()).Replace('\\', '/').TrimEnd '/'
        let refs462 = x.ResolveFsc "res462" [] (fun () -> fsc { targetfwk "net-4.6.2"; src !!"a.fs"; out (File.make "r462.dll"); resolve })
        Assert.That(refs462 |> List.map Path.GetFileName, Is.EqualTo [ "FSharp.Core.dll"; "mscorlib.dll"; "netstandard.dll" ])
        Assert.That(refs462.[0], Is.EqualTo (sprintf "%s/fsharp.core/%s/lib/netstandard2.0/FSharp.Core.dll" nuget DotNetFwk.fsharpCoreVersion))
        Assert.That(refs462.[1], Does.StartWith (nuget + "/microsoft.netframework.referenceassemblies.net462/"))
        Assert.That(refs462.[2], Does.EndWith "/Microsoft/Microsoft.NET.Build.Extensions/net461/lib/netstandard.dll")

        let refs472 = x.ResolveFsc "res472" [] (fun () -> fsc { targetfwk "net472"; src !!"a.fs"; out (File.make "r472.dll"); resolve })
        Assert.That(refs472 |> List.map Path.GetFileName, Is.EqualTo [ "FSharp.Core.dll"; "mscorlib.dll"; "netstandard.dll" ])
        Assert.That(refs472.[2], Does.StartWith (nuget + "/microsoft.netframework.referenceassemblies.net472/"))
        Assert.That(refs472.[2], Does.EndWith "/Facades/netstandard.dll")

    // netstandard: the package's FSharp.Core rather than the one fsc would pick up silently
    // (its own, untracked); netstandard2.1 takes the package's netstandard2.1 build
    [<Test; Category("Integration")>]
    member x.``fsc resolve for netstandard lists the package FSharp.Core``() =
        let nuget = (DotNetFwk.nugetRoot ()).Replace('\\', '/').TrimEnd '/'
        let refs20 = x.ResolveFsc "resns20" [] (fun () -> fsc { targetfwk "netstandard2.0"; src !!"a.fs"; out (File.make "ns20.dll"); resolve })
        Assert.That(refs20, Is.EqualTo [ sprintf "%s/fsharp.core/%s/lib/netstandard2.0/FSharp.Core.dll" nuget DotNetFwk.fsharpCoreVersion
                                         sprintf "%s/netstandard.library/2.0.3/build/netstandard2.0/ref/netstandard.dll" nuget ])
        let refs21 = x.ResolveFsc "resns21" [] (fun () -> fsc { targetfwk "netstandard2.1"; src !!"a.fs"; out (File.make "ns21.dll"); resolve })
        Assert.That(refs21.[0], Is.EqualTo (sprintf "%s/fsharp.core/%s/lib/netstandard2.1/FSharp.Core.dll" nuget DotNetFwk.fsharpCoreVersion))

    // a `ref` on an FSharp.Core.dll (or a netstandard.dll) replaces the implicit one
    [<Test; Category("Integration")>]
    member x.``fsc ref on FSharp.Core and netstandard suppresses the defaults``() =
        let fsharpCore = System.Reflection.Assembly.GetAssembly(typeof<option<int>>).Location
        let facade = Path.GetFullPath "facade/netstandard.dll"
        Directory.CreateDirectory "facade" |> ignore
        File.WriteAllText (facade, "")
        let refs = x.ResolveFsc "resref" [] (fun () ->
            fsc { targetfwk "net-4.6.2"; src !!"a.fs"; ref (Fileset.Empty ++ fsharpCore ++ facade); out (File.make "rref.dll"); resolve })
        Assert.That(refs |> List.map Path.GetFileName, Is.EqualTo [ "FSharp.Core.dll"; "netstandard.dll"; "mscorlib.dll" ])
        Assert.That(refs.[0], Is.EqualTo (fsharpCore.Replace('\\', '/')))
        Assert.That(refs.[1], Is.EqualTo (facade.Replace('\\', '/')))

    // the script variable FSHARP_CORE_VERSION picks the package version
    [<Test; Category("Integration")>]
    member x.``FSHARP_CORE_VERSION picks the FSharp.Core package version``() =
        let nuget = (DotNetFwk.nugetRoot ()).Replace('\\', '/').TrimEnd '/'
        let refs = x.ResolveFsc "resver" ["FSHARP_CORE_VERSION", "8.0.403"] (fun () ->
            fsc { targetfwk "net-4.6.2"; src !!"a.fs"; out (File.make "rver.dll"); resolve })
        Assert.That(refs.[0], Is.EqualTo (nuget + "/fsharp.core/8.0.403/lib/netstandard2.0/FSharp.Core.dll"))
