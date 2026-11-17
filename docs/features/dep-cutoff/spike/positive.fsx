#load "Header.fsx"
open Xake
open Header
let r1 = target' "out/(n:*).dll" {
    also "out/(n:*).pdb"
    also "out/(n:*).xml"
    needs "obj/(n:*).rsp"
    noContentCutoff
    let! m = getRuleMatches ()
    do! need ["a"]
    for f in ["x"; "y"] do
        do! need [f]
    let x = 1
    if x = 1 then do! need ["z"]
    match x with 1 -> () | _ -> ()
    try do! need ["q"] with _ -> ()
}
let r2 = target' "out/(n:*).dll" { also "out/(n:*).pdb" }      // header-only
let r3 = target' "out/(n:*).dll" { do! need ["a"] }             // no header
let r4 = target' "out/(n:*).dll" { for f in ["x"] do do! need [f] }  // no header, for first
let r5 = target' "out/(n:*).dll" { let! m = getRuleMatches () in printfn "%A" m }
let r6 = target' "a" { () }
let r7 = target' "a" { needs "b"; return () }
ignore [r1;r2;r3;r4;r5;r6;r7]
