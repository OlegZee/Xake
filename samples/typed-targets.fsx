// Typed targets: rule patterns whose groups are the fields of an F# record.
//
// Needs the first Xake release with `Xake.Typed` (after 3.11, not yet published); until then run
// `dotnet fsi build.fsx -- -- build` first, which fills out/netstandard2.0 (the #r below), or point
// the #r at src/core/bin/<cfg>/netstandard2.0/Xake.dll.
//
// USAGE:
//   dotnet fsi typed-targets.fsx                 -- builds "main"
//   dotnet fsi typed-targets.fsx -- -- clean
#r "../out/netstandard2.0/Xake.dll"

open Xake
open Xake.Typed      // after `open Xake`

// The groups of a pattern are the fields of a record: `m.fwk` and `Lib.Format { .. }` are checked
// by the compiler; the record and the pattern are compared once, when the script starts.
type LibG = { fwk: string; lib: string }
// A multi-file rule names its outputs with a second record, one pattern per field.
type LibOut = { dll: string; xml: string }
let Lib = Target<LibG, LibOut> { dll = "out/(fwk:*)/(lib:*).dll"; xml = "out/(fwk:*)/(lib:*).xml" }

// Bare wildcards are the fields _1, _2, ... by position.
type SrcG = { lib: string; _1: string; _2: string }
let Sources = Target<SrcG> "src/(lib:*)/**/*.txt"

// A command always runs (as `command`); `string` carries a single group, `unit` none.
type PackG = { lib: string; ver: string }
let Pack = Command<PackG> "pack::(lib:*)::(ver:*)"
let Report = Command<string> "report::(lib:*)"
let Clean = Command<unit> "clean"

let libraries = [ "Core"; "Tools" ]
let frameworks = [ "net8.0"; "netstandard2.0" ]

let write (path: string) (text: string) =
    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath path)) |> ignore
    System.IO.File.WriteAllText(path, text)

do xake { ExecOptions.Default with FileLog = "" } {
    rules [
        "main" <== Lib.For [ for l in libraries do for f in frameworks -> { fwk = f; lib = l } ]
                   @ [ Pack.Format { lib = "Core"; ver = "1.0.0" }; Report.Format "Tools" ]

        // form D: the rule is a function of the typed match
        Lib.Rule <| fun m -> recipe {
            let out = Lib.Make m
            let! sources = getFiles (Sources.Glob { Sources.Any with lib = m.lib })
            do! needFiles sources
            do! trace Message "compile %s for %s -> %s, %s" m.lib m.fwk out.dll out.xml
            write out.dll $"{m.lib} for {m.fwk}"
            write out.xml $"<doc>{m.lib}</doc>"
        }

        // a record pattern as the parameter
        Pack.Rule <| fun { lib = lib; ver = ver } -> recipe {
            do! need [ for f in frameworks -> (Lib.Make { fwk = f; lib = lib }).dll ]
            do! trace Message "pack %s %s" lib ver
        }

        // form B: the rule is a builder, `matched` gives the typed match
        Report.rule {
            let! lib = matched
            do! need [ Lib.Format { fwk = "net8.0"; lib = lib } ]
            do! trace Message "report %s" lib
        }

        Clean.Rule <| fun () -> recipe {
            if System.IO.Directory.Exists "out" then System.IO.Directory.Delete("out", true)
        }
    ]
}
