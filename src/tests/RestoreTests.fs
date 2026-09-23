namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Dotnet

/// `Restore`: turning the packages a lock names back into files on disk, in a folder the
/// build chooses. The pure half (which packages are missing, what the restore project says,
/// what the nupkg hash check reports) needs no network; the integration half restores a real
/// package into a scratch folder and compiles against it.
[<TestFixture>]
type ``Restore``() =
    inherit XakeTestBase("restore")

    /// A package that is small (2 MB), has a stable `ref/netstandard2.0` assembly, and is on
    /// nuget.org -- so the integration tests can fetch it into a scratch folder cheaply.
    let packageId, packageVersion = "Microsoft.Win32.Registry", "5.0.0"
    let refPath (root: string) =
        Path.Combine (root, packageId.ToLowerInvariant (), packageVersion, "ref", "netstandard2.0", packageId + ".dll")

    let userNugetRoot = Roots.nugetRoot ()

    /// An entry that compiles one source file against `netstandard.dll` (wherever this
    /// machine has it) plus one assembly from `packageId`, taken from `packageRoot`. The
    /// second reference is what a lock brings to a machine whose package folder is empty.
    let makeEntry (dir: string) (packageRoot: string) (refSha256: string) (packageSha512: string) =
        let fwk = DotNetFwk.locateFramework (Some "netstandard2.0")
        let cscDll =
            if Impl.endsWith ".dll" fwk.CscTool then fwk.CscTool
            else Path.Combine (Path.GetDirectoryName fwk.CscTool, "csc.dll")
        // `DotNetFwk.locateFramework` memoizes, and another fixture in this assembly points
        // `NUGET_PACKAGES` at a scratch folder while it runs: whichever call warms the memo
        // first decides where netstandard.dll is said to live. Fall back to the machine's own
        // cache rather than inheriting a stale answer and failing on a path that never existed.
        let netstandardDll =
            match DotNetFwk.locateAssembly fwk "netstandard.dll" with
            | located when File.Exists located -> located
            | _ ->
                let cached = Path.Combine (userNugetRoot, "netstandard.library", "2.0.3", "build", "netstandard2.0", "ref", "netstandard.dll")
                if not (File.Exists cached) then Assert.Ignore "netstandard.dll was not found on this machine"
                cached

        let helloCs = Path.Combine (dir, "Hello.cs")
        File.WriteAllText (helloCs, "public class Hello {}\n")
        let outDll = Path.Combine (dir, "obj", "Hello.dll")

        let compilation, references, analyzers =
            Lock.Compilation.ofArgs
                [ "/noconfig"; "/nostdlib+"; "/target:library"; "/deterministic+"
                  "/reference:" + netstandardDll
                  "/reference:" + refPath packageRoot
                  "/out:" + outDll
                  helloCs ]
        let entry : Lock.Entry = {
            Name = "Hello"
            Framework = "netstandard2.0"
            Evaluation = { Project = Path.Combine (dir, "Hello.csproj"); ProjectRefs = []; Imports = []; Sdk = fwk.Version; SdkPin = None; Properties = Map.empty }
            Compilation = { compilation with Directory = dir }
            Dependencies =
                { Compiler = { Tool = "csc"; Path = cscDll; Sha256 = Lock.sha256 cscDll; Version = Lock.compilerVersion cscDll }
                  References =
                    references |> List.map (fun r ->
                        if r.Path = refPath packageRoot then { r with Sha256 = refSha256 }
                        else { r with Sha256 = Lock.sha256 r.Path })
                  Analyzers = analyzers
                  Packages = [ { Id = packageId; Version = packageVersion; Sha512 = packageSha512; Direct = true; DependsOn = [] } ] }
        }
        entry, outDll

    let scratchRoot () = Path.Combine (Path.GetTempPath (), "xake-restore-" + System.Guid.NewGuid().ToString("N"))

    // ---- what is missing ----------------------------------------------------------------

    [<Test>]
    member __.``missing names the package of every absent file under the folder, and ignores the rest``() =
        let root = scratchRoot ()
        let entry, _ = makeEntry (Directory.GetCurrentDirectory()) root "" "sha512-of-the-nupkg"
        let options = { Restore.Options.Default with PackageRoot = Some root }

        match Restore.missing options [entry] with
        | [ one ] ->
            // the original casing and the nupkg hash come from the lock's package graph, not
            // from the (lowercase) directory name in the path
            Assert.That(one.Id, Is.EqualTo packageId)
            Assert.That(one.Version, Is.EqualTo packageVersion)
            Assert.That(one.Sha512, Is.EqualTo "sha512-of-the-nupkg")
            Assert.That(one.Files |> List.length, Is.EqualTo 1)
            Assert.That(one.Files.Head.Replace('\\', '/'), Is.EqualTo ((refPath root).Replace('\\', '/')))
        | other -> Assert.Fail(sprintf "expected exactly the one package, got %A" other)

    [<Test>]
    member __.``missing says nothing when every file the entry names is there``() =
        // point the folder at the machine's own cache: `netstandard.dll` and the compiler are
        // either outside it (ignored) or in it (present), and no reference is absent
        let entry, _ = makeEntry (Directory.GetCurrentDirectory()) userNugetRoot "" ""
        let entry = { entry with Dependencies = { entry.Dependencies with References = entry.Dependencies.References |> List.filter (fun r -> File.Exists r.Path) } }
        Assert.That(Restore.missing Restore.Options.Default [entry], Is.Empty)

    [<Test>]
    member __.``one restore project lists every package at an exact version``() =
        let text = Restore.projectText [ "A.B", "1.2.3"; "C", "4.5.6-pre" ]
        Assert.That(text, Does.Contain "<PackageDownload Include=\"A.B\" Version=\"[1.2.3]\" />")
        Assert.That(text, Does.Contain "<PackageDownload Include=\"C\" Version=\"[4.5.6-pre]\" />")
        // no PackageReference: nothing here resolves a version or walks a dependency graph
        Assert.That(text, Does.Not.Contain "PackageReference")

    // ---- the nupkg hash -----------------------------------------------------------------

    [<Test>]
    member __.``verify reports a package that is absent, and one whose nupkg hash is not the recorded one``() =
        let root = scratchRoot ()
        let dir = Path.Combine (root, "some.package", "1.0.0")
        Directory.CreateDirectory dir |> ignore
        File.WriteAllText (Path.Combine (dir, ".nupkg.metadata"), "{ \"version\": 2, \"contentHash\": \"aaa==\" }")
        let options = { Restore.Options.Default with PackageRoot = Some root }
        let package id version sha512 : Restore.Missing = { Id = id; Version = version; Sha512 = sha512; Files = [] }

        try
            Assert.That(Restore.verify options [ package "Some.Package" "1.0.0" "aaa==" ], Is.Empty)

            let mismatch = Restore.verify options [ package "Some.Package" "1.0.0" "bbb==" ]
            Assert.That(mismatch |> List.length, Is.EqualTo 1)
            Assert.That(mismatch.Head, Does.Contain "Some.Package 1.0.0")
            Assert.That(mismatch.Head, Does.Contain "expected sha512 bbb==, got aaa==")

            // no hash recorded means nothing to compare against, not a failure
            Assert.That(Restore.verify options [ package "Some.Package" "1.0.0" "" ], Is.Empty)

            let absent = Restore.verify options [ package "Other.Package" "2.0.0" "aaa==" ]
            Assert.That(absent |> List.length, Is.EqualTo 1)
            Assert.That(absent.Head, Does.Contain "not restored")
        finally
            try Directory.Delete (root, true) with _ -> ()

    // ---- the folder itself --------------------------------------------------------------

    [<Test>]
    member x.``into takes the package folder relative to the project root``() =
        let mutable root = ""
        do xake {x.TestOptions with FileLog="restore-into.log"; ThrowOnError = true} {
            wantOverride (["into"])
            rules [
                "into" => recipe {
                    let! options = Restore.into ".packages"
                    root <- Restore.packageRoot options
                }
            ]
        }
        Assert.That(root, Is.EqualTo ((x.TestOptions.ProjectRoot </> ".packages").Replace('\\', '/')))

    // ---- end to end ---------------------------------------------------------------------

    /// The gap this module closes: a lock naming a reference package that the folder does not
    /// have used to fail the build with `expected <sha256>, got missing`. It now restores into
    /// the folder the build names -- not the machine's cache -- and compiles.
    [<Test; Category("Integration")>]
    member x.``restores a missing reference package into the build's own folder and compiles``() =
        let cached = refPath userNugetRoot
        Assume.That(File.Exists cached, Is.True,
            sprintf "%s %s is not in this machine's cache (%s) -- nothing to take the expected hashes from" packageId packageVersion cached)

        let root = scratchRoot ()
        try
            let dir = Directory.GetCurrentDirectory()
            let sha512 = (Nuget.readCache userNugetRoot packageId packageVersion).Sha512
            let entry, outDll = makeEntry dir root (Lock.sha256 cached) sha512
            if File.Exists outDll then File.Delete outDll
            Assert.That(File.Exists (refPath root), Is.False, "the scratch folder already has the package -- test setup is wrong")

            let options = { RunOptions.Default with Restore = { Restore.Options.Default with PackageRoot = Some root } }
            do xake {x.TestOptions with FileLog="restore-e2e.log"; ThrowOnError = true} {
                wantOverride (["hello-restored"])
                rules [ "hello-restored" => recipe { do! CscLock.compileWith options entry } ]
            }

            Assert.That(File.Exists (refPath root), Is.True, "the reference package was not restored into the build's own folder")
            Assert.That(File.Exists (Path.Combine (userNugetRoot, "restore-should-not-have-touched-this")), Is.False)
            Assert.That(File.Exists outDll, Is.True, "csc did not produce Hello.dll after the restore")
        finally
            try Directory.Delete (root, true) with _ -> ()

    /// The opt-out: a build that must not reach the network fails exactly as it did before
    /// this module existed -- every missing file named with its expected hash.
    [<Test; Category("Integration")>]
    member x.``with restore off the missing reference is reported, not fetched``() =
        let root = scratchRoot ()
        try
            let dir = Directory.GetCurrentDirectory()
            let entry, _ = makeEntry dir root (String.replicate 64 "a") ""
            let options = { RunOptions.Default with Restore = { PackageRoot = Some root; Enabled = false } }

            let build () =
                xake {x.TestOptions with FileLog="restore-off.log"; ThrowOnError = true} {
                    wantOverride (["hello-no-restore"])
                    rules [ "hello-no-restore" => recipe { do! CscLock.compileWith options entry } ]
                }
            let ex = Assert.Throws<XakeException> (fun () -> build () |> ignore)
            Assert.That(ex.Data0, Does.Contain "got missing")
            Assert.That(ex.Data0, Does.Contain (refPath root))
            Assert.That(Directory.Exists root, Is.False, "restore was off, yet something was written to the package folder")
        finally
            try Directory.Delete (root, true) with _ -> ()

    /// The lock's own nupkg hash is checked against what the restore delivered, so a package
    /// that is not the one the lock recorded is named before anything compiles.
    [<Test; Category("Integration")>]
    member x.``a restored package whose nupkg hash is not the recorded one fails, naming it``() =
        let root = scratchRoot ()
        try
            let dir = Directory.GetCurrentDirectory()
            let entry, _ = makeEntry dir root "" "not-the-hash-of-this-nupkg=="
            let options = { RunOptions.Default with Restore = { Restore.Options.Default with PackageRoot = Some root } }

            let build () =
                xake {x.TestOptions with FileLog="restore-sha512.log"; ThrowOnError = true} {
                    wantOverride (["hello-bad-sha512"])
                    rules [ "hello-bad-sha512" => recipe { do! CscLock.compileWith options entry } ]
                }
            let ex = Assert.Throws<XakeException> (fun () -> build () |> ignore)
            Assert.That(ex.Data0, Does.Contain (packageId + " " + packageVersion))
            Assert.That(ex.Data0, Does.Contain "expected sha512 not-the-hash-of-this-nupkg==")
        finally
            try Directory.Delete (root, true) with _ -> ()
