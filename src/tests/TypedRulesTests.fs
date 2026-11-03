namespace Tests

open System.IO
open NUnit.Framework

open Xake
open Xake.Typed      // after `open Xake`

module TypedRulesTypes =
    type LibG = { fwk: string; lib: string }
    type LibOut = { dll: string; xml: string }
    type AsmG = { cfg: string; fwk: string; name: string }
    type SrcG = { dir: string; _1: string; _2: string }
    type TestG = { project: string; slice: string }
    type LockG = { name: string; cfg: string }

    type Library = { Name: string; Dir: string; Needs: string list }
    let libraries =
        [ { Name = "Core"; Dir = "core"; Needs = [] }
          { Name = "Dotnet"; Dir = "dotnet"; Needs = [ "Core" ] } ]
    let library name = libraries |> List.find (fun l -> l.Name = name)
    let frameworks = [ "netstandard2.0"; "net462" ]

    let Lib = Target<LibG, LibOut> { dll = "out/(fwk:*)/(lib:*).dll"; xml = "out/(fwk:*)/(lib:*).xml" }
    let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
    let Sources = Target<SrcG> "src/(dir:*)/**/*.fs"
    let TestBuild = Command<string> "test-build::(project:*)"
    let Test = Command<TestG> "test::(project:*)/(slice:*)"
    let Lock = Command<LockG> "lock::(name:*)::(cfg:*)"
    let Clean = Command<unit> "clean"

open TypedRulesTypes

/// The rules of the build scripts (a multi-output library rule, an assembly rule, commands with
/// and without groups) on typed targets, in form D (`X.Rule <| fun m -> ..`) and form B
/// (`X.rule { let! m = matched .. }`); compiling is simulated by writing files.
[<TestFixture>]
type ``Typed targets: rules``() =
    inherit XakeTestBase("typed")

    let log = ResizeArray<string>()
    let say fmt = Printf.kprintf log.Add fmt
    let write (path: string) (text: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
        File.WriteAllText(path, text)

    let common = [
        "main" <== [ "build"; "test"; "locks" ]
        "build" <== Lib.For [ for l in libraries do for f in frameworks -> { fwk = f; lib = l.Name } ]
        "test" <== Test.For [ for p, s in [ "Core", "1"; "Core", "2"; "Dotnet", "1" ] -> { project = p; slice = s } ]
        "locks" <== Lock.For [ for l in libraries -> { name = l.Name; cfg = "Release" } ]
        // record destructuring in the lambda's parameter
        Lock.Rule <| fun { name = name; cfg = cfg } -> recipe {
            for r in (library name).Needs do
                do! need [ Lock.Format { name = r; cfg = cfg } ]
            say "lock %s %s" name cfg
        }
    ]

    let compile (m: LibG) = recipe {
        let lib = library m.lib
        let out = Lib.Make m
        let! sources = getFiles (Sources.Glob { Sources.Any with dir = lib.Dir })
        do! needFiles sources
        do! need [ for dep in lib.Needs -> (Lib.Make { m with lib = dep }).dll ]
        say "compile %s %s" m.lib m.fwk
        let (Fileset.Filelist files) = sources
        let text = files |> List.map (fun f -> File.ReadAllText f.FullName) |> List.sort |> String.concat ""
        write out.dll text
        write out.xml $"<doc>{m.lib}</doc>"
    }

    let rulesD = common @ [
        Lib.Rule <| fun m -> compile m
        Asm.Rule <| fun m -> recipe {
            let input = Lib.Format { fwk = m.fwk; lib = m.name }
            do! need [ input ]
            say "asm %s %s %s" m.cfg m.fwk m.name
            write (Asm.Format m) (File.ReadAllText input)
        }
        TestBuild.Rule <| fun project -> recipe {
            do! need [ Asm.Format { cfg = "Release"; fwk = "netstandard2.0"; name = project } ]
            say "test-build %s" project
        }
        Test.Rule <| fun m -> recipe {
            do! need [ TestBuild.Format m.project ]
            say "test %s %s" m.project m.slice
        }
        Clean.Rule <| fun () -> recipe { say "clean" }
    ]

    let rulesB = common @ [
        Lib.rule {
            let! m = matched
            do! compile m
        }
        Asm.rule {
            let! { name = name; fwk = fwk } as m = matched      // a partial record pattern
            let input = Lib.Format { fwk = fwk; lib = name }
            do! need [ input ]
            say "asm %s %s %s" m.cfg fwk name
            write (Asm.Format m) (File.ReadAllText input)
        }
        TestBuild.rule {
            let! project = matched
            do! need [ Asm.Format { cfg = "Release"; fwk = "netstandard2.0"; name = project } ]
            say "test-build %s" project
        }
        Test.rule {
            let! { project = project; slice = slice } = matched
            do! need [ TestBuild.Format project ]
            say "test %s %s" project slice
        }
        Clean.rule { say "clean" }
    ]

    member x.Run (rs: ExecContext Rule list) (targets: string list) =
        log.Clear()
        do xake { x.TestOptions with Targets = targets; ThrowOnError = true } { rules rs }
        List.ofSeq log |> List.sort

    member x.Scenario (rules: ExecContext Rule list) =
        if Directory.Exists "out" then Directory.Delete("out", true)
        if Directory.Exists "src" then Directory.Delete("src", true)
        write "src/core/Path.fs" "core-a;"
        write "src/core/sub/Engine.fs" "core-b;"
        write "src/dotnet/Csc.fs" "dotnet-a;"

        let first = x.Run rules [ "main" ]
        Assert.That(first, Is.EqualTo (List.sort [
            "compile Core netstandard2.0"; "compile Core net462"; "compile Dotnet netstandard2.0"; "compile Dotnet net462"
            "asm Release netstandard2.0 Core"; "asm Release netstandard2.0 Dotnet"
            "test-build Core"; "test-build Dotnet"; "test Core 1"; "test Core 2"; "test Dotnet 1"
            "lock Core Release"; "lock Dotnet Release" ]))
        Assert.That(File.ReadAllText "out/net462/Dotnet.dll", Is.EqualTo "dotnet-a;")
        Assert.That(File.ReadAllText "out/net462/Dotnet.xml", Is.EqualTo "<doc>Dotnet</doc>")
        Assert.That(File.ReadAllText "out/Release/netstandard2.0/Core.dll", Is.EqualTo "core-a;core-b;")

        // nothing changed: targets skip, commands run again
        let second = x.Run rules [ "main" ]
        Assert.That(second, Is.EqualTo (List.sort [
            "test-build Core"; "test-build Dotnet"; "test Core 1"; "test Core 2"; "test Dotnet 1"
            "lock Core Release"; "lock Dotnet Release" ]))

        // a source of Core changed: Core rebuilds, and Dotnet, which needs Core.dll (the pause
        // keeps the new write times apart from the old ones: the engine tolerates 10 ms)
        System.Threading.Thread.Sleep 50
        write "src/core/sub/Engine.fs" "core-b2;"
        let third = x.Run rules [ "build" ]
        Assert.That(third, Is.EqualTo (List.sort [
            "compile Core netstandard2.0"; "compile Core net462"; "compile Dotnet netstandard2.0"; "compile Dotnet net462" ]))
        Assert.That(File.ReadAllText "out/net462/Core.dll", Is.EqualTo "core-a;core-b2;")

        Assert.That(x.Run rules [ "build" ], Is.Empty)
        Assert.That(x.Run rules [ "clean" ], Is.EqualTo [ "clean" ])
        Assert.That(x.Run rules [ "clean" ], Is.EqualTo [ "clean" ])

    [<Test>]
    member x.``form D: rules, incremental runs, commands always run``() = x.Scenario rulesD

    [<Test>]
    member x.``form B: rules, incremental runs, commands always run``() = x.Scenario rulesB

    [<Test>]
    member x.``a multi-output rule builds both outputs once``() =
        let mutable runs = 0
        let rules = [
            "both" <== [ "out/x/One.xml"; "out/x/One.dll" ]
            Lib.Rule <| fun m -> recipe {
                runs <- runs + 1
                let out = Lib.Make m
                write out.dll "dll"
                write out.xml "xml"
            } ]
        x.Run rules [ "both" ] |> ignore
        Assert.That(runs, Is.EqualTo 1)
        Assert.That(File.Exists "out/x/One.dll" && File.Exists "out/x/One.xml")

    [<Test>]
    member x.``bare wildcards read as _1, _2``() =
        let mutable got = None
        let T = Target<SrcG> "gen/(dir:*)/**/*.txt"
        let rules = [ T.Rule <| fun m -> recipe { got <- Some m; write (T.Format m) "" } ]
        x.Run rules [ "gen/a/b/c/file.txt" ] |> ignore
        Assert.That(got, Is.EqualTo (Some { dir = "a"; _1 = "b/c"; _2 = "file" }))
