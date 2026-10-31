# Development process

This document describes the practical workflow for developing and releasing Xake.

## Requirements

- .NET SDK 8.0 or newer. `global.json` pins `8.0.0` with `rollForward: latestMajor`, so
  the newest installed SDK is used and 8.0 is the lowest one that works.
- GitHub access to the repository

## Branch workflow

Repository owner flow:

1. Create a feature branch from `dev`
2. Push branch
3. Open a pull request to `dev`
4. Wait for checks
5. Push fixes if checks fail
6. Merge with squash

External contribution flow:

1. Fork or clone repository
2. Create a feature branch from `origin/dev`
3. Push branch
4. Open a pull request to `dev`
5. Address review comments and failed checks

## Build and test

Primary command:

```bash
dotnet fsi build.fsx -- -- build test
```

Build core only:

```bash
dotnet build src/core -c Release
```

Run all tests (two test projects: `src/tests` for the engine and `Xake.Dotnet`,
`src/hermetic.tests` for `Xake.Hermetic.Dotnet`; the build script's `test` runs both):

```bash
dotnet test src/tests -c Release
dotnet test src/hermetic.tests -c Release
```

The build scripts' `test` (build.fsx and build.fsc.fsx alike) splits the two projects into
the parallel jobs listed in `testjobs.fsx` (slices of whole fixture classes, the last one per
project taking the rest): `test-build::<project>` builds each test project once, then every
`test::<project>/<slice>` runs `dotnet test --no-build` on its slice, writing the trx and its
full output (`console.log`) into `out/test/<project>/<slice>/`; the console shows one summary
line per job. One job runs alone by naming it: `dotnet fsi build.fsx -- -- test::hermetic/run`.
A new fixture lands in the `rest` slice of its project; move it into a slice when it gets slow.
Fixtures that use the compiler server go into the `server` jobs, which run one after the
other (the server is one per machine); every other job runs with `XAKE_CSC_SERVER=off` in its
environment, which `XakeTestBase` passes to the scripts as `-d XAKE_CSC_SERVER=off` (Xake.Dotnet
reads build variables only).

Run filtered tests:

```bash
dotnet test src/tests -c Release --filter "Name~Rm"
```

Run filtered tests through Xake variable:

```bash
dotnet fsi build.fsx -- -- test -d FILTER=Rm
```

(or `$FILTER`; every job keeps its tests whose name matches the regex, NUnit's `name =~ 'Rm'`.)

## Command line style

Use this format for Xake scripts:

```bash
dotnet fsi build.fsx -- -- [options] [targets]
```

Examples:

```bash
dotnet fsi build.fsx
dotnet fsi build.fsx -- -- clean build test
dotnet fsi build.fsx -- -- build;test
dotnet fsi build.fsx -- -- build --dryrun
```

## Packaging and publishing

Two NuGet packages, **versioned and released independently**:

| Package | Packed from | Contains | Version |
|---|---|---|---|
| `Xake` | `src/dotnet` | `Xake.dll` (engine) and `Xake.Dotnet.dll` (.NET tasks) | semver (3.x; 4.0 is next on `dev`), `-d Version=` / `$VERSION` |
| `Xake.Hermetic.Dotnet` | `src/hermetic` | `Xake.Hermetic.Dotnet.dll` (locks, restore, SBOM, verify, pack, sign) | 0.x, `-d HermeticVersion=` / `$HERMETIC_VERSION`; default is the `<Version>` in its fsproj |

`Xake` is packed from the leaf project `src/dotnet`, which references the core and pulls its
assembly into the same nupkg — so the engine and the tasks always share one version and one
publish. `src/core` is marked `IsPackable=false` and is never published on its own.

`Xake.Hermetic.Dotnet` **builds on the in-repo `Xake` until 4.0** (owner, 2026-10-31):
`src/hermetic` has ProjectReferences to `src/core` (private: `Xake.dll` ships inside package
`Xake`) and `src/dotnet`. `dotnet pack` turns the `src/dotnet` reference into a dependency on
package `Xake` at the Xake version of the same build: `build.fsx` packs the hermetic project with
`-p:HermeticVersion=<its version> -p:XakePackageVersion=<-d Version>` (never a global
`/p:Version`, which would become the dependency's version too). So a hermetic release goes out
together with the Xake release it was built with; until 4.0 `publish.yml` refuses a lone
`hermetic-v*` tag. At 4.0 `Xake.Dotnet` and the hermetic package leave the `Xake` package
together (release-process decision D). The published 0.10 keeps its range `[3.14.0.45, 4.0)`.
The hermetic assembly still uses only public `Xake`/`Xake.Dotnet` API. `build.fsx`'s pack checks
that the packed nuspec depends on `Xake` and not on `Xake.Dotnet` (which is not a package), and
fails (deleting the nupkg) otherwise. **`Xake.Hermetic.Dotnet` 0.x may break** between minor
versions; it is a preview. `build.fsc.fsx bump-base` (the Xake floor of the published-package
period) is disabled until then.

**The records the hermetic package builds.** An F# record expression or `{ r with ... }` copy
compiled against one Xake calls the record's full constructor, so a field added in a later Xake
breaks a published hermetic package at run time (`MissingMethodException`). Until Xake 4.0 these
records do not grow: `ExecContext`, `CscSettingsType`, `RunOptions`, `Csc`, `FscSettingsType`,
`FscRunOptions`, `Fsc`. A new setting goes through a function or a builder operation instead;
code outside `Xake.dll` changes a context through `ExecContext.withLogger` and the like, never a
record copy. `src/tests/RecordShapeTests.fs` holds the field lists and fails on a change. Why and
how this was decided: docs/features/release-process/review.md (option B).

**Developing the hermetic package** needs nothing extra: it builds and tests against the
working tree's `src/core` and `src/dotnet`. `test-project::<project>` runs all test jobs of one
test project (`tests` or `hermetic`).

Note that `build.fsx` builds only `netstandard2.0` into `out/`; the `net462` assets come from
`dotnet pack`, which builds both target frameworks.

Create both packages locally, each at its own version:

```bash
dotnet fsi build.fsx -- -- pack -d Version=X.Y.Z -d HermeticVersion=0.A.B
```

Each package goes into a folder of its own: `out/pkg/Xake/Xake.X.Y.Z.nupkg` and
`out/pkg/Xake.Hermetic.Dotnet/Xake.Hermetic.Dotnet.0.A.B.nupkg` (one mask for both would
confuse `Xake.Hermetic.Dotnet.0.1.0` with a Xake version `Hermetic.Dotnet.0.1.0`). The two packs
are independent: the hermetic one compiles against the restored `Xake` package, whatever
`Version` the base is packed at. `pack-hermetic` packs only `Xake.Hermetic.Dotnet`.
Inspect before publishing:

```bash
unzip -l out/pkg/Xake/Xake.X.Y.Z.nupkg
unzip -p out/pkg/Xake.Hermetic.Dotnet/Xake.Hermetic.Dotnet.0.A.B.nupkg Xake.Hermetic.Dotnet.nuspec
```

`Xake` should contain `lib/net462/` and `lib/netstandard2.0/`, each with `Xake.dll`,
`Xake.Dotnet.dll` and the matching `.xml` doc files, plus `Icon.png` and `readme.md`.
`Xake.Hermetic.Dotnet` holds only `Xake.Hermetic.Dotnet.dll`/`.xml` per framework, plus
`Icon.png` and its own `readme.md`.

Push by hand (only needed when the tag-driven workflow is not used):

```bash
export NUGET_KEY=...
dotnet fsi build.fsx -- -- push -d Version=X.Y.Z -d NUGET_KEY=$NUGET_KEY                   # Xake
dotnet fsi build.fsx -- -- push-hermetic -d HermeticVersion=0.A.B -d NUGET_KEY=$NUGET_KEY  # Xake.Hermetic.Dotnet
```

`pack` must be run before `push`; `push` does not imply it.

## Supported baseline and the dependency floor

The lowest supported SDK is **.NET 8.0**. Three things enforce it, and they have to move
together:

| Where | Setting |
|---|---|
| `global.json` | `version: 8.0.0`, `rollForward: latestMajor` — 8.0 is the minimum, the newest installed SDK is what gets used |
| `src/tests/tests.fsproj`, `src/hermetic.tests/hermetic.tests.fsproj` | `net8.0` |
| `.github/workflows/build.yml` | builds on both `8.0.x` and `10.0.x` |

Separately, all three library projects set `DisableImplicitFSharpCoreReference` and pin
`FSharp.Core` to `8.0.100`. Without the pin the SDK injects its own FSharp.Core, that version
lands in the nuspec, and **every consumer of the package is forced onto it** — building the
release on a newer SDK would silently raise their floor. The pin makes the published
dependency independent of the machine that built it.

One consequence of the F# 8 floor is worth knowing when writing scripts and docs: the empty
settings block `builder {}` only compiles from F# 9 onward. Write `builder { () }`, which is
valid on the whole supported range.

## Release

Releases are published by CI, not from a workstation. The
[`publish.yml`](../.github/workflows/publish.yml) workflow triggers on a tag, picks the package
by the tag's prefix, runs `build test pack` for `v*` (both packages; the hermetic one is not
pushed) or `build test pack-hermetic` for `hermetic-v*` (refused until Xake 4.0: the hermetic
package builds on the in-repo `Xake`, so it goes out together with it), and pushes **only the tagged package** to
nuget.org with the `NUGET_API_KEY` repository secret:

| Tag | Publishes | Version variable |
|---|---|---|
| `vX.Y.Z` | `Xake` | `VERSION` |
| `hermetic-vX.Y.Z` | `Xake.Hermetic.Dotnet` | `HERMETIC_VERSION` |

1. Merge `dev` into `master`
2. Tag the release: `v` prefix for Xake, `hermetic-v` for Xake.Hermetic.Dotnet
3. Push the tag

```bash
git checkout master
git pull
git merge --ff-only dev
git tag vX.Y.Z                 # or: git tag hermetic-v0.A.B
git push origin master --tags
```

Until Xake 4.0 only `v*` tags publish. The hermetic package (0.11) ships together with Xake 4.0,
built on the same checkout, depending on package `Xake` at the version of that build; how it is
tagged and published once `Xake.Dotnet` and the hermetic package are split out of this
repository (release-process decision D) is settled with that split
([features/release-process](features/release-process/session.md)).

**The published version is the tag**, exactly: `v3.15.0` publishes `3.15.0`, `hermetic-v0.10.0`
publishes `0.10.0` (up to 3.14.0 / 0.9.0 the run number was appended: `3.14.0.45`, `0.9.0.46`;
in 3.4..3.14 it was never used to re-publish a version). Pushing uses `--skip-duplicate`, so a
re-run of the same tag is a no-op. A release that went out broken is not re-published: fix it
and tag the next patch.

### Release checklist

- `dotnet fsi build.fsx -- -- build test pack` is green (both test projects, both packages,
  the nuspec check), and so is the `dev` build on CI; `dotnet restore src/hermetic.tests`
  shows no NU1603
- `dotnet test src/tests --filter 'Category=Integration'` and the same on `src/hermetic.tests`
  pass (real compiler invocations)
- `dotnet fsi features.fsx` and `dotnet fsi fullframework.fsx` run from `samples/` (they use
  the local build, so run `build` first). `gettingstarted.fsx` references the *published*
  package and can only be checked after the release lands: run it then
- `dotnet pack src/dotnet -c Release /p:Version=X.Y.Z-rc --output /tmp/pk` produces no
  packaging warnings, and the nupkg contains both target frameworks
- Docs mention no stale version numbers, and the SDK baseline is stated consistently
- The `FSharp.Core` version in the packed nuspec is still the pinned `8.0.100`, not whatever
  SDK produced the build
- `dev` is merged into `master` and the tag is pushed from `master`

### Bootstrapping note

Both build scripts bootstrap from nuget.org: the build builds Xake with an already published
Xake. `build.fsx` starts with `#r "nuget: Xake, <version>"`; `build.fsc.fsx` (the declarative
build, which needs `Locks`/`Import`/`Fingerprints` from the hermetic package) with the same and
`#r "nuget: Xake.Hermetic.Dotnet, <version>"` (the first lines of the scripts are the one place
the versions are written). Those references are intentionally *behind* the
version being released: bump them only after a release has landed on nuget.org, to the exact
published version (the tag from 3.15 on; the four-part `X.Y.Z.<run>` for releases up to
3.14.0 / hermetic 0.9.0, where a bare `X.Y.Z` does not exist and resolves upwards with
NU1603), and only when the script needs what it brings. Bump `Xake` in both
scripts together, so the fsc build does not run on an older engine than the msbuild one.

To work on a script against *unreleased* libraries, there are two ways:

- a temporary `#r` on built dlls. `build.fsx` carries it as the commented
  `// #r "out/netstandard2.0/Xake.dll"` (copy the dlls elsewhere first: `build.fsx` overwrites
  `out/netstandard2.0`, and overwriting assemblies fsi has loaded kills the run with a
  `BadImageFormatException`). `build.fsc.fsx` writes only `out/Release/`, so it can `#r`
  `out/netstandard2.0/{Xake,Xake.Dotnet,Xake.Hermetic.Dotnet}.dll` directly in place of its two
  `#r "nuget: ..."` lines, after `dotnet fsi build.fsx -- -- build`.

- a local feed: pack at a version nuget.org does not have into a folder, add
  `#i "nuget: /tmp/xake-feed"` to the script and `#r` that version. The packed version lands in
  `~/.nuget/packages/` and shadows the release from there on; delete it when done.

Neither goes into a commit.
