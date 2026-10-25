// Experiment 10 probes: name resolution and language facts for Core's record-declared patterns.
//   sh ../run.sh cases.fsx    (each `//#case` becomes its own script under cases/, run on SDK 10 and 8)
#r "../../engine/src/core/bin/Release/netstandard2.0/Xake.dll"
type AsmG = { cfg: string; fwk: string; name: string }
//#case p01-command-typed-opened-last
open Xake
open Xake.Typed
// `open Xake.Typed` after `open Xake`: the type Command<'G> shadows Logging's union case Level.Command.
let TestBuild = Command<string> "test-build::(project:*)"
let Clean = Command<unit> "clean"
printfn "%s %s" (TestBuild.Format "Xake") (Clean.Format ())
//#case p02-command-typed-opened-first
open Xake.Typed
open Xake
// The other order: in an expression the union case Level.Command wins.
let TestBuild = Command<string> "test-build::(project:*)"
printfn "%s" (TestBuild.Format "Xake")
//#case p03-command-typed-opened-first-new
open Xake.Typed
open Xake
// ...`new` still reaches the type.
let TestBuild = new Command<string>("test-build::(project:*)")
printfn "%s" (TestBuild.Format "Xake")
//#case p05-target-and-du-coexist
open Xake
open Xake.Typed
// Target<'G> (generic) next to the engine's DU Target (non-generic): both usable after `open Xake`.
let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
let t : Target = FileTarget (File.make "a.txt")
let ts : Target list = [ PhonyAction "x" ]
printfn "%s | %b | %d" (Asm.Format { cfg = "Release"; fwk = "net8.0"; name = "Core" }) (match t with FileTarget _ -> true | _ -> false) ts.Length
//#case p06-shared-labels-type-directed
open Xake
open Xake.Typed
// Two records with the same labels: the later one is F#'s default for `{ cfg = ..; fwk = ..; name = .. }`.
// Does the known parameter type of Format pick AsmG anyway?
let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
type RunG = { cfg: string; fwk: string; name: string }
let Run = Target<RunG> "run/(cfg:*)/(fwk:*)/(name:*)"
printfn "%s" (Asm.Format { cfg = "Release"; fwk = "net8.0"; name = "Core" })
printfn "%s" (Run.Format { cfg = "Release"; fwk = "net8.0"; name = "Core" })
//#case p07-shared-labels-let-binding
open Xake
open Xake.Typed
// ...but a record built away from the call (a `let`) takes the latest type: RunG.
let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
type RunG = { cfg: string; fwk: string; name: string }
let g = { cfg = "Release"; fwk = "net8.0"; name = "Core" }
printfn "%s" (Asm.Format g)
//#case p08-shared-record-two-patterns
open Xake
open Xake.Typed
// One record serves several patterns with the same groups (the outOf/runOf pair).
let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
let Run = Target<AsmG> "run/(cfg:*)/(fwk:*)/(name:*)"
let g = { cfg = "Release"; fwk = "net8.0"; name = "Core" }
printfn "%s %s" (Asm.Format g) (Run.Format g)
//#case p09-destructure-lambda-partial
open Xake
open Xake.Typed
// A partial record pattern as the lambda parameter, and copy-and-update for "same, another name".
let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
let r = Asm.Rule <| fun ({ fwk = fwk } as m) -> recipe { printfn "%s %s" fwk (Asm.Format { m with name = "Other" }) }
printfn "%A" (match r with FileRule (p, _) -> p | _ -> "?")
//#case p10-tuple-variant
open Xake
open Xake.Typed
// Tuple of strings: positional (pattern order), arity checked at script start; swaps are silent.
let Asm = Target<string * string * string> "out/(cfg:*)/(fwk:*)/(name:*).dll"
printfn "%s" (Asm.Format ("Release", "net8.0", "Core"))
printfn "%s (swapped, silently)" (Asm.Format ("net8.0", "Release", "Core"))
//#case p11-untyped-pattern
open Xake
open Xake.Typed
// The untyped Pattern: checked names at the call, glob, multi-output with labels.
let p = Pattern [ "dll=out/(fwk:*)/(lib:*).dll"; "xml=out/(fwk:*)/(lib:*).xml" ]
printfn "%A %A %A" p.Groups p.Labels (p.FormatAll [ "fwk", "net8.0"; "lib", "Xake" ])
printfn "%A" (p.Glob [ "lib", "Xake" ])
//#case p12-glob-any
open Xake
open Xake.Typed
type SrcG = { dir: string; _1: string; _2: string }
let Sources = Target<SrcG> "src/(dir:*)/**/*.fs"
printfn "%A" (Sources.Glob { Sources.Any with dir = "core" })
printfn "%A" (Sources.Glob Sources.Any)
//#case p13-format-literal-and-wildcards
open Xake
open Xake.Typed
type W = { _1: string }
let Lit = Target<unit> "out/readme.txt"
let Wild = Target<W> "out/*.txt"
printfn "%s %s" (Lit.Format ()) (Wild.Format { _1 = "a" })
//#case p14-perf
open Xake
open Xake.Typed
// Cost of the reflection: create, Format (with the round-trip re-match), Read of a rule match.
let sw = System.Diagnostics.Stopwatch.StartNew()
for i in 1 .. 1000 do Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll" |> ignore
printfn "create x1000: %d ms" sw.ElapsedMilliseconds
let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
sw.Restart()
for i in 1 .. 100000 do Asm.Format { cfg = "Release"; fwk = "net8.0"; name = string i } |> ignore
printfn "Format x100000: %d ms" sw.ElapsedMilliseconds
let p = Pattern "out/(cfg:*)/(fwk:*)/(name:*).dll"
let m = Map.ofList [ "cfg", "Release"; "fwk", "net8.0"; "name", "Core"; "0", "x"; "1", "a"; "2", "b"; "3", "c" ]
sw.Restart()
for i in 1 .. 100000 do Asm.Spec.Match(m).["fwk"] |> ignore
printfn "untyped read x100000: %d ms" sw.ElapsedMilliseconds
let read = Asm.Rule (fun g -> recipe { return () })   // the typed read runs inside the recipe; measured via Format above
sw.Restart()
for i in 1 .. 100000 do sprintf "out/%s/%s/%s.dll" "Release" "net8.0" (string i) |> ignore
printfn "sprintf baseline x100000: %d ms" sw.ElapsedMilliseconds
//#case p16-private-record-type
// A record type with private accessibility (public representation): reflection still sees it.
open Xake
open Xake.Typed
type private PG = { cfg: string; fwk: string; name: string }
let private Asm = Target<PG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
printfn "%s" (Asm.Format { cfg = "Release"; fwk = "net8.0"; name = "Core" })
//#case p17-private-representation
// A record with a PRIVATE representation: FSharpType.IsRecord is false without the private flag.
open Xake
open Xake.Typed
type HG = private { cfg: string; fwk: string; name: string } with static member Of(c, f, n) = { cfg = c; fwk = f; name = n }
let Asm = Target<HG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
printfn "%s" (Asm.Format (HG.Of("Release", "net8.0", "Core")))
//#case p19-pattern-from-runtime-data
// build.fsx:180 builds its pattern with interpolation; a TP needs a [<Literal>], Core does not.
open Xake
open Xake.Typed
type VerG = { ver: string }
let xakePackage = System.Environment.GetEnvironmentVariable "HOME" |> fun _ -> "Xake"
let Pkg = Target<VerG> $"out/pkg/%s{xakePackage}/%s{xakePackage}.(ver:*).nupkg"
printfn "%s" (Pkg.Format { ver = "1.2.3" })
//#case p20-pattern-as-a-library-parameter
// A real type: a library function can take a typed pattern (the hermetic outOf/runOf convention).
open Xake
open Xake.Typed
type AsmG2 = { cfg: string; fwk: string; name: string }
let compileAs (out: Target<AsmG2>) cfg fwk name = sprintf "compile -> %s" (out.Format { cfg = cfg; fwk = fwk; name = name })
let Asm = Target<AsmG2> "out/(cfg:*)/(fwk:*)/(name:*).dll"
let Flat = Target<AsmG2> "flat/(cfg:*)-(fwk:*)-(name:*).dll"
printfn "%s | %s" (compileAs Asm "Release" "net8.0" "Core") (compileAs Flat "Release" "net8.0" "Core")
//#case p18-form-b-full-destructure-and-for
// Form B with a full record pattern, and need over a generated list, in one rule.
open Xake
open Xake.Typed
type LibG = { fwk: string; lib: string }
let Lib = Target<LibG> "out/(fwk:*)/(lib:*).dll"
let r = Lib.rule {
    let! { fwk = fwk; lib = lib } = matched
    do! need (Lib.For [ for dep in [ "A"; "B" ] -> { fwk = fwk; lib = dep } ])
    printfn "%s %s" fwk lib
}
printfn "%A" (Lib.For [ for f in [ "net462"; "net8.0" ] do for l in [ "A"; "B" ] -> { fwk = f; lib = l } ])
