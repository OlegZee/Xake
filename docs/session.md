# Session notes

## State of `dev`: the 4.0 line (2026-10-31)

`dev` carries the variable rework (#111, #112) and is the line of **Xake 4.0**, a breaking release
(owner; not 3.16). Release notes: [release-notes.md](release-notes.md), "4.0.0 (unreleased)". The
feature folder: [features/vars-precedence](features/vars-precedence/session.md).

- **Variables are evaluated once at launch**, in `ScriptRunner.createScriptContext`
  (`src/core/VarEvaluation.fs`; the types and the pure parts are in `src/core/Vars.fs`): Run,
  `--dryrun`, `--dump` and engine `start`; `--help` evaluates nothing. A recipe only reads.
- **The chain:** engine per-demand `?vars` > `-d` (the last wins) > the script's one definition
  (`var "N" "v"` or `ExecOptions.Vars`, script constants only) > the declaration's sources
  (`var "N" [Cli ..; Env ..; Default ..]` or a `varschema` field), first defined in list order.
  A name defined twice in the script is a launch error.
- **Keys vs `-d` names:** the key is the internal name (a field's `name`, else the field name; the
  `var` rule's name); `cliArg` / `Cli` is only the `-d` name. `-d` of a non-key name, of a
  secret or of a `Var.env` field stops the run.
- **A `Var` read** (`let! x = vars.f`) returns the stored value of an equal declaration; no
  declaration or a different one is an error (E-7/E-8). It records `Var (N, value)`, never its
  source, so moving an equal value between environment, `-d` and `var` does not rebuild.
  `getVar` reads the set, `getEnv` the environment (recorded as `EnvVar`).
- **Secrets:** `Secret` records only whether the value is set, `SecretHashed` a salted hash
  (`SecretDep (key, SecretArg)`); logs, the banner and the debug dump show `***`; `getVar` of a
  secret returns the real value. The banner lists each variable once with its source.
- **Libraries read only the variable set**, never the environment: Xake.Dotnet
  (`NUGET_PACKAGES`, default `<project root>/.packages`; `NUGET_FETCH`, `CSC_SERVER`,
  `XAKE_CSC_SERVER`, `CSC_JOBS`, `FSC_JOBS`; strict switch values) and Xake.Hermetic.Dotnet
  (`CI`, `LOCK_REFRESH`, `LOCK_STALE`, `SOURCE_DATE_EPOCH`). They read through `Var.read`; the
  optional import is `varschema DotNetVars.Default` / `HermeticVars.Default`. A CI that used the
  environment adds `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]` and the like.
- **Release state:** published are Xake 3.15.0 and Xake.Hermetic.Dotnet 0.10.0 (range
  `[3.14.0.45, 4.0)`). Until 4.0 the hermetic package builds on the in-repo Xake (project
  references; its nupkg depends on `Xake` at the build's version), `bump-base` and the CI job
  `hermetic-compat` are gone, `publish.yml` refuses `hermetic-v*`. At 4.0 hermetic 0.11 ships
  with Xake, and `Xake.Dotnet` and the hermetic package are split out together (release-process
  decision D). Records frozen until 4.0 (`RecordShapeTests`) may change in 4.0.
- Traps: a local `.xake` from before the rework makes the hermetic `test-build` misbehave (the
  dependency records of Var reads and secrets changed): use `--resetdb`. The SDK pin is
  10.0.402 in the locks and `build.yml`; another SDK needs `update-locks`. .NET removes an
  environment variable set to `""` in-process, so a test cannot make an "empty value" through
  the environment on Unix (pass it with `-d N=`). The importer reads `PrivateAssets=all` as
  "bundled" (`package.bundle: ["Xake"]` in `src/hermetic/xake.project.json`).

## Release prep (branch `feature/rulesless-syntax`)

Preparing the first release that ships `Xake.Dotnet` inside the `Xake` package. What was
touched, so it is not re-litigated:

- [`docs/dotnet-build.md`](dotnet-build.md) is the reference for toolchain discovery: the three
  providers (`sdkImpl`, `msImpl`, `monoFwkImpl`), the fallback chain, framework-name
  normalization, and the `NETFX`/`NETFX-TARGET`/`FSCVER` variables. Read it before touching
  `DotNetFwk.fs`.
- The nupkg had **no license metadata at all** and a deprecated `PackageIconUrl`. It now
  declares `MIT`, packs `Icon.png` and `readme.md`, and packs with zero warnings. The readme is
  referenced as `readme.md` — that is the git-tracked casing, and `README.md` would break the
  pack on a case-sensitive CI filesystem.
- The published version is the tag from 3.15 on (`v3.15.0` -> `3.15.0`); up to 3.14.0 /
  hermetic 0.9.0 `publish.yml` appended the run number (`v3.14.0` -> `3.14.0.45`). Decision:
  docs/features/release-process/review.md, option A.
- `FSharp.Core` in the nupkg is whatever the SDK pins — currently `10.1.400`. Bumping the SDK
  raises the floor for every consumer. Pin it explicitly in the projects if that matters.
- The mono provider built its `PATH` env var as `sdkroot </> ("bin" + ";" + PATH)` — `+` binds
  tighter than `</>`, and `;` is not the Unix separator. Fixed, but the mono path has no test
  coverage.
- `build.fsx` and `build.fsc.fsx` bootstrap from nuget.org (versions in their first lines;
  `build.fsc.fsx` also takes `Xake.Hermetic.Dotnet`).
  The base script stays on the base package only. The nuget references stay behind the release being made; bump
  them only after a release lands on nuget.org, to the exact published version (the tag from
  3.15 on, `X.Y.Z.<run>` before).
  Developing against unreleased libraries: a temporary `#r` on dlls (or a `#i` local feed);
  see docs/devprocess.md, "Bootstrapping note". `samples/gettingstarted.fsx` references the
  published package (`#r "nuget: Xake"`) and is checked against it after each release.
- `src/hermetic` builds on the in-repo `src/core`/`src/dotnet` (ProjectReferences) until Xake
  4.0 (owner, 2026-10-31; before: the published `Xake` with a range, #108..#109). Its nupkg
  depends on package `Xake` at the build's Xake version (`-p:XakePackageVersion`, build.fsx);
  `bump-base` and CI `hermetic-compat` are gone; the next hermetic release ships with Xake 4.0.
- **The supported floor is .NET 8**, enforced in three places (`global.json`, the tests TFM, the
  CI matrix) and by an explicit `FSharp.Core` 8.0.100 pin with
  `DisableImplicitFSharpCoreReference`. Without the pin the SDK's own FSharp.Core lands in the
  nuspec and every consumer inherits it. See docs/devprocess.md.
- `builder {}` (the empty settings block) does **not** compile on F# 8 — `builder { () }` does,
  and is what the docs, samples and tests use.
- `csc` with no `targetfwk` (and no `NETFX-TARGET`): in 3.4 an error (`Csc.ofSettings` failed
  asking for one; the older fallback passed no framework references at all and only worked on
  Windows via `csc.rsp`). Since 3.5 both `csc` and `fsc` default to the .NET framework of the SDK
  the build runs on (`DotNetFwk.sdkFramework`, from the probed SDK's
  `Microsoft.NETCoreSdk.BundledVersions.props`: `net10.0` for SDK 10.0.x), compiled against the
  SDK's own targeting pack `<dotnet>/packs/Microsoft.NETCore.App.Ref/<ver>/ref/netN.0` (all of
  it, no download); `fsc` also references the SDK's `FSharp/FSharp.Core.dll`. Precedence:
  `targetfwk` > `NETFX-TARGET` > the SDK's framework. The "needs a target framework" error is
  left only for when the SDK framework cannot be determined.
- `samples/features.fsx` had a catch-all `"(dir:*)/(file:*).(ext:c*)"` rule declared *after*
  `temp/AssemblyInfo.cs`; since the last matching rule wins, it shadowed it and the sample had
  been failing for a long time. The catch-all now comes first.

- Trap: tag `v3.12.0` was used by #89; check the tag list before writing a release-notes heading.

## Earlier work: modernizing `src/dotnet`

`src/dotnet` (Xake.Dotnet) was merged in from a separate repository and had not caught up with
the engine. It has now been reworked to reuse the engine's APIs and match its style. What is
worth knowing before touching it again:

- **`src/dotnet` no longer contains engine code.** Its `ProcessExec.fs` and `CommonLib.fs`
  copies are gone; it uses the engine's via `InternalsVisibleTo("Xake.Dotnet")` declared in
  `src/core/Xake.fsproj`. If assembly signing is ever added, that attribute needs the public
  key or Xake.Dotnet silently loses access to the internals.
- **`pexecSync` exists for `DotNetFwk` only.** Framework and tool probing happens in plain
  memoized functions with no async context; everything inside a recipe uses the `shell`/`sh`
  task. `Async.RunSynchronously` is safe there (recipes run via `Async.StartAsTask` with no
  `SynchronizationContext`) but it holds a CPU slot, which is why it is confined to the
  memoized lookups.
- **`#if NET46` was dead code** -- the projects target `net462`, for which the SDK defines
  `NETFRAMEWORK`/`NET462`/`NET46_OR_GREATER` but *not* `NET46`. Use `NETFRAMEWORK`. Verify any
  new conditional empirically; a wrong one fails silently by compiling nothing.
- **resx compilation needs `System.Windows.Forms`** (`ResXResourceReader`), which is not one of
  the SDK's implicit net462 references. It is referenced explicitly for that TFM only.
- **Compiler diagnostics used to be invisible.** Roslyn writes them to stdout, which the old
  `_system` logged at `Verbose`, and `levelFromString` only recognized diagnostics carrying a
  source position -- so `error FS0084: ...` was missed too. Both are fixed; keep
  `Impl.levelFromString` covering both diagnostic shapes.

### Known limitations, not defects of this work

- **fsc for .NET Framework from any host (3.5).** It used to fail on macOS with FS0074 ("add
  a reference to assembly 'netstandard'"): every FSharp.Core there is (the SDK's, the
  package's netstandard builds) references `netstandard 2.0.0.0`, and the net4x reference
  assemblies below 4.7.1 have no `netstandard.dll`. Nothing net4x-specific was missing, only
  that facade and an FSharp.Core reference; both are implicit now (`Fsc.ofSettings`):
  FSharp.Core is the NuGet package at `DotNetFwk.fsharpCoreVersion` 8.0.100 /
  `FSHARP_CORE_VERSION` (`lib/netstandard2.0`) for netstandard and .NET Framework, the SDK's
  own for netN.0; the facade is the reference assemblies' `Facades/netstandard.dll` (4.7.1+)
  or the SDK's `Microsoft/Microsoft.NET.Build.Extensions/net461/lib/netstandard.dll`. **Trap:**
  not `NETStandard.Library`'s `build/netstandard2.0/ref/netstandard.dll` -- it *defines* the
  types, so against `mscorlib` fsc warns FS3242 on every attribute and fails FS0193 on `lazy`
  (a hello world still compiles, which hides it). csc is untouched (no implicit facade).
  Runtime: below 4.7.2 the exe needs a `netstandard.dll` next to it; Xake does not copy it.
- **fsc through the shared runner (3.5, B6).** `Fsc` is the twin of `Csc` (fsc's own
  `--name:value` dialect in `FscArgs`, over `ArgsCore`, shared with `CscArgs`); `Csc.run` and
  `Fsc.run` both fill an internal `CompilerRunner.RunPlan` and call `CompilerRunner.run`, so the
  gate (generated files, `needFiles`, hash check, rsp) is one piece of code. `Fsc` and
  `FscRunOptions` are `[<RequireQualifiedAccess>]`: their labels equal `Csc`'s/`RunOptions`',
  and an unqualified record expression would otherwise infer the F# type. fsc reads the
  response file `Impl.escapeArgument` writes (a source path with a space is tested). The
  composed line has no `--nocopyfsharpcore`, so fsc copies the referenced `FSharp.Core.dll`
  next to the output unless `args ["--nocopyfsharpcore"]`.
- **A target requested twice in one run used to be built twice** — `need ["x"]` followed by
  `needFiles` on the same `x` rebuilt it. Fixed in `WorkerPool.fs`; what actually went wrong
  took two mechanisms, so it is worth writing down:
  - the pool deduped only *in-flight* requests, dropping the entry when the task finished, and
  - the "does it need rebuilding" verdict is memoized for the whole run
    (`getChangeReasons ctx |> memoizeRec`, `ExecCore.fs`) — the memo is how the recursive graph
    analysis ties its knot, not an optimization that can be dropped — so the second request did
    not ask the database again and got the pre-build "Not built yet" answer.

  The pool now keeps two maps: `running`, which is shared by whoever asks whenever they ask,
  and `finished`, which serves the rest of the run and is emptied when the next one starts.
  `Scheduler.newRun` marks that boundary and is posted exactly where the memo is created. So:
  within a run a target executes once; across runs the database decides again; and a task that
  was already going when a run began still serves it, which is the pre-existing behaviour for
  concurrent demands. Covered by
  `builds a target requested twice in one run only once` and `builds a file needed and then
  needFiled only once`.
- `build.fsx` builds `netstandard2.0` only; the `net462` asset comes from `dotnet pack`. The
  FSharp.Core reason given before (no net4x assembly in the pinned package) does not hold: a
  net462 compile takes FSharp.Core's netstandard2.0 build plus the facade, which is what
  `dotnet build` does too. What remains for a net462 leg of `build.fsc.fsx` is work, not a
  blocker: `projects/net462/*.json` evaluations to read the references from (checking that
  they carry the facade msbuild adds), and comparing its output with `dotnet pack`'s asset.

## Trap: rule patterns with `..` never matched

A file rule such as `"../sibling/(x:*).dll"` failed with "neither rule nor file is found":
`File.make` normalizes `..` out of a target path, but `Path.matchGroups` combined the project
root and the pattern with `Path.Combine` and kept the literal `..`, so the two could never
agree. The literal prefix before the first wildcard or group is now normalized
(`impl.normalizeLiteralPrefix`, `src/core/Path.fs`). Covered by `ParentDirTargetTests.fs`.

## Trap: a transient sharing violation on the `.xake` database looked like corruption

Closing a database and reopening it moments later (a second `xake {}` run in the same folder,
which the tests do constantly) occasionally threw `IOException: being used by another process`
even after `Dispose()` had returned. `Storage.impl.openDatabaseFile` treated every exception as
a corrupt file, deleted it and started over -- so the next build saw "Not built yet" and re-ran
a rule it should have skipped (the flaky `executes need only once`). The three
file opens now retry an `IOException` up to five times with a 20 ms backoff; real corruption still
goes to the recreate path. The `EndOfStreamException` warning in test runs is deliberate: the
`handles broken database` and storage tests write garbage on purpose.

## Trap: fsc does not unquote its response file, and `shell` quotes nothing

Compiles failed with the SDK under `C:\Program Files\dotnet`. Two layers: `shell` joins
`args` with a space (the compiler dll and `@rsp` split at the space), and fsc reads each rsp
line verbatim -- `"--reference:a b/l.dll"` is a file name to it (FS2020), `--reference:"a b/l.dll"`
keeps the quotes (FS0084); only the bare line works. csc is the opposite (a bare line with a
space is split, CS2001). `CompilerRunner.RunPlan.ResponseQuoting` (`Argv` for csc, `Verbatim`
for fsc); every argument handed to `shell` by Xake.Dotnet and the hermetic package is quoted
with `Tool.quoteArgument` (Windows argv rules, which .NET applies on every OS too, so a
symlinked SDK directory with a space reproduces it on macOS/Linux: `PathSpacesTests.fs`).
Scripts' own `shell { args }` stay raw.

## How to verify changes here

```bash
dotnet build src/core -c Release && dotnet build src/dotnet -c Release   # both TFMs, 0 warnings
dotnet restore src/hermetic.tests                                        # builds on the in-repo Xake (until 4.0), no NU1603
dotnet build src/hermetic -c Release --no-restore                        # Xake.Hermetic.Dotnet, both TFMs, 0 warnings
dotnet test src/tests -c Release                                         # all green (547 tests at the vars rework)
dotnet test src/hermetic.tests -c Release --no-restore
dotnet test src/tests --filter 'Category=Integration'                    # real csc and fsc runs
dotnet test src/hermetic.tests --filter 'Category=Integration'
dotnet fsi build.fsx -- -- build test pack                               # self-hosting: 3 libraries, both test projects, both packages
dotnet fsi samples/fullframework.fsx                                     # end-to-end csc
```

Tests move between the two projects, so the split shifts. `pack` writes `out/pkg/Xake/` and `out/pkg/Xake.Hermetic.Dotnet/` (independent:
the hermetic pack builds on the in-repo `Xake` and depends on package `Xake` at the build's version; `pack-hermetic` packs only that one) and fails
if the hermetic nuspec has no `Xake` dependency or names `Xake.Dotnet` (docs/devprocess.md).

`samples/*.fsx` reference `out/netstandard2.0/*.dll`, so run the self-hosting build first.

## The fsc build (`build.fsc.fsx`)

The declarative self-build (fsc-rework PR 7): the three libraries compiled by fsc from committed
data, no msbuild for them. `build.fsx` stays the build of record (packages, releases).

- Data: `src/<dir>/xake.project.json` (descriptions, written by `import`, msbuild evaluating the
  `.fsproj`), `locks/<name>/Release.json` (written by `update-locks` = the phony
  `lock::<name>::<cfg>` rules over `Locks.generate`), `xake.fingerprints` (one line per
  description and lock: its hash, its inputs' hash, the inputs). All committed.
- `build` = `Fingerprints.warn` + `Locks.needAll`, the rule `out/(cfg:*)/(fwk:*)/(name:*).dll` =
  `Locks.compileAs`; `check` = `Fingerprints.check`. `test` is `dotnet test` as in `build.fsx`.
  Release only; `pack` waits for `Package.nupkg` (PR 8).
- Trap: any change to `src/hermetic/Xake.Hermetic.Dotnet.fsproj` (a version bump included) makes
  `src/hermetic/xake.project.json` stale for the self-build; run `dotnet fsi build.fsc.fsx -- -- import`
  (and `update-locks -d LOCK_REFRESH=Xake` when the core range moved) in the same PR, or CI's
  `build-fsc` fails on the fingerprint check (#72, #79). The locks name the SDK compiler by
  version: a removed SDK patch needs `update-locks` and the matching pin in `build.yml`.
- Trap: adding a source file to a project needs `import` AND `update-locks` (the lock lists the
  sources); CI `build-fsc` failed on #95 with FS0039 when only `import` ran.
- Bootstrap: `#r "nuget: Xake, 3.15.0"` and `#r "nuget: Xake.Hermetic.Dotnet, 0.10.0"` (the first lines), no
  `build.fsx` run first. Its `clean` removes `out/Release` only (`out/netstandard2.0` is
  `build.fsx`'s).
- Own database `.xake.fsc` (`xake { ExecOptions.Default with DbFileName = ... }`): the scripts no
  longer share a bootstrap, and their phony targets (`build`, `test`, `clean`) share names.
- The locks name the SDK's `fsc.dll` by path with an empty `Sha256`: the SDK version is its
  identity (prerequisite `dotnet-sdk` 10.0.402, pinned in `build.yml`). The committed locks hold for SDK
  10.0.402 on any OS/architecture; the CI job `build-fsc` runs on ubuntu-latest. Another SDK
  needs `update-locks`.
- `HERMETIC=on` is not set: it needs an exact SDK pin in `global.json` (`rollForward: disable`),
  which the repository does not have (CI matrix 8.0/10.0). Open question.
- `Fingerprints.warn` logs at `Message` (since #51), visible at the default console level; the
  script sets no `consolelog`.
- After a project-file change: `import`, `update-locks`, commit descriptions, locks and
  `xake.fingerprints`. Without it `build` warns once per stale line and still builds; `check`
  fails.

## Earlier work: delegated execution (merged, PR #15)

The delegated/distributed execution model (`Resource`, `runDetached`, `delegated`) is designed
in [`docs/delegated.md`](delegated.md). Two facts from that work still bite:

- Delegated rules are never bounded by the local CPU pool -- the engine runs executor and body
  detached, so `runDetached` inside a delegated body is redundant.
- `Scheduler.withYieldedSlot` assumes the caller holds exactly one CPU slot. Calling it without
  one releases a phantom permit and silently disables the `Threads` limit. Inside a
  `DelegatedExecutor` (already detached) use `Resource.acquire`/`withAcquired` instead.
