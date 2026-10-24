// expected: compile error, a phony type has no file outputs
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../Xake.Patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
type Lock = Command<"lock::(name:*)::(cfg:*)">
let r = Lock.Rule(fun m -> recipe { printfn "%A" m.Out0 })
