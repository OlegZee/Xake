# Session: dep-cutoff (Xake 5.0)

Branch `feature/dep-cutoff` (worktree `.claude/worktrees/dep-cutoff-dabef6`), cut from `dev` at
`1837856`. Brief: [brief.md](brief.md) (copied as-is 2026-11-17). Owner decisions from the
kickoff: content cut-off on by default, `--content-cutoff=off` on the command line only; error
policy, `also` shorthand, multi-output deprecation and a header on `*..>` are out of scope; the
brief's Acceptance is the spec; where the brief and the 4.0 sources disagree the sources win.

## Where things stand (2026-11-17, later)

Step 0 done ([probe.md](probe.md), [spike/](spike/)). The owner approved the syntax (`target` /
`X.rule`; `also`, `needs`, `noContentCutoff`) and the `FileRule`/`MultiFileRule` merge; both are
recorded in brief.md "Decisions". The owner then asked four questions; the answers are in probe.md
"Owner's questions". The main one: header operations on `recipe` itself plus an SRTP `..>`
compile the whole test suite unchanged (1744 passed), so the proposal is now one syntax (the header
in `recipe`, accepted by every file-rule form). Also proposed: a header on `targets`/`*..>` and
on `..?>` (no `also` there), `requires <resource>` (static needs, then acquire, then body), and
masks-list sugar later. Reported to the orchestrator.

## Next step

Wait for the owner's decisions on the four answers; record them in brief.md "Decisions". Then the
fresh-eyes critic subagent (model Fable; gets brief, probe, code, docs only), owner decides on its
findings, then docs from the API examples, then step 1. Do not start step 1 before that.
