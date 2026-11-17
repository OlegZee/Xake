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

## Owner's questions on the spike (2026-11-17)

The owner approved the syntax (`target` / `X.rule`; `also`, `needs`, `noContentCutoff`) and the
merge of `FileRule` and `MultiFileRule` (item 5), and asked four questions. Answers, each with a
proposal. Checked in a scratch worktree (not on this branch): header operations added to the real
`RecipeBuilder`, `..>` made an SRTP operator over `Recipe` and the headed type.

### 3. The header in `recipe` (answered first: it changes 1 and 2)

**It works, and it is cheap.** With `Yield`/`also`/`needs`/`noContentCutoff`/`For`/`Delay`
overloads on `RecipeBuilder` itself and `..>` accepting both a `Recipe` and a headed recipe:

- `src/core` builds with 0 warnings; **`src/tests` compiles unchanged and passes (1744 passed,
  10 skipped)**: no existing recipe, helper function or generic recipe code needed a change.
- `"out/(n:*).dll" ..> recipe { also ..; needs ..; do! .. }` works inside `xakeScript`, next to
  plain `..> recipe { }` rules; a header-only `recipe { also "x" }` works.
- A headed body is a value: `let body = recipe { needs "common.rsp"; .. }` then
  `"a/*.dll" ..> body` and `"b/*.dll" ..> body` (the header is checked against each rule's pattern
  at registration).
- Misuse stays a compile error: `do! recipe { also .. }` inside another recipe (FS0041 "No
  overloads match for method 'Bind'"; can get a clear `CompilerMessage` text, as the `do!` case);
  header op after `do!` FS3095 with our text; a headed recipe given to `=>` / `*..>` today FS0193
  (each rule form decides by its overloads).
- The body stays lazy (`For` delays it): a throwing first statement does not fail registration.
- A local named `also`/`needs` inside a recipe still works (`let also = "x" in ..`): custom operation
  names are keywords only at statement start.

Costs, honestly:
- `..>` (and `*..>`, `..?>`) become `inline` SRTP operators. A wrong right-hand side reads "No
  overloads match for method 'op_Dollar'" instead of "expected Recipe"; fixable in part with a
  documented message on the witness type, not fully.
- `also`/`needs`/`noContentCutoff` become keywords at statement start in **every** recipe, also in
  helpers that are never rule bodies (where a header is a type error at the use site, not at the
  `recipe { }`).
- The `recipe` builder's `Delay` gets two overloads; no inference regressions in the test suite,
  IDE behaviour not checked.
- Typed form D, `X.Rule <| fun m -> recipe { also ..; .. }`: the header is inside the lambda. It can
  be read at registration by calling the lambda once with `X.Any` (all groups null): the body is
  lazy, so only the header operations run; a header argument that uses `m` fails registration with
  a clear message. Works, but it is a trick; form B (`X.rule { }`) needs none.

**Proposal: one syntax, the header lives in `recipe`.** Every file-rule form accepts a headed
recipe: `..>`, `*..>`, `..?>` (with the limits in 1), `target`, `targets`, `X.rule`, `X.Rule`
(with the `X.Any` call). Phony forms (`=>`, `command`, `Command.rule`) reject it at compile time.
`target`/`X.rule` keep working as approved, because they inherit `RecipeBuilder`; the approved
syntax is a subset, nothing is lost. This supersedes "header only on `target`/`X.rule`".

### 1. `targets` / `*..>` and the condition rule `..?>`

- **`targets` / `*..>`: no extra cost after the merge (item 5).** A multi-output rule becomes the
  same file-rule case with the outputs list (first = main); `also` appends to it; `needs`,
  `noContentCutoff`, `requires` work the same. Proposal: yes, in 5.0 (removes an out-of-scope
  line from the brief).
- **`..?>` (no pattern, no groups):**
  - `needs` of paths and filesets **without groups**: yes (nothing to substitute); a group in a
    `needs` of a condition rule is a registration error.
  - `noContentCutoff` / `contentCutoff`: yes, they need no pattern.
  - `requires <resource>`: yes.
  - `also`: **no**, registration error. A condition rule matches many unrelated paths; a side output
    cannot be named without groups, and a literal `also` would make every matched file produce the
    same path.
  - Rule lookup is unchanged (condition rules rank below every pattern rule).
- Phony: out, as agreed.

### 2. `requires <resource>`

4.0: `requiring lock rule` wraps the whole recipe: yields the CPU slot while waiting
(`Scheduler.withYieldedSlot`), holds 1 unit for the whole body, and while held a `need` of a
target that has a rule fails (`HeldResources`, deadlock guard; `allowNeed` lifts it).

Proposal:
- `requires lock` (1 unit) and `requires (lock, n)`; several allowed.
- Order: **static `needs` first, then acquire, then the body, release at the end** (also on
  failure). The `needs` are built without holding the lock, so they never hit the deadlock guard:
  today a locked rule cannot `need` a rule target at all, with `needs` in the header it can.
  The guard stays for dynamic `need` in the body, as with `requiring`.
- Several resources are acquired in a fixed order (by resource name), so two rules naming the same
  locks in different orders cannot deadlock.
- Waiting yields the CPU slot, as `requiring` does.
- `|> requiring lock` stays (it also serves phony rules); on a headed rule it is the same as a
  `requires` added to the header. `requires` and `requiring` naming the same concept is now
  intended.
- Content cut-off verdict (step 3) runs before acquiring: an up-to-date target never takes the lock.
- Implementation note: `Resource` (and `WorkerPool`) compile after `RecipeBuilder.fs`; neither
  depends on `Recipe`, so they move before it.

### 4. Rules over a list of masks

With the merged representation this is cheap, but it is a different thing from outputs: several
**alternative main patterns** for one body are several rules sharing one recipe. 4.0 already
writes it in a script (`for m in masks do yield m ..> body`), and with the header in `recipe` the
shared body carries its header too. A sugar (`masks |..> body`, expanding to one rule per mask)
is a one-liner on top. Proposal: later, not in 5.0; noted in the tracker.

## Second round (2026-11-17): owner's follow-ups

Decisions on the answers above are in brief.md "Decisions". Checked against
[spike/recipe-header.patch](spike/recipe-header.patch) (now also with `ruleHeader`-style header
values and `including`, see (c)); the nesting cases are re-runnable with
[spike/nested.sh](spike/nested.sh).

### `needs` with a list: order does not matter

`needs "a"`, `needs ["b"; "c"]` and any number of `needs` lines are one set. **All of them are
requested together before the body starts; their order, and how they are split into lines and
lists, does not matter.** Dependencies between them are ordinary graph edges, so one `needs` may
itself depend on another and the engine sorts it out. A failure of any is an ordinary build error
(the rule's body does not run), not a speculative one. A duplicate is requested once. This goes
into the brief (Part 2, "Static needs") and the docs.

### Groups: one plain sentence for the docs

For the docs plan: "A group is a named part of the rule's pattern, such as `(n:*)` in
`out/(n:*).dll`; in the header you write the same group name and the engine fills in what the
target matched. A condition rule (`..?>`) has no pattern, so it has no groups: its `needs` are
plain paths or filesets, and it cannot have an `also` (there is nothing to name the side output
from)."

### `requires`: confirmed, and the verdict comes before the lock

Yes, that is the proposal: `requires lock` / `requires (lock, n)` is a header operation; the engine
takes the lock itself, the body does nothing. For one target the order is:

1. Static `needs` are brought up to date (they are needed for the verdict anyway), without the lock.
2. The up-to-date check / content cut-off verdict (cheap checks, then the recorded dynamic
   dependencies, speculatively), without the lock.
3. Up to date: done, **the lock is never taken**.
4. Out of date: acquire (yielding the CPU slot while waiting; several resources in name order),
   run the body, release (also on failure). A dynamic `need` of a rule target in the body still hits
   the 4.0 deadlock guard, as under `requiring`.

### Nested recipes and the header

**(a) A headed recipe used inside another recipe is always a compile error** (`spike/nested.sh`):

| Form | Result |
|------|--------|
| `do! headed` | FS0041 No overloads match for method 'Bind' |
| `let! x = headed` | FS0041 |
| `return! headed` | FS0708 (the recipe builder has no `ReturnFrom`, as in 4.0) |
| `match! headed with ..` | FS0041 |
| `for .. do do! headed` | FS0041 |
| `do! helper ()` where `helper ()` returns a headed recipe | FS0041 |
| `run headed` where `run (body: Recipe<..>)` | FS0001 |
| `run headed` where `let run body = recipe { do! body }` (inferred) | FS0041, the definition of `run` itself fails, as it does in 4.0 |
| `headed` as a statement (first or last) | FS0001 / FS0041 |
| `if c then headed` | FS0001 |
| `withResource lock 1 headed` (and every function taking a `Recipe`) | FS0001 |
| `ignore headed` | compiles: an explicit discard, the body never runs, as `ignore (need [..])` today |

The reason is structural, not a list of guarded cases: a headed recipe has its own type
(`Headed`), and only the rule forms accept it; every recipe combinator takes a `Recipe`. The
messages can be improved (an error-marked `Bind(Headed, ..)` overload naming the problem, as done
for the header-after-`do!` case).

**(b) A plain helper recipe that calls `need`:** correct, but dynamic. The helper's `need` runs
when the body reaches it, is recorded as a dynamic dependency (speculated on the next run, Part 3),
and is not part of the plan or started before the body. Docs: "Only `needs` in the header is
static; a `need` anywhere in the body, including inside helper recipes, is dynamic."

**(c) Helpers contributing header items: proposed, cheap.** A header is a value of its own:

```fsharp
// library or script helper: the static part as a header, the dynamic part as a recipe
let compileHeader = ruleHeader { needs "obj/(n:*).rsp"; needs ["obj/(n:*).res"]; requires cscLock }
let compileBody (n: string) = recipe { do! need [n] ... }

"out/(n:*).dll" ..> recipe {
    including compileHeader          // its items join this rule's header
    also "out/(n:*).pdb"
    let! m = getRuleMatches ()
    do! compileBody m.["n"]
}
```

- `ruleHeader { .. }` builds a header value with the same operations (no body); `including h`
  merges it into the rule's header (lists append, a later cut-off setting wins). Prototyped:
  compiles; `including` after `do!` is FS3095 with Xake's message.
- Its patterns use group names; they are checked against the pattern of each rule that includes
  it, at registration (XP013-style error naming both).
- Cost: one builder type (header operations only) and one operation; the merge is a record
  append. The helper must expose two things (header and body) instead of one; that is the honest
  limit: automatic collection of headers from recipes bound in the body is impossible without
  running the body (binds depend on values).
- Names: `include` is reserved by F# (warning FS0046), hence `including`. `header` as the builder
  name is a common word in user scripts and would collide on `open Xake`; hence `ruleHeader`.
- Proposal: in 5.0, with step 1 (small, and it makes the library rule "pure single-purpose
  function" story work for static needs).

## Third round (2026-11-17): making group substitution in a shared header obvious

The owner accepted `ruleHeader` + `including`, with one concern: in

```fsharp
let compileHeader = ruleHeader { needs "obj/(n:*).rsp" }
"out/(n:*).dll" ..> recipe { including compileHeader; .. }
```

nothing at the header says that `(n:*)` is filled in from whichever rule includes it, and nothing
at the rule says which of its groups the header uses. Three options.

### A. Declared groups (the header lists the groups it uses)

```fsharp
let compileHeader = ruleHeader ["n"] {          // "this header uses the group n of the including rule"
    needs "obj/(n:*).rsp"
    needs ["obj/(n:*).res"]
    requires cscLock
}
"out/(n:*).dll" ..> recipe { including compileHeader; .. }
```

- Checked twice. At creation: every group in the header's patterns is declared, every declared
  group is used ("ruleHeader [n]: the pattern 'obj/(m:*).rsp' uses the group m, not declared;
  declared: n"). At registration: the including rule has every declared group ("rule
  'out/(name:*).dll' includes the header ruleHeader [n] (build.fsx line 12), which uses the group
  n; the rule has the groups name").
- A header with no groups is `ruleHeader [] { .. }` (or `ruleHeader { .. }`, the same).
- Cost: a list argument on the builder, two checks; the location comes from
  `CallerFilePath`/`CallerLineNumber` on the builder's constructor-like function (or is left out:
  then the message names the header by its groups and first pattern).

### B. Explicit group parameters (a function of the groups)

```fsharp
let compileHeader = ruleHeader (fun (g: Groups) -> header {
    needs $"obj/{g.n}.rsp"                       // g.n: a placeholder that stands for the group
})
// typed variant: the fields of 'G are the groups, checked by the compiler
let compileHeaderT = ruleHeader (fun (g: AsmG) -> header { needs $"obj/{g.n}.rsp" })
Asm.rule { including compileHeaderT; .. }        // compile error if Asm's group type is not AsmG
```

- At registration the function is called with placeholders (`g.n` = `"(n:*)"`, or a typed record
  of them), so the text the reader sees names the group as a value, like the body does with
  `m.n`.
- The typed variant gives a compile-time check against a typed rule; the untyped `g.n` needs `?`
  or an indexer (`g.["n"]`), no better than A for untyped rules.
- Cost: higher. Two builders (`ruleHeader` over a function, `header` inside), placeholder
  values that must not be used for anything but patterns (a `g.n.ToUpper()` silently produces a
  wrong pattern), and an overloaded `including` for typed and untyped headers.

### C. Explicit mapping at the including rule

```fsharp
let compileHeader = ruleHeader { needs "obj/(n:*).rsp" }
"out/(name:*).dll" ..> recipe { including compileHeader ["n", "name"]; .. }
```

- The rule says which of its groups feeds which header group; the header may use its own names.
- Cost: low, but every inclusion carries a mapping, even the common case where the names are
  equal; it answers "the names may differ", which is not the concern.

### Recommendation: A

It puts the fact the owner found missing (this header uses group `n` of the including rule) at
the top of the header, in one word, and both mistakes (an undeclared group in the header, a
missing group in the rule) fail at once with a message naming the header, the rule and the group.
It costs one argument and two checks. B's typed variant is attractive only for typed targets and
can be added later as an overload (`ruleHeader (fun (g: AsmG) -> ..)`) without breaking A. C
solves a different problem.

Docs example (A), with the rule this enables: a library function returns the header and the body
separately.

```fsharp
/// The static part of a C# compile rule: the response file and the compiler lock.
let cscHeader = ruleHeader ["n"] { needs "obj/(n:*).rsp"; requires cscLock }

"out/(n:*).dll" ..> recipe {
    including cscHeader              // needs obj/<n>.rsp, takes cscLock when the target runs
    also "out/(n:*).pdb"
    let! m = getRuleMatches ()
    do! cscBody m.["n"]
}
```

## Step 1 findings (2026-11-17)

Found while implementing step 1; they change or refine the decisions above.

1. **The typed function form `X.Rule <| fun m -> ..` cannot take a header.** F# rejects an
   overloaded method used through `<|` (FS0041, "A unique overload for method 'Rule' could not be
   determined"), and an inline SRTP member as well (FS0002). Accepting a headed recipe there would
   break every existing `X.Rule <| fun m -> recipe { .. }`. So `X.Rule` stays recipe-only; a
   typed rule with a header is written `X.rule { needs ..; let! m = matched .. }`. This also
   settles critic M7 (calling the lambda with `X.Any` broke valid 4.0 lambdas). The decision
   "every file-rule form accepts it (… `X.Rule`)" is narrowed to every form but `X.Rule`.
2. **A statement with a value inside `recipe { }` is now an error** (critic m3). With `Yield` on
   the builder, `f (); do! ..` where `f ()` returns a value is FS0001; in 4.0 it was warning FS0020.
   The common case was a bug: `need ["a"]` without `do!` built nothing. Kept, listed as breaking in
   the release notes. The test suite had no such statement.
3. **Helpers over the operators still compile** (critic m2): `let mk p body = p ..> body`,
   `List.map (fun p -> p ..> recipe { .. })` and `List.map ((..>) "p")` all compile with the SRTP
   operators.
4. **A headed recipe value needs its type** (critic m5): `let h = recipe { needs "x" }` is a value
   restriction error (FS0030), as any generic recipe value is; `let h : Headed<ExecContext> = ..`
   compiles. Documented; `ruleHeader` (pending) avoids it.
5. **`{ do! a; do! b }` on one line does not compile, with or without a header.** An F# parsing
   rule, not new; the tests write such bodies on several lines.
6. **Duplicate locks** (critic M12): first merged (larger units); **superseded by the owner
   (2026-11-17): a resource named twice on one rule is a registration error**, and so are two
   resources of the same name on one rule. Locks are ordered by name, then by creation ordinal.
7. **Delegated rules apply the header** (critic M6): the static `needs` and the locks run inside
   the delegated body (`runDelegated` gets the same composed action), so `|> requiring` on a
   delegated file rule still locks.
8. **When static needs run in step 1.** The verdict is still the 4.0 one (recorded dependencies,
   transitive, computed before the task runs); static needs are built when the target runs, before
   the lock and the body, and recorded as ordinary dependencies. "Static needs first, then the
   verdict" (decision of 2026-11-17) comes with step 3, where the verdict moves into `execOne`.
   Until then, a `needs` added to the header of a target that is up to date does not rebuild it
   (as an edit of a recipe body does not in 4.0; critic M9).

## Speculation: options A, B, C (2026-11-17, for the owner)

The owner doubts speculative execution (brief Part 3, "Decision for a dependent", step 3): "on the
previous run we may have turned on obfuscation; will we build the obfuscation then? It may be an
expensive operation." Below: what each option decides, what cut-off it keeps on the Acceptance
scenarios, the cost risk, the implementation cost, and the changes to the brief.

### Terms

T is the target being decided. Its record lists what its body used last time: sources (files
without a rule), variables and environment, filesets, **static `needs`** (the header) and
**dynamic dependencies** (`need` in the body), among them **rule targets** D with the epoch T saw.
"Brought up to date" means: built if out of date, so the comparison is with what D is now.

In every option the static `needs` are brought up to date before T is decided (decision of
2026-11-17), so cut-off through static `needs` is the same in A, B and C. The options differ only
for dynamic dependencies that are rule targets.

### The obfuscation question

- **The flag is a tracked variable** (`getVar`, a `Var` field, read by T before it needs the
  obfuscated output): the variable is compared before anything is built and T reruns. Nothing is
  built speculatively. Same in A, B and C.
- **The flag is read by another rule** whose output T needs first (a config file made by a rule):
  in A, with strict recorded order, that config group is brought up to date and compared first; it
  changed, so T reruns and the obfuscation group after it is never built. In B, nothing is built.
- **The flag is untracked** (`File.Exists`, `System.Environment` read directly, the time of day):
  A brings the recorded obfuscation dependency up to date although the new body would not ask for
  it: the expensive build runs for nothing. B never does.
- **The obfuscation's own inputs changed and T still needs it:** A builds it during the verdict,
  B builds it in T's body. Same cost, at a different moment.

So with tracked state A builds only what the body would build anyway (Shake's argument); the cost
risk of A is exactly the untracked state, plus the side-effecting phony of C3.

### Option A: speculation, strict

- Recorded dynamic dependencies are brought up to date **in recorded order and grouping**, stopping
  at the first changed group (fixes critic C1); everything else (sources, variables, filesets) is
  compared at its place in that order, not "cheap checks first".
- Phony and `AlwaysRerun` targets are never built speculatively: out of date = changed (C3).
- Needs, in the engine: the record of order and grouping (database 0.7); a speculative build path
  whose failure is not a build error (log lines demoted or buffered, no cancellation of siblings,
  the failed result kept for the run, M10); wait-chain cycle detection, because speculation can
  meet a cycle the body would not (C4); docs on untracked state.

### Option B: no speculation

What B decides **without building anything** (other than the static `needs`):

| T's recorded dependency | B's decision |
|---|---|
| source file | stamp and size equal: unchanged; moved: hash, compare with the history (epoch rules): equal content is unchanged |
| variable, environment, secret, fileset membership | compared, as 4.0 |
| static `needs` | brought up to date (built if needed), then compared by epoch: full cut-off |
| dynamic rule target D | unchanged iff D's current record carries the epoch T saw **and** D is up to date by the same no-build decision, recursively. If D has already finished in this run (built or found up to date), its record is current and the rule above is exact. If D is running now, T waits for it (T's body would wait for it too). Otherwise D out of date means T changed, without building D |
| dynamic phony target | out of date by the no-build decision: changed; up to date: its completion stamp, as 4.0 |

So B keeps cut-off through sources, variables and static `needs`, and through dynamic rule targets
that are **up to date** (touched-but-equal sources make them so). It loses cut-off through a dynamic
rule target that is **out of date but would rebuild to the same bytes**: T reruns, as in 4.0.

**Where B loses what A gets.** `out/(n:*).dll` is a deterministic compiler rule over `src/(n:*)/*.cs`.
`out/App.dll` reads `App.proj` and needs the references it lists (`need [ "out/Core.dll" ]`): a
**dynamic** need, computed from a file. A comment is edited in `src/Core/x.cs`.
- A: App's verdict brings `out/Core.dll` up to date (Core recompiles, needed anyway), the bytes are
  equal, the epoch is kept: App is not recompiled, and nothing downstream of App reruns.
- B: `out/Core.dll` is out of date and B does not build it to find out, so App reruns: its body
  needs Core (recompiled, same bytes) and App recompiles (same bytes). App's dependents that need
  it dynamically rerun too, and so on up the chain: 4.0 behaviour for dynamic chains.
- B gets A's result if App names Core statically. The reference list is known when the script loads
  (the project files are read then), so the rules can be generated with literal `needs`:
  `for p in projects do yield ("out/" + p.Name + ".dll") ..> recipe { needs [ for r in p.Refs -> "out/" + r + ".dll" ]; .. }`.
  This is what the header exists for.

### Option C: opt-in per rule (`speculate` in the header), default B

- The dependent says "my recorded dynamic dependencies may be built to decide me". Default B.
- Cost: all of A (order record, speculative failure path, cycle detection, phony rule) **plus** B's
  no-build decision, plus a flag and its docs. The most code and the most behaviour to explain.
- It puts the switch on the wrong side for the owner's case: the risk is a **dependee** that is
  expensive to build (obfuscation), not a dependent. If A is ever wanted, a dependee-side opt-out
  (`noSpeculation` in the obfuscation rule's header: "never build me to decide someone else") fits
  the concern better.

### Acceptance scenarios

| Scenario | A | B | C (default B) |
|---|---|---|---|
| Control file A→B→A, the 100 targets name it in `needs` | cut-off | cut-off | cut-off |
| Control file A→B→A, the 100 targets `need` it in the body | cut-off | the 100 rerun on return to A (the route rule is out of date; not built to compare) | as B unless the 100 say `speculate` |
| Deterministic compiler, comment edit, dependents name the output in `needs` | cut-off | cut-off | cut-off |
| Same, dependents `need` it dynamically | cut-off | rerun up the dynamic chain (4.0 behaviour) | as B unless `speculate` |
| Source touched without an edit (twice) | nothing reruns | nothing reruns (the producer is up to date by hash, so its dependents are too) | same |
| Expensive dependee needed last time, untracked branch drops it | built for nothing | not built | not built unless `speculate` |
| Non-monotonic stamps, empty database, old databases | as specified | as specified | as specified |

### Implementation cost (step 3), relative

- **B:** epochs, lazy hashes, static `needs` before the verdict (already planned), and a no-build
  decision that is 4.0's `getChangeReasons` generalised from stamps to epochs, memoised per run but
  refreshed when a target finishes. No speculative failure path, no speculation cycles (C4 shrinks
  to cycles of static `needs`, rejected when the plan is built), no C3, no M10, and C1 does not
  arise (nothing is built during the verdict except static `needs`, and the record is compared
  after them).
- **A:** B's machinery minus the no-build recursion, plus the record of order and grouping, the
  speculative build path (failures, logs, cancellation), cycle detection, the phony rule and the
  untracked-state docs. Roughly twice B's verdict work and most of the step-3 risk.
- **C:** A + B + the flag. The largest.

### Changes to the brief

- **B:** Part 3 "Decision for a dependent" step 3 becomes the no-build decision above; Acceptance
  "Speculation" is removed and replaced by "a dynamic dependency on an out-of-date rule target reruns
  the dependent (documented); a static `needs` on a deterministic output gets cut-off"; the
  control-file scenario names the route in `needs`. Docs: "for cut-off through a rule's output,
  name it in `needs`". Critic C1, C3, C4 (for speculation), M10 close.
- **A:** step 3 keeps speculation with strict recorded order, no phony speculation, cycle detection;
  C1, C3, C4, M10 are implemented as their proposals say.
- **C:** both, plus the flag.

### Recommendation: B for 5.0

B gives cut-off where the motivating cases put it (a control file and fixed compiler inputs are
fixed inputs, and the header makes them static), never builds anything to decide a target except
what the rule declares, and removes most of the risk of step 3. What it gives up, cut-off through
dynamic dependencies on out-of-date rule targets, is the 4.0 behaviour, so nothing gets worse. The
database 0.7 record should still keep the order and grouping of dynamic needs (cheap, critic m1),
so A, or a dependee-side opt-out variant of it, can be added in a minor release without a format
change if real scripts show the need.
