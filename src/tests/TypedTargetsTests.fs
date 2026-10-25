namespace Tests

open NUnit.Framework

open Xake
open Xake.Typed      // after `open Xake`

module TypedTargetsTypes =
    type AsmG = { cfg: string; fwk: string; name: string }
    type LibG = { fwk: string; lib: string }
    type LibOut = { dll: string; xml: string }
    type SrcG = { dir: string; _1: string; _2: string }
    type ExtG = { dir: string; file: string; ext: string }
    type LockG = { name: string; cfg: string }

/// Records that do not fit their patterns, kept apart so their labels do not shadow the good ones.
module TypedTargetsBad =
    type MisspeltG = { cfg: string; fkw: string; name: string }
    type PartG = { fwk: string; name: string }
    type IntG = { cfg: string; fwk: string; name: int }
    type WildG = { dir: string }
    type NamedWildG = { name: string; _1: string }
    type PdbOut = { dll: string; pdb: string }
    type IntOut = { dll: string; size: int }

/// A private type and a private representation: reflection still reads them.
module TypedTargetsPrivate =
    type private PrivG = { cfg: string; fwk: string; name: string }
    type HiddenG = private { cfg: string; fwk: string; name: string }
                   with static member Of(c, f, n) = { cfg = c; fwk = f; name = n }

    let privateFormat () =
        let t = Target<PrivG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
        t.Format { cfg = "Release"; fwk = "net8.0"; name = "Core" }

open TypedTargetsTypes

[<TestFixture>]
type ``Typed targets: declaration``() =

    /// The message of the PatternException `f` raises.
    let failsWith (f: unit -> obj) =
        try
            f () |> ignore
            Assert.Fail "expected a PatternException"; ""
        with PatternAst.PatternException message -> message

    [<Test>]
    member _.``a record field not in the pattern``() =
        let m = failsWith (fun () -> box (Target<TypedTargetsBad.MisspeltG> "out/(cfg:*)/(fwk:*)/(name:*).dll"))
        Assert.That(m, Is.EqualTo "Target<MisspeltG> \"out/(cfg:*)/(fwk:*)/(name:*).dll\": the record MisspeltG has fields [cfg; fkw; name], the pattern has [cfg; fwk; name] (not in the record: [fwk]; not in the pattern: [fkw])")

    [<Test>]
    member _.``a group missing from the record``() =
        let m = failsWith (fun () -> box (Target<TypedTargetsBad.PartG> "out/(cfg:*)/(fwk:*)/(name:*).dll"))
        Assert.That(m, Does.Contain "(not in the record: [cfg])")
        Assert.That(m, Does.Not.Contain "not in the pattern")

    [<Test>]
    member _.``an anonymous record is named as F# writes it``() =
        let m = failsWith (fun () -> box (Target<{| fwk: string; lib: string |}> "out/(fwk:*)/(name:*).dll"))
        Assert.That(m, Does.StartWith "Target<{| fwk; lib |}> \"out/(fwk:*)/(name:*).dll\": the record {| fwk; lib |} has fields [fwk; lib]")
        Assert.That(m, Does.Not.Contain "AnonymousType")

    [<Test>]
    member _.``outputs of a multi-file pattern have different groups``() =
        let m = failsWith (fun () -> box (Target<LibG> [ "out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(name:*).xml" ]))
        Assert.That(m, Does.StartWith "Target<LibG> \"out/(fwk:*)/(lib:*).dll\", \"out/(fwk:*)/(name:*).xml\": output \"out/(fwk:*)/(name:*).xml\" has groups [fwk; name], the first output [fwk; lib] (missing [lib], extra [name])")

    [<Test>]
    member _.``the outputs record has a field that is not string``() =
        let m = failsWith (fun () -> box (Target<LibG, TypedTargetsBad.IntOut> { dll = "out/(fwk:*)/(lib:*).dll"; size = 1 }))
        Assert.That(m, Is.EqualTo "Target<LibG, IntOut>: the field [size] of IntOut is not string")

    [<Test>]
    member _.``an output pattern left null``() =
        let m = failsWith (fun () -> box (Target<LibG, TypedTargetsBad.PdbOut> { dll = "out/(fwk:*)/(lib:*).dll"; pdb = null }))
        Assert.That(m, Is.EqualTo "Target<LibG, PdbOut>: no pattern for the output [pdb] (a null field)")

    [<Test>]
    member _.``outputs that disagree on their groups``() =
        let m = failsWith (fun () -> box (Target<LibG, TypedTargetsBad.PdbOut> { dll = "out/(fwk:*)/(lib:*).dll"; pdb = "out/(fwk:*)/(name:*).pdb" }))
        Assert.That(m, Does.StartWith "Target<LibG, PdbOut> \"out/(fwk:*)/(lib:*).dll\", \"out/(fwk:*)/(name:*).pdb\": output \"out/(fwk:*)/(name:*).pdb\" has groups [fwk; name]")

    [<Test>]
    member _.``an unclosed group``() =
        let m = failsWith (fun () -> box (Target<AsmG> "out/(cfg:*)/(fwk:*/(name:*).dll"))
        Assert.That(m, Is.EqualTo "Target<AsmG> \"out/(cfg:*)/(fwk:*/(name:*).dll\": the group '(fwk:' in \"out/(cfg:*)/(fwk:*/(name:*).dll\" is not closed")

    [<Test>]
    member _.``a field that is not string``() =
        let m = failsWith (fun () -> box (Target<TypedTargetsBad.IntG> "out/(cfg:*)/(fwk:*)/(name:*).dll"))
        Assert.That(m, Is.EqualTo "Target<IntG> \"out/(cfg:*)/(fwk:*)/(name:*).dll\": the field [name] of IntG is not string")

    [<Test>]
    member _.``a tuple is not a carrier``() =
        let m = failsWith (fun () -> box (Target<string * string> "out/(cfg:*)/(fwk:*)"))
        Assert.That(m, Is.EqualTo "Target<string * string> \"out/(cfg:*)/(fwk:*)\": string * string is a tuple; a tuple's elements are positional (a swap goes unnoticed): declare a record with the fields [cfg; fwk]")

    [<Test>]
    member _.``another type is not a carrier``() =
        let m = failsWith (fun () -> box (Target<int> "out/(cfg:*)"))
        Assert.That(m, Does.EndWith ": int is not a record, string or unit")

    [<Test>]
    member _.``unit with groups``() =
        let m = failsWith (fun () -> box (Command<unit> "lock::(name:*)::(cfg:*)"))
        Assert.That(m, Is.EqualTo "Command<unit> \"lock::(name:*)::(cfg:*)\": unit carries no groups, the pattern has [name; cfg]; declare a record with these fields")

    [<Test>]
    member _.``string with several groups``() =
        let m = failsWith (fun () -> box (Command<string> "lock::(name:*)::(cfg:*)"))
        Assert.That(m, Does.Contain "string carries one group, the pattern has [name; cfg]")

    [<Test>]
    member _.``bare wildcards in a multi-output pattern``() =
        let m = failsWith (fun () -> box (Target<TypedTargetsBad.NamedWildG> [ "out/*/(name:*).dll"; "out/*/(name:*).xml" ]))
        Assert.That(m, Does.EndWith ": bare wildcards (*, **, ?) cannot be paired across outputs; name them, e.g. (x:*)")

    [<Test>]
    member _.``bare wildcards missing from the record``() =
        let m = failsWith (fun () -> box (Target<TypedTargetsBad.WildG> "src/(dir:*)/**/*.fs"))
        Assert.That(m, Does.Contain "(not in the record: [_1; _2]); bare wildcards are fields _1, _2, ...")

    [<Test>]
    member _.``the exception prints as its message alone``() =
        try
            Target<TypedTargetsBad.MisspeltG> "out/(cfg:*)/(fwk:*)/(name:*).dll" |> ignore
            Assert.Fail "expected a PatternException"
        with e ->
            Assert.That(e.ToString(), Is.EqualTo e.Message)
            Assert.That(e.ToString(), Does.Not.Contain " at ")

[<TestFixture>]
type ``Typed targets: generation``() =

    let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
    let Lib = Target<LibG, LibOut> { dll = "out/(fwk:*)/(lib:*).dll"; xml = "out/(fwk:*)/(lib:*).xml" }
    let Src = Target<SrcG> "src/(dir:*)/**/*.fs"
    let Ext = Target<ExtG> "src/(dir:*)/(file:*).(ext:c*)"

    let failsWith (f: unit -> obj) =
        try
            f () |> ignore
            Assert.Fail "expected a PatternException"; ""
        with PatternAst.PatternException message -> message

    [<Test>]
    member _.``Format and Pattern``() =
        Assert.That(Asm.Format { cfg = "Release"; fwk = "net8.0"; name = "Core" }, Is.EqualTo "out/Release/net8.0/Core.dll")
        Assert.That(Asm.Pattern, Is.EqualTo "out/(cfg:*)/(fwk:*)/(name:*).dll")
        Assert.That(Lib.Patterns, Is.EqualTo [ "out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml" ])

    [<Test>]
    member _.``Paths, Make and For of a multi-output pattern``() =
        let g = { fwk = "net8.0"; lib = "Core" }
        Assert.That(Lib.Paths g, Is.EqualTo [ "out/net8.0/Core.dll"; "out/net8.0/Core.xml" ])
        Assert.That(Lib.Make g, Is.EqualTo { dll = "out/net8.0/Core.dll"; xml = "out/net8.0/Core.xml" })
        Assert.That(Lib.Format g, Is.EqualTo "out/net8.0/Core.dll")
        let all = Lib.For [ for f in [ "a"; "b" ] -> { fwk = f; lib = "L" } ]
        Assert.That(all, Is.EqualTo [ "out/a/L.dll"; "out/a/L.xml"; "out/b/L.dll"; "out/b/L.xml" ])

    [<Test>]
    member _.``outputs declared as an anonymous record``() =
        let L = Target<LibG, _>({| xml = "doc/(lib:*)/(fwk:*).xml"; dll = "out/(fwk:*)/(lib:*).dll" |})
        let out = L.Make { fwk = "net8.0"; lib = "Core" }
        Assert.That(out.dll, Is.EqualTo "out/net8.0/Core.dll")
        Assert.That(out.xml, Is.EqualTo "doc/Core/net8.0.xml")

    [<Test>]
    member _.``an anonymous record carrier: access, Format, copy-and-update``() =
        let A = Target<{| cfg: string; fwk: string |}> "o/(fwk:*)/(cfg:*)"
        let g = {| cfg = "Release"; fwk = "net8.0" |}
        Assert.That(A.Format g, Is.EqualTo "o/net8.0/Release")
        Assert.That(A.Format {| g with cfg = "Debug" |}, Is.EqualTo "o/net8.0/Debug")

    [<Test>]
    member _.``copy-and-update of a named record``() =
        let g : AsmG = { cfg = "Release"; fwk = "net8.0"; name = "Core" }
        Assert.That(Asm.Format { g with name = "Dotnet" }, Is.EqualTo "out/Release/net8.0/Dotnet.dll")

    [<Test>]
    member _.``one record serves two patterns``() =
        let Run = Target<AsmG> "run/(cfg:*)/(fwk:*)/(name:*)"
        let g : AsmG = { cfg = "Release"; fwk = "net8.0"; name = "Core" }
        Assert.That((Asm.Format g, Run.Format g), Is.EqualTo(("out/Release/net8.0/Core.dll", "run/Release/net8.0/Core")))

    [<Test>]
    member _.``string and unit carriers``() =
        let TB = Command<string> "test-build::(project:*)"
        let Clean = Command<unit> "clean"
        let One = Target<string> "out/(name:*).txt"
        let Fixed = Target<unit> "version.json"
        Assert.That(TB.Format "Xake", Is.EqualTo "test-build::Xake")
        Assert.That(TB.For [ "a"; "b" ], Is.EqualTo [ "test-build::a"; "test-build::b" ])
        Assert.That(Clean.Format (), Is.EqualTo "clean")
        Assert.That(One.Format "a", Is.EqualTo "out/a.txt")
        Assert.That(Fixed.Format (), Is.EqualTo "version.json")

    [<Test>]
    member _.``bare wildcards are fields _1, _2``() =
        Assert.That(Src.Format { dir = "core"; _1 = "a/b"; _2 = "File" }, Is.EqualTo "src/core/a/b/File.fs")

    [<Test>]
    member _.``Glob binds the given groups, the others stay wildcards``() =
        let (Fileset.Fileset (_, items)) = Src.Glob { Src.Any with dir = "core" }
        Assert.That(items, Is.EqualTo [ Fileset.Includes (Path.parse "src/core/**/*.fs") ])
        let (Fileset.Fileset (_, items)) = Lib.Glob { Lib.Any with lib = "Core" }
        Assert.That(items, Is.EqualTo [ Fileset.Includes (Path.parse "out/*/Core.dll"); Fileset.Includes (Path.parse "out/*/Core.xml") ])

    [<Test>]
    member _.``a pattern built from run-time data``() =
        let pkg = System.String.Concat("Xa", "ke")
        let Pkg = Target<{| ver: string |}> $"out/pkg/{pkg}.(ver:*).nupkg"
        Assert.That(Pkg.Format {| ver = "4.0.0" |}, Is.EqualTo "out/pkg/Xake.4.0.0.nupkg")

    [<Test>]
    member _.``an escaped reserved character is a literal``() =
        let T = Target<{| n: string |}> @"out/\(lit\)/(n:*).dll"
        Assert.That(T.Format {| n = "x" |}, Is.EqualTo "out/(lit)/x.dll")

    [<Test>]
    member _.``private record types``() =
        Assert.That(TypedTargetsPrivate.privateFormat (), Is.EqualTo "out/Release/net8.0/Core.dll")
        let H = Target<TypedTargetsPrivate.HiddenG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
        Assert.That(H.Format (TypedTargetsPrivate.HiddenG.Of("Release", "net8.0", "Core")), Is.EqualTo "out/Release/net8.0/Core.dll")

    [<Test>]
    member _.``a '/' in a '*' group fails``() =
        let m = failsWith (fun () -> box (Asm.Format { cfg = "Release"; fwk = "net8.0/x"; name = "Core" }))
        Assert.That(m, Is.EqualTo "Target<AsmG> \"out/(cfg:*)/(fwk:*)/(name:*).dll\": the values cfg = \"Release\", fwk = \"net8.0/x\", name = \"Core\" give \"out/Release/net8.0/x/Core.dll\", which the pattern \"out/(cfg:*)/(fwk:*)/(name:*).dll\" does not match back (a '/' in a '*' group, or a value outside a group's constraint?)")

    [<Test>]
    member _.``a value outside a group's constraint fails``() =
        Assert.That(Ext.Format { dir = "d"; file = "a"; ext = "cs" }, Is.EqualTo "src/d/a.cs")
        let m = failsWith (fun () -> box (Ext.Format { dir = "d"; file = "a"; ext = "fs" }))
        Assert.That(m, Does.Contain "give \"src/d/a.fs\", which the pattern \"src/(dir:*)/(file:*).(ext:c*)\" does not match back")

    [<Test>]
    member _.``a null field fails``() =
        let m = failsWith (fun () -> box (Asm.Format { cfg = "Release"; fwk = null; name = "Core" }))
        Assert.That(m, Is.EqualTo "Target<AsmG> \"out/(cfg:*)/(fwk:*)/(name:*).dll\": no value for [fwk] (a null field)")

    [<Test>]
    member _.``ToString names the declaration``() =
        Assert.That(string Lib, Is.EqualTo "Target<LibG, LibOut> \"out/(fwk:*)/(lib:*).dll\", \"out/(fwk:*)/(lib:*).xml\"")
        Assert.That(string (Command<LockG> "lock::(name:*)::(cfg:*)"), Is.EqualTo "Command<LockG> \"lock::(name:*)::(cfg:*)\"")
