namespace Tests

open System
open System.IO
open NUnit.Framework

open Xake
open Xake.Hermetic.Dotnet
open Xake.Hermetic.Dotnet.MsbuildLog

/// What msbuild and `dotnet restore` print, classified (`MsbuildLog`): the diagnostic shapes,
/// the failure summary, the levels and the MSBuild floor of an import. Pure: no msbuild.
[<TestFixture>]
type ``Import diagnostics``() =

    let parse = MsbuildLog.parse

    [<Test>]
    member _.``msbuild's diagnostic shapes are recognized, with origin, code, text and entry``() =
        let restore = parse "/p/a.csproj : error NU1101: Unable to find package Foo. No packages exist with this id in source(s): nuget.org [/t/import.sln]"
        Assert.That(restore, Is.EqualTo (Some { MsbuildLog.Kind = MsbuildLog.ErrorLine; Origin = "/p/a.csproj"; Code = "NU1101"
                                                Text = "Unable to find package Foo. No packages exist with this id in source(s): nuget.org"
                                                Entry = "/t/import.sln" }))
        let noEntry = parse "    /p/a.csproj : error NU1008: Projects that use central package version management should not define the version on the PackageReference items but on the PackageVersion items: Newtonsoft.Json."
        Assert.That(noEntry |> Option.map (fun d -> d.Code, d.Origin, d.Entry), Is.EqualTo (Some ("NU1008", "/p/a.csproj", "")))
        let windows = parse @"C:\src\a.csproj : warning NU1603: Foo 1.0.0 depends on Bar (>= 1.0.0) but Bar 1.0.0 was not found. [C:\t\import.sln]"
        Assert.That(windows |> Option.map (fun d -> d.Kind, d.Origin, d.Code, d.Entry),
                    Is.EqualTo (Some (MsbuildLog.WarningLine, @"C:\src\a.csproj", "NU1603", @"C:\t\import.sln")))
        let unknownSwitch = parse "MSBUILD : error MSB1001: Unknown switch."
        Assert.That(unknownSwitch |> Option.map (fun d -> d.Origin, d.Code, d.Text), Is.EqualTo (Some ("MSBUILD", "MSB1001", "Unknown switch.")))
        let positioned = parse "/p/C.cs(12,5): error CS0103: The name 'x' does not exist in the current context [/p/a.csproj]"
        Assert.That(positioned |> Option.map (fun d -> d.Origin, d.Code), Is.EqualTo (Some ("/p/C.cs(12,5)", "CS0103")))
        let bare = parse "error NU1301: Unable to load the service index for source https://example.invalid/v3/index.json."
        Assert.That(bare |> Option.map (fun d -> d.Origin, d.Code), Is.EqualTo (Some ("", "NU1301")))
        Assert.That(parse "CSC : error CS2001: Source file '/p/X.cs' could not be found." |> Option.map (fun d -> d.Code), Is.EqualTo (Some "CS2001"))

    [<Test>]
    member _.``the summary lines and the counts are not diagnostics``() =
        for line in [ "Build FAILED."; "    0 Error(s)"; "    1 Warning(s)"; "Time Elapsed 00:00:01.23"
                      "  Determining projects to restore..."; "MSBuild version 17.11.9+a69bbaaf5 for .NET"
                      "Switch: -bogusSwitch:x"; ""; null ] do
            Assert.That(parse line, Is.EqualTo None, string line)

    [<Test>]
    member _.``the summary names the first five distinct errors with their project, then how many more``() =
        let error origin code entry =
            { MsbuildLog.Kind = MsbuildLog.ErrorLine; Origin = origin; Code = code; Text = "text " + code; Entry = entry }
        let errors =
            [ error "/p/a/a.csproj" "NU1008" "/t/import.sln"
              error "/p/a/a.csproj" "NU1008" "/t/import.sln"   // the same again: once
              error "/p/b/b.csproj" "NU1101" ""
              error "MSBUILD" "MSB1001" ""
              error "/p/a/C.cs(1,2)" "CS0103" "/p/a/a.csproj"
              error "" "NU1301" ""
              error "/p/c/c.fsproj" "NU1605" ""
              error "/p/d/d.csproj" "NU1102" "" ]
        let summary = MsbuildLog.summarize 5 errors
        Assert.That(summary.Split ([| "\n  " |], StringSplitOptions.None),
                    Is.EqualTo [| "a.csproj: NU1008: text NU1008"; "b.csproj: NU1101: text NU1101"; "MSBUILD: MSB1001: text MSB1001"
                                  "C.cs(1,2) (a.csproj): CS0103: text CS0103"; "NU1301: text NU1301"; "+2 more, see the log" |])
        Assert.That(MsbuildLog.summarize 5 (List.take 3 errors), Does.Not.Contain "more")

    [<Test>]
    member _.``errors are logged at Error and kept; a warning at Info once per engine run; other lines as before``() =
        let seen = Collections.Concurrent.ConcurrentDictionary<string, unit> ()
        let log = MsbuildLog.Collector seen
        Assert.That(log.Level Level.Verbose "/p/a.csproj : error NU1008: no versions here [/t/import.sln]", Is.EqualTo Level.Error)
        Assert.That(log.Level Level.Verbose "/p/a.csproj : warning NU1603: approximate [/t/import.sln]", Is.EqualTo Level.Info)
        // the same warning from another entry, then from another run of the same engine run: logged once
        Assert.That(log.Level Level.Verbose "/p/a.csproj : warning NU1603: approximate [/t/other.sln]", Is.EqualTo Level.Verbose)
        Assert.That((MsbuildLog.Collector seen).Level Level.Verbose "/p/a.csproj : warning NU1603: approximate", Is.EqualTo Level.Verbose)
        Assert.That(log.Level Level.Verbose "  Determining projects to restore...", Is.EqualTo Level.Verbose)
        Assert.That(log.Level Level.Error "some text on stderr", Is.EqualTo Level.Error)
        Assert.That(log.Errors |> List.map (fun d -> d.Code), Is.EqualTo [ "NU1008" ])
        Assert.That(MsbuildLog.failure "msbuild" "/p/a.csproj" 1 log,
                    Is.EqualTo "'/p/a.csproj': msbuild failed with exit code 1:\n  a.csproj: NU1008: no versions here")
        // no error line: the last lines say what happened
        let quiet = MsbuildLog.Collector seen
        quiet.Level Level.Verbose "something went wrong" |> ignore
        Assert.That(quiet.Summary, Does.Contain "no error line")
        Assert.That(quiet.Summary, Does.Contain "something went wrong")

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
                    Is.EqualTo (Some "the import needs MSBuild 17.10 or later (.NET SDK 8.0.300+); found 17.8.3 (SDK 8.0.121): raise the pin in global.json (/r/global.json)"))
        Assert.That(refusal (Version (17, 8, 3)) "8.0.121" None,
                    Is.EqualTo (Some "the import needs MSBuild 17.10 or later (.NET SDK 8.0.300+); found 17.8.3 (SDK 8.0.121): install SDK 8.0.300 or later"))
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
        Assert.That(string e, Does.Contain "msbuild failed with exit code 1")
        Assert.That(string e, Does.Contain "cpm.csproj: NU1008: ")
        Assert.That(string e, Does.Not.Contain "Exiting due to FailOnError")
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
