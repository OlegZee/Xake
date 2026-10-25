# Critique: typed rule matches, the feature as a whole

Independent read of `brief.md` (decisions 1–45), `research.md`, the six follow-up notes, `plan.md`,
`experiments/README.md`, the prototypes (08 `engine.patch`/`Provider.fs`/`Runtime.fs`/`showcase.fsx`,
10 `TypedPatterns.fs`/`consumer/Rules.fs`) and the engine as it is (`XakeScript.fs`, `RuleBuilder.fs`,
`ScriptFuncs.fs`, `ExecCore.fs`, `build.fsx`, `build.fsc.fsx`, `samples/`). Written 2026-10-25, before
PR1/PR2 land. Where a point touches a recorded decision it is marked **(decided — risk)**; where a
document states something that is not so, **(factual)**.

## 1. Does the end design still solve the original problem? Is it proportionate?

**Yes, the Core layer solves it — and it is the part of the design that did not exist when the
discussion started.** The original complaint (`session.md` Goal) was `getRuleMatch "name"`: verbose,
untyped, a typo gives `""` silently (`experiments/01`). Experiment 10's `Target<'G>` over a record turns
`m.fkw` into FS0039 with a suggestion, `Format { .. }` with a missing or misspelt field into
FS0764/FS1129, and the record-vs-pattern mismatch into one precise line at script start
(`core-without-tp.md` §3). It also gives the inverse — `Format`, `For`, `Glob` — which `research.md` §3
shows is where the real duplication in the build scripts lives (`binaries`, `packagePath`, `$"lock::{r}::{cfg}"`,
`testJobName`). Real F# record patterns (`fun { name = name; cfg = cfg } ->`) are something the type
provider can never offer (`destructure.md` §2). Cost: ~270 lines in `Xake.dll`, +56 KB, no dependency,
no tooling. That is proportionate, and better than the roadmap's positional "Rules with arguments"
(`docs/roadmap.md:26-42`), whose silent swaps the research rightly rejected (options 3–5).

**What is not proportionate is everything built around it to keep the type provider alive:**

- Two typed surfaces with the same short names (decision 34) that cannot be open in one script
  (`core-without-tp.md` §5: FS0618 in either order), differing in semantics behind identical member
  names: `Make` returns the match in the TP, the outputs record in Core; `For` is a cross product of
  lists in the TP, a sequence of records in Core; `Glob(dir = ..)` vs `Glob { X.Any with dir = .. }`.
  "Moving between them is mechanical" (`core-without-tp.md` §5) understates this: it is a rewrite of
  every call site, and two sets of docs, tests and error texts to keep aligned.
- The TP's marginal value over Core has shrunk to: names written once, pattern-syntax errors at
  compile time instead of script start, outputs without a declared record, a cross-product helper
  (`core-without-tp.md` §7). Against that: a 1.2 MB design-time dll, FS3031 whenever the dll is misplaced,
  a real IDE incident already (README 08, "assembly-identity clash" — self-inflicted by the experiments,
  but a preview of what users with two package versions in one editor session will hit), Windows file
  locking and Rider/VS untested, and no `[<Literal>]`-free patterns (`build.fsx:180,190` must change).
- Three of the four breaking changes in 4.0.0 exist for the TP, not for Core (see §3).

**A simpler design that captures most of the value:** PR1 (public `PatternAst`, `format` replacing
`applyWildcards`, escaping) + a trimmed PR3: `Target<'G>` with record carriers only, `Format`/`Paths`/`For`/
`Glob`/`Rule`, `Command<'G>` for phony, and — the cheapest check of all, still missing from the plan —
the multi-file group-set check at rule creation for the *string* API (`research.md` option 0, first
bullet; `targets ["..(lib:*).dll"; "..(name:*).xml"]` still silently yields `out/a/.xml` today). No `Level`
change, no DU rename, no TP package, no hermetic re-release forced by binary breaks. Form B and `Target<'G,'O>`
can follow once someone asks; both are additive.

## 2. Coherence after the pivots

1. **The TP still shapes Core's API although it is now opt-in and unscheduled.** Three Core decisions
   are justified only by the provider: the DU rename (decision 39; Core's `Target<'G>` and the DU
   `Target` coexist — arity differs, `core-without-tp.md` §5 p05); `[<RequireQualifiedAccess>]` on `Level`
   (decision 35; Core needs only `open Xake.Typed` after `open Xake`, `TypedPatterns.fs` header note —
   the qualification matters for the provider's inline `Command<"clean">`, `tp-probes`); `matched` moved
   into namespace `Xake` so the provider can reuse the marker (`plan.md` §1 "Namespace"). If PR7 slips or
   is dropped, 4.0.0 will have broken scripts for a package nobody uses. **(decided — risk)**

2. **Decision 42 vs PR6.** Decision 42 keeps the build scripts' grouped phony rules on `=>`; decisions
   21/31 make `Command<'G>.Rule`/`.rule` AlwaysRerun-only; and Core has **no typed phony form with `=>`
   semantics** (`Command<'G>` is the only phony type; `Target<'G>.Pattern => recipe {..}` loses the typed
   read because `read` is private). So PR6 cannot convert `test-build::`, `test::` (both scripts) or
   `lock::` — 5 of the 7 grouped rules the research used as motivation (`phony-types.md` §1: "every
   grouped rule is written with `=>`, none with `command`"). Dogfooding is left with two file rules and
   the nupkg rules. `phony-types.md` §2/§5 recommended `=>` semantics for exactly this reason; the brief
   went the other way. Note also README 08 step 2: a phony with no dependency reruns anyway, and a phony
   dependency's stamp is its completion time, so for `test::` (depends on `test-build::`, which has no
   inputs) `=>` and `command` behave the same in practice — the caution behind 42 may be moot; one run
   would tell.

3. **Two policies for the same mistake.** Decision 7 keeps `getRuleMatch "fkw"` → `""`; the new
   `PatternMatch.Item` and `Pattern.Read` raise (`TypedPatterns.fs`, `Pattern.Read`). Defensible
   (compat vs new API), but the docs must say plainly that the string API stays silent by design.

4. **Names.**
   - `Target<'G>` ≈ `target "p" {..}` and `Command<'G>` ≈ `command "n" {..}` line up with the existing
     builders — good. But `Target<'G,'O>` ≈ `targets [..] {..}`, and `Command<'G>` ≠ `=>` (see 2):
     a reader who knows `"x" => recipe` has no typed counterpart.
   - `X.Rule` (method, form D) next to `X.rule` (builder, form B) differ by case only
     (`ce-syntax.md` §8 q1 raised it; never answered). Inside `xakeScript` the custom operation is also
     `rule` (`rule (X.rule { .. })`, FS3099 without parentheses, README 07). Three `rule`s.
   - `matched` is a common word injected into every script via `open Xake` (plan). A script's own
     `let matched = ...` shadows it silently; `ce-syntax.md` §8 q3 flagged this, unanswered.
   - `For` returns `string list` — the name says loop, not "paths for". `Paths`/`PathsFor` would read
     as what `need` takes. `Format` on `Command<>` returns a *name*; fine after the 08 fix, but the
     README still documents `Command<>.Pattern(...)` returning the name in one place and `Format` in
     another (README 08 "Names first" vs "Opening in an IDE").
   - `Xake.Typed` names a mechanism, not a thing (`Xake.Patterns`/`Xake.Rules` were rejected for
     similar reasons). Acceptable; just note that `open Xake.Typed` must come *after* `open Xake` and
     the compiler error when it does not (FS0717 "Unexpected type arguments") names nothing useful.
   - `BuildTarget`: fine as a name; the premise in decision 26 ("the DU is internal by intent — scripts
     are not meant to use it") is contradicted by `plan.md` §1's own facts (`ExecContext.Targets`,
     `DelegatedExecutor`, `samples/delegated*.fsx`, `docs/delegated.md:137`). **(factual, in brief.md)**

5. **Leftovers from superseded directions.** `brief.md` "Open" still lists the phony-type cost,
   the hermetic sketch and IDE/packaging as open (all answered); decision 25 sits after 45;
   `plan.md` is built "on decisions 1–38" and still says 3.12.0 (steps 4, 5, 7; §3; PR5 range
   `[3.12.0.<run>, 3.13)`) and "branch from `dev`, squash merge to `dev`" (§2 header), both superseded
   by decisions 41 and 45; `session.md`'s PR table already follows 45. **(factual, stale)**
   `hermetic-sketch.md` is written against `Pattern<"...">` with `Path`/`All` — fine as a sketch, but
   its "passing patterns into the library" section assumes erased types; Core's real `Target<'G>`
   makes option 2 trivial (`Locks.*` can take a `Target<_>`), which nobody has noted.

6. **Label syntax `"label=pattern"`** (decision 29) is parsed by regex on the pattern text: a file
   pattern that legitimately starts with `word=` is read as a label (README 08 deviations). Core could
   take labels from the 'O record instead (it already checks them) and keep the text clean.

## 3. Risks the discussion under-weighted

- **The silent escaping change (decisions 15/28) is the one real 4.0 item, and it has no safety net.**
  `[(]` was the *documented-by-test* way to match a literal parenthesis (`PathTests.fs:18` "want brackets
  escaped", `XakeScriptTests.fs:288,340`), and it also reaches `Fileset` masks (`!!`, `ls`, `fileset {}`).
  A user mask `[abc]*.txt` or `src/[(]x[)]/**` will match nothing — no error, an empty fileset, a rule
  that is never selected. The plan has only a release note. A one-release warning is cheap: when a
  pattern or mask contains `[`, `]` or `|`, log once "since 4.0 these are literal; was: regex class/alternation".
- **4.0.0 bundles four breaks for very different reasons.** (1) escaping — a correctness fix, worth a
  major; (2) `Level` qualified — to free a name; with the shim (40) it breaks `Command` and pattern
  matches only, but a `match` on unqualified cases degrades to a *silent catch-all* (FS0049 is a
  warning); (3) DU `Target` → `BuildTarget` — for the TP; binary break that *forces* a hermetic
  re-release (PR5) and a rebuild of every compiled consumer; (4) `applyWildcards` edge fixes — fine.
  Each of (2) and (3) has a workaround that costs nothing (`DomainTypes.Target`, `open Xake` first).
  **(decided — risk)**: the user chose 4.0.0 knowing (1); (2) and (3) ride along without being needed
  for anything that ships in 4.0.
- **Migration story.** No migration guide, no before/after table for `getRuleMatch` → `Target<'G>`,
  no deprecation policy (decision 7 keeps `getRuleMatch`; good — but "recommended" vs "kept" must be
  explicit in `cheatsheet.md`). Scripts on `3.7.0.26` (`build.fsx` itself) jump five minors; the plan
  notes it for PR6 only.
- **Learning curve.** A newcomer now meets: string rules (`..>`, `=>`, `target`, `command`), typed
  Core (`Target<'G>`, record, `Rule`/`rule`, `matched`, `Format`/`Paths`/`For`/`Make`/`Glob`/`Any`),
  and later a TP with the same names and different members. Three ways to write one rule.
- **IDE behaviour of the Core variant** is plain F# — the main win of the pivot — but untested in an
  editor for the one construct that depends on overload resolution: `let! m = matched` picking
  `TypedRuleBuilder<'G>.Bind(Matched, ..)` over the inherited `RecipeBuilder.Bind`s. fsi passes; FSAC's
  incremental checking occasionally differs on CE overloads. Open `consumer/Rules.fs` in Ionide once.
- **Script-start errors.** An `XakeException` from a top-level `let X = Target<G> "..."` prints with
  a stack trace in fsi (plan PR3 step 4 probes this). Also: a script whose patterns are declared inside
  `rules [ .. ]` fails at the same point today, so "before any target runs" holds; but a rule list built
  lazily (functions returning rules, as `Rules.fs` does) moves the check to first use.
- **Reflection** (`FSharpType.IsRecord(t, true)`, precomputed ctors): 0.13 ms per declaration — fine.
  `allowAccessToPrivateRepresentation = true` means a private record in a *user's* compiled assembly
  works only with reflection permissions; irrelevant for scripts, note for library authors.
- **Core ctor arity:** `Target<'G>` has constructors for 1–3 patterns and the list ctor is internal
  (`TypedPatterns.fs`), so a 4-output rule is impossible in Core while the TP allows 4. Add a
  `string list` constructor.
- **Tuple carriers** reintroduce the silent swap the research rejected (options 3/4; `neg` p10). They
  cost nothing to implement and something to document and support. Records, anonymous records,
  `string`, `unit` suffice.
- **Hermetic / library parameters.** Deferred (9). Note that PR5 exists only because of the binary
  breaks in PR2; without them `Xake.Hermetic.Dotnet 0.7` (`[3.11.0.40, 3.12)`) would still need a
  range bump for 4.0 — so a hermetic release happens either way, but with PR2 it also needs code changes.

## 4. The plan

- **Split and ordering.** PR1 is right and should land first: it fixes real bugs (empty group value,
  `)` in a value) and is the base for everything. PR2 is the most contentious and the least needed;
  do it last, or not at all (see §5). PR3 depends only on PR1 — good. PR4 (docs) after PR3 — good.
  PR5/PR6 are release plumbing. PR7 should not be in this plan at all; it is a separate feature with
  its own brief once Core has been used.
- **Integration branch (decision 45).** Workable, but it delays CI on `dev` for PR1's bug fixes, and
  the final squash of ~2k lines into one commit on `dev` loses the per-PR history. Since releases are
  tags, breaking changes can sit on `dev` unreleased; merging each PR to `dev` (the project's own
  convention, `CLAUDE.md`) would be simpler. **(decided — risk)**
- **Testability.** The `--typecheck-only` fixtures (43) are the right tool for C-cases; amortise with
  one fixture file and `//#case` markers (the experiments' runner already does this) instead of one
  process per case. Missing tests: a *compat corpus* — every `samples/*.fsx` and every fenced F# block in
  `docs/*.md` type-checked against the 4.0 build (the escaping change and `Level` will break some);
  an incremental-build test proving the `=>` vs `Command<>` difference (or its absence) on `test::`-shaped
  rules; a `Fileset` test for `|` and `[x]` in directory names (planned) *plus* the warning of §3.
- **Missing from the plan:** the string-API multi-file group check (option 0); a migration guide
  (`docs/migration-4.0.md` or a section in the release notes with before/after); the one-release
  pattern warning; an explicit statement of what `getRuleMatch` is (kept, not deprecated); the
  `string list` ctor; a decision on `rule`/`Rule` casing and the `matched` name (open since `ce-syntax.md`).
- **What could be cut without losing the user's goals:** tuple carriers; `Target<'G,'O>` for 4.0
  (`Paths` covers outputs; `Make` can come later); PR7; PR2's `Level` change (keep `open Xake` then
  `open Xake.Typed` as the rule, which the TP needs anyway); the DU rename (keep `DomainTypes.Target`
  as the documented spelling for the rare annotation, README 08 w1).

## 5. Recommendations, ranked

1. **Ship Core typed patterns (PR1 + PR3 + PR4) and stop there for 4.0; take PR7 out of this plan.**
   Revisit the provider after one release of real use. If it never comes, nothing in Core needs to
   change — provided recommendation 2 is followed.
2. **Drop or defer PR2.** Neither rename is needed by anything that ships. If the user keeps 4.0.0
   for the escaping change, let 4.0 carry *that* break and keep `Level` and `Target` as they are;
   document `open Xake` then `open Xake.Typed`. If `BuildTarget` is still wanted, do it in its own
   PR with its own justification, not as a dependency of typed matches.
3. **Give the escaping change a safety net:** a one-release warning when a rule pattern or fileset
   mask contains `[`, `]` or `|`; a compat run of `samples/` and the docs' code blocks; a migration
   section with the exact before/after (`[(]` → `(`).
4. **Close the phony gap before PR6:** either a typed phony form with `=>` semantics (a `Phony<'G>`,
   or `Command<'G>.Rule` as `=>` plus `alwaysRerun()` where wanted, as `phony-types.md` §5 proposed)
   or measure that `=>`/`command` make no difference for the scripts' `test::`/`lock::` rules and
   retire decision 42. Without this, dogfooding cannot touch the rules that motivated the feature.
5. **Trim and settle the Core surface in PR3:** records/anonymous records/`string`/`unit` only (no
   tuples); add the `string list` ctor; add the option-0 group-set check to `*..>`/`targets`; decide
   `Rule`/`rule` casing and the `matched` name now (a builder cannot be piped into, so the method
   needs another name if the pair is judged confusing — e.g. `X.Recipe` or `X.With` — or the case-only
   pair is accepted and stated in the docs); label outputs via the 'O record rather than `"label=pattern"`.

## Factual notes on the documents

- `plan.md` is stale against brief rounds 7–8 in three places: version 3.12.0 (decision 41 says
  4.0.0), per-PR merge to `dev` (decision 45 says the integration branch), PR0 (decision 45 says not
  needed). The hermetic range `[3.11.0.40, 3.12)` and version 0.7.0 it cites are correct
  (`src/hermetic/Xake.Hermetic.Dotnet.fsproj:11,82`).
- `brief.md` decision 26's premise ("the DU is internal by intent") is contradicted by the plan's
  own inventory of script-facing uses; the "Open" list and the placement of decision 25 are stale.
- `core-without-tp.md` §5 "moving between them is mechanical" is an overstatement (`Make`, `For`,
  `Glob` differ in meaning or shape between Core and TP).
- `experiments/README.md` 08 names `Command<>.Pattern(...)` as the name generator in one section and
  `Format` in a later one; the code has `Format`.
- Everything else I checked against the code holds: `command` adds `AlwaysRerun` (`RuleBuilder.fs`),
  `<==`/`<<<` call `alwaysRerun()` (`ScriptFuncs.fs`), `getRuleMatch` returns `""` on a miss
  (`ScriptFuncs.fs`), `applyWildcards` is the third encoding (`ExecCore.fs:20-39` before the patch),
  the `[(]` tests exist at the cited lines.
