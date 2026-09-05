// Builds a .NET Framework executable on any OS -- no Framework installation and no registry
// involved: the compiler comes from the .NET SDK, and the reference assemblies from the
// Microsoft.NETFramework.ReferenceAssemblies.net462 package (restored on first run).
//
// The resulting exe targets CLR v4.0.30319 and runs on Windows.
//
// USAGE:
// * `dotnet fsi fullframework.fsx`

#r "../out/netstandard2.0/Xake.dll"
#r "../out/netstandard2.0/Xake.Dotnet.dll"

open Xake
open Xake.Tasks
open Xake.Dotnet

do xakeScript {
    rules [
        "main" <== ["temp/helloworld.exe"]

        "clean" => rm {file "temp/helloworld.exe"}

        // `targetfwk` picks the framework to compile against; on Windows a real Framework
        // installation found through the registry is preferred, everywhere else (and as a
        // fallback) the SDK compiler over NuGet reference assemblies is used
        "temp/helloworld.exe" ..> csc {
            targetfwk "net-4.6.2"
            src !!"helloworld.cs"
            grefs ["System.dll"]
        }
    ]
}
