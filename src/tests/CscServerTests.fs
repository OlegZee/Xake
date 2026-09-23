namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// The Roslyn compiler server (`VBCSCompiler`) behind `csc {}`: `run` adds `/shared` (and
/// `/keepalive`) on the command line when the compiler about to run has a `VBCSCompiler.dll`
/// next to it, so a warm server compiles instead of a fresh csc process per assembly. The
/// switches are client-side -- the client strips them before the request reaches the server --
/// and never enter a lock's `Args`; the output has to be byte-identical either way (with
/// `/deterministic`, which is what makes two compiles comparable at all).
[<TestFixture>]
type ``Csc compiler server``() =
    inherit XakeTestBase("csc-server")

    let settings (server: CompilerServer) (src: string) (out: string) = {
        CscSettingsType.Default with
            Src = !!src
            Out = File.make out
            Target = Library
            TargetFramework = "net-4.6.2"
            RefGlobal = ["System.dll"; "System.Core.dll"]
            CommandArgs = ["/deterministic"]
            Server = server
    }

    member private x.Build (label: string) (settings: CscSettingsType) =
        xake {x.TestOptions with FileLog = label + ".log"; FileLogLevel = Verbosity.Diag; ThrowOnError = true} {
            wantOverride ([label])
            rules [ label => recipe { do! Csc settings } ]
        }

    [<Test; Category("Integration")>]
    member x.``server and in-process compiles produce byte-identical output``() =

        File.WriteAllText ("Server.cs", "public class Server { public static int Answer() { return 42; } }\n")

        x.Build "server-inproc" (settings InProcess "Server.cs" "Server.dll")
        Assert.That(File.Exists "Server.dll", Is.True, "csc did not produce Server.dll")
        File.Copy ("Server.dll", "Server.inproc.dll", true)
        File.Delete "Server.dll"

        x.Build "server-shared" (settings (Shared (Some 120)) "Server.cs" "Server.dll")
        Assert.That(File.Exists "Server.dll", Is.True, "csc did not produce Server.dll through the server")

        let diffs = Verify.compare "Server.inproc.dll" "Server.dll"
        Assert.That(diffs, Is.Empty, sprintf "the two compiles differ: %s" (Verify.verdict diffs))

        // the switch went to the compiler in one run and not the other (the command line is
        // traced at Debug)
        let inprocLog = File.ReadAllText "server-inproc.log"
        let sharedLog = File.ReadAllText "server-shared.log"
        Assert.That(sharedLog, Does.Contain "/shared", "the shared run did not pass /shared")
        Assert.That(sharedLog, Does.Contain "/keepalive:120")
        Assert.That(inprocLog, Does.Not.Contain "/shared", "the in-process run passed /shared")

        // and a server is now running from the compiler's own directory (Unix: `ps`; Windows
        // has no cheap way to read another process's command line from here)
        if Env.isUnix then
            let fwk = DotNetFwk.locateFramework (Some "net-4.6.2")
            let bincore = Path.GetDirectoryName fwk.CscTool
            let psi = System.Diagnostics.ProcessStartInfo ("ps", "-axo command", RedirectStandardOutput = true, UseShellExecute = false)
            use ps = System.Diagnostics.Process.Start psi
            let commands = ps.StandardOutput.ReadToEnd()
            ps.WaitForExit()
            Assert.That(commands, Does.Contain (bincore </> "VBCSCompiler"),
                sprintf "no VBCSCompiler from '%s' is running after a /shared compile" bincore)

    /// `/shared` is a parameter of the run, not of the compilation: the entry `resolve`
    /// composes -- what a lock records -- is the same whatever `Server` says.
    [<Test; Category("Integration")>]
    member x.``the lock's arguments never carry /shared``() =

        File.WriteAllText ("ServerLock.cs", "public class ServerLock {}\n")
        let mutable entries : Lock.Entry list = []

        do xake {x.TestOptions with FileLog = "server-lock.log"; ThrowOnError = true} {
            wantOverride (["server-lock"])
            rules [
                "server-lock" => recipe {
                    let! shared = CscLock.resolve (settings (Shared (Some 60)) "ServerLock.cs" "ServerLock.dll")
                    let! inproc = CscLock.resolve (settings InProcess "ServerLock.cs" "ServerLock.dll")
                    entries <- [shared; inproc]
                }
            ]
        }

        let shared, inproc = entries.[0], entries.[1]
        Assert.That(shared.Args |> List.exists (fun a -> a.StartsWith "/shared" || a.StartsWith "/keepalive"), Is.False)
        Assert.That(shared, Is.EqualTo inproc)

    /// Not an assertion, a measurement: N compiles of the same one-file library through the
    /// server and in-process, wall-clock per compile, written to the test output.
    [<Test; Category("Integration")>]
    member x.``timing: N compiles with and without the server``() =

        File.WriteAllText ("Timing.cs",
            "using System; using System.Linq; using System.Collections.Generic;\n" +
            "public class Timing { public static string Run() { return string.Join(\",\", Enumerable.Range(0, 100).Select(i => i * i)); } }\n")

        let n = 5
        let time (server: CompilerServer) (label: string) =
            [ for i in 1 .. n do
                let sw = System.Diagnostics.Stopwatch.StartNew()
                x.Build (sprintf "%s-%d" label i) (settings server "Timing.cs" "Timing.dll")
                yield sw.Elapsed.TotalSeconds ]

        // the first shared compile may start the server; it is listed, not excluded
        let shared = time (Shared (Some 120)) "timing-shared"
        let inproc = time InProcess "timing-inproc"
        let fmt (xs: float list) = xs |> List.map (sprintf "%.2f") |> String.concat " "
        TestContext.WriteLine (sprintf "csc timing, %d compiles each (seconds per compile, whole recipe):" n)
        TestContext.WriteLine (sprintf "  in-process: %s  (avg %.2f)" (fmt inproc) (List.average inproc))
        TestContext.WriteLine (sprintf "  /shared:    %s  (avg %.2f, avg of runs 2..%d %.2f)" (fmt shared) (List.average shared) n (List.average (List.tail shared)))
        Assert.That(File.Exists "Timing.dll", Is.True)
