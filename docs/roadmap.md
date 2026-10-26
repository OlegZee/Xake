# Roadmap

Ideas and larger items that are agreed in direction but not scheduled. Working state of a
feature lives in `docs/features/<name>/`; this page is the cross-feature list.

## Delivered

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

### Line-ending-neutral hashes of msbuild files (idea, 2026-10-26)

An imported lock records the SHA-256 of every msbuild file its evaluation read (`Evaluation.Imports`: the project file,
`Directory.Build.props`/`.targets`, `.projitems`, the script's own probe targets), and `Locks.staleOf` (`check`, and every
replay) compares them byte for byte. Git's `core.autocrlf` checks the same commit out with LF on macOS/Linux and CRLF on
Windows, so a lock set recorded on one OS is stale on the other: every import named at once ("... changed since the
import that wrote it"), although nothing changed (the consumer solution: locks recorded on macOS, built on Windows). The lock file itself is
already compared with `\r\n` normalized (`Locks.fs`, `existing`); the files it names are not.

Proposal: hash the text inputs of an evaluation with line endings normalized (CRLF -> LF before hashing; a lone CR kept)
and record that the hash is normalized, e.g. a lock format version bump or `"Eol": "lf"` beside `Sha256`, so an old lock is
still checked byte for byte instead of failing. Only `Evaluation.Imports` and other text the lock hashes as msbuild input
change; binaries (references, analyzers, the compiler, package files) stay byte hashes. Open points: which inputs count as
text (by role, not by extension); `FileHash.sha256`'s memo key must include the mode; the docs (`docs/hermetic/lock.md`)
say a lock set is portable across OSes and `core.autocrlf` settings.

### Secret script variables (idea, 2026-10-26)

`Var.env`/`Var.string` resolve through `getEnv`/`getVar`, which record the value as a dependency: `EnvVar (name, value)`
in `.xake`, and a change is reported as "Environment variable X was changed from '<old>' to '<new>'"
(`DependencyAnalysis.fs`). A token or license key declared as a variable (the consumer solution: a feed token and an obfuscator license key)
thus lands in clear text in the database on every machine and, when it rotates, in the build log.

Proposal: a secret variable, e.g. `Var.secret<'t>(envVar = ...)` or `|> secret` on any var. It resolves like any other
(`required`, `withDefault`, `--help` with the name and description, never the value) but records a salted hash of the
value (so a change still rebuilds what depends on it) or, opt-in, only its presence (a rotated token rebuilds nothing),
and every message that would quote it prints `***`. Related: the tools a recipe runs get the secret explicitly, not
through the inherited process environment, so `Import.RecordOptions` needs an `Environment: (string * string) list` for
the msbuild runs of an import (today it passes only `NUGET_PACKAGES`), as `shell { envs ... }` already does for a tool.
Open points: CLI `-d NAME=value` for a secret (it shows in the process list; env-only by default); the hash salt per
database; trace levels that dump the context must mask it too.

### Targeting packs from NuGet by default in `Import.record` (idea, 2026-10-26)

An imported entry names a targeting pack file as `pack:<id>/<version>/...` and hashes whichever copy msbuild used: the
installed one under `<dotnet>/packs` or the nuget.org package in the package folder. The two are not byte-identical
(the package's assemblies are signed), so the same project recorded on two machines with the same SDK gets different
hashes when one has the pack installed and the other downloads it (the consumer solution: macOS vs Windows, 336 hashes per entry).
The consumer solution works around it in its msbuild probe (`NetCoreTargetingPackRoot` set to a folder that does not exist, so every pack
comes from NuGet). Proposal: `Import.RecordOptions.PacksFrom = NuGet | Installed` with NuGet as the default (the import
passes the property itself); together with a pinned pack version (`KnownFrameworkReference` /
`KnownAppHostPack`, today also a probe target in the consumer script) the recorded references do not depend on the machine.

### The MSBuild an import needs, checked up front (idea, 2026-10-26)

`Import.record` evaluates every project with `-getResultOutputFile` (MSBuild 17.9+, .NET SDK 8.0.200+). On SDK 8.0.1xx each
evaluation fails with `MSB1001: Unknown switch` and the import ends with "Exiting due to FailOnError set on ...import.sln"
(seen on Windows with SDK 8.0.121). Proposal: read the MSBuild version once (`dotnet msbuild -version`) before the restore
and fail with "the import needs MSBuild 17.9 or later (.NET SDK 8.0.200+); found <v> (SDK <sdk>)"; say the minimum in
`docs/hermetic/`.

### msbuild errors of an import in the log (idea, 2026-10-26)

When the import's restore or an evaluation fails, the build log says only "('<temp>/import.sln') failed with exit code '1'
... Exiting due to FailOnError": msbuild's own lines go out at Verbose, which `filelog ... Chatty` does not keep, so NU1008
(central package management), NU1605 (a downgrade as error) or NU1301 (feed credentials) had to be found by running msbuild by
hand. Proposal: lines of `Project.runMsbuild` that are msbuild diagnostics (`: error `, `: warning NU`) at Error/Warning
level, and the failure message naming the first few errors and the project they belong to.

### A build log per run (idea, 2026-10-26)

`filelog "build.log" ...` appends every message to the file (`File.AppendAllLines`), so the log grows across runs and a CI
artifact holds every earlier build of that checkout (the consumer's CI steps delete it first). Proposal: start the file
anew per run by default, with `filelog ... append` for the old behaviour (or keep the last N runs as `build.1.log`, ...).

### Satellite assemblies in imported locks (gap, 2026-10-26)

`Import.record` records no satellite (`<culture>/<asm>.resources.dll`) compilations, so a build from locks has none and a
nuspec that ships them leaves them out (the consumer solution: 37 files per brand, `ja` and `zh-CN`; one brand ships without
its `zh-CN` resources). Proposal: record msbuild's satellite generation (the culture `.resx` -> `.resources` ->
`al`/csc `/out:<culture>/<asm>.resources.dll` step, signed like the main assembly) as entries of their own, with the
replay writing `out/.../<culture>/`; `RunFolder` and pack then find them like any other built file.