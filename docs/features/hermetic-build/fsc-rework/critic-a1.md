# Critic: round A1 (`pr/import-fidelity`, a36bcdd..b165cb4)

Checked: `dotnet build src/hermetic --no-incremental` -> `0 Warning(s)`, `0 Error(s)`;
`dotnet test src/hermetic.tests` -> `Passed! - Failed: 0, Passed: 211, Skipped: 0, Total: 211`.
`Lock.fs`, `Roots.fs`, `src/dotnet`, `src/core` untouched: the lock format, the reader/writer and
the golden round-trip are unaffected; no engine change.

## Verdicts

| change | verdict | grounds |
|---|---|---|
| model + schema: `options.noWarn`, `assemblyAttributes`, `$defs.attribute`; `resources[].name` reused as manifest | SHIP | `Description.fields` and the schema agree; the sync test now covers `attribute`. Reusing `name` (already "manifest name") is right; the plan's `manifest` key would duplicate it |
| `project` compiles `.resx` (`/resource:<obj>/<manifest>,<manifest>`, pair into `Csc/Fsc.Resources`) | SHIP WITH FIX (F1) | reuses the existing runner slot, nothing hashed, resx `needFiles`d by the runner; `.resources` byte-equal to msbuild's in the fixture |
| `Described.manifestName` (msbuild rule) + default root namespace = `name` (was `assemblyName`) | SHIP WITH FIX (F1) | matches `CreateCSharpManifestResourceName`; F# SDK sets `UseStandardResourceNames=true` (10.0.401 props), so the same rule holds for F#. The root-namespace change is a bug fix but changes manifest names of hand-written descriptions with `assemblyName` != `name`, plain resources, no `rootNamespace`: say so in release notes |
| `Described.refreshResources` (delete `.resources` older than its resx) | SHIP | fixes a real stale-resource bug. `<cfg>` is always in the path for lock builds (`Locks.generate` always passes `config`), so Debug/Release never share a folder; only two `project` rules without `config` for the same assembly+fwk would race on one `.resources` (identical content; possible IO error). Prefer the runner fix (report Q1) in a later `src/dotnet` round |
| `/nowarn:` = SDK defaults + `noWarn`; `/warnaserror+` only with `warningsAsErrors` | SHIP | not a format change (same `Options` slot); it changes lock *content*: every C# lock gains one line on its next `update-locks`, once. No C# lock is committed in this repo; F# adds nothing, so Xake's 3 locks stay byte-identical. Matches msbuild and is needed for `TreatWarningsAsErrors` |
| generated file `<asm>.InternalsVisibleTo` vs `<asm>.AssemblyAttributes` | SHIP | a pure function of the description: stable; friend-only locks keep their text |
| Import: metadata case-insensitive | SHIP | msbuild metadata names are case-insensitive, so no case was meaningful; exact match is tried first |
| Import: `NoWarn` minus SDK defaults | SHIP | a project cannot remove 1701/1702 (it appends to `$(NoWarn)`), so re-adding them is exact |
| Import: `AssemblyAttribute` -> `assemblyAttributes` (WriteCodeFragment rules) | SHIP WITH FIX (F2) | good coverage of `_ParameterN`, `_IsLiteral`, `_TypeName`, named args, unknown types |
| Import: `EmbeddedResource` (culture refused, `DependentUpon`/same-named `.cs` reported) | SHIP WITH FIX (F3) | culture satellites: refused with a `Dropped` line, not silent; DependentUpon: kept with the default name and reported (not silent, may be wrong) |
| `ImportFidelityTests` | SHIP | the integration test really runs `dotnet build` on the fixture and compares manifest names and `.resources` bytes with msbuild's, plus resx-edit rebuild and G13 re-import |
| docs (`description.md`, `csc-syntax.md`, schema) | SHIP | every new field and the manifest rule documented |

## Required fixes (small)

- **F1** `Described.everett`: the "single `_` folder becomes `__`" check tests the whole builder
  (`b.ToString () = "_"`), not the current folder. msbuild (`MakeValidEverettFolderIdentifier`)
  checks the folder's own span, so `a/_/x.txt` (or `a/-/x.txt`) is `Root.a.__.x.txt` in msbuild and
  `Root.a._.x.txt` here. Record the builder length before each folder and compare that span. Add the
  case to ``manifest names are msbuild's``.
- **F2** a `\` in an attribute argument is silently rewritten to `/` in a lock (`Roots.tokenizeAll`
  on `Generated`), so the lock-built assembly differs from the `project`-built one. Until the writer
  is fixed (report Q2), refuse it loudly: Import drops such an attribute with a `Dropped` line, or
  `Locks.generate` fails when a `Generated` text contains `\`.
- **F3** `EmbeddedResource` `Link` (and `ManifestResourceName`) metadata is ignored silently: msbuild
  names a linked resource after its `Link` path, Import writes the file path and `project` derives a
  different (wrong) name. Either write `name = manifestName rootNamespace Link` or report it like
  `DependentUpon`.

## Nits

- `WarningsAsErrors`/`WarningsNotAsErrors` (lists) are not carried: reported as `Dropped` when a
  repository file sets them, silently ignored when the SDK or an import outside the repo does. Say
  so in `description.md` (import section).
- `assemblyAttributes[].args` is source text pasted into generated code: by design, same trust as
  `sources`/`options.args`; one sentence in `description.md` would make that explicit.
- `project` does not refuse a hand-written culture resx (`X.de.resx`): it would embed it in the
  main assembly. Import refuses; `project` could fail with the same message.
- Culture detection uses `CultureInfo.GetCultures`, which is near-empty under
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`: satellites would then be imported as plain resx.
- Type inference for attribute arguments depends on the types the importing process can load
  (`Type.GetType`): an attribute from another assembly falls back to strings; output can differ by
  host. Acceptable, documented ("when the importing process knows it").
- Renaming `.InternalsVisibleTo` -> `.AssemblyAttributes` leaves the old file in `obj/xake/`; harmless
  while C# masks exclude `obj/**` (import default), not if a hand mask includes it.
- `refreshResources` misses a resx restored with an older mtime (`cp -p`); the engine reruns the
  compile but embeds the stale `.resources`. Another argument for Q1 (runner compiles on every run).
- Tests: no import test for a culture resx or `DependentUpon` drop line; no F# resx build.
