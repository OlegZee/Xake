# Kickoff: pattern module Phase 4b/5 and the Xake.Dotnet split (fresh orchestrator session)

Written 2026-11-03 at the end of the session that produced PRs #117–#123. Read first, in
this order: `docs/session.md`, then this file, then `docs/features/pattern-module/{session,
tracker,brief}.md` (brief: decisions D1–D43 at the end; do not reopen them) and
`docs/features/dotnet-split/{session,tracker,brief}.md` (decisions S-D1–S-D3).

## Working style (unchanged; see also `kickoff.md` in this folder)

- Orchestrator reads indexes, digests and this file; subagents do the heavy work into files and
  report a digest of at most ~20 lines. Models: haiku measurements, sonnet docs/mechanical,
  opus design and implementation. Never fable for subagents.
- Interview the user one question at a time; write every answer to the feature's `brief.md`
  as a numbered decision (`D44…`, `S-D4…`) with the date, immediately.
- Subagents write only in their own worktree: launch with `isolation: worktree` FROM THE MAIN
  CHECKOUT `/Users/olegz/projects/Xake` (an isolation worktree is created from the current
  working directory's repo: do not launch from another repo's folder), tell them to `git fetch`
  + `git reset --hard origin/<base>` + `git branch -m feature/<name>` first. After the digest,
  `git worktree unlock` if locked, then `git worktree move` to `.claude/worktrees/feature/<name>`.
- The orchestrator may push feature branches and open PRs (`gh --repo OlegZee/Xake`), never
  force-push and never push to `dev` / `feature/patterns2` directly. `git reset --hard` in the
  main checkout is denied to the orchestrator: give the user the command instead.
- After every merge, give the user the removal command:
  `git -C /Users/olegz/projects/Xake worktree remove .claude/worktrees/feature/<name> && git -C /Users/olegz/projects/Xake branch -D feature/<name>`.
- Public repo: no customer or company names anywhere. The audit of the user's private work
  script lives OUTSIDE the repo (see memory); never copy it in.
- CI: `build` (ubuntu, `build.fsx build test pack`), `build-fsc` (locks, SDK pin 10.0.403),
  `test-windows` (windows-latest, full `dotnet test src/tests`). The workflow runs for PRs into
  `dev` and `feature/patterns2`. Wait for it with `gh pr checks <n> --watch` in the background.
  Trap: any `Xake.fsproj` file-list change needs `build.fsc.fsx import` + `update-locks -d
  LOCK_REFRESH=Xake` + `build check -d NUGET_FETCH=on` in the same PR (docs/session.md).
  Trap: inside `xake { }` a local named `rule`/`phony`/`rules` shadows the custom operation.
  Trap: run `dotnet build src/tests --no-incremental` before claiming a test build is clean.

## State at hand-off

| PR | What | Target | Status |
|----|------|--------|--------|
| #117 docs, #118 parser, #119 matcher, #120 scanner | the pattern module | `feature/patterns2` | merged |
| #121 split design note | D1 | `dev` | merged |
| #122 seam S1 (`ProcessExec` public, `hasRule`, no IVT, B4) | split | `dev` | open, CI green, user was reading it |
| #123 Phase 4a (rules on the module; D42/D43; `Rule.validate` in `Yield` per review; FileLogger flush fix; CommandLineTests isolation) | pattern module | `feature/patterns2` | open; last push fixed a test compile error, CI result to check first |

Worktrees: `.claude/worktrees/feature/pattern-rules` (#123), `.claude/worktrees/feature/dotnet-seam` (#122).

## First concrete steps

1. `gh pr checks 123 --repo OlegZee/Xake` — if red, read the job log and fix in the branch.
2. Ask the user whether #122 and #123 are merged; give the removal commands; confirm D43
   (brief, provisional: `x/..` collapsing in a rule's literal prefix; `<==`/`<<<` check; empty
   signing target is XP018) — it is marked provisional until the user says so.
3. **Phase 4b** (after #123 merges; base `origin/feature/patterns2`, branch
   `feature/pattern-fileset`): `Fileset` scanning on `PatternScan.scan` (one ordered
   `(MaskRole * string)` list; D9/D13/D17/D25/D39/D41), warnings reported through
   `PatternScan.reportWarnings`, delete `Path.LegacyMask`/`matchImpl`/`matchesPattern` and the
   regex golden test, hermetic's direct `Path.parse`/`matchesPattern`/`PathMask` uses
   (`Lock.fs`, `Locks.fs`, `Import.fs`) onto the module (keep `Includes of PathMask`, D16; C8),
   the `dir/**/*` workaround can go (D12), `FilesetTests` rewritten per spec section 3 (duplicates
   removed, ordinal order, `obj/` prunes, symlinks not followed, case per OS), mask items appended
   to `docs/release-notes.md`, fsc refresh if the file list changes. Vectors stay green. Opus.
4. **Phase 5** (after 4b): port the typed layer from the frozen branch `feature/typed-matches`
   (worktree `.claude/worktrees/feature/typed-matches`, READ-ONLY, never merge; PR #91 there:
   `src/core/TypedTargets.fs`, tests `TypedTargets*`, `TypedCompileTests`) onto the module;
   then docs (#92 there: `docs/typed-targets.md`, overview "Patterns, masks and paths",
   cheatsheet) and the phony `=>` → `Command` announcement. Ask the user how to slice (one or
   two PRs) before starting.
5. Then `feature/patterns2` → `dev` (one PR; the user merges), release notes 4.0 complete.
6. **Split, in parallel with 3–5** (independent of the pattern module until S5):
   S2 the user tags `legacy-1.x` on the old repo `/Users/olegz/projects/Xake.Dotnet` (reused,
   history kept); S3 Xake tag `v4.0.0-preview.1` (user); S4 scaffold the new repo on the preview
   (one package `Xake.Dotnet` with both assemblies, `build.fsx`, `bump-base`, CI with a windows
   leg, rehearsal history import with `git filter-repo`) — the orchestrator must NOT write into
   the old repo without an explicit go; propose the S4 plan first. S5 gate = Phase 4/5 on `dev`.

## Open items the user has not answered

- None on the specs. D43 awaits confirmation (step 2). Phase 5 PR slicing (step 4).
