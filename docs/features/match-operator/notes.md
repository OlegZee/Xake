# Match operator: a short way to read a rule's groups (research, 2026-11-18)

Question from the owner: replace `let! n = getRuleMatch "n"` / `let! m = getRuleMatches ()` +
`m.["n"]` with a prefix operator, ideally usable without `let!`
(`do! need [ "obj/" + OP"n" + ".rsp" ]`). Base `dev` at `fa5b33b`. Spikes in `spike/`
(`dotnet fsi`, against `src/core/bin/Debug/netstandard2.0/Xake.dll`).

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
