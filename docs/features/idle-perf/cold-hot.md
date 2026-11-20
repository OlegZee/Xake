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

## The full build (2026-11-20, measured)

Same bench, empty database, `-t 16`: 5.0 takes 5.5-8.0 s against 0.40-0.47 s on 4.0. Temporary
timers ran in the agent (per message kind: count, busy time, queue length at dequeue; time spent
in `Receive`) and at the caller (latency of `observe ctx Recording`, `ExecCore.fs:387`). Two
runs:

| Agent messages | n | busy per message | busy total | avg queue at dequeue |
|---|---|---|---|---|
| `Observe Recording` | 178,200 | 3.9-4.7 us | 695-833 ms | 245-310 |
| `Store` (with `persist`) | 1,195 | 190-320 us | 232-387 ms | 140-275 |
| `Settle` | 1,188 | 30-47 us | 35-56 ms | 117-128 |
| `GetResult` | 4,783 | 1.8-2.5 us | 9-12 ms | |
| `Observe` Verdict/Output/PreHash | 3 x 1,188 | 3.4-6.8 us | ~20 ms | |
| `persist` (one `Write` + `Flush` per record) | 5,628 | | 301-438 ms | |

- The agent's own work is about 1.1-1.3 s. 4.0 had none of it: a `need` of a file without a
  rule took the file's time on the calling thread.
- The agent spends 4.2-6.7 s in `Receive`. Almost all of that is in 118-184 waits longer than
  100 us, together 4.1-4.7 s, about 25-40 ms each. When the agent resumes after one of them,
  100-140 messages are already queued.
- So the agent is not waiting for messages. It is ready and waiting for a thread: its loop runs
  as one work item on the shared thread pool, and the pool's queue holds the build's own flood
  of continuations (1,188 targets x 150 parallel `need` items, each posting a `Recording`). Each
  time the agent loop goes back to the pool, it waits behind that flood.
- The likely trigger is the F# async trampoline, which hands a long-running async loop back to
  the pool every few hundred binds; a profiler has not confirmed it.
- At the caller, a `Recording` takes 1.9-3.0 ms on average, against the agent's 4 us of work.

What this means for the fix:
- Fewer messages (a batch per `need` list: 1,188 instead of 178,200) shrinks both the flood and
  the agent's queue.
- Keeping the file table out of the agent (direction C) removes the dependency on the agent's
  scheduling altogether.
- Giving the agent its own thread instead of the pool would remove the parking, but not the
  round-trip cost per message.

## Mailbox throughput (2026-11-20, bench/mailbox.fsx)

How many "the state of this file" requests an agent like the database agent answers. The agent
holds an immutable `Map` of 10,000 files, as Xake's does, and answers from memory unless noted.
180,000 requests per pass, 18 cores, two runs; the second run's figures in brackets.

| Pattern | us/request | requests/s | files/s | B/request |
|---|---|---|---|---|
| one caller, `PostAndReply` | 7.3 (7.7) | 137,000 | 137,000 | 1,287 |
| one caller, `PostAndAsyncReply` + `RunSynchronously` (Xake's verdict today) | 9.8 (8.7) | 102,000 | 102,000 | 1,924 |
| same, the agent also runs `File.Exists` | 14.0 | 71,000 | 71,000 | 2,046 |
| one async loop awaiting each reply, no `RunSynchronously` | 2.4 (2.2) | 418,000 | 418,000 | 1,517 |
| 4 / 16 / 64 async callers, each awaiting its replies | 1.40 / 1.36 / 1.31 | 713,000-766,000 | same | 1,177 |
| all requests at once, `Async.Parallel` of 150 per group | 1.5 (1.6) | 658,000 | 658,000 | 1,596 |
| same, the agent also runs `File.Exists` (the full build's shape) | 3.6 | 281,000 | 281,000 | 1,576 |
| `Post` without a reply, then one `Ping` (the agent's own ceiling) | 0.59 | 1,700,000 | 1,700,000 | 1,308 |
| one caller, batch of 10, `RunSynchronously` | 13.0 | 77,000 | 772,000 | 2,472 |
| one caller, batch of 150, `RunSynchronously` | 46 | 21,600 | 3,240,000 | 8,080 |
| one caller, batch of 1,000, `RunSynchronously` | 237 | 4,200 | 4,210,000 | 42,080 |
| 16 async callers, batch of 150 | 35.5 | 28,100 | 4,220,000 | 7,221 |
| own thread (`BlockingCollection`, `Dictionary`), one caller, blocking `.Result` | 0.94 (0.85) | 1,070,000 | 1,070,000 | 152 |
| own thread, 16 async callers | 1.03 | 967,000 | 967,000 | 777 |
| no agent: `Map.tryFind` / `File.Exists` on one thread | 0.23 / 1.3-1.4 | | | 24 / 0 |

Findings:
- The agent itself is not slow. Answering from memory it handles 1.7 M messages/s, about 0.6
  us each, and 0.7 M/s when callers keep its queue full.
- The cost is the caller waiting for each reply. Synchronous waiting (`RunSynchronously`,
  `PostAndReply`) is about 7-10 us a request, so one waiting caller gets about 100,000 answers/s.
  The same wait inside an async loop, with no `RunSynchronously`, is 2.2-2.4 us.
- A stat inside the agent makes it serial on the file system: 71,000/s with one caller, and
  281,000/s with a full queue.
- A batch of 150 costs about 46 us for one caller, about 0.3 us a file: 3.2 M files/s, and
  4.2 M/s with 16 callers. That is about 30x one request per file with `RunSynchronously`, and
  faster than a `Map.tryFind` loop would be with the transport added.
- A service on its own thread (a blocking queue and a `Dictionary`) answers one waiting caller in
  0.85-0.94 us with 152 B, about 10x the `MailboxProcessor` with `RunSynchronously`.
- For the bench's 180,000 dependencies this gives:
  - Xake today: about 1.8 s of transport, plus 1.4 s of stats when the agent also stats;
  - batches of 150 from one caller: about 0.06 s;
  - the stats, moved to the callers: about 0.25 s on one thread, less on several.
- The micro-benchmark does not reproduce the full build's thread-pool parking. Its flood of
  180,000 requests drains in 0.3-0.6 s, because nothing else competes for the pool; in the real
  build the recipes' own continuations do.

## Step 2 measured (2026-11-20, design.md L2-L5)

The `Storage.observeFiles` facade, the snapshot, `Apply`, the hasher and `RunFiles`. The verdict
is still synchronous, with one `RunSynchronously` per target instead of per file, and the
`need` of plain files is still one facade call per file (step 3 changes both). Same bench, `-t 16`:

| Run | Before (preview.2) | Step 2 | 4.0.0 (same session) |
|---|---|---|---|
| full build | 5.5-8.0 s | 1.51 s (2 runs) | 0.41-0.45 s |
| no-change | 2.24-2.38 s | 0.87-0.92 s (3 runs) | 0.58-0.63 s |

Split of the no-change walk (temporary timers): the facade takes 0.71-0.81 s of a 0.85-0.96 s
walk, for 2,388 calls and 181,764 files. The cost is almost all in the existence check per
dependency (`File.Exists`, about 2 us, the owner's decision) and one `Map` lookup per file
against the snapshot, both on the one thread of the walk. Claims cost 0.08 s and the decisions
0.01 s. In a no-change run the agent receives one `NewRun` and the `Store`s of the phony targets,
and nothing else. Step 3 runs the walk on several threads.
