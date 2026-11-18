# Critic of the phase 1 decision (2026-11-18)

A fresh-eyes critic read [decision.md](decision.md), [brief.md](brief.md), [design.md](design.md)
and the `dev` sources; nothing was executed. Findings 1-3 were checked against the code by the
orchestrating session (`getChangeReasons` maps every stored dependency; `XakeEngine.Demand`
serialises per target name only; `runTargets` calls `newRun` per target group). All findings are
taken into decision.md as "Amendments after the critic" (A1-A9); none re-opens an owner decision.

| # | Severity | Finding | Amendment |
|---|---|---|---|
| 1 | correctness (needless rebuild) | a file without a rule that the run rewrites (generated source, `.pdb`) is observed by a verdict before the rewrite and, with "once per run", never re-statted when a body records it; the next no-change run rebuilds the chain (4.0 stats at `need` time) | A1 |
| 2 | correctness (missed rebuild) | engine mode: `Demand` of different names runs concurrently, runs overlap; a straggler of run r stamping `changed = r` hides a change from a target with epoch r | A2 |
| 3 | gap (missed rebuild) | `runTargets` calls `newRun` per target group; the observed set must be cleared exactly when the epoch advances | A2 |
| 4 | gap | delegated rules: a cache hit returns a result with a foreign epoch and unobserved dependencies | A3 |
| 5 | gap | a failed write must stop appends until the database is closed (engine mode); a pickling failure writes nothing and must not stop the agent (`AgentFailureTests`) | A4 |
| 6 | gap | `--resetdb` and the `.bak` restore must happen under the lock; the lock file is never deleted; released on a failed open; readers need `FileShare.Delete`; a nested Xake on the same root waits and fails | A5 |
| 7 | correctness (silent stale build) | ordinal identity makes the spelling of the root significant (`c:\proj` vs `C:/proj`); `File.display` must test the prefix on a segment boundary; `\foo` and `C:foo` on Windows | A6 |
| 8 | minor | upgrade: take the stamp of the result with the latest `Built`; `DateTime.MinValue` is "no stamp"; Linux filesets re-scanned without case after the upgrade | A7 |
| 9 | minor | `noopDb` needs `Observe`, files and epochs; `--dump`/dry-run observations; the verdict cases kept as today; a multi-output rule re-observes all its outputs | A8 |
| 10 | minor | two spellings: file targets that matched a rule only, phony names excluded; the `PatternMatch` cache key needs the case flag | A9 |
| 11 | none | the spec's excess over the brief (lock, read API, torn tail, epochs in the verdict) is all recorded as owner decisions | - |

Checked and found sound by the critic: crash ordering between `Result` and `FileRec`, compaction
dropping the largest epoch, a phony needed dynamically, `AlwaysRerun`, a missing output, a source
that later gets a rule, a torn tail without framing, append-only writing.
