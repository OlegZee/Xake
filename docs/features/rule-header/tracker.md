# Tracker: rule-header (Xake 5.0 phase 2)

Specification: [brief.md](brief.md). Ported from `archive/xake5-attempt1` (dep-cutoff items 1a-1g, 1i, 2).

| Item | Status |
|---|---|
| Design report to the orchestrator (port, changes, line budget) | done; approved 2026-11-18 |
| Header types, one file-rule case, validation (M8, M12, XP013/XP014, own-output needs) | done |
| Header in `recipe` and every file-rule form; `ruleHeader` + `including`; `X.RuleWith` | done |
| `execOne`: needs -> locks -> body on a fresh result; `headerDeps @ bodyDeps`, `StaticCount` | done |
| `also`: side-output lookup, M1, M2, missing current output | done |
| M9: static set changed -> rerun | done |
| Tests (ported, StaticCount order, M9), compile-negative fixtures | done |
| Docs `rule-header.md`, release notes, implnotes | done |
| Release build 0 warnings, tests, `build.fsx build test pack` (macOS) | done |
| Windows VM (core 0 warnings, 1803 passed, 21 skipped) | done |

## Later (for the owner)

- Size: core 519 changed lines (424 added, 95 removed) against the 452 budget; the first attempt's
  in-scope header code was 468 by the same count. The M9 hook (`currentRule`, `selectRule`,
  `StaticKey`, the `getChangeReasons` argument) is about 55 of them and was not in the first
  attempt's step 2. Possible trims are cosmetic (doc comments, `SharedHeader` accessors).
