Xake 5.0, phase 1: the database layer — the `File` type, simple compact paths, and the record shapes for content cut-off. Plan step 0: report a short design and size budget before code.

## Context

Xake 5.0 is being redone in phases after a first attempt (PR OlegZee/Xake#140, closed; code at tag `archive/xake5-attempt1`, commit `e034997`) produced a database layer the owner found too large: a separate `PathTable` module with a directory tree, named roots, length-framed records, salvage, carried records, re-rooting. **None of that is wanted now.** This phase builds the database layer simply. Later phases (separate sessions): phase 2 the rule header and `also`, phase 3 content cut-off in the engine.

## Repository and branch

- `~/projects/Xake` (OlegZee/Xake). Your worktree is cut from local `dev` (= `origin/dev`, `1837856`). First step: `git log -1`; if HEAD is not the current `origin/dev`, stop and report. Rename the branch: `git branch -m feature/db-paths-simple`.
- Read `CLAUDE.md`, `docs/session.md`. Create `docs/features/db-paths-simple/` (`brief.md` = this brief, `session.md`, `tracker.md`).
- Reference only (do not merge or cherry-pick wholesale): the closed attempt at `archive/xake5-attempt1` — `docs/features/dep-cutoff/brief.md` (decisions on content cut-off, "no epochs"), `docs/features/db-paths/critic.md` (facts about `Database.fs`), `src/core/ContentStore.fs`.
- The `File` type brief: [file-type-brief.md](file-type-brief.md) (owner-agreed, copied here). Copy it into the feature folder; check it names no product, customer or company.

## Scope

**1a. The `File` type** (as the file-type brief, owner decisions):
- `File` = a normalised absolute path (`Path.collapsePath` only: `/` internally, `.`/`..` collapsed, repeated separators removed, root kept, case kept; lexical, no symlink resolution). No `FileInfo`, no root inside, `undefined` removed (`File option` where needed).
- One comparison rule everywhere: ordinal on every OS, one shared comparer (`File`, `Target`, rule/pattern matching, `WorkerPool.mapKey`). `Equals`/`GetHashCode`/`CompareTo` consistent.
- `File.display root f`: relative under `root`, absolute otherwise. Decide `Name` per the brief (kept as display or removed) and note it in release notes.
- The check "one file under two spellings" during a run: target → error, source → warning, on all OSes.

**1b. Paths in database 0.7, the simple form:**
- Every `File` in a record (`FileTarget`, `FileDep`, `ArtifactDep`/`ArtifactDep2`, `GetFiles` lists) is written as `File.display ProjectRoot f` (relative under the root, absolute outside) and joined with the current `ProjectRoot` on load. Targets and dependencies by the same rule.
- Compactness: a **flat** path table in the same append-only file: a record `PathRec (id, path)` written before the first record that uses the id; references are varint ids. One dictionary path→id in the writer, id→File on load (one `File` per path).
- **Not in scope:** a directory tree, named roots / `pathRoot` / `fromVar`, length framing, salvage, carrying records, re-rooting, root records, read-only open, a lock file. An unreadable file is recreated as today. A failed write stops further appends in that run (and is logged).
- 0.4–0.6 files load (old absolute paths accepted as they are) and are rewritten as 0.7; `XakeDbVersion` = "0.7".

**1c. Record shapes for content cut-off** (stored and round-tripped, NOT used by the engine in this phase):
- The size of each file dependency as the dependent saw it (alongside its stamp).
- A per-file content history `(stamp, size) -> hash option` (short list, newest first), keyed by path id; last record per file wins on load.
- The marking of static needs and the grouping of dynamic `need` calls, only if it is a field or two; otherwise "Later".
- Shapes follow the dep-cutoff decisions in the reference brief (no epochs, option B). Keep public type changes minimal and list them.

## Budget and rules

- Allowed new types: what 1a/1b/1c above name. No new source module unless the design report justifies one and the owner accepts it. Target: `Database.fs` about +150 lines for 1b/1c; report actuals.
- Any extra entity, robustness mechanism or critic proposal → a "Later" list in `tracker.md` for the owner, not code.
- A fresh-eyes critic subagent (model Fable) after the design report is optional; if run, only correctness bugs are fixed without the owner, the rest goes to "Later".

## Process

1. Design report (no code): types/signatures for `File`, the 0.7 record list, where each change lands, line budget per file, public breaking changes, measurement plan → report to the orchestrator (the session that started you) and stop.
2. After approval: docs first (`docs/` + release notes "5.0.0 (unreleased)"), then code, tests.

## Done means

Tests: `File` normalisation and identity on Unix and Windows rules; two-spellings check; database round trip of every record kind; 0.6 → 0.7 upgrade with no rebuild on a no-change run; relocation (copy the working copy with `.xake`, build: nothing rebuilt); size on a synthetic database with 114-byte mean paths (target at least 5x smaller than 0.6) plus an `[<Explicit>]` measurement test on a real file given by an environment variable. `dotnet build src/core -c Release` 0 warnings; `dotnet test src/tests -c Release` green; `dotnet fsi build.fsx -- -- build test pack` green.

## Owner's rules

- A library recipe is a pure single-purpose function over Xake primitives; script logic is shown in doc examples.
- Until a feature is released, breaking changes are allowed.
- Xake is public: no product, customer or company names in code, commits, docs or PRs.
- Feature branches merge into `dev` by squash.
- Do not push and do not open a PR until the owner says so. Local commits are fine.
- Before ending the session, update `docs/features/db-paths-simple/session.md`: where things stand and the exact next step.
