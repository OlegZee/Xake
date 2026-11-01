# Session: dotnet split (`feature/dotnet-split`)

## Goal

At Xake 4.0, `Xake.Dotnet` and `Xake.Hermetic.Dotnet` leave the Xake repository and the `Xake`
package for the repository `OlegZee/Xake.Dotnet` (release-process decision D, steps D1-D3). Design:
[brief.md](brief.md).

## PR, branch, worktree

| PR | Repo | Branch | Worktree | Status |
|----|------|--------|----------|--------|
| — D1 design note | Xake | `feature/dotnet-split` (from `dev`) | `.claude/worktrees/feature/dotnet-split` | brief written, not pushed |

## State

- 2026-11-01: D1 brief written. Facts established: the seam is exactly three engine internals
  (`ProcessExec.pexecSync` x5, `CommonLib.memoize` x2, `ExecCore.locateRule` x1), found by building
  a scratch copy of `src/core` + `src/dotnet` without the IVT (both TFMs); hermetic uses public
  engine API only. The old `Xake.Dotnet` repo has 7 commits from 2018 (Xake 1.1, FAKE/Travis) and
  owns the nuget id `Xake.Dotnet` (1.1.x alpha/beta). Pattern-module Phase 4 keeps every public
  signature hermetic uses (D16, C8) but changes the meaning of `dir/**` and `(a|b)`; those fixes
  happen in-repo before the split (brief §9, S5).

## Next step

The owner answers the open questions in brief §11 (nine, each with a recommendation). Then S1
(the seam PR on `dev`, independent of the pattern module) can start.
