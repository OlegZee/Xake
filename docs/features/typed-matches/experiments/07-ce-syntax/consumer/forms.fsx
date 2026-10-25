// Experiment 07: lower-noise rule syntax for typed patterns. Three real rules (build.fsc.fsx's
// compile rule, build.fsx's multi-file library rule, build.fsx's test phony rule) written in each
// form, run by the engine, then all forms and the string API together in one `rules [...]`.
//   (cd ../DesignTime && dotnet build -c Release) && dotnet fsi forms.fsx
#r "../../../../../../src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../DesignTime/bin/Release/netstandard2.0/Xake.Patterns.Runtime.dll"
open Xake

type Dll  = Pattern<"out/(cfg:*)/(fwk:*)/(name:*).dll">                       // build.fsc.fsx
type Lib  = Pattern<"out/(fwk:*)/(lib:*).dll", "out/(fwk:*)/(lib:*).xml">     // build.fsx
type Test = Command<"test::(project:*)/(slice:*)">                            // build.fsx
type TestBuild = Command<"test-build::(project:*)">

// stand-ins for the scripts' helpers
let compileAs lock (fwk: string) = recipe { printfn "    compileAs %s %s" lock fwk }
let lockOf name cfg = $"locks/{name}.{cfg}.lock"
let dotnet (args: string list) = recipe { printfn "    dotnet %s" (String.concat " " args) }
let sh (cmd: string) = recipe { printfn "    sh %s" cmd }

let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-typed-matches-07")
System.IO.Directory.CreateDirectory root |> ignore
let opts targets =
    { ExecOptions.Default with
        ProjectRoot = root; Targets = targets; Threads = 1
        ConLogLevel = Silent; FileLogLevel = Silent; NoPersist = true; ResetDb = true }
let targets = [ "out/Release/net8.0/Core.dll"; "out/netstandard2.0/Xake.xml"; "test::core/2" ]
let run title (rs: ExecContext Rule list) =
    printfn "--- %s" title
    do xake (opts targets) { rules rs }

// ---------------------------------------------------------------- form A: pattern as an operand
run "A: Dll.Pattern ..> recipe { let! m = Dll.Get ... }" [
    Dll.Pattern ..> recipe {
        let! m = Dll.Get
        do! compileAs (lockOf m.name m.cfg) m.fwk
    }
    Lib.Patterns *..> recipe {
        let! m = Lib.Get
        do! dotnet [ "build"; $"src/{m.lib}"; "--framework"; m.fwk; "--output"; $"./out/{m.fwk}"; $"# {m.dll} {m.xml}" ]
    }
    Test.Pattern => recipe {
        let! m = Test.Get
        do! need [ TestBuild.Target(project = m.project) ]
        do! sh $"dotnet test {m.project} slice {m.slice} ({m.Name})"
    }
    TestBuild.Pattern => recipe {
        let! m = TestBuild.Get
        do! sh $"dotnet build {m.project}"
    }
]

// ---------------------------------------------------------------- form B: a builder on the type
run "B: Dll.rule { let! m = matched ... }" [
    Dll.rule {
        let! m = matched
        do! compileAs (lockOf m.name m.cfg) m.fwk
    }
    Lib.rule {
        let! m = matched
        do! dotnet [ "build"; $"src/{m.lib}"; "--framework"; m.fwk; "--output"; $"./out/{m.fwk}"; $"# {m.dll} {m.xml}" ]
    }
    Test.rule {
        let! m = matched
        do! need [ TestBuild.Target(project = m.project) ]
        do! sh $"dotnet test {m.project} slice {m.slice} ({m.Name})"
    }
    TestBuild.rule {
        let! m = matched
        do! sh $"dotnet build {m.project}"
    }
]

// ---------------------------------------------------------------- form D: pipe into Rule
run "D: Dll.Rule <| fun m -> recipe { ... }" [
    Dll.Rule <| fun m -> recipe {
        do! compileAs (lockOf m.name m.cfg) m.fwk
    }
    Lib.Rule <| fun m -> recipe {
        do! dotnet [ "build"; $"src/{m.lib}"; "--framework"; m.fwk; "--output"; $"./out/{m.fwk}"; $"# {m.dll} {m.xml}" ]
    }
    Test.Rule <| fun m -> recipe {
        do! need [ TestBuild.Target(project = m.project) ]
        do! sh $"dotnet test {m.project} slice {m.slice} ({m.Name})"
    }
    TestBuild.Rule <| fun m -> recipe {
        do! sh $"dotnet build {m.project}"
    }
]

// ---------------------------------------------------------------- today's 05 shape, for reference
run "05: Dll.Rule(fun m -> recipe { ... })" [
    Dll.Rule(fun m -> recipe {
        do! compileAs (lockOf m.name m.cfg) m.fwk
    })
    Lib.Rule(fun m -> recipe {
        do! dotnet [ "build"; $"src/{m.lib}"; "--framework"; m.fwk; "--output"; $"./out/{m.fwk}"; $"# {m.dll} {m.xml}" ]
    })
    Test.Rule(fun m -> recipe {
        do! need [ TestBuild.Target(project = m.project) ]
        do! sh $"dotnet test {m.project} slice {m.slice} ({m.Name})"
    })
    TestBuild.Rule(fun m -> recipe { do! sh $"dotnet build {m.project}" })
]

// ---------------------------------------------------------------- mixed: every form + string API
run "mixed list (string API, A, B, D, existing target/command builders)" [
    "main" <== [ "build"; "test" ]
    "build" <== [ "out/Release/net8.0/Core.dll"; "out/netstandard2.0/Xake.xml" ]
    "test" <== [ Test.Target(project = "core", slice = "2") ]
    Dll.rule {
        let! m = matched
        let! v = getVar "VERSION"        // any other recipe op, Bind overloads of RecipeBuilder
        do! compileAs (lockOf m.name m.cfg) m.fwk
    }
    Lib.Patterns *..> recipe {
        let! m = Lib.Get
        do! dotnet [ "build"; $"src/{m.lib}"; "-f"; m.fwk ]
    }
    Test.Rule <| fun m -> recipe {
        do! need [ TestBuild.Target(project = m.project) ]
        do! sh $"dotnet test {m.project} slice {m.slice}"
    }
    "test-build::(project:*)" => recipe {      // string API, unchanged
        let! project = getRuleMatch "project"
        do! sh $"dotnet build {project}"
    }
    command "pack" { do! sh "pack" }           // existing builders, unchanged
    target "out/pkg/(ver:*).nupkg" { do! sh "nupkg" }
]
printfn "--- mixed, 'main'"
do xake (opts [ "main" ]) {
    rules [
        "main" <== [ "build"; "test" ]
        "build" <== [ "out/Release/net8.0/Core.dll"; "out/netstandard2.0/Xake.xml" ]
        "test" <== [ Test.Target(project = "core", slice = "2") ]
        Dll.rule { let! m = matched in do! compileAs (lockOf m.name m.cfg) m.fwk }
        Lib.Patterns *..> recipe { let! m = Lib.Get in printfn "    lib %s %s" m.fwk m.lib }
        Test.Rule <| fun m -> recipe {
            do! need [ TestBuild.Target(project = m.project) ]
            do! sh $"dotnet test {m.project} slice {m.slice}" }
        "test-build::(project:*)" => recipe {
            let! project = getRuleMatch "project"
            do! sh $"dotnet build {project}" }
    ]
}
