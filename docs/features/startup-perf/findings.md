# Startup performance: findings (2026-11-20)

Analysis and measurement only; nothing here is a decision. Base: `v5.0.0-preview.3` (407765f).

Machine: Apple M5 Pro, 18 cores, macOS, .NET 8. Data: copies of a consumer's databases, kept
outside the repository (a 5.0 database of 12.5 MB, 0.7 format; a 4.x database of 88 MB, 0.6).
Tools (outside the repository): a console harness built against `src/core` with access to the
internals (`Persist.read`, `toDatabase`, `encode`), a one-rule toy script, Stopwatch marks gated
by an environment variable, and `docs/features/idle-perf/bench/build.fsx`.

## Summary

| Question | Answer |
|---|---|
| Q2 load of 12 MB takes ~1.4 s | **Quadratic path table**: `PathTable.load`/`add` read `Map.Count` (O(n) on an F# `Map`) once per path record. 28,384 files cost 1.2-2.0 s of a 1.33-1.79 s `openDb`. |
| Q3 growth 4.7 -> 12 MB | **History, by design**: 6,996 results, 1,833 live; each full rebuild appends ~2.1 MB. Compaction runs at open when results > 5 x live (9,165 here), so it has not fired yet; it would at about two more full rebuilds (~17 MB). |
| Q1 +1.1 s between banner and first recipe | **It is the database load (Q2).** The consumer's run with a marks build: `database open` 2.72 s of the window (17.3 MB database by then); variables, engine options, banner and the setup demand take milliseconds. Their 4.x run loaded a 361 MB database in about 1.4 s, so 5.0's slower reader is the difference. |

## Q2. Database load

### Breakdown (12.5 MB, 41,672 records: 6,996 results, 34,676 file states, 28,384 paths, 4,975 dirs)

Harness, decode of the bytes already in memory, 3-4 runs in one process:

| Phase | Time |
|---|---|
| read the file (12.5 MB) | 1.4-2.9 ms |
| path records (`PathTable.load`, 33,359 records) | **1,209-1,976 ms** |
| file state records | 10-22 ms |
| result records (1.6 M dependencies in all results) | 643-972 ms (450-470 ms with less GC noise) |
| `List.ofSeq` of the records | < 1 ms |
| `toDatabase` (fold into the maps, the epoch) | 71-78 ms |
| secrets scrub over `Status` | 19-52 ms |
| live results, result count | < 3 ms |
| lock (`.xake.lock`) | not measurable (< 1 ms) |
| legacy reader | not used for a 0.7 file (upgrade only, below) |

Decode allocates ~360 MB (47 gen0, 16 gen1, 7 gen2 collections).

End to end, `Storage.openDb` on a copy: **1,790 ms cold, 1,331-1,352 ms warm**. The toy script
(one phony rule, `dotnet fsi`, time from the script body's start to the first recipe):

| Version, database | First recipe |
|---|---|
| preview.3, empty | 60-70 ms |
| preview.3, the consumer's 12.5 MB | **1,464-1,477 ms** (the consumer measured 1.36-1.49 s) |
| preview.3, the same compacted (4.5 MB) | 1,130-1,246 ms |
| 4.0.0, empty | 50-51 ms |
| 4.0.0, the consumer's 88 MB 0.6 database | **372-418 ms** |
| fix A (below), 12.5 MB | 662-709 ms |
| fix A, compacted | 318-328 ms |
| fix A + C (below), 12.5 MB | 503-531 ms |
| fix A + C, compacted | 285-294 ms |

### Cause

`Persist.PathTable` (Database.fs) numbers dirs and files by the size of its maps:

- `addDir`: `let d = t.Dirs.Count`
- `add`: `let id = t.Ids.Count`
- `load`: `when d = t.Dirs.Count`, `when id = t.Ids.Count`

`Map.Count` walks the whole tree. One walk per path record makes the load O(n²): 28,384 files
x ~14,000 nodes on average. The same applies to `encode` (`PathTable.add` per new file): the
writer pays it per new path during a run, and a compaction or an upgrade pays it for every file
(the compaction rewrite of this database encodes in 1,419 ms; 906 ms with fix A).

4.0's format has no path table; it reads its 88 MB in about 0.3 s (toy above: 372-418 ms
including fsi start). The 5.0 file is 7x smaller but read 4x slower.

### What a 12 MB load should cost

Reading the bytes is ~3 ms. With the quadratic term gone the load is ~0.6 s on this file, of which
~0.45 s is decoding 6,996 results with 1.6 M dependencies through the combinators (`alt`, `wrap`,
`List.init`, an id lookup in `Map<int, File>` per file reference) and ~0.1 s maps and scrub.
A 12 MB 0.7 file should open in **0.3-0.5 s** with the reader as it is shaped now (fix A + C
measured 0.50 s), and in ~0.3 s when it holds only live records (compacted, 4.5 MB).

### Options

- **A. Counters in `PathTable` (recommended).** Two fields `NFiles`, `NDirs`, set in `addDir`
  and `addFile`, read instead of `Map.Count` in `add` and `load`. ~10 lines, Database.fs only, no
  format change. Measured: `openDb` 1.33-1.79 s -> 0.73-1.06 s; toy 1.47 s -> 0.66-0.71 s; the
  compaction encode 1.42 s -> 0.91 s; the 4.x upgrade 1.91-2.18 s -> 1.42-1.62 s. The prototype
  diff is `fixA.diff` in the session notes folder.
- **C. Id -> file as an array while reading.** `Persist.read` keeps a `ResizeArray<File>` the
  path records append to, and the `file` unpickler indexes it instead of `Map.TryFind` through
  the `table ()` closure. ~10-15 lines (the `record` pickler takes a lookup function rather than
  the table). Measured with A: toy 0.66-0.71 s -> 0.50-0.53 s (12.5 MB); 0.32 -> 0.29 s
  (compacted). The experiment (a static switch, not a design) is
  `fixA+arr-experiment.diff` in the notes folder.
- **B. Compact sooner** (see Q3): the load scales with the history in the file; with A + C a
  compacted file opens in ~0.29 s against ~0.5 s for this one.

Not measured: a hand-written result decoder without the combinators (perhaps another 0.1-0.2 s,
~60 lines); not worth it before A and C.

## Q3. Database size

### Contents (the 12.5 MB copy)

| Kind | Records | Bytes |
|---|---|---|
| results | 6,996 (1,833 live, 5,163 superseded) | 10.29 MB |
| file states | 34,676 (28,333 distinct files) | 0.59 MB |
| path table (and header) | 28,384 files, 4,975 dirs | 1.59 MB |

- Live results hold 410,808 dependencies: 353,633 `FileDep`, 43,241 `TargetDep`, 12,164 `Var`,
  1,493 `GetFiles` (139,736 files listed inside them), 235 `SecretDep`, 42 `AlwaysRerun`. The
  largest results are ~145 KB each (2,867 dependencies).
- Results per epoch: four full rebuilds (epochs 41, 57, 59, 61) of 1,191 results each, 2.05-2.15 MB
  apiece, 8.39 MB of the 10.29 MB of results; five partial runs of 199-590 results add 1.58 MB;
  the other ~40 runs add 1-15 results (0.4-38 KB) each.
- 2,572 of the 5,163 re-stored results have the same `Depends` as the result they replace (only
  `Built`, `Epoch`, `Steps` differ). That is what a rebuild records; not a defect.
- File states: 27,323 written by the upgrade (epoch 1), the rest by runs; no state equals the
  previous one of its file; 1,184 differ only in the hash (content cut-off filling in hashes).
- Path table: 51 paths with no file state (paths of results' dependencies never observed);
  it only grows until a compaction.
- Compacted (the live results and the states they refer to): **4.47 MB**, the size the consumer
  saw right after the upgrade.

### Why compaction did not run

Compaction runs only at open (`openDatabaseFile`), when the file is old, holds clear secrets, or
`resultCount > live.Length * 5`. Here 6,996 <= 9,165, so it is not due: by this rule the file
grows to about 5x its live size (~20 MB here) before it is rewritten. File states and the path
table are not counted. The rule is 4.0's (records > 5 x `Status.Count`), carried over.

So the growth is accumulated history (whole results re-stored on rebuild), not leaked or
duplicated records. It matters because the load time is proportional to the history (Q2:
12.5 MB 0.5-0.7 s vs compacted 0.29-0.32 s with A or A + C).

### Options

- **B1. Lower the ratio** (e.g. results > 2 x live). 1 line. Here it would have compacted after
  the second full rebuild; the file would stay at 4.5-~9 MB. A compaction costs a rewrite at open
  (encode ~0.9 s with fix A, plus the write) once every couple of full rebuilds.
- **B2. A byte-based trigger**: compact when the bytes of superseded records exceed the live
  bytes (the reader knows each record's span from the stream position). ~10-15 lines. Tracks the
  load cost directly (a few huge results weigh more than many small ones).
- **B3. Compact at close** instead of at open, when the trigger holds, so a startup never pays
  the rewrite; the writer already owns the table and the stream. ~20-30 lines; the cost moves to
  the end of the run.

## Q1. Script setup: banner to the first recipe

### What a run does in that window

`ScriptRunner.runScript` opens the database in `createScriptContext` (variables, engine options,
`openDbWith`, context) and only then prints the banner. After the banner: setup targets
(`demandTarget`: `beginRun`, `planRun` with the verdict walk of that target), then per group
`beginRun`, `planRun` (the verdict walk of the whole group), `execParallel`.

### Marks (bench script, 1,188 file targets + a phony `info`, no-change run, preview.3 + marks)

| Mark | ms since process start |
|---|---|
| script body evaluated (Run) | 521-525 |
| command line applied | 523-527 |
| variables evaluated | 525-530 |
| engine options (rules) | 526-531 |
| database open (712 KB) | 629-647 |
| context created | 630-649 |
| banner printed | 637-655 |
| group `info` planned | 654-672 |
| group `build` planned (whole verdict walk) | 795-802 |

Variables, rule registration and engine options cost 1-5 ms; the window from the banner to the
first recipe is ~20 ms here, the full no-change verdict walk ~100-120 ms.

### The consumer's +1.1 s

How the consumer measured (their session, 2026-11-20): stdout of `dotnet fsi build.fsx` through a
pipe stamping each line with ms since launch. Start: Xake's `XAKE build tool <version>` line
(`XakeScriptBuilder.ApplyCommandLine`, printed before `createScriptContext`): 3.16 s on 4.x,
3.31 s on 5. End: the first output line of their setup target `info`: 4.41 s on 4.x, 5.66 s on 5.
All of `info`'s lines arrived in the same millisecond, so the end is the first flush, not the
recipe's start. Their 4.x run used a 361 MB database, the 5.0 run the 12.5 MB one.

So the window contains, on both versions: variable evaluation, engine options, **the database
load**, the banner, the setup demand (rule match of `info`, its verdict) and `info`'s own work
up to the first flush. The toy script with the same databases reaches its first recipe in
1.37-1.49 s (4.x, 361 MB) and 1.36-1.49 s (5, 12.5 MB): the load is about the same on both, so it
does not explain the difference. 4.x: window 1.25 s ~ load. 5: window 2.35 s = load ~1.4 s +
~1.0 s unexplained.

The bench shows nothing of that size between the `XAKE build tool` line and the first recipe
outside the load (1-5 ms for variables and engine options, ~20 ms from the banner to the first
recipe). Candidates specific to their script: the first rule match (`makeTarget` of `info`
against all of their rules and patterns), the verdict of `info`, the variables (19 declarations,
one secret), or `info`'s own work before its first flush (several `git` runs; output buffering
differs between versions). A build of preview.3 with Stopwatch marks
(`Xake 5.0.0-preview.3.marks1`, local feed, marks on stderr with `XAKE_MARKS=1`) was run once by
the consumer (no-change run, `-t 16`, 1,438 targets, "Build completed" 1.88 s; the database had
grown to 17.3 MB):

| Mark | ms since process start | Step |
|---|---|---|
| script body evaluated (Run) | 3,525 | |
| `XAKE build tool` line printed | 3,527 | 2 |
| variables evaluated | 3,531 | 4 |
| engine options (rules) | 3,533 | 2 |
| **database open** | **6,258** | **2,725** |
| context created | 6,260 | 2 |
| banner printed | 6,276 | 16 |
| setup `info`: makeTarget, begin, planned | 6,277-6,297 | 21 |
| setup `info` done (its own work: `git` runs) | 6,512 | 215 |
| group `build` planned (verdict walk) | 7,356 | 841 |
| group `pack::...` planned | 8,222 | 284 |

The run before (a rebuild of 602 targets) opened the database in 2.04 s. So the extra second
is the load, which on 5.0 takes longer for a 17 MB file than 4.x took for 361 MB, and grows with
the file (Q3). Variables, rule registration, rule headers and the first rule match cost
milliseconds.

The 17.3 MB copy here: 10,515 results, 2,545 live (compaction at 12,725, still not due);
`openDb`'s decode 1.75 s (1.19 s of it with fix A). Toy script to the first recipe:
preview.3 1.80-1.88 s, fix A 0.89-1.16 s, fix A + C 0.66-0.74 s.

With fixes A + C the load share of the window falls from ~1.4 s to ~0.5 s on 5.0, whatever the
rest turns out to be.
