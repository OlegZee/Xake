// Experiment 08: a clean showcase of the typed API. Opens with zero errors in VSCode (Ionide) once
//   sh make-engine.sh && dotnet build engine/src/core -c Release -f netstandard2.0 && dotnet build patterns -c Release
// have been run from the experiment folder (see ../../README.md, "Opening in an IDE").
//   dotnet fsi showcase.fsx
#r "../engine/src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
open Xake.Patterns      // after `open Xake`: Target<...> is the provided type

// one output, unlabelled: the output is m.Out1
type Asm = Target<"out/(cfg:*)/(fwk:*)/(name:*).dll">
// several outputs with labels: m.dll, m.xml
type Lib = Target<"dll=out/(fwk:*)/(lib:*).dll", "xml=out/(fwk:*)/(lib:*).xml">
// only globbed, never a rule
type Sources = Target<"src/(dir:*)/**/*.fs">
// commands: with groups, and without
type Test = Command<"test::(project:*)/(slice:*)">
type Clean = Command<"clean">

// Format: a path (the first output); For: the cross product of lists; Glob: a Fileset
let one = Asm.Format(cfg = "Release", fwk = "net8.0", name = "App")
let both = Lib.Make(fwk = "net8.0", lib = "Xake")      // typed value: both.dll, both.xml
let dll, xml = both.dll, both.xml
let all = Lib.For(fwk = [ "net8.0"; "net462" ], lib = [ "Xake"; "Xake.Dotnet" ])
let sources = Sources.Glob(dir = "core")
let testName = Test.Format(project = "Xake", slice = "1")   // "test::Xake/1"

// form D: `X.Rule <| fun m -> recipe { ... }`
let ruleD =
    Lib.Rule <| fun m -> recipe {
        do! need [ Asm.Format(cfg = "Release", fwk = m.fwk, name = m.lib) ]
        printfn "%s %s -> %s, %s" m.lib m.fwk m.dll m.xml
    }

// form B: `X.rule { let! m = matched ... }`
let ruleB =
    Asm.rule {
        let! m = matched
        printfn "%s %s %s -> %s" m.cfg m.fwk m.name m.Out1
    }

// commands: a typed match when there are groups, unit otherwise
let testD = Test.Rule <| fun m -> recipe { printfn "test %s slice %s (%s)" m.project m.slice m.Name }
let testB =
    Test.rule {
        let! m = matched
        printfn "test %s" m.project
    }
let cleanD = Clean.Rule <| fun () -> recipe { printfn "clean" }
let cleanB = Clean.rule { printfn "clean" }

// need over a generated list; phony rules
let rules = [
    "build" <== [ for fwk in [ "net8.0"; "net462" ] do yield! Lib.For(fwk = [ fwk ], lib = [ "Xake" ]) ]
    "test" <== [ for p, s in [ "Xake", "1"; "Xake", "2" ] -> Test.Format(project = p, slice = s) ]
    "all" => recipe { do! need [ for fwk in [ "net8.0"; "net462" ] -> Lib.Format(fwk = fwk, lib = "Xake") ] }
    ruleD; ruleB; testD; testB; cleanD; cleanB
]

printfn "%s | %s | %s | %A | %s | %d rules" one dll xml all testName rules.Length
