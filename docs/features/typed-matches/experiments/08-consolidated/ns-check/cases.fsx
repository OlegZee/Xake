// Experiment 08: does the provided `Xake.Patterns.Target<...>` live beside the engine's DU
// `Xake.Target` (FileTarget | PhonyAction, in the AutoOpen module Xake.DomainTypes)?
// `sh run.sh` splits this file at `//#case` lines (header + case) into cases/<name>.fsx and runs each
// with the SDK 10 and the SDK 8 fsi; output.txt gets the errors or the output.
#r "../../engine/src/core/bin/Release/netstandard2.0/Xake.dll"
#r "../../patterns/bin/Release/netstandard2.0/Xake.Patterns.Exp08.dll"

//#case a-xake-then-patterns
open Xake
open Xake.Patterns
type Bin = Target<"out/(name:*).dll">
let describe (t: Target) = match t with FileTarget f -> "file " + f.Name | PhonyAction a -> "phony " + a
let r : Rule<ExecContext> = Bin.Rule <| fun m -> recipe { printfn "%s" m.name }
printfn "%s | %s | %A" (Bin.Format(name = "core")) (describe (PhonyAction "x")) [ PhonyAction "y" ]

//#case b-patterns-then-xake
open Xake.Patterns
open Xake
type Bin = Target<"out/(name:*).dll">
let describe (t: Target) = match t with FileTarget f -> "file " + f.Name | PhonyAction a -> "phony " + a
let r : Rule<ExecContext> = Bin.Rule <| fun m -> recipe { printfn "%s" m.name }
printfn "%s | %s | %A" (Bin.Format(name = "core")) (describe (PhonyAction "x")) [ PhonyAction "y" ]

//#case c-patterns-only-plain-du
open Xake.Patterns
type Bin = Target<"out/(name:*).dll">
let describe (t: Target) = match t with Xake.FileTarget f -> "file " + f.Name | Xake.PhonyAction a -> "phony " + a
printfn "%s | %s" (Bin.Format(name = "core")) (describe (Xake.PhonyAction "x"))

//#case c-patterns-only-qualified-du
open Xake.Patterns
type Bin = Target<"out/(name:*).dll">
let describe (t: Xake.Target) = match t with Xake.FileTarget f -> "file " + f.Name | Xake.PhonyAction a -> "phony " + a
printfn "%s | %s" (Bin.Format(name = "core")) (describe (Xake.PhonyAction "x"))

//#case d-xake-only-plain-provided
open Xake
type Bin = Target<"out/(name:*).dll">
printfn "%s" (Bin.Format(name = "core"))

//#case d-xake-only-qualified-provided
open Xake
type Bin = Patterns.Target<"out/(name:*).dll">
let describe (t: Target) = match t with FileTarget f -> "file " + f.Name | PhonyAction a -> "phony " + a
printfn "%s | %s" (Bin.Format(name = "core")) (describe (PhonyAction "x"))

//#case e-du-annotation-after-patterns
open Xake
open Xake.Patterns
// the DU used as a type in signatures that do not mention static arguments
let targets : Target list = [ PhonyAction "a"; PhonyAction "b" ]
let first (ts: Target list) : Target = List.head ts
type Holder = { T: Target }
printfn "%A %A" (first targets) { T = PhonyAction "c" }

//#case f-du-annotation-patterns-last-with-provided
open Xake
open Xake.Patterns
type Bin = Target<"out/(name:*).dll">
type Holder = { T: Target; Path: string }
let h = { T = PhonyAction "c"; Path = Bin.Format(name = "x") }
printfn "%A" h

// ---------------------------------------------------------------- workarounds

//#case w1-du-via-domaintypes
open Xake
open Xake.Patterns
type Bin = Target<"out/(name:*).dll">
let describe (t: DomainTypes.Target) = match t with FileTarget f -> "file " + f.Name | PhonyAction a -> "phony " + a
let ts : Xake.DomainTypes.Target list = [ PhonyAction "y" ]
printfn "%s | %s | %A" (Bin.Format(name = "core")) (describe (PhonyAction "x")) ts

//#case w2-provided-qualified-patterns-first
open Xake.Patterns
open Xake
type Bin = Patterns.Target<"out/(name:*).dll">
type Bin2 = Xake.Patterns.Target<"out/(name:*).xml">
let describe (t: Target) = match t with FileTarget f -> "file " + f.Name | PhonyAction a -> "phony " + a
printfn "%s %s | %s" (Bin.Format(name = "core")) (Bin2.Format(name = "core")) (describe (PhonyAction "x"))

//#case w3-du-abbreviation
open Xake
type BuildTarget = Target
open Xake.Patterns
type Bin = Target<"out/(name:*).dll">
let describe (t: BuildTarget) = match t with FileTarget f -> "file " + f.Name | PhonyAction a -> "phony " + a
printfn "%s | %s" (Bin.Format(name = "core")) (describe (PhonyAction "x"))

//#case w4-no-annotation-inferred
open Xake
open Xake.Patterns
type Bin = Target<"out/(name:*).dll">
let describe t = match t with FileTarget (f: File) -> "file " + f.Name | PhonyAction a -> "phony " + a
printfn "%s | %s" (Bin.Format(name = "core")) (describe (PhonyAction "x"))

//#case w5-xake-qualified-du
open Xake.Patterns
type Bin = Target<"out/(name:*).dll">
let describe (t: Xake.DomainTypes.Target) = match t with Xake.FileTarget f -> "file " + f.Name | Xake.PhonyAction a -> "phony " + a
printfn "%s | %s" (Bin.Format(name = "core")) (describe (Xake.PhonyAction "x"))
