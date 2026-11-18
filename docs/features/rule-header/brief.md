Xake 5.0, phase 2: the rule header and side outputs (`also`). Report a short design (what is ported, what changes, line budget) before code.

## Context

Xake 5.0 is done in phases (plan approved by the owner 2026-11-17). Phase 1 is merged into `dev` (OlegZee/Xake#141, squash `b6aa7c1`): `File` as the normalised absolute path, ordinal rules, database 0.7 with epochs, `FileRec`, dependencies by id (`FileDep`, `TargetDep`), `StaticCount` (written as 0 so far). Phase 3 (hashing and content cut-off) comes after this phase in another session.

The rule header was already designed and implemented once, in a closed first attempt (PR OlegZee/Xake#140; code at tag `archive/xake5-attempt1`, `e034997`). Its header decisions stand. Its database layer was rejected and redone in phase 1; do not bring any of it back.

## Repository and branch

- `~/projects/Xake`. Your worktree is cut from local `dev`. First: `git fetch`, check `git log -1` equals `origin/dev` (`b6aa7c1` or later); if not, stop and report. Rename: `git branch -m feature/rule-header`.
- Read `CLAUDE.md`, `docs/session.md`, `docs/implnotes.md`, `docs/features/db-paths-simple/` (brief, tracker incl. "Later", session). Create `docs/features/rule-header/` (`brief.md` = this brief + decisions, `session.md`, `tracker.md`).

## Reference (port from, do not merge)

At tag `archive/xake5-attempt1`:
- decisions: `docs/features/dep-cutoff/brief.md` ("Decisions" — header syntax, `ruleHeader`/`including` option A, `requires` order, errors M1/M2/M8/M12, form D = `X.RuleWith`), `docs/features/dep-cutoff/probe.md`, `critic.md`;
- code: `src/core/RecipeBuilder.fs`, `RuleBuilder.fs`, `TypedTargets.fs`, `XakeScript.fs`, `XakeScriptBuilder.fs`, the header/also parts of `Types.fs` and `ExecCore.fs`, `Resource.fs`; tests `src/tests/RuleHeaderTests.fs`, `src/tests/fixtures/header-neg/`; docs `docs/rule-header.md`.
- Tracker items to port: 1a, 1b, 1c, 1d, 1e, 1f, 1g, 1i, 2 (see `docs/features/dep-cutoff/tracker.md` there). NOT 1h (`RunSummary`) and NOT 3a-3c (cut-off, fast path): those belong to phase 3.

## Scope

- Header in `recipe` and every file-rule form (`..>`, `*..>`, `..?>`, `target`, `targets`, `X.rule`); phony forms and `X.Rule` reject it; `X.RuleWith h <| fun m -> ..`.
- Operations: `also`, `needs` (one or a list; all requested together before the body), `requires lock` / `(lock, n)`, `noContentCutoff` / `contentCutoff` (parsed and stored on the rule; used by phase 3), `ruleHeader ["n"] { }` + `including`.
- One file-rule case (`FileRule` and `MultiFileRule` merged, outputs main-first).
- `also`: lookup by a side output builds the main target; a missing declared output reruns the rule; an undeclared output not written by the body is an error (M2); overlapping outputs of two rules is an error (M1); XP013/XP014; `needs` of an own output is an error.
- Errors: header-only file rule (M8), a resource named twice (M12).
- Docs `docs/rule-header.md`, release notes "5.0.0 (unreleased)".

## What changes against the first attempt (phase 1 results the port must absorb)

1. Dependencies are `FileDep fileId` / `TargetDep` (no `ArtifactDep2`, no stamps); `File.undefined` is gone; `FullName` is obsolete (use `Name` / `getSysFileName`).
2. **`StaticCount` becomes real and must be reliable.** The body's recording order is not (`need` appends; `getVar`, `getEnv`, `getFiles`, `alwaysRerun` prepend; `command` prepends `AlwaysRerun`; `setResult` and delegated executors replace the result). So `execOne` builds the header dependencies itself, runs the body on a fresh result, and stores `Depends = headerDeps @ bodyDeps` with `StaticCount = headerDeps.Length`.
3. **M9:** if the rule's current static set differs from the first `StaticCount` recorded dependencies, the target reruns (the verdict otherwise stays phase 1's).
4. Case: `also` and rule patterns are ordinal (as rules); a wildcard `needs` is a fileset and ignores case (as filesets).
5. Epochs: header dependencies are observed like any dependency; no new database record. The database format must not change in this phase; if you think it must, stop and report.

## Budget and rules

- Report the line budget per file in the design; the first attempt's header code is the upper bound. No new mechanisms beyond the scope; anything else goes to "Later" in the tracker for the owner.
- A fresh-eyes critic (subagent, model Fable) on the design is welcome; only correctness bugs are fixed without the owner.

## Done means

Tests for every scope item (port `RuleHeaderTests.fs` and the compile-negative fixtures, adapted), `StaticCount` order tests (header deps first even with `getVar` in the body), M9 test; `dotnet build src/core -c Release` 0 warnings; `dotnet test src/tests -c Release` green; `dotnet fsi build.fsx -- -- build test pack` green; Windows check on the VM (`ssh win11`, see `docs/session.md`/the phase 1 session notes) before the PR.

## Owner's rules

- A library recipe is a pure single-purpose function over Xake primitives; script logic is shown in doc examples.
- Until a feature is released, breaking changes are allowed.
- Xake is public: no product, customer or company names in code, commits, docs or PRs.
- Feature branches merge into `dev` by squash.
- Do not push and do not open a PR until the owner says so. Local commits are fine.
- Before ending the session, update `docs/features/rule-header/session.md`: where things stand and the exact next step.
- Report to the orchestrator session that started you at: the design, the end of implementation, before push.

## Decisions (owner)

- 2026-11-18: **the design below is approved as proposed** (316f8eb), with M9 in the verdict (the
  `current` hook in `getChangeReasons`). Implement docs first, then code and tests, within the
  452-line core budget; report the actual sizes.

## Design (approved 2026-11-18)

Ported from `archive/xake5-attempt1` (commits 6e20af1, 00462b8, 1bfd4c4, the `X.RuleWith` part of
219071e, bc86619), tracker items 1a-1g, 1i, 2. Not ported: `RunSummary` (1h), `ExecContext.Counters`,
`SummarySink`/`onSummary`, `LastSummary`, anything of 3a-3c, `ContentStore`, `PathTable`.

### Ported as is (names adapted: `getFullName` -> `Name`, `ShortName` -> `Display root`)

- `Types.fs`: `RuleHeader` (`Also`, `Needs`, `ContentCutoff`, `Requires`, `Included`, `HeaderOnly`,
  `Locks`, `Merge`), `SharedHeader`, `Headed<'ctx>`; `Rule`: `FileRule of outputs * header * recipe`
  (main first; `MultiFileRule` gone), `FileConditionRule of cond * header * recipe`;
  `Rule.outputs`, `Rule.share`, header checks and `Rule.validate` (XP013/XP014 over `outputs`,
  unknown group, `needs` of an own output, M8 header-only, M12 resource named twice, `..?>` without
  `also`/groups).
- `RecipeBuilder.fs`: the header operations (`also`, `needs` one/list, `requires r` / `(r, n)`,
  `noContentCutoff`, `contentCutoff`, `including`), `Yield`/`For`/`Delay` giving `Headed`, the two
  FS3095 `Bind` guards; `ruleHeader [groups] { }`. Change: the operations live once, in a base class
  both builders inherit, instead of being written twice.
- `RuleBuilder.fs` (`FileRuleBuilder`, `CommandBuilder`), `XakeScript.fs` (`RuleBody` SRTP for
  `..>`, `*..>`, `..?>`; `requiring` = `requires` in a file rule's header, a phony keeps the wrapped
  recipe), `TypedTargets.fs` (`TypedFileRuleBuilder`, `X.RuleWith`), `Resource.fs` (creation
  ordinal for the lock order).
- `ExecCore.fs`: `explainMatch` over the one file-rule case (a match of any output names them all,
  main first); M1 overlap check after selection; M2 `checkOutputsWritten` after the body (normal
  and delegated); `resolveNeeds` (no wildcard -> one target through `makeTarget`; wildcard or
  braces -> `Fileset.ls` of the glob with the groups, recorded as `GetFiles` plus its files).

### What changes against the first attempt

1. **Header outside the recipe.** The first attempt wrapped the body in a recipe (`withHeader`)
   that appended the header's dependencies to the running result. Now `execOne`'s `run` does it
   itself: (a) resolve the `needs` with the match groups and request them together through
   `execNeed` (CPU slot yielded) -> `headerDeps` (the `GetFiles` of wildcard needs first, then the
   targets' `FileDep`/`TargetDep`, as `execNeed` returns them); (b) take the `requires` locks in
   `Locks` order, yielding the slot while waiting; (c) run the body on a **fresh**
   `BuildLog.makeResult` holding the locks (`HeldResources`), release them in `finally`;
   (d) store `Depends = headerDeps @ body.Depends`, `StaticCount = headerDeps.Length`. Whatever the
   body does to its result (`need` appends, `getVar`/`getEnv`/`getFiles`/`alwaysRerun` prepend,
   `setResult` replaces) cannot move the header entries. Wait time of (a) goes to the step as before.
   Delegated rules: the same steps wrap `bodyThunk`; a result the executor returns without running
   the body is stored as returned (its own `StaticCount`).
2. **Dependencies by id.** Header dependencies are whatever `execNeed` records (`FileDep file` for a
   plain file, `TargetDep` for a rule target, observed `Recording` as any dependency); no stamps, no
   `ArtifactDep2`. No `File.undefined`, no `FullName`.
3. **M9 in the verdict, not in `run`.** The first attempt checked a missing `also` output in `run`,
   outside the verdict, so a dependent asking whether this target is out of date (the transitive
   rule) did not see it. Proposal: `DependencyAnalysis.getChangeReasons` takes one more argument,
   `current: Target -> (Target list * StaticKey list) option` (from `ExecCore`: the selected rule's
   current outputs and static set; None without a rule or header), and adds two reasons after the
   recorded dependencies:
   - "output X is missing" when one of the rule's **current** outputs does not exist (covers an
     `also` added since the last run; the old check covered only the recorded targets);
   - "its static needs changed" when the set of keys of the first `StaticCount` recorded
     dependencies differs from the current static set. Key: a file or target name (`FileDep f`,
     `TargetDep t` -> `Name`), or the fileset (`GetFiles (fs, _)` -> `fs`). The current set is
     resolved without building: single path -> `makeTarget` -> `Name`; wildcard -> the fileset and
     its files now (`toFileList`, as the `GetFiles` verdict already does). Compared as sets (the
     header is one unordered set).
   The three users of `getChangeReasons |> memoizeRec` (run, `--dryrun`/`--dump` in
   `ScriptRunner`, `dumpDeps`) pass the hook; the reasons then flow through the memo like any other,
   so dependents see them. A result from before phase 2 has `StaticCount = 0`, so a rule that has
   `needs` now reruns once (the static set changed from empty): correct, not a migration step.
4. **Case.** `also` and rule patterns match ordinally (phase 1's rule matching, nothing to do); a
   wildcard `needs` is a fileset and ignores case (phase 1's `Fileset.ls` default).
5. **No database change.** `StaticCount` is already in the 0.7 `Result` record; no new record, field
   or tag. `ExecContext` keeps its phase-1 shape (no `Counters`).

### Line budget (changed lines, core; the first attempt's header code in brackets)

| File | Budget | [attempt 1] | What |
|---|---|---|---|
| `Types.fs` | 140 | [150] | header types, one file-rule case, validation |
| `ExecCore.fs` | 125 | [121] | match over outputs, M1, M2, needs/locks/body/combine, `current` hook |
| `RecipeBuilder.fs` | 50 | [68] | header ops once in a shared base, `ruleHeader` |
| `XakeScript.fs` | 50 | [57] | `RuleBody`, operators, `requiring` |
| `DependencyAnalysis.fs` | 25 | [0] | the two M9 reasons, the hook argument |
| `TypedTargets.fs` | 25 | [25] | `X.rule` header, `X.RuleWith` |
| `RuleBuilder.fs` | 22 | [24] | file-rule and command builders |
| `Resource.fs` | 12 | [12] | creation ordinal |
| `ScriptRunner.fs` | 3 | [0] | pass the hook |
| **core total** | **452** | **[457]** | |

Tests: `RuleHeaderTests.fs` ported without "Run summary" (about 400 lines), plus `StaticCount`
order tests (header entries first with `getVar`/`getEnv`/`alwaysRerun`/`setResult` in the body;
delegated) and M9 tests (a `needs` added, removed, changed group; an `also` added; a dependent of the
target rebuilt through the transitive rule), about 80 lines; `fixtures/header-neg/cases.fsx`
(about 100) and its runner; small edits where tests build `FileRule`/`MultiFileRule` (about 50).
Docs: `docs/rule-header.md` without the cut-off parts (about 180), release notes "5.0.0
(unreleased)" (about 25), `implnotes.md` `StaticCount` paragraph (about 5).

### Open points for the owner

- **M9 placement** (change 3): in the verdict, with the hook argument. Alternative: in `run` as in
  the first attempt (fewer lines, about -15) but dependents do not see the reason in the same run.
- **Cost:** the hook runs rule selection once more per target per run (memoized with the verdict) and
  scans a wildcard `needs` fileset once for the static set (the `GetFiles` verdict scans it too).
