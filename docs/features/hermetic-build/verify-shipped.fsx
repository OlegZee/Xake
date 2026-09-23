// Compares two PE assemblies (shipped nupkg dll vs local tag build) with the Verify module.
// Usage: dotnet fsi verify-shipped.fsx -- <pathA> <pathB>
#r "../../../.bootstrap/Xake.dll"
#r "../../../.bootstrap/Xake.Dotnet.dll"

open Xake.Dotnet

let args = fsi.CommandLineArgs |> Array.skip 1
let a, b =
    match args with
    | [| pa; pb |] -> pa, pb
    | _ -> failwith "usage: verify-shipped.fsx <pathA> <pathB>"

printfn "A: %s" a
printfn "B: %s" b
printfn "sha256 A          = %s" (Verify.sha256 a)
printfn "sha256 B          = %s" (Verify.sha256 b)
printfn "authenticodeHash A = %s" (Verify.authenticodeHash a)
printfn "authenticodeHash B = %s" (Verify.authenticodeHash b)

let diffs = Verify.compare a b
printfn "verdict: %s" (Verify.verdict diffs)
printfn "first 20 differences:"
diffs
|> List.truncate 20
|> List.iter (fun d -> printfn "  offset=0x%06x length=%d field=%s" d.Offset d.Length d.Field)

printfn "total differences: %d" (List.length diffs)
