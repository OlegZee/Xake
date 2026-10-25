// Side probe on 08 alone (no Core typed patterns): decision 23's inline form
// `Command<"clean">.Rule <| fun () -> ...` in EXPRESSION position, with `open Xake` in scope
// (Logging's union case Level.Command).   dotnet fsi inline-command-08.fsx
#r "../../08-consolidated/patterns/bin/Release/netstandard2.0/Xake.dll"
#r "../../08-consolidated/patterns/bin/Release/netstandard2.0/Xake.Patterns.Exp08.dll"
open Xake
open Xake.Patterns
let viaType = (Xake.Patterns.Command<"clean">.Rule <| fun () -> recipe { () })
printfn "qualified: ok"
let inline' = Command<"clean">.Rule <| fun () -> recipe { () }
printfn "inline: ok"
