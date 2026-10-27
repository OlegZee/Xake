# Variable precedence, `setup`, run parameters in the log: analysis

Read-only analysis of `src/core` (and of how `src/dotnet` and `src/hermetic` read variables) on
origin/dev at 92ef634 (2026-10-27). Three questions: A. why a script's `var` cannot be overridden
from CI, B. whether a `setup` symmetric to `teardown` fits, C. why the run parameters are not in
`build.log`.

## A. Variable precedence today

### How a value reaches `ctx.Vars`

1. **Initial options.** `xake opts` / `xakeArgs args opts` start from the `ExecOptions` record the
   script passes in (`ExecOptions.Vars`, default `[]`, `XakeScript.fs:26,78`).
2. **Command line, parsed first.** `xakeArgs` folds the CLI over the initial options *before* the
   builder runs (`Program.fs:135-144`). `-d NAME=value` (or `NAME:value`, split on the first
   `=`/`:`, `Program.fs:90-97`) *appends*: `Vars = o.Vars @ [(k,v)]` (`Program.fs:50-51`).
   `IgnoreCommandLine = true` skips the CLI altogether (`Program.fs:137`).
3. **Script ops, applied after.** `RulesBuilder(options)` receives the CLI-merged options and every
   custom op rewrites them: `var` *removes every entry with that name, including the `-d` one*, and
   prepends its own (`XakeScriptBuilder.fs:45-46,93-96`). The same "last writer wins" holds for
   `rootdir` over `-r` (`:89-91`), `filelog` over `-fl`/`-fll` (`:98-102`), `consolelog` over `-ll`
   (`:104-108`), `dryrun` (`:84-86`). The one exception is `want`, which keeps CLI targets
   (`:132-135`, comment "Options override script!"); `wantOverride` replaces them (`:137-140`).
4. **Run.** `Run` hands the final options to `ScriptRunner.runScript` (`XakeScriptBuilder.fs:78-82`);
   `createScriptContext` copies `opts.Vars` into the context unchanged (`ScriptRunner.fs:55-65`,
   `ExecCore.fs:401-430`).
5. **Lookup is first match.** `Util.getVar` is `List.tryPick` (`ExecTypes.fs:65-67`), so among
   duplicates the *first* entry wins: `-d A=1 -d A=2` gives `1`; an initial `ExecOptions.Vars`
   entry beats a `-d` of the same name (it sits in front); engine mode's per-demand `?vars` are
   appended after the script's (`XakeScriptBuilder.fs:167-169`), so they cannot override either.
   The dependency check reads the same list (`DependencyAnalysis.fs:141`).

### Readers

| Reader | Sources, in order | Where |
|---|---|---|
| `getVar "X"` in a recipe | `ctx.Vars` only (first match); records a `Var` dependency | `ScriptFuncs.fs:116-123` |
| `getEnv "X"` | process environment only; records `EnvVar` | `ScriptFuncs.fs:100-106`, `ExecTypes.fs:63` |
| `Var.string/int/bool/create` (scope `ArgAndEnv`) | `getVar cliName` (so `-d` *or* `var`), else `getEnv envName`, else `withDefault`, else None / "Required variable ... is not provided" | `Vars.fs:63-73,110-149` |
| `Var.env` (scope `EnvOnly`) | env only; `-d` and `var` are **silently ignored** | `Vars.fs:76-77,125` |
| `Var.arg` (scope `ArgOnly`) | `ctx.Vars` only | `Vars.fs:80-81,128` |
| `Var.secret` (`EnvOnly` + secret) | env only; a `-d`/`var` of the name is an error (exit 2) | `Vars.fs:86-87,118-124`, `ScriptRunner.fs:27-52,173-177` |
| `varschema` | only `--help` metadata and secret declaration; its `withDefault` value is shown in `--help` but **never put into `ctx.Vars`** | `Vars.fs:155-206`, `XakeScriptBuilder.fs:11-33` |

Xake.Dotnet / Xake.Hermetic.Dotnet read their settings with `getVar`, i.e. from `ctx.Vars` only;
schema vars never reach them. Fallbacks differ per variable:

| Variable | Chain | Where |
|---|---|---|
| `NUGET_PACKAGES` | `getVar` (relative to root) > env `NUGET_PACKAGES` > `~/.nuget/packages`; only the script-variable source counts as "the build's own" for `HERMETIC=on` | `DotNetFwk.fs:340-345,756-763,784-795`, `HermeticMode.fs:155` |
| `NUGET_FETCH` | `getVar` only, no env fallback; unset = on | `DotNetFwk.fs:886-892` |
| `NETFX`, `NETFX-TARGET` | `getVar` only | `Dotnet.Msbuild.fs:52`, `Csc.fs:514,520`, `ProjectPlan.fs:267` |
| `CSC_SERVER` | target setting > `getVar "CSC_SERVER"` > env **`XAKE_CSC_SERVER`** (other name) > server on | `Csc.fs:30-63` |
| `CSC_TOOLSET`, `FSHARP_CORE_VERSION`, `FSCVER`, `NETSTANDARD_*`, `NETCORE_REF_VERSION` | `getVar` only | `Csc.fs:696`, `Fsc.fs:395,444`, `DotNetFwk.fs:1070-1073,1230` |
| `CI`, `LOCK_REFRESH` | `getVar` > `getEnv` same name | `hermetic/Lock.fs:1342-1354`, `hermetic/Locks.fs:106-111` |
| `HERMETIC`, `LOCK_STALE` | `getVar` only | `HermeticMode.fs:75`, `hermetic/Locks.fs:498` |

### Effective precedence (one name, all layers present)

`var` op > initial `ExecOptions.Vars` > first `-d` > later `-d` > (only for `Var.*` `ArgAndEnv`,
`CI`, `LOCK_REFRESH`, `NUGET_PACKAGES`) environment > `withDefault` / built-in default.

### The dissonance

- `var` is meant as a script default but behaves as a hard pin: `-d` of the same name is deleted
  (`XakeScriptBuilder.fs:46`). The docs present them as interchangeable ("`var "NUGET_PACKAGES"
  ".nuget/packages"` (or `-d NUGET_PACKAGES=<dir>`)", `docs/hermetic/workflows.md:418,666`,
  `docs/tasks.md:518,531`, `docs/dotnet-build.md:317`, `HermeticMode.fs:155`), which only holds
  when the script does not set the name.
- `want` lets the CLI win, every other op does not (`XakeScriptBuilder.fs:135` vs `:84-108`).
- Duplicates resolve first-wins (`ExecTypes.fs:67`) while `var` itself is last-wins (`:46`), and an
  initial `ExecOptions.Vars` entry beats `-d`, so `xake { ExecOptions.Default with Vars = [...] }`
  is not an escape either.
- `varschema ... withDefault` documents a default in `--help` that `getVar` never sees.
- `Var.env` ignores `-d` without a word; `Var.string` treats a `var` value as if it came from the
  CLI and so ranks it above the environment.
- Env fallback is per library variable (`NUGET_PACKAGES` yes, `NUGET_FETCH` no, `CSC_SERVER` under
  another name).
- The in-repo `build.fsc.fsx:34` has exactly the consumer's pattern (`var "NUGET_PACKAGES" ".packages"`).

### Options

1. **`-d` wins over `var`.** Keep CLI vars apart from script vars and lay them on top in `Run`
   (effective = CLI vars, last `-d` wins; then `var` values; then initial `ExecOptions.Vars`).
   Who relies on `var` as a hard pin: nothing in the repo. No sample uses `var` (`samples/`),
   `build.fsx` has none, `build.fsc.fsx:34` would *gain* an override; no test combines `var` with
   `-d` (tests seed `ExecOptions.Vars`: `PackageRootTests.fs:36,79,132,159`, `CscServerTests.fs:108`,
   `ToolsetTests.fs:52,78`, `FscTests.fs:484`, hermetic tests `Vars = ["CI","off"]`). External
   scripts that pin with `var` to defeat a stray `-d` would change behaviour; that is unlikely and
   contradicts the docs. Cost: small, contained in `Program.fs`/`XakeScriptBuilder.fs`. The
   environment stays where it is (CI passes `-d`).
2. **New op `vardefault`, `var` stays a pin.** Non-breaking, fits 3.13.x, but adds a second op whose
   difference users must learn, keeps the docs wrong for `var`, and the consumer must edit the
   script anyway.
3. **Schema-first.** `varschema` defaults (and `var` as sugar for them) are resolved into `ctx.Vars`
   at `Run` as CLI > env > script default. One chain everywhere and `--help` becomes truthful, but
   env values would then be recorded as `Var` rather than `EnvVar` dependencies, secret handling
   (`ScriptRunner.fs:27-52`) must be redone, every library variable needs a schema entry, and the
   per-variable env names (`XAKE_CSC_SERVER`) do not fit. Large; a 4.x-sized design.
4. **No engine change, `Var.env "NUGET_PACKAGES" |> withDefault ".packages"`.** Verified: does not
   work. `Var.env` is `EnvOnly`, so `-d` is ignored (`Vars.fs:125`); the value only exists inside
   the recipe that binds it and is never written into `ctx.Vars`, while `DotNetFwk.packageRoot`
   reads `getVar "NUGET_PACKAGES"` (`DotNetFwk.fs:758`). Without the `var` the library falls back
   to the env var as `EnvironmentVariable`, which `HERMETIC=on` rejects (`HermeticMode.fs:155`).
   The only script-only workaround is to compute the value before the builder, e.g.
   `var "NUGET_PACKAGES" (Environment.GetEnvironmentVariable "NUGET_PACKAGES" |> Option.ofObj |> Option.defaultValue ".packages")`
   (env > default, still no `-d`), or to read the CLI args by hand.

**Recommendation: option 1**, plus last-`-d`-wins for duplicates and engine-mode `?vars` overriding
script vars. It makes the existing docs true and needs no consumer change. It changes observable
behaviour (a script that pins with `var` now yields to `-d`; targets whose `Var` dependency value
changes rebuild once), so it belongs in **3.14** with a release note, not 3.13.x. Leave the
other ops (`consolelog` vs `-ll`, `filelog` vs `-fl`, `rootdir` vs `-r`) for a separate decision.

## B. `setup` symmetric to `teardown`

`teardown` today: the op stores a target list and *replaces* any earlier one
(`XakeScriptBuilder.fs:146-148`, `ExecOptions.Teardown` `XakeScript.fs:57`). In CLI mode it runs
only in the `Run` branch, after the build, whether it succeeded, failed or was cancelled by Ctrl+C
(`ScriptRunner.fs:183-219`); targets run sequentially through `demandTarget`
(`ScriptRunner.fs:67-71`, `ExecCore.fs:438-451`), i.e. through the normal up-to-date check. A
teardown failure is logged; it sets exit code 2 (and `XakeException` with `ThrowOnError`) only when
the build succeeded, otherwise the build error is kept (`ScriptRunner.fs:208-219`). It does not run
in `--dump`/`--dryrun` or when a secret check fails (`ScriptRunner.fs:173-182`). Engine mode runs it
from `StopAsync` (`XakeScriptBuilder.fs:214-223`). Tests: `CommandLineTests.fs:176-215`.

A `setup` op would fit the same shape: a target list run via `demandTarget` before the first target
group (before `ExecCore.runBuild` at `ScriptRunner.fs:187`), after options and vars are final, with
logging and `getVar` available; a failure would skip the build, run teardown, exit 2.

What already serves: a phony target listed first, `"main" <<< ["setup"; "build"]`, or `want`. Gaps:
targets given on the CLI bypass `main`; a target that reads vars records `Var` dependencies and is
then skipped as up to date on the next run, so it needs `alwaysRerun()`
(`DependencyAnalysis.fs:135-153`); it runs in parallel with siblings unless a `<<<`-style sequence is
used.

For the concrete use (a banner of effective settings) neither is needed: the engine knows all of it
(see C). **Recommendation:** engine banner now; `setup` only when a use appears that is real work
(e.g. a lock or a login), then with the semantics above and `setup` accumulating, not replacing.

## C. Run parameters missing from the log

The only dump is `logger.Log Level.Debug "Options: %A" { options with Vars = []; VarSchema = ... }`
(`ScriptRunner.fs:155-157`). Two reasons it is invisible:

- `Debug` is only in `Diag` (`Logging.fs:41-47`); the default file level is `Chatty`
  (`XakeScript.fs:75`, also the default of the `filelog` op, `XakeScriptBuilder.fs:101`), which
  stops at `Info`. So it appears only with `-fll Diag` / `filelog ... Diag` (as `build.fsx:97` does).
- Even then the vars are blanked (`Vars = []`), because secret values are not yet registered: the
  dump runs before `checkSecretVars` (`ScriptRunner.fs:169`).

What does reach a `Chatty` log: `Build target list [...]` at `Info` (`ExecCore.fs:379`), "No
target(s) specified. Defaulting to 'main'" (`ScriptRunner.fs:163`), dry-run targets at `Command`
(`ScriptRunner.fs:80`). Nothing records `-d` values, root, log file, threads or db file.

**Proposal:** after `checkSecretVars` (so `SecretMask` holds the values), log one compact block:
Xake version, mode (run/dryrun/dump), targets, project root, db file (or "no persist"),
threads, rule match, console/file log path and levels, teardown, and the vars as
`NAME=value (cli|script)`, with values of names where `Secrets.isSecret` (`Types.fs:165`) is true
shown as `***` (the `MaskingLogger`, `ScriptRunner.fs:14`, masks registered values as a second
guard). Keep the `Debug` `%A` dump as the verbose form. `--dump`/`--dryrun` print the same banner.

## Tests that pin the current behaviour

- Vars: `src/tests/VarsTests.fs` (CLI over default `:76`, CLI over env `:90`, `Var.env ignores CLI
  arg` `:526`, `Var.arg ignores env var` `:561`, Var/EnvVar dependency recording `:450-505`).
- Secrets with `-d`: `src/tests/SecretVarsTests.fs:272-295`.
- CLI parsing incl. `-d` order: `src/tests/CommandLineTests.fs:26-54`; teardown `:176-215`.
- Library vars via `ExecOptions.Vars`: `PackageRootTests.fs`, `CscServerTests.fs:108`,
  `ToolsetTests.fs`, `FscTests.fs:484`, `DotnetTasksTests.fs:412`, `src/hermetic.tests/*` (`CI`,
  `CSC_SERVER`, `LOCK_STALE`).
- Not covered: `var` vs `-d`, duplicate `-d`, `ExecOptions.Vars` vs `-d`, engine `?vars` vs script
  vars, the options dump. A change under option 1 needs these as new tests.

## Open questions for the owner

1. Option 1 in 3.14 (behaviour change) or a non-breaking `vardefault` in 3.13.x?
2. Should the other ops also yield to the CLI (`consolelog`/`-ll`, `filelog`/`-fl`/`-fll`,
   `rootdir`/`-r`), as `want` does?
3. Should `NUGET_FETCH` (and `NETFX`, `HERMETIC`) fall back to an env var of the same name, as
   `NUGET_PACKAGES`, `CI` and `LOCK_REFRESH` do?
4. Banner level: decided 2026-10-27 by the owner: **Info** (file at default levels).
5. `setup`: decided 2026-10-27: **do it now**, in one PR with the banner.
