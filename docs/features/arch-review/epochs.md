# Topic 11: two epochs (candidate 12)

State at 7fb86dc.

## 1. The two epochs

| Epoch | Read from | Used for |
|---|---|---|
| the run's own | `ctx.Run.RunEpoch` (from `Db.newRun` at `beginRun`) | a result's `Epoch` (the run its task started in, A2); `observeOutputs` asks "changed in this run" (`since = RunEpoch - 1`) |
| the newest handed out | `Db.Snapshot.Epoch`, read in `FileTracker.settle` | a file change's `Changed` (`RuleContext.Epoch`) |

The verdict compares them: a dependency is changed when `file.Changed > result.Epoch`, or a
dependee's `Epoch > result.Epoch`. Without overlapping runs the two are the same number, and
`r <= r` makes a target that `need`s a file changed earlier in its own run up to date on the next
run (implnotes, "Epochs").

## 2. A finding: overlapping runs can miss a change

With two engine runs at once (two `Demand`s of different targets), the newest epoch belongs to
one of them. A change observed by the **older** run gets the newer run's epoch, which can equal
the epoch of a result the newer run built from the file's previous content. `changed > epoch` is
then false and the stale result is kept.

Reproduced deterministically (a scratch test, 3 runs out of 3; the diff is kept outside the
repository):

1. run e1 starts `slow` and waits;
2. run e2 builds `d.txt` from `f.c` = v1 (result epoch 2);
3. `f.c` becomes v2;
4. run e1 `need`s `f.c`: a moved stat, recorded with `Changed` = newest = 2;
5. a third `Demand` of `d.txt`: `2 > 2` is false, `d.txt` is not rebuilt and still holds v1.

Within one run this cannot happen (A1 keeps the record; the next run sees the moved stat). It needs
overlapping engine runs and a change during them, first seen by the older run.

## 3. Options

- **a. A change seen by a run that is not the newest gets a new epoch (recommended).** In
  `settle`, when the newest epoch is greater than the observing run's own (`RunEpoch`), a write
  that marks a change takes a fresh epoch from the agent (as `Db.newRun` does) instead of the
  newest. Then `changed` is above every result built so far, and the step 5 rebuild happens.
  - Without overlapping runs nothing changes: the newest is the run's own.
  - The cost, only with overlapping runs: an agent round trip per such `settle` round, and a
    result the newer run built **after** the change may rebuild once (as in the C13 test: never a
    missed change, at most one extra build).
  - A context with `RunEpoch = 0` (`--dryrun` and `--dump`, which take a run but keep the
    context's epoch; library contexts before a run) keeps today's rule; `--dryrun`/`--dump`
    could set `RunEpoch` properly in the same step.
  - The scratch test becomes a test; C13's row in architecture.md gets this case.
- **b. Document it** as a limitation of overlapping engine runs.

## 4. Names (either option)

`RuleContext.Epoch` becomes `ChangeEpoch` ("the epoch a change is stamped with"); `settle` names
the newest epoch where it reads it; architecture.md section 1 gets the table of section 1.
