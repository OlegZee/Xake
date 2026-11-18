# Tracker: rule-header (Xake 5.0 phase 2)

Specification: [brief.md](brief.md). Ported from `archive/xake5-attempt1` (dep-cutoff items 1a-1g, 1i, 2).

| Item | Status |
|---|---|
| Design report to the orchestrator (port, changes, line budget) | done; awaiting the owner |
| Header types, one file-rule case, validation (M8, M12, XP013/XP014, own-output needs) | pending |
| Header in `recipe` and every file-rule form; `ruleHeader` + `including`; `X.RuleWith` | pending |
| `execOne`: needs -> locks -> body on a fresh result; `headerDeps @ bodyDeps`, `StaticCount` | pending |
| `also`: side-output lookup, M1, M2, missing current output | pending |
| M9: static set changed -> rerun | pending |
| Tests (ported, StaticCount order, M9), compile-negative fixtures | pending |
| Docs `rule-header.md`, release notes, implnotes | pending |
| Release build 0 warnings, tests, `build.fsx build test pack`, Windows VM | pending |

## Later (for the owner)
