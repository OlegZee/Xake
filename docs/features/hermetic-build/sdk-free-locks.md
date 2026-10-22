# SDK-free locks: the compiler and the analyzers as packages (agenda, 2026-10-21)

Status: Xake side implemented in PR 83 (Xake.Hermetic.Dotnet 0.6.0). Per step:
1. done: `Lock.withRecordedPrerequisites` / `recordedSdkVersions`, applied in `Import.record`
   (`entryOf`) and `Lock.rehash`; the replay's probe checks the `$(DotnetRoot)` the lock is read
   against (`Lock.unmetPrerequisitesAt`). Tests: `RecordTests.fs` (pure), `SdkFreeImportTests.fs`.
2. done: a `pack:` resolves to the copy with the recorded hash (S78, `Identity.toPathHashed`,
   `Lock.packMismatches`), `Restore.ensure` fetches the package; tested with a net6.0 project
   and in `RecordTests.fs`. Finding: an installed pack is not byte-identical to the
   nuget.org package (signed; facades and source generators are other builds: 8.0.32, 65 of 234
   files differ beyond the signatures), so a lock recorded from `packs/` still needs that pack
   installed. Since S78 (below) a `pack:` resolves to whichever copy has the recorded hash.
3. done: a net472 + netstandard2.0 fixture on the toolset compiler and package analyzers, no
   `sdk:`, no prerequisite, byte-equal to `dotnet build` (DebugType none). Caveats: a project
   setting `EnableNETAnalyzers=true` in its body gets the SDK's analyzers too; the toolset
   package 5.9.0 is another build than SDK 10.0.402's Roslyn (5.9.0-1.26357.3 vs
   5.9.0-1.26505.111).
4. a note in lock.md (Prerequisites).

Owner's decision of 2026-10-21: the way out of the SDK drift is to pin
the compiler and the analyzers (the "rules") as packages, so a lock names nothing under
`$(DotnetRoot)/sdk/<v>/` and carries no `dotnet-sdk` prerequisite. A lower bound on the SDK
version was considered and set aside as unlikely to hold.

## Probe on a consumer solution (2026-10-21)

A consumer solution (one brand, one configuration, 180 locks) re-imported with the product
declaring `Microsoft.Net.Compilers.Toolset` + `RoslynCompilerType=Toolset`,
`Microsoft.CodeAnalysis.NetAnalyzers` and `NetCoreTargetingPackRoot` pointing at a missing
folder (so msbuild takes the targeting packs from the package folder): `sdk:` identities 540 -> 0,
prerequisites 180 -> 1, output byte-identical to msbuild (561/561 files).

Two gaps, fixed on the same PR:

- **S78 (blocker):** the import named a pack file msbuild took from the package folder by the
  same `pack:` identity as the installed pack, with the package's hash; the replay resolved
  `pack:` to `<dotnet>/packs` first, so on a machine with that pack installed the hash check
  failed (24 errors; it built only against a .NET root without `packs/`). Now a `pack:`
  resolves to the candidate whose SHA-256 is the recorded one (`Lock.Identity.toPathHashed`),
  and a package copy matching neither fails naming both (`Lock.packMismatches`).
- **S79:** with no SDK, an apphost for an explicit `RuntimeIdentifier` was written by Xake's
  fallback without the SDK's HostModel: no version resource, no icon (119 296 against 194 048
  bytes); the build only warned and the lock had no prerequisite. Owner's decision: an entry with an apphost gets the
  `dotnet-sdk <v>` prerequisite, derived like the compiler's (`Lock.recordedSdkVersions`).

What the consumer had to declare: the three properties above (the two package references and
`RoslynCompilerType`, plus `NetCoreTargetingPackRoot`); an `Import` of the root props in the 23
projects that did not import it; `PackageVersion` items in the one repository on central package
management; `NoWarn` NU1605 where a transitive dependency brought a newer toolset. `deps.json`
lists the two packages despite `PrivateAssets=all`. The msbuild output on the packages differs
from the earlier `packs/` and SDK-compiler build only through the compiler build the pdb
records; IL and metadata are equal.

## The problem

A recorded lock names the SDK's compiler (`sdk:Roslyn/bincore/csc.dll`, `sdk/<v>/FSharp/fsc.dll`)
and the SDK's analyzers (`sdk:Sdks/Microsoft.NET.Sdk/analyzers/*NetAnalyzers.dll`) by the SDK
version, and always carries the prerequisite `dotnet-sdk <NETCoreSdkVersion>` of the SDK that
recorded it ([lock.md, Prerequisites](../../hermetic/lock.md#prerequisites)). With a
`global.json` that rolls forward, the import takes whatever patch is installed, so one
developer's SDK update re-records every lock (seen 2026-10-21: a package-manager upgrade moved
8.0.425 to 8.0.426 and rewrote the SDK pin in 180 locks of a consumer with unchanged sources),
and from then on every developer, CI and build agent has to carry that exact SDK. A pinned
`global.json` (`rollForward: disable`) only moves the same update to a commit: still one SDK for
everybody.

What ties a consumer's locks to the SDK today (one configuration, one brand, 182 locks):

| in the lock | entries | comes from |
|---|---|---|
| `sdk:Roslyn/bincore/csc.dll` | 180 | the SDK's compiler, by SDK version, unhashed |
| `sdk:Sdks/Microsoft.NET.Sdk/analyzers/{Microsoft.CodeAnalysis.NetAnalyzers,Microsoft.CodeAnalysis.CSharp.NetAnalyzers}.dll` | 360 | the SDK's analyzers |
| `pack:microsoft.netcore.app.ref/8.0.x/...`, `pack:microsoft.windowsdesktop.app.ref/...` | 11 662 | targeting packs from `$(DotnetRoot)/packs/`, by hash |
| `pack:microsoft.netcore.app.host.<rid>/...` | 1 | the apphost pack |
| `Prerequisites: dotnet-sdk <v>` | every lock | the rule for recorded entries |

A composed `csc { toolset "<v>" }` (or `CSC_TOOLSET`) already names nothing under `sdk/` and has
no prerequisite: the compiler is `pkg:microsoft.net.compilers.toolset/<v>/...` with its hash,
fetched by the lock's restore. The agenda is to get a *recorded* (imported) lock to the same
state.

## Steps

Xake side (`src/hermetic`, one PR each or one together):

1. **The prerequisite follows the content, not the kind of entry.** A recorded entry gets
   `dotnet-sdk <v>` only when it names a path under `sdk/<v>/` (compiler, analyzers, any other
   file), as a composed entry does today. `Import.record` / `Lock.rehash`: derive it from the
   tokenized paths. Test: an imported project on the toolset compiler and package analyzers
   records no prerequisite and replays with the SDK missing from the machine (point the probe at
   an empty `sdk/` folder).
2. **`pack:` files fetched as packages when `packs/` lacks them.** The identities match
   nuget.org package ids and versions (`Microsoft.NETCore.App.Ref`,
   `Microsoft.WindowsDesktop.App.Ref`, `Microsoft.NETCore.App.Host.<rid>`,
   `Microsoft.AspNetCore.App.Ref`): the lock's restore (`Restore.ensure` of the
   `restoreRequest`) treats a `pack:` entry whose file is not under `$(DotnetRoot)/packs/` as a
   package to fetch into the package folder, and the replay accepts the file from there by the
   same hash. Test: a lock naming a ref-pack file builds with an empty `packs/` and the package
   folder holding `microsoft.netcore.app.ref/<v>/`.
3. **The import on a package compiler and package analyzers, verified byte-identical.** No
   code expected: `parseImport` already reads the compiler msbuild wired up (the toolset
   package's `bincore/csc.dll` when `<RoslynCompilerType>Toolset</RoslynCompilerType>` is set),
   and a `PackageReference` to `Microsoft.CodeAnalysis.NetAnalyzers` makes the SDK skip its
   bundled copy. Add the fixture (a net472 + netstandard2.0 library with both references) and
   assert: no `sdk:` identity in the lock, no prerequisite, output byte-equal to msbuild's.
4. **The fsc side**: `Fsc` has no toolset package; the F# compiler comes from the SDK only. Out of
   scope for the consumer (C#), noted as the remaining `dotnet-sdk` case; a lock mixing C# and
   F# keeps the prerequisite for the F# entries.

Consumer side (the script or the repository's `Directory.Build.props`, not Xake):

5. `<PackageReference Include="Microsoft.Net.Compilers.Toolset" Version="<v>" PrivateAssets="all" />`
   plus `<RoslynCompilerType>Toolset</RoslynCompilerType>`, and
   `<PackageReference Include="Microsoft.CodeAnalysis.NetAnalyzers" Version="<v>" PrivateAssets="all" />`
   (the versions the SDK bundles today, so the output does not change: take the exact versions
   from the current locks' `Compiler.Version` and the analyzers' assembly versions). Re-import,
   compare the output with msbuild's `bin/` byte for byte, and with the previous locks: only the
   compiler/analyzer identities and the prerequisite may differ.

What remains on a build agent after 1–5: a .NET SDK of *any* version for `dotnet fsi` (the
host of the script; a pinned version is not needed), the package feeds, and for WPF projects the
SDK's markup compiler (msbuild) as today. A compiled runner in place of `dotnet fsi` would drop
the SDK entirely; separate topic.

## Not chosen

- A lower bound on the SDK (`dotnet-sdk >= 8.0.400`): the compiler and analyzers differ between
  patches (Roslyn 4.11 vs 4.12, NetAnalyzers rule sets), so a lower bound breaks the
  byte-identical guarantee; and the consumer's experience says such bounds do not hold across
  teams anyway.
- Pinning `global.json` exactly: moves the drift to a commit, every machine still follows.
