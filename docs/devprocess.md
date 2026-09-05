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

Run all tests:

```bash
dotnet test src/tests -c Release
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

The whole tool ships as a **single NuGet package, `Xake`**, containing both `Xake.dll` (the
engine) and `Xake.Dotnet.dll` (the .NET Framework tasks), for `net462` and `netstandard2.0`.
It is packed from `src/dotnet`, which references the core project and pulls its assembly into
the same nupkg — so both assemblies always share one version and one publish. `src/core` is
marked `IsPackable=false` and is never published on its own.

Note that `build.fsx` builds only `netstandard2.0` into `out/`; the `net462` asset comes from
`dotnet pack`, which builds both target frameworks.

Create the package locally:

```bash
dotnet fsi build.fsx -- -- pack -d Version=X.Y.Z
```

The result is `out/Xake.X.Y.Z.nupkg`. Inspect it before publishing:

```bash
unzip -l out/Xake.X.Y.Z.nupkg
```

It should contain `lib/net462/` and `lib/netstandard2.0/`, each with `Xake.dll`,
`Xake.Dotnet.dll` and the matching `.xml` doc files, plus `Icon.png` and `readme.md`.

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
| `.github/workflows/build.yml` | builds on both `8.0.x` and `10.0.x` |

Separately, both library projects set `DisableImplicitFSharpCoreReference` and pin
`FSharp.Core` to `8.0.100`. Without the pin the SDK injects its own FSharp.Core, that version
lands in the nuspec, and **every consumer of the package is forced onto it** — building the
release on a newer SDK would silently raise their floor. The pin makes the published
dependency independent of the machine that built it.

One consequence of the F# 8 floor is worth knowing when writing scripts and docs: the empty
settings block `builder {}` only compiles from F# 9 onward. Write `builder { () }`, which is
valid on the whole supported range.

## Release

Releases are published by CI, not from a workstation. The
[`publish.yml`](../.github/workflows/publish.yml) workflow triggers on any `v*` tag and runs
`build test pack`, then pushes to nuget.org with the `NUGET_API_KEY` repository secret.

1. Merge `dev` into `master`
2. Tag the release with a `v` prefix
3. Push the tag

```bash
git checkout master
git pull
git merge --ff-only dev
git tag vX.Y.Z
git push origin master --tags
```

**The published version is not the tag.** The workflow appends the GitHub run number as a
fourth component, so tag `v3.3.0` publishes `3.3.0.<run>`. That makes re-running a release
harmless — the version is always fresh — but it means the nuget.org version and the git tag
never match exactly. Pushing uses `--skip-duplicate`, so a re-run never fails on an
already-published version.

### Release checklist

- `dotnet fsi build.fsx -- -- build test` is green, and so is the `dev` build on CI
- `dotnet test src/tests --filter 'Category=Integration'` passes (real compiler invocations)
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
