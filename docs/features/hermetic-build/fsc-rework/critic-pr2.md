# Critic: PR 2 `pr/resolved-inputs` (Import.record, recorded locks, format 2), 2026-10-16

Scope: `git diff b2a0d1e..pr/resolved-inputs -- src docs`. That range includes the night base
74bf4d9 (merged as fc95e69), which earlier critics already reviewed. The review therefore
concentrates on the PR's own 7 commits, `git diff fc95e69..HEAD`: 19 files, +1958/-168. Of
those, `Import.fs` is +958, `Lock.fs` +263, `RecordTests.fs` +294 and the docs +375.

Judged against:

- the author's report `pr2-resolved-inputs.md`;
- `interview.md` (pr/ar-full), the two 2026-10-15 sections (binding);
- `night-changes-triage.md` Part 5 (pr/apphost-resx);
- `verify-dataengine.md` §6 (main checkout), on the restore and `TargetFramework`.

This review is read-only. The only file written is this one. AR was not run; the AR lock files
the author left in `~/.claude/jobs/7147ff60/tmp/pr2/ar/` were **read** for the size figures.

## Test run

`dotnet fsi build.fsx -- -- build test` (macOS, SDKs 8.0.425 and 10.0.401, global.json
`8.0.0 latestMajor`, so 10.0.401) is green, in 2 min 09 s. Counts from
`dotnet test --no-build -c Release`:

- core (`src/tests`): **341 passed, 1 skipped** (342)
- hermetic (`src/hermetic.tests`): **234 passed** (234)

Both match the report.

---

## 1. Faithfulness to the principle

**The lock side is faithful.**

- `entryOf` (`Import.fs:913-1085`) takes msbuild's compiler command line, `ReferencePath`,
  `Analyzer`, `Compile` and `EmbeddedResource` as given.
- It changes only two things: it drops `/out:`, `/refout:`, `/sourcelink:` and `/embed`, and it
  moves the files under the intermediate folder.
- Defines, switches, `/nowarn`, `/langversion`, `/target` and the analyzer configs are the
  recorded switches. Decision 4a holds.
- Resource names come from `ManifestResourceName`, `LogicalName` and `WithCulture` (decision
  4b, record branch `Import.fs:368-381`).
- The night's emulation functions are **absent, not merely unused.** None of
  `resolveConflicts`, `frameworkConflicts`, `supersede`, `windowsDesktop*`,
  `designTimeFacades`, `netstandardShims`, `restoreGraphOf`, `analyzerItems`,
  `assemblyInfoOf`, `codeFragment*`, `globalUsingsText*`, `defineConstantsOf`,
  `defaultLangVersion`, `firstClassName`, `isCulture`, `packageSources` or `msbuildNewLine`
  occurs in `src/hermetic` or `src/dotnet`. They were never merged.
- The remaining hits for `dependsOn` are the base's `Package.DependsOn` field. The remaining
  hits for `sdkNoWarn`, `manifestName`, `attributeArgs` and `frameworkAssemblies` are the base's
  composed route.

**Is the composed route untouched?** The PR's range does not touch `src/dotnet` (`Csc.fs` and
`CompilerRunner.fs` change only in the night base). In `src/hermetic` the PR changes the
composed path in three places, all ports of night "group 1":

- `Described.nearestFramework`/`platformOf` (`Described.fs:39-62, 229-251`). These are now used
  **only** by the composed route (`Locks.fs:184, 328, 334`, `Described.fs:509`). The record path
  takes msbuild's `NearestTargetFramework`. Triage row 16 keeps them ("A, remains"). They extend
  Xake's own resolution (NuGet's FrameworkReducer for `-windows`) on a route that decision 5
  restricts to simple cases and ASP.NET Core. That is a nit, but say it in the tracker.
- `byFramework.<tfm>.resources` in the composed plan (`Described.fs:802-806`).
- `expandCsc` (`Described.fs:932-944`) now keeps generated sources after the masks. For a
  composed compilation, whose generated sources are all in front, the result is identical.

**What is still derived on the import path:**

- **D1 (nit, design note).** `Recorded.Masks` (`Import.fs:991-1011`, used at build by
  `Locks.fs:451-459`) makes Xake decide what to compile when a file is added: it evaluates
  msbuild's default globs with Xake's fileset. This derivation is deliberate and documented,
  and the import checks it once (masks = msbuild's list). After that it can drift. A new `.cs`
  file in a folder that msbuild excludes under a condition is compiled by Xake and not by
  msbuild. That is acceptable as a convenience, but the lock.md principle paragraph
  ("Xake does not derive ... the compile list") should name the exception.
- **D2 (should-fix, low).** The description's evaluation-only branch still uses the culture
  table of the importing machine (`CultureInfo.GetCultures`, `Import.fs:360-394`) and the
  `DependentUpon` heuristic. That branch runs for `Import.project`/`descriptions`, not
  `record`. Decision 4b ("свой C#-парсер и ICU-детекция уходят") is unconditional; the report
  says "no culture table" only for `record`. Either scope 4b explicitly to `record` in
  interview.md, or drop the ICU path (an evaluation-only import then reports a culture-looking
  suffix as "unknown, set `name`").
- `describe` still projects the evaluation into `defines`, `noWarn`, `assemblyAttributes`
  (`attributeArgs`, an emulation of `WriteCodeFragment`) and so on. That is the description
  contract, and the recorded lock does not read it. This is acceptable under decision 5.3, but
  `assemblyAttributes` in a description that sits beside a recorded lock is now unused data
  that can contradict the lock's `AssemblyInfo.cs`. See 4.

---

## 2. Design-time build mechanics (`Import.record`, `Import.fs:1129-1277`)

### B1 (blocker): the restore solution hard-codes `Release|Any CPU`

`solutionText` (`Import.fs:785-814`) writes only `Release|Any CPU`. The restore then runs with
`-p:Configuration=<primary>` and the script's properties (`Import.fs:1154`).

Verified by hand on a one-project solution generated the same way:

- `-p:Configuration=Debug` fails with `MSB4126: The specified solution configuration
  "Debug|Any CPU" is invalid`.
- `-p:Platform=x64` fails with the same error.

So `RecordOptions.Configurations = ["Debug"]` (or `Debug` first), or any `Platform` in
`Properties`, makes the import fail at step 1. AR uses `Release` and no `Platform`, so neither
the AR check nor the e2e test sees it.

Fix: write one `SolutionConfigurationPlatforms` line per configuration (and the platform from
`Properties`, default `Any CPU`), with matching `ActiveCfg`/`Build.0` lines. Or drop the
solution and restore a traversal project with `-t:Restore` (NuGet supports a traversal through
`_GenerateRestoreGraph`), which avoids the solution-configuration machinery entirely. Add a test
with `Configurations = ["Debug"; "Release"]`.

### S1 (should-fix): restore under the primary configuration only

Related: the restore uses only the primary configuration's properties. A `PackageReference`
conditioned on `Configuration` leaves the second configuration's design-time builds with an
assets file that lacks its packages (NETSDK1005 at best, a silently different `ReferencePath`
at worst).

Either restore once per configuration whose evaluation differs, or document "a package set may
not depend on `Configuration`".

### Determinism: good

- Each (project, TFM) dump goes to its own file and is keyed by
  `(MSBuildProjectFullPath, TargetFramework)` (`Import.fs:1205-1213`). `-m` cannot reorder
  entries.
- Item order inside a dump is msbuild's order inside one project instance.
- Entries follow the description's framework order.
- `-nodeReuse:false` keeps no node holding the deleted targets file.
- The format `P\tname\tvalue` / `I\t...` breaks on a metadata value containing a newline (nit:
  practically never).

### Global properties in one process: correct for what is asked

- `TargetFramework` reaches the design-time builds only through `AdditionalProperties` of the
  traversal items, never as a command-line global.
- The restore is a separate run without `TargetFramework` and the traversal has no `-restore`.
  So the failure in verify-dataengine.md §6 (a global `TargetFramework` restoring one target and
  rewriting the referenced projects' assets files) cannot recur.
- `ProvideCommandLineArgs`, `SkipCompilerExecution`, `NonExistentFile` and `NuGetAudit` do leak
  into the referenced projects' `GetTargetPath` instances. That is harmless with
  `BuildProjectReferences=false`.
- A single-TFM referenced project is evaluated twice (the traversal instance carries
  `TargetFramework=X`, the reference instance does not). This costs time, not correctness.

### S2 (should-fix): `CustomAfterMicrosoftCommonTargets` set inside the repository is lost

`Import.fs:1173-1183` chains the value only when the script passes it in `Properties`. A
`Directory.Build.props` that sets the property itself is overridden by the global
`-p:CustomAfterMicrosoftCommonTargets=<xake>`, and its targets do not run during the recording
design-time build. They do run in `dotnet build`, so the recorded compilation differs and
nothing says so.

Fix: read the evaluated value (add `CustomAfterMicrosoftCommonTargets` to `properties`, take
it from `ev.Outer`) and chain it. Per project, the value may differ: pass it through
`AdditionalProperties` as `XakeChainedAfterCommonTargets`.

### Targets and writes into the project's `obj/` and `bin/`

The targets are `BuildOnlySettings;PrepareForBuild;ResolveReferences;PrepareResources;ResolveKeySource;Compile`.
Of these:

- `PrepareForBuild` creates `bin/<cfg>/<tfm>/` and `obj/<cfg>/<tfm>/`.
- `PrepareResources` runs `GenerateResource`, which writes `.resources` and
  `.GenerateResource.cache`, and `CompileLicxFiles`.
- `Compile` writes `AssemblyInfo.cs`, `AssemblyInfoInputs.cache`, `GlobalUsings.g.cs`,
  `<TFM>.AssemblyAttributes.cs`, `GeneratedMSBuildEditorConfig.editorconfig`,
  `CoreCompileInputs.cache` and `sourcelink.json`.
- The restore writes `project.assets.json` and `nuget.g.*`, which the import needs anyway.

Consequences:

- **Clean clone:** this is fine and tested. The e2e fixture starts with no `obj/`, and the
  referenced projects' outputs need not exist (`BuildProjectReferences=false`, reference paths
  mapped by `projectsOf`).
- **`HERMETIC=on` build:** no effect. The build reads only the lock, the sources and the
  materialised identities. The generated texts go to the root's `obj/xake/...`, and the masks
  exclude `<project>/obj/**`.
- **S3 (should-fix): concurrent imports race on `obj/<cfg>/<tfm>/`.** Two `Import.record` calls
  over the same projects with different `Properties` (the AR brands mescius/gccn) both write
  `obj/Release/<tfm>/<name>.AssemblyInfo.cs`. Each reads it back afterwards (`entryOf`,
  `generated`). Run in parallel, one brand can record the other's AssemblyInfo text, and nothing
  detects it. The same applies to an IDE design-time build running at the same time.
  Fix: serialize imports per project root (a named mutex or lock file under `obj/xake/import/`),
  or check `AssemblyInfo` against the dump's `Version`/`AssemblyName`. Document it in lock.md
  either way.
- A subsequent incremental `dotnet build` of the other brand recompiles, because the
  AssemblyInfo changed. This is the same as switching brands in Visual Studio, so worth a line
  in the docs only.

### Failure modes

- Restore failure, or a design-time build failure: non-zero exit, import fails. Good.
- An assets file without a TFM target: NETSDK1005, msbuild fails, import fails. Good.
- A dump file missing: explicit message (`Import.fs:1212`). Good.
- **S4 (should-fix): a `ProjectReference` to a project outside `projects`.** The lock is written
  with `project:<name>`, and no lock for `<name>` exists. The build then fails later in the
  sibling's output rule with a "no lock" message that does not point back to the import.
  `record` knows every recorded project. Fail the import, naming the referrer and the missing
  project, and suggest adding it to `projects`.
- **Nit:** `projectsOf` `List.distinctBy (fun (p, _) -> p.Name)` (`Import.fs:880`). Two
  references whose project files share a name (`src/A/Core.csproj`, `test/B/Core.csproj`)
  collapse into one. The second's `ReferencePath` is then not mapped, and `Csc.sha256` is called
  on an output that may not exist (an exception, not a message). Fail with a message, or key by
  full path.
- **Nit:** msbuild reads every environment variable as a property (`CI`, `GITHUB_ACTIONS`,
  `TF_BUILD` and `ContinuousIntegrationBuild` switches in repo props). An import on CI and one
  on a laptop may record different locks. Either document this or clear the well-known CI
  variables for the msbuild runs.

---

## 3. Portable identities (`Lock.Identity`, `Lock.fs:448-562`)

**Covered and correct:**

- `pkg:` under `$(NuGetPackageRoot)`, regardless of `ref/` vs `lib/` vs `build/` (the .NET
  Framework reference assemblies and `Facades/` are `pkg:`, confirmed in the e2e lock).
- `runtimes/<rid>/` assets: still `pkg:`, but see the hostStrings note for PR 3.
- `pack:` for `*.App.Ref` in `$(DotnetRoot)/packs` or in the package folder, with one identity
  for both (tested). `Microsoft.AspNetCore.App.Ref` and `Microsoft.WindowsDesktop.App.Ref` are
  `.app.ref`.
- `sdk:` only for the entry's own SDK version.
- `project:` mapped from both `ReferencePath` and `ReferenceAssembly`.
- A `HintPath` under a root becomes a tokenized root path (tested).
- Anything else is refused, with a message naming the file, the SDK and "declare its folder as
  a root".

**Edge cases:**

- **S5 (should-fix): switch values are not identity-checked.** `Identity.ofPath` runs on
  references, analyzers and the compiler only. A path in `Options` (`/analyzerconfig:`,
  `/keyfile:`, `/win32icon:`, `/win32manifest:`, `/additionalfile:`, `/ruleset:`, `/resource:`)
  is written through `Roots.tokenizeAll`. One outside every root is written absolute. Only
  `hostStrings` (roots, home, temp, a RID) can catch it.
  - A `.editorconfig` above the repository (`/Users/x/projects/.editorconfig`) is caught,
    because it is under home.
  - `/opt/shared/.editorconfig`, `/Volumes/keys/ar.snk`, or a Windows `C:/build/...` outside
    the user profile go into the lock silently. That breaks the "no absolute path" promise of
    lock.md §"What is checked".
  - Fix: run `Identity.ofPath` (or at least "is tokenized") over every path-valued switch
    (`CscArgs.inputs` already knows which ones), with the same failure message.
  - Related: SDK files in `Options` are written as `$(DotnetRoot)/sdk/8.0.425/...globalconfig`,
    not `sdk:...`. That is portable under the pin, but it gives two spellings for one kind of
    file. The schema should either say so or use `sdk:` in switches as well.
- **Nit:** `NETStandard.Library.Ref` (netstandard2.1) gets `pack:` from `packs/` but `pkg:` when
  downloaded into the package folder (`isPack` checks only `.app.ref`, `Lock.fs:505`). Two
  machines can then record different locks. The same holds for any non-`.App.Ref` pack. Match on
  "is a folder of `$(DotnetRoot)/packs` on some machine", i.e. `*.Ref` packs, or a list.
- **Windows, unverified:** a .NET Framework project on a machine with the targeting pack
  installed may resolve from `C:/Program Files (x86)/Reference Assemblies/...` (if
  `Microsoft.NETFramework.ReferenceAssemblies` is not used). The import then fails at
  `identityOf`. The message is right, but "declare a root" is the wrong advice there. The e2e
  assertion `pkg:microsoft.netframework.referenceassemblies.net472/` would fail as well.
  CI is Ubuntu only, so check once on Windows before the "hermetic after PR 2" release.
- `Private=false` and `CopyLocal` do not affect `ReferencePath`, so there is nothing to do here.
  Copy-local is PR 3.
- Satellite/culture outputs are dropped with a `Dropped` line. That is right for now (PR 3/4).
- `FrameworkReference` to `Microsoft.AspNetCore.App` is `pack:microsoft.aspnetcore.app.ref/...`.
  This works by construction but is untested. Decision 5 sends ASP.NET Core to the composed
  route anyway.
- **Nit:** `Lock.Identity.toPath` calls `Directory.GetDirectories packs` once per `pack:`
  reference at every parse (about 204 per net8.0-windows entry; each lock is parsed by
  `needAll`, `compileAs` and the `.xml` rule). Cache it per parse.

---

## 4. Lock format 2

**Size.** Read from the AR locks of the author's run:

- `Design.Win/Release.json` is **5,887 lines**, about 2,900 per cfg×TFM.
- `net472` has 255 options, 2,179 sources, 66 references (42 `pkg:`, 24 `project:`), 3
  generated, 137 resx pairs and 45 masks.
- `net8.0-windows` has 241 references (204 `pack:`).
- All 85 locks together are 80,544 lines.

**S6 (should-fix): the source list is duplicated.** The driver of the size is
`Compilation.Sources`. It holds msbuild's full file list, per TFM, **and** the masks that
produce the same list (`Recorded.Masks`). Every added or removed `.cs` file makes the lock stale
in 2×N lines at the next import, even though the build no longer needs a re-import for it.

Options:

1. Keep only the order-relevant part: masks plus generated sources, with the order
   msbuild-sorted, which is deterministic (msbuild's glob order is ordinal per directory).
2. Keep `Sources` only when the masks do not match.

Owner's call. The metadata-identity argument for msbuild's order is real, but costs lock churn.

**The `Recorded` section against the rest:**

- The same fact sits in three places: `Evaluation.ProjectRefs` (names), `References`
  (`project:lib`) and `Recorded.Projects` (name, cfg, TFM, path). They cannot contradict each
  other as written, because one writer produces all three, but a reader must look at three
  places.
- `Recorded.Projects[].Configuration` always equals the document's `Configuration`.
- `Recorded.Projects[].Path` freezes the script's `OutOf` layout at import time. A script that
  changes its layout needs a re-import. lock.md should say so.
- **`Recorded.Masks` against the description's `sources`:** a hand edit of the description is
  ignored by the build (the lock's masks win), and nothing tells the user. Say so in
  description.md ("for a recorded project, the masks come from the lock; edit the project, then
  re-import").

**Nit: the format-1 refusal message is inaccurate.** `Lock.fs:822` says "records an imported
project by machine paths". Format 1 wrote tokenized paths. The real difference is that the
resolution was Xake's, not msbuild's.

**S7 (should-fix): the `Project.import` gap.** `Project.import` still writes imported entries
with tokenized paths. They are now labelled `"Format": 2` without `Recorded` and are accepted,
which is exactly the content format 1 is refused for. Either:

- refuse an entry with `Evaluation.Project <> ""` and no `Recorded` too ("written by
  `Project.import`; use `Import.record`"), or
- retire `Project.import` in this PR's release.

As it stands, the guard catches old locks and lets new locks of the same kind through.

**Hashing:**

- Every `pkg:` and `pack:` file is hashed, and so are the `Imports`.
- `sdk:` files and project outputs are unhashed, covered by the prerequisite and the build.
- Materialised `pack:` files from `$(DotnetRoot)/packs` are accepted under `HERMETIC=on` by
  their hash (`hashedPack`, `Lock.fs:1093-1098`).
- Generated texts are in the lock itself.
- `Lock.fs:1106` `| _ -> recorded` puts every role under `sdk/<v>/` under the prerequisite,
  references included. That is consistent with decision 3.

**S8 (should-fix): no staleness check at build.** The `Imports` hashes are recorded but never
compared at build. `Locks.compileAs` does not read them, and `Import.record` records no input.
After a `PackageReference` is added to a csproj, Xake builds the stale lock silently, while
`dotnet build` builds something else.

The composed route had `update-locks`. Here "the import is that step", so nothing reminds the
user to run it. Compare the `Imports` hashes in `compileAs`:

- a warning locally;
- a failure under `CI` (the interview's "fail on mismatch on CI");
- a message naming the changed file and saying "run the import".

The file reads are cheap and already done for `needFiles`.

**SDK pin (decision 1):**

- `Prerequisites` always holds `dotnet-sdk <NETCoreSdkVersion>`.
- `Evaluation.SdkPin` records global.json's own pin. AR has `8.0.100 rollForward:latestFeature`
  with prerequisite `8.0.425`; the e2e test has `8.0.0 latestMajor` with `10.0.401`.

So the lock is **stricter than global.json**. A teammate or CI agent with 8.0.426, allowed by
`latestFeature`, gets "the lock needs the .NET SDK 8.0.425 ... install it". If they re-import
instead, all 85 locks churn: `Sdk`, `Prerequisites`, the compiler `Version`, and every
`/analyzerconfig:$(DotnetRoot)/sdk/<v>/...` switch.

Decision 1 accepts "re-import on an SDK change", so this is not a defect. But:

- **S9 (should-fix):** warn at import when global.json does not pin exactly, with the
  suggestion to pin `"version": "<v>", "rollForward": "disable"`. `sdkUnpinnedWarning` already
  exists, but `entryOf` never emits it.
- **Nit:** with no global.json, `Pin` is `""`, and the message reads "(prerequisite from )".

**`$(SourceRevisionId)` and LF:**

- `lf` is applied to the texts generated under the intermediate folder, to package sources
  (`Import.fs:968-972`), and to the existing lock when comparing (`Import.fs:1261`).
- It does **not** touch resx (a repository source compiled by Xake) or any `.g.cs` from source
  generators (in memory, not on disk).
- It would touch WPF's `*.g.cs` (on disk under `obj/`), which are parked.
- The sha is replaced in every text (`Lock.mapText`, `Import.fs:1073`). It is resolved at build
  from the project's own repository, so a lock imported in a git checkout cannot be built from a
  source tarball. That is acceptable and documented in `compileEntry`, but say it in lock.md.

**S10 (should-fix, latent silent corruption): unknown binaries are read as text.**
`entryOf` classifies scratch inputs by a binary **deny-list** (`.resources .res .dll .exe .pdb
.ico .snk`, `Import.fs:965`). Every other existing file under the intermediate folder is read
with `File.ReadAllText` and recorded as text.

- WinForms `licenses.licx` produces `obj/.../X.exe.licenses`, a binary that `CompileLicxFiles`
  writes during `PrepareResources` and passes as `/resource:`. It would be recorded as text
  (corrupted) and materialised corrupted, which surfaces as a runtime license failure.
- A WPF `X.g.resources` is in the deny-list. It is moved but never materialised (it has no resx
  pair), so csc fails later with "resource not found", far from the cause.
- AR has neither today (checked: no `.licenses` and no non-resx `/resource:` under
  `obj/xake`).

Fix:

- Use an **allow-list** for recorded text (`.cs .fs .vb .editorconfig .globalconfig .json
  .txt`).
- Fail the import on any other scratch input that is not a resx `OutputResource`: "msbuild
  generated `<file>`, which the lock cannot reproduce". Do the same for a `.resources` under
  scratch that is not a resx pair.

**The dropped `/sourcelink:` and `/embed`:**

- They are dropped in `parked` (`Import.fs:929-931`) and in `isOutput`, which is consistent and
  matches decision 2.
- On SDK 8 in a git checkout, SourceLink is on by default, so every recorded lock differs from
  msbuild's compilation by these two switches. The PDB loses its source link, and that is the
  intended park.
- Without `/pathmap` (msbuild adds none unless `ContinuousIntegrationBuild`), the outputs embed
  each machine's absolute source paths (PDB documents, `CallerFilePath`). The lock is portable,
  but **the outputs are not reproducible across machines**. That matters for the SBOM and
  reproducibility positioning. Track it for `pr/sourcelink` (the two come together:
  `DeterministicSourcePaths` needs `SourceRoot`).

---

## 5. Materialisation at build

- **`pkg:`** resolves to `$(NuGetPackageRoot)/<id>/<v>/<path>`. Missing files are fetched by
  `Restore.ensure` from the path segments. Hashes are checked by the runner.
- **`pack:`** fallback: `toPath` gives the package-folder path when the pack version is not
  installed. `Restore.ensure` fetches the `.app.ref` package like any other.

  **Is the fallback hermetic?** Yes at file level. Every referenced pack file carries its
  SHA-256 and the runner checks it. The `.nupkg` is not hashed (the pack is not in `Packages`,
  so it has no `Sha512`), so the SBOM lists no component for a downloaded pack. Add the pack as
  a `Packages` row (id, version, the nupkg's sha512 from the `.nupkg.metadata`) or record it in
  the SBOM by identity.

  The 39 s for 199 packages is rounds of `dotnet restore` (`Restore.rounds`). That is
  acceptable for a cold machine.
- **`project:`** resolves to `Recorded.Projects[].Path`. The path is in the entry itself, so the
  build does **not** read sibling locks to resolve it. The sibling's lock is read only by the
  sibling's own output rule, which `compileAs` triggers through `needFiles` on the unhashed
  reference.
  - Cycles are impossible from msbuild.
  - A missing sibling lock fails in the sibling's rule (see S4 for catching it at import).
  - A composed project referencing a recorded one uses `e.Name + ".dll"` (`Locks.fs:190`), not
    `outputName`. That is wrong for a recorded .NET Framework exe. Nit: use `outputName e`.
- **Clean build 34 s against 30.4 s (night):** not measured here (AR not run). The candidates,
  in likely order:
  1. msbuild's full analyzer set and `/analyzerconfig` globalconfigs now run in every compile
     (12 analyzers on `net8.0-windows`, the SDK's NetAnalyzers and code-style analyzers,
     generators in the ref packs). If the night's composed set was smaller, this alone explains
     seconds.
  2. `Described.expand r.Masks` per entry (Design.Win: a 2,179-file glob, twice).
  3. Each lock parsed about 3 times, with a `packs` directory scan per `pack:` reference (above).

  Suggest one `--profile`/`FileLogLevel=Loud` run with `CSC_SERVER=off` comparing the per-task
  times of the two routes before PR 3 adds more per-compile work.

---

## 6. Tests

`RecordTests.fs` against the report:

| report claims | covered? |
|---|---|
| identities `pkg:`/`pack:` (both places)/`sdk:`/`project:`/roots/refusals | yes, pure |
| pack fallback materialisation | yes (`/r/dotnet/packs` does not exist) |
| format-2 round trip, text equal after read+write | yes |
| non-portable file refused | yes |
| format 1 refused (imported) / read (composed) / format 3 refused | yes |
| host strings | yes |
| e2e: no absolute path/home/CR; 4 outputs; import→build→import unchanged; `dotnet build` same types/methods/resources; import after `dotnet build` unchanged; added `.cs` | yes, one test |
| `Restore.rounds` | yes, pure |

**Gaps (should-fix unless marked):**

- **The masks-mismatch fallback** (warning, `Masks = []`, a build then compiles msbuild's files
  only) is untested.
- **Multiple configurations**: untested, and B1 hides there.
- **`CustomAfterMicrosoftCommonTargets` chaining**: untested.
- **F#** (`FscCommandLineArgs`, `compilerOf` with `DotnetFscCompilerPath`, F# source order):
  the code path exists but is not exercised (the report says so). The self-build stays
  composed, so nothing guards it. Add a one-project fsproj e2e case (the fixture style is
  cheap), or mark `record` "C# only" in the docs until then.
- **Package sources** (Test.Sdk `Program.cs` → `packaged`): untested.
- **The S.R.E guard** (`GenerateResourceUsePreserializedResources` on .NET Framework without
  System.Resources.Extensions): untested.
- **The `"import-2"` step proves little.** "import after Xake's build: unchanged" is trivially
  true, because Xake builds into `out/` and does not touch msbuild's `obj/`/`bin/`. The
  meaningful step is `import-3`, after `dotnet build`, which exists. Nit.

**Robustness:**

- The e2e assertions avoid SDK-specific switch text (`/nullable:enable`, `sdk:Roslyn/bincore/
  csc.dll`, `app.GlobalUsings.g.cs` are stable between SDK 8 and 10). It passes here on 10.0.401
  and should pass on the CI 8.0.x leg.
- It needs network (Newtonsoft, the net472 reference assemblies, `microsoft.netcore.app.ref`
  8.0 on an SDK-10-only machine), the same as the existing e2e tests.
- On Linux CI, `hostStrings` takes `Path.GetTempPath()` = `/tmp/` as the candidate `/tmp`. Any
  lock line containing `/tmp` fails the import, including a non-path text in a generated
  editorconfig or AssemblyInfo. That is unlikely but a false positive by construction. Nit:
  match path segments (`/tmp/`), as the RID check does.
- Windows: unverified (see 3).

---

## 7. Bugs fixed along the way: are they tested?

| fix | test |
|---|---|
| `~` quoting (`Import.unquote`, `Import.fs:885-894`) | **none.** `unquote` is `internal` and pure. Add cases: `"/analyzerconfig:\"/a/~b/x\""`, `"\"/a b/c.cs\""`, `"/reference:\"/a,b.dll\",alias"` (keeps its quotes), `/define:"A;B"`. Also verify that a path left with a space after `unquote` is re-quoted in the response file `Csc.run` writes. |
| `PrepareForBuild` for resource names (`AssignLinkMetadata`, a shared project's `Link`) | **none.** The e2e fixture has no `.projitems`/`Link`. ImportFidelityTests covers the evaluation route only. |
| `Restore.download` in rounds | yes, `Restore rounds` (pure). The integration (one id in two versions actually fetched) only via the AR copy. |
| global.json pin tokenized (`Pin'`, `Import.fs:1238-1242`) | **indirect.** The e2e lock shows `"Pin": "global.json"` (pin file outside the roots) but asserts nothing. The tokenized case (`$(ProjectRoot)/global.json`) is not exercised. |
| document `Properties` tokenized (`Lock.fs:684`) | **none.** The golden has `"Brand": "X"` only. Add a path-valued property to the golden. |

---

## 8. Docs and the release note

**Accurate:** lock.md "Recorded locks" (steps, identities, format 2 sample, building, checks);
description.md "Import that records"; the schema `items.resources`.

**Contradictions (should-fix, S11):**

- `lock.md:316-344` §Prerequisites still says:
  - the prerequisite is written only for an exact pin, "otherwise no prerequisite, and a
    warning";
  - "Not `$(DotnetRoot)/packs/`";
  - "every lock without one ... is byte-identical to what 0.1 and 0.2 previews wrote".

  All three are false now: a recorded entry always has the prerequisite, `pack:` under `packs/`
  is accepted by hash, and `"Format": 2` is in every lock. Add the recorded-entry rule there.
- Code doc comments carrying the same stale statements:
  - `Lock.fs:101-107` (`Prerequisite`: "written only when global.json pins that version
    exactly");
  - `Lock.fs:1067-1073` (`hermeticViolations`: "never a reference, and never
    `$(DotnetRoot)/packs/`");
  - `Import.fs:1108-1115` (`record`: "one restore per project ... concurrently", "one
    design-time build per (configuration, framework)": now one solution restore and one process
    per configuration).
- `hermetic-mode.md` should get the recorded-entry rules (pack by hash, every SDK role under the
  prerequisite).

**Release note (S12): missing.** The PR has no release-note or changelog entry. For the
"hermetic after PR 2" release (decision 8) it needs at least:

- format 2, a breaking change for imported locks;
- `Import.record` as the new import;
- `Project.import` status;
- `TargetFileName` outputs (`*.exe` rules needed);
- the SDK pin being stricter than global.json;
- writes into `obj/`;
- `Restore.download` rounds.

**Nit:** the committed self-build locks (`locks/*/Release.json`) are still format 1 (no
`"Format"`). They read fine, but the next `update-locks` rewrites all three. Regenerate them in
this PR so the churn does not land in an unrelated one.

**Nits in code:**

- `RecordContext.PinFile` is assigned but never read.
- The `NuGetPackageId`/`NuGetPackageVersion` metadata is dumped but never read (the identities
  are path-based by design). Drop it, or use it to cross-check `pkg:` naming.
- `Pin'` is an unusual name for a public-looking field.

---

## Verdict

**Mergeable into `feature/hermetic-build` after B1.** B1 is a small, verified bug: a
configuration other than Release, or any Platform, fails the import.

The design is faithful to "Xake does not emulate msbuild": the night's emulation is genuinely
absent and the composed C# runner is untouched. The one-process design-time build is sound and
fast. The identities are well chosen and round-trip.

Before the hermetic **release** (decision 8: "hermetic after PR 2"), also do:

- S5: identity-check switch paths;
- S8: staleness check of the `Imports`;
- S10: allow-list the recorded texts;
- S11 and S12: docs contradictions and release note;
- the `unquote` test.

The rest can go to the tracker.

## What PR 3 / PR 4 must not forget

1. **App outputs:** `runtimeconfig.json`, `deps.json`, `<exe>.config` (`AppConfigWithTargetPath`
   is not recorded yet), the apphost as part of the SDK without a RID (decision 3), copy-local
   from `ReferenceCopyLocalPaths`.
2. **`hostStrings` against copy-local/deps:** a RID-agnostic app's copy-local and deps.json name
   `runtimes/<every rid>/...`. The current RID check (`/osx-arm64/`, `"win-x64"`) would refuse
   every such lock. Narrow it to "the importing machine's RID outside a package identity" before
   PR 3 records them.
3. **Satellite assemblies** (`WithCulture` resources, now dropped with a line). AR has
   localized resources.
4. **Generated binaries** that are not resx: WPF `g.resources`/BAML (with the WPF step,
   decision 3), WinForms `.licenses`. Fail loudly until supported (S10).
5. **Deploy (PR 4):** `GetCopyToOutputDirectoryItems` and `Content`/`None` copy items, package
   `contentFiles`, the `.xml`/`.pdb` beside `TargetFileName`.
6. **`pr/sourcelink`:** `/sourcelink`, `/embed` and `/pathmap` from the script's URL and roots,
   for outputs that are reproducible across machines.
7. **The SBOM** for downloaded `pack:` packages (no `Packages` row today).
8. **Retire or rebase `Project.import`** (S7). Exercise `record` on an fsproj before claiming
   F# support.
9. **Verify on Windows:** .NET Framework reference assemblies from Program Files, case of
   `packs/` lookups, CRLF from git autocrlf on the lock and on recorded texts.
10. **Keep the composed route frozen:** `platformOf`/`nearestFramework` should not grow further
    (decision 5).
