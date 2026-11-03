# B2.1 report: batch restore (G15) and the direct-route closure helper

Branch `pr/batch-restore` (worktree `.claude/worktrees/pr/batch-restore`), stacked on `pr/lock-fidelity` 9bf45d6.
3 commits 20088f9, c6be72c, c2b5eb2, not pushed. No `src/core` change, lock format unchanged, no NuGet.Client.

## Design

- **Key and cache.** A restore is keyed by (framework, package folder, package set sorted by id/version/private):
  folder `obj/xake/restore/<fwk>-<sha256[..16]>/restore.csproj` (was `obj/xake/packages/<name>/<fwk>/`).
  The `Restored` is cached **per build** (a `ConditionalWeakTable` on `ctx.Engine`, i.e. one engine run),
  not per process: test processes and several `xake` runs in one fsi do not share stale results
  (a floating version re-resolves on the next run). A per-key `Resource` makes two parallel misses run one restore.
- **Batch.** `Described.prefetch [(fwk, packages)]`: writes each missing key's project, then ONE
  `dotnet restore obj/xake/restore/batch-<hash>.proj`, a plain project
  (`<Project><ItemGroup><ProjectReference .../></ItemGroup><Import Project="$(MSBuildToolsPath)/Microsoft.Common.targets"/></Project>`).
  Checked on SDK 10.0.401 and 8.0.425: each referenced graph is resolved on its own (no NU1201 across TFMs,
  no version unification), in one process. Not the suggested SDK `all.proj`: as an SDK project it
  would check TFM compatibility and merge the graphs. Batch failure: nothing cached, one warning,
  each target restores alone and reports its own error.
- **Fallback.** A target reached without a batch (or with a set the batch did not predict) restores alone, as before.

## API

- `Described.restoreAll descriptionOf (descriptions: string list) (frameworks: string list)`: direct route,
  the `restoreWith` sets (`[]` frameworks = each description's own).
- `Locks.restoreAll (descriptions: Fileset) cfg lockOf descriptionOf`: the sets `generate` will restore,
  computed by the same function `generate` now uses (`restoreFor`: kept versions, `LOCK_REFRESH`, refs'
  Direct packages from their locks; before a ref has a lock, from its description at the exact declared
  version, recursively). Unpredictable sets (a floating version in a lock-less ref) are skipped.
- `Described.prefetch`, `Described.restoreInput d fwk` (pure: what `restore` restores), `Described.restoreProcesses ()` (counter).
- `Described.closure descriptionOf d`: transitive projectRefs, dependencies first, each once; a cycle fails
  naming it. Chosen over making `project` add dlls: `project` cannot know the layout (`dllOf`) and the script
  must `need` them anyway; the trial project direct script changes by 2 lines (`let! refs = ...`, `refs` instead of `d.ProjectRefs`).
- `Locks.needAll` now `Lock.load`s every lock and hands `Restore.ensure` the union request before `need`:
  a fresh package folder is filled by one restore instead of one per compile; nothing missing = `File.Exists` per path.

## Numbers (self-build, `Locks.generate` x 3 descriptions x 2 TFMs, scratch script `~/.claude/jobs/ca6a2625/tmp/b21/measure-*.fsx`)

| run | before (9bf45d6): restores / build / wall | after: restores / build / wall |
|---|---|---|
| no locks, cold `obj` | 6 / 2.30 s / 2.85 s | 1 / 0.88 s / 1.43 s |
| locks present, warm `obj` | 6 / 2.48 s / 3.04 s | 1 / 0.96 s / 1.51 s |
| locks present, cold `obj` | 6 / 2.41 s / 2.98 s | 1 / 0.95 s / 1.50 s |

Before = 6 by construction (one `restore` per description x TFM, all have packages); after = the counter.
Generated locks are byte-identical between the two (`diff -r`). the trial project not measured (another agent).

## Script changes

- `update-locks`: `command "update-locks" { do! Locks.restoreAll descriptions "Release" lockOf descriptionOf; do! need [ ... ] }`
  (cheatsheet, lock.md updated). `build.fsc.fsx` not changed: it `#r`s the published 0.3.0.28.
- Direct the trial project: `"main" => recipe { do! Described.restoreAll descOf [ for n in names -> descOf n ] fwks; do! need [...] }`;
  in the dll rule `let! refs = Described.closure descOf d` and `refs` for `need`/`ref` (csc-syntax.md has the shape).
- `build` via `Locks.needAll`: no change.

## Build and tests (verbatim)

- `dotnet build src/hermetic --no-incremental`: `0 Warning(s)` `0 Error(s)` (netstandard2.0 + net462)
- `Passed!  - Failed:     0, Passed:   222, Skipped:     0, Total:   222, Duration: 1 m 15 s - hermetic.tests.dll (net8.0)`
- New: same set in two parallel targets -> 1 restore; `Described.restoreAll` 3 descriptions x 2 TFMs -> 1 process,
  0 afterwards, results equal to restoring alone; `closure` on A->B->C (+A->C, D->A) and a cycle; `Locks.restoreAll`
  on BrA->BrB->BrC x 2 TFMs -> 1 restore on first generation and on regeneration, locks identical to the plain run.

## Risks

- One `dotnet restore` process still runs per build even when every set is up to date (msbuild no-op, ~0.5 s).
- One bad package fails the whole batch -> every target restores alone (old cost) plus a warning.
- `Locks.restoreAll` reads current locks without depending on them (as `generate` does); stale prediction = cache miss only.
- `needAll` now depends on every lock file (it is phony; harmless) and reads them once more.
- Old `obj/xake/packages/` folders stay as garbage in existing checkouts.

## Questions

1. Cache per build, not per process as the plan said: OK?
2. Move the self-build's `update-locks` to `Locks.restoreAll` when 0.3.x with it is published?
3. `Described.restoreAll` also for plain `restore` sets (scripts not using `restoreWith`), or keep it `restoreWith`-only?
