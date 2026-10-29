namespace Tests

open System.IO
open NUnit.Framework

open Xake

type XakeTestBase(folder) =

    let mutable rememberDir = ""
    let mutable outFolder = ""

    let mutable testOptions = ExecOptions.Default
    
    member x.TestOptions with get() = testOptions

    /// The build's package folder of a script run with `TestOptions` and no `NUGET_PACKAGES`:
    /// `.packages` under the project root (Xake.Dotnet's default).
    member x.PackageRoot = Path.GetFullPath (Path.Combine (testOptions.ProjectRoot, ".packages"))

    [<OneTimeSetUp>]
    member __.Setup () =
        rememberDir <- Directory.GetCurrentDirectory()
        outFolder <- __SOURCE_DIRECTORY__ </> "~testout~" </> folder
        testOptions <- {
            ExecOptions.Default with
                Threads = 1
                Targets = ["main"]
                ConLogLevel = Diag; FileLogLevel = Silent
                ProjectRoot = outFolder
                // the test jobs switch the compiler server with the environment variable
                // XAKE_CSC_SERVER (testjobs.fsx); Xake.Dotnet reads only build variables, so it
                // is passed on as the test runner's `-d`
                CliVars =
                    match System.Environment.GetEnvironmentVariable "XAKE_CSC_SERVER" with
                    | null | "" -> []
                    | value -> [ "XAKE_CSC_SERVER", value ]
        }

        Directory.CreateDirectory outFolder |> ignore
        Directory.SetCurrentDirectory outFolder

    [<OneTimeTearDown>]
    member __.Teardown () =
        Directory.SetCurrentDirectory rememberDir

    [<SetUp>]
    member __.SetupTest () =
        try File.Delete("." </> ".xake") with _ -> ()
