namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Dotnet

/// `Lock.rehash` (fills hashes from what is on disk) and `Lock.diff` (a pure, human-readable
/// comparison of two locks) -- the two pieces `lock-from-settings.md` recommendations 5 and 3
/// add on top of `Csc.resolve` (here `CscLock.resolve`, see `FromLockTests.fs`). Neither needs
/// the compiler or msbuild.
[<TestFixture>]
type ``Lock rehash and diff``() =

    let sampleProject () : Lock.Project =
        { Name = "Sample"
          Project = "/proj/Sample.csproj"
          Directory = "/proj"
          Compiler = { Tool = "csc"; Path = "/sdk/csc.dll"; Sha256 = ""; Sdk = "8.0.100" }
          Args = [ "/target:library"; "/out:/proj/obj/Sample.dll"; "A.cs" ]
          References = [ { Path = "/pkgs/a.dll"; Sha256 = "hash-a" } ]
          Analyzers = []
          ProjectRefs = []
          Imports = []
          Generated = [ "/proj/obj/AssemblyInfo.cs", "// v1" ]
          Resources = []
          Properties = Map.empty }

    [<Test>]
    member x.``rehash fills hashes for files that exist, leaves missing ones empty``() =
        let dir = Path.Combine (Path.GetTempPath(), "xake-lockdiff-" + System.Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            let existing = Path.Combine (dir, "a.dll")
            File.WriteAllText (existing, "hello")
            let missing = Path.Combine (dir, "missing.dll")

            let project =
                { sampleProject () with
                    References = [ { Path = existing; Sha256 = "" } ]
                    Analyzers = [ { Path = missing; Sha256 = "" } ]
                    Imports = [ { Path = existing; Sha256 = "" } ]
                    Compiler = { Tool = "csc"; Path = existing; Sha256 = ""; Sdk = "8.0.100" } }

            let rehashed = Lock.rehash project

            Assert.That(rehashed.References.[0].Sha256, Is.EqualTo (Lock.sha256 existing))
            Assert.That(rehashed.References.[0].Sha256, Is.Not.Empty)
            Assert.That(rehashed.Analyzers.[0].Sha256, Is.EqualTo "")
            Assert.That(rehashed.Imports.[0].Sha256, Is.EqualTo (Lock.sha256 existing))
            Assert.That(rehashed.Compiler.Sha256, Is.EqualTo (Lock.sha256 existing))
        finally
            Directory.Delete (dir, true)

    [<Test>]
    member x.``diff of a lock against itself is empty``() =
        let project = sampleProject ()
        Assert.That(Lock.diff project project, Is.Empty)

    [<Test>]
    member x.``diff reports a changed arg, a changed reference hash, and a changed generated file``() =
        let a = sampleProject ()
        let b =
            { a with
                Args = [ "/target:library"; "/out:/proj/obj/Sample.dll"; "B.cs" ]
                References = [ { Path = "/pkgs/a.dll"; Sha256 = "hash-b" } ]
                Generated = [ "/proj/obj/AssemblyInfo.cs", "// v2" ] }

        let lines = Lock.diff a b

        Assert.That(List.length lines, Is.EqualTo 4)
        Assert.That(lines, Is.EqualTo [
            "- A.cs"
            "+ B.cs"
            "~ Reference /pkgs/a.dll: hash-a -> hash-b"
            "~ Generated /proj/obj/AssemblyInfo.cs: content changed" ])
