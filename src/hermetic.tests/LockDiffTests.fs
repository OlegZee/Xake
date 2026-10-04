namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet

/// `Lock.rehash` (fills hashes from what is on disk) and `Lock.diff` (a pure, human-readable
/// comparison of two locks) -- the two pieces `lock-from-settings.md` recommendations 5 and 3
/// add on top of `Csc.ofSettings` (`csc { ...; resolve }`, see `FromLockTests.fs`). Neither needs
/// the compiler or msbuild.
[<TestFixture>]
type ``Lock rehash and diff``() =

    let sampleEntry () : Lock.Entry =
        let c = Csc.ofArgs [ "/target:library"; "/define:TRACE"; "/out:/proj/obj/Sample.dll"; "/reference:/pkgs/a.dll"; "A.cs" ]
        { Compilation = Lock.Compilation.Csc
            { c with
                Name = "Sample"
                Framework = "netstandard2.0"
                Directory = "/proj"
                Generated = [ "/proj/obj/AssemblyInfo.cs", "// v1" ]
                Dependencies =
                  { c.Dependencies with
                      Compiler = { Tool = "csc"; Path = "/sdk/csc.dll"; Sha256 = ""; Version = "4.11.0" }
                      References = c.Dependencies.References |> List.map (fun r -> { r with Sha256 = "hash-a" }) } }
          Evaluation = { Project = "/proj/Sample.csproj"; ProjectRefs = []; Imports = []; Sdk = "8.0.100"; SdkPin = None; Properties = Map.empty }
          Packages = [ { Id = "Foo.Bar"; Version = "1.2.3"; Sha512 = "AAAA"; Direct = true; DependsOn = [] } ] }

    [<Test>]
    member x.``rehash fills hashes for files that exist, leaves missing ones empty``() =
        let dir = Path.Combine (Path.GetTempPath(), "xake-lockdiff-" + System.Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory dir |> ignore
        try
            let existing = Path.Combine (dir, "a.dll")
            File.WriteAllText (existing, "hello")
            let missing = Path.Combine (dir, "missing.dll")

            let sample = sampleEntry ()
            let entry =
                { sample with
                    Evaluation = { sample.Evaluation with Imports = [ { Path = existing; Sha256 = "" } ] }
                    Compilation = Lock.Compilation.Csc
                        { sample.Csc with
                            Dependencies =
                                { References = [ { Path = existing; Sha256 = ""; Alias = "" } ]
                                  Analyzers = [ { Path = missing; Sha256 = "" } ]
                                  Compiler = { Tool = "csc"; Path = existing; Sha256 = ""; Version = "" } } }
                    Packages = [] }

            let rehashed = Lock.rehash entry

            Assert.That(rehashed.Csc.Dependencies.References.[0].Sha256, Is.EqualTo (Csc.sha256 existing))
            Assert.That(rehashed.Csc.Dependencies.References.[0].Sha256, Is.Not.Empty)
            Assert.That(rehashed.Csc.Dependencies.Analyzers.[0].Sha256, Is.EqualTo "")
            Assert.That(rehashed.Evaluation.Imports.[0].Sha256, Is.EqualTo (Csc.sha256 existing))
            Assert.That(rehashed.Csc.Dependencies.Compiler.Sha256, Is.EqualTo (Csc.sha256 existing))
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
                Compilation = Lock.Compilation.Csc
                    { a.Csc with
                        Sources = [ "B.cs" ]
                        Defines = [ "DEBUG" ]
                        Generated = [ "/proj/obj/AssemblyInfo.cs", "// v2" ]
                        Dependencies =
                            { a.Csc.Dependencies with
                                References = [ { Path = "/pkgs/a.dll"; Sha256 = "hash-b"; Alias = "" } ]
                                Compiler = { a.Csc.Dependencies.Compiler with Version = "4.12.0" } } } }

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
        let withPackages ps = { a with Packages = ps }

        Assert.That(Lock.diff (withPackages packagesA) (withPackages packagesB), Is.EqualTo [
            "~ Package Foo.Bar: 1.2.3 -> 1.3.0"
            "- Package Gone@1.0.0"
            "+ Package New@0.1.0"
            "~ Package Same@2.0.0: sha512 changed" ])

/// The lock file format did not move when `Lock.Entry` became `{ Csc; Evaluation; Packages }`:
/// the writer flattens the entry back into the `Name`/`Framework`/`Evaluation`/`Compilation`/
/// `Dependencies` shape (with `Packages` inside `Dependencies`) and the reader regroups it. The
/// expected text below was written by the pre-split `Lock.format` (commit 99faffc) from the
/// same entry, so this pins the format byte for byte.
[<TestFixture>]
type ``Lock file format``() =

    let roots = [ "$(NuGetPackageRoot)", "/r/pkgs"; "$(ProjectRoot)", "/r/proj"; "$(DotnetRoot)", "/r/dotnet" ]

    let golden = """{
  "Configuration": "Release",
  "Properties": {
    "Brand": "X"
  },
  "Entries": [
    {
      "Name": "Sample",
      "Framework": "netstandard2.0",
      "Evaluation": {
        "Project": "$(ProjectRoot)/src/Sample/Sample.csproj",
        "ProjectRefs": [
          "$(ProjectRoot)/src/Other/Other.csproj"
        ],
        "Imports": [
          { "Path": "$(ProjectRoot)/Directory.Build.props", "Sha256": "0a0b" }
        ],
        "Sdk": "8.0.100",
        "SdkPin": "8.0.100 rollForward:latestFeature",
        "Properties": {
          "AssemblyName": "Sample",
          "Version": "1.0.0"
        }
      },
      "Compilation": {
        "Directory": "$(ProjectRoot)/src/Sample",
        "Options": [
          "/noconfig",
          "@Defines",
          "@References",
          "@Analyzers",
          "/out:$(ProjectRoot)/src/Sample/obj/Sample.dll",
          "@Sources",
          "/warnaserror+:NU1605"
        ],
        "Defines": [
          "TRACE",
          "RELEASE"
        ],
        "Sources": [
          "$(ProjectRoot)/src/Sample/A.cs"
        ],
        "Generated": {
          "$(ProjectRoot)/src/Sample/obj/AssemblyInfo.cs": "// <auto>\r\n[assembly: A(\"x\")]\n"
        },
        "Resources": {
          "$(ProjectRoot)/src/Sample/S.resx": "$(ProjectRoot)/src/Sample/obj/S.resources"
        }
      },
      "Dependencies": {
        "Compiler": { "Tool": "csc", "Path": "$(DotnetRoot)/sdk/8.0.100/Roslyn/bincore/csc.dll", "Sha256": "c0ffee", "Version": "4.8.0-7.23572.1" },
        "References": [
          { "Path": "$(NuGetPackageRoot)/foo.bar/1.2.3/lib/netstandard2.0/Foo.Bar.dll", "Sha256": "cd02" },
          { "Path": "$(ProjectRoot)/src/Other/bin/Other.dll", "Sha256": "", "Alias": "ext" }
        ],
        "Analyzers": [
          { "Path": "$(DotnetRoot)/sdk/8.0.100/an.dll", "Sha256": "ef03" }
        ],
        "Packages": [
          { "Id": "Foo.Bar", "Version": "1.2.3", "Sha512": "AAAA==", "Direct": true, "DependsOn": ["Baz.Qux"] },
          { "Id": "Baz.Qux", "Version": "4.5.6", "Sha512": "", "Direct": false, "DependsOn": [] }
        ]
      }
    }
  ]
}
"""

    let document () : Lock.Document =
        let c =
            Csc.ofArgs
                [ "/noconfig"; "/define:TRACE;RELEASE"; "/reference:/r/pkgs/foo.bar/1.2.3/lib/netstandard2.0/Foo.Bar.dll"
                  "/reference:ext=/r/proj/src/Other/bin/Other.dll"; "/analyzer:/r/dotnet/sdk/8.0.100/an.dll"
                  "/out:/r/proj/src/Sample/obj/Sample.dll"; "/r/proj/src/Sample/A.cs"; "/warnaserror+:NU1605" ]
        let entry : Lock.Entry =
            { Compilation = Lock.Compilation.Csc
                { c with
                    Name = "Sample"
                    Framework = "netstandard2.0"
                    Directory = "/r/proj/src/Sample"
                    Generated = [ "/r/proj/src/Sample/obj/AssemblyInfo.cs", "// <auto>\r\n[assembly: A(\"x\")]\n" ]
                    Resources = [ "/r/proj/src/Sample/S.resx", "/r/proj/src/Sample/obj/S.resources" ]
                    Dependencies =
                        { Compiler = { Tool = "csc"; Path = "/r/dotnet/sdk/8.0.100/Roslyn/bincore/csc.dll"; Sha256 = "c0ffee"; Version = "4.8.0-7.23572.1" }
                          References = c.Dependencies.References |> List.mapi (fun i r -> if i = 0 then { r with Sha256 = "cd02" } else r)
                          Analyzers = c.Dependencies.Analyzers |> List.map (fun a -> { a with Sha256 = "ef03" }) } }
              Evaluation =
                { Project = "/r/proj/src/Sample/Sample.csproj"; ProjectRefs = [ "/r/proj/src/Other/Other.csproj" ]
                  Imports = [ { Path = "/r/proj/Directory.Build.props"; Sha256 = "0a0b" } ]
                  Sdk = "8.0.100"; SdkPin = Some (Lock.RollsForward ("8.0.100", "latestFeature"))
                  Properties = Map.ofList [ "AssemblyName", "Sample"; "Version", "1.0.0" ] }
              Packages =
                [ { Id = "Foo.Bar"; Version = "1.2.3"; Sha512 = "AAAA=="; Direct = true; DependsOn = [ "Baz.Qux" ] }
                  { Id = "Baz.Qux"; Version = "4.5.6"; Sha512 = ""; Direct = false; DependsOn = [] } ] }
        { Configuration = "Release"; Properties = [ "Brand", "X" ]; Entries = [ entry ] }

    [<Test>]
    member x.``an entry with evaluation and packages is written byte-identical to the pre-split format``() =
        Assert.That(Lock.format roots (document ()), Is.EqualTo golden)

    [<Test>]
    member x.``the pre-split text reads back into the same entry and writes back unchanged``() =
        let parsed = Lock.parse roots golden
        Assert.That(parsed, Is.EqualTo (document ()))
        Assert.That(Lock.format roots parsed, Is.EqualTo golden)
