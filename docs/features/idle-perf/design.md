# Idle performance: design of the batched verdict query

Decision and measurements: [brief.md](brief.md). Prototype: `idle-perf-batch-prototype.diff`
(orchestrator's notes folder), 0.66-0.93 s against 2.29-2.50 s (preview.2) and 0.56-0.64 s
(4.0.0) on `bench/build.fsx`.

## 1. The message

In `Storage` (`Database.fs`), next to `Observe`:

```fsharp
/// The reply for one file of an `ObserveVerdicts` batch.
type VerdictReply =
    /// what `Observe (file, Verdict, since)` would have replied, `HashNeeded` included
    | Observed of Observation
    /// the file is being hashed for another observation: ask it alone (`Observe`), which waits
    | AskAlone

type DatabaseApi =
    ...
    /// Verdict observations of the files of one target at once (the no-build verdict): one reply
    /// per file, in the order given. Each is `Observe (file, Verdict, since)`, except that a file
    /// being hashed replies `AskAlone` instead of waiting.
    | ObserveVerdicts of File list * since: int * AsyncReplyChannel<VerdictReply list>
```

`DatabaseApi` is public; the case is added at the end before `InMemory` (a new case, no existing
case changes). `Observe` stays for every other caller.

## 2. The agent

For each file in order, against the state threaded through the fold:

- `pending` holds the file (another caller is hashing it): `AskAlone`. Nothing is observed.
- otherwise `observeReply db out Verdict since file`, the function `Observe` uses: the first
  observation in the run stats the file, the next ones take the cached path (`observed`, then
  `File.exists`); a new or moved state is stored and appended as today. Its reply is wrapped in
  `Observed`; a `HashNeeded` registers the file in `pending` exactly as for `Observe`, so a later
  file of the same batch, or another caller, sees it pending.

One reply for the whole batch, after the fold. A batch never waits inside the agent.

## 3. The caller

`DependencyAnalysis`:

- `hashAndSettle ctx request` is factored out of `observeSince` (hash off the agent, the
  `FileHashed` counter, `Settle`); `observeSince` uses it, unchanged in behaviour.
- `observeVerdicts ctx since files : Dictionary<File, Observation>`: one `ObserveVerdicts` (none
  for an empty list); `Observed (HashNeeded r)` -> `hashAndSettle` (one by one, as now);
  `Observed o` -> `o`; `AskAlone` -> left out.
- `getChangeReasons`, for a target with a stored result of epoch `e`: the distinct files of its
  `FileDep f` and `TargetDep (FileTarget f)` dependencies go in one `observeVerdicts ctx e`. The
  `observe` given to `getDepState` reads the dictionary; a file not in it (`AskAlone`) goes
  through `observeSince ctx Verdict e f |> Async.RunSynchronously`, as every file does today.
  `getDepState` itself does not change (it always passes `epoch`, the same `since`).

Every caller of `getChangeReasons` gets it: the group-start walk (`planRun` / `openProgress`,
through the memo), the fast path (`UpToDate`) and the task's own verdict (`NeedRebuild`). `--dump`
(`dumpDeps`) keeps single observations.

The order of observations changes inside one verdict: all of a target's files are observed before
`getDepState` recurses into its dependees' verdicts (today a file is observed just before its own
dependency is judged). A verdict observation is cached for the run, so a run with nothing else
going on gets the same answers. With tasks running at the same time (the task's own verdict, the
fast path during a run), the set of observations is the same and their interleaving with other
tasks' `Output`/`Recording` observations was already timing-dependent.

## 4. What does not change

- The database format: no record changes; the same `FileRec`s are appended in the same cases.
- Epochs: `since` is the dependent's epoch as now; changes are stamped with the agent's
  `db.Epoch` (the last `NewRun`). `NewRun` clears `observed`/`recorded` as now. Overlapping engine
  runs share the agent as now.
- Content cut-off: hashing happens only where `Observe` would have hashed (`hashing` set by
  `NewRun`; off with `--content-cutoff=off`); a file is hashed once (`pending`), and a waiting
  caller gets the settled state through the fallback.
- `--dryrun` / `--dump` (`InMemory`): the same agent functions, nothing appended.
- Verdict results, change reasons and their order (`getDepState` maps the dependencies in order).

## 5. Tests

`src/tests/StorageTests.fs` (agent level):

1. A batch equals single observations: two agents on copies of one database, a batch on one,
   `Observe Verdict` file by file on the other, over a missing file, a file without a record, an
   unchanged file, a moved file with hashing off, twice in one run (cached path): equal replies
   and equal database contents after `CloseWait`.
2. Hashing in a batch: hashing on, a moved file -> `Observed (HashNeeded r)`; before `Settle`, a
   second batch with that file -> `AskAlone`, and a single `Observe` of it is answered only after
   the `Settle` (with the settled state).
3. Duplicate file in one list (the caller dedups, but the agent must be safe): the second entry
   is `AskAlone` when the first was `HashNeeded`, else the cached reply.

Engine level: the existing `EpochTests`, `ContentCutoffTests`, `StaleArtifactDepTests`,
`CriticalPathTests`, `ProgressTests` cover verdict behaviour through the new path unchanged; one
new test in `ContentCutoffTests`: a target with many file dependencies where one changed with the
same content (cut-off) and one really changed, built after the change: the second rebuilds, the
hashed file counted once.

Perf: no timing test in CI (noisy). `bench/build.fsx` is the check, run by hand before the release:
the no-change run within 1.2 x of 4.0.0 on the same machine (measured: 0.66-0.93 s vs 0.56-0.64 s).
Recorded in the release checklist note of this folder.

## 6. Docs and release notes

- `docs/implnotes.md`, "Epochs", the observation bullet: "a verdict observes the files of one
  target in one `ObserveVerdicts` message; a file being hashed is asked alone and waits".
- `docs/release-notes.md`, "5.0.0 (unreleased)", "Also:":
  - "A no-change run checks a target's file dependencies with one database request instead of
    one per file; with preview.2 a no-change run of a large build took about twice as long as on
    4.x, now it is back to 4.x time."
  - "A recursive recipe (`let rec f n = recipe { ... do! f (n + 1) }`) may fail type inference
    with FS0041 ('A unique overload for method 'Bind' could not be determined'), because `Bind`
    now has overloads; annotate the return type: `let rec f n : Recipe<ExecContext, unit> =
    recipe { ... }`." (verified on preview.2; 4.0.0 compiles it)

## 7. Line budget

| Where | Lines |
|---|---|
| `Database.fs`: `VerdictReply`, the case, the handler | ~20 |
| `DependencyAnalysis.fs`: `hashAndSettle`, `observeVerdicts`, `getChangeReasons` | ~25 |
| tests | ~90 |
| docs, release notes | ~10 |

About 45 lines of code, ~90 of tests.

## 8. Critic's remarks (fresh-eyes review, 2026-11-19) and proposals

Ordered by severity; no blocker. "Proposal" is this session's; the owner decides.

| # | Remark | Proposal |
|---|---|---|
| M1 | Deadlock freedom rests on an unstated rule: the walk runs under `memoize`'s one lock (`CommonLib.fs:9-22`, confirmed); a caller that waits on an `AskAlone` file (or recurses into the memo) before settling its own `HashNeeded` files can deadlock with another caller. | Accept. §3 rule: every `HashNeeded` of a batch is hashed and settled right after the reply, before any lookup, fallback or recursion. Test: two concurrent batches `[A;B]`/`[B;A]`, hashing on, both moved: both finish within a timeout. |
| M2 | A batch can leave several files in `pending`; a failure between reply and the last `Settle` leaves them pending forever (`NewRun` does not clear `pending`), hanging later observations in engine mode. | Accept the try/finally: unsettled requests are settled `Unreadable` (a change, so a rebuild: safe). `NewRun` releasing leftover waiters: Later (today's single `Observe` has the same hole). |
| M3 | Speed claims overstate: the batch measured 0.66-0.93 s vs 0.56-0.64 s (up to 1.5x), the 1.2x gate already fails, the consumer was not re-measured. | Accept. Release note: "close to 4.x"; gate: within 1.5x of 4.0.0 on the bench; ask the consumer to re-run before 5.0.0. Brief's table gets the batch row. |
| M4 | No CI guard against a fall back to per-file messages. | Accept: a `RunCounters` count of single `Verdict` observations (caller side, ~4 lines); an engine test on a small no-change build asserts 0. |
| M5 | One verdict hashes all its moved files one by one while holding them; other tasks wait instead of hashing in parallel (after a checkout touching many files). | Accept: hash a batch's `HashNeeded` files in parallel (`Async.Parallel` of `hashAndSettle`); also shortens M2's window. |
| m6 | An exception inside the agent's fold (e.g. `FileInfo` on an illegal path on net462) kills the agent with files possibly left pending. | Accept: per-file try/with replying "ask alone", so the single `Observe` reports the error as today. Test. |
| m7 | A batch of thousands of first observations keeps the agent busy; other messages wait behind it. | Mention in §3; no split (Later if seen). |
| m8 | `AskAlone` has no engine-level test. | Accept: two parallel targets sharing a moved file, hashing on: verdicts as a sequential run, `FileHashed = 1`. |
| m9 | Public API change not stated (`DatabaseApi` case, new public type). | Reply `Observation option` (None = ask alone), as the prototype: no new type; the new case listed in the 5.0 API changes. |
| n10 | §1 placement wording contradicts itself. | Fix: the case goes after `Settle`. |
| n11 | The prototype's `since = epoch` guard is dead. | Keep it out (the design already does). |
| n12 | The FS0041 note is unrelated to the fix. | Same PR, its own commit (the task asked for it here). |
| n13 | Test 1 "database contents" is vague. | Compare `readDatabase` results (`Files`, `Status`), not bytes. |

Budget after the remarks: code ~60 lines (`Database.fs` ~22, `DependencyAnalysis.fs` ~32, counter
~6), tests ~150, docs ~12.
