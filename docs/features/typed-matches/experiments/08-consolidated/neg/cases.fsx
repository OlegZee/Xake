// INTENTIONALLY RED in an editor: every case below is a deliberate compile error (and the cases
// repeat `let r`, because `sh ../ns-check/run.sh cases.fsx` splits this file per case). For the
// editor open a single case, neg/cases/NN-*.fsx, or ../consumer/showcase.fsx for the working API.
// Experiment 08, negative cases. `sh ../ns-check/run.sh cases.fsx` splits this file at the `//#case`
// lines (header + case -> cases/<name>.fsx), runs each with the SDK 10 and SDK 8 fsi, and writes
// the errors (or the run-time output) to output.txt.
#r "../../engine/src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../../patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
open Xake.Patterns
type Asm  = Target<"out/(cfg:*)/(fwk:*)/(name:*).dll">
type Lib  = Target<"dll=out/(fwk:*)/(lib:*).dll", "xml=out/(fwk:*)/(lib:*).xml">
type Test = Command<"test::(project:*)/(slice:*)">
type Clean = Command<"clean">

//#case 01-group-typo-form-d
let r = Asm.Rule <| fun m -> recipe { printfn "%s" m.fkw }

//#case 02-group-typo-form-b
let r = Asm.rule {
    let! m = matched
    printfn "%s" m.fkw }

//#case 03-output-label-typo
let r = Lib.Rule <| fun m -> recipe { printfn "%s" m.pdb }

//#case 04-output-by-extension-not-label
type Lib2 = Target<"out/(fwk:*)/(lib:*).dll", "out/(fwk:*)/(lib:*).xml">
let r = Lib2.Rule <| fun m -> recipe { printfn "%s %s" m.dll m.Out2 }

//#case 05-make-missing-argument
printfn "%A" (Lib.Make(fwk = "net8.0").dll)

//#case 06-glob-on-command
printfn "%A" (Test.Glob(project = "core"))

//#case 07-matched-outside-a-rule
let r = recipe {
    let! m = matched
    printfn "%s" m.fwk }

//#case 08-matched-in-plain-recipe-inside-rule
let r = Asm.rule {
    do! recipe { let! m = matched in printfn "%A" m }
}

//#case 09-clash-group-named-like-member
type Bad = Target<"out/(Make:*).dll">
printfn "%s" (Bad.Format(Make = "x"))

//#case 10-clash-label-and-group
type Bad = Target<"lib=out/(lib:*).dll", "xml=out/(lib:*).xml">
printfn "%s" (Bad.Format(lib = "x"))

//#case 11-clash-command-group-Name
type Bad = Command<"run::(Name:*)">
printfn "%s" (Bad.Format(Name = "x"))

//#case 12-multi-output-group-mismatch
type Bad = Target<"out/(fwk:*)/(lib:*).dll", "out/(fwk:*)/(name:*).xml">
printfn "%A" (Bad.For(fwk = [ "a" ], lib = [ "b" ]))

//#case 13-no-group-command-given-a-match
let r = Clean.Rule <| fun m -> recipe { printfn "%A" m }

//#case 14-no-group-command-form-b-matched
let r = Clean.rule {
    let! m = matched
    printfn "%A" m }

//#case 15-pattern-member-on-target
printfn "%s" (Asm.Pattern(cfg = "Release", fwk = "net8.0", name = "x"))

//#case 16-format-member-on-command-now-valid
printfn "%s" (Test.Format(project = "a", slice = "1"))

//#case 17-run-time-value-not-matching-back
printfn "%s" (Test.Format(project = "a/b", slice = "1"))
