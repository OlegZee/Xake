# Session: arch-review

**Where things stand (2026-11-20).** Worktree `.claude/worktrees/arch-review-b79de6`, branch
`feature/arch-review` at 6f39dbb (nothing committed yet beyond this folder). Topic 1 (the
owner's: `Database.fs` holds the hasher and decision code) is mapped and a layout is proposed in
[database-layout.md](database-layout.md): `Database.fs` keeps L2 only; new `FileProbe.fs`
(stat, hash, hasher), `FileRule.fs` (modes, observation, rule), `Observe.fs` (RunFiles,
ObserveContext, observeFiles); the hasher moves to `EngineState.Hasher`; `Step`, `makeResult`
to Types.fs, `Agent` to Prelude.fs.

**Next step.** The owner decides on the layout and the four choices in section 3 of
database-layout.md. Then: one commit with the pure move, full test suite, idle-perf bench
(`docs/features/idle-perf/bench/build.fsx`), architecture.md sections 1 and 5 updated.
