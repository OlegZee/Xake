# Spec: variable evaluation

Status: rev. 5 for the owner's approval, 2026-10-28. It replaces rev. 4 and applies the owner's final decisions on it:
`Var.*` and `varschema` stay exactly as in 3.13 (the "Var untouched" delta of [spec4-trace.md](spec4-trace.md) Task 2),
a second definition of a name is an error, invalid switch values are errors, an empty switch is off, `NUGET_PACKAGES`
defaults to `.packages`, secrets are not refused to readers (E-7 dropped). It formalizes [concept.md](concept.md) §8
(variant 3). The 3.13 behaviour is in [analysis.md](analysis.md), the state after #103 in [after-103.md](after-103.md).
File:line references are to this branch (dev with #103). MUST, MUST NOT, SHOULD, MAY are normative. Every rule has an
id that the tests of §9 reference; rev. 4 ids are kept where the rule is unchanged. Retired: S-1..S-5, K-2, K-3, W-1,
E-3/E-4 as launch errors. Open points are in §10. No code before this spec is approved and the tests of §9 exist.

## 1. Scope and non-goals

In scope: how a build variable gets its value (the chain), the `var` source-list overload, second definitions,
secrets, what is recorded as a dependency, the banner, `--help`, the debug options dump, engine mode, and how
Xake.Dotnet and Xake.Hermetic.Dotnet read their variables.

- **N-1** No lazy evaluation, no declaration during the run: every value of the variable set is computed at launch.
  `getEnv` and the 3.13 environment fallback of `Var.*` stay live reads (N-3, Y-1).
- **N-2** No database format change: `Var`, `EnvVar`, `SecretDep` as in 3.13 (database 0.6).
- **N-3** `getEnv` is unchanged: an explicit, live read of the process environment that records `EnvVar`. It is not a
  variable read and libraries do not use it for variables.
- **N-4** The environment used for tool discovery is not a build variable: `PATH`, `DOTNET_ROOT`, `DOTNET_HOST_PATH`,
  `ProgramFiles` (`DotNetFwk.fs:128,181,410-424`).
- **N-5** The options precedence (`-ll`/`-fl`/`-fll`/`-r`/targets over `consolelog`/`filelog`/`rootdir`/`want`)
  landed in #103 and is not changed.
- **N-6** No warning for unused `-d` names or unused engine `?vars`.
- **N-7** `Var.*` and `varschema` are not changed at all (§3.3; `Vars.fs` untouched). In particular the canonical
  form of typed values (`Var.int`: `-d N=04` then `-d N=4` rebuilds, the string is recorded) is out of scope.
- **N-8** The secrets registry stays process-wide by name (`Types.fs:142-154`): a name declared secret by one script
  masks and digests that name for every reader in the process. Harmless (it only masks more); out of scope.

## 2. Definitions

- **Input values**: name/value pairs given from outside a rule: engine `?vars` (request), `-d` (cli),
  `ExecOptions.Vars` entries and constant `var "N" "v"` ops (script).
- **Rule**: a `var "N" [sources]` op. **Source**: an element of its list, `Env "X"` (environment variable `X`) or
  `Default "v"` (the constant `v`). The markers `Secret` and `SecretHashed` are list elements that define nothing;
  they set the secret mode.
- **Script definition**: an `ExecOptions.Vars` entry, a constant `var` op or a rule; each names one key.
- **Typed reader**: a `Var.*` value (`Var.string/int/bool/create/env/arg/secret`) read in a recipe by `let!`, inline
  or as a `varschema` field. Its **key** is `cliArg`, else `name`, else the field name, verbatim (3.13
  `resolvedCliName`, `Vars.fs:111-112`); its environment name is `envVar`, else `toEnvName` of the name
  (`Vars.fs:56-57,113`).
- **Launch**: in `Run` (run, dry-run, dump), after `ApplyCommandLine` and the `--help` exit, before the debug options
  dump, the banner, `setup` and any target; in `XakeEngine.Start`, before `setup`.
- **Variable set**: the immutable map `key -> entry` built at launch (V-1): value (or none), origin, secret mode,
  whether a rule exists for the key.
- **Origin**: where the winning value came from: `request`, `cli`, `script` (the **input origins**), `env:<NAME>`,
  `default`.
- **Switch**: a library variable with a closed set of values: `HERMETIC`, `NUGET_FETCH`, `CI`, `CSC_SERVER`,
  `XAKE_CSC_SERVER`, `LOCK_STALE`.

## 3. Rules

### 3.1 The variable set and the chain

**V-1.** At launch Xake builds the variable set once. It holds every key that appears in `-d`, `ExecOptions.Vars` or
a `var` op (either form); `varschema` adds no key (Y-2). A key that nothing names is absent and reads as `None`. The
set does not change during the run; in engine mode it lives as long as the engine, with a per-demand overlay (G-2).

**V-2 (chain, first defined wins).** For a key `N` the value comes from the first layer that defines `N`; no later
layer is consulted:
1. request: the engine's `?vars` of the demand, the last pair for `N` wins;
2. cli: `-d N=v`, the last `-d` of `N` wins;
3. script: the `ExecOptions.Vars` entry or the constant `var "N" "v"` (at most one, R-4);
4. the rule `var "N" [...]` (at most one, R-4): its sources in list order.

**V-3 (environment read once).** Each `Env "X"` source is read once, at launch, and only when the layers above have
not defined the key. No value of the set reads the environment after launch.

**V-4 (definedness).** `-d N=` and `var "N" ""` define `N` as `""`. `Env "X"` defines iff `X` is present, an empty
value included (Windows has no empty variables). `Default "v"` always defines. The evaluator neither trims nor
interprets values; readers do (L-7).

**V-5.** Keys are compared ordinally (case-sensitive), as `Util.getVar` does. The case-insensitive `-d EXPLAIN_RULE`
(`ExecCore.fs:208-210`) is unchanged.

### 3.2 `var` with a source list

**R-1.** `var "N" "v"` keeps its meaning: a script value, layer 3.

**R-2.** `var "N" [sources]` is a rule for `N`, layer 4: `Env "X"` and `Default "v"` tried in list order. `Secret`
makes `N` a presence secret, `SecretHashed` an HMAC secret (the 3.13 modes); a marker may stand anywhere in the list;
with both, `SecretHashed` wins (3.13 `Secrets.declare`).

**R-3.** The list is evaluated once, at launch (V-3). A list with no defining source (no `Env` present, no
`Default`) leaves `N` unset unless a layer above defines it.

**R-4 (one script definition per key).** A key MUST have at most one script definition; a second one, of any kind and
in any order, fails the launch with E-2: two rules, two constant `var` ops, a constant and a rule, an
`ExecOptions.Vars` entry and a `var` op of either form, two `ExecOptions.Vars` entries; also a `varschema` secret
field against a rule (X-6). Repeated `-d` (last wins) and `?vars` are not script definitions and stay allowed (V-2).
The constant op no longer removes an earlier entry (`XakeScriptBuilder.fs:156-157,225`); how definition sites are
kept until launch is an implementation choice.

### 3.3 `Var.*` and `varschema` (3.13, kept)

**Y-1 (typed reader).** A read `let! x = v` behaves as in 3.13 (`Vars.fs:110-149`), on the flattened set
(`ExecContext.Vars`, §5):
- `ArgAndEnv`/`ArgOnly`: `getVar key` first, recording `Var (key, value)`; so `-d`, a script value, a rule and
  `?vars` of the same key win, and the read is value-based (K-5);
- then, unless `ArgOnly`: `getEnv envName`, live, recording `EnvVar`; `EnvOnly` (`Var.env`, `Var.secret`) reads only
  this, never the set;
- then its own `withDefault` (not recorded); `required` without a value fails the reading target with
  `Required variable 'N' is not provided` (`Vars.fs:148`); conversion failures fail the reading target with the 3.13
  texts (`Vars.fs:41,43`).

**Y-2 (`varschema`).** `varschema` only feeds `--help` (B-2) and declares its secret fields in the secrets registry
when the op is applied (`Vars.fs:188-189`), as in 3.13. It defines no key and adds nothing to the set or the banner;
its `withDefault` is invisible to `getVar` and the banner. A non-secret field and `var "X" [...]` of the same key are
no conflict: the rule plus a typed reader of it. A secret field against a rule is E-2 (X-6).

**Y-3.** `Vars.fs` is not changed, its read-time secret check (`Vars.fs:117-124`) included. The checks of X-6 live
in `Run`/`Start`.

### 3.4 Reads and dependencies

**K-1 (`getVar`).** `getVar "N"` returns the set's value of `N` and records one `Var (N, value)` (`SecretDep` for a
secret, X-4). A key with a rule returns the rule's value, its `Default` included.

**K-4.** One read records one dependency per key per recipe, whatever the number of reads.

**K-5 (value, not source).** At the next run `Var (N, v)` is up to date iff the set's value of `N` equals `v`
(`DependencyAnalysis.fs:100`, unchanged). The origin is never recorded: moving an equal value between the environment
(through a rule), `-d`, `var` and a `Default` does not rebuild. No `EnvVar` is recorded for a rule's `Env` source.
This holds for `getVar` and for typed readers whose key is in the set; a typed reader's own environment fallback stays
per source (Y-1, §6.1).

**K-6.** `EnvVar` records (`getEnv`, typed readers' fallback) are checked against the live environment as today
(`DependencyAnalysis.fs:97`).

### 3.5 Secrets

**X-1.** A key is secret iff its rule has `Secret`/`SecretHashed`, or a typed reader declares it (Y-2, 3.13). At
launch Xake declares each rule secret's key and its `Env` names in the `Secrets` registry and registers its value
with `SecretMask` before anything is logged, the debug options dump and the banner included.

**X-2 (own sources only).** A rule secret takes its value only from its rule. At launch a `-d` of the key or of any of
its `Env` names fails with E-1a (E-1b when the rule has no `Env` source); a script value for the key is a second
definition (E-2); a request value fails the demand (G-4). The offending value is registered for masking first.

**X-3.** A secret's value is `***` in the banner, the debug options dump and every log line (`MaskingLogger`,
unchanged); `--help` shows no default for it.

**X-4.** A read of a secret records `SecretDep (key, source, digest)` with the 3.13 digest of its mode (presence:
set/unset; hashed: salted HMAC), never the value, checked against the set (`SecretArg`) or the live environment
(`SecretEnv`) (`DependencyAnalysis.fs:88-93`, unchanged).

**X-5 (typed-reader secrets, 3.13).** The 3.13 checks stay: an `EnvOnly` secret (`Var.secret`, `Var.env |> secret`)
given by `-d` (its key or env name) fails with E-1a, at launch for a `varschema` field (`checkSecretVars`,
`ScriptRunner.fs:27-52`) and at the read for an inline reader (`Vars.fs:117-124`); an argument-scope secret given by
`-d` logs W-3 and takes the value (a `Var.arg ... |> secret` has no other source). **[owner]** §10.1.

**X-6 (launch checks on inputs; secret field vs rule).** Outside `Var`:
- `checkSecretVars` looks only at input values (`-d`, `ExecOptions.Vars`, constant `var` ops; `?vars` in a demand),
  never at values a rule put into the set;
- a `varschema` secret field whose key or environment name is the name of a rule fails the launch with E-2.
An inline `Var.secret`/`Var.env |> secret` (not in `varschema`) whose key or env name is a rule's name cannot be seen
at launch: its 3.13 read-time check finds the rule's value in `ctx.Vars` and fails the reading target with E-1a.
Known limitation, documented (§6.1), pinned by test 29.

**X-7 (reads are not refused).** `getVar` and `getEnv` of a secret return the real value (rev. 3 E-7 is dropped).
Masking applies to output only; the dependency is presence or HMAC per mode (X-4).

### 3.6 Libraries

**L-1.** Xake.Dotnet and Xake.Hermetic.Dotnet read build variables only from the variable set: `getVar` for tracked
reads, `ctx.Vars` for untracked ones. They MUST NOT read the process environment for a variable of L-2 (no `getEnv`,
no `GetEnvironmentVariable`). An unset name takes the library's fixed default. Libraries declare nothing: the script
owns the rule for each name.

**L-2 (affected variables).** "set" = the variable set (`-d`, `var`, `?vars`, a rule).

| Name | 3.13 / 3.14-pre | 3.14 | A CI that relied on the environment adds |
|---|---|---|---|
| `NUGET_PACKAGES` | set, else env (raw, untracked, `DotNetFwk.fs:341,790`), else `~/.nuget/packages` | set (relative to the project root, or absolute), else `<project root>/.packages` (L-4) | `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]` |
| `NUGET_FETCH` | set, default on; any unknown value is on (`DotNetFwk.fs:888`) | set, default on; strict switch (L-6) | - |
| `NETFX`, `NETFX-TARGET`, `CSC_TOOLSET`, `FSCVER`, `FSHARP_CORE_VERSION`, `NETSTANDARD_LIBRARY_VERSION`, `NETSTANDARD_LIBRARY_REF_VERSION`, `NETFX_REFERENCE_ASSEMBLIES_VERSION`, `NETCORE_REF_VERSION` | set only | unchanged | - |
| `HERMETIC` | set only, default off, strict (`HermeticMode.fs:59-66`) | unchanged | - |
| `CSC_SERVER` / `XAKE_CSC_SERVER` | `CSC_SERVER` from the set (`on`/`off`/seconds, else warning and server, `Csc.fs:44-63`), else env `XAKE_CSC_SERVER` (lenient, `Csc.fs:31-35`), else server | `CSC_SERVER` from the set (L-6), else `XAKE_CSC_SERVER` **from the set** (strict switch), else server | `var "XAKE_CSC_SERVER" [Env "XAKE_CSC_SERVER"]` or `-d XAKE_CSC_SERVER=0` |
| `CSC_JOBS` / `XAKE_CSC_JOBS`, `FSC_JOBS` / `XAKE_FSC_JOBS` | `*_JOBS` from `ctx.Vars`, else env `XAKE_*_JOBS` (`CompilerRunner.fs:138-150`); untracked; invalid warns | `*_JOBS` from `ctx.Vars` only, untracked; invalid warns (a limit, not a switch) | `var "CSC_JOBS" [Env "XAKE_CSC_JOBS"]` (likewise FSC) |
| `CI` (hermetic) | set (strict), else env (lenient, `getEnv`, `Lock.fs:1344-1354`) | set only, strict (L-6), empty or unset = off | `var "CI" [Env "CI"]` |
| `LOCK_REFRESH` (hermetic) | set, else env (`Locks.fs:106-110`); blank = unset | set only; blank = unset | `var "LOCK_REFRESH" [Env "LOCK_REFRESH"]` |
| `LOCK_STALE` (hermetic) | set only, default fail (`Locks.fs:498-505`) | unchanged; empty = fail (L-7) | - |
| `SOURCE_DATE_EPOCH` (hermetic) | env only, raw (`Pack.fs:44`, `Sbom.fs:596`) | set only; recipe-level callers pass it; context-free defaults use the 1980 epoch; empty or unparsable = 1980 epoch (3.13) | `var "SOURCE_DATE_EPOCH" [Env "SOURCE_DATE_EPOCH"]` |

**L-3 (child processes).** A library that starts `dotnet restore`/`msbuild` passes the resolved package folder as
`NUGET_PACKAGES=<folder>` to the child in every case, so an environment value cannot redirect it (today
`Import.fs:1684` does so only for a script variable). `DotNetFwk.nugetRoot ()` and `Roots.nugetRoot ()` (context-free,
no build) return `~/.nuget/packages` without reading the environment.

**L-4 (package folder and hermetic policy).** The package folder is the set's `NUGET_PACKAGES`, trimmed, resolved
against the project root (3.13 `packageRoot`, `DotNetFwk.fs:755-761`); a blank value counts as unset (rev. 4); unset
is `<project root>/.packages` (the base of rev. 3 D-8). `packageRootWithSource` returns `ScriptVariable` when
`NUGET_PACKAGES` is defined and not blank in the set, whatever its origin (`env:` included: the script chose that
rule), and `ProjectDefault` (new case) otherwise. `HERMETIC=on` accepts both: each is a folder of the build's own.
`EnvironmentVariable` and `UserProfile` are no longer produced (kept, `[<Obsolete>]`); the refusal message of
`HermeticMode.fs:155` stays for a `Roots` value built by hand with `PackageRootDeclared = false`. **[owner]** §10.2.

**L-5.** `Csc.RunOptions.Default.Server` becomes `Shared None` and no longer reads `XAKE_CSC_SERVER` (`Csc.fs:166`).
In a recipe the server is resolved from the set (L-2); a direct `Csc.run` outside a recipe sets `Server` itself.

**L-6 (invalid switch values fail).** A switch value outside its set, from any origin, fails the reading target with
E-5; nothing guesses or only warns. Values are trimmed and case-insensitive. `HERMETIC`, `NUGET_FETCH`, `CI`,
`XAKE_CSC_SERVER`: `on`/`true`/`yes`/`1`, `off`/`false`/`no`/`0`. `CSC_SERVER`: `on`, `off` or a non-negative integer
(keepalive seconds). `LOCK_STALE`: `fail`, `warn`. Libraries declare nothing, so the check is at the read.

**L-7 (empty switch = unset).** An empty or blank switch value from any origin (`-d CI=`, `var "CI" ""`, a rule whose
`Env` is present and empty) is treated as unset: `HERMETIC` off, `NUGET_FETCH` on, `CI` off, `CSC_SERVER` falls to
`XAKE_CSC_SERVER`, `XAKE_CSC_SERVER` server, `LOCK_STALE` fail. The dependency records the value as read (`""`).

### 3.7 Banner, help, debug dump

**B-1 (banner).** The `vars:` section prints one line per key of the set, sorted ordinally: `    NAME=value (origin)`
with origin `cli`, `script`, `env:<NAME>` or `default`; a secret as `***`; a key with a rule and no value as
`    NAME (not set)`. Each name once (no shadowed entries); `varschema` fields only through a set key. The 3.13 label
`(script/cli)` goes. The engine prints no banner; the `request` origin is kept in the set for demands.

**B-2 (help).** `--help` prints "Script variables:": the `varschema` fields in the 3.13 format
(`XakeScriptBuilder.fs:122-144`), then each rule as `  -d N=<value> [string], env: X, Y, default: d`, with `(secret)`
and no default for a secret. Constant `var` ops are not listed. `--help` exits before launch: no environment read, no
launch error.

**B-3 (debug options dump).** The `Options: %A` dump at `Debug` (`ScriptRunner.fs:207-208`) runs after launch and
shows `Vars` and `CliVars` with their values, `***` for a secret name or a value registered with `SecretMask`;
`VarRules` with a secret rule's `Default` as `***`; `VarSchema` without secret defaults (3.13). No more `Vars = []`.

### 3.8 Engine mode

**G-1.** `XakeEngine.Start` builds the base set once, reading the environment once; later environment changes are not
seen by the set until a new engine starts. `getEnv` and a typed reader's fallback stay live (N-3, Y-1).

**G-2 (request on top).** `Demand(target, ?vars)` evaluates an overlay: `?vars` is layer 1 over the base set (V-2),
origin `request`. `makeCtx` puts the request pairs first (`XakeScriptBuilder.fs:299` appends them today); the test
`engine per-demand vars come after the script's vars` (`CommandLineTests.fs:548`) is inverted.

**G-3.** Overlays are per demand: concurrent demands do not see each other's `?vars`; a demand without `?vars` uses
the base set; a demand's dependency check uses its overlay.

**G-4.** A `?vars` pair for a rule secret fails that demand with E-1b before any target runs. `Start` raises
`XakeException` on any launch error (E-1a, E-1b, E-2).

## 4. Diagnostics

Launch errors are collected, printed together at `Error`, and fail the run with exit code 2 before `setup` and any
target (`XakeException` under `ThrowOnError`), as the 3.13 secret errors do (`ScriptRunner.fs:226-230`); in engine
mode they fail `Start` or the demand (G-4). No launch error prints a value.

- **E-1a** (3.13 text, `Types.fs:206`), `-d` of a secret with an environment source; `GIVEN` the name given to `-d`,
  `ENV` the first `Env` source (typed reader: its env name):
  `Variable 'GIVEN' is secret: secret variables come from the environment, set ENV instead of passing -d GIVEN=...`
- **E-1b**, any other input value for a rule secret, `WHERE` = `-d N=...` (no `Env` source) or `the ?vars of Demand`:
  `Variable 'N' is secret: its value comes only from its declaration's sources, not from WHERE`
- **E-2** (R-4, X-6) `Variable 'N' is defined twice in the script: by A and by B; define it once`; `A`, `B` the first
  two definitions in script order (`ExecOptions.Vars` first), each `ExecOptions.Vars`, `var "N" "..."`,
  `var "N" [...]` or `varschema secret field F`. One error per key.
- **E-5** (L-6; at the read, fails the reading target) `NAME='v': expected ALLOWED`, `ALLOWED` = `on or off`
  (`HERMETIC`, `NUGET_FETCH`, `CI`, `XAKE_CSC_SERVER`), `on, off or a number of seconds` (`CSC_SERVER`), `fail or
  warn` (`LOCK_STALE`); the `HERMETIC`, `CI`, `LOCK_STALE` texts are 3.13 (`HermeticMode.fs:66`, `Lock.fs:1347`,
  `Locks.fs:505`).
- **W-3** (3.13 text, `ScriptRunner.fs:47`, X-5)
  `Secret variable 'N' is given by -d: its value is visible in the process list and the shell history[; prefer the environment variable E]`
- **C-1** A `Var` change reason stays `Global script variable N was changed 'a'->'b'` (`DependencyAnalysis.fs:101`);
  secrets keep the 3.13 masked reasons. Typed readers' read-time errors keep their 3.13 texts (Y-1).

## 5. Data shapes

```fsharp
// namespace Xake, auto-opened
type VarSource =                    // a source of a `var` rule, tried in list order; markers define nothing
    | Env of string                 // environment variable, read once at launch
    | Default of string             // constant
    | Secret                        // presence secret (3.13 SecretPresence)
    | SecretHashed                  // HMAC secret (3.13 SecretHash)
[<RequireQualifiedAccess>]
type VarOrigin = Request | Cli | Script | Env of string | Default   // request | cli | script | env:<NAME> | default
type VarEntry = { Value: string option; Origin: VarOrigin option; Secret: SecretMode; HasRule: bool }
type VarRule = { Name: string; Sources: VarSource list }
// RulesBuilder
[<CustomOperation("var")>] member AddVar(script, name: string, value: string)              // unchanged
[<CustomOperation("var")>] member AddVar(script, name: string, sources: VarSource list)   // new
// Xake.Dotnet, DotNetFwk
type PackageRootSource = ScriptVariable | ProjectDefault (*new*) | EnvironmentVariable (*obsolete*) | UserProfile (*obsolete*)
```

`VarSource.Env` coexists with the `Xake.Env` module (`Env.isUnix` still resolves; checked with `dotnet fsi`); the
overloaded custom operation compiles on SDK 10 (owner's check). Records:
- `ExecOptions.Vars` (type unchanged): only script values (`ExecOptions.Vars` entries, constant `var` ops),
  duplicates kept for E-2.
- `ExecOptions.CliVars : (string * string) list` (new): the `-d` pairs, last first; `ApplyCommandLine` writes `-d`
  here instead of prepending to `Vars`, which keeps `cli` apart from `script`.
- `ExecOptions.VarRules : VarRule list` (new), script order. `VarSchema` stays for `--help` (`VarHelp` unchanged).
- `ExecContext.Vars` (type unchanged): the set flattened, one pair per defined key with its final value (request
  pairs first in a demand). `Util.getVar`, typed readers, the dependency check, `EXPLAIN_RULE`, `jobsLimit` use it.
- `ExecContext.VarSet : Map<string, VarEntry>` (new): origins and secret modes, for the banner and L-4.

Source compatibility: `{ X.Default with ... }` compiles unchanged; a full record expression adds the new fields; code
that looked for `-d` values in `options.Vars` after `ApplyCommandLine` reads `CliVars`. `getVar`, `var "N" "v"`,
`Var.*`, `varschema`, `withDefault`, `required`, `secret`, `secretHashed` keep signatures and behaviour. An
exhaustive match on `PackageRootSource` gets a new case. No database change (N-2).

## 6. Compatibility

### 6.1 `Var.*` and `varschema`: 3.13 behaviour kept

Documented as is (doc page, XML docs), with the line: **"For a value evaluated at launch and value-based
dependencies, use `var [...]`."**
- A typed reader reads the set first: a rule, `-d`, `var` and `?vars` of its key win, value-based (K-5).
- Without a set value it reads its own environment variable **live**, at the read: an environment change after launch
  (in-process, or in an engine after `Start`) is seen by it, not by rules.
- Its dependencies are then per source, `Var (key, None)` + `EnvVar (env, v)`: moving an equal value between the
  environment and `-d` rebuilds once.
- Required and conversion errors fire at the read, in the reading target, after other targets may have run.
- `Var.env`/`Var.secret` (`EnvOnly`) ignore the set: `-d`, `var` and rule values never reach them.
- An inline `Var.secret` not in `varschema` given by `-d` fails when its target runs, not at launch.
- **Do not read a rule-defined secret through `Var.secret`; use `getVar`.** An inline `Var.secret` whose key or env
  name is a `var "T" [Secret; ...]` rule's name fails its target with the 3.13 `-d` message (E-1a), because the rule's
  value is in the set (X-6); in `varschema` it is E-2 at launch.
- Key mismatch: the key is `cliArg`/`name`/field name verbatim, the env name upper-snaked. `Var.string
  "nugetPackages"` reads `getVar "nugetPackages"` (absent), then env `NUGET_PACKAGES`, and never sees the rule
  `var "NUGET_PACKAGES" [...]`. Use the rule's key (`Var.string "NUGET_PACKAGES"`).
- A `varschema` `withDefault` is shown by `--help` but invisible to `getVar` and the banner; a field shows in the
  banner only when the set has its key. For both, write `var "N" [Env "E"; Default "d"]` and keep the field for its
  type and help text.

### 6.2 Release note (3.14)

> **Variables are evaluated once, at launch, by rules the script states.**
> - New: `var "N" [Env "X"; Default "v"]`, sources tried in order; `Secret` / `SecretHashed` make it a secret.
> - Precedence: engine `?vars` (now above the script) > `-d` (last wins) > `var "N" "v"` / `ExecOptions.Vars` > rule.
>   Moving an equal value between the environment (through a rule), `-d` and `var` no longer rebuilds.
> - **Breaking:** a name defined twice in the script fails the launch (3.14-pre: the `var` op won). A rule secret
>   accepts no value from `-d` or `?vars`. `Var.*` and `varschema` are unchanged (use `var [...]` for launch-time
>   values and value-based dependencies).
> - **Breaking:** Xake.Dotnet no longer reads `NUGET_PACKAGES`, `XAKE_CSC_SERVER`, `XAKE_CSC_JOBS`, `XAKE_FSC_JOBS`
>   from the environment (add `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]` or pass `-d`); unset `NUGET_PACKAGES` is
>   `<project root>/.packages`; an invalid `CSC_SERVER`, `NUGET_FETCH`, `XAKE_CSC_SERVER` is an error; an empty
>   on/off value is the default (`-d CI=` is off).
> - The banner shows each variable once with its source; `--help` lists rules; `-v` shows the variables, masked.
> - Xake.Hermetic.Dotnet 0.9 (on Xake 3.14) no longer reads `CI`, `LOCK_REFRESH`, `SOURCE_DATE_EPOCH` from the
>   environment: add `var "CI" [Env "CI"]` and the like on CI.

## 7. Implementation split

1. **PR 1, core (Xake 3.14)**: the types of §5; the `var` overload; `CliVars`; launch evaluation in `Run`/`Start`
   (V, R, X-1, X-2, X-6, E-2) with `checkSecretVars` on inputs; `Vars.fs` untouched; B-1, B-2, B-3; engine overlay
   G-1..G-4; tests `CommandLineTests.fs:472,548` updated. Tests §9.1-9.6.
2. **PR 2, Xake.Dotnet readers** (same `Xake` package, ships with PR 1): L-1..L-7 for `NUGET_PACKAGES`,
   `NUGET_FETCH`, `HERMETIC`, the `CSC_SERVER` pair and `*_JOBS`; `ProjectDefault` and L-4; docs (`csc-server.md`,
   the `HermeticMode.fs:155` hint). Tests §9.7.
3. **PR 3, Xake.Hermetic.Dotnet 0.9** on Xake `[3.14, 3.15)` (`src/hermetic/*.fsproj:84`): `CI`, `LOCK_REFRESH`,
   `LOCK_STALE`, `SOURCE_DATE_EPOCH` per L-2/L-6/L-7, `Import.fs:1684` per L-3, docs. Tests §9.8.

## 8. Engine summary

`Start`: ApplyCommandLine → base set (env read once) → launch errors fail `Start` → setup. `Demand`: overlay (G-2-G-4).

## 9. Conformance tests

One line each: given / when / then (rules). "Launch fails": error printed, exit 2, neither `setup` nor any target runs.

### 9.1 Chain, `var` lists, second definitions
1. Given `var "X" [Env "A"; Default "d"]`; when a recipe reads `getVar "X"` with `A=a` (resp. `A` unset, resp. `A=` on Unix); then `Some "a"`, banner `X=a (env:A)` (resp. `X=d (default)`, resp. `Some ""`) (R-2, V-2, V-4, K-1).
2. Given `var "X" [Env "A"; Env "B"; Default "d"]`, `A` unset, `B=b`; then `b (env:B)`; with also `-d x=1`, `getVar "X"` is still `b` and `getVar "x"` is `1` (V-2, V-5).
3. Given `var "X" [Env "A"]`, `A=a` and `-d X=c` (resp. `-d X=1 -d X=2`, resp. `-d X=c` and `var "X" "s"`); then `c (cli)` (resp. `2 (cli)`, `c (cli)`), no error (V-2, R-1, R-4).
4. Given `ExecOptions.Vars = [X,a]` and `var "X" "b"`; then launch fails with E-2 `by ExecOptions.Vars and by var "X" "..."` (R-4).
5. Given `var "X" [Default "d"]` and `var "X" "s"` in either order (resp. two rules, two constant `var "X"`, `ExecOptions.Vars = [X,a; X,b]`); then launch fails with E-2 naming the first two in script order (R-4).
6. Given second definitions of `X` and of `Y`, one with a secret value; then both E-2 are printed together and no value is printed (R-4, §4).
7. Given `var "X" [Env "A"]`, `A` unset; then `getVar "X"` = `None`, banner `X (not set)` (R-3, B-1).
8. Given `var "X" [Env "A"]`, `A=a` at launch; when a recipe sets `A=z` in the process and another reads `getVar "X"`; then `a` (V-3).

### 9.2 `Var.*` and `varschema` (3.13 kept)
9. Given `var "X" [Env "A"; Default "d"]`, `A` unset; when a recipe reads `let! x = Var.string "X"`; then `Some "d"`, recorded `Var (X, Some "d")`, no `EnvVar` (Y-1, K-5).
10. Given no definition of `X`, env `X=a`; when a recipe reads `Var.string "X"`; then `a`, recorded `Var (X, None)` and `EnvVar (X, Some "a")` (Y-1).
11. Given `var "NUGET_PACKAGES" [Default ".packages"]`, env `NUGET_PACKAGES=/m`; when a recipe reads `Var.string "nugetPackages"`; then `/m` (Y-1, §6.1 key mismatch).
12. Given `Var.env(envVar = "A")` as field `X`, `A=a` and `-d X=c`; then the read gives `a` (Y-1).
13. Given `varschema {| X = Var.string() |> withDefault "d" |}`, nothing set; then `getVar "X"` = `None`, `--help` shows default `d`, the banner has no `X` line (Y-2, B-1).
14. Given a `varschema` field `X` and `var "X" [Default "e"]`; then no error, `let! x = schema.X` = `e`, `getVar "X"` = `Some "e"` (Y-2, R-4).
15. Given `Var.int "N"` in `varschema` and `-d N=x` (resp. `Var.string "R" |> required`, `R` unset); then the launch succeeds, other targets run, and the reading target fails with the 3.13 conversion text (resp. `Required variable 'R' is not provided`) (Y-1).
16. Given `--help` and an unset `required` field; then help is printed, exit 0, no error (B-2).

### 9.3 Reads and dependencies
17. Given a target built with env `A=v` and `var "X" [Env "A"]` reading `getVar "X"`; when rerun with no `A` and `-d X=v` (resp. `-d X=w`); then up to date (resp. rebuilt with C-1 `'Some "v"'->'Some "w"'`) (K-5, C-1).
18. Given a target reading `var "X" [Env "A"; Default "d"]` with `A` unset; when rerun with `Default "e"`; then rebuilt (K-5).
19. Given a recipe reading `getVar "X"` twice and `let! x = Var.string "X"`; then one `Var (X, _)` is recorded (K-4).
20. Given `getEnv "PATH"` in a recipe; then `EnvVar (PATH, _)` recorded and checked live (N-3, K-6).
21. Given a target that read `Var.string "X"` with env `X=a` only; when rerun with no env and `-d X=a`; then rebuilt once (Y-1, §6.1).

### 9.4 Secrets
22. Given `var "T" [Secret; Env "A"]` and `-d T=s` (resp. `-d A=s`); then launch fails with E-1a, `GIVEN` = `T` (resp. `A`), `ENV` = `A`, `s` masked in all output (X-2).
23. Given `var "T" [Secret; Default "x"]` and `-d T=s`; then E-1b `-d T=...` (X-2).
24. Given `var "T" [Secret; Env "A"]` and `var "T" "s"` (resp. `ExecOptions.Vars [T,s]`); then E-2, `s` masked (X-2, R-4).
25. Given `var "T" [Secret; Env "A"]`, `A=s`; then banner `T=*** (env:A)`, `s` in no log line, db holds `SecretDep (T, SecretArg, SecretPresent)` (X-1, X-3, X-4).
26. Given `[SecretHashed; Env "A"]` and a changed `A`; then rebuilt with the masked reason; with `[Secret; Env "A"]` up to date; with `A` unset, rebuilt (X-4).
27. Given `[Secret; SecretHashed; Env "A"]`; then the mode is HMAC (R-2).
28. Given `var "T" [Secret; Default "x"]`; then `--help` lists `T` with `(secret)` and no default (X-3, B-2).
29. Given `var "T" [Secret; Env "A"]`, `A=s`, and an inline `Var.secret(envVar = "A")` under key `T`; then the launch succeeds and the reading target fails with E-1a, `s` masked (X-6, Y-3; pins the 3.13 limitation of §6.1).
30. Given `var "T" [Secret; Env "A"]` and `varschema {| T = Var.secret(envVar = "B") |}` (resp. field `U` with `envVar = "T"`); then launch fails with E-2 `by var "T" [...] and by varschema secret field T` (resp. `U`) (X-6).
31. Given `varschema {| T = Var.secret(envVar = "A") |}` and `-d T=s`; then launch fails with E-1a (X-5, 3.13).
32. Given an inline `Var.secret(envVar = "A")` not in `varschema` and `-d A=s`; then the launch succeeds and the reading target fails with E-1a (X-5, §6.1).
33. Given `varschema {| T = Var.string() |> secret |}` and `-d T=s`; then W-3, the read gives `s`, masked (X-5).
34. Given `var "T" [Secret; Env "A"]`, `A=s`; when a recipe calls `getVar "T"` and `getEnv "A"`; then both return `s`, `s` in no log line, `SecretDep` recorded for each (X-7, X-1).

### 9.5 Banner, help, debug dump
35. Given `-d A=1 -d X=1 -d X=2`, `var "B" "2"`, `var "C" [Env "E"]` with `E=3`, `var "D" [Default "4"]`, `var "F" [Env "G"]` with `G` unset; then the vars section is exactly `A=1 (cli)`, `B=2 (script)`, `C=3 (env:E)`, `D=4 (default)`, `F (not set)`, `X=2 (cli)` (B-1).
36. Given `varschema` fields, `var "R" [Env "A"; Env "B"; Default "d"]` and `var "K" "v"`; then `--help` lists the fields, then `-d R=<value> [string], env: A, B, default: d`, and not `K` (B-2).
37. Given `-v Debug`, `-d P=1`, `varschema {| T = Var.string() |> secret |}` with `-d T=s`, and `var "K" [Secret; Default "x"]`; then the dump shows `("P", "1")`, `("T", "***")`, `Default "***"`, and neither `s` nor `x` (B-3, X-3).

### 9.6 Engine
38. Given `xakeEngine { var "V" "s"; ... }`; when `Demand("t", ["V","r"])`, then a plain `Demand("t")`; then `r`, then `s` (G-2; inverts `CommandLineTests.fs:548`).
39. Given two concurrent demands with `["V","r1"]` and `["V","r2"]`; then each sees its own value (G-3).
40. Given `var "T" [Secret; Env "A"]`; when `Demand("t", ["T","x"])`; then the demand fails with E-1b `the ?vars of Demand`, no target runs (G-4).
41. Given `var "X" [Env "A"]`, `A=a` at `Start`; when `A` changes and `Demand` runs; then `getVar "X"` = `a`, while `Var.string(envVar = "A")` of an unset key reads the new value (G-1).
42. Given `xakeEngine { var "X" "a"; var "X" "b" }`; when `start`; then `Start` raises `XakeException` with E-2 (G-4).

### 9.7 Xake.Dotnet
43. Given env `NUGET_PACKAGES=/m`, no `var`/`-d`; then `packageRoot ()` = `<root>/.packages`, the child restore gets `NUGET_PACKAGES=<root>/.packages`, source `ProjectDefault`, `HERMETIC=on` accepts it (L-2, L-3, L-4).
44. Given `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]`, env `/m`; then `/m`, source `ScriptVariable`, `HERMETIC=on` accepts it (L-4).
45. Given `-d NUGET_PACKAGES=` (resp. `-d NUGET_PACKAGES=pkgs`); then `<root>/.packages`, `ProjectDefault` (resp. `<root>/pkgs`, `ScriptVariable`) (L-4).
46. Given env `XAKE_CSC_SERVER=0` only; then the compiler server is used; with `-d XAKE_CSC_SERVER=0`, in-process; with also `-d CSC_SERVER=30`, keepalive 30 (L-2).
47. Given `-d CSC_SERVER=bogus` (resp. `var "CSC_SERVER" [Env "S"]` with `S=bogus`, resp. `-d XAKE_CSC_SERVER=maybe`); then the compiling target fails with E-5 `CSC_SERVER='bogus': expected on, off or a number of seconds` (resp. same, resp. `expected on or off`) (L-6).
48. Given `-d CSC_SERVER=` and `-d XAKE_CSC_SERVER=0`; then in-process; given `-d XAKE_CSC_SERVER=` only, the server (L-7).
49. Given `-d NUGET_FETCH=bogus` (resp. `-d HERMETIC=bogus`); then E-5 at the read (resp. the 3.13 text); given `-d NUGET_FETCH=` (resp. `-d HERMETIC=`), fetching on (resp. off) (L-6, L-7).
50. Given env `XAKE_CSC_JOBS=2` only; then no limit; with `var "CSC_JOBS" [Env "XAKE_CSC_JOBS"]`, limit 2 and no dependency recorded; with `-d CSC_JOBS=x`, the 3.13 warning and no limit (L-2).
51. Given env `XAKE_CSC_SERVER=0`; then `Csc.RunOptions.Default.Server = Shared None` (L-5).
52. Given the sources of `src/dotnet`; then no `getEnv` or `GetEnvironmentVariable` names a variable of L-2 (L-1; a source scan).

### 9.8 Xake.Hermetic.Dotnet
53. Given env `CI=true`, no `var`; then `underCi` = false; with `var "CI" [Env "CI"]`, true; with `-d CI=bogus`, E-5 (L-2, L-6).
54. Given `-d CI=` (resp. `var "CI" [Env "CI"]` with env `CI=` on Unix, resp. `var "CI" " "`); then `underCi` = false, no error (L-7).
55. Given a stale lock and `-d LOCK_STALE=`; then it fails as with `fail`; with `-d LOCK_STALE=x`, the 3.13 E-5 text (L-6, L-7).
56. Given env `LOCK_REFRESH=all` only; then no refresh; with `var "LOCK_REFRESH" [Env "LOCK_REFRESH"]`, refresh (L-2).
57. Given env `SOURCE_DATE_EPOCH=1700000000` only; then pack and SBOM use 1980-01-01; with `var "SOURCE_DATE_EPOCH" [Env "SOURCE_DATE_EPOCH"]`, 2023-11-14 (L-2).
58. Given the sources of `src/hermetic`; then no `getEnv` or `GetEnvironmentVariable` names a variable of L-2 (L-1).

## 10. Points for the owner

1. **X-5, argument-scope secret given by `-d`.** spec4-trace.md Task 2 kept rev. 4's error; this revision keeps the 3.13
   warning (W-3), because `Var.*` stays as in 3.13 and a `Var.arg ... |> secret` has no source but `-d`: an error would
   make it unusable. Rule secrets do refuse `-d` (X-2). Confirm, or choose the error.
2. **L-4, blank `NUGET_PACKAGES`.** As in rev. 4, blank counts as unset, so `-d NUGET_PACKAGES=` gives
   `<root>/.packages`; inside a recipe the machine folder is then chosen only by an explicit path, and `HERMETIC=on`
   never refuses a package folder. Alternative (rev. 3 P-2): blank means `~/.nuget/packages` (`UserProfile`), refused
   by `HERMETIC=on`. Confirm.
