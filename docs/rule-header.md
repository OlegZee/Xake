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
`target "p" { }`, `targets [..] { }`, and the typed `X.rule { }` and `X.Rule <| fun m -> recipe { }`.
Phony rules (`=>`, `command`, `Command<_>`) take no header: giving them one is a compile error.

## Operations

| Operation | Meaning |
|---|---|
| `also "pattern"` | another output of the rule, besides the main one (see [Side outputs](#side-outputs-also)) |
| `needs "pattern"`, `needs ["p1"; "p2"]` | inputs every run needs (see [Static needs](#static-needs-needs)) |
| `requires lock`, `requires (lock, n)` | a resource the engine holds while the body runs (see [Locks](#locks-requires)) |
| `noContentCutoff` | every rebuild of this rule's outputs counts as a change for its dependents (content cut-off, Xake 5.0) |
| `contentCutoff` | the default; exists for symmetry |

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
- Static needs are recorded like the body's `need`s.

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
For each target the engine:

1. builds the static `needs`, without the lock;
2. decides whether the target is up to date, without the lock; an up-to-date target never takes it;
3. takes the lock (several in the order of their names, so two rules naming the same locks cannot
   deadlock), yielding its CPU slot while it waits;
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

A body with a header can be shared by several rules:

```fsharp
let compile = recipe { needs "obj/(n:*).rsp"; let! m = getRuleMatches () in do! cscBody m.["n"] }
"out/(n:*).dll" ..> compile
"tests/(n:*).dll" ..> compile
```

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

`Asm.Rule <| fun m -> recipe { needs ..; .. }` works too: the engine calls the function once when
the script loads, with every group empty, to read the header (the body is not run). A header
argument that uses `m` there fails with a message naming the rule.
