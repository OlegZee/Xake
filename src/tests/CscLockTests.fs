namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Tasks
open Xake.Dotnet

/// `csc { lock "path" }` -- migration path A of `lock-from-settings.md` §9, with the strict
/// semantics decided on 2026-09-24: the settings are the source of truth (`resolve` always
/// runs), the lock decides whether this is the compilation that was recorded. No lock: record
/// and compile. Lock present and matching: compile the *recorded* entry, so its hashes gate
/// the build. Lock present and different: fail with the diff. Updating is explicit --
/// `CscLock.record`, or deleting the file.
[<TestFixture>]
type ``Csc lock``() =
    inherit XakeTestBase("csc-lock")

    /// The shape the other integration tests here use: a net-4.6.2 library, one source file.
    let settings (src: Fileset) (out: string) (lockPath: string option) = {
        CscSettingsType.Default with
            Src = src
            Out = File.make out
            Target = Library
            TargetFramework = "net-4.6.2"
            RefGlobal = ["System.dll"]
            Lock = lockPath
    }

    let readLock (path: string) =
        Lock.readWith (Roots.builtin (Directory.GetCurrentDirectory())) path

    member private x.Build (label: string) (settings: CscSettingsType) =
        xake {x.TestOptions with FileLog = label + ".log"; ThrowOnError = true} {
            wantOverride ([label])
            rules [ label => recipe { do! Csc settings } ]
        }

    member private x.Run (label: string) (body: Recipe<ExecContext, unit>) =
        xake {x.TestOptions with FileLog = label + ".log"; ThrowOnError = true} {
            wantOverride ([label])
            rules [ label => body ]
        }

    [<Test; Category("Integration")>]
    member x.``records the lock on the first build and compiles``() =

        File.WriteAllText ("LockA.cs", "public class LockA {}\n")
        let lockPath = "locks/locka.json"
        if File.Exists lockPath then File.Delete lockPath

        x.Build "lock-a" (settings !!"LockA.cs" "LockA.dll" (Some lockPath))

        Assert.That(File.Exists lockPath, Is.True, "the lock file was not recorded")
        Assert.That(File.Exists "LockA.dll", Is.True, "csc did not produce LockA.dll")

        let doc = readLock lockPath
        Assert.That(doc.Entries, Has.Length.EqualTo 1)
        Assert.That(doc.Framework, Is.EqualTo "net-4.6.2")
        let entry = doc.Entries.Head
        Assert.That(entry.Name, Is.EqualTo "LockA")
        Assert.That(entry.Sources |> List.exists (fun s -> s.EndsWith "LockA.cs"), Is.True)
        Assert.That(entry.Dependencies.References, Is.Not.Empty)
        Assert.That(entry.Dependencies.References |> List.forall (fun r -> r.Sha256 <> ""), Is.True,
            "the recorded entry has to be hashed -- that is what gates the next build")
        Assert.That(entry.Dependencies.Compiler.Sha256, Is.Not.Empty)

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
        Assert.That((readLock lockPath).Entries.Head.Sources |> List.exists (fun s -> s.EndsWith "LockC2.cs"), Is.False)

    [<Test; Category("Integration")>]
    member x.``CscLock.record overwrites the lock and the next build passes``() =

        File.WriteAllText ("LockD.cs", "public class LockD {}\n")
        File.WriteAllText ("LockD2.cs", "public class LockD2 {}\n")
        let lockPath = "locks/lockd.json"
        if File.Exists lockPath then File.Delete lockPath

        x.Build "lock-d" (settings !!"LockD.cs" "LockD.dll" (Some lockPath))

        // the settings moved: the idiomatic update step a script declares as its own target
        let updated = settings (!!"LockD.cs" + "LockD2.cs") "LockD.dll" (Some lockPath)
        x.Run "update-locks" (recipe { do! CscLock.record lockPath updated })

        Assert.That((readLock lockPath).Entries.Head.Sources |> List.exists (fun s -> s.EndsWith "LockD2.cs"), Is.True,
            "CscLock.record did not overwrite the lock with the new settings")

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
            { settings !!"LockE.cs" "LockE.dll" (Some lockPath) with Ref = !!"LockLib.dll" }

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

    /// `CscLock.verify` -- the diff alone, no compile, nothing written (scenario 3).
    [<Test; Category("Integration")>]
    member x.``CscLock.verify reports the difference without compiling``() =

        File.WriteAllText ("LockF.cs", "public class LockF {}\n")
        File.WriteAllText ("LockF2.cs", "public class LockF2 {}\n")
        let lockPath = "locks/lockf.json"
        if File.Exists lockPath then File.Delete lockPath

        x.Build "lock-f" (settings !!"LockF.cs" "LockF.dll" (Some lockPath))
        let bytes = File.ReadAllBytes lockPath

        let mutable unchanged : string list = ["not run"]
        let mutable changed : string list = []

        x.Run "verify-locks" (recipe {
            let! same = CscLock.verify lockPath (settings !!"LockF.cs" "LockF.dll" (Some lockPath))
            unchanged <- same
            let! diff = CscLock.verify lockPath (settings (!!"LockF.cs" + "LockF2.cs") "LockF.dll" (Some lockPath))
            changed <- diff
        })

        Assert.That(unchanged, Is.Empty, "an unchanged compilation has to verify clean")
        Assert.That(changed |> List.exists (fun line -> line.Contains "LockF2.cs"), Is.True)
        Assert.That(File.ReadAllBytes lockPath, Is.EqualTo bytes, "verify must not write the lock")
