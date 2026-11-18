#r "../../../../src/core/bin/Debug/netstandard2.0/Xake.dll"
open Xake
open Xake.Tasks
open FSharp.Quotations
open FSharp.Quotations.Patterns

/// lambda parameter names from a quotation (curried or tupled)
let rec paramNames (e: Expr) =
    match e with
    | WithValue (_, _, e) -> paramNames e
    | Lambda (v, Let (a, TupleGet (Var t, 0), body)) when t = v ->      // fun (a, b, ..) ->
        let rec lets acc = function
            | Let (x, TupleGet (Var t', _), b) when t' = v -> lets (x.Name :: acc) b
            | _ -> List.rev acc
        lets [] (Expr.Let (a, Expr.TupleGet (Expr.Var t, 0), body))
    | Lambda (v, body) -> v.Name :: paramNames body
    | _ -> []

type Rule =
    /// ReflectedDefinition(true): the caller writes a plain lambda; we get the compiled value AND the tree
    static member file(pattern: string, [<ReflectedDefinition(true)>] body: Expr<string -> Recipe<ExecContext,unit>>) =
        let names = paramNames body
        let f = match body with WithValue (v, _, _) -> v :?> (string -> Recipe<ExecContext,unit>) | _ -> failwith "no value"
        names, FileRule ([pattern], RuleHeader.Empty, recipe { let! m = getRuleMatches () in do! f (Map.find names.[0] m) })
    static member file(pattern: string, [<ReflectedDefinition(true)>] body: Expr<string -> string -> Recipe<ExecContext,unit>>) =
        let names = paramNames body
        let f = match body with WithValue (v, _, _) -> v :?> (string -> string -> Recipe<ExecContext,unit>) | _ -> failwith "no value"
        names, FileRule ([pattern], RuleHeader.Empty, recipe { let! m = getRuleMatches () in do! f (Map.find names.[0] m) (Map.find names.[1] m) })
    static member file(pattern: string, [<ReflectedDefinition(true)>] body: Expr<string * string -> Recipe<ExecContext,unit>>) =
        let names = paramNames body
        let f = match body with WithValue (v, _, _) -> v :?> (string * string -> Recipe<ExecContext,unit>) | _ -> failwith "no value"
        names, FileRule ([pattern], RuleHeader.Empty, recipe { let! m = getRuleMatches () in do! f (Map.find names.[0] m, Map.find names.[1] m) })

let n1, r1 = Rule.file("out/(n:*).one", fun n -> recipe { do! writeText ("one " + n) })
let n2, r2 = Rule.file("out/(cfg:*)/(n:*).two", fun cfg n -> recipe {
    do! trace Message "cfg=%s n=%s" cfg n
    do! writeText $"two {cfg} {n}" })
let n3, r3 = Rule.file("out/(cfg:*)/(n:*).three", fun (cfg, n) -> recipe { do! writeText $"three {cfg} {n}" })
printfn "names: %A %A %A" n1 n2 n3

// reflection on closures, no quotation
let showInvoke (f: obj) = f.GetType().GetMethods() |> Array.filter (fun m -> m.Name = "Invoke") |> Array.map (fun m -> m.GetParameters() |> Array.map (fun p -> p.Name)) 
printfn "closure 1: %A" (showInvoke (box (fun (n: string) -> recipe { do! writeText n })))
printfn "closure 2: %A" (showInvoke (box (fun (cfg: string) (n: string) -> recipe { do! writeText (cfg + n) })))

try System.IO.Directory.Delete("out", true) with _ -> ()
xake { ExecOptions.Default with Targets = [ "out/a.one"; "out/dbg/b.two"; "out/rel/c.three" ]; ThrowOnError = true; Progress = false; FileLog = ""; ConLogLevel = Quiet } {
    rules [ r1; r2; r3 ] }
for f in [ "out/a.one"; "out/dbg/b.two"; "out/rel/c.three" ] do printfn "%s" (System.IO.File.ReadAllText f)
