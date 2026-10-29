module ``Command line interface``

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks

let currentDir = __SOURCE_DIRECTORY__
let XakeOptions = ExecOptions.Default

/// Parses CLI args on top of initial ExecOptions, returning the merged ExecOptions.
let private parseArgs args initial =
    args |> List.fold ParseArgs.foldFunction (initial, ParseArgs.TopLevel) |> fst

let private withTempRoot testName action =
    let testRoot = currentDir </> "~testout~" </> (testName + "-" + System.Guid.NewGuid().ToString "N")
    Directory.CreateDirectory testRoot |> ignore
    try
        action testRoot
    finally
        try
            Directory.Delete(testRoot, true)
        with _ -> ()

[<Test>]
let ``accepts various switches``() =

    let engineOptions = ref EngineOptions.Default
    let args =
        ["/t"; "33"; "/R"; currentDir; "/LL"; "Loud";
        "/FL"; "aaaaa"; "/D"; "AA=BBB"; "/D"; "AA1=CCC"; "/FLL"; "Silent"]

    do xakeArgs args {XakeOptions with DbFileName = ".xake-vari"} {
        want (["test"])

        rules [
            "test" => recipe {
                let! opts = getCtxOptions()
                engineOptions := opts
            }
        ]
    }

    // Engine-level assertions
    let eo = !engineOptions
    Assert.AreEqual(33, eo.Threads)
    Assert.AreEqual(currentDir, eo.ProjectRoot)

    // CLI-level assertions: verify parsing directly
    let parsed = parseArgs args {XakeOptions with DbFileName = ".xake-vari"}
    Assert.AreEqual([("AA1", "CCC"); ("AA", "BBB")], parsed.CliVars)   // last -d first
    Assert.AreEqual("aaaaa", parsed.FileLog)
    Assert.AreEqual(Verbosity.Silent, parsed.FileLogLevel)
    Assert.AreEqual(Verbosity.Loud, parsed.ConLogLevel)

[<Test>]
let ``reads target lists``() =

    let engineOptions = ref EngineOptions.Default
    let executed2 = ref false
    let args = ["/t"; "31"; "target1"; "target2"]

    do xakeArgs args XakeOptions {

        rules [
            "target1" => recipe {
                let! opts = getCtxOptions()
                engineOptions := opts
            }
            "target2" => recipe {
                executed2 := true
            }
        ]
    }

    let eo = !engineOptions
    Assert.AreEqual(31, eo.Threads)
    Assert.IsTrue !executed2

    // Targets are a CLI-level concern; verify via parseArgs
    let parsed = parseArgs args XakeOptions
    Assert.AreEqual(["target1"; "target2"], parsed.Targets)

[<Test>]
let ``preserves target name case``() =

    let engineOptions = ref EngineOptions.Default
    let executed = ref false
    let args = ["MyTarget"]

    do xakeArgs args XakeOptions {
        rules [
            "MyTarget" => recipe {
                let! opts = getCtxOptions()
                engineOptions := opts
                executed := true
            }
        ]
    }

    let eo = !engineOptions
    Assert.IsTrue(!executed, "Rule with mixed-case name should be executed")

    // Targets are a CLI-level concern; verify via parseArgs
    let parsed = parseArgs args XakeOptions
    Assert.AreEqual(["MyTarget"], parsed.Targets)


[<Test; Ignore("")>]
let ``warns on incorrect switch``() =

    do xakeArgs ["/xxx"] XakeOptions {
        want ["ss"]
    }

    //raise <| new System.NotImplementedException()

[<Test>]
let ``supports ignoring command line``() =

    Directory.CreateDirectory "~testout~" |> ignore
    let engineOptions = ref EngineOptions.Default
    let args =
        ["/t"; "33"; "/R"; currentDir; "/LL"; "Loud";
        "/FL"; "aaaaa"; "target"]

    do xakeArgs args {XakeOptions with IgnoreCommandLine = true; Threads = 2; FileLog = "~testout~" </> "ss"; Targets = ["main"]} {
        rules [
            "main" => recipe {
                let! opts = getCtxOptions()
                engineOptions := opts
            }
        ]
    }

    let eo = !engineOptions
    Assert.AreEqual(2, eo.Threads)

[<Test>]
let ``resetdb ignores previously recorded information``() =

    let testRoot = currentDir </> "~testout~" </> ("resetdb-" + System.Guid.NewGuid().ToString("N"))
    let dbFile = ".xake-resetdb-test"
    let runCount = ref 0

    Directory.CreateDirectory(testRoot) |> ignore
    File.WriteAllText(testRoot </> "input.txt", "seed")

    let runBuild args =
        xakeArgs args { XakeOptions with ProjectRoot = testRoot; DbFileName = dbFile } {
            rules [
                "out.txt" ..> action {
                    do! need ["input.txt"]
                    runCount := !runCount + 1
                    do! writeText "result"
                }
            ]
        }

    try
        runBuild ["out.txt"]
        Assert.AreEqual(1, !runCount, "First build should execute target")

        runBuild ["out.txt"]
        Assert.AreEqual(1, !runCount, "Second build should reuse recorded database state")

        runBuild ["--resetdb"; "out.txt"]
        Assert.AreEqual(2, !runCount, "--resetdb should force target execution")
    finally
        try
            Directory.Delete(testRoot, true)
        with _ -> ()

/// The file logger writes asynchronously: waits until the log satisfies the predicate.
let private readLogEventually (path: string) (ready: string -> bool) =
    let read () = try File.ReadAllText path with _ -> ""
    let deadline = System.DateTime.Now.AddSeconds 5.0
    while not (ready (read ())) && System.DateTime.Now < deadline do System.Threading.Thread.Sleep 50
    read ()

[<Test>]
let ``runs teardown after successful CLI execution``() =
    withTempRoot "cli-teardown-success" <| fun testRoot ->
        let log = System.Collections.Generic.List<string> ()

        xakeArgs ["build"] { XakeOptions with ProjectRoot = testRoot; DbFileName = ".xake-teardown-success"; NoPersist = true; Nologo = true } {
            teardown ["cleanup"]
            rules [
                "build" => recipe {
                    lock log <| fun () -> log.Add "build"
                }
                "cleanup" => recipe {
                    lock log <| fun () -> log.Add "cleanup"
                }
            ]
        }

        Assert.AreEqual(["build"; "cleanup"], log |> Seq.toList)

[<Test>]
let ``runs teardown after failed CLI execution when ThrowOnError is enabled``() =
    withTempRoot "cli-teardown-failure" <| fun testRoot ->
        let log = System.Collections.Generic.List<string> ()

        Assert.Throws<XakeException>(fun () ->
            xakeArgs ["build"] { XakeOptions with ProjectRoot = testRoot; DbFileName = ".xake-teardown-failure"; NoPersist = true; Nologo = true; ThrowOnError = true } {
                teardown ["cleanup"]
                rules [
                    "build" => recipe {
                        lock log <| fun () -> log.Add "build"
                        failwith "boom"
                    }
                    "cleanup" => recipe {
                        lock log <| fun () -> log.Add "cleanup"
                    }
                ]
            }) |> ignore

        Assert.AreEqual(["build"; "cleanup"], log |> Seq.toList)

[<Test>]
let ``skips teardown for dryrun and dump CLI modes``() =
    withTempRoot "cli-teardown-nonrun" <| fun testRoot ->
        let log = System.Collections.Generic.List<string> ()
        let options = { XakeOptions with ProjectRoot = testRoot; DbFileName = ".xake-teardown-nonrun"; NoPersist = true; Nologo = true }

        xakeArgs ["--dryrun"; "build"] options {
            teardown ["cleanup"]
            rules [
                "build" => recipe {
                    lock log <| fun () -> log.Add "build"
                }
                "cleanup" => recipe {
                    lock log <| fun () -> log.Add "cleanup"
                }
            ]
        }

        xakeArgs ["--dump"; "build"] options {
            teardown ["cleanup"]
            rules [
                "build" => recipe {
                    lock log <| fun () -> log.Add "build"
                }
                "cleanup" => recipe {
                    lock log <| fun () -> log.Add "cleanup"
                }
            ]
        }

        Assert.IsEmpty(log)

[<Test>]
let ``teardown failure is reported when build succeeds``() =
    withTempRoot "cli-teardown-fail-after-success" <| fun testRoot ->
        let log = System.Collections.Generic.List<string> ()

        let exn =
            Assert.Throws<XakeException>(fun () ->
                xakeArgs ["build"] { XakeOptions with ProjectRoot = testRoot; NoPersist = true; Nologo = true; ThrowOnError = true } {
                    teardown ["cleanup"]
                    rules [
                        "build" => recipe {
                            lock log <| fun () -> log.Add "build"
                        }
                        "cleanup" => recipe {
                            lock log <| fun () -> log.Add "cleanup"
                            failwith "teardown boom"
                        }
                    ]
                })

        Assert.AreEqual(["build"; "cleanup"], log |> Seq.toList)
        Assert.That(exn.Message, Does.Contain("Teardown failure"))

[<Test>]
let ``original build error is preserved when teardown also fails``() =
    withTempRoot "cli-teardown-both-fail" <| fun testRoot ->
        let log = System.Collections.Generic.List<string> ()

        let exn =
            Assert.Throws<XakeException>(fun () ->
                xakeArgs ["build"] { XakeOptions with ProjectRoot = testRoot; NoPersist = true; Nologo = true; ThrowOnError = true } {
                    teardown ["cleanup"]
                    rules [
                        "build" => recipe {
                            lock log <| fun () -> log.Add "build"
                            failwith "build boom"
                        }
                        "cleanup" => recipe {
                            lock log <| fun () -> log.Add "cleanup"
                            failwith "teardown boom"
                        }
                    ]
                })

        Assert.AreEqual(["build"; "cleanup"], log |> Seq.toList)
        Assert.That(exn.Message, Does.Contain "build boom")

// --- setup ---

[<Test>]
let ``runs setup targets before the first target, in order``() =
    withTempRoot "cli-setup-order" <| fun testRoot ->
        let log = System.Collections.Generic.List<string> ()
        let add s = recipe { lock log <| fun () -> log.Add s }

        xakeArgs ["build"] { XakeOptions with ProjectRoot = testRoot; NoPersist = true; Nologo = true; FileLog = "" } {
            setup ["first"; "second"]
            rules [
                "first" => add "first"
                "second" => add "second"
                "build" => add "build"
            ]
        }

        Assert.AreEqual(["first"; "second"; "build"], log |> Seq.toList)

[<Test>]
let ``several setup ops accumulate while teardown replaces``() =
    withTempRoot "cli-setup-accumulate" <| fun testRoot ->
        let log = System.Collections.Generic.List<string> ()
        let add s = recipe { lock log <| fun () -> log.Add s }

        xakeArgs ["build"] { XakeOptions with ProjectRoot = testRoot; NoPersist = true; Nologo = true; FileLog = "" } {
            setup ["a"]
            setup ["b"]
            teardown ["t1"]
            teardown ["t2"]
            rules [
                "a" => add "a"
                "b" => add "b"
                "t1" => add "t1"
                "t2" => add "t2"
                "build" => add "build"
            ]
        }

        Assert.AreEqual(["a"; "b"; "build"; "t2"], log |> Seq.toList)

[<Test>]
let ``setup failure skips the build, runs teardown and fails the run``() =
    withTempRoot "cli-setup-failure" <| fun testRoot ->
        let log = System.Collections.Generic.List<string> ()
        let add s = recipe { lock log <| fun () -> log.Add s }

        let exn =
            Assert.Throws<XakeException>(fun () ->
                xakeArgs ["build"] { XakeOptions with ProjectRoot = testRoot; NoPersist = true; Nologo = true; FileLog = ""; ThrowOnError = true } {
                    setup ["login"; "second"]
                    teardown ["cleanup"]
                    rules [
                        "login" => recipe {
                            lock log <| fun () -> log.Add "login"
                            failwith "setup boom"
                        }
                        "second" => add "second"
                        "cleanup" => add "cleanup"
                        "build" => add "build"
                    ]
                })

        Assert.AreEqual(["login"; "cleanup"], log |> Seq.toList)
        Assert.That(exn.Message, Does.Contain "setup boom")

[<Test>]
let ``setup is not run for dryrun and dump but dryrun lists it``() =
    withTempRoot "cli-setup-nonrun" <| fun testRoot ->
        let log = System.Collections.Generic.List<string> ()
        let logFile = testRoot </> "build.log"
        let options = { XakeOptions with ProjectRoot = testRoot; NoPersist = true; Nologo = true; FileLog = logFile; FileLogLevel = Chatty; ConLogLevel = Silent }
        let script args (options: ExecOptions) =
            xakeArgs args options {
                setup ["prepare"]
                rules [
                    "build" => recipe { lock log <| fun () -> log.Add "build" }
                    "prepare" => recipe { lock log <| fun () -> log.Add "prepare" }
                ]
            }

        script ["--dump"; "build"] { options with FileLog = testRoot </> "dump.log" }
        script ["--dryrun"; "build"] options

        Assert.AreEqual(0, log.Count)
        let text = readLogEventually logFile (fun t -> t.Contains "Setup targets (not run in dry mode)")
        Assert.That(text, Does.Contain "Setup targets (not run in dry mode)")

// --- run banner ---

[<Test>]
let ``banner is logged at Info with the effective settings``() =
    withTempRoot "cli-banner" <| fun testRoot ->
        let logFile = testRoot </> "build.log"
        xakeArgs ["-d"; "FLAVOR=blue"; "build"] { XakeOptions with ProjectRoot = testRoot; NoPersist = true; Nologo = true; FileLog = logFile; FileLogLevel = Chatty; ConLogLevel = Silent; Threads = 3 } {
            var "SCRIPT_VAR" "yes"
            setup ["prepare"]
            teardown ["cleanup"]
            rules [
                "prepare" => recipe { return () }
                "cleanup" => recipe { return () }
                "build" => recipe { return () }
            ]
        }

        let text = readLogEventually logFile (fun t -> t.Contains "SCRIPT_VAR=")
        for expected in [ "[INF]"; "Xake "; "mode:         run"; "targets:      build"; "project root: " + testRoot
                          "database:     (no persist)"; "threads:      3"; "rule match:   MostSpecific"
                          "console log:  Silent"; "(Chatty)"; "setup:        prepare"; "teardown:     cleanup"
                          "FLAVOR=blue (cli)"; "SCRIPT_VAR=yes (script)" ] do
            Assert.That(text, Does.Contain expected)

[<Test>]
let ``banner defaults to main and is not shown at the Normal console level``() =
    withTempRoot "cli-banner-console" <| fun testRoot ->
        let logged = System.Collections.Generic.List<string> ()
        let logger = CustomLogger (fun _ -> true) (fun s -> lock logged (fun () -> logged.Add s))
        xakeArgs [] { XakeOptions with ProjectRoot = testRoot; NoPersist = true; Nologo = true; FileLog = ""; ConLogLevel = Normal; CustomLogger = logger } {
            rules [ "main" => recipe { return () } ]
        }
        let all = lock logged (fun () -> logged |> String.concat "\n")
        Assert.That(all, Does.Contain "targets:      main (default)")
        Assert.That(all, Does.Contain "setup:        (none)")

[<Test>]
let ``banner masks the values of secret variables``() =
    withTempRoot "cli-banner-secret" <| fun testRoot ->
        let logFile = testRoot </> "build.log"
        let schema = {| licenseKey = Var.string(cliArg = "licenseKey", envVar = "BANNER_LICENSE_KEY") |> secret |}
        System.Environment.SetEnvironmentVariable("BANNER_LICENSE_KEY", "banner-secret-value-42")
        try
            xakeArgs ["-d"; "PLAIN=visible"; "main"] { XakeOptions with ProjectRoot = testRoot; NoPersist = true; Nologo = true; FileLog = logFile; FileLogLevel = Chatty; ConLogLevel = Silent } {
                varschema schema
                rules [ "main" => recipe { return () } ]
            }
        finally
            System.Environment.SetEnvironmentVariable("BANNER_LICENSE_KEY", null)

        let text = readLogEventually logFile (fun t -> t.Contains "PLAIN=")
        Assert.That(text, Does.Contain "licenseKey=*** (env:BANNER_LICENSE_KEY)")
        Assert.That(text, Does.Contain "PLAIN=visible (cli)")
        Assert.That(text, Does.Not.Contain "banner-secret-value-42")

// --- the command line wins over the script's ops ---

/// The script options after the command line is applied (what Run gets).
let private scriptOptions (XakeScript (options, _)) = options
let private quiet = { XakeOptions with Nologo = true; NoPersist = true; FileLog = ""; ConLogLevel = Silent; ThrowOnError = true }

[<Test>]
let ``-d wins over the var op``() =
    let seen = ref None
    do xakeArgs ["-d"; "X=cli"] quiet {
        var "X" "script"
        phony "main" (recipe { let! v = getVar "X" in seen := v })
    }
    Assert.AreEqual(Some "cli", !seen)

[<Test>]
let ``var op without -d gives the script value``() =
    let seen = ref None
    do xakeArgs ["-d"; "Y=other"] quiet {
        var "X" "script"
        phony "main" (recipe { let! v = getVar "X" in seen := v })
    }
    Assert.AreEqual(Some "script", !seen)

[<Test>]
let ``-d wins over ExecOptions Vars``() =
    let seen = ref None
    do xakeArgs ["-d"; "X=cli"] { quiet with Vars = ["X", "options"] } {
        phony "main" (recipe { let! v = getVar "X" in seen := v })
    }
    Assert.AreEqual(Some "cli", !seen)

[<Test>]
let ``a var op and ExecOptions Vars of one name fail the launch``() =   // spec 8.1-4
    let ex =
        Assert.Throws<XakeException>(fun () ->
            xakeArgs [] { quiet with Vars = ["X", "options"] } {
                var "X" "script"
                phony "main" (recipe { () })
            })
    StringAssert.Contains("Variable 'X' is defined twice in the script: by ExecOptions.Vars and by var \"X\" \"...\"; define it once", ex.Data0)

[<Test>]
let ``last -d of a duplicated name wins``() =
    let seen = ref None
    do xakeArgs ["-d"; "X=first"; "-d"; "X=second"] quiet {
        var "X" "script"
        phony "main" (recipe { let! v = getVar "X" in seen := v })
    }
    Assert.AreEqual(Some "second", !seen)

[<Test>]
let ``IgnoreCommandLine keeps the var op value``() =
    let seen = ref None
    do xakeArgs ["-d"; "X=cli"] { quiet with IgnoreCommandLine = true } {
        var "X" "script"
        phony "main" (recipe { let! v = getVar "X" in seen := v })
    }
    Assert.AreEqual(Some "script", !seen)

[<Test>]
let ``-ll wins over consolelog``() =
    let b = xakeArgs ["-ll"; "Diag"] quiet
    Assert.AreEqual(Verbosity.Diag, (b.ConLog(b.Zero(), Loud) |> b.ApplyCommandLine |> scriptOptions).ConLogLevel)
    let b2 = xakeArgs [] quiet
    Assert.AreEqual(Verbosity.Loud, (b2.ConLog(b2.Zero(), Loud) |> b2.ApplyCommandLine |> scriptOptions).ConLogLevel)

[<Test>]
let ``-fl wins over the filelog path, the level stays the script's``() =
    let b = xakeArgs ["-fl"; "b.log"] quiet
    let o = b.FileLog(b.Zero(), "a.log", Chatty) |> b.ApplyCommandLine |> scriptOptions
    Assert.AreEqual("b.log", o.FileLog)
    Assert.AreEqual(Verbosity.Chatty, o.FileLogLevel)

[<Test>]
let ``-fll wins over the filelog level, the path stays the script's``() =
    let b = xakeArgs ["-fll"; "Diag"] quiet
    let o = b.FileLog(b.Zero(), "a.log", Chatty) |> b.ApplyCommandLine |> scriptOptions
    Assert.AreEqual("a.log", o.FileLog)
    Assert.AreEqual(Verbosity.Diag, o.FileLogLevel)

[<Test>]
let ``-r wins over rootdir``() =
    let b = xakeArgs ["-r"; "y"] quiet
    Assert.AreEqual("y", (b.RootDir(b.Zero(), "x") |> b.ApplyCommandLine |> scriptOptions).ProjectRoot)
    let b2 = xakeArgs [] quiet
    Assert.AreEqual("x", (b2.RootDir(b2.Zero(), "x") |> b2.ApplyCommandLine |> scriptOptions).ProjectRoot)

[<Test>]
let ``an invalid -ll value does not block consolelog``() =
    let b = xakeArgs ["-ll"; "Shouty"] quiet
    Assert.AreEqual(Verbosity.Loud, (b.ConLog(b.Zero(), Loud) |> b.ApplyCommandLine |> scriptOptions).ConLogLevel)

[<Test>]
let ``IgnoreCommandLine lets the ops apply``() =
    let b = xakeArgs ["-ll"; "Diag"; "-r"; "y"; "-fl"; "b.log"] { quiet with IgnoreCommandLine = true }
    let o = b.RootDir(b.FileLog(b.ConLog(b.Zero(), Loud), "a.log", Chatty), "x") |> b.ApplyCommandLine |> scriptOptions
    Assert.AreEqual(Verbosity.Loud, o.ConLogLevel)
    Assert.AreEqual("a.log", o.FileLog)
    Assert.AreEqual("x", o.ProjectRoot)

[<Test>]
let ``want yields to the CLI targets``() =
    let b = xakeArgs ["cliTarget"] quiet
    Assert.AreEqual(["cliTarget"], (b.Want(b.Zero(), ["scriptTarget"]) |> b.ApplyCommandLine |> scriptOptions).Targets)
    let b2 = xakeArgs [] quiet
    Assert.AreEqual(["scriptTarget"], (b2.Want(b2.Zero(), ["scriptTarget"]) |> b2.ApplyCommandLine |> scriptOptions).Targets)

[<Test>]
let ``engine per-demand vars win over the script's vars``() =   // spec 8.7-39
    let seen = ref (None, None)
    let eng =
        xakeEngine {
            var "V" "script"
            phony "t" (recipe {
                let! v = getVar "V"
                let! w = getVar "W"
                seen := (v, w)
            })
            start
        }
    try eng.Demand("t", ["V", "demand"; "W", "demand"]).Wait()
    finally eng.StopAsync().Wait()
    Assert.AreEqual((Some "demand", Some "demand"), !seen)

// --- mixed: the CLI wins per name / per field, not all-or-nothing ---

[<Test>]
let ``vars mix per name: -d where given, var where not``() =
    let seen = ref []
    do xakeArgs ["-d"; "A=cli"; "-d"; "C=cli"] quiet {
        var "A" "script"
        var "B" "script"
        phony "main" (recipe {
            let! a = getVar "A"
            let! b = getVar "B"
            let! c = getVar "C"
            seen := [a; b; c]
        })
    }
    Assert.AreEqual([Some "cli"; Some "script"; Some "cli"], !seen)

[<Test>]
let ``vars mix per name with ExecOptions Vars``() =
    let seen = ref []
    do xakeArgs ["-d"; "A=cli"; "-d"; "C=cli"] { quiet with Vars = ["D", "options"] } {
        var "A" "script"
        var "B" "script"
        phony "main" (recipe {
            let! a = getVar "A"
            let! b = getVar "B"
            let! c = getVar "C"
            let! d = getVar "D"
            seen := [a; b; c; d]
        })
    }
    Assert.AreEqual([Some "cli"; Some "script"; Some "cli"; Some "options"], !seen)

let private applyLogAndRoot (b: RulesBuilder) =
    b.RootDir(b.FileLog(b.ConLog(b.Zero(), Loud), "a.log", Chatty), "x") |> b.ApplyCommandLine |> scriptOptions

[<Test>]
let ``options mix per field: only -ll given``() =
    let o = xakeArgs ["-ll"; "Diag"] quiet |> applyLogAndRoot
    Assert.AreEqual(Verbosity.Diag, o.ConLogLevel)
    Assert.AreEqual("a.log", o.FileLog)
    Assert.AreEqual(Verbosity.Chatty, o.FileLogLevel)
    Assert.AreEqual("x", o.ProjectRoot)

[<Test>]
let ``options mix per field: only -fl and -r given``() =
    let o = xakeArgs ["-fl"; "b.log"; "-r"; "y"] quiet |> applyLogAndRoot
    Assert.AreEqual(Verbosity.Loud, o.ConLogLevel)
    Assert.AreEqual("b.log", o.FileLog)
    Assert.AreEqual(Verbosity.Chatty, o.FileLogLevel)
    Assert.AreEqual("y", o.ProjectRoot)

[<Test>]
let ``end to end: -d and var read together, CLI log level, script file log path``() =
    withTempRoot "cli-mixed-e2e" <| fun testRoot ->
        let logFile = testRoot </> "script.log"
        let seen = ref []
        do xakeArgs ["-d"; "A=cli"; "-fll"; "Chatty"] { quiet with ProjectRoot = testRoot } {
            var "A" "script"
            var "B" "script"
            filelog logFile Loud    // at Loud the Info banner would not be written
            phony "main" (recipe {
                let! a = getVar "A"
                let! b = getVar "B"
                seen := [a; b]
            })
        }
        Assert.AreEqual([Some "cli"; Some "script"], !seen)
        // the run banner shows the effective file log: the script's path at the CLI's level
        let text = readLogEventually logFile (fun t -> t.Contains "file log:")
        Assert.That(text, Does.Contain (sprintf "%s (Chatty)" (Path.GetFullPath logFile)))
