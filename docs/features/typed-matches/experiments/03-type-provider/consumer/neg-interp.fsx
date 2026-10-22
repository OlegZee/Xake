// expected: concatenation inside the type argument is a syntax error; concatenation of
// [<Literal>]s into another [<Literal>] compiles; an interpolated string does not (static
// arguments must be constants). Run with the last line commented in/out.
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../Xake.Patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
[<Literal>]
let Pkg = "Xake"
[<Literal>]
let PkgPattern = "out/pkg/" + Pkg + "/" + Pkg + ".(ver:*).nupkg"
type Lit = Pattern<PkgPattern>
printfn "literal concat: %s -> %s" Lit.Text (Lit.Path(ver = "3.12.0"))
// [<Literal>] let Interp = $"out/pkg/{Pkg}/{Pkg}.(ver:*).nupkg"   // see README for the result
