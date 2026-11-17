# Session: db-paths (Xake 5.0, database 0.7)

Branch `feature/db-paths` (worktree `.claude/worktrees/db-paths-ced680`), cut from local `feature/dep-cutoff` at
`4ece63b`. Merge later dep-cutoff commits only when the orchestrator says so. Brief: [brief.md](brief.md) (copied
as-is 2026-11-17; engine facts in it are from `43706d3`, the sources win). Shares the 0.7 version bump with
[dep-cutoff](../dep-cutoff/brief.md) ("Database (0.7)").

## Where things stand (2026-11-17, implemented)

All owner decisions are in brief.md "Decisions" (Q1-Q6, critic Q-A..Q-I). Implemented, local commits only:

- `src/core/PathTable.fs` (new, internal): varint, roots (`split` by longest root on a separator boundary, Target's
  case rule), `Table` (DirRec/FileRec by id, resolution per run, one `File` per path, mis-rooted check), `Tx` (ids
  handed out while pickling, records collected, committed only after the write; ids never reused).
- `src/core/Database.fs`: format 0.7 (length-framed records, tags 0-3, 4-15 reserved), 0.6 pickler kept for
  0.4-0.6 (`Storage.resultPU`), `openDbWith` (roots, read-only, `DbOpenReport`), `readDatabase`; rewrites for
  upgrade, scrub, compaction, damaged tail (salvage), re-root; records under undeclared roots carried as raw
  frames with their path records; Error per undeclared root, `root moved`, `files outside all roots` (hint when no
  roots are declared), `database: n records, d dirs, f files, size` at Info. After a failed write the agent stops
  appending.
- `ExecOptions.PathRoots`, `RootValue` (`RootPath`, `RootVar`), `fromVar`, `pathRoot` builder op;
  `ScriptRunner.resolveRoots` checks roots before the database opens (launch errors: name, duplicate, same folder,
  project root, secret variable); unset variable = undeclared; `--dump`/`--dryrun` open read-only.
  Switch `undeclaredRootFailsRun` (ScriptRunner.fs) = false: the owner's open point.
- Tests: `src/tests/DatabasePathsTests.fs` (25 + 1 explicit). Docs: `docs/database.md`, release notes 5.0.0.
- Measured: [measurements.md](measurements.md).
- Verified 2026-11-17: `dotnet build src/core -c Release` 0 warnings; `dotnet test src/tests -c Release` 1788 passed,
  11 skipped; `dotnet fsi build.fsx -- -- build test pack` green.

Not done / open:
- Owner: does an undeclared root fail the run (`undeclaredRootFailsRun`)?
- No test injects a failed `Write` (IO error mid-append); the code path stops appending and logs.
- Root-relative display in logs, progress and `--dump` (Q1: a later feature).
- dep-cutoff merge: its content records take tags 4-15 (Q-D); `feature/dep-cutoff` not merged here.

## Next step

Owner review of the implementation and the switch; then merge `feature/dep-cutoff` when the orchestrator says so,
and check that its result records use their own tag.
