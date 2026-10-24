// expected: compile error, no Target for a bare wildcard (positional groups are decision 6, not prototyped)
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../Xake.Patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
type Lock = Command<"lock::(name:*)::(cfg:*)">
let n = Command<"testsuite-*">.Target()
