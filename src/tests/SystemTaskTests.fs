module ``SystemTasksTests``

open NUnit.Framework

open Xake
open Xake.Tasks

type private File = System.IO.File

let TestOptions = {ExecOptions.Default with Threads = 1; Targets = ["main"]; ConLogLevel = Diag; FileLogLevel = Silent; ResetDb = true}


// TODO make correct test
[<Test; Platform("Win")>]
let ``shell``() =
    do xake TestOptions {
        rules [
            "main" => recipe {

                do! Shell {
                    ShellOptions.Default with
                        Command = "dir"; Args = ["*.*"]
                        WorkingDir = Some "."; UseClr = true; FailOnErrorLevel = true} |> Recipe.Ignore
                
                let! error = shell {
                    cmd "dir"
                    args ["*.*"]
                    failonerror
                    workdir "."
                    }
                Assert.AreEqual (0, error)
            }
        ]
    }

    File.Exists "samplefile" |> Assert.False


let TestOptionsStrict =
    {ExecOptions.Default with
        Threads = 1; Targets = ["main"]
        ConLogLevel = Diag; FileLogLevel = Silent
        ThrowOnError = true; FileLog = ""
        ResetDb = true}

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``sh runs echo command successfully on Unix``() =
    do xake TestOptionsStrict {
        rules [
            "main" => recipe {
                do! sh "echo" { arg "hello" }
            }
        ]
    }

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``sh fails by default on non-zero exit code on Unix``() =
    let mutable exceptionThrown = false
    do xake TestOptionsStrict {
        rules [
            "main" => (WhenError (fun _ -> exceptionThrown <- true) <| recipe {
                do! sh "false" {}
            })
        ]
    }
    Assert.IsTrue exceptionThrown

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``stdout handler on shell builder captures output lines``() =
    let captured = System.Collections.Generic.List<string>()
    do xake TestOptionsStrict {
        rules [
            "main" => recipe {
                let! _ = shellCmd "" {
                    cmd "echo"
                    arg "hello"
                    stdout captured.Add
                }
                ()
            }
        ]
    }
    Assert.AreEqual(1, captured.Count)
    Assert.AreEqual("hello", captured.[0])

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``stderr handler on shell builder captures stderr lines``() =
    let captured = System.Collections.Generic.List<string>()
    do xake TestOptionsStrict {
        rules [
            "main" => recipe {
                let! _ = shellCmd "" {
                    cmd "/bin/sh"
                    arg "-c"
                    arg "\"echo error >&2\""
                    stderr captured.Add
                }
                ()
            }
        ]
    }
    Assert.AreEqual(1, captured.Count)
    Assert.AreEqual("error", captured.[0])

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``shell builder stdout handler captures output and returns exit code``() =
    let captured = System.Collections.Generic.List<string>()
    do xake TestOptionsStrict {
        rules [
            "main" => recipe {
                let! exitCode = shellCmd "" {
                    cmd "echo"
                    arg "world"
                    stdout captured.Add
                }
                Assert.AreEqual(0, exitCode)
            }
        ]
    }
    Assert.AreEqual(1, captured.Count)
    Assert.AreEqual("world", captured.[0])

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``stdout and stderr handlers can be combined on shell builder``() =
    let outLines = System.Collections.Generic.List<string>()
    let errLines = System.Collections.Generic.List<string>()
    do xake TestOptionsStrict {
        rules [
            "main" => recipe {
                let! _ = shellCmd "" {
                    cmd "/bin/sh"
                    arg "-c"
                    arg "\"echo out; echo err >&2\""
                    stdout outLines.Add
                    stderr errLines.Add
                }
                ()
            }
        ]
    }
    Assert.AreEqual(1, outLines.Count)
    Assert.AreEqual("out", outLines.[0])
    Assert.AreEqual(1, errLines.Count)
    Assert.AreEqual("err", errLines.[0])
