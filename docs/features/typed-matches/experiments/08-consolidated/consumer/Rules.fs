/// Experiment 08, the consumer: real rules of build.fsx / build.fsc.fsx rewritten on Target<> and
/// Command<>, in form D (`X.Rule <| fun m -> recipe { }`) and form B (`X.rule { let! m = matched }`).
/// Compilation is simulated (files are written, not compiled) so that the engine really builds,
/// tracks the dependencies and rebuilds incrementally. Shared by the scripts (`#load "Rules.fs"`)
/// and the compiled projects (`<Compile Include="../consumer/Rules.fs" />`).
module Rules

open System.IO
open Xake
open Xake.Patterns      // after `open Xake`: Target<...> is the provided type (see README 08, names)

// ---------------------------------------------------------------- the patterns
// build.fsx:140, `targets ["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"]` -- labelled outputs
type Lib = Target<"dll=out/(fwk:*)/(lib:*).dll", "xml=out/(fwk:*)/(lib:*).xml">
// build.fsc.fsx:63, `target "out/(cfg:*)/(fwk:*)/(name:*).dll"` -- one output, unlabelled (m.Out1)
type Asm = Target<"out/(cfg:*)/(fwk:*)/(name:*).dll">
// build.fsx:145, the sources of a library: no rule, only Glob (bare wildcards are _1, _2)
type Sources = Target<"src/(dir:*)/**/*.fs">
// build.fsx:109,117 -- phony with groups
type TestBuild = Command<"test-build::(project:*)">
type Test = Command<"test::(project:*)/(slice:*)">
// build.fsx:104 -- a command without groups
type Clean = Command<"clean">

type Library = { Name: string; Dir: string; Needs: string list }
let libraries =
    [ { Name = "Xake"; Dir = "core"; Needs = [] }
      { Name = "Xake.Dotnet"; Dir = "dotnet"; Needs = [ "Xake" ] } ]
let library name = libraries |> List.find (fun l -> l.Name = name)
let frameworks = [ "netstandard2.0"; "net462" ]
let testJobs = [ "Xake", "1"; "Xake", "2"; "Xake.Dotnet", "1" ]

let say fmt = Printf.kprintf (fun s -> printfn "    %s" s) fmt
let write (root: string) (path: string) (text: string) =
    let full = Path.Combine(root, path)
    Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
    File.WriteAllText(full, text)

/// The rules that do not depend on the form: the need lists and a string-API phony rule (`=>`,
/// no AlwaysRerun) to compare the Command<> rules with.
let common = [
    "main" <== [ "build"; "test"; "docs::Xake" ]
    // the `binaries` helper of build.fsx, from the pattern: every output for every framework
    "build" <== [ for lib in libraries do yield! Lib.For(fwk = frameworks, lib = [ lib.Name ]) ]
    "test" <== [ for project, slice in testJobs -> Test.Format(project = project, slice = slice) ]
    // a phony rule WITH a dependency: `=>` reruns only when the dependency changes (a phony rule
    // with no dependency at all reruns every time anyway, `=>` or not -- see clean/clean-string)
    "docs::(project:*)" => recipe {
        let! project = getRuleMatch "project"
        do! need [ Lib.Make(fwk = "netstandard2.0", lib = project).xml ]
        say "docs %s (string API, =>)" project
    }
]

// ---------------------------------------------------------------- form D
let rulesD (root: string) = common @ [
    Lib.Rule <| fun m -> recipe {
        let lib = library m.lib
        let! sources = getFiles (Sources.Glob(dir = lib.Dir))
        do! needFiles sources
        do! need [ for dep in lib.Needs -> Lib.Make(fwk = m.fwk, lib = dep).dll ]
        say "compile %s for %s -> %s + %s" m.lib m.fwk m.dll m.xml
        let (Fileset.Filelist files) = sources
        let text = files |> List.map (fun f -> File.ReadAllText f.FullName) |> String.concat ""
        write root m.dll (text + String.concat "," lib.Needs)
        write root m.xml $"<doc>{m.lib}</doc>"
    }
    Asm.Rule <| fun m -> recipe {
        let input = Lib.Format(fwk = m.fwk, lib = m.name)
        do! need [ input ]
        say "compileAs %s %s %s -> %s" m.name m.cfg m.fwk m.Out1
        write root m.Out1 (File.ReadAllText(Path.Combine(root, input)))
    }
    TestBuild.Rule <| fun m -> recipe {
        do! need [ Asm.Format(cfg = "Release", fwk = "netstandard2.0", name = m.project) ]
        say "test-build %s" m.project
    }
    Test.Rule <| fun m -> recipe {
        do! need [ TestBuild.Format(project = m.project) ]
        say "test %s slice %s (%s)" m.project m.slice m.Name
    }
    Clean.Rule <| fun () -> recipe {
        say "clean"
        if Directory.Exists(Path.Combine(root, "out")) then Directory.Delete(Path.Combine(root, "out"), true)
    }
    "clean-string" => recipe { say "clean-string (string API, =>)" }
]

// ---------------------------------------------------------------- form B
let rulesB (root: string) = common @ [
    Lib.rule {
        let! m = matched
        let lib = library m.lib
        let! sources = getFiles (Sources.Glob(dir = lib.Dir))
        do! needFiles sources
        do! need [ for dep in lib.Needs -> Lib.Make(fwk = m.fwk, lib = dep).dll ]
        say "compile %s for %s -> %s + %s" m.lib m.fwk m.dll m.xml
        let (Fileset.Filelist files) = sources
        let text = files |> List.map (fun f -> File.ReadAllText f.FullName) |> String.concat ""
        write root m.dll (text + String.concat "," lib.Needs)
        write root m.xml $"<doc>{m.lib}</doc>"
    }
    Asm.rule {
        let! m = matched
        let input = Lib.Format(fwk = m.fwk, lib = m.name)
        do! need [ input ]
        say "compileAs %s %s %s -> %s" m.name m.cfg m.fwk m.Out1
        write root m.Out1 (File.ReadAllText(Path.Combine(root, input)))
    }
    TestBuild.rule {
        let! m = matched
        do! need [ Asm.Format(cfg = "Release", fwk = "netstandard2.0", name = m.project) ]
        say "test-build %s" m.project
    }
    Test.rule {
        let! m = matched
        do! need [ TestBuild.Format(project = m.project) ]
        say "test %s slice %s (%s)" m.project m.slice m.Name
    }
    Clean.rule {
        say "clean"
        if Directory.Exists(Path.Combine(root, "out")) then Directory.Delete(Path.Combine(root, "out"), true)
    }
    "clean-string" => recipe { say "clean-string (string API, =>)" }
]

// ---------------------------------------------------------------- the scenario
let private opts root targets reset =
    { ExecOptions.Default with
        ProjectRoot = root; Targets = targets; Threads = 1
        ConLogLevel = Silent; FileLogLevel = Silent; ResetDb = reset; ThrowOnError = true }

/// Builds, rebuilds with nothing changed, changes one source, rebuilds; then the two clean commands
/// twice each. Every rule says when it runs, so what the engine reruns is visible.
let scenario (form: string) (rules: string -> ExecContext Rule list) =
    let baseDir =
        match System.Environment.GetEnvironmentVariable "XAKE_EXP_DIR" with
        | null | "" -> Path.GetTempPath()
        | d -> d
    let root = Path.Combine(baseDir, "xake-08-" + form)
    if Directory.Exists root then Directory.Delete(root, true)
    write root "src/core/Path.fs" "core-a;"
    write root "src/core/sub/Engine.fs" "core-b;"
    write root "src/dotnet/Csc.fs" "dotnet-a;"
    let rs = rules root
    let step title targets reset =
        printfn "--- [%s] %s: %s" form title (String.concat " " targets)
        do xake (opts root targets reset) { rules rs }
    step "1 first build" [ "main" ] true
    step "2 nothing changed" [ "main" ] false
    write root "src/core/sub/Engine.fs" "core-b2;"
    step "3 src/core/sub/Engine.fs changed" [ "main" ] false
    step "4 nothing changed" [ "build" ] false
    printfn "    out/netstandard2.0/Xake.Dotnet.dll = %s" (File.ReadAllText(Path.Combine(root, "out/netstandard2.0/Xake.Dotnet.dll")))
    step "5 clean" [ "clean"; "clean-string" ] false
    step "6 clean again" [ "clean"; "clean-string" ] false
