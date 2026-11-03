namespace Tests

open System
open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks

/// Secret script variables (`|> secret`, `|> secretHashed`, `Var.secret`): the database keeps a
/// digest, never the value; the loggers mask the value; `-d` is refused or warned about.
/// Every test uses its own variable names: the secret registries are process-wide.
[<TestFixture>]
type ``Secret variables``() =
    inherit XakeTestBase("secretvars")

    let setEnv (name: string) (value: string) = Environment.SetEnvironmentVariable(name, value)

    let withEnv (name: string) (value: string) f =
        setEnv name value
        try f () finally setEnv name null

    /// A secret read needs its declaration (spec T-2): the variable goes into a schema, keyed by
    /// its env name (each test has its own).
    let named (sv: OptionalVar<string>) = {| Token = { sv with Name = sv.EnvName } |}

    /// options with a persistent database, log messages captured
    member x.Opts (logged: Collections.Generic.List<string>) =
        { x.TestOptions with
            ThrowOnError = true; FileLog = ""; ResetDb = false; ConLogLevel = Silent
            CustomLogger = CustomLogger (fun _ -> true) (fun s -> lock logged (fun () -> logged.Add s)) }

    member x.Run (logged, sv: OptionalVar<string>, runs: int ref) =
        let schema = named sv
        do xakeArgs [] (x.Opts logged) {
            varschema schema
            phony "main" (recipe {
                let! _ = schema.Token
                runs.Value <- runs.Value + 1
            })
        }

    member x.RunHashed (logged, sv: OptionalVar<string>, runs: int ref) = x.Run (logged, sv |> secretHashed, runs)

    [<Test>]
    member x.``records a SecretDep, the database file does not contain the value``() =
        let logged = Collections.Generic.List<string>()
        let value = "acme-token-recorded-7f3a"
        withEnv "ACME_API_TOKEN_REC" value (fun () ->
            let schema = named (Var.secret<string>(envVar = "ACME_API_TOKEN_REC"))
            let deps = ref []
            do xakeArgs [] (x.Opts logged) {
                varschema schema
                phony "main" (recipe {
                    let! _ = schema.Token
                    let! r = getResult()
                    deps.Value <- r.Depends
                })
            }
            Assert.That(deps.Value, Does.Contain(SecretDep ("ACME_API_TOKEN_REC", SecretArg, SecretPresent)))
            Assert.IsFalse(deps.Value |> List.exists (function EnvVar _ -> true | _ -> false), "no EnvVar record")
            let bytes = File.ReadAllBytes ".xake"
            let text = Text.Encoding.UTF8.GetString bytes
            Assert.IsFalse(text.Contains value, "the value is not in the database"))

    [<Test>]
    member x.``presence only: a rotated value rebuilds nothing, unset to set rebuilds``() =
        let logged = Collections.Generic.List<string>()
        let runs = ref 0
        let name = "ACME_API_TOKEN_PRES"
        let sv = Var.secret<string>(envVar = name)
        setEnv name null
        try
            x.Run (logged, sv, runs)
            Assert.AreEqual(1, runs.Value)
            setEnv name "first-value-1234"
            x.Run (logged, sv, runs)
            Assert.AreEqual(2, runs.Value, "unset -> set rebuilds")
            Assert.That(logged, Has.Some.Contains(sprintf "Secret variable %s is now set" name))
            setEnv name "rotated-value-5678"
            x.Run (logged, sv, runs)
            Assert.AreEqual(2, runs.Value, "a rotated value rebuilds nothing")
            setEnv name null
            x.Run (logged, sv, runs)
            Assert.AreEqual(3, runs.Value, "set -> unset rebuilds")
            Assert.That(logged, Has.Some.Contains(sprintf "Secret variable %s is now unset" name))
        finally
            setEnv name null

    [<Test>]
    member x.``hashed: a rotated value rebuilds, the same value does not, no value in the reason``() =
        let logged = Collections.Generic.List<string>()
        let runs = ref 0
        let name = "LICENSE_KEY_HASHED"
        let sv = Var.secret<string>(envVar = name)
        try
            setEnv name "license-one-aaaa"
            x.RunHashed (logged, sv, runs)
            x.RunHashed (logged, sv, runs)
            Assert.AreEqual(1, runs.Value, "same value: up to date")
            setEnv name "license-two-bbbb"
            x.RunHashed (logged, sv, runs)
            Assert.AreEqual(2, runs.Value, "rotated value rebuilds")
            Assert.That(logged, Has.Some.Contains(sprintf "Secret variable %s changed" name))
            Assert.That(logged, Has.None.Contains "license-one-aaaa")
            Assert.That(logged, Has.None.Contains "license-two-bbbb")
            let text = Text.Encoding.UTF8.GetString(File.ReadAllBytes ".xake")
            Assert.IsFalse(text.Contains "license-two-bbbb")
        finally
            setEnv name null

    [<Test>]
    member x.``a plain getEnv of a name declared secret records a SecretDep``() =
        let logged = Collections.Generic.List<string>()
        let name = "ACME_API_TOKEN_PLAIN"
        let schema = {| Token = Var.secret<string>(envVar = name) |}
        withEnv name "plain-getenv-value-99" (fun () ->
            let deps = ref []
            do xake (x.Opts logged) {
                varschema schema
                phony "main" (recipe {
                    let! _ = getEnv name
                    let! r = getResult()
                    deps.Value <- r.Depends
                })
            }
            Assert.AreEqual([SecretDep (name, SecretEnv, SecretPresent)], deps.Value))

    [<Test>]
    member x.``a 0.5 database with the clear value loads, is upgraded and scrubbed, no spurious rebuild``() =
        let logged = Collections.Generic.List<string>()
        let name = "ACME_API_TOKEN_OLD"
        let value = "old-clear-value-0001"
        let schema = named (Var.secret<string>(envVar = name))
        let old =
            { BuildLog.makeResult [PhonyAction "main"] with
                Depends = [ EnvVar (name, Some value); EnvVar ("XAKE_UNRELATED_OLD", None) ] }
        OldDatabase.write ".xake" "0.5" [old]
        withEnv name "a-rotated-value-0002" (fun () ->
            let runs = ref 0
            do xake (x.Opts logged) {
                varschema schema
                phony "main" (recipe {
                    let! _ = schema.Token
                    let! _ = getEnv "XAKE_UNRELATED_OLD"
                    runs.Value <- runs.Value + 1
                })
            }
            Assert.AreEqual(0, runs.Value, "presence unchanged: up to date")
            Assert.That(logged, Has.Some.Contains "Upgrading database")
            let ver, results = OldDatabase.read ".xake"
            Assert.AreEqual(BuildLog.XakeDbVersion, ver)
            Assert.AreEqual("0.6", ver)
            let deps = results |> List.collect (fun r -> r.Depends)
            Assert.That(deps, Does.Contain(SecretDep (name, SecretEnv, SecretPresent)))
            Assert.That(deps, Does.Contain(EnvVar ("XAKE_UNRELATED_OLD", None)))
            let text = Text.Encoding.UTF8.GetString(File.ReadAllBytes ".xake")
            Assert.IsFalse(text.Contains value, "the clear value is removed from the file"))

    [<Test>]
    member x.``a 0.4 database without secrets still loads``() =
        let logged = Collections.Generic.List<string>()
        let old =
            { BuildLog.makeResult [PhonyAction "main"] with Depends = [ EnvVar ("XAKE_OLD_04_VAR", None) ] }
        OldDatabase.write ".xake" "0.4" [old]
        let runs = ref 0
        do xake (x.Opts logged) {
            phony "main" (recipe {
                let! _ = getEnv "XAKE_OLD_04_VAR"
                runs.Value <- runs.Value + 1
            })
        }
        Assert.AreEqual(0, runs.Value)
        Assert.AreEqual(BuildLog.XakeDbVersion, OldDatabase.read ".xake" |> fst)

    [<Test>]
    member x.``persists every SecretDep digest``() =
        let deps =
            [ SecretDep ("A_SECRET", SecretEnv, SecretAbsent)
              SecretDep ("B_SECRET", SecretArg, SecretPresent)
              SecretDep ("C_SECRET", SecretEnv, SecretHmac ([| 1uy; 2uy; 3uy |], Array.init 32 byte)) ]
        let result = { BuildLog.makeResult [PhonyAction "x"] with Depends = deps }
        use stream = new MemoryStream()
        use w = new BinaryWriter(stream)
        Storage.resultPU.pickle result w
        stream.Position <- 0L
        use r = new BinaryReader(stream)
        Assert.AreEqual(result, Storage.resultPU.unpickle r)

    [<Test>]
    member x.``--dryrun shows the reason without the value``() =
        let logged = Collections.Generic.List<string>()
        let name = "LICENSE_KEY_DRY"
        let sv = Var.secret<string>(envVar = name) |> secretHashed
        let runs = ref 0
        try
            setEnv name "dryrun-value-one-11"
            x.Run (logged, sv, runs)
            setEnv name "dryrun-value-two-22"
            logged.Clear()
            let schema = named sv
            do xakeArgs [] { x.Opts logged with DryRun = true } {
                varschema schema
                phony "main" (recipe { let! _ = schema.Token in () })
            }
            Assert.That(logged, Has.Some.Contains(sprintf "Reason: Secret variable %s changed" name))
            Assert.That(logged, Has.None.Contains "dryrun-value")
        finally
            setEnv name null

    [<Test>]
    member x.``--dump shows the state without the value``() =
        let logged = Collections.Generic.List<string>()
        let name = "LICENSE_KEY_DUMP"
        let sv = Var.secret<string>(envVar = name)
        let runs = ref 0
        try
            setEnv name "dump-value-one-33"
            x.Run (logged, sv, runs)
            // an old-style clear record too: shown masked
            let out = new StringWriter()
            let stdout = Console.Out
            Console.SetOut out
            try
                let schema = named sv
                do xakeArgs [] { x.Opts logged with DumpDeps = true } {
                    varschema schema
                    phony "main" (recipe { let! _ = schema.Token in () })
                }
            finally
                Console.SetOut stdout
            let text = out.ToString()
            StringAssert.Contains(sprintf "Secret Script var: '%s' = set" name, text)
            StringAssert.DoesNotContain("dump-value-one-33", text)
        finally
            setEnv name null

    [<Test>]
    member x.``the shell envs Debug dump masks the value``() =
        let logged = Collections.Generic.List<string>()
        let name = "ACME_API_TOKEN_SHELL"
        let value = "shell-secret-value-4242"
        withEnv name value (fun () ->
            let schema = {| Token = { Var.secret<string>(envVar = name) with Name = Some name } |> required |}
            do xakeArgs [] { x.Opts logged with NoPersist = true } {
                varschema schema
                phony "main" (recipe {
                    let! token = schema.Token
                    do! sh "dotnet" { arg "--version"; envs [name, token] }
                })
            }
            Assert.That(logged, Has.Some.Contains "[shell] settings")
            Assert.That(logged, Has.Some.Contains "***")
            Assert.That(logged, Has.None.Contains value))

    [<Test>]
    member x.``a conversion error does not show the value``() =
        let name = "ACME_PORT_SECRET"
        withEnv name "not-a-number-secret" (fun () ->
            let schema = {| Token = { Var.secret<int>(envVar = name) with Name = Some name } |> required |}
            let ex =
                Assert.Throws<XakeException>(fun () ->
                    do xakeArgs [] { x.TestOptions with ThrowOnError = true; FileLog = ""; NoPersist = true } {
                        varschema schema
                        phony "main" (recipe { let! _ = schema.Token in () })
                    })
            StringAssert.Contains("secret variable", ex.Data0)
            StringAssert.DoesNotContain("not-a-number-secret", ex.Data0))

    [<Test>]
    member x.``the masking registry masks values of 4 characters or more``() =
        SecretMask.register "abc"
        SecretMask.register "wxyz-mask-test"
        Assert.AreEqual("abc and ***", SecretMask.apply "abc and wxyz-mask-test")
        let logged = Collections.Generic.List<string>()
        let logger = CustomLogger (fun _ -> true) logged.Add
        logger.Log Info "value: %s" "wxyz-mask-test"
        Assert.AreEqual("[INF] value: ***", logged.[0])

    [<Test>]
    member x.``-d for an environment-only secret is refused``() =
        let schema = {| AcmeApiToken = Var.secret<string>(envVar = "ACME_API_TOKEN_REFUSE") |}
        let ex =
            Assert.Throws<XakeException>(fun () ->
                do xakeArgs ["-d"; "ACME_API_TOKEN_REFUSE=given-on-cli-555"] { x.TestOptions with ThrowOnError = true; FileLog = ""; NoPersist = true } {
                    varschema schema
                    phony "main" (recipe { let! _ = schema.AcmeApiToken in () })
                })
        StringAssert.Contains("secret variables come from the environment", ex.Data0)
        StringAssert.DoesNotContain("given-on-cli-555", ex.Data0)

    [<Test>]
    member x.``an inline secret read outside the schema is not declared``() =   // spec T-4
        let sv = Var.secret<string>(envVar = "ACME_API_TOKEN_REFUSE2")
        let ex =
            Assert.Throws<XakeException>(fun () ->
                do xakeArgs ["-d"; "ACME_API_TOKEN_REFUSE2=given-on-cli-666"] { x.TestOptions with ThrowOnError = true; FileLog = ""; NoPersist = true } {
                    phony "main" (recipe { let! _ = sv in () })
                })
        StringAssert.Contains("is read through Var but is not declared", ex.Data0)
        StringAssert.DoesNotContain("given-on-cli-666", ex.Data0)

    [<Test>]
    member x.``-d for an argument secret is refused``() =   // spec X-5: the 3.13 warning is an error now
        let logged = Collections.Generic.List<string>()
        let schema = {| licenseKey = Var.string(cliArg = "licenseKey", envVar = "LICENSE_KEY_ARG") |> secret |}
        let ex =
            Assert.Throws<XakeException>(fun () ->
                do xakeArgs ["-d"; "licenseKey=cli-license-777"] { x.Opts logged with NoPersist = true } {
                    varschema schema
                    phony "main" (recipe { let! _ = schema.licenseKey in () })
                })
        StringAssert.Contains("Variable 'licenseKey' is secret: secret variables come from the environment, set LICENSE_KEY_ARG instead of passing -d licenseKey=...", ex.Data0)
        Assert.That(logged, Has.None.Contains "visible in the process list")
        Assert.That(logged, Has.None.Contains "cli-license-777")

    [<Test>]
    member x.``--help shows the name and description, never a value or default``() =
        let schema =
            {| AcmeApiToken = Var.secret<string>(envVar = "ACME_API_TOKEN_HELP", description = "the API token")
               licenseKey = Var.string(cliArg = "licenseKey") |> secret |> withDefault "default-license-888"
               plain = Var.string(cliArg = "plain") |> withDefault "visible" |}
        let options =
            let builder = RulesBuilder(ExecOptions.Default)
            builder.Vars(builder.Zero(), schema) |> fun (XakeScript (o, _)) -> o
        let lines = XakeScriptBuilder.formatVarsHelp options.VarSchema
        let text = String.concat "\n" lines
        StringAssert.Contains("$ACME_API_TOKEN_HELP [string] (secret) — the API token", text)
        StringAssert.Contains("-d licenseKey=<value> [string] (secret)", text)
        StringAssert.DoesNotContain("default-license-888", text)
        StringAssert.Contains("default: \"visible\"", text)
