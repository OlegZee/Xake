# Architecture review of Xake 5: decisions

Database, observation and verdicts, before #148 merges into dev (PR #149, 2026-11-20..21). One
decision per line; not re-litigated. How the code is now: [architecture.md](../idle-perf/architecture.md).

- Rules: a library function is pure and single-purpose; breaking changes are fine until the
  release; no format work here.
- Files: `DatabaseFormat.fs` (format), `Database.fs` (the database only), `FileProbe.fs` (stat,
  hash, hasher: I/O only), `FileTracker.fs` (the rule, `RunFiles`, `observe`). The hasher is not
  part of `Db`.
- API: these modules are engine internals; a module is public or internal as a whole. Public:
  `Storage.DbReader` (read API), `Database`, `DbRecord`. `Db.Dispose` closes the database.
- `ExecContext` keeps what a recipe reads; per-run state is the opaque `Run: RunState`.
- Two wait-for maps by design (tasks; verdicts: no cycle runs through both), one `WaitFor`.
- Two verdicts by design: `Planned` (never waits) and `ForTask` (after running dependees: with
  cut-off the planned one can be stale).
- `RunFiles` claims stay: a claim is an ordering barrier, not a cache (a memo of the first answer
  would be stale after a rule rewrites the file).
- `FileRule`: one pair of rules per kind of observation. `PreHash` stays (transitions such as
  `--content-cutoff=off` would cost one extra cascade). `LegacyRead` stays for now.
- `FastGate` stays (measured: no gain without it, full builds slower and less stable).
- Overlapping engine runs: a run that is not the newest stamps a change with a fresh epoch (the
  newest alone missed a change).
