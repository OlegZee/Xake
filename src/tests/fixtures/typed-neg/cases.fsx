// Compile-time mistakes with typed targets (Xake.Typed), each with the error it must give.
// `TypedCompileTests.fs` type-checks this file with `dotnet fsi --typecheck-only`: everything
// before the first `//#case` is the shared header (its `#r` is replaced by the Xake.dll under
// test), and every `//#case <name> <code>` becomes a script of its own (the header, then the
// case) whose errors must all be <code> (no code: it must compile). By hand, after `dotnet build src/core`,
//   dotnet fsi --typecheck-only src/tests/fixtures/typed-neg/cases.fsx   stops at the first case (FS0039)
#r "../../../core/bin/Debug/netstandard2.0/Xake.dll"
open Xake
open Xake.Typed
type AsmG = { cfg: string; fwk: string; name: string }
type LibG = { fwk: string; lib: string }
type LibOut = { dll: string; xml: string }
let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
let Lib = Target<LibG, LibOut> { dll = "out/(fwk:*)/(lib:*).dll"; xml = "out/(fwk:*)/(lib:*).xml" }
let Clean = Command<unit> "clean"
//#case ok-the-header-and-correct-uses
let r1 = Asm.Rule <| fun m -> recipe { printfn "%s" m.fwk }
let r2 = Asm.rule { let! { name = n } as m = matched in printfn "%s %s" n m.cfg }
let r3 = Lib.Rule <| fun m -> recipe { printfn "%s" (Lib.Make m).xml }
let r4 = Clean.Rule <| fun () -> recipe { () }
let r5 = Clean.rule { printfn "clean" }
//#case misspelt-group-form-d FS0039
let r = Asm.Rule <| fun m -> recipe { printfn "%s" m.fkw }
//#case misspelt-group-form-b FS0039
let r = Asm.rule { let! m = matched in printfn "%s" m.fkw }
//#case format-missing-field FS0764
let p = Asm.Format { cfg = "Release"; fwk = "net8.0" }
//#case format-misspelt-field FS1129
let p = Asm.Format { cfg = "Release"; fkw = "net8.0"; name = "Core" }
//#case destructure-misspelt-label FS1129
let r = Asm.rule { let! { fkw = f } = matched in printfn "%s" f }
//#case misspelt-output-label FS0039
let r = Lib.Rule <| fun m -> recipe { printfn "%s" (Lib.Make m).pdb }
//#case outputs-record-missing-field FS0764
let L = Target<LibG, LibOut> { dll = "out/(fwk:*)/(lib:*).dll" }
//#case another-patterns-record FS0001
let r = Asm.Rule <| fun (m: LibG) -> recipe { printfn "%s" m.lib }
//#case matched-outside-a-rule FS0041
let r = recipe { let! m = matched in printfn "%A" m }
//#case unit-command-has-no-groups FS0039
let r = Clean.Rule <| fun m -> recipe { printfn "%s" m.name }
//#case typed-opened-before-xake FS0717
open Xake.Typed
open Xake
let C = Command<string> "test::(project:*)"
