# ActiveReports: where the time goes in a Xake run (plan item C0)

Date 2026-10-13. Experiment folder `/Users/olegz/Projects-work/ar/xake-ar/`. Repos untouched. Machine: Apple M5 Pro,
18 cores, quiet (no other Xake run; a second session was not running, checked with `ps`). Measured with
`tools/cputime.pl` (wall, CPU summed over the process group, util = CPU/wall). All runs `-d CSC_SERVER=off`,
SDK 8.0.425, `build.fsx` unchanged (line 82 already `<==`).

## 0. How the database was read

- `tools/xakestats.fsx <db-copy> [build.log]`. The `Database` module's `Persist` and `impl` are `private`, so the
  file cannot be opened through a public function. The script calls `Xake.Storage+impl.openDatabaseFile` by
  reflection (in `Database.fs`, the function reads the file and returns `Database ref * writer`). The result
  (`Status : Map<Target, BuildResult>`, public) is read directly, so the Steps, Depends and Built fields are
  real data, not parsed.
- The reader is not read-only (it may restore a `.bak` and writes a header), so the script runs on a **copy**
  of `.xake` in `/tmp/xakestats/`. The original was never opened by the script.
- `build.log` mode parses `Started X as task N`, `N> compiling`, `Completed X in N ms (wait M ms)`.
- Caveats found while doing it, which change how the numbers read:
  - `WaitTime` comes **before** `OwnTime` in a step. Checked against the log: `Completed` time = Start + Wait + Own
    (Rendering 1:16:58 + 11.3 s + 4.6 s = 1:17:14). The script therefore draws each step as
    [Start+Wait, Start+Wait+Own]; the first version drew [Start, Start+Own] and was wrong for every waiting target.
  - Steps of a target that is **not rebuilt** stay in the db from its last build. A no-op does not clear them, so
    the no-op's per-target table shows the clean run's numbers (Built times unchanged). The no-op's own work is
    only visible as `Skipped ... (up to date)` lines in `build.log`.
  - `OwnTime` does not contain the whole csc process time (section 4).

## 1. Totals (mescius Release, 30 dlls + 4 resx + 1 phony in the db)

| run | wall (s) | CPU (s) | util | sum OwnTime (s) | sum WaitTime (s) | wall first activity..last end (s) | avg concurrency (sumOwn/wall) | histogram peak (targets active in one second) |
|---|---|---|---|---|---|---|---|---|
| sequential clean `build::mescius::Release` | 27.6 | 96.6 | 3.50 | 26.8 | 461.0 | 26.4 | 1.02 | 8 (after the fix; first version counted 31 at t=0) |
| no-op of the same command | 6.4 | 6.8 | 1.06 | (stale, see 0) | (stale) | (stale) | n/a | n/a |
| parallel `build` (all four graphs, 120 dlls) | **217.6** | 584.4 | 2.69 | 497.8 | 13756.6 | 216.2 | 2.30 | **11** at t=166 s; 116 of 217 seconds with 0 active targets |

Dump/dryrun of the sequential graph (`--dryrun`, `--dump`, on the db of the clean run):

| item | value |
|---|---|
| `--dryrun`: "Build will be completed (estimate)" | **9.7 s** (measured wall 27.6 s; estimate is 2.8x too low) |
| `--dryrun`: "Total tasks duration (estimate)" | 26.8 s (equals sum OwnTime of the clean run) |
| `--dryrun`: "Parallelist degree" | 2.76 |
| `--dump` size | 7264 lines, 1.13 MB |
| `--dump` distinct nodes | 34 Artifact nodes, 3054 File nodes, 31 GetFiles; 290 Artifact edges (the tree repeats shared subgraphs) |
| db after dryrun/dump | unchanged (cmp equal) |

## 2. Sequential clean, no-op (mescius Release)

Dependency kinds (db, all 35 targets of the sequential graph; identical in clean and no-op):

| kind | count | note |
|---|---|---|
| FileDep | 6448 | 33 files for a typical dll; 3054 distinct files |
| GetFiles | 31 | 30 dlls + 1 (the graph's own description mask) |
| Var | 180 | |
| other (ArtifactDep, EnvVar, AlwaysRerun) | 291 | |

GetFiles masks: 31 dependencies, 16 distinct. Of the 31, **30 carry the `!**/.*/**/*` exclude**
(`Excludes (PathMask [Recurse; DirectoryMask ".*"; Recurse; FileMask "*"])`). Each dll is one GetFiles mask over
its own folder (`Recurse; FileMask "*.cs"` plus excludes `bin`, `obj`, `.*`).

No-op: 6.4 s wall, 6.8 s CPU, util 1.06. The log shows `build::mescius::Release` `AlwaysRerun`, then 30 dll tasks
`Skipped ... (up to date)` and the resx tasks skipped. The engine re-expands the 31 GetFiles deps on every run
(the mask cost of ~1.4 s per Rendering mask from ar-experiment.md, G8, is part of the 6.4 s).

## 3. Parallel run (`build -d CSC_SERVER=off`, four graphs, 120 dlls)

- Completed: 133 Completed lines, `Build completed in 00:03:36.8`, exit 0, one run, no hang (the Monitor watched
  build.log for 3 min of silence; it had 40 s of silence at most).
- Log concurrency (from Started/Completed, second resolution): peak 20, mean ~3.
- db concurrency (per step, [Start+Wait, +Own]): peak **11**, avg **2.30** (= sumOwn / wall), 116 of 217 seconds
  with 0 active file targets.

Top file targets (120 dlls):

| target | own (s) | wait (s) |
|---|---|---|
| mescius Release net472 Export.Excel.Page | 34.4 | 181.9 |
| gccn Release netstandard2.0 Export.Excel.Page | 33.4 | 182.7 |
| gccn Release net472 Export.Excel.Page | 33.4 | 183.0 |
| gccn Debug net472 Export.Excel.Page | 32.8 | 180.2 |
| gccn Debug netstandard2.0 Export.Excel.Page | 32.6 | 180.2 |
| mescius Debug net472 Export.Excel.Page | 32.6 | 182.9 |
| mescius Release netstandard2.0 Export.Excel.Page | 31.2 | 181.5 |
| mescius Debug netstandard2.0 Export.Excel.Page | 29.8 | 182.7 |
| gccn Release net472 Rendering | 20.2 | 81.4 |
| mescius Debug net472 Rendering | 19.7 | 81.1 |

Totals over the 120 file targets: own 497.1 s, wait 12676.2 s (the wait of 120 targets sums to 105 s each on
average, i.e. most of them wait for their graph).

Compare with the sequential run: Excel own 3.1 s (netstandard2.0), Rendering 4.6 s. **Own work of the same target
is about 10x longer when four graphs run together** (34 s vs 3 s for Excel), and Excel waits ~182 s, i.e. nearly the
whole wall, before its work starts. The 8 Excel targets are the 8 slowest by far; the 8 Excel dlls are the last to
start. Which dependency they wait for was not traced (the `.resources` targets of Export.Excel.Page are the
candidates: they are the G16 hang targets; their db Built/Start do not give a reliable timeline, see 0).

## 4. Per-target preparation (sequential clean, mescius Release)

The log does not give the csc duration. Two proxies were tried (`logs/s-mescius-Release-clean.prep.txt`):
- `compiling 'X'` (csc start, task-number prefix `N>`) to `Completed` (second resolution): sum over 30 dlls = 74 s.
- db OwnTime sum over the same 30 dlls = 26.7 s.

So OwnTime is **smaller** than the csc process time for the same targets, and the 1-second resolution gives
±1 s per target. The difference (own minus csc) cannot be read per target from the existing data; the top-10
table of the plan would be noise (values -676 ms to +482 ms, all within rounding). Conclusion: OwnTime does not
include the whole csc process (the external process is probably counted as wait); a sub-step mark around the csc
call is needed to split prep from csc (C2).

## 5. Time sinks (numbers)

1. **Waiting on the graph, not work**: sequential clean sum WaitTime 461 s vs OwnTime 26.8 s (17x). Critical path
   of the sequential clean run (wall 27.6 s): VBFunctionLib (0.3 s) -> ExpressionInfo (0.5 s) -> DataEngine/Rdl
   (0.5-1.0 s) -> Rendering (own 4.6 s, wait 11.3 s) -> Export.Excel (own 3.1 s, wait 23.2 s). Excel's wait of
   23 s is longer than the chain before it: the remaining 7 s between Rendering's end and Excel's start is the
   `.resources` step and the mask/lock work of Excel, not yet split.
2. **Excel under parallel load**: own 3.1 s -> 33 s and wait 23 s -> 182 s in the four-graph run; this is the
   largest single cost of the parallel case (`own` 34 s for each of 8 Excel dlls, i.e. ~270 s of own time in 8
   targets, out of 497 s of own time in the whole run).
3. **Rendering**: own 4.6 s sequential (19.7-20.2 s parallel), the biggest single own time in the sequential
   graph (1308 C# files; the GetFiles mask over its tree is part of its wait/own, not separated).

## 6. Conclusions

- **Caps?** No. Peak 11 targets active in a second (of 18 cores, of 120 dlls); the sequential clean run peaks at 8.
  The engine does not cap the parallel count at a number near the cores; the four graphs reach 11 at most, and
  the average is 2.3. Concurrency is low because the graph is a chain (VBFunctionLib -> ... -> Rendering -> Export),
  and 116 of the 217 parallel seconds have no active file target.
- **Wait or work?** Mostly wait: 461 s of WaitTime vs 26.8 s of OwnTime (sequential); 13757 s vs 498 s in the
  parallel run. Work inside a target is slow only for the Excel dlls in parallel (own 3 s -> 33 s).
- **Serial inside themselves?** Not measurable with these marks. OwnTime does not contain the csc process (section
  4), so "work" here means only the F# recipe part.
- **What C2 must still profile** (the marks C0 cannot give): (a) the wait of the Excel dlls (~180 s in the parallel
  run) and whether it is the `.resources` chain (G16 hang candidate); (b) the csc process time inside Locks.compileAs
  vs prep (mask expansion, lock read, reference hashing), since OwnTime excludes csc; (c) the no-op's 6.4 s split
  between the 31 GetFiles expansions and the file stamps (the db keeps stale steps, so a no-op cannot be timed from
  the db: a Step mark in the dependency check is needed); (d) the dryrun estimate (9.7 s vs 27.6 s measured): it
  uses OwnTime only, so it ignores the wait; a fix in the estimate is cheap once (a) is known.

## 7. Files

- Script: `/Users/olegz/Projects-work/ar/xake-ar/tools/xakestats.fsx`
- Outputs: `/Users/olegz/Projects-work/ar/xake-ar/logs/s-mescius-Release-{clean,noop}.{stats.txt,build.log}`,
  `logs/p-mescius-gccn-clean.{stats.txt,build.log}`, `logs/s-mescius-Release-dryrun.txt`,
  `logs/s-mescius-Release-dump.txt`, `logs/s-mescius-Release-clean.prep.txt`, `logs/p-run.stdout`
- The cputime outputs: `logs/s-mescius-Release-clean.log`, `logs/s-mescius-Release-noop.log`,
  `logs/p-mescius-gccn-clean.log`
