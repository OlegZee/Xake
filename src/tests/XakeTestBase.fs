namespace Tests

open System.IO
open NUnit.Framework

open Xake

type XakeTestBase(folder) =

    let mutable rememberDir = ""
    let mutable outFolder = ""

    let mutable testOptions = ExecOptions.Default
    
    /// The test jobs switch the compiler server with the environment variable XAKE_CSC_SERVER
    /// (testjobs.fsx); Xake.Dotnet reads build variables only, so the value is passed on as the
    /// test runner's `-d` (`ExecOptions.CliVars`).
    let withRunnerSwitch (options: ExecOptions) : ExecOptions =
        match System.Environment.GetEnvironmentVariable "XAKE_CSC_SERVER" with
        | null | "" -> options
        | value -> { options with CliVars = [ "XAKE_CSC_SERVER", value ] }

    member x.TestOptions with get() = testOptions

    /// The build's package folder of a script run with `TestOptions` and no `NUGET_PACKAGES`:
    /// `.packages` under the project root (Xake.Dotnet's default).
    member x.PackageRoot = Path.GetFullPath (Path.Combine (testOptions.ProjectRoot, ".packages"))

    /// The build's package folder and where it came from, as Xake.Dotnet answers it for a script
    /// run with `options` (the build variable NUGET_PACKAGES, else `.packages` under the root).
    member x.BuildPackageRoot (options: ExecOptions) =
        let result = ref None
        do xake { options with FileLog = ""; NoPersist = true; ThrowOnError = true } {
            want [ "package-root" ]
            rules [ "package-root" => recipe {
                let! r = Xake.Dotnet.DotNetFwk.packageRootWithSource ()
                result.Value <- Some r } ]
        }
        Option.get result.Value

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
        } |> withRunnerSwitch

        Directory.CreateDirectory outFolder |> ignore
        Directory.SetCurrentDirectory outFolder

    [<OneTimeTearDown>]
    member __.Teardown () =
        Directory.SetCurrentDirectory rememberDir

    [<SetUp>]
    member __.SetupTest () =
        try File.Delete("." </> ".xake") with _ -> ()
