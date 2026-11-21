# Idle performance: design by layers (2026-11-20, third version)

This version replaces the first two: the batched query (d6522fe) and the caller stats with the
agent deciding (057cba5, 8e03e77). Both are still in the git history.

It follows the owner's decisions in [brief.md](brief.md):
- the database decides nothing;
- the per-run state belongs to the run;
- hashes are computed behind their own mailbox;
- no `Async.RunSynchronously` on the dependency path;
- existence is checked per dependency;
- the observation API lives in the database module.

The measurements are in [cold-hot.md](cold-hot.md), and the overview with diagrams is
[architecture.html](architecture.html).

The design is laid out bottom-up, one layer per section. Each layer lists:
- what it owns;
- its interface;
- what it must not do;
- its tests.

A layer calls only the layers below it.

```
L7  ExecCore             tasks, need, outputs, Store
L6  Verdicts             the verdict of a target, its memo per run, warm, estimate
L5  Observation facade   observe a group of files: stat, rule, hash, one batch of writes
L4  RunFiles             per-run state: files seen by a verdict, files recorded by a body
L3  Hasher (mailbox)     one hash per (file, stat); everyone else waits for it
L2  Database agent       records in memory, a published snapshot, atomic batch writes, .xake
L1  FileRule (pure)      known record + stat + mode -> keep / write / hash
L0  Disk                 stat, content hash
```

## L0. Disk

**Owns:** nothing. These are functions that read the file system.

```fsharp
type FileStat = Missing | Present of stamp: Timestamp * size: int64
module FileStat = val take : File -> FileStat
module Content  = val hash : File -> HashResult   // stat, SHA-256, stat (as today)
type HashResult = Hashed of stamp * size * hash: uint64 | Unreadable of FileStat
```

**Must not:** keep state or know about records or epochs.

**Tests:** today's `Content.hash` tests, plus a test that `Unreadable` carries the stat.

## L1. FileRule (pure)

**Owns:** the decision that the database agent makes today in `observe` and `settle`
(Database.fs). It moves out unchanged, as a pure function.

```fsharp
type RuleContext = { Epoch: int; Hashing: bool; SeenByVerdict: bool; RecordedBefore: bool }
type Decision =
  | Keep of FileState option   // answer from the record; nothing to write
  | Write of FileState         // the new record (applied only if the record is still `known`)
  | NeedHash                   // hash the file, then decideHashed

val decide       : known: FileState option -> FileStat -> ObserveMode -> RuleContext -> Decision
val decideHashed : known: FileState option -> HashResult -> ObserveMode -> RuleContext -> Decision
val classify     : since: int -> FileState option -> Observation   // Absent | Unchanged | Changed
```

These are today's rules, case by case:

| Mode | When | Today | Decision |
|---|---|---|---|
| any | the stat says missing | `Seen None` | `Keep None`, so `Absent` |
| `Verdict` | a verdict already saw the file in this run | `Seen known` after `File.exists` | `Keep known`. Existence comes from the stat, which is taken per dependency |
| `Output false` | | `stored (moved ())` | `Write` with `changed` = epoch and no hash |
| `Output true` | hashing | `Hash` | `NeedHash` |
| `PreHash` | hashing, no hash in the record, equal stat | `Hash` | `NeedHash`. Otherwise `Keep` |
| any | equal stat (within 10 ms, size equal or unknown) | `stored {k with Size}` | `Keep k`. `Write` only when the size is filled in |
| `Recording` | the file was already recorded in this run (A1) | `Seen k` | `Keep k` |
| `Verdict`, `Recording` | moved stat, hashing on | `Hash` | `NeedHash` |
| any | moved stat, hashing off, or no record | `stored (moved ())` | `Write` with `changed` = epoch |

`decideHashed` follows today's `settle` (equal hash keeps `changed`; an output never hashed and
not moved fills in its hash; `PreHash` writes only the hash; `Unreadable` writes a change
without a hash, or `Keep` for `PreHash`).

**Must not:** do any I/O, mutate anything, or know about agents.

**Tests:** a table test per row and per `settle` case, ported from today's `StorageTests`
(`Observe` and `Settle` against an agent become calls to the pure function). These are the
fastest and most complete tests of the change semantics.

## L2. Database agent

**Owns:**
- the records: `Status: Map<Target, BuildResult>`, `Files: Map<File, FileState>`, `Epoch`;
- the path table and the `.xake` stream, with the lock, compaction, upgrade, `.bak` and
  `InMemory` handling as today.

**Gives up:** `Observe`, `Settle`, `GetResult`, `pending`, `observed`, `recorded`, `HashRequest`,
and every stat. It makes no decisions.

```fsharp
type Snapshot = { Status: Map<Target, BuildResult>; Files: Map<File, FileState>; Epoch: int }

type DatabaseApi =
  /// Writes records if each is still the one the caller decided against (compare-and-set,
  /// per file). Replies with the conflicts: the file and its current record.
  | Apply   of (File * expected: FileState option * FileState) list * AsyncReplyChannel<(File * FileState option) list>
  /// Stores a result; replies once it is in the snapshot (see "Store is awaited" below).
  | Store   of BuildResult * AsyncReplyChannel<unit>
  | NewRun  of AsyncReplyChannel<int>
  | InMemory | Close | CloseWait of AsyncReplyChannel<unit>

/// The current snapshot, published by the agent after every message (an immutable value
/// behind a volatile reference). Readers never post.
val snapshot : Db -> Snapshot
```

- **Reads** (`getResult`, a file's record, the epoch) come from the snapshot, with no message.
  The maps are immutable, so a reader always sees a consistent version.
- **Writes:** one `Apply` per batch. The records that pass the compare-and-set are pickled into
  one buffer and written with one `Write` and one `Flush`. The rule "a record that fails to pickle
  writes nothing and leaves the table as it was" holds for the whole batch.
- **Store is awaited.** Today a reader orders itself after a `Store` through the mailbox, which
  is FIFO. A snapshot reader has no such ordering. So the task awaits `Store` (async, no blocking)
  before it reports itself finished. A dependent that sees `Finished` then also sees the result.
  This costs one round trip per built target.
- **Epoch:** `NewRun` increments it and publishes it in the snapshot. A write's `changed` uses
  the snapshot's epoch at decision time: the newest handed out, as today for overlapping engine
  runs.

**Must not:** read project files, decide whether something changed, or wait for anyone.

**Tests:**
- the compare-and-set: an equal `expected` applies, a different one is a conflict and replies
  the current record;
- one write per `Apply`;
- after `Store`'s reply, the snapshot contains the result;
- `InMemory` appends nothing;
- every existing open, lock, compaction, upgrade and torn-record test stays.

## L3. Hasher (mailbox)

**Owns:** the hashes that are being computed. It replaces today's `pending` and the
`HashNeeded`/`Settle` protocol.

```fsharp
/// The hash of the file as it is at `stat`; one computation per (file, stat) while it runs,
/// every other asker gets the same result.
val hash : Hasher -> File -> FileStat -> Async<HashResult>
```

- The mailbox keeps a map from (file, stamp, size) to the waiting callers. The first request
  starts the hash on the thread pool, limited to as many at once as there are cores (a
  semaphore). Requests that arrive meanwhile wait for the same result. When the hash finishes,
  every waiter gets the result and the entry is removed.
- The mailbox only coordinates; the hashing itself runs in parallel. One hasher serves the
  engine (hashes do not depend on the run), and it is closed with the database.
- No completed-hash cache is needed: once a hash is written, its record carries it, so later
  observations with an equal stat need no hash.

**Must not:** know about records, epochs or runs.

**Tests:**
- two concurrent requests for one (file, stat) compute once;
- a different stat computes again;
- the concurrency limit holds;
- a failing hash replies `Unreadable` to every waiter.

## L4. RunFiles (per-run state)

**Owns:** what today's agent keeps in `observed` and `recorded`, now per run instead of
cleared by the next `NewRun`.

```fsharp
type RunFiles =
  member SeenByVerdict : File -> bool    // test and set
  member RecordedBefore : File -> bool   // test and set (A1)
```

These are two `ConcurrentDictionary` sets, created in `beginRun` and kept in `ExecContext`. With
overlapping engine runs, each run now has its own sets. Today the sets are shared, and a second
run's `NewRun` clears the first run's. This is a small behaviour change, and a correction.

**Must not:** hold records or results.

**Tests:** covered through L5.

## L5. Observation facade (`Storage.observe`)

**Owns:** the sequence for a group of files. It is the only place where L0-L4 meet.

```fsharp
val observe : ctx -> ObserveMode -> since: int -> File list -> Async<Observation[]>
val getResult : Db -> Target -> BuildResult option        // from the snapshot
val store : Db -> BuildResult -> Async<unit>
```

The sequence, all on the calling thread except the hashing:
1. Take the stat of every file (L0).
2. Take the snapshot once. For each file, run `decide` against its record (L1), using
   `RunFiles` (L4) and the snapshot's epoch.
3. `NeedHash`: hash the file (L3; the hashes of one batch run in parallel), then `decideHashed`.
4. Send all `Write`s in one `Apply` (L2). No writes means no message.
5. For each conflict: stat the file again, decide against the current record, and apply again.
   This repeats at most 3 times, then the current record is taken as is. The fresh stat is what
   stops an older stat from overwriting a newer record (critic's M3).
6. Return `classify since` for each file.

**Must not:** keep state of its own.

**Tests:**
- a batch equals single observations (same records afterwards);
- two callers race on a new file, and one record wins;
- stats arriving in reverse order never move a stamp back;
- a moved file observed by two callers is hashed once;
- a no-change batch sends no message to the agent.

## L6. Verdicts (`DependencyAnalysis`)

**Owns:** the verdict of a target and its memo per run, the warm walk and the estimate.

```fsharp
type Verdicts =
  member Of       : Target -> Async<ChangeReason list>
  member Warm     : Target list -> Async<unit>
  member Estimate : Target -> int<ms> * Target list   // synchronous, after Warm
val create : ctx -> current -> Verdicts
```

- `Of`:
  1. `getResult` from the snapshot.
  2. One `observe Verdict epoch` over the distinct files.
  3. The dependees' verdicts through the memo, in parallel.
  4. `getDepState`, unchanged and pure.
- The memo is `Lazy<Task>` per target. A wait-for map refuses an edge that would close a cycle,
  and that edge counts as a change (critic's B2).
- `Warm` walks the targets `getDurationDeps` would visit, regardless of schedule. `dryRun` uses
  it as well.
- `Estimate` reads durations from the snapshot's results: no `getExecTime` and no message.

**Must not:** stat, hash or post to the agent. It works only through L5.

**Tests:**
- two entries into one cycle at once;
- as-requested with the progress estimate;
- `dryRun`;
- existing verdict, epoch and cut-off tests.

## L7. ExecCore

- `UpToDate: Target -> Async<bool>` and `NeedRebuild` call `Verdicts.Of`.
- `execParallel` handles the plain files of a `need`:
  - rule selection, the spelling check, `EXPLAIN_RULE` and the "Neither rule nor file" error,
    all before anything starts, as today;
  - then one `observe Recording` for all of them, as one branch of `Async.Parallel` next to the
    rule targets.
- The outputs and the pre-hash: one `observe` per task.
- `store` is awaited.
- Delegated results: after `store`, the files of their dependencies that have no record in the
  snapshot are observed with `Verdict` and no hashing, as today. No special reply is needed
  (this resolves the critic's M6).

## What goes through the agent (benchmark)

| | No-change now | No-change after | Full build now | Full build after |
|---|---|---|---|---|
| file observations | 181,764 messages | 0 (every stat equal, nothing to write) | 183,000 messages | about 2,400 `Apply` (only batches with writes) |
| `GetResult` | 1,256 | 0 (snapshot) | 4,783 | 0 |
| `Store` | 7 | 7 | 1,195 | 1,195 (awaited) |
| hashes | 0 | 0 | 1,188 via `Settle` | 1,188 via the hasher |
| stats inside the agent | 181,764 | 0 | about 183,000 | 0 |

In a no-change run the agent receives about 8 messages, which is less than 4.0 sent. The walk
costs a stat (1.1-1.7 us), a `Map` lookup (about 0.5 us) and the rule per dependency, spread
over several threads. Expected: about 0.1-0.2 s, against 0.56-0.64 s on 4.0.

## What does not change

- The database format and the records written, case by case (L1 is today's rule).
- Epochs, `since`, cut-off, hashing once per (file, stat).
- `--dryrun` / `--dump` (`InMemory`).
- Verdict reasons and their order.

## What changes in behaviour (admitted)

- Overlapping engine runs keep their own per-run sets (L4). Today a newer run's `NewRun` clears
  the older run's.
- `Store` is awaited by the task (L2): one round trip per built target. It is async, not
  blocking.
- API: `ExecContext.UpToDate` becomes async. `Storage.DatabaseApi` is reduced to
  `Apply`/`Store`/`NewRun`/... . `HashRequest`, `Observe` and `Settle` disappear. New:
  `FileStat`, `Storage.snapshot`, `Storage.observe`. All of them were new in 5.0.

## Order of work

1. L0-L1: the pure rule, with the ported table tests. No behaviour change yet: the agent calls
   the rule.
2. L2-L5: the snapshot, `Apply`, the hasher, `RunFiles` and the facade, then measure (no-change
   and full build, the agent's `Receive` waits).
3. L6-L7: the async verdict and memo, `Warm`, and the callers.

Each step leaves the tests green and can be reviewed on its own.

## Budget (rough)

- **Code:** about 600 lines touched. The agent loses about 150 lines; the rule (about 120), the
  hasher (about 60), the facade (about 80) and the async verdict (about 150) are new or
  rewritten.
- **Tests:** about 400 lines.

## Later

- `Files` as a structure faster than `Map` (an immutable hash map).
- The agent on its own thread (measure after step 2).
