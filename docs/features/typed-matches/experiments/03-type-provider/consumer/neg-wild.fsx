// expected: compile error, no Path/Make is provided for a pattern with a bare `*`
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../Xake.Patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
type Wild = Pattern<"out/*/(lib:*).dll">
let w = Wild.Path(lib = "x")
