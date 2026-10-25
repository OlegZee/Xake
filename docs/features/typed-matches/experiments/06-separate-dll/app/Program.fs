open Xake

type Lib = Pattern<"out/(fwk:*)/(lib:*).dll", "out/(fwk:*)/(lib:*).xml">
type Pos = Pattern<"gen/*/(name:*)-?.txt">

[<EntryPoint>]
let main _ =
    printfn "Lib.All     = %A" (Lib.All(fwk = [ "net8.0" ], lib = [ "A"; "B" ]))
    printfn "Pos.Format  = %s" (Pos.Format(name = "core", _1 = "a", _2 = "7"))
    printfn "Lib.Glob    = %A" (Lib.Glob(fwk = "net8.0"))
    let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-typed-matches-06")
    System.IO.Directory.CreateDirectory root |> ignore
    xake { ExecOptions.Default with ProjectRoot = root; Targets = [ "out/net8.0/Core.xml" ]; Threads = 1; ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true; ResetDb = true } {
        rules [ Lib.Rule(fun m -> recipe { printfn "multi: fwk=%s lib=%s dll=%s xml=%s" m.fwk m.lib m.dll m.xml }) ]
    }
    0
