// Experiment 08: the consumer through the package, as a user script would reference Xake.
// The feed is the pack output of ../pkg (sh stage.sh feed -> consumer/.feed).
//   dotnet fsi build.fsx [D|B]
#r "nuget: Xake.Exp08, 0.0.8-exp08"
#load "Rules.fs"

let forms = match fsi.CommandLineArgs |> Array.tail with [||] -> [ "D"; "B" ] | a -> List.ofArray a
for f in forms do
    Rules.scenario f (if f = "D" then Rules.rulesD else Rules.rulesB)
