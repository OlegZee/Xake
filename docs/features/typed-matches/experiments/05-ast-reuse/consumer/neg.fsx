// Experiment 05: compile-time checks read off the AST. Each `type` line is an error; fsi stops at
// the first, so the other two were run as copies holding only that line.
//   dotnet fsi neg.fsx
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../DesignTime/bin/Release/netstandard2.0/Xake.Patterns.Runtime.dll"
open Xake

type Multi = Pattern<"out/(fwk:*)/(lib:*).dll", "out/(fwk:*)/(name:*).xml">
type BadRx = Pattern<"out/[(lib:*).dll">
type MultiWild = Pattern<"out/*/(lib:*).dll", "out/*/(lib:*).xml">
