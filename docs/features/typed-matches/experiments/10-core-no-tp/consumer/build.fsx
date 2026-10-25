// Experiment 10: the consumer against the engine with TypedPatterns.fs -- one dll, no provider.
//   dotnet fsi build.fsx [D|B]
#r "../engine/src/core/bin/Release/netstandard2.0/Xake.dll"
#load "Rules.fs"

let forms = match fsi.CommandLineArgs |> Array.tail with [||] -> [ "D"; "B" ] | a -> List.ofArray a
for f in forms do
    Rules.scenario f (if f = "D" then Rules.rulesD else Rules.rulesB)
