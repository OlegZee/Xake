# Lock content: line endings, targeting packs, satellites

This analysis covers three roadmap items in `docs/roadmap.md`: "Line-ending-neutral hashes of msbuild files",
"Targeting packs from NuGet by default in `Import.record`" and "Satellite assemblies in imported
locks". Each was checked against `src/hermetic` and `src/dotnet` on `feature/hermetic-windows`
(2026-10-26). Read-only: nothing was run.

A note on the roadmap text itself: these three items (and "Secret script variables") name the consumer
by its repository name, a brand and its token variables. The public-repo rule asks for neutral wording
("the consumer solution"), so the roadmap needs that cleanup whichever item goes ahead.

## 1. Line-ending-neutral hashes of msbuild files

### Root cause: confirmed, and wider than the roadmap says

- Where the hash is recorded: `Import.fs:1501` `Imports = ctx.Inputs @ appSources |> List.map Csc.hashed`, raw bytes
  (`Csc.sha256`, `src/dotnet/Csc.fs:329`, over `Hash.sha256`, `src/dotnet/Hash.fs:9`). `ctx.Inputs`
  are the repository's msbuild files (`Import.fs:675-686` `repositoryFiles`: not the SDK's, not the package
  folder's, not under `obj/`). `appSources` adds `App.config` and
  `runtimeconfig.template.json` (`Import.fs:1488-1497`).
- Where it is checked: `Locks.staleOf` (`Locks.fs:440-443`, through `FileHash.sha256`, `Lock.fs:13-21`). Two callers use it:
  `staleWith`/`checkWith` (`Locks.fs:456-479`, the `check` target) and every replay through
  `staleImports` (`Locks.fs:488-504`, called at `Locks.fs:535-536`).
- Also re-hashed raw: `Lock.rehash` (`Lock.fs:541-543`, calls `Csc.sha256` directly and bypasses the memo) and
  the legacy `Project.import` (`Project.fs:312`).
- The lock text itself is LF-normalized before comparing (`Locks.fs:262`, `Import.fs:1032`, `Import.fs:1932`), and
  `Generated` text is stored as LF. That part of the roadmap is accurate.

The roadmap misses a second case. It is the same hazard, but it fails the build instead of `check`:

- `App.Files` beside the lock (`<lock>.files/<fwk>/app/*.runtimeconfig.json`, `*.deps.json`,
  `*.config`) are written as LF by the import (`Import.fs:1258-1277`, hashed over the LF bytes). `Apps.write` `checkHash`
  checks them raw (`Apps.fs:318-321`). On Windows with
  `core.autocrlf=true`, git checks these committed `.json`/`.config` files out with CRLF, and the build fails with
  "does not have the hash the lock records". The consumer may have escaped it only because of its
  `.gitattributes` or the shape of its apps. It is the same bug.
- The project's own config, copied as is (`ownConfig`, `Import.fs:1245`, a repository `App.config`),
  is an `App.Files` item hashed raw, so it has the same problem.
- Not affected: package files, references, analyzers and compilers (binaries from nupkgs or the SDK);
  binaries under `<lock>.files/` (`.licenses`, `g.resources`, which git's heuristic keeps binary);
  repository items in `App.Content` and sources (both unhashed).

Where the roadmap is inaccurate:
- "Byte for byte" is precise only through `FileHash`.
- Its list of text inputs misses `App.Files` and the project's own config.
- The memo key is not the only cache to change: `Lock.rehash` bypasses `FileHash`.
- A per-item `"Eol"` field costs more than it looks. `Hashed` is a Xake.Dotnet type
  (`src/dotnet/Csc.fs:211`), so a new field means a Xake.Dotnet release and the roadmap's rule on
  binary compatibility between the packages.

### Design options

**A. Bump the format to 3 and hash text roles normalized.**
- Record the hash after CRLF -> LF (a lone CR kept, the BOM unchanged) for `Evaluation.Imports` and the text items of `App.Files`.
- Check a format-3 lock normalized and a format-2 lock raw.
- Catch: `Lock.parse` refuses `format < FormatVersion` for imported entries (`Lock.fs:1169-1170`).
  Bumping to 3 makes every format-2 lock unreadable unless that guard becomes `format < 2`.
- The gain: an older package refuses a format-3 lock with a clear message (`Lock.fs:1161`).

**B. No bump: record normalized, accept either hash when checking.**
- `staleOf` and `checkHash` compute the normalized hash and accept a match on it *or* on the raw hash.
- Format-2 locks recorded on LF machines already carry hashes equal to the normalized ones, so they
  become portable without a re-import.
- Locks recorded on Windows (raw CRLF hashes) still match raw on Windows and become portable after
  one re-import.
- Costs: an older package on Windows reading a new lock reports a false "stale" (confusing, but not silent),
  and each file is hashed a second time when the raw hash does not match.

**C. Document `.gitattributes` instead.**
- Either document `*.props *.targets *.csproj *.projitems text eol=lf` and
  `<lock>.files/** -text`, or have the import write a `.gitattributes` into `<lock>.files/`.
- No code for the msbuild files, but it relies on every consumer repository and does not fix files
  already checked out.

**Recommended: B.** C's `<lock>.files/.gitattributes` can be added as a cheap extra if wanted.
- B needs no format change and no Xake.Dotnet change, and it makes existing LF-recorded lock sets portable without a re-import.
- Put the normalized hash in the hermetic package, not in `Xake.Dotnet.Hash`: a `FileHash.textSha256` beside `FileHash.sha256` in
  `Lock.fs`, with its own memo dictionary keyed like the raw one (or the mode added to the key).
- Decide what is text by role, not by extension:
  - everything in `Evaluation.Imports`;
  - the `App.Files` items the import wrote as text (`isRecordedText` or `.config`, `Import.fs:1041-1044`,
    `Import.fs:1912-1915`);
  - `ownConfig`.

Touch points:
- `Import.fs:1501` (the record).
- `Import.fs:1245` and `Import.fs:1276` (the app files).
- `Lock.rehash` (`Lock.fs:543`).
- `Locks.staleOf` (`Locks.fs:440-443`).
- `Apps.write` `checkHash` (`Apps.fs:318-321`).
- The legacy `Project.fs:312` (optional).
- `Lock.diff` (`Lock.fs:665`) needs no change: it compares the recorded strings.

What else changes:
- `check` and the replay follow `staleOf`.
- Restore, verify and the SBOM do not read `Imports` (the SBOM reads `Sdk`/`Properties`) and need nothing.
- Docs: `docs/hermetic/lock.md` should say that a lock set is portable across OSes and
  `core.autocrlf` settings.
- Windows vs macOS/Linux: the code path is identical; only the checkout differs.

### Effort, risks, tests

Effort: M, 2-3 days.

Risks:
- A file whose only change is its line endings is deliberately not stale (msbuild does not care either).
- UTF-16 msbuild files are rare and would need encoding-aware normalization. Hash them raw and say so.
- The memo must never hand a raw hash to a caller that asked for the normalized one.

Tests (Xake suites only):
- `staleOf` finds a CRLF copy of an LF-recorded file current, and the reverse.
- `staleOf` finds a file stale after a content change.
- A lone CR is not normalized.
- An old raw CRLF hash is still current on a CRLF checkout.
- The raw and normalized `FileHash` of one path in one process are each correct.
- `Apps.write` accepts a CRLF `runtimeconfig.json`.
- The record writes the normalized hash for a CRLF `Directory.Build.props`.

### Open questions

1. Bump the format or not? Suggested default: no bump (option B), accepting the false "stale" from older packages.
2. Include `App.Files` and the project's own `App.config` (the check at build time) in this change? Suggested default: yes.
3. Should the import write `<lock>.files/.gitattributes` (`* -text`)? Suggested default: no. Normalized
   hashing covers it; mention `.gitattributes` in the docs only.

## 2. Targeting packs from NuGet by default in `Import.record`

### Root cause: confirmed, with one correction

- Identity: `Lock.Identity.ofPath` (`Lock.fs:734-758`) gives both copies the same name, `pack:<id>/<ver>/<path>`.
  `isPack` (`Lock.fs:727-729`) covers `.App.Ref`, `.App.Host.<rid>` and `.App.Runtime.<rid>`. The hash
  is of whichever copy msbuild resolved (`Import.fs:1427` `hash`). The lock therefore changes back and forth between
  machines that have the pack installed and machines that download it.
- Correction: the replay already copes in one direction.
  - `toPathHashed` (`Lock.fs:826-830`) uses the installed copy only when its hash matches. Otherwise it takes
    the package copy, which `Restore.ensure` fetches (`restoreRequest`, `Lock.fs:1332-1347`).
  - So a lock recorded from NuGet builds everywhere.
  - A lock recorded from an installed pack fails on a machine without that pack version. It fails after the
    fetch, in `packMismatches` (`Lock.fs:1590-1615`, called at `Lock.fs:1682`).
  - In short: the lock churns both ways and fails hard one way.
- SBOM: a pack row's `Sha512` comes from the copy in the package folder. It is empty when only the installed
  pack was used (`Import.fs:1461-1480`). NuGet by default would therefore also complete the SBOM.
- Xake passes no `NetCoreTargetingPackRoot` today (no match in `src/`). The consumer's probe does.
- Version pinning:
  - `DotNetFwk.knownTargetingPackVersion` (`src/dotnet/DotNetFwk.fs:509-545`) reads
    `KnownFrameworkReference`, but only for `HermeticMode`.
  - With an exact `global.json` pin, the SDK already fixes the bundled pack versions, and the entry gets a `dotnet-sdk` prerequisite
    (`Lock.prerequisitesFor`, `Lock.fs:156-161`).
  - A separate pack pin therefore matters only for an unpinned SDK, which already warns (`sdkUnpinnedWarning`).

### Design options

**A. `RecordOptions.PacksFrom = NuGet | Installed`, with NuGet as the default.**
- The import adds `-p:NetCoreTargetingPackRoot=<a folder under temp that does not exist>` to `globals`
  (`Import.fs:1645`).
- The restore (which downloads the packs), the evaluations and the design-time builds then all see it.
- It applies to targeting packs, apphost packs (with a RID) and runtime packs. The SDK's own apphost (`SdkHost`) is
  unaffected.

**B. Post-process at record time.** Keep msbuild as is, then rehash each `pack:` file from the
package copy (fetching it). The lock is the same as with A, but the recorded hash is of a file msbuild did not read.

**C. Record both hashes for every pack file.** This doubles the pack lines and depends on what the recording machine has
installed.

**Recommended: A.**
- The consumer has already proven it, it takes one property, and msbuild's inputs are the
  recorded ones.
- No lock format change: the identities stay the same and only the hashes switch to the package's.
  `parse`, the replay and `packMismatches` stay unchanged.
- Every lock with a pack reference changes once, on the next import.
- The extra global also changes the `key cfg` intermediate folder (`Import.fs:1650-1656`), which is harmless.
- Windows vs macOS: the code is the same. Windows machines more often have side-by-side
  pack versions from Visual Studio, which is where the churn came from.

### Effort, risks, tests

Effort: S, 1 day plus docs.

Risks:
- The import needs feed access to the `Microsoft.*.App.Ref` packages. A private feed without a nuget.org upstream fails the
  restore; the error should say so.
- Each machine downloads tens of MB per pack version.
- A pack root that does not exist must not trip another SDK target. Verify on SDK 8 and 9:
  `ProcessFrameworkReferences`, `ResolveAppHosts`, `ResolveTargetingPackAssets`.

Tests:
- `RecordTests`: the property is in the switches by default and absent with `Installed`.
- A record test (needs the SDK): a `pack:` reference path lies under the package folder, and its `Packages` row has a
  `Sha512`.
- The existing `toPathHashed`/`packMismatches` tests stay.

### Open questions

1. Make NuGet the default? Every lock with a pack reference changes once, and imports need the packs on
   the feed. Suggested default: yes.
2. Keep `Installed` as an opt-in for offline imports? Suggested default: yes.
3. Add a pack version pin independent of the SDK (a `KnownFrameworkReference` override)? Suggested default: no.
   The exact `global.json` pin is the mechanism, and the unpinned-SDK warning stays.

## 3. Satellite assemblies in imported locks

### Root cause: confirmed; it is a known gap that was deferred on purpose

- Dropped at import: an `EmbeddedResource` with `WithCulture` is reported and left out
  (`Import.fs:410-411`, `Import.fs:443-444`). The main assembly's resx pairs skip culture items
  (`Import.fs:1415-1422`). The composed path refuses them (`src/dotnet/ProjectPlan.fs:301`).
- A referenced project's satellites are not copied. `appOf` drops files beside a referenced
  project's output (`Import.fs:1176-1180`). The run folder takes only files with `Related` extensions beside
  the referenced assembly (`Locks.fs:842`, `Locks.fs:878`), not culture subfolders.
- The earlier decision: "Satellite assemblies: deferred by decision (S71)" (`docs/hermetic/README.md:214-218`,
  `docs/features/hermetic-build/session.md:1315`).
  - It notes a regression against 0.3.7-0.3.8 for the package entries that shipped satellites, and missing `"resources"` lines in `deps.json`.
  - `lock.md:466`, `lock.md:644` and `pack.md:63` list satellites as not covered.
  - No reason is recorded beyond "a later step".
  - This roadmap item reopens S71, so it needs the owner's explicit go.
- Where the roadmap is inaccurate:
  - "`al`/csc": the import runs `dotnet msbuild`, where `GenerateSatelliteAssembliesForCore` is true on every OS. So it is always the `Csc` task in
    `CoreGenerateSatelliteAssemblies` (a generated attributes source plus the culture `.resources`).
    `al.exe` is only the full-framework MSBuild route.
  - It omits the run folder and the `deps.json` parts.
  - It omits the version: a satellite's `AssemblyVersion` must equal the main assembly's,
    which the build substitutes (`VersionToken`, `Locks.fs:401`, `Locks.fs:577-608`).
- Satellites from packages (`lib/<tfm>/ja/*.resources.dll`) already work:
  - They are copy-local items with a `DestinationSubDirectory`, recorded in `App.Copy`.
  - SBOM tier 1 already lists satellites shipped in a nupkg (`Sbom.fs:544`, `SbomTests.fs:397-474`).
  - Pack takes them once they exist (`Pack.fs:360-378`: a wildcard per folder works; `**` is refused).

### Design options

**A. A nested `Satellites` section in the entry, recorded from msbuild.**
- The import's targets file (`xake-record.targets`, `Import.fs:1745`) hooks `CoreGenerateSatelliteAssemblies` the same way `Compile` is
  captured today (`ProvideCommandLineArgs`/`SkipCompilerExecution`, `Import.fs:1753`).
- Recorded per culture:
  - the csc switches (key file, delay or public signing, deterministic);
  - the generated attributes source as `Generated` text, with the version token;
  - the culture resx -> `.resources` pairs, compiled by the existing byte-for-byte `Resx` (`src/dotnet/Resx.fs`).
- The compiler and references are the parent entry's.
- The replay writes `<out>/<culture>/<asm>.resources.dll` as side outputs of the assembly rule.

**B. Xake composes the satellite compile** from the culture resources and the main entry, with no capture. The import is simpler,
but Xake re-derives msbuild's call, which goes against the "record msbuild" principle. Drift in attributes and
signing switches is likely.

**C. One entry per culture.** This breaks the one-entry-per-framework invariant
(`Locks.fs:516-525`), run folders and the SBOM. Not recommended.

**Recommended: A**, with these additions:
- Run folders add each referenced lock's satellites by name, taken from the lock rather than by globbing `out/`.
- `deps.json` `resources` lines: write stand-in satellites next to the stand-in assemblies in the app pass
  (`Import.fs:1801-1824`, `stubAssembly`), so msbuild's own `GenerateDepsFile` lists them.
- Signing takes satellites like any other built file.

Lock format: a new optional field.
- An older reader ignores it (fields are read by name, `Lock.fs:1019-1021`) and builds without
  satellites, as today. No bump is needed; if item 1 bumps the format, this field rides along.
- Locks without culture resources do not change.

Windows vs macOS/Linux: `AssignCulture` depends on the machine's culture list.
- Under invariant globalization (`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`, Linux containers without ICU), a
  `Strings.ja.resx` is treated as a neutral resource (`critic-a1.md:51`). The same project then yields a different lock.
- The ICU and NLS culture lists also differ at the edges.

### Effort, risks, tests

Effort: L, 6-9 days.

Risks:
- The exact `Csc` parameters of `CoreGenerateSatelliteAssemblies` differ between
  SDK 8 and 9: capture them, do not guess.
- Signing parity: the satellite's public key token must match the main assembly's.
- The version substitution.
- Run-folder conflicts in culture subfolders.
- The invariant-globalization trap above.

Tests:
- A write/read round trip of `Satellites`.
- A lock without culture resx stays byte-identical to today's.
- Recording a fixture with `Strings.ja.resx` and `Strings.zh-CN.resx` (C#; F# if fsc supports the route).
- The replay yields `ja/X.resources.dll` with `AssemblyCulture("ja")` and the main assembly's version and
  public key token.
- A referenced project's satellites appear in the run folder and in the `deps.json` `resources` lines.
- `Pack.fromNuspec` picks them up.
- The import fails or warns under invariant globalization.

### Open questions

1. Reopen S71 now, covering both a project's own satellites and a referenced project's? Suggested default: yes, both.
   Without the referenced part, the run folders of localized apps are wrong.
2. Capture msbuild's satellite `Csc` call (A) or compose it (B)? Suggested default: capture.
3. When culture-suffixed resx files meet invariant globalization: fail the import or warn? Suggested default: fail.
4. Are F# projects in scope? Suggested default: C# first, F# when a consumer needs it.

## Interactions

- Re-imports: items 1 and 2 each change existing locks on the next import (normalized hashes for sets recorded on Windows,
  package hashes for packs), and item 3 adds a field. Land them so the consumer re-imports once.
  If item 1 ends up bumping the format, put item 3's field under the same bump.
- Item 2 feeds item 3: satellite compiles share the parent's references, so their pack hashes are exactly as
  portable as the parent's.
- Item 1's normalized hashing must not touch any binary role. Satellite `.resources` are compiled
  by Xake, not hashed, and resx sources stay unhashed.
- Item 3 reverses a recorded decision (S71). Items 1 and 2 reverse no earlier decision.
