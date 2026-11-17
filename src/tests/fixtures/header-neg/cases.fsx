// Compile-time cases of the rule header (Xake 5.0, docs/rule-header.md), each with the error it
// must give; the format is that of `fixtures/typed-neg/cases.fsx` (`TypedCompileTests.fs` runs both).
#r "../../../core/bin/Debug/netstandard2.0/Xake.dll"
open Xake
open Xake.Typed
type AsmG = { n: string }
let Asm = Target<AsmG> "out/(n:*).dll"
let cscLock = Resource.newResource "csc" 1
let body : Recipe<ExecContext, unit> = recipe { () }
let shared : Headed<ExecContext> = recipe { needs "obj/(n:*).rsp"; do! body }
//#case ok-every-form
let r1 = "out/(n:*).dll" ..> recipe {
    also "out/(n:*).pdb"
    needs "obj/(n:*).rsp"
    needs [ "src/(n:*)/**/*.cs"; "obj/(n:*).res" ]
    requires cscLock
    requires (cscLock, 1)
    noContentCutoff
    contentCutoff
    let! m = getRuleMatches ()
    for f in [ "a" ] do
        do! need [ f ]
    if m.Count > 0 then do! body
    match m.Count with 0 -> () | _ -> ()
}
let r2 = "out/(n:*).dll" ..> recipe { needs "obj/(n:*).rsp" }
let r3 = [ "out/(n:*).dll"; "out/(n:*).xml" ] *..> recipe { needs "a"; do! body }
let r4 = (fun (f: string) -> f.EndsWith ".x") ..?> recipe { needs "a"; do! body }
let r5 = target "out/(n:*).dll" { also "out/(n:*).pdb"; do! body }
let r6 = targets [ "out/(n:*).dll"; "out/(n:*).xml" ] { needs "a" }
let r7 = Asm.rule { needs "obj/(n:*).rsp"; let! m = matched in do! need [ m.n ] }
let r8 = Asm.Rule <| fun m -> recipe { do! need [ m.n ] }
let r9 = "out/(n:*).dll" ..> shared
let r10 = "out/(n:*).dll" ..> recipe { do! body }
//#case header-after-do FS3095
let r = "out/(n:*).dll" ..> recipe {
    do! body
    needs "obj/(n:*).rsp"
}
//#case header-after-let-bang FS0001
let r = "out/(n:*).dll" ..> recipe {
    let! m = getRuleMatches ()
    needs "obj/(n:*).rsp"
}
//#case header-after-let FS0001
let r = "out/(n:*).dll" ..> recipe {
    let x = 1
    needs "obj/(n:*).rsp"
}
//#case header-inside-for FS0001
let r = "out/(n:*).dll" ..> recipe {
    for f in [ "a" ] do
        needs f
}
//#case header-after-if FS3086
let r = "out/(n:*).dll" ..> recipe {
    if true then printfn "x"
    needs "obj/(n:*).rsp"
}
//#case header-after-let-bang-typed FS0001
let r = Asm.rule {
    let! m = matched
    also "out/(n:*).pdb"
}
//#case header-after-do-typed FS3095
let r = Asm.rule {
    do! body
    also "out/(n:*).pdb"
}
//#case headed-do-bang FS3095
let r : Recipe<ExecContext, unit> = recipe { do! shared }
//#case headed-let-bang FS3095
let r : Recipe<ExecContext, unit> = recipe { let! x = shared in () }
//#case headed-in-for FS3095
let r : Recipe<ExecContext, unit> = recipe { for i in [ 1 ] do do! shared }
//#case headed-as-recipe-argument FS0001
let run (b: Recipe<ExecContext, unit>) = recipe { do! b }
let r = run shared
//#case headed-statement FS0001
let r : Recipe<ExecContext, unit> = recipe { shared; do! body }
//#case header-on-phony FS0193
let r = "main" => recipe { needs "a" }
//#case header-on-command FS0193
let r = command "main" { needs "a" }
//#case header-on-typed-form-d FS0193
let r = Asm.Rule <| fun m -> recipe { needs "a" }
