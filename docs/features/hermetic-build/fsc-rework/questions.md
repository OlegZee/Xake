# Questions raised by the implementation subagents (2026-10-11)

Collected from `pr<N>-report.md`. None is decided; the code follows the subagent's provisional
choice named here. Answers go into `interview.md` and the PR in question.

## PR 1, description model (#44)
1. `Description.find` depends only on the found file, not on the fileset; a duplicate added later is unnoticed until the recipe reruns. Depend on the fileset too?
2. Writer format: a value inline if it fits 100 columns, else one item per line. Acceptable as canonical?
3. Two cross-field checks (`byFramework` keys in `frameworks`, `bundle` entries in `projectRefs`) absent from the draft: keep?

## PR 5, import (#45)
1. C# mask convention: `!` prefix = exclude (in order); msbuild `dir/**` written as `dir/**/*`; resources as a file list. (PR 2 follows it.) Accept?
2. `Import.descriptions` (not `Description.import`; compile order) returning a list, so `let! _ =` in the script. Fine?
3. `bundle` inferred from project refs with `PrivateAssets=all`: keep, or leave bundling to the script?
4. Every packable project gets a `package` block (msbuild default for libraries): keep, or require an explicit `PackageId`?

## PR 2, `fsc/csc { project }` (#46)
1. `project "path"` takes the description file path, not a loaded record (relative paths resolve from its folder). Acceptable?
2. Keep `RELEASE` in the Release defines, as the SDK does?
3. SDK implicits (.NET Framework assemblies, `netstandard.dll`, `System.ValueTuple`) added by `project`/`restore`, or carried by the description? (PR 5's import drops them.)
4. Mask base = folder of the first include (PR 5 expands against the project folder): unify?
5. `.resx` resources ("not yet") and assembly versioning: a later PR, or with the ActiveReports experiment?
Deviation to note: packages restored via `dotnet restore` of a generated PackageReference project, not `Restore.download` (PackageDownload gives no graph / compile assets).

## PR 3, lock generation (#48), after the rework to a phony rule (rev. 2)
Superseded by the rework: q1 (`Locks` module stays, `generateAll` gone), q2 partly (see new q2).
New:
1. `Evaluation.ProjectRefs` now holds the referenced project's name, not its project file (the primitive has no `descriptionOf`): acceptable, or add a `descriptionOf` parameter?
2. With the canonical rule a ref's lock in the same configuration always exists, so the Release/Debug fallback in `refConfig` only matters for refs asked in another configuration: keep or drop?
3. Should the `lock::` rule record a fingerprint line (`Fingerprints.record`) itself, or is that PR 7's wiring?
Original rev. 1 questions kept for reference:
1. `Locks.generateAll` instead of `Lock.generateAll` (compile order); PR 4 put `compileAs`/`needAll` there too. OK?
2. Ref configuration fallback fixed as: A's config, Release, Debug. Keep fixed rather than a script option?
3. A bare name in `ProjectRefs` when B is not described in the run: fine?
4. B's direct packages flow into A's restore at B's locked version (no Private flag in the lock): keep?
5. `--doc:` left in Options at the layout path (PR 4 re-targets it at build): strip like `-o`?

## PR 6, fingerprints library (#47)
1. Tab-separated fields (gaps.md said spaces): acceptable?
2. Missing `xake.fingerprints` is treated as empty: should `check` fail instead?
3. Should `check` fail when a described project has no fingerprint line? (Only testable after wiring.)

## PR 4, build from lock (#49)
1. `versions` holds `RequiredVar<string>` (not plain strings) so a version change rebuilds. OK?
2. "Project path = folder of the first source" for matching `versions` masks: acceptable?
3. net462 facade expansion (`System.IO.Compression` had to be added to the hermetic fixture's `byFramework.net462.references`): fix in `Described`, or keep in descriptions?
4. The rule's `name` capture is the lock name, assuming lock name = description name = assembly name: fine for now?

## PR 7, self-build `build.fsc.fsx` (#50)
1. Bootstrap: the script `#r`s `build.fsx`'s `out/netstandard2.0/*.dll` (no release carries `Locks`/`Import`/`Fingerprints`), so a fresh clone runs msbuild once for the script's own libraries and both scripts share `.xake`. Keep until a release, or use the local feed (Q6 of the interview)?
2. The locks hash the SDK's `fsc.dll`, so they hold for SDK 10.0.401 on macOS arm64 only (hence the macOS CI job). Keep, take the compiler from a package, or relax the compiler hash?
3. `Fingerprints.record` is not thread-safe (lock rules run one at a time) and `warn` is hidden at the default console level (script sets `consolelog Loud`). Fix both in `src/core`?
4. `HERMETIC=on` is off for the self-build: it needs an exact SDK pin in `global.json` (affects the 8.0 CI leg). Pin or leave off?
5. Hermetic's lock references the published `Xake` 3.6.0.24 package, not the Xake built in `out/Release`. Fine for now? (brief §6 "hermetic's reference to Xake")
6. Rebuilds in the same folder give identical dlls; a clone in another folder differs by 59 bytes (PE checksum, debug-directory stamps; pdbs identical). Chase before PR 8's nupkg-equality acceptance?
Also: `clean` removes `out/Release` only; `-d FILTER=` does not reach `TestFilter` (pre-existing in build.fsx); no `src/Directory.Build.props` exists, the stale-warning proof edited `src/core/Xake.fsproj`.

### PR 7 rev. 2 (after the releases)
1. A locked package version that falls outside the description's new range is kept until `LOCK_REFRESH` names it (the hermetic lock kept Xake 3.6.0.24 under `[3.7.0.26, 3.8)` until `-d LOCK_REFRESH=Xake`). Drop it automatically when out of range? (library change)
2. Run the `lock::` rules in parallel now that `Fingerprints.record` is thread-safe (#51)?
3. CI SDK: `setup-dotnet` 10.0.401 on ubuntu; a newer preinstalled 10.0.x could be picked by `latestMajor` roll-forward. Pin tighter (exact `global.json`, which is also the HERMETIC question)?
