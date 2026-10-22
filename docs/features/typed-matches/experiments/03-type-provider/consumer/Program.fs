open Xake

type Lib = Pattern<"out/(fwk:*)/(lib:*).dll; out/(fwk:*)/(lib:*).xml">

[<EntryPoint>]
let main _ =
    let l = Lib.Make(fwk = "net8.0", lib = "Core")
    printfn "compiled: %s %s %s" l.dll l.xml (String.concat "," Lib.Texts)
    0
