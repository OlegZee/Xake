// Experiment 07, form C: the rule as a custom operation of the xakeScript builder. The existing
// `rule` operation takes any `ExecContext Rule`, so every typed form can be used through it;
// the question is only whether an operation removes noise. Run: dotnet fsi form-c.fsx
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../DesignTime/bin/Release/netstandard2.0/Xake.Patterns.Runtime.dll"
open Xake

type Dll  = Pattern<"out/(cfg:*)/(fwk:*)/(name:*).dll">
type Test = Command<"test::(project:*)/(slice:*)">

let opts targets =
    { ExecOptions.Default with
        ProjectRoot = System.IO.Path.GetTempPath(); Targets = targets; Threads = 1
        ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true; ResetDb = true }

do xake (opts [ "out/Release/net8.0/Core.dll"; "test::core/2" ]) {
    // C1: the existing `rule` operation with a form-B builder as its argument. Without the
    // parentheses (`rule Dll.rule { .. }`): error FS3099: 'rule' is used with an incorrect number
    // of arguments. ... Expected 1 argument(s), but given 2.
    rule (Dll.rule {
        let! m = matched
        printfn "C1 rule (Dll.rule { }): %s %s %s" m.cfg m.fwk m.name
    })
    // C2: the existing `rule` operation with form D
    rule (Test.Rule <| fun m -> recipe {
        printfn "C2 rule (Test.Rule <| fun m -> ..): %s %s" m.project m.slice })
}
