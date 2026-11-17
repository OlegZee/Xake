# The rule header (Xake 5.0)

A file rule may start with a **header**: declarations the engine reads when the script is loaded,
without running the rule's body. The header says what else the rule writes (`also`), what it
always needs (`needs`), which lock it takes (`requires`) and how its outputs take part in content
cut-off (`noContentCutoff`). The body after it is the recipe as before.

```fsharp
"out/(n:*).dll" ..> recipe {
    also  "out/(n:*).pdb"                // a side output of this rule
    also  "out/(n:*).xml"
    needs "obj/(n:*).rsp"                // always needed: built before the body starts
    needs ["src/(n:*)/**/*.cs"; "obj/(n:*).res"]
    requires cscLock                     // taken by the engine around the body
    // ---- the body, as before
    let! m = getRuleMatches ()
    do! need [ ... ]                     // a dynamic need: still allowed
}
```

The header lives in `recipe { }`, so every file-rule form takes it: `..>`, `*..>`, `..?>`,
`target "p" { }`, `targets [..] { }` and the typed `X.rule { }`; the typed function form takes it as
a value (`X.RuleWith h <| fun m -> ..`, see [Typed targets](#typed-targets)). Phony rules (`=>`,
`command`, `Command<_>`) and `X.Rule <| fun m -> recipe { }` take no header: giving them one is a
compile error.

## Operations

| Operation | Meaning |
|---|---|
| `also "pattern"` | another output of the rule, besides the main one (see [Side outputs](#side-outputs-also)) |
| `needs "pattern"`, `needs ["p1"; "p2"]` | inputs every run needs (see [Static needs](#static-needs-needs)) |
| `requires lock`, `requires (lock, n)` | a resource the engine holds while the body runs (see [Locks](#locks-requires)) |
| `noContentCutoff` | every rebuild of this rule's outputs counts as a change for its dependents (content cut-off, Xake 5.0) |
| `contentCutoff` | the default; exists for symmetry |
| `including h` | the items of a shared header `h` (see [Shared headers](#shared-headers-ruleheader-and-including)) |

## Groups

A **group** is a named part of the rule's pattern, such as `(n:*)` in `out/(n:*).dll`. In the
header you write the same group name, and the engine fills in what the target matched: for
`out/Core.dll`, `needs "obj/(n:*).rsp"` is `obj/Core.rsp`. A header pattern may use only groups of
the rule's main pattern; another name is an error when the script loads.

A condition rule (`..?>`) has no pattern, so it has no groups: its `needs` are plain paths or
filesets, and it cannot have an `also` (there is nothing to name the side output from).

Header arguments are evaluated when the script loads, so they are plain values: a computed
string (`needs (sprintf ..)`) is fine, a value known only from the match (`m.["n"]`) is not
available there. Anything computed from the match stays a `need` in the body.

## A header needs a body

A file rule must have a body that writes its target: `"out/x" ..> recipe { needs "a" }` (a header
and nothing else) is an error when the script loads. To run inputs before something, put them in
the `needs` of the rule that uses them, or use a phony rule (`"prepare" => recipe { do! need [..] }`).
An explicit empty body (`recipe { needs "a"; () }`) is a body.

## The header comes first

Header operations must come before the body. This is checked by the compiler:

| Written | Result |
|---|---|
| a header operation after `do!` | error FS3095: "The rule header (also, needs, ...) must come before the body" |
| after `let!`, after `let`, inside `for` | error FS0001 (the message points at the bound name) |
| after `if`, `match`, `try`, `use`, `return`, a `for` loop | error FS3086 |

A plain statement before the header (`printfn ..`) is allowed and runs when the script loads.
`if`/`match`/`try` are fine in the body, after the header.

## Static needs: `needs`

- All `needs` of a rule (every line, every element of a list) are **one set**: they are requested
  together, before the body starts. Their order, and how they are split into lines and lists,
  does not matter. One may depend on another; the engine builds them in the right order.
- After the groups are filled in, a pattern **with wildcards** is a fileset (an empty fileset is
  fine); a pattern **without wildcards** is one target, resolved like `need`: a rule, a phony
  target (`needs "clean"`), or an existing file. A single target that resolves to nothing fails
  before the body runs.
- A failure of a static need is an ordinary build error; the body does not run.
- Static needs are recorded like the body's `need`s. A fileset is recorded as the set of files it
  matched (a file added or removed later is a change) and each file as a dependency; a matched
  file that has a rule is built.
- A fileset is matched against the files **on disk** when the rule runs: it does not see outputs
  of other rules that have not been built yet. To depend on generated files, name them (`needs
  "gen/(n:*).cs"`) or need the rule that makes them.

**For content cut-off through a rule's output, name it in `needs`.** A static `needs` is brought up
to date before the rule is decided, so a dependee rebuilt with the same bytes does not rerun the
rule. A rule's output needed in the body is not built to decide the rule: if it is out of date, the
rule reruns (as in Xake 4.0).

**Only the header is static.** A `need` anywhere in the body, including inside a helper recipe
the body calls, is dynamic: it runs when the body reaches it, as in Xake 4.0. It is correct, but the
engine does not know of it before the body runs.

## Side outputs: `also`

`also "out/(n:*).pdb"` declares another output of the rule. It has the same named groups as the
main pattern and nothing a match cannot fill in (XP013, XP014, as for `*..>`).

- `need ["out/A.pdb"]` builds the rule of `out/(n:*).dll` for `out/A.dll`.
- The rule is out of date when any of its outputs is missing.
- `also` and `*..>` are the same thing: a rule with several outputs, the first one main.
- A file the body writes but does not declare stays a plain file for its dependents.

## Locks: `requires`

`requires lock` holds one unit of the resource while the body runs; `requires (lock, n)` holds `n`.
A rule names each resource once: the same resource twice (two `requires`, `requires` and
`|> requiring`, or an included header that brings it) is an error when the script loads, naming the
rule and the resource. So are two different resources with the same name on one rule.
For each target the engine:

1. builds the static `needs`, without the lock;
2. decides whether the target is up to date, without the lock; an up-to-date target never takes it;
3. takes the lock, yielding its CPU slot while it waits; several are taken in one order for the
   whole build (by name, then by creation), so two rules naming the same locks cannot deadlock;
4. runs the body and releases the lock, also when the body fails.

While the lock is held, a `need` of a target that has a rule fails, as under `withResource` (it
could deadlock; see [resources.md](resources.md)); static `needs` are built before the lock, so
they are the way for a locked rule to depend on other rules. `rule |> requiring lock` is the same
as `requires lock` in the header for a file rule; on a phony rule it locks the whole recipe, as in
Xake 4.0.

## A recipe with a header is a rule body

`recipe { needs ..; .. }` is a value of its own type, which only the rule forms accept. Using it
inside another recipe (`do!`, `let!`, `match!`, passing it to a function that takes a recipe) is a
compile error, so a header can never be silently lost in a nested recipe. A recipe without a header
is used as before.

A body with a header can be shared by several rules (a value needs its type written, as any
recipe value does that nothing else fixes):

```fsharp
let compile : Headed<ExecContext> = recipe {
    needs "obj/(n:*).rsp"
    let! m = getRuleMatches ()
    do! cscBody m.["n"]
}
"out/(n:*).dll" ..> compile
"tests/(n:*).dll" ..> compile
```

## Shared headers: `ruleHeader` and `including`

A header can be a value of its own, so a library or a script helper can provide the static part of
a rule and the rules can include it:

```fsharp
/// The static part of a C# compile rule: the response file and the compiler lock.
let cscHeader = ruleHeader ["n"] {
    needs "obj/(n:*).rsp"
    requires cscLock
}

"out/(n:*).dll" ..> recipe {
    including cscHeader              // needs obj/<n>.rsp, takes cscLock when the target runs
    also "out/(n:*).pdb"
    let! m = getRuleMatches ()
    do! cscBody m.["n"]
}
```

- `ruleHeader ["n"]` lists the groups the header uses: they are the groups of **the rule that
  includes it**, filled in from that rule's match. `ruleHeader [] { .. }` is a header without
  groups.
- Two checks, both when the script loads:
  - in the header, every group a pattern uses is declared and every declared group is used;
  - in the rule, `including h` needs every group `h` declares; otherwise the error names the rule,
    the header and the missing group, e.g. `rule "out/(name:*).dll" includes ruleHeader [n]
    (needs "obj/(n:*).rsp"), which uses the group n; the rule has the groups [name]`.
- A shared header takes the same operations as a rule header (`also`, `needs`, `requires`,
  `noContentCutoff`, `contentCutoff`). `including` is a header operation itself: it comes before the
  body, may appear several times, and merges in place (lists append, a later cut-off setting wins).
- A helper that needs inputs computed from the match provides a recipe for them too: the header is
  only what is known when the script loads.

## Typed targets

```fsharp
type AsmG = { n: string }
let Asm = Target<AsmG> "out/(n:*).dll"

Asm.rule {
    also  "out/(n:*).pdb"
    needs "obj/(n:*).rsp"
    let! m = matched
    do! need [ m.n ]
}
```

The function form `Asm.Rule <| fun m -> recipe { .. }` takes no header: the header would be inside
the function, which runs only when the rule runs. Give it the header as a value with `RuleWith`, or
use `Asm.rule { }`:

```fsharp
let cscHeader = ruleHeader ["n"] { needs "obj/(n:*).rsp"; requires cscLock }
Asm.RuleWith cscHeader <| fun m -> recipe { do! cscBody m.n }
```

The header's groups must be groups of the target's pattern, checked when the script loads, as for
`including`; the function is not called then.
