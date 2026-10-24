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
| — | `feature/typed-matches` | `.claude/worktrees/feature/typed-matches` |

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

## Next step

Ask the user the open questions of `phony-types.md` (5) and `ast-reuse.md` (5), record in
`brief.md`. Then an implementation plan — only on the user's go-ahead.
