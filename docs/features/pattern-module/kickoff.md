# Kickoff: pattern module (fresh orchestrator session)

Read first, in this order: `docs/session.md`, then this folder's `session.md`, `tracker.md`,
`brief.md`. Branch `feature/pattern-module`, worktree `.claude/worktrees/feature/pattern-module`.

## Goal

Design the pattern module (grammar, matcher, errors, API) as a spec with conformance tests
BEFORE any engine code, then move rules and filesets onto it, then bring the typed layer back.
The previous attempt (`feature/typed-matches`) changed the grammar while implementing; do not
repeat that: no code before the spec is approved.

## Working style (the user's rules)

- You are the orchestrator: read only indexes, digests and this kickoff. Heavy reading and
  coding go to subagents that write into files; you read their short digests.
- Subagent models: haiku for measurements, sonnet for docs, opus for design and implementation.
  Never fable for subagents.
- Discussion before decisions: a question or an observation from the user wants analysis, not a
  plan and not a code change. Act only on an explicit decision.
- Interview the user one question at a time; write each answer to `brief.md` immediately
  (with the date), then ask the next.
- Subagents can only write to the worktree the session is in. Switch with
  `EnterWorktree path=<worktree>` before delegating.
- One worktree per PR, named like the branch, under `.claude/worktrees/` (no `work/*`, `prep/*`).
- PRs go to `OlegZee/Xake`: always pass `--repo OlegZee/Xake` to `gh` (it defaults to upstream
  FakeBuild/Xake). Feature PRs squash; whether they go straight into `dev` or into an
  integration branch (as `feature/typed-matches` did) is NOT decided — ask the user before the
  first implementation PR.
- Right after each merge give the user the worktree/branch removal command.
- Keep the PR/branch/worktree table in `session.md` current; update `session.md` and
  `tracker.md` before ending a session.
- Public repo: no customer or company names anywhere (files, commits, PRs); neutral wording,
  brands Acme/Globex. English only.
- Do not run AR/customer suites; Xake unit tests (`dotnet test src/tests`) are fine.

## Frozen reference

`feature/typed-matches` (worktree `.claude/worktrees/feature/typed-matches`) is the prototype
and test corpus. **It must not be merged.** Read it, never write to it. The user has an
uncommitted edit to `experiments/08-consolidated/consumer/showcase.fsx` there: leave it alone.
Useful there: `docs/features/typed-matches/{brief,critique,escaping-today,core-without-tp}.md`,
`src/core/{PatternAst,Path,Fileset,TypedTargets}.fs`, `src/tests/{EscapingTests,
EscapingDiskTests,PatternAstTests,TypedTargets*,TypedCompileTests}.fs`, `docs/overview.md`
("Patterns, masks and paths"), `docs/typed-targets.md`.

## Phases (each ends with the user's approval before the next starts)

1. **SPEC.** Output `docs/features/pattern-module/spec.md`: EBNF grammar, semantics tables
   (rule vs include vs exclude), error/warning model with exception types, minimal public API and
   its name/namespace, compat and deprecation policy, worked examples. An opus agent drafts it
   from `brief.md` and the frozen-branch materials, each "Open for design" item answered with a
   recommendation and the alternatives. Then review with the user question by question; record
   outcomes in `brief.md`, update the spec. Exit: user approves `spec.md`.
2. **CONFORMANCE TESTS** written from the spec before any code: table-driven vectors, including
   mainstream-glob style cases (gitignore/picomatch) and the on-disk corpus from
   `EscapingDiskTests`; they fail or are skipped until Phase 3. Exit: user approves the vectors.
3. **IMPLEMENTATION** of the module (one AST, one matcher; no third-party glob library) until the
   conformance tests pass. Exit: tests green, review by the user, PR merged (target branch per
   the user's choice above).
4. **MIGRATION** of rules and `Fileset` onto the module. Compat corpus: `samples/`, code blocks in
   docs, existing tests; `getRuleMatch` unchanged; `applyWildcards` onto `format`. Exit: whole test
   suite and `dotnet fsi build.fsx -- -- build test` green, behaviour changes listed in release
   notes, PR merged.
5. **TYPED LAYER + DOCS.** Port #91 (`Xake.Typed`) from the frozen branch, then port/rewrite #92
   (`docs/typed-targets.md`, overview, cheatsheet pointers). Phony `=>` phase-out announced via
   `Command`. Exit: PR merged; version decided (open item f in the brief).

PR sizing within a phase is the user's call; propose, do not assume.

## First concrete step

Launch the Phase-1 spec agent (opus): hand it `brief.md`, the frozen-branch file list above, and
the target `spec.md`; ask for a draft with a recommendation per open item and a digest of at most
30 lines. Then walk the user through the open items one question at a time.
