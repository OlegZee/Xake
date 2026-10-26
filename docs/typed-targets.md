# Typed targets (`Xake.Typed`)

`open Xake.Typed` lets a rule declare its pattern once, with its groups as the fields of an F#
record. The rule reads them as `m.fwk`, and the same declaration generates the paths other rules
need. Misspelt group names are compile errors, a pattern that disagrees with its record fails when
the script starts, and the path you `need` can no longer drift from the pattern that builds it.

It is plain F# in the `Xake` package (no type provider, no extra package), and it sits beside the
string API: `"out/(name:*).o" ..> ...`, `getRuleMatch` and `=>` keep working. See
[Migration](#migration) for the move from one to the other and
[the comparison](#string-api-and-typed-api) for what each gives.

- [Why](#why)
- [Declaring a target](#declaring-a-target)
- [Writing the rule](#writing-the-rule)
- [Generating paths](#generating-paths)
- [Commands](#commands)
- [Errors at script start](#errors-at-script-start)
- [What is checked when](#what-is-checked-when)
- [Limits](#limits)
- [String API and typed API](#string-api-and-typed-api)
- [Migration](#migration)

A runnable script is [samples/typed-targets.fsx](../samples/typed-targets.fsx). The F# blocks of
this page that are marked `<!-- snippet -->` in the source form one script that the test suite
type-checks against the built `Xake.dll` (`TypedCompileTests`), so they compile as written; blocks
without the mark (the "before" code of the migration, error texts) are fragments.

## Why

A rule with groups reads them by name, as strings:

```fsharp
"out/(cfg:*)/(fwk:*)/(name:*).dll" ..> recipe {
    let! fwk = getRuleMatch "fkw"       // a typo: compiles, runs, gives ""
    ...
}
```

`getRuleMatch` answers `""` for a name the pattern does not have, so the typo above surfaces as a
wrong path far from its cause. The other direction has the same trouble: a rule needing that dll
writes `$"out/{cfg}/{fwk}/{name}.dll"` by hand, and the string drifts from the pattern the day one
of them changes.

With a typed target the pattern text appears once, the groups are fields, and the path is
generated from the pattern.

## Declaring a target

The setup is two opens, in this order, and a record for the groups:

<!-- snippet -->
```fsharp
open Xake
open Xake.Typed      // after `open Xake`, see below

type AsmG = { cfg: string; fwk: string; name: string }
let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"
```

The fields of the record are exactly the named groups of the pattern, all `string`. Declare the
value at the top level of the script, so a mistake is reported when the script starts.

**Why `open Xake.Typed` goes second.** `open Xake` brings the logging level `Command` into scope,
and only a namespace opened later makes `Command<_>` mean the typed command. In the other order
the compiler reports FS0717 ("Unexpected type arguments") at the first `Command<...>`.

What can carry the groups (`'G` in `Target<'G>`):

| `'G` | for a pattern with | access |
|---|---|---|
| a record | any number of groups | `m.fwk` |
| an anonymous record | any number of groups | `m.fwk` (cannot be destructured) |
| `string` | exactly one group | the value itself |
| `unit` | no groups | `()` |

Tuples are not accepted on purpose: their elements are positional, so a swap goes unnoticed.

<!-- snippet -->
```fsharp
let Anon = Target<{| fwk: string; lib: string |}> "out/(fwk:*)/(lib:*).dll"
let Version = Target<string> "out/version-(v:*).txt"   // one group
let Plain = Target<unit> "out/plain.txt"               // none
```

**Bare wildcards** (`*`, `**`, `?` outside a named group) are fields too, by position from the
left: `_1`, `_2`, ...

<!-- snippet -->
```fsharp
type SrcG = { lib: string; _1: string; _2: string }
let Sources = Target<SrcG> "src/(lib:*)/**/*.txt"     // _1 is the `**`, _2 the `*`
```

### Several outputs

A rule that produces several files declares them as a second record of patterns: the field names
are the labels of the outputs.

<!-- snippet -->
```fsharp
type LibG = { fwk: string; lib: string }
type LibOut = { dll: string; xml: string }
let Lib = Target<LibG, LibOut> { dll = "out/(fwk:*)/(lib:*).dll"; xml = "out/(fwk:*)/(lib:*).xml" }
```

Every output needs the same named groups (the outputs are named from the groups of the one that
matched), and no bare wildcards across outputs: name them, `(x:*)`. Without labels, a
`Target<LibG> [ "...dll"; "...xml" ]` (a `string list`) declares the same multi-file rule, and its
`Paths` returns the list.

## Writing the rule

### Form D: a function of the match

<!-- snippet -->
```fsharp
let rulesD = [
    Asm.Rule <| fun m -> recipe {
        do! trace Message "building %s for %s" m.name m.fwk
        // m.cfg, m.fwk, m.name : string
    }
    Lib.Rule <| fun m -> recipe {
        let out = Lib.Make m                  // { dll = "out/net8.0/Core.dll"; xml = "..." }
        do! trace Message "%s, %s" out.dll out.xml
    }
]
```

`X.Rule` takes a function from the groups to a recipe and gives an ordinary rule: put it in
`rules [ ... ]` or the script body like any other. A named record can be destructured in the
parameter:

<!-- snippet -->
```fsharp
let ruleDestructured =
    Asm.Rule <| fun { cfg = cfg; fwk = fwk; name = name } -> recipe {
        do! trace Message "%s/%s/%s" cfg fwk name
    }
```

Anonymous records cannot be destructured (F# has no such pattern); use `m.fwk`.

### Form B: a builder

<!-- snippet -->
```fsharp
let ruleB =
    Asm.rule {
        let! m = matched
        do! trace Message "building %s" m.name
    }
```

`X.rule { ... }` is `recipe { ... }` plus `let! m = matched`, which gives the groups of the rule
being run. `let! { name = n } = matched` destructures. The case-only pair `Rule` (form D) and
`rule` (form B) is intentional.

## Generating paths

The pattern turns into paths in the other direction too, so rules never spell a path twice.

| Member | Gives |
|---|---|
| `X.Format g` | the (first) output's path for the groups `g` |
| `X.Paths g` | every output's path, a `string list` |
| `X.For gs` | every output of every group set in `gs` (a sequence), flattened |
| `X.Make g` | (`Target<'G,'O>`) the outputs as the record `'O`: `(Lib.Make g).xml` |
| `X.Glob g` | a `Fileset` of the existing files; `null` fields stay wildcards |
| `X.Any` | the groups all `null`, the start for `Glob` |
| `X.Pattern`, `X.Patterns` | the pattern text(s), as the string API takes them |

<!-- snippet -->
```fsharp
let frameworks = [ "net8.0"; "netstandard2.0" ]

let build = recipe {
    // one path
    do! need [ Asm.Format { cfg = "Release"; fwk = "net8.0"; name = "Core" } ]
    // a list: every combination you can write as a comprehension
    do! need (Lib.For [ for f in frameworks -> { fwk = f; lib = "Core" } ])
    // copy and update
    let m : AsmG = { cfg = "Release"; fwk = "net8.0"; name = "Core" }
    do! need [ Asm.Format { m with cfg = "Debug" } ]
    // existing files, some groups bound
    let! sources = getFiles (Sources.Glob { Sources.Any with lib = "Core" })
    do! needFiles sources
}
```

`Format` checks its result: the produced path is matched back against the pattern, so a value
with a `/` in a `*` group, or one outside a constraint such as `(ext:c*)`, fails instead of giving
a path the rule would never build. A `null` field fails too (`Glob` is where `null` is the way to
say "any"). Bound values in `Glob`'s masks are escaped; the mask grammar is described in
[Patterns, masks and paths](overview.md#patterns-masks-and-paths).

## Commands

`Command<'G>` is the typed replacement for phony rules. **A command always runs**: its rule
records `AlwaysRerun`, as `command "name" { }` does. Ordinary targets skip when nothing changed;
a command is what you ask to be done.

<!-- snippet -->
```fsharp
type PackG = { lib: string; ver: string }
let Pack = Command<PackG> "pack::(lib:*)::(ver:*)"
let Report = Command<string> "report::(lib:*)"
let Clean = Command<unit> "clean"

let rulesCmd = [
    Pack.Rule <| fun { lib = lib; ver = ver } -> recipe {
        do! need (Lib.For [ for f in frameworks -> { fwk = f; lib = lib } ])
        do! trace Message "pack %s %s" lib ver
    }
    Report.rule {
        let! lib = matched
        do! need [ Lib.Format { fwk = "net8.0"; lib = lib } ]
    }
    Clean.Rule <| fun () -> recipe { printfn "clean" }
]
let toRun = [ Pack.Format { lib = "Core"; ver = "1.0.0" }; Report.Format "Tools" ]
```

A command has `Pattern`, `Format` and `For`; there is no `Glob` or `Make`. `Command<unit>` takes
`fun () ->` and needs no `matched` in form B (`Clean.rule { printfn "clean" }`). Bare wildcards
are allowed in command patterns, as fields `_1`, `_2`, ....

## Errors at script start

The record and the pattern are compared when the value is created. For a `let` at the top level
of a script that is the moment the script starts, before any rule runs. The error is a
`PatternAst.PatternException` shown as its message alone (no stack trace), with the declaration
and the types as F# writes them. Messages, as the tests check them:

```text
Target<MisspeltG> "out/(cfg:*)/(fwk:*)/(name:*).dll": the record MisspeltG has fields [cfg; fkw; name], the pattern has [cfg; fwk; name] (not in the record: [fwk]; not in the pattern: [fkw])

Target<{| fwk; lib |}> "out/(fwk:*)/(name:*).dll": the record {| fwk; lib |} has fields [fwk; lib], ...

Target<LibG> "out/(fwk:*)/(lib:*).dll", "out/(fwk:*)/(name:*).xml": output "out/(fwk:*)/(name:*).xml" has groups [fwk; name], the first output [fwk; lib] (missing [lib], extra [name]); ...

Target<AsmG> "out/(cfg:*)/(fwk:*/(name:*).dll": the group '(fwk:' in "out/(cfg:*)/(fwk:*/(name:*).dll" is not closed

Command<unit> "lock::(name:*)::(cfg:*)": unit carries no groups, the pattern has [name; cfg]; declare a record with these fields

Target<string * string> "out/(cfg:*)/(fwk:*)": string * string is a tuple; a tuple's elements are positional (a swap goes unnoticed): declare a record with the fields [cfg; fwk]

Target<IntG> "out/(cfg:*)/(fwk:*)/(name:*).dll": the field [name] of IntG is not string
```

Errors of generation come at the call:

```text
Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll": no value for [fwk] (a null field)

Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll": the values cfg = "Release", fwk = "net8.0/x", name = "Core" give "out/Release/net8.0/x/Core.dll", which the pattern "out/(cfg:*)/(fwk:*)/(name:*).dll" does not match back (a '/' in a '*' group, or a value outside a group's constraint?)
```

## What is checked when

| When | What |
|---|---|
| compile time | a misspelt group (`m.fkw`), a missing or unknown field in `Format { ... }` or in the outputs record, the record of another target, `matched` outside a rule, `m.name` in a `Command<unit>` rule, the `open` order |
| script start | the record against the pattern (fields, types, closed groups), the outputs' group sets, the outputs record |
| run time | the values given to `Format`/`Paths`/`For` against the pattern (`null`, a value the pattern would not match back) |

The compile-time cases are tested with `dotnet fsi --typecheck-only`
(`src/tests/fixtures/typed-neg/cases.fsx`).

## Limits

- The pattern is a string and nothing requires `[<Literal>]`: it can be built at run time, like
  any other rule pattern. The price is when the check happens: a literal at the top level is
  compared with its record when the script starts, a computed pattern when it is computed.
- Groups are `string`; a record with an `int` field is rejected. A number goes through `string n`.
- Not part of this release: tuples as carriers, a type provider for patterns, a typed form of
  conditional rules (`..?>`).
- A typed `Target` is a file rule (or one multi-file rule); `Command` is the phony kind. There is
  no typed form of a rule with arguments.

## String API and typed API

| | String API | `Xake.Typed` |
|---|---|---|
| declare | `"out/(name:*).o" ..> recipe { }` | `let X = Target<G> "out/(name:*).o"` |
| read a group | `let! n = getRuleMatch "name"` (`""` when the name is unknown) | `m.name`, checked by the compiler |
| path for `need` | `$"out/{n}.o"` written by hand | `X.Format { name = n }` |
| several outputs | `[ ".dll"; ".xml" ] *..> ...`, groups read by name | `Target<G, O>`, `X.Make m` gives the labelled paths |
| group name mismatch | silent `""` at run time | compile error / error at script start |
| phony with groups | `"lock::(n:*)" => recipe { }` (no `AlwaysRerun`) | `Command<G>` (always runs) |
| patterns built at run time | yes | yes (checked where created) |
| sharing a target between scripts | strings kept in sync | a record and a value |

Use the string API for one-off rules and patterns without groups; use `Xake.Typed` where a rule
has groups or where other rules need to name its outputs.

## Migration

### `getRuleMatch` stays

`getRuleMatch`, `getRuleMatches` and the string rules (`..>`, `*..>`, `target`, `targets`,
`command`) are **not deprecated** and behave as before (an unknown group still gives `""`).
Migrate where it helps; nothing forces it.

### A single-file rule with groups

Before:

```fsharp
"out/(cfg:*)/(fwk:*)/(name:*).dll" ..> recipe {
    let! cfg = getRuleMatch "cfg"
    let! fwk = getRuleMatch "fwk"
    let! name = getRuleMatch "name"
    do! need [ $"obj/{cfg}/{fwk}/{name}.cs" ]
    ...
}
// elsewhere
do! need [ $"out/{cfg}/net8.0/{name}.dll" ]
```

After (`Asm` as declared above):

<!-- snippet -->
```fsharp
let Obj = Target<AsmG> "obj/(cfg:*)/(fwk:*)/(name:*).cs"

let migrated = [
    Asm.Rule <| fun m -> recipe {
        do! need [ Obj.Format m ]             // the same groups, another pattern
    }
    Asm.rule {
        let! m = matched
        do! need [ Asm.Format { m with fwk = "net8.0" } ]
    }
]
```

### A multi-file rule

Before:

```fsharp
[ "out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml" ] *..> recipe {
    let! fwk = getRuleMatch "fwk"
    let! lib = getRuleMatch "lib"
    let dll, xml = $"out/{fwk}/{lib}.dll", $"out/{fwk}/{lib}.xml"
    ...
}
```

After: `Lib` of [Several outputs](#several-outputs), and in the rule `let out = Lib.Make m`.

String multi-file rules (`*..>`, `targets`) now **check their patterns when the rule is created**:
every pattern must have the same set of named groups, otherwise a `PatternException` names the
output that differs. Before, outputs with different groups were accepted. This is a behaviour
change, and it can fail a script that used to start.

### A phony rule with groups becomes a `Command`

Before:

```fsharp
"lock::(name:*)::(cfg:*)" => recipe {
    let! name = getRuleMatch "name"
    let! cfg = getRuleMatch "cfg"
    do! alwaysRerun ()
    ...
}
need [ "lock::Core::Release" ]
```

After:

<!-- snippet -->
```fsharp
type LockG = { name: string; cfg: string }
let Lock = Command<LockG> "lock::(name:*)::(cfg:*)"

let lockRule =
    Lock.Rule <| fun { name = name; cfg = cfg } -> recipe {
        do! trace Message "lock %s %s" name cfg
    }
let wantLocks = need [ Lock.Format { name = "Core"; cfg = "Release" } ]
```

A command runs every time it is demanded, so an explicit `alwaysRerun ()` goes. A phony `=>`
rule without `alwaysRerun ()` does not rerun by itself; if a rule relied on that, it changes
under `Command`, so keep such a rule as `=>` for now.

### The future of `=>`

The phony operator `=>` is planned to be phased out in favour of `Command`, so that "phony" and
its semantics can be retired. This is an announcement, not a deprecation: `=>` compiles without a
warning in this release and does not change. `command "name" { }` (already always-run) stays.

### Escaping and the `\` separator

The same release reserves `( ) [ ] { } |` in rule patterns and fileset masks and makes `\` the
escape; the full rules are in [Patterns, masks and paths](overview.md#patterns-masks-and-paths).
What to change in a script:

| Before | After |
|---|---|
| `"out/a(1).txt" ..> ...` (the rule reads `(1)` as a regex group and matches `out/a1.txt`) | `@"out/a\(1\).txt" ..> ...` |
| `!! "src/a[test.c"` (literal in a mask, now warns) | `!! @"src/a\[test.c"` |
| `!! "lib[x].dll"` (now means `libx.dll`, warns) | `!! "libx.dll"`, or `!! @"lib\[x\].dll"` for literal brackets |
| `!! @"src\*.cs"` (backslash as the separator, warns) | `!! "src/*.cs"` |
| `!! "odd{dir}/*.c"` (literal braces, warns) | `!! @"odd\{dir\}/*.c"` |
| a group in a mask, `!! "src/(x:*)/*.cs"` (warns) | masks have no groups: `!! "src/*/*.cs"`, or `PatternAst.glob` from a rule pattern |

In this release an unescaped reserved character keeps its old meaning and logs one warning per
pattern ("warning: pattern '...': ..."); the warnings become errors in a later major release.
Three changes are **intentional breaks**, silent in the sense that the script still runs:

- a `\` before a reserved character is an escape: `src\(x)\*.cs` now means the directory `src(x)`
  (with `\` as a separator it was `src/(x)/*.cs`);
- `[c]` in a mask include means the literal `c`;
- `sub[1]` in a mask means `sub1`.

Typed patterns follow the same grammar, so `Target<G> @"out/a\(1\)/(name:*).txt"` escapes the
same way; an unclosed `(name:` in a typed pattern is an error at script start (the string API
does not warn about it).
