# Topic 2: the public API of the four modules

State at 48ca3c7. Modules: `BuildLog` + `Persist` (DatabaseFormat.fs), `Storage` (Database.fs),
`FileProbe` (FileProbe.fs), `FileTracker` (FileTracker.fs). Xake.Dotnet uses none of them.

## 1. What is public today and who uses it

"Core" means callers inside Xake.dll outside the four modules; tests see internals anyway.

| Module | Public item | Core callers | Kind |
|---|---|---|---|
| `BuildLog` | `XakeDbVersion` | none (tests) | read API |
| | `XakeDbMinReadVersion` | none | read API |
| | `Database` (Status, Files, Epoch) | ExecCore (`Snapshot.Files`, A3) | read API / snapshot |
| | `newDatabase` | none | internal helper |
| | `DbRecord` (Result, FileState) | none (tests) | read API |
| `Storage` | `DatabaseApi` (Apply, Store, NewRun, InMemory, Close, CloseWait) | ExecCore (NewRun, CloseWait), ScriptRunner (InMemory, NewRun) | raw mailbox |
| | `Db.Post`, `PostAndReply`, `PostAndAsyncReply` | the same | raw mailbox |
| | `Db.Snapshot` | ExecCore | read |
| | `Db : IDisposable` | none (tests: `use db = openDb ...`) | lifetime |
| | `openDb` (lock wait fixed at 30 s) | none (tests) | lifetime |
| | `openDbWith` (internal) | ScriptRunner | lifetime |
| | `noopDb`, `cleanupDb` | ScriptRunner, ExecCore / tests | lifetime |
| | `readRecords`, `readDatabase` | none (tests) | read API for tools |
| | `tryGetResult` | DependencyAnalysis, ExecCore | read |
| | `storeResult` | ExecCore | engine |
| `FileProbe` | `FileStat` (+ `take`), `HashResult`, `Content.hash` | none (tests) | I/O |
| | `Hasher` (`Hash`) | ExecCore creates it; `EngineState.Hasher` holds it | engine handle |
| `FileTracker` | `ObserveMode`, `Observation` | DependencyAnalysis, ExecCore | engine |
| | `RunFiles` (`Claim`, `RecordedBefore` internal) | ExecContext field; ExecCore, XakeScriptBuilder create it | engine handle |
| | `ObserveContext` | `ExecContext.Observe` (a public member) | engine |
| | `observeFiles` | DependencyAnalysis, ExecCore | engine |

Every core caller is the engine itself (ExecCore, DependencyAnalysis, ScriptRunner,
XakeScriptBuilder). Nothing outside the engine needs to observe, hash, store or start a run.

## 2. Why some types must stay public

Three types appear in public records, so the type itself must be public:

- `Storage.Db` in `EngineState.Db`;
- `FileProbe.Hasher` in `EngineState.Hasher`;
- `FileTracker.RunFiles` in `ExecContext.RunFiles` (frozen within 5.x).

`ExecContext.Observe` is a public member returning `ObserveContext`, which keeps
`ObserveContext`, `Db`, `Hasher`, `RunFiles` public. The member can become internal.

## 3. Proposal

Principle: these modules are engine internals. Public is (a) the read API a tool needs to look
at a `.xake` file, and (b) opaque handles: a public type whose constructor and members are
internal, as `RunFiles` already is.

| Module | Public after | Internal after |
|---|---|---|
| `BuildLog` | `XakeDbVersion`, `Database`, `DbRecord` | `XakeDbMinReadVersion`, `newDatabase`, `addResult` |
| `Storage` | `Db` (opaque; `Snapshot` public), `readRecords`, `readDatabase`, `tryGetResult` | `DatabaseApi`, `Db.Post*`, `openDb*`, `noopDb`, `cleanupDb`, `storeResult`, and new `newRun`, `inMemory`, `close` |
| `FileProbe` | `Hasher` (opaque) | `FileStat`, `HashResult`, `Content.hash`, `Hasher.Hash` |
| `FileTracker` | `RunFiles` (opaque) | `ObserveMode`, `Observation`, `FileRule`, `ObserveContext`, `observeFiles` |
| `ExecContext` | | the `Observe` member |

The raw mailbox becomes functions with one purpose each, used by the engine in place of
`Post*` (candidate 6):

```
Storage.newRun   : Db -> Async<int>        // NewRun
Storage.inMemory : Db -> unit              // InMemory
Storage.close    : Db -> Async<unit>       // CloseWait: drains the writer, releases the lock
Storage.storeResult : Db -> BuildResult -> Async<unit>
Storage.apply (internal to FileTracker's use) : Db -> writes -> Async<conflicts>
```

## 4. Questions for the owner

1. **`Db.Dispose` does not close.** It disposes the agent's mailbox only: the writer is not
   drained, the stream and the `.xake.lock` stay open until the process ends. The engine closes
   with `CloseWait`; tests use `use db = openDb ...`. Proposal: `Dispose` = `close`
   synchronously, or drop `IDisposable`. This is a behaviour change (tests only).
2. **`tryGetResult` and `Db.Snapshot` public?** No external caller today. A script or tool may
   want the last result of a target; keep them public, or make the read API only
   `readRecords`/`readDatabase`.
3. **`openDb` public?** An embedder starts through `XakeEngine`/`xakeScript`, which open the
   database themselves. Proposal: internal, with `openDbWith`'s parameters folded into one
   internal `open`.
4. **Names.** The database has five names: file `Database.fs`, module `Storage`, type `Db`,
   record `BuildLog.Database`, union `DatabaseApi`; the format file's public module is
   `BuildLog`. Options:
   - a. leave names, change visibility only (smallest change);
   - b. one namespace `Xake.Storage` over both files: `Xake.Storage.Database` (the record),
     `DbRecord`, `Persist` (internal), `Db` and a module `Db` with the functions (`Db.open`,
     `Db.tryGetResult`, `Db.store`, `Db.newRun`, `Db.close`). `BuildLog` and `DatabaseApi`
     disappear as public names.
   Recommendation: b, after the visibility change, as its own commit.
5. **Renames in `FileTracker`** (deferred to this topic): `observeFiles` -> `FileTracker.observe`,
   `ObserveContext` -> `FileTracker.Context`. Once they are internal the gain is small;
   recommendation: do it together with 4b.
