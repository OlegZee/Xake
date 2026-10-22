// Can the provided type be used inline at the rule site, with no `type X = ...` line?
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../Xake.Patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake
let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-typed-matches-03")
do xake { ExecOptions.Default with ProjectRoot = root; Targets = [ "gen/a/b.txt" ]; ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true } {
    rules [
        Pattern<"gen/(x:*)/(y:*).txt">.Rule(fun m -> recipe { printfn "inline: x=%s y=%s" m.x m.y })
    ]
}
