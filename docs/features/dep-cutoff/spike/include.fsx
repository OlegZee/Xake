#r "__XAKE_DLL__"
open Xake
// a library helper: its static part as a header value, its dynamic part as a recipe
let compileHeader = ruleHeader { needs "obj/(n:*).rsp"; needs ["obj/(n:*).res"] }
let compileBody (n: string) : Recipe<ExecContext,unit> = recipe { do! need [n] }
let r = "out/(n:*).dll" ..> recipe {
    including compileHeader
    also "out/(n:*).pdb"
    let! m = getRuleMatches ()
    do! compileBody m.["n"]
}
let bad : Recipe<ExecContext,unit> = recipe { do! need ["x"] }
printfn "COMPILED"
