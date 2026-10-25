// Experiment 08: the consumer against the build output (in-repo use: the design-time dll is found
// under patterns/bin/typeproviders/fsharp41/netstandard2.0/, copied there by the design-time build).
//   dotnet fsi build-local.fsx [D|B]
#r "../engine/src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
#load "Rules.fs"

let forms = match fsi.CommandLineArgs |> Array.tail with [||] -> [ "D"; "B" ] | a -> List.ofArray a
for f in forms do
    Rules.scenario f (if f = "D" then Rules.rulesD else Rules.rulesB)
