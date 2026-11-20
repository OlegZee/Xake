# Session: arch-review

**Where things stand (2026-11-20).** Worktree `.claude/worktrees/arch-review-b79de6`, branch
`feature/arch-review` (from 6f39dbb, `feature/idle-perf`); local commits only, nothing pushed.

- Topic 1 done: `DatabaseFormat.fs`, `Database.fs`, `FileProbe.fs`, `FileTracker.fs` (renamed
  from `FileStaleness`); the hasher in `EngineState.Hasher`; `makeResult`/`Step` in Types.fs,
  `Agent` in Prelude.fs.
- Topic 2 done (api-review.md, option 4b): namespace `Xake.Storage` over the two database files
  (`Database`, `DbRecord`, handle `Db`, module `Db`); public only the read API
  (`Db.readRecords`, `readDatabase`, `tryGetResult`, `formatVersion`) and the opaque handles
  `Db`, `Hasher`, `RunFiles`; the raw mailbox is internal behind `Db.apply/store/newRun/
  keepInMemory/close`; `FileTracker.observe`, `FileTracker.Context`; `ExecContext.Observe`
  internal. Full suite 1,889 passed, 0 failed; bench (process wall, 3 rounds, `-t 16`): base
  full 1.52-1.77 s, no-change 0.94-1.13 s; after topic 2 full 1.55-1.84 s, no-change 0.91-1.02 s.
- `architecture.html` (Russian render) is stale.

- `Db.Dispose` closes the database synchronously (drain, file, lock); a second close or dispose
  returns at once. Two tests; full suite 1,891 passed, 0 failed.

- Topic 4 done (option a): `WaitFor` primitive (ExecTypes.fs), `RunCounters.TaskWaits`,
  `Verdicts.verdictWaits`; `Fresh` registers no edges. Full suite 1,893 passed, 0 failed; bench
  full 1.46-1.87 s, no-change 0.90-0.99 s.

**Next step.** Topic 5 is queued: simplify `FileRule.decide` (tracker.md). Then the owner picks
from architecture.md section 7 (candidate 3, the per-run memo of observations, is noted). PR into
`feature/idle-perf` only on the owner's word.
