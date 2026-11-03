# Session: rule-specificity

Branch `feature/rule-specificity`, worktree `.claude/worktrees/feature+rule-specificity`.

## Where things stand

Implemented end to end (2026-10-23). Spec locked in `brief.md`; all tracker core/switch/
diagnostics/tests/docs items `done`. Full unit suite green: **416 passed, 1 skipped**; core builds
both TFMs with 0 warnings. Committed on this branch.

What landed:
- `Path.specificity` / `Path.Specificity` (tokenizer + cache) — `src/core/Path.fs`.
- `explainMatch` + `RuleMatchInfo` + `conditionSpecificity`, `locateRule` rewritten, selection +
  ambiguity `Warning` + `-d EXPLAIN_RULE` + `-d RULE_MATCH=last` in `execOne` — `src/core/ExecCore.fs`.
- `RuleMatchMode` in `Types.fs`; `RuleMatch` on `ExecOptions` (XakeScript.fs) and `EngineOptions`
  (ExecTypes.fs), wired in `ScriptRunner.toEngineOptions`.
- `src/tests/RuleSpecificityTests.fs` (registered in `tests.fsproj`).
- Docs: `overview.md` "Rule selection" section; `release-notes.md` unreleased bullet.

## Exact next step

Open the PR to `dev`. In the PR (not here, to avoid the user's uncommitted `roadmap.md` edit):
flip the roadmap item from "Not started" and add the engine fact to `docs/session.md`. Before
release, run the `Explicit` `locateRule` benchmarks (`RuleMatchTests.fs:37-73`) to confirm the
all-matches scan has no regression, and add the follow-up roadmap item for a CLI verb `explain`.

## Settled decisions (do not reopen)

- Default `MostSpecific`; legacy `-d RULE_MATCH=last` deprecated after one minor.
- `?` more specific than `*`; `**` penalized at rank 2 (ahead of total wildcard count).
- Pattern rule always beats `FileConditionRule`; tie across different patterns → `Warn`.
- Identical pattern strings → silent last-declared (keep `FlatScriptTests` green).
- `explainMatch` is the single source of truth for selection, the tie Warn, and
  `-d EXPLAIN_RULE=<target>`; docs section mandatory; CLI verb `explain` deferred.

## Notes

- Left `docs/roadmap.md` untouched on purpose: the user has an uncommitted edit to it in the main
  checkout. Flip the item's "Not started" status in the PR that lands the code, not here.
</content>
