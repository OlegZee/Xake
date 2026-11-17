#load "Header.fsx"
open Xake
open Header
let a = try target' "p" { also "x"; failwith "boom-headed"; do! need ["a"] } |> ignore; "registered" with e -> "THREW " + e.Message
let b = try target' "p" { failwith "boom-plain"; do! need ["a"] } |> ignore; "registered" with e -> "THREW " + e.Message
let c = try target' "p" { needs "x"; printfn "BODY RAN"; return () } |> ignore; "registered" with e -> "THREW " + e.Message
printfn "%s\n%s\n%s" a b c
