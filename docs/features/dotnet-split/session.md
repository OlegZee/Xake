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

## Next step

The note is revised for one package; the next step is the owner's approval of the note. Then S1,
the seam PR on `dev` (branch `feature/dotnet-seam`), independent of the pattern module.
