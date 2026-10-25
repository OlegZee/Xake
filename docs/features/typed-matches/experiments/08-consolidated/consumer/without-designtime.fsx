// Experiment 08, control for engine-only.fsx: Xake.dll + Xake.Patterns.dll in a folder with no
// design-time dll -> FS3031 at the Xake.Patterns.dll reference (whether Target<> is used or not).
#r ".stage/without/Xake.dll"
#r ".stage/without/Xake.Patterns.dll"
open Xake
printfn "%s" (PatternAst.formatMatches Map.empty "out/a.txt")
