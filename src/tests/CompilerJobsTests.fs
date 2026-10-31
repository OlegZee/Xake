namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// `CSC_JOBS` / `FSC_JOBS` (then `XAKE_CSC_JOBS` / `XAKE_FSC_JOBS`) cap the concurrent compiler
/// launches of `Csc.run` / `Fsc.run`; unset or 0 is unlimited, and the value is not a
/// dependency.
[<TestFixture>]
type ``Compiler jobs``() =
    inherit XakeTestBase("compiler-jobs")

    // A compiler that records how many copies of itself run at once (a file per running
    // process in `<name>.running/`, the count after its own file is created, so an overlap is
    // never missed), sleeps, and writes the output its response file names.
    let fakeCompiler (name: string) =
        let script = name + ".sh"
        let running = Path.GetFullPath (name + ".running")
        let peaks = Path.GetFullPath (name + ".peaks")
        if Directory.Exists running then Directory.Delete (running, true)
        Directory.CreateDirectory running |> ignore
        File.Delete peaks
        File.WriteAllText (script,
            "#!/bin/sh\n"
            + sprintf "touch '%s/'$$\n" running
            + sprintf "ls '%s' | wc -l | tr -d ' ' >> '%s'\n" running peaks
            + "sleep 1\n"
            + "for a in \"$@\"; do case \"$a\" in @*) rsp=\"${a#@}\";; esac; done\n"
            + "out=$(sed -n -e 's#^\"\\{0,1\\}/out:\\([^\"]*\\)\"\\{0,1\\}$#\\1#p' -e 's#^\"\\{0,1\\}-o:\\([^\"]*\\)\"\\{0,1\\}$#\\1#p' \"$rsp\")\n"
            + "[ -n \"$out\" ] && : > \"$out\"\n"
            + sprintf "rm -f '%s/'$$\n" running
            + "exit 0\n")
        (System.Diagnostics.Process.Start ("chmod", "+x " + script)).WaitForExit ()
        let launches () = if File.Exists peaks then File.ReadAllLines peaks |> Array.map int else [||]
        Path.GetFullPath script, launches

    member private x.BuildCsc (label: string) (fake: string) (vars: (string * string) list) (count: int) =
        x.BuildCscThrottled label fake vars None count

    member private x.BuildCscThrottled (label: string) (fake: string) (vars: (string * string) list) (throttled: (Resource * int) option) (count: int) =
        // written once: a rewritten source would rebuild on its own
        for i in 1..count do
            let source = sprintf "%s%d.cs" label i
            if not (File.Exists source) then File.WriteAllText (source, sprintf "class C%d {}\n" i)
        // one target needing all of them, so they are built in parallel (the targets of
        // `want` are built one after another)
        xake {x.TestOptions with FileLog = label + ".log"; FileLogLevel = Verbosity.Diag; ThrowOnError = true; Threads = 4; ScriptVars = vars} {
            want ["main"]
            rules [
                "main" => recipe { do! need [ for i in 1..count -> sprintf "%s%d.dll" label i ] }
                sprintf "%s(n:*).dll" label ..> recipe {
                    let! n = getRuleMatch "n"
                    match throttled with
                    | Some (resource, units) ->
                        do! csc { targetfwk "netstandard2.0"; src (!! (sprintf "%s%s.cs" label n)); cscpath fake; throttle (resource, units) }
                    | None ->
                        do! csc { targetfwk "netstandard2.0"; src (!! (sprintf "%s%s.cs" label n)); cscpath fake }
                }
            ]
        }

    [<Test>]
    member _.``jobsLimit: the build variable, else unlimited; the environment is not read``() =
        let env = "XAKE_TEST_JOBS_" + System.Guid.NewGuid().ToString("N")
        let names = "TEST_JOBS", env
        try
            Assert.That(CompilerRunner.jobsLimit [] names, Is.EqualTo ((None: int option), (None: (string * string) option)))
            System.Environment.SetEnvironmentVariable (env, "3")
            Assert.That(fst (CompilerRunner.jobsLimit [] names), Is.EqualTo (None: int option))
            Assert.That(fst (CompilerRunner.jobsLimit ["TEST_JOBS", "2"] names), Is.EqualTo (Some 2))
            Assert.That(fst (CompilerRunner.jobsLimit ["TEST_JOBS", "0"] names), Is.EqualTo (None: int option))
            Assert.That(fst (CompilerRunner.jobsLimit ["TEST_JOBS", " "] names), Is.EqualTo (None: int option))
            Assert.That(CompilerRunner.jobsLimit ["TEST_JOBS", "many"] names, Is.EqualTo ((None: int option), Some ("TEST_JOBS", "many")))
            Assert.That(snd (CompilerRunner.jobsLimit ["TEST_JOBS", "-1"] names), Is.EqualTo (Some ("TEST_JOBS", "-1")))
        finally
            System.Environment.SetEnvironmentVariable (env, null)

    [<Test>]
    member x.``CSC_JOBS=1 runs one compiler at a time``() =
        if Env.isWindows then Assert.Ignore "a shell script stands in for the compiler"
        let fake, launches = fakeCompiler "csc-one"
        x.BuildCsc "one" fake ["CSC_JOBS", "1"] 4
        Assert.That(launches().Length, Is.EqualTo 4)
        Assert.That(Array.max (launches()), Is.EqualTo 1, "more than one compiler ran at once under CSC_JOBS=1")

    [<Test>]
    member x.``without CSC_JOBS compilers run in parallel``() =
        if Env.isWindows then Assert.Ignore "a shell script stands in for the compiler"
        if System.Environment.GetEnvironmentVariable "XAKE_CSC_JOBS" <> null then Assert.Ignore "XAKE_CSC_JOBS is set"
        let fake, launches = fakeCompiler "csc-free"
        x.BuildCsc "free" fake [] 4
        Assert.That(launches().Length, Is.EqualTo 4)
        Assert.That(Array.max (launches()), Is.GreaterThan 1, "compilers never overlapped without a limit")

    [<Test>]
    member x.``changing CSC_JOBS does not rebuild``() =
        if Env.isWindows then Assert.Ignore "a shell script stands in for the compiler"
        let fake, launches = fakeCompiler "csc-dep"
        x.BuildCsc "dep" fake ["CSC_JOBS", "1"] 2
        Assert.That(launches().Length, Is.EqualTo 2)
        x.BuildCsc "dep" fake ["CSC_JOBS", "3"] 2
        Assert.That(launches().Length, Is.EqualTo 2, "a different CSC_JOBS rebuilt the targets")

    [<Test>]
    member x.``XAKE_FSC_JOBS limits fsc through a var rule``() =
        if Env.isWindows then Assert.Ignore "a shell script stands in for the compiler"
        let fake, launches = fakeCompiler "fsc-env"
        let fsharpCore = System.Reflection.Assembly.GetAssembly(typeof<option<int>>).Location
        for i in 1..3 do File.WriteAllText (sprintf "fj%d.fs" i, sprintf "module M%d\nlet v = %d\n" i i)
        let previous = System.Environment.GetEnvironmentVariable "XAKE_FSC_JOBS"
        System.Environment.SetEnvironmentVariable ("XAKE_FSC_JOBS", "1")
        try
            xake {x.TestOptions with FileLog = "fsc-env.log"; FileLogLevel = Verbosity.Diag; ThrowOnError = true; Threads = 4} {
                var "FSC_JOBS" [Env "XAKE_FSC_JOBS"]
                want ["main"]
                rules [
                    "main" => recipe { do! need [ for i in 1..3 -> sprintf "fj%d.dll" i ] }
                    "fj(n:*).dll" ..> recipe {
                        let! n = getRuleMatch "n"
                        let! f = fsc {
                            targetfwk "netstandard2.0"
                            src (!! (sprintf "fj%s.fs" n))
                            ref (Fileset.Empty ++ fsharpCore)
                            out (File.make (sprintf "fj%s.dll" n))
                            resolve
                        }
                        do! Fsc.run { FscRunOptions.Default with FscPath = Some fake } f
                    }
                ]
            }
        finally
            System.Environment.SetEnvironmentVariable ("XAKE_FSC_JOBS", previous)
        Assert.That(launches().Length, Is.EqualTo 3)
        Assert.That(Array.max (launches()), Is.EqualTo 1, "more than one fsc ran at once under XAKE_FSC_JOBS=1")

    [<Test>]
    member _.``Throttle defaults to None everywhere``() =
        Assert.That(CscSettingsType.Default.Throttle, Is.EqualTo (None: (Resource * int) option))
        Assert.That(RunOptions.Default.Throttle, Is.EqualTo (None: (Resource * int) option))
        Assert.That(FscSettingsType.Default.Throttle, Is.EqualTo (None: (Resource * int) option))
        Assert.That(FscRunOptions.Default.Throttle, Is.EqualTo (None: (Resource * int) option))

    [<Test>]
    member x.``throttle with a resource of limit 1 runs one compiler at a time``() =
        if Env.isWindows then Assert.Ignore "a shell script stands in for the compiler"
        if System.Environment.GetEnvironmentVariable "XAKE_CSC_JOBS" <> null then Assert.Ignore "XAKE_CSC_JOBS is set"
        let fake, launches = fakeCompiler "csc-throttle"
        use compilers = Resource.newResource "compilers" 1
        x.BuildCscThrottled "throttle" fake [] (Some (compilers, 1)) 4
        Assert.That(launches().Length, Is.EqualTo 4)
        Assert.That(Array.max (launches()), Is.EqualTo 1, "more than one compiler ran at once under a throttle of limit 1")

    [<Test>]
    member x.``throttle takes the place of CSC_JOBS``() =
        if Env.isWindows then Assert.Ignore "a shell script stands in for the compiler"
        let fake, launches = fakeCompiler "csc-override"
        use compilers = Resource.newResource "compilers" 3
        x.BuildCscThrottled "override" fake ["CSC_JOBS", "1"] (Some (compilers, 1)) 4
        Assert.That(launches().Length, Is.EqualTo 4)
        Assert.That(Array.max (launches()), Is.GreaterThan 1, "CSC_JOBS=1 applied although the compilation names a throttle")

    [<Test>]
    member x.``FscRunOptions.Throttle limits fsc``() =
        if Env.isWindows then Assert.Ignore "a shell script stands in for the compiler"
        let fake, launches = fakeCompiler "fsc-throttle"
        let fsharpCore = System.Reflection.Assembly.GetAssembly(typeof<option<int>>).Location
        for i in 1..3 do File.WriteAllText (sprintf "ft%d.fs" i, sprintf "module M%d\nlet v = %d\n" i i)
        use compilers = Resource.newResource "fsc-compilers" 1
        xake {x.TestOptions with FileLog = "fsc-throttle.log"; FileLogLevel = Verbosity.Diag; ThrowOnError = true; Threads = 4} {
            want ["main"]
            rules [
                "main" => recipe { do! need [ for i in 1..3 -> sprintf "ft%d.dll" i ] }
                "ft(n:*).dll" ..> recipe {
                    let! n = getRuleMatch "n"
                    let! f = fsc {
                        targetfwk "netstandard2.0"
                        src (!! (sprintf "ft%s.fs" n))
                        ref (Fileset.Empty ++ fsharpCore)
                        out (File.make (sprintf "ft%s.dll" n))
                        resolve
                    }
                    do! Fsc.run { FscRunOptions.Default with FscPath = Some fake; Throttle = Some (compilers, 1) } f
                }
            ]
        }
        Assert.That(launches().Length, Is.EqualTo 3)
        Assert.That(Array.max (launches()), Is.EqualTo 1, "more than one fsc ran at once under a throttle of limit 1")
