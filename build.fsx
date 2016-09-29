// xake build file
// boostrapping xake.core
System.Environment.CurrentDirectory <- __SOURCE_DIRECTORY__

let file = System.IO.Path.Combine("packages", "Xake.Core.dll")
if not (System.IO.File.Exists file) then
    printf "downloading xake.core assembly..."; System.IO.Directory.CreateDirectory("packages") |> ignore
    let url = "https://github.com/OlegZee/Xake/releases/download/v0.7.0/Xake.Core.dll"
    use wc = new System.Net.WebClient() in wc.DownloadFile(url, file + "__"); System.IO.File.Move(file + "__", file)
    printfn ""

#r @"packages/Xake.Core.dll"
//#r @"bin/Debug/Xake.Core.dll"

open Xake
open Xake.SystemTasks

let TestsAssembly = "bin/XakeLibTests.dll"

do xake {ExecOptions.Default with ConLogLevel = Verbosity.Chatty } {
    var "NETFX-TARGET" "4.5"
    filelog "build.log" Verbosity.Diag

    rules [
        "main"  => action {
            do! need ["get-deps"]
            do! need ["build"]
            do! need ["test"]
            }

        "build" <== [TestsAssembly; "bin/Xake.Core.dll"]
        "clean" => action {
            do! rm ["bin/*.*"]
        }

        "get-deps" =>
        action {
            try
                do! system (useClr >> checkErrorLevel) ".paket/paket.bootstrapper.exe" [] |> Action.Ignore
                do! system (useClr >> checkErrorLevel) ".paket/paket.exe" ["install"] |> Action.Ignore
            with e ->
                failwithf "Failed to install packages. Error is %s" e.Message
        }

        "test" => action {
            do! need[TestsAssembly]
            do! system (useClr >> checkErrorLevel) "packages/NUnit.Runners/tools/nunit-console.exe" [TestsAssembly] |> Action.Ignore
        }

        ("bin/FSharp.Core.dll") *> fun outfile ->
            WhenError ignore <| action {
                do! copyFile "packages/FSharp.Core/lib/net40/FSharp.Core.dll" outfile.FullName
                do! copyFiles ["packages/FSharp.Core/lib/net40/FSharp.Core.*data"] "bin"
            }

        ("bin/nunit.framework.dll") *> fun outfile -> action {
            do! copyFile "packages/NUnit/lib/nunit.framework.dll" outfile.FullName
        }

        "bin/Xake.Core.dll" *> fun file -> action {

            // TODO multitarget rule!
            let xml = "bin/Xake.Core.XML" // file.FullName .- "XML"

            let sources = fileset {
                basedir "core"
                includes "Logging.fs"
                includes "Pickler.fs"
                includes "Env.fs"
                includes "Path.fs"
                includes "File.fsi"
                includes "File.fs"
                includes "Fileset.fs"
                includes "Types.fs"
                includes "CommonLib.fs"
                includes "Database.fs"
                includes "ActionBuilder.fs"
                includes "ActionFunctions.fs"
                includes "WorkerPool.fs"
                includes "Progress.fs"
                includes "ExecTypes.fs"
                includes "DependencyAnalysis.fs"
                includes "ExecCore.fs"
                includes "XakeScript.fs"
                includes "ScriptFuncs.fs"
                includes "SystemTasks.fs"
                includes "FileTasks.fs"
                includes "ResourceFileset.fs"
                includes "DotNetFwk.fs"
                includes "DotnetTasks.fs"
                includes "VersionInfo.fs"
                includes "AssemblyInfo.fs"
                includes "Program.fs"
            }

            do! Fsc {
                FscSettings with
                    Out = file
                    Src = sources
                    Ref = !! "bin/FSharp.Core.dll"
                    RefGlobal = ["mscorlib.dll"; "System.dll"; "System.Core.dll"; "System.Windows.Forms.dll"]
                    Define = ["TRACE"]
                    CommandArgs = ["--optimize+"; "--warn:3"; "--warnaserror:76"; "--utf8output"; "--doc:" + xml]
            }

        }

        TestsAssembly *> fun file -> action {

            // TODO --doc:..\bin\Xake.Core.XML --- multitarget rule!

            let sources = fileset {
                basedir "XakeLibTests"
                includes "ActionTests.fs"
                includes "FilesetTests.fs"
                includes "ScriptErrorTests.fs"
                includes "XakeScriptTests.fs"
                includes "MiscTests.fs"
                includes "StorageTests.fs"
                includes "FileTasksTests.fs"
                includes "ProgressTests.fs"
                includes "CommandLineTests.fs"
            }

            do! Fsc {
                FscSettings with
                    Out = file
                    Src = sources
                    Ref = !! "bin/FSharp.Core.dll" + "bin/nunit.framework.dll" + "bin/Xake.Core.dll"
                    RefGlobal = ["mscorlib.dll"; "System.dll"; "System.Core.dll"]
                    Define = ["TRACE"]
                    CommandArgs = ["--optimize+"; "--warn:3"; "--warnaserror:76"; "--utf8output"]
            }

        }
    ]
}
