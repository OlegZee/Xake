# Session: dep-cutoff (Xake 5.0)

Branch `feature/dep-cutoff` (worktree `.claude/worktrees/dep-cutoff-dabef6`), cut from `dev` at
`1837856`. Brief: [brief.md](brief.md) (copied as-is 2026-11-17). Owner decisions from the
kickoff: content cut-off on by default, `--content-cutoff=off` on the command line only; error
policy, `also` shorthand, multi-output deprecation and a header on `*..>` are out of scope; the
brief's Acceptance is the spec; where the brief and the 4.0 sources disagree the sources win.

## Where things stand (2026-11-17)

Step 0 (spike) done: [probe.md](probe.md), prototype in [spike/](spike/). Proposed: the header on
`target "p" { .. }` and typed `X.rule { .. }`; operations `also`, `needs` (string or list),
`noContentCutoff`, `contentCutoff`; `..>` unchanged. Six brief-vs-sources differences are listed
in probe.md (error codes are FS0001/FS0041/FS3086, FS3095 only via a `CompilerMessage` overload;
`For` returns header+body; `rule {}` collides with the script op; args not literal-only; 4.0 does
not detect a hand-edited output; the public `Rule` DU changes shape). Reported to the orchestrator.

## Next step

Wait for the owner's approval of the syntax. Then: the fresh-eyes critic subagent (model Fable,
gets brief, probe, code, docs only), owner decides on findings, accepted ones into brief.md; then
docs from the brief's API examples, then step 1.
