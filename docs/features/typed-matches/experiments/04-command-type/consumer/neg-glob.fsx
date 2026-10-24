// expected: compile error, a phony name has nothing to glob
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../Xake.Patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
type Lock = Command<"lock::(name:*)::(cfg:*)">
let g = Lock.Glob(name = "Xake")
