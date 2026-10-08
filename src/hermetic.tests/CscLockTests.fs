namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// `csc { lock "path" }` -- migration path A of `lock-from-settings.md` §9, with the strict
/// semantics decided on 2026-09-24: the settings are the source of truth (`resolve` always
/// runs), the lock decides whether this is the compilation that was recorded. No lock: record
/// and compile. Lock present and matching: compile the *recorded* entry, so its hashes gate
/// the build. Lock present and different: fail with the diff. Updating is explicit --
/// `Lock.record`, or deleting the file. Under CI (`Lock.underCi`) a missing lock fails.
///
/// Every test that records a lock runs with the script variable `CI=off` (`Build`, `Run`),
/// so the suite passes with `CI=true` in the environment (GitHub Actions). The environment
/// variable is process-wide: the tests that set it hold this.
module private CscLockGate =
    let gate = obj ()

[<TestFixture>]
type ``Csc lock``() =
    inherit XakeTestBase("csc-lock")

    /// The shape the other integration tests here use: a net-4.6.2 library, one source file.
    /// The lock path goes alongside: the settings no longer carry one.
    let settings (src: Fileset) (out: string) (lockPath: string option) =
        { CscSettingsType.Default with
            Src = src
            Out = File.make out
            Target = Library
            TargetFramework = "net-4.6.2"
            RefGlobal = ["System.dll"] }, lockPath

    let readLock (path: string) =
        Lock.read (Roots.builtin (Directory.GetCurrentDirectory())) path

    /// Without a lock: `Csc.compile`. With one: `Lock.build`, through the same run options
    /// `csc { ...; lock }` uses (`Csc.runOptions`).
    member private x.Build (label: string) ((settings: CscSettingsType), (lockPath: string option)) =
        xake {x.TestOptions with FileLog = label + ".log"; ThrowOnError = true; Vars = [ "CI", "off" ]} {
            wantOverride ([label])
            rules [
                label => recipe {
                    match lockPath with
                    | None -> do! Csc.compile settings
                    | Some path ->
                        let! c = Csc.ofSettings settings
                        let! run = Csc.runOptions settings
                        do! Lock.buildWith { Lock.Options.Default with Run = run } path (Lock.Compilation.Csc c)
                }
            ]
        }

    member private x.Run (label: string) (body: Recipe<ExecContext, unit>) =
        x.RunWith label [ "CI", "off" ] body

    /// `Run` with explicit script variables (none: the environment variable `CI` decides).
    member private x.RunWith (label: string) (vars: (string * string) list) (body: Recipe<ExecContext, unit>) =
        xake {x.TestOptions with FileLog = label + ".log"; ThrowOnError = true; Vars = vars; FileLogLevel = Loud} {
            wantOverride ([label])
            rules [ label => body ]
        }

    [<Test; Category("Integration")>]
    member x.``records the lock on the first build and compiles``() =

        File.WriteAllText ("LockA.cs", "public class LockA {}\n")
        let lockPath = "locks/locka.json"
        if File.Exists lockPath then File.Delete lockPath

        // the builder spelling of the same thing: `lock` as the block's last operation
        x.Run "lock-a" (csc {
            src !!"LockA.cs"
            out (File.make "LockA.dll")
            target Library
            targetfwk "net-4.6.2"
            grefs ["System.dll"]
            lock lockPath
        })

        Assert.That(File.Exists lockPath, Is.True, "the lock file was not recorded")
        Assert.That(File.Exists "LockA.dll", Is.True, "csc did not produce LockA.dll")

        let doc = readLock lockPath
        Assert.That(doc.Entries, Has.Length.EqualTo 1)
        Assert.That(doc.Entries.Head.Csc.Framework, Is.EqualTo "net-4.6.2")
        let entry = doc.Entries.Head
        Assert.That(entry.Csc.Name, Is.EqualTo "LockA")
        Assert.That(entry.Csc.Sources |> List.exists (fun s -> s.EndsWith "LockA.cs"), Is.True)
        Assert.That(entry.Csc.Dependencies.References, Is.Not.Empty)
        Assert.That(entry.Csc.Dependencies.References |> List.forall (fun r -> r.Sha256 <> ""), Is.True,
            "the recorded entry has to be hashed -- that is what gates the next build")
        Assert.That(entry.Csc.Dependencies.Compiler.Sha256, Is.Not.Empty)

    [<Test; Category("Integration")>]
    member x.``second build with unchanged settings leaves the lock untouched``() =

        File.WriteAllText ("LockB.cs", "public class LockB {}\n")
        let lockPath = "locks/lockb.json"
        if File.Exists lockPath then File.Delete lockPath
        let settings = settings !!"LockB.cs" "LockB.dll" (Some lockPath)

        x.Build "lock-b" settings
        let bytes = File.ReadAllBytes lockPath
        let written = File.GetLastWriteTimeUtc lockPath

        x.Build "lock-b2" settings

        Assert.That(File.ReadAllBytes lockPath, Is.EqualTo bytes, "the second build rewrote the lock")
        Assert.That(File.GetLastWriteTimeUtc lockPath, Is.EqualTo written, "the second build touched the lock file")
        Assert.That(File.Exists "LockB.dll", Is.True, "csc did not produce LockB.dll")

    [<Test; Category("Integration")>]
    member x.``settings that moved away from the lock fail the build with the diff``() =

        File.WriteAllText ("LockC.cs", "public class LockC {}\n")
        File.WriteAllText ("LockC2.cs", "public class LockC2 {}\n")
        let lockPath = "locks/lockc.json"
        if File.Exists lockPath then File.Delete lockPath

        x.Build "lock-c" (settings !!"LockC.cs" "LockC.dll" (Some lockPath))

        let build () = x.Build "lock-c2" (settings (!!"LockC.cs" + "LockC2.cs") "LockC.dll" (Some lockPath))
        let ex = Assert.Throws<XakeException> (fun () -> build () |> ignore)

        Assert.That(ex.Data0, Does.Contain "LockC2.cs", "the diff has to name the source that was added")
        Assert.That(ex.Data0, Does.Contain "lock", "the message has to say a lock is what refused the build")
        // refusing is all it does: nothing is rewritten behind the user's back
        Assert.That((readLock lockPath).Entries.Head.Csc.Sources |> List.exists (fun s -> s.EndsWith "LockC2.cs"), Is.False)

    [<Test; Category("Integration")>]
    member x.``Lock.record overwrites the lock and the next build passes``() =

        File.WriteAllText ("LockD.cs", "public class LockD {}\n")
        File.WriteAllText ("LockD2.cs", "public class LockD2 {}\n")
        let lockPath = "locks/lockd.json"
        if File.Exists lockPath then File.Delete lockPath

        x.Build "lock-d" (settings !!"LockD.cs" "LockD.dll" (Some lockPath))

        // the settings moved: the idiomatic update step a script declares as its own target
        let updated = settings (!!"LockD.cs" + "LockD2.cs") "LockD.dll" (Some lockPath)
        x.Run "update-locks" (recipe {
            let! c = Csc.ofSettings (fst updated)
            do! Lock.record lockPath (Lock.Compilation.Csc c) })

        Assert.That((readLock lockPath).Entries.Head.Csc.Sources |> List.exists (fun s -> s.EndsWith "LockD2.cs"), Is.True,
            "Lock.record did not overwrite the lock with the new settings")

        x.Build "lock-d2" updated
        Assert.That(File.Exists "LockD.dll", Is.True, "csc did not produce LockD.dll after the lock was updated")

    /// The point of compiling the *recorded* entry rather than the resolved one: the hashes
    /// taken at record time are verified against disk by the runner, so a reference swapped
    /// afterwards fails the build even though the settings did not change. Same shape as
    /// `FromLockTests`' "refuses a reference whose hash changed", through `csc { lock }`.
    [<Test; Category("Integration")>]
    member x.``a reference tampered after recording fails the hash check``() =

        File.WriteAllText ("LockLib.cs", "public class LockLib { public static int F() { return 1; } }\n")
        File.WriteAllText ("LockE.cs", "public class LockE { public static int G() { return LockLib.F(); } }\n")
        let lockPath = "locks/locke.json"
        if File.Exists lockPath then File.Delete lockPath

        // the reference is built here, so tampering with it touches nothing shared
        x.Build "lock-e-lib" (settings !!"LockLib.cs" "LockLib.dll" None)
        Assert.That(File.Exists "LockLib.dll", Is.True, "the reference library was not built")

        let referencing =
            let s, path = settings !!"LockE.cs" "LockE.dll" (Some lockPath)
            { s with Ref = !!"LockLib.dll" }, path

        x.Build "lock-e" referencing
        Assert.That(File.Exists "LockE.dll", Is.True, "csc did not produce LockE.dll")

        // swap the reference for something else entirely, leaving the settings alone
        File.WriteAllBytes ("LockLib.dll", File.ReadAllBytes "LockLib.dll" |> Array.append [| 0uy; 1uy; 2uy |])

        let build () = x.Build "lock-e2" referencing
        let ex = Assert.Throws<XakeException> (fun () -> build () |> ignore)
        Assert.That(ex.Data0, Does.Contain "LockLib.dll")
        // the recorded hash is what caught it, not the settings-vs-lock diff (the settings
        // did not move, and the resolved side carries no hashes to compare)
        Assert.That(ex.Data0, Does.Contain "expected")

    /// `Lock.verify` -- the diff alone, no compile, nothing written (scenario 3).
    [<Test; Category("Integration")>]
    member x.``Lock.verify reports the difference without compiling``() =

        File.WriteAllText ("LockF.cs", "public class LockF {}\n")
        File.WriteAllText ("LockF2.cs", "public class LockF2 {}\n")
        let lockPath = "locks/lockf.json"
        if File.Exists lockPath then File.Delete lockPath

        x.Build "lock-f" (settings !!"LockF.cs" "LockF.dll" (Some lockPath))
        let bytes = File.ReadAllBytes lockPath

        let mutable unchanged : string list = ["not run"]
        let mutable changed : string list = []

        x.Run "verify-locks" (recipe {
            let! same = Csc.ofSettings (fst (settings !!"LockF.cs" "LockF.dll" None))
            let! same = Lock.verify lockPath (Lock.Compilation.Csc same)
            unchanged <- same
            let! moved = Csc.ofSettings (fst (settings (!!"LockF.cs" + "LockF2.cs") "LockF.dll" None))
            let! diff = Lock.verify lockPath (Lock.Compilation.Csc moved)
            changed <- diff
        })

        Assert.That(unchanged, Is.Empty, "an unchanged compilation has to verify clean")
        Assert.That(changed |> List.exists (fun line -> line.Contains "LockF2.cs"), Is.True)
        Assert.That(File.ReadAllBytes lockPath, Is.EqualTo bytes, "verify must not write the lock")

    // ---- a missing lock under CI ---------------------------------------------------------

    /// Each test that sets the environment variable `CI` holds the gate: the variable is
    /// process-wide.
    member private x.WithCi (value: string) (body: unit -> unit) =
        lock CscLockGate.gate (fun () ->
            let saved = System.Environment.GetEnvironmentVariable "CI"
            System.Environment.SetEnvironmentVariable ("CI", value)
            try body ()
            finally System.Environment.SetEnvironmentVariable ("CI", saved))

    member private x.LockBuild (label: string) (vars: (string * string) list) (source: string) (lockPath: string) =
        x.RunWith label vars (recipe {
            let! c = Csc.ofSettings (fst (settings !!source (Path.ChangeExtension (source, ".dll")) None))
            do! Lock.build lockPath (Lock.Compilation.Csc c)
        })

    static member private MissingUnderCi (name: string) (lockPath: string) =
        sprintf "'%s': the lock '%s' is not there. Under CI a lock is never recorded: record it on a developer machine (build once, or run the target that calls Lock.record \"%s\", e.g. update-locks) and commit it."
            name lockPath lockPath

    member private x.Fresh (name: string) =
        File.WriteAllText (name + ".cs", sprintf "public class %s {}\n" name)
        let lockPath = sprintf "locks/%s.json" (name.ToLowerInvariant ())
        if File.Exists lockPath then File.Delete lockPath
        if File.Exists (name + ".dll") then File.Delete (name + ".dll")
        lockPath

    [<Test; Category("Integration")>]
    member x.``under CI (script variable) a missing lock fails, nothing recorded or compiled``() =
        let lockPath = x.Fresh "LockG"

        let ex = Assert.Throws<XakeException> (fun () -> x.LockBuild "lock-g" [ "CI", "on" ] "LockG.cs" lockPath)
        Assert.That(ex.Data0, Does.Contain (``Csc lock``.MissingUnderCi "LockG" lockPath))
        Assert.That(File.Exists lockPath, Is.False, "under CI a missing lock must not be recorded")
        Assert.That(File.Exists "LockG.dll", Is.False, "under CI a missing lock must not compile")

    [<Test; Category("Integration")>]
    member x.``under CI FailOnError = false does not let a missing lock pass``() =
        let lockPath = x.Fresh "LockN"

        let run () =
            x.RunWith "lock-n" [ "CI", "true" ] (recipe {
                let s = fst (settings !!"LockN.cs" "LockN.dll" None)
                let! c = Csc.ofSettings s
                let! run = Csc.runOptions s
                do! Lock.buildWith { Lock.Options.Default with Run = { run with FailOnError = false } } lockPath (Lock.Compilation.Csc c)
            })
        let ex = Assert.Throws<XakeException> (fun () -> run ())
        Assert.That(ex.Data0, Does.Contain (``Csc lock``.MissingUnderCi "LockN" lockPath))
        Assert.That(File.Exists lockPath, Is.False)
        Assert.That(File.Exists "LockN.dll", Is.False)

    [<Test; Category("Integration")>]
    member x.``under CI (environment variable) a missing lock fails through csc { lock }``() =
        let lockPath = x.Fresh "LockH"

        x.WithCi "true" (fun () ->
            let run () =
                x.RunWith "lock-h" [] (csc {
                    src !!"LockH.cs"
                    out (File.make "LockH.dll")
                    target Library
                    targetfwk "net-4.6.2"
                    grefs ["System.dll"]
                    lock lockPath
                })
            let ex = Assert.Throws<XakeException> (fun () -> run ())
            Assert.That(ex.Data0, Does.Contain (``Csc lock``.MissingUnderCi "LockH" lockPath)))

        Assert.That(File.Exists lockPath, Is.False)
        Assert.That(File.Exists "LockH.dll", Is.False)

    [<Test; Category("Integration")>]
    member x.``-d CI=off with the environment variable set records as a developer would``() =
        let lockPath = x.Fresh "LockO"

        x.WithCi "true" (fun () -> x.LockBuild "lock-o" [ "CI", "off" ] "LockO.cs" lockPath)

        Assert.That(File.Exists lockPath, Is.True, "CI=off did not record the lock")
        Assert.That(File.Exists "LockO.dll", Is.True)

    [<Test; Category("Integration")>]
    member x.``not under CI a missing lock is recorded and compiled``() =
        let lockPath = x.Fresh "LockI"

        x.WithCi null (fun () -> x.LockBuild "lock-i" [] "LockI.cs" lockPath)

        Assert.That(File.Exists lockPath, Is.True, "not under CI the missing lock has to be recorded")
        Assert.That(File.Exists "LockI.dll", Is.True)

    [<Test; Category("Integration")>]
    member x.``under CI drift still fails with the differences``() =
        let lockPath = x.Fresh "LockP"
        File.WriteAllText ("LockP2.cs", "public class LockP2 {}\n")
        x.Build "lock-p" (settings !!"LockP.cs" "LockP.dll" (Some lockPath))

        let run () =
            x.RunWith "lock-p2" [ "CI", "on" ] (recipe {
                let! c = Csc.ofSettings (fst (settings (!!"LockP.cs" + "LockP2.cs") "LockP.dll" None))
                do! Lock.build lockPath (Lock.Compilation.Csc c)
            })
        let ex = Assert.Throws<XakeException> (fun () -> run ())
        Assert.That(ex.Data0, Does.Contain "differs from the lock")
        Assert.That(ex.Data0, Does.Contain "$(ProjectRoot)/LockP2.cs")

    [<Test; Category("Integration")>]
    member x.``an unrecognized CI value fails``() =
        let run () = x.RunWith "lock-q" [ "CI", "maybe" ] (recipe { let! _ = Lock.underCi in return () })
        let ex = Assert.Throws<XakeException> (fun () -> run ())
        Assert.That(ex.Data0, Does.Contain "CI='maybe'")

    // ---- drift messages and a missing lock in verify -------------------------------------

    [<Test; Category("Integration")>]
    member x.``drift messages name paths by their root token, not this checkout``() =
        File.WriteAllText ("LockJ.cs", "public class LockJ {}\n")
        File.WriteAllText ("LockJ2.cs", "public class LockJ2 {}\n")
        let lockPath = "locks/lockj.json"
        if File.Exists lockPath then File.Delete lockPath

        x.Build "lock-j" (settings !!"LockJ.cs" "LockJ.dll" (Some lockPath))

        let build () = x.Build "lock-j2" (settings (!!"LockJ.cs" + "LockJ2.cs") "LockJ.dll" (Some lockPath))
        let ex = Assert.Throws<XakeException> (fun () -> build () |> ignore)
        let root = x.TestOptions.ProjectRoot.Replace('\\', '/').TrimEnd '/'
        Assert.That(ex.Data0, Does.Contain "$(ProjectRoot)/LockJ2.cs")
        Assert.That(ex.Data0, Does.Not.Contain root)

        let mutable lines = []
        x.Run "verify-j" (recipe {
            let! moved = Csc.ofSettings (fst (settings (!!"LockJ.cs" + "LockJ2.cs") "LockJ.dll" None))
            let! diff = Lock.verify lockPath (Lock.Compilation.Csc moved)
            lines <- diff })
        Assert.That(lines, Has.Some.Contains "$(ProjectRoot)/LockJ2.cs")
        Assert.That(lines, Has.None.Contains root)

    [<Test; Category("Integration")>]
    member x.``Lock.verify on a missing lock fails naming it and Lock.record``() =
        File.WriteAllText ("LockK.cs", "public class LockK {}\n")
        let lockPath = "locks/lockk.json"
        if File.Exists lockPath then File.Delete lockPath

        let run () =
            x.Run "verify-k" (recipe {
                let! c = Csc.ofSettings (fst (settings !!"LockK.cs" "LockK.dll" None))
                let! _ = Lock.verify lockPath (Lock.Compilation.Csc c)
                return () })
        let ex = Assert.Throws<XakeException> (fun () -> run ())
        Assert.That(ex.Data0, Does.Contain lockPath)
        Assert.That(ex.Data0, Does.Contain "Lock.record")
        Assert.That(ex.Data0, Does.Not.Contain "Neither rule nor file")

    // ---- nofetch -------------------------------------------------------------------------
    // `packageroot` is gone in 0.2: the package folder is the build's one (`NUGET_PACKAGES`);
    // `nofetch` (formerly `norestore`) forbids fetching what that folder lacks.

    [<Test>]
    member x.``nofetch after lock turns fetching off, for csc and fsc``() =
        let (CscLocked (cscPath, _, cscFetch)) = csc.Lock (CscSettingsType.Default, "locks/a.json")
        let (CscLocked (_, _, cscNoFetch)) = csc.NoFetch (csc.Lock (CscSettingsType.Default, "locks/a.json"))
        Assert.That((cscPath, cscFetch, cscNoFetch), Is.EqualTo (("locks/a.json", true, false)))
        let (FscLocked (fscPath, _, fscFetch)) = fsc.Lock (FscSettingsType.Default, "locks/b.json")
        let (FscLocked (_, _, fscNoFetch)) = fsc.NoFetch (fsc.Lock (FscSettingsType.Default, "locks/b.json"))
        Assert.That((fscPath, fscFetch, fscNoFetch), Is.EqualTo (("locks/b.json", true, false)))

    /// What `nofetch` sets (`Restore.Options.Enabled = false`), against a package folder that
    /// lacks the lock's packages: the folder is a throwaway one (`Restore.into`, the lock read
    /// against it), so nothing in the machine's cache is touched.
    [<Test; Category("Integration")>]
    member x.``with fetching off a package the folder lacks fails before compiling``() =
        File.WriteAllText ("LockL.cs", "public class LockL {}\n")
        let lockPath = "locks/lockl.json"
        if File.Exists lockPath then File.Delete lockPath
        x.Build "lock-l" (settings !!"LockL.cs" "LockL.dll" (Some lockPath))
        File.Delete "LockL.dll"

        let packages = Path.Combine (Path.GetTempPath (), "xake-nofetch-" + System.Guid.NewGuid().ToString("N"))
        let doc = Lock.read (Roots.make (Directory.GetCurrentDirectory()) (Roots.packageRootOverride packages)) lockPath
        let underFolder = doc.Entries.Head.Csc.Dependencies.References |> List.filter (fun r -> r.Path.StartsWith packages)
        if List.isEmpty underFolder then Assert.Ignore "the composed compilation names no package reference here"

        try
            let ex =
                Assert.Throws<XakeException> (fun () ->
                    x.Run "lock-l2" (recipe {
                        let s = fst (settings !!"LockL.cs" "LockL.dll" None)
                        let! c = Csc.ofSettings s
                        let! run = Csc.runOptions s
                        let! restore = Restore.into packages
                        do! Lock.buildWith { Lock.Options.Default with Run = run; Restore = { restore with Enabled = false } } lockPath (Lock.Compilation.Csc c) }))
            Assert.That(ex.Data0, Does.Contain "got missing")
            Assert.That(ex.Data0, Does.Contain underFolder.Head.Path)
            let log = File.ReadAllText "lock-l2.log"
            Assert.That(log, Does.Contain "fetching is off (nofetch / NUGET_FETCH=off)", "the fetching-off warning was not reported")
            Assert.That(File.Exists "LockL.dll", Is.False, "nothing may be compiled")
            Assert.That(Directory.Exists packages, Is.False, "fetching was off, yet the package folder was written")
        finally
            try Directory.Delete (packages, true) with _ -> ()
