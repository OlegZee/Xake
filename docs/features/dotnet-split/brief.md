# Brief: moving `Xake.Dotnet` and the hermetic library out of Xake, as one package (D1)

Status: **design note, revised 2026-11-01: S-D1 and S-D2 applied** (see "Decisions" at the end).
Step D1 of the release-process plan
([../release-process/tracker.md](../release-process/tracker.md), rows D0-D3; the reasoning for D is in
[../release-process/review.md](../release-process/review.md) §4 D and §6). The owner's decisions
S-D1 (nine questions) and S-D2 (one package) are folded into the body; what remains is the owner's
approval of the note as a whole. The facts are reproducible from the files named.

Sources read: `docs/session.md`, `docs/devprocess.md`, release-process `review.md`/`tracker.md`/
`session.md`, pattern-module `brief.md` (D1-D39), `tracker.md`, `spec.md` §6.1, §8.1, §10.2, §13 (C3,
C8); the code of `src/core`, `src/dotnet`, `src/hermetic`, `src/hermetic.tests`, `src/tests`,
`build.fsx`, `build.fsc.fsx`, `testjobs.fsx`, `.github/workflows/*`; the repository
`OlegZee/Xake.Dotnet`; the nuget.org indexes of `Xake`, `Xake.Dotnet`, `Xake.Hermetic.Dotnet`.

## 1. Goal and non-goals

**Goal.** At Xake 4.0 the `Xake` package carries the engine only (`Xake.dll`). The .NET tasks
(`Xake.Dotnet`: `csc`, `fsc`, `msbuild`, `resgen`, project descriptions, toolchain discovery) and
the hermetic library (`Xake.Hermetic.Dotnet`: locks, restore, SBOM, verify, pack, sign) live in the
repository `OlegZee/Xake.Dotnet`, build on the *published* `Xake` package, and are released from
there **as one NuGet package, `Xake.Dotnet`, carrying two assemblies** (S-D2). The engine reaches
them only through public API: `InternalsVisibleTo("Xake.Dotnet")` is removed from
`src/core/Xake.fsproj`.

Why (review §3): `src/dotnet` changed in 10 of 13 releases and every API hermetic needed came from
`src/dotnet`, never from the engine; the coupling is `src/dotnet` <-> hermetic, and 12 of 12 hermetic
releases were paired with Xake anyway. After the split an engine release touches nothing .NET, and a
.NET change needs no engine release. Hermetic has no external package dependencies, so shipping it
inside `Xake.Dotnet` costs consumers nothing and removes a second id, version line and tag prefix.

**Non-goals.**

- No meta-package and no second package. 4.0 is breaking anyway (vars variant 3, pattern module); review §6 already
  drops the `Xake.Core` + meta-`Xake` variant when D ships in 4.0.
- No change to namespaces or public API of `Xake.Dotnet` / `Xake.Hermetic.Dotnet` beyond what the
  seam needs (§3). Scripts keep `open Xake.Dotnet` and `open Xake.Hermetic.Dotnet`.
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
| `src/dotnet/Xake.Dotnet.fsproj` | `src/dotnet/` | `PackageId` `Xake` -> `Xake.Dotnet`; the `IncludeCoreOutput` target and `XakePackageVersion` go; `ProjectReference ..\core` -> `PackageReference Xake` (range §4); IVT `tests` -> `dotnet.tests`; description/URLs point to the new repo; this is **the packable project of the one package**: a target in the same style as the old `IncludeCoreOutput` adds the hermetic assembly and its xml to `lib/<tfm>/` (hermetic references `src/dotnet`, so the reverse project reference would be a cycle) |
| `src/dotnet/readme.md`, `src/dotnet/xake.project.json` | `src/dotnet/` | readme: `#r "nuget: Xake.Dotnet"`; it is the readme of the one package (gets a hermetic section marked "API may change"), not the root `readme.md` |
| `src/hermetic/*` (18 `.fs`, fsproj, readme, `xake.project.json`) | `src/hermetic/` | fsproj: not packable any more (`IsPackable` false, its `PackageId`, nuspec dependencies and version properties go); the private `ProjectReference ..\core` goes (Xake comes as a `PackageReference`), `ProjectReference ..\dotnet` stays; the readme text is folded into the package readme; `Lock.fs:1584` `{ ctx with Logger = ... }` -> `ExecContext.withLogger` (deferred B4) |
| `src/hermetic.tests/*` incl. `fixtures/` | `src/hermetic.tests/` | the linked `..\tests\Common.fs`, `..\tests\XakeTestBase.fs` point to the new repo's copies (`src/dotnet.tests/`) |
| `src/tests/{DotnetTasksTests, ToolsetTests, PackageRootTests, SdkProbeTests, CscServerTests, ResxTests, CompilerCrashTests, CompilerJobsTests, FscTests, PathSpacesTests, HermeticModeTests, DescriptionTests, ProjectPlanTests}.fs` (13 files, ~3,250 lines) and `src/tests/fixtures/description/` | `src/dotnet.tests/` | none of them uses an engine internal (checked: no `ExecCore`, `CommonLib`, `ProcessExec`, ... reference); some use `Xake.Dotnet` internals, so the IVT follows them. Ten carry `Category=Integration` (real csc/fsc) |
| `src/tests/LibraryVarsTests.fs` | split | the `8.8-46..54` and `pr2-76..83` tests (DotNetVars, `NUGET_PACKAGES`, `CSC_SERVER`, jobs) move; `pr2-69..75` (`Var.read`, engine only) stay |
| `src/tests/RecordShapeTests.fs` | split | the six compiler records (`CscSettingsType`, `RunOptions`, `Csc`, `FscSettingsType`, `FscRunOptions`, `Fsc`) move; `ExecContext` and the `withLogger` test stay |
| `src/tests/XakeTestBase.fs`, `src/tests/Common.fs` | copy, then trim | Xake's copy loses `PackageRoot`, `BuildPackageRoot` (calls `DotNetFwk.packageRootWithSource`) and the `XAKE_CSC_SERVER` runner switch; the new repo's copy keeps them |
| `testjobs.fsx` | split | Xake keeps `tests/jobs` (`Delegated and resource tests`) and `tests/rest`; `tests/server`, `Compiler jobs` and all `hermetic/*` jobs go |
| `build.fsc.fsx`, `locks/Xake.Dotnet/`, `locks/Xake.Hermetic.Dotnet/`, `xake.fingerprints` | repo root | decided (S-D1, question 4): the fsc/locks self-build moves; Xake drops it with `locks/Xake/` and `src/core/xake.project.json` |
| `docs/dotnet-build.md`, `docs/csc-server.md`, `docs/csc-syntax.md`, `docs/description.md`, `docs/hermetic/*`, `docs/hermetic-guide.md`, `docs/hermetic-architecture.md`, `docs/fingerprints.md` (as far as it describes descriptions/locks), `docs/schemas/xake.project.schema.json` | `docs/` | the descriptions point to `../../docs/schemas/...`, same relative path in the new repo |
| `samples/fullframework.fsx`, `samples/toolset.fsx`, `samples/hermetic/`, `samples/netcore-compile/`, `samples/dotnet-fake.csproj`, `samples/helloworld.cs` | `samples/` | `#r` the new repo's `out/` or the package |
| `Icon.png`, `LICENSE` | repo root | copied (both repos keep them) |
| (new) | `.github/workflows/build.yml`, `publish.yml`, `build.fsx`, `global.json`, `readme.md`, `docs/release-notes.md`, `docs/devprocess.md`, `CLAUDE.md`, `docs/session.md` | §6, §7 |

### 2.2 Stays in Xake, with edits

| File | Edit |
|---|---|
| `src/core/Xake.fsproj` | `IsPackable` true, `PackageId` `Xake`, the package metadata now on `src/dotnet` (authors, tags, license, icon, readme, repository URL) moves here; IVT `Xake.Dotnet` removed; the seam of §3 |
| `src/tests/tests.fsproj` | the `src/dotnet` reference and the 13 files go |
| `build.fsx` | one library, one package (`pack` = `dotnet pack src/core`); `HermeticVersion`, `pack-hermetic`, `push-hermetic`, `checkXakeDependency`, `hermeticVersionArgs` and the `HERMETIC_VERSION` variable go |
| `.github/workflows/build.yml` | `build-fsc` job goes (S-D1, question 4); the `build` job stays |
| `.github/workflows/publish.yml` | `v*` only; the `hermetic-v*` branch goes |
| `readme.md` (the `Xake` package readme) | the csc example becomes an engine example, plus one line "`.NET` compilers: package `Xake.Dotnet`" |
| `samples/gettingstarted.fsx`, `samples/features.fsx`, `samples/book/intro.fsx` | the csc parts take `#r "nuget: Xake.Dotnet"` or are replaced by engine-only steps |
| `docs/tasks.md`, `cheatsheet.md`, `index.md`, `variables.md`, `resources.md`, `roadmap.md`, `devprocess.md`, `CLAUDE.md`, `docs/session.md` | .NET sections become links to the new repo; packaging section rewritten |
| `docs/release-notes.md` | history stays; a 4.0.0 entry "`Xake.Dotnet` is a package of its own, hermetic included; `Xake.Hermetic.Dotnet` ends at 0.10.0" with the script change |
| `docs/features/hermetic-build/`, `hermetic-windows/`, `release-process/` | stay as the record; new hermetic work gets feature folders in the new repo |

## 3. The seam: engine internals used by `Xake.Dotnet` and hermetic

Measured, not grepped only: a scratch copy of `src/core` + `src/dotnet` with the IVT to
`Xake.Dotnet` removed fails with exactly these errors (both TFMs); making the three modules public
builds both TFMs clean. `Xake.Hermetic.Dotnet` has no IVT from the engine and uses public API only
(its IVT is to `hermetic.tests`). Shape and names below are decided (S-D1, questions 1 and 2).

| Internal | Uses | Recommendation |
|---|---|---|
| `Xake.ProcessExec.pexecSync` (module `internal`; `pexec` is the async form `Tasks/Shell.fs` uses) | 5, all in `DotNetFwk.fs` (`pkg-config` x2, `dotnet --version`, `dotnet` restore-probe, `chmod`), inside memoized lookups outside any recipe | **make public** (decided): the module `Xake.ProcessExec` with `pexec`, `pexecSync`, `drainTimeoutMs` as they are, `[<RequireQualifiedAccess>]` so `open Xake` adds nothing to scripts' scope. It is the "process plumbing" the review names as the engine contract; copying it would duplicate the drain-timeout logic (pipes held by a child process), which is the subtle part. Document it as low-level and stable within 4.x |
| `Xake.CommonLib.memoize` (module `internal`) | 2, `DotNetFwk.fs:1077` (`locateFrameworkMemo`), `:1376` (`lookupFile`) | **replace inside `Xake.Dotnet`** (decided) by the `ConcurrentDictionary<_, Lazy<_>>` cache `DotNetFwk.fs:510` already uses for the SDK probe. Nothing in `CommonLib` is engine contract; making it public would expose `take`, `unique`, `memoizeRec` too |
| `Xake.ExecCore.locateRule` (module `internal`) | 1, `CompilerRunner.fs:301`: "does any rule of the script produce this hashed file?" before `needFiles`, so missing inputs are all reported at once | **new public recipe function** (decided) in `ScriptFuncs` (auto-opened with `getCtxOptions`, `getRuleMatch`): `hasRule : string -> Recipe<ExecContext, bool>` (a path, resolved like `need`; true when a rule would build it). Its signature does not depend on the matcher, so it can land on `dev` now and survive pattern-module Phase 4 (which rewrites `explainMatch`/`locateRule` underneath) |
| `InternalsVisibleTo("Xake.Dotnet")` | — | **removed** in the same PR (S1); the IVT to `tests` stays |
| `ExecContext` record copy in hermetic (`Lock.fs:1584`) | public, but a record copy outside `Xake.dll` breaks on a new field | not a seam item; switch to `ExecContext.withLogger` (B1 helper, 3.15) in the seam PR (S1), closing B4 (`withLogger`) |

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

**One nupkg** (S-D2), built from the repository `OlegZee/Xake.Dotnet`:

| Package | Repo | Contains (per TFM `net462`, `netstandard2.0`) | Dependencies |
|---|---|---|---|
| `Xake` 4.0.0 | Xake | `Xake.dll`, `Xake.xml` | `FSharp.Core` 8.0.100 |
| `Xake.Dotnet` 4.0.0 | Xake.Dotnet | `Xake.Dotnet.dll`, `Xake.Dotnet.xml`, `Xake.Hermetic.Dotnet.dll`, `Xake.Hermetic.Dotnet.xml` | `Xake [4.0.0, 5.0)`, `FSharp.Core` 8.0.100, `System.Resources.Extensions` 8.0.0, `Microsoft.Win32.Registry` 5.0.0 (netstandard2.0 only); framework references on net462: `System.Windows.Forms`, `System.IO.Compression` |

- **No `Xake.Hermetic.Dotnet` package from 4.0.** The assembly and the namespace
  (`open Xake.Hermetic.Dotnet`) are unchanged; only the package id they ship in changes.
  Hermetic's own dependency on `Xake.Dotnet` is now an in-package assembly reference, and its direct
  use of the engine is covered by the one `Xake` range.
- **Ranges:** release-process A (exact `X.Y.Z` versions, no run number). The upper bound of `Xake`
  is the next major, with a CI leg against the newest published `Xake` 4.x (§7); rule B's
  frozen-record guard across the package boundary is void (S-D2). The floor of `Xake` is the
  version the repository builds against; it moves only when the code needs something newer (E,
  `bump-base`, §6).
- **What `Xake` stops shipping:** `Xake.Dotnet.dll`/`.xml`, the two `System.*`/`Microsoft.*`
  dependencies and the net462 `System.Windows.Forms` reference; its readme loses the csc example.
- **What a script changes:** add `#r "nuget: Xake.Dotnet"`; it brings `Xake` and the hermetic
  assembly, so `#r "nuget: Xake.Dotnet"` covers hermetic too; `open Xake.Dotnet` and `open
  Xake.Hermetic.Dotnet` are unchanged. A script that references `Xake.Hermetic.Dotnet` replaces that
  `#r` line with `Xake.Dotnet` (no other edit; it need not also list `Xake.Dotnet`). A script that
  pins `#r "nuget: Xake, 4.x"` keeps the line to fix the engine version. Release-notes entry in
  Xake 4.0.0 and the first line of the new repo's readme.
- **The hermetic part is marked "API may change"** in the package readme and in `docs/hermetic*`
  until it settles; this is a documented exception to the version rules of §5.
- **FSharp.Core:** 8.0.100 pinned with `DisableImplicitFSharpCoreReference` in both projects,
  as today; `global.json` `8.0.0` + `latestMajor` copied.
- **TFMs:** libraries `net462;netstandard2.0` (unchanged), tests `net8.0`.
- **The package id `Xake.Dotnet` exists:** nuget.org lists `1.1.0.2-alpha`, `1.1.0.4-alpha`,
  `1.1.4.7-beta` (2018, from the old repository). 4.0.0 sorts above them; unlist the three after
  4.0.0 is out (S-D1, question 6).

## 5. Versioning of the new repository

Decided (S-D2): one version line, one tag prefix.

- **`Xake.Dotnet` starts at 4.0.0** and then versions on its own: its major follows the `Xake`
  major it targets (4.x on `Xake [4, 5)`), minors and patches are its own. The assembly shipped as
  `Xake` 3.x until now, so 4.0.0 reads as the continuation, and it sorts above the 2018 betas. A
  change in the hermetic part is a `Xake.Dotnet` release like any other; because that part is marked
  "API may change", an incompatible change in it is a minor bump with a release-notes line, not a
  major.
- **One tag prefix, `v*`**: a tag `vX.Y.Z` builds, packs and publishes `Xake.Dotnet` X.Y.Z. No
  `hermetic-v*` tags, no "floor is published" check between two packages, no lockstep question: it
  is lockstep by construction. "Release Xake first" remains only for an engine change.
- **`Xake.Hermetic.Dotnet` ends at 0.10.0.** The planned 0.11.0 is dropped. On nuget.org, after
  `Xake.Dotnet` 4.0.0 is published, the id `Xake.Hermetic.Dotnet` is marked deprecated (reason
  "legacy", alternate package `Xake.Dotnet`) and unlisted; 0.10.0 and older stay downloadable. A
  script that still says `#r "nuget: Xake.Hermetic.Dotnet"` keeps restoring 0.10.0, which as
  released depends on `Xake` below 4.0, i.e. it keeps working on the 3.x engine and stops being
  maintained; it cannot be mixed with `Xake.Dotnet` 4.x in one script. The fix is the one-line
  `#r` change above. The deprecation is a manual owner step on nuget.org (S9).

## 6. Build of the new repository

- **`build.fsx`** bootstraps on the published engine only: `#r "nuget: Xake, 4.0.0"` (the
  current Xake `build.fsx` uses `Xake` and `Xake.Tasks` only, and drives `dotnet build`/`pack`
  through `sh`). It is Xake's `build.fsx` with the libraries `Xake.Dotnet` and `Xake.Hermetic.Dotnet`
  built in one run, one `Version`, and `pack` producing **one package**; `push` pushes it.
  `pack-hermetic`, `push-hermetic`, `HermeticVersion` and `HERMETIC_VERSION` do not exist. The
  nuspec check is one check: `Xake.Dotnet` depends on `Xake` and carries both assemblies per TFM
  and neither `Xake.dll`.
- **`testjobs.fsx`**: the `dotnet` and `hermetic` slices (`dotnet/server`, `hermetic/server`
  serial; `dotnet/jobs`, `dotnet/rest`, `hermetic/record|run|lock|rest`), unchanged in content.
- **The fsc/locks self-build moves too** (S-D1, question 4): `build.fsc.fsx`, the two locks, the
  two descriptions and `xake.fingerprints` describe the two libraries of this repository. It
  bootstraps on published packages: until 4.0.0 is out, `Xake.Hermetic.Dotnet` 0.10.0 on `Xake 3.15`
  (its own bootstrap, separate from what it builds); from the S9 bump, one line `#r "nuget:
  Xake.Dotnet, 4.0.0"`, the second `#r` is gone. The `Xake` description is no longer a project of
  it: the engine is a package reference in the descriptions, which exercises the importer's package
  path (and drops the `package.bundle: ["Xake"]` gap of the in-repo period). Xake keeps no locks.
- **`bump-base` comes back** in the new repo's `build.fsc.fsx` as release-process E1 had it
  (Xake version -> the fsproj range, `import`, refresh only this repo's locks), for the one
  `Xake` range. It runs only when the code needs a newer engine.
- **Developing against an unreleased engine**: Xake publishes previews (`v4.0.0-preview.N` tags,
  §7, S-D1 question 3); the new repo references the preview until 4.0.0 is out. Day to day a local
  feed (`#i "nuget: /tmp/xake-feed"`, devprocess "Bootstrapping note") stays the escape hatch.

## 7. CI and publish workflows of the new repository

- `build.yml`, on push/PR to `dev`: job `build` (ubuntu-latest, SDK 10.0.x: `dotnet fsi build.fsx
  -- -- build test pack`); job `build-fsc` (SDK 10.0.403 pinned: `build.fsc.fsx build check -d
  NUGET_FETCH=on`); job `xake-latest`: restore against the newest published `Xake` 4.x (an
  overridable `XakeVersion` property, as B3's `hermetic-compat` did) and run the tests, so a new
  engine minor that breaks `Xake.Dotnet` is seen without a release; and a **windows-latest leg**
  (S-D1, question 7): `build` with `FILTER` on the integration tests that touch paths and the SDK
  probe (Windows is where the `Program Files` space trap came from, docs/session.md; Xake's CI
  has no Windows leg today).
- `publish.yml`, on tags: `v*` -> pack and push the one package `Xake.Dotnet`. The version is the
  tag (A); `--skip-duplicate`; secret `NUGET_API_KEY` (a new repository secret; the nuget.org key
  must be scoped to `Xake.Dotnet`, an id the owner already holds). One tag, one package: no second
  trigger branch and no floor check.
- Xake side: `publish.yml` accepts `v4.0.0-preview.N` (the `v*` glob already matches; the version
  string is the tag minus `v`, valid SemVer), so previews need no workflow change; decided yes
  (S-D1, question 3). After S7 its `hermetic-v*` branch is gone.
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
`dev`; the split itself waits for it (D0, owner 2026-10-29): the gate is pattern-module Phase 4/5
(S5).

| # | Step | Repo / branch -> target | Depends on | Before patterns? |
|---|---|---|---|---|
| S1 | Seam: `ProcessExec` public (`RequireQualifiedAccess`), `hasRule`, `memoize` -> local cache, `CompilerRunner` on `hasRule`, IVT `Xake.Dotnet` removed, hermetic `Lock.fs:1584` -> `withLogger` (B4), docs/session.md engine facts | Xake `feature/dotnet-seam` -> `dev` | the note approved | yes |
| S2 | Old repo prepared: tag `legacy-1.x`, delete the stale branch (owner, no PR) | Xake.Dotnet | — | yes |
| S3 | First preview `v4.0.0-preview.1` of `Xake` from `dev` (vars + S1) | Xake tag | S1 | yes |
| S4 | Scaffold of the new repo against `Xake 4.0.0-preview.1`: history import (§8) as a dry run from the S1 commit, `build.fsx` (one package), `testjobs.fsx`, workflows incl. the windows leg, readme, CLAUDE.md; CI green. A rehearsal: redone at S6 from the final cut | Xake.Dotnet `feature/split-scaffold` -> `dev` | S3 | yes |
| S5 | Pattern-module Phase 4 including hermetic/`Xake.Dotnet` on the 4.0 pattern meaning (C3 braces, `dir/**`, compat corpus) = release-process D2; Phase 5; `feature/patterns2` -> `dev` | Xake `feature/patterns2` -> `dev` | — | is the gate |
| S6 | Final import: re-run the extraction at the `dev` commit after S5, replace S4's imported tree, reference `Xake 4.0.0-preview.N` built after S5; CI green | Xake.Dotnet `feature/split-import` -> `dev` | S5, S4, a preview after S5 | no |
| S7 | Removal in Xake: §2.2 edits; `src/core` packs `Xake`; tests, docs, samples, workflows, build scripts (`hermetic-v*`, `pack-hermetic`, `build-fsc`); release notes 4.0 entry; `RecordShapeTests` split | Xake `feature/dotnet-split-remove` -> `dev` | S6 green | no |
| S8 | Release: Xake `v4.0.0` from `master`; then the new repo bumps `Xake` to `4.0.0` (`bump-base`) and tags **one** `v4.0.0` = package `Xake.Dotnet` 4.0.0 (both assemblies) | Xake tag; Xake.Dotnet PR `release/4.0.0` -> `dev` -> `master`, tag | S7 | no |
| S9 | Follow-ups: bootstrap bumps (`build.fsx` of both repos to the published 4.0.0, `build.fsc.fsx` to `Xake.Dotnet` 4.0.0), unlist `Xake.Dotnet` 1.1.x betas, **deprecate and unlist `Xake.Hermetic.Dotnet`** (alternate `Xake.Dotnet`; owner on nuget.org), wiki `#r` lines and links, `docs/session.md` of both repos | both | S8 | no |

Between S7 and S8 `dev` of Xake has no .NET tasks; the gap is a few days and nothing is published
from it. If S5 slips, S1-S4 still pay off: the seam is gone and the new repo is rehearsed.

## 10. Risks

- **Hermetic's direct engine calls** (`Path.parse`, `Path.matchesPattern`, `Includes of PathMask`,
  `ExecContext`, `Logging`, `Var.read`). Kept public in 4.0 (D16, C8), but the *meaning* of
  `dir/**` and `(a|b)` changes in 4.0; fixed in S5 while hermetic is still in the repo, which is
  why the split waits. After 4.0, `ExecContext` stays a frozen record within 4.x (the
  `RecordShapeTests` row is kept in Xake). Rule B is void as a release-process rule (S-D2), but
  the record copy at `Lock.fs:1584` is gone after S1 either way, and the `xake-latest` CI leg
  watches the engine side. The compiler-records test moves with the code; it is an in-package
  check now, not a boundary guard.
- **Users of `Xake.Hermetic.Dotnet` 0.10.0** (a public preview id): their scripts keep restoring
  0.10.0 on `Xake` 3.x and get no updates; a deprecation notice and the readme/release-notes line
  must say "use `Xake.Dotnet`". An engine 3.x -> 4.x migration then needs one `#r` change. Cheap,
  but it must be announced in the 4.0.0 release notes.
- **"API may change" under a 4.x version line**: consumers who pin `Xake.Dotnet` 4.x for the
  tasks may be broken by a hermetic minor. Accepted (the part is marked in the readme and in
  release notes); revisit when hermetic settles.
- **Packaging two assemblies in one project**: the hermetic dll is added by a target in
  `Xake.Dotnet.fsproj` (as `IncludeCoreOutput` does today for the engine); `S4` must show `dotnet
  pack` produces both assemblies per TFM and the nuspec has no hermetic dependency (nuspec check).
- **Integration tests need real csc/fsc** (10 files in `src/tests`, 18 in `src/hermetic.tests`);
  the new repo's CI needs the SDK and NuGet access (`NUGET_FETCH=on` in `build-fsc`), as Xake's
  does now. The mono provider has no test anywhere (docs/session.md); it stays untested.
- **Test helpers diverge**: `Common.fs`/`XakeTestBase.fs` exist twice after the split. Accepted;
  they are small and test-only.
- **Engine previews on nuget.org** are public and permanent; a preview that breaks the new
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
- **`build.fsc.fsx` bootstrap after the move** needs a published hermetic that runs on `Xake 4.x`
  only after S8; until then the new repo's `build-fsc` job uses `Xake.Hermetic.Dotnet` 0.10.0 on
  `Xake 3.15` (its own bootstrap, separate from what it builds) - fine as long as the script uses
  only API 0.10 has, which is the case today. S9 switches it to `Xake.Dotnet` 4.0.0.
- **History import mistakes** (a path missed by `filter-repo`) are cheap to redo before S6 merges,
  expensive after; S4 is the rehearsal for that.

## 11. Decided, and what is still open

Decided (details in "Decisions" below):

- S-D1, questions 1-4 and 6-9: `hasRule` in `ScriptFuncs`; `ProcessExec` public as is; previews
  `v4.0.0-preview.N`; the fsc/locks self-build moves; unlist the 1.1.x betas after 4.0.0; a
  windows-latest leg; test project `src/dotnet.tests`; hermetic feature folders stay in Xake.
- S-D2 (replaces the old question 5): one package `Xake.Dotnet`, one version line from 4.0.0, one
  tag prefix `v*`; `Xake.Hermetic.Dotnet` ends at 0.10.0, deprecated and unlisted; rule B void;
  hermetic marked "API may change".

Still open:

1. **Breaking changes in the hermetic part under one version line** (§5): proposed as a minor
   bump with a release-notes line, since the part is marked "API may change". Alternative:
   treat them as majors like the rest (no exception). Needs one word from the owner; nothing in the
   sequencing waits for it.

The remaining step is the owner's approval of the note, then S1.

## Decisions (2026-11-01 onwards)

- **S-D1 (2026-11-01): open questions 1–4 and 6–9 accepted as recommended.** `hasRule : string
  -> Recipe<ExecContext, bool>` in `ScriptFuncs`; `ProcessExec` public as is with
  `RequireQualifiedAccess`; Xake previews `v4.0.0-preview.N` on nuget.org; the fsc/locks
  self-build moves to the new repo; unlist the 1.1.x betas after 4.0.0; a windows-latest CI leg;
  test project `src/dotnet.tests`; the hermetic feature folders stay in Xake as the record.
- **S-D2 (2026-11-01): one package.** `Xake.Dotnet` and the hermetic library ship in ONE nupkg
  (id `Xake.Dotnet`, two assemblies, `open Xake.Hermetic.Dotnet` unchanged), one version line
  starting at 4.0.0, one tag prefix `v*`. Consequences: the hermetic 0.x line ends at 0.10.0 and
  the id `Xake.Hermetic.Dotnet` is deprecated/unlisted on nuget.org pointing to `Xake.Dotnet`;
  release-process rule B (frozen records across the package boundary) is void; `pack-hermetic`,
  the hermetic nuspec check and the second bootstrap `#r` go away; the hermetic part is marked
  "API may change" in the readme until it settles. Rationale: hermetic has no external package
  dependencies, and 12/12 hermetic releases were paired with Xake anyway.
