// expected: compile errors, a group value missing; no Path/Make for a pattern with a bare `*`
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../Xake.Patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
type Dll = Pattern<"out/(fwk:*)/(lib:*).dll">
let p = Dll.Path(fwk = "net8.0")
type Wild = Pattern<"out/*/(lib:*).dll">
let w = Wild.Path(lib = "x")
