# Rule definition syntax for typed patterns: fewer parentheses

Answers the "Under discussion" item of [`brief.md`](brief.md): `Dll.Rule(fun m -> recipe { ... })`
has too many parentheses; can a computation expression do better? Prototype:
[`experiments/07-ce-syntax`](experiments/README.md) (section 07). Status: material for
discussion, nothing decided here.

## 1. What the forms have to fit

- Groups typed and checked at compile time: a misspelt group is a compile error.
- `Pattern<>` with one output, `Pattern<"p1", "p2">` (labelled outputs `m.dll`, `m.xml`) and
  `Command<>` (phony, `=>` semantics as in experiment 04).
- Any form goes into the same `rules [ ... ]` list as `"x" => recipe`, `target "..." { }`,
  `command "..." { }`; works in `dotnet fsi`.

Two facts settle most of the design space:

1. **A computation expression cannot introduce a variable by itself.** The translation is
   `b.Run(b.Delay(fun () -> body))`; a name becomes visible in the body only through syntax the
   user writes: a lambda (`fun m ->`), `let!`/`use!`/`match!`, or `for m in ...`. No `Run`, `Delay`,
   custom-operation or implicit-yield trick hands the match to the body. So the choice is between
   a lambda (today) and one `let!` line.
2. **A provided type is not a value.** `Dll ..> recipe {...}` or `rule Dll (...)` cannot be
   written; the operand has to be a member of the type (`Dll.Pattern`, `Dll.rule`).

What does work (verified): an erased provided type can expose a static property whose type is a
generic builder class from the runtime dll, instantiated at the provided type itself
(`TypedRuleBuilder<Dll>`, erased to `TypedRuleBuilder<Map<string,string>>`). From there it is an
ordinary F# CE: the builder inherits `RecipeBuilder` and adds one `Bind` overload whose result
type is the builder's type parameter.

## 2. The forms

**A — pattern as an operand, typed `Get` inside.** Existing operators, a plain `recipe`:
```fsharp
Dll.Pattern ..> recipe {          // `Pattern`: only on single-output types
    let! m = Dll.Get
    ... }
Lib.Patterns *..> recipe { let! m = Lib.Get ... }
Test.Pattern => recipe { let! m = Test.Get ... }
```
**B — a builder on the provided type.** Mirrors the existing `target`/`targets`/`command`
builders (`RuleBuilder.fs`), with the pattern named once:
```fsharp
Dll.rule {
    let! m = matched              // `matched`: a marker value; m : Dll, fixed by the builder
    ... }
Lib.rule { let! m = matched ... }     // MultiFileRule
Test.rule { let! m = matched ... }    // PhonyRule
```
**C — a custom operation in `xakeScript`.** The existing `rule` operation takes any `Rule`, so
it already accepts every form, but only parenthesised: `rule Dll.rule { ... }` is
`FS3099: 'rule' is used with an incorrect number of arguments ... Expected 1 argument(s), but
given 2`. A new operation could take at most a pattern member and a lambda
(`ruleOf Dll.Pattern (fun m -> ...)`): more noise than today, and Xake scripts put their rules in
`rules [ ... ]`, where a custom operation cannot appear. **Dropped.**

**D — pipe into today's `Rule`.** No new member; the parentheses go:
```fsharp
Dll.Rule <| fun m -> recipe {
    ... }
```

Not prototyped, dropped on paper: per-group typed getters (`let! fwk = Dll.fwk`: a `let!` per
group, which is today's noise, and a static `fwk` clashes with the instance property); `for m in
matched do` (indents the whole body); a generic `rule<Dll> { }` (an erased type has no run-time
identity, the pattern text could not reach the rule).

## 3. Before / after on three real rules

**Single-file rule with groups** (`build.fsc.fsx:63-68`), `type Dll = Pattern<"out/(cfg:*)/(fwk:*)/(name:*).dll">`

```fsharp
// today (string API)
target "out/(cfg:*)/(fwk:*)/(name:*).dll" {
    let! cfg = getRuleMatch "cfg"
    let! fwk = getRuleMatch "fwk"
    let! name = getRuleMatch "name"
    do! Locks.compileAs (lockOf name cfg) fwk versions
}
// 05 prototype
Dll.Rule(fun m -> recipe {
    do! Locks.compileAs (lockOf m.name m.cfg) m.fwk versions
})
// A
Dll.Pattern ..> recipe {
    let! m = Dll.Get
    do! Locks.compileAs (lockOf m.name m.cfg) m.fwk versions
}
// B
Dll.rule {
    let! m = matched
    do! Locks.compileAs (lockOf m.name m.cfg) m.fwk versions
}
// D
Dll.Rule <| fun m -> recipe {
    do! Locks.compileAs (lockOf m.name m.cfg) m.fwk versions
}
```

**Multi-file rule** (`build.fsx:139-162`), `type Lib = Pattern<"out/(fwk:*)/(lib:*).dll", "out/(fwk:*)/(lib:*).xml">`

```fsharp
// today
targets ["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"] {
    let! framework = getRuleMatch "fwk"
    let! lib = getRuleMatch "lib" |> Recipe.map library
    ...
    do! need [for dep in lib.Needs -> $"out/%s{framework}/%s{dep}.dll"]
// 05
Lib.Rule(fun m -> recipe {
    let lib = library m.lib
    ...
    do! need [ for dep in lib.Needs -> Lib.Make(fwk = m.fwk, lib = dep).dll ]
})
// A                                   // B                         // D
Lib.Patterns *..> recipe {             Lib.rule {                   Lib.Rule <| fun m -> recipe {
    let! m = Lib.Get                       let! m = matched             let lib = library m.lib
    let lib = library m.lib                let lib = library m.lib      ...
    ...                                    ...                      }
}                                      }
```

**Phony rule with groups** (`build.fsx:117-133`), `type Test = Command<"test::(project:*)/(slice:*)">`,
`type TestBuild = Command<"test-build::(project:*)">`

```fsharp
// today
"test::(project:*)/(slice:*)" => recipe {
    let! project = getRuleMatch "project"
    let! slice = getRuleMatch "slice"
    let job = testJob project slice
    do! need [ $"test-build::{project}" ]
    ...
// A
Test.Pattern => recipe {
    let! m = Test.Get
    let job = testJob m.project m.slice
    do! need [ TestBuild.Target(project = m.project) ]
    ...
// B
Test.rule {
    let! m = matched
    let job = testJob m.project m.slice
    do! need [ TestBuild.Target(project = m.project) ]
    ...
// D
Test.Rule <| fun m -> recipe {
    let job = testJob m.project m.slice
    ...
```

All of these, with the scripts' helpers stubbed, are in `experiments/07-ce-syntax/consumer/forms.fsx`
and run by the engine (targets `out/Release/net8.0/Core.dll`, `out/netstandard2.0/Xake.xml`,
`test::core/2`); a last run puts A, B, D, the string API, `command` and `target` into one
`rules [ ... ]` list and builds `main` through them.

## 4. Results

| | A `Dll.Pattern ..>` | B `Dll.rule { }` | D `Dll.Rule <\|` | 05 `Dll.Rule(fun ..)` |
|---|---|---|---|---|
| compiles, runs: single / multi / phony | yes / yes / yes | yes / yes / yes | yes / yes / yes | yes |
| in one `rules [...]` with the string API | yes | yes | yes | yes |
| `dotnet fsi`, F# 10 and the SDK 8 fsi | same output | same output | same output | same |
| parentheses around the body | 0 | 0 | 0 | 1 pair, `})` |
| lines per rule (head + match) | 2 | 2 | 1 | 1 |
| pattern type named | twice (`Dll.Pattern`, `Dll.Get`) | once | once | once |
| misspelt group `m.fkw` | FS0039 ... `Maybe you want ... fwk` | the same | the same | the same |
| misspelt output `m.pdb` (multi) | FS0039 | FS0039 | FS0039 | FS0039 |
| wrong pattern's accessor inside | `let! m = Lib.Get` compiles; **run time**: `XakeException "[\|out/(fwk:*)/(lib:*).dll; ...\|].Get: the rule being run has no groups [\|"lib"\|] (it has ["0"; "1"; "2"; "3"; "cfg"; "fwk"; "name"])..."` (the checked `Get`, new in 07) | `matched` cannot be of another type (compile time); `Lib.Get` inside is still possible, same run-time error | impossible | impossible |
| wrong operator for the kind | `Test.Pattern ..> recipe {..}` compiles to a `FileRule "test::..."`, silently (as the string API); `Lib.Pattern ..>`: FS0039 (no `Pattern` on a multi-output type, `Maybe you want ... Patterns`) | impossible: the type picks the rule kind | impossible | impossible |
| `matched` outside the builder | — | in a plain `recipe`: `FS0072 The type of this expression could not be inferred...` (when `m.` is used) or `FS0041 No overloads match for method 'Bind'. Known types of arguments: Matched * ...` | — | — |

Error texts are from `consumer/neg/output.txt` (`sh consumer/neg/run-neg.sh`, 14 cases). The
typo error in B is as good as in the lambda forms: `m`'s type is fixed by the builder's `Bind`
signature before the body is checked.

Other B observations: the builder keeps every `recipe` feature (`let! v = getVar ...`, `do!`,
`for`, `try`, nested `recipe { }`) since it inherits `RecipeBuilder`; the new `Bind` overload
resolves next to the inherited ones. A body that never binds `matched` is still a valid rule.
One-liners read fine: `Dll.rule { let! m = matched in printfn "%s" m.fwk }`.

## 5. Readability

- **B** reads like the builders the scripts already use: `target "..." {` / `targets [...] {` /
  `command "..." {` become `Dll.rule {` / `Lib.rule {` / `Test.rule {`, the pattern having moved
  to the `type` line. The price is the `let! m = matched` line, which looks like boilerplate but
  is the only way a CE body gets a name (section 1), and doubles as the place the type is fixed.
- **D** is the shortest and needs nothing new, but `<| fun m -> recipe {` is three concepts on one
  line; for F# users idiomatic, for build-script authors less so than B.
- **A** keeps the exact shape of today's rules (`..>`, `*..>`, `=>`), which helps migrating a
  string rule (only the head and the `getRuleMatch` lines change), but the type is named twice
  and the two mentions are not tied together: the safety comes from a run-time check.
- **05** (`Dll.Rule(fun m -> ...)`): the closing `})` on its own line is what the complaint is
  about.

## 6. Implementation cost (on top of 05; numbers from the prototype)

| form | runtime (Xake.dll) | provider | engine |
|---|---|---|---|
| A | `getFor` (the checked `Get`), 9 lines; `Pattern`/`Patterns` properties replace `Text`/`Texts` | ~6 lines | none |
| B | `Matched` + `matched` + `TypedRuleBuilder<'M>` (8 lines) + `PatternBuilders.rule`/`command` (9 lines) | 3 lines (one `MakeGenericType`, one property) | none |
| C | — | — | — (dropped) |
| D | none (`Rule` exists) | none | none |

Whole 07 provider 129 lines (05: 115) with `Command<>` ported onto the AST; runtime 127 (05: 68,
of which ~30 are A/B and ~20 are `Command<>`). Design-time dll unchanged at 1.2 MB. In the real
design `TypedRuleBuilder` and `matched` sit in Xake.dll next to `RuleBuilder` (namespace `Xake`,
so `open Xake` brings `matched` in, as the prototype does with a second `namespace Xake` block).

## 7. Leaning (labelled as such — not a decision)

**B as the documented form**, with `Get` kept (checked, form A) for bodies that stay on the
string operators, and D available for free because `Rule` stays. B has no parentheses, names the
pattern once, takes the rule kind from the type (no `..>` vs `=>` mistake), and matches the
`target`/`command` builders authors already write. If two ways to write a rule on one type
(`rule { }` and `Rule(fun ..)`) is one too many, drop `Rule` and keep B only.

## 8. Open questions

1. **Names.** One `rule` for all kinds, or the existing builder names per kind: `Dll.target { }`,
   `Lib.targets { }`, `Test.command { }`? `rule` beside `Rule` differs only in case; if both stay,
   one of them wants another name.
2. **`Command<>.rule` semantics.** It now gives a plain `PhonyRule` (`=>`). The `command "..." { }`
   builder adds `AlwaysRerun`; a `Test.command { }` would suggest the same. Which one?
3. **The marker's name.** `matched` is short and reads well in `let! m = matched`; it is a common
   word to put into `open Xake` scope (a user value of that name would shadow it; not tried).
   Alternatives: `rulematch`, `groups`, `this`-like `it`.
4. **A's operand names.** `Dll.Pattern` / `Lib.Patterns` (07) or keep `Text` / `Texts` (03, 05).
   Should `Get` stay run-time checked (costs a map lookup per key per run of the rule)?
5. IDE behaviour of `Dll.rule {` (completion of `m.` after `let! m = matched`, hover) is untested,
   as for the whole provider.
