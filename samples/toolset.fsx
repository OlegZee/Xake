// Compiles with a pinned compiler instead of whatever the .NET SDK ships: `toolset` takes
// csc.dll from the Microsoft.Net.Compilers.Toolset package of that version, restored into the
// NuGet cache on first run (nuget.org must be reachable then).
//
// USAGE:
// * `dotnet fsi toolset.fsx`

#r "../out/netstandard2.0/Xake.dll"
#r "../out/netstandard2.0/Xake.Dotnet.dll"

open Xake
open Xake.Tasks
open Xake.Dotnet

do xakeScript {
    rules [
        "main" <== ["temp/helloworld-toolset.exe"]

        "clean" => rm {file "temp/helloworld-toolset.exe"}

        "temp/helloworld-toolset.exe" ..> csc {
            toolset "4.11.0"
            targetfwk "net-4.6.2"
            src !!"helloworld.cs"
            grefs ["System.dll"]
        }
    ]
}
