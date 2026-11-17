# Tracker: db-paths-simple

| Item | Status |
|---|---|
| Design report (design.md) | done, awaiting approval |
| Docs + release notes 5.0.0 (unreleased) | todo |
| 1a `File` normalised absolute path, ordinal comparer, matching ordinal | todo |
| 1a two-spellings check | todo |
| 1b database 0.7: tags, flat path table, relative paths, legacy read | todo |
| 1c content shapes: sizes, static needs, need groups, content history | todo |
| Tests + size measurement (>= 5x) + explicit real-file test | todo |

## Later (for the owner)

- Content history bound and pruning of unreferenced entries (phase 3).
- Varint/delta timestamps (next size win).
- `Fileset.BaseDir` stored relative to the root.
- Version string compare breaks at "0.10".
