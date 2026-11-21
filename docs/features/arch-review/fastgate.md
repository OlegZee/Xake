# Topic 9: `FastGate` (candidate 13)

State at 0628369.

## 1. What it is

`RunCounters.FastGate`, a `SemaphoreSlim(max 2 cores)`, wraps the fast path in
`ExecCore.prepareOne`: a target not started in this run asks `ctx.Run.UpToDate` (=
`Verdicts.Planned`) under the gate. It came with content cut-off (#143), when the verdict made
blocking database calls; its comment still says "blocking database lookups".

## 2. Is it still needed?

The database lookups are gone (the snapshot is read without a message), but the verdict is not
free of blocking work. A target that `Warm` did not visit computes its verdict on the fast path,
and that verdict's observation takes the stats synchronously on the calling pool thread (`claim`,
`FileStat.take`). `Warm` visits the goals and the dependees named in their reasons; in a no-change
run the reasons are empty, so on the bench all 1,188 file targets get their verdict on the fast
path. Without the gate, all of them start at once.

Measured on the bench (Xake's own "Build completed in", 18 cores, same commit with and without
the gate, alternating):

| `-t 16` | with the gate | without |
|---|---|---|
| full build, 8 runs | 0.54, 0.54, 0.58, 0.59, 0.60, 0.65, 0.66, 0.73 s (median 0.60) | 0.53, 0.56, 0.59, 0.62, 0.77, 0.77, 0.86, 0.87 s (median 0.70) |
| no-change, 24 runs | 0.15-0.20 s | 0.14-0.20 s |

With `-t 4` (2 rounds): full 0.93 and 0.86 s with the gate, 0.92 and 1.23 s without.

A no-change run is the same either way. A full build is the same or faster with the gate, and
more stable. There is no case for removing it.

## 3. Options

- **a. Keep it, say why, and put it where it belongs (recommended).**
  - The comment and architecture.md (C15, candidate 13) give the real reason: a verdict that was
    not warmed stats files synchronously on a pool thread; the gate bounds how many do so at once.
  - `FastGate` and `TaskWaits` move from `RunCounters` to `RunState`: they are run state, not
    counters. `RunCounters` keeps only the counters and `Summary`. Same lifetime: `beginRun`
    copies the run state and keeps them, as it keeps `Counters` now.
  No behaviour change.
- **b. Remove the gate.** The measurements give no reason to.
- **c. Leave the code, fix the comment only.**

A later, separate question: the stats in `claim` could run off the calling thread (or in parallel
for a large group), which would make the gate unnecessary. That is performance work, not part of
this review.
