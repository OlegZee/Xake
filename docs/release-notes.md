# Release notes: `Xake`

The `Xake` package (engine and `Xake.Dotnet`). Xake.Hermetic.Dotnet has its own notes
([hermetic/README.md](hermetic/README.md)).

## 4.0.0 (unreleased)

A breaking release: variables are evaluated at launch by declarations, and Xake.Dotnet reads
only the variable set. Xake.Hermetic.Dotnet builds against the in-repo Xake until 4.0 and
ships its next release (0.11) together with it; at 4.0 `Xake.Dotnet` and the hermetic package
are split out of this repository (release-process decision D).

Migration, in short: a CI that put `NUGET_PACKAGES`, `XAKE_CSC_SERVER`, `XAKE_CSC_JOBS` or
`XAKE_FSC_JOBS` in the environment adds `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]` (and the
like, see below) or passes `-d`; a recipe that reads `Var.*` declares the key in `varschema`; a
script that reads `getVar "pkg"` for a field with `cliArg = "pkg"` reads the field's own key;
a name defined twice in a script is removed; credentials are not passed with `-d`; a field
without `envVar` that was set from the environment under its plain name gets `envVar = "NAME"`
(or the environment variable is renamed to `XAKE__NAME`), and a CI that happens to export
`XAKE__<NAME>` for a constant `var` checks that it means it.

**Variables are evaluated once, before the build, by declarations the script states.**
Details: [tasks.md](tasks.md#script-variables).

- **New: `var "N" [sources]`.** `var "N" [Env "X"; Default "v"]` takes the first defined source
  in list order; `Cli "ext"` sets the name's command-line name (`-d ext=...`); `Secret` /
  `SecretHashed` make it a secret. `var "N" "v"` keeps its meaning (a script value).
- **Evaluation at launch.** `var` rules and `varschema` fields are evaluated once, before `setup`
  and the first target (run, `--dryrun`, `--dump`, engine `start`): the environment is read
  there, and a missing `required` value or a value that does not convert to the field's type
  stops the run before any target (before: when a recipe read it). A `varschema` field's value is
  visible to `getVar` and in the banner. `--help` evaluates nothing.
- **Precedence:** engine per-demand variables > `-d` (the last wins) > the script's one definition
  of the name (`var` or `ExecOptions.Vars`) > the declaration's sources. A read records the final
  value (`Var (N, value)`), not its source, so moving an equal value between the environment,
  `-d` and `var` no longer rebuilds.
- **Breaking: a `Var.*` read in a recipe needs a declaration** of its key: a `varschema` field
  (or an equal `var [...]` rule). Without one the reading target fails ("... is read through Var
  but is not declared: declare it in varschema, or use getVar ..."); with a different one (another
  env name, default, type, required flag, secret mode or command-line name) it fails too. A Var
  read no longer reads the environment and no longer records `EnvVar`; use `getVar` for script
  values and `getEnv` for the environment.
- **Breaking: the key of a field is its `name`, else its field name.** `cliArg` is only its
  command-line name: with `p = Var.string(cliArg = "pkg")`, `-d pkg=v` sets `p` and `getVar "p"`
  reads it (3.15 keyed it by `pkg`; a script reading `getVar "pkg"` changes). `-d p=v` for such
  a field stops the run (`-d p: the variable is set on the command line as pkg; use -d pkg=...`).
- **Breaking: a name defined twice in the script stops the run**: two `var` ops, a `var` op and an
  `ExecOptions.Vars` entry, a `varschema` field and a rule (before: the later `var` replaced the
  earlier value, `var` won over `ExecOptions.Vars`).
- **Breaking: `-d` of a secret stops the run**, under its key, its command-line name or one of its
  environment names (3.15 warned for an argument secret); `-d` of a `Var.env` field stops the run
  (3.15 ignored it). `getVar`/`getEnv` of a secret return the value; masking applies to output.
  Mark credential fields secret: `build.fsx` now has `NUGET_KEY = Var.env(envVar = "NUGET_KEY") |> secret`.
- **Breaking: an implicit `XAKE__<NAME>` environment source.** Every declaration without an
  explicit environment name reads `XAKE__<NAME>` first, `NAME` being its key in UPPER_SNAKE
  (`buildConfig` -> `XAKE__BUILD_CONFIG`, `netfx-target` -> `XAKE__NETFX_TARGET`): a `var` rule
  without an `Env` source (`var "N" [Default "d"]` reads `XAKE__N`, then the default), a
  `Var.string`/`int`/`bool`/`create`/`env`/`secret` field without `envVar`. The 3.15 derived name
  without the prefix (`BUILD_CONFIG`) is no longer read, with no compatibility path. An explicit
  `Env "X"` / `envVar = "X"` replaces the implicit source; `Var.arg` has no environment source.
  Migration: keep a plain name with `envVar = "NAME"` / `Env "NAME"`, or export `XAKE__NAME`.
- **Breaking: a constant is overridden by `XAKE__<NAME>`.** `var "N" "v"` and an
  `ExecOptions.Vars` entry take `XAKE__N` when it is present (banner `env:XAKE__N`), else `v`;
  `-d` still wins. Names that nothing declares get no implicit source: `XAKE__*` variables are not
  scanned into the variable set. Two keys whose implicit names are equal (`a-b` and `A_B`,
  `fooBar` and `FOO_BAR`) stop the run: `Variables 'A' and 'B' both read the environment variable
  XAKE__N; rename one, or give one an explicit Env source`.
- **Secrets through `XAKE__`:** `var "T" [Secret]` reads `XAKE__T`, the normal way to pass a
  secret; `-d T=...` and `-d XAKE__T=...` stop the run ("set XAKE__T instead"). `--help` shows the
  environment name of every rule and field, implicit or explicit.
- **Engine:** `Demand(target, vars)` values now win over the script's values (they were shadowed);
  a per-demand value for a secret, an environment-only or a mistyped variable fails the demand
  before any target. `XakeEngine.Start` raises `XakeException` on a launch error.
- **Output:** the banner lists each variable once with its source (`cli`, `cli:<name>`, `script`,
  `env:<NAME>`, `default`, `(not set)`), secrets as `***` (3.14 labelled all `script/cli`);
  `--help` lists `var [...]` rules after the `varschema` fields, with their command-line names;
  the `Debug` options dump shows the variables, secrets masked.
- **Dependencies:** a target whose record comes from a 3.15 Var read (`Var (N, None)` +
  `EnvVar`) rebuilds once; a secret field's record moves from `SecretDep (env name, SecretEnv)` to
  `SecretDep (key, SecretArg)`, one rebuild. Repeated reads of a name in one recipe record one
  dependency. The database format is unchanged (0.6).
- **API:** new `VarSource`, `VarOrigin`, `VarDecl`, `VarEntry`; new fields `ExecOptions.CliVars`
  (the `-d` pairs; `-d` no longer goes into `ExecOptions.Vars`), `ExecOptions.VarDecls`,
  `ExecOptions.VarDefinedBy` (where each `var`/`varschema` definition came from, for the
  diagnostics) and `EngineOptions.Variables` (the variable set built at launch). Binary break for
  code that builds `ExecOptions`/`EngineOptions` with a full record expression (not `with`);
  `ExecContext` and the records Xake.Hermetic.Dotnet builds are unchanged.

### Library readers (Xake.Dotnet) and the optional import of library declarations

- **Breaking: Xake.Dotnet reads its build variables only from the variable set**, never from
  the environment. `NUGET_PACKAGES`, `XAKE_CSC_SERVER`, `XAKE_CSC_JOBS`, `XAKE_FSC_JOBS` set
  only in the environment are ignored; a CI that relied on one adds a declaration, e.g.
  `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]`, `var "XAKE_CSC_SERVER" [Env "XAKE_CSC_SERVER"]`,
  `var "CSC_JOBS" [Env "XAKE_CSC_JOBS"]`, or passes `-d`.
- **Breaking: the package folder defaults to `<project root>/.packages`** (was the environment
  variable `NUGET_PACKAGES`, else `~/.nuget/packages`). It is always the build's own:
  `packageRootWithSource` returns `ScriptVariable` for it, so `HERMETIC=on` accepts it without a
  `NUGET_PACKAGES` variable; `PackageRootSource.EnvironmentVariable`/`UserProfile` are
  `[<Obsolete>]`. A child `dotnet restore` gets the folder as `NUGET_PACKAGES`. The context-free
  `DotNetFwk.nugetRoot ()` is `~/.nuget/packages` and reads no environment.
- **Breaking: strict switches.** `NUGET_FETCH`, `XAKE_CSC_SERVER` (`on`/`off`, `true`/`false`,
  `yes`/`no`, `1`/`0`) and `CSC_SERVER` (`on`, `off`, keepalive seconds) fail the reading target
  on any other value (`CSC_SERVER='bogus': expected on, off or a number of seconds`; 3.15 warned
  or took it as on). An empty value is the default (`-d NUGET_FETCH=` is on).
- `RunOptions.Default.Server` is `Shared None` and reads nothing; `CompilerServer.fromEnvironment`
  is `[<Obsolete>]` and returns `Shared None`. A recipe resolves the server from `CSC_SERVER`,
  then `XAKE_CSC_SERVER` (`CompilerServer.resolve`).
- **`Var.read<'t> "NAME"`**: the read for a library. A declared name is read through the
  script's declaration, whatever its sources and default, only its type checked
  (`Variable 'N' is read as 'T' but is declared by ... as 'U'`); an undeclared one is `getVar`.
  No environment read, the dependency `getVar` records.
- **`DotNetVars.Default`**: the variables Xake.Dotnet reads, as an optional schema
  (`NUGET_PACKAGES`, `NUGET_FETCH`, `HERMETIC`, `CSC_SERVER`, `XAKE_CSC_SERVER`, `CSC_JOBS`,
  `FSC_JOBS`; strings, `-d` only, the library's defaults). `varschema DotNetVars.Default` lists them
  in `--help` and the banner; `{ DotNetVars.Default with NUGET_PACKAGES = Var.string(envVar = "NUGET_PACKAGES") |> withDefault ".packages" }`
  also takes the environment (without `envVar`, `XAKE__NUGET_PACKAGES`). Importing changes no value; a target built before the import
  rebuilds once (its dependency records the default).

## 3.15.0 (2026-10-29)

- **The published version is the tag.** `v3.15.0` goes to nuget.org as `3.15.0`, no longer with
  the run number as a fourth component (3.14.0 is `3.14.0.45`); the same for
  Xake.Hermetic.Dotnet from its next release. A dependency range or `#r` can be written from the
  tag. Older releases keep their four-part versions.
- **`ExecContext.withLogger logger ctx`**: the context with its logger replaced, for code
  outside `Xake.dll` (a `{ ctx with Logger = ... }` compiled elsewhere breaks when
  `ExecContext` gains a field). The records Xake.Hermetic.Dotnet builds — `ExecContext`,
  `CscSettingsType`, `RunOptions`, `Csc`, `FscSettingsType`, `FscRunOptions`, `Fsc` — get no new
  fields until 4.0 (`RecordShapeTests` guards the field lists; docs/devprocess.md).

## 3.14.0 (released as 3.14.0.45, 2026-10-28)

- **`setup` op.** `setup ["a"; "b"]` runs the targets sequentially before the build (CLI: after
  options and variables are final; engine: once at start). Symmetric to `teardown`, but several
  `setup` ops accumulate while `teardown` replaces. A setup failure is logged, skips the build,
  still runs teardown and fails the run (exit code 2, `XakeException` with `ThrowOnError`).
  Not run in `--dump`/`--dryrun`; the dry run lists the setup targets. New `ExecOptions.Setup`
  (binary break for code that builds `ExecOptions` with a record expression, not `with`).
  Declare setup targets that must run every time with `command "name" { ... }` (always reruns).
- **Run banner at `Info`.** Version, mode, targets, root, database, threads, rule matching, log
  settings, setup/teardown and the script variables (secrets as `***`, source labelled
  `script/cli`) are logged once per run, also for `--dump` and `--dryrun`. Visible in the default
  `build.log`, not on the default console.
- **Behaviour change: the command line wins over the script.** A script that pinned a value with
  `var "NAME" v` now yields to `-d NAME=value`; `-d` also wins over `ExecOptions.Vars` passed to
  `xake`/`xakeArgs`. The last `-d` wins when a name is given twice (before: the first); `var`
  still wins over `ExecOptions.Vars`. `consolelog` yields to `-ll`, `filelog` to `-fl` (path) and
  `-fll` (level), each separately, and `rootdir` to `-r`, the way `want` already yielded to
  targets on the command line. `IgnoreCommandLine = true` still skips the command line entirely.
  The environment is not involved. Engine mode's per-demand variables are unchanged. Mechanism:
  `Run` applies the command line over the script's options, changing only the fields whose flags
  are given; the logo and `--help` text are printed then. This adds nothing to `ExecOptions` or
  `XakeScript`.
- **Breaking (source): `wantOverride` removed; `want` always yields to command-line targets.** A
  script that uses `wantOverride` fails to compile; replace it with `want`. `want` now replaces
  `ExecOptions.Targets` passed to `xake`/`xakeArgs` (before: it yielded to them), the way `var`
  wins over `ExecOptions.Vars`; command-line targets replace both. `src/hermetic.tests` moved to `want`
  with Xake.Hermetic.Dotnet 0.9.0.

## 3.13.0 (released as 3.13.0.43, 2026-10-27)

- **msbuild and NuGet diagnostics in the log.** `Tool.diagnosticLevel` (and the forwarder
  `levelFromString`) also recognizes `<origin> : error|warning CODE: text [entry]`
  (`MSBUILD : error MSB1001`, `CSC : error CS2001`, `/p/a.csproj : error NU1101`) and NuGet's
  `warning NU1xxx:` without an origin. Errors go to `Error` (before: `Verbose`, invisible at the
  default verbosity), these warnings to `Info`. The `msbuild` task, csc/fsc and the
  reference-pack `dotnet restore` classify through the new `Tool.DiagnosticLog` (a repeated
  warning is logged once per run) and fail with the first 5 distinct errors, then
  "+N more, see the log" (`Tool.failOnExitCodeWith`, `Tool.errorSummary`). Signatures unchanged;
  more lines now reach `Error`. See [dotnet-build.md](dotnet-build.md#troubleshooting).
- **Breaking (binary): secret script variables.** `Var.secret<'t>(envVar = ...)` (environment
  only), `|> secret` and `|> secretHashed` on any var. The database keeps a digest, never the
  value: presence only by default (a rotated token rebuilds nothing, unset/set does), a salted
  HMAC-SHA256 with `secretHashed` (a changed value rebuilds). Change reasons name the variable
  only. Every logger masks resolved secret values (4 characters or more) as `***`, `--dump` and
  `--dryrun` included; `--help` shows no default for a secret; `-d` is refused for `Var.secret`
  and warned about for an argument var marked `secret`. A plain `getEnv`/`getVar` of a name
  declared secret is treated as secret. `OptionalVar`/`RequiredVar` gain a `Secret` field and
  `VarHelp` an `IsSecret` field: code compiled against an older Xake that builds these records
  must be recompiled (Xake.Hermetic.Dotnet needs a range bump). Database format 0.6 (new
  dependency case `SecretDep`); 0.4/0.5 databases load and are upgraded, clear values of names
  now declared secret are replaced by their digest. See [tasks.md](tasks.md#secret-variables).

## 3.12.0 (tagged v3.12.0, 2026-10-24)

- **Rule selection: the most specific matching rule wins.** When several rules match a target,
  Xake now picks the one with the most literal characters, then the fewest `**`, then the
  fewest/tightest wildcards (`?` tighter than `*`; a named group scores like its bare wildcard),
  instead of simply the last declared. Two *different* patterns that tie fall back to last-declared
  and log a warning; two rules with the *same* pattern string keep the silent last-wins override.
  This only changes builds that relied on declaration order against specificity. `ruleMatch LastDeclared`
  in the script restores the old order — a **deprecated** one-release migration aid.
  `-d EXPLAIN_RULE=<target>` prints the candidate rules and the winner. See
  [overview.md](overview.md#rule-selection--the-most-specific-rule-wins).
- Internal: one pattern AST drives both rule matching and the specificity score (no behaviour change).

## 3.11.0 (tagged v3.11.0, 2026-10-22)

- `csc`/`fsc` compile from an SDK path that contains a space: SDK and response-file paths are
  quoted (`Tool.quoteArgument`, see [hermetic/README.md](hermetic/README.md) for the package side).

## 3.10.1 (tagged v3.10.1, 2026-10-21)

- **Progress line: running tasks as coloured Braille dots** (one dot per task, cells filled
  column-wise `⠁⠃⠇⡇⡏⡟⡿⣿`, at most 4 cells for 30 tasks, `⣿⣿⣿⣿+` above; green, yellow past
  half, red above the cap). Redirected output keeps the `N tasks` text.
- **`rm { }` and `cp { }` take many targets**: `dir`, `file` and `files` may repeat and combine,
  applied in declaration order (before, each op overwrote the previous one and only the first
  kind ran). `rm` skips paths removed by an earlier target; `cp` keeps each source's relative
  path rule (`file` always flattened). `RmArgs` is now `{ targets: RmTarget list; verbose }`,
  `CpArgs` has `sources: CpSource list` instead of `dir`/`file`/`files`.
- Build scripts (not the package): `test` runs 8 parallel jobs (`testjobs.fsx`, shared by
  build.fsx and build.fsc.fsx) on test projects built once; each job logs to
  `out/test/<project>/<slice>/`. `test` takes about 55 s instead of 3 min 40 s.
  `-d FILTER=...` now reaches the scripts' variable (only `$FILTER` did).

## 3.10.0 (released as 3.10.0.35)

- **Breaking: `need` of a rule target while a resource is held fails.** Inside `withResource`,
  and anywhere in a `requiring` rule, a `need`/`needFiles`/`dependsOn` of a target that has a rule
  raises an error naming the resource, the needing and the needed target. It could deadlock the
  build (every unit held by a recipe waiting for dependencies that wait for a unit; a hang at zero
  CPU). Plain files without a rule are still allowed. Need first, then acquire; `allowNeed body`
  lifts the check when the dependency never takes the resource. See [resources.md](resources.md)
  and [delegated.md](delegated.md#1-resource).
- **`CSC_JOBS` / `FSC_JOBS`** cap the concurrent compiler processes of `Csc.run` / `Fsc.run`
  (script variable, then env `XAKE_CSC_JOBS` / `XAKE_FSC_JOBS`; unset or `0` unlimited). Taken
  around the process launch only, after every input is built, so it replaces a `withResource`
  around the compile rule. Not a dependency: changing it rebuilds nothing. See
  [csc-server.md](csc-server.md#limiting-concurrent-compiles-csc_jobs-fsc_jobs).
- **`throttle (resource, units)`** in `csc { }` / `fsc { }` (`Throttle` in `CscSettingsType`,
  `RunOptions`, `FscSettingsType`, `FscRunOptions`; default `None`): the compiler launch takes
  the script's own resource, at the same place, in place of `CSC_JOBS` / `FSC_JOBS`.
- **Breaking for record construction:** `ExecContext` gains `HeldResources`. Code built on 3.9
  that copies an `ExecContext` (`{ ctx with ... }`) has to be rebuilt; Xake.Hermetic.Dotnet
  0.4 (`[3.9.0.30, 3.10)`) does, and needs a release on 3.10.
- **Xake.Hermetic.Dotnet: `Locks.compile settings lockPath fwk`**, with `Locks.Settings = { Roots;
  Versions; Lock: Lock.Options }` (`Settings.Default`: no roots, no versions, default options).
  `compileAsWith`/`compileAs` are thin wrappers over it, unchanged. The script's `Lock.Options`
  reach the runner (an explicit `Run.Server` wins over `CSC_SERVER`); on a release built on 3.10
  that is where `Run.Throttle`/`FscRun.Throttle` go. See
  [hermetic/lock.md](hermetic/lock.md#building-from-xake-generated-locks).

## 3.9.0 (tagged v3.9.0, 2026-10-18; 3.9.1 the same day)

- **Project description in the core.** `xake.project.json` (the format Xake.Hermetic.Dotnet
  introduced) is now `Xake.Dotnet.Description`: parse, validate, `load`/`save`/`find`/`beside`,
  schema `docs/schemas/xake.project.schema.json`. The schema is the hermetic package's latest plus
  `frameworkReferences` (shared frameworks). See [description.md](description.md).
- **`csc { project "path" }` and `fsc { project "path" }`** compose a compilation from a
  description: sources (masks, `!` excludes), resources, defines and the SDK's framework symbols,
  options, `internalsVisibleTo`/`assemblyAttributes`, framework assemblies by name or path, and
  `frameworkReferences` (`Microsoft.AspNetCore.App`, `Microsoft.WindowsDesktop.App`) from the
  SDK's targeting packs. What the block gives explicitly wins. No msbuild, no restore: a
  description with `packages` or an unresolved `projectRefs` entry is rejected, naming
  Xake.Hermetic.Dotnet.
- `DotNetFwk.sharedFrameworkReferences`: a shared framework's reference assemblies for a .NET
  target framework.
- **Breaking for record construction:** `CscSettingsType` and `FscSettingsType` gain a `Project`
  field (build them with `{ CscSettings with ... }` as before; a full record expression needs the
  new field). Packages built on the 3.8 records (Xake.Hermetic.Dotnet 0.3 builds on `[3.7.0.26,
  3.8)`) need a release on 3.9.

## 3.8.0 (tagged v3.8.0, 2026-10-17)

- **`ArtifactDep2`: a dependency on a target with a rule carries the dependee's change stamp**, so
  a dependent goes stale when its dependee was rebuilt by an earlier separate run (database
  format 0.6; `ArtifactDep` is read from older databases). See [implnotes.md](implnotes.md).
- **`.resx` compiles on any host** (`Resx.compile`/`Resx.refresh`, the SDK's `GenerateResource`
  format: typed values, `ResXFileRef`, code pages), with csc crash diagnostics and platform
  target-framework monikers (`net8.0-windows`).

## 3.7.0 (tagged v3.7.0, 2026-10-12)

- `Fingerprints` library for committed generated files (recorded atomically, thread-safe).

## 3.6.0 (tagged v3.6.0, 2026-10-10)

- **`HERMETIC=on`**: the completeness gate in `Csc.ofSettings`/`Fsc.ofSettings` (see
  [dotnet-build.md](dotnet-build.md#hermetic-mode)).
- **`NUGET_PACKAGES` and `NUGET_FETCH`** script variables; the .NET targeting pack is restored when
  it is not installed, `NETCORE_REF_VERSION` pins one per major.minor.
- Reference packages for the SDK-sourced defaults: netstandard2.1, the net46x `netstandard.dll`
  facade, `FSharp.Core` for `netN.0` targets.
- `csc`/`fsc` write `<out>.runtimeconfig.json` for `netN.0` executables only when the rule
  declares it as a target.

## 3.5.0 (tagged v3.5.0, 2026-10-04)

- **`Fsc` through the shared compiler runner**: `Fsc` record, `Fsc.ofSettings`, `Fsc.run`,
  `fsc { resolve }`.
- The default target framework is the SDK's own .NET; `net5.0` and later compose.
- **`fsc` builds for .NET Framework from any host**: `FSharp.Core` and a `netstandard.dll` facade
  are referenced implicitly (before: FS0074).
- Reference packs go through the one restore mechanism.

## 3.4.0 (tagged v3.4.0, 2026-10-01)

- **`Csc` as the resolved compilation**, `csc { resolve }`, `Csc.run`, the Roslyn compiler server
  (`CSC_SERVER`) and a compiler `toolset` (`CSC_TOOLSET`). See [csc-syntax.md](csc-syntax.md).
- The SDK probe honours the project's `global.json`.
- A target requested twice is built once per run; file rules whose pattern contains `..` match;
  a transient sharing violation opening the database is retried instead of recreating it.
- The Xake.Hermetic.Dotnet package appears in this period; see
  [hermetic/README.md](hermetic/README.md).
