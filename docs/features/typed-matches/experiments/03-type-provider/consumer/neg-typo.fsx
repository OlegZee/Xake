// expected: compile error, the provided type has no property `fkw`
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../Xake.Patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
type Dll = Pattern<"out/(fwk:*)/(lib:*).dll">
let r = Dll.Rule(fun m -> recipe { printfn "%s" m.fkw })
