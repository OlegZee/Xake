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

## Next step

Discuss with the user the 11 open questions at the end of `research.md` (one at a time),
record answers in `brief.md`. No implementation before decisions.
