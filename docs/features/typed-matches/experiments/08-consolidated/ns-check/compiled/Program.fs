#if CASE_B
open Xake.Patterns
open Xake
#else
open Xake
open Xake.Patterns
#endif

type Bin = Target<"out/(name:*).dll">

#if CASE_W
let describe (t: DomainTypes.Target) =
#else
let describe (t: Target) =
#endif
    match t with
    | FileTarget f -> "file " + f.Name
    | PhonyAction a -> "phony " + a

[<EntryPoint>]
let main _ =
    printfn "%s | %s" (Bin.Format(name = "core")) (describe (PhonyAction "x"))
    0
