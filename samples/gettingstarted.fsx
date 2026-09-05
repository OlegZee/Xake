// Compiles helloworld.exe from helloworld.cs.
//
// The package ships both the engine and the .NET Framework tasks, so one reference is enough.
// `targetfwk` is what wires up the reference assemblies -- without it the compiler is invoked
// with no framework references at all, which only happens to work on Windows.
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
