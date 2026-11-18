# Code review of the phase 1 implementation (2026-11-18)

Reviewed `git diff dev...8f7f5c1` against [decision.md](decision.md) (A1-A9). `dotnet test src/tests -c
Release`: 1782 passed, 11 skipped, 0 failed. Edge cases checked with a copy of `collapseWith` and a
script against the built `Xake.dll` on macOS. Verdict: ready after the fixes below.

**Found sound:** the verdict (`changed > epoch`, transitive rule, phony by `Epoch`); observation
(once per run, A1 `recorded`, A2 newest epoch, results with the task's `RunEpoch`); `InMemory` for
`--dump`/`--dryrun`; `noopDb`; multi-output re-observation; `DirRec`/`PathRec` before use in one
`Write`; pickling rollback; write failure stopping appends; torn-tail cut; compaction; the A7
conversion; the lock (A5); `File`/`Target` equality and comparison; `File.display` on a segment
boundary.

## Findings

| # | Kind | Where | Finding | Fix |
|---|---|---|---|---|
| R1 | bug | `Path.fs:257`, `Database.fs:159` | Windows `collapse` keeps `//` after a drive (the UNC branch fires at index 1 without `segs.[0] = ""`): `C://a`, `C:\\a\b` -> `C://a/b`, `a//b`. A project at a drive root (`C:\`, `rootName = "C:/"`) loads every file as `C://x`: no `FileRec` matches, full rebuild every run | guard the UNC case with `segs.[0] = ""`; join without an extra `/` when the root ends with `/`; add these cases to `WindowsCases` |
| R2 | risk | `PatternScan.fs:499-500, 549, 568` | on a case-insensitive volume literal segments are probed and come back in the mask's spelling: macOS `casesensitive` + `Src/*.cs` finds `src/a.cs` as `Src/a.cs`; Windows default the same. Breaks "files keep their spelling on disk"; two `FileRec`s and spelling warnings | ignoring case: list instead of probing (or take the disk name after a hit); `casesensitive`: check a hit's spelling ordinally |
| R3 | risk | `ScriptRunner.fs:14`, `ExecCore.fs:488` | `ProjectRoot` is normalised only for scripts; `XakeEngine.Start(EngineOptions)` keeps the root as given; with ordinal rules a lowercase drive cwd (`c:\proj`) never matches (unverified, Windows only) | normalise in `createContextCore` |
| R4 | risk | `Database.fs:521-523` | a failed rewrite (upgrade, compaction) still deletes the `.bak`: the only complete copy is lost (needless rebuilds) | keep the `.bak` when the writer stopped |
| R5 | spec | `ExecCore.fs:213-218`, A3 | a delegated cache hit keeps an old epoch, so after its sources changed its dependents rerun on every hit; same as 4.0 | Later |
| R6 | spec gap | `Path.fs:335`, `PatternMatch.fs:221, 248` | the public `Path.matches` keeps 4.0's OS-dependent case mode: a third case rule; on macOS `Path.matches "*.CS"` is false while `!! "*.CS"` finds `a.cs` | owner decision |
| R7 | minor | `Path.fs:259` | `collapse` keeps `..` directly above a root (`/../a`, `C:/../a`): two `File`s for one path | drop `..` when the top segment is a root |

## Test gaps

- A2: overlapping engine runs (`Demand` of different names at once); separate epochs per
  `runTargets` group.
- A1 end to end: a generated file without a rule rewritten in a run, then a no-change run rebuilds
  nothing (only the agent-level unit test exists).
- A4: a failed write stops appends until close (only the pickling failure is tested).
- A5: `--resetdb` under the lock; a failed open releases the lock; a reader during a compaction.
- A6: `File.make` with `\foo`, `C:foo`, a lowercase drive; `C://a` in the table.
- A7: the Linux re-scan of an upgraded `GetFiles`.
- A8: `--dump` appends nothing; multi-output re-observation.
- Weak: `EpochTests.fs:165` ("a delegated result never stores a foreign epoch") passes also with the
  run's epoch; assert the epoch after the second run equals the one after the first.

## Simplifications (behaviour unchanged, about 45-50 lines)

a. `Persist.read`: one loop with a per-version step (8). b. `PathTable`: tuple key instead of
`dirKey` concatenation (a latent collision above root 9) and a one-expression join (6).
c. `Writer` logs its own stop; drop the `stopped` ref and `onStop` (6). d. Fold `impl.observe` and
`impl.stat` into the agent's `observe` (4). e. One shared fold and `lastEpoch` for `readDatabase`
and `openDatabaseFile` (3). f. `Rec = DirRec | PathRec | Data of DbRecord` (4). g. `getDepState`:
drop the `File.exists` pre-checks, `observe` already returns None for a missing file (4; one stat
less per dependency). h. One observe helper instead of `DependencyAnalysis.observe` and
`ExecCore.observeFile` (3). i. `checkSpelling` stores the `File` (2). j. Dead code:
`DependencyAnalysis.TimeCompareToleranceMs`, `PatternScan.scan` (tests only), the `regexText` case
flag (6).

Where the overage came from (not removable without changing the spec): legacy pickler and
conversion about 85 lines (moves to `LegacyRead`, B3), rollback and pending ids about 15 (go with
B1), lock and reset about 25, `ObserveMode`/`recorded`/`NewRun`/`InMemory`/A3 observation about 40.
