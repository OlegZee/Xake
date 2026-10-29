namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// `fsc { lock "path" }` and the lock functions over an F# compilation: the same gate as
/// `csc { lock }` (`CscLockTests`) -- record a missing lock and compile, compile the recorded
/// entry when it matches, fail with the differences when it does not, fail on a missing lock
/// under CI -- with `Fsc.run` behind it. The compilations target the SDK's own .NET (no
/// `targetfwk`), so the references are the SDK's targeting pack and its own `FSharp.Core`.
///
/// Every test that records a lock runs with the script variable `CI=off`, so the suite passes
/// with `CI=true` in the environment.
[<TestFixture>]
type ``Fsc lock``() =
    inherit XakeTestBase("fsc-lock")

    let settings (src: Fileset) (out: string) =
        { FscSettingsType.Default with Src = src; Out = File.make out; Target = Library }

    let readLock (path: string) =
        Lock.read (Roots.builtin (Directory.GetCurrentDirectory())) path

    member private x.RunWith (label: string) (vars: (string * string) list) (body: Recipe<ExecContext, unit>) =
        xake {x.TestOptions with FileLog = label + ".log"; ThrowOnError = true; Vars = vars; FileLogLevel = Loud} {
            want ([label])
            rules [ label => body ]
        }

    member private x.Run (label: string) (body: Recipe<ExecContext, unit>) = x.RunWith label [ "CI", "off" ] body

    /// `Lock.buildWith` over the F# compilation, with the run options `fsc { lock }` uses.
    member private x.Build (label: string) (settings: FscSettingsType) (lockPath: string) =
        x.Run label (recipe {
            let! f = Fsc.ofSettings settings
            let! run = Fsc.runOptions settings
            do! Lock.buildWith { Lock.Options.Default with FscRun = run } lockPath (Lock.Compilation.Fsc f)
        })

    member private x.Fresh (name: string) =
        File.WriteAllText (name + ".fs", sprintf "module %s\nlet value = 1\n" name)
        let lockPath = sprintf "locks/%s.json" (name.ToLowerInvariant ())
        if File.Exists lockPath then File.Delete lockPath
        if File.Exists (name + ".dll") then File.Delete (name + ".dll")
        lockPath

    [<Test; Category("Integration")>]
    member x.``fsc { lock } records an F# entry on the first build and compiles``() =
        let lockPath = x.Fresh "FLockA"

        x.Run "flock-a" (fsc {
            src !!"FLockA.fs"
            out (File.make "FLockA.dll")
            target Library
            lock lockPath
        })

        Assert.That(File.Exists lockPath, Is.True, "the lock file was not recorded")
        Assert.That(File.Exists "FLockA.dll", Is.True, "fsc did not produce FLockA.dll")

        let entry = (readLock lockPath).Entries |> List.exactlyOne
        match entry.Compilation with
        | Lock.Compilation.Fsc f ->
            Assert.That(f.Name, Is.EqualTo "FLockA")
            Assert.That(f.Sources |> List.exists (fun s -> s.EndsWith "FLockA.fs"), Is.True)
            Assert.That(f.Dependencies.Compiler.Tool, Is.EqualTo "fsc")
            Assert.That(f.Dependencies.Compiler.Path, Does.EndWith "fsc.dll")
            Assert.That(f.Dependencies.Compiler.Sha256, Is.Empty, "the SDK's fsc is identified by the SDK version, not its bytes")
            Assert.That(f.Dependencies.Compiler.Version, Is.Not.Empty)
            Assert.That(f.Dependencies.References |> List.exists (fun r -> r.Path.EndsWith "FSharp.Core.dll"), Is.True)
            Assert.That(f.Dependencies.References |> List.forall (fun r -> r.Sha256 <> ""), Is.True,
                "the recorded entry has to be hashed -- that is what gates the next build")
            Assert.That(f.Dependencies.Analyzers, Is.Empty)
        | Lock.Compilation.Csc _ -> Assert.Fail "an fsc lock was read back as a C# entry"
        Assert.That(File.ReadAllText lockPath, Does.Contain "\"Tool\": \"fsc\"")

    [<Test; Category("Integration")>]
    member x.``a second build leaves the lock untouched; drift fails with the diff``() =
        let lockPath = x.Fresh "FLockB"
        File.WriteAllText ("FLockB2.fs", "module FLockB2\nlet other = 2\n")

        x.Build "flock-b" (settings !!"FLockB.fs" "FLockB.dll") lockPath
        let bytes = File.ReadAllBytes lockPath
        x.Build "flock-b2" (settings !!"FLockB.fs" "FLockB.dll") lockPath
        Assert.That(File.ReadAllBytes lockPath, Is.EqualTo bytes, "the second build rewrote the lock")

        let ex = Assert.Throws<XakeException> (fun () -> x.Build "flock-b3" (settings (!!"FLockB.fs" + "FLockB2.fs") "FLockB.dll") lockPath)
        Assert.That(ex.Data0, Does.Contain "differs from the lock")
        Assert.That(ex.Data0, Does.Contain "$(ProjectRoot)/FLockB2.fs")
        Assert.That(File.ReadAllBytes lockPath, Is.EqualTo bytes, "a refused build must not rewrite the lock")

    [<Test; Category("Integration")>]
    member x.``Lock.record and Lock.verify over an F# compilation``() =
        let lockPath = x.Fresh "FLockC"
        File.WriteAllText ("FLockC2.fs", "module FLockC2\nlet other = 2\n")

        let mutable before : string list = [ "not run" ]
        let mutable after : string list = [ "not run" ]
        let mutable moved : string list = []
        x.Run "flock-c" (recipe {
            let! f = Fsc.ofSettings (settings !!"FLockC.fs" "FLockC.dll")
            do! Lock.record lockPath (Lock.Compilation.Fsc f)
            let! same = Lock.verify lockPath (Lock.Compilation.Fsc f)
            before <- same
            let! f2 = Fsc.ofSettings (settings (!!"FLockC.fs" + "FLockC2.fs") "FLockC.dll")
            let! diff = Lock.verify lockPath (Lock.Compilation.Fsc f2)
            moved <- diff
            do! Lock.record lockPath (Lock.Compilation.Fsc f2)
            let! same2 = Lock.verify lockPath (Lock.Compilation.Fsc f2)
            after <- same2 })

        Assert.That(before, Is.Empty, "an unchanged compilation has to verify clean")
        Assert.That(moved, Has.Some.Contains "$(ProjectRoot)/FLockC2.fs")
        Assert.That(after, Is.Empty, "Lock.record did not overwrite the lock")
        Assert.That(File.Exists "FLockC.dll", Is.False, "record and verify compile nothing")

    [<Test; Category("Integration")>]
    member x.``under CI a missing lock fails through fsc { lock }``() =
        let lockPath = x.Fresh "FLockD"

        let run () =
            x.RunWith "flock-d" [ "CI", "on" ] (fsc {
                src !!"FLockD.fs"
                out (File.make "FLockD.dll")
                target Library
                lock lockPath
            })
        let ex = Assert.Throws<XakeException> (fun () -> run ())
        Assert.That(ex.Data0, Does.Contain (sprintf "'FLockD': the lock '%s' is not there. Under CI a lock is never recorded" lockPath))
        Assert.That(File.Exists lockPath, Is.False, "under CI a missing lock must not be recorded")
        Assert.That(File.Exists "FLockD.dll", Is.False, "under CI a missing lock must not compile")

    /// `Lock.compile` of an F# entry read back from a lock: the recorded hashes gate the
    /// replay, so a reference whose hash no longer matches fails before fsc starts.
    [<Test; Category("Integration")>]
    member x.``Lock.compile of an F# entry refuses a reference whose hash does not match``() =
        let lockPath = x.Fresh "FLockE"

        x.Build "flock-e" (settings !!"FLockE.fs" "FLockE.dll") lockPath
        File.Delete "FLockE.dll"

        let entry = (readLock lockPath).Entries |> List.exactlyOne
        let f = entry.Fsc
        let core = f.Dependencies.References |> List.find (fun r -> r.Path.EndsWith "FSharp.Core.dll")
        let zeros = String.replicate 64 "0"
        let tampered =
            { entry with
                Compilation =
                    Lock.Compilation.Fsc
                        { f with
                            Fsc.Dependencies =
                                { f.Dependencies with
                                    References = f.Dependencies.References |> List.map (fun r -> if r.Path = core.Path then { r with Sha256 = zeros } else r) } } }

        let ex = Assert.Throws<XakeException> (fun () -> x.Run "flock-e2" (Lock.compile tampered))
        Assert.That(ex.Data0, Does.Contain "FSharp.Core.dll")
        Assert.That(ex.Data0, Does.Contain "expected")
        Assert.That(File.Exists "FLockE.dll", Is.False, "a hash mismatch must stop the compile")

        // the untampered entry replays
        x.Run "flock-e3" (Lock.compile entry)
        Assert.That(File.Exists "FLockE.dll", Is.True, "Lock.compile did not produce FLockE.dll")

    // ---- prerequisites -------------------------------------------------------------------

    /// `Run` against another project root (one with a `global.json` of the test's own).
    member private x.RunIn (root: string) (label: string) (body: Recipe<ExecContext, unit>) =
        xake {x.TestOptions with ProjectRoot = root; FileLog = label + ".log"; ThrowOnError = true; Vars = [ "CI", "off" ]; FileLogLevel = Loud} {
            want ([label])
            rules [ label => body ]
        }

    [<Test; Category("Integration")>]
    member x.``an fsc lock under an exact global.json pin records the SDK as a prerequisite``() =
        let root = Path.Combine (Directory.GetCurrentDirectory (), "pinned-" + System.Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        File.WriteAllText (Path.Combine (root, "FLockP.fs"), "module FLockP\nlet value = 1\n")
        let globalJson = Path.Combine (root, "global.json")
        let lockPath = "locks/flockp.json"
        if File.Exists "FLockP.dll" then File.Delete "FLockP.dll"
        let mutable version = ""
        let mutable same : string list = [ "not run" ]
        let mutable unpinned : string list = []
        try
            x.RunIn root "flock-p" (recipe {
                let! f = Fsc.ofSettings (settings !!"FLockP.fs" "FLockP.dll")
                let! roots = Roots.current
                version <- Lock.sdkVersionsOf roots [ f.Dependencies.Compiler.Path ] |> List.exactlyOne
                File.WriteAllText (globalJson, sprintf "{ \"sdk\": { \"version\": \"%s\", \"rollForward\": \"disable\" } }" version)
                do! Lock.record lockPath (Lock.Compilation.Fsc f)
                let! diff = Lock.verify lockPath (Lock.Compilation.Fsc f)
                same <- diff
                // the prerequisite is met here: the recorded entry replays
                let! doc = Lock.load lockPath
                do! Lock.compile doc.Entries.Head
                // the pin loosened: the configuration no longer makes the SDK a prerequisite
                File.WriteAllText (globalJson, sprintf "{ \"sdk\": { \"version\": \"%s\", \"rollForward\": \"latestFeature\" } }" version)
                let! diff = Lock.verify lockPath (Lock.Compilation.Fsc f)
                unpinned <- diff })

            let text = File.ReadAllText (Path.Combine (root, lockPath))
            Assert.That(text, Does.Contain (sprintf "\"Prerequisites\": [\n          { \"Kind\": \"dotnet-sdk\", \"Version\": \"%s\", \"Pin\": \"$(ProjectRoot)/global.json\" }\n        ]" version))
            let entry = (Lock.read (Roots.builtin root) (Path.Combine (root, lockPath))).Entries |> List.exactlyOne
            Assert.That(entry.Prerequisites, Is.EqualTo [ ({ Kind = "dotnet-sdk"; Version = version; Pin = "$(ProjectRoot)/global.json" }: Lock.Prerequisite) ])
            Assert.That(same, Is.Empty, "the same configuration has to verify clean")
            Assert.That(File.Exists "FLockP.dll", Is.True, "the entry with a met prerequisite did not compile")
            Assert.That(unpinned, Is.EqualTo [ sprintf "- Prerequisite dotnet-sdk %s ($(ProjectRoot)/global.json)" version ])
            Assert.That(File.ReadAllText "flock-p.log", Does.Not.Contain "does not pin it exactly")
        finally
            try Directory.Delete (root, true) with _ -> ()

    [<Test; Category("Integration")>]
    member x.``without an exact pin no prerequisite is written and the recording warns``() =
        // a project root of its own with a global.json that rolls forward (not an exact pin),
        // so the test does not depend on how the repository pins its SDK
        let root = Path.Combine (Directory.GetCurrentDirectory (), "unpinned-" + System.Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory root |> ignore
        File.WriteAllText (Path.Combine (root, "FLockQ.fs"), "module FLockQ\nlet value = 1\n")
        File.WriteAllText (Path.Combine (root, "global.json"), "{ \"sdk\": { \"version\": \"8.0.100\", \"rollForward\": \"latestMajor\" } }")
        let lockPath = "locks/flockq.json"
        try
            x.RunIn root "flock-q" (recipe {
                let! f = Fsc.ofSettings (settings !!"FLockQ.fs" "FLockQ.dll")
                do! Lock.record lockPath (Lock.Compilation.Fsc f) })

            let full = Path.Combine (root, lockPath)
            let entry = (Lock.read (Roots.builtin root) full).Entries |> List.exactlyOne
            Assert.That(entry.Prerequisites, Is.Empty)
            Assert.That(File.ReadAllText full, Does.Not.Contain "Prerequisites")
            Assert.That(File.ReadAllText "flock-q.log", Does.Match "'FLockQ': depends on the \\.NET SDK [^ ]+ but global\\.json does not pin it exactly; the lock will only build where that SDK is installed")
        finally
            try Directory.Delete (root, true) with _ -> ()

    [<Test; Category("Integration")>]
    member x.``replay fails before anything else when the prerequisite SDK is not installed``() =
        let lockPath = x.Fresh "FLockR"
        x.Run "flock-r" (recipe {
            let! f = Fsc.ofSettings (settings !!"FLockR.fs" "FLockR.dll")
            do! Lock.record lockPath (Lock.Compilation.Fsc f) })
        let entry = (readLock lockPath).Entries |> List.exactlyOne
        let bogus = { entry with Prerequisites = [ { Kind = Lock.DotnetSdk; Version = "0.0.1-xake-missing"; Pin = "$(ProjectRoot)/global.json" } ] }

        let ex = Assert.Throws<XakeException> (fun () -> x.Run "flock-r2" (Lock.compile bogus))
        let root = (DotNetFwk.dotnetRoot () |> Option.defaultValue "").Replace('\\', '/')
        Assert.That(ex.Data0, Does.Contain (sprintf "'FLockR': the lock needs the .NET SDK 0.0.1-xake-missing (prerequisite from $(ProjectRoot)/global.json), which is not installed under '%s'; install it (dotnet-install --version 0.0.1-xake-missing --install-dir %s)" root root))
        Assert.That(File.Exists "FLockR.dll", Is.False, "nothing may be compiled")

        // the same entry written to a lock file and read back fails the same way
        File.WriteAllText (lockPath, Lock.format (Roots.builtin (Directory.GetCurrentDirectory ())) { Configuration = ""; Properties = []; Entries = [ bogus ] })
        Assert.That((readLock lockPath).Entries.Head.Prerequisites, Is.EqualTo bogus.Prerequisites)


    // ---- the SDK's fsc: identified by the SDK version, not by a hash ----------------------

    /// `entry` with its compiler's hash set to `sha` (a lock from Xake 0.2, or from a machine
    /// of another OS/architecture, whose ReadyToRun `fsc.dll` has other bytes).
    member private x.WithCompilerHash (sha: string) (entry: Lock.Entry) =
        let f = entry.Fsc
        { entry with
            Compilation =
                Lock.Compilation.Fsc
                    { f with Fsc.Dependencies = { f.Dependencies with Compiler = { f.Dependencies.Compiler with Sha256 = sha } } } }

    [<Test; Category("Integration")>]
    member x.``a lock from 0.2 with a hash of the SDK's fsc round-trips unchanged and builds``() =
        let lockPath = x.Fresh "FLockS"
        x.Run "flock-s" (recipe {
            let! f = Fsc.ofSettings (settings !!"FLockS.fs" "FLockS.dll")
            do! Lock.record lockPath (Lock.Compilation.Fsc f) })
        let recorded = (readLock lockPath).Entries |> List.exactlyOne
        Assert.That(recorded.Fsc.Dependencies.Compiler.Sha256, Is.Empty)

        // what 0.2 wrote: the real hash of fsc.dll
        let compiler = recorded.Fsc.Dependencies.Compiler.Path
        let old = x.WithCompilerHash (Csc.sha256 compiler) recorded
        let roots = Roots.builtin (Directory.GetCurrentDirectory ())
        let text = Lock.format roots { Configuration = ""; Properties = []; Entries = [ old ] }
        File.WriteAllText (lockPath, text)
        let readBack = (readLock lockPath).Entries |> List.exactlyOne
        Assert.That(readBack.Fsc.Dependencies.Compiler.Sha256, Is.EqualTo (Csc.sha256 compiler), "the hash is read as written")
        Assert.That(Lock.format roots { Configuration = ""; Properties = []; Entries = [ readBack ] }, Is.EqualTo text, "the format is unchanged")

        // the same configuration matches the old lock, which builds and stays as it was
        x.Build "flock-s2" (settings !!"FLockS.fs" "FLockS.dll") lockPath
        Assert.That(File.Exists "FLockS.dll", Is.True, "the 0.2 lock did not build")
        Assert.That(File.ReadAllText lockPath, Is.EqualTo text, "building must not rewrite the lock")

    [<Test; Category("Integration")>]
    member x.``a wrong hash of the SDK's fsc (another OS) is ignored when the SDK version is present``() =
        let lockPath = x.Fresh "FLockT"
        x.Run "flock-t" (recipe {
            let! f = Fsc.ofSettings (settings !!"FLockT.fs" "FLockT.dll")
            do! Lock.record lockPath (Lock.Compilation.Fsc f) })
        let entry = (readLock lockPath).Entries |> List.exactlyOne
        let other = x.WithCompilerHash (String.replicate 64 "a") entry
        File.WriteAllText (lockPath, Lock.format (Roots.builtin (Directory.GetCurrentDirectory ())) { Configuration = ""; Properties = []; Entries = [ other ] })

        // the entry itself, and the lock file through the gate
        x.Run "flock-t2" (Lock.compile ((readLock lockPath).Entries |> List.exactlyOne))
        Assert.That(File.Exists "FLockT.dll", Is.True, "Lock.compile refused an SDK fsc over its hash")
        File.Delete "FLockT.dll"
        x.Build "flock-t3" (settings !!"FLockT.fs" "FLockT.dll") lockPath
        Assert.That(File.Exists "FLockT.dll", Is.True, "the lock with another OS's fsc hash did not build")

    [<Test; Category("Integration")>]
    member x.``an fsc of an SDK version that is not installed still fails``() =
        let lockPath = x.Fresh "FLockU"
        x.Run "flock-u" (recipe {
            let! f = Fsc.ofSettings (settings !!"FLockU.fs" "FLockU.dll")
            do! Lock.record lockPath (Lock.Compilation.Fsc f) })
        let entry = (readLock lockPath).Entries |> List.exactlyOne
        let f = entry.Fsc
        let compiler = f.Dependencies.Compiler
        let root = (DotNetFwk.dotnetRoot () |> Option.defaultValue "").Replace('\\', '/').TrimEnd '/'
        let version = (DotNetFwk.sdkVersionOf root compiler.Path).Value
        let missingPath = compiler.Path.Replace('\\', '/').Replace (sprintf "/sdk/%s/" version, "/sdk/0.0.1-xake-missing/")
        let missing =
            { entry with
                Compilation =
                    Lock.Compilation.Fsc
                        { f with Fsc.Dependencies = { f.Dependencies with Compiler = { compiler with Path = missingPath } } } }

        let ex = Assert.Throws<XakeException> (fun () -> x.Run "flock-u2" (Lock.compile missing))
        Assert.That(ex.Data0, Does.Contain "'FLockU': the lock names the compiler of SDK 0.0.1-xake-missing")
        Assert.That(File.Exists "FLockU.dll", Is.False, "nothing may be compiled")

        // and the configuration, resolved with the installed SDK, differs from such a lock
        File.WriteAllText (lockPath, Lock.format (Roots.builtin (Directory.GetCurrentDirectory ())) { Configuration = ""; Properties = []; Entries = [ missing ] })
        let ex2 = Assert.Throws<XakeException> (fun () -> x.Build "flock-u3" (settings !!"FLockU.fs" "FLockU.dll") lockPath)
        Assert.That(ex2.Data0, Does.Contain "differs from the lock")
        Assert.That(ex2.Data0, Does.Contain "~ Compiler.Path")
