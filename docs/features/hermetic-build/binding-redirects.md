# Binding redirects at import — design note (2026-10-18)

Status: implemented (Xake.Hermetic.Dotnet 0.5.1, branch `pr/binding-redirects`), as recommended in
2.2. Owner's decisions on section 5: Q1 accept and document, Q2 no build-time warning, Q3 run 1
unchanged, Q4 LF as today, Q5 version 0.5.1, Q6 name kept, comment reworded. Gap S55 ("binding
redirects are not generated").

Sources: `src/hermetic/Import.fs` (`record`, `dumpTargets`, `appOf`, `stubAssembly`),
`Lock.fs` (`App`), `Apps.fs` (`write`), `Locks.fs` (`compileAsWith`, `runFolderWith`),
`docs/hermetic/lock.md`, the SDK 8.0.425 targets (`Microsoft.Common.CurrentVersion.targets`,
`Microsoft.NET.Sdk.BeforeCommon.targets`, `Microsoft.NET.GenerateSupportedRuntime.targets`),
`Microsoft.NET.Test.Sdk`'s build files, and a trial product's locks and msbuild reference build.

## 0. Summary

**Most of the work is already done, and the remaining gap is smaller than the gap entry says.**
The import's design-time builds already run `GenerateBindingRedirects`: `BuildOnlySettings` sets
`BuildingProject=true`, and `ResolveReferences` depends on `GenerateBindingRedirects` and
`GenerateBindingRedirectsUpdateAppConfig`. The second run (the "app run", referenced projects
replaced by stand-ins) leaves msbuild's finished `<TargetFileName>.config` in the intermediate
folder. Three things go wrong after that:

1. **.NET Framework applications** (`<app>.exe.config`): the importer records the file *before*
   redirects on purpose (`_XakeAppConfigBeforeRedirects`, after `GenerateSupportedRuntime`).
2. **.NET Framework test libraries** (`<name>.dll.config`): never recorded. `appOf` drops the
   config when `HasRuntimeOutput` is false, and these projects are not in the app run.
3. **.NET applications and tests** with `AutoGenerateBindingRedirects=true` (a redirect-only
   `.dll.config`): already recorded *with* redirects, because no `_XakeAppConfig` is captured there
   and the code falls back to the final `AppConfigWithTargetPath`. This works by accident.

A read-only probe on a trial product (one brand, Release) compared the 49 configs the last import
left in `obj/xake-record/<key>/<tfm>/` with msbuild's `bin/Release/<tfm>/`:

| kind | count | result |
|---|---|---|
| net472 application `.exe.config` | 4 | **byte-identical** (up to 24 redirects) |
| net472 test library `.dll.config` | 34 | **byte-identical** (but from the first run: see 2.4) |
| net8.0-windows application/test `.dll.config` | 11 | msbuild has 1–3 **more** redirects, never fewer, same order: `Microsoft.CSharp`, `System.Threading.Tasks.Extensions`, `System.Drawing.Common` |

The 11 differences are conflicts that RAR finds only through a referenced project's *built*
assembly. For example, a netstandard2.0 library compiled against `Microsoft.CSharp` 4.x, while
net8.0 has 8.0.0.0. The stand-ins have no references, so the import cannot see these conflicts.
The .NET runtime ignores `<assemblyBinding>`, so the difference does not change behaviour.

**Recommendation.** Record msbuild's finished config from the stand-in run for every project where
msbuild generates redirects. The build copies it as-is. That needs no script change, no API change
and no lock format change. Accept the via-project residue as a documented known difference for now.

## 1. What msbuild does

Targets (Microsoft.Common.CurrentVersion.targets, SDK 8.0.425):

```
ResolveReferences = ... ResolveProjectReferences; ...; ResolveAssemblyReferences;
                    GenerateBindingRedirects; GenerateBindingRedirectsUpdateAppConfig; ...
ResolveAssemblyReferences   RAR(AutoUnify=$(AutoUnifyAssemblyReferences),
                                SupportsBindingRedirectGeneration=$(GenerateBindingRedirectsOutputType),
                                AppConfigFile=@(_ResolveAssemblyReferencesApplicationConfigFileForExes))
                            -> Output SuggestedRedirects -> @(SuggestedBindingRedirects)
_GenerateSuggestedBindingRedirectsCache   hash of @(SuggestedBindingRedirects) ->
                            $(IntermediateOutputPath)$(MSBuildProjectFile).SuggestedBindingRedirects.cache
                            (WriteOnlyWhenDifferent)
GenerateBindingRedirects    Condition: AutoGenerateBindingRedirects == true
                                   and GenerateBindingRedirectsOutputType == true
                                   and @(SuggestedBindingRedirects) != ''
                                   and DesignTimeBuild != true and BuildingProject == true
                            Inputs: $(MSBuildAllProjects); @(AppConfigWithTargetPath); the cache file
                            Outputs/writes: $(_GenerateBindingRedirectsIntermediateAppConfig)
                                          = $(IntermediateOutputPath)$(TargetFileName).config
GenerateBindingRedirectsUpdateAppConfig   if that file exists: AppConfig := it,
                            AppConfigWithTargetPath := it (TargetPath $(TargetFileName).config)
_CopyAppConfigFile          copies @(AppConfigWithTargetPath) to $(OutDir)%(TargetPath)
```

Where the gating properties come from:

- `GenerateBindingRedirectsOutputType`: true for `OutputType` `exe`/`winexe` (common targets).
  `Microsoft.NET.Test.Sdk`'s `build/net462/*.targets` also sets it true for a .NET Framework test
  library. This is why such a project gets `<name>.dll.config`.
- `AutoGenerateBindingRedirects`: the common targets default it to true for .NETFramework ≥ 4.7.2.
  The SDK defaults it to true for .NETFramework with `HasRuntimeOutput`, and Test.Sdk net462 sets
  it too. A repository may set it explicitly, which makes .NET applications and tests get a
  redirect-only `.dll.config` as well.
- `AutoUnifyAssemblyReferences`: false only when the output type supports redirects and
  `AutoGenerateBindingRedirects` is not true. The SDK sets it true for libraries and for
  .NETCore/.NETStandard. RAR still emits `SuggestedRedirects` when it unifies.
- `AppConfig`: the project's own `App.config` (`PrepareForBuild`: the `AppConfig` property, else
  `FindAppConfigFile` over `None`/`Content`). For .NETFramework applications, the SDK's
  `GenerateSupportedRuntime` (`BeforeTargets="GenerateBindingRedirects"`) first writes
  `$(TargetFileName).withSupportedRuntime.config` with `<startup><supportedRuntime>` and
  repoints `AppConfigWithTargetPath` at it. `GenerateBindingRedirects` takes that as its input.
- `TargetFileName`: `X.exe` for a .NET Framework application, so the file is `X.exe.config`.
  It is `X.dll` for a .NET application (the apphost is separate) and for a test library, so the
  file is `X.dll.config`.

What the file contains: the input config (if any) as parsed. For each suggested redirect not
already present, the task adds its own `<assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">`
holding `<dependentAssembly><assemblyIdentity name publicKeyToken culture/>`
`<bindingRedirect oldVersion="0.0.0.0-V" newVersion="V"/>`. The range always starts at 0.0.0.0
and ends at the winning version. So the content depends only on **which** strong-named
assemblies conflict and on the winner's version, never on the losing version. The order is RAR's
order: the position of the winning (primary) reference in RAR's input list. On macOS the file
was LF, without BOM and without a final newline. Windows is not checked (see Q4).

Behaviour by kind:

- net472 exe: own App.config + `supportedRuntime` + the redirects. Conflicts come from package
  assemblies against each other, which RAR finds from the package files. Framework assemblies are
  unified by the redist list without a redirect.
- net472 test library: redirect-only or App.config + redirects `.dll.config`. A referencing test
  project copies it as a related file (`AllowedReferenceRelatedFileExtensions` includes
  `.dll.config`).
- .NET exe/test with redirects on: `.dll.config` with 100+ redirects. Most are framework
  assemblies against the packages' and the libraries' references. The runtime does not read them.

## 2. Import side

### 2.1 What runs today

`Import.record` per configuration:

1. **Run 1**, one traversal: `BuildOnlySettings;PrepareForBuild;ResolveReferences;PrepareResources;`
   `ResolveKeySource;Compile;XakeRecordDump` with `BuildProjectReferences=false`,
   `SkipCompilerExecution`, `ProvideCommandLineArgs`, and `IntermediateOutputPath =
   obj/xake-record/<key>/<tfm>/` (the "scratch"). The dump is written here. RAR resolves a project
   reference to msbuild's own `bin/<cfg>/<tfm>/X.dll`. It reads that file if it exists, so after
   a `dotnet build` run 1 sees the real libraries. On a fresh clone it sees nothing.
2. **Run 2**, the app run, only over projects with `HasRuntimeOutput`:
   `BuildOnlySettings;PrepareForBuild;ResolveReferences;XakeAppFiles` with `-p:OutDir=<temp>/app-out-<cfg>/`.
   Every referenced project's output is a stand-in in that folder (`stubAssembly`: empty, version
   `65534.65533.65532.65531`, no references). Before the run, the importer deletes
   `xake-app.deps.json` and `xake-app.runtimeconfig.json` in the scratch.
3. `appOf` then reads the scratch files and records them in `<cfg>.files/<tfm>/app/<name>`
   (`App.Files`, LF, sha256).

`GenerateBindingRedirects` runs in **both** runs, in the same scratch folder. Run 2 regenerates the
file when its suggestions hash differs from run 1's and leaves run 1's file in place when it has
none. This explains the probe: the .NET files are run 2 (stand-ins), and the .NET Framework test
files are run 1 (msbuild's real `bin/`, host state).

### 2.2 Proposed change (no new msbuild invocation, no new target in the target list)

1. **Run 2 covers every project that generates redirects**: `HasRuntimeOutput` *or*
   (`AutoGenerateBindingRedirects` and `GenerateBindingRedirectsOutputType`). Both properties are
   already in `designTimeProperties`. This adds the .NET Framework test libraries. Their
   `XakeAppFiles` is a no-op (conditioned on `HasRuntimeOutput`). Stand-ins are created for their
   project references as for the applications.
2. **Delete `<scratch>/<TargetFileName>.config` before run 2**, next to the deps/runtimeconfig
   deletion. Afterwards that file exists only if run 2's `GenerateBindingRedirects` wrote it. Run 1
   (host-dependent) and an earlier import can no longer leak in. Deleting the
   `.SuggestedBindingRedirects.cache` is not needed: a missing output forces the target.
3. **`appOf` picks the config in this order**:
   1. `<scratch>/<TargetFileName>.config` if it exists: msbuild's finished file with redirects
      (add `_GenerateBindingRedirectsIntermediateAppConfig` to the dumped properties rather than
      reconstructing the name);
   2. otherwise the config before redirects: `_XakeAppConfig` (.NET Framework:
      `withSupportedRuntime.config`), then a new `_XakeOwnAppConfig`
      (`AfterTargets="PrepareForBuild"`: the project's own App.config, copied as-is and named by
      path and hash as today);
   3. none.

   The "final `AppConfigWithTargetPath`" fallback goes away. After step 2 it can name a file that
   no longer exists (run 1's redirect file), which would silently drop a project's own App.config.
4. **Record it for libraries too**: drop the `if runtime then config` filter when
   `BindingRedirects` is true. A .NET Framework test library already has an `App` (copy-local
   packages), so `App.Files` gets `<name>.dll.config`.
5. **Guard**: fail the import, naming the file, when the stand-in version `65534.65533.65532.65531`
   appears in a recorded config. That would mean a redirect to a stand-in, the same rule
   `depsTokens` applies to `deps.json`.
6. Keep `_XakeAppConfigBeforeRedirects`. It now serves only as the fallback for a .NET Framework
   application without suggestions.

Captured output path: no new item is needed. Run 2 shares run 1's scratch (same
`runProperties`), and `IntermediateOutputPath` and `TargetFileName` are already dumped.

### 2.3 Storage

As today, no format change: `App.Files` gets `{ Name: "<TargetFileName>.config", Path:
"<cfg>.files/<tfm>/app/<TargetFileName>.config", Sha256 }`. The file sits next to the
runtimeconfig/deps.json under `<cfg>.files/<tfm>/app/`, as LF text. The existing `hostStrings`
check already covers `.config` files. `App.BindingRedirects: bool` stays. Its documented meaning
changes from "msbuild adds redirects; the recorded config has none" to "the recorded config is
msbuild's finished one, redirects included".

Lock format stays 2. A re-import changes the config hashes of .NET Framework applications and adds
the test libraries' `.dll.config`. An old lock still builds, without redirects, as before.

### 2.4 Determinism: what the redirects depend on

| input | in the lock? |
|---|---|
| package assemblies (versions, references, public key tokens) | yes: `pkg:` identities with sha256 |
| targeting/reference packs, framework list (net472 redist unification) | yes: `pack:`/`sdk:` identities, SDK version prerequisite |
| reference order (RAR order = output order) | yes: msbuild's `ReferencePath` order |
| the project's own App.config | yes: `Evaluation.Imports` (an edit means stale lock) |
| referenced projects' built assemblies | **no**: stand-ins (fixed version, no references), so the result does not depend on whether anything was built (the same rule as `deps.json`) |
| machine paths | none in the file; `hostStrings` checks |
| line endings / BOM | LF, no BOM on macOS; Windows unverified (Q4) |

The fixture test "the locks hold no host string and an import after both builds records the
same" keeps holding. It is the regression test for this table.

**Fidelity limit** (by construction): a conflict visible only through a referenced project's
compiled assembly, meaning its `AssemblyRef` to an older version of something the application
resolves newer, is not seen. Measured: 0 of 4 .NET Framework applications. All 11 .NET
(windows) files are affected, with 1–3 framework assemblies each, which the runtime ignores. The
.NET Framework test libraries are not measured under stand-ins yet. Expect 0, as for the
applications: on .NET Framework the library and the application resolve the same package versions
through PackageReference transitivity, and framework assemblies unify without a redirect.

### 2.5 Cost

Run 2 grows by the .NET Framework test libraries: about 34 per configuration on the trial
product. Each costs an evaluation plus RAR in the already-running parallel msbuild process, an
estimated +2–5 s against a ~50 s import. To be measured. No extra process starts.

## 3. Build side

Nothing new. The existing paths already consume `App.Files`:

- **Compile rule** (`Locks.compileAsWith` → `Apps.write`): writes every `App.Files` entry next to
  the assembly, checked against its hash, bytes as recorded. `Apps.write` runs for any entry with
  an `App`, so the test libraries' `.dll.config` lands in the output folder too.
- **Run folder** (`Locks.runFolderWith`): the root's own files come through
  `outputsOf ... (fun _ -> true)`, so the config is placed. A referenced project's
  `.dll.config`/`.exe.config` comes through the root's `Related` extensions, which the locks record
  as `.pdb;.xml;.pri;.dll.config;.exe.config`, the way msbuild copies it.
- `Locks.runs`: an `App` with non-empty `Files` "runs". The only libraries that gain `Files` are
  Test.Sdk ones, which are `IsTestProject` anyway.
- **pack**: unaffected.
- **The consumer script**: no change. Its `.exe`/`.dll` compile rules already produce the side files,
  and its deploy step already places them.

An alternative was to compute RAR's conflicts in Xake at lock time and write the redirects
ourselves. It is not taken: the importer keeps its rule that it records msbuild and derives
nothing.

## 4. Fidelity check

1. **Before coding** (done read-only, section 0): compare `**/obj/xake-record/<key>/<tfm>/*.config`
   with `bin/Release/<tfm>/` of the same project.
2. **After the PR**: re-import, build, deploy, and run the consumer's comparison against msbuild's
   reference build:
   - expected **identical**: every .NET Framework `.exe.config` and test `.dll.config`. The
     "msbuild only" `.config` files in compile outputs and run folders go to 0.
   - expected **"differs by binding redirects only"**: the .NET (windows) `.dll.config` files. A
     sharper bucket should be added: *"msbuild has extra redirects only"*, true when Xake's
     `<dependentAssembly>` blocks are an ordered subsequence of msbuild's. Report the extra names.
     Anything beyond the via-project names, or a redirect Xake has and msbuild lacks, is a bug.
   - the existing coarse check strips `<runtime>…</runtime>` and reads with `utf-8-sig`, so it
     hides BOM differences. The new bucket should compare bytes.
3. **Xake repo tests** (section 6): byte equality against `dotnet build`.

## 5. Open questions (with recommended defaults)

- **Q1. Via-project redirects on .NET: accept or complete?** Default: **accept** and document as a
  known difference. The runtime ignores them, completing them would mean re-implementing RAR's
  conflict walk in Xake, and the comparison bucket keeps them visible.
- **Q2. A build-time guard for .NET Framework?** `Apps.write` would read the `AssemblyRef`s of the
  referenced projects' outputs and warn when a strong-named reference differs from the version
  placed and no recorded redirect covers it. Default: **not in this PR**. Measured 0 on .NET
  Framework applications. Follow up only if a case shows.
- **Q3. Run 1 still generates redirects (wasted, host-dependent): suppress it?** For example,
  `AutoGenerateBindingRedirects=false` in run 1. Default: **no**. That flips
  `AutoUnifyAssemblyReferences` for .NET Framework executables and could change what RAR hands to
  csc. Run 1's file is deleted before run 2 anyway.
- **Q4. Line endings on Windows.** The lock stores LF. If msbuild on Windows writes CRLF, the build
  differs byte-wise there. Default: **keep LF in the lock. Decide after a Windows run.** If needed,
  `Apps.write` converts a generated `.config` to the host's newline, never the project's own
  copied App.config.
- **Q5. Version.** No API and no format change, but locks change on re-import. Default:
  **Xake.Hermetic.Dotnet 0.4.3**, with a release note: "re-import to record binding redirects; old
  locks build as before".
- **Q6. `_XakeAppConfigBeforeRedirects`.** Default: keep the name and reword the comment
  ("fallback when msbuild suggests no redirect").

Not reopened: the `Csc` record, no hooks, `Import.record` as the single entry point, no new
`RecordOptions` field. Redirects are always recorded when msbuild generates them.

## 6. Work breakdown (PR to `dev`)

- `src/hermetic/Import.fs`
  - `dumpTargets`: add `_XakeOwnAppConfig` (`AfterTargets="PrepareForBuild"`) and its item in
    `designTimeItems`. Add `_GenerateBindingRedirectsIntermediateAppConfig` to
    `designTimeProperties`. Reword the comment of `_XakeAppConfigBeforeRedirects`.
  - `record`: select `appRuns` by `HasRuntimeOutput || (AutoGenerateBindingRedirects &&
    GenerateBindingRedirectsOutputType)`. Delete the scratch `<TargetFileName>.config` with the
    deps/runtimeconfig files.
  - `appOf`: the config choice of 2.2.3, recorded also for a library with `BindingRedirects`, plus
    the stand-in version guard. Update the doc comment.
  - about 40–60 lines.
- `src/hermetic/Lock.fs`: doc comments of `App`/`AppFile`/`BindingRedirects` only.
- `docs/hermetic/lock.md` (`Files`, `BindingRedirects`, "Not recorded", "Known differences") and
  `docs/hermetic/README.md` ("Not yet: binding redirects").
- `src/hermetic.tests/AppTests.fs`, fixture `Import record applications`:
  - `winexe` (net472): add a package conflict (`System.Memory` 4.5.5 +
    `System.Runtime.CompilerServices.Unsafe` 6.0.0). Assert `winexe.exe.config` is byte-equal to
    msbuild's (today a text compare) and contains the `0.0.0.0-6.0.0.0` redirect.
  - `tests`: multi-target `net8.0;net472`, giving a .NET Framework Test.Sdk library. Assert its
    `.dll.config` is recorded, written by the compile, byte-equal to msbuild's, and placed in its
    run folder.
  - the existing "import after both builds records the same" covers independence from built
    state.
- `src/hermetic.tests/RecordTests.fs` (pure): `appOf` on a fake dump plus scratch files. The final
  file wins. Without it: `_XakeAppConfig`, then the own App.config. A library with
  `BindingRedirects` gets its `.dll.config`. The stand-in version in a config fails, naming the
  file.
- Size: about 150–250 lines including tests and docs. One PR.
- PR title: **`hermetic: record msbuild's binding redirects at import (.exe.config, test .dll.config)`**
