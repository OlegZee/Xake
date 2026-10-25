// Experiment 06: the split provider from a package laid out as decision 1 wants it
// (runtime in lib/, design-time under typeproviders/fsharp41/netstandard2.0).
//   (cd ../Pkg && dotnet pack -c Release -o <feed dir>) && dotnet fsi use-nuget.fsx
#i "nuget: /Users/olegz/.claude/jobs/d57e8547/tmp/feed-06"
#r "nuget: Xake.SepDllExp, 0.0.1-exp06"
open Xake

type Lib = Pattern<"out/(fwk:*)/(lib:*).dll", "out/(fwk:*)/(lib:*).xml">
type Pos = Pattern<"gen/*/(name:*)-?.txt">

printfn "nuget: Lib.All      = %A" (Lib.All(fwk = [ "net8.0" ], lib = [ "A"; "B" ]))
printfn "nuget: Pos.Format   = %s" (Pos.Format(name = "core", _1 = "a", _2 = "7"))
printfn "nuget: Lib.GlobMasks= %A" (Lib.GlobMasks(fwk = "net8.0"))
let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-typed-matches-06")
System.IO.Directory.CreateDirectory root |> ignore
do xake { ExecOptions.Default with ProjectRoot = root; Targets = [ "out/net8.0/Core.xml" ]; Threads = 1; ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true; ResetDb = true } {
    rules [ Lib.Rule(fun m -> recipe { printfn "nuget: multi fwk=%s lib=%s dll=%s" m.fwk m.lib m.dll }) ]
}
