# Tracker: the most specific rule wins

Status: `todo` · `wip` · `done`. One line per item. Keep current as work lands.

## Core

- [ ] `todo` Specificity tokenizer + `SpecKey` in `Path.fs`, cached per pattern string beside
  `regexCache` (`Path.fs:191`). Grammar = the matcher's own (`Path.fs:182`). `?` lighter than
  `*`/group; `**` penalized at rank 2.
- [ ] `todo` `explainMatch : Rules -> projectRoot -> target -> Candidate list` in `ExecCore.fs`
  — all matches with `SpecKey`, sorted, winner + tie partners flagged. Single source of truth.
- [ ] `todo` Rewrite `locateRule` to pick the head-most strictly-max candidate via `explainMatch`
  (replace `List.tryPick`, `ExecCore.fs:85`). Return shape unchanged.
- [ ] `todo` Tie detection → `Warn` with the self-explaining breakdown line; identical pattern
  strings stay silent.
- [ ] `todo` `FileConditionRule` fixed lowest specificity; pattern rule always beats condition rule.
- [ ] `todo` `MultiFileRule` uses the matched pattern's specificity; `DelegatedRule` uses inner's.

## Switch & diagnostics

- [ ] `todo` `ExecOptions.RuleMatch : MostSpecific | LastDeclared`, default `MostSpecific`; fed by
  `-d RULE_MATCH=last`. Legacy path = today's `List.tryPick`.
- [ ] `todo` `-d EXPLAIN_RULE=<target>` prints the `explainMatch` candidate table + winner.

## Tests

- [ ] `todo` obf-style: specific-with-literal declared **before** general → specific wins (fails today).
- [ ] `todo` `clean::(b:*)` vs `clean::(b:*)::(cfg:*)` on two-arg and one-arg targets.
- [ ] `todo` `**` vs `*` and `?` vs `*` ranking at equal literals.
- [ ] `todo` ambiguous equal-specificity different patterns → `Warn` emitted + last-declared chosen.
- [ ] `todo` regression: identical-pattern override stays silent & last-declared
  (`FlatScriptTests.fs:90,101`).
- [ ] `todo` legacy switch (`RULE_MATCH=last`) restores old order.
- [ ] `todo` extend the direct `locateRule` test (`RuleMatchTests.fs:20-35`) with specificity cases;
  check it still builds `Rules` raw (bypasses builder reversal).
- [ ] `todo` run the `locateRule` benchmarks (`RuleMatchTests.fs:37-73`) — no regression from
  all-matches evaluation.

## Docs & rollout

- [ ] `todo` Docs section: the algorithm (tuple + token semantics) with worked examples; the
  LLM-quotable explanation.
- [ ] `todo` Release note: behavior change + `RULE_MATCH=last` switch + one-minor deprecation.
- [ ] `todo` Flip roadmap item from "Not started"; add engine fact to `docs/session.md`.
- [ ] `todo` Follow-up roadmap item: dedicated CLI verb `explain <target>`.

## Verify

- [ ] `todo` `dotnet test src/tests -c Release` green (incl. new + untouched order tests).
- [ ] `todo` `dotnet fsi build.fsx -- -- build test` self-hosting build passes.
</content>
