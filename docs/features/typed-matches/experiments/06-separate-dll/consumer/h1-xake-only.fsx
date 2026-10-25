// Experiment 06, H1: only Xake.dll is referenced, no design-time dll anywhere (stage/xake-only holds
// nothing else). Must compile and run, and Xake.dll must carry no TypeProviderAssembly attribute.
#r "../stage/xake-only/Xake.dll"
open Xake
let asm = typeof<ExecContext>.Assembly
printfn "assembly = %s" asm.FullName
printfn "TypeProviderAssembly attributes on Xake.dll = %d"
    (asm.GetCustomAttributes(typeof<FSharp.Core.CompilerServices.TypeProviderAssemblyAttribute>, false).Length)
let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-typed-matches-06")
System.IO.Directory.CreateDirectory root |> ignore
do xake { ExecOptions.Default with ProjectRoot = root; Targets = [ "out/net8.0/Core.dll" ]; Threads = 1; ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true; ResetDb = true } {
    rules [ "out/(fwk:*)/(lib:*).dll" ..> recipe { let! m = getRuleMatches () in printfn "h1 rule ran: fwk=%s lib=%s" m.["fwk"] m.["lib"] } ]
}
