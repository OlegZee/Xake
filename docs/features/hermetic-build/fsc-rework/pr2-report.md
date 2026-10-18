# PR 2 report: `fsc/csc { project }` + `config` + packages via msbuild restore

Branch `pr/csc-project` (worktree `.claude/worktrees/pr/csc-project`, on `pr/description-model`), 4 commits,
not pushed: 7a8db6d `Nuget.compileItems`, ac91be9 `Described.fs` + builder ops, d2877c2 tests + fixtures, f450a1b docs.

## What landed

- `src/hermetic/Described.fs` (~540 lines with doc comments), compiled after `Lock.fs`. Module `Described`:
  `restore d fwk : Recipe<Restored>`, `Restored = { Framework; References; Packages }`, `preset`, `frameworkDefines`,
  `implicitFrameworkReferences`, `implicitPackages`, `restoreProjectText`, `expand` (C# masks via `getFiles`),
  `fsc settings spec`, `csc settings spec`, `expandCsc`, `buildCsc options path masked`.
- Builder ops (extensions, like `lock`): `project "path"` on `FscSettingsBuilder`/`CscSettingsBuilder` returns
  `FscDescribed`/`CscDescribed`; on that state: `config`, `packages`, `lock`, `nofetch`, `resolve` (overloaded custom
  operations; checked to work with F# extension members). `Run` compiles (no lock) or gates by the lock.
- `src/hermetic/Nuget.fs`: `Nuget.compileItems assets fwk` (compile assets per package; `_._` skipped), additive.
- Tests `src/hermetic.tests/DescribedTests.fs` (10); fixtures `fixtures/src/core/xake.project.json`,
  `fixtures/src/dotnet/xake.project.json` (writer format, `project` = the fsproj name as if beside it, `net462` first;
  a test checks them against the fsproj: compile order, TFMs, packages per framework, references, IVT, doc).
- Docs: `docs/csc-syntax.md` new section "From a project description", `docs/tasks.md` (csc and fsc notes),
  `docs/hermetic/lock.md` two API rows.

## Where `project`/`config` live and why

In `Xake.Hermetic.Dotnet`, not `src/dotnet`: hermetic builds on the published `Xake` 3.6 package, so it cannot change
the settings records, and the base must not know descriptions. `project` cannot be a pure settings transform: the
framework, the output (for `doc`), `NETFX-TARGET` and the description file itself are only known at recipe time. So
`project "path"` takes the **description file path** (not a `Description.Project`: the project folder is relative to
the description's location) and switches the block to a small state; ordinary ops (`targetfwk`, `out`, `ref` for
projectRefs' outputs) go before it. Framework: `targetfwk` (must be in `frameworks`), else the only one, else
`NETFX-TARGET`, else fail.

## Presets (SDK 10.0.401, checked against `dotnet build -c Debug|Release` command lines)

| | F# | C# | Defines |
|---|---|---|---|
| Debug | `--debug+ --debug:portable --optimize- --tailcalls-` | `/debug+ /debug:portable /optimize-` | TRACE;DEBUG |
| Release | `--debug:portable --optimize+` | `/debug- /debug:portable /optimize+` | TRACE;RELEASE |

Sources: `Microsoft.NET.Sdk.props`, `Microsoft.FSharp.NetSdk.props`, `ImplicitConfigurationDefine`. Note: Release
defines `RELEASE` too (the task text said TRACE only; the SDK adds it). Plus always: SDK framework symbols
(`NETSTANDARD2_0`, `NET462`, `*_OR_GREATER`, verified for netstandard2.0/net462/net8.0), `--deterministic+`,
`--pathmap:<ProjectRoot>/=/_/` (written `$(ProjectRoot)/=/_/` in the lock via `tokenizeAll`). SDK constants
(`--warn:3`, `/nowarn:1701,1702`, `/highentropyva+`, ...) are not carried. Only Debug/Release; others fail.

## How packages flow

`Described.restore d fwk` (the step PR 3 calls) writes `obj/xake/packages/<name>/<fwk>/restore.csproj` with one
`PackageReference` per package (version as written, floating left to msbuild; F# on net40..net47 adds the SDK's
implicit `System.ValueTuple` 4.6.2; `DisableImplicitFrameworkReferences`, no reference-assembly package), runs
`dotnet restore` (same flags as `DotNetFwk.restoreArgs`, `NUGET_PACKAGES` = build's folder), reads
`project.assets.json` with `Nuget.readAssets` + `Nuget.compileItems` -> `References` (sorted) and `Packages`
(`Lock.packagesOf`, for `Entry.Packages`). The block takes it with `packages r`; a description with packages and
no `packages` fails naming `Described.restore`. fsc/csc never restore.

## C# masks and the 0.2 lock reader

Convention aligned with PR 5: `!` prefix = exclude, applied in order; `dir/**/*` = all files below. `resolve` returns
a `Csc` whose `Sources` are the generated IVT file then the masks made absolute (`!` kept in front:
`!$(ProjectRoot)/cs/obj/**/*` in the lock). `expand` builds **one** Xake fileset based at the first include's
wildcard-free folder, each mask relative to it, `Includes`/`Excludes` in order, read through `getFiles` (GetFiles
dependency), result sorted ordinal. `lock` (`Described.buildCsc`): missing -> record masks (fails under CI); present ->
`Lock.verify` masks as written; then the recorded entry is compiled with masks expanded (`Lock.compileWith`).
Test: adding `Sub/C.cs` rebuilds the dll, lock unchanged; `obj/Broken.cs` is excluded.
**0.2 reader: accepts a mask (and a `!` mask) in `Sources` unchanged** (`Lock.format`/`Lock.parse` round trip
byte-identical; `Sources` are plain strings). But `Lock.build`/`Lock.compile` of a masked entry would fail
(`needFiles` on a mask): replay needs the expansion (`buildCsc` now, `Lock.compileAs` in PR 4).

## Deviations

1. Restore is a `PackageReference` restore of a synthesized project, not `Restore.download` (PackageDownload gives
   no graph and no compile assets); same mechanism/flags/folder, `Nuget.readAssets` as asked.
2. `project` takes a path; ops after `project` limited to config/packages/lock/nofetch/resolve.
3. `project` adds SDK implicits the import drops: .NET Framework assemblies (`System`, `System.Core`, `System.Xml`, ...),
   `netstandard.dll` for C# netstandard (composed csc references only mscorlib), F# `System.ValueTuple`.
4. Generated `InternalsVisibleTo` source (`obj/xake/<asm>/<fwk>/<asm>.InternalsVisibleTo.fs|cs`, in `Generated`);
   no `$(Version)`/assembly-version attributes (brief §6 open).
5. Resources: plain files as `--resource:`/`/resource:` with msbuild's default name; `.resx` fails ("not yet").
6. `-o`: kept (needed to compile); removing it from lock-bound options is left to PR 3/4's generation.
7. projectRefs: passed by the script with `ref`; a missing one only warns.

## Commands

- `dotnet build src/hermetic --no-incremental`: `Build succeeded. 0 Warning(s) 0 Error(s)` (net462 + netstandard2.0)
- `dotnet build src/dotnet --no-incremental`: `0 Warning(s) 0 Error(s)` (unchanged project)
- `dotnet test src/hermetic.tests`: `Passed! - Failed: 0, Passed: 181, Skipped: 0, Total: 181`
- `dotnet test src/tests`: `Passed! - Failed: 0, Passed: 314, Skipped: 1, Total: 315`

## Questions for the user

1. `project "path"` (file path) rather than `project d`: acceptable? (A description alone has no location.)
2. Release defines `RELEASE` as the SDK does: keep?
3. Adding SDK implicits (framework assemblies, `System.ValueTuple`) in `project`/`restore` rather than in the description: agreed?
4. Mask base = first include's fixed folder (PR 5 expands against the project folder): fine, or carry the project folder?
5. `.resx` resources and assembly version attributes: next PR or PR 9 (the trial project)?
