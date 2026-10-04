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
            wantOverride ([label])
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
            Assert.That(f.Dependencies.Compiler.Sha256, Is.Not.Empty)
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
