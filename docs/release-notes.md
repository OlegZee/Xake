# Release notes: `Xake`

The `Xake` package (engine and `Xake.Dotnet`). Xake.Hermetic.Dotnet has its own notes
([hermetic/README.md](hermetic/README.md)).

## 3.10.0 (unreleased)

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
