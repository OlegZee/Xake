# Session: typed rule matches (`feature/typed-matches`)

## Goal

Research syntax for multi-file targets and typed access to rule pattern groups, including
F# Type Providers. Motivation: `getRuleMatch "name"` calls are verbose, untyped and unchecked
(a misspelled group name is only found at run time).

A second, separate benefit of a typed `Pattern<>` (the user, 2026-10-23): the inverse of
matching — **generating paths** from the pattern and group values, which scripts do very often
by hand and which drifts from the rule's pattern.

Research only: the deliverable is material for discussion (`research.md`), not an
implementation. Nothing under `src/` changes on this branch until a decision is taken.

## PR, branch, worktree

| PR | Branch | Worktree |
|----|--------|----------|
| — (integration → `dev` at the end) | `feature/typed-matches` (pushed) | `.claude/worktrees/feature/typed-matches` |
| #90 PR1 → `feature/typed-matches` — **merged** 2026-10-25 (ba03903); worktree/branch to be removed by the user | `feature/pattern-ast-format` | `.claude/worktrees/feature/pattern-ast-format` |
| PR3 → `feature/typed-matches` (in progress) | `feature/typed-targets` | `.claude/worktrees/feature/typed-targets` |
| PR2 deferred (decision 47) | `feature/script-surface-names` (empty, at 9e42f4c) | `.claude/worktrees/feature/script-surface-names` |

Subagents can only write to the worktree the session is in: switch the session
(`EnterWorktree path=…`) to the PR's worktree before delegating; PRs run one at a time.
`pattern-ast-format` still has an untracked copy of `critique.md` — not part of PR1, delete it
there before more PR1 work.

Sub-PR branches use this feature folder (`docs/features/typed-matches/`); they branch from and
merge (squash) into `feature/typed-matches` (decision 45).

## State

- 2026-10-23: branch created from `dev` (1332bc6); research agent (opus) started, writing
  `research.md` and `experiments/`.
- 2026-10-23: research done — `research.md` (options 0–6, comparison incl. path and path-list
  generation), `experiments/` (type provider prototype `Xake.Patterns`, record-declared
  patterns, positional/arity, reflection on parameter names; commands and outputs in
  `experiments/README.md`). Type provider verdict: feasible (erased, TP SDK 8.11.0,
  FSharp.Core 8.0.100, works in fsi SDK 8/10 and `#r "nuget:"`); IDEs and packaging into the
  Xake nupkg not yet tested. Pattern must be a compile-time literal.

- 2026-10-25: the user answered the 11 questions → `brief.md` (type provider in the same
  package, separate static params per output, labelled outputs, positional bare wildcards,
  `Glob` → `Fileset`, `getRuleMatch` untouched). `dev` merged in (pattern AST, #89, which the
  provider should build on).

- 2026-10-25: three follow-ups done. `phony-types.md`: separate phony type ≈1–1.5 days
  (+0.5–1 over a `.Phony` member), no engine change, prototype 04 works. `ast-reuse.md`:
  extract `PatternAst` into `src/core/PatternAst.fs` (+ generation: format/glob/wildcards),
  design-time dll `Xake.DesignTime` reaches it via InternalsVisibleTo; nupkg with
  `typeproviders/fsharp41/netstandard2.0` loads via `#r "nuget:"` (prototype 05). Risk: FS3031
  is an error — once Xake.dll carries `TypeProviderAssembly`, every build path must ship the
  design-time dll. `hermetic-sketch.md`: keep hermetic's function API, feed it from patterns
  first.

- 2026-10-25: brief round 2 recorded (Command<>, public PatternAst, applyWildcards on format,
  escaping, TP SDK from its package). Experiment 06: the TP attribute on a separate
  `Xake.Patterns.dll` in the same package — Xake.dll-only consumers need no design-time dll
  (FS3031 only at the Xake.Patterns.dll reference); `#r "nuget:"` and a compiled project work;
  the typeproviders folder must hold the whole design-time output. Experiment 07 / `ce-syntax.md`:
  forms A (`Dll.Pattern ..> recipe { let! m = Dll.Get ... }`), B (`Dll.rule { let! m = matched
  ... }`, agent's leaning) and D (`Dll.Rule <| fun m -> recipe {...}`) compile; no CE can bind
  `m` implicitly; a type is not a value (`Dll ..>` impossible).

- 2026-10-25: brief rounds 3–4 + decision 25 recorded. Experiment 08 (`experiments/08-consolidated/`,
  README section 08): the whole decided design end to end — `engine.patch` (+181/−119, public
  `PatternAst`, `formatMatches` replaces `applyWildcards`, escaping), `tests.patch`, runtime
  `Xake.Patterns.dll` 112 lines, provider 148 lines, TP SDK sources compiled from its package
  folder, one nupkg works in fsi SDK 10/8, net8.0 and net462 builds. Type renamed
  `Pattern<>` → `Target<>` on the user's idea (pending): it clashes with the DU
  `Xake.DomainTypes.Target` — the namespace opened last wins (FS3081 / FS0033, misleading).

- 2026-10-25: brief rounds 5–6 (Target<>, Format/Pattern, escape `|`, labels, Command = always
  runs; **decision 32: the TP goes to a separate package, Core gets the non-TP parts**).
  Exp 09 (`destructure.md`): no field punning in F#; provided types can't be record-patterned;
  tuple `let! a, b = groups` works (arity checked, order not). IDE FS3033 explained: same
  assembly identity `Xake.Patterns 1.0.0.0` built by experiments 03/04/09 — first loaded wins
  in FsAutoComplete; 08 renamed to `Xake.Patterns.Exp08` 0.8.0.0, open only the 08 folder.
  Exp 10 (`core-without-tp.md`): record-declared `Target<'G>`/`Target<'G,'O>`/`Command<'G>` in
  plain F# (~270 lines, `namespace Xake.Typed`): typed `m.fwk`, `Format {…}` checked at
  compile time, record destructuring works, record↔pattern checked at script start; same build
  as 08's TP. Name clashes: `Command` vs `Level.Command` (FS0717), Core `Target<'G>` hides TP
  `Target<"...">` (FS0618), two `matched` (FS0041).
- `experiments/08-consolidated/consumer/showcase.fsx` has uncommitted edits by the user (form
  `Lib.Rule (fun m -> …)`, `Clean.rule {…}`) — leave them to the user.

- 2026-10-25: brief round 7 (decisions 33–38); `plan.md` written: PR0 this branch (docs),
  PR1 pattern-ast-format, PR2 script-surface-names (Level qualified, DU Target → BuildTarget),
  PR3 typed-targets (Xake.Typed), PR4 docs → release 3.12.0, PR5 hermetic on 3.12, PR6 dogfood,
  PR7 patterns-provider package.

## Next step

Get the user's answers on the open items at the end of `plan.md`, record them in `brief.md`,
then write the kickoff file for the implementation session and restart.

## Older next step

Discuss with the user `core-without-tp.md`'s open questions (shared short names vs distinct;
`[<RequireQualifiedAccess>]` on `Level`; TP erasing onto Core's runtime; cross-product `For`;
`'O` outputs record; script-start error rendering), then the implementation plan: Core first
(engine.patch + record-declared types), TP package later.

## Older next step (superseded line below kept for history)

Decide with the user: the `Target<>` rename given the clash; the name of the generation member
(`Format` suggested); escaping `|`; label syntax `"label=pattern"`. Then the implementation plan.

Old: ask the user: confirm the separate dll; the CE form and its 5 questions (`ce-syntax.md`); the
remaining phony questions (`phony-types.md`: semantics, `Need`, `TryMatch`, no-group type).
Record in `brief.md`. Then an implementation plan — only on the user's go-ahead.
