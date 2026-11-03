# Session: dotnet split (`feature/dotnet-split`)

## Goal

At Xake 4.0, `Xake.Dotnet` and `Xake.Hermetic.Dotnet` leave the Xake repository and the `Xake`
package for the repository `OlegZee/Xake.Dotnet`, shipped there as ONE package `Xake.Dotnet`
(release-process decision D, steps D1-D3; S-D2). Design:
[brief.md](brief.md).

## PR, branch, worktree

| PR | Repo | Branch | Worktree | Status |
|----|------|--------|----------|--------|
| — D1 design note | Xake | `feature/dotnet-split` (from `dev`) | `.claude/worktrees/feature/dotnet-split` | brief revised for one package, not pushed |
| — S1 seam | Xake | `feature/dotnet-seam (#122)` (from `dev`) | `.claude/worktrees/feature/dotnet-seam` | #122 open, CI running |
| — S4 plan | Xake | `docs/dotnet-split-s4-plan` (from `dev` af45852) | agent worktree | committed, not pushed |
| — S3b unbundle | Xake | `feature/xake-unbundle-dotnet` (from `dev` 0e804ba) | agent worktree | committed, not pushed |

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

## Next step

S1: refresh the fsc locks (`import`, `update-locks -d LOCK_REFRESH=Xake`, `build check -d NUGET_FETCH=on`)
the pin moved to 10.0.403 in #122 (as on feature/patterns2); PR #122 open against `dev`, CI running.
After the merge: S3 (`v4.0.0-preview.1`).
