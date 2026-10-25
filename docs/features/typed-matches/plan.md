# Implementation plan: typed rule matches and path generation

Built on [`brief.md`](brief.md) decisions 1–38 (binding; later rounds supersede earlier ones:
32 over 1/17/24 on packaging, 26/27 over 4, 22 over 19, 33–38 for Core). Concrete templates:
`experiments/08-consolidated/{engine.patch,tests.patch}` (engine), `experiments/10-core-no-tp/TypedPatterns.fs`
(Core typed layer), `experiments/08-consolidated/{patterns,designtime,pkg}` (provider package).
Nothing here re-opens a decision; proposals that still need a yes are listed in §4.

## 1. Scope and order

1. **Core engine** (`Xake`, `src/core`): `PatternAst` public in its own file with generation
   (`format`, `glob`, positional wildcards, `formatMatches`); `ExecCore.applyWildcards` replaced by
   `PatternAst.formatMatches` (14); literal `( ) [ ] |` escaped (15, 28). `getRuleMatch` untouched (7).
2. **Core script surface**: `[<RequireQualifiedAccess>]` on `Logging.Level` (35); the DU
   `DomainTypes.Target` leaves the script surface (26) — proposal below.
3. **Core typed layer** (33, 34, 36, 38): `Pattern`, `Target<'G>`, `Target<'G,'O>`, `Command<'G>`
   (incl. `unit`), forms D (`X.Rule <| fun m -> ..`) and B (`X.rule { let! m = matched .. }`),
   readable script-start errors. No `Need`/`TryMatch` (25), no cross-product helper in Core (37).
4. **Docs + samples**, release notes → **release `Xake` 3.12.0** (user tags `v3.12.0`).
5. **Hermetic on 3.12** (range + rebuild; required by the DU rename) → `hermetic-v0.8.0`.
6. **Dogfooding** in `build.fsx` / `build.fsc.fsx`. Both bootstrap from the **published** packages
   (`build.fsx`: `#r "nuget: Xake, 3.7.0.26"`; `build.fsc.fsx`: Xake 3.10.0.35 + Hermetic 0.5.0.36),
   so this waits for steps 4 and 5 to land on nuget.org at an exact `X.Y.Z.<run>`.
7. **Provider package `Xake.Patterns`** (32, 36, 37): opt-in, erases onto Core's runtime of step 3,
   builds on the published `Xake` ≥ 3.12 (like `src/hermetic`). Can be developed in parallel with
   5–6 against a local feed; merges after 3.12 is published.
8. **Hermetic path functions from patterns** (9): later; `hermetic-sketch.md` stays the sketch, no plan.

### Namespace for the Core typed layer

Keep **`Xake.Typed`** (the prototype's name). Reasons: it says what the script gains, it pairs with
`Xake.Patterns` (`open Xake.Typed` vs `open Xake.Patterns`, decision 34), it is short, and no type
or module named `Typed` exists. Rejected: `Xake.Records` (names the mechanism, not the benefit;
anonymous records and tuples/`string`/`unit` carriers also work), `Xake.Rules` (clashes in reading
with `DomainTypes.Rules`). The form-B marker `Matched`/`matched` and `TypedRuleBuilder<'G>` go into
namespace **`Xake`** (an `[<AutoOpen>]` module in the same file), so the provider reuses one marker
(34; `core-without-tp.md` §5: two markers give FS0041).

### Taking the DU `Target` off the script surface (proposal)

Facts: the DU is used by scripts through `ExecContext.Targets : Target list` and
`DelegatedExecutor` (`docs/delegated.md:137`, `samples/delegated*.fsx`), by `src/dotnet/DotnetTasks.fs:77`,
`src/hermetic/{Sign,Described}.fs`. F# has no `CompiledName` for types, so any rename or move changes
the IL name. Removing `[<AutoOpen>]` from `DomainTypes` keeps IL but hides `Recipe`, `Rule`,
`XakeException`, `Dependency` too — far too broad.

Proposal: **rename the type to `BuildTarget`**, keep the case names `FileTarget`/`PhonyAction`, no
`Target` alias (an alias would keep the clash with the provider's `Target<"...">`).
- Source impact: only code that *names the type* (`targets: Target list`); matching on cases and
  unannotated code compile unchanged. In repo: `ExecTypes.fs`, `Types.fs`, `Database.fs`,
  `ExecCore.fs`, `DotnetTasks.fs:77`, `samples/delegated.fsx:38`, `samples/delegated-tests.fsx:43`,
  `docs/delegated.md:137`.
- Binary impact: compiled consumers must rebuild; `Xake.Hermetic.Dotnet` 0.7 (range `[3.11.0.40, 3.12)`)
  does not admit 3.12 anyway and gets a rebuilt 0.8 (PR 5).
- Database: the `.xake` pickler is tag-based (`Database.fs:54-56`), the type name is not stored →
  old databases load; covered by a `StorageTests` round-trip assertion.

## 2. PR breakdown

Each PR: branch from `dev`, worktree `.claude/worktrees/<branch>` (named like the branch), squash
merge to `dev`, PR to `OlegZee/Xake` (`gh pr create --repo OlegZee/Xake --base dev`), the user pushes.
Each branch creates its feature folder `docs/features/<name>/{session,tracker}.md` (short, linking
to this plan); the PR/branch/worktree table lives in `docs/features/typed-matches/session.md`.
Common acceptance (not repeated below): `dotnet build src/core -c Release` and
`dotnet build src/dotnet -c Release` with **0 warnings on net462 and netstandard2.0**,
`dotnet test src/tests -c Release` green with the new tests, `docs/release-notes.md` updated,
`dotnet fsi build.fsc.fsx -- -- check` passes (fingerprints) when `src/core` sources change.

### PR 0 — `feature/typed-matches` (this branch): research + plan, docs only

- Worktree `.claude/worktrees/feature/typed-matches`. Goal: land `docs/features/typed-matches/`
  (brief, research, experiments, this plan) on `dev` so later branches start from it.
- Steps: confirm no build output is tracked (`git ls-files docs/features/typed-matches | grep -E '/bin/|/obj/|\.dll$'`
  is empty today); leave `experiments/08-consolidated/consumer/showcase.fsx` to the user. Size: XS.

### PR 1 — `feature/pattern-ast-format`: public PatternAst, generation, escaping

- Worktree `.claude/worktrees/feature/pattern-ast-format`. Depends on: PR 0 (soft).
- Files: new `src/core/PatternAst.fs` (`module Xake.PatternAst`, before `Path.fs`); `src/core/Path.fs`
  (parser moved out, `Path.Specificity` an alias); `src/core/ExecCore.fs` (`applyWildcards`,
  `wildcardsRegex`, `patternTagRegex`, `replace`, `ifNone` removed; `generateName` =
  `PatternAst.formatMatches`); `src/core/Xake.fsproj`, `src/core/xake.project.json`,
  `locks/Xake/Release.json`, `xake.fingerprints`; tests `src/tests/{PatternAstTests,PathTests,XakeScriptTests,FilesetTests,RuleMatchTests}.fs`;
  `docs/release-notes.md`, `docs/implnotes.md` (if it describes `applyWildcards`).
- Steps:
  1. `patch -p1 < docs/features/typed-matches/experiments/08-consolidated/engine.patch` and
     `tests.patch` at the worktree root (made against `dev` after #89; port by hand on conflicts).
  2. Add `|` to the escaped literals (`metachars` = `[.$^+!={}()\[\]|]`, decision 28).
  3. XML doc comments on every public `PatternAst` member (it becomes API: `Token`, `parse`,
     `unparse`, `toRegex`, `specificity`, `groupNames`, `wildcards`, `format`, `glob`, `formatMatches`).
  4. `dotnet fsi build.fsc.fsx -- -- import`, then `update-locks`, then `check` (new source file in
     `src/core`; the self-build trap of `docs/session.md`).
- Tests (from `tests.patch` + `08-consolidated/consumer/engine-only.fsx`): escaping of `( ) [ ]`
  **and `|`** in rule patterns (`a|b.txt` matches only the literal name); a literal-only pattern
  formats to itself (14); positional format of `*`/`**`/`?`; empty group value and `)` in a value
  (the old `applyWildcards` bugs); `glob` with unbound groups; `Fileset`: `ls`/`!!` with a literal
  `(`, `[x]` and `|` in a directory name finds the file; multi-file rule `out/(lit)/(n:*).dll` +
  `.xml` asked for `out/(lit)/x.xml` names `out/(lit)/x.dll`; `getRuleMatch` of an unknown group
  still `""` (7). Rewritten: the `[(]` cases in `PathTests.fs:18-19`, `XakeScriptTests.fs:288,340`.
- Behaviour change (release notes): `( ) [ ] |` are literal in rule patterns **and fileset masks**;
  `[(]` and other regex character classes / alternations no longer work; write the character itself.
- Risks: a user mask relying on `[abc]` silently matches nothing → called out in the notes; the
  former crash of the test host on `(name:[(]*)` (README 08) must be gone, not skipped.
- Size: S–M (≈ +250/−130 incl. tests, ~1 day).

### PR 2 — `feature/script-surface-names`: `Level` qualified, DU `Target` → `BuildTarget`

- Worktree `.claude/worktrees/feature/script-surface-names`. Independent of PR 1 (parallel).
- Files: `src/core/Logging.fs`; every `Log`/`trace` call naming a level in `src/core` (`ExecCore`,
  `ScriptRunner`, `Database`, `Fingerprints`, `Tasks/{Cp,Rm,Shell}.fs`), `src/dotnet`
  (`CompilerRunner`, `Csc`, `Fsc`, `Dotnet.Msbuild`, `Dotnet.resgen`, `Tool`), `src/tests`
  (`ScriptErrorTests`, `XakeScriptTests`, `DotnetTasksTests`, `MiscTests`, `RecipeTests`,
  `FileTasksTests`); `src/core/{Types,ExecTypes,Database,ExecCore}.fs`, `src/dotnet/DotnetTasks.fs`
  for the rename; `samples/delegated*.fsx`, `samples/{features,catch_errors}.fsx`,
  `samples/Research/*.fsx`, `samples/netcore-compile/Program.fs`; docs naming levels or the DU
  (`readme.md`, `docs/{overview,cheatsheet,tasks,csc-syntax,hermetic-guide,delegated}.md`,
  `docs/hermetic/{description,lock,workflows}.md`). Not `src/hermetic` (published-Xake build, PR 5).
- Steps:
  1. `[<RequireQualifiedAccess>]` on `Level`; qualify every case use in the files above.
  2. Compatibility shim (proposal, §4): in `Logging` add values `Message`, `Error`, `Warning`,
     `Info`, `Debug`, `Verbose`, `Never` = the `Level` cases, **not** `Command`. Then
     `trace Info "..."` (readme, samples, user scripts) keeps compiling; only `Command` and
     *pattern matches* on unqualified cases break. Inside the repo use `Level.X` everywhere anyway.
  3. Rename the DU to `BuildTarget` (cases unchanged), fix the IL-visible signatures
     (`ExecContext.Targets`, `NeedRebuild`, `DelegatedExecutor`, `Dependency.ArtifactDep*`).
- Tests: a script-like test using `trace Info`/`trace Level.Command`; `Level` round-trip of
  `LevelToString`; `StorageTests`: a database written before the rename (fixture bytes or a
  pickled `FileTarget`/`PhonyAction`) reads back; `CommandLineTests` verbosity unchanged.
- Behaviour/compat (release notes): `Command` level must be `Level.Command`; a script that
  `match`es on unqualified level cases now binds a variable (warning FS0049) — must qualify;
  `Target` type annotations become `BuildTarget`; compiled consumers rebuild.
- Risks: the FS0049 catch-all in user code is silent at run time → explicit note; missing a
  qualification in `src/hermetic` is caught in PR 5's build.
- Size: M, mostly mechanical (~40 files, ~0.5–1 day).

### PR 3 — `feature/typed-targets`: record-declared typed patterns in Core (`Xake.Typed`)

- Worktree `.claude/worktrees/feature/typed-targets`. Depends on: PR 1 (public `PatternAst`).
  Rebase over PR 2 if it merged first (no overlap expected).
- Files: new `src/core/TypedPatterns.fs` (after `ScriptFuncs.fs`), `src/core/Xake.fsproj`,
  `src/core/xake.project.json`, locks/fingerprints; new `src/tests/TypedPatternTests.fs`,
  `src/tests/TypedRuleTests.fs`, `src/tests/tests.fsproj`; `docs/release-notes.md`.
- Steps:
  1. Start from `experiments/10-core-no-tp/TypedPatterns.fs`: `Pattern`, `PatternMatch`,
     `PatternOutput`, internal `Codec`, `Target<'G>`, `Target<'G,'O>`, `Command<'G>` in
     `namespace Xake.Typed`; `Matched`/`matched`/`TypedRuleBuilder<'G>` moved to namespace `Xake`
     (AutoOpen module) for the provider (34).
  2. Carriers: named record (recommended), anonymous record, tuple of strings (documented as
     positional, swaps silent), `string` (one group), `unit` (none). Private records keep
     `allowAccessToPrivateRepresentation = true`.
  3. Members as prototyped: `Pattern`, `Patterns`, `Spec`, `Format`, `Paths`, `For` (a sequence of
     group values), `Any`, `Glob` (null = unbound, returns `Fileset`, 8), `Make` (`Target<'G,'O>`),
     `Rule` (form D), `rule` (form B); `Command<'G>`: `Pattern`, `Spec`, `Format`, `For`, `Rule`,
     `rule` with today's `command` semantics (AlwaysRerun, 21/31); `Command<unit>.Rule <| fun () ->`
     and `fun m ->` both compile (23, 30); `Command<unit>.rule { }` needs no `matched`.
     Labels `"label=pattern"`, else `Out1`, `Out2` (29); bare wildcards `_1`, `_2` (6), rejected
     in multi-output patterns.
  4. Readable script-start errors (38): `Codec.nameOf` renders anonymous records as `{| fwk; lib |}`,
     generic/nested types by their F# name; every message carries the pattern text. A dedicated
     exception (e.g. `PatternDeclarationException`) whose `ToString()` is the message only, so fsi
     prints no stack trace. First step of the PR: probe this in fsi SDK 8 and 10 (top-level `let`
     raising it); if fsi still prints a trace, fall back to recording the error at creation and
     raising it from `xake`/`xakeScript` start through the runner's error path (`ScriptErrorTests`
     style), still before any target runs.
  5. With PR 2 merged, `Command<_>` works regardless of open order; still document
     `open Xake` then `open Xake.Typed`.
- Tests (port `experiments/10-core-no-tp/neg/output.txt` S/G/R cases and the consumer):
  - Script start, one test per message: record field not in pattern / missing; multi-output group
    mismatch; outputs record vs labels; unclosed `(fwk:*/`; non-string field; tuple arity; `unit`
    with groups; bare wildcards in multi-output; wildcards missing from the record (`_1`); duplicate
    label; anonymous-record type name rendered `{| .. |}`; `ToString()` has no stack trace.
  - Generation: `/` in a `*` value and violated `(ext:c*)` fail with the round-trip message;
    `null` field → "no value for"; untyped `Pattern.Format` unknown name; `m.["fkw"]` in a rule fails.
  - Positive: `Format`/`Paths`/`For`/`Make`/`Glob { X.Any with dir = .. }` outputs; private record
    types (p16/p17); a pattern built from run-time data (p19); one record serving two patterns (p08).
  - Engine runs (`XakeTestBase`): the `consumer/Rules.fs` rules — labelled `Target<LibG, LibOut>`
    multi-file, `Target<AsmG>`, `Command<string>`, `Command<LockG>` with a record-pattern lambda,
    `Command<unit>`, `"build" <== Lib.For [..]` — in form D and form B; incremental: a second run
    skips targets and reruns only commands with dependencies (README 08 step 2).
  - Compile-time negatives (C cases: FS0039, FS0764, FS1129, FS0041, FS0001) as fixture scripts
    `src/tests/fixtures/typed-neg/*.fsx`, type-checked by `dotnet fsi --typecheck-only` against the
    built `Xake.dll` in a `[<Category("Integration")>]` test asserting the error code (§4 item 5).
- Acceptance: `Xake.dll` grows ≈ 60 KB, no new package dependency (FSharp.Reflection is in the
  FSharp.Core 8.0.100 pin), both TFMs 0 warnings.
- Risks: anonymous-record field order (alphabetical) vs pattern order — irrelevant for name-based
  codec, test it; same-label records picking the latest type (FS0001, documented, p07).
- Size: M–L (~300 lines + ~400 lines of tests, 2 days).

### PR 4 — `feature/typed-targets-docs`: user docs, samples, release notes

- Worktree `.claude/worktrees/feature/typed-targets-docs`. Depends on PR 1–3 (merged).
- Files: new `docs/typed-targets.md` (guide: when to use; `open Xake.Typed`; carriers; forms D/B;
  `Format`/`Paths`/`For`/`Glob`/`Make`; `Command<'G>` = always runs; when each mistake fires —
  the table of `core-without-tp.md` §3; migrating a `getRuleMatch` rule; run-time patterns;
  `PatternAst` for library authors); `docs/index.md` (link); `docs/overview.md`, `docs/cheatsheet.md`
  (rules section: typed form next to `getRuleMatch`, which stays documented, 7); `docs/roadmap.md`
  (typed matches done, "Rules with arguments" still open, 11); new `samples/typed-targets.fsx`
  (references `out/netstandard2.0/Xake.dll` like the other samples); `docs/release-notes.md`
  (final 3.12.0 section, §3).
- Acceptance: `dotnet fsi samples/typed-targets.fsx` runs after `dotnet fsi build.fsx -- -- build`;
  every code block in `docs/typed-targets.md` is taken from the sample or a test.
- Size: M (1 day). Model: sonnet.

**Release** (user): tag `v3.12.0` → published `3.12.0.<run>`.

### PR 5 — `feature/hermetic-on-xake-3.12`: hermetic rebuilt on the new base

- Worktree `.claude/worktrees/feature/hermetic-on-xake-3.12`. Depends on the 3.12 release.
- Files: `src/hermetic/Xake.Hermetic.Dotnet.fsproj` (`Xake` range `[3.12.0.<run>, 3.13)`, version
  0.8.0), any `Level` case / `Target` annotation in `src/hermetic/*.fs` and `src/hermetic.tests`,
  `src/hermetic/xake.project.json`, `locks/Xake.Hermetic.Dotnet/Release.json`, `xake.fingerprints`,
  `docs/hermetic/README.md` (notes).
- Steps: bump the range; build; `dotnet fsi build.fsc.fsx -- -- import` and
  `update-locks -d LOCK_REFRESH=Xake` (session trap #72/#79); `dotnet test src/hermetic.tests`.
- Acceptance: hermetic both TFMs 0 warnings, hermetic tests green, `check` passes.
- Size: S. **Release** (user): tag `hermetic-v0.8.0`.

### PR 6 — `feature/dogfood-typed-targets`: build scripts on `Xake.Typed`

- Worktree `.claude/worktrees/feature/dogfood-typed-targets`. Depends on PR 5's release.
- Files: `build.fsx`, `build.fsc.fsx`, `testjobs.fsx` (if the `test::` names move into a shared
  `Command<..>`), `docs/session.md` (bootstrap versions).
- Steps: `#r "nuget: Xake, 3.12.0.<run>"` in both scripts and `Xake.Hermetic.Dotnet, 0.8.0.<run>`
  in `build.fsc.fsx` (`build.fsx` jumps from 3.7 — read the 3.8–3.12 notes for other breaks, e.g.
  `RmArgs`); declare records + `Target<..>`/`Command<..>` for the rules now using `getRuleMatch`
  (`build.fsx:109,117,138-143,181,191`; `build.fsc.fsx:47-49,60-67,74-83`); generate need-lists with
  `X.For [ .. ]` (e.g. `"build" <==` in `build.fsx:103`, `update-locks` in `build.fsc.fsx:56`).
- Behaviour: a `=>` phony rule with groups moved to `Command<>` gains AlwaysRerun (21) — decide per
  rule (§4 item 4); keep `=>` where skipping is wanted.
- Acceptance: `dotnet fsi build.fsx -- -- build test pack` and `dotnet fsi build.fsc.fsx -- -- build check`
  succeed; second run of `build` is a no-op except commands; CI `build` and `build-fsc` green.
- Size: M (1 day).

### PR 7 — `feature/patterns-provider`: the `Xake.Patterns` type-provider package

- Worktree `.claude/worktrees/feature/patterns-provider`. Design/coding can start after PR 3 merges,
  against a local feed (`dotnet pack src/dotnet -p:Version=3.12.0.99 -o /tmp/xake-feed`,
  `-d NUGET_SOURCE=`, `docs/devprocess.md`; delete `~/.nuget/packages/xake/3.12.0.99` afterwards).
  Merges after 3.12 is on nuget.org. Parallel with PR 5/6.
- Files: new `src/patterns/` (`Xake.Patterns.fsproj`, `AssemblyInfo.fs` with
  `TypeProviderAssembly("Xake.Patterns.DesignTime")`, minimal glue only), new
  `src/patterns.designtime/` (`Xake.Patterns.DesignTime.fsproj`, `Provider.fs`), new test project
  `src/patterns.tests/` (net9.0), `build.fsx` (`pack-patterns`, `push-patterns`, `PatternsVersion`
  var), `.github/workflows/publish.yml` (tag prefix `patterns-v*`), `.github/workflows/build.yml`
  (build + test the package), `docs/devprocess.md` (packages table, release order), new
  `docs/patterns-provider.md` (usage + IDE notes), `docs/index.md`.
- Steps:
  1. Port `08-consolidated/designtime/Provider.fs` with the decided names: provided
     `Target<"p1", ?"p2", ..>` (labels `"label=pattern"`, `Out1`..; group and output properties;
     `_1`..), `Command<"...">`; members `Format` (both types), `Pattern` (pattern text), `Make`,
     `Glob` (Target only), `Rule` (D), `rule` (B), **`All(g = [..], ..)`** cross product (37, replaces
     08's list-taking `For`); no `Need`/`TryMatch` (25). Static checks as in 08 (FS3033 for groups
     named like members, label = group, multi-output mismatch).
  2. Erase onto Core (36): provided instances erase to `Xake.Typed.PatternMatch`; members call
     `Xake.Typed.Pattern` (`FormatAll`, `Glob`, `Match`, `FileRule`) and `Command` paths through
     `RuleBuilder.command`; form B returns Core's `TypedRuleBuilder<_>` bound to Core's `matched`.
     Delete 08's `Runtime.fs` (`PatternRuntime`, own builders). Same runtime messages as Core.
  3. Packaging (exp 06/08): package `Xake.Patterns`, dependency `Xake` `[3.12.0.<run>, 3.13)`;
     `lib/{net462,netstandard2.0}/Xake.Patterns.dll`; `typeproviders/fsharp41/netstandard2.0/` =
     the whole design-time output (`Xake.Patterns.DesignTime.dll`, `Xake.Patterns.dll`, `Xake.dll`
     from the referenced package), `NoWarn NU5100;NU5118`, FSharp.Core 8.0.100.
  4. TP SDK from its package (16): `PackageReference FSharp.TypeProviders.SDK 8.11.0
     GeneratePathProperty ExcludeAssets=all PrivateAssets=all`, compile
     `$(PkgFSharp_TypeProviders_SDK)/src/ProvidedTypes.fs(i)`; `NoWarn FS0026;FS3005` there only.
  5. Assembly identity (README 08 lesson): unique names `Xake.Patterns` / `Xake.Patterns.DesignTime`,
     `AssemblyVersion` = package version (never 1.0.0.0); the experiments' dlls already carry
     `Exp08`; the in-repo copy target (08 `CopyToRuntimeBin`) puts the design-time output in
     `src/patterns/bin/typeproviders/fsharp41/netstandard2.0/` for the tests.
  6. `docs/patterns-provider.md`: `open Xake` then `open Xake.Patterns`; do not open `Xake.Typed` in
     the same script (FS0618), qualified `Xake.Patterns.Target<"...">` works; the DU is
     `BuildTarget`; `[<Literal>]` patterns only (10); IDE: restart the F# language server after an
     upgrade, the design-time dll is locked/cached; a project referencing only `Xake` never needs it.
- Tests: the 08 consumer rules (`consumer/Rules.fs`) run in forms D and B and give the same build
  lines as PR 3's Core version; `All` cross product; the 08 negative cases as type-check fixtures
  (FS0039 misspelt group/label, FS0496, FS3033 name collisions and multi-output mismatch);
  coexistence: `open Xake.Typed` + qualified `Xake.Patterns.Target<"...">` in one script (34);
  `#r "nuget: Xake.Patterns"` from a local feed in fsi SDK 8 and 10; a net462 consumer builds;
  a script referencing only `Xake` has no FS3031.
- Risks: design-time loads its own `Xake.dll` next to the script's — keep the dependency range
  narrow (one minor); Windows file locking and Rider/VS untested (manual check before release);
  `build.fsc.fsx` cannot build the package (§4 item 6).
- Size: L (~400 lines code + tests + packaging, 3–5 days). **Release** (user): `patterns-v0.1.0`.

### Later — `feature/hermetic-typed-paths`

`lockOf`/`outOf`/`runOf` from patterns (`hermetic-sketch.md`; option 1 in `build.fsc.fsx` first). Not planned yet.

### Dependencies and parallelism

```
PR0 ─┬─ PR1 ──┬─ PR3 ── PR4 ── release v3.12.0 ── PR5 ── release hermetic-v0.8.0 ── PR6
     └─ PR2 ──┘              └─ PR7 (local feed; merge after v3.12.0) ── release patterns-v0.1.0
```
PR1 ∥ PR2. PR3 needs PR1 only. PR4 after PR3 (and PR2 for the `BuildTarget`/`Level` docs).
PR7 ∥ PR5/PR6. One worktree per PR at a time; at most two code PRs open against `dev`.

## 3. Release and versioning

- **`Xake` 3.12.0** (PR 1–4; also carries the unreleased most-specific-rule change #88/#89, which
  is not in `v3.11.0` but sits under a stale `3.10.1 (unreleased)` heading although `v3.10.1` is
  tagged — move it under `3.12.0`). Minor bump with breaking notes, as #88 (or 4.0.0, §4 item 3):
  1. **Breaking — literal `( ) [ ] |`** in rule patterns and fileset masks (`!!`, `ls`, `Fileset`):
     they match themselves; `[(]` and character classes / `|` alternation no longer work.
  2. **Breaking — `Level` is `[<RequireQualifiedAccess>]`**: write `Level.Command`; `trace Info ..`
     keeps working (shim); a `match` on unqualified level cases must be qualified (else FS0049 and a
     catch-all).
  3. **Breaking — the DU `Target` is `BuildTarget`** (cases unchanged); annotate
     `ctx.Targets : BuildTarget list`; compiled consumers rebuild; `Xake.Hermetic.Dotnet` needs 0.8.
  4. Fixed: multi-file rule names with an empty group value or a `)` in a value.
  5. New: public `Xake.PatternAst`; typed patterns `open Xake.Typed` (link `docs/typed-targets.md`).
  6. Unchanged: `getRuleMatch` (unknown group still `""`).
- **`Xake.Hermetic.Dotnet` 0.8.0** (PR 5): requires `Xake` 3.12; no API change.
- **`Xake.Patterns` 0.1.0** (PR 7): new preview package, own version line and tag prefix
  `patterns-v`, requires `Xake` 3.12; release order base first (as for hermetic).
- Dogfooding bumps (`build.fsx`, `build.fsc.fsx` `#r`) only after the packages are on nuget.org,
  to the exact `X.Y.Z.<run>` (`docs/session.md`).

## 4. Open items for the user

1. DU rename target name **`BuildTarget`**, no alias — OK? (implementation of 26)
2. **Shim values** `Message`/`Error`/`Warning`/`Info`/`Debug`/`Verbose`/`Never` next to the
   qualified `Level`, so `trace Info` keeps compiling — OK? (implementation of 35)
3. Version: **3.12.0** with breaking notes (project precedent) or 4.0.0 (semver)?
4. Build scripts: phony rules with groups now `=>` (`test-build::`, `test::`, `lock::`) — move to
   `Command<>` (AlwaysRerun) or keep `=>` with the typed match read some other way?
5. Compile-time negative tests through `dotnet fsi --typecheck-only` (Integration, ~3 s each) —
   accept, or keep the C cases as documented fixtures only?
6. `build.fsc.fsx` building the provider package (open part of 16): defer — `Xake.Patterns` is
   built by `build.fsx`/msbuild only for 0.1?

## 5. How to execute

- The orchestrator (this session's role) creates each worktree:
  `git worktree add .claude/worktrees/feature/<name> -b feature/<name> origin/dev`, adds a row to
  the PR table in `docs/features/typed-matches/session.md`, and spawns one subagent per PR in it.
- Models: **opus** — PR 1, PR 2 (compat design), PR 3, PR 6, PR 7; **sonnet** — PR 0, PR 4, PR 5.
  Never fable for subagents.
- Subagent brief (per PR): "Work only in `<worktree>`. Read `CLAUDE.md`, `docs/session.md`,
  `docs/features/typed-matches/{brief,plan}.md` (§2 PR n is your scope; brief decisions are binding),
  and the experiment files the PR names. Create `docs/features/<name>/{session,tracker}.md`.
  Implement the steps; add the listed tests; run the common acceptance commands; do not push, do
  not touch other branches or `experiments/`; no customer names. Commit on the branch with the
  attribution trailer. Report: files changed, test counts before/after, warnings, deviations from
  the plan with reasons, behaviour changes for the release notes, open questions."
- After each subagent: the orchestrator reads the report and `git diff --stat`, runs the acceptance
  commands once more, then asks the user to push and open the PR
  (`gh pr create --repo OlegZee/Xake --base dev`); after the merge it gives the user
  `git worktree remove .claude/worktrees/feature/<name>` and updates the tracker.
- Releases and pushes are the user's; dogfooding (PR 6) and the provider merge (PR 7) start only
  when the needed packages are visible on nuget.org.
