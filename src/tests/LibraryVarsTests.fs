namespace Tests

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open NUnit.Framework

open Xake
open Xake.Dotnet

/// The conformance tests of docs/features/vars-precedence/spec.md §8.8 (Xake.Dotnet reads its
/// variables from the variable set only) and of pr2-spec.md §4 (`Var.read`, the exported schema
/// `DotNetVars`, importing it); each test is named by its spec number. Every recipe runs in a
/// phony target with no database; the environment is process-wide, so each test restores it.
[<TestFixture>]
type ``Library vars conformance (spec section 8-8, PR 2 spec)``() =
    inherit XakeTestBase("libvars")

    let setEnv (name: string) (value: string) = Environment.SetEnvironmentVariable(name, value)

    /// runs `f` with the named environment variables set (null removes), restoring them after
    let withEnv (pairs: (string * string) list) f =
        let saved = [ for (n, _) in pairs -> n, Environment.GetEnvironmentVariable n ]
        for (n, v) in pairs do setEnv n v
        try f () finally for (n, v) in saved do setEnv n v

    let bannerVars (logged: List<string>) =
        lock logged (fun () -> List.ofSeq logged)
        |> List.filter (fun l -> l.StartsWith "[INF]     ")
        |> List.map (fun l -> l.Substring 10)

    /// the messages of an exception and of everything it wraps
    let rec messages (e: exn) : string list =
        [ match e with
          | XakeException text -> yield text
          | _ -> yield e.Message
          match e with
          | :? AggregateException as a -> for i in a.InnerExceptions do yield! messages i
          | _ -> if not (isNull e.InnerException) then yield! messages e.InnerException ]

    let fails (f: unit -> unit) =
        let ex = Assert.Throws<XakeException>(fun () -> f ())
        ex.Data0

    let full (p: string) = Path.GetFullPath p

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

    member x.Root = full x.TestOptions.ProjectRoot

    // --- spec.md 8.8 Xake.Dotnet ---

    [<Test>]
    member x.``8.8-46 the environment's NUGET_PACKAGES is not read, the folder is .packages``() =
        withEnv ["NUGET_PACKAGES", "/m"] (fun () ->
            let (root, source), deps, _ = x.Read [] id (DotNetFwk.packageRootWithSource ())
            Assert.AreEqual(full (Path.Combine (x.Root, ".packages")), root)
            Assert.AreEqual(DotNetFwk.ScriptVariable, source)
            Assert.IsFalse(deps |> List.exists (function EnvVar _ -> true | _ -> false), "no EnvVar")
            let roots, _, _ = x.Read ["-d"; "HERMETIC=on"] id (HermeticMode.roots ())
            Assert.IsTrue(roots.PackageRootDeclared, "HERMETIC=on accepts .packages"))

    [<Test>]
    member x.``8.8-47 a var rule brings the environment's NUGET_PACKAGES in``() =
        let m = full "m-pkgs"
        withEnv ["NUGET_PACKAGES", m] (fun () ->
            let ops (s: XakeScript) = (xakeArgs [] (x.Opts (List<string>()))).AddVar(s, "NUGET_PACKAGES", [Env "NUGET_PACKAGES"])
            let (root, source), _, _ = x.Read [] ops (DotNetFwk.packageRootWithSource ())
            Assert.AreEqual(m, root)
            Assert.AreEqual(DotNetFwk.ScriptVariable, source)
            let roots, _, _ = x.Read [] ops (HermeticMode.roots ())
            Assert.IsTrue(roots.PackageRootDeclared))

    [<Test>]
    member x.``8.8-48 a blank NUGET_PACKAGES is unset, a relative one is under the root``() =
        withEnv ["NUGET_PACKAGES", null] (fun () ->
            let root, _, _ = x.Read ["-d"; "NUGET_PACKAGES="] id (DotNetFwk.packageRoot ())
            Assert.AreEqual(full (Path.Combine (x.Root, ".packages")), root)
            let root, _, _ = x.Read ["-d"; "NUGET_PACKAGES=pkgs"] id (DotNetFwk.packageRoot ())
            Assert.AreEqual(full (Path.Combine (x.Root, "pkgs")), root))

    [<Test>]
    member x.``8.8-49 XAKE_CSC_SERVER comes from the set, CSC_SERVER first``() =
        withEnv ["XAKE_CSC_SERVER", "0"] (fun () ->
            let server, _, _ = x.Read [] id (CompilerServer.resolve None)
            Assert.AreEqual(Shared None, server)
            let server, _, _ = x.Read ["-d"; "XAKE_CSC_SERVER=0"] id (CompilerServer.resolve None)
            Assert.AreEqual(InProcess, server)
            let server, _, _ = x.Read ["-d"; "XAKE_CSC_SERVER=0"; "-d"; "CSC_SERVER=30"] id (CompilerServer.resolve None)
            Assert.AreEqual(Shared (Some 30), server))

    [<Test>]
    member x.``8.8-50 an invalid CSC_SERVER or XAKE_CSC_SERVER fails the reading target``() =
        let text = x.ReadFails ["-d"; "CSC_SERVER=bogus"] id (CompilerServer.resolve None)
        StringAssert.Contains("CSC_SERVER='bogus': expected on, off or a number of seconds", text)
        withEnv ["VL50_S", "bogus"] (fun () ->
            let ops (s: XakeScript) = (xakeArgs [] (x.Opts (List<string>()))).AddVar(s, "CSC_SERVER", [Env "VL50_S"])
            let text = x.ReadFails [] ops (CompilerServer.resolve None)
            StringAssert.Contains("CSC_SERVER='bogus': expected on, off or a number of seconds", text))
        let text = x.ReadFails ["-d"; "XAKE_CSC_SERVER=maybe"] id (CompilerServer.resolve None)
        StringAssert.Contains("XAKE_CSC_SERVER='maybe': expected on or off", text)

    [<Test>]
    member x.``8.8-51 an empty switch is its default, an invalid one fails``() =
        let server, _, _ = x.Read ["-d"; "CSC_SERVER="; "-d"; "XAKE_CSC_SERVER=0"] id (CompilerServer.resolve None)
        Assert.AreEqual(InProcess, server)
        let server, _, _ = x.Read ["-d"; "XAKE_CSC_SERVER="] id (CompilerServer.resolve None)
        Assert.AreEqual(Shared None, server)
        let text = x.ReadFails ["-d"; "NUGET_FETCH=bogus"] id (DotNetFwk.fetchEnabled ())
        StringAssert.Contains("NUGET_FETCH='bogus': expected on or off", text)
        let text = x.ReadFails ["-d"; "HERMETIC=bogus"] id (HermeticMode.enabled ())
        StringAssert.Contains("HERMETIC='bogus': expected on or off", text)
        let fetch, deps, _ = x.Read ["-d"; "NUGET_FETCH="] id (DotNetFwk.fetchEnabled ())
        Assert.IsTrue(fetch)
        Assert.That(deps, Does.Contain(Var ("NUGET_FETCH", Some "")))
        let hermetic, _, _ = x.Read ["-d"; "HERMETIC="] id (HermeticMode.enabled ())
        Assert.IsFalse(hermetic)

    [<Test>]
    member x.``8.8-52 the jobs limits come from the set only, untracked``() =
        let jobs = recipe {
            let! ctx = getCtx()
            return CompilerRunner.jobsLimit ctx.Vars ("CSC_JOBS", "XAKE_CSC_JOBS") }
        withEnv ["XAKE_CSC_JOBS", "2"] (fun () ->
            let limit, _, _ = x.Read [] id jobs
            Assert.AreEqual(((None: int option), (None: (string * string) option)), limit)
            let ops (s: XakeScript) = (xakeArgs [] (x.Opts (List<string>()))).AddVar(s, "CSC_JOBS", [Env "XAKE_CSC_JOBS"])
            let limit, deps, _ = x.Read [] ops jobs
            Assert.AreEqual(Some 2, fst limit)
            Assert.IsFalse(deps |> List.exists (function Var ("CSC_JOBS", _) | EnvVar _ -> true | _ -> false), "no dependency"))
        let limit, _, _ = x.Read ["-d"; "CSC_JOBS=x"] id jobs
        Assert.AreEqual(((None: int option), Some ("CSC_JOBS", "x")), limit)

    [<Test>]
    member _.``8.8-53 RunOptions Default does not read XAKE_CSC_SERVER``() =
        withEnv ["XAKE_CSC_SERVER", "0"] (fun () ->
            Assert.AreEqual(Shared None, RunOptions.Default.Server))

    [<Test>]
    member _.``8.8-54 Xake.Dotnet reads no variable of L-2 from the environment``() =
        let names = [ "NUGET_PACKAGES"; "NUGET_FETCH"; "HERMETIC"; "CSC_SERVER"; "XAKE_CSC_SERVER"; "CSC_JOBS"; "XAKE_CSC_JOBS"; "FSC_JOBS"; "XAKE_FSC_JOBS" ]
        let alternatives = names |> String.concat "|"
        let envRead = Regex (sprintf "(GetEnvironmentVariable\\s*\\(?\\s*|getEnv\\s+|%%)\"(%s)\"" alternatives)
        let varRead = Regex (sprintf "Var\\.(string|int|bool|env|arg|create|secret)[^\\n]*\"(%s)\"" alternatives)
        let dir = Path.Combine (__SOURCE_DIRECTORY__, "..", "dotnet")
        let offending =
            [ for file in Directory.GetFiles (dir, "*.fs") do
                for line in File.ReadAllLines file do
                    let code = match line.IndexOf "//" with -1 -> line | i -> line.Substring (0, i)
                    if envRead.IsMatch code || varRead.IsMatch code then yield Path.GetFileName file + ": " + line.Trim () ]
        Assert.That(offending, Is.Empty)

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
        withEnv ["VL74T", "s3cr3t-value"] (fun () ->
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

    // --- pr2-spec.md 4.2 schemas and import ---

    [<Test>]
    member _.``pr2-76 DotNetVars exports the L-2 keys as argument-only strings``() =
        let expected =
            [ "NUGET_PACKAGES", Some ".packages"; "NUGET_FETCH", Some "on"; "HERMETIC", Some "off"
              "CSC_SERVER", None; "XAKE_CSC_SERVER", Some "on"; "CSC_JOBS", None; "FSC_JOBS", None ]
        let d = DotNetVars.Default
        let actual =
            [ "NUGET_PACKAGES", (d.NUGET_PACKAGES.Name, d.NUGET_PACKAGES.CliArgName, d.NUGET_PACKAGES.EnvName, d.NUGET_PACKAGES.Scope, d.NUGET_PACKAGES.DefaultValue)
              "NUGET_FETCH", (d.NUGET_FETCH.Name, d.NUGET_FETCH.CliArgName, d.NUGET_FETCH.EnvName, d.NUGET_FETCH.Scope, d.NUGET_FETCH.DefaultValue)
              "HERMETIC", (d.HERMETIC.Name, d.HERMETIC.CliArgName, d.HERMETIC.EnvName, d.HERMETIC.Scope, d.HERMETIC.DefaultValue)
              "CSC_SERVER", (d.CSC_SERVER.Name, d.CSC_SERVER.CliArgName, d.CSC_SERVER.EnvName, d.CSC_SERVER.Scope, None)
              "XAKE_CSC_SERVER", (d.XAKE_CSC_SERVER.Name, d.XAKE_CSC_SERVER.CliArgName, d.XAKE_CSC_SERVER.EnvName, d.XAKE_CSC_SERVER.Scope, d.XAKE_CSC_SERVER.DefaultValue)
              "CSC_JOBS", (d.CSC_JOBS.Name, d.CSC_JOBS.CliArgName, d.CSC_JOBS.EnvName, d.CSC_JOBS.Scope, None)
              "FSC_JOBS", (d.FSC_JOBS.Name, d.FSC_JOBS.CliArgName, d.FSC_JOBS.EnvName, d.FSC_JOBS.Scope, None) ]
        for (name, def), (name', (n, c, e, scope, def')) in List.zip expected actual do
            Assert.AreEqual(name, name')
            Assert.AreEqual((None, None, None, ArgOnly, def), (n, c, e, scope, def'), name)
        // every property is one of the above, each a string variable
        let props = typeof<DotNetVars>.GetProperties() |> Array.map (fun p -> p.Name, p.PropertyType.GetGenericArguments().[0]) |> List.ofArray
        Assert.That(props |> List.map fst |> List.sort, Is.EqualTo (expected |> List.map fst |> List.sort))
        Assert.That(props |> List.forall (fun (_, t) -> t = typeof<string>), Is.True)

    [<Test>]
    member x.``pr2-77 importing DotNetVars changes no value``() =
        withEnv ["NUGET_PACKAGES", null] (fun () ->
            let ops (s: XakeScript) = (xakeArgs [] (x.Opts (List<string>()))).Vars(s, DotNetVars.Default)
            let both = recipe {
                let! root = DotNetFwk.packageRoot ()
                let! fetch = DotNetFwk.fetchEnabled ()
                let! hermetic = HermeticMode.enabled ()
                let! server = CompilerServer.resolve None
                return root, fetch, hermetic, server }
            let imported, _, logged = x.Read [] ops both
            let plain, _, _ = x.Read [] id both
            Assert.AreEqual(plain, imported)
            Assert.AreEqual((full (Path.Combine (x.Root, ".packages")), true, false, Shared None), imported)
            Assert.That(bannerVars logged, Does.Contain "NUGET_FETCH=on (default)"))

    [<Test>]
    member x.``pr2-78 an overridden field brings the environment in``() =
        let m = full "m78-pkgs"
        withEnv ["NUGET_PACKAGES", m] (fun () ->
            let schema = { DotNetVars.Default with NUGET_PACKAGES = Var.string() |> withDefault ".packages" }
            let ops (s: XakeScript) = (xakeArgs [] (x.Opts (List<string>()))).Vars(s, schema)
            let root, deps, logged = x.Read [] ops (DotNetFwk.packageRoot ())
            Assert.AreEqual(m, root)
            Assert.That(bannerVars logged, Does.Contain (sprintf "NUGET_PACKAGES=%s (env:NUGET_PACKAGES)" m))
            Assert.That(deps, Does.Contain(Var ("NUGET_PACKAGES", Some m))))

    [<Test>]
    member x.``pr2-79 an overridden jobs field takes XAKE_CSC_JOBS``() =
        withEnv ["XAKE_CSC_JOBS", "2"] (fun () ->
            let schema = { DotNetVars.Default with CSC_JOBS = Var.string(envVar = "XAKE_CSC_JOBS") }
            let ops (s: XakeScript) = (xakeArgs [] (x.Opts (List<string>()))).Vars(s, schema)
            let jobs = recipe {
                let! ctx = getCtx()
                return CompilerRunner.jobsLimit ctx.Vars ("CSC_JOBS", "XAKE_CSC_JOBS") }
            let limit, deps, _ = x.Read [] ops jobs
            Assert.AreEqual(Some 2, fst limit)
            Assert.IsFalse(deps |> List.exists (function Var ("CSC_JOBS", _) -> true | _ -> false), "no dependency"))

    [<Test>]
    member x.``pr2-80 an imported key defined again fails the launch``() =
        let b () = xakeArgs [] (x.Opts (List<string>()))
        let text = x.ReadFails [] (fun s -> let b = b () in b.AddVar(b.Vars(s, DotNetVars.Default), "NUGET_FETCH", "off")) (recipe { return () })
        StringAssert.Contains("Variable 'NUGET_FETCH' is defined twice in the script: by varschema field NUGET_FETCH and by var \"NUGET_FETCH\" \"...\"", text)
        let text = x.ReadFails [] (fun s -> let b = b () in b.Vars(b.Vars(s, DotNetVars.Default), DotNetVars.Default)) (recipe { return () })
        StringAssert.Contains("by varschema field NUGET_FETCH and by varschema field NUGET_FETCH", text)

    [<Test>]
    member x.``pr2-81 an imported switch is checked at the read``() =
        let ops (s: XakeScript) = (xakeArgs [] (x.Opts (List<string>()))).Vars(s, DotNetVars.Default)
        let text = x.ReadFails ["-d"; "NUGET_FETCH=bogus"] ops (DotNetFwk.fetchEnabled ())
        StringAssert.Contains("NUGET_FETCH='bogus': expected on or off", text)
        // the launch itself succeeds: a target that does not read it runs
        let v, _, _ = x.Read ["-d"; "NUGET_FETCH=bogus"] ops (recipe { return 1 })
        Assert.AreEqual(1, v)

    [<Test>]
    member x.``pr2-82 a library key declared with another type fails the read``() =
        let schema = {| HERMETIC = Var.bool() |}
        let ops (s: XakeScript) = (xakeArgs [] (x.Opts (List<string>()))).Vars(s, schema)
        let text = x.ReadFails ["-d"; "HERMETIC=true"] ops (HermeticMode.enabled ())
        StringAssert.Contains("Variable 'HERMETIC' is read as 'System.String' but is declared by varschema field HERMETIC as 'System.Boolean'", text)

    [<Test>]
    member x.``pr2-83 --help lists the imported fields``() =
        let b = xakeArgs ["--help"] { x.TestOptions with ThrowOnError = true; FileLog = ""; NoPersist = true }
        let (XakeScript (o, _)) = b.Vars(b.Zero(), DotNetVars.Default)
        let text = XakeScriptBuilder.formatHelp o |> String.concat "\n"
        StringAssert.Contains("-d NUGET_PACKAGES=<value> [string]", text)
        StringAssert.Contains("\".packages\"", text)
        Assert.That(text, Does.Not.Contain "env: NUGET_PACKAGES")
