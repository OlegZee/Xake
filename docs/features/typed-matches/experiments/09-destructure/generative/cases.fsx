// Experiment 09: a generative provided type in a record pattern.  sh ../run.sh cases.fsx
#r "../provider/bin/Release/netstandard2.0/Exp09.Generative.dll"
open Exp09.Generative
type DllG = Gen<"cfg,fwk,name">
let g = DllG("Release", "net8.0", "Core")

//#case g01-generated-class-works
printfn "%s %s %s (%s)" g.cfg g.fwk g.name (g.GetType().FullName)

//#case g02-record-pattern
let { fwk = fwk } = g
printfn "%s" fwk

//#case g03-record-pattern-qualified
let { DllG.fwk = fwk } = g
printfn "%s" fwk

//#case g04-record-pattern-annotated
let ({ fwk = fwk }: DllG) = g
printfn "%s" fwk

//#case g05-record-construction
let r: DllG = { cfg = "a"; fwk = "b"; name = "c" }
printfn "%s" r.fwk
