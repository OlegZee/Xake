let names (f: obj) =
    let ms = f.GetType().GetMethods() |> Array.filter (fun m -> m.Name = "Invoke" && m.DeclaringType = f.GetType())
    if ms.Length = 0 then [ "<no Invoke on " + f.GetType().Name + ">" ] else
    let best = ms |> Array.maxBy (fun m -> m.GetParameters().Length)
    [ for p in best.GetParameters() -> p.Name ]
let handler (framework: string) (library: string) = framework.Length + library.Length
let take (f: string -> string -> int) = names (box f)

[<EntryPoint>]
let main _ =
    printfn "lambda      %A" (take (fun fwk lib -> fwk.Length * lib.Length))
    printfn "eta lambda  %A" (take (fun fwk lib -> handler fwk lib))
    printfn "nested fun  %A" (take (fun fwk -> fun lib -> fwk.Length - lib.Length))
    printfn "function    %A" (take handler)
    printfn "tupled pat  %A" (names (box (fun (fwk: string, lib: string) -> fwk + lib)))
    0
