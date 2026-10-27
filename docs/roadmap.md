# Roadmap

Ideas and larger items that are agreed in direction but not scheduled. Working state of a
feature lives in `docs/features/<name>/`; this page is the cross-feature list.

## Delivered

- Xake 3.13.0 and Xake.Hermetic.Dotnet 0.8.0 (2026-10-27): line-ending-neutral hashes of msbuild files in locks; targeting
  packs from NuGet by default in `Import.record`; a check of the MSBuild 17.10 floor before an import; msbuild and NuGet
  errors in the log with a summary on failure; secret script variables (presence or HMAC digest in `.xake`, masked in every log).
- Xake 3.11.0 and Xake.Hermetic.Dotnet 0.7.0 (2026-10): compile from an SDK path with a space,
  `Locks.doc` for the `.xml` rule, `Locks.compile` checks its target, SDK-free recorded locks
  (0.6.0, shipped in 0.7.0), `Tool.quoteArgument`. Earlier: project descriptions in the core
  (3.9), recorded import, applications and run folders (0.4), binding redirects (0.5.1), the
  fsc path (3.5 / 0.2), `rm`/`cp` with many targets. The consumer's build script runs on 3.11.0
  / 0.7.0. The working list is `docs/features/hermetic-build/tracker.md`.

## Principles

- **Hosts: the .NET SDK only; targets: every platform.** No backports of the engine or the
  tasks to the full framework or mono as build hosts. Composed `csc {}`/`fsc {}` must produce
  `netN.0`, `netstandard2.x` and `.NET Framework 4.x` outputs from any host (macOS, Linux,
  Windows) with no manual setup: reference packs and facades restored or taken from the SDK.
  Verified 2026-10-04 on macOS: csc net462 works; fsc net462 needs an implicit `netstandard.dll`
  facade and a default `FSharp.Core` (fix in 3.5).

## Engine

### Rules with arguments (idea, 2026-10-02)

Every rule that uses its pattern's capture groups starts with a row of `let! x = getRuleMatch "x"`
and, for multi-file targets, a list pattern over `getTargetFiles()`. Proposal: a rule form that
binds the capture groups **positionally**, in the order they appear in the pattern, to the
parameters of a lambda, and the target file(s) after them:

```fsharp
"out/(fwk:*)/(lib:*).dll" ==> fun (fwk, lib) out -> recipe { ... }

["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"] ==> fun (fwk, lib) (dll, doc) -> recipe { ... }
```

The pattern parser knows the number of groups, so an arity mismatch fails when the script is
loaded, not inside the recipe. Tuple overloads for 1–4 groups and 1–4 target files via static
members; the operator is sugar over them. Group names stay as documentation and keep working
through `getRuleMatch` for existing scripts. `build.fsc.fsx` is the first customer. Not started.

### Project descriptions in the core (description done in 3.9, `Import` open)

Done (3.9, #59): `xake.project.json` and `Description` live in the core, next to
`Fingerprints`; `csc/fsc { project }` reads them. Still in Xake.Hermetic.Dotnet: `Import`
(project file -> description), the lock and the hermetic build. Moving `Import` to the core is
open and not scheduled.

### A CLI command that writes descriptions (idea, 2026-10-12)

A built-in Xake CLI command that generates `xake.project.json` from project files (csproj,
fsproj), so a repository needs no script target for the import. Today the script calls
`Import.descriptions` from a target of its own.

## Xake.Dotnet

### B6: `Fsc` through the shared runner (done in 3.5 / hermetic 0.2)

Done: `Fsc` record, `fsc { resolve }`, `fsc { lock }`, `Fsc.run` on the shared runner core;
kept below as the original plan (designed 2026-10-04, `docs/features/hermetic-build/fsc-design.md`;
breaking for fsc in 3.5: `Fsc settings` -> `Fsc.compile`, `targetfwk` required).

`fsc {}` has no resolved record, no `resolve` operation and no hash-checked runner, so there is
no `fsc { lock }` and `build.fsc.fsx` (the build of record) is hermetic only through its kept
msbuild evaluations and `--deterministic+`. Plan: an `Fsc` record next to `Csc` with
`ofSettings`/`run`/`compile`, a private runner core shared with `Csc.run`, `fsc { ...; resolve }`,
then `fsc { lock }` as an extension in Xake.Hermetic.Dotnet, `Project.import` for `.fsproj`
(retiring `Fsproj`), and `build.fsc.fsx` rewritten on a lock. Limits to document: the F#
compiler is not a NuGet package, so the only reproducible compiler path for fsc is the SDK pinned
by `global.json`; the net462 leg still comes from `dotnet pack`.

## Xake.Dotnet, later

### SDK restore by the lock

An fsc lock names an SDK's `fsc.dll` that NuGet cannot restore. The official `dotnet-install`
script installs an exact version into a private folder without admin rights, and Microsoft
publishes release hashes: a "restore SDK" step into the project's `DOTNET_ROOT` would make the
SDK as restorable as a package.

### An F# compiler `toolset`

`FSharp.Compiler.Service` is a current NuGet package carrying the whole compiler; `fsc.dll` is a
thin host over it. A small host shipped with Xake.Dotnet would give `fsc` a restorable
`toolset` like csc's `Microsoft.Net.Compilers.Toolset`.

### Small base fixes found while writing workflows-ru (2026-10-04, not re-checked in October)

- Composed `csc {}` does not pass `/deterministic` (fsc `--deterministic+`): lock-built outputs
  are not byte-reproducible unless the script adds it. Make it the default in composed mode.
- `norestore`/`packageroot` do not cover `toolset`/`CSC_TOOLSET`, the framework reference
  packages or fsc's default `FSharp.Core`: those resolve through `restorePackage None` into the
  machine cache before the lock is read. Offline builds need them routed through the same
  options.
- Decided 2026-10-06 (PR #35 reworked): `runtimeconfig.json` for a `netN.0` application is
  written only when the rule declares it as a target, `["hw.dll"; "hw.runtimeconfig.json"] *..> csc { ... }`.
  A rule declared for one file never puts a second one next to it on its own; the task produces
  what is declared (like `doc`).
- With the `netN.0` default the lock names the SDK's targeting pack under `$(DotnetRoot)`, so
  `CSC_TOOLSET` alone does not make a lock portable; `global.json` is still required. Document
  loudly; the "SDK restore" idea above is the real fix.

### Binary compatibility between the packages (found 2026-10-07, practised since 0.5)

Adding a field to the `Csc`/`Fsc` records (`RuntimeConfig`, PR #35) makes the released
`Xake.Hermetic.Dotnet 0.1.0.22` fail at run time against the new base (`Method not found:
Csc..ctor`), although its nuspec range `[3.4.0.21, 4.0)` says it is compatible. F# records are not
binary-compatible across field changes. Rule in use: the hermetic package depends on the base
**within one minor** (0.7.0: `[3.11.0.40, 3.12)`), and every base release that touches `Csc`/
`Fsc`/`RunOptions`/`FscRunOptions` is followed by a hermetic release.

## Xake.Hermetic.Dotnet

Next, in the order of the tracker's Open list: a Windows run with the SDK under `Program Files`;
the consumer on `Microsoft.Net.Compilers.Toolset` + NetAnalyzers (SDK-free replay); `HERMETIC=on`
for the self-build (needs an exact SDK pin); `Generated`/`.resources` as engine targets; a
`cached` rule decorator; Babel and a real signer (wait for the owner).

### Small: in-lock project references must stay unhashed (found 2026-10-11)

`Project.import` hashes a project reference's output when an old `src/<x>/bin/...` happens to
exist, so the lock differs depending on leftover build output (empty hash vs a real one). A
reference whose project is in the same lock should always be recorded without a hash, as the
lock format intends; `check-locks` ignores empty hashes, so this is churn, not a failure.

From `docs/hermetic/workflows.md` ("Gaps"), in priority order:

1. *(done in PR #30)* A missing lock fails under CI (env `CI`, or `-d CI=on|off`) instead of being
   recorded.
2. `csc { lock }` cannot take `Restore.Options` (package root, no network).
3. Reference assemblies of a composed block are downloaded outside the restore mechanism.
4. The sha512 of a package already present in the package folder is not verified.
5. Import as a file rule re-imports on every fresh clone; `update-locks` is the pattern.
   (The consumer commits descriptions and locks and runs `import` only on demand.)
6. No document-level `Lock.diff`; no one-call build of a whole imported lock; drift messages
   print absolute paths; `Lock.verify` on a missing lock says only "Neither rule nor file".
7. SBOM default internal prefixes are a customer's; the default should be empty.
8. The six SBOM questions in `docs/hermetic/nuget-sbom.md`; `sign {}` shadows `sign`;
   `StrongName.signFile` does not recompute the PE checksum; no real signer ships.
9. Rework `hermetic-guide.md` section 6, building from an imported lock: the hand-written
   project-reference remap (`unbuilt`/`mapped` over empty hashes) is the pattern `Locks.compileAs`
   replaced for Xake-generated locks; give imported locks the same shape, or document
   `Locks.compileAs` as the way and retire the example.

### Build log defects (backlog, 2026-10-26)

The first reading was that `filelog "build.log" ...` appends to the file across runs. That was wrong: `FileLogger`
(`Logging.fs`) recreates the file for every `xake {}` run. The real defects, found while checking it:

- the tail is lost on failure: nothing flushes the log before the process exits;
- two runs in one folder clobber or interleave each other's file;
- a relative name is resolved against the process's current directory, not the project root;
- the message "See build.log" is hard-coded, whatever name `filelog` was given.

Owner's decision: not now. Trace the growth the consumer solution saw in its own script or CI first.

### Satellite assemblies in imported locks (deferred, S71)

`Import.record` records no satellite (`<culture>/<asm>.resources.dll`) compilations, so a build from locks has none and a
nuspec that ships them leaves them out (the consumer solution: 37 files per brand, `ja` and `zh-CN`). Still deferred by
decision S71 (`docs/hermetic/README.md`); doing it reopens S71. Correction to the first idea: msbuild does not use `al`;
`dotnet msbuild` runs the `Csc` task in `CoreGenerateSatelliteAssemblies`.

Recommended shape: a nested optional `Satellites` field in the entry (older readers ignore it, no format bump), captured
from msbuild's own `Csc` call per culture rather than composed by Xake, replayed into `out/.../<culture>/`; the run folder,
the `deps.json` `resources` lines and the version (a satellite's version must equal the main assembly's) come with it.
Risk: under invariant globalization the culture-specific compile cannot work, so the capture must fail there, not skip.
The owner's recorded preferences: capture the call, fail under invariant globalization, C# first.

### Backlog from the Windows findings

- `Import.RecordOptions.Environment` and `Project.ImportOptions.Environment`: explicit secrets for the import's msbuild
  runs (today it passes only `NUGET_PACKAGES`).
- The floor check through the SDK probe and `MSBuild.deps.json` instead of `dotnet msbuild -version` (needs `sdkDir`
  public in the core).
- A confirmation run of the 17.10 floor on an SDK 8.0.2xx.

