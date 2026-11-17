# Tracker: dep-cutoff (Xake 5.0)

| # | Item | Status |
|---|------|--------|
| 0 | Spike: header type, For/Run, which builders, typed targets, op names ([probe.md](probe.md)) | done; three rounds of owner decisions recorded in brief.md |
| 0a | Fresh-eyes critic (Fable) on brief + probe + code + docs ([critic.md](critic.md)) | done; findings await the owner |
| 1a | Header in `recipe`; SRTP `..>`, `*..>`, `..?>`; `target`/`targets`/`X.rule`; phony and `X.Rule` reject it | done (local) |
| 1b | `FileRule`/`MultiFileRule` merged: `FileRule (outputs, header, recipe)`; `FileConditionRule` with header | done (local) |
| 1c | Static `needs`: group substitution, single path vs fileset, recorded, built before the body | done (local); built when the target runs, the verdict stays 4.0's until step 3 |
| 1d | `requires lock` / `(lock, n)`; `requiring` on file rules = header; locks after needs, name order, merged | done (local) |
| 1e | `ruleHeader ["n"] { }` + `including h` (option A, declared groups) | done (local) |
| 1f | Docs: rule-header.md, cheatsheet, release notes 5.0.0 (unreleased) | done for 1a-1d |
| 1g | Tests: RuleHeaderTests.fs (17), fixtures/header-neg (compile cases) | done |
| 2 | `also` on the multi-output representation (lookup, missing outputs, M1 overlap check) | pending; matching already shared with `*..>` |
| 3 | Content cut-off: epochs, lazy hashes, verdict in `execOne`, database 0.7 | pending; critic C1-C4, M2-M5, M9-M11 belong here |
| 4 | Plan for progress and `--dryrun` (may follow 5.0) | pending |
| 5 | `produces` (may follow 5.0) | pending |
| 6 | Sugar for one body over several masks (`masks \|..> body`) | later, not 5.0 |
