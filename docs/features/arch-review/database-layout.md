# Topic 1: what `Database.fs` holds, and a layout where it is only the database

Owner's remark (2026-11-20): `Database.fs` holds much that is not the database, the hasher and
the decision code among it. Base: 6f39dbb (`feature/idle-perf`, 869 lines in `Database.fs`).

## 1. What is in the file today

| Lines | Part | What it is | Called from (core; tests) |
|---|---|---|---|
| 3-39 | `BuildLog`: `XakeDbVersion`, `XakeDbMinReadVersion`, `Database` record, `newDatabase`, `addResult` | **database** | ExecCore (none of these but the type); tests read `XakeDbVersion` |
| 25 | `BuildLog.makeResult` | **build result** (an empty `BuildResult`) | ExecCore.runRule; 7 test files |
| 42 | `Agent<'t>` alias of `MailboxProcessor` | **utility** | ExecTypes (`Progress`), WorkerPool |
| 57 | `DbRecord` | **database** (the read API's record) | StorageTests, EpochTests, StaleArtifactDepTests |
| 62 | `ObserveMode` (Verdict / Recording / Output / PreHash) | **observation** (why a file is looked at) | FileRule, observeFiles, DependencyAnalysis, ExecCore |
| 75-83 | `FileStat`, `FileStat.take` | **file probe** (L0, a stat) | FileRule, observeFiles, Content.hash, Hasher key |
| 86 | `HashResult` | **hashing** | FileRule.decideHashed, Hasher |
| 95 | `Observation` (Absent / Unchanged / Changed) | **observation** (the reply) | DependencyAnalysis, ExecCore |
| 101-118 | `Content.hash` (stat, SHA-256, stat) | **hashing** (L0) | Hasher; StorageTests |
| 122-189 | `FileRule` (internal): `RuleContext`, `Decision`, `decide`, `decideHashed`, `classify`, `sameStat` | **decisions** (L1, pure) | observeFiles; FileRuleTests |
| 194-207 | `DatabaseApi` (Apply, Store, NewRun, InMemory, Close, CloseWait) | **database** | observeFiles (Apply), storeResult, ExecCore (NewRun, CloseWait), ScriptRunner (InMemory, NewRun); StorageTests (34) |
| 209-394 | `Persist` (internal): header, 0.7 picklers, `PathTable`, `encode`, `read`, `toDatabase` | **database** (format) | impl, writer, readRecords; StorageTests |
| 396-506 | `impl`: backup restore, open with retry, the lock, load / scrub secrets / upgrade / compact | **database** (file lifecycle) | openDbWith, cleanupDb |
| 508-533 | `readRecords`, `readDatabase`, `cleanup` | **database** | tests (readRecords in 7 files) |
| 535-648 | `SnapshotCell`, `WriterMsg`, `writer`, `agent` | **database** (L2) | `Db.Create` |
| 650-691 | `HasherMsg`, `Hasher` (one hash per (file, stat), a core-count semaphore) | **hashing** (L3) | created in `Db.Create`, used by observeFiles through `Db.Hasher` |
| 695-710 | `Db` (Snapshot, Post*, IDisposable) **and `Db.Hasher`** | **database**, except that it carries the hasher | everything |
| 715-729 | `RunFiles` (claims, recorded-before) | **run state** (L4) | ExecContext field; ExecCore.beginRun, createContextCore, XakeScriptBuilder; RecordShapeTests |
| 733 | `ObserveContext` (Db, Run, Hashing, OnHashed) | **observation** | ExecTypes `ctx.Observe` |
| 736-739 | `tryGetResult`, `storeResult` | **database** | DependencyAnalysis, ExecCore |
| 746-806 | `observeFiles` (claims, stats, rule, hashes, Apply, conflict rounds) | **observation** (L5) | DependencyAnalysis.observeMany, ExecCore.storeResult (A3); StorageTests |
| 812-843 | `openDbWith`, `openDb`, `noopDb`, `cleanupDb` | **database** | ScriptRunner, ExecCore; tests |
| 846-869 | `Step` (internal): step timing over `BuildResult.Steps` | **build result** | ExecCore, RecipeFunctions, XakeScript, ScriptFuncs |

About 330 of 869 lines are not the database: observation types, the stat and the hash, the
hasher, the rule, run state, `observeFiles`, `Step`, `makeResult`, the `Agent` alias. Xake.Dotnet
uses none of these names (checked).

## 2. Proposed layout

Compile order (new files in bold):

```
Prelude.fs        + Agent<'t> alias
Types.fs          + BuildResult helpers: makeResult, Step (internal)
LegacyRead.fs
Database.fs       Storage: version, Database, DbRecord, DatabaseApi, Persist, file
                  lifecycle, writer, agent, Db, open/noop/cleanup, readRecords,
                  tryGetResult, storeResult                      (L2 only)
**FileProbe.fs**  FileStat (+ take), HashResult, Content.hash, Hasher   (L0, L3)
**FileRule.fs**   ObserveMode, Observation; FileRule (internal)          (L1)
**Observe.fs**    RunFiles, ObserveContext (+ Hasher), observeFiles      (L4, L5)
RecipeBuilder.fs ... ExecTypes.fs ... DependencyAnalysis.fs, ExecCore.fs (unchanged order)
```

Dependency direction (an arrow = "uses"):

```
Observe ──> FileRule ──> FileProbe ──> Types (FileState, File)
   └──────> Database ─────────────────> Types, Pickler, LegacyRead
ExecTypes ──> Observe, Database      DependencyAnalysis, ExecCore ──> ExecTypes
```

- **`Database.fs` uses nothing of observation.** It keeps `FileState` as data and `BuildResult`
  as data; it neither stats, hashes nor decides. `Db.Create` no longer makes a hasher, and `Db`
  has no `Hasher` member.
- **`FileProbe` and `FileRule` do not know the database.** `FileRule` stays pure over
  `FileState option`, `FileStat`, `HashResult`.
- **`Observe` is the only place that joins them**: it reads `Db.Snapshot`, posts `Apply`, asks
  the hasher and claims in `RunFiles`.

### Who owns the hasher

It moves out of `Db` and becomes an engine-level object next to it: `EngineState.Hasher`
(EngineState is not frozen by `RecordShapeTests`; `ExecContext` is not touched). It is created
where the `Db` is (ExecCore.createContextCore, XakeScriptBuilder), and `ctx.Observe` passes it in
`ObserveContext.Hasher`. Behaviour is the same: one hasher per engine, shared across runs.

### Names and modules

| Today | Proposed |
|---|---|
| `Xake.BuildLog.{XakeDbVersion, XakeDbMinReadVersion, Database, newDatabase}` | `Xake.Storage.*` (`BuildLog` disappears; `addResult` stays internal) |
| `BuildLog.makeResult` | `Xake.DomainTypes` (Types.fs), same name |
| `Xake.Step` (internal) | Types.fs, same name, internal |
| `Xake.Agent<'t>` | Prelude.fs, same name |
| `Storage.FileStat`, `HashResult`, `Content.hash`, `Hasher` | module `Xake.FileProbe` |
| `Storage.ObserveMode`, `Observation`, `Storage.FileRule` | module `Xake.FileRule` (the two types public, the functions and `RuleContext`/`Decision` internal) |
| `Storage.RunFiles`, `ObserveContext`, `observeFiles` | module `Xake.Observe` |

Callers change only their `open` lines and qualifiers (`Storage.RunFiles` -> `Observe.RunFiles`
in ExecTypes, ExecCore, XakeScriptBuilder, RecordShapeTests; `open Storage` gains
`open Observe` in DependencyAnalysis and ExecCore). No function is renamed in this step.

### Public API after the move

- `Xake.Storage`: `Database`, `DbRecord`, `DatabaseApi`, `Db` (`Snapshot`, `Post`,
  `PostAndReply`, `PostAndAsyncReply`, `IDisposable`), `openDb`, `noopDb`, `cleanupDb`,
  `readRecords`, `readDatabase`, `tryGetResult`, `storeResult`, `XakeDbVersion`,
  `XakeDbMinReadVersion`, `makeResult` moves out.
- `Xake.FileProbe`: `FileStat`, `HashResult`, `Content.hash`, `Hasher`.
- `Xake.FileRule`: `ObserveMode`, `Observation`.
- `Xake.Observe`: `RunFiles` (its `Claim`/`RecordedBefore` internal), `ObserveContext`,
  `observeFiles`.
- Internal as today: `Persist`, the agent, the writer, `SnapshotCell`, `Db.Create`, `FileRule`'s
  functions, `Step`.

Whether `DatabaseApi` and the raw `Post*` stay public is candidate 6, a separate topic; this
layout neither needs nor blocks it (`Observe` is in the same assembly).

## 3. Choices for the owner

1. **The format in its own file?** `Persist` (190 lines: header, picklers, path table, read,
   encode) is the database, so it may stay. Splitting it into `DatabaseFormat.fs` leaves
   `Database.fs` at about 360 lines (records, lifecycle, agent, writer) instead of about 550.
   It needs `DbRecord`, the version constants and `toDatabase`'s `Database` to move with it or
   before it. Recommendation: yes, as a second commit.
2. **Module names**: `FileProbe` / `FileRule` / `Observe` as above, or other names.
3. **Renames now or later**: `observeFiles` -> `Observe.files`, `ObserveContext` ->
   `Observe.Context`, and `RequireQualifiedAccess` on `ObserveMode`/`Observation` (`Verdict`,
   `Changed`, `Absent` are short names). Recommendation: later, with the public API topic, so this
   step is a pure move.
4. **The hasher's owner**: `EngineState.Hasher` (recommended), or per run in `RunFiles`.

## 4. How the change is made (after the decision)

One commit: the move, no behaviour or format change. Then the format split if chosen. Each with
the full test suite and the idle-perf bench, and `architecture.md` updated (layers table, section
5).
