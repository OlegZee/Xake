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

Run all tests (one test project, `src/tests`, the engine; the tests of the .NET tasks and the
hermetic library are in [OlegZee/Xake.Dotnet](https://github.com/OlegZee/Xake.Dotnet)):

```bash
dotnet test src/tests -c Release
```

The build script's `test` splits the project into the parallel jobs listed in `testjobs.fsx`
(slices of whole fixture classes, the last one taking the rest): `test-build::<project>` builds
the test project once, then every `test::<project>/<slice>` runs `dotnet test --no-build` on its
slice, writing the trx and its full output (`console.log`) into `out/test/<project>/<slice>/`;
the console shows one summary line per job. One job runs alone by naming it:
`dotnet fsi build.fsx -- -- test::tests/jobs`. A new fixture lands in the `rest` slice; move it
into a slice when it gets slow.

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

One NuGet package:

| Package | Packed from | Contains | Version |
|---|---|---|---|
| `Xake` | `src/core` | `Xake.dll` (engine) | semver (3.x; 4.0 is next on `dev`), `-d Version=` / `$VERSION` |

Up to 3.15 `Xake` was packed from `src/dotnet` and carried `Xake.Dotnet.dll` too, and
`Xake.Hermetic.Dotnet` (0.x, ended at 0.10.0) was packed from `src/hermetic`. From 4.0 both are
the one package `Xake.Dotnet`, built, tested and released from its own repository,
[OlegZee/Xake.Dotnet](https://github.com/OlegZee/Xake.Dotnet) ([features/dotnet-split](features/dotnet-split/brief.md));
it depends on the published `Xake` package with a range up to the next major and uses public
engine API only.

**`ExecContext` is frozen within 4.x.** An F# record expression or `{ r with ... }` copy compiled
against one Xake calls the record's full constructor, so a field added in a later 4.x would break
a package built against an older 4.x at run time (`MissingMethodException`). A new setting goes
through a function instead; code outside `Xake.dll` changes a context through
`ExecContext.withLogger` and the like, never a record copy. `src/tests/RecordShapeTests.fs`
holds the field list and fails on a change. Why: docs/features/release-process/review.md (option
B) and docs/features/dotnet-split/brief.md §10.

Note that `build.fsx` builds only `netstandard2.0` into `out/`; the `net462` assets come from
`dotnet pack`, which builds both target frameworks.

This repository packs one package, `Xake` (src/core, the engine alone). Create it locally:

```bash
dotnet fsi build.fsx -- -- pack -d Version=X.Y.Z
```

The package goes into `out/pkg/Xake/Xake.X.Y.Z.nupkg`. `pack` fails if it lacks `Xake.dll`/
`.xml` for a framework or carries anything else under `lib/`. Inspect before publishing:

```bash
unzip -l out/pkg/Xake/Xake.X.Y.Z.nupkg
```

`Xake` should contain `lib/net462/` and `lib/netstandard2.0/`, each with `Xake.dll` and
`Xake.xml`, plus `Icon.png` and `readme.md`.

Push by hand (only needed when the tag-driven workflow is not used):

```bash
export NUGET_KEY=...
dotnet fsi build.fsx -- -- push -d Version=X.Y.Z -d NUGET_KEY=$NUGET_KEY
```

`pack` must be run before `push`; `push` does not imply it.

## Supported baseline and the dependency floor

The lowest supported SDK is **.NET 8.0**. Three things enforce it, and they have to move
together:

| Where | Setting |
|---|---|
| `global.json` | `version: 8.0.0`, `rollForward: latestMajor` — 8.0 is the minimum, the newest installed SDK is what gets used |
| `src/tests/tests.fsproj` | `net8.0` |
| `.github/workflows/build.yml` | builds with SDK `10.0.x` (one leg: `global.json` rolls forward to the newest installed SDK), plus a `windows-latest` test leg |

Separately, the library project sets `DisableImplicitFSharpCoreReference` and pins
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
on a `v*` tag, runs `build test pack` and pushes `Xake` to nuget.org with the
`NUGET_API_KEY` repository secret (`hermetic-v*` tags ended with Xake.Hermetic.Dotnet 0.10.0):

| Tag | Publishes | Version variable |
|---|---|---|
| `vX.Y.Z` (also `vX.Y.Z-preview.N`) | `Xake` | `VERSION` |

1. Merge `dev` into `master`
2. Tag the release with the `v` prefix
3. Push the tag

```bash
git checkout master
git pull
git merge --ff-only dev
git tag vX.Y.Z
git push origin master --tags
```

`Xake.Dotnet` (with the hermetic assembly) is tagged and published from its own repository
([features/dotnet-split](features/dotnet-split/brief.md)).

**The published version is the tag**, exactly: `v3.15.0` publishes `3.15.0`, `hermetic-v0.10.0`
publishes `0.10.0` (up to 3.14.0 / 0.9.0 the run number was appended: `3.14.0.45`, `0.9.0.46`;
in 3.4..3.14 it was never used to re-publish a version). Pushing uses `--skip-duplicate`, so a
re-run of the same tag is a no-op. A release that went out broken is not re-published: fix it
and tag the next patch.

### Release checklist

- `dotnet fsi build.fsx -- -- build test pack` is green (the tests, the `Xake` package and its
  content check), and so is the `dev` build on CI
- `dotnet fsi features.fsx` runs from `samples/` (it uses the local build, so run `build`
  first). `gettingstarted.fsx` references the *published* package and can only be checked
  after the release lands: run it then
- `dotnet pack src/core -c Release /p:Version=X.Y.Z-rc --output /tmp/pk` produces no
  packaging warnings, and the nupkg contains both target frameworks
- Docs mention no stale version numbers, and the SDK baseline is stated consistently
- The `FSharp.Core` version in the packed nuspec is still the pinned `8.0.100`, not whatever
  SDK produced the build
- `dev` is merged into `master` and the tag is pushed from `master`

### Bootstrapping note

The build script bootstraps from nuget.org: the build builds Xake with an already published
Xake. `build.fsx` starts with `#r "nuget: Xake, <version>"` (its first line is the one place
the version is written). That reference is intentionally *behind* the version being released:
bump it only after a release has landed on nuget.org, to the exact published version (the tag
from 3.15 on; the four-part `X.Y.Z.<run>` for releases up to 3.14.0, where a bare `X.Y.Z` does
not exist and resolves upwards with NU1603), and only when the script needs what it brings.
(The fsc/locks self-build, `build.fsc.fsx`, moved to OlegZee/Xake.Dotnet with the hermetic
library it runs on.)

To work on a script against *unreleased* libraries, there are two ways:

- a temporary `#r` on built dlls. `build.fsx` carries it as the commented
  `// #r "out/netstandard2.0/Xake.dll"` (copy the dlls elsewhere first: `build.fsx` overwrites
  `out/netstandard2.0`, and overwriting assemblies fsi has loaded kills the run with a
  `BadImageFormatException`).

- a local feed: pack at a version nuget.org does not have into a folder, add
  `#i "nuget: /tmp/xake-feed"` to the script and `#r` that version. The packed version lands in
  `~/.nuget/packages/` and shadows the release from there on; delete it when done.

Neither goes into a commit.
