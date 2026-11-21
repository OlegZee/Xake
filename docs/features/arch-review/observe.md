# Topic 6: how `FileTracker.observe` uses the decision

State at b17f086. `observe oc mode since files` is about 60 lines; it covers candidates 3
(`RunFiles` claims), 4 (two kinds of stat) and 9 (the retry limit).

## 1. What it does, step by step

1. **Claim.** Each distinct file is claimed in `RunFiles` (not in `PreHash`):
   - the first observer in the run holds a completion (`firsts`);
   - a later `Verdict` waits for the first (`waits`);
   - a later `Recording`/`Output` decides again (`todo`); `Recording` also tests A1.
2. **Stat** every file in `todo`.
3. **Settle**, in rounds:
   - `decide` each file against the snapshot's record;
   - hash the `NeedHash` ones in parallel, then `decideHashed`;
   - `Keep` -> the answer; `Write` -> the answer and a write;
   - all writes in one `Db.apply`; a conflict (another observer wrote the file meanwhile) is
     decided again on a fresh stat against the current record; after round 3 the current record
     is the answer.
4. **Complete** the claims (`finally`).
5. **Later verdicts**: wait for the first observation, then the current record if the file
   exists now.
6. **Reply** `classify since` per file, in the order given.

## 2. What makes it hard to read

1. **Parallel collections by file**: `firsts`, `waits`, `todo`, `stats`, `states`, then per round
   `first`, `hashed`, `hashedBy`, `decided`, `writes`, `byFile`; tuples of four or five fields.
   One per-file record would carry what a round needs.
2. **A dead branch the type allows**: `decideHashed` returns a `Decision`, so `observe` handles a
   `NeedHash` after hashing ("decideHashed never asks again").
3. **Unstated reasons**: why `PreHash` does not claim (its record is the one before the rule
   runs; a verdict waiting for it would read a stale record), and why `Recording`/`Output` claim
   but never wait (they must stat again: they follow writes made in this run; they claim so that
   a verdict arriving after them waits for their write).
4. **The limit of 3** is a bare number; giving up logs nothing.

## 3. Is the claim a cache that a per-run memo could replace? (candidate 3)

Not as a memo of values. A later verdict must answer from the **current** record, not from the
first observation's: when a rule rewrites the file in this run (`Output`), a later verdict must
see `changed` = this run, so its dependents rebuild. A memo of the first answer would be stale.
What the claim gives is an ordering barrier ("the first observation has written its record") plus
"no full stat again" (only `File.exists`). That is what exists; it needs a name and a comment, not
a different structure.

## 4. Proposal (no behaviour change)

1. **Types.** `decideHashed` returns only `Keep | Write`:
   ```fsharp
   type Outcome = Keep of FileState option | Write of FileState
   type Decision = Decided of Outcome | NeedHash
   ```
   The dead branch goes; tests write `Decided (Keep x)` for `decide`.
2. **One record per file decided here** and named steps:
   ```fsharp
   type private Pending = { File: File; RecordedBefore: bool; Known: FileState option; Stat: FileStat }

   let private claim  oc mode files   -> firsts, waits, pending      // step 1-2
   let private settle oc mode pending -> Async<answers>              // step 3, rounds inside
   let private answerLater oc waits   -> Async<answers>              // step 5
   let observe oc mode since files = ...                             // claim; try settle finally complete; later; classify
   ```
   Inside a round: decide synchronously, hash only the `NeedHash` files in parallel (as today:
   no `Async` per file for the common case), apply, re-stat the conflicts.
3. **Comments** for point 2.3, on `RunFiles` and in `claim`.
4. **The limit**: a named constant `maxRounds = 3`, and a line at Debug when it is reached
   (the file and its current record), so a livelock would show in `build.log`. `Context` has no
   logger today; it gains one (`ExecContext.Observe` passes `ctx.Logger`). A loop without a
   limit is possible (each round needs another writer of the same file in between), but a bound
   with a log is safer.

Alternative for 4.1: the continuation in the decision (`NeedHash of (HashResult -> Outcome)`),
decided against in topic 5; `Outcome` gives the same safety without closures.

## 5. Tests

The safety net is `StorageTests` (observe through the facade: racing observations, conflicts,
A1, a verdict's later observation, the hasher) and `FileRuleTests`. New: the limit's log line
(a forced conflict on every round needs a fake `Db`; if that costs a test seam, skip it).
