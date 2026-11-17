# Session: dep-cutoff (Xake 5.0)

Branch `feature/dep-cutoff` (worktree `.claude/worktrees/dep-cutoff-dabef6`), cut from `dev` at
`1837856`. Brief: [brief.md](brief.md) (copied as-is 2026-11-17). Owner decisions from the
kickoff: content cut-off on by default, `--content-cutoff=off` on the command line only; error
policy, `also` shorthand, multi-output deprecation and a header on `*..>` are out of scope; the
brief's Acceptance is the spec; where the brief and the 4.0 sources disagree the sources win.

## Where things stand (2026-11-17, end of the spike rounds)

Step 0 done ([probe.md](probe.md), [spike/](spike/)). Owner decisions, all in brief.md
"Decisions": the header lives in `recipe` and every file-rule form accepts it (phony rejects);
header on `targets`/`*..>` in 5.0; `..?>` header without `also`, `needs` without groups;
`requires lock` / `(lock, n)` (needs and verdict first, lock only when the target runs);
`needs` list kept, all needs one unordered set; `FileRule`/`MultiFileRule` merged; masks list later.
Second-round answers in probe.md: a headed recipe nested in a recipe is always a compile error
(`spike/nested.sh`); helper `need` stays dynamic; proposed `ruleHeader { }` + `including h` for
helpers contributing header items (awaiting the owner). Reported to the orchestrator.

## Next step

Wait for the owner to see the second-round answers and decide on `ruleHeader`/`including`. Then
the fresh-eyes critic subagent (model Fable; gets brief, probe, code, docs only), owner decides
on its findings (accepted ones into brief.md), then docs from the API examples, then step 1
(header in `recipe`, SRTP rule operators, static `needs`, `requires`). The prototype to start
from: [spike/recipe-header.patch](spike/recipe-header.patch) on `1837856`.
