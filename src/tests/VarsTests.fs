module ``Vars feature``

open NUnit.Framework
open Xake

let DebugOptions = { ExecOptions.Default with ThrowOnError = true; FileLog = ""; Nologo = true; NoPersist = true }

[<Test>]
let ``withDefault returns default when no CLI arg``() =
    let myVar = Var.create(cliArg = "config") |> withDefault "Debug"
    let result = ref ""

    do xake DebugOptions {
        phony "main" (recipe {
            let! v = myVar
            result := v
        })
    }

    Assert.AreEqual("Debug", !result)

[<Test>]
let ``withDefault with int returns typed default``() =
    let myVar = Var.create<int>(cliArg = "count") |> withDefault 42
    let result = ref 0

    do xake DebugOptions {
        phony "main" (recipe {
            let! v = myVar
            result := v
        })
    }

    Assert.AreEqual(42, !result)

[<Test>]
let ``envVar param resolves from environment variable``() =
    System.Environment.SetEnvironmentVariable("XAKE_TEST_VAR_ENV", "33")
    try
        let myVar1 = Var.string(envVar = "XAKE_TEST_VAR_ENV") |> required
        let myVar = Var.create<int>(envVar = "XAKE_TEST_VAR_ENV") |> required
        myVar.FieldName <- "testVar"
        let result = ref 0
        let result1 = ref ""

        do xake DebugOptions {
            phony "main" (recipe {
                let! v = myVar
                let! v1 = myVar1
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
    let myVar = Var.create<int>(envVar = "XAKE_MISSING_VAR_99999") |> withDefault 42
    myVar.FieldName <- "count"
    let result = ref 0

    do xake DebugOptions {
        phony "main" (recipe {
            let! v = myVar
            result := v
        })
    }

    Assert.AreEqual(42, !result)

[<Test>]
let ``CLI arg overrides default``() =
    let myVar = Var.create(cliArg = "config") |> withDefault "Debug"
    let result = ref ""

    do xakeArgs ["-d"; "config=Release"] DebugOptions {
        phony "main" (recipe {
            let! v = myVar
            result := v
        })
    }

    Assert.AreEqual("Release", !result)

[<Test>]
let ``CLI arg overrides env var``() =
    System.Environment.SetEnvironmentVariable("XAKE_TEST_CLI_WINS", "from-env")
    try
        let myVar = Var.create(cliArg = "cliWins", envVar = "XAKE_TEST_CLI_WINS") |> required
        let result = ref ""

        do xakeArgs ["-d"; "cliWins=from-cli"] DebugOptions {
            phony "main" (recipe {
                let! v = myVar
                result := v
            })
        }

        Assert.AreEqual("from-cli", !result)
    finally
        System.Environment.SetEnvironmentVariable("XAKE_TEST_CLI_WINS", null)

[<Test>]
let ``required fails at execution time when not provided``() =
    let myVar = Var.create(cliArg = "missingRequired") |> required

    Assert.Throws<XakeException>(fun () ->
        xake DebugOptions {
            phony "main" (recipe {
                let! _ = myVar
                ()
            })
        }
    ) |> ignore

[<Test>]
let ``envVar param required fails when value is absent``() =
    let myVar = Var.create(envVar = "XAKE_MISSING_VAR_REQUIRED_99999") |> required
    myVar.FieldName <- "requiredEnv"

    Assert.Throws<XakeException>(fun () ->
        xake DebugOptions {
            phony "main" (recipe {
                let! _ = myVar
                ()
            })
        }
    ) |> ignore

[<Test>]
let ``cliArg param sets CLI arg name for lookup``() =
    let myVar = Var.create(cliArg = "my-ext-name") |> withDefault "fallback"
    let result = ref ""

    do xakeArgs ["-d"; "my-ext-name=provided"] DebugOptions {
        phony "main" (recipe {
            let! v = myVar
            result := v
        })
    }

    Assert.AreEqual("provided", !result)

[<Test>]
let ``OptionalVar returns None when both CLI arg and env var are absent``() =
    let myVar = Var.create(cliArg = "absentVar")
    let result = ref (Some "sentinel")

    do xake DebugOptions {
        phony "main" (recipe {
            let! v = myVar
            result := v
        })
    }

    Assert.AreEqual(None, !result)

[<Test>]
let ``RequiredVar throws when absent``() =
    let myVar = Var.create(cliArg = "namedMissing") |> required

    Assert.Throws<XakeException>(fun () ->
        xake DebugOptions {
            phony "main" (recipe {
                let! _ = myVar
                ()
            })
        }
    ) |> ignore

[<Test>]
let ``auto-env: buildConfig resolves from BUILD_CONFIG env var``() =
    System.Environment.SetEnvironmentVariable("BUILD_CONFIG", "Release")
    try
        let myVar = Var.create<string>(cliArg = "buildConfig")
        let result = ref None

        do xake DebugOptions {
            phony "main" (recipe {
                let! v = myVar
                result := v
            })
        }

        Assert.AreEqual(Some "Release", !result)
    finally
        System.Environment.SetEnvironmentVariable("BUILD_CONFIG", null)

[<Test>]
let ``auto-env: apiKey resolves from API_KEY env var``() =
    System.Environment.SetEnvironmentVariable("API_KEY", "secret")
    try
        let myVar = Var.create<string>(cliArg = "apiKey")
        let result = ref None

        do xake DebugOptions {
            phony "main" (recipe {
                let! v = myVar
                result := v
            })
        }

        Assert.AreEqual(Some "secret", !result)
    finally
        System.Environment.SetEnvironmentVariable("API_KEY", null)

[<Test>]
let ``vars sets FieldName from anonymous record property name``() =
    let vars = {| myConfig = Var.create() |> withDefault "Debug" |}
    let result = ref ""

    do xake DebugOptions {
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
        apiKey = Var.create() |> required
    |}

    do xake DebugOptions {
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

    do xake DebugOptions {
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
let ``Var.create with cliArg sets FieldName directly``() =
    let myVar = Var.create(cliArg = "config") |> withDefault "Debug"
    let result = ref ""

    do xake DebugOptions {
        phony "main" (recipe {
            let! v = myVar
            result := v
        })
    }

    Assert.AreEqual("config", myVar.FieldName)
    Assert.AreEqual("Debug", !result)

[<Test>]
let ``Var.create with envVar arg resolves from that env var``() =
    System.Environment.SetEnvironmentVariable("MY_CUSTOM_VAR", "from-env-custom")
    try
        let myVar = Var.create(envVar = "MY_CUSTOM_VAR") |> required
        myVar.FieldName <- "dummy"
        let result = ref ""

        do xake DebugOptions {
            phony "main" (recipe {
                let! v = myVar
                result := v
            })
        }

        Assert.AreEqual("from-env-custom", !result)
    finally
        System.Environment.SetEnvironmentVariable("MY_CUSTOM_VAR", null)

[<Test>]
let ``Var.create with cliArg and envVar args combines both``() =
    let myVar = Var.create(cliArg = "myField", envVar = "MY_COMBINED_VAR")
    Assert.AreEqual("myField", myVar.FieldName)
    Assert.AreEqual(Some "MY_COMBINED_VAR", myVar.EnvName)

[<Test>]
let ``Var.create int with cliArg resolves typed value from CLI``() =
    let myVar = Var.create<int>(cliArg = "count") |> withDefault 0
    let result = ref 0

    do xakeArgs ["-d"; "count=7"] DebugOptions {
        phony "main" (recipe {
            let! v = myVar
            result := v
        })
    }

    Assert.AreEqual(7, !result)

[<Test>]
let ``Var dependency is recorded when resolving``() =
    let myVar = Var.create(cliArg = "config") |> withDefault "Debug"
    let capturedDepends = ref []

    do xake DebugOptions {
        phony "main" (recipe {
            let! _ = myVar
            let! result = getResult()
            capturedDepends := result.Depends
        })
    }

    let hasVarDep =
        !capturedDepends |> List.exists (function
            | Var ("config", _) -> true
            | _ -> false)
    Assert.IsTrue(hasVarDep, "Expected Var(\"config\", _) dependency to be recorded")

[<Test>]
let ``Var.env reads from env var``() =
    System.Environment.SetEnvironmentVariable("XAKE_SCOPE_ENV", "from-env")
    try
        let myVar = Var.env(envVar = "XAKE_SCOPE_ENV") |> required
        myVar.FieldName <- "scopeTest"
        let result = ref ""

        do xake DebugOptions {
            phony "main" (recipe {
                let! v = myVar
                result := v
            })
        }

        Assert.AreEqual("from-env", !result)
    finally
        System.Environment.SetEnvironmentVariable("XAKE_SCOPE_ENV", null)

[<Test>]
let ``Var.env ignores CLI arg``() =
    System.Environment.SetEnvironmentVariable("MY_TOKEN", "from-env")
    try
        let vars = {|
            myToken = Var.env()
        |}
        let result = ref (Some "sentinel")

        do xakeArgs ["-d"; "myToken=from-cli"] DebugOptions {
            varschema vars
            phony "main" (recipe {
                let! v = vars.myToken
                result := v
            })
        }

        Assert.AreEqual(Some "from-env", !result)
    finally
        System.Environment.SetEnvironmentVariable("MY_TOKEN", null)

[<Test>]
let ``Var.arg reads from CLI arg``() =
    let myVar = Var.arg<int>(cliArg = "count") |> withDefault 0
    let result = ref 0

    do xakeArgs ["-d"; "count=7"] DebugOptions {
        phony "main" (recipe {
            let! v = myVar
            result := v
        })
    }

    Assert.AreEqual(7, !result)

[<Test>]
let ``Var.arg ignores env var``() =
    System.Environment.SetEnvironmentVariable("PATH", "system-path")
    try
        let myVar = Var.arg(cliArg = "path") |> withDefault "default"
        let result = ref ""

        do xake DebugOptions {
            phony "main" (recipe {
                let! v = myVar
                result := v
            })
        }

        Assert.AreEqual("default", !result)
    finally
        ()
