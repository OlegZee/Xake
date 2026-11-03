# G16 "parallel build hangs": report (2026-10-13)

Worktree `.claude/worktrees/pr/pool-hang`, branch `pr/pool-hang` (from `origin/dev` 0139682).
Result: **not reproduced**. No engine change. Repro tests committed (5176f6e), not pushed.

## 1. What the run-3 log actually shows (correction to the G16 premise)

The two `Strings.resources` targets did **not** hang: task 132 (globex, requested by task 58) and
task 133 (acme, requested by task 28) both logged `Completed` and `Task done` at 12:55:40.
The tasks that never finished are **29** (`out/acme/Debug/net472/Export.Sheet`) and
**59** (`out/globex/Release/net472/Export.Sheet`), plus their parents (`build::acme::Debug`,
`build::globex::Release`, `build`). Each is the net472 sibling (same brand, same cfg, same lock
file) of the task that first requested the `.resources`. The other four users of the same
`.resources` (88, 89, 118, 119) joined it and finished at 12:55:41-44.

29 and 59 logged `compiling '...'` (src/dotnet/CompilerRunner.fs:84) at 12:54:19 and never
`[shell] starting`, so they stopped inside `CompilerRunner.run` before the compiler process
(lines 84-165): three `needFiles` (compiler, resx list, `plan.Inputs` incl. the `.resources`),
writes of `Generated`, two `verify` hash passes. The compiler (csc) was never started for them.

Engine state at the hang, from the log: 133 `Task queued`, 133 `Started`, 128 `Completed`;
the 5 unfinished tasks are all waiting inside a `need`. So the pool mailbox was not stuck
waiting for a slot (WorkerPool.fs:44 runs only after `Task queued`, and every queued task started),
and no task held a slot.

## 2. Reproduction matrix (src/tests/PoolHangTests.fs, 60 s timeout per run)

| shape | pool 1 | pool 2 | pool 4 | pool 18 |
|---|---|---|---|---|
| 4 graphs x 8 leaves, every leaf needs `shared.txt` at once | pass | pass | pass | pass |
| two parents need `shared.txt` simultaneously | pass | pass | pass | pass |
| two parents, one after another (2nd joins a finished task) | pass | pass | pass | pass |
| shared target deep (leaf -> mid -> shared) | pass | pass | pass | pass |
| the trial project shape: 2 brands x 2 cfg x 2 fwk x chain of 8 dlls, `obj/resx/(b:*)/(name:*)/**/*.resources` | pass | pass | pass | pass |
| the trial project load (Explicit): 240 dlls with DAG refs, 200 plain inputs each, shared `.resources` every 5th project, thread-blocking work, 5 runs | | | pass | pass (ProcessorCount) |
| joins racing the end of the shared task (Explicit): 8 parents, random 0-5 ms delays, 150 runs | pass | pass | pass | |

## 3. Why the engine join cannot deadlock (reading, file:line)

- `need` -> `execNeed` (ExecCore.fs:197) releases the caller's slot before posting
  `Run` (WorkerPool.fs:95-104 `withYieldedSlot`) and reacquires it after; so a task waiting for a
  dependency never holds a slot, and the classic pool-exhaustion deadlock does not apply.
- A request for a target already running or finished gets `Async.AwaitTask task`
  (WorkerPool.fs:36-40); the task releases its slot and posts `Done` in `finally` (:51-53).
- Slot accounting is balanced on every path (task acquire/release, yield/reacquire,
  `withResource`); a recipe calling `need` without a slot only over-releases (more parallelism).

## 4. Hypotheses that remain (in order)

1. **A dead agent.** An exception inside a `MailboxProcessor` loop kills it silently (nobody
   subscribes `.Error`), and every later `PostAndAsyncReply`/`PostAndReply` waits forever at 0 % CPU.
   Candidates: the pool loop (WorkerPool.fs:29-68), the database (Database.fs:201 `pickle`; its
   `GetResult` is read synchronously under the memoize lock, CommonLib.fs:17, DependencyAnalysis.fs:83).
   No throwing statement found, and 88-119 got their replies after 12:55:40, which argues against
   the pool agent.
2. **Something blocking in `CompilerRunner.run` lines 84-165 for the two siblings only.** Nothing
   there takes a lock (checked: Fingerprints file lock, Restore/Project `Resource`s, `Lazy` SDK
   probe are all outside this range or not in the build path). The siblings share the lock file and
   `Generated` paths with the requester; a write/read race there would throw, not hang.
3. Thread-pool starvation (many threads blocked in `proc.WaitForExit`, `PostAndReply`,
   `RunSynchronously`): slows, but the pool keeps injecting threads, so not a 10-minute stop.

Not supported by the log: a hang in csc (never started), `Locks.compileAs` restore locking
(runs before `compiling`), `Fingerprints.record` (#51; only `warn` runs in `build`).

**Next step:** on the next hang, run `dotnet-stack report -p <pid>` (or `dotnet-dump collect`)
before killing it; it decides between 1 and 2 in one look. Cheap diagnostics worth a separate PR:
log `.Error` of the pool and database agents; log at Debug after each `needFiles` in
`CompilerRunner.run`.

## 5. Side finding for G17 (measured, not fixed)

`locateRule` (ExecCore.fs:43) calls `Path.matchGroups` per rule per target, which builds a new
`Regex` with `RegexOptions.Compiled` every time (Path.fs:186 via :275). Construct+first match:
**0.67 ms compiled vs 0.004 ms interpreted** (1000 patterns, `dotnet fsi`). Every input of every
`needFiles`, and every hashed file in `verify` (`hasRule`, CompilerRunner.fs:143), goes through
it for every rule: about 1000 inputs x ~6 rules x 2 passes per compile is ~8 s of CPU per dll.
This matches the 84 s between `compiling` and the `.resources` request of the Excel.Page
compiles (12:54:15 -> 12:55:39) and the large WaitTimes, and is the first suspect for
"parallel slower than sequential". Fix: cache the regex per (pattern, root) or drop `Compiled`.

## 6. Tests

`dotnet build src/core --no-incremental`: 0 Warning(s), 0 Error(s) (both TFMs).
`dotnet test src/tests` on 5176f6e (the 20 matrix cases included, the 5 Explicit stress cases not):

    Passed!  - Failed:     0, Passed:   344, Skipped:     1, Total:   345, Duration: 1 m 16 s - tests.dll (net8.0)

Filtered runs: matrix `Passed: 20`; the trial project load `Passed: 2` (58 s); racing joins `Passed: 3` (13 s).
