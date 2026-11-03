// Spec test 60, locally: the published Xake.Hermetic.Dotnet 0.10.0 binary on this branch's Xake
// (packed as 3.999.0-vars2), NUGET_PACKAGES unset, HERMETIC=on: a csc lock records, its
// package folder is <root>/.packages (the policy accepts it), and the lock names it as
// $(NuGetPackageRoot). Pack this branch first: dotnet pack src/dotnet -c Release /p:Version=3.999.0-vars2
// --output /tmp/xake-vars2-feed (or change the #i folder and the version below); run from an empty folder.
#i "nuget: /tmp/xake-vars2-feed"
#r "nuget: Xake, 3.999.0-vars2"
#r "nuget: Xake.Hermetic.Dotnet, 0.10.0"

open System.IO
open Xake
open Xake.Tasks
open Xake.Dotnet
open Xake.Hermetic.Dotnet

let root = "/tmp/vars2-t60/proj"
if Directory.Exists root then Directory.Delete (root, true)
Directory.CreateDirectory root |> ignore
File.WriteAllText (Path.Combine (root, "A.cs"), "public class A {}\n")
System.Environment.SetEnvironmentVariable ("NUGET_PACKAGES", "/tmp/vars2-t60/env-pkgs-not-used")

let asm = typeof<Lock.Entry>.Assembly.GetName ()
printfn "hermetic assembly: %s %O" asm.Name asm.Version
printfn "xake assembly: %O" (typeof<ExecContext>.Assembly.GetName ()).Version

do xakeArgs [ "-d"; "HERMETIC=on"; "-d"; "CSC_TOOLSET=4.12.0"; "-d"; "CI=off" ]
            { ExecOptions.Default with ProjectRoot = root; FileLog = "t60.log"; ThrowOnError = true; Targets = [ "A.dll" ] } {
    rules [
        "A.dll" ..> csc {
            targetfwk "netstandard2.0"
            src !!"A.cs"
            grefs ["netstandard.dll"]
            lock "locks/a.json"
        }
    ]
}

let lockText = File.ReadAllText (Path.Combine (root, "locks/a.json"))
let ok1 = lockText.Contains "$(NuGetPackageRoot)/microsoft.net.compilers.toolset/4.12.0"
let ok2 = Directory.Exists (Path.Combine (root, ".packages", "microsoft.net.compilers.toolset"))
let ok3 = not (Directory.Exists "/tmp/vars2-t60/env-pkgs-not-used")
printfn "lock names $(NuGetPackageRoot) toolset: %b; restored into <root>/.packages: %b; env folder untouched: %b" ok1 ok2 ok3
if not (ok1 && ok2 && ok3) then exit 1
printfn "TEST 60: PASS"
