# Session: dep-cutoff (Xake 5.0)

Branch `feature/dep-cutoff` (worktree `.claude/worktrees/dep-cutoff-dabef6`), cut from `dev` at
`1837856`. Brief: [brief.md](brief.md) (copied as-is 2026-11-17). Owner decisions from the
kickoff: content cut-off on by default, `--content-cutoff=off` on the command line only; error
policy, `also` shorthand, multi-output deprecation and a header on `*..>` are out of scope; the
brief's Acceptance is the spec; where the brief and the 4.0 sources disagree the sources win.

## Where things stand (2026-11-17, step 1)

Step 0 done ([probe.md](probe.md), [spike/](spike/)); owner decisions in brief.md "Decisions".
The fresh-eyes review is in [critic.md](critic.md) (4 critical, 13 major, 13 minor, each triaged
to a step); it awaits the owner.

Step 1 implemented (local commits, see `git log`):
- `RuleHeader` and `Headed<'ctx>` in `Types.fs`; `Resource.fs` moved before `Types.fs`.
- `FileRule (outputs, header, recipe)` replaces `FileRule`/`MultiFileRule`;
  `FileConditionRule (cond, header, recipe)`; `Rule.validate` checks header groups, `..?>` limits.
- Header operations on `RecipeBuilder` (`also`, `needs` string/list, `requires` resource/tuple,
  `noContentCutoff`, `contentCutoff`), `For`/`Delay` overloads, error-marked `Bind`s (FS3095).
- `..>`, `*..>`, `..?>` are inline SRTP over `RuleBody` (`XakeScript.fs`); `target`/`targets` are
  `FileRuleBuilder`, `command` is `CommandBuilder`; typed `X.rule` is `TypedFileRuleBuilder`.
- `ExecCore.withHeader`: static needs (`resolveNeeds`: groups substituted, single path via
  `makeTarget`, fileset as `GetFiles` + files) through `execNeed` with the task context, then the
  locks (`RuleHeader.Locks`: merged, name order, yielded slot), then the body; applied to delegated
  rules too. `requiring` on file rules adds to the header.
- Tests: `src/tests/RuleHeaderTests.fs` (14), `fixtures/header-neg/cases.fsx` run by
  `TypedCompileTests.fs`. Docs: `docs/rule-header.md`, cheatsheet, release notes.
- Not yet: `ruleHeader`/`including` (owner's choice pending), `also` behaviour beyond matching
  (step 2), the verdict move (step 3: until then static needs run only when the target runs).

## Next step

Report step 1 to the orchestrator with the critic summary. Then, on the owner's answers:
`ruleHeader`/`including` with the chosen group-substitution option; accepted critic findings into
brief.md; then step 2 (`also`: missing-output rebuild with current outputs, M1 overlap check,
`needs` naming an own `also`, missing declared output after the body = error).
