# Night report 2026-10-13: the build.fsc.fsx rework

Charts (bar chart, both Gantt charts, concurrency areas) are in `night-report-2026-10-13.html`. Nothing is pushed or merged.

## 1. Summary

ActiveReports clean build, one config: 33.4 s -> 6.9 s. Four configs: 224 s -> 19.7 s. No-op: 7.1 s -> 0.9 s. Fresh clone: 141 s -> 26.2 s.
Lock-route script 179 -> 124 lines (92 + 32 pack) with zero workarounds. Direct route 57 -> 8.5 s. msbuild reference: 24 s for one config, 80 s for four sequential.
The gain comes mainly from the regex caches (C1, C2a) and one restore per build (B2.1). Gaps G1-G9 and G12-G15 are closed for AR.

## 2. Wall time (server off; 18 cores)

| run | before | after | msbuild |
|---|---|---|---|
| clean, 1 config (mescius Release) | 33.4 s | 6.9 s | 24.2 s (27.9 s round 1) |
| clean, 4 configs | 224 s (141 s with `<<<`) | 19.7 s | 80 s sequential |
| no-op, 1 config | 7.1 s | 0.9 s | 2.1-2.5 s |
| no-op, 4 configs | 33 s | 1.3 s | not measured |
| fresh clone, 4 configs | 141 s | 26.2 s | not measured |
| direct route, clean, 1 config | 57.1 s | 8.5 s | not measured |

Server on (after): 6.0 s, 11.1 s (four configs), direct 5.6 s.

## 3. Gantt charts

See the HTML. Sequential clean (31 targets, `seq-clean.csv`): wall 6.2 s, critical path VBFunctionLib 0.3 -> ExpressionInfo 0.4 -> Rdl 0.9 -> Rendering 3.1-3.3 -> Excel 1.2 s.
Parallel clean (125 rows, `parallel-clean.csv`): wall 18.9 s; the 8 Rendering compiles own 10.7-12.0 s each (3.1 s alone), CPU contention (util 14.4 of 18).

## 4. Concurrency

| run | peak | mean | seconds with no active target |
|---|---|---|---|
| sequential, before | 8 | 1.02 | not measured |
| sequential, after | 20 | 6.9 | 1 |
| parallel, before | 11 | 2.30 | 116 of 217 |
| parallel, after | 36 | 16.1 | 1 |

## 5. What changed

| round | gaps | branch | tests | critic |
|---|---|---|---|---|
| A1 | G1-G4, G13 | `pr/import-fidelity` | 211 (hermetic) | SHIP; 3 fixes (F1 _ folder, F2 backslash args, F3 ignored metadata), all applied (F2 in B1, F1/F3 in D1) |
| B1 | G5-G7, G12, G14 | `pr/lock-fidelity` | 218 hermetic + 324 core | SHIP on all items, nits (critic-b) |
| B2.1 | G15 (+ Described.closure) | `pr/batch-restore` | 222 hermetic | SHIP; one fix on the needAll prefetch, nits (critic-b) |
| D1 | G10, G11 (test only) + A1 critic fixes | `pr/lock-roots` | 226 hermetic | No separate critic file; critic-b fix and nits applied per the session table; open question 1 |
| C1 | G8 | `pr/fileset-perf` | 329 core (+ 206 hermetic) | SHIP (critic-core) |
| C2a | G9 part; agent failure guards | `pr/rule-match-cache` | 332 core after merge of C1 (327 reported by the author) | SHIP; db agent guard SHIP WITH FIX F1 (atomic record write), applied by the author in 006243f (record pickled in memory, written in one call; test reopens the db); nits N2-N4, N6 applied |
| G16 | G16 hang | `pr/pool-hang` | 344 core (repro tests only) | Not reproduced; no critic file |

## 6. Still open

- HERMETIC=on on generated C# locks (D1 question 1): the lock names the SDK csc.dll outside the roots and not covered by a prerequisite; AR pins latestFeature and uses the SDK csc, so it is refused.
- Pack as PR 8: still 32 lines of script; Locks.restoreAll takes one config.
- G10 not exercised with sibling repos: the AR worktrees sit inside the experiment folder.
- Doc bug: the Described.closure doc comment and csc-syntax.md name the result refs, which clashes with the csc { ref } custom operation (FS3095); use deps.
- One-time lock rewrite on the next update-locks: Directory = project folder, the nowarn line, framework assemblies (net462 System.Windows.Forms.dll in the self-build locks; WindowsBase on net472 AR).
- G16 hang not reproduced: 20 cases + 2 stress passes; the agent-failure guards (pool failure fails the build, db store failure logged) were added instead; next hang: dotnet-stack report -p <pid>.
- msbuild concurrent is not a valid baseline: the four Rebuilds share bin/ and obj/ and fail (CS0006, MSB3030); the fair number is the sequential 80 s.
- Server-on CPU is invisible to cputime.pl; a VBCSCompiler started by these runs stays until its idle timeout.

## 7. Branch and merge order

1. Core: `pr/fileset-perf` -> `pr/rule-match-cache`.
2. Hermetic stack: `pr/import-fidelity` -> `pr/lock-fidelity` -> `pr/batch-restore` -> `pr/lock-roots`.
3. `pr/pool-hang` holds repro tests only; optional.

Nothing pushed, nothing merged. The integration worktree `night-integration` (detached 74bf4d9) is disposable.
Then release Xake 3.7.1 and Xake.Hermetic.Dotnet 0.3.1 so that `build.fsc.fsx` and the AR scripts can leave the `-night` feed.
