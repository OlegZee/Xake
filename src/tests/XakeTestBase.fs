namespace Tests

open System.IO
open NUnit.Framework

open Xake

type XakeTestBase(folder) =

    let mutable rememberDir = ""
    let mutable outFolder = ""

    let mutable testOptions = ExecOptions.Default
    
    /// The test jobs switch the compiler server with the environment variable XAKE_CSC_SERVER
    /// (testjobs.fsx). Xake.Dotnet from 3.16 reads build variables only, so the value is passed
    /// on as the test runner's `-d` (`ExecOptions.CliVars`). This file is also compiled into
    /// src/hermetic.tests, which builds against the published Xake too: there `ExecOptions` may
    /// have no `CliVars`, and that Xake reads the environment variable itself.
    let withRunnerSwitch (options: ExecOptions) : ExecOptions =
        match System.Environment.GetEnvironmentVariable "XAKE_CSC_SERVER" with
        | null | "" -> options
        | value ->
            let t = typeof<ExecOptions>
            let fields = Microsoft.FSharp.Reflection.FSharpType.GetRecordFields t
            if fields |> Array.exists (fun f -> f.Name = "CliVars") |> not then options
            else
                let values =
                    Microsoft.FSharp.Reflection.FSharpValue.GetRecordFields options
                    |> Array.mapi (fun i v -> if fields.[i].Name = "CliVars" then box [ "XAKE_CSC_SERVER", value ] else v)
                Microsoft.FSharp.Reflection.FSharpValue.MakeRecord (t, values) :?> ExecOptions

    member x.TestOptions with get() = testOptions

    /// The build's package folder of a script run with `TestOptions` and no `NUGET_PACKAGES`:
    /// `.packages` under the project root (Xake.Dotnet's default).
    member x.PackageRoot = Path.GetFullPath (Path.Combine (testOptions.ProjectRoot, ".packages"))

    /// The build's package folder and where it came from, as Xake.Dotnet answers it for a script
    /// run with `options` (from Xake 3.16 the build variable NUGET_PACKAGES, else `.packages`;
    /// before, the environment variable or `~/.nuget/packages`): shared tests run on both.
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
