# Tracker: match-operator

Specification: [brief.md](brief.md). Research: [research.md](research.md).

| Item | Status |
|---|---|
| Research: operators, ambient context, lambda parameter names | done, dropped (2026-11-19) |
| Decision: `let! m = matched` + strict `m["n"]` in every recipe | approved 2026-11-19 |
| Tests (`MatchedTests.fs`; typed-neg case `matched-in-a-plain-recipe-is-untyped`) | done |
| `RuleMatch.fs`: `Matched`, `RuleMatch`, `RuleRecipeBuilder` (`recipe`, `action`) | done |
| Docs (cheatsheet, rule-header, typed-targets, overview), release notes item 11 | done |
| Build, tests, PR | in progress |

## Notes

- An extension `Bind(Matched, RuleMatch -> ..)` on `RecipeBuilder` was tried first: the typed
  builders then see two `Bind(Matched, ..)` and lose their compile errors (`m.fkw` gives FS0072
  instead of FS0039). Hence a derived `RuleRecipeBuilder` for scripts, the typed builders staying
  on `RecipeBuilder`.
- `Builder.recipe` is gone (an internal one still shadowed the new `recipe` in the tests, through
  `InternalsVisibleTo`); `RecipeFunctions.fs` has a private one.

## Later (for the owner)

- `getRuleMatch` strict or obsolete: not decided; unchanged here.
