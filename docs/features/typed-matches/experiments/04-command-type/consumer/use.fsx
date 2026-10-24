// Experiment 04: `Xake.Command<"...">` (phony names) next to `Xake.Pattern<"...">`.
//   dotnet fsi use.fsx
// Needs `dotnet build src/core -c Release` and `dotnet build -c Release` in ../Xake.Patterns.
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../Xake.Patterns/bin/Release/netstandard2.0/Xake.Patterns.dll"
open Xake

type Lock  = Command<"lock::(name:*)::(cfg:*)">        // build.fsc.fsx:47
type Test  = Command<"test::(project:*)/(slice:*)">    // build.fsx:117
type TestBuild = Command<"test-build::(project:*)">    // build.fsx:109
type Suite = Command<"testsuite-*">                     // samples/delegated-tests.fsx:99 (bare wildcard)
type Dll   = Pattern<"out/(cfg:*)/(fwk:*)/(name:*).dll">

let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-typed-matches-04")
System.IO.Directory.CreateDirectory root |> ignore
let opts targets =
    { ExecOptions.Default with
        ProjectRoot = root; Targets = targets; Threads = 1
        ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true; ResetDb = true }

// --- generation, no engine
printfn "Lock.Text             = %s" Lock.Text
printfn "Lock.Target(...)      = %s" (Lock.Target(name = "Xake", cfg = "Release"))
printfn "Lock.For (cross)      = %A" (Lock.For(name = [ "Xake"; "Xake.Dotnet" ], cfg = [ "Release"; "Debug" ]))
let t = Lock.Make(name = "Xake", cfg = "Debug")
printfn "Lock.Make(...).Name   = %s ; .cfg = %s" t.Name t.cfg
printfn "Test.Target(...)      = %s" (Test.Target(project = "tests", slice = "rest"))
try printfn "%s" (Test.Target(project = "a/b", slice = "x"))
with e -> printfn "Test.Target '/' value = %s" e.Message
let lockOf : string * string -> string = Lock.Target     // first-class, like Pattern.Path
printfn "first-class           = %s" (lockOf ("Core", "Release"))

// --- engine: typed phony rules, generated names resolved as phony by `need`, file rule beside
let ran = ResizeArray<string>()
let refs = Map [ "App", [ "Core" ]; "Core", [] ]
do xake (opts [ "update-locks"; "test" ]) {
    rules [
        Lock.Rule(fun m -> recipe {
            for r in refs.[m.name] do do! Lock.Need(name = r, cfg = m.cfg)   // a typed `need`
            ran.Add (sprintf "%s (name=%s cfg=%s)" m.Name m.name m.cfg)
        })
        "update-locks" <== Lock.For(name = [ "App" ], cfg = [ "Release"; "Debug" ])
        TestBuild.Rule(fun m -> recipe { ran.Add m.Name })
        Test.Rule(fun m -> recipe {
            do! TestBuild.Need(project = m.project)
            do! need [ Dll.Path(cfg = "Release", fwk = "net8.0", name = m.project) ]   // a file, same `need`
            ran.Add (sprintf "%s (project=%s slice=%s)" m.Name m.project m.slice)
        })
        "test" <== [ for p, s in [ "core", "fast"; "core", "slow" ] -> Test.Target(project = p, slice = s) ]
        Dll.Rule(fun m -> recipe { ran.Add (sprintf "file %s" m.dll) })
        Suite.Rule(fun m -> recipe { ran.Add m.Name })          // matching only: no Target/For for a bare `*`
    ]
}
ran |> Seq.iter (printfn "ran: %s")

do xake (opts [ "testsuite-007" ]) { rules [ Suite.Rule(fun m -> recipe { printfn "ran: %s" m.Name }) ] }
