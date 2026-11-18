#r "../../../../src/core/bin/Debug/netstandard2.0/Xake.dll"
open Xake
open Xake.Tasks
open FSharp.Quotations
open FSharp.Quotations.Patterns
let rec paramNames (e: Expr) =
    match e with
    | WithValue (_, _, e) -> paramNames e
    | Lambda (v, body) -> v.Name :: paramNames body
    | _ -> []
type Pat = Pat of string with
    static member ( ..>> ) (Pat p, [<ReflectedDefinition(true)>] body: Expr<string -> string -> Recipe<ExecContext,unit>>) = p, paramNames body
type Rule =
    static member file(pattern: string, [<ReflectedDefinition(true)>] body: Expr<string -> string -> Recipe<ExecContext,unit>>) = pattern, paramNames body, body
// a named function instead of a lambda
let build cfg n = recipe { do! writeText (cfg + n) }
let _, names, e = Rule.file("out/(cfg:*)/(n:*).x", build)
printfn "named fn: %A  tree: %A" names e
// eta-reducible lambda
let _, names2, _ = Rule.file("out/(cfg:*)/(n:*).x", fun c n -> build c n)
printfn "eta: %A" names2
