# Tracker: the most specific rule wins

Status: `todo` · `wip` · `done`. One line per item.

## Core

- [x] `done` Specificity tokenizer + `Specificity`/`specificity` in `Path.fs`, cached per pattern
  string beside `regexCache`. Grammar = the matcher's own. `?` lighter than `*`/group; `**` at rank 2.
- [x] `done` `explainMatch : Rules -> projectRoot -> target -> RuleMatchInfo list` in `ExecCore.fs`
  — all matches with specificity, sorted most-specific-first then last-declared. Single source of truth.
- [x] `done` `locateRule` now = `explainMatch >> List.tryHead`; return shape unchanged.
- [x] `done` Tie detection → `Warning` with the self-explaining breakdown; identical pattern strings silent.
- [x] `done` `FileConditionRule` fixed lowest specificity (`conditionSpecificity`); pattern rule always wins.
- [x] `done` `MultiFileRule` uses the matched pattern's specificity; `DelegatedRule` uses inner's.

## Switch & diagnostics

- [x] `done` `RuleMatchMode` in `Types.fs`; `ExecOptions.RuleMatch` + `EngineOptions.RuleMatch`
  (default `MostSpecific`), wired in `ScriptRunner.toEngineOptions`. `-d RULE_MATCH=last` overrides
  at runtime (resolved in `execOne`).
- [x] `done` `-d EXPLAIN_RULE=<target>` prints the candidate table + winner.

## Tests (`src/tests/RuleSpecificityTests.fs`)

- [x] `done` obf-style: specific-with-literal declared **before** general → specific wins.
- [x] `done` `clean::(b:*)` vs `clean::(b:*)::(cfg:*)` on two-arg and one-arg targets.
- [x] `done` `**` vs `*` and `?` vs `*` ranking; named group == bare wildcard.
- [x] `done` ambiguous equal-specificity different patterns → `Warning` + last-declared chosen.
- [x] `done` regression: identical-pattern override stays silent & last-declared.
- [x] `done` legacy switch (`RULE_MATCH=last`) restores old order.
- [x] `done` pattern-rule-beats-condition-rule.
- [x] `done` full unit suite green (416 passed, 1 skipped); `RuleMatchTests`/`FlatScriptTests` unchanged.

## Docs & rollout

- [x] `done` `docs/overview.md` "Rule selection" section (algorithm + examples + switches).
- [x] `done` Release note in `docs/release-notes.md` (unreleased section).
- [ ] `todo` Flip roadmap item from "Not started" — deferred to merge PR (user has an uncommitted
  `roadmap.md` edit in the main checkout; avoid the conflict here).
- [ ] `todo` Add `docs/session.md` engine fact — deferred to merge PR for the same reason.
- [ ] `todo` Follow-up roadmap item: dedicated CLI verb `explain <target>` (not in this change).

## Open / not done

- [ ] `todo` `locateRule` benchmarks (`RuleMatchTests.fs:37-73`, `Explicit`) — not run; the
  all-matches scan is the already-worst-case O(#rules) with cached regexes, but measure before release.
- [ ] `todo` Self-hosting `build.fsx`/`build.fsc.fsx` bootstrap from the **published** package, so
  they do not exercise this change until the next release; re-verify after publish.
</content>
