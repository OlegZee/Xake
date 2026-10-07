namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// The script variable `NUGET_PACKAGES`: the build's package folder, overriding the
/// environment variable for everything Xake restores or looks up there from a recipe -- the
/// toolset compiler, the reference packs, fsc's default FSharp.Core.
[<TestFixture>]
type ``Package folder script variable``() =
    inherit XakeTestBase("pkgroot")

    let toolsetVersion = "4.12.0"
    let norm (p: string) = p.Replace('\\', '/').TrimEnd '/'

    /// A fresh, empty folder under the test directory.
    let freshDir (name: string) =
        let dir = Path.GetFullPath name
        if Directory.Exists dir then Directory.Delete (dir, true)
        dir

    let packageIds dir =
        Directory.GetDirectories dir |> Array.map Path.GetFileName |> Array.sort |> List.ofArray

    [<Test; Category("Integration")>]
    member x.``an absolute NUGET_PACKAGES takes the toolset compiler and the reference pack``() =
        let pkgs = freshDir "pkgs-abs"
        File.WriteAllText ("PkgAbs.cs", "public class PkgAbs {}\n")
        let resolved = ref None

        do xake {x.TestOptions with FileLog = "pkgroot-abs.log"; ThrowOnError = true
                                    Vars = ["NUGET_PACKAGES", pkgs]} {
            wantOverride (["PkgAbs.dll"; "pkgabs"])
            rules [
                "PkgAbs.dll" ..> csc {
                    targetfwk "netstandard2.0"
                    src !!"PkgAbs.cs"
                    grefs ["netstandard.dll"]
                    toolset toolsetVersion
                }
                "pkgabs" => recipe {
                    let! c = csc {
                        targetfwk "netstandard2.0"
                        out (File.make "PkgAbs.dll")
                        src !!"PkgAbs.cs"
                        toolset toolsetVersion
                        resolve
                    }
                    resolved.Value <- Some c
                }
            ]
        }

        Assert.That(File.Exists "PkgAbs.dll", Is.True, "csc did not produce PkgAbs.dll")
        let c = resolved.Value |> Option.get
        let root = norm pkgs + "/"
        Assert.That(norm c.Dependencies.Compiler.Path,
                    Is.EqualTo (root + "microsoft.net.compilers.toolset/" + toolsetVersion + "/tasks/netcore/bincore/csc.dll"))
        let refs = c.Dependencies.References |> List.map (fun r -> norm r.Path)
        Assert.That(refs, Does.Contain (root + "netstandard.library/2.0.3/build/netstandard2.0/ref/mscorlib.dll"))
        Assert.That(refs |> List.filter (fun r -> not (r.StartsWith root)), Is.Empty,
                    "every reference comes from the script variable's folder")
        // exactly the two packages, restored into the folder (not into the machine's cache)
        Assert.That(packageIds pkgs, Is.EqualTo [ "microsoft.net.compilers.toolset"; "netstandard.library" ])
        Assert.That(File.Exists (pkgs </> "microsoft.net.compilers.toolset" </> toolsetVersion </> "tasks" </> "netcore" </> "bincore" </> "csc.dll"))
        Assert.That(Directory.Exists (pkgs </> "netstandard.library" </> "2.0.3" </> "build" </> "netstandard2.0" </> "ref"))

    [<Test; Category("Integration")>]
    member x.``a relative NUGET_PACKAGES is under the project root and takes fsc's FSharp.Core``() =
        let pkgs = freshDir "pkgs-rel"
        File.WriteAllText ("pkgrel.fs", "module PkgRel\nlet a = 1\n")
        let resolved = ref None

        do xake {x.TestOptions with FileLog = "pkgroot-rel.log"; ThrowOnError = true
                                    Vars = ["NUGET_PACKAGES", "pkgs-rel"]} {
            wantOverride (["pkgrel"])
            rules [
                "pkgrel" => recipe {
                    let! f = fsc { targetfwk "netstandard2.0"; src !!"pkgrel.fs"; out (File.make "pkgrel.dll"); resolve }
                    resolved.Value <- Some f
                }
            ]
        }

        let f = resolved.Value |> Option.get
        let root = norm (x.TestOptions.ProjectRoot </> "pkgs-rel") + "/"
        Assert.That(norm pkgs + "/", Is.EqualTo root)
        let refs = f.Dependencies.References |> List.map (fun r -> norm r.Path)
        Assert.That(refs, Does.Contain (root + "fsharp.core/" + DotNetFwk.fsharpCoreVersion + "/lib/netstandard2.0/FSharp.Core.dll"))
        Assert.That(refs, Does.Contain (root + "netstandard.library/2.0.3/build/netstandard2.0/ref/netstandard.dll"))
        Assert.That(packageIds pkgs, Is.EqualTo [ "fsharp.core"; "netstandard.library" ])

    [<Test; Category("Integration")>]
    member x.``without the script variable the environment's package folder is used``() =
        File.WriteAllText ("PkgEnv.cs", "public class PkgEnv {}\n")
        let resolved = ref None

        do xake {x.TestOptions with FileLog = "pkgroot-env.log"; ThrowOnError = true} {
            wantOverride (["pkgenv"])
            rules [
                "pkgenv" => recipe {
                    let! c = csc {
                        targetfwk "netstandard2.0"
                        out (File.make "PkgEnv.dll")
                        src !!"PkgEnv.cs"
                        toolset toolsetVersion
                        resolve
                    }
                    resolved.Value <- Some c
                }
            ]
        }

        let c = resolved.Value |> Option.get
        let root = norm (DotNetFwk.nugetRoot ()) + "/"
        Assert.That(norm c.Dependencies.Compiler.Path, Does.StartWith (root + "microsoft.net.compilers.toolset/"))
        Assert.That(c.Dependencies.References |> List.map (fun r -> norm r.Path),
                    Does.Contain (root + "netstandard.library/2.0.3/build/netstandard2.0/ref/mscorlib.dll"))

    [<Test; Category("Integration")>]
    member x.``with NUGET_FETCH off a missing toolset fails naming it and downloads nothing``() =
        let pkgs = freshDir "pkgs-off"
        Directory.CreateDirectory pkgs |> ignore
        File.WriteAllText ("PkgOff.cs", "public class PkgOff {}\n")
        let ex =
            Assert.Catch(fun () ->
                do xake {x.TestOptions with FileLog = "pkgroot-off.log"; ThrowOnError = true
                                            Vars = ["NUGET_PACKAGES", pkgs; "NUGET_FETCH", "off"]} {
                    wantOverride (["PkgOff.dll"])
                    rules [
                        "PkgOff.dll" ..> csc {
                            targetfwk "netstandard2.0"
                            src !!"PkgOff.cs"
                            grefs ["netstandard.dll"]
                            toolset toolsetVersion
                        }
                    ]
                })
        let rec messages (e: exn) = if isNull e then "" else e.Message + "\n" + messages e.InnerException
        let text = messages ex
        Assert.That(text, Does.Contain "fetching packages is off (NUGET_FETCH=off)")
        // the first package the compile needs is the reference pack; the toolset is named when
        // that one is present (next test)
        Assert.That(text, Does.Contain "NETStandard.Library 2.0.3")
        Assert.That(Directory.GetFileSystemEntries pkgs, Is.Empty, "nothing is restored")
        Assert.That(File.Exists "PkgOff.dll", Is.False)

    [<Test; Category("Integration")>]
    member x.``with NUGET_FETCH off packages already in the folder compile without a download``() =
        let pkgs = freshDir "pkgs-warm"
        let restoreDir = x.TestOptions.ProjectRoot </> "obj" </> "xake" </> "restore"
        File.WriteAllText ("PkgWarm.cs", "public class PkgWarm {}\n")
        let build name (vars: (string * string) list) =
            do xake {x.TestOptions with FileLog = "pkgroot-warm.log"; ThrowOnError = true
                                        Vars = ["NUGET_PACKAGES", pkgs] @ vars} {
                wantOverride ([name])
                rules [
                    name ..> csc {
                        targetfwk "netstandard2.0"
                        src !!"PkgWarm.cs"
                        grefs ["netstandard.dll"]
                    }
                ]
            }
        let toolset = [ "CSC_TOOLSET", toolsetVersion ]

        // the reference pack only: the toolset is still missing, and named
        build "PkgWarm0.dll" []
        Assert.That(packageIds pkgs, Is.EqualTo [ "netstandard.library" ])
        let ex = Assert.Catch(fun () -> build "PkgWarm1.dll" (toolset @ [ "NUGET_FETCH", "off" ]))
        let rec messages (e: exn) = if isNull e then "" else e.Message + "\n" + messages e.InnerException
        Assert.That(messages ex, Does.Contain ("Microsoft.Net.Compilers.Toolset " + toolsetVersion))
        Assert.That(messages ex, Does.Contain "fetching packages is off (NUGET_FETCH=off)")
        Assert.That(packageIds pkgs, Is.EqualTo [ "netstandard.library" ])

        // warm: fetch on once, then off with no restore project written
        build "PkgWarm2.dll" toolset
        if Directory.Exists restoreDir then Directory.Delete (restoreDir, true)
        build "PkgWarm3.dll" (toolset @ [ "NUGET_FETCH", "off" ])
        Assert.That(File.Exists "PkgWarm3.dll", Is.True)
        let projects =
            if Directory.Exists restoreDir then Directory.GetFiles (restoreDir, "restore.csproj", SearchOption.AllDirectories) else [||]
        Assert.That(projects, Is.Empty, "no restore project is written with fetching off")
