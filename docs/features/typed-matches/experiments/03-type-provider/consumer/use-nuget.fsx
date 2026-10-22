// The provider loaded through `#r "nuget: ..."` from a local feed (see README for the pack step).
#i "nuget: /tmp/xake-tp-feed"
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "nuget: Xake.Patterns, 0.0.1-exp"
open Xake
type Dll = Pattern<"out/(fwk:*)/(lib:*).dll">
printfn "via nuget: %s" (Dll.Path(fwk = "net8.0", lib = "Xake"))
