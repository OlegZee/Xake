namespace Tests

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open NUnit.Framework

open Xake
open Xake.Hermetic.Dotnet

/// The conformance tests of docs/features/vars-precedence/spec.md §8.9 (Xake.Hermetic.Dotnet
/// reads its variables from the variable set only) and pr2-spec.md test 84 (importing
/// `HermeticVars`); each test is named by its spec number. The environment is process-wide:
/// the tests that set `CI` hold the gate of the CI tests (`CscLockGate`).
[<TestFixture>]
type ``Hermetic vars conformance (spec section 8-9)``() =
    inherit XakeTestBase("hermvars")

    let withEnv (pairs: (string * string) list) f =
        lock CscLockGate.gate (fun () ->
            let saved = [ for (n, _) in pairs -> n, Environment.GetEnvironmentVariable n ]
            for (n, v) in pairs do Environment.SetEnvironmentVariable (n, v)
            try f () finally for (n, v) in saved do Environment.SetEnvironmentVariable (n, v))

    let fails (f: unit -> unit) =
        let ex = Assert.Throws<XakeException>(fun () -> f ())
        ex.Data0

    /// options without a database and without the test runner's `-d` (`XakeTestBase`)
    member x.Opts () =
        { x.TestOptions with ThrowOnError = true; FileLog = ""; ConLogLevel = Silent; NoPersist = true; Nologo = true; CliVars = [] }

    /// Runs `r` in the target "main" of a script with the command line `args` and `ops`
    /// applied: the value and the dependencies.
    member x.Read (args: string list) (ops: XakeScript -> XakeScript) (r: Recipe<ExecContext, 'T>) =
        let seen = ref None
        let deps = ref []
        let b = xakeArgs args (x.Opts ())
        b.Phony(ops (b.Zero()), "main", recipe {
            let! v = r
            let! result = getResult()
            deps.Value <- result.Depends
            seen.Value <- Some v })
        |> b.Run
        Option.get seen.Value, deps.Value

    member x.ReadFails args ops r = fails (fun () -> x.Read args ops r |> ignore)

    member x.Builder = xakeArgs [] (x.Opts ())

    member x.Rule (name: string) (sources: VarSource list) (s: XakeScript) = x.Builder.AddVar(s, name, sources)
    member x.Const (name: string) (value: string) (s: XakeScript) = x.Builder.AddVar(s, name, value)

    [<Test>]
    member x.``8.9-55 CI comes from the set only``() =
        withEnv ["CI", "true"] (fun () ->
            Assert.AreEqual(false, fst (x.Read [] id Lock.underCi))
            Assert.AreEqual(true, fst (x.Read [] (x.Rule "CI" [Env "CI"]) Lock.underCi)))
        let text = x.ReadFails ["-d"; "CI=bogus"] id Lock.underCi
        StringAssert.Contains("CI='bogus': expected on or off", text)

    [<Test>]
    member x.``8.9-56 an empty CI is off``() =
        Assert.AreEqual(false, fst (x.Read ["-d"; "CI="] id Lock.underCi))
        Assert.AreEqual(false, fst (x.Read [] (x.Const "CI" " ") Lock.underCi))
        if not Env.isWindows then
            withEnv ["CI", ""] (fun () ->
                if Environment.GetEnvironmentVariable "CI" = "" then
                    Assert.AreEqual(false, fst (x.Read [] (x.Rule "CI" [Env "CI"]) Lock.underCi)))

    [<Test>]
    member x.``8.9-57 LOCK_STALE empty is fail, LOCK_REFRESH from the set only``() =
        let stale value = recipe { return Locks.staleWarns value false }
        Assert.AreEqual(false, fst (x.Read [] id (stale (Some ""))))
        Assert.AreEqual(false, fst (x.Read [] id (stale None)))
        Assert.AreEqual(true, fst (x.Read [] id (stale (Some "warn"))))
        Assert.AreEqual(false, Locks.staleWarns (Some "warn") true)
        let text = x.ReadFails [] id (stale (Some "x"))
        StringAssert.Contains("LOCK_STALE='x': expected fail or warn", text)
        // the switch itself is read from the set: -d LOCK_STALE= is fail
        let read = recipe { let! v = Var.read<string> "LOCK_STALE" in return Locks.staleWarns v false }
        Assert.AreEqual(false, fst (x.Read ["-d"; "LOCK_STALE="] id read))
        withEnv ["LOCK_REFRESH", "all"] (fun () ->
            Assert.AreEqual(None, fst (x.Read [] id Locks.refreshValue))
            Assert.AreEqual(Some "all", fst (x.Read [] (x.Rule "LOCK_REFRESH" [Env "LOCK_REFRESH"]) Locks.refreshValue)))
        Assert.AreEqual(None, fst (x.Read ["-d"; "LOCK_REFRESH= "] id Locks.refreshValue))

    [<Test>]
    member x.``8.9-58 SOURCE_DATE_EPOCH comes from the set, context-free defaults are 1980``() =
        let epoch = DateTime (1980, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        let stamp = DateTime (2023, 11, 14, 22, 13, 20, DateTimeKind.Utc)
        let both = recipe {
            let! pack = Pack.Options.current ()
            let! sbom = Sbom.PackageScopeOptions.current ()
            return pack.Timestamp, sbom.AnnotationTimestamp }
        withEnv ["SOURCE_DATE_EPOCH", "1700000000"] (fun () ->
            Assert.AreEqual(epoch, Pack.Options.Default.Timestamp)
            Assert.AreEqual("1980-01-01T00:00:00Z", Sbom.PackageScopeOptions.Default.AnnotationTimestamp)
            Assert.AreEqual((epoch, "1980-01-01T00:00:00Z"), fst (x.Read [] id both))
            Assert.AreEqual((stamp, "2023-11-14T22:13:20Z"), fst (x.Read [] (x.Rule "SOURCE_DATE_EPOCH" [Env "SOURCE_DATE_EPOCH"]) both)))
        Assert.AreEqual((epoch, "1980-01-01T00:00:00Z"), fst (x.Read ["-d"; "SOURCE_DATE_EPOCH="] id both))
        Assert.AreEqual((epoch, "1980-01-01T00:00:00Z"), fst (x.Read ["-d"; "SOURCE_DATE_EPOCH=soon"] id both))

    [<Test>]
    member _.``8.9-59 Xake.Hermetic.Dotnet reads no variable of L-2 from the environment``() =
        let names = [ "NUGET_PACKAGES"; "NUGET_FETCH"; "HERMETIC"; "CI"; "LOCK_REFRESH"; "LOCK_STALE"; "SOURCE_DATE_EPOCH"; "CSC_SERVER"; "XAKE_CSC_SERVER" ]
        let alternatives = names |> String.concat "|"
        let envRead = Regex (sprintf "(GetEnvironmentVariable\\s*\\(?\\s*|getEnv\\s+)\"(%s)\"" alternatives)
        let varRead = Regex (sprintf "Var\\.(string|int|bool|env|arg|create|secret)[^\\n]*\"(%s)\"" alternatives)
        let dir = Path.Combine (__SOURCE_DIRECTORY__, "..", "hermetic")
        let offending =
            [ for file in Directory.GetFiles (dir, "*.fs") do
                for line in File.ReadAllLines file do
                    let code = match line.IndexOf "//" with -1 -> line | i -> line.Substring (0, i)
                    if envRead.IsMatch code || varRead.IsMatch code then yield Path.GetFileName file + ": " + line.Trim () ]
        Assert.That(offending, Is.Empty)

    [<Test>]
    member x.``pr2-84 HermeticVars imported, CI overridden to take the environment``() =
        let import schema (s: XakeScript) = x.Builder.Vars(s, schema)
        withEnv ["CI", "true"] (fun () ->
            let schema = { HermeticVars.Default with CI = Var.string() |> withDefault "off" }
            Assert.AreEqual(true, fst (x.Read [] (import schema) Lock.underCi))
            Assert.AreEqual(false, fst (x.Read [] (import HermeticVars.Default) Lock.underCi)))
        let d = HermeticVars.Default
        Assert.AreEqual((None, None, None, ArgOnly, Some "off"), (d.CI.Name, d.CI.CliArgName, d.CI.EnvName, d.CI.Scope, d.CI.DefaultValue))
        Assert.AreEqual((ArgOnly, Some "fail"), (d.LOCK_STALE.Scope, d.LOCK_STALE.DefaultValue))
        Assert.AreEqual(ArgOnly, d.LOCK_REFRESH.Scope)
        Assert.AreEqual(ArgOnly, d.SOURCE_DATE_EPOCH.Scope)
        let props = typeof<HermeticVars>.GetProperties() |> Array.map (fun p -> p.Name) |> Array.sort |> List.ofArray
        Assert.That(props, Is.EqualTo [ "CI"; "LOCK_REFRESH"; "LOCK_STALE"; "SOURCE_DATE_EPOCH" ])
