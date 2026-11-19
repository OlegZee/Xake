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
