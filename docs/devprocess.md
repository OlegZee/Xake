# Development process

This document describes the practical workflow for developing and releasing Xake.

## Requirements

- .NET SDK 9.0 or newer
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

Set `NUGET_KEY` in environment.

Create package:

```bash
dotnet fsi build.fsx -- -- pack -d Version=3.0.0
```

Push package:

```bash
dotnet fsi build.fsx -- -- push -d Version=3.0.0 -d NUGET_KEY=$NUGET_KEY
```

## Release

1. Merge `dev` into `master`
2. Tag release with `v` prefix
3. Push tag

```bash
git checkout master
git pull
git tag v3.0.0
git push --tags
```