// expected: compile error, a missing group in Target
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../Xake.Patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
type Lock = Command<"lock::(name:*)::(cfg:*)">
let n = Lock.Target(name = "Xake")
