# Idle performance: a no-change run on 5.0 (brief)

## Problem

A consumer's large build (macOS, 18 CPU, ~1,700 targets) reports a no-change run at 8.7-8.9 s on
Xake 5.0.0-preview.2 against 4.0 s on Xake 4.x. It must be fixed before 5.0.0.

## Reproduction (2026-11-19, this machine, `-t 16`)

`bench/build.fsx`: 6 target groups under one phony `build`, 198 file targets each (1,188), 150
file dependencies per target over 10,000 sources (4,433 distinct used). No-change run, "Build
completed":

| Version | Build completed | Process wall |
|---|---|---|
| 4.0.0 | 0.56-0.64 s | 1.45 s |
| 5.0.0-preview.2 | 2.29-2.50 s | 3.16 s |
| 5.0.0-preview.2 `--schedule=as-requested` | 2.28-2.33 s | |
| 5.0.0-preview.2 `--content-cutoff=off` | 2.34-2.44 s | |
| prototype (option 1 below) | 0.47-0.59 s | |

Neither new mechanism (critical path, content cut-off) is the cause. The time outside "Build
completed" (fsi, load, close) is the same on both versions here (~0.8 s); the consumer's extra
~2 s outside the timer was not reproduced (a script compile on a cache miss is a guess, unchecked).

## Where the time goes (timing patch, preview.2 code)

| Phase | Time |
|---|---|
| `beginRun` | 4 ms |
| group-start walk (`planRun`; with `as-requested`, the same walk moves to `openProgress`) | 2,256-2,325 ms |
| tasks (`execParallel`) | 57-60 ms |
| agent: 181,764 `Observe Verdict` | 1,090-1,121 ms inside the agent |
| agent: 1,256 `GetResult` | 4 ms |

The walk is the run's first visit of the memoised no-build verdict (`getChangeReasons |>
memoizeRec`) over every target; 4.0 did the same walk in `openProgress`, single-threaded too.
What changed is the cost per dependency:

- 4.0: `File.exists` and `getLastWriteTime` on the calling thread (two stats).
- 5.0: `observeSince ... |> Async.RunSynchronously`: one mailbox round trip to the database agent
  per dependency. The agent handles the 4,433 first observations with a stat, and the other
  177,000 by the "already observed in this run" path, which still does `Map.tryFind` on
  `Map<File, _>` (ordinal compares of long paths) and a `File.exists`. ~6 µs in the agent plus
  ~6.5 µs of round trip (post, reply channel, thread hop, `RunSynchronously`) per dependency, all
  serial.

## Fix options

1. **A per-run view of observed files, read off the agent (recommended).** The agent keeps a
   `ConcurrentDictionary<File, FileState option>` of the files observed in this run, written only
   by the agent: set when it replies a state for an observed file and on every `store` of one,
   removed when the file goes to `HashNeeded` (so a reader then waits in the mailbox as today),
   cleared at `NewRun`. A `Verdict` observation that finds the file there answers on the calling
   thread: `File.exists` (kept, as the agent does now), then the same `classify since`. The verdict
   closure in `getChangeReasons` checks it synchronously before `Async.RunSynchronously`.
   Prototype measured: 2.36 s -> 0.47-0.59 s (4.0.0: 0.56-0.64 s); first observations, hashing,
   `Recording`/`Output`/`PreHash` unchanged. Budget ~30 lines (`Database.fs` ~18,
   `DependencyAnalysis.fs` ~10), plus the way the view reaches the caller (a field of the agent's
   handle or the `NewRun` reply; not a static, as the prototype had). No behaviour change, no
   database change. Ordering: a reader sees the agent's latest state for the file, which is what a
   mailbox reply queued at that moment would have said.
2. **Option 1, and existence cached per run as well** (no `File.exists` on a repeat verdict
   observation). The remaining ~0.4 s of the walk is that stat (177,000 x ~2.5 µs). Behaviour
   change: a file deleted during the run after its first verdict observation would still count as
   present for later verdicts in the same run. +2 lines. Not recommended without a reason.
3. **Batch observation per target** (`ObserveMany`: one round trip per target, not per
   dependency). Removes the round-trip half only; the agent's serial ~1.1 s stays. Expected
   ~1.2 s. ~30 lines, a new message. Worse than 1.

4. **Batched verdict query per target (prototyped, the owner's question).** One message
   `ObserveVerdicts (files, since, reply)` carries every file dependency of a target; the agent
   runs the usual verdict observation for each and replies at once. A file being hashed by
   someone else replies "ask alone" (the caller falls back to `Observe` and waits); a first
   observation that needs a hash comes back as `HashNeeded` and the caller hashes and `Settle`s it
   as today. Measured: 0.66-0.93 s (1,188 messages instead of 181,764). ~25 lines, a new message,
   no behaviour or format change. A batch over a whole group is not practical: the walk finds a
   target's dependees as it goes (memoised recursion), so the per-target batch is the natural
   unit, and it already removes almost all messages. Option 3 above is this same idea.

   Against option 1: both remove the round trips; the batch keeps the per-file work (map lookup,
   stat) serial in the agent, option 1 does it on the caller (still one thread in the walk). Option
   1 is ~0.2-0.3 s faster here and keeps the message set as it is; the batch keeps every read in
   the agent (no state shared outside it). Together they gain little: after either, the cost left
   is the `File.exists` per dependency. One is enough.

## Decisions

- 2026-11-19: option 4 (the batched verdict query) was recorded here as chosen; on 2026-11-20 the
  owner said it is **not accepted**. The design (design.md, d6522fe) stays as a candidate only.
- 2026-11-20, the owner: **get rid of `Async.RunSynchronously` on the dependency path** (async all
  the way, no blocking waits on the agent).
- Open, with the owner in this session: the direction (A: per-target batch for reads and
  `Recording` writes; B: reads bypass the agent through a table only the agent writes; C: no agent
  for the file table, a concurrent dictionary and a lazy hash per file, the agent only persists);
  existence re-checked per dependency or one stat per file per run; the full-build regression
  (cold-hot.md).

## Also: recursive recipes and FS0041

Verified on preview.2: `let rec f n = recipe { if n < 3 then do! f (n + 1) }` fails with FS0041
("A unique overload for method 'Bind' could not be determined"); 4.0.0 compiles it; `let rec f n :
Recipe<ExecContext, unit> = ...` compiles on preview.2. A release-notes line goes with the fix.

## Later

- `getDurationDeps` / the progress `reportProgress` re-walk (`execMany` on every refresh) is not a
  cost in a no-change run (tasks: ~60 ms); not touched.
- The consumer's extra ~2 s outside "Build completed": ask for a split (fsi compile vs load).
