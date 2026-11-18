# Tracker: db-paths-simple (Xake 5.0 phase 1)

Specification: [brief.md](brief.md). PR [OlegZee/Xake#141](https://github.com/OlegZee/Xake/pull/141).

| Item | Status |
|---|---|
| Docs and release notes "5.0.0 (unreleased)" | done |
| `File` as the normalised absolute path (`Name`), ordinal comparer, `display`, `getSysFileName`, `getFileInfo`; `FullName`/`getFullName`/`getTargetFullName` obsolete | done |
| `Target.Name` / `Display`; rule matching ordinal, one case flag in `PatternMatch`, roots and drive letters normalised | done |
| Filesets and `Path.matches` ignore case, `casesensitive`; a probed literal keeps the disk spelling | done |
| Two-spellings check | done |
| Database 0.7: tagged records, path table relative to the root (functional), `FileRec`, varints, one `Write` per record | done |
| Lock `.xake.lock` (taken first, never deleted), version messages, torn tail, `.bak` kept on a failed rewrite | done |
| `LegacyRead`: 0.4-0.6 upgrade and conversion | done |
| Read API `readRecords` / `readDatabase` | done |
| Epochs: `beginRun`, `RunEpoch`, observation modes, verdict, `--dump`/`--dryrun` in memory, delegated results | done |
| Tests, size measurement (synthetic 20.1x, real database 28.9x) | done |
| Windows CI (PR #141) | on the Windows VM |

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
