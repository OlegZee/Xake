// Side probe on 08 alone: the inline `Command<"clean">.Rule` with the opens REVERSED
// (`open Xake.Patterns` first, then `open Xake`).   dotnet fsi inline-command-08-reversed.fsx
#r "../../08-consolidated/patterns/bin/Release/netstandard2.0/Xake.dll"
#r "../../08-consolidated/patterns/bin/Release/netstandard2.0/Xake.Patterns.Exp08.dll"
open Xake.Patterns
open Xake
let inline' = Command<"clean">.Rule <| fun () -> recipe { () }
printfn "inline: ok"
