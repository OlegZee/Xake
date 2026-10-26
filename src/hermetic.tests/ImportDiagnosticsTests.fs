namespace Tests

open System
open System.IO
open NUnit.Framework

open Xake
open Xake.Hermetic.Dotnet

/// What an import adds to the core's classifier (`MsbuildLog`): a warning once per engine run,
/// and the MSBuild floor of an import. Pure: no msbuild.
[<TestFixture>]
type ``Import diagnostics``() =

    [<Test>]
    member _.``errors are kept for the failure; a warning is logged once per engine run, whatever its entry``() =
        let engine = obj ()
        let log = MsbuildLog.diagnostics engine Level.Verbose
        Assert.That(log.Level "/p/a.csproj : error NU1008: no versions here [/t/import.sln]", Is.EqualTo Level.Error)
        Assert.That(log.Level "/p/a.csproj : warning NU1603: approximate [/t/import.sln]", Is.EqualTo Level.Info)
        Assert.That(log.Level "/p/a.csproj : warning NU1603: approximate [/t/other.sln]", Is.EqualTo Level.Never)
        // another run of the same engine run: logged already; of another engine run: logged again
        Assert.That((MsbuildLog.diagnostics engine Level.Verbose).Level "/p/a.csproj : warning NU1603: approximate", Is.EqualTo Level.Never)
        Assert.That((MsbuildLog.diagnostics (obj ()) Level.Verbose).Level "/p/a.csproj : warning NU1603: approximate", Is.EqualTo Level.Info)
        Assert.That(log.Level "  Determining projects to restore...", Is.EqualTo Level.Verbose)
        Assert.That(log.Errors, Is.EqualTo [ "/p/a.csproj : error NU1008: no versions here" ])
        Assert.That(log.Summary, Is.EqualTo "\n  /p/a.csproj : error NU1008: no versions here")

    [<Test>]
    member _.``the MSBuild version is read from -version, banner or not``() =
        let v = MsbuildLog.parseVersion
        Assert.That(v [ "17.11.9.46202" ], Is.EqualTo (Some (Version (17, 11, 9))))
        Assert.That(v [ "MSBuild version 17.8.3+195e7f5a3 for .NET"; "17.8.3.51904"; "" ], Is.EqualTo (Some (Version (17, 8, 3))))
        Assert.That(v [ "18.0.0-preview-25101-01+abc" ], Is.EqualTo (Some (Version (18, 0, 0))))
        Assert.That(v [ "MSBuild version x"; "" ], Is.EqualTo None)
        Assert.That(v [], Is.EqualTo None)

    [<Test>]
    member _.``below 17.10 the import is refused with the SDK found and what to do; 17.10 and later pass``() =
        let refusal = MsbuildLog.floorRefusal
        Assert.That(refusal (Version (17, 8, 3)) "8.0.121" (Some "/r/global.json"),
                    Is.EqualTo (Some "the import needs MSBuild 17.10 or later (.NET SDK 8.0.300+, or any 9.x/10.x SDK); found 17.8.3 (SDK 8.0.121): raise the pin in global.json (/r/global.json)"))
        Assert.That(refusal (Version (17, 8, 3)) "8.0.121" None,
                    Is.EqualTo (Some "the import needs MSBuild 17.10 or later (.NET SDK 8.0.300+, or any 9.x/10.x SDK); found 17.8.3 (SDK 8.0.121): install SDK 8.0.300 or later"))
        Assert.That(refusal (Version (17, 9, 8)) "8.0.204" None, Is.Not.EqualTo None)
        for v in [ Version (17, 10, 4); Version (17, 11, 9); Version (18, 0, 0) ] do
            Assert.That(refusal v "x" None, Is.EqualTo None, string v)


/// A restore that fails offline and deterministically (central package management with a
/// `Version` on a `PackageReference`: NU1008): the import's failure names the code and the
/// project, and the log has the line at Error. And the SDK of this machine passes the floor.
[<TestFixture>]
type ``Import diagnostics with msbuild``() =
    inherit XakeTestBase("import-diagnostics")

    [<Test; Category("Integration")>]
    member x.``a failed restore names its first errors and their project``() =
        RecordKit.write [ "cpm"; "out"; "locks"; "obj" ]
            [ "cpm/Directory.Packages.props",
                "<Project>\n  <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>\n"
                + "  <ItemGroup><PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.3\" /></ItemGroup>\n</Project>\n"
              "cpm/cpm.csproj",
                RecordKit.sdkProject "<TargetFramework>net8.0</TargetFramework>" "<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />"
              "cpm/C.cs", "class C { }\n" ]
        let e = Assert.Catch(fun () -> RecordKit.import x.TestOptions "import-nu1008" RecordKit.recordDefault [ "cpm/cpm.csproj" ] |> ignore)
        Assert.That(string e, Does.Contain "Exiting due to FailOnError set on")
        Assert.That(string e, Does.Contain "cpm.csproj : error NU1008: ")
        let errorLines = File.ReadAllLines "import-nu1008.log" |> Array.filter (fun l -> l.Contains "[ERROR]" && l.Contains "NU1008")
        Assert.That(errorLines, Is.Not.Empty)

    [<Test; Category("Integration")>]
    member x.``this machine's MSBuild is new enough for the import``() =
        let ran = ref false
        RecordKit.run x.TestOptions "msbuild-floor"
            [ "floor" => recipe {
                do! alwaysRerun ()
                do! Project.requireImportMsbuild ()
                ran.Value <- true } ]
            [ "floor" ]
        Assert.That(ran.Value)
