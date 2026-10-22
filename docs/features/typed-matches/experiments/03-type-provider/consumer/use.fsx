// Experiment 03: the erased `Xake.Pattern<...>` provider used from `dotnet fsi`.
//   dotnet fsi use.fsx
// Needs `dotnet build src/core -c Release` and `dotnet build -c Release` in ../Xake.Patterns.
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../Xake.Patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake

type Dll = Pattern<"out/(fwk:*)/(lib:*).dll">
type Lib = Pattern<"out/(fwk:*)/(lib:*).dll; out/(fwk:*)/(lib:*).xml">
type Src = Pattern<"(dir:*)/(file:*).(ext:c*)">

let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-typed-matches-03")
System.IO.Directory.CreateDirectory root |> ignore
let opts targets =
    { ExecOptions.Default with
        ProjectRoot = root; Targets = targets; Threads = 1
        ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true; ResetDb = true }

// --- generation (inverse of matching), no engine involved
printfn "Dll.Text            = %s" Dll.Text
printfn "Dll.Path(...)       = %s" (Dll.Path(fwk = "net8.0", lib = "Xake"))
let l = Lib.Make(fwk = "netstandard2.0", lib = "Xake")
printfn "Lib.Make(...).dll   = %s ; .xml = %s ; fwk = %s" l.dll l.xml l.fwk
printfn "Src.Path ok         = %s" (Src.Path(dir = "src", file = "a", ext = "cs"))
try printfn "%s" (Src.Path(dir = "src", file = "a", ext = "fs"))
with e -> printfn "Src.Path constraint = %s" e.Message

// --- lists for `need`
let frameworks = [ "net462"; "netstandard2.0" ]
printfn "comprehension       = %A" [ for f in frameworks -> Dll.Path(fwk = f, lib = "Xake") ]
printfn "Lib.All (cross)     = %A" (Lib.All(fwk = frameworks, lib = [ "Xake"; "Xake.Dotnet" ]))
printfn "Lib.Glob(lib=Xake)  = %A" (Lib.Glob(lib = "Xake"))
printfn "Dll.Glob()          = %A" (Dll.Glob())
// a provided method as a first-class function (the hermetic API takes `lockOf`-style functions)
let dllOf : string * string -> string = Dll.Path
printfn "first-class         = %s" (dllOf ("net8.0", "Core"))
let outOf fwk lib = Dll.Path(fwk = fwk, lib = lib)     // curried wrapper, when the API wants curried
printfn "curried wrapper     = %s" (outOf "net8.0" "Core")

// --- matching: Rule(fun m -> ...) with typed access
do xake (opts [ Dll.Path(fwk = "net8.0", lib = "Core") ]) {
    rules [
        Dll.Rule(fun m -> recipe {
            printfn "rule: fwk=%s lib=%s" m.fwk m.lib
        })
    ]
}

// --- multi-file: the other output's path is a typed property
do xake (opts [ "out/net8.0/Core.xml" ]) {
    rules [
        Lib.Rule(fun m -> recipe {
            printfn "multi: fwk=%s lib=%s dll=%s xml=%s" m.fwk m.lib m.dll m.xml
        })
    ]
}

// --- the Get form, inside a classic `..>` rule
do xake (opts [ "out/net9.0/Other.dll" ]) {
    rules [
        Dll.Text ..> recipe {
            let! m = Dll.Get
            printfn "get: fwk=%s lib=%s" m.fwk m.lib
        }
    ]
}
