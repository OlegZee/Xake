# Secret masking: performance measurement

Question (owner): how much do about five secret variables of medium length slow down logging of a
typical 10,000-line log?

## Answer

Five secrets (24-40 characters) cost about **1.1 ms of CPU per 10,000 log lines** (about 110 ns per
line). That is about **0.3 % of the logging time** of a 10,000-line run (327 ms of file-logging time
in the end-to-end runs) and about **0.1 % of the whole process wall time** (1.15 s, including
`dotnet fsi` start-up). The end-to-end difference between "no secrets" and "five secrets" is inside
the run-to-run noise (about +/-20 ms on 330 ms), so it cannot be resolved end to end; the micro
measurement is the number to use.

## Machine and toolchain

| | |
|---|---|
| OS | macOS 27.2 (Darwin 27.2.0), arm64 |
| CPU | Apple M5 Pro, 18 logical CPUs |
| .NET SDK | 10.0.403 (`dotnet --version`); `dotnet fsi` runs on the SDK's .NET 10 runtime |
| Xake | `src/core`, Release build, `netstandard2.0/Xake.dll`, commit `c2d9bbf` (`origin/dev`) |

## Method

Scripts are in `docs/features/vars-precedence/perf/`:

- `corpus.fsx`: the shared log. 10,000 lines from a fixed seed: 60 % compiler-like lines (80-160
  chars, e.g. `src/core/Logging.fs(45,12): warning FS0064: This construct causes ...`), 38 % short
  status lines (20-40 chars), 2 % long dump lines (about 400 chars). The five secrets come from
  `secrets.env` (random alphanumeric, 24, 29, 33, 37 and 40 chars). A secret line is the base
  line plus ` token=<secret>` at a seeded random position; 1 % = 100 lines, 10 % = 1000 lines.
- `micro.fsx` (measurement A): `#r`s the Release `Xake.dll` and calls `SecretMask.apply`
  (`SecretMask` is public, so no `internal` workaround was needed). One process per case
  (`PERF_CASE=1..4`), so the append-only, process-global mask list is clean for each case. One
  warm-up pass, then 20 timed passes over the 10,000 lines; median, p95 (nearest rank) and min
  reported. After timing, the script checks that the masked-line count is exactly what was
  injected and that no secret text survives.
- `e2e.fsx` + `run_e2e.py` (measurement B): a phony target that writes the same 10,000 lines with
  `trace Info "%s" line` in a recipe, with `consolelog Silent` and `filelog "build.log" Diag`.
  Variants: `none` (no declarations), `declared` (five `var "TOKENi" [Secret]` with
  `XAKE__TOKEN1..5` set, no secret line), `pct1` (the same plus 1 % secret lines). Each variant is
  run 7 times, in separate `dotnet fsi` processes, interleaved by round, with
  `--resetdb` and a fresh work directory each time.
  - The file logger posts to a `MailboxProcessor` and returns at once. The target therefore appends
    a final marker line and waits until it is on disk, so the timing includes the write. Two
    timings are printed: `enqueue` (the trace calls only) and `disk` (until the marker is in
    `build.log`). The primary number is `disk`; `wall` is the whole `dotnet fsi` process.
  - Checks per run: `pct1` has exactly 100 `token=***` lines and no secret text in `build.log`;
    `none` and `declared` have no `token=***` and no secret text. The variable banner prints the
    secrets as `***` too, so the check counts only the injected `token=***` lines. Every run
    passed these checks.

Raw output: `perf/results-micro.txt`, `perf/results-e2e.txt`.

## A. Micro: `SecretMask.apply` over 10,000 lines

Times in ms for one pass over the 10,000 lines (20 timed runs after one warm-up).

| Case | Secrets | Lines with a secret | Median ms | p95 ms | Min ms | ns/line (median) | Overhead vs case 1, median |
|---|---|---|---|---|---|---|---|
| 1 | 0 | 0 | 0.037 | 0.038 | 0.024 | 3.7 | - |
| 2 | 5 | 0 | 1.151 | 1.312 | 1.053 | 115.2 | +1.114 ms (+111 ns/line) |
| 3 | 5 | 1 % (100) | 1.169 | 1.392 | 1.038 | 116.9 | +1.132 ms |
| 4 | 5 | 10 % (1000) | 1.221 | 1.256 | 1.161 | 122.1 | +1.184 ms |

Case 1 is fast because `apply` returns at once when no value is registered. With secrets, every
line pays five `Contains` scans; the replacement only adds a little for the lines that match.

## B. End to end: the 10,000-line target through the file logger

Seven runs per variant, median and min. "disk" is the time from the first trace call to the last
line on disk (the primary number). "wall" is the whole `dotnet fsi` process.

| Variant | Disk ms, median | Disk ms, min | Enqueue ms, median | Wall s, median | Wall s, min |
|---|---|---|---|---|---|
| none | 327.4 | 303.7 | 95.7 | 1.15 | 1.13 |
| 5 secrets, no secret line | 334.8 | 310.0 | 98.5 | 1.17 | 1.15 |
| 5 secrets, 1 % secret lines | 330.3 | 297.3 | 98.2 | 1.19 | 1.14 |

Overhead of five secrets (the 1 % variant, which is the realistic case; the no-secret-line variant
is the same code path with no match):

| | Median | Min |
|---|---|---|
| Absolute, per 10,000 lines (micro, case 3 vs case 1) | +1.1 ms | - |
| Share of disk time (330 ms) | about 0.3 % | - |
| Share of wall time (1.15 s) | about 0.1 % | - |
| End-to-end median difference (pct1 vs none) | +2.9 ms (+0.9 %) | -6.4 ms |
| End-to-end median difference (declared vs none) | +7.4 ms (+2.3 %) | +6.3 ms |

The end-to-end differences are not a reliable signal: the medians move in both directions across
variants and the spread within one variant is about 30 ms (the `none` runs range 304-341 ms). The
micro numbers are the only measured masking cost.

## Conclusion

Masking five secrets is cheap. It costs about 110 ns per log line, so about 1.1 ms for a
10,000-line log, which is about 0.3 % of the time that file logging of such a log takes and about
0.1 % of a short build's wall time. The cost is linear in lines times registered secrets, and it is
paid on every logger output, so it would grow with many more secrets or much longer lines, but
nothing near that scale is in the typical case. Masking is not a reason to avoid secret variables.

## Notes

- A finding outside the question: in a first end-to-end attempt, without the wait for the logger
  to drain, the `none` variant wrote only 3,445 of its ~10,050 lines to `build.log` before the
  process exited (exit code 0). The file logger posts to a `MailboxProcessor` and the script can
  end with lines still queued. The measurement waits for the marker to be written so it does not
  depend on this, but the owner may want to confirm that a normal Xake script loses the tail of its
  file log when its last target logs a burst of lines. This was observed once and not investigated
  further.
- Scratch work directories are under `/tmp/perf-secrets-e2e/` and are not part of the repo.
- `secrets.env` holds random test values (not real credentials), used only by these scripts.
- To rerun: `dotnet build src/core -c Release`, then
  `PERF_CASE=n dotnet fsi docs/features/vars-precedence/perf/micro.fsx` for n = 1..4, and
  `python3 docs/features/vars-precedence/perf/run_e2e.py`.
