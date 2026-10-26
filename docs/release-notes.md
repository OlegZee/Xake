# Release notes: `Xake`

The `Xake` package (engine and `Xake.Dotnet`). Xake.Hermetic.Dotnet has its own notes
([hermetic/README.md](hermetic/README.md)).

## 3.13.0 (unreleased)

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

## 3.10.1 (unreleased)

- **Rule selection: the most specific matching rule wins.** When several rules match a target,
  Xake now picks the one with the most literal characters, then the fewest `**`, then the
  fewest/tightest wildcards (`?` tighter than `*`; a named group scores like its bare wildcard),
  instead of simply the last declared. Two *different* patterns that tie fall back to last-declared
  and log a warning; two rules with the *same* pattern string keep the silent last-wins override.
  This only changes builds that relied on declaration order against specificity. `ruleMatch LastDeclared`
  in the script restores the old order — a **deprecated** one-release migration aid.
  `-d EXPLAIN_RULE=<target>` prints the candidate rules and the winner. See
  [overview.md](overview.md#rule-selection--the-most-specific-rule-wins).
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

## 3.9.0 (unreleased)

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
