namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// A compiler that dies without a diagnostic (csc's `Environment.FailFast`, exit 134) is
/// reported with its exit code and what it printed, and the response file is kept.
[<TestFixture>]
type ``Compiler crash``() =
    inherit XakeTestBase("compiler-crash")

    [<Test>]
    member x.``a compiler exiting without an error line is reported with its output``() =
        if Env.isWindows then Assert.Ignore "a shell script stands in for the compiler"
        File.WriteAllText ("a.cs", "class A {}\n")
        File.WriteAllText ("fake-csc.sh", "#!/bin/sh\necho 'Unhandled exception. System.InvalidOperationException: boom' >&2\nexit 134\n")
        (System.Diagnostics.Process.Start ("chmod", "+x fake-csc.sh")).WaitForExit ()
        let fake = Path.GetFullPath "fake-csc.sh"
        let failed =
            try
                xake {x.TestOptions with FileLog = "crash.log"; FileLogLevel = Verbosity.Diag; ThrowOnError = true} {
                    want [ "a.dll" ]
                    rules [ "a.dll" ..> csc { targetfwk "netstandard2.0"; src (!! "a.cs"); cscpath fake } ]
                }
                false
            with _ -> true
        Assert.That(failed, Is.True)
        let log = File.ReadAllText "crash.log"
        Assert.That(log, Does.Contain "exited with code 134 and reported no error")
        Assert.That(log, Does.Contain "Environment.FailFast")
        Assert.That(log, Does.Contain "boom")
        Assert.That(log, Does.Contain "response file kept")
