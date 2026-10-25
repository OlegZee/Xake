# Destructuring the typed match: `let! { fwk; brand } = matched`?

The user's wish on form B (decision 20): `Dll.rule { let! m = matched ... m.fwk ... }` reads well,
but `m.` in front of every group is noise; destructuring like `let! { fwk; brand } = matched` "would
compensate more than half of the inconvenience". This note says what F# allows, what was prototyped,
and what each variant costs. Prototype and every compiler message quoted here:
[`experiments/09-destructure`](experiments/README.md) (section 09, `output.txt`). Material for
discussion; nothing decided here.

## 1. Bottom line

- **Destructuring that is checked by group name at compile time cannot be built on the provider.**
  F# checks a name in a pattern only against an F# record field or an F# union-case field. A type
  provider, erased or generative, cannot produce either (verified for both, section 2). Field punning
  `{ fwk; brand }` does not exist in F# at all (parse error, F# 8, 10 and `--langversion:preview`).
- **What works:** a tuple, `let! cfg, fwk, name = groups`, arity checked at compile time, order not;
  a user-declared F# record, `let! { cfg = cfg; fwk = fwk } = fields`, checked against the record at
  compile time but against the pattern only when the rule runs; a tuple lambda for form D,
  `Asm.RuleOf <| fun (cfg, fwk, name) -> recipe { }`.
- **What breaks things:** a second `Bind` overload on the same marker `matched` (tuple or record)
  makes today's `let! m = matched ... m.fwk` fail with FS0072; a second `Rule` overload makes every
  `Asm.Rule <| fun ...` fail with FS0041. Any new shape needs its own marker / member name.

## 2. What the language accepts (plain F#, `plain/cases.fsx`; generative TP, `generative/`)

Identical on the SDK 10 and SDK 8 fsi (only FS0039's wording differs).

| construct | result |
|---|---|
| punning `let { fwk; name } = r` (F# record) | `FS0010: Unexpected symbol ';' in pattern. Expected '.', '=' or other token.` — also with `--langversion:preview` |
| partial record pattern `let { fwk = fwk; name = name } = r`, also in `let!` | **works** (any subset of fields) |
| misspelt label `let { fkw = fwk } = r` | `FS0039: The record label 'fkw' is not defined. Maybe you want one of the following: fwk` |
| anonymous record pattern `let {\| fwk = fwk \|} = a` | `FS0010: Unexpected symbol '{\|' in binding` (no anonymous-record patterns) |
| record pattern on a .NET class (`let { Major = m } = System.Version(1, 2)`) | `FS0039: The record label 'Major' is not defined.`; qualified `{ System.Version.Major = m }`: `FS0039: The type 'Version' does not define a field, constructor, or member named 'Major'.` |
| **generative** provided type (`Gen<"cfg,fwk,name">`, a real class with fields and properties, `generative/provider`) | `let { fwk = fwk } = g`, with or without `: DllG`: `FS0039: The record label 'fwk' is not defined.`; `{ DllG.fwk = fwk }`: `FS0039: The type 'DllG' does not define a field, constructor, or member named 'fwk'.`; construction `{ cfg = ..}` likewise. The class itself works (`g.fwk`). A generated type is an IL class; F# record-ness lives in F# metadata a provider cannot emit. Erased types (03–08) are not even classes. |
| C#-style `Deconstruct` + `let fwk, name = M(...)` | ignored: `FS0193: Type constraint mismatch. The type 'M' is not compatible with type ''a * 'b'` |
| active pattern as a static member `static member (\|Groups\|) r = ...` | cannot be declared: `FS0827: '(\|Groups\|)' is not a valid method name. Use a 'let' binding instead.` |
| a provided static method named `\|Parts\|` (probe, `XAKE09_AP=1`) | provided fine and callable as ``Asm.``\|Parts\|`` m``, but not a pattern: `Asm.Parts (cfg, fwk, name)` -> `FS0039: The type 'Target<...>' does not define ... 'Parts'. Maybe you want ... (\|Parts\|)`; after `open type Asm`: `FS0039: The pattern discriminator 'Parts' is not defined.` Active patterns come only from `let` values in F# modules. |
| union case named fields `let (DllMatch (fwk = fwk; name = name)) = u` | **works**, misspelt: `FS3174: The union case 'DllMatch' does not have a field named 'fkw'.` — F# unions only, so same verdict as records |
| tuple `let cfg, fwk = r.cfg, r.fwk, r.name` | `FS0001: Type mismatch. Expecting a tuple of length 2 of type 'a * 'b but given a tuple of length 3 of type string * string * string` |

So the only name-checked destructuring needs a record or union **written in F# source**. The
provider sees only the pattern literal; it cannot read a record the script declares, and the script
cannot get a record from the provider.

## 3. The variants on real rules

Rules from `build.fsc.fsx` (helpers stubbed): `Asm = Target<"out/(cfg:*)/(fwk:*)/(name:*).dll">` (:64),
`Lock = Command<"lock::(name:*)::(cfg:*)">` (:47), `TestBuild = Command<"test-build::(project:*)">`
(:74), and build.fsx's labelled `Lib = Target<"dll=out/(fwk:*)/(lib:*).dll", "xml=...">`. All in
`consumer/cases.fsx`, run by the engine on both SDKs.

**b — status quo** (`let! m = matched`):
```fsharp
Asm.rule {
    let! m = matched
    do! Locks.compileAs (lockOf m.name m.cfg) m.fwk versions
}
Lock.rule {
    let! m = matched
    let! d = Description.load (descriptionOf m.name)
    for r in d.ProjectRefs do do! need [ Lock.Format(name = r, cfg = m.cfg) ]
    do! Locks.generate m.cfg (descriptionOf m.name)
}
```

**a2 — tuple through its own marker `groups`** (works):
```fsharp
Asm.rule {
    let! cfg, fwk, name = groups
    do! Locks.compileAs (lockOf name cfg) fwk versions
}
Lock.rule {
    let! name, cfg = groups
    let! d = Description.load (descriptionOf name)
    for r in d.ProjectRefs do do! need [ Lock.Format(name = r, cfg = cfg) ]
    do! Locks.generate cfg (descriptionOf name)
}
TestBuild.rule {
    let! project = groups               // one group: a plain string
    do! sh $"dotnet build {testProjectDir project} -c Release"
}
Lib.rule {                              // labelled outputs are not in the tuple
    let! fwk, lib = groups
    let! m = matched                    // m.dll, m.xml
    ...
}
```
Tuple order = `Make`'s parameter order: named groups as they appear in the pattern, then bare
wildcards `_1`, `_2`, …; no groups gives `unit`.

| mistake | result |
|---|---|
| wrong arity `let! cfg, fwk = groups` | `FS0001: Type mismatch. Expecting a tuple of length 3 of type string * string * string but given a tuple of length 2 of type 'a * 'b` |
| **swapped** `let! fwk, cfg, name = groups` | compiles, runs: `compileAs locks/Core.net8.0.lock Release` (cfg and fwk exchanged, silently) |
| wrong type `let! (project: int) = groups` | `FS0001: This expression was expected to have type 'string' but here has type 'int'` |

How bad is the silent swap: all groups are strings, so nothing catches it but a wrong path at run
time. The order is on the `type` line (usually a screen away, not in the rule), and is the same
order `Make`/`Format`/`For` take positionally. With one group it cannot happen; with two it is a
real risk (`lock::(name:*)::(cfg:*)` vs the natural "cfg, name" of `lockOf name cfg`); with three
or more (`Asm`) it needs reading the type line each time.

**a1 — the tuple through `matched` itself** (a second `Bind` overload, opt-in module
`Xake.Patterns.TupleOnMatched`): `let! cfg, fwk, name = matched` works, one-group
`let! project = matched` works when the body uses it as a string — but **today's form breaks**:
`let! m = matched` + `m.name` gives `FS0072: The type of this expression could not be inferred before
accessing its members.` With overloads that differ in the function's domain, F# checks the lambda
before choosing, so `m` has no type when `m.name` is looked up. Wrong arity becomes
`FS0041: No overloads match for method 'Bind'` with the whole overload list. **Rejected.**

**d — `for ... in groups do`** (a `For` overload on the marker): works,
```fsharp
Asm.rule {
    for cfg, fwk, name in groups do
        do! Locks.compileAs (lockOf name cfg) fwk versions
}
```
but indents the whole body and reads as a loop. The other CE route, a custom operation with
`into` (`plain/ce.fsx`, toy builder): `matched into (cfg, fwk, name)` works, `let!` may follow; a
record pattern after `into` does not parse (`FS3099: 'matched' is used with an incorrect number of
arguments ... Expected 0 argument(s), but given 2.`). A custom operation without `into` can only keep
or replace the names the user bound — CE machinery never adds a name of its own. Both are the tuple
of a2 in another syntax; not pursued.

**e — form D parity** (`RuleOf`, a separate member):
```fsharp
Asm.RuleOf <| fun (cfg, fwk, name) -> recipe {
    do! Locks.compileAs (lockOf name cfg) fwk versions
}
TestBuild.RuleOf <| fun project -> recipe { ... }
```
works; arity: the same FS0001 as a2. Naming it `Rule` (an overload, probe `XAKE09_RULE_OVERLOAD=1`)
breaks decision 18's form: every `Asm.Rule <| fun m -> ...` **and** `Asm.Rule <| fun (cfg, fwk, name) -> ...`
gives `FS0041: A unique overload for method 'Rule' could not be determined ... Known type of argument: 'a0`
(the pipe hides the lambda from overload resolution); parenthesised `Asm.Rule(fun (a, b, c) -> ..)`
resolves, `Asm.Rule(fun m -> .. m.name ..)` gets FS0072. Without a new member: `fun m -> recipe { let
cfg, fwk, name = m.cfg, m.fwk, m.name ...` (each name written twice, but name-checked).

**f — a record the script declares** (marker `fields`; through `matched` it breaks form b exactly
like a1, case `f1-status-quo-with-record-overload`):
```fsharp
type AsmGroups = { cfg: string; fwk: string; name: string }   // next to `type Asm = Target<...>`
Asm.rule {
    let! { cfg = cfg; fwk = fwk; name = name } = fields
    do! Locks.compileAs (lockOf name cfg) fwk versions
}
```
The runtime fills the record by field name (`FSharpValue.MakeRecord`); a partial record
(`{ fwk: string }`) works too.

| mistake | result |
|---|---|
| pattern label misspelt `{ fkw = fwk }` | compile time: `FS0039: The record label 'fkw' is not defined. Maybe you want one of the following: fwk` |
| record field misspelt (`fkw: string` in the type) | **run time**, when the rule runs: `XakeException "let! { ... }: AsmGroups: the rule's pattern has no groups [\|"fkw"\|] (it has ["cfg"; "fwk"; "name"])"` |
| the record of another pattern (`LibGroups` in an `Asm` rule) | run time: `... LibGroups: the rule's pattern has no groups [\|"lib"\|] ...` |

Costs: a record per pattern that repeats the group names (the pattern stays the only source of
truth only by a run-time check); no punning, so each name is still written twice
(`cfg = cfg`); records sharing labels need care (F# picks the record that has all the labels used,
`p13`; an annotation settles it, `p14`). It brings back exactly the duplication the provider removed.

## 4. Side by side (the `Lock` rule, 2 groups used 5 times)

| form | head lines | group uses | names checked | order risk |
|---|---|---|---|---|
| b `let! m = matched` | 2 | `m.name`, `m.cfg` ×5 | compile time | none |
| a2 `let! name, cfg = groups` | 2 | `name`, `cfg` | arity only | silent swap |
| d `for name, cfg in groups do` | 2 + indent | `name`, `cfg` | arity only | silent swap |
| e `Lock.RuleOf <\| fun (name, cfg) ->` | 1 | `name`, `cfg` | arity only | silent swap |
| f `let! { name = name; cfg = cfg } = fields` | 2 + a record type | `name`, `cfg` | vs record: compile time; vs pattern: run time | none |

## 5. Cost (on top of 08)

Runtime (`patterns/Runtime.fs`, 112 -> 177 lines, of which the opt-in probe modules are 10):
`tupleOf` (FSharpValue.MakeTuple), `recordOf`, `ruleTuple`/`commandTuple`, markers `Groups`/`Fields`,
`TypedRuleBuilder<'M, 'G>` (second type argument, `Bind(Groups)`, `For(Groups)`, `Bind(Fields)`).
Provider (`designtime/Provider.fs`, 148 -> 183): the tuple type per pattern, `RuleOf`, the builder
built through `MakeGenericMethod`; the probes behind environment variables. Engine: none.
Clean build of both dlls: 0 warnings.

## 6. Leaning (labelled as such — not a decision)

The wish as stated (`{ fwk; brand }`, checked by name) is not reachable: no punning in F#, and no
provider can feed a record pattern. Of what is reachable, **a2 `let! cfg, fwk, name = groups`** is
the only one that removes the `m.` noise without new declarations; it keeps `matched` untouched, so
it can ship as an addition to form B. Its price is that group names become the author's own
variable names, checked for count but not order. Leaning: offer `groups` beside `matched`,
documented as "in pattern order", recommended for rules with one or two groups. For rules with
three or more groups, or with labelled outputs, recommend `let! m = matched`. Do not overload
`matched` or `Rule`. `RuleOf` (e) only if form D should have the same shortcut. Do not offer the
record variant (f): it brings the duplicated names back and checks them only when the rule runs.

## 7. Open questions

1. Ship `groups` at all, given it gives up name checking (the point of the provider) for brevity?
2. The marker's name: `groups` reads well in `let! cfg, fwk, name = groups`; `parts`, `values`?
3. Should the tuple include labelled outputs after the groups (`let! fwk, lib, dll, xml = groups`)?
   Not prototyped; it makes the order risk worse.
4. The `X.rule` property's XML doc could list the tuple order (`groups: (cfg, fwk, name)`) for
   hover; IDE behaviour is untested, as for the whole provider.
