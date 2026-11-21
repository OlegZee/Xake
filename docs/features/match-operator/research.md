# Match operator: a short way to read a rule's groups (research, 2026-11-18)

Question from the owner: replace `let! n = getRuleMatch "n"` / `let! m = getRuleMatches ()` +
`m.["n"]` with a prefix operator, ideally usable without `let!`
(`do! need [ "obj/" + OP"n" + ".rsp" ]`). Base `dev` at `fa5b33b`. The spikes (`dotnet fsi` scripts against the Debug `Xake.dll`) were
throwaway and are not kept; the `spike/*.fsx` names below are for reference only.

## Facts checked

**Prefix operator names** (`spike/lex.fsx`, `spike/op2.fsx`, F# in SDK 11.0.100):

| Candidate | Defines | Notes |
|---|---|---|
| `$"n"` | no | `$` is reserved in operator names (FS0035); `$"..."` is interpolation. Infix `$` alone works (Xake's `RuleBody $`). |
| `!"n"` | yes | shadows FSharp.Core deref (deprecated for ref cells); next to Xake's `!!"*.fs"` it reads as a fileset |
| `!!` | taken | Xake fileset (`Fileset.fs:134`) |
| `~+` / `+"n"` | taken | Xake: `~+ dir` is an empty fileset (`Fileset.fs:140`) |
| `~-` / `-"n"` | yes | shadows unary minus |
| `%"n"`, `%%"n"` | yes | `(~%)` is the quotation splice: shadowing breaks `<@ %x @>` |
| `&"n"`, `&&"n"` | yes | `&` is address-of; reads poorly |
| `~~"n"` | yes | free in FSharp.Core (`~~~` is bitwise not) |
| `!?"n"`, `!@"n"`, `!^"n"`, `!%"n"`, `!*"n"`, `!~"n"` | yes | free; any `!` + op chars except `!=`; `!$` refused |
| `~@`, `~^`, `~?`, `~$` | no | FS1208 / FS0035 |

All accepted forms parse inside expressions: `"obj/" + ~~"n" + ".rsp"`, `f ~~"n"`. Inside an
interpolated string the quoted name needs a triple-quoted outer string: `$"""obj/{~~"n"}.rsp"""`.

**Return type.** An operator with no context can return a plain `string` only by reading an
ambient value. A CE cannot intercept an ordinary expression nested inside `need [ ... ]`: no
`Bind` overload, custom operation or SRTP trick turns `"obj/" + ~~"n"` into a bind. The only
builder-level route would be a template type (`~~"n"` returns `Tpl`, `string + Tpl` overloads,
`need` accepts `Tpl`) - it works only where the API accepts `Tpl` (not `sh`, `trace`, `writeText`),
so it is not considered further.

**`ctx.RuleMatches`** holds numbered groups too (`0`, `1`, ... next to `n`); an error message
listing names should drop the numeric ones.

**Unknown group.** `getRuleMatch` returns `""` silently today. Any string-named form can only fail
at run time (a pattern is a string; the body is opaque until it runs - `RuleHeaderTests`: loading
a script runs no body). Compile-time checks exist only with typed targets (`m.n`). A registration
check is possible only for the header (`needs`) - it already resolves groups there.

## Options (all verified in fsi)

**A. `~~"n"` returns `Recipe<_, string>`** (`spike/real.fsx`). `let! n = ~~"n"`. Strict: unknown
name fails the task with `no group 'name' in the rule match (groups: n)`. Pure, no engine change.
Gain over `getRuleMatch "n"` is only length; still one `let!` per group.

**B. Ambient: `!?"n"` (or `~~"n"`) returns `string`** (`spike/real.fsx`). The engine sets an
`AsyncLocal<Map<string,string>>` with the task's matches before the body; the operator reads it.
`do! need [ "obj/" + !?"n" + ".rsp" ]` works, no `let!`. 40 parallel rules on 8 threads with
awaits between reads: 0 mismatches. Outside a rule: a clear error. Costs: hidden context (a value
read in a helper called from a recipe, a closure run later, or a `Task` started outside the flow
may see another rule's matches or none); F# async does not restore ExecutionContext, so the
engine must scope it (set per task, clear in `finally`); a different style from everything else in
Xake (all context goes through the recipe).

**C. `m?n` - the `(?)` dynamic operator on the match map.** `let! m = getRuleMatches ()` once,
then `m?n` anywhere, including single-quote interpolation `$"obj/{m?n}.rsp"` (no nested quotes).
Strict lookup. Engine-free: one `(?)` for `Map<string,string>` (or a `RuleMatch` wrapper type, to
not claim `?` for every map).

**D. C plus a lambda body: `"out/(n:*).dll" ..> fun m -> recipe { ... m?n ... }`**
(`spike/lambda.fsx`). One more `RuleBody.($)` overload for `Map<string,string> -> Recipe`; no
`let!` at all; the same shape as `X.Rule(fun m -> recipe { .. })`. A header in such a body needs a
fourth overload (`Map -> Headed`) - and header items cannot depend on `m` anyway (resolved before
the body), so the recipe-with-header form should keep `let! m = matched`/`getRuleMatches ()`.

Recommendation in the summary to the owner: C (+ D if the lambda form is liked); A as a cheap
alias; B only if "no `let!`, no `m`" is worth an ambient context in the engine. Independently:
make `getRuleMatch` strict (5.0 allows breaking changes).

## Round 2 (owner: "D is great"; look at quotations, many groups in one line, `fun m ->` as the last line)

**The owner's literal last line** `recipe { ...; fun m -> do! need [..] }` cannot compile: `do!`
inside a lambda is outside the CE (FS0750), and a bare lambda as the last line must be `unit`
(FS0193). What works (`spike/lastline.fsx`, `lastline2.fsx`): `return! fun m -> recipe { .. }`
with a `ReturnFrom(Map -> Recipe)` overload, after `do!` steps and after a header; `yield fun m ->`
compiles too.

**Group names from the lambda's parameter names** - many groups in one line, no `m` at all:
`"out/(cfg:*)/(n:*).dll" ..> fun cfg n -> recipe { .. }`.

- Quotation (`spike/names.fsx`, `names2.fsx`): a method parameter
  `[<ReflectedDefinition(true)>] body: Expr<string -> string -> Recipe<..>>` - the caller writes a
  plain lambda and the method gets the compiled value (`WithValue`) and the tree with the names.
  Curried and tupled lambdas both give `["cfg"; "n"]`; a named function `build` is eta-expanded by
  the compiler and gives `build`'s parameter names. Overloads by arity resolve. **But only for a
  method call** (`Rule.file("..", fun cfg n -> ..)`): an operator does not auto-quote (FS0002).
- Reflection on the closure (`spike/generic.fsx`): the F# closure class keeps parameter names on
  its widest `Invoke`. `..>` as a plain generic function (`body: 'B`, dispatched when the script
  loads: a recipe, or an F# function of the groups) takes `fun n ->`, `fun cfg n ->`,
  `fun c a b ->` (any order), a named function, a plain recipe - no parentheses needed. The body
  is called through the widest `Invoke` with values by name. Parameter names are checked against
  the pattern **when the script loads**: `rule out/(cfg:*)/(n:*).bad: parameter 'nme' is not a group
  of the pattern`. Checked in fsi `--optimize+`/`--optimize-` and in a compiled Release build
  without PDB: same output.
  - The SRTP `RuleBody $` dispatcher (today's `..>`) cannot take these lambdas without
    parentheses: with more than one function overload, or a generic one, a curried lambda gives
    FS0002 (`..> (fun cfg n -> ..)` in parentheses works). Hence the plain generic `..>`; cost:
    a wrong body type is a load-time error, not a compile error.
  - Unknowns: relies on how the F# compiler lays out closures (stable for years, not specified);
    a partially applied function (`..> build "x"`) or an unused `_` parameter (`_arg1`) - the
    latter fails the load-time check, which is the right outcome; parameter types are inferred
    (`obj` when only interpolated) - values are passed as strings either way.

**Inside a recipe with a header** (`spike/inrecipe.fsx`), all checked against a run:

- `let! cfg = group` - `Bind(Group, f)` reads `f`'s parameter name: the variable's name is the
  group's name. One group per line; `and!` loses the names (the continuation takes `_arg1`).
- Active pattern, no reflection: `let! (G "cfg" cfg & G "n" n) = getRuleMatches ()`.
- `return! fun n cfg -> recipe { .. }` after `needs ..` - the owner's "last line", by name.
