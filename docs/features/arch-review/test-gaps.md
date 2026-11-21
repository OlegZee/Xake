# Topic 10: test gaps (candidate 16)

State at aacd497. The concurrency table (architecture.md, section 4) lists points without a
direct test: C2, C4, C6, C12, C13, C15; and the estimate with `--schedule=as-requested` and
`--dryrun` are tested only at the `Verdicts` level.

## 1. A finding first: C12 has a hole

`claim` takes the stats inside its loop, after it has claimed earlier files of the group, and
outside the `try ... finally` that completes the claims. If `FileStat.take` throws for a file
(`FileInfo` raises `ArgumentException` for a path with `\0`; `File.make` accepts it on Unix), the
claims already taken are never completed, and every later verdict observing those files in the
run waits forever. It was so before topic 6 too (the stats were taken before the `try`).

Fix: `claim` completes the claims it took when it fails (or the stats move inside the `try`).
A test can show it without a seam: `observe [good; bad]` raises, then another `Verdict`
observation of `good` in the same run must return, within a timeout.

## 2. The plan

| Point | Test | How |
|---|---|---|
| C12 | a failed observation does not leave its claims behind | as above; fails today, passes with the fix |
| C6 | no deadlock between claims | two `Verdict` observations over `[f; g]` and `[g; f]` at once, 500 times, with a timeout |
| C4 | an older stat never moves a record back | call `settle` with a stale `Pending` (its record older than the current one): the write conflicts, the file is statted again, and the record keeps the newer stamp. `claim`/`settle` become `internal` instead of `private` (the module is internal; only the tests gain access) |
| C13 | overlapping engine runs keep their own per-run state | an engine, two `Demand`s of different targets at once that share a source file: both build, their results carry different epochs, a later `Demand` of each skips |
| C2 | a dependent sees its dependee's result before its own task runs | phony `b` needs `a`; inside `b`'s recipe the snapshot already holds `a`'s result with this run's epoch (`Db.tryGetResult`, visible to the tests) |
| C15 | `FastGate` | no test; measured in fastgate.md |
| `--dryrun` | the plan of a built project | build, change one source, `--dryrun`: the listed targets are the changed one and its dependents, and nothing is built |
| as-requested estimate | the estimate is the same whatever the schedule | `CriticalPathTests` already compares records under both schedules; add the estimate (`Verdicts.Estimate` after `Warm`) for both |

C2, C6 and C13 are expected to pass today; they guard the order the code relies on. C4 needs the
visibility change only. C12 needs the fix.

Order of commits: the C12 test and fix together; then the other tests in one commit.
