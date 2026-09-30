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

Run filtered tests:

```bash
dotnet test src/tests -c Release --filter "Name~Rm"
```

Run filtered tests through Xake variable:

```bash
dotnet fsi build.fsx -- -- test -d FILTER=Rm
```

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
| `Xake` | `src/dotnet` | `Xake.dll` (engine) and `Xake.Dotnet.dll` (.NET tasks) | semver 3.x, `-d Version=` / `$VERSION` |
| `Xake.Hermetic.Dotnet` | `src/hermetic` | `Xake.Hermetic.Dotnet.dll` (locks, restore, SBOM, verify, pack, sign) | 0.x, `-d HermeticVersion=` / `$HERMETIC_VERSION`; default is the `<Version>` in its fsproj |

`Xake` is packed from the leaf project `src/dotnet`, which references the core and pulls its
assembly into the same nupkg — so the engine and the tasks always share one version and one
publish. `src/core` is marked `IsPackable=false` and is never published on its own.

`Xake.Hermetic.Dotnet` depends on package `Xake` with the range **`[3.4.0, 4.0.0)`**. The range
is the whole contract between the two: the hermetic assembly uses only public `Xake`/`Xake.Dotnet`
API (no `InternalsVisibleTo` between packages), so any 3.x from 3.4.0 on works with it and a
Xake major bump must re-release it. `dotnet pack` would otherwise write `>= 1.0.0` (or the
hermetic version itself); the `XakeDependencyRange` target in the fsproj replaces it, and since
that target relies on a private NuGet item name, `build.fsx`'s pack **asserts the packed nuspec**
and fails the build (deleting the nupkg) if the range is anything else. **`Xake.Hermetic.Dotnet`
0.x may break** between minor versions; it is a preview.

Note that `build.fsx` builds only `netstandard2.0` into `out/`; the `net462` assets come from
`dotnet pack`, which builds both target frameworks.

Create both packages locally, each at its own version:

```bash
dotnet fsi build.fsx -- -- pack -d Version=X.Y.Z -d HermeticVersion=0.A.B
```

Each package goes into a folder of its own: `out/pkg/Xake/Xake.X.Y.Z.nupkg` and
`out/pkg/Xake.Hermetic.Dotnet/Xake.Hermetic.Dotnet.0.A.B.nupkg` (one mask for both would
confuse `Xake.Hermetic.Dotnet.0.1.0` with a Xake version `Hermetic.Dotnet.0.1.0`). The hermetic
pack builds `src/dotnet` at the *Xake* version first and packs without rebuilding project
references, so its assembly references `Xake`/`Xake.Dotnet` at that version, not its own.
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
by the tag's prefix, runs `build test pack` (both packages are packed and the hermetic nuspec's
range is asserted every time) and pushes **only the tagged package** to nuget.org with the
`NUGET_API_KEY` repository secret:

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

A release of both is two tags and two workflow runs. Release `Xake` first when the hermetic
package needs something new from it: its range floor (`3.4.0` today) must be on nuget.org
before a `Xake.Hermetic.Dotnet` that requires it is pushed; raise `XakeDependencyRange` in
`src/hermetic/Xake.Hermetic.Dotnet.fsproj` (and `expectedXakeRange` in `build.fsx`) when it does.

**The published version is not the tag.** The workflow appends the GitHub run number as a
fourth component, so tag `v3.3.0` publishes `3.3.0.<run>` and `hermetic-v0.1.0` publishes
`0.1.0.<run>`. That makes re-running a release harmless — the version is always fresh — but it
means the nuget.org version and the git tag never match exactly. Pushing uses
`--skip-duplicate`, so a re-run never fails on an already-published version.

### Release checklist

- `dotnet fsi build.fsx -- -- build test pack` is green (both test projects, both packages,
  the range assertion), and so is the `dev` build on CI
- `dotnet test src/tests --filter 'Category=Integration'` and the same on `src/hermetic.tests`
  pass (real compiler invocations)
- `dotnet fsi features.fsx` and `dotnet fsi fullframework.fsx` run from `samples/` (they use
  the local build, so run `build` first). `gettingstarted.fsx` references the *published*
  package and can only be checked after the release lands
- `dotnet pack src/dotnet -c Release /p:Version=X.Y.Z-rc --output /tmp/pk` produces no
  packaging warnings, and the nupkg contains both target frameworks
- Docs mention no stale version numbers, and the SDK baseline is stated consistently
- The `FSharp.Core` version in the packed nuspec is still the pinned `8.0.100`, not whatever
  SDK produced the build
- `dev` is merged into `master` and the tag is pushed from `master`

### Bootstrapping note

`build.fsx` starts with `#r "nuget: Xake, <version>"` — the build script builds Xake with an
already published Xake. That reference is intentionally *behind* the version being released;
bump it in a separate commit after a release has landed on nuget.org, and only to a version
whose features the script actually needs.
