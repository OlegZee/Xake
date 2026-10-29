namespace Tests

open System
open System.Collections.Generic
open NUnit.Framework

open Xake

/// The conformance tests of docs/features/vars-precedence/spec.md §8.1-8.7 and §8.10 (variable evaluation
/// at launch); each test is named by its spec number. Every test uses its own variable and
/// environment names: the secret registries and the environment are process-wide.
[<TestFixture>]
type ``Vars conformance (spec section 8)``() =
    inherit XakeTestBase("varsconf")

    let setEnv (name: string) (value: string) = Environment.SetEnvironmentVariable(name, value)

    let withEnv (pairs: (string * string) list) f =
        for (n, v) in pairs do setEnv n v
        try f () finally for (n, _) in pairs do setEnv n null

    /// the lines of the banner's vars section, without the log prefix
    let bannerVars (logged: List<string>) =
        lock logged (fun () -> List.ofSeq logged)
        |> List.filter (fun l -> l.StartsWith "[INF]     ")
        |> List.map (fun l -> l.Substring 10)

    let allText (logged: List<string>) = lock logged (fun () -> String.concat "\n" logged)

    /// the launch (or the build) fails: the XakeException's text
    let fails (f: unit -> unit) =
        let ex = Assert.Throws<XakeException>(fun () -> f ())
        ex.Data0

    /// the messages of an exception and of everything it wraps
    let rec messages (e: exn) : string list =
        [ match e with
          | XakeException text -> yield text
          | _ -> yield e.Message
          match e with
          | :? AggregateException as a -> for i in a.InnerExceptions do yield! messages i
          | _ -> if not (isNull e.InnerException) then yield! messages e.InnerException ]

    let demandFails (t: Threading.Tasks.Task) =
        let ex = Assert.Throws<AggregateException>(fun () -> t.Wait())
        messages ex |> String.concat "\n"

    let e2 name a b = sprintf "Variable '%s' is defined twice in the script: by %s and by %s; define it once" name a b

    /// options without a database, every log line captured
    member x.Opts (logged: List<string>) =
        { x.TestOptions with
            ThrowOnError = true; FileLog = ""; ConLogLevel = Silent; NoPersist = true; Nologo = true
            CustomLogger = CustomLogger (fun _ -> true) (fun s -> lock logged (fun () -> logged.Add s)) }

    /// options with a persistent database (deleted before each test)
    member x.PersistOpts (logged: List<string>) = { x.Opts logged with NoPersist = false; ResetDb = false }

    // --- 8.1 Chain, `var` lists, second definitions ---

    [<Test>]
    member x.``8.1-1 a var rule takes its first defined source``() =
        let run () =
            let logged = List<string>()
            let seen = ref None
            do xakeArgs [] (x.Opts logged) {
                var "VC01X" [Env "VC01_A"; Default "d"]
                phony "main" (recipe { let! v = getVar "VC01X" in seen.Value <- v })
            }
            seen.Value, bannerVars logged
        withEnv ["VC01_A", "a"] (fun () ->
            let v, banner = run ()
            Assert.AreEqual(Some "a", v)
            Assert.That(banner, Does.Contain "VC01X=a (env:VC01_A)"))
        let v, banner = run ()
        Assert.AreEqual(Some "d", v)
        Assert.That(banner, Does.Contain "VC01X=d (default)")
        // `A=` defines X as "": .NET removes a variable set to "" in-process, so checked only where it stays
        withEnv ["VC01_A", ""] (fun () ->
            if Environment.GetEnvironmentVariable "VC01_A" = "" then
                Assert.AreEqual(Some "", fst (run ())))

    [<Test>]
    member x.``8.1-2 sources are tried in list order, keys are ordinal``() =
        withEnv ["VC02_B", "b"] (fun () ->
            let logged = List<string>()
            let seen = ref []
            do xakeArgs ["-d"; "vc02x=1"] (x.Opts logged) {
                var "VC02X" [Env "VC02_A"; Env "VC02_B"; Default "d"]
                phony "main" (recipe {
                    let! a = getVar "VC02X"
                    let! b = getVar "vc02x"
                    seen.Value <- [a; b] })
            }
            Assert.AreEqual([Some "b"; Some "1"], seen.Value)
            Assert.That(bannerVars logged, Does.Contain "VC02X=b (env:VC02_B)"))

    [<Test>]
    member x.``8.1-3 -d wins over a rule and over a constant, the last -d wins``() =
        withEnv ["VC03_A", "a"] (fun () ->
            let run args withRule =
                let logged = List<string>()
                let seen = ref None
                if withRule then
                    do xakeArgs args (x.Opts logged) {
                        var "VC03X" [Env "VC03_A"]
                        phony "main" (recipe { let! v = getVar "VC03X" in seen.Value <- v })
                    }
                else
                    do xakeArgs args (x.Opts logged) {
                        var "VC03X" "s"
                        phony "main" (recipe { let! v = getVar "VC03X" in seen.Value <- v })
                    }
                seen.Value, bannerVars logged
            let v, banner = run ["-d"; "VC03X=c"] true
            Assert.AreEqual(Some "c", v)
            Assert.That(banner, Does.Contain "VC03X=c (cli)")
            let v, banner = run ["-d"; "VC03X=1"; "-d"; "VC03X=2"] true
            Assert.AreEqual(Some "2", v)
            Assert.That(banner, Does.Contain "VC03X=2 (cli)")
            let v, banner = run ["-d"; "VC03X=c"] false
            Assert.AreEqual(Some "c", v)
            Assert.That(banner, Does.Contain "VC03X=c (cli)"))

    [<Test>]
    member x.``8.1-4 ExecOptions Vars and a var op of one name fail the launch``() =
        let logged = List<string>()
        let ran = ref false
        let text =
            fails (fun () ->
                do xakeArgs [] { x.Opts logged with Vars = ["VC04X", "a"]; Setup = ["prep"] } {
                    var "VC04X" "b"
                    phony "prep" (recipe { ran.Value <- true })
                    phony "main" (recipe { ran.Value <- true })
                })
        StringAssert.Contains(e2 "VC04X" "ExecOptions.Vars" "var \"VC04X\" \"...\"", text)
        Assert.IsFalse(ran.Value, "neither setup nor a target runs")

    [<Test>]
    member x.``8.1-5 a second definition of any kind fails the launch, named in script order``() =
        let logged = List<string>()
        let main = recipe { return () }
        let rule = "var \"VC05X\" [...]"
        let constant = "var \"VC05X\" \"...\""
        let text = fails (fun () -> do xakeArgs [] (x.Opts logged) { var "VC05X" [Default "d"]; var "VC05X" "s"; phony "main" main })
        StringAssert.Contains(e2 "VC05X" rule constant, text)
        let text = fails (fun () -> do xakeArgs [] (x.Opts logged) { var "VC05X" "s"; var "VC05X" [Default "d"]; phony "main" main })
        StringAssert.Contains(e2 "VC05X" constant rule, text)
        let text = fails (fun () -> do xakeArgs [] (x.Opts logged) { var "VC05X" [Default "d"]; var "VC05X" [Env "VC05_A"]; phony "main" main })
        StringAssert.Contains(e2 "VC05X" rule rule, text)
        let text = fails (fun () -> do xakeArgs [] (x.Opts logged) { var "VC05X" "a"; var "VC05X" "b"; phony "main" main })
        StringAssert.Contains(e2 "VC05X" constant constant, text)
        let text = fails (fun () -> do xakeArgs [] { x.Opts logged with Vars = ["VC05X", "a"; "VC05X", "b"] } { phony "main" main })
        StringAssert.Contains(e2 "VC05X" "ExecOptions.Vars" "ExecOptions.Vars", text)

    [<Test>]
    member x.``8.1-6 all second definitions are reported together, no value printed``() =
        let logged = List<string>()
        let text =
            fails (fun () ->
                do xakeArgs [] (x.Opts logged) {
                    var "VC06X" "a"
                    var "VC06X" "b"
                    var "VC06Y" [Secret; Env "VC06_A"]
                    var "VC06Y" "vc06-secret-value"
                    phony "main" (recipe { return () })
                })
        StringAssert.Contains(e2 "VC06X" "var \"VC06X\" \"...\"" "var \"VC06X\" \"...\"", text)
        StringAssert.Contains(e2 "VC06Y" "var \"VC06Y\" [...]" "var \"VC06Y\" \"...\"", text)
        StringAssert.DoesNotContain("vc06-secret-value", text)
        StringAssert.DoesNotContain("vc06-secret-value", allText logged)
        Assert.That(logged, Has.Some.Contains "[ERROR]")

    [<Test>]
    member x.``8.1-7 a rule with no defining source leaves the name unset``() =
        let logged = List<string>()
        let seen = ref []
        do xakeArgs [] (x.Opts logged) {
            var "VC07X" [Env "VC07_A"]
            var "VC07Y" []
            phony "main" (recipe {
                let! a = getVar "VC07X"
                let! b = getVar "VC07Y"
                seen.Value <- [a; b] })
        }
        Assert.AreEqual(([None; None]: string option list), seen.Value)
        let banner = bannerVars logged
        Assert.That(banner, Does.Contain "VC07X (not set)")
        Assert.That(banner, Does.Contain "VC07Y (not set)")

    [<Test>]
    member x.``8.1-8 the environment is read at launch, once``() =
        let logged = List<string>()
        let schema = {| VC08Y = Var.string(envVar = "VC08_A") |}
        try
            setEnv "VC08_A" "a"
            let b = xakeArgs [] (x.Opts logged)
            let ops = b.AddVar(b.Vars(b.Zero(), schema), "VC08X", [Env "VC08_A"])
            let (XakeScript (o, _)) = ops
            Assert.AreEqual(["VC08Y"; "VC08X"], o.VarDecls |> List.map (fun d -> d.Key))
            Assert.IsEmpty(o.Vars, "the ops record no value")
            // changed after the ops, before Run: seen
            setEnv "VC08_A" "z"
            let seen = ref []
            b.Run(b.Phony(ops, "main", recipe {
                let! a = getVar "VC08X"
                let! c = getVar "VC08Y"
                seen.Value <- [a; c] }))
            Assert.AreEqual([Some "z"; Some "z"], seen.Value)
            // changed by a setup target or a recipe: not seen
            setEnv "VC08_A" "a"
            let script =
                b.Setup(
                    b.Phony(
                        b.Phony(ops, "prep", recipe { setEnv "VC08_A" "by-setup" }),
                        "main", recipe {
                            let! a = getVar "VC08X"
                            setEnv "VC08_A" "by-recipe"
                            let! c = getVar "VC08Y"
                            seen.Value <- [a; c] }),
                    ["prep"])
            b.Run script
            Assert.AreEqual([Some "a"; Some "a"], seen.Value)
        finally
            setEnv "VC08_A" null

    // --- 8.2 varschema ---

    [<Test>]
    member x.``8.2-9 a varschema field is the rule of its sources``() =
        let schema = {| VC09X = Var.string(envVar = "VC09_A") |> withDefault "d" |}
        let run () =
            let logged = List<string>()
            let seen = ref None
            do xakeArgs [] (x.Opts logged) {
                varschema schema
                phony "main" (recipe { let! v = getVar "VC09X" in seen.Value <- v })
            }
            seen.Value, bannerVars logged
        withEnv ["VC09_A", "a"] (fun () ->
            let v, banner = run ()
            Assert.AreEqual(Some "a", v)
            Assert.That(banner, Does.Contain "VC09X=a (env:VC09_A)"))
        let v, banner = run ()
        Assert.AreEqual(Some "d", v)
        Assert.That(banner, Does.Contain "VC09X=d (default)")

    [<Test>]
    member x.``8.2-10 a field's env name derives from its key``() =
        withEnv ["BUILD_CONFIG", "r"] (fun () ->
            let logged = List<string>()
            let schema = {| buildConfig = Var.string() |}
            do xakeArgs [] (x.Opts logged) {
                varschema schema
                phony "main" (recipe { return () })
            }
            Assert.That(bannerVars logged, Does.Contain "buildConfig=r (env:BUILD_CONFIG)"))

    [<Test>]
    member x.``8.2-11 an ArgOnly field takes no environment value``() =
        withEnv ["VC11X", "e"] (fun () ->
            let schema = {| VC11X = Var.arg<string>() |}
            let run args =
                let logged = List<string>()
                do xakeArgs args (x.Opts logged) {
                    varschema schema
                    phony "main" (recipe { return () })
                }
                bannerVars logged
            Assert.That(run [], Does.Contain "VC11X (not set)")
            Assert.That(run ["-d"; "VC11X=c"], Does.Contain "VC11X=c (cli)"))

    [<Test>]
    member x.``8.2-12 a typed default is read typed, getVar sees its string``() =
        let logged = List<string>()
        let schema = {| VC12N = Var.int() |> withDefault 5 |}
        let typed = ref 0
        let raw = ref None
        do xakeArgs [] (x.Opts logged) {
            varschema schema
            phony "main" (recipe {
                let! n = schema.VC12N
                let! s = getVar "VC12N"
                typed.Value <- n
                raw.Value <- s })
        }
        Assert.AreEqual(5, typed.Value)
        Assert.AreEqual(Some "5", raw.Value)
        Assert.That(bannerVars logged, Does.Contain "VC12N=5 (default)")

    [<Test>]
    member x.``8.2-13 a missing required field fails the launch``() =
        let logged = List<string>()
        let ran = ref false
        let schema = {| VC13R = Var.string() |> required |}
        let text =
            fails (fun () ->
                do xakeArgs [] (x.Opts logged) {
                    varschema schema
                    phony "main" (recipe { ran.Value <- true })
                })
        StringAssert.Contains("Required variable 'VC13R' is not provided", text)
        Assert.IsFalse ran.Value

    [<Test>]
    member x.``8.2-14 a bad typed value fails the launch, whatever its source``() =
        let schema = {| VC14N = Var.int() |}
        let run args =
            let logged = List<string>()
            let seen = ref None
            do xakeArgs args (x.Opts logged) {
                varschema schema
                phony "main" (recipe { let! n = schema.VC14N in seen.Value <- n })
            }
            seen.Value
        let expected = "Failed to convert value 'x' for variable 'VC14N' to type 'System.Int32'."
        StringAssert.Contains(expected, fails (fun () -> run ["-d"; "VC14N=x"] |> ignore))
        withEnv ["VC14N", "x"] (fun () ->
            StringAssert.Contains(expected, fails (fun () -> run [] |> ignore))
            Assert.AreEqual(Some 4, run ["-d"; "VC14N=4"]))

    [<Test>]
    member x.``8.2-15 a field and another definition of its key fail the launch``() =
        let logged = List<string>()
        let schema = {| VC15X = Var.string() |}
        let field = "varschema field VC15X"
        let main = recipe { return () }
        let text = fails (fun () -> do xakeArgs [] (x.Opts logged) { varschema schema; var "VC15X" [Default "d"]; phony "main" main })
        StringAssert.Contains(e2 "VC15X" field "var \"VC15X\" [...]", text)
        let text = fails (fun () -> do xakeArgs [] (x.Opts logged) { varschema schema; var "VC15X" "s"; phony "main" main })
        StringAssert.Contains(e2 "VC15X" field "var \"VC15X\" \"...\"", text)
        let text = fails (fun () -> do xakeArgs [] { x.Opts logged with Vars = ["VC15X", "s"] } { varschema schema; phony "main" main })
        StringAssert.Contains(e2 "VC15X" "ExecOptions.Vars" field, text)
        let other = {| VC15X = Var.string(envVar = "VC15_B") |}
        let text = fails (fun () -> do xakeArgs [] (x.Opts logged) { varschema schema; varschema other; phony "main" main })
        StringAssert.Contains(e2 "VC15X" field field, text)

    [<Test>]
    member x.``8.2-16 -d of an EnvOnly field fails the launch``() =
        let schema = {| VC16X = Var.env<string>(envVar = "VC16_A") |}
        let run args =
            let logged = List<string>()
            let seen = ref None
            do xakeArgs args (x.Opts logged) {
                varschema schema
                phony "main" (recipe { let! v = schema.VC16X in seen.Value <- v })
            }
            seen.Value
        let text = fails (fun () -> run ["-d"; "VC16X=c"] |> ignore)
        StringAssert.Contains("Variable 'VC16X' comes only from the environment variable VC16_A, not from -d VC16X=...", text)
        withEnv ["VC16_A", "e"] (fun () -> Assert.AreEqual(Some "e", run ["-d"; "VC16_A=c"]))

    [<Test>]
    member x.``8.2-17 --help evaluates nothing``() =
        withEnv ["VC17_A", "vc17-env-value"] (fun () ->
            let schema = {| VC17R = Var.string() |> required; VC17N = Var.int() |}
            let b = xakeArgs ["--help"; "-d"; "VC17N=x"] { x.TestOptions with ThrowOnError = true; FileLog = ""; NoPersist = true }
            let script = b.AddVar(b.Vars(b.Zero(), schema), "VC17X", [Env "VC17_A"])
            let (XakeScript (o, _)) = b.ApplyCommandLine script
            Assert.IsTrue o.ShowHelp
            let text = XakeScriptBuilder.formatHelp o |> String.concat "\n"
            StringAssert.Contains("-d VC17R=<value> [string] (required)", text)
            StringAssert.Contains("-d VC17X=<value> [string], env: VC17_A", text)
            StringAssert.DoesNotContain("vc17-env-value", text))

    // --- 8.3 Var reads ---

    [<Test>]
    member x.``8.3-18 a Var read takes the value of the stored declaration``() =
        withEnv ["VC18_A", "v"] (fun () ->
            let logged = List<string>()
            let schema = {| VC18X = Var.string(envVar = "VC18_A"); VC18Y = Var.string() |> withDefault "d" |}
            let seen = ref (None, "")
            let deps = ref []
            do xakeArgs [] (x.Opts logged) {
                varschema schema
                phony "main" (recipe {
                    let! v = schema.VC18X
                    let! y = Var.string "VC18Y" |> withDefault "d"
                    let! r = getResult()
                    deps.Value <- r.Depends
                    seen.Value <- (v, y) })
            }
            Assert.AreEqual((Some "v", "d"), seen.Value)
            Assert.That(deps.Value, Does.Contain(Var ("VC18X", Some "v")))
            Assert.IsFalse(deps.Value |> List.exists (function EnvVar _ -> true | _ -> false), "no EnvVar"))

    [<Test>]
    member x.``8.3-19 a read through another declaration fails: sources``() =
        let logged = List<string>()
        let schema = {| VC19X = Var.string(envVar = "VC19_A") |}
        let text =
            fails (fun () ->
                do xakeArgs [] (x.Opts logged) {
                    varschema schema
                    phony "main" (recipe { let! _ = Var.string("VC19X", envVar = "VC19_B") in () })
                })
        StringAssert.Contains(
            "Variable 'VC19X' is read through a Var declaration that differs from its declaration by varschema field VC19X in sources: read it through that declaration, or use getVar \"VC19X\"", text)

    [<Test>]
    member x.``8.3-20 a read through another declaration fails: type, required, secret mode``() =
        let logged = List<string>()
        let schema = {| VC20N = Var.int(); VC20R = Var.string() |> required; VC20T = Var.string() |> secretHashed |}
        let read (r: Recipe<ExecContext, unit>) =
            fails (fun () ->
                do xakeArgs ["-d"; "VC20R=r"] (x.Opts logged) {
                    varschema schema
                    phony "main" r
                })
        StringAssert.Contains("by varschema field VC20N in type:", read (recipe { let! _ = Var.string "VC20N" in () }))
        StringAssert.Contains("by varschema field VC20R in required:", read (recipe { let! _ = Var.string "VC20R" in () }))
        StringAssert.Contains("by varschema field VC20T in secret mode:", read (recipe { let! _ = Var.string "VC20T" |> secret in () }))

    [<Test>]
    member x.``8.3-21 a Var read through a var rule``() =
        let logged = List<string>()
        let seen = ref ""
        let deps = ref []
        do xakeArgs [] (x.Opts logged) {
            var "VC21X" [Env "VC21X"; Default "d"]
            phony "main" (recipe {
                let! v = Var.string "VC21X" |> withDefault "d"
                let! r = getResult()
                deps.Value <- r.Depends
                seen.Value <- v })
        }
        Assert.AreEqual("d", seen.Value)
        Assert.That(deps.Value, Does.Contain(Var ("VC21X", Some "d")))
        let text =
            fails (fun () ->
                do xakeArgs [] (x.Opts logged) {
                    var "VC21X" [Env "VC21X"; Default "d"]
                    phony "main" (recipe { let! _ = Var.string "VC21X" in () })
                })
        StringAssert.Contains("by var \"VC21X\" [...] in sources:", text)

    [<Test>]
    member x.``8.3-22 a Var read without a declaration fails``() =
        let logged = List<string>()
        let readString = recipe { let! _ = Var.string "VC22Y" in () }
        let expected = "Variable 'VC22Y' is read through Var but is not declared: declare it in varschema, or use getVar \"VC22Y\" or getEnv \"VC22Y\""
        StringAssert.Contains(expected, fails (fun () -> do xakeArgs ["-d"; "VC22Y=1"] (x.Opts logged) { phony "main" readString }))
        StringAssert.Contains(expected, fails (fun () -> do xakeArgs [] (x.Opts logged) { var "VC22Y" "1"; phony "main" readString }))
        StringAssert.Contains(expected, fails (fun () -> do xakeArgs [] { x.Opts logged with Vars = ["VC22Y", "1"] } { phony "main" readString }))
        StringAssert.Contains(expected, fails (fun () -> do xakeArgs [] (x.Opts logged) { phony "main" readString }))
        let text = fails (fun () -> do xakeArgs [] (x.Opts logged) { phony "main" (recipe { let! _ = Var.arg<string>(cliArg = "VC22Y") in () }) })
        StringAssert.Contains("declare it in varschema, or use getVar \"VC22Y\"", text)
        StringAssert.DoesNotContain("getEnv", text)

    [<Test>]
    member x.``8.3-23 getVar reads the set, getEnv the environment; one dependency per key``() =
        let logged = List<string>()
        let runs = ref 0
        let run () =
            let seen = ref (None, None)
            let deps = ref []
            do xakeArgs [] (x.PersistOpts logged) {
                phony "main" (recipe {
                    let! v = getVar "VC23X"
                    let! e = getEnv "VC23X"
                    let! r = getResult()
                    deps.Value <- r.Depends
                    runs.Value <- runs.Value + 1
                    seen.Value <- (v, e) })
            }
            seen.Value, deps.Value
        try
            setEnv "VC23X" "a"
            let seen, deps = run ()
            Assert.AreEqual((None, Some "a"), seen)
            Assert.That(deps, Does.Contain(EnvVar ("VC23X", Some "a")))
            run () |> ignore
            Assert.AreEqual(1, runs.Value, "up to date")
            setEnv "VC23X" "b"
            run () |> ignore
            Assert.AreEqual(2, runs.Value, "getEnv is checked live")
        finally
            setEnv "VC23X" null
        let schema = {| VC23K = Var.string() |> withDefault "k" |}
        let deps = ref []
        do xakeArgs [] (x.Opts logged) {
            varschema schema
            phony "main" (recipe {
                let! _ = getVar "VC23K"
                let! _ = getVar "VC23K"
                let! _ = schema.VC23K
                let! r = getResult()
                deps.Value <- r.Depends })
        }
        Assert.AreEqual(1, deps.Value |> List.filter (function Var ("VC23K", _) -> true | _ -> false) |> List.length)

    // --- 8.4 Dependencies ---

    [<Test>]
    member x.``8.4-24 the value is recorded, not its source``() =
        let logged = List<string>()
        let runs = ref 0
        let runRule args =
            do xakeArgs args (x.PersistOpts logged) {
                var "VC24X" [Env "VC24_A"]
                phony "main" (recipe { let! _ = getVar "VC24X" in runs.Value <- runs.Value + 1 })
            }
        withEnv ["VC24_A", "v"] (fun () -> runRule [])
        runRule ["-d"; "VC24X=v"]
        Assert.AreEqual(1, runs.Value, "an equal value moved from the environment to -d: up to date")

        let schema = {| VC24F = Var.string(envVar = "VC24_B") |}
        let runField args =
            do xakeArgs args (x.PersistOpts logged) {
                varschema schema
                phony "main" (recipe { let! _ = schema.VC24F in runs.Value <- runs.Value + 1 })
            }
        withEnv ["VC24_B", "v"] (fun () -> runField [])
        Assert.AreEqual(2, runs.Value)
        runField ["-d"; "VC24F=w"]
        Assert.AreEqual(3, runs.Value, "a changed value rebuilds")
        Assert.That(allText logged, Does.Contain "Global script variable VC24F was changed 'Some \"v\"'->'Some \"w\"'")

    [<Test>]
    member x.``8.4-25 a default replaced by an environment value rebuilds; a 3.15 record rebuilds once``() =
        let logged = List<string>()
        let runs = ref 0
        let runRule () =
            do xakeArgs [] (x.PersistOpts logged) {
                var "VC25X" [Env "VC25_A"; Default "d"]
                phony "main" (recipe { let! _ = getVar "VC25X" in runs.Value <- runs.Value + 1 })
            }
        runRule ()
        withEnv ["VC25_A", "e"] runRule
        Assert.AreEqual(2, runs.Value)

        let schema = {| VC25F = Var.string(envVar = "VC25_B") |> withDefault "d" |}
        let runField () =
            do xakeArgs [] (x.PersistOpts logged) {
                varschema schema
                phony "main" (recipe { let! _ = schema.VC25F in runs.Value <- runs.Value + 1 })
            }
        runField ()
        withEnv ["VC25_B", "e"] runField
        Assert.AreEqual(4, runs.Value)

        // a 3.15 Var read recorded Var (X, None) + EnvVar (A, Some v)
        IO.File.Delete ".xake"
        let old = { BuildLog.makeResult [PhonyAction "main"] with Depends = [ Var ("VC25G", None); EnvVar ("VC25_C", Some "v") ] }
        OldDatabase.write ".xake" BuildLog.XakeDbVersion [old]
        let schema = {| VC25G = Var.string(envVar = "VC25_C") |}
        withEnv ["VC25_C", "v"] (fun () ->
            let runOld () =
                do xakeArgs [] (x.PersistOpts logged) {
                    varschema schema
                    phony "main" (recipe { let! _ = schema.VC25G in runs.Value <- runs.Value + 1 })
                }
            runOld ()
            Assert.AreEqual(5, runs.Value, "rebuilt once")
            runOld ()
            Assert.AreEqual(5, runs.Value, "then up to date"))

    // --- 8.5 Secrets ---

    [<Test>]
    member x.``8.5-26 -d of a secret rule or of its Env name fails the launch``() =
        let run args =
            let logged = List<string>()
            let text =
                fails (fun () ->
                    do xakeArgs args (x.Opts logged) {
                        var "VC26T" [Secret; Env "VC26_A"]
                        phony "main" (recipe { return () })
                    })
            text, allText logged
        let text, log = run ["-d"; "VC26T=vc26-given-on-cli"]
        StringAssert.Contains("Variable 'VC26T' is secret: secret variables come from the environment, set VC26_A instead of passing -d VC26T=...", text)
        StringAssert.DoesNotContain("vc26-given-on-cli", text + log)
        let text, log = run ["-d"; "VC26_A=vc26-given-env-name"]
        StringAssert.Contains("Variable 'VC26_A' is secret: secret variables come from the environment, set VC26_A instead of passing -d VC26_A=...", text)
        StringAssert.DoesNotContain("vc26-given-env-name", text + log)

    [<Test>]
    member x.``8.5-27 -d of a secret without an Env source fails the launch``() =
        let logged = List<string>()
        let text =
            fails (fun () ->
                do xakeArgs ["-d"; "VC27T=vc27-given-on-cli"] (x.Opts logged) {
                    var "VC27T" [Secret; Default "x"]
                    phony "main" (recipe { return () })
                })
        StringAssert.Contains("Variable 'VC27T' is secret: its value comes only from its declaration's sources, not from -d VC27T=...", text)
        StringAssert.DoesNotContain("vc27-given-on-cli", text + allText logged)

    [<Test>]
    member x.``8.5-28 a script value of a secret is a second definition``() =
        let logged = List<string>()
        let text =
            fails (fun () ->
                do xakeArgs [] (x.Opts logged) {
                    var "VC28T" [Secret; Env "VC28_A"]
                    var "VC28T" "vc28-secret-script"
                    phony "main" (recipe { return () })
                })
        StringAssert.Contains(e2 "VC28T" "var \"VC28T\" [...]" "var \"VC28T\" \"...\"", text)
        StringAssert.DoesNotContain("vc28-secret-script", text + allText logged)
        let text =
            fails (fun () ->
                do xakeArgs [] { x.Opts logged with Vars = ["VC28T", "vc28-secret-options"] } {
                    var "VC28T" [Secret; Env "VC28_A"]
                    phony "main" (recipe { return () })
                })
        StringAssert.Contains(e2 "VC28T" "ExecOptions.Vars" "var \"VC28T\" [...]", text)
        StringAssert.DoesNotContain("vc28-secret-options", text + allText logged)

    [<Test>]
    member x.``8.5-29 -d of a Var.secret field or of its env name fails the launch``() =
        let schema = {| VC29T = Var.secret<string>(envVar = "VC29_A") |}
        let run args =
            fails (fun () ->
                do xakeArgs args (x.Opts (List<string>())) {
                    varschema schema
                    phony "main" (recipe { return () })
                })
        StringAssert.Contains("Variable 'VC29T' is secret: secret variables come from the environment, set VC29_A instead of passing -d VC29T=...", run ["-d"; "VC29T=vc29-given"])
        StringAssert.Contains("Variable 'VC29_A' is secret: secret variables come from the environment, set VC29_A instead of passing -d VC29_A=...", run ["-d"; "VC29_A=vc29-given"])

    [<Test>]
    member x.``8.5-30 -d of an argument secret fails the launch, no warning``() =
        let logged = List<string>()
        let schema = {| VC30T = Var.string() |> secret |}
        let text =
            fails (fun () ->
                do xakeArgs ["-d"; "VC30T=vc30-given"] (x.Opts logged) {
                    varschema schema
                    phony "main" (recipe { return () })
                })
        StringAssert.Contains("Variable 'VC30T' is secret: secret variables come from the environment, set VC30T instead of passing -d VC30T=...", text)
        Assert.That(logged, Has.None.Contains "[WARN]")

    [<Test>]
    member x.``8.5-31 a secret is masked in the banner and recorded by presence``() =
        withEnv ["VC31_A", "vc31-secret-rule"; "VC31_B", "vc31-secret-field"] (fun () ->
            let logged = List<string>()
            let schema = {| VC31F = Var.secret<string>(envVar = "VC31_B") |}
            let deps = ref []
            do xakeArgs [] (x.Opts logged) {
                var "VC31T" [Secret; Env "VC31_A"]
                varschema schema
                phony "main" (recipe {
                    let! t = getVar "VC31T"
                    let! f = schema.VC31F
                    do! trace Info "values %A %A" t f
                    let! r = getResult()
                    deps.Value <- r.Depends })
            }
            let banner = bannerVars logged
            Assert.That(banner, Does.Contain "VC31T=*** (env:VC31_A)")
            Assert.That(banner, Does.Contain "VC31F=*** (env:VC31_B)")
            Assert.That(logged, Has.None.Contains "vc31-secret")
            Assert.That(deps.Value, Does.Contain(SecretDep ("VC31T", SecretArg, SecretPresent)))
            Assert.That(deps.Value, Does.Contain(SecretDep ("VC31F", SecretArg, SecretPresent))))

    [<Test>]
    member x.``8.5-32 hashed secrets rebuild on change, presence secrets on unset``() =
        let logged = List<string>()
        let runs = ref 0
        let runHashed () =
            do xakeArgs [] (x.PersistOpts logged) {
                var "VC32H" [SecretHashed; Env "VC32_A"]
                phony "main" (recipe { let! _ = getVar "VC32H" in runs.Value <- runs.Value + 1 })
            }
        withEnv ["VC32_A", "vc32-hashed-one"] runHashed
        withEnv ["VC32_A", "vc32-hashed-two"] runHashed
        Assert.AreEqual(2, runs.Value)
        Assert.That(logged, Has.Some.Contains "Secret variable VC32H changed")
        Assert.That(logged, Has.None.Contains "vc32-hashed")

        IO.File.Delete ".xake"
        let runPresence () =
            do xakeArgs [] (x.PersistOpts logged) {
                var "VC32P" [Secret; Env "VC32_B"]
                phony "main" (recipe { let! _ = getVar "VC32P" in runs.Value <- runs.Value + 1 })
            }
        withEnv ["VC32_B", "vc32-presence-one"] runPresence
        withEnv ["VC32_B", "vc32-presence-two"] runPresence
        Assert.AreEqual(3, runs.Value, "presence: a changed value is up to date")
        runPresence ()
        Assert.AreEqual(4, runs.Value, "unset rebuilds")

    [<Test>]
    member x.``8.5-33 with both markers the mode is HMAC; --help shows no default of a secret``() =
        withEnv ["VC33_A", "vc33-secret"] (fun () ->
            let deps = ref []
            do xakeArgs [] (x.Opts (List<string>())) {
                var "VC33T" [Secret; SecretHashed; Env "VC33_A"]
                phony "main" (recipe {
                    let! _ = getVar "VC33T"
                    let! r = getResult()
                    deps.Value <- r.Depends })
            }
            Assert.IsTrue(deps.Value |> List.exists (function SecretDep ("VC33T", SecretArg, SecretHmac _) -> true | _ -> false)))
        let b = RulesBuilder(ExecOptions.Default)
        let (XakeScript (o, _)) = b.AddVar(b.Zero(), "VC33K", [Secret; Default "vc33-default"])
        let text = XakeScriptBuilder.formatHelp o |> String.concat "\n"
        StringAssert.Contains("-d VC33K=<value> [string] (secret)", text)
        StringAssert.DoesNotContain("vc33-default", text)

    [<Test>]
    member x.``8.5-34 getVar and getEnv of a secret return the value, masked in output``() =
        withEnv ["VC34_A", "vc34-secret-value"] (fun () ->
            let logged = List<string>()
            let seen = ref (None, None)
            let deps = ref []
            do xakeArgs [] (x.Opts logged) {
                var "VC34T" [Secret; Env "VC34_A"]
                phony "main" (recipe {
                    let! t = getVar "VC34T"
                    let! a = getEnv "VC34_A"
                    do! trace Info "values %A %A" t a
                    let! r = getResult()
                    deps.Value <- r.Depends
                    seen.Value <- (t, a) })
            }
            Assert.AreEqual((Some "vc34-secret-value", Some "vc34-secret-value"), seen.Value)
            Assert.That(logged, Has.None.Contains "vc34-secret-value")
            Assert.That(deps.Value, Does.Contain(SecretDep ("VC34T", SecretArg, SecretPresent)))
            Assert.That(deps.Value, Does.Contain(SecretDep ("VC34_A", SecretEnv, SecretPresent))))

    [<Test>]
    member x.``8.5-35 a bad typed secret fails the launch without its value``() =
        withEnv ["VC35T", "vc35-not-a-number"] (fun () ->
            let logged = List<string>()
            let schema = {| VC35T = Var.int() |> secret |}
            let text =
                fails (fun () ->
                    do xakeArgs [] (x.Opts logged) {
                        varschema schema
                        phony "main" (recipe { return () })
                    })
            StringAssert.Contains("Failed to convert the value of secret variable 'VC35T' to type 'System.Int32'.", text)
            StringAssert.DoesNotContain("vc35-not-a-number", text + allText logged))

    // --- 8.6 Banner, help, debug dump ---

    [<Test>]
    member x.``8.6-36 the banner lists each name once with its source``() =
        withEnv ["VC36_E", "3"] (fun () ->
            let logged = List<string>()
            let schema = {| VC36D = Var.int() |> withDefault 4 |}
            do xakeArgs ["-d"; "VC36A=1"; "-d"; "VC36X=1"; "-d"; "VC36X=2"] (x.Opts logged) {
                var "VC36B" "2"
                var "VC36C" [Env "VC36_E"]
                varschema schema
                var "VC36F" [Env "VC36_G"]
                phony "main" (recipe { return () })
            }
            Assert.AreEqual(
                [ "VC36A=1 (cli)"; "VC36B=2 (script)"; "VC36C=3 (env:VC36_E)"; "VC36D=4 (default)"; "VC36F (not set)"; "VC36X=2 (cli)" ],
                bannerVars logged))

    [<Test>]
    member x.``8.6-37 --help lists the fields, then the rules, not the constants``() =
        let b = RulesBuilder(ExecOptions.Default)
        let schema = {| VC37S = Var.string() |}
        let script = b.AddVar(b.AddVar(b.Vars(b.Zero(), schema), "VC37R", [Env "VC37_A"; Env "VC37_B"; Default "d"]), "VC37K", "v")
        let (XakeScript (o, _)) = script
        let lines = XakeScriptBuilder.formatHelp o
        let field = lines |> List.findIndex (fun l -> l.Contains "-d VC37S=<value> [string]")
        let rule = lines |> List.findIndex (fun l -> l = "  -d VC37R=<value> [string], env: VC37_A, VC37_B, default: d")
        Assert.Less(field, rule)
        Assert.That(lines, Has.None.Contains "VC37K")

    [<Test>]
    member x.``8.6-38 the debug dump shows the variables, secrets masked``() =
        withEnv ["VC38_A", "vc38-secret-env"] (fun () ->
            let logged = List<string>()
            let schema = {| VC38T = Var.secret<string>(envVar = "VC38_A") |}
            do xakeArgs ["-d"; "VC38P=1"] (x.Opts logged) {
                varschema schema
                var "VC38K" [Secret; Default "vc38-secret-default"]
                phony "main" (recipe { return () })
            }
            let dump = logged |> Seq.filter (fun l -> l.StartsWith "[DBG]") |> String.concat "\n"
            StringAssert.Contains("(\"VC38P\", \"1\")", dump)
            StringAssert.Contains("Variables", dump)
            StringAssert.Contains("VarDecls", dump)
            StringAssert.Contains("VC38T", dump)
            StringAssert.Contains("VC38K", dump)
            StringAssert.DoesNotContain("vc38-secret-env", dump)
            StringAssert.DoesNotContain("vc38-secret-default", dump))

    // --- 8.7 Engine ---

    [<Test>]
    member x.``8.7-39 per-demand vars win over the script's``() =
        let seen = List<string option>()
        let eng =
            xakeEngine {
                var "VC39V" "s"
                phony "t" (recipe { let! v = getVar "VC39V" in lock seen (fun () -> seen.Add v) })
                start
            }
        try
            eng.Demand("t", ["VC39V", "r"]).Wait()
            eng.Demand("t").Wait()
        finally eng.StopAsync().Wait()
        Assert.AreEqual([Some "r"; Some "s"], List.ofSeq seen)

    [<Test>]
    member x.``8.7-40 concurrent demands see their own vars``() =
        let seen = Collections.Concurrent.ConcurrentDictionary<string, string option>()
        let read name = recipe {
            do! Async.Sleep 50     // the two demands overlap
            let! v = getVar "VC40V"
            seen.[name] <- v }
        let eng =
            xakeEngine {
                var "VC40V" "s"
                phony "a" (read "a")
                phony "b" (read "b")
                start
            }
        try
            let t1 = eng.Demand("a", ["VC40V", "r1"])
            let t2 = eng.Demand("b", ["VC40V", "r2"])
            Threading.Tasks.Task.WaitAll(t1, t2)
        finally eng.StopAsync().Wait()
        Assert.AreEqual(Some "r1", seen.["a"])
        Assert.AreEqual(Some "r2", seen.["b"])

    [<Test>]
    member x.``8.7-41 a bad per-demand value fails the demand before any target``() =
        let runs = ref 0
        let schema = {| VC41E = Var.env<string>(); VC41N = Var.int() |}
        let eng =
            xakeEngine {
                var "VC41T" [Secret; Env "VC41_A"]
                varschema schema
                phony "t" (recipe { runs.Value <- runs.Value + 1 })
                start
            }
        try
            StringAssert.Contains(
                "Variable 'VC41T' is secret: its value comes only from its declaration's sources, not from the ?vars of Demand",
                demandFails (eng.Demand("t", ["VC41T", "vc41-request-secret"])))
            StringAssert.Contains(
                "Variable 'VC41E' comes only from the environment variable VC41E, not from the ?vars of Demand",
                demandFails (eng.Demand("t", ["VC41E", "x"])))
            StringAssert.Contains(
                "Failed to convert value 'x' for variable 'VC41N' to type 'System.Int32'.",
                demandFails (eng.Demand("t", ["VC41N", "x"])))
        finally eng.StopAsync().Wait()
        Assert.AreEqual(0, runs.Value)

    [<Test>]
    member x.``8.7-42 a per-demand value of a typed field is read converted``() =
        let schema = {| VC42N = Var.int() |> withDefault 1 |}
        let seen = List<int>()
        let eng =
            xakeEngine {
                varschema schema
                phony "t" (recipe { let! n = schema.VC42N in lock seen (fun () -> seen.Add n) })
                start
            }
        try
            eng.Demand("t", ["VC42N", "7"]).Wait()
            eng.Demand("t").Wait()
        finally eng.StopAsync().Wait()
        Assert.AreEqual([7; 1], List.ofSeq seen)

    [<Test>]
    member x.``8.7-43 the engine reads the environment once, at Start``() =
        let seen = List<string option * string option>()
        let b = xakeEngine
        let script =
            b.Phony(b.AddVar(b.Zero(), "VC43X", [Env "VC43_A"]), "t", recipe {
                let! v = getVar "VC43X"
                let! e = getEnv "VC43_A"
                lock seen (fun () -> seen.Add((v, e))) })
        try
            setEnv "VC43_A" "before-start"     // after the script is built, before Start: seen
            let eng = XakeEngine.Start script
            try
                setEnv "VC43_A" "after-start"
                eng.Demand("t").Wait()
            finally eng.StopAsync().Wait()
        finally
            setEnv "VC43_A" null
        Assert.AreEqual([Some "before-start", Some "after-start"], List.ofSeq seen)

    [<Test>]
    member x.``8.7-44 Start raises the launch errors``() =
        let text =
            fails (fun () ->
                xakeEngine {
                    var "VC44X" "a"
                    var "VC44X" "b"
                    phony "t" (recipe { return () })
                    start
                } |> ignore)
        StringAssert.Contains(e2 "VC44X" "var \"VC44X\" \"...\"" "var \"VC44X\" \"...\"", text)
        let schema = {| VC44R = Var.string() |> required |}
        let text =
            fails (fun () ->
                xakeEngine {
                    varschema schema
                    phony "t" (recipe { return () })
                    start
                } |> ignore)
        StringAssert.Contains("Required variable 'VC44R' is not provided", text)

    [<Test>]
    member x.``8.7-45 an engine from EngineOptions has no declarations``() =
        let eng =
            XakeEngine.Start { EngineOptions.Default with
                                 Rules = Rules [ "t" => recipe { let! _ = Var.string "VC45X" in () } ] }
        try
            StringAssert.Contains("Variable 'VC45X' is read through Var but is not declared", demandFails (eng.Demand "t"))
        finally eng.StopAsync().Wait()

    // --- 8.10 Names: the key and the external (-d) name ---

    [<Test>]
    member x.``8.10-61 -d of the external name sets the key``() =
        let logged = List<string>()
        let schema = {| VC61P = Var.string(cliArg = "vc61pkg", envVar = "VC61_A") |}
        let seen = ref []
        do xakeArgs ["-d"; "vc61pkg=v"] (x.Opts logged) {
            varschema schema
            phony "main" (recipe {
                let! p = getVar "VC61P"
                let! ext = getVar "vc61pkg"
                seen.Value <- [p; ext] })
        }
        Assert.AreEqual([Some "v"; None], seen.Value)
        Assert.That(bannerVars logged, Does.Contain "VC61P=v (cli:vc61pkg)")

    [<Test>]
    member x.``8.10-62 -d of the key of a declaration with an external name fails the launch``() =
        let schema = {| VC62P = Var.string(cliArg = "vc62pkg", envVar = "VC62_A") |}
        let text =
            fails (fun () ->
                do xakeArgs ["-d"; "VC62P=v"] (x.Opts (List<string>())) {
                    varschema schema
                    phony "main" (recipe { return () })
                })
        StringAssert.Contains("-d VC62P: the variable is set on the command line as vc62pkg; use -d vc62pkg=...", text)

    [<Test>]
    member x.``8.10-63 a Cli source names the external name of a rule, anywhere in the list``() =
        let run args first =
            let logged = List<string>()
            if first then
                do xakeArgs args (x.Opts logged) {
                    var "VC63P" [Cli "vc63pkg"; Env "VC63_A"]
                    phony "main" (recipe { return () })
                }
            else
                do xakeArgs args (x.Opts logged) {
                    var "VC63P" [Env "VC63_A"; Cli "vc63pkg"]
                    phony "main" (recipe { return () })
                }
            bannerVars logged
        Assert.That(run ["-d"; "vc63pkg=v"] true, Does.Contain "VC63P=v (cli:vc63pkg)")
        Assert.That(run ["-d"; "vc63pkg=v"] false, Does.Contain "VC63P=v (cli:vc63pkg)")
        let text = fails (fun () -> run ["-d"; "VC63P=v"] true |> ignore)
        StringAssert.Contains("-d VC63P: the variable is set on the command line as vc63pkg; use -d vc63pkg=...", text)

    [<Test>]
    member x.``8.10-64 two Cli sources fail the launch``() =
        let text =
            fails (fun () ->
                do xakeArgs [] (x.Opts (List<string>())) {
                    var "VC64P" [Cli "a"; Cli "b"]
                    phony "main" (recipe { return () })
                })
        StringAssert.Contains("Variable 'VC64P' has more than one command-line name: a and b", text)

    [<Test>]
    member x.``8.10-65 an undeclared -d name is its own key``() =
        let logged = List<string>()
        let seen = ref None
        do xakeArgs ["-d"; "VC65Z=1"] (x.Opts logged) {
            phony "main" (recipe { let! z = getVar "VC65Z" in seen.Value <- z })
        }
        Assert.AreEqual(Some "1", seen.Value)
        Assert.That(bannerVars logged, Does.Contain "VC65Z=1 (cli)")

    [<Test>]
    member x.``8.10-66 -d of a secret fails under any of its names``() =
        let run (given: string) =
            let logged = List<string>()
            let text =
                fails (fun () ->
                    do xakeArgs ["-d"; given + "=vc66-secret-given"] (x.Opts logged) {
                        var "VC66T" [Secret; Env "VC66_A"; Cli "vc66tok"]
                        phony "main" (recipe { return () })
                    })
            StringAssert.Contains(sprintf "Variable '%s' is secret: secret variables come from the environment, set VC66_A instead of passing -d %s=..." given given, text)
            StringAssert.DoesNotContain("vc66-secret-given", text + allText logged)
        run "vc66tok"
        run "VC66T"
        run "VC66_A"

    [<Test>]
    member x.``8.10-67 the external name takes part in declaration equality``() =
        let schema = {| VC67X = Var.string(cliArg = "a") |}
        let text =
            fails (fun () ->
                do xakeArgs [] (x.Opts (List<string>())) {
                    varschema schema
                    phony "main" (recipe { let! _ = Var.string("VC67X", cliArg = "b") in () })
                })
        StringAssert.Contains("Variable 'VC67X' is read through a Var declaration that differs from its declaration by varschema field VC67X in command-line name:", text)

    [<Test>]
    member x.``8.10-68 --help shows the external name with the key``() =
        let b = RulesBuilder(ExecOptions.Default)
        let schema = {| VC68P = Var.string(cliArg = "vc68pkg") |}
        let (XakeScript (o, _)) = b.AddVar(b.Vars(b.Zero(), schema), "VC68R", [Cli "vc68r"; Env "VC68_E"])
        let lines = XakeScriptBuilder.formatHelp o
        Assert.That(lines, Has.Some.Contains("-d vc68pkg=<value>").And.Contains("(key VC68P)"))
        Assert.That(lines, Has.Some.Contains("-d vc68r=<value>").And.Contains("(key VC68R)").And.Contains("env: VC68_E"))
