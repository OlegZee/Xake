# Typed patterns in Core, without a type provider

Decision 32 moves the type provider and the new declaration forms into a separate opt-in package and
asks what Core (`Xake`) can get **with plain F#**: useful on its own, and the foundation the provider
package builds on. This note answers that with a prototype on the engine of experiment 08:
[`experiments/10-core-no-tp`](experiments/README.md) (section 10; every message quoted here is in its
`output.txt` files, SDK 10 and SDK 8 fsi give the same results). Research option 2 ("record-declared
groups", `research.md` §4) is the core of it. Material for discussion; nothing decided here.

## 1. Bottom line

- **Record-declared patterns work in Core and give most of the provider's value**: typed group access
  (`m.fwk`), typed and complete generation (`Asm.Format { cfg = ..; fwk = ..; name = .. }` — a
  misspelt or missing field is a compile error), typed outputs by label, and — what the provider can
  never do (destructure.md) — **real F# record patterns**: `fun { name = name; cfg = cfg } -> ...`,
  `let! { fwk = fwk; lib = lib } = matched`, `{ m with lib = dep }`.
- The price: **group names are written twice** (record + pattern string). The record and the pattern
  are compared **when the value is created — at script start** (`let Asm = Target<AsmG> "..."` at top
  level), with an exact message, before any target runs.
- The real rules of 08 (build.fsx / build.fsc.fsx: multi-output with labels, single output, phony
  with groups, no-group command, a `need` over a generated list) run on it in forms D and B with
  **the same build lines as the 08 provider prototype** (sorted, incremental scenario included), in
  fsi on SDK 10 and SDK 8 and compiled (net8.0 Release). One new file in `Xake.dll`, ~270 lines,
  no dependency, both TFMs, 0 warnings.
- Two naming facts constrain the design: the types must live in **their own namespace opened after
  `open Xake`** (Logging's union case `Level.Command` beats a type `Command<_>` otherwise), and a
  generic `Target<'G>` **hides the provider's `Target<"...">`** whenever both namespaces are open.

## 2. What fits in Core, layer by layer

| layer | what | status |
|---|---|---|
| 0. engine (already planned, 08's `engine.patch`) | public `PatternAst` (parse, `format`, `glob`, `formatMatches`), escaping, `applyWildcards` replaced | unchanged here; everything below is built on it |
| 1. `Pattern` — a checked pattern value | parsed once; checks syntax (an unclosed `(name:`), one group set across outputs, no bare wildcards in multi-output, unique labels (`"label=pattern"`, else `Out1`, `Out2`); `Format`/`FormatAll`/`Glob` take `(name, value)` pairs, an unknown or missing name raises `XakeException` (not `""`); `Rule <| fun m -> .. m.["fwk"]` with a name-checked indexer; `Groups`, `Slots`, `Labels`, `Texts` | prototyped (`TypedPatterns.fs`) |
| 2. `Target<'G>`, `Target<'G,'O>`, `Command<'G>` — record-declared | 'G carries the groups: an F# record (by field name), a tuple of strings (by position), `string` (one group), `unit` (none); 'O carries the outputs by label (record) or position (tuple). Members: `Format`, `Paths` (all outputs), `For` (a sequence of group values -> every output), `Glob` (null fields unbound, `X.Any` = all null), `Make` (outputs, `Target<'G,'O>` only), `Rule` (form D), `rule` (form B), `Pattern`, `Spec` (the layer-1 value) | prototyped |
| 3. form B marker `matched` + `TypedRuleBuilder<'G>` | `X.rule { let! m = matched ... }`; one `Bind` on the marker, so `m.fwk` infers (no FS0072 of 09's overloads) | prototyped; **the provider package should reuse this marker** (§5) |

### The surface on real rules (`consumer/Rules.fs`)

```fsharp
open Xake
open Xake.Typed                     // after `open Xake` (§5)

type LibG = { fwk: string; lib: string }
type LibOut = { dll: string; xml: string }
let Lib = Target<LibG, LibOut>("dll=out/(fwk:*)/(lib:*).dll", "xml=out/(fwk:*)/(lib:*).xml")   // build.fsx:140
type AsmG = { cfg: string; fwk: string; name: string }
let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"                 // build.fsc.fsx:63
type SrcG = { dir: string; _1: string; _2: string }                      // bare wildcards are _1, _2
let Sources = Target<SrcG> "src/(dir:*)/**/*.fs"
let TestBuild = Command<string> "test-build::(project:*)"                // one group: the string itself
type LockG = { name: string; cfg: string }
let Lock = Command<LockG> "lock::(name:*)::(cfg:*)"                      // build.fsc.fsx:47
let Clean = Command<unit> "clean"

"build" <== Lib.For [ for lib in libraries do for f in frameworks -> { fwk = f; lib = lib.Name } ]
"locks" <== Lock.For [ for l in libraries -> { name = l.Name; cfg = "Release" } ]

Lib.Rule <| fun m -> recipe {                                            // form D
    let out = Lib.Make m                                                 // out.dll, out.xml
    let! sources = getFiles (Sources.Glob { Sources.Any with dir = (library m.lib).Dir })
    do! need [ for dep in (library m.lib).Needs -> (Lib.Make { m with lib = dep }).dll ]
    ...
}
Lock.Rule <| fun { name = name; cfg = cfg } -> recipe {                  // a record pattern as the parameter
    for r in (library name).Needs do do! need [ Lock.Format { name = r; cfg = cfg } ]
}
Asm.rule {                                                               // form B
    let! { name = name; fwk = fwk } as m = matched                       // partial record pattern
    do! need [ Lib.Format { fwk = fwk; lib = name } ]
}
TestBuild.Rule <| fun project -> recipe { ... }
Clean.Rule <| fun () -> recipe { ... }
```

Differences from the provider surface (08): `let X = Target<G> "..."` is a value, not a `type X =`
line; `Format { .. }` takes a record instead of named arguments; `For` takes a sequence of records
(no cross product of per-group lists); `Glob { X.Any with dir = "core" }` instead of
`Glob(dir = "core")`; outputs come from `X.Make m` (a separate 'O record), not from `m.dll`.

## 3. When each mistake fires (`neg/output.txt`)

C = compile time (editor squiggle), S = script start (value creation, before any target),
G = at the generating call (script start for `<==` lists, rule run inside a recipe), R = rule run.

| mistake | when | message |
|---|---|---|
| `m.fkw` (form D and B) | C | `FS0039: The type 'AsmG' does not define a field, constructor, or member named 'fkw'. Maybe you want one of the following: fwk` |
| `Asm.Format { cfg = ..; fwk = .. }` (a group left out) | C | `FS0764: No assignment given for field 'name' of type 'AsmG'` |
| `Asm.Format { ..; fkw = ..; .. }` | C | `FS1129: The record type 'AsmG' does not contain a label 'fkw'. Maybe you want one of the following: fwk` |
| `let! { fkw = f } = matched` | C | the same FS1129 |
| `(Lib.Make m).pdb` | C | `FS0039: The type 'LibOut' does not define ... 'pdb'.` |
| `Asm.Rule <\| fun (m: LibG) -> ..` (another pattern's record) | C | `FS0001: This expression was expected to have type 'AsmG' but here has type 'LibG'` |
| `matched` outside `X.rule` | C | `FS0041: No overloads match for method 'Bind'. Known types of arguments: Matched * ...` |
| tuple carrier, `fun (cfg, fwk) ->` for 3 groups | C | `FS0001: Type mismatch. Expecting a tuple of length 3 ...` |
| record field misspelt in the type (`fkw: string`) | S | `Target<BadG> "out/(cfg:*)/(fwk:*)/(name:*).dll": the record BadG has fields ["cfg"; "fkw"; "name"], the pattern has ["cfg"; "fwk"; "name"] (not in the record: ["fwk"]; not in the pattern: ["fkw"])` |
| a group missing from the record | S | `... (not in the record: ["cfg"])` |
| multi-output groups differ | S | `Pattern "out/(fwk:*)/(lib:*).dll", "out/(fwk:*)/(name:*).xml": output "out/(fwk:*)/(name:*).xml" has groups ["fwk"; "name"], the first output ["fwk"; "lib"] (missing ["lib"], extra ["name"])` |
| outputs record vs labels | S | `Target<LibG, Outs> ...: the record Outs has fields ["dll"; "pdb"], the outputs are ["dll"; "xml"] (not in the record: ["xml"]; not in the outputs: ["pdb"])` |
| unclosed group `(fwk:*/` | S | `Pattern "...": a group '(name:...' is not closed (it parses as "...")` |
| non-string field, tuple arity, `unit` with groups, bare wildcards in multi-output, wildcards not in the record, duplicate label | S | one precise line each (`neg/output.txt` s06–s11) |
| a `/` in a `*` value, a constraint `(ext:c*)` violated | G | `Pattern "src/(dir:*)/(file:*).(ext:c*)": [("dir", "src"); ("file", "a"); ("ext", "fs")] do not give a path the pattern matches back (got "src/src/a.fs")` |
| `null` field passed to `Format` | G | `... no value for ["fwk"]` |
| untyped `Pattern`: unknown name in `Format`, `m.["fkw"]` in a rule | G / R | `... no group ["fkw"] (it has ["cfg"; "fwk"; "name"])`; today's `getRuleMatch "fkw"` gives `''` silently (r02) |
| **swapped tuple elements** (tuple carrier) | never | compiles and formats `out/net8.0/Release/Core.dll` — the reason to prefer records |

Two records with the same labels (`AsmG` for `out/`, `RunG` for `run/`): a record literal written
straight into `Asm.Format { .. }` takes the expected type (works, p06); one bound to a `let` first takes
the latest declared type, and passing it gives FS0001 at compile time (p07) — never a silent mix-up.
One record can also serve several patterns with the same groups (`Asm`/`Run`, p08, r04).

## 4. Cost

| item | measured |
|---|---|
| code | `TypedPatterns.fs` ~270 lines (with doc comments), one file after `ScriptFuncs.fs`; no new dependency |
| `Xake.dll` | 683 KB -> 740 KB (+56 KB), net462 and netstandard2.0 both build, 0 warnings |
| FSharp.Core | only `FSharp.Reflection` (`PreComputeRecordConstructor`/`Reader`, `PreComputeTuple*`), present in the 8.0.100 pin |
| run time | creating a `Target<_>` (parse, checks, reflection precompute): 0.13 ms; `Format` 4–5 µs per call including the round-trip re-match (sprintf: 0.25 µs); a typed read of a rule match is one precomputed constructor call |
| private records | need `allowAccessToPrivateRepresentation = true` in the reflection calls (found by p16/p17: `type private G` and `type G = private { .. }` were rejected before the fix) |
| fsi / IDE | plain F#: no design-time dll, no FS3031, no file locking, no language-server restart; identical on SDK 10 and SDK 8 fsi and compiled |

## 5. Names: Core and the provider package side by side (`probes/`, `tp-probes/`)

- **`Command<_>` needs its own namespace.** `open Xake` brings Logging's union case `Level.Command`;
  in an expression a union case wins over a type of the same name declared in `Xake` (as a namespace
  type or in a later `[<AutoOpen>]` module): `Command<string> "..."` -> `FS0717: Unexpected type
  arguments`, also `Command<string>.Of "..."`. Only a namespace opened explicitly after `open Xake`
  shadows the case — hence `namespace Xake.Typed` in the prototype (`new Command<_>(...)` also works).
  The same applies to the provider's `Command<"...">` in expression position (decision 23's inline
  `Command<"clean">.Rule <| ...`): it works in 08 only because `open Xake.Patterns` comes last; with
  the opens reversed it is the same FS0717 (`tp-probes/inline-command-08-output.txt`). Alternative: `[<RequireQualifiedAccess>]` on `Level`
  (Core's own code uses `Log Command` unqualified; no script or sample does).
- **The generic `Target<'G>` hides the provider's `Target<"...">`**: with both namespaces open, in
  either order, `Target<"out/...">` resolves to the generic type: `FS0618: Invalid literal in type`
  (same for `Command<"...">`). Qualified `Xake.Patterns.Target<"...">` works. The DU `Xake.Target`
  is not affected (`Target` and `Target<'G>` differ in arity; p05).
- **`matched`**: two markers in scope — the last open wins, and the other builder gets FS0041. If the
  provider package reuses Core's `Matched`/`matched` (its builder binds Core's marker), there is one
  marker and no clash. Core's marker should then live where both can see it (`Xake` itself — the
  name does not clash with anything there).

So a script uses one of the two: `open Xake.Typed` (Core) or `open Xake.Patterns` (provider). Moving
between them is mechanical: `let X = Target<G> "p"` + `type G = {..}` <-> `type X = Target<"p">`,
`X.Format { a = 1 }` <-> `X.Format(a = 1)`; `X.Rule <| fun m -> .. m.a ..` and
`X.rule { let! m = matched .. }` read the same.

## 6. Core vs the provider package

| | Core: record-declared (`Xake.Typed`) | provider package (`Xake.Patterns`, 08) |
|---|---|---|
| group names written | twice: record + pattern; agreement checked **S** | once (the pattern) |
| typo in access `m.fkw` | **C** | **C** |
| generation names / completeness | **C** (FS1129 / FS0764) | **C** (FS0039 / FS0496) |
| pattern syntax, multi-output consistency | **S** | **C** (FS3033) |
| outputs | `X.Make m` -> a declared 'O record, labels checked **S**, use **C** | `m.dll`, **C**, nothing to declare |
| destructuring | **real record patterns** (`fun { fwk = fwk } ->`, `let! {..} = matched`), copy-and-update `{ m with lib = dep }` | impossible (09); tuple `groups` only, order unchecked |
| lists for `need` | `X.For [ for .. -> { .. } ]` | `X.For(fwk = [..], lib = [..])` cross product, or a comprehension |
| `Glob` | `X.Glob { X.Any with dir = "core" }` | `X.Glob(dir = "core")` |
| constraint / `/` in a value | G | R (same re-match) |
| pattern from run-time data (`$"out/pkg/%s{pkg}/..."`) | **yes** (p19), checked at creation | no: `[<Literal>]` only |
| a pattern as a library parameter (`outOf`-style API) | **yes**, a real type `Target<AsmG>` (p20) | no: erased types |
| inline use without a declaration | only with `string`/`unit`/tuple carriers | `Target<"...">.Rule` inline |
| IDE / tooling risk | none | design-time dll, FS3031 when misplaced, locking, untested IDEs |
| package / size | in `Xake.dll`, +56 KB | separate package, design-time dll 1.2 MB + TP SDK sources |
| name constraints | `open Xake.Typed` after `open Xake` | `open Xake.Patterns` after `open Xake`; DU written `DomainTypes.Target` |

## 7. Leaning (labelled as such — not a decision)

- **Into Core:** layer 0 (as planned), layer 1 `Pattern` (the checked value; also what the provider's
  erased members can call instead of their own runtime — one implementation of format/glob/re-match/
  checks), layer 2 with **records as the recommended carrier** and `string`/`unit` for one/no group,
  layer 3 `matched` + `TypedRuleBuilder<'G>` placed so the provider reuses it.
- **Tuple carriers**: keep possible (they cost nothing — the codec handles them) but document as
  positional: swaps are silent (p10), exactly 09's verdict on `groups`.
- **Not in Core:** a cross-product `For` over per-group lists (no typed shape without a provider);
  `Need`/`TryMatch` (decision 25 holds for Core too).
- **What the provider package still adds on top:** names written once; pattern errors at compile time
  instead of script start; outputs as properties with nothing to declare; named-argument
  generation and the cross-product `For`; inline `Target<"...">` with no declaration.
  What it cannot add: record destructuring, run-time patterns, patterns as library parameters.

## 8. Open questions

1. Names: Core's types in `Xake.Typed` (or another namespace) and the provider's in `Xake.Patterns`
   with the same short names `Target`/`Command` — accepting that a script opens one of them — or
   different names in Core (e.g. `Pattern<'G>`, `Phony<'G>`) so both can be open?
   And `[<RequireQualifiedAccess>]` on `Logging.Level` to free `Command` in `Xake` itself?
2. Should the provider package erase to Core's `Pattern`/`Target<Map>`-style runtime (one runtime,
   one set of messages), making it a pure compile-time layer over Core?
3. `For`: sequence of records only, or also a helper for cross products
   (`Lib.For(frameworks, libs, fun f l -> { fwk = f; lib = l })`)?
4. Outputs: a second type argument 'O (prototyped) vs. a label lookup on the match
   (`Lib.Out(m, "xml")`, stringly) vs. nothing (`Lib.Paths m`)?
5. Error type: `XakeException` at script start prints as an fsi exception with a stack trace; is a
   shorter "script error" rendering wanted?
