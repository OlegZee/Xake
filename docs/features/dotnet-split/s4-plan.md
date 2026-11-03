# S4 plan: scaffold of `OlegZee/Xake.Dotnet` on `Xake 4.0.0-preview.1`

Status: **plan, for owner review** (2026-11-03). Step S4 of [brief.md](brief.md) §9. Decisions
S-D1..S-D3 are taken as final; nothing here reopens them. S4 is a rehearsal: S6 repeats the
history import from `dev` after S5 (pattern-module Phase 4/5), on top of what S4 merged.

Inputs: brief §2, §4-§9; `build.fsx`, `build.fsc.fsx`, `testjobs.fsx`, `.github/workflows/*`,
`src/dotnet`, `src/hermetic`, `src/hermetic.tests`, `src/tests` of Xake `dev` at af45852
(= tag `v4.0.0-preview.1`); the local clone of `OlegZee/Xake.Dotnet` (read only); GitHub settings
of both repos (read via `gh`); the nuget.org flat-container indexes of `Xake` and `Xake.Dotnet`.

## 1. Starting point (facts, 2026-11-03)

| Item | State |
|---|---|
| Xake `v4.0.0-preview.1` | tagged at af45852; **not yet in the nuget.org index** (newest listed: 3.15.0). S4's build cannot restore before it is |
| Xake commit that last touched the moving code | 846beee (S1, #122); af45852 has the same tree for every moving path |
| `OlegZee/Xake.Dotnet` remote | `master` = 930219b (7 commits, 2018), tags `v1.1.0`, `v1.1.4`; `feature/refactoring-core` already gone from the remote; default branch `master`; issues disabled; Actions enabled; merge commit, squash and rebase all allowed; no PRs |
| Local tag `legacy-1.x` | annotated tag 225b572 -> **ba4acca**, the tip of the deleted `feature/refactoring-core` (one commit on top of `master`), **not pushed**. So S2 is half done: the tag exists locally only |
| nuget.org `Xake.Dotnet` | 1.1.0.2-alpha, 1.1.0.4-alpha, 1.1.4.7-beta (unlisted at S9) |
| Locks / SDK | locks pinned to SDK 10.0.403 (`build-fsc` job); this machine has 10.0.403 |
| `git filter-repo` | **not installed** here; the rehearsal (§5) used `git fast-export` + a Python path filter + `git fast-import`, which does the same job deterministically |

## 2. Layout of the new repository

### 2.1 Branches

| Branch | Role | How it changes |
|---|---|---|
| `master` | release line, as in Xake (tags `vX.Y.Z` on it) | **not replaced, never force-pushed**. It stays at the 1.x tip until S8, when `dev` is merged into it (a plain merge: `dev` descends from `master`) |
| `dev` | main line, PR target, GitHub default branch | created by the owner from `master` (930219b) before the S4 PR |
| `feature/split-scaffold` | the S4 PR -> `dev` | removal commit, import merge, scaffold commits |
| `feature/split-import` | the S6 PR -> `dev` | the incremental re-import (§5.4) |
| `import/xake-cut` | local only (never pushed) | the filtered Xake history the merge takes |

**The S4 and S6 PRs are merged with "Create a merge commit"**, never squash or rebase: a squash
throws away the imported history, a rebase rewrites it (and the S6 incremental merge relies on
the imported SHAs staying the same). Every later PR in the new repo is squash-merged as in Xake.

### 2.2 Removed from 1.x `master` (one commit on `feature/split-scaffold`)

`.gitignore`, `.travis.yml`, `build.cmd`, `build.fsx`, `build.fsx.lock`, `build.proj`, `build.sh`,
`readme.md`, `src/` (10 files of the 1.x tasks), `tests/` (3 files). Kept: `LICENSE` (MIT; the
year line is updated in the scaffold), the history and the tags `v1.1.0`, `v1.1.4`, `legacy-1.x`.
The old `src/*.fs` sat directly in `src/`, so no imported path collides with a 1.x path either way.

### 2.3 Imported with history (134 files at af45852)

| Xake path | New path |
|---|---|
| `src/dotnet/`, `src/hermetic/`, `src/hermetic.tests/` (incl. `fixtures/`) | same |
| `src/tests/{DotnetTasks,Toolset,PackageRoot,SdkProbe,CscServer,Resx,CompilerCrash,CompilerJobs,Fsc,PathSpaces,HermeticMode,Description,ProjectPlan}Tests.fs` | `src/dotnet.tests/` |
| `src/tests/{LibraryVarsTests,RecordShapeTests}.fs` (split files), `src/tests/{Common,XakeTestBase}.fs` (copied helpers), `src/tests/tests.fsproj` | `src/dotnet.tests/`, then trimmed in the scaffold (the fsproj becomes `dotnet.tests.fsproj` by `git mv`) |
| `src/tests/fixtures/description/` | `src/dotnet.tests/fixtures/description/` |
| `build.fsc.fsx`, `testjobs.fsx`, `xake.fingerprints`, `locks/Xake.Dotnet/`, `locks/Xake.Hermetic.Dotnet/` | same |
| `docs/dotnet-build.md`, `csc-server.md`, `csc-syntax.md`, `description.md`, `fingerprints.md`, `hermetic-guide.md`, `hermetic-architecture.md`, `docs/hermetic/`, `docs/schemas/xake.project.schema.json` | same |
| `samples/fullframework.fsx`, `toolset.fsx`, `dotnet-fake.csproj`, `helloworld.cs`, `samples/hermetic/`, `samples/netcore-compile/` | same |

Not carried: history of these files from before they reached their current paths. The
`src/dotnet` files were re-added at #16 (2026-09-05); their earlier Xake paths (`core/`,
`src/core/`, `src/`) would drag in engine history, and the 1.x extraction is already in the old
repo's own history. `docs/features/hermetic-*` stay in Xake (S-D1 q9).

### 2.4 Added new in the scaffold

| File | Content |
|---|---|
| `build.fsx` | §3.1 |
| `build.sh`, `build.cmd`, `x` | copies of Xake's wrappers |
| `global.json` | copy (`8.0.0`, `latestMajor`) |
| `.gitignore`, `Icon.png` | copies |
| `.github/workflows/build.yml`, `publish.yml` | §4 |
| `readme.md` (repo), `src/dotnet/readme.md` (package; hermetic section "API may change") | brief §2.1 |
| `docs/release-notes.md` | starts with an unreleased `4.0.0` entry; points to Xake's notes for the history |
| `docs/devprocess.md`, `CLAUDE.md`, `docs/session.md` | the parts of Xake's that apply here |
| `tools/import-from-xake.py` | the path filter of §5, committed so S6 runs the identical filter |

Docs links from the imported docs to engine docs (`../tasks.md`, `../variables.md`, ...) become
absolute links to `OlegZee/Xake`; a link check (every relative `.md` link resolves) is part of the
S4 checks.

## 3. Build and versioning

### 3.1 `build.fsx` (one package)

- Bootstrap `#r "nuget: Xake, 4.0.0-preview.1"` (published engine only, brief §6).
- Libraries: `Xake.Dotnet` (`src/dotnet`) and `Xake.Hermetic.Dotnet` (`src/hermetic`), one
  `Version` (`VERSION` env, default `0.0.1`), both built into `out/<fwk>/`.
- `pack` = `dotnet pack src/dotnet -p:Version=<v>` -> `out/pkg/Xake.Dotnet.<v>.nupkg`. One nuspec
  check: depends on `Xake`; `lib/net462/` and `lib/netstandard2.0/` each hold `Xake.Dotnet.dll/.xml`
  and `Xake.Hermetic.Dotnet.dll/.xml`; no `Xake.dll`; no `Xake.Hermetic.Dotnet` dependency.
  `push` pushes it. No `HermeticVersion`, `pack-hermetic`, `push-hermetic`, `checkXakeDependency`.
- `test`, `test-project::<p>`, `test::<p>/<slice>` as in Xake, over the new `testjobs.fsx`.

### 3.2 Project files

- `src/dotnet/Xake.Dotnet.fsproj`: `PackageId` `Xake.Dotnet`; `XakeVersion` property with the
  range `[4.0.0-preview.1, 5.0)` (overridable: `-p:XakeVersion=...`, used by the `xake-latest`
  job) and `PackageReference Xake` on it; the `IncludeCoreOutput` target becomes
  `IncludeHermeticOutput` (adds `..\hermetic\bin\$(Configuration)\$(TargetFramework)\Xake.Hermetic.Dotnet.dll/.xml`,
  built by an explicit `MSBuild` call on `src/hermetic` before pack, since a project reference
  would be a cycle); IVT `tests` -> `dotnet.tests`; description and URLs to the new repo.
- `src/hermetic/Xake.Hermetic.Dotnet.fsproj`: `IsPackable` false; `PackageId`, `Version`,
  `HermeticVersion` go; `ProjectReference ..\core` goes; `..\dotnet` stays.
- `src/dotnet.tests/dotnet.tests.fsproj`, `src/hermetic.tests/hermetic.tests.fsproj`: no
  `..\core` reference (Xake arrives transitively); hermetic.tests links `..\dotnet.tests\Common.fs`,
  `..\dotnet.tests\XakeTestBase.fs`.
- Descriptions (`xake.project.json` of both libraries and the hermetic.tests fixture):
  `projectRefs: ["Xake"]` becomes a package `Xake` `4.0.0-preview.1`; `package.bundle: ["Xake"]` goes.

### 3.3 Versioning, tags, previews

- One version line from 4.0.0, tag `vX.Y.Z` = package `Xake.Dotnet` X.Y.Z (brief §5). The version
  is the tag minus `v`; `--skip-duplicate`.
- `v4.0.0-preview.N` tags of `Xake.Dotnet` are accepted by the same glob. **None is pushed in S4**
  (open question 4).
- `bump-base` (in `build.fsc.fsx`, release-process E1 restored): `-d XAKE_FLOOR=<v>` (default the
  newest `Xake` on nuget.org, previews included while no 4.x stable exists) -> the floor of
  `XakeVersion` in `src/dotnet/Xake.Dotnet.fsproj` and the package version in the descriptions,
  `import`, `update-locks` with `LOCK_REFRESH=Xake`, clear the NuGet HTTP cache when the floor
  changes. Refuses a version nuget.org does not have. S6 uses it to move to the post-S5 preview,
  S8 to 4.0.0.

### 3.4 The fsc/locks self-build (`build.fsc.fsx`, S-D1 q4)

- Bootstrap stays `#r "nuget: Xake, 3.15.0"` + `#r "nuget: Xake.Hermetic.Dotnet, 0.10.0"` until S9.
- `projects` = `Xake.Dotnet`, `Xake.Hermetic.Dotnet` (no `Xake`); `versions` = one mask `**`,
  `vars.Version`. `locks/Xake/` is not imported.
- S4 runs `import`, `update-locks -d LOCK_REFRESH=Xake`, `build check -d NUGET_FETCH=on` on SDK
  10.0.403 and commits the refreshed descriptions, locks and `xake.fingerprints`.

## 4. Tests and CI

### 4.1 `testjobs.fsx`

`testProjects` = `dotnet` -> `src/dotnet.tests`, `hermetic` -> `src/hermetic.tests`. Jobs:
`dotnet/server` (`Csc compiler server`), `hermetic/server` (unchanged), `dotnet/jobs`
(`Compiler jobs` only; `Delegated and resource tests` stays in Xake), `dotnet/rest`,
`hermetic/record|run|lock|rest` (unchanged). Xake's copy loses the same jobs in S7.

### 4.2 `build.yml` (push/PR to `dev`)

| Job | Runner | SDK | Command |
|---|---|---|---|
| `build` | ubuntu-latest | 10.0.x | `dotnet fsi build.fsx -- -- build test pack` |
| `build-fsc` | ubuntu-latest | 10.0.403 | `dotnet fsi build.fsc.fsx -- -- build check -d NUGET_FETCH=on` |
| `xake-latest` | ubuntu-latest | 10.0.x | `build test` with `-p:XakeVersion=4.*-*` (newest 4.x incl. previews; `4.*` after S8) |
| `windows` | windows-latest | 10.0.x | `build test-project::dotnet` (see open question 3) |

All upload `build.log`; timeouts as in Xake (40/20 min).

### 4.3 `publish.yml` (tags `v*`)

One branch: version = tag minus `v`, `dotnet fsi build.fsx -- -- build test pack`, then
`dotnet nuget push out/pkg/Xake.Dotnet.<v>.nupkg --api-key ${{ secrets.NUGET_API_KEY }}
--skip-duplicate`. Committed in S4 but inert: no tag is pushed and no secret exists until S8.

## 5. History import

### 5.1 Method

1. Fresh clone of Xake at the cut commit (S4: af45852; S6: `dev` after S5).
2. `git fast-export --no-data --signed-tags=strip --tag-of-filtered-object=drop <cut>` ->
   `tools/import-from-xake.py` (keeps the §2.3 paths, applies the `src/tests` renames, drops commits
   left empty, collapses parents of dropped commits) -> `git fast-import` onto `refs/heads/cut`.
   With `git filter-repo` installed the same is `--paths-from-file` + `--path-rename` lines; either
   way the filter must be the same in S4 and S6 (§5.4).
3. Proposed (open question 2): the filter also rewrites a trailing `(#N)` in subjects to
   `(OlegZee/Xake#N)`; 63 of the kept subjects end in `(#N)`, which on the new repo would link to
   its own future PRs.
4. In the new repo: `git fetch <scratch> cut:import/xake-cut`; on `feature/split-scaffold` after
   the removal commit: `git merge --allow-unrelated-histories --no-ff import/xake-cut -m "Import
   ... from Xake <sha> (history kept)"`. Then the scaffold commits on top.

Rejected: a subtree merge (needs a prefix; the layout keeps Xake's paths), a rebase of the cut onto
`master` (rewrites all 155 commits and flattens the 32 merges, no gain), grafts/`replace` (not pushed by
default, invisible on GitHub).

### 5.2 Rehearsal (done, scratch clones only, nothing pushed)

| Measure | Value |
|---|---|
| Xake commits at af45852 | 617 (64 merges), pack 4.81 MiB |
| Kept by the filter | **155 commits** (32 merges, mostly 2015 git-flow merges kept by the test-helper history), tip = rewritten #122 |
| Files in the cut | 134; file-set and blob ids equal the mapped Xake tree (0 missing, 0 extra, 0 differing) |
| Old repo + removal + merge | 164 commits, 135 files (+`LICENSE`), pack **1.03 MiB** after `gc --aggressive` |
| Blame | `src/dotnet/DotNetFwk.fs`: 1388 lines from 18 commits; `src/dotnet.tests/DotnetTasksTests.fs`: 19 commits; `src/dotnet` + `src/hermetic`: 70 commits |
| Determinism | two runs from af45852 give the same tip SHA |
| Incremental | a cut at an older commit (#115) is an ancestor of the af45852 cut (1 commit between) |
| Name scan | no customer names in the kept messages or the tip tree |
| Filter | a scratch script of the planning session (not committed); committed as `tools/import-from-xake.py` in step 7. The rehearsal did not apply the `(#N)` rewrite |

`filter-repo` would likely prune a few more degenerate merges than this filter; the numbers above
are for the committed filter, which is what S4 and S6 use.

### 5.3 Verification (each import)

- File set: the script compares `git ls-tree -r cut` with `git ls-tree -r <cut commit>` mapped
  through the filter (count, paths, blob ids); must be zero differences.
- After the merge: the tree is the cut plus `LICENSE`; after the scaffold, `git diff
  import/xake-cut HEAD --stat -- <imported paths>` lists only the planned edits (§3.2, test trims).
- Build and tests: `build test pack` green, nuspec check passes, `build.fsc.fsx build check` green,
  CI green on all four jobs.

### 5.4 S6 repeats it

Because the filter is deterministic and Xake's `dev` only appends (squash merges), the S6 cut
from `dev` after S5 **contains S4's 155 rewritten commits unchanged** plus the new ones. S6 is then
a normal merge of the new `import/xake-cut` into `feature/split-import` (no unrelated histories,
no tree replacement): only the post-af45852 Xake commits come in, and conflicts appear only in files
the S4 scaffold edited (fsprojs, descriptions, locks, test trims, `testjobs.fsx`, `build.fsc.fsx`),
resolved by keeping the scaffold edit plus the incoming change. Then `bump-base` to the post-S5
preview, `update-locks`, the §5.3 checks. Condition: the path list and message rewrite do not change
between S4 and S6; if a path must be added, add it as a plain copy in S6 (no history) or accept a
one-off unrelated-histories merge of that path only.

## 6. Ordered steps

"Owner" = an action that writes to `OlegZee/Xake.Dotnet` or nuget.org, **owner-approved and done
by the owner**. "Agent" = local work in a worktree or scratch clone, nothing pushed.

| # | Step | Who | Reversible | Check |
|---|---|---|---|---|
| 1 | Owner reviews this plan; answers §7 | owner | — | tracker S4 row updated |
| 2 | Wait until `Xake 4.0.0-preview.1` is in the nuget.org index | agent checks | — | flat-container index lists it |
| 3 | Finish S2: `git push origin legacy-1.x` (tag on ba4acca) | **owner** | yes (delete remote tag) | `git ls-remote --tags origin` shows it |
| 4 | Create `dev` from `master` on the remote; set default branch `dev`; keep merge commits allowed | **owner** | yes | `gh repo view --json defaultBranchRef` |
| 5 | Create the local worktree `Xake.Dotnet/.claude/worktrees/feature/split-scaffold` on `feature/split-scaffold` from `origin/dev` | owner (or agent if granted, Q1) | yes (remove worktree) | `git worktree list` |
| 6 | Removal commit (§2.2) | agent | yes (local) | only `LICENSE` left |
| 7 | Cut Xake at af45852 with `tools/import-from-xake.py`; file-set check (§5.3) | agent | yes (scratch) | 0 differences; ~155 commits |
| 8 | Merge `import/xake-cut` with `--allow-unrelated-histories` | agent | yes (local reset) | tree = cut + `LICENSE` |
| 9 | Scaffold: project files (§3.2), test trims (`LibraryVarsTests`, `RecordShapeTests`, helpers), `testjobs.fsx`, `build.fsx`, wrappers, `global.json`, `.gitignore`, `Icon.png`, readmes, docs, `CLAUDE.md`, `tools/` | agent | yes (local) | `dotnet fsi build.fsx -- -- build test pack` green; nuspec check |
| 10 | `build.fsc.fsx`: projects, `bump-base`, `import`, `update-locks -d LOCK_REFRESH=Xake`, commit locks/fingerprints | agent | yes (local) | `build check -d NUGET_FETCH=on` green |
| 11 | Workflows `build.yml` (4 jobs), `publish.yml` | agent | yes (local) | `actionlint` if available; YAML parses |
| 12 | Link check of imported docs; name scan of the tree | agent | yes | no broken relative link; no customer names |
| 13 | Push `feature/split-scaffold`, open PR -> `dev` (`gh pr create --repo OlegZee/Xake.Dotnet`) | **owner** (agent prepares the text) | branch: yes; PR: close | CI green on 4 jobs |
| 14 | Merge the PR with **"Create a merge commit"** | **owner** | hard after others build on it | `git log --oneline dev | wc -l` ≈ 165 + scaffold commits |
| 15 | Record in Xake: tracker S4 done, session entry; Xake.Dotnet `docs/session.md` | agent (Xake PR) | yes | — |
| later | `NUGET_API_KEY` secret (key scoped to `Xake.Dotnet`), before S8 | **owner** | yes (rotate/delete) | publish dry run at S8 |

Nothing in S4 tags, publishes, or touches `master` of `OlegZee/Xake.Dotnet`.

## 7. Open questions for the owner

**Answered 2026-11-03: all seven as recommended (brief S-D5 to S-D11). S-D4: `legacy-1.x` stays at ba4acca.**

1. **Where does the agent work on Xake.Dotnet?** Agents may write only in their own worktree.
   Recommended: the owner creates the worktree of step 5 and hands its path to the agent; the
   agent commits there and never pushes.
2. **Rewrite `(#N)` in imported subjects to `(OlegZee/Xake#N)`?** Recommended: yes (GitHub would
   otherwise link them to the new repo's PRs); decide now, because S6 must use the same rewrite.
3. **Windows leg scope.** Recommended: in S4 run all of `src/dotnet.tests` (`test-project::dotnet`)
   on windows-latest, unfiltered, to learn its time and flakiness; narrow it with `FILTER` later only
   if it is slow. Hermetic tests stay ubuntu-only for now (locks are pinned to one SDK, not one OS,
   so a windows `build-fsc` could be added later).
4. **`Xake.Dotnet` previews before 4.0.0?** Recommended: none from S4 (pre-patterns tree, the
   history would be re-shaped by S6); allow `v4.0.0-preview.N` after S6 if a user needs it.
5. **`filter-repo` vs the committed Python filter.** Recommended: keep the committed filter
   (no extra tool, proven deterministic here); `filter-repo` stays an option if installed.
6. **Default branch `dev` now?** Recommended: yes (PRs target `dev`, as in Xake); the 1.x readme
   then disappears from the landing page at the S4 merge, which is the intent.
7. **Release notes of the new repo.** Recommended: start fresh with the 4.0.0 entry and a link to
   Xake's notes for everything up to 3.15 / hermetic 0.10; do not copy the old entries.

## 8. Risks

- **Preview not indexed yet**: the S4 build cannot restore `Xake 4.0.0-preview.1` until nuget.org
  lists it (step 2). A defect in it that blocks the scaffold is fixed by `preview.2`, never by
  re-publishing.
- **Packing two assemblies**: the hermetic dll must be built before `pack` of `src/dotnet` (no
  project reference, cycle); the nuspec check of §3.1 catches a missing or extra assembly.
- **The 0.10 bootstrap of `build.fsc.fsx` on a prerelease package**: `Xake.Hermetic.Dotnet` 0.10.0
  on `Xake 3.15` must import and lock a description whose `Xake` is a package
  `4.0.0-preview.1`; untested so far. If it fails, the fallback is to keep `build-fsc` off in S4
  and turn it on at S6, recorded as such.
- **Squash by mistake** at step 14 loses the history; recover by reverting and re-merging the
  branch (it is still on the remote) - the owner must pick "Create a merge commit".
- **Path list drift before S6**: a file added under a new path in Xake between S4 and S6 (e.g. a
  new test helper) is outside the filter; §5.4 says how to take it without breaking SHA continuity.
- **Imported docs/sample links** to engine docs break silently; step 12 checks them.
- **Two copies of the moving code** live in both repos until S7; a fix landing in Xake between S4
  and S6 reaches the new repo through S6, not by hand.
