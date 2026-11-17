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
| 1g | Tests: RuleHeaderTests.fs, fixtures/header-neg (compile cases) | done |
| 1h | `RunSummary`: counters per run / per `Demand`, log line, `onSummary`, `XakeEngine.LastSummary` | done (local); `CutOff`, `FilesHashed`, `HashTime` filled in step 3 |
| 1i | Typed form D header: `X.RuleWith h <| fun m -> ..` (owner: D2) | done (local) |
| 2 | `also` on the multi-output representation: lookup, missing current output reruns (M9), unwritten output = error (M2), overlapping outputs = error (M1), XP013/XP014, `needs` of an own output | done (local) |
| 3a | Content cut-off in memory: `ContentStore` histories, lazy hashing, decision in `execOne` (static needs first, option B), pre/post output hashes, cut-off count, `--content-cutoff=off`, M9 static set | done (local) |
| 3b | Persistence: `BuildResult.Content` and the content table in the 0.7 layout (db-paths' records, tags 4-15); M11 upgrade test; script-run acceptance tests | waits for the merge with feature/db-paths |
| 3c | M4 fast path for no-change runs | pending |
| 4 | Plan for progress and `--dryrun` (may follow 5.0) | pending |
| 5 | `produces` (may follow 5.0) | pending |
| 6 | Sugar for one body over several masks (`masks \|..> body`) | later, not 5.0 |
