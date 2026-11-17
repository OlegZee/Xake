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
- `ruleHeader ["n"] { }` (`RuleHeaderBuilder`, `Rule.share` checks declared/used groups) and
  `including h` (`RuleHeader.Included` keeps each header's name and groups for the registration
  check in `Rule.validate`).
- Critic part 1 applied (owner, 2026-11-17): header-only file rule = registration error
  (`RuleHeader.HeaderOnly`, checked last in `Rule.validate`); a resource named twice on a rule =
  error (by name); locks ordered by name, then `Resource.CreationOrdinal`. M13: "Acceptance by
  step" and a `RunSummary` counters proposal in brief.md, for the owner.
- Verified 2026-11-17: `dotnet build src/core -c Release` 0 warnings, `dotnet test src/tests -c
  Release` 1764 passed / 10 skipped, `dotnet fsi build.fsx -- -- build test pack` green.
- Not yet: `also` behaviour beyond matching (step 2), the verdict move (step 3: until then static
  needs run only when the target runs).

Owner decision 2026-11-17: **no speculation in 5.0 (option B)**; brief Part 3 "Decision for a
dependent" and both Acceptance sections rewritten; critic C1, C3, C4 (speculation part), M10
closed; rule-header.md says "for cut-off through a rule's output, name it in `needs`".

Items 1h and 1i (2026-11-17): `RunSummary`/`RunCounters`/`IRunSummarySink` in `ExecTypes.fs`,
`ExecContext.Counters` (record shape test moved to "frozen within Xake 5"), increments in
`execOne.run`/`runDelegated`, summary logged by `ScriptRunner` (also on failure), `onSummary`
script operation, `XakeEngine.LastSummary` per demand; `Target<'G>.RuleWith`. A delegated rule's
header lock is tested. Verified: 0 warnings, 1768 passed / 10 skipped, `build test pack` green.

No epochs in 5.0 (owner, 2026-11-17; supersedes the C2 id decisions): dependents record the
`(Stamp, Size)` they saw; output records and source entries keep a history `(Stamp, Size) -> Hash
option` (last 8); a dependent decided unchanged by hash gets its pair refreshed. Brief Part 3 and
"Database (0.7)" rewritten; content epochs are a roadmap idea (docs/roadmap.md).

Step 2 done (2026-11-17, local): in `ExecCore.execOne` a missing current output reruns a
multi-output rule; `checkOutputsWritten` fails a run whose body left a declared output unwritten
(nothing recorded); the selected rule's other outputs must resolve back to it (overlap = error
naming both); `Rule.validate` refuses a `needs` equal to an own output. Tests: `Side outputs` in
RuleHeaderTests.fs; one 4.0 test (`multitarget rule generates names`) now writes its outputs.
Trap found: a failed build without `ThrowOnError` calls `exit`, which kills the NUnit host
("Test Run Aborted" with a misleading "Passed!" line); check the totals, not only "Passed!".
Verified: 0 warnings, 1775 passed / 10 skipped, `build test pack` green.

Step 3a done (2026-11-17, local): `src/core/ContentStore.fs` (per-file `(Stamp, Size) -> Hash`
history, lazy SHA-256 once per file and state, invariant one state one content, last 8);
`EngineState.Content`; `BuildResult.Content` (`ContentRecord`: sizes seen, static needs keys, need
groups, `Recorded`); `ExecCore.decide` (missing outputs, static needs built first, M9 static set,
`decideRecord`: outputs changed outside, then each dependency by pair then hash; rule targets:
finished in this run / running = changed / not started = `decideNoBuild`, memoised per run in
`RunCounters.Decisions`); `recordOutputsBefore/After` (pre-hash, post-hash, cut-off count and Info
line); `--content-cutoff=off`, `ExecOptions/EngineOptions.ContentCutoff`. The 4.0 memo
(`getChangeReasons`) stays for progress and `--dump` only. Database.fs: only the result pickler
fills `Content = Empty` (db-paths owns the rest). Tests: `ContentCutoffTests.fs` (11, engine mode);
storage round-trip tests compare without `Content`. Verified: 0 warnings, 1786 passed / 10 skipped,
`build test pack` green.

Merged feature/db-paths (0396f41) as 122e9b1 (conflict: ExecOptions fields, both kept). Step 3b
done: `Tag.ResultContent` (4) = tag-2 body + `ContentRecord` (`Persist.result07c`), every result
written with it, tag 2 read as "no content data"; `Tag.Content` (5) = file id + history
(`contentHistory`), last per file wins, kept by a rewrite, carried as bytes under an undeclared root;
`DatabaseApi.StoreContent` / `LoadContent`; `ContentStore` persists through them
(`createContextCore`). Changes in db-paths' code: the two tags in `PathTable.Tag`; `read07`
handles tags 4 and 5; `rewrite` writes the histories; `ReadState.Content`; the compaction count
includes histories; two `DatabasePathsTests` (0.6 upgrade, read-only) expect `Content = Empty`
from a 0.6 file. Verified: 0 warnings, 1816 passed / 11 skipped, `build test pack` green.

## Next step

Report 3b. Next: 3c (M4 fast path for no-change runs), then the PR question (owner). Then step 2 (`also`: missing-output rebuild with current outputs, M1 overlap check,
`needs` naming an own `also`, missing declared output after the body = error).
