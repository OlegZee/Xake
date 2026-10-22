// expected: compile error, the .xml pattern names its group `name`, not `lib`
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../Xake.Patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
type Lib = Pattern<"out/(fwk:*)/(lib:*).dll; out/(fwk:*)/(name:*).xml">
printfn "%s" Lib.Text
