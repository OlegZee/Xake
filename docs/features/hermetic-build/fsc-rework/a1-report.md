# A1 report: import and project cover resx, NoWarn, assembly attributes, metadata case

Branch `pr/import-fidelity` (worktree `.claude/worktrees/pr/import-fidelity`, base `origin/dev` 0139682),
5 commits a36bcdd..b165cb4, not pushed. No change in `src/core` or `src/dotnet`. Lock format unchanged.

## Per gap

- **G1 `.resx`**: `csc/fsc { project }` (so `Locks.generate`/`compileAs`) embeds a description's `.resx`
  as `/resource:<obj>/<manifest>,<manifest>` and adds the pair (resx, `.resources`) to the existing
  `Csc.Resources`/`Fsc.Resources` slot; the lock keeps the resx path (tokenised), the manifest lives in
  the `/resource:` switch, nothing hashed. The existing runner step compiles it with `Xake.Dotnet.Resx`
  (when missing) and `needFiles` the resx. Output: `obj/xake/<assembly>/<fwk>/<cfg>/<manifest>`.
  New `Described.refreshResources` (called by `project`'s run and `Locks.compileAs`) deletes a
  `.resources` older than its resx, because the runner only compiles a missing one (an edited resx
  reran the compile but kept the stale `.resources`: pre-existing runner gap, fixed on the hermetic side).
  `Res\*` / `LogicalName` plain resources unchanged.
- **G2**: `Import` reads item metadata (and `Identity`) case-insensitively.
- **G3**: `options.noWarn`; Import = `NoWarn` minus SDK defaults; `project` emits `/nowarn:` = SDK
  defaults then the description's (distinct), always for C#; `/warnaserror+` only with the existing
  `options.warningsAsErrors` (no new field needed).
- **G4**: `assemblyAttributes: [{ type, args }]`; Import from `AssemblyAttribute` items (non-IVT);
  generated into the same file as the friends, friends first, description order.
- **G13**: re-import of the three self-build descriptions with the local build (scratch script
  `#r` of `src/hermetic/bin/.../Xake.Hermetic.Dotnet.dll`, since `build.fsc.fsx` uses the published
  0.3.0.28): `written=false` for all three, `git status` clean. The fixture test re-imports: not written.

## Schema additions (`docs/schemas/xake.project.schema.json`, `Description.fs`)

- `options.noWarn: string[]` (after `warningsAsErrors`)
- `assemblyAttributes: [{ "type": string, "args"?: string[] }]` (after `internalsVisibleTo`), `$defs.attribute`
- `resources[].name` kept as the manifest field (no new `manifest` key; see deviations)

## Manifest-name rule as implemented (`Described.manifestName`)

msbuild's `CreateCSharpManifestResourceName` without DependentUpon/culture: `rootNamespace` (else the
project `name` with spaces -> `_`, msbuild's default; previously Xake fell back to `assemblyName`, wrong),
then the folder with `/` -> `.` and each dot-part made an Everett identifier (invalid char -> `_`,
non-identifier start -> leading `_`: `1st Folder` -> `_1st_Folder`), then the file name; a `.resx`
without extension plus `.resources`. `name` (LogicalName) overrides. Because the rule is msbuild's,
Import writes `name` only from `LogicalName`. Import reports (Dropped) and omits culture resources
(`X.de.resx`, satellites); it reports but keeps (default name) a resx with `DependentUpon` or a
same-named `.cs` beside it (msbuild names it after the class).

## Attribute arguments (Import)

As `WriteCodeFragment`: `_ParameterN` positional, other metadata named (`Name = v`), `_IsLiteral`
verbatim, `_TypeName` honoured, else the first constructor of the type known to the importing
process that takes all values (CLSCompliant `true` -> `true`), else strings. Strings with `"` or `\`
become verbatim `@"..."`: **the lock writer (`Roots.tokenizeAll`) turns every `\` in `Generated`
content into `/`**, so `\"` escapes broke (found by the test). A value containing `\` is still mangled
in a lock (pre-existing; not fixed, would touch the writer).

## What the AR script may drop

`fixup` entirely (resx rewrite, `pin` of lower-case `version`, the CLSCompliant source file, the
`/nowarn` arg) and the `obj/resx/...resources` rule; the `.resources` names equal msbuild's for
Rdl/Rendering/Excel/Html folders (plain names). Still needed: `transitive` (G5), `frameworkRefs` (G6),
`1589` in nowarn (G7). Not re-run on AR here (no AR build was in scope); the fixture test covers it.

## Commands

- `dotnet build src/hermetic --no-incremental`: netstandard2.0 + net462, `0 Warning(s)`, `0 Error(s)`.
- `dotnet test src/hermetic.tests`: `Passed! - Failed: 0, Passed: 211, Skipped: 0, Total: 211`.
- `src/tests` not run (no `src/dotnet` change).
- New tests (`ImportFidelityTests.fs`): fixture C# project (2 resx, one under `1st Folder/`, `LogicalName`,
  `Res\*`, CLSCompliant + AssemblyMetadata, NoWarn 1591;0618 + TreatWarningsAsErrors + doc) ->
  import (fields asserted) -> lock -> build; manifest names equal `dotnet build`'s, `.resources`
  byte-equal to msbuild's, attributes present, resx edit recompiled; case-insensitive metadata;
  WriteCodeFragment argument cases; manifest names; composed `/nowarn`, `/warnaserror+`, generated file.

## Deviations

- Manifest field: reused the existing `resources[].name` (already "manifest name") instead of adding
  `manifest`; a second field would duplicate it.
- `.resources` path `obj/xake/<assembly>/<fwk>[/<cfg>]/` (not `obj/<cfg>/<fwk>/<name>/`): beside the
  generated attributes file; assembly name (brand-safe) and config (no Debug/Release write race).
- Generated file is `<asm>.InternalsVisibleTo.cs|fs` while it holds only friends (existing locks
  unchanged), `<asm>.AssemblyAttributes.cs|fs` when it has attributes.
- C# compiles now always carry `/nowarn:1701,1702` (as msbuild), so existing C# locks change by one line.
- F# SDK default NoWarn: none (SDK 8.0.425 and 10.0.401 props set none for F#; msbuild evaluates
  `NoWarn` empty for Xake's fsproj).

## Questions

1. Fix the runner (`CompilerRunner`: compile when missing *or older than the resx*) in `src/dotnet`
   instead of `Described.refreshResources`? It affects composed and imported resx too.
2. Should the lock writer stop rewriting `\` inside `Generated` content (writer change, not format)?
