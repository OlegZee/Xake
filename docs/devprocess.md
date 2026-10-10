# Development process

This document describes the practical workflow for developing and releasing Xake.

## Requirements

- .NET SDK **10.0.401 exactly**: `global.json` pins it with `rollForward: disable` (see
  [The SDK pin](#the-sdk-pin)); `dotnet` refuses to run in the repository without it. The
  tests also need the .NET 8 runtime (they target `net8.0`). The supported floor for building
  `build.fsx` is still SDK 8.0, checked by CI with `global.json` removed.
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

### The fsc build of record (`build.fsc.fsx`)

`build.fsc.fsx` compiles the three libraries (`Xake`, `Xake.Dotnet`, `Xake.Hermetic.Dotnet`,
netstandard2.0) with fsc from one committed lock, `locks/xake.json`: msbuild's own command line
for each project, recorded by `Project.import` together with the SHA-256 of the compiler, every
reference and every msbuild file that took part ([hermetic/lock.md](hermetic/lock.md)). `build`
replays the lock and fails if any of those inputs differs; it runs neither msbuild nor a restore
of the projects.

```bash
dotnet fsi build.fsc.fsx -- -- build test       # compile from the lock into out/netstandard2.0/, run both test projects
dotnet fsi build.fsc.fsx -- -- update-locks     # re-import the three fsproj into locks/xake.json
dotnet fsi build.fsc.fsx -- -- check-locks      # re-import under obj/xake/check/ and fail if the lock is stale
dotnet fsi build.fsc.fsx -- -- pack             # as build.fsx: dotnet pack of both packages
```

- **Run `update-locks` after any change to a project file** (a source added, removed or
  reordered, a define, a package version), to `Directory.Build.*`, or to `global.json`; review
  the lock's diff and commit it with the change. `check-locks` says whether that is needed.
- The script sets `HERMETIC=on` and `NUGET_PACKAGES=.packages`: the replay restores what the
  lock names into `.packages/` (gitignored) and fails if a compilation names a path outside
  the checkout and that folder, other than the pinned SDK's `fsc.dll`.
- The assemblies carry the version the lock was imported with, the projects' own `<Version>`;
  `-d Version=`/`-d HermeticVersion=` only reach `pack`.

### The SDK pin

`global.json` is `{ "sdk": { "version": "10.0.401", "rollForward": "disable" } }`. No NuGet
package carries a current F# compiler, so the lock's compiler is the SDK's own
`sdk/10.0.401/FSharp/fsc.dll`; only an exact pin makes that a fixed input (the lock records it
as the `dotnet-sdk 10.0.401` prerequisite, checked before every replay, and `HERMETIC=on`
accepts it only under an exact pin).

Consequences:

- Contributors need exactly that SDK (`dotnet-install --version 10.0.401`); with any other SDK
  `dotnet` fails in the repository with "A compatible .NET SDK was not found".
- CI installs it from the file: `actions/setup-dotnet` with `global-json-file: global.json`
  (plus `8.0.x` for the test runtime) in `build.yml`'s `pinned` leg and in `publish.yml`. The
  `floor` leg of `build.yml` deletes `global.json` and builds `build.fsx` on SDK 8.0.
- Bumping the SDK is one change: edit `global.json`, run `update-locks` (the compiler path,
  hash and prerequisite change), commit both.

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

`Xake.Hermetic.Dotnet` is a **consumer of the published `Xake`**: `src/hermetic` has
`<PackageReference Include="Xake" Version="[3.4.0.21, 4.0)" />`, not ProjectReferences to
`src/core`/`src/dotnet`, so it compiles against the assemblies on nuget.org and the nuspec
dependency (`[3.4.0.21, 4.0.0)`) comes straight from that reference. The range is the whole
contract between the two: the hermetic assembly uses only public `Xake`/`Xake.Dotnet` API (no
`InternalsVisibleTo` between packages), so any 3.x from the floor on works with it and a Xake
major bump must re-release it. `build.fsx`'s pack checks only that the packed nuspec depends on
`Xake` and not on `Xake.Dotnet` (which is not a package), and fails (deleting the nupkg)
otherwise. **`Xake.Hermetic.Dotnet` 0.x may break** between minor versions; it is a preview.

**The lower bound is the exact published version.** The release workflow appends the run
number, so `v3.4.0` went to nuget.org as `3.4.0.21`; there is no `3.4.0`. A floor of `3.4.0`
makes NuGet resolve the next version up and warn **NU1603** ("depends on Xake (>= 3.4.0) but
Xake 3.4.0 was not found") on every restore, ours and every consumer's. Raise the floor only to
a version that is on nuget.org, and only when the hermetic package needs what it brings.

**Developing against an unreleased base.** Normally `src/hermetic` restores `Xake` from
nuget.org and needs nothing else. When it needs a `src/core`/`src/dotnet` change that is not
released yet, pack the base into a folder at a version inside the range and hand that folder to
the restore as an extra source:

```bash
dotnet pack src/dotnet -c Release -p:Version=3.4.0.99 -o /tmp/xake-feed
dotnet fsi build.fsx -- -- build test pack -d NUGET_SOURCE=/tmp/xake-feed   # or export NUGET_SOURCE
# by hand: dotnet restore src/hermetic.tests -p:RestoreAdditionalProjectSources=/tmp/xake-feed
```

`NUGET_SOURCE` (unset by default) is passed to the hermetic build and pack as
`-p:RestoreAdditionalProjectSources=`. The packed version lands in the NuGet global cache
(`~/.nuget/packages/xake/3.4.0.99`) and wins over the released one from then on: delete that
folder when done, and release the base before the hermetic package that needs it.

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

The lowest supported SDK for `build.fsx` and the tests is **.NET 8.0**. These have to move
together:

| Where | Setting |
|---|---|
| `global.json` | not any more: it pins `10.0.401` exactly for the fsc build ([The SDK pin](#the-sdk-pin)) |
| `src/tests/tests.fsproj`, `src/hermetic.tests/hermetic.tests.fsproj` | `net8.0` |
| `.github/workflows/build.yml` | the `floor` leg removes `global.json` and builds on `8.0.x`; the `pinned` leg uses the pinned SDK |

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
pushed) or `build test pack-hermetic` for `hermetic-v*` (the base is not packed: the hermetic
package builds on the `Xake` already on nuget.org), and pushes **only the tagged package** to
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

A release of both is two tags and two workflow runs, **base first**: tag `vX.Y.Z`, wait for
`Xake X.Y.Z.<run>` to be on nuget.org, raise the `Xake` floor in
`src/hermetic/Xake.Hermetic.Dotnet.fsproj` to that exact published version (if the hermetic
package needs it; see the lower-bound rule above), merge, then tag `hermetic-v0.A.B`. If the
floor is not on nuget.org yet, the hermetic release fails at restore, before anything is pushed.

**The published version is not the tag.** The workflow appends the GitHub run number as a
fourth component, so tag `v3.3.0` publishes `3.3.0.<run>` and `hermetic-v0.1.0` publishes
`0.1.0.<run>`. That makes re-running a release harmless — the version is always fresh — but it
means the nuget.org version and the git tag never match exactly. Pushing uses
`--skip-duplicate`, so a re-run never fails on an already-published version.

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
Xake. `build.fsx` starts with `#r "nuget: Xake, 3.4.0.21"`; `build.fsc.fsx` (the fsc build of
record, which needs the lock API of the hermetic package) with `#r "nuget: Xake, 3.6.0.24"` and
`#r "nuget: Xake.Hermetic.Dotnet, 0.2.0.<run>"` (until `hermetic-v0.2.0` is on nuget.org, the placeholder `0.2.0`, which only a local feed provides). Those references are intentionally *behind* the
version being released: bump them only after a release has landed on nuget.org, to the exact
published version (`X.Y.Z.<run>`, not the tag; a bare `X.Y.Z` does not exist and resolves
upwards with NU1603), and only when the script needs what it brings. `build.fsc.fsx` needs a
`Xake` at least as new as the one the hermetic package was built on (0.2 needs 3.6), so its
`Xake` can be ahead of `build.fsx`'s; never behind.

After a `hermetic-vX.Y.Z` release, bump `build.fsc.fsx`'s `Xake.Hermetic.Dotnet` `#r` to the
published `X.Y.Z.<run>` (and its `Xake` `#r` to the floor that release needs), run
`dotnet fsi build.fsc.fsx -- -- check-locks build`, commit. The lock itself does not depend on
the script's references, only on the projects and the SDK.

To work on a script against *unreleased* libraries, there are two ways; `NUGET_SOURCE` is not
one of them (it is only the extra restore source of the hermetic `pack`, see above):

- a temporary `#r` on built dlls. `build.fsx` carries it as the commented
  `// #r "out/netstandard2.0/Xake.dll"`; for `build.fsc.fsx` point the `#r` lines at a copy
  (`/tmp/xake-dev/*.dll`), never at `out/`, because it overwrites `out/` and overwriting
  assemblies fsi has loaded kills the run with a `BadImageFormatException`:

  ```bash
  dotnet fsi build.fsx -- -- build
  mkdir -p /tmp/xake-dev && cp out/netstandard2.0/*.dll /tmp/xake-dev/
  # replace the #r "nuget: ..." lines of build.fsc.fsx with #r "/tmp/xake-dev/<name>.dll", then
  dotnet fsi build.fsc.fsx -- -- build test
  ```

- a local feed: pack at a version nuget.org does not have into a folder, add
  `#i "nuget: /tmp/xake-feed"` to the script and `#r` that version. The packed version lands in
  `~/.nuget/packages/` and shadows the release from there on; delete it when done.

Neither goes into a commit.
