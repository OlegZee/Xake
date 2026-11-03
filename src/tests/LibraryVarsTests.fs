namespace Tests

open System
open System.Collections.Generic
open NUnit.Framework

open Xake

/// The conformance tests of docs/features/vars-precedence/pr2-spec.md §4.1 (`Var.read`), each
/// named by its spec number. Every recipe runs in a phony target with no database; the
/// environment is process-wide, so each test restores it. (The §8.8 and §4.2 tests, of the
/// variables Xake.Dotnet reads and its exported schema, live with that library in its own
/// repository, OlegZee/Xake.Dotnet.)
[<TestFixture>]
type ``Var read conformance (PR 2 spec section 4-1)``() =
    inherit XakeTestBase("libvars")

    let setEnv (name: string) (value: string) = Environment.SetEnvironmentVariable(name, value)

    /// runs `f` with the named environment variables set (null removes), restoring them after
    let withEnv (pairs: (string * string) list) f =
        let saved = [ for (n, _) in pairs -> n, Environment.GetEnvironmentVariable n ]
        for (n, v) in pairs do setEnv n v
        try f () finally for (n, v) in saved do setEnv n v

    let fails (f: unit -> unit) =
        let ex = Assert.Throws<XakeException>(fun () -> f ())
        ex.Data0

    /// options without a database and without the test runner's `-d` (`XakeTestBase`), every
    /// log line captured
    member x.Opts (logged: List<string>) =
        { x.TestOptions with
            ThrowOnError = true; FileLog = ""; ConLogLevel = Silent; NoPersist = true; Nologo = true; CliVars = []
            CustomLogger = CustomLogger (fun _ -> true) (fun s -> lock logged (fun () -> logged.Add s)) }

    /// Runs `r` in the target "main" of a script with the command line `args` and `ops` applied
    /// to the builder: the value, the dependencies and the log.
    member x.Read (args: string list) (ops: XakeScript -> XakeScript) (r: Recipe<ExecContext, 'T>) =
        let logged = List<string>()
        let seen = ref None
        let deps = ref []
        let b = xakeArgs args (x.Opts logged)
        let script =
            b.Phony(ops (b.Zero()), "main", recipe {
                let! v = r
                let! result = getResult()
                deps.Value <- result.Depends
                seen.Value <- Some v })
        b.Run script
        Option.get seen.Value, deps.Value, logged

    /// `Read`, expected to fail: the failure text.
    member x.ReadFails (args: string list) (ops: XakeScript -> XakeScript) (r: Recipe<ExecContext, 'T>) =
        fails (fun () -> x.Read args ops r |> ignore)

    // --- pr2-spec.md 4.1 Var.read ---

    [<Test>]
    member x.``pr2-69 Var.read of an undeclared key is getVar``() =
        let v, deps, _ = x.Read ["-d"; "VL69K=v"] id (Var.read<string> "VL69K")
        Assert.AreEqual(Some "v", v)
        Assert.That(deps |> List.filter (function Var ("VL69K", _) -> true | _ -> false), Is.EqualTo [Var ("VL69K", Some "v")])
        let v, deps, _ = x.Read [] id (Var.read<string> "VL69K")
        Assert.AreEqual(None, v)
        Assert.That(deps, Does.Contain(Var ("VL69K", None)))

    [<Test>]
    member x.``pr2-70 Var.read converts an undeclared value, a bad one fails``() =
        let v, _, _ = x.Read ["-d"; "VL70N=4"] id (Var.read<int> "VL70N")
        Assert.AreEqual(Some 4, v)
        let text = x.ReadFails ["-d"; "VL70N=x"] id (Var.read<int> "VL70N")
        StringAssert.Contains("Failed to convert value 'x' for variable 'VL70N' to type 'System.Int32'.", text)

    [<Test>]
    member x.``pr2-71 Var.read returns a field's typed default``() =
        let schema = {| VL71N = Var.int() |> withDefault 5 |}
        let ops (s: XakeScript) = (xakeArgs [] (x.Opts (List<string>()))).Vars(s, schema)
        let v, _, _ = x.Read [] ops (Var.read<int> "VL71N")
        Assert.AreEqual(Some 5, v)
        let v, _, _ = x.Read ["-d"; "VL71N=7"] ops (Var.read<int> "VL71N")
        Assert.AreEqual(Some 7, v)

    [<Test>]
    member x.``pr2-72 Var.read trusts the stored declaration``() =
        withEnv ["VL72_A", "a"] (fun () ->
            let schema = {| VL72P = Var.string(envVar = "VL72_A") |> withDefault "d" |}
            let ops (s: XakeScript) = (xakeArgs [] (x.Opts (List<string>()))).Vars(s, schema)
            let v, deps, _ = x.Read [] ops (Var.read<string> "VL72P")
            Assert.AreEqual(Some "a", v)
            Assert.That(deps, Does.Contain(Var ("VL72P", Some "a")))
            Assert.IsFalse(deps |> List.exists (function EnvVar _ -> true | _ -> false), "no EnvVar"))

    [<Test>]
    member x.``pr2-73 Var.read of another type fails``() =
        let schema = {| VL73N = Var.int() |}
        let ops (s: XakeScript) = (xakeArgs [] (x.Opts (List<string>()))).Vars(s, schema)
        let text = x.ReadFails ["-d"; "VL73N=4"] ops (Var.read<string> "VL73N")
        StringAssert.Contains(
            "Variable 'VL73N' is read as 'System.String' but is declared by varschema field VL73N as 'System.Int32': declare it as 'System.String'", text)

    [<Test>]
    member x.``pr2-74 Var.read of a secret records presence``() =
        withEnv ["XAKE__VL74T", "s3cr3t-value"] (fun () ->
            let schema = {| VL74T = Var.string() |> secret |}
            let ops (s: XakeScript) = (xakeArgs [] (x.Opts (List<string>()))).Vars(s, schema)
            let v, deps, _ = x.Read [] ops (Var.read<string> "VL74T")
            Assert.AreEqual(Some "s3cr3t-value", v)
            Assert.That(deps, Does.Contain(SecretDep ("VL74T", SecretArg, SecretPresent))))

    [<Test>]
    member x.``pr2-75 Var.read through a var rule``() =
        withEnv ["VL75_A", null] (fun () ->
            let ops (s: XakeScript) = (xakeArgs [] (x.Opts (List<string>()))).AddVar(s, "VL75K", [Env "VL75_A"; Default "d"])
            let v, deps, _ = x.Read [] ops (Var.read<string> "VL75K")
            Assert.AreEqual(Some "d", v)
            Assert.That(deps, Does.Contain(Var ("VL75K", Some "d"))))
