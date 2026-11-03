# PR 4: run folders driven by the lock (2026-10-16)

Summary. The full report with the trial-project numbers is kept outside this repository.

## Design

- The import records more: `App.Content` (copy-to-output items at their `TargetPath`; a
  repository file by path, a package's by identity and hash), an `App` for a library msbuild
  copies into, `IsTestProject`. Copy items under obj/out (written by build targets) are left out.
- The compile writes only what is named after the assembly (runtimeconfig, deps.json, config,
  apphost); copy-local files belong to the run folder.
- `Locks.runFolderWith extraRoots mode lockOf outOf cfg fwk name`: target is the manifest
  `.xake-run.txt`. Inputs: the root's outputs, content and copy-local; then the project closure
  (assemblies with related files, referenced apps' runtime files, content, non-package copy
  items). Package files come from the root's restore only, as msbuild does.
- `RunFolder.plan`: root first, higher assembly version wins in the closure, conflicts reported.
  `RunFolder.apply`: clone by default, hard links opt-in, copy fallback; never writes into a placed
  or foreign file; every path stays inside the folder; stale files removed by stamp; a no-op
  writes nothing.
- `Locks.staleOf`/`staleWith`/`checkWith`/`check`: lock staleness for a script's check target.
- WPF projects with XAML items are refused (not supported yet).

## Tests

hermetic.tests green (+ `src/hermetic.tests/RunFolderTests.fs`): placing, precedence, links,
no-op, copy mode; e2e run folders equal `dotnet build`'s bin for a console app, a test project,
a net472 WinExe; `dotnet test` from the run folder passes; edit -> build -> deploy in separate
runs refreshes the folder; path, case and stale-removal hazards.

## Verification

Verified on a large customer solution (94 projects, 4 build variants): 77 run folders, extra
files 0, missing files only the known open items (satellites, binding redirects, files written by
build targets), deploy no-op about 2 s.
