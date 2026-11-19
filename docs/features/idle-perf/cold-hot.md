# Idle performance: cold and hot no-change runs (2026-11-20)

The owner's questions after the design: how 4.0 decided, whether a no-change run hashes, cold
against hot. Measurements only; the design in design.md is still open.

## How each version decides a file dependency

- **4.0**: the recorded stamp is inside the target's `BuildResult` (`FileDep (file, wrtime)`), read
  with one `GetResult` per target. For each dependency, on the calling thread, it calls
  `File.Exists` and then `File.GetLastWriteTime`: two stats and no agent. Measured 2.3-2.5 us per
  dependency.
- **5.0**: the stamp moved out of the result into the file table (`FileRec`, one per file, kept
  by the agent; this is how the database got 70x smaller). For each dependency, the verdict calls
  `observeSince` and then `Async.RunSynchronously`, which is one `Observe` message to the agent.
  - The agent stats a file once per run (first observation). After that it answers from its
    cache, but still does `Map.tryFind`, `HashSet.Add` and `File.Exists` for each dependency.
  - No component measured this path at scale. Tests have tens of files. The epoch and cut-off
    work measured database size and hashing, not the time of a no-change run with 10^5
    dependencies.

## Cold and hot (bench/build.fsx: 1,188 targets x 150 deps, 4,433 distinct sources, -t 16)

Cold is the first no-change run after a full build, in a new process. Hot is the runs after it.
The counters were temporary, in the agent.

| Run | 4.0.0 | 5.0 (this branch = preview.2) | hashes | Observe calls (5.0) |
|---|---|---|---|---|
| full build | 0.40-0.47 s | 5.5-7.9 s | 1,188 (outputs) | 178,200 Recording, 1,188 Verdict, 1,188 Output, 1,188 PreHash, 1,188 Settle |
| cold | 0.56 s | 2.37 s | 0 | 181,764 Verdict |
| hot | 0.57-0.64 s | 2.24-2.38 s | 0 | 181,764 Verdict |

Cold and hot are the same. A no-change run hashes nothing:
- The sources were never hashed. A first `Recording` with no record stores no hash.
- Their stat is equal, so it is kept.
- The outputs were hashed once, by the full build.

**The full build is 13-17x slower than 4.0 here.** This is a separate regression, not looked into
yet.

## Per dependency in a hot run (bench/micro.fsx, 178,200 operations)

| Item | us/op |
|---|---|
| `Map<File,_>.tryFind` (10k files) | 0.45-0.54 |
| `HashSet<File>.Add` | 0.13 |
| `Dictionary<File,_>` lookup | 0.12 |
| `File.Exists` (one stat) | 1.6-1.7 |
| `FileInfo` exists + time + length (one stat) | 1.5-2.0 |
| 4.0's `File.Exists` + `GetLastWriteTime` | 2.3-2.5 |
| mailbox round trip (`PostAndReply`) | 7.2-7.3 |
| `PostAndAsyncReply` + `Async.RunSynchronously` | 7.0-7.8 |
| trivial `async { return 1 }` + `Async.RunSynchronously` | 4.7 |

The hot 5.0 cost per dependency is about 12.5 us:
- about 7 us of round trip (two-thirds of it is `Async.RunSynchronously` itself);
- about 2.3 us of agent work (`Map.tryFind`, `HashSet.Add`, `File.Exists`);
- about 3 us of mailbox and agent loop overhead.

Across 181,764 calls that is about 2.3 s. 4.0 paid about 2.4 us (two stats) for each of 178,200
dependencies (about 0.42 s), plus about 0.15 s of other verdict work.

## What hot could cost without the agent in the path

- **Stat kept** for each repeat check (today's semantics): a dictionary lookup plus `File.Exists`
  is about 1.85 us. The pass would take about 0.33 s plus the ~0.15 s of other verdict work, so
  about 0.5 s against 4.0's 0.57 s.
- **One stat per distinct file per run**, then in memory: 4,433 x 2 us is about 10 ms, plus
  178,200 lookups x 0.12 us is about 20 ms, plus the ~0.15 s of other verdict work. That is about
  0.2 s, roughly 3x faster than 4.0. The cost is that a file deleted during a run, after its
  first check, would not be seen by later verdicts in the same run. The agent already decides
  "changed" once per run; only existence is re-checked today.
- **The batch** (option 4, measured 0.66-0.93 s) removes the round trips but keeps the stat and
  the map lookup in the agent for each dependency. With the agent's repeat path made a
  dictionary lookup with no stat, the batch would also come close to about 0.2 s.

## The agent's 1.1 s, broken down (2026-11-20, measured)

Temporary timers ran inside the agent and around the walk on this branch (= preview.2). For
comparison, 4.0.0 was built from source with timers around its walk. Hot run, 3 runs each.

### 5.0, per message (177,331 cached-path verdicts + 4,433 first observations)

| Step | us/msg | Above 4.0? |
|---|---|---|
| path normalisation, `File` construction | 0 | no: the `File`s come from the loaded results, none is built in the walk |
| `pending.TryGetValue` | 0.02 | |
| `Map.tryFind file db.Files` (10k, `CompareOrdinal` on ~100-char paths with a common prefix) | 0.94-1.02 | yes: 4.0 had the stamp in the result, with no lookup |
| `observed.Add` (`HashSet`, ordinal hash of the path) | 0.23 | yes: 4.0 had none |
| `File.exists` | 2.9-3.3 | about the same as 4.0's two stats (2.3-2.8), for one stat |
| `classify` (epoch compare) | 0.03 | no |
| first observation (`FileInfo`, `sameStat`, `FileRec`): 4,433 of them | total 17-18 ms | no |
| `chnl.Reply` (wakes the caller) | 1.43-1.54 | yes: transport |
| handler total | 5.8-6.3 | |
| from the handler's end to the next `Receive` (the `return! loop` async machinery) | 3.0-3.7 | yes: transport |
| allocated in the handler | 346 B | |

The same calls are about 2x slower in the agent than on one thread (`micro.fsx`, `micro2.fsx`):

| Call | Calling thread, tight loop | Inside an agent fed one message at a time |
|---|---|---|
| `File.exists` | 1.1-1.7 us | 2.3 us (`micro2.fsx`), 2.9-3.3 us (the real agent) |
| `Map.tryFind` | 0.45-0.54 us | 0.94-1.02 us |

So the agent's own lookups are cheap: 1.2 us in a tight loop. The doubling looks like the cost of
running each message on whichever thread-pool thread wakes up: the caches are cold, and on this
machine the thread may also move between cores. This is generic: a trivial agent shows it too. It
is not verified with a profiler.

### The walk as a whole

| | 4.0.0 (from source) | 5.0 |
|---|---|---|
| walk | 468-558 ms | 2,271-2,356 ms |
| per file dependency | 2.3-2.8 us (two stats, the whole check) | 12.1-12.6 us per `Observe` round trip, measured at the caller |
| allocated during the walk | 13 MB (82 B/dep) | 528 MB (3,089 B/dep) |
| GCs during the walk | 0 | gen0 +10..15, gen1 +1..2, gen2 +1 |

### One mailbox round trip (micro2.fsx, a trivial agent)

| Pattern | us | B/call |
|---|---|---|
| `PostAndReply` | 6.8-6.9 | 1,213-1,218 |
| `PostAndAsyncReply |> Async.RunSynchronously` (ours) | 7.3-7.5 | 1,765 |
| `async { let! x = PostAndAsyncReply ... }` then `RunSynchronously` (`observeSince`'s shape) | 6.4-6.6 | 2,168 |
| `async { return 1 } |> Async.RunSynchronously`, no agent | 4.5-4.9 | 728 |

The round trip is the generic F# `MailboxProcessor` plus `RunSynchronously` pattern, not anything
specific to Xake's agent. A trivial agent costs 6.8-7.5 us. Most of that is
`Async.RunSynchronously` (4.5 us and 728 B even with no agent), plus two thread wake-ups.
Xake's own share on top of the generic pattern: ~0.9 KB per call (the `observeSince` closures, the
`Observation`, the handler's tuples), and the slower stat and lookup in the agent.

### Accounting for 12.4 us per dependency (5.0) against 2.5 us (4.0)

- The work 4.0 also did, one stat instead of two: 2.9-3.3 us in the agent, versus 2.3-2.8 us for
  4.0's two stats on the calling thread. This is equal or more because of where it runs, not
  because of what it does.
- New lookups: `Map.tryFind` + `HashSet.Add` + pending, about 1.2 us. In a tight loop they would
  be 0.6 us.
- Transport: the reply (1.5 us), the loop back to `Receive` (3.0-3.7 us) and the caller's
  post/wait (`RunSynchronously`), about 3 us. About 8 us in all, plus 3 KB allocated and the GCs
  that follow.

The owner's point holds: apart from the messaging, the per-file work is about what 4.0 paid.
About two-thirds of the 5.0 cost is transport. Most of the remaining third is the agent's
thread-hopping penalty on the same stat and lookups.
