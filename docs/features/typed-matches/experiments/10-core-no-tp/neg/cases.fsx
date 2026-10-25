// Experiment 10 negative cases: what each mistake gives, and WHEN (compile / script start / call /
// rule run).   sh ../run.sh cases.fsx
#r "../../engine/src/core/bin/Release/netstandard2.0/Xake.dll"
open Xake
open Xake.Typed
type AsmG = { cfg: string; fwk: string; name: string }
type LibG = { fwk: string; lib: string }
type LibOut = { dll: string; xml: string }
let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
let Lib = Target<LibG, LibOut>("dll=out/(fwk:*)/(lib:*).dll", "xml=out/(fwk:*)/(lib:*).xml")
/// Runs one rule for one target in a temp folder; the rule's failure is printed (ThrowOnError).
let runRule (one: ExecContext Rule) (target: string) =
    let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xake-10-neg")
    System.IO.Directory.CreateDirectory root |> ignore
    let opts = { ExecOptions.Default with ProjectRoot = root; Targets = [ target ]; ConLogLevel = Silent; FileLogLevel = Silent; ResetDb = true; ThrowOnError = true }
    try do xake opts { rules [ one ] } with e -> printfn "rule run: %s" (let rec inner (e: exn) = (match e.InnerException with null -> e.Message | i -> inner i) in inner e)
printfn "(header evaluated: Asm, Lib created)"
//#case c01-misspelt-group-form-d
let r = Asm.Rule <| fun m -> recipe { printfn "%s" m.fkw }
//#case c02-misspelt-group-form-b
let r = Asm.rule { let! m = matched in printfn "%s" m.fkw }
//#case c03-format-missing-field
printfn "%s" (Asm.Format { cfg = "Release"; fwk = "net8.0" })
//#case c04-format-misspelt-field
printfn "%s" (Asm.Format { cfg = "Release"; fkw = "net8.0"; name = "Core" })
//#case c05-destructure-misspelt-label
let r = Asm.rule { let! { fkw = f } = matched in printfn "%s" f }
//#case c06-misspelt-output-label
let r = Lib.Rule <| fun m -> recipe { printfn "%s" (Lib.Make m).pdb }
//#case c07-another-patterns-record
let r = Asm.Rule <| fun (m: LibG) -> recipe { printfn "%s" m.lib }
//#case c08-matched-outside-rule
let r = recipe { let! m = matched in printfn "%A" m }
//#case c09-tuple-destructure-arity
let T = Target<string * string * string> "out/(cfg:*)/(fwk:*)/(name:*).dll"
let r = T.Rule <| fun (cfg, fwk) -> recipe { printfn "%s" cfg }
//#case s01-record-field-misspelt
type BadG = { cfg: string; fkw: string; name: string }
let Bad = Target<BadG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
//#case s02-record-missing-field
type PartG = { fwk: string; name: string }
let Bad = Target<PartG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
//#case s03-multi-output-groups-differ
let Bad = Target<LibG>("out/(fwk:*)/(lib:*).dll", "out/(fwk:*)/(name:*).xml")
//#case s04-outputs-record-mismatch
type Outs = { dll: string; pdb: string }
let Bad = Target<LibG, Outs>("dll=out/(fwk:*)/(lib:*).dll", "xml=out/(fwk:*)/(lib:*).xml")
//#case s05-unclosed-group
let Bad = Target<AsmG> "out/(cfg:*)/(fwk:*/(name:*).dll"
//#case s06-non-string-field
type IntG = { cfg: string; fwk: string; name: int }
let Bad = Target<IntG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
//#case s07-tuple-arity
let Bad = Target<string * string> "out/(cfg:*)/(fwk:*)/(name:*).dll"
//#case s08-unit-with-groups
let Bad = Command<unit> "lock::(name:*)::(cfg:*)"
//#case s09-bare-wildcards-multi-output
type NG = { name: string; _1: string }
let Bad = Target<NG>("out/*/(name:*).dll", "out/*/(name:*).xml")
//#case s10-wildcard-not-in-record
type SrcG = { dir: string }
let Bad = Target<SrcG> "src/(dir:*)/**/*.fs"
//#case s11-duplicate-label
let Bad = Target<LibG>("a=out/(fwk:*)/(lib:*).dll", "a=out/(fwk:*)/(lib:*).xml")
//#case g01-format-slash-in-star
printfn "%s" (Asm.Format { cfg = "Release"; fwk = "net8.0/x"; name = "Core" })
//#case g02-format-constraint
type SrcG = { dir: string; file: string; ext: string }
let Src = Target<SrcG> "src/(dir:*)/(file:*).(ext:c*)"
printfn "%s" (Src.Format { dir = "src"; file = "a"; ext = "cs" })
printfn "%s" (Src.Format { dir = "src"; file = "a"; ext = "fs" })
//#case g03-format-null
printfn "%s" (Asm.Format { cfg = "Release"; fwk = null; name = "Core" })
//#case g04-untyped-unknown-name
let P = Pattern "out/(cfg:*)/(fwk:*)/(name:*).dll"
printfn "%s" (P.Format [ "cfg", "Release"; "fkw", "net8.0"; "name", "Core" ])
//#case g05-untyped-missing-name
let P = Pattern "out/(cfg:*)/(fwk:*)/(name:*).dll"
printfn "%s" (P.Format [ "cfg", "Release"; "name", "Core" ])
//#case r01-untyped-misspelt-group-in-rule
let P = Pattern "out/(cfg:*)/(fwk:*)/(name:*).dll"
runRule (P.Rule <| fun m -> recipe { printfn "fwk = '%s'" m.["fkw"] }) "out/Release/net8.0/Core.dll"
//#case r02-today-misspelt-group-in-rule
// today's API, for comparison: "" and the recipe goes on
runRule ("out/(cfg:*)/(fwk:*)/(name:*).dll" ..> recipe { let! f = getRuleMatch "fkw" in printfn "fwk = '%s'" f }) "out/Release/net8.0/Core.dll"
//#case r03-format-constraint-in-rule
type SrcG = { dir: string; file: string; ext: string }
let Src = Target<SrcG> "src/(dir:*)/(file:*).(ext:c*)"
runRule (Asm.Rule <| fun m -> recipe { do! need [ Src.Format { dir = m.cfg; file = m.name; ext = "fs" } ] }) "out/Release/net8.0/Core.dll"
//#case r04-shared-record-ok
// not an error: a record of the same shape serves several patterns (AsmG for out/ and run/)
let Run = Target<AsmG> "run/(cfg:*)/(fwk:*)/(name:*)"
runRule (Asm.Rule <| fun m -> recipe { printfn "%s -> %s" (Asm.Format m) (Run.Format m) }) "out/Release/net8.0/Core.dll"
