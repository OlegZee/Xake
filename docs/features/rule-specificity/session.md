# Session: rule-specificity

Branch `feature/rule-specificity`, worktree `.claude/worktrees/feature+rule-specificity`.

## Where things stand

Spec is settled and locked in `brief.md` (all §9 decisions + the §10 explain-conflicts scope
resolved with the user, 2026-10-23). No engine code written yet — this session produced the
design only. Plan is in `tracker.md`.

## Exact next step

Start the core item in `tracker.md`: add the specificity tokenizer and `SpecKey` to
`src/core/Path.fs`, cached per pattern string beside `regexCache` (`Path.fs:191`), using the
matcher's own token grammar (`Path.fs:182`). Then `explainMatch` in `ExecCore.fs`, then rewrite
`locateRule` (`ExecCore.fs:85`) to pick the head-most strictly-max candidate.

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
