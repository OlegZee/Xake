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

/// The lock file format did not move when `Lock.Entry` became `{ Csc; Evaluation; Packages }`
/// (now `{ Compilation; Evaluation; Packages }`, a C# entry written exactly as before):
/// the writer flattens the entry back into the `Name`/`Framework`/`Evaluation`/`Compilation`/
/// `Dependencies` shape (with `Packages` inside `Dependencies`) and the reader regroups it. The
/// expected text below was written by the pre-split `Lock.format` (commit 99faffc) from the
/// same entry, so this pins the format byte for byte.
[<TestFixture>]
type ``Lock file format``() =

    let roots = [ "$(NuGetPackageRoot)", "/r/pkgs"; "$(ProjectRoot)", "/r/proj"; "$(DotnetRoot)", "/r/dotnet" ]

    static let golden = """{
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

    static member Golden = golden

    [<Test>]
    member x.``an entry with evaluation and packages is written byte-identical to the pre-split format``() =
        Assert.That(Lock.format roots (document ()), Is.EqualTo golden)

    [<Test>]
    member x.``the pre-split text reads back into the same entry and writes back unchanged``() =
        let parsed = Lock.parse roots golden
        Assert.That(parsed, Is.EqualTo (document ()))
        Assert.That(Lock.format roots parsed, Is.EqualTo golden)

/// An F# entry has the C# entry's shape: the same sections and keys in the same order, the
/// options in fsc's own `-`/`--` dialect, references as `-r:` behind `@References`, no
/// `@Analyzers` marker and an empty `Analyzers` list (written the way the writer writes an
/// empty list for a C# entry without analyzers). `Dependencies.Compiler.Tool = "fsc"` is what
/// makes the reader build `Compilation.Fsc`.
[<TestFixture>]
type ``Lock file format, F# entry``() =

    let roots = [ "$(NuGetPackageRoot)", "/r/pkgs"; "$(ProjectRoot)", "/r/proj"; "$(DotnetRoot)", "/r/dotnet" ]

    let golden = """{
  "Configuration": "Release",
  "Properties": {

  },
  "Entries": [
    {
      "Name": "Lib",
      "Framework": "netstandard2.0",
      "Evaluation": {
        "Project": "$(ProjectRoot)/src/Lib/Lib.fsproj",
        "ProjectRefs": [
          "$(ProjectRoot)/src/Other/Other.fsproj"
        ],
        "Imports": [

        ],
        "Sdk": "8.0.100",
        "SdkPin": "exact 8.0.100",
        "Properties": {
          "AssemblyName": "Lib"
        }
      },
      "Compilation": {
        "Directory": "$(ProjectRoot)/src/Lib",
        "Options": [
          "-o:$(ProjectRoot)/src/Lib/obj/Lib.dll",
          "-g",
          "--debug:portable",
          "--noframework",
          "@Defines",
          "--doc:$(ProjectRoot)/src/Lib/obj/Lib.xml",
          "--optimize+",
          "@References",
          "--target:library",
          "--warn:3",
          "--targetprofile:netstandard",
          "--nocopyfsharpcore",
          "--deterministic+",
          "--resource:$(ProjectRoot)/src/Lib/obj/Lib.S.resources,Lib.S.resources",
          "@Sources"
        ],
        "Defines": [
          "TRACE",
          "RELEASE"
        ],
        "Sources": [
          "$(ProjectRoot)/src/Lib/obj/Lib.AssemblyInfo.fs",
          "$(ProjectRoot)/src/Lib/A.fs",
          "$(ProjectRoot)/src/Lib/B.fs"
        ],
        "Generated": {
          "$(ProjectRoot)/src/Lib/obj/Lib.AssemblyInfo.fs": "namespace Microsoft.BuildSettings\n"
        },
        "Resources": {
          "$(ProjectRoot)/src/Lib/S.resx": "$(ProjectRoot)/src/Lib/obj/Lib.S.resources"
        }
      },
      "Dependencies": {
        "Compiler": { "Tool": "fsc", "Path": "$(DotnetRoot)/sdk/8.0.100/FSharp/fsc.dll", "Sha256": "f5c0", "Version": "12.8.0" },
        "References": [
          { "Path": "$(NuGetPackageRoot)/fsharp.core/8.0.100/lib/netstandard2.0/FSharp.Core.dll", "Sha256": "fc01" },
          { "Path": "$(ProjectRoot)/src/Other/bin/Other.dll", "Sha256": "" }
        ],
        "Analyzers": [

        ],
        "Packages": [
          { "Id": "FSharp.Core", "Version": "8.0.100", "Sha512": "BBBB==", "Direct": true, "DependsOn": [] }
        ]
      }
    }
  ]
}
"""

    let fscEntry () : Lock.Entry =
        let f =
            Fsc.ofArgs
                [ "-o:/r/proj/src/Lib/obj/Lib.dll"; "-g"; "--debug:portable"; "--noframework"
                  "--define:TRACE"; "--define:RELEASE"; "--doc:/r/proj/src/Lib/obj/Lib.xml"; "--optimize+"
                  "-r:/r/pkgs/fsharp.core/8.0.100/lib/netstandard2.0/FSharp.Core.dll"
                  "-r:/r/proj/src/Other/bin/Other.dll"
                  "--target:library"; "--warn:3"; "--targetprofile:netstandard"; "--nocopyfsharpcore"; "--deterministic+"
                  "--resource:/r/proj/src/Lib/obj/Lib.S.resources,Lib.S.resources"
                  "/r/proj/src/Lib/obj/Lib.AssemblyInfo.fs"; "/r/proj/src/Lib/A.fs"; "/r/proj/src/Lib/B.fs" ]
        { Compilation = Lock.Compilation.Fsc
            { f with
                Fsc.Name = "Lib"
                Fsc.Framework = "netstandard2.0"
                Fsc.Directory = "/r/proj/src/Lib"
                Fsc.Generated = [ "/r/proj/src/Lib/obj/Lib.AssemblyInfo.fs", "namespace Microsoft.BuildSettings\n" ]
                Fsc.Resources = [ "/r/proj/src/Lib/S.resx", "/r/proj/src/Lib/obj/Lib.S.resources" ]
                Fsc.Dependencies =
                    { f.Dependencies with
                        Compiler = { Tool = "fsc"; Path = "/r/dotnet/sdk/8.0.100/FSharp/fsc.dll"; Sha256 = "f5c0"; Version = "12.8.0" }
                        References = f.Dependencies.References |> List.mapi (fun i r -> if i = 0 then { r with Sha256 = "fc01" } else r) } }
          Evaluation =
            { Project = "/r/proj/src/Lib/Lib.fsproj"; ProjectRefs = [ "/r/proj/src/Other/Other.fsproj" ]
              Imports = []
              Sdk = "8.0.100"; SdkPin = Some (Lock.Pinned "8.0.100")
              Properties = Map.ofList [ "AssemblyName", "Lib" ] }
          Packages = [ { Id = "FSharp.Core"; Version = "8.0.100"; Sha512 = "BBBB=="; Direct = true; DependsOn = [] } ] }

    let document () : Lock.Document = { Configuration = "Release"; Properties = []; Entries = [ fscEntry () ] }

    [<Test>]
    member x.``an F# entry is written in the sectioned shape with fsc's own options``() =
        Assert.That(Lock.format roots (document ()), Is.EqualTo golden)

    [<Test>]
    member x.``an F# entry reads back as Compilation.Fsc and writes back unchanged``() =
        let parsed = Lock.parse roots golden
        Assert.That(parsed, Is.EqualTo (document ()))
        match parsed.Entries with
        | [ { Compilation = Lock.Compilation.Fsc f } ] ->
            Assert.That(f.Dependencies.Analyzers, Is.Empty)
            Assert.That(f.Args, Does.Contain "-r:/r/pkgs/fsharp.core/8.0.100/lib/netstandard2.0/FSharp.Core.dll")
            Assert.That(f.Output, Is.EqualTo (Some "/r/proj/src/Lib/obj/Lib.dll"))
        | other -> Assert.Fail (sprintf "expected one F# entry, got %A" other)
        Assert.That(Lock.format roots parsed, Is.EqualTo golden)

    [<Test>]
    member x.``a lock with a C# and an F# entry keeps each entry's case``() =
        let csc = (Lock.parse roots (``Lock file format``.Golden)).Entries.Head
        let doc = { document () with Entries = [ csc; fscEntry () ] }
        let parsed = Lock.parse roots (Lock.format roots doc)
        Assert.That(parsed, Is.EqualTo doc)
        Assert.That(parsed.Entries |> List.map (fun e -> e.Dependencies.Compiler.Tool), Is.EqualTo [ "csc"; "fsc" ])
        Assert.That((Lock.entry "Lib" parsed).Fsc.Name, Is.EqualTo "Lib")
        Assert.That((Lock.entry "Sample" parsed).Csc.Name, Is.EqualTo "Sample")

    [<Test>]
    member x.``entry.Csc on an F# entry fails naming it``() =
        let ex = Assert.Throws<exn>(fun () -> (fscEntry ()).Csc |> ignore)
        Assert.That(ex.Message, Does.Contain "'Lib' is an F# entry")

    [<Test>]
    member x.``diff names a change of compiler tool``() =
        let a = fscEntry ()
        let b = Lock.mapPaths id a
        Assert.That(Lock.diff a b, Is.Empty)
        let asCsc = { a with Compilation = Lock.Compilation.Csc (Csc.ofArgs [ "/out:/r/proj/src/Lib/obj/Lib.dll" ]) }
        Assert.That(Lock.diff a asCsc, Does.Contain "~ Compiler.Tool: fsc -> csc")

    [<Test>]
    member x.``an F# entry with analyzers is refused``() =
        let text =
            golden.Replace ("\"Analyzers\": [\n\n        ]", "\"Analyzers\": [\n          { \"Path\": \"/a.dll\", \"Sha256\": \"\" }\n        ]")
        Assert.That(text, Is.Not.EqualTo golden)
        let ex = Assert.Throws<exn>(fun () -> Lock.parse roots text |> ignore)
        Assert.That(ex.Message, Does.Contain "F# entry with analyzers")
