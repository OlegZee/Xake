# Session notes

## State of `dev`: the 4.0 line (2026-10-31, pattern module 2026-11-03)

`dev` carries the variable rework (#111, #112) and is the line of **Xake 4.0**, a breaking release
(owner; not 3.16). Release notes: [release-notes.md](release-notes.md), "4.0.0 (2026-11-03)", published as Xake 4.0.0. The
feature folder: [features/vars-precedence](features/vars-precedence/session.md). The 4.0 pattern
module and typed targets landed with #131 (2026-11-03,
[features/pattern-module](features/pattern-module/session.md)); the release continues with the
split, S6-S9 ([features/dotnet-split](features/dotnet-split/session.md)). From S7 (branch
`feature/dotnet-split-remove`) this repository is the engine alone: `src/dotnet`, `src/hermetic`,
their tests, docs, samples and the fsc/locks self-build (`build.fsc.fsx`, `locks/`,
`xake.fingerprints`) live in OlegZee/Xake.Dotnet.

**Where things stand (end of 2026-11-03).** Xake 4.0.0 is out; the rest of the 4.0 release
(Xake.Dotnet 4.0.0, S9) and the hermetic Windows work continue in OlegZee/Xake.Dotnet, in a
session started there. Landed in Xake after the split: readme on `sh "dotnet build"` and
`command`/`target` (#134, #136), `build.fsx` for one library (#134), the 4.0 release notes rewrite
(#134), fileset and string entries in `rm`/`cp` (#135), `hermetic-windows/` moved out (#138). The
wiki (`~/projects/Xake.wiki`, pushed by the owner) is on 4.0: patterns, variables, typed targets,
`command`/`target` examples, versioned `#r` lines. Open small items here: `--help` lists
variables but not targets; `for`/`yield!` inside `rm`/`cp` are not supported
([features/rm-fileset-syntax](features/rm-fileset-syntax/research.md)).

- **Variables are evaluated once at launch**, in `ScriptRunner.createScriptContext`
  (`src/core/VarEvaluation.fs`; the types and the pure parts are in `src/core/Vars.fs`): Run,
  `--dryrun`, `--dump` and engine `start`; `--help` evaluates nothing. A recipe only reads.
- **The chain:** engine per-demand `?vars` > `-d` (the last wins) > the script's one definition
  (`var "N" "v"` or `ExecOptions.ScriptVars`, script constants only) > the declaration's sources
  (`var "N" [Cli ..; Env ..; Default ..]` or a `varschema` field), first defined in list order.
  A name defined twice in the script is a launch error.
- **Environment prefix (2026-10-31):** every declaration without an explicit `Env` / `envVar`
  reads `XAKE__<NAME>` first (`VarEvaluation.implicitEnvName`: the key in UPPER_SNAKE, characters
  other than `A-Z0-9_` as `_`); a constant `var "N" "v"` / `ExecOptions.ScriptVars` entry is
  overridden by `XAKE__N` (origin `env:XAKE__N`, still no declaration for a Var read); `Var.arg`
  has no env source; undeclared names get none (no `XAKE__*` scan); two keys of one implicit name
  are a launch error (E-12). The 3.15 derived name without the prefix is gone. `build.fsx` names
  `VERSION`, `NUGET_KEY` with explicit `envVar` (publish.yml exports them, and the bootstrap is
  still 3.15.0).
- **Keys vs `-d` names:** the key is the internal name (a field's `name`, else the field name; the
  `var` rule's name); `cliArg` / `Cli` is only the `-d` name. `-d` of a key whose command-line name differs, of a
  secret or of a `Var.env` field stops the run.
- **A `Var` read** (`let! x = vars.f`) returns the stored value of an equal declaration; no
  declaration or a different one is an error (E-7/E-8). It records `Var (N, value)`, never its
  source, so moving an equal value between environment, `-d` and `var` does not rebuild.
  `getVar` reads the set, `getEnv` the environment (recorded as `EnvVar`).
- **Secrets:** `Secret` records only whether the value is set, `SecretHashed` a salted hash
  (`SecretDep (key, SecretArg)`); logs, the banner and the debug dump show `***`; `getVar` of a
  secret returns the real value. The banner lists each variable once with its source.
- **Libraries read only the variable set**, never the environment, through `Var.read` (the
  engine's contract; `src/tests/LibraryVarsTests.fs` covers it). Xake.Dotnet's variables
  (`NUGET_PACKAGES`, `CSC_SERVER`, `CI`, `LOCK_REFRESH`, ... and the `DotNetVars`/`HermeticVars`
  schemas) are documented in [variables.md](variables.md#library-variables) and tested in
  OlegZee/Xake.Dotnet.
- **`ExecOptions` split:** `Vars` holds script constants only, `CliVars` the `-d` pairs,
  `VarDecls` the declarations, `VarDefinedBy` where each definition came from (for E-2);
  `EngineOptions.Variables` is the evaluated set (`Map<string, VarEntry>`). User reference:
  [variables.md](variables.md).
- **Masking:** the launch collects the secret values (declared names, their env names) and every
  logger Xake builds replaces those of 4+ characters with `***`; nothing outside the logger is
  masked. The database keeps presence or a salted HMAC (`SecretDep`).
- **Masking cost (measured 2026-10-31):** about 22 ns per secret per log line; 5 secrets is about
  1.1 ms per 10,000 lines, negligible. See [features/vars-precedence/perf-secrets.md](features/vars-precedence/perf-secrets.md).
- **Perf note:** `recordDependency` (`ScriptFuncs.fs`) dedups `Var`/`EnvVar`/`SecretDep` reads with
  `List.contains` over the target's dependencies: O(deps) per read.
- **Release state:** published are Xake **4.0.0** (2026-11-03, tag `v4.0.0` at 4297ea3 on `master`), 3.15.0 and `4.0.0-preview.1..3` (preview.2 on: the
  `Xake` package is the engine alone, #129), and Xake.Hermetic.Dotnet 0.10.0 (range
  `[3.14.0.45, 4.0)`; its last release, S-D2). `publish.yml` takes `v*` tags only. From
  OlegZee/Xake.Dotnet: `Xake.Dotnet 4.0.0-preview.1` (both assemblies, on `Xake [4.0.0, 5.0)`;
  tag on its `dev` at c2c3f06, 2026-11-03) is published and checked (its `build.fsc.fsx` and
  scripts outside the repository); `Xake.Dotnet 4.0.0` is the next tag there (S8).
  `ExecContext` is frozen within 4.x (`RecordShapeTests`); the compiler records' test moved with
  their code.
- Traps: a local `.xake` from before the rework can make a run misbehave (the dependency records
  of Var reads and secrets changed): use `--resetdb`. .NET removes an environment variable set
  to `""` in-process, so a test cannot make an "empty value" through the environment on Unix
  (pass it with `-d N=`).

## Release and packaging facts (from the 3.x release prep)

- The nupkg declares `MIT`, packs `Icon.png` and `readme.md`, and packs with zero warnings. The
  readme is referenced as `readme.md` (the git-tracked casing; `README.md` would break the pack
  on a case-sensitive CI filesystem).
- The published version is the tag from 3.15 on (`v3.15.0` -> `3.15.0`); up to 3.14.0 /
  hermetic 0.9.0 `publish.yml` appended the run number (`v3.14.0` -> `3.14.0.45`). Decision:
  docs/features/release-process/review.md, option A.
- `build.fsx` bootstraps from nuget.org (the version in its first line), behind the release
  being made; bump it only after a release lands, to the exact published version. Developing
  against unreleased libraries: a temporary `#r` on dlls (or a `#i` local feed); see
  docs/devprocess.md, "Bootstrapping note". `samples/gettingstarted.fsx` references the
  published package (`#r "nuget: Xake"`) and is checked against it after each release.
- **The supported floor is .NET 8**: `global.json`, the tests TFM, and an explicit `FSharp.Core`
  8.0.100 pin with `DisableImplicitFSharpCoreReference`. Without the pin the SDK's own
  FSharp.Core lands in the nuspec and every consumer inherits it. See docs/devprocess.md.
- `builder {}` (the empty settings block) does **not** compile on F# 8 — `builder { () }` does,
  and is what the docs, samples and tests use.
- `samples/features.fsx` had a catch-all `"(dir:*)/(file:*).(ext:c*)"` rule declared *after*
  `temp/AssemblyInfo.cs`; since the last matching rule wins, it shadowed it and the sample had
  been failing for a long time. The catch-all now comes first.
- `build.fsx` builds `netstandard2.0` only; the `net462` asset comes from `dotnet pack`.
- Trap: tag `v3.12.0` was used by #89; check the tag list before writing a release-notes heading.

## Trap: a target requested twice in one run used to be built twice

`need ["x"]` followed by `needFiles` on the same `x` rebuilt it. Fixed in `WorkerPool.fs`; two
mechanisms were involved:

- the pool deduped only *in-flight* requests, dropping the entry when the task finished, and
- the "does it need rebuilding" verdict is memoized for the whole run
  (`getChangeReasons ctx |> memoizeRec`, `ExecCore.fs`) — the memo is how the recursive graph
  analysis ties its knot, not an optimization that can be dropped — so the second request did
  not ask the database again and got the pre-build "Not built yet" answer.

The pool now keeps two maps: `running`, which is shared by whoever asks whenever they ask, and
`finished`, which serves the rest of the run and is emptied when the next one starts.
`Scheduler.newRun` marks that boundary and is posted exactly where the memo is created. So:
within a run a target executes once; across runs the database decides again; and a task that
was already going when a run began still serves it. Covered by `builds a target requested twice
in one run only once` and `builds a file needed and then needFiled only once`.

## Xake.Dotnet facts (moved at 4.0)

The .NET tasks and the hermetic library are in OlegZee/Xake.Dotnet; their docs moved with them
(`docs/dotnet-build.md` for toolchain discovery, `docs/csc-syntax.md`, `docs/csc-server.md`,
`docs/hermetic*`). The facts below were learnt here and are kept until that repository's
`docs/session.md` takes them (split S9):

- `src/dotnet` uses public engine API only (S1): `ProcessExec` (public,
  `RequireQualifiedAccess`; `pexecSync` only in `DotNetFwk`'s memoized probes, never inside a
  recipe) and `hasRule`. Use `NETFRAMEWORK`, not `NET46` (never defined for `net462`); resx
  needs `System.Windows.Forms` on net462; keep `Impl.levelFromString` covering both compiler
  diagnostic shapes. The mono provider has no test coverage.
- `csc`/`fsc` with no `targetfwk` (and no `NETFX-TARGET`) target the SDK's own framework
  (`DotNetFwk.sdkFramework`). fsc for .NET Framework needs an implicit FSharp.Core and the
  `netstandard.dll` facade; **trap:** not `NETStandard.Library`'s `ref/netstandard.dll`, which
  defines the types (FS3242/FS0193 against `mscorlib`).
- **Trap: fsc does not unquote its response file, and `shell` quotes nothing.** csc wants quoted
  rsp lines, fsc bare ones (`CompilerRunner.RunPlan.ResponseQuoting`: `Argv` / `Verbatim`); every
  argument Xake.Dotnet hands to `shell` goes through `Tool.quoteArgument` (`PathSpacesTests.fs`).
- The fsc/locks self-build (`build.fsc.fsx`): a project-file change needs `import` AND
  `update-locks` in the same PR, or CI `build-fsc` fails on the fingerprint check (#72, #79,
  #95); the locks name the SDK compiler by version (10.0.403, pinned in its `build.yml`); the
  importer reads `PrivateAssets=all` as "bundled".

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

## How to verify changes here

```bash
dotnet build src/core -c Release              # both TFMs, 0 warnings
dotnet build src/tests --no-incremental       # 0 warnings
dotnet test src/tests -c Release              # all green (1747 tests, 10 skipped, after the split)
dotnet fsi build.fsx -- -- build test pack    # self-hosting: the engine, the test jobs, the Xake package
```

`pack` writes `out/pkg/Xake/` only and fails if the nupkg carries anything but `Xake.dll`/`.xml`
per framework under `lib/` (docs/devprocess.md).
- Trap: the msbuild runs of `build.fsx` (`test-build`, the `out/` build, `pack`) all build src/core through project
  references into the same `bin/Release` and `obj` folders; they take the `msbuild` resource (one at a time), since two
  at once failed the v4.0.0-preview.1 publish run (MSB4018 GenerateDepsFile, `Xake.deps.json` in use). Run any new
  msbuild step through the script's `dotnet` helper, which takes it.
- Trap: the `out/` build's sources exclude `src/core/obj/`: `dotnet build` and `pack` write the version into
  `obj/**/Xake.AssemblyInfo.fs`, which made every `build` a rebuild before 4.0.

`samples/features.fsx` references `out/netstandard2.0/Xake.dll`, so run the self-hosting build first.

## Earlier work: delegated execution (merged, PR #15)

The delegated/distributed execution model (`Resource`, `runDetached`, `delegated`) is designed
in [`docs/delegated.md`](delegated.md). Two facts from that work still bite:

- Delegated rules are never bounded by the local CPU pool -- the engine runs executor and body
  detached, so `runDetached` inside a delegated body is redundant.
- `Scheduler.withYieldedSlot` assumes the caller holds exactly one CPU slot. Calling it without
  one releases a phantom permit and silently disables the `Threads` limit. Inside a
  `DelegatedExecutor` (already detached) use `Resource.acquire`/`withAcquired` instead.
