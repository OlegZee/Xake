# Critic: rounds B1 and B2.1 (`pr/batch-restore`, diff b165cb4...c2b5eb2)

Read-only review. Builds and tests run in the worktree on c2b5eb2:

- `dotnet build src/hermetic --no-incremental`: 0 Warning(s), 0 Error(s)
- `dotnet build src/dotnet --no-incremental`: 0 Warning(s), 0 Error(s)
- `Passed!  - Failed: 0, Passed: 222, Skipped: 0, Total: 222` (hermetic.tests, net8.0)
- `Passed!  - Failed: 0, Passed: 324, Skipped: 1, Total: 325` (tests, net8.0)

Constraints hold: no `src/core` change, the lock format is unchanged (only values change), packages are still
resolved by msbuild, and nothing uses NuGet.Client.

## Verdicts

| change | verdict | grounds |
|---|---|---|
| G5 transitive refs (c166650) | SHIP | `Locks.fs:174` takes B's unhashed refs from B's lock. A's lock rule needs B's lock rule first, so B's lock is fresh. Output is the direct refs, then the transitive ones, then `List.distinct`: order is stable and a diamond's D appears once. There is no private-projectRef concept, so nothing leaks. |
| G14 `restoreWith` / `flowingPackages` | SHIP (nit 1) | `seen` stops cycles. Private packages are excluded, and an id A declares wins. Declared versions on the direct route vs locked versions on the lock route is msbuild's own behavior (PackageReference flows by range), so the difference is acceptable. |
| `Described.closure` | SHIP | Depth first with a path stack: a cycle fails and is named, and each project appears once after its references. Tested (A->B->C, A->C, D->A, cycle). |
| G6 frameworkAssemblies (3375a5e) | SHIP (nit 2) | NETFRAMEWORK only (`Described.fs` packageFrameworkRefs). Goes through `GlobalRefs`, resolved like the implicit refs, i.e. from `$(NuGetPackageRoot)/microsoft.netframework.referenceassemblies.*` and hashed (see the self-build locks), so no new HERMETIC exposure. `List.distinct` drops duplicates with the implicit/explicit refs. |
| G7 Directory = project folder (f7c3be3) | SHIP | Every other path in the lock is absolute (`$(ProjectRoot)/...`: Sources masks, Generated keys, `obj/xake` resx, keyfile, `--doc`, `--pathmap`), and `Directory` is used only as the runner's `workdir`. Old locks (`"Directory": "$(ProjectRoot)"`) build as before, and `Lock.diff` ignores the field. Golden test green. |
| G12 `.xml` rule (d1a27dd) | SHIP | `docOf` only checks the lock asks for doc, `needFiles` the dll, and checks the file exists. It does not compile, so there is no double compile and no race. The reason it is not `targets [dll; xml]` (endless rebuild for a no-doc lock) is correct. |
| runner `Resx.refresh` (2f80ca3) | SHIP | Runs only when the engine reruns the compile, not on a no-op build. In-memory compile, written only when the bytes differ, so mtimes stay stable. The `.resources` path is per assembly/fwk/config (`resxDir`), so parallel targets do not share one. |
| Generated keeps `\` (9bf45d6) | SHIP | Keys still go through `str`; only values use `tokenizeText` (`Lock.fs:479`). |
| B2.1 per-build cache + key | SHIP | Key = fwk + sha256(package folder + sorted project text with versions and PrivateAssets). Kept versions and `LOCK_REFRESH` are already in the versions `restoreFor` emits, so different kept versions give different keys. `ConditionalWeakTable` on `ctx.Engine` (one `EngineState` per `createContextCore`) gives one cache per build; it is not static state, so it is safe for tests and for two builds in one fsi. The per-key `Resource` double-checks the cache. 64-bit key: no realistic collision. |
| B2.1 batch project / fallback | SHIP (nit 3) | The same `-p:` globals propagate to the referenced projects, and each project resolves `nuget.config`/source mapping from its own folder, which has the same ancestors as before. CPM stays off as before. On failure nothing is cached and each target restores alone. |
| `Locks.restoreAll` | SHIP | Uses the same `restoreFor` as `generate`; a wrong prediction is only a cache miss. Tested: same locks, one restore. |
| `Locks.needAll` prefetch (c6be72c) | SHIP WITH FIX | See fix 1. NUGET_FETCH is honored (`Restore.ensure` -> `resolve`, off = warning). |

## Required fixes

1. `src/hermetic/Locks.fs:545`: the union `Restore.ensure` throws when the one `dotnet restore` fails
   (`DotNetFwk.downloadPackages` -> `failwith restoreFailure`). For example, one unreachable feed or one bad
   version in one lock now fails the whole `build` before any compile, with a message listing every package. Under
   `-k`, projects that do not need the bad package no longer build. Before this change each compile failed alone,
   with its own entry's packages. Fix: wrap the prefetch in try/with, and on failure trace a warning ("prefetch
   failed; each compile restores its own"), then `need targets`. The failed packages are not memoized
   (`restored` is written after a successful download), so each compile retries and reports its own error.
   Add a test: two locks, one naming a missing version; the other project still builds with `-k`.

## Nits (ship as is, or fix when convenient)

1. `Described.fs:442` `flowingPackages` uses `itemsFor r rf`, not `restoreInput`: B's implicit packages
   (`System.ValueTuple` for F# on net40-net47) do not flow on the direct route. They do flow on the lock route (they
   are `Direct` in B's lock) and in msbuild. Pre-existing and the opposite case: the lock route flows B's `Private`
   packages (`Locks.fs` `filter Direct`), because the lock has no private flag.
2. `Described.fs:765` `List.distinct` is case-sensitive (`windowsbase` vs `WindowsBase` would give two refs).
   A `frameworkAssemblies` name missing from the reference pack probably fails resolution, where msbuild only warns
   (MSB3245). Not seen in practice; worth one line in the docs.
3. The direct route's `"main"` now runs one `dotnet restore` on every build, including a no-op build (~0.5 s, risk
   listed by the author). Optional: skip a key whose `restore.csproj` is unchanged and whose `obj/project.assets.json`
   exists (msbuild's no-op restore would not re-resolve a floating version either).
4. `prefetch` does not take the per-key `Resource`. A `restoreAll` running in parallel with a target restoring the
   same key would run two msbuilds on one `obj`. The documented call order (before `need`) avoids this.
5. `needAll` fetches before `compileEntry`'s prerequisite and HERMETIC gates, so an entry that is refused later has
   still been fetched. This is harmless (package folder only, hash-checked), but the `compileFrom` doc order no
   longer describes the build.
6. The G5 filter `Sha256 = ""` stands in for "project output". Every unhashed reference generate writes is one
   today, but a hand-edited or imported B lock would leak its unhashed paths into A. Optional: also require the path
   to be under `$(ProjectRoot)`.
7. No diamond test for G5 (the code is right by `List.distinct`). Old `obj/xake/packages/` is left behind.
   `obj/xake/restore/` is shared with DotNetFwk's numeric `<n>/` folders (no name clash). `Restored` and
   `Nuget.Assets` gained a field (record construction by consumers breaks; 0.x package).
8. `Described.restore` (generation) still ignores `NUGET_FETCH=off`. This is pre-existing, not introduced here.

## One-time lock rewrite on regeneration (tell the user)

- `"Directory"` changes from `$(ProjectRoot)` to `$(ProjectRoot)/<project folder>` in every entry (self-build:
  `src/core`, `src/dotnet`, `src/hermetic`). `Lock.diff` ignores it, so committed locks keep building.
- Referencing projects gain their transitive project outputs in `References` (unhashed). The self-build has no
  change: `Xake.Hermetic.Dotnet`'s lock has no projectRefs and `Xake.Dotnet -> Xake` is one level.
- net4x entries whose packages declare `frameworkAssemblies` gain those refs (e.g. `WindowsBase.dll`, hashed).
- A `Generated` value holding `\` keeps it. An old lock where it became `/` now differs from the resolved
  compilation, so a `csc { lock }` gate reports a drift until the lock is regenerated (self-build: none).
- The nowarn line comes from A1, not B.
