module ``Vars feature``

open NUnit.Framework
open Xake

// A Var read needs a declaration of its key (spec T-2..T-4): the reads below go through
// `varschema` fields; inline reads are covered by VarsConformanceTests (E-7, E-8).

let DebugOptions = { ExecOptions.Default with ThrowOnError = true; FileLog = ""; Nologo = true; NoPersist = true }

[<Test>]
let ``withDefault returns default when no CLI arg``() =
    let vars = {| config = Var.create(cliArg = "config") |> withDefault "Debug" |}
    let result = ref ""

    do xakeArgs [] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! v = vars.config
            result := v
        })
    }

    Assert.AreEqual("Debug", !result)

[<Test>]
let ``withDefault with int returns typed default``() =
    let vars = {| count = Var.create<int>(cliArg = "count") |> withDefault 42 |}
    let result = ref 0

    do xakeArgs [] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! v = vars.count
            result := v
        })
    }

    Assert.AreEqual(42, !result)

[<Test>]
let ``envVar param resolves from environment variable``() =
    System.Environment.SetEnvironmentVariable("XAKE_TEST_VAR_ENV", "33")
    try
        let vars = {|
            testVar = Var.create<int>(envVar = "XAKE_TEST_VAR_ENV") |> required
            testVar1 = Var.string(envVar = "XAKE_TEST_VAR_ENV") |> required
        |}
        let result = ref 0
        let result1 = ref ""

        do xakeArgs [] DebugOptions {
            varschema vars
            phony "main" (recipe {
                let! v = vars.testVar
                let! v1 = vars.testVar1
                result := v
                result1 := v1
            })
        }

        Assert.AreEqual(33, !result)
        Assert.AreEqual("33", !result1)
    finally
        System.Environment.SetEnvironmentVariable("XAKE_TEST_VAR_ENV", null)

[<Test>]
let ``envVar param withDefault returns typed default when neither CLI nor env is set``() =
    let vars = {| count = Var.create<int>(envVar = "XAKE_MISSING_VAR_99999") |> withDefault 42 |}
    let result = ref 0

    do xakeArgs [] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! v = vars.count
            result := v
        })
    }

    Assert.AreEqual(42, !result)

[<Test>]
let ``CLI arg overrides default``() =
    let vars = {| config = Var.create(cliArg = "config") |> withDefault "Debug" |}
    let result = ref ""

    do xakeArgs ["-d"; "config=Release"] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! v = vars.config
            result := v
        })
    }

    Assert.AreEqual("Release", !result)

[<Test>]
let ``CLI arg overrides env var``() =
    System.Environment.SetEnvironmentVariable("XAKE_TEST_CLI_WINS", "from-env")
    try
        let vars = {| cliWins = Var.create(cliArg = "cliWins", envVar = "XAKE_TEST_CLI_WINS") |> required |}
        let result = ref ""

        do xakeArgs ["-d"; "cliWins=from-cli"] DebugOptions {
            varschema vars
            phony "main" (recipe {
                let! v = vars.cliWins
                result := v
            })
        }

        Assert.AreEqual("from-cli", !result)
    finally
        System.Environment.SetEnvironmentVariable("XAKE_TEST_CLI_WINS", null)

[<Test>]
let ``required fails at launch when not provided``() =
    let vars = {| missingRequired = Var.create(cliArg = "missingRequired") |> required |}
    let ran = ref false

    let ex =
        Assert.Throws<XakeException>(fun () ->
            xakeArgs [] DebugOptions {
                varschema vars
                phony "main" (recipe { ran := true })
            }
        )
    StringAssert.Contains("Required variable 'missingRequired' is not provided", ex.Data0)
    Assert.IsFalse(!ran, "no target runs")

[<Test>]
let ``envVar param required fails when value is absent``() =
    let vars = {| requiredEnv = Var.create(envVar = "XAKE_MISSING_VAR_REQUIRED_99999") |> required |}

    Assert.Throws<XakeException>(fun () ->
        xakeArgs [] DebugOptions {
            varschema vars
            phony "main" (recipe { () })
        }
    ) |> ignore

[<Test>]
let ``cliArg param sets CLI arg name for lookup``() =
    let vars = {| myExt = Var.create(cliArg = "my-ext-name") |> withDefault "fallback" |}
    let result = ref ""

    do xakeArgs ["-d"; "my-ext-name=provided"] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! v = vars.myExt
            result := v
        })
    }

    Assert.AreEqual("provided", !result)

[<Test>]
let ``OptionalVar returns None when both CLI arg and env var are absent``() =
    let vars = {| absentVar = Var.create(cliArg = "absentVar") |}
    let result = ref (Some "sentinel")

    do xakeArgs [] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! v = vars.absentVar
            result := v
        })
    }

    Assert.AreEqual(None, !result)

[<Test>]
let ``auto-env: buildConfig resolves from BUILD_CONFIG env var derived from name``() =
    System.Environment.SetEnvironmentVariable("BUILD_CONFIG", "Release")
    try
        let vars = {| cfg = Var.create<string>(name = "buildConfig") |}
        let result = ref None

        do xakeArgs [] DebugOptions {
            varschema vars
            phony "main" (recipe {
                let! v = vars.cfg
                result := v
            })
        }

        Assert.AreEqual(Some "Release", !result)
    finally
        System.Environment.SetEnvironmentVariable("BUILD_CONFIG", null)

[<Test>]
let ``name param derives env while cliArg overrides CLI lookup``() =
    System.Environment.SetEnvironmentVariable("LOGICAL_NAME", "from-env")
    try
        let vars = {| logical = Var.create<string>(name = "logicalName", cliArg = "externalName") |}
        let fromEnv = ref None
        let fromCli = ref None

        do xakeArgs [] DebugOptions {
            varschema vars
            phony "main" (recipe {
                let! v = vars.logical
                fromEnv := v
            })
        }

        do xakeArgs ["-d"; "externalName=from-cli"] DebugOptions {
            varschema vars
            phony "main" (recipe {
                let! v = vars.logical
                fromCli := v
            })
        }

        Assert.AreEqual(Some "from-env", !fromEnv)
        Assert.AreEqual(Some "from-cli", !fromCli)
    finally
        System.Environment.SetEnvironmentVariable("LOGICAL_NAME", null)

[<Test>]
let ``vars sets FieldName from anonymous record property name``() =
    let vars = {| myConfig = Var.create() |> withDefault "Debug" |}
    let result = ref ""

    do xakeArgs [] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! v = vars.myConfig
            result := v
        })
    }

    Assert.AreEqual("Debug", !result)
    Assert.AreEqual("myConfig", vars.myConfig.FieldName)

[<Test>]
let ``vars sets FieldName on multiple schema entries``() =
    let schema = {|
        buildConfig = Var.create() |> withDefault "Debug"
        apiKey = Var.create() |> withDefault "none"
    |}

    do xakeArgs [] DebugOptions {
        varschema schema
        phony "main" (recipe { () })
    }

    Assert.AreEqual("buildConfig", schema.buildConfig.FieldName)
    Assert.AreEqual("apiKey", schema.apiKey.FieldName)

[<Test>]
let ``two plain vars in schema each get their own FieldName``() =
    let schema = {|
        buildConfig = Var.create()
        apiKey      = Var.create()
    |}

    do xakeArgs [] DebugOptions {
        varschema schema
        phony "main" (recipe { () })
    }

    Assert.AreEqual("buildConfig", schema.buildConfig.FieldName)
    Assert.AreEqual("apiKey",      schema.apiKey.FieldName)

[<Test>]
let ``two plain vars in schema resolve independently from CLI args``() =
    let vars = {|
        buildConfig = Var.create<string>()
        apiKey      = Var.create<string>()
    |}
    let config = ref None
    let key    = ref None

    do xakeArgs ["-d"; "buildConfig=Release"; "-d"; "apiKey=secret"] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! c = vars.buildConfig
            let! k = vars.apiKey
            config := c
            key    := k
        })
    }

    Assert.AreEqual(Some "Release", !config)
    Assert.AreEqual(Some "secret",  !key)

[<Test>]
let ``varschema preserves explicit cliArg for lookup and FieldName``() =
    let vars = {|
        myAlias = Var.create(cliArg = "originalName") |> withDefault "default"
    |}
    let result = ref ""

    do xakeArgs ["-d"; "originalName=expected"] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! v = vars.myAlias
            result := v
        })
    }

    Assert.AreEqual("expected", !result)
    Assert.AreEqual("myAlias", vars.myAlias.FieldName)

[<Test>]
let ``varschema uses explicit name for derived CLI and env lookup``() =
    System.Environment.SetEnvironmentVariable("BUILD_CONFIG", "Release")
    try
        let vars = {|
            myAlias = Var.create<string>(name = "buildConfig")
        |}
        let fromEnv = ref None
        let fromCli = ref None

        do xakeArgs [] DebugOptions {
            varschema vars
            phony "main" (recipe {
                let! v = vars.myAlias
                fromEnv := v
            })
        }

        do xakeArgs ["-d"; "buildConfig=Debug"] DebugOptions {
            varschema vars
            phony "main" (recipe {
                let! v = vars.myAlias
                fromCli := v
            })
        }

        Assert.AreEqual(Some "Release", !fromEnv)
        Assert.AreEqual(Some "Debug", !fromCli)
        Assert.AreEqual("myAlias", vars.myAlias.FieldName)
    finally
        System.Environment.SetEnvironmentVariable("BUILD_CONFIG", null)

[<Test>]
let ``varschema mixes explicit cliArg names with implicit property names``() =
    let vars = {|
        config = Var.create(name = "logicalConfig", cliArg = "buildConfig") |> withDefault "Release"
        outputDir = Var.create() |> withDefault "bin"
        accessToken = Var.create(name = "tokenName", cliArg = "apiToken") |> required
    |}
    let config = ref ""
    let outputDir = ref ""
    let accessToken = ref ""

    do xakeArgs ["-d"; "buildConfig=Debug"; "-d"; "outputDir=dist"; "-d"; "apiToken=secret123"] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! currentConfig = vars.config
            let! currentOutputDir = vars.outputDir
            let! currentAccessToken = vars.accessToken
            config := currentConfig
            outputDir := currentOutputDir
            accessToken := currentAccessToken
        })
    }

    Assert.AreEqual("Debug", !config)
    Assert.AreEqual("dist", !outputDir)
    Assert.AreEqual("secret123", !accessToken)
    Assert.AreEqual("config", vars.config.FieldName)
    Assert.AreEqual("outputDir", vars.outputDir.FieldName)
    Assert.AreEqual("accessToken", vars.accessToken.FieldName)

[<Test>]
let ``Var.create with name sets Name directly``() =
    let myVar = Var.create(name = "config") |> withDefault "Debug"
    let vars = {| cfg = myVar |}
    let result = ref ""

    do xakeArgs [] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! v = vars.cfg
            result := v
        })
    }

    Assert.AreEqual(Some "config", myVar.Name)
    Assert.AreEqual("Debug", !result)

[<Test>]
let ``Var.create with envVar arg resolves from that env var``() =
    System.Environment.SetEnvironmentVariable("MY_CUSTOM_VAR", "from-env-custom")
    try
        let vars = {| dummy = Var.create(envVar = "MY_CUSTOM_VAR") |> required |}
        let result = ref ""

        do xakeArgs [] DebugOptions {
            varschema vars
            phony "main" (recipe {
                let! v = vars.dummy
                result := v
            })
        }

        Assert.AreEqual("from-env-custom", !result)
    finally
        System.Environment.SetEnvironmentVariable("MY_CUSTOM_VAR", null)

[<Test>]
let ``Var.create with cliArg and envVar args combines both``() =
    let myVar = Var.create(cliArg = "myField", envVar = "MY_COMBINED_VAR")
    Assert.AreEqual(Some "myField", myVar.CliArgName)
    Assert.AreEqual(Some "MY_COMBINED_VAR", myVar.EnvName)

[<Test>]
let ``Var.create int with cliArg resolves typed value from CLI``() =
    let vars = {| count = Var.create<int>(cliArg = "count") |> withDefault 0 |}
    let result = ref 0

    do xakeArgs ["-d"; "count=7"] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! v = vars.count
            result := v
        })
    }

    Assert.AreEqual(7, !result)

[<Test>]
let ``Var dependency is recorded when resolving``() =
    let vars = {| config = Var.create(cliArg = "config") |> withDefault "Debug" |}
    let capturedDepends = ref []

    do xakeArgs [] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! _ = vars.config
            let! result = getResult()
            capturedDepends := result.Depends
        })
    }

    Assert.That(!capturedDepends, Does.Contain(Var ("config", Some "Debug")))

[<Test>]
let ``EnvVar dependency is NOT recorded when CLI var is set``() =
    let vars = {| config = Var.create(cliArg = "config", envVar = "MY_CONFIG") |}
    let capturedDepends = ref []

    do xakeArgs ["-d"; "config=Release"] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! _ = vars.config
            let! result = getResult()
            capturedDepends := result.Depends
        })
    }

    let hasEnvDep =
        !capturedDepends |> List.exists (function
            | EnvVar ("MY_CONFIG", _) -> true
            | _ -> false)
    Assert.IsFalse(hasEnvDep, "EnvVar dependency should not be recorded when CLI var is set")

[<Test>]
let ``a Var read records the value, not an EnvVar, when CLI var is absent``() =
    let vars = {| config = Var.create(cliArg = "config", envVar = "MY_CONFIG") |}
    let capturedDepends = ref []

    do xakeArgs [] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! _ = vars.config
            let! result = getResult()
            capturedDepends := result.Depends
        })
    }

    Assert.AreEqual([Var ("config", None)], !capturedDepends)

[<Test>]
let ``Var.env reads from env var``() =
    System.Environment.SetEnvironmentVariable("XAKE_SCOPE_ENV", "from-env")
    try
        let vars = {| scopeTest = Var.env(envVar = "XAKE_SCOPE_ENV") |> required |}
        let result = ref ""

        do xakeArgs [] DebugOptions {
            varschema vars
            phony "main" (recipe {
                let! v = vars.scopeTest
                result := v
            })
        }

        Assert.AreEqual("from-env", !result)
    finally
        System.Environment.SetEnvironmentVariable("XAKE_SCOPE_ENV", null)

[<Test>]
let ``Var.env refuses a CLI arg``() =
    System.Environment.SetEnvironmentVariable("MY_TOKEN", "from-env")
    try
        let vars = {|
            myToken = Var.env()
        |}

        let ex =
            Assert.Throws<XakeException>(fun () ->
                xakeArgs ["-d"; "myToken=from-cli"] DebugOptions {
                    varschema vars
                    phony "main" (recipe { let! _ = vars.myToken in () })
                })
        StringAssert.Contains("Variable 'myToken' comes only from the environment variable MY_TOKEN, not from -d myToken=...", ex.Data0)
    finally
        System.Environment.SetEnvironmentVariable("MY_TOKEN", null)

[<Test>]
let ``Var.arg reads from CLI arg``() =
    let vars = {| count = Var.arg<int>(cliArg = "count") |> withDefault 0 |}
    let result = ref 0

    do xakeArgs ["-d"; "count=7"] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! v = vars.count
            result := v
        })
    }

    Assert.AreEqual(7, !result)

[<Test>]
let ``Var.arg ignores env var``() =
    System.Environment.SetEnvironmentVariable("EXT_MY_PATH", "system-path")
    try
        let vars = {| extMyPath = Var.arg(cliArg = "extMyPath") |> withDefault "default" |}
        let result = ref ""

        do xakeArgs [] DebugOptions {
            varschema vars
            phony "main" (recipe {
                let! v = vars.extMyPath
                result := v
            })
        }

        Assert.AreEqual("default", !result)
    finally
        System.Environment.SetEnvironmentVariable("EXT_MY_PATH", null)

[<Test>]
let ``a field reads the -d value; a var op of its key is a second definition``() =
    let vars = {| pinned = Var.string(cliArg = "pinned") |> withDefault "default" |}
    let result = ref ""

    do xakeArgs ["-d"; "pinned=from-cli"] DebugOptions {
        varschema vars
        phony "main" (recipe {
            let! v = vars.pinned
            result := v
        })
    }

    Assert.AreEqual("from-cli", !result)
    let ex =
        Assert.Throws<XakeException>(fun () ->
            xakeArgs ["-d"; "pinned=from-cli"] DebugOptions {
                var "pinned" "from-script"
                varschema vars
                phony "main" (recipe { () })
            })
    StringAssert.Contains("Variable 'pinned' is defined twice in the script", ex.Data0)
