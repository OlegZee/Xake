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

    let sampleEntry () : Lock.Entry =
        let compilation, references, analyzers =
            Lock.Compilation.ofArgs [ "/target:library"; "/define:TRACE"; "/out:/proj/obj/Sample.dll"; "/reference:/pkgs/a.dll"; "A.cs" ]
        { Name = "Sample"
          Evaluation = { Project = "/proj/Sample.csproj"; ProjectRefs = []; Imports = []; Sdk = "8.0.100"; SdkPin = None; Properties = Map.empty }
          Compilation = { compilation with Directory = "/proj"; Generated = [ "/proj/obj/AssemblyInfo.cs", "// v1" ] }
          Dependencies =
            { Compiler = { Tool = "csc"; Path = "/sdk/csc.dll"; Sha256 = ""; Version = "4.11.0" }
              References = references |> List.map (fun r -> { r with Sha256 = "hash-a" })
              Analyzers = analyzers
              Packages = [ { Id = "Foo.Bar"; Version = "1.2.3"; Sha512 = "AAAA"; Direct = true; DependsOn = [] } ] } }

    [<Test>]
    member x.``rehash fills hashes for files that exist, leaves missing ones empty``() =
        let dir = Path.Combine (Path.GetTempPath(), "xake-lockdiff-" + System.Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            let existing = Path.Combine (dir, "a.dll")
            File.WriteAllText (existing, "hello")
            let missing = Path.Combine (dir, "missing.dll")

            let entry =
                { sampleEntry () with
                    Evaluation = { (sampleEntry ()).Evaluation with Imports = [ { Path = existing; Sha256 = "" } ] }
                    Dependencies =
                        { References = [ { Path = existing; Sha256 = ""; Alias = "" } ]
                          Analyzers = [ { Path = missing; Sha256 = "" } ]
                          Compiler = { Tool = "csc"; Path = existing; Sha256 = ""; Version = "" }
                          Packages = [] } }

            let rehashed = Lock.rehash entry

            Assert.That(rehashed.Dependencies.References.[0].Sha256, Is.EqualTo (Lock.sha256 existing))
            Assert.That(rehashed.Dependencies.References.[0].Sha256, Is.Not.Empty)
            Assert.That(rehashed.Dependencies.Analyzers.[0].Sha256, Is.EqualTo "")
            Assert.That(rehashed.Evaluation.Imports.[0].Sha256, Is.EqualTo (Lock.sha256 existing))
            Assert.That(rehashed.Dependencies.Compiler.Sha256, Is.EqualTo (Lock.sha256 existing))
        finally
            Directory.Delete (dir, true)

    [<Test>]
    member x.``diff of a lock against itself is empty``() =
        let entry = sampleEntry ()
        Assert.That(Lock.diff entry entry, Is.Empty)

    [<Test>]
    member x.``diff reports a changed source, a changed define, a changed reference hash, and a changed generated file``() =
        let a = sampleEntry ()
        let b =
            { a with
                Compilation =
                    { a.Compilation with
                        Sources = [ "B.cs" ]
                        Defines = [ "DEBUG" ]
                        Generated = [ "/proj/obj/AssemblyInfo.cs", "// v2" ] }
                Dependencies =
                    { a.Dependencies with
                        References = [ { Path = "/pkgs/a.dll"; Sha256 = "hash-b"; Alias = "" } ]
                        Compiler = { a.Dependencies.Compiler with Version = "4.12.0" } } }

        let lines = Lock.diff a b

        Assert.That(lines, Is.EqualTo [
            "- A.cs"
            "+ B.cs"
            "- Define TRACE"
            "+ Define DEBUG"
            "~ Compiler.Version: 4.11.0 -> 4.12.0"
            "~ Reference /pkgs/a.dll: hash-a -> hash-b"
            "~ Generated /proj/obj/AssemblyInfo.cs: content changed" ])

    [<Test>]
    member x.``diff reports packages added, removed, upgraded, and re-hashed``() =
        let a = sampleEntry ()
        let packagesA : Lock.Package list =
            [ { Id = "Foo.Bar"; Version = "1.2.3"; Sha512 = "AAAA"; Direct = true; DependsOn = [] }
              { Id = "Gone"; Version = "1.0.0"; Sha512 = ""; Direct = false; DependsOn = [] }
              { Id = "Same"; Version = "2.0.0"; Sha512 = "S1"; Direct = false; DependsOn = [] } ]
        let packagesB : Lock.Package list =
            [ { Id = "Foo.Bar"; Version = "1.3.0"; Sha512 = "BBBB"; Direct = true; DependsOn = [] }
              { Id = "New"; Version = "0.1.0"; Sha512 = ""; Direct = false; DependsOn = [] }
              { Id = "Same"; Version = "2.0.0"; Sha512 = "S2"; Direct = false; DependsOn = [] } ]
        let withPackages ps = { a with Dependencies = { a.Dependencies with Packages = ps } }

        Assert.That(Lock.diff (withPackages packagesA) (withPackages packagesB), Is.EqualTo [
            "~ Package Foo.Bar: 1.2.3 -> 1.3.0"
            "- Package Gone@1.0.0"
            "+ Package New@0.1.0"
            "~ Package Same@2.0.0: sha512 changed" ])
