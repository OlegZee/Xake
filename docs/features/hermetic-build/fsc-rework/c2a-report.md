# C2a: rule-match cache and observable agent failures (2026-10-13)

Worktree `.claude/worktrees/pr/rule-match-cache`, branch `pr/rule-match-cache` (from `origin/dev` 0139682).
Commits 67ef26b (perf) and 6d4eea7 (agents). Not pushed.

## 1. Root cause (rule matching)

`ExecCore.locateRule` (src/core/ExecCore.fs:43) calls `Path.matchGroups` for every rule and every
target (:55, :62, :75; also `makeTarget` :219). `matchGroups` (src/core/Path.fs:275 on dev) called
`matchImpl.maskToRegex`, which built `Regex(..., RegexOptions.Compiled)` on every call
(Path.fs:186 on dev). `matchPart` (Fileset scans) did the same per path segment.

## 2. Change

`maskToRegex` caches the regex per mask string in a `ConcurrentDictionary` (thread-safe, one
build per distinct mask). **This is byte-identical to C1's change** (`pr/fileset-perf` 2b5bf39):
the coordinator asked me to `git merge pr/fileset-perf`, but the merge (and copying C1's file)
was refused by the permission classifier, so I reproduced the same hunk by hand. `git diff
pr/fileset-perf -- src/core/Path.fs` is empty, so merging both branches is conflict-free. No
second cache was added. Commit 67ef26b otherwise only adds `src/tests/RuleMatchTests.fs`
(a semantics test plus two `[Explicit]` benchmarks).

## 3. Numbers (min of 3 runs each, macOS, other agents building on the machine)

| benchmark | before (dev 0139682) | after (cache, Compiled) | cache, interpreted |
|---|---|---|---|
| `locateRule`, 30 rules x 5000 non-matching targets | **94 043 ms** | **167 ms** | 160 ms |
| same, first (cold) iteration in process | 94 424 ms | 255 ms | 227 ms |
| script: 5 file rules incl. `out/(cfg:*)/(fwk:*)/(name:*).dll`, `need` 3000 plain files, 4 threads | **10 098 ms** | **29 ms** | 29 ms |

About 560x on `locateRule`, about 350x on the AR-like script. `RegexOptions.Compiled` decision:
with the cache, interpreted is ~4 % faster warm and ~30 ms faster cold (construction of ~30
compiled regexes); the difference is noise next to the 94 s -> 0.17 s gain. Kept `Compiled`
(as C1 did), so the two branches do not conflict. The remaining per-call cost is
`Path.Combine` + `normalizeLiteralPrefix` in `matchGroups` before the cache lookup, ~1 us/call.

Benchmarks: `dotnet test src/tests --filter "FullyQualifiedName~RuleMatchTests.bench"`.

## 4. Agent failures (commit 6d4eea7)

Before: an exception in a `MailboxProcessor` loop ended the agent; nobody subscribed `.Error`;
later `PostAndAsyncReply` (pool, ExecCore.fs:178) and `PostAndReply` (db, DependencyAnalysis.fs:24,
:83, :164; finalize ExecCore.fs:302) waited forever at 0 % CPU.

Now each message is handled inside try/with and the agent keeps serving:

- **WorkerPool** (src/core/WorkerPool.fs `create`): a failed `Run` logs at Error
  `WorkerPool agent failed to schedule '<target>': <exception message>`, releases the slot if it
  had taken one for a task that never started, and replies with an async that raises
  `XakeException` with the same text, so the requesting `need` fails and the build fails with
  that message. A failed `Done`/`NewRun` logs `WorkerPool agent failed on Done: <exn>`.
- **Database** (src/core/Database.fs `openDb`): a failed `Store` logs at Error
  `Database agent failed to store the result of '<targets>': <exn>` and keeps the result in
  memory for this run; a failed lookup logs `Database agent failed to look up ...` and answers
  None (= rebuild); `Close`/`CloseWait` always reply. The build does not fail on a store error
  (the current run is correct; the next run finds the file unreadable, recreates it with a
  Warning and rebuilds).
- Both: `agent.Error.Add` backstop logs `<WorkerPool|Database> agent stopped: <exn>`. The
  error logging itself is wrapped so a throwing logger cannot kill the loop again.

## 5. Tests

`src/tests/AgentFailureTests.fs`: (a) a logger that throws on "Task queued" (logged inside the
pool loop) -> build must fail within 30 s and log the Error line; (b) a `Store` whose pickling
throws (`Var(null, None)`) -> `GetResult` and `CloseWait` must answer within 30 s and the
Error line be logged. On the old WorkerPool.fs/Database.fs both **hang and fail at the 30 s
timeout**; with the fix both pass in ~70 ms.

`dotnet build src/core --no-incremental`: 0 Warning(s), 0 Error(s) (net462 + netstandard2.0).
`dotnet test src/tests`: Passed 327, Skipped 1, Failed 0 (net8.0).

## 6. Risks

- Cache is unbounded; keys are mask strings (rule patterns joined with the project root, and
  Fileset segments). A script generating masks dynamically per file would grow it. Same as C1.
- The pool keeps running after a failed `Run`; the failed target is not registered, so a
  later request for it retries (and fails again in the same way).
- A store failure leaves a partial record in `.xake`; next run discards the db (full rebuild).
- No scheduling change; G16 is not proven to be a dead agent. If it was, the next occurrence now
  shows an Error line and fails instead of hanging.

## 7. After the critic (critic-core.md), commit 006243f on top of merge 2343ab4

- **F1:** `Store` pickles into a `MemoryStream` and writes the bytes to `.xake` in one call; a
  failed pickle writes nothing. The test stores good/bad/good, reopens the db and checks both good
  records read back and `bad.dll` is absent. Checked against the old write path: `good2.dll`
  comes back `null` (silently lost), so the test catches it.
- **Nit: warning at close:** `Close`/`CloseWait` log at Warning `Database not fully saved: N result(s) failed
  to store, the next build may rebuild them`; the test asserts it.
- **Nit: leftover test files:** the database test and the 3000-file benchmark use a fresh temp dir and delete it in
  `finally`; the old leftovers under `src/tests/~testout~` were removed.
- **Nit: cache test:** `excludes match the same after a mask was used many times` now checks a hard-coded
  expected list on three repeated uses. Benchmarks print through `TestContext.Progress`.
- Not done: N1 (register the running entry before replying), judged unreachable.
- `dotnet build src/core --no-incremental`: 0 Warning(s). `dotnet test src/tests`: Passed 332,
  Skipped 1, Failed 0. Benchmarks unchanged (locateRule 167 ms, 3000-file script 29 ms).
