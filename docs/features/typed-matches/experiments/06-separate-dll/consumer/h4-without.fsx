// Experiment 06: experiment 03's consumer, adapted to the split provider (runtime dll + separate
// design-time dll) built on the engine's PatternAst.
//   sh ../slice-ast.sh && (cd ../DesignTime && dotnet build -c Release) && dotnet fsi use.fsx
// The runtime dll is referenced from the design-time project's output folder, where the
// design-time dll sits next to it (the compiler looks for TypeProviderAssembly("...") there).
#r "../stage/without/lib/netstandard2.0/Xake.dll"
#r "../stage/without/lib/netstandard2.0/Xake.Patterns.dll"
open Xake

type Dll = Pattern<"out/(fwk:*)/(lib:*).dll">
type Lib = Pattern<"out/(fwk:*)/(lib:*).dll", "out/(fwk:*)/(lib:*).xml">   // decision 2
type Src = Pattern<"(dir:*)/(file:*).(ext:c*)">
type Pos = Pattern<"gen/*/(name:*)-?.txt">                                 // decision 6
type Deep = Pattern<"src/**/(name:*).fs">
type Nest = Pattern<"(root:*/(leaf:*-a)).ss">

let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-typed-matches-06")
System.IO.Directory.CreateDirectory root |> ignore
let opts targets =
    { ExecOptions.Default with
        ProjectRoot = root; Targets = targets; Threads = 1
        ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true; ResetDb = true }

// --- generation, now an AST walk (PatternAstGen.format) checked by the engine's matchGroups
printfn "Dll.Text            = %s" Dll.Text
printfn "Dll.Format(...)     = %s" (Dll.Format(fwk = "net8.0", lib = "Xake"))
let l = Lib.Make(fwk = "netstandard2.0", lib = "Xake")
printfn "Lib.Make(...).dll   = %s ; .xml = %s ; fwk = %s" l.dll l.xml l.fwk
printfn "Src.Format ok       = %s" (Src.Format(dir = "src", file = "a", ext = "cs"))
try printfn "%s" (Src.Format(dir = "src", file = "a", ext = "fs"))
with e -> printfn "Src.Format constr.  = %s" e.Message
try printfn "%s" (Dll.Format(fwk = "net8.0/x", lib = "Xake"))
with e -> printfn "'/' in a * value    = %s" e.Message

// --- positional bare wildcards
printfn "Pos.Format          = %s" (Pos.Format(name = "core", _1 = "a", _2 = "7"))
printfn "Deep.Format         = %s" (Deep.Format(name = "Main", _1 = "x/y"))
printfn "Nest.Format         = %s" (Nest.Format(root = "top/x-a", leaf = "x-a"))

// --- lists and filesets
let frameworks = [ "net462"; "netstandard2.0" ]
printfn "Lib.All (cross)     = %A" (Lib.All(fwk = frameworks, lib = [ "Xake"; "Xake.Dotnet" ]))
printfn "Lib.GlobMasks(lib)  = %A" (Lib.GlobMasks(lib = "Xake"))
printfn "Pos.GlobMasks(_2)   = %A" (Pos.GlobMasks(_2 = "1"))
printfn "Nest.GlobMasks()    = %A" (Nest.GlobMasks())
printfn "Dll.Glob() : Fileset= %A" (Dll.Glob())

// --- matching
do xake (opts [ Dll.Format(fwk = "net8.0", lib = "Core") ]) {
    rules [ Dll.Rule(fun m -> recipe { printfn "rule: fwk=%s lib=%s" m.fwk m.lib }) ]
}
do xake (opts [ "out/net8.0/Core.xml" ]) {
    rules [ Lib.Rule(fun m -> recipe { printfn "multi: fwk=%s lib=%s dll=%s xml=%s" m.fwk m.lib m.dll m.xml }) ]
}
do xake (opts [ "gen/abc/core-7.txt" ]) {
    rules [ Pos.Rule(fun m -> recipe { printfn "positional: _1=%s name=%s _2=%s path=%s" m._1 m.name m._2 m.txt }) ]
}
do xake (opts [ "src/a/b/Main.fs" ]) {
    rules [ Deep.Text ..> recipe {
        let! m = Deep.Get
        printfn "get: _1=%s name=%s" m._1 m.name } ]
}
