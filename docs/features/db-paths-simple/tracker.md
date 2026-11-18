# Tracker: db-paths-simple

| Item | Status |
|---|---|
| Design report (design.md) | decisions of 2026-11-18 recorded (epochs in phase 1, incremental writing checked, 0.7 measured on a real database); lists with a varint length, string table Later; questions 1-4 open |
| Docs + release notes 5.0.0 (unreleased) | todo |
| 1a `File` normalised absolute path, ordinal comparer, matching ordinal | todo |
| 1a two-spellings check | todo |
| 1b database 0.7: tags, path table with one-level DirRec (owner 2026-11-18), relative paths, legacy read | todo |
| 1b exclusive database (`.xake.lock`, owner 2026-11-18) | todo |
| 1c epochs (owner 2026-11-18): `FileRec (stamp, size, hash?, changed)`, `Result.built`, dependencies by id, `TargetDep`, `StaticCount`; verdict by epochs without hashing (owner 2026-11-18) | todo |
| Lists with a varint length in 0.7 records (owner 2026-11-18) | todo |
| 0.6 -> 0.7 conversion (latest stamp per file, `built = 0` for results that saw an older one) | todo |
| Tests + size measurement (>= 5x) + explicit real-file test | todo |

## Later (for the owner)

- Need groups (Shake-style dependency groups): only with a speculative mode.
- Hashing and the cut-off verdict: phase 3 (format ready in 0.7).
- `Fileset.BaseDir` stored relative to the root.
- Version string compare breaks at "0.10".
- String table for repeated strings (variables, masks, step and phony names): about -12% compacted (owner 2026-11-18: Later).
