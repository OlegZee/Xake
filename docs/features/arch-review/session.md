# Session: arch-review

**Where things stand (2026-11-20).** Worktree `.claude/worktrees/arch-review-b79de6`, branch
`feature/arch-review` (from 6f39dbb, `feature/idle-perf`); local commits only, nothing pushed.
Topic 1 is done (layout A, brief.md): `DatabaseFormat.fs` (`BuildLog`: version, `Database`,
`DbRecord`; internal `Persist`), `Database.fs` (`Storage`: agent, writer, file lifecycle, `Db`
without a hasher), `FileProbe.fs` (`FileStat`, `HashResult`, `Content.hash`, `Hasher`),
`FileTracker.fs` (`ObserveMode`, `Observation`, `FileRule`, `RunFiles`, `ObserveContext` with
`Hasher`, `observeFiles`); `EngineState.Hasher`; `makeResult` and `Step` in Types.fs, `Agent` in
Prelude.fs. Pure move, no behaviour or format change. Full suite 1,889 passed, 0 failed. Bench
(process wall time, 3 rounds, `-t 16`): base full 1.52-1.77 s, no-change 0.94-1.13 s; after full
1.52-1.62 s, no-change 0.94-1.02 s. architecture.md section 1 and 5 updated;
`architecture.html` (Russian render) is stale.

**Next step.** The owner picks the next topic from architecture.md section 7 (the per-run memo
of observations, candidate 3, is noted in tracker.md). PR into `feature/idle-perf` only on the
owner's word.
