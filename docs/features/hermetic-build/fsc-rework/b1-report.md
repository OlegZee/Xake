# B1 report: transitive refs, refs' packages, framework assemblies, compile directory, xml, A1 leftovers

Branch `pr/lock-fidelity` (worktree `.claude/worktrees/pr/lock-fidelity`), stacked on `pr/import-fidelity`
b165cb4 (A1). 7 commits c166650..9bf45d6, not pushed. No `src/core` change; lock format unchanged.

| commit | gap |
|---|---|
| c166650 | G5 transitive project references in `Locks.generate` |
| 3fb4cea | G14 `Described.restoreWith`: packages of project references, direct route |
| 3375a5e | G6 `frameworkAssemblies` of packages referenced on .NET Framework |
| f7c3be3 | G7 compiler runs in the project folder (`Directory`) |
| d1a27dd | G12 `Locks.compileAs` serves an `.xml` rule |
| 2f80ca3 | A1 leftover 1: runner recompiles `.resources` (`src/dotnet`) |
| 9bf45d6 | A1 leftover 2: lock writer keeps `\` in `Generated` content |

## Per gap

- **G5**: for each ref B, `generate` also takes the unhashed references of B's lock entry (B's project-ref
  outputs, already B's closure since B's lock was generated the same way) and appends them after the direct
  outputs, `List.distinct`, order stable. They are references (unhashed) and `compileAs` needs them first, as
  before. `Evaluation.ProjectRefs` keeps only the direct names. Test: A3 -> B3 -> C (F#, A3 uses C's record
  through B3, names only B3) generates and builds; C's output follows B3's in A3's lock.
- **G14**: new `Described.restoreWith descriptionOf d fwk` = `restore` plus `Described.flowingPackages`: the
  closure of `projectRefs` (breadth first, each once), each description read (a dependency) at
  `descriptionOf name`, its packages for its nearest framework, at the declared version, not `private`, not
  an id `d` declares (first wins). `restore` is unchanged (own packages only); `Locks.generate` keeps flowing
  from the refs' locks. `packages` now passes every reference of the restore even when the description
  declares no package, so `Locks.generate`'s `ref restoredRefs` workaround is gone. `nearestFramework` moved
  to `Described` (`Locks.nearestFramework` delegates). Test: direct `csc { project }` of A -> B, only B declares
  System.Memory 4.6.0, A's source uses `Span<byte>` from it: `restore` gives A nothing, `restoreWith` compiles.
- **G6**: `Nuget.Assets.FrameworkAssemblies` (the target's `frameworkAssemblies`, distinct case-insensitive,
  file order); `Described.Restored.FrameworkReferences`; `project` adds them to the global references (resolved
  like `implicitFrameworkReferences`) for NETFRAMEWORK TFMs. Flows into locks automatically (via `packages`).
  Tests: unit on a synthetic assets file; DocumentFormat.OpenXml 3.2.0 on net472 compiles with `WindowsBase.dll`
  among the references, and without `FrameworkReferences` fails with exactly AR's `CS0012 ... 'WindowsBase'`.
- **G7**: `Described.fsc/csc` set `Directory = ProjectDir`; the lock writes `"Directory": "$(ProjectRoot)/p/Doc"`;
  the runner already used it as the working directory. `--pathmap` unchanged; `Lock.diff` ignores `Directory`, so
  existing locks do not drift, but regenerating them changes that line. Test: C# `/// <include file='../docs/Common.xml' .../>`
  with doc on; the `.xml` holds the included text.
- **G12**: chosen form = a second rule over the `.xml` with the same body. `compileAs` on a `.xml` target needs
  the `.dll` beside it (whose rule writes the doc), fails "asks for no XML doc" when the entry has no `/doc`/`--doc`,
  and fails clearly if the xml is missing while the dll is up to date. `needAll` needs the dlls only, so "Debug
  without doc does not rebuild" holds (test unchanged and green). Not `targets [dll; xml]`: the engine reruns a
  rule while any declared target is missing (`DependencyAnalysis`, "target file does not exist"), so a no-doc lock
  would rebuild forever. Documented in `lock.md`. Test: B.xml needed first builds B, second run no-op, A.xml (no doc) refused.

## G14 `descriptionOf` choice

Explicit parameter on a new function, `Described.restoreWith descriptionOf d fwk`, not a sibling-folder
convention: the self-build's names are not folder names (`Xake` lives in `src/core`), and the lock rule already
has the same `descriptionOf`. Restore is the script's call, not the `project` op's (`fsc/csc` never restore), so
the op itself is unchanged. Direct AR script: `let! pkgs = Described.restoreWith descOf d fwk`.

## Runner change for resources

`CompilerRunner.run` now calls `Resx.refresh resx resources` for every pair each time it runs: compiles to memory,
writes only when missing or the bytes differ (no timestamp comparison, so not a second rebuilder; an unchanged
`.resources` keeps its stamp). The resx stays a `needFiles` input, so the engine decides when it runs. Test in
`src/tests/DotnetTasksTests.fs` (composed resx): edit -> rerun, new string in `.resources` and exe, then a no-op.

## A1 leftover 2

`Roots.tokenizeText` (roots only) for `Generated` values; `tokenizeAll` (`\` -> `/` then roots) for paths, as before.
Test: `AssemblyDescription("a\\b \"q\"")` round-trips; a resource path `C:\r\S.resx` still becomes `C:/r/S.resx`.

## What the AR script may drop

Lock route (`ar-build.fsx`): `transitive` and its call (G5); `frameworkRefs` and the OpenXml `byFramework` block of
`fixup` (G6); `1589` in `noWarn` (G7; with A1 the whole `noWarn` string goes to the description). The pack rule may
`need` the `.xml` files directly once it has the `.xml` rule (G12). With A1 that removes `fixup` entirely, leaving
the draft's shape plus pack. Direct route (`ar-direct.fsx`): `restoreWith descOf` instead of `restore` (G14: drop
the hand-added System.Text.Json/System.Memory); `WindowsBase` in descriptions (G6); `1589` (G7). Not re-run on AR.

## Build and tests (verbatim)

- `dotnet build src/hermetic --no-incremental`: `0 Warning(s)` `0 Error(s)` (netstandard2.0 + net462)
- `dotnet build src/dotnet --no-incremental`: `0 Warning(s)` `0 Error(s)`
- `Passed!  - Failed:     0, Passed:   218, Skipped:     0, Total:   218, Duration: 1 m 15 s - hermetic.tests.dll (net8.0)`
- `Passed!  - Failed:     0, Passed:   324, Skipped:     1, Total:   325, Duration: 37 s - tests.dll (net8.0)`
- Every commit builds (`src/hermetic.tests`, `src/tests`: 0 warnings, 0 errors); suites run on the last commit.

## Deviations

1. **`Described.refreshResources` kept** (with a removal note): `Xake.Hermetic.Dotnet` builds against the
   published Xake `[3.7.0.26, 3.8)`, whose bundled `Xake.Dotnet` runner still compiles only a missing
   `.resources`; removing it now fails A1's resx-edit test. Drop it when the floor moves to a Xake with 2f80ca3.
2. **G12** as an `.xml` rule, not `targets [dll; xml]` (reason above).
3. **G14** as `restoreWith` (a function), not a parameter of the `project` op.
4. `packages` passes all restore references even without declared packages (needed by G14; removes the
   `Locks.generate` workaround; lock text unchanged for that path).

## Questions

1. Direct route and G5: `csc { ref ...; project }` gets only the refs the script passes; the AR direct script
   passes `d.ProjectRefs`, so it still needs the closure. Add `Described.projectRefClosure descriptionOf d`
   (same walk as `flowingPackages`) for the script's `ref`, or have `project` warn on a missing transitive one?
2. Release a Xake 3.7.x patch with the runner fix so the hermetic floor can move and `refreshResources` go?
3. Regenerated locks change: `Directory` becomes the project folder, referencing projects gain transitive
   outputs (self-build: Xake.Hermetic.Dotnet). Regenerate the committed self-build locks in this PR or the next?
