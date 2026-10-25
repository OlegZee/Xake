/// Experiment 10, the consumer: the rules of 08's consumer (real rules of build.fsx / build.fsc.fsx)
/// rewritten on Core's record-declared Target<'G> / Target<'G,'O> / Command<'G> -- no type provider,
/// only Xake.dll (`open Xake` then `open Xake.Typed`). Form D (`X.Rule <| fun m -> recipe { }`), form B (`X.rule { let! m = matched }`)
/// and form R (destructuring a real F# record: `fun { fwk = fwk } ->`, `let! { fwk = fwk } = matched`).
/// Compilation is simulated (files are written) so the engine really builds and rebuilds.
module Rules

open System.IO
open Xake
open Xake.Typed      // after `open Xake`: see the NOTE at the top of TypedPatterns.fs

// ---------------------------------------------------------------- the patterns
// build.fsx:140, `targets ["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"]` -- labelled outputs
type LibG = { fwk: string; lib: string }
type LibOut = { dll: string; xml: string }
let Lib = Target<LibG, LibOut>("dll=out/(fwk:*)/(lib:*).dll", "xml=out/(fwk:*)/(lib:*).xml")
// build.fsc.fsx:63, `target "out/(cfg:*)/(fwk:*)/(name:*).dll"`
type AsmG = { cfg: string; fwk: string; name: string }
let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
// build.fsx:145, the sources of a library: only Glob; bare wildcards are fields _1, _2
type SrcG = { dir: string; _1: string; _2: string }
let Sources = Target<SrcG> "src/(dir:*)/**/*.fs"
// build.fsx:109,117 -- phony with groups; one group may be a plain string
let TestBuild = Command<string> "test-build::(project:*)"
type TestG = { project: string; slice: string }
let Test = Command<TestG> "test::(project:*)/(slice:*)"
// build.fsc.fsx:47 -- phony with groups, destructured below
type LockG = { name: string; cfg: string }
let Lock = Command<LockG> "lock::(name:*)::(cfg:*)"
// build.fsx:104 -- a command without groups
let Clean = Command<unit> "clean"

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

let common = [
    "main" <== [ "build"; "test"; "docs::Xake"; "locks" ]
    // the `binaries` helper of build.fsx, from the pattern: every output for every framework
    "build" <== Lib.For [ for lib in libraries do for f in frameworks -> { fwk = f; lib = lib.Name } ]
    "test" <== [ for project, slice in testJobs -> Test.Format { project = project; slice = slice } ]
    // build.fsc.fsx:56 `need [ for n in projects -> $"lock::{n}::Release" ]`
    "locks" <== Lock.For [ for l in libraries -> { name = l.Name; cfg = "Release" } ]
    "docs::(project:*)" => recipe {
        let! project = getRuleMatch "project"
        do! need [ (Lib.Make { fwk = "netstandard2.0"; lib = project }).xml ]
        say "docs %s (string API, =>)" project
    }
    // form R for a command: the lambda's parameter is a record pattern
    Lock.Rule <| fun { name = name; cfg = cfg } -> recipe {
        for r in (library name).Needs do
            do! need [ Lock.Format { name = r; cfg = cfg } ]
        say "lock %s %s" name cfg
    }
]

// ---------------------------------------------------------------- form D
let rulesD (root: string) = common @ [
    Lib.Rule <| fun m -> recipe {
        let lib = library m.lib
        let out = Lib.Make m
        let! sources = getFiles (Sources.Glob { Sources.Any with dir = lib.Dir })
        do! needFiles sources
        do! need [ for dep in lib.Needs -> (Lib.Make { m with lib = dep }).dll ]
        say "compile %s for %s -> %s + %s" m.lib m.fwk out.dll out.xml
        let (Fileset.Filelist files) = sources
        let text = files |> List.map (fun f -> File.ReadAllText f.FullName) |> String.concat ""
        write root out.dll (text + String.concat "," lib.Needs)
        write root out.xml $"<doc>{m.lib}</doc>"
    }
    Asm.Rule <| fun m -> recipe {
        let input = Lib.Format { fwk = m.fwk; lib = m.name }
        do! need [ input ]
        let self = Asm.Format m
        say "compileAs %s %s %s -> %s" m.name m.cfg m.fwk self
        write root self (File.ReadAllText(Path.Combine(root, input)))
    }
    TestBuild.Rule <| fun project -> recipe {
        do! need [ Asm.Format { cfg = "Release"; fwk = "netstandard2.0"; name = project } ]
        say "test-build %s" project
    }
    Test.Rule <| fun m -> recipe {
        do! need [ TestBuild.Format m.project ]
        say "test %s slice %s (%s)" m.project m.slice (Test.Format m)
    }
    Clean.Rule <| fun () -> recipe {
        say "clean"
        if Directory.Exists(Path.Combine(root, "out")) then Directory.Delete(Path.Combine(root, "out"), true)
    }
    "clean-string" => recipe { say "clean-string (string API, =>)" }
]

// ---------------------------------------------------------------- form B (+ R: record patterns in let!)
let rulesB (root: string) = common @ [
    Lib.rule {
        let! m = matched
        let lib = library m.lib
        let out = Lib.Make m
        let! sources = getFiles (Sources.Glob { Sources.Any with dir = lib.Dir })
        do! needFiles sources
        do! need [ for dep in lib.Needs -> (Lib.Make { m with lib = dep }).dll ]
        say "compile %s for %s -> %s + %s" m.lib m.fwk out.dll out.xml
        let (Fileset.Filelist files) = sources
        let text = files |> List.map (fun f -> File.ReadAllText f.FullName) |> String.concat ""
        write root out.dll (text + String.concat "," lib.Needs)
        write root out.xml $"<doc>{m.lib}</doc>"
    }
    Asm.rule {
        let! { name = name; fwk = fwk } as m = matched      // R: a partial record pattern
        let input = Lib.Format { fwk = fwk; lib = name }
        do! need [ input ]
        say "compileAs %s %s %s -> %s" name m.cfg fwk (Asm.Format m)
        write root (Asm.Format m) (File.ReadAllText(Path.Combine(root, input)))
    }
    TestBuild.rule {
        let! project = matched                               // Command<string>: the value itself
        do! need [ Asm.Format { cfg = "Release"; fwk = "netstandard2.0"; name = project } ]
        say "test-build %s" project
    }
    Test.rule {
        let! { project = project; slice = slice } = matched  // R: the whole record
        do! need [ TestBuild.Format project ]
        say "test %s slice %s (%s)" project slice (Test.Format { project = project; slice = slice })
    }
    Clean.rule {
        say "clean"
        if Directory.Exists(Path.Combine(root, "out")) then Directory.Delete(Path.Combine(root, "out"), true)
    }
    "clean-string" => recipe { say "clean-string (string API, =>)" }
]

// ---------------------------------------------------------------- the scenario (as in 08)
let private opts root targets reset =
    { ExecOptions.Default with
        ProjectRoot = root; Targets = targets; Threads = 1
        ConLogLevel = Silent; FileLogLevel = Silent; ResetDb = reset; ThrowOnError = true }

let scenario (form: string) (rules: string -> ExecContext Rule list) =
    let baseDir =
        match System.Environment.GetEnvironmentVariable "XAKE_EXP_DIR" with
        | null | "" -> Path.GetTempPath()
        | d -> d
    let root = Path.Combine(baseDir, "xake-10-" + form)
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
