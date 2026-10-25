// Experiment 07, negative cases. `sh run-neg.sh` splits this file at the `//#case` lines: every
// case is the header (up to the first case) plus its own lines, written to cases/<name>.fsx and
// run with `dotnet fsi`; the error lines (or the run-time output) go to output.txt.
#r "../../../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../../../DesignTime/bin/Release/netstandard2.0/Xake.Patterns.Runtime.dll"
open Xake
type Dll  = Pattern<"out/(cfg:*)/(fwk:*)/(name:*).dll">
type Lib  = Pattern<"out/(fwk:*)/(lib:*).dll", "out/(fwk:*)/(lib:*).xml">
type Test = Command<"test::(project:*)/(slice:*)">
let opts t = { ExecOptions.Default with ProjectRoot = System.IO.Path.GetTempPath(); Targets = [ t ]; ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true; ThrowOnError = true }

//#case a-typo
let r = Dll.Pattern ..> recipe {
    let! m = Dll.Get
    printfn "%s" m.fkw }

//#case b-typo
let r = Dll.rule {
    let! m = matched
    printfn "%s" m.fkw }

//#case d-typo
let r = Dll.Rule <| fun m -> recipe {
    printfn "%s" m.fkw }

//#case a-multi-typo-output
let r = Lib.Patterns *..> recipe {
    let! m = Lib.Get
    printfn "%s" m.pdb }

//#case b-multi-typo-output
let r = Lib.rule {
    let! m = matched
    printfn "%s" m.pdb }

//#case b-command-typo
let r = Test.rule {
    let! m = matched
    printfn "%s" m.slcie }

//#case a-multi-single-operand
let r = Lib.Pattern ..> recipe { () }

//#case a-command-with-file-operator
// a phony name given to the file-rule operator: compiles (a FileRule named "test::..."), as the
// string API does; form A cannot tell the operator from the pattern kind
let r : ExecContext Rule = Test.Pattern ..> recipe { () }
printfn "compiled: %A" r

//#case b-matched-in-plain-recipe
let r = Dll.Pattern ..> recipe {
    let! m = matched
    printfn "%s" m.fwk }

//#case b-matched-in-nested-recipe
let r = Dll.rule {
    let! m = matched
    do! recipe { let! m2 = matched in () } }

//#case a-get-of-another-pattern
do xake (opts "out/Release/net8.0/Core.dll") {
    rules [ Dll.Pattern ..> recipe {
        let! m = Lib.Get
        printfn "lib=%s" m.lib } ] }

//#case b-get-of-another-pattern
do xake (opts "out/Release/net8.0/Core.dll") {
    rules [ Dll.rule {
        let! m = Lib.Get
        printfn "lib=%s" m.lib } ] }

//#case b-no-matched
do xake (opts "out/Release/net8.0/Core.dll") {
    rules [ Dll.rule { printfn "no match bound, still a rule" } ] }

//#case b-one-line
do xake (opts "out/Release/net8.0/Core.dll") {
    rules [ Dll.rule { let! m = matched in printfn "one line: %s %s %s" m.cfg m.fwk m.name } ] }
