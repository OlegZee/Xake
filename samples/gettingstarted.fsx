// Compiles helloworld.exe from helloworld.cs.
//
// The package ships both the engine and the .NET Framework tasks, so one reference is enough.
// `targetfwk` names the framework to compile against (here .NET Framework 4.6.2); the reference
// assemblies come from a NuGet package when no Framework is installed, so this works on any OS.
// Without it the default is the SDK's own .NET framework.
//
// USAGE:
// * `dotnet fsi gettingstarted.fsx`

#r "nuget: Xake"

open Xake
open Xake.Dotnet

// rules are written directly in the script body -- no `rules [ ... ]` wrapper
do xakeScript {
    "main" <== ["helloworld.exe"]

    "helloworld.exe" ..> csc {
        targetfwk "net-4.6.2"
        src !!"helloworld.cs"
        grefs ["System.dll"]
    }
}
