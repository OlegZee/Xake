#load "Header.fsx"
open Xake
open Header
open Xake.Typed
type FileRuleBuilderT<'G>(pattern, read: Map<string,string> -> 'G) =
    inherit FileRuleBuilder(pattern)
    member this.Bind(_: Matched, f: 'G -> Recipe<ExecContext, 'b>) : Recipe<ExecContext, 'b> =
        this.Bind(getRuleMatches (), fun (m: Map<string, string>) -> f (read m))
    [<CustomOperation("needs")>] member _.Needs(h: Header, ps: string list) = { h with Needs = h.Needs @ ps }
type AsmG = { n: string }
let Asm = FileRuleBuilderT<AsmG>("out/(n:*).dll", fun m -> { n = m.["n"] })
let t1 = Asm {
    also "out/(n:*).pdb"
    needs ["obj/(n:*).rsp"; "obj/(n:*).res"]
    let! m = matched
    do! need [m.n]
}
let t2 = Asm { let! m = matched in do! need [m.n] }
let t3 = Asm { needs "single" }
ignore [t1;t2;t3]
