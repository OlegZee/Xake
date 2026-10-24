// Experiment 05: a literal '(' in a pattern is a regex group today (escapeLiteral leaves
// parentheses alone), so it shifts the numbers bare wildcards are captured under.
//   dotnet fsi edge.fsx
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../DesignTime/bin/Release/netstandard2.0/Xake.Patterns.Runtime.dll"
open Xake

type Paren = Pattern<"lit(x)/*.txt">

let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-typed-matches-05")
System.IO.Directory.CreateDirectory root |> ignore
do xake { ExecOptions.Default with ProjectRoot = root; Targets = [ "litx/a.txt" ]; Threads = 1; ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true; ResetDb = true } {
    rules [ Paren.Rule(fun m -> recipe {
        let! all = getRuleMatches ()
        printfn "matched 'litx/a.txt' (the parens are a group, not literals): _1=%s, all=%A" m._1 (Map.toList all) }) ]
}
