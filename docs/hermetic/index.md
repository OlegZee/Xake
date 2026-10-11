# Xake.Hermetic.Dotnet docs: index

Product reference of `Xake.Hermetic.Dotnet` (`src/hermetic/`), version 0.2.0.25 on Xake
3.6.0.24. Up: [../index.md](../index.md). The maintainer view is
[../hermetic-architecture.md](../hermetic-architecture.md), the task-oriented guide
[../hermetic-guide.md](../hermetic-guide.md). Lengths are approximate line counts.

## Entry and flows

| File | What it is | Read when | Lines | Date | Status |
|---|---|---|---|---|---|
| [README.md](README.md) | package entry: what 0.2 adds and breaks, the pieces, two ways in (composed `csc {}`/`fsc {}` with `lock`, or `Project.import`), building from source | first look at the package | ~170 | 2026-10-11 | current |
| [workflows.md](workflows.md) | team flows: A composed lock, B imported msbuild project, C fresh machine, D CI, E release, F offline, G troubleshooting, gaps | deciding how a repo lives with locks; §B for import-based builds like `build.fsc.fsx` | ~750 | 2026-10-11 | current; header and snippets still name 3.4 / 0.1 versions |
| [hermetic-mode.md](hermetic-mode.md) | `HERMETIC=on`: the invariant (no path outside `$(ProjectRoot)`/`$(NuGetPackageRoot)` unless a prerequisite), messages 1-11, pending (SDK restore, F# toolset, netN.0 imports) | the mode or the SDK pin comes up | ~180 | 2026-10-11 | current, shipped in 3.6 / 0.2 |

## The lock

| File | What it is | Read when | Lines | Date | Status |
|---|---|---|---|---|---|
| [lock.md](lock.md) | the lock: format, "API at a glance" table, F# entries, prerequisites, hermetic mode, replay (`Lock.compile`), `Project.import`, `csc/fsc { lock }`, CI flag, `nofetch` | any work with locks; the API table (top ~60 lines) is usually enough | ~990 | 2026-10-11 | current |
| [restore.md](restore.md) | `Restore`: fetch the packages a lock names into a chosen folder, verify sha512 | clean machine, package folder, `NUGET_PACKAGES`/`NUGET_FETCH` | ~240 | 2026-10-11 | current |

## Evidence and release steps

| File | What it is | Read when | Lines | Date | Status |
|---|---|---|---|---|---|
| [nuget-sbom.md](nuget-sbom.md) | `Nuget` (assets graph, cache metadata) and `Sbom` (CycloneDX 1.6 from the lock, package scope) | SBOM work; the six open SBOM questions | ~385 | 2026-10-06 | current |
| [verify.md](verify.md) | `Verify`: Authenticode PE hash, labelled byte differences | comparing a shipped dll with a local build | ~85 | 2026-09-30 | current |
| [pack.md](pack.md) | `Pack`: deterministic zip and nupkg from a nuspec and file list | packing without `dotnet pack` (hermetic nupkg) | ~80 | 2026-09-30 | current |
| [strongname.md](strongname.md) | `StrongName`: PE stamp, checksum, strong-name re-sign matching csc | obfuscation (Babel) or strong-name questions | ~70 | 2026-09-30 | current |
| [signing.md](signing.md) | `Sign`: Authenticode and nupkg signing as a delegated rule, fake-signer skeleton | signing design | ~255 | 2026-09-30 | current, skeleton only |
