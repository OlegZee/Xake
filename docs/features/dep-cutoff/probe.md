# Probe: the rule header on the 4.0 builders (step 0)

Date: 2026-11-17. Checked with `dotnet fsi` (SDK 11.0.100) against the built 4.0 `Xake.dll`
(`dev` at `1837856`). The prototype is in [spike/](spike/): `Header.fsx` (the builder),
`positive.fsx`, `lazy.fsx`, `typed.fsx`, `script.fsx` (inside a real `xakeScript`) and
`negative.sh` (one file per misplaced operation). Build `src/core -c Release` first, then
`dotnet fsi --nologo spike/positive.fsx` and so on.

## The 4.0 rule builders

| Form | Builds | Body type |
|------|--------|-----------|
| `"p" ..> recipe {..}` | `FileRule` | `Recipe` (operator over a finished recipe) |
| `["p"; "q"] *..> recipe {..}` | `MultiFileRule` | `Recipe` |
| `"n" => recipe {..}` | `PhonyRule` | `Recipe` |
| `cond ..?> recipe {..}` | `FileConditionRule` | `Recipe` |
| `target "p" {..}` | `FileRule` | builder (`RuleBuilder`, inherits `RecipeBuilder`) |
| `targets ["p"; "q"] {..}` | `MultiFileRule` | same `RuleBuilder` class |
| `command "n" {..}` | `PhonyRule` + `AlwaysRerun` | same `RuleBuilder` class |
| `X.Rule <\| fun m -> recipe {..}` (typed form D) | `FileRule`/`MultiFileRule` (`Target`), phony (`Command`) | lambda |
| `X.rule { let! m = matched ..}` (typed form B) | as form D | `TypedRuleBuilder<'G>` |
| `phony "n" (recipe ..)`, `rule (..)` | ops of `xakeScript` | `Recipe` |

The operators take a finished `Recipe`; only `target`, `targets`, `command` and the typed
`X.rule` are rule-level builders, and they know the pattern before the body.

## Chosen syntax (proposal)

The header goes on the **pattern-carrying builders**: `target` and the typed form B `X.rule`.
No new builder name, no change to `..>`.

```fsharp
// untyped
target "out/(n:*).dll" {
    also  "out/(n:*).pdb"
    also  "out/(n:*).xml"
    needs "obj/(n:*).rsp"               // or needs ["obj/(n:*).rsp"; "obj/(n:*).res"]
    noContentCutoff
    let! m = getRuleMatches ()           // body, as today; dynamic need stays
    do! need [ ... ]
}

target "out/all.stamp" { needs "out/*.dll" }          // header-only rule
target "out/(n:*).txt" { do! writeText ... }          // no header: unchanged from 4.0
"out/(n:*).txt" ..> recipe { ... }                     // operators: unchanged, never a header

// typed (form B)
type AsmG = { n: string }
let Asm = Target<AsmG> "out/(n:*).dll"
Asm.rule {
    also  "out/(n:*).pdb"
    needs "obj/(n:*).rsp"
    let! m = matched
    do! need [ m.n ]
}
```

- Operations: `also <pattern>`, `needs <pattern>` and `needs <pattern list>` (custom
  operations may be overloaded; checked), `noContentCutoff`, `contentCutoff`.
- Builders that get the header: `target` and `Target<'G>.rule` / `Target<'G,'O>.rule`.
  Not: `targets` and `*..>` (header on multi-output is out of scope), `command`, `=>`,
  `Command<'G>.rule` (phony), `..?>`, `delegated` (wraps a finished rule, keeps its header),
  `recipe`. So `target` gets its own builder class (today one `RuleBuilder` serves `target`,
  `targets` and `command`), and `Target.rule` a typed builder class separate from
  `Command.rule`'s.
- Typed form D (`X.Rule <| fun m -> ..`) gets no header: form B is already "a builder around the
  lambda" (the brief's requirement), since `matched` is a bind inside it. If a form D header is
  wanted later, `X.Rule(header, body)` with a header value is the natural shape.
- Names: **`needs`**, not `requires`: Xake already has `requiring` (resource lock, `|> requiring
  lock`), and `requires` next to it reads as the same thing. `needs` vs `need` is safe in practice:
  `need` in header position is a type error (FS0001), `needs` in the body is FS3095 with our message.

### Builder shape (as prototyped)

```fsharp
type FileRuleBuilder(pattern) =
    inherit RecipeBuilder()
    member _.Yield(()) = Header.Empty
    [<CustomOperation("also")>]  member _.Also (h: Header, p: string) = ..
    [<CustomOperation("needs")>] member _.Needs(h: Header, p: string) = ..
    [<CustomOperation("needs")>] member _.Needs(h: Header, ps: string list) = ..
    [<CustomOperation("noContentCutoff")>] member _.NoContentCutoff(h: Header) = ..
    member _.For(h: Header, body: unit -> Recipe<ExecContext, unit>) = Headed (h, recipe.Delay body)  // lazy!
    member _.Delay(f: unit -> Header) = f ()
    member _.Delay(f: unit -> Headed) = f ()
    // the error message for a header op after do! (see below)
    [<CompilerMessage("The rule header (also, needs, noContentCutoff) must come before the body: move it above the first let!/do!.", 3095, IsError = true)>]
    member _.Bind(m: Recipe<'a,'b>, f: 'b -> Header) : Header = failwith "unreachable"
    member _.Run(Headed (h, r)) : ExecContext Rule = ..   // header + body
    member _.Run(h: Header)     : ExecContext Rule = ..   // header-only
    member _.Run(r: Recipe<ExecContext, unit>) : ExecContext Rule = ..   // no header, as 4.0
```

The typed builder is the same plus `Bind(Matched, 'G -> Recipe)` (as `TypedRuleBuilder` today).

## Results

Positive (all compile and give the expected header): header + body with `let!`, `do!`, `for`,
`let`, `if`, `match`, `try` in the body; header-only; no header; a body starting with `for`;
`needs` with a list; typed form B with and without header; both inside a real `xakeScript { }`
next to `..>` rules.

Negative (`spike/negative.sh`):

| Header op placed | Result |
|------------------|--------|
| after `do!` | FS3095 with our message (via the `CompilerMessage` overload); without it FS0041 "No overloads match for method 'Bind'" |
| after `let!` | FS0001 "expected unit but here has type Map<..>" (at the bound name) |
| after `let x = ..` | FS0001 (same, at `x`) |
| inside `for` | FS0001 |
| after `if`/`match`/`for`/`use`/`return` | FS3086 |
| `need [..]` statement before a header op | FS0001 |
| plain statement (`printfn`) before a header op | **compiles**; runs at registration |

Registration does not run the body: `target "p" { also "x"; failwith "boom"; do! .. }` registers
(`spike/lazy.fsx`), once `For` delays the body (the first prototype called `body ()` in `For` and
threw at registration).

## Differences from the brief

1. **Error code.** The brief says an operation after `let!` or `do!` is FS3095. On the real
   builders (which inherit `RecipeBuilder`'s `Bind` overloads) it is FS0041 after `do!` and FS0001
   after `let!`/`let`/inside `for`. All are compile errors, so the guarantee holds; only the code
   and message differ. An error-marked `Bind(Recipe, _ -> Header)` overload turns the `do!` case
   into FS3095 with a clear message; the `let!`/`let`/`for` cases come from the variable-space
   typing and cannot be intercepted (their message points at the bound name). The FS3095 the
   brief saw is probably the one below (item 3), or a prototype without `RecipeBuilder`.
   Acceptance "FS3095" should read "does not compile" (tests use the compiler service or an
   `fsi` run and assert an error).
2. **`For` returns the header and body, not a `Recipe`.** The brief's `For: Header * (unit ->
   Recipe<'a>) -> Recipe<'a>` would drop the header; `For` returns `Headed (header, recipe)`,
   with `Delay` overloads for `Header` and `Headed`. `For` must wrap the body lazily, or a
   statement before the first bind runs at registration.
3. **`rule { }` as the builder name does not work inside a script.** `"p" ..> rule { .. }` inside
   `xakeScript { }` fails with FS3095 "'rule' is not used correctly. This is a custom operation":
   `rule` is a `xakeScript` operation. The brief's illustrative shape is possible under another
   name (`"p" ..> header { .. }` compiles with an SRTP `..>` overloaded on `Recipe` and the header
   type; `spike/Header.fsx`), but it makes `..>` inline/SRTP for every use and duplicates what
   `target` already is. Proposal: header on `target` and `X.rule` only.
4. **Header arguments are not restricted to literals.** `also (sprintf ..)` compiles and is
   evaluated at registration; a plain statement before the header also runs then. Only `let`,
   binds and control flow are excluded. Docs say "evaluated at registration", the brief's
   "a throwing argument fails registration" holds.
5. **An output edited outside the build: 4.0 does not detect it.** `getChangeReasons` checks only
   that every `BuildResult.Targets` file exists (a missing output of a multi-output rule already
   reruns it, as the brief wants); output stamps are not recorded, so a hand-edited `out/A.dll`
   with an older or newer time is not a change for its own rule. The brief's "do what 4.0 does for
   its own target" is "nothing"; the Acceptance item "an output edited by hand: rebuilt" is new
   5.0 behaviour that comes with the output records (step 3).
6. **Public `Rule` DU.** `FileRule of string * Recipe` is matched outside the engine
   (OlegZee/Xake.Dotnet `hermetic.tests/SignTests.fs`: `FileRule (mask, _)`,
   `DelegatedRule (FileRule (mask, _), _)`). Carrying a header changes the case shape; Xake.Dotnet
   follows with its 5.0 release anyway. Proposal for steps 1-2: one file case with a header whose
   outputs list is main-first (`also` and `*..>` both build it), `MultiFileRule` folded into it;
   decided with the code, reported in the PR.

## Not checked here

- Compile time and error-message quality of the overloaded `Delay`/`Run` inside big scripts.
- IDE tooltips / colouring of the new keywords.
