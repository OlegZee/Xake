# Implementation notes

This file keeps high level technical notes about Xake internals.

## Build model

Xake scripts define rules for targets. During execution, the engine records dependencies and stores build state in `.xake`.

A target is rebuilt when at least one condition is true:

- output does not exist
- dependency files changed
- dependency target was rebuilt, by this run or an earlier one (its change stamp moved: see below)
- variable or environment dependency changed
- recipe requests rerun by `alwaysRerun`

## Dependency storage

The build database stores:

- target graph
- requested dependencies
- file list snapshots from filesets
- variable and environment reads

This enables incremental rebuild and `--dryrun` estimation.

A dependency on a target that has a rule is stored as `ArtifactDep2 (target, stamp)`: the dependee's
change stamp as seen right after it was built or found up to date. For a file target the stamp is the
file's last write time; for a phony target it is the completion time of its last build
(`BuildResult.Built`). The dependent is out of date when the stamp moved (a file's write time differs
beyond the 10 ms tolerance; a phony's completion time is later) or when the dependee itself is out of
date. So a dependee rebuilt by an earlier separate run, or an earlier command-line target group,
still triggers its dependents.

The database file starts with a version header (`BuildLog.XakeDbVersion`, now `0.6`). Versions from
`XakeDbMinReadVersion` (`0.4`) on are read; an older-version file is rewritten with the current header
on open. A `0.4` database holds the stampless `ArtifactDep`, which counts as changed once, so each
such dependent rebuilds one time and records `ArtifactDep2`. Older databases are discarded.

## Rules and scheduling

- `<==` demands dependencies in parallel
- `<<<` demands dependencies in sequence
- worker count defaults to CPU core count and can be overridden by `-t`

Parallel execution still respects dependency order.

## Filesets

Filesets support Ant style masks, recursive `**`, and named match groups in rule patterns.

Common behavior:

- path separators `/` and `\\` are accepted
- file list result is tracked as dependency input
- change in resolved file list can trigger rebuild

## Recipe behavior

`recipe` computation supports standard control flow and async style composition with `do!` and `let!`.

Notes:

- `do!` works in `with` blocks
- `do!` does not work in `finally` blocks due to F# computation expression limits
- `WhenError`, `FailWhen`, and `CheckErrorLevel` are available for explicit error flow

## Logging

Xake supports console and file logging with separate verbosity levels.

Log levels:

- `Silent`
- `Quiet`
- `Normal`
- `Loud`
- `Chatty`
- `Diag`

## Runtime and platform

The current repository targets modern `dotnet` workflow with .NET SDK 8.0+ (see `global.json`).

The build script itself always runs on modern .NET. Full framework and Mono are compilation
*targets* handled by the `Xake.Dotnet` tasks; see [dotnet-build.md](dotnet-build.md) for how
that toolchain is discovered and selected.

## Teardown behavior

Teardown rules are defined in script and run during shutdown when engine mode calls `StopAsync`.

In CLI script mode the `teardown` targets run automatically after the build, whether it succeeded or failed (`ScriptRunner.fs`). A teardown failure is logged; it fails the run (exit code 2) only when the build itself succeeded, otherwise the original build error is kept.
