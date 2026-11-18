# Tracker: db-paths-simple

| Item | Status |
|---|---|
| Design report (design.md) | decisions of 2026-11-18 recorded (epochs in phase 1, incremental writing checked, 0.7 measured on a real database); lists with a varint length, string table Later, always upgrade without record length, answers 1-4 and the proposals accepted; reading of "`File` holds the absolute path" confirmed; approved 2026-11-18; spec in decision.md |
| Docs + release notes 5.0.0 (unreleased) | done (step 1) |
| 1a `File` normalised absolute path, ordinal comparer, matching ordinal | done (step 2) |
| 1a two-spellings check | done (step 2) |
| 1b database 0.7: tags, path table with one-level DirRec (owner 2026-11-18), relative paths, legacy read | todo |
| 1b exclusive database (`.xake.lock`, owner 2026-11-18) | todo |
| 1c epochs (owner 2026-11-18): `FileRec (stamp, size, hash?, changed)`, `Result.built`, dependencies by id, `TargetDep`, `StaticCount`; verdict by epochs without hashing (owner 2026-11-18) | todo |
| Lists with a varint length in 0.7 records (owner 2026-11-18) | todo |
| Root number in `DirRec` (one value used), `openDb root` with `ProjectRoot` only (answer 1, orchestrator) | todo |
| Public read API `readRecords` / `readDatabase`, `Built` kept next to `Epoch`, `--dump` with epochs (answer 2) | todo |
| Filesets case-insensitive by default, `casesensitive` option (owner 2026-11-18) | done (step 2; pickled from step 3) |
| `File.Name` = absolute path (owner 2026-11-18, confirmed) | done (step 2) |
| Version messages (newer / too old), torn last record cut off | todo |
| 0.6 -> 0.7 conversion (latest stamp per file, `built = 0` for results that saw an older one) | todo |
| Tests + size measurement (>= 5x) + explicit real-file test | todo |

## Later (for the owner)

- Need groups (Shake-style dependency groups): only with a speculative mode.
- Hashing and the cut-off verdict: phase 3 (format ready in 0.7).
- `Fileset.BaseDir` stored relative to the root.
- Version string compare breaks at "0.10".
- Run record (epoch, start, demanded targets, change reasons) for analysis.
- String table for repeated strings (variables, masks, step and phony names): about -12% compacted (owner 2026-11-18: Later).
