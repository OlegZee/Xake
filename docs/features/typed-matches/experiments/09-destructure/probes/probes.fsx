// Experiment 09, probes that need the provider switched (the design-time code reads the variables
// when the type is instantiated):
//   sh run.sh   (runs every case once per switch -> output-overload.txt, output-ap.txt)
// XAKE09_RULE_OVERLOAD=1: the tuple lambda as a second `Rule` overload (form D parity, item e);
// XAKE09_AP=1: a provided static method named `|Parts|` (a provided active pattern?, item c).
// Same header as ../consumer/cases.fsx.
#r "../../engine/src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../../patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
open Xake.Patterns

// build.fsc.fsx:64   target "out/(cfg:*)/(fwk:*)/(name:*).dll"
type Asm = Target<"out/(cfg:*)/(fwk:*)/(name:*).dll">
// build.fsc.fsx:47   "lock::(name:*)::(cfg:*)" => recipe { ... alwaysRerun ... }
type Lock = Command<"lock::(name:*)::(cfg:*)">
// build.fsc.fsx:74   "test-build::(project:*)"  -- one group
type TestBuild = Command<"test-build::(project:*)">
// build.fsx:140      targets ["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"]  -- labelled outputs
type Lib = Target<"dll=out/(fwk:*)/(lib:*).dll", "xml=out/(fwk:*)/(lib:*).xml">

// stubs of the script's helpers
let lockOf (name: string) (cfg: string) = $"locks/{name}.{cfg}.lock"
let descriptionOf (name: string) = $"projects/{name}.json"
let versions = "versions.props"
module Locks =
    let compileAs (lock: string) (fwk: string) (_: string) = recipe { printfn "compileAs %s %s" lock fwk }
    let generate (cfg: string) (desc: string) = recipe { printfn "generate %s %s" desc cfg }
module Description =
    let load (d: string) = recipe { return {| ProjectRefs = (if d.Contains "Dotnet" then [ "Core" ] else []) |} }
let testProjectDir (p: string) = $"src/tests/{p}"
let sh (cmd: string) = recipe { printfn "sh: %s" cmd }

let run (targets: string list) (rs: ExecContext Rule list) =
    let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-09")
    if System.IO.Directory.Exists root then System.IO.Directory.Delete(root, true)
    System.IO.Directory.CreateDirectory root |> ignore
    xake { ExecOptions.Default with
             ProjectRoot = root; Targets = targets; Threads = 1
             ConLogLevel = Silent; FileLogLevel = Silent; ResetDb = true; ThrowOnError = true } {
        rules (rs @ [ "out/(cfg:*)/(fwk:*)/(name:*).txt" ..> recipe { () } ])
    }

//#case c1-provided-active-pattern-in-match
let r = Asm.Rule <| fun m -> recipe {
    match m with
    | Asm.Parts (cfg, fwk, name) -> do! Locks.compileAs (lockOf name cfg) fwk versions }
run [ "out/Release/net8.0/Core.dll" ] [ r ]

//#case c2-provided-active-pattern-in-lambda
let r = Asm.Rule <| fun (Asm.Parts (cfg, fwk, name)) -> recipe {
    do! Locks.compileAs (lockOf name cfg) fwk versions }
run [ "out/Release/net8.0/Core.dll" ] [ r ]

//#case c3-provided-active-pattern-opened
open type Asm
let r = Asm.Rule <| fun (Parts (cfg, fwk, name)) -> recipe {
    do! Locks.compileAs (lockOf name cfg) fwk versions }
run [ "out/Release/net8.0/Core.dll" ] [ r ]

//#case c4-provided-method-exists
// the method itself is there, callable by its odd name
let r = Asm.Rule <| fun m -> recipe {
    let cfg, fwk, name = Asm.``|Parts|`` m
    do! Locks.compileAs (lockOf name cfg) fwk versions }
run [ "out/Release/net8.0/Core.dll" ] [ r ]

//#case e1-rule-overloaded-typed-lambda
// `Rule` with two overloads (typed match, tuple): today's `fun m -> ... m.fwk`
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.Rule <| fun m -> recipe {
        do! Locks.compileAs (lockOf m.name m.cfg) m.fwk versions }
]

//#case e2-rule-overloaded-tuple-lambda
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.Rule <| fun (cfg, fwk, name) -> recipe {
        do! Locks.compileAs (lockOf name cfg) fwk versions }
]

//#case e3-rule-overloaded-parenthesised
run [ "out/Release/net8.0/Core.dll" ] [
    Asm.Rule(fun (cfg, fwk, name) -> recipe {
        do! Locks.compileAs (lockOf name cfg) fwk versions })
    Asm.Rule(fun m -> recipe {
        do! Locks.compileAs (lockOf m.name m.cfg) m.fwk versions })
]
