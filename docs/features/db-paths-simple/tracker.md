# Tracker: db-paths-simple

| Item | Status |
|---|---|
| Design report (design.md) | decisions of 2026-11-18 recorded (epochs in phase 1, incremental writing checked, 0.7 measured on a real database); lists with a varint length, string table Later, always upgrade without record length, answers 1-4 and the proposals accepted; reading of "`File` holds the absolute path" confirmed; approved 2026-11-18; spec in decision.md |
| Docs + release notes 5.0.0 (unreleased) | done (step 1) |
| 1a `File` normalised absolute path, ordinal comparer, matching ordinal | done (step 2) |
| 1a two-spellings check | done (step 2) |
| 1b database 0.7: tags, path table with one-level DirRec (owner 2026-11-18), relative paths, legacy read | done (steps 3+4) |
| 1b exclusive database (`.xake.lock`, owner 2026-11-18) | done (steps 3+4) |
| 1c epochs (owner 2026-11-18): `FileRec (stamp, size, hash?, changed)`, `Result.built`, dependencies by id, `TargetDep`, `StaticCount`; verdict by epochs without hashing (owner 2026-11-18) | done (steps 3+4) |
| Lists with a varint length in 0.7 records (owner 2026-11-18) | done (steps 3+4) |
| Root number in `DirRec` (one value used), `openDb root` with `ProjectRoot` only (answer 1, orchestrator) | done (steps 3+4) |
| Public read API `readRecords` / `readDatabase`, `Built` kept next to `Epoch`, `--dump` with epochs (answer 2) | done (steps 3+4) |
| Filesets case-insensitive by default, `casesensitive` option (owner 2026-11-18) | done (step 2; pickled from step 3) |
| `File.Name` = absolute path (owner 2026-11-18, confirmed) | done (step 2) |
| Version messages (newer / too old), torn last record cut off | done (steps 3+4) |
| 0.6 -> 0.7 conversion (latest stamp per file, `built = 0` for results that saw an older one) | done (steps 3+4) |
| Tests + size measurement (>= 5x) + explicit real-file test | done (steps 3+4) |

| Amendments A1-A9 (critic, 2026-11-18) | done (follow-up commit) |
| B1 functional path table, B2 `beginRun` / epoch in `ExecContext`, B3 `LegacyRead` (owner 2026-11-18) | done (one commit: the three rewrite the same code) |
| Review fixes R1, R2, R3, R4, R7; simplifications a-j; review test gaps | done (A4 write failure untested: no way to inject a failing write) |
| R6 / B4 `Path.matches` ignores case on every OS | done |
| Windows CI (PR #141): C2 root normalised for matching; B5/C1 `getSysFileName`, `getFileInfo`, `FullName`/`getFullName`/`getTargetFullName` obsolete | done, Windows run by the starting session |

## Next fixes for PR #141 (one commit, collected 2026-11-18): done

1. Epoch as `int` (int32): `BuildResult.Epoch`, `FileState.Changed`, `ExecContext.RunEpoch`,
   `BuildLog.Database.Epoch`; the epoch pickled with `varint` (`varint64` goes if unused). The
   file does not change (varints); 2.1e9 runs is enough.
2. `PatternMatch` takes one flag, `ignoreCase`; `windows` goes. Every path
   reaching it is already in Xake's form (`Path.collapse`: `/`, drive upper-cased, UNC only on
   Windows), so its `normalize` and the UNC guard are redundant. `matchMask` (case = OS, the 4.0
   mode, used only by tests) folds into `maskMatcher`. The vector wrapper collapses targets with the
   Windows rules, so the Windows vectors keep running on every host. `PatternScan` keeps its
   `windows` flag: there it is the volume (probe vs listing, the directory comparer), not the case
   policy, and the scan vectors simulate both volumes through it.
3. `getFiles` (`ScriptFuncs.fs:156`) no longer observes every member: a
   `GetFiles` dependency compares only the list, so a member's `FileRec` changes no verdict, and
   `dependsOn`/`needFiles` observe the members they record anyway (`ExecCore.fs:305`). Drops N
   sequential agent round trips per call (up to 2 176 members on the measured database). The
   spelling check of the members stays. A1 in decision.md says "a `FileDep`", without "also each
   file of a fileset result".
4. `Target` (all members internal, so no `[<Obsolete>]` needed): remove
   `FullName` (the OS form); rename `ShortName` to `Name` (Xake's full name, the identity used by
   `Equals`/`GetHashCode`/`CompareTo`). Engine messages that showed `FullName` (`ExecCore`: rule
   candidates for `EXPLAIN_RULE`, rule ties, "Neither rule nor file is found", resources) show
   `Display ProjectRoot`; `EXPLAIN_RULE` still finds a target by its full or relative name;
   `Path.ruleEvidence` and `WorkerPool.mapKey` take `Name` (no per-call conversion on Windows in the
   pool's hot path).

## Later (for the owner)

- **Case policy (direction, owner 2026-11-18):** work as on Windows by default, i.e. ignore case
  in searches and matching of names on disk; on Linux allow stricter rules on demand that enforce
  correct path resolution and search (the spelling on disk). Probably much work; not phase 1.
- **Known problem (owner 2026-11-18):** an explicit file name without a wildcard in a fileset
  (`includes "SRC/a.cs"`) stays as typed (vector SC-219): it is a literal, returned even when it
  does not exist. On a case-insensitive disk a wrong case gives a second `File` for one file, and
  only the two-spellings warning shows it.

- Need groups (Shake-style dependency groups): only with a speculative mode.
- Hashing and the cut-off verdict: phase 3 (format ready in 0.7).
- `Fileset.BaseDir` stored relative to the root.
- Version string compare breaks at "0.10".
- Delegated cache hits keep an old epoch, so their dependents rerun on every hit once the sources changed (review R5; same as 4.0).
- Run record (epoch, start, demanded targets, change reasons) for analysis.
- String table for repeated strings (variables, masks, step and phony names): about -12% compacted (owner 2026-11-18: Later).
