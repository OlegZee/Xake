# Phase 1 design report: `File`, database 0.7, content record shapes

Status: **for the owner's approval** (no code written). Base: `dev` at `1837856`, branch
`feature/db-paths-simple`. Inputs: [brief.md](brief.md), [file-type-brief.md](file-type-brief.md),
the dep-cutoff brief of the closed attempt (decisions "no epochs", option B).

## 1a. The `File` type

### Signature (`File.fsi`)

```fsharp
[<Sealed>]
type File =
    interface System.IComparable
    override Equals: obj -> bool          // ordinal on the normalised string
    override GetHashCode: unit -> int     // ordinal hash of the same string
    member Name : string                  // = File.display <current directory> f (kept, see below)
    member FullName : string              // absolute, OS form ('\' on Windows)
    // static member undefined  -- REMOVED

module File =
    val make : string -> File             // absolute as is, relative against the cwd at the call
    val display : root: string -> File -> string   // NEW: relative under root ('/'), else absolute
    val comparer : System.StringComparer  // NEW: StringComparer.Ordinal, the one path comparer
    val getFileName / getFileExt / getDirName / getFullName / exists / getLastWriteTime  // unchanged
```

- Representation: `File = private F of string`, the normalised absolute path with `/` separators.
  No `FileInfo`, no root, nothing cached. `exists` / `getLastWriteTime` ask the disk each call.
- Normalisation: one function, today's `Path.RuleImpl.collapsePath`, made an internal
  `Path.collapse` (`Path.fs` compiles before `File.fs`), called by `File.make` and by rule/mask
  matching. Rules: `\` -> `/` on Windows; `.`/`..`
  collapsed; repeated separators removed; root (`/`, `C:`) kept; a trailing separator dropped (new,
  except the root); case kept; lexical. A leading `//` (UNC) is kept on Windows (today's
  `collapsePath` loses it; fixed in the same function, 1 line).
- **`Name` decision: kept**, as `display (current directory)`. Reason: about 200 `.Name` reads in
  Xake.Dotnet (not all on `File`) and the engine's own logs (`Target.ShortName`); with
  `ProjectRoot` = cwd (the default) it is exactly the project-relative display. Release note: `Name`
  is no longer "the string as given" but the path relative to the current directory (or absolute).
- `undefined` removed. Xake.Dotnet uses it in `Csc`/`Fsc` settings (`Out`, `Doc`); it moves to
  `File option` there at its 5.0 (noted as a breaking change for Xake.Dotnet, not done here).

### One comparison rule

| Place | Today | After |
|---|---|---|
| `File` (`File.fs`) | ignore-case on Unix (inverted), hash inconsistent | `File.comparer` (ordinal) |
| `Target` (`Types.fs`) | `OrdinalIgnoreCase` on Windows | `File.comparer` |
| rule / mask matching (`PatternMatch.fs:201`, `PatternScan.fs:243,317-318`) | ignore-case on Windows | ordinal; `windows` keeps meaning `\` separator and UNC only |
| `WorkerPool.mapKey` | `Target.FullName` string key (F# `Map`, ordinal) | unchanged, already ordinal once `FullName` is normalised |
| `Fingerprints.fs:81,130` | ignore-case on Windows | `File.comparer` (2 lines; in core, so in scope; flag if you disagree) |

### Two spellings of one file

- `ExecContext` gets `Spellings: ConcurrentDictionary<string, string * bool>` (key: the path
  upper-cased invariant; value: first spelling, was-target), created per run with the memo.
- Checked where a `File` enters the run: a target when `execOne` starts it (`ExecCore`), a source
  when a `FileDep` is recorded (`ScriptFuncs.recordDependency`) and fileset results (`GetFiles`).
- Same key, different spelling: both targets -> error `target 'out/Foo.dll' is also named
  'out/foo.dll'` (the target fails); otherwise -> warning, once per pair. All OSes. Disk case is not
  checked.

## 1b. Database 0.7

### File layout

```
header  (XakeSign "XAKE", XakeVer "0.7", ScriptDate)          -- as today
record* -- each starts with a tag byte (new in 0.7):
  0  PathRec    (id: varint, path: string)                    -- before the first record using id
  1  Result     BuildResult (files as varint ids)
  2  ContentRec (fileId: varint, history: ContentEntry list)  -- last per file wins
```

- Path string = `File.display ProjectRoot f` with `/`: relative under the root, absolute outside
  it. Load: `File.make (root </> path)` (absolute stays absolute). Targets, `FileDep`,
  `ArtifactDep`/`ArtifactDep2` targets, `GetFiles` file lists all go through the same `file` pickler.
  `Fileset` masks and `BaseDir` stay strings (as today).
- Path table: writer `Dictionary<File, int>` (ids 0..n, seeded from the loaded table so appends
  continue), reader `ResizeArray<File>` (one `File` per path). Unknown id on load -> the read fails ->
  the database is recreated, as today for any unreadable file.
- Writing a result: pickle into a buffer with a codec that collects newly allocated ids, then write
  their `PathRec`s and the result in one `Write`. **A failed write stops further appends for the
  run** (logged once; results stay in memory for the run). This replaces today's
  "count and continue" and avoids ids that were allocated but never written.
- Compaction / upgrade rewrites with a fresh table of live paths only. The compaction trigger
  counts `Result` records only.

### Picklers

`Persist` becomes parameterised by the file pickler: `results (file: PU<File>)` builds `target`,
`dependency`, `filelist`, `result`. Two instances:

- 0.7: `file` = varint id through the codec;
- 0.4-0.6 (read only): `file` = string, `File.make (root </> s)` (old `FileDep` absolute
  `FullName`s load as is; old `FileTarget` raw `Name`s, usually relative to cwd, are joined to the
  root). Old files have no tags: the reader branches on the header version.

`Pickler.fs` gains `varint` (7-bit encoding; `Write7BitEncodedInt` is not public on
netstandard2.0) and `int64v` for sizes.

### Upgrade and version

`XakeDbVersion = "0.7"`, `XakeDbMinReadVersion = "0.4"` unchanged; an old file is read with the
legacy picklers and rewritten as 0.7 (existing "Upgrading database" path). A no-change run after
the upgrade rebuilds nothing (sizes absent compare as "no data", unused anyway in this phase).

### Not done (by the brief)

No directory tree, named roots, framing, salvage, carried records, re-rooting, root records,
read-only open, lock file.

## 1c. Content record shapes (stored, round-tripped, unused)

New public types in `Types.fs`:

```fsharp
/// One known state of a file: the stamp and size, and the content hash if computed (SHA-256).
type ContentEntry = { Stamp: Timestamp; Size: int64; Hash: byte[] option }

/// What a dependent saw beyond the 4.0 records (database 0.7; not used by the engine in 5.0 phase 1).
type ContentRecord =
    { Sizes: Map<File, int64>      // size of each file dependency (FileDep / ArtifactDep2 of a file), as seen
      StaticNeeds: string list     // static needs: target names or fileset masks (phase 2/3 fills it)
      NeedGroups: int list }       // sizes of the dynamic `need` call groups, in Depends order
    static member Empty : ContentRecord

type BuildResult = { Targets; Built; Depends; Steps; Content: ContentRecord }   // + Content
type Database = { Status: Map<Target, BuildResult>; Content: Map<File, ContentEntry list> }  // + Content
```

- Sizes as a map in `BuildResult.Content`, not a third field of `FileDep`: no change to the
  `Dependency` cases, so no pattern-match churn (6 `FileDep (...)` matches here, 4 in Xake.Dotnet);
  record literals of `BuildResult` change (about 12 here, 1 in Xake.Dotnet), `makeResult` fills
  `Empty`. On the wire the size is written right after the stamp of its dependency (varint, 0 = none),
  so no second id per dependency.
- Static needs and need groups: two fields, as the brief allows; filled by phase 2/3.
- Content history: `ContentRec` per file, newest first, last record wins; agent messages
  `GetContent of File * reply` and `StoreContent of File * ContentEntry list`. The bound (8) and the
  pruning of entries no record references belong to phase 3 (Later).

## Public breaking changes (release notes "5.0.0 (unreleased)")

1. Path case: ordinal on every OS for `File`, `Target`, rules and filesets. On Linux `A.cs`/`a.cs`
   are now two files (they were one!); on Windows/macOS rule masks and filesets become
   case-sensitive. Two spellings in one run: error for targets, warning for sources.
2. `File`: `undefined` removed; `Name` means display relative to the current directory; `make`
   normalises (trailing separator dropped, `\` -> `/` internally on Windows); `File.display`,
   `File.comparer` added.
3. `BuildResult.Content`, `BuildLog.Database.Content`, `ContentEntry`, `ContentRecord` added
   (record literals must set `Content`).
4. `Storage.openDb root dbpath logger` (new `root` parameter); `DatabaseApi` gains `GetContent`,
   `StoreContent`. `Storage.resultPU` (public, used by tests) is replaced by
   `Storage.resultPickler root` (string paths) for tests, or removed - see question 2.
5. `ExecContext` gains `Spellings` (record shape test updated).
6. Database 0.7: an older Xake cannot read it (as at every bump); 0.4-0.6 load and upgrade.

## Line budget (estimates, net)

| File | Change | Lines |
|---|---|---|
| `Database.fs` | tags, path codec, parameterised picklers, legacy branch, content table + messages, stop-on-failure, root param | **+140** |
| `Pickler.fs` | `varint`, `int64v` | +15 |
| `File.fsi` / `File.fs` | new representation, `display`, `comparer`, `undefined` gone | +5 / -5 |
| `Path.fs` | `collapse` made internal + trailing/UNC fix | +5 |
| `PatternMatch.fs`, `PatternScan.fs` | drop ignore-case | ~0 (edits) |
| `Types.fs` | ordinal `Target`, `ContentEntry`, `ContentRecord`, field | +20 |
| `ExecTypes.fs`, `ExecCore.fs`, `ScriptFuncs.fs` | two-spellings check | +35 |
| `ScriptRunner.fs`, `Fingerprints.fs`, `DependencyAnalysis.fs` | root to `openDb`, comparer, `Name` uses | ~+3 |
| **core total** | | **about +220** |
| tests (`FileTests`, `StorageTests`, new `DatabaseFormatTests`) | | +300 |

No new source module.

## Tests and measurement

- `File`: normalisation table (Unix and Windows rules; the Windows rules run on every OS through the
  internal `collapse windows` flag), `Equals`/`GetHashCode`/`CompareTo` consistency (property test
  with case variants), case variants distinct in `Set<File>`, `Set<Target>`, rule lookup, `WorkerPool`.
- Two spellings: target error naming both, source warning.
- Round trip of every record kind (each `Dependency` case, `PathRec`, `ContentRec`, sizes, static
  needs, groups); paths outside the root stay absolute.
- 0.6 -> 0.7: a 0.6 file written by the 4.0 pickler (kept in the test as a fixture writer), then a
  no-change run builds nothing and the file is 0.7.
- Relocation: build, copy the tree with `.xake` (times preserved) to another folder, build: nothing
  rebuilt.
- Size: synthetic database, 1000 results x 30 file deps, paths of 114 bytes mean, 0.6 vs 0.7 bytes;
  expect about 6x (0.6: ~124 B per dep; 0.7: tag + id + 8 B stamp + size ~ 14-16 B plus the table
  once). Target >= 5x. The 8-byte stamp dominates afterwards (varint stamps: Later).
- `[<Explicit>]` test: measure `XAKE_DB_SAMPLE` (a real 0.6 file): bytes before / after the upgrade.
- `dotnet build src/core -c Release` 0 warnings; `dotnet test src/tests -c Release`;
  `dotnet fsi build.fsx -- -- build test pack`.

## Questions for the owner

1. `Name` kept as display relative to the current directory (recommended) or removed?
2. `Storage.resultPU`: drop it from the public surface (tests use an internal) - recommended - or keep
   a string-path pickler public?
3. Filesets on Windows become case-sensitive with rules (as the file-type brief says for masks):
   confirm, since `*.CS` stops finding `a.cs` there.
4. `Fingerprints.fs` path comparisons switch to ordinal too (in core): confirm.

## Later (not in this phase)

- Content history bound and pruning of entries no record references (phase 3).
- Varint/delta stamps (the next size win after paths).
- `Fileset.BaseDir` stored relative to the root.
- Version comparison is a string compare (`"0.10" < "0.7"`): fine until 0.10.
