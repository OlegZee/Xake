# Critic: core changes on `pr/rule-match-cache` (incl. `pr/fileset-perf`)

Scope: `git diff origin/dev...HEAD` (Path.fs, WorkerPool.fs, Database.fs, 3 test files). Read-only review.

## Verified

- `dotnet build src/core --no-incremental`: `Build succeeded. 0 Warning(s) 0 Error(s)` (net462 + netstandard2.0).
- `dotnet test src/tests`: `Passed! - Failed: 0, Passed: 332, Skipped: 1, Total: 333, Duration: 26 s - tests.dll (net8.0)`.
  332 = the reported 327 plus the 5 new fileset tests (merge of both branches). Explicit benchmarks are not run (not even counted as skipped).

## Verdicts

| Change | Verdict | Reason |
|---|---|---|
| Regex cache, `Path.fs` `maskToRegex` | SHIP | See below: key complete, thread safe, growth bounded by distinct masks a script writes. 94 s -> 0.17 s on `locateRule`, 1.25 s -> 7 ms per fileset expansion. |
| WorkerPool agent guard | SHIP | Previously any exception in the loop killed the agent and every later `PostAndAsyncReply` hung. Now the failing requester gets a `XakeException`, slot released, agent keeps serving. No path that stopped correctly before now continues. |
| Database agent guard | SHIP WITH FIX | Strictly better than the old hang, but a failed `Store` leaves a partial record in `.xake` and later records are appended after it. See fix F1. |
| Fileset edge tests (5) | SHIP | Pin semantics on a real temp tree (dot folders, `dir/**` vs `dir/**/*`, include order, OS case rule); no timing asserts. |
| `locateRule` semantic test | SHIP | Pins first-match rule and the three captured groups. |
| AgentFailureTests (2) | SHIP | Fault injection, 30 s timeout only as a hang guard (pass ~70 ms): not flaky. |
| Benchmarks (3, `[Explicit]`) | SHIP | Excluded from the normal run (confirmed by the count). |

### Regex cache details (point 1)
- Key = mask string. Everything else in the regex text is either derived from that string (slash normalization, named groups) or a process constant: `^...$` anchoring always, `Compiled` always, `IgnoreCase` from `Env.isUnix`. Complete.
- `matchGroups` keys on `Path.Combine(rootPath, pattern)` after `normalizeLiteralPrefix`, so the root is in the key. Captures unchanged: same regex text, same `GetGroupNames()`.
- `ConcurrentDictionary.GetOrAdd` may run the factory twice under a race; harmless (one instance wins). `Regex.Match` is thread safe. `ConcurrentDictionary` exists in net462 and netstandard2.0. No `Lazy` used.
- Growth: literal segments never reach the cache (`Directory`/`FileName` compared with `eq`); only wildcard segments and rule patterns (x root). A script computing masks per item in a loop grows it by one regex per distinct mask, but before the change each such mask was compiled on every match anyway, so cost only went down; memory is per process and `dotnet fsi` exits. Acceptable without a bound.
- `Compiled`: with the cache the trade-off is ~30 ms cold for ~30 masks vs ~4 % warm; noise. Keeping it is fine.
- Public API: `maskToRegex` is in `module internal matchImpl`, `buildRegex` private. No change.

### Agent details (point 2)
- Ordering: messages still handled one at a time; `return! loop next` sits outside the `try`, so no stack growth.
- Every waiter answered: each `Run` carries its own reply channel; concurrent requests for a target whose scheduling failed are separate messages, each retried and each answered (with the same failure if deterministic).
- `acquired` covers the window between `WaitAsync` and `StartAsTask`; after that the task's `finally` releases. No double release.
- Theoretical gap (nit N1): an exception after `chnl.Reply result` but before the state is returned (only `Map.add`) would reply twice (swallowed) and drop the running entry, so a later request could start the task a second time. Not reachable in practice.
- `.Error` handlers fire only if something escapes the guards (all logging is wrapped); no double logging.
- Store failure: logged at Error once, build result unaffected, no summary at end of build (nit N2).

## Fixes required

- **F1 (Database.fs `Store`)**: pickle into a `MemoryStream` first, then write its bytes to `dbwriter` in one call. A failed pickle then writes nothing, the file stays well-formed, and the comment's claim ("next run finds the file unreadable and rebuilds") becomes unnecessary. Today the reader (`openDatabaseFile`) discards the whole db only if the misaligned bytes throw; if they happen to parse, the next run reads wrong records silently. Extend `database agent survives a failed store` to reopen the db and assert the good records read back.

## Nits

- N1: see above; could register the running entry before replying. Optional.
- N2: a failed `Store` could be remembered and repeated as a Warning at `Close` ("database not fully saved, next build may rebuild"). Optional.
- N3: `acquired := true` / `ref` cells: fine (0 warnings), a `let mutable` captured is not possible in async, so keep.
- N4: `database agent survives a failed store` leaves `.xake-agentfail` in the test root; `bench script needing 3000 files` leaves `inputs/` with 3000 files. Clean up in `finally`.
- N5: `excludes match the same after a mask was used many times` compares two listings of the same fileset; it does not prove anything about the cache. Compare against a hard-coded expected list instead, or drop it.
- N6: RuleMatchTests benchmarks use `printfn`, FilesetTests use `TestContext.Progress`; pick one.
