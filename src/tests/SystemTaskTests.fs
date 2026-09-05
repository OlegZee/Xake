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
                do! sh "false" { () }
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

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``result keyword returns exit code`` () =
    do xake TestOptionsStrict {
        rules [
            "main" => recipe {
                let! code = sh "echo" { arg "hello"; result }
                Assert.AreEqual(0, code)
            }
        ]
    }

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``output keyword captures stdout lines`` () =
    do xake TestOptionsStrict {
        rules [
            "main" => recipe {
                let! lines = sh "echo" { arg "hello"; output }
                Assert.AreEqual(1, List.length lines)
                Assert.AreEqual("hello", lines.[0])
            }
        ]
    }

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``resultAndOutput keyword captures both exit code and stdout`` () =
    do xake TestOptionsStrict {
        rules [
            "main" => recipe {
                let! code, lines = sh "echo" { arg "hello"; resultAndOutput }
                Assert.AreEqual(0, code)
                Assert.AreEqual(["hello"], lines)
            }
        ]
    }

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``result and output keywords compose`` () =
    do xake TestOptionsStrict {
        rules [
            "main" => recipe {
                let! code, lines = sh "echo" { arg "hello"; result; output }
                Assert.AreEqual(0, code)
                Assert.AreEqual(["hello"], lines)
            }
        ]
    }

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``stdout handler and output keyword both fire`` () =
    let sideEffect = System.Collections.Generic.List<string>()
    do xake TestOptionsStrict {
        rules [
            "main" => recipe {
                let! lines = sh "echo" { arg "hello"; stdout sideEffect.Add; output }
                Assert.AreEqual(["hello"], lines)
                Assert.AreEqual(1, sideEffect.Count)
            }
        ]
    }

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``captureOutput ToFile writes stdout to file`` () =
    let tmpFile = System.IO.Path.GetTempFileName()
    try
        do xake TestOptionsStrict {
            rules [
                "main" => recipe {
                    do! sh "echo" {
                        arg "hello"
                        captureOutput (ToFile (Stdout, tmpFile))
                    }
                }
            ]
        }
        let lines = File.ReadAllLines tmpFile
        Assert.AreEqual(1, lines.Length)
        Assert.AreEqual("hello", lines.[0])
    finally
        File.Delete tmpFile

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``captureOutput ToFile writes stderr to file`` () =
    let tmpFile = System.IO.Path.GetTempFileName()
    try
        do xake TestOptionsStrict {
            rules [
                "main" => recipe {
                    let! _ = shellCmd "" {
                        cmd "/bin/sh"
                        arg "-c"
                        arg "\"echo errline >&2\""
                        captureOutput (ToFile (Stderr, tmpFile))
                    }
                    ()
                }
            ]
        }
        let lines = File.ReadAllLines tmpFile
        Assert.AreEqual(1, lines.Length)
        Assert.AreEqual("errline", lines.[0])
    finally
        File.Delete tmpFile

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``captureOutput ToFileAppend accumulates across invocations`` () =
    let tmpFile = System.IO.Path.GetTempFileName()
    try
        do xake TestOptionsStrict {
            rules [
                "main" => recipe {
                    do! sh "echo" {
                        arg "first"
                        captureOutput (ToFileAppend (Stdout, tmpFile))
                    }
                    do! sh "echo" {
                        arg "second"
                        captureOutput (ToFileAppend (Stdout, tmpFile))
                    }
                }
            ]
        }
        let lines = File.ReadAllLines tmpFile
        Assert.AreEqual(2, lines.Length)
        Assert.AreEqual("first", lines.[0])
        Assert.AreEqual("second", lines.[1])
    finally
        File.Delete tmpFile

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``captureOutput ToFile with Both captures stdout and stderr`` () =
    let tmpFile = System.IO.Path.GetTempFileName()
    try
        do xake TestOptionsStrict {
            rules [
                "main" => recipe {
                    let! _ = shellCmd "" {
                        cmd "/bin/sh"
                        arg "-c"
                        arg "\"echo out; echo err >&2\""
                        captureOutput (ToFile (Both, tmpFile))
                    }
                    ()
                }
            ]
        }
        let lines = File.ReadAllLines tmpFile |> Array.sort
        Assert.AreEqual(2, lines.Length)
        CollectionAssert.AreEquivalent([|"err"; "out"|], lines)
    finally
        File.Delete tmpFile

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``captureOutput ToHandler receives stdout lines`` () =
    let captured = System.Collections.Generic.List<string>()
    do xake TestOptionsStrict {
        rules [
            "main" => recipe {
                do! sh "echo" {
                    arg "hello"
                    captureOutput (ToHandler (Stdout, captured.Add))
                }
            }
        ]
    }
    Assert.AreEqual(1, captured.Count)
    Assert.AreEqual("hello", captured.[0])

[<Test; Platform("Unix,MacOsX,Linux")>]
let ``captureOutput ToLog suppresses default log for that stream`` () =
    do xake TestOptionsStrict {
        rules [
            "main" => recipe {
                do! sh "echo" {
                    arg "hello"
                    captureOutput (ToLog (Stdout, fun _ -> Warning))
                }
            }
        ]
    }
