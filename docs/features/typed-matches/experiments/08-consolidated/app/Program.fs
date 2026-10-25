open Xake
open Xake.Patterns

// the engine's DU next to the provided Target<>: `open Xake.Patterns` (last) shadows the DU's
// name, so the DU is written DomainTypes.Target (README 08, names)
#if NS_A
let describe (t: Target) =
#else
let describe (t: DomainTypes.Target) =
#endif
    match t with
    | FileTarget f -> "file " + f.Name
    | PhonyAction a -> "phony " + a

[<EntryPoint>]
let main argv =
    printfn "%s | %s" (Rules.Lib.Format(fwk = "net8.0", lib = "Xake")) (describe (PhonyAction "x"))
    let forms = if argv.Length = 0 then [ "D"; "B" ] else List.ofArray argv
    for f in forms do
        Rules.scenario ("app-" + f) (if f = "D" then Rules.rulesD else Rules.rulesB)
    0
