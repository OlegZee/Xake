# PR 2: the import records msbuild's resolved compile inputs (2026-10-16)

Summary. The full report with the trial-project numbers is kept outside this repository.

## Design

- For an imported project Xake derives nothing: msbuild resolves references, defines, generated
  sources, resource names and compiler switches; the import records them; the build turns them
  into paths and runs the compiler. The composed route (`csc {}`, `Locks.generate`) is unchanged.
- `Import.record options projects` (`src/hermetic/Import.fs`): one restore of all projects, the
  evaluations for the description, then one msbuild process per configuration running the
  design-time builds of every (project, framework) through a generated traversal project
  (`ProvideCommandLineArgs`, `SkipCompilerExecution`, targets up to `Compile`). A targets file
  injected via `CustomAfterMicrosoftCommonTargets` writes the command line and items per build.
- `entryOf` strips the output switches, moves generated sources under `obj/xake/<key>/...`,
  records `.resx` pairs, sources in msbuild's order with the description masks beside them,
  project references as sibling outputs, hashes for references/analyzers, and always a
  `dotnet-sdk` prerequisite. Before writing: every file needs a portable identity and the text
  must hold no host string.
- Identities (`Lock.Identity`): `pkg:`, `pack:`, `sdk:`, `project:`, or a tokenized root path.
- Lock format 2: `"Format": 2`, a `Recorded` marker, `Id` instead of `Path` for recorded inputs;
  format 1 locks with imported entries are refused ("run the import again").
- Fixes on the way: `~` quoting in msbuild args, restore of several versions of one id in rounds,
  tokenized `Properties` and global.json pin.

## Tests

`dotnet fsi build.fsx -- -- build test`: tests 341 passed + 1 skipped, hermetic.tests 234 passed
(+8 in `src/hermetic.tests/RecordTests.fs`): identities and format round trips; an e2e import of a
two-project solution (import -> build -> import leaves the lock unchanged, `dotnet build` gives
the same outputs, an added `.cs` compiles without re-import); restore rounds.

## Verification

Verified on a large customer solution (94 projects, 4 build variants): every compile output
present on both sides, type and method counts equal to msbuild's, import -> build -> import
leaves every lock unchanged.
