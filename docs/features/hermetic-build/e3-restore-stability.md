# E3: `packages.lock.json` stability and floating versions

Scope: `ar-net-core-dataengine` (DE) and `ar-net-core-page` (Page). Real checkouts under
`~/Projects-work/ar/*`; restored `origin/develop` copies at `tmp/de` and `tmp/ar/*`.

## Verdict

**Restore is reproducible from the repo alone, for the packages currently in play** -- neither
repo has a lock file, but neither has any floating version pattern either: every
`PackageVersion`/`PackageReference` is an exact pin. Two independent `origin/develop` restores
(same commit) produced byte-identical `sha512` for every shared library. The real exposure
isn't "two resolves diverge today" but "nothing stops that tomorrow": no lock to freeze the
graph, and a few inputs are pinned by the *installed SDK*, not the repo.

## 1. RestorePackagesWithLockFile / RestoreLockedMode

Empty for both repos:
`grep -rn "RestorePackagesWithLockFile" --include='*.props' --include='*.csproj' --include='*.targets' .`,
same for `RestoreLockedMode`, and `find . -name packages.lock.json` -- no matches anywhere.
Neither repo generates or commits a lock file; CI has no `--locked-mode` gate. This is the gap
the Xake lock backstops.

## 2. Central Package Management / floating versions

- DE: `Directory.Packages.props:3: <ManagePackageVersionsCentrally>true</...>`. All 7
  `PackageVersion` entries are exact pins, e.g.
  `Directory.Packages.props:6: <PackageVersion Include="SauceControl.InheritDoc" Version="2.0.1" .../>`,
  `:8: <PackageVersion Include="Moq" Version="4.14.3" />`. No `*`, no open ranges, no
  `VersionOverride` anywhere.
- Page: **no** `Directory.Packages.props` -- CPM not used at all. Every `PackageReference`
  has an inline exact version, e.g.
  `tests/Drawing.Tests/Drawing.Tests.csproj:22: <PackageReference Include="Moq" Version="4.16.1" />`.
  Cross-repo consumer refs use MSBuild properties (`Directory.Build.props:24:
  <DataEngineVersion>5.5.1-alpha-1959</DataEngineVersion>`, consumed at
  `src/Rendering/Rendering.csproj:31`) but each resolves to one exact value at restore time.
- No `Version="...*"` / `[x,)` / `(,x]` range found anywhere in either repo. The only regex
  hits are the custom `GetSemanticVersionRange` task (`Directory.Build.targets:15-40`, both
  repos) -- it converts DE/Page's own fixed version into a semver *range written into the
  published nuspec* the repo produces (affects downstream consumers' floating, not this
  repo's own restore).

## 3. Feeds

Identical shape in both `NuGet.config`s: `:4 <clear/>`, `:5` nuget.org, `:6` `ar-core-dev`
(Azure DevOps feed). `packageSourceMapping` is present in both:
- DE maps `ar-core-dev` to `Babel.*`, `semverchecker*` only; `*` -> nuget.org
  (`NuGet.config:14-23`).
- Page maps `ar-core-dev` to `Babel.*`, `semverchecker*`, `Mescius.ActiveReports.Core.*`,
  `GCCN.ActiveReports.Core.*`, `GrapeCity.DataVisualization.*`; `*` -> nuget.org (`:14-28`).

Substitution risk is already closed by mapping -- one of the standard mitigations, already in
place. Credentials are an env placeholder (`%AA_AR_TOKEN%`), not a literal.

## 4. Implicit / SDK-provided versions

`global.json`: DE pins `8.0.100`, Page `8.0.0`, both `rollForward: latestFeature`. Both repos
target `netstandard2.0`/`net472` somewhere, triggering SDK-implicit packages. Checked against
installed SDK 8.0.425:
- `NETStandard.Library` -- hardcoded in
  `Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.DefaultItems.targets:55` as `2.0.3`.
  Matches both restores.
- `Microsoft.NETFramework.ReferenceAssemblies` -- hardcoded in
  `Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.props:123` as `1.0.3`. Matches both
  restores (net472-targeting projects only).

Both are `IsImplicitlyDefined="true"` with no explicit override in either repo. The version is
fixed *per installed SDK build*, not per repo: two machines both satisfying
`8.0.100`/`rollForward: latestFeature` but on different feature bands (8.0.1xx vs 8.0.4xx) can
restore different values here with zero repo change. This is a real, un-lockable-by-repo-content
drift vector.

## 5. tmp/de vs tmp/ar diff (DataEngine, same commit)

Diffed `libraries` (keys + `sha512`) of `src/DataEngine/obj/project.assets.json`: tmp/de
restored `.NETStandard,Version=v2.0` only (6 libraries); tmp/ar restored that plus
`.NETFramework,Version=v4.7.2` (8 libraries -- 2 extra are the ReferenceAssemblies pair, only
present because net472 was in scope). **For all 6 shared libraries, `sha512` is byte-identical
-- zero diffs.** The extra entries are restore-scope, not version drift.

Aside: real checkout (`43b952c`) pins `SauceControl.InheritDoc` at `2.0.1`, both
`origin/develop` restores resolve `2.0.2` -- confirmed as the checkout trailing `develop` by a
version bump (both restored copies' own props also say `2.0.2`), not nondeterminism.

## 6. Transitive pinning

`CentralPackageTransitivePinningEnabled` -- unset in DE (its only CPM repo). Low-impact today
(shallow, fully-pinned graph) but if a direct package's own nuspec range re-resolves against
the feed, the transitive pick can move with zero DE commit. Page has no CPM, so the switch
isn't even available -- its entire transitive graph (e.g. `System.Text.Json/8.0.5`,
`System.Memory/4.6.0`, `Microsoft.Bcl.AsyncInterfaces/8.0.0` under `Rdl`) floats purely on
NuGet's resolver with no central pin as backstop.

## Ranked drift vectors (can change with zero commit)

1. **SDK feature-band drift** (most likely): implicit `NETStandard.Library`/
   `ReferenceAssemblies` tied to installed SDK build; `global.json` only floors + rolls forward.
2. **Unpinned transitive graph in Page** (no CPM): any transitive can move if NuGet's resolver
   picks a different minimum-satisfying version as feed metadata changes over time.
3. **DE's transitive pinning switch off** -- same mechanism, smaller blast radius today.
4. **No lock file / no locked-mode CI gate** in either repo -- nothing stops a floating range
   being introduced tomorrow and passing CI silently.

Feed substitution and floating direct versions -- the usual CPM/mapping targets -- are **not**
present today in either repo.

## Recommendations

- Add `RestorePackagesWithLockFile=true` + commit `packages.lock.json` in both; add
  `RestoreLockedMode=true` on CI so an unreviewed graph change fails the build.
- Turn on `CentralPackageTransitivePinningEnabled=true` in DE now; adopt CPM in Page so a pin
  surface exists there too.
- Pin `global.json` to an exact SDK version if bit-for-bit reproducibility of the SDK-implicit
  packages matters for the hermetic-build story.
- `packageSourceMapping` + `<clear/>` already correct in both -- keep in sync as new id
  prefixes appear.
- What the Xake lock adds on top: `References` hashes the *actual restored package files*
  per-file, not just resolved version + nupkg hash. That surfaces drift NuGet's own lock is
  silent on -- e.g. SDK-implicit package versions moving with the SDK -- as a visible file-hash
  diff the next time DE/Page builds through it.
