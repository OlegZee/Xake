# Xake 5.0 phase 2: the rule header and side outputs

The specification of phase 2 as implemented. Phase 1 (`File`, database 0.7, epochs) is
[db-paths-simple](../db-paths-simple/brief.md) (OlegZee/Xake#141); phase 3 (hashing and content
cut-off) follows. The user reference is [rule-header.md](../../rule-header.md); the engine rules are
in [implnotes.md](../../implnotes.md) ("Dependency storage"); the release notes in
[release-notes.md](../../release-notes.md), "5.0.0".

The header was designed and implemented once before, in a closed first attempt (OlegZee/Xake#140,
tag `archive/xake5-attempt1`, items 1a-1g, 1i, 2 of its tracker). Its header decisions stand and
were ported; its database layer, `RunSummary` and the cut-off were not.

## Decisions (owner)

- 2026-11-17 (first attempt, kept): the header lives in `recipe { }`; every file-rule form takes
  it (`..>`, `*..>`, `..?>`, `target`, `targets`, `X.rule`), phony forms and `X.Rule <| fun m -> ..`
  reject it at compile time; `X.RuleWith h <| fun m -> ..` takes a `ruleHeader` value (form D2).
  `FileRule` and `MultiFileRule` merge into one case, outputs main first. `needs` (one or a list)
  are one unordered set requested together before the body. `requires lock` / `(lock, n)`: locks
  after the needs, in name then creation order. `ruleHeader ["n"] { }` + `including` with declared
  groups (option A). Errors: M1 overlapping outputs, M2 an unwritten declared output, M8 a
  header-only file rule, M12 a resource named twice. M9: a changed static set reruns the target.
- 2026-11-18: the phase 2 design approved as proposed, with M9 **in the verdict** (the `current`
  argument of `getChangeReasons`), not in `run` as in the first attempt, so a dependent sees the
  reason in the same run. The size (519 changed core lines against a 452 estimate) accepted.

## 1. Header

`RuleHeader` (`Types.fs`): `Also`, `Needs`, `ContentCutoff` (parsed and stored; used by phase 3),
`Requires`, `Included`, `HeaderOnly`; `Locks` (by name, then creation ordinal), `Merge`.
`SharedHeader` from `ruleHeader`; `Headed<'ctx>` is a recipe with a header, accepted only by the
rule forms. The operations live once in `HeaderBuilder`, inherited by `recipe` and `ruleHeader`.
`Rule.validate` checks, when the script loads: XP013/XP014 over all outputs, groups of `needs` and
of included headers, `needs` of an own output, M8, M12, a condition rule without `also` or groups.

## 2. Execution

`execOne` (`ExecCore.runHeaded`), when the verdict says the target must run:

1. resolves the `needs` with the match groups (`resolveNeeds`: no wildcard -> one target through
   `makeTarget`; wildcard or braces -> a fileset of the files on disk, recorded as `GetFiles`) and
   requests them together through `execNeed` (CPU slot yielded); the wait goes to the "all" step;
2. takes the `requires` locks in `Locks` order, yielding the slot while waiting;
3. runs the body on a **fresh** result holding them (`HeldResources`), releasing them in `finally`;
4. stores `Depends = headerDeps @ bodyDeps`, `StaticCount = headerDeps.Length`.

The body's recording order (`need` appends; `getVar`, `getEnv`, `getFiles`, `alwaysRerun`
prepend; `setResult` replaces) cannot move the header entries. A delegated rule runs the same steps
inside `bodyThunk`; a result the executor returns without the body is stored as returned. Header
dependencies are observed like any dependency (phase 1's epochs); no database change.

## 3. Verdict (M9) and side outputs

`getChangeReasons ctx current` takes `ExecCore.currentRule`: the selected rule's current outputs and
static set. Two reasons are added to phase 1's:

- **an output is missing**: one of the rule's current outputs does not exist (an `also` added since
  the last run included);
- **the static needs changed**: the keys of the first `StaticCount` recorded dependencies differ,
  as a set, from the rule's static set now. Key (`StaticKey`): a file or target by `Name`, a fileset
  by itself; the current set is resolved without building (a fileset with its files now).

They flow through the verdict memo, so dependents see them (the transitive rule). A result built
before phase 2 has `StaticCount = 0`, so a rule with `needs` reruns once.

Side outputs: a match of any output of a rule names them all, main first; M1 (another output of the
selected rule selects another rule) and M2 (a declared output not written, nothing recorded) fail
the build naming the rule and the file. `also` and rule patterns match ordinally; a wildcard
`needs` is a fileset and ignores case.

## 4. Size

Core 519 changed lines (424 added, 95 removed): `Types.fs` 152, `ExecCore.fs` 144,
`RecipeBuilder.fs` 58, `XakeScript.fs` 57, `TypedTargets.fs` 34, `DependencyAnalysis.fs` 32,
`RuleBuilder.fs` 24, `Resource.fs` 12, `ScriptRunner.fs` 4, `Xake.fsproj` 2. The first attempt's
code for the same scope was 468; the M9 hook is about 55 lines it did not have.
