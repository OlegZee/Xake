# Brief: moving `Xake.Dotnet` and the hermetic package out of Xake (D1)

Status: **design note for a decision**, 2026-11-01. Step D1 of the release-process plan
([../release-process/tracker.md](../release-process/tracker.md), rows D0-D3; the reasoning for D is in
[../release-process/review.md](../release-process/review.md) §4 D and §6). Nothing here is decided until
the owner answers the open questions at the end; the facts are reproducible from the files named.

Sources read: `docs/session.md`, `docs/devprocess.md`, release-process `review.md`/`tracker.md`/
`session.md`, pattern-module `brief.md` (D1-D39), `tracker.md`, `spec.md` §6.1, §8.1, §10.2, §13 (C3,
C8); the code of `src/core`, `src/dotnet`, `src/hermetic`, `src/hermetic.tests`, `src/tests`,
`build.fsx`, `build.fsc.fsx`, `testjobs.fsx`, `.github/workflows/*`; the repository
`OlegZee/Xake.Dotnet`; the nuget.org indexes of `Xake`, `Xake.Dotnet`, `Xake.Hermetic.Dotnet`.

## 1. Goal and non-goals

**Goal.** At Xake 4.0 the `Xake` package carries the engine only (`Xake.dll`). The .NET tasks
(`Xake.Dotnet`: `csc`, `fsc`, `msbuild`, `resgen`, project descriptions, toolchain discovery) and
the hermetic package (`Xake.Hermetic.Dotnet`: locks, restore, SBOM, verify, pack, sign) live in the
repository `OlegZee/Xake.Dotnet`, build on the *published* `Xake` package, and are released from
there. The engine reaches them only through public API: `InternalsVisibleTo("Xake.Dotnet")` is
removed from `src/core/Xake.fsproj`.

Why (review §3): `src/dotnet` changed in 10 of 13 releases and every API hermetic needed came from
`src/dotnet`, never from the engine; the coupling is `src/dotnet` <-> hermetic. After the split an
engine release touches nothing .NET, and a .NET change needs no engine release.

**Non-goals.**

- No meta-package. 4.0 is breaking anyway (vars variant 3, pattern module); review §6 already
  drops the `Xake.Core` + meta-`Xake` variant when D ships in 4.0.
- No change to namespaces or public API of `Xake.Dotnet` / `Xake.Hermetic.Dotnet` beyond what the
  seam needs (§3). Scripts keep `open Xake.Dotnet`.
- No new features; no rework of the descriptions/locks format.
- The typed layer (pattern-module Phase 5) and anything else of 4.0 is not part of this work.
- Not the wiki rewrite; only the links and `#r` lines that the split breaks.

## 2. What moves and what stays

Layout rule: the new repository keeps Xake's relative paths (`src/dotnet`, `src/hermetic`,
`src/hermetic.tests`, `docs/...`), so the imported history needs no renames, and the descriptions
(`xake.project.json`), locks and docs links keep their paths. One new folder: `src/dotnet.tests`
for the tests that leave `src/tests`.

### 2.1 Moves (Xake path -> `Xake.Dotnet` path)

| Source in Xake | Destination | Notes |
|---|---|---|
| `src/dotnet/*.fs` (20 files: `Tool`, `Hash`, `DotNetFwk`, `HermeticMode`, `ResourceFileset`, `ResxFormat`, `Resx`, `DotnetTasks`, `CscArgs`, `FscArgs`, `CompilerRunner`, `Json`, `Description`, `ProjectPlan`, `Csc`, `Fsc`, `Dotnet.csc`, `Dotnet.fsc`, `Dotnet.resgen`, `Dotnet.Msbuild`) | `src/dotnet/` | the three seam edits of §3 (`DotNetFwk.fs` x2, `CompilerRunner.fs` x1) |
| `src/dotnet/Xake.Dotnet.fsproj` | `src/dotnet/` | `PackageId` `Xake` -> `Xake.Dotnet`; the `IncludeCoreOutput` target and `XakePackageVersion` go; `ProjectReference ..\core` -> `PackageReference Xake` (range §4); IVT `tests` -> `dotnet.tests`; description/URLs point to the new repo |
| `src/dotnet/readme.md`, `src/dotnet/xake.project.json` | `src/dotnet/` | readme: `#r "nuget: Xake.Dotnet"`; the package readme becomes this file, not the root `readme.md` |
| `src/hermetic/*` (18 `.fs`, fsproj, readme, `xake.project.json`) | `src/hermetic/` | fsproj: the private `ProjectReference ..\core` goes (Xake comes as a `PackageReference`), `ProjectReference ..\dotnet` stays; `Lock.fs:1584` `{ ctx with Logger = ... }` -> `ExecContext.withLogger` (deferred B4) |
| `src/hermetic.tests/*` incl. `fixtures/` | `src/hermetic.tests/` | the linked `..\tests\Common.fs`, `..\tests\XakeTestBase.fs` point to the new repo's copies (`src/dotnet.tests/`) |
| `src/tests/{DotnetTasksTests, ToolsetTests, PackageRootTests, SdkProbeTests, CscServerTests, ResxTests, CompilerCrashTests, CompilerJobsTests, FscTests, PathSpacesTests, HermeticModeTests, DescriptionTests, ProjectPlanTests}.fs` (13 files, ~3,250 lines) and `src/tests/fixtures/description/` | `src/dotnet.tests/` | none of them uses an engine internal (checked: no `ExecCore`, `CommonLib`, `ProcessExec`, ... reference); some use `Xake.Dotnet` internals, so the IVT follows them. Ten carry `Category=Integration` (real csc/fsc) |
| `src/tests/LibraryVarsTests.fs` | split | the `8.8-46..54` and `pr2-76..83` tests (DotNetVars, `NUGET_PACKAGES`, `CSC_SERVER`, jobs) move; `pr2-69..75` (`Var.read`, engine only) stay |
| `src/tests/RecordShapeTests.fs` | split | the six compiler records (`CscSettingsType`, `RunOptions`, `Csc`, `FscSettingsType`, `FscRunOptions`, `Fsc`) move; `ExecContext` and the `withLogger` test stay |
| `src/tests/XakeTestBase.fs`, `src/tests/Common.fs` | copy, then trim | Xake's copy loses `PackageRoot`, `BuildPackageRoot` (calls `DotNetFwk.packageRootWithSource`) and the `XAKE_CSC_SERVER` runner switch; the new repo's copy keeps them |
| `testjobs.fsx` | split | Xake keeps `tests/jobs` (`Delegated and resource tests`) and `tests/rest`; `tests/server`, `Compiler jobs` and all `hermetic/*` jobs go |
| `build.fsc.fsx`, `locks/Xake.Dotnet/`, `locks/Xake.Hermetic.Dotnet/`, `xake.fingerprints` | repo root | recommended (Q4): the fsc/locks self-build moves; Xake drops it with `locks/Xake/` and `src/core/xake.project.json` |
| `docs/dotnet-build.md`, `docs/csc-server.md`, `docs/csc-syntax.md`, `docs/description.md`, `docs/hermetic/*`, `docs/hermetic-guide.md`, `docs/hermetic-architecture.md`, `docs/fingerprints.md` (as far as it describes descriptions/locks), `docs/schemas/xake.project.schema.json` | `docs/` | the descriptions point to `../../docs/schemas/...`, same relative path in the new repo |
| `samples/fullframework.fsx`, `samples/toolset.fsx`, `samples/hermetic/`, `samples/netcore-compile/`, `samples/dotnet-fake.csproj`, `samples/helloworld.cs` | `samples/` | `#r` the new repo's `out/` or the package |
| `Icon.png`, `LICENSE` | repo root | copied (both repos keep them) |
| (new) | `.github/workflows/build.yml`, `publish.yml`, `build.fsx`, `global.json`, `readme.md`, `docs/release-notes.md`, `docs/devprocess.md`, `CLAUDE.md`, `docs/session.md` | §6, §7 |

### 2.2 Stays in Xake, with edits

| File | Edit |
|---|---|
| `src/core/Xake.fsproj` | `IsPackable` true, `PackageId` `Xake`, the package metadata now on `src/dotnet` (authors, tags, license, icon, readme, repository URL) moves here; IVT `Xake.Dotnet` removed; the seam of §3 |
| `src/tests/tests.fsproj` | the `src/dotnet` reference and the 13 files go |
| `build.fsx` | one library, one package (`pack` = `dotnet pack src/core`); `HermeticVersion`, `pack-hermetic`, `push-hermetic`, `checkXakeDependency`, `hermeticVersionArgs` go |
| `.github/workflows/build.yml` | `build-fsc` job goes (Q4); the `build` job stays |
| `.github/workflows/publish.yml` | `v*` only; the `hermetic-v*` branch goes |
| `readme.md` (the `Xake` package readme) | the csc example becomes an engine example, plus one line "`.NET` compilers: package `Xake.Dotnet`" |
| `samples/gettingstarted.fsx`, `samples/features.fsx`, `samples/book/intro.fsx` | the csc parts take `#r "nuget: Xake.Dotnet"` or are replaced by engine-only steps |
| `docs/tasks.md`, `cheatsheet.md`, `index.md`, `variables.md`, `resources.md`, `roadmap.md`, `devprocess.md`, `CLAUDE.md`, `docs/session.md` | .NET sections become links to the new repo; packaging section rewritten |
| `docs/release-notes.md` | history stays; a 4.0.0 entry "`Xake.Dotnet` is a package of its own" with the script change |
| `docs/features/hermetic-build/`, `hermetic-windows/`, `release-process/` | stay as the record; new hermetic work gets feature folders in the new repo |

## 3. The seam: engine internals used by `Xake.Dotnet` and hermetic

Measured, not grepped only: a scratch copy of `src/core` + `src/dotnet` with the IVT to
`Xake.Dotnet` removed fails with exactly these errors (both TFMs); making the three modules public
builds both TFMs clean. `Xake.Hermetic.Dotnet` has no IVT from the engine and uses public API only
(its IVT is to `hermetic.tests`).

| Internal | Uses | Recommendation |
|---|---|---|
| `Xake.ProcessExec.pexecSync` (module `internal`; `pexec` is the async form `Tasks/Shell.fs` uses) | 5, all in `DotNetFwk.fs` (`pkg-config` x2, `dotnet --version`, `dotnet` restore-probe, `chmod`), inside memoized lookups outside any recipe | **make public**: the module `Xake.ProcessExec` with `pexec`, `pexecSync`, `drainTimeoutMs` as they are, `[<RequireQualifiedAccess>]` so `open Xake` adds nothing to scripts' scope. It is the "process plumbing" the review names as the engine contract; copying it would duplicate the drain-timeout logic (pipes held by a child process), which is the subtle part. Document it as low-level and stable within 4.x |
| `Xake.CommonLib.memoize` (module `internal`) | 2, `DotNetFwk.fs:1077` (`locateFrameworkMemo`), `:1376` (`lookupFile`) | **replace inside `Xake.Dotnet`** by the `ConcurrentDictionary<_, Lazy<_>>` cache `DotNetFwk.fs:510` already uses for the SDK probe. Nothing in `CommonLib` is engine contract; making it public would expose `take`, `unique`, `memoizeRec` too |
| `Xake.ExecCore.locateRule` (module `internal`) | 1, `CompilerRunner.fs:301`: "does any rule of the script produce this hashed file?" before `needFiles`, so missing inputs are all reported at once | **new public recipe function** in `ScriptFuncs` (auto-opened with `getCtxOptions`, `getRuleMatch`): `hasRule : string -> Recipe<ExecContext, bool>` (a path, resolved like `need`; true when a rule would build it). Its signature does not depend on the matcher, so it can land on `dev` now and survive pattern-module Phase 4 (which rewrites `explainMatch`/`locateRule` underneath) |
| `InternalsVisibleTo("Xake.Dotnet")` | — | **removed** in the same PR; the IVT to `tests` stays |
| `ExecContext` record copy in hermetic (`Lock.fs:1584`) | public, but a record copy outside `Xake.dll` breaks on a new field | not a seam item; switch to `ExecContext.withLogger` (B1 helper, 3.15) in the seam PR, closing B4 |

Not seam items, recorded because they are the remaining engine surface the two packages use
(all public, all kept in 4.0 by decision): recipes and `ExecContext`; the variable set (`Var.read`,
`varschema`); `Logging`; `Fileset`/`!!`/`Includes of PathMask`, `Path.parse`, `Path.matchesPattern`
(spec C8, D16); `Fingerprints` (build.fsc.fsx); `Tasks.Shell`. Pattern-module Phase 4 keeps all
those signatures, so the split does not wait for new pattern API; it waits for the 4.0 *meaning*
of patterns to land, because hermetic has two pattern adjustments that belong to Phase 4 (C3:
`(ext:dll|exe|nupkg)` -> `(ext:{dll,exe,nupkg})` in `Sign.fs` docs and `SignTests.fs`; C8/D12: the
`dir/**` -> `dir/**/*` workaround in `Import.fs:254`, `Lock.fs:360` vs the 4.0 meaning of `dir/**`
and XP113) and the compat corpus (D19) includes hermetic's direct uses.

## 4. Package layout

| Package | Repo | Contains (per TFM `net462`, `netstandard2.0`) | Dependencies |
|---|---|---|---|
| `Xake` 4.0.0 | Xake | `Xake.dll`, `Xake.xml` | `FSharp.Core` 8.0.100 |
| `Xake.Dotnet` 4.0.0 | Xake.Dotnet | `Xake.Dotnet.dll`, `.xml` | `Xake [4.0.0, 5.0)`, `FSharp.Core` 8.0.100, `System.Resources.Extensions` 8.0.0, `Microsoft.Win32.Registry` 5.0.0 (netstandard2.0 only); `System.Windows.Forms` framework reference on net462 |
| `Xake.Hermetic.Dotnet` 0.11.0 | Xake.Dotnet | `Xake.Hermetic.Dotnet.dll`, `.xml` | `Xake.Dotnet [<this build's Xake.Dotnet version>, 5.0)`, `Xake [4.0.0, 5.0)` (explicit: it calls the engine directly), `FSharp.Core` 8.0.100; `System.IO.Compression` reference on net462 |

- **Ranges:** release-process A (exact `X.Y.Z` versions, no run number) and B (upper bound = next
  major, guarded by the frozen-record rule and a CI leg against the newest published `Xake`). The
  floor of `Xake` is the version the repository builds against; it moves only when the code needs
  something newer (E, `bump-base`, §6).
- **What `Xake` stops shipping:** `Xake.Dotnet.dll`/`.xml`, the two `System.*`/`Microsoft.*`
  dependencies and the net462 `System.Windows.Forms` reference; its readme loses the csc example.
- **What a script changes:** add `#r "nuget: Xake.Dotnet"` (it brings `Xake`); `open Xake.Dotnet`
  is unchanged. A script that pins `#r "nuget: Xake, 4.x"` keeps the line to fix the engine
  version. Release-notes entry in Xake 4.0.0 and the first line of the new repo's readme.
- **FSharp.Core:** 8.0.100 pinned with `DisableImplicitFSharpCoreReference` in both new projects,
  as today; `global.json` `8.0.0` + `latestMajor` copied.
- **TFMs:** libraries `net462;netstandard2.0` (unchanged), tests `net8.0`.
- **The package id `Xake.Dotnet` exists:** nuget.org lists `1.1.0.2-alpha`, `1.1.0.4-alpha`,
  `1.1.4.7-beta` (2018, from the old repository). 4.0.0 sorts above them; unlist the three after
  4.0.0 is out (Q6).

## 5. Versioning of the new repository

Recommendation:

- **`Xake.Dotnet` starts at 4.0.0** and then versions on its own: its major follows the `Xake`
  major it targets (4.x on `Xake [4, 5)`), minors and patches are its own. Reasons: the assembly
  shipped as `Xake` 3.x until now, so 4.0.0 reads as the continuation; it sorts above the 2018
  betas; continuing hermetic's 0.x would mark a stable, widely used library as preview.
- **`Xake.Hermetic.Dotnet` continues 0.x: 0.11.0** at the split (already planned: "hermetic 0.11
  ships with 4.0", release-process session 2026-10-31). The 0.x preview signal stays.
- **Two tag prefixes in one repository, as Xake has today:** `v*` -> `Xake.Dotnet`,
  `hermetic-v*` -> `Xake.Hermetic.Dotnet`. Both tags may sit on one commit. The hermetic package
  depends on the `Xake.Dotnet` version of the same build (as #112 does with `Xake` today), so a
  hermetic release that needs new `Xake.Dotnet` API is tagged on the same commit as that
  `Xake.Dotnet` release; the hermetic publish run checks that this floor is on nuget.org (or fails
  with "tag `v<floor>` first"). This removes review §1's "release Xake first, local feed" for the
  6 of 7 cases where the API came from `src/dotnet`; only an engine change still means
  "release Xake first", which by the history happened once (0.5.0).

## 6. Build of the new repository

- **`build.fsx`** bootstraps on the published engine only: `#r "nuget: Xake, 4.0.0"` (the
  current Xake `build.fsx` uses `Xake` and `Xake.Tasks` only, and drives `dotnet build`/`pack`
  through `sh`). It is Xake's `build.fsx` with the library list `Xake.Dotnet`, `Xake.Hermetic.Dotnet`,
  the versions `Version` (Xake.Dotnet) and `HermeticVersion`, `pack` (both), `pack-hermetic`,
  `push`, `push-hermetic`, and the nuspec check rewritten: hermetic depends on `Xake.Dotnet` and
  `Xake`; `Xake.Dotnet` depends on `Xake`; neither carries `Xake.dll`.
- **`testjobs.fsx`**: the `dotnet` and `hermetic` slices (`dotnet/server`, `hermetic/server`
  serial; `dotnet/jobs`, `dotnet/rest`, `hermetic/record|run|lock|rest`).
- **The fsc/locks self-build moves too** (recommended, Q4): `build.fsc.fsx`, the two locks, the
  two descriptions and `xake.fingerprints` describe the two libraries of this repository; it
  bootstraps on the published `Xake`, `Xake.Dotnet` and `Xake.Hermetic.Dotnet` as today. The
  `Xake` description is no longer a project of it: the engine is a package reference in the
  descriptions, which exercises the importer's package path (and drops the `package.bundle:
  ["Xake"]` gap of the in-repo period). Xake keeps no locks.
- **`bump-base` comes back** in the new repo's `build.fsc.fsx` as release-process E1 had it
  (Xake version -> both fsproj ranges, `import`, refresh only this repo's locks), now for the
  `Xake` floor only. Runs only when the code needs a newer engine (B).
- **Developing against an unreleased engine**: Xake publishes previews (`v4.0.0-preview.N` tags,
  §7, Q3); the new repo references the preview until 4.0.0 is out. Day to day a local feed
  (`#i "nuget: /tmp/xake-feed"`, devprocess "Bootstrapping note") stays the escape hatch.

## 7. CI and publish workflows of the new repository

- `build.yml`, on push/PR to `dev`: job `build` (ubuntu-latest, SDK 10.0.x: `dotnet fsi build.fsx
  -- -- build test pack`); job `build-fsc` (SDK 10.0.403 pinned: `build.fsc.fsx build check -d
  NUGET_FETCH=on`); job `xake-latest`: restore against the newest published `Xake` 4.x (an
  overridable `XakeVersion` property, as B3's `hermetic-compat` did) and run the tests, so a new
  engine minor that breaks `Xake.Dotnet` is seen without a release. A windows-latest leg is
  optional (Q7).
- `publish.yml`, on tags: `v*` -> pack and push `Xake.Dotnet`; `hermetic-v*` -> check the
  `Xake.Dotnet` floor is published, pack and push `Xake.Hermetic.Dotnet`. The version is the tag
  (A); `--skip-duplicate`; secret `NUGET_API_KEY` (a new repository secret; the nuget.org key
  must be scoped to both ids, the `Xake.Dotnet` id is already owned).
- Xake side: `publish.yml` accepts `v4.0.0-preview.N` (the `v*` glob already matches; the version
  string is the tag minus `v`, valid SemVer), so previews need no workflow change, only a decision
  (Q3).
- Branches as in Xake: feature branches -> `dev` (squash) -> `master`, tags on `master`.

## 8. The old repository

`OlegZee/Xake.Dotnet`: 7 commits on `master` (2018-05-12 .. 2018-11-04, "extracted the source code
from Xake"), tags `v1.1.0`, `v1.1.4`, one stale branch `feature/refactoring-core` (1 commit,
2019-11-12). FAKE + paket + Travis, `dotnet 2.1.300`, `Xake 1.1.0.413-alpha`; `src/` holds 10 files
of the 1.x tasks (with their own `ProcessExec`/`CommonLib` copies). Nothing of the code is worth
keeping; the readme and build are obsolete; `LICENSE` (MIT) matches.

Recommendation: **reuse the repository and its remote** (the package id and old links point
there), **keep its history**, and replace the contents:

1. tag the old tip `legacy-1.x` (the `v1.1.*` tags stay), delete `feature/refactoring-core`;
2. extract the moving paths from Xake with history (`git filter-repo --path src/dotnet --path
   src/hermetic --path src/hermetic.tests --path docs/hermetic ...` on a fresh clone of Xake at
   the cut commit), so `blame` on the ~16,700 lines of `src/dotnet` + `src/hermetic` keeps working;
3. merge that into `master` with `--allow-unrelated-histories`, after one commit that deletes the
   2018 files; the merge commit names the Xake commit it was cut from;
4. add the moved test files (they enter `src/dotnet.tests` by `git mv` in the extracted history
   before the merge), the new build and CI files, and create `dev` from it.

Rejected: an orphan branch (loses the 2018 history for nothing); a new repository (the old one
would keep pointing users at 1.x); a squashed snapshot without history (loses blame on code
that is mostly recent).

## 9. Sequencing

Each step is one PR unless noted. "Before patterns" = can merge while `feature/patterns2` is not on
`dev`; the split itself waits for it (D0, owner 2026-10-29).

| # | Step | Repo / branch -> target | Depends on | Before patterns? |
|---|---|---|---|---|
| S1 | Seam: `ProcessExec` public, `hasRule`, `memoize` -> local cache, `CompilerRunner` on `hasRule`, IVT `Xake.Dotnet` removed, hermetic `Lock.fs:1584` -> `withLogger` (B4), docs/session.md engine facts | Xake `feature/dotnet-seam` -> `dev` | — | yes |
| S2 | Old repo prepared: tag `legacy-1.x`, delete the stale branch (owner, no PR) | Xake.Dotnet | — | yes |
| S3 | First preview `v4.0.0-preview.1` of `Xake` from `dev` (vars + S1), if Q3 is yes | Xake tag | S1 | yes |
| S4 | Scaffold of the new repo against `Xake 4.0.0-preview.1`: history import (§8) as a dry run from the S1 commit, `build.fsx`, `testjobs.fsx`, workflows, readme, CLAUDE.md; CI green. A rehearsal: it is redone at S6 from the final cut | Xake.Dotnet `feature/split-scaffold` -> `dev` | S3 | yes |
| S5 | Pattern-module Phase 4 including hermetic/`Xake.Dotnet` on the 4.0 pattern meaning (C3 braces, `dir/**`, compat corpus) = release-process D2; Phase 5; `feature/patterns2` -> `dev` | Xake `feature/patterns2` -> `dev` | — | is the gate |
| S6 | Final import: re-run the extraction at the `dev` commit after S5, replace S4's imported tree, reference `Xake 4.0.0-preview.N` built after S5; CI green | Xake.Dotnet `feature/split-import` -> `dev` | S5, S4, a preview after S5 | no |
| S7 | Removal in Xake: §2.2 edits; `src/core` packs `Xake`; tests, docs, samples, workflows, build scripts; release notes 4.0 entry; `RecordShapeTests` split | Xake `feature/dotnet-split-remove` -> `dev` | S6 green | no |
| S8 | Release: Xake `v4.0.0` from `master`; then the new repo bumps `Xake` to `4.0.0` (`bump-base`), tags `v4.0.0` (`Xake.Dotnet`) and `hermetic-v0.11.0` on one commit | Xake tag; Xake.Dotnet PR `release/4.0.0` -> `dev` -> `master`, tags | S7 | no |
| S9 | Follow-ups: bootstrap bumps (`build.fsx` of both repos to the published 4.0.0 / 0.11.0), unlist `Xake.Dotnet` 1.1.x betas, wiki `#r` lines and links, `docs/session.md` of both repos | both | S8 | no |

Between S7 and S8 `dev` of Xake has no .NET tasks; the gap is a few days and nothing is published
from it. If S5 slips, S1-S4 still pay off: the seam is gone and the new repo is rehearsed.

## 10. Risks

- **Hermetic's direct engine calls** (`Path.parse`, `Path.matchesPattern`, `Includes of PathMask`,
  `ExecContext`, `Logging`, `Var.read`). Kept public in 4.0 (D16, C8), but the *meaning* of
  `dir/**` and `(a|b)` changes in 4.0; fixed in S5 while hermetic is still in the repo, which is
  why the split waits. After 4.0, `ExecContext` stays a frozen record within 4.x (B's rule,
  `RecordShapeTests` row kept in Xake), and the new repo keeps its own frozen-record test for the
  six compiler records hermetic builds.
- **Integration tests need real csc/fsc** (10 files in `src/tests`, 18 in `src/hermetic.tests`);
  the new repo's CI needs the SDK and NuGet access (`NUGET_FETCH=on` in `build-fsc`), as Xake's
  does now. The mono provider has no test anywhere (docs/session.md); it stays untested.
- **Test helpers diverge**: `Common.fs`/`XakeTestBase.fs` exist twice after the split. Accepted;
  they are small and test-only.
- **Engine previews on nuget.org** (Q3) are public and permanent; a preview that breaks the new
  repo is replaced by the next one, never re-published.
- **Docs links**: 14 files (`docs/*.md`, `readme.md`, `CLAUDE.md`) link to the moving docs (`dotnet-build.md`,
  `description.md`, `hermetic/*`); after S7 they become absolute links to the new repo. The
  `$schema` paths in the descriptions keep working (same relative path in both repos).
- **Samples** that `#r "../out/netstandard2.0/Xake.Dotnet.dll"` break in Xake at S7; the moved
  ones work in the new repo, the remaining ones switch to `#r "nuget: Xake.Dotnet"` and can only
  be checked after S8 (as `gettingstarted.fsx` today).
- **The wiki** (`Tasks-|-Csc`, `Tasks-|-Fsc`, `Tasks-|-Dotnet`, `Tasks-|-MSBuild-and-Resources`,
  `Tasks-|-Project-Descriptions`, `Reference-|-Dotnet-*`) lives in the Xake wiki and says "one
  `#r` is enough"; it needs the `#r` change at S9 (it has no hermetic pages).
- **`build.fsc.fsx` bootstrap after the move** needs a published `Xake.Hermetic.Dotnet` that runs
  on `Xake 4.x`; until 0.11.0 is out, the new repo's `build-fsc` job uses 0.10 on `Xake 3.15`
  (its own bootstrap, separate from what it builds) — fine as long as the script uses only API
  0.10 has, which is the case today.
- **History import mistakes** (a path missed by `filter-repo`) are cheap to redo before S6 merges,
  expensive after; S4 is the rehearsal for that.

## 11. Open questions for the owner

1. **`hasRule` name and shape.** Recommendation: `hasRule : string -> Recipe<ExecContext, bool>`
   in the auto-opened `ScriptFuncs`, no target-type overload. Alternative: keep `locateRule`
   internal and drop the early "missing hashed inputs" report in `CompilerRunner` (worse errors).
2. **`ProcessExec` public as is, or under a new name** (`Xake.Process.run`/`runSync`)?
   Recommendation: as is, `[<RequireQualifiedAccess>]`; renaming adds churn for no reader.
3. **Engine previews on nuget.org** (`v4.0.0-preview.N`) so the new repo builds on a published
   engine before 4.0? Recommendation: yes; the alternative (local feed in CI, or a git submodule)
   brings back the machinery release-process removed.
4. **Where the fsc/locks self-build lives.** Recommendation: moves to the new repo (it describes
   its two libraries and is hermetic's showcase); Xake drops `build.fsc.fsx`, `locks/`,
   `xake.fingerprints`, `src/core/xake.project.json` and the `build-fsc` job. Alternative: keep it
   in Xake for `src/core` only, as a consumer test of the published hermetic package.
5. **Versions**: `Xake.Dotnet` 4.0.0 then independent minors, hermetic 0.11.0 and 0.x on, two tag
   prefixes (§5)? Recommendation: yes. Alternative: one tag publishing both (lockstep inside the
   new repo; hermetic-only changes then cost an empty `Xake.Dotnet` patch).
6. **Unlist `Xake.Dotnet` 1.1.x betas** on nuget.org after 4.0.0? Recommendation: yes (they
   target Xake 1.1 and net46).
7. **A windows-latest CI leg** in the new repo? Recommendation: yes for `build` with `FILTER`
   on the integration tests that touch paths and the SDK probe; Windows is where the `Program
   Files` space trap came from (docs/session.md) and Xake's CI has no Windows leg today.
8. **Test project name** `src/dotnet.tests` (assembly `dotnet.tests`)? Recommendation: yes,
   mirrors `hermetic.tests`; the paths of everything else stay as in Xake.
9. **Docs of the in-flight feature folders** (`hermetic-build`, `hermetic-windows`): stay in Xake
   as the record, new work in the new repo? Recommendation: yes; nothing is copied.

## Decisions (2026-11-01 onwards)

- **S-D1 (2026-11-01): open questions 1–4 and 6–9 accepted as recommended.** `hasRule : string
  -> Recipe<ExecContext, bool>` in `ScriptFuncs`; `ProcessExec` public as is with
  `RequireQualifiedAccess`; Xake previews `v4.0.0-preview.N` on nuget.org; the fsc/locks
  self-build moves to the new repo; unlist the 1.1.x betas after 4.0.0; a windows-latest CI leg;
  test project `src/dotnet.tests`; the hermetic feature folders stay in Xake as the record.
- **Question 5 reopened by the owner: one package instead of two.** `Xake.Dotnet` and the
  hermetic library ship in ONE nupkg (id `Xake.Dotnet`), one version line, one tag prefix.
  Pitfall analysis pending; recorded here once decided.
