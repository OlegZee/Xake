// Writes out/hello.txt, then out/greeting.txt from it: two file rules and a dependency.
//
// The `Xake` package is the engine alone. To compile C# or F# (`csc`, `fsc`), add
// `#r "nuget: Xake.Dotnet"` and `open Xake.Dotnet`: that package is built in its own repository,
// https://github.com/OlegZee/Xake.Dotnet.
//
// USAGE:
// * `dotnet fsi gettingstarted.fsx`
// * `dotnet fsi gettingstarted.fsx -- -- clean`

#r "nuget: Xake"

open Xake
open Xake.Tasks

// rules are written directly in the script body -- no `rules [ ... ]` wrapper
do xakeScript {
    "main" <== ["out/greeting.txt"]

    "out/hello.txt" ..> writeText "Hello"

    // rebuilt only when out/hello.txt changes
    "out/greeting.txt" ..> recipe {
        let! hello = readText "out/hello.txt"
        do! writeText (hello + " world!")
    }

    "clean" => rm { dir "out" }
}
