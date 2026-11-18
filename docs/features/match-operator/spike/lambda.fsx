#r "../../../../src/core/bin/Debug/netstandard2.0/Xake.dll"
open Xake
open Xake.Tasks
let (?) (m: Map<string,string>) (name: string) =
    match Map.tryFind name m with Some v -> v | None -> failwithf "no group '%s'" name
// a copy of RuleBody with a third case: a body that takes the match
type RB = RB with
    static member ($) (RB, body: Recipe<ExecContext, unit>) = RuleHeader.Empty, body
    static member ($) (RB, Headed (h, body): Headed<ExecContext>) = h, body
    static member ($) (RB, f: Map<string,string> -> Recipe<ExecContext, unit>) =
        RuleHeader.Empty, recipe { let! m = getRuleMatches () in do! f m }
let inline ( ..>> ) (pattern: string) body : ExecContext Rule =
    let h, r = RB $ body in FileRule ([ pattern ], h, r)
try System.IO.Directory.Delete("out", true) with _ -> ()
xake { ExecOptions.Default with Targets = [ "out/q.txt" ]; ThrowOnError = true; Progress = false; FileLog = ""; ConLogLevel = Quiet } {
    rules [
        "out/(n:*).txt" ..>> fun m -> recipe {
            do! trace Message "n = %s" m?n
            do! writeText m?n }
        "out/(n:*).y" ..>> recipe { do! writeText "y" }
    ]
}
printfn "%s" (System.IO.File.ReadAllText "out/q.txt")
