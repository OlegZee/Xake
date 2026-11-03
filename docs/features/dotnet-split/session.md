# Session: dotnet split (`feature/dotnet-split`)

## Goal

At Xake 4.0, `Xake.Dotnet` and `Xake.Hermetic.Dotnet` leave the Xake repository and the `Xake`
package for the repository `OlegZee/Xake.Dotnet`, shipped there as ONE package `Xake.Dotnet`
(release-process decision D, steps D1-D3; S-D2). Design:
[brief.md](brief.md).

## PR, branch, worktree

| PR | Repo | Branch | Worktree | Status |
|----|------|--------|----------|--------|
| — D1 design note | Xake | `feature/dotnet-split` (from `dev`) | — | #121 merged |
| — S1 seam | Xake | `feature/dotnet-seam (#122)` (from `dev`) | — | #122 merged |
| — S4 plan | Xake | `docs/dotnet-split-s4-plan` (from `dev` af45852) | — | #126 merged |
| — S3b unbundle | Xake | `feature/xake-unbundle-dotnet` (from `dev` 0e804ba) | — | #129 merged |
| — S6 final import | Xake.Dotnet | `feature/split-import` (tip e5fe8b7) | `~/projects/Xake.Dotnet/.claude/worktrees/feature/split-import` | PR #1 merged (6535bf8) |
| — S7 removal | Xake | `feature/dotnet-split-remove` (from `dev` f550556) | agent worktree | local, not pushed |

## State

- 2026-11-01: D1 brief written. Facts established: the seam is exactly three engine internals
  (`ProcessExec.pexecSync` x5, `CommonLib.memoize` x2, `ExecCore.locateRule` x1), found by building
  a scratch copy of `src/core` + `src/dotnet` without the IVT (both TFMs); hermetic uses public
  engine API only. The old `Xake.Dotnet` repo has 7 commits from 2018 (Xake 1.1, FAKE/Travis) and
  owns the nuget id `Xake.Dotnet` (1.1.x alpha/beta). Pattern-module Phase 4 keeps every public
  signature hermetic uses (D16, C8) but changes the meaning of `dir/**` and `(a|b)`; those fixes
  happen in-repo before the split (brief §9, S5).

- 2026-11-01: the owner decided S-D1 (questions 1-4, 6-9 as recommended) and S-D2 (one package
  `Xake.Dotnet` with both assemblies, one version line from 4.0.0, one tag prefix `v*`,
  `Xake.Hermetic.Dotnet` ends at 0.10.0 and is deprecated/unlisted, rule B void, hermetic marked
  "API may change"). The note is revised accordingly: package layout, versioning, build, CI,
  sequencing S1-S9, risks; §11 is now a "Decided" list with one small open point (breaking
  changes of the hermetic part under one version line).

- 2026-11-02: S1 on `feature/dotnet-seam`. `ProcessExec` public (`RequireQualifiedAccess`,
  names kept); `hasRule : string -> Recipe<ExecContext, bool>` in `ScriptFuncs` (resolved like
  `need`, records no dependency), `CompilerRunner` uses it; `DotNetFwk` caches with
  `ConcurrentDictionary<_, Lazy<_>>` (a failed computation is dropped, as with `memoize`; lookups
  for different keys are no longer serialized by one lock); IVT `Xake.Dotnet` removed, no other
  internal was in use (src/dotnet and src/hermetic build both TFMs with 0 warnings); hermetic
  `Lock.fs` on `ExecContext.withLogger` (B4). The core description and the hermetic.tests fixture
  lose `Xake.Dotnet` from `internalsVisibleTo`.
- Open in S1: the fsc locks are not refreshed. `update-locks -d LOCK_REFRESH=Xake` on this machine
  (only SDK 10.0.403 installed) moves the compiler in all locks from 10.0.402 to 10.0.403, while
  `build.yml` pins 10.0.402; reverted. Without the refresh `build.fsc.fsx check` fails (the core
  description and the `Xake` lock changed with the IVT). Needs either a machine with SDK 10.0.402
  or a decision to move the pin to 10.0.403 (locks + `build.yml` in the same PR).

- 2026-11-03: S4 plan written ([s4-plan.md](s4-plan.md)). Facts: `v4.0.0-preview.1` tagged at
  af45852 but not yet in the nuget.org index; the local `legacy-1.x` tag points to the stale
  branch's tip ba4acca and is not pushed (S2 half done; the remote branch is already gone);
  `OlegZee/Xake.Dotnet` has default branch `master`, no PRs, issues off. Rehearsal (scratch clones,
  `fast-export` + a Python path filter, `filter-repo` not installed): 617 -> 155 commits, 134 files,
  identical blobs; old repo + removal + merge = 164 commits, 1.03 MiB pack; deterministic, and an
  older cut is an ancestor of a newer one, so S6 is a normal merge on top of S4. Seven open
  questions for the owner in §7 of the plan.

- 2026-11-03: S3b (S-D14) on `feature/xake-unbundle-dotnet`. `Xake` is packed from `src/core`
  (`PackageId Xake`, the metadata, icon and readme moved there from `src/dotnet`); `src/dotnet`
  is `IsPackable=false`, `PackageId Xake.Dotnet`, `IncludeCoreOutput` gone. The nupkg holds
  `lib/{net462,netstandard2.0}/Xake.dll/.xml`, `Icon.png`, `readme.md`; dependencies FSharp.Core
  8.0.100 (+ System.ValueTuple on net462). `build.fsx`: `pack` packs only `Xake` and checks the
  nupkg (`checkXakePackage`: Xake.dll/.xml per TFM, nothing named `Xake.Dotnet.*`);
  `pack-hermetic`, `push-hermetic`, `checkXakeDependency` removed (the hermetic nupkg would now
  depend on a non-published `Xake.Dotnet`, and Xake.Hermetic.Dotnet ended at 0.10.0, S-D2);
  `publish.yml` refuses `hermetic-v*` for good. Publish race (preview.1 run, MSB4018
  GenerateDepsFile on `src/core/bin/Release/netstandard2.0/Xake.deps.json` in
  `[test-build tests]`): `test-build::tests` and `test-build::hermetic` ran `dotnet build` in
  parallel, both building src/core through project references; every msbuild rule now takes a
  one-unit `msbuild` resource (`oneMsbuild`), the test jobs stay parallel. `build test pack`
  85 s vs 93 s before. Descriptions re-imported (core gains `package`, dotnet loses it); locks
  unchanged; `build check` green. Readme (packed into `Xake`) still shows `open Xake.Dotnet`.

- 2026-11-03: S3b merged (#129), `v4.0.0-preview.2` published from ebfab1e. The S4 scaffold in
  `~/projects/Xake.Dotnet` (local `feature/split-scaffold`, tip 0422f3d) is rebuilt on preview.2,
  not pushed. S5 passed: `feature/patterns2` merged into `dev` as #131 (fe842d5).

- 2026-11-03: S4 done (the scaffold is on Xake.Dotnet `dev`, 0422f3d, on preview.2). S6 done:
  Xake.Dotnet PR #1 (`feature/split-import`, e5fe8b7), merged with a merge commit (6535bf8), carries `src/dotnet`,
  `src/hermetic`, their tests, docs and samples on Xake `4.0.0-preview.3`.

- 2026-11-03: S7 on `feature/dotnet-split-remove`. Removed what the import filter
  (`tools/import-from-xake.py` in Xake.Dotnet) took: `src/dotnet`, `src/hermetic`,
  `src/hermetic.tests`, 13 test files of `src/tests` and `fixtures/description`, the fsc/locks
  self-build (`build.fsc.fsx`, `locks/` including `locks/Xake`, `xake.fingerprints`,
  `src/core/xake.project.json`), the moved docs and samples. Kept in Xake although the filter
  copied them: `testjobs.fsx` (trimmed to `tests/jobs` = "Delegated and resource tests" and
  `tests/rest`), `docs/fingerprints.md` (the `Fingerprints` module is engine; its wiring example
  now names Xake.Dotnet), the engine halves of `LibraryVarsTests` (pr2-69..75, fixture renamed
  "Var read conformance") and `RecordShapeTests` (`ExecContext`, `withLogger`; "frozen within
  Xake 4"), `Common.fs`/`XakeTestBase.fs` (trimmed: no `PackageRoot`, `BuildPackageRoot`,
  `XAKE_CSC_SERVER` switch). `build.fsx`: one library, no `HermeticVersion`; the pack check
  refuses anything but `Xake.dll`/`.xml` under `lib/`. `build.yml`: `build-fsc` gone;
  `publish.yml`: `v*` only. Samples: `gettingstarted.fsx` and `features.fsx` engine-only,
  `book/intro.fsx` on `#r "nuget: Xake.Dotnet"`. Docs point to OlegZee/Xake.Dotnet; the
  `docs/tasks.md` .NET reference (csc/fsc operations, script variables, package folder) is
  replaced by a pointer, and `roadmap.md` keeps the Xake.Dotnet/hermetic backlog as the record.

## Next step

1. Owner: review and merge the S7 PR (`feature/dotnet-split-remove` -> `dev`).
2. Xake.Dotnet follow-up: it links to Xake's `docs/tasks.md#csc` (and other .NET anchors) for
   the task reference, which S7 replaced by a pointer; that reference (Xake `docs/tasks.md` at
   f550556, section ".NET tasks") belongs in Xake.Dotnet's docs. Also its `docs/session.md`
   should take the "Xake.Dotnet facts" section of Xake's `docs/session.md` and its roadmap the
   Xake.Dotnet/hermetic sections of Xake's `docs/roadmap.md` (then Xake drops them, S9).
3. S8 (release), S9 (follow-ups).
