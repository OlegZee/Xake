# ActiveReports: Xake `build` run in parallel (`<==`) versus `<<<`, and msbuild

Experiment folder: `/Users/olegz/Projects-work/ar/xake-ar/` (repos not touched). Method as in
`ar-experiment.md` section 4: `tools/cputime.pl` (CPU summed over the process group, polled 0.1 s),
util = CPU / wall, 18 logical cores. Fair setting: `-d CSC_SERVER=off` for Xake,
`-nodeReuse:false -p:UseSharedCompilation=false` for msbuild, unless a row says "server on".

## 1. The edit

Original line (kept, `build.fsx` line 82):

    "build" <<< [ for b in brands do for cfg in configs -> $"build::{b}::{cfg}" ]

Only change: `<<<` became `<==`. Copy of the original file: `/tmp/ar-parallel/build.fsx.orig`.

## 2. Clean state and the other session in the same folder

Before the runs: `rm -rf out .xake* obj` in the experiment folder (`obj/` held only script output:
`obj/xake`, `obj/pack`, `obj/resx`; `.packages`, `mescius/`, `gccn/`, `logs/` kept; repos untouched).

A second session was running `build.fsx` in `xake-ar/direct/` during my runs (`direct/logs/clean-off.log`,
`direct/out` and `direct/.xake` modified at 00:50-00:51 while my run 2 was going). Its CPU
is not in my numbers but its load is in my wall times. Each run below is marked with whether it
overlapped that activity (checked from file mtimes and `ps` before and after).

## 3. Xake parallel runs (`build -d CSC_SERVER=off`)

| run | wall | CPU | util | exit | notes |
|---|---|---|---|---|---|
| 1 | 167.3 s | 540.7 s | 3.23 | 0 | overlapped `direct/` build from 00:46:13 to 00:46:54 |
| 2 | 194.2 s | 596.1 s | 3.07 | 0 | `direct/` writing `out` and `.xake` at 00:50-00:51 (overlap) |
| 3 | **did not finish** | 5:43 CPU of the `dotnet fsi` process after 10 min (idle, ~0.3 %) | | none | HUNG, see below |

Run 3 (`logs/p-xake-server-off-run3.log`, `build.log` copied to `logs/p-xake-server-off-run3.build.log`):
at 00:55:44 two of the four top-level tasks completed (`build::mescius::Release`, `build::gccn::Debug`);
`build::mescius::Debug` and `build::gccn::Release` never completed. Output: `out/mescius/Debug` 29 of 30
dlls, `out/gccn/Release` 29 of 30. The last two tasks started were
`obj/resx/gccn/Export.Excel.Page/AssemblyResources/Strings.resources` (00:55:39) and
`obj/resx/mescius/Export.Excel.Page/AssemblyResources/Strings.resources` (00:55:40); neither completed.
Cause in the script (not yet proven): `target "obj/resx/(b:*)/(name:*)/**/*.resources"` has no `cfg`
in its path, so `Debug` and `Release` of one brand write the same `.resources` file concurrently.
I tried to stop the hung process and the permission check denied the kill, so I did not
pursue further kills. The process is still alive at the time of writing (`cputime.pl` has not printed).


## 4. Numbers recovered from the logs after the agent was stopped (orchestrator, 2026-10-13)

The agent was stopped during run 5 (killed by the user's decision after the hang of run 3).
Wall times below are from `/usr/bin/time -l` (`real`); its `user`/`sys` miss child processes and
are not usable (see ar-experiment.md section 4), so CPU/util are not given here.

| run | wall | note |
|---|---|---|
| Xake parallel `<==`, all four, server off, run 4 (quiet machine, completed: 133 "Completed" lines, "Build completed in 00:03:43") | **224 s** | sequential `<<<` sum from round 1: 141 s |
| msbuild sequential, four Rebuilds one after another (seq / seq2) | 12.2 + 25.3 + 13.8 + 28.9 = **80 s** / 11.4 + 25.6 + 14.2 + 27.8 = 79 s | per pair as in round 1 |
| msbuild concurrent, four Rebuilds started together (`&` + `wait`) | 27.1, 40.1, 8.2, 9.0 -> all done in **~40 s** | msbuild's own `-m` nodes share the machine |
| msbuild "no-op" rows `p-msb-noop-*` | 12-27 s | these repeated `-t:Rebuild`, so they are rebuilds, not no-ops; the one true no-op `noop2-mescius-Debug`: **2.5 s** |
| Xake parallel run 3 | hung | two of four graphs complete, `.resources` targets of Export.Excel.Page of both brands never completed (section 3) |
| Xake parallel run 5 | killed at 11 s | - |

Reading: msbuild gains 2x from running the four builds concurrently (80 -> 40 s); Xake with four
independent graphs in one run is **slower than its own sequential sum** (224 vs 141 s). Together with
the no-op at util 1.04 this points at the engine, not at the compiler: something serialises or
contends across targets (G9), and the hang (G16) shows up only under this load.
