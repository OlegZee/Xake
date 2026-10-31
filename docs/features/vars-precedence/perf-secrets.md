# Secret masking: performance measurement

Question (owner): how much do about five secret variables of medium length slow down logging of a
typical 10,000-line log?

## Answer

Five secrets (24-40 characters) cost about **1.1 ms of CPU per 10,000 log lines** (about 110 ns per
line). That is about **0.3 %** of the file-logging time of a 10,000-line run (327 ms in the
end-to-end runs) and about **0.1 %** of the whole process wall time (1.15 s, including `dotnet fsi`
start-up). The end-to-end difference between "no secrets" and "five secrets" is inside the
run-to-run noise (about +/-20 ms on 330 ms), so it cannot be resolved end to end; the micro
measurement is the number to use.

## Machine and toolchain

| | |
|---|---|
| OS | macOS 27.2 (Darwin 27.2.0), arm64 |
| CPU | Apple M5 Pro, 18 logical CPUs |
| .NET SDK | 10.0.403; the script host runs on the SDK's .NET 10 runtime |
| Xake | `src/core`, Release build (`netstandard2.0`), commit `c2d9bbf` (`origin/dev`) |

## Method

**Log corpus.** One deterministic list of 10,000 lines, generated from a fixed seed: 60 % are
compiler-like lines of 80-160 characters (a source path, line and column, a warning code and a
message, in the form `src/core/Logging.fs(45,12): warning FS0064: ...`), 38 % are short status
lines of 20-40 characters, and 2 % are long dump lines of about 400 characters. The base corpus
contains no secret.

**Secrets.** Five random alphanumeric values of 24, 29, 33, 37 and 40 characters, fixed for all
measurements.

**Secret lines.** For the cases with secret lines, a seeded shuffle picks the lines; the k-th
chosen line gets ` token=<secret k mod 5>` appended. 1 % of the corpus is 100 lines, 10 % is 1,000
lines. Each secret line contains exactly one secret.

**Measurement A (micro).** The Release `Xake.dll` is referenced directly and the public
`SecretMask.apply` is called on every line. The four cases:

1. no secret registered;
2. five secrets registered, no line contains one;
3. five secrets registered, 1 % of lines contain one;
4. five secrets registered, 10 % of lines contain one.

`SecretMask` is process-global and append-only, so each case runs in its own process; case 1 never
registers a secret. Each process does one warm-up pass, then 20 timed passes over the 10,000 lines.
Median, p95 (nearest rank) and minimum are reported. After timing, the masked-line count is checked
against the injected count, and no secret text may survive masking.

**Measurement B (end to end).** A Xake script with one phony target writes the same 10,000 lines,
each as `trace Info "%s" line` in a recipe. Console logging is `Silent`; the file log is
`build.log` at `Diag`. Three variants:

- `none`: no variable declarations;
- `declared`: five `var "TOKENi" [Secret]` declarations, with the environment variables
  `XAKE__TOKEN1..5` set to the five values; no line contains a secret;
- `pct1`: the same declarations and environment, and 1 % of the lines carry a secret.

Each variant runs 7 times, each run in a separate `dotnet fsi` process with `--resetdb` and an
empty work directory. The rounds are interleaved (none, declared, pct1, then again) so that drift
affects all variants alike.

*Timing.* The file logger posts each line to a `MailboxProcessor` and returns at once, so a trace
call does not mean the line is on disk. The target therefore posts a final marker line last and
polls the end of `build.log` until the marker appears, then stops the clock. Two times are taken:
`enqueue` (the trace calls, up to the marker post) and `disk` (up to the marker on disk). `disk` is
the primary figure. `wall` is the time of the whole `dotnet fsi` process, measured by the driver.

*Checks.* In every run, the `pct1` log must contain exactly 100 lines with an injected `***` and no
secret text; the `none` and `declared` logs must have no injected `***` and no secret text. The
variable banner that Xake prints at launch shows the secret values as `***` too, so only the
injected `token=***` lines are counted. All runs passed these checks, and each log had at least
10,000 info lines.

## A. Micro: `SecretMask.apply` over 10,000 lines

Times in ms for one pass over the 10,000 lines (20 timed passes after one warm-up).

| Case | Secrets | Lines with a secret | Median ms | p95 ms | Min ms | ns/line (median) | Overhead vs case 1, median |
|---|---|---|---|---|---|---|---|
| 1 | 0 | 0 | 0.037 | 0.038 | 0.024 | 3.7 | - |
| 2 | 5 | 0 | 1.151 | 1.312 | 1.053 | 115.2 | +1.114 ms (+111 ns/line) |
| 3 | 5 | 1 % (100) | 1.169 | 1.392 | 1.038 | 116.9 | +1.132 ms |
| 4 | 5 | 10 % (1,000) | 1.221 | 1.256 | 1.161 | 122.1 | +1.184 ms |

Case 1 is fast because `apply` returns at once when no value is registered. With secrets, every
line pays five `Contains` scans; the replacement adds little for the lines that match.

## B. End to end: the 10,000-line target through the file logger

Seven runs per variant. "disk" is the time from the first trace call to the last line on disk (the
primary figure). "wall" is the whole `dotnet fsi` process.

| Variant | Disk ms, median | Disk ms, min | Enqueue ms, median | Wall s, median | Wall s, min |
|---|---|---|---|---|---|
| none | 327.4 | 303.7 | 95.7 | 1.15 | 1.13 |
| 5 secrets, no secret line | 334.8 | 310.0 | 98.5 | 1.17 | 1.15 |
| 5 secrets, 1 % secret lines | 330.3 | 297.3 | 98.2 | 1.19 | 1.14 |

Overhead of five secrets (the 1 % variant is the realistic case; the no-secret-line variant runs
the same code path with no match):

| | Median | Min |
|---|---|---|
| Absolute, per 10,000 lines (micro, case 3 vs case 1) | +1.1 ms | - |
| Share of disk time (330 ms) | about 0.3 % | - |
| Share of wall time (1.15 s) | about 0.1 % | - |
| End-to-end median difference (pct1 vs none) | +2.9 ms (+0.9 %) | -6.4 ms |
| End-to-end median difference (declared vs none) | +7.4 ms (+2.3 %) | +6.3 ms |

The end-to-end differences are not a reliable signal: the medians move in both directions across
variants, and the spread within one variant is about 30 ms (the `none` runs range 304-341 ms). The
micro numbers are the only measured masking cost.

## Conclusion

Masking five secrets is cheap. It costs about 110 ns per log line, so about 1.1 ms for a
10,000-line log, which is about 0.3 % of the time that file logging of such a log takes and about
0.1 % of a short build's wall time. The cost is linear in lines times registered secrets, and it is
paid on every logger output, so it would grow with many more secrets or much longer lines, but
nothing near that scale is in the typical case. Masking is not a reason to avoid secret variables.

## Side finding: the file log can lose its tail at exit

In a first end-to-end attempt, without the wait for the marker, the `none` variant wrote only 3,445
of its roughly 10,050 lines to `build.log` before the process exited (exit code 0). The file logger
posts to a `MailboxProcessor`, so a script can end with lines still queued. The measurement above
waits for the marker and does not depend on this. The owner may want to confirm whether a normal
Xake script loses the tail of its file log when its last target logs a burst of lines. This was
observed once and not investigated further.
