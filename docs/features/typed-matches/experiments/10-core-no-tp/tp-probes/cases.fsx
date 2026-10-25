// Experiment 10: Core's Target<'G>/Command<'G> next to 08's provider Target<"...">/Command<"...">
// (Xake.Patterns.Exp08.dll, design-time dll found under 08's patterns/bin/typeproviders): what a
// script that opens both gets. Needs 08 built (08 README, "Opening in an IDE").
//   sh ../run.sh cases.fsx
#r "../.stage/bin/Release/netstandard2.0/Xake.dll"
#r "../.stage/bin/Release/netstandard2.0/Xake.Patterns.Exp08.dll"
type AsmG = { cfg: string; fwk: string; name: string }
//#case t01-open-typed-then-patterns
open Xake
open Xake.Typed
open Xake.Patterns
type AsmP = Target<"out/(cfg:*)/(fwk:*)/(name:*).dll">
let AsmC = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
printfn "%s | %s" (AsmP.Format(cfg = "Release", fwk = "net8.0", name = "Core")) (AsmC.Format { cfg = "Release"; fwk = "net8.0"; name = "Core" })
//#case t02-open-patterns-then-typed
open Xake
open Xake.Patterns
open Xake.Typed
type AsmP = Target<"out/(cfg:*)/(fwk:*)/(name:*).dll">
let AsmC = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
printfn "%s | %s" (AsmP.Format(cfg = "Release", fwk = "net8.0", name = "Core")) (AsmC.Format { cfg = "Release"; fwk = "net8.0"; name = "Core" })
//#case t03-matched-both
// Both define `matched`; the one opened last wins. Core's form B with the provider's marker in scope:
open Xake
open Xake.Typed
open Xake.Patterns
let AsmC = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
let r = AsmC.rule { let! m = matched in printfn "%s" m.fwk }
printfn "ok"
//#case t04-matched-both-provider-rule
open Xake
open Xake.Typed
open Xake.Patterns
type AsmP = Xake.Patterns.Target<"out/(cfg:*)/(fwk:*)/(name:*).dll">
let r = AsmP.rule { let! m = matched in printfn "%s" m.fwk }
printfn "ok"
//#case t05-command-both
open Xake
open Xake.Typed
open Xake.Patterns
type LockP = Xake.Patterns.Command<"lock::(name:*)::(cfg:*)">
let LockC = Xake.Typed.Command<string> "test-build::(project:*)"
printfn "%s | %s" (LockP.Format(name = "Core", cfg = "Release")) (LockC.Format "Core")
//#case t06-provider-qualified
open Xake
open Xake.Typed
open Xake.Patterns
type AsmP = Xake.Patterns.Target<"out/(cfg:*)/(fwk:*)/(name:*).dll">
let AsmC = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
printfn "%s | %s" (AsmP.Format(cfg = "Release", fwk = "net8.0", name = "Core")) (AsmC.Format { cfg = "Release"; fwk = "net8.0"; name = "Core" })
//#case t07-provider-only
open Xake.Patterns
type AsmP = Target<"out/(cfg:*)/(fwk:*)/(name:*).dll">
printfn "%s" (AsmP.Format(cfg = "Release", fwk = "net8.0", name = "Core"))
//#case t08-core-matched-qualified
open Xake
open Xake.Typed
open Xake.Patterns
let AsmC = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
let r = AsmC.rule { let! m = Xake.Typed.TypedMatch.matched in printfn "%s" m.fwk }
printfn "ok"
//#case t09-core-command-unqualified-patterns-last
open Xake
open Xake.Typed
open Xake.Patterns
let TestBuild = Command<string> "test-build::(project:*)"
printfn "%s" (TestBuild.Format "Core")
//#case t10-provider-command-typed-last
open Xake
open Xake.Patterns
open Xake.Typed
type LockP = Command<"lock::(name:*)::(cfg:*)">
printfn "%s" (LockP.Format(name = "Core", cfg = "Release"))
