# Spec: variable evaluation

Status: rev. 4 for the owner's approval, 2026-10-28. It replaces rev. 3 (rejected: lazy evaluation,
`defaulted` flag, database 0.7). It formalizes [concept.md](concept.md) §8 (variant 3) and the
owner's decisions of 2026-10-28, which are not to be reopened. The 3.13 behaviour is in
[analysis.md](analysis.md); the state after #103 is in [after-103.md](after-103.md). File:line
references are to this branch (dev with #103). Keywords MUST, MUST NOT, SHOULD, MAY are normative.
Every rule has an id; the conformance tests of §9 reference them. Points the owner still has to
confirm are marked **[owner]** and collected in §10. No code before this spec is approved and the
tests of §9 exist.

## 1. Scope and non-goals

In scope: how a build variable gets its value (the chain), the `var` source-list overload, how
`varschema` evaluates `Var.*` declarations, secrets, what is recorded as a dependency, the banner and
`--help`, engine mode, and how Xake.Dotnet and Xake.Hermetic.Dotnet read their variables.

Non-goals:
- **N-1** No lazy evaluation and no declaration during the run. Every value is computed at launch.
- **N-2** No database format change. The `Var`, `EnvVar` and `SecretDep` cases stay as in 3.13
  (database 0.6).
- **N-3** `getEnv` is unchanged: an explicit, live read of the process environment by a script
  recipe that records `EnvVar`. It is not a variable read and libraries do not use it for variables.
- **N-4** The environment used for tool discovery is not a build variable and is out of scope:
  `PATH`, `DOTNET_ROOT`, `DOTNET_HOST_PATH`, `ProgramFiles` (`DotNetFwk.fs:128,181,410-424`).
- **N-5** The options precedence (`-ll`/`-fl`/`-fll`/`-r`/targets over `consolelog`/`filelog`/
  `rootdir`/`want`) landed in #103 and is not changed here.
- **N-6** No warning for unused `-d` names or unused engine `?vars`.

## 2. Definitions

- **Input values**: name/value pairs given from outside a declaration: engine `?vars` (request),
  `-d` (cli), `ExecOptions.Vars` and `var "N" "v"` (script). `ExecOptions.Vars` stays the input
  layer; it is not a declaration.
- **Declaration**: the rule that computes a name from sources: a `var "N" [sources]` op, or a
  `Var.*` value handed to `varschema`. `Var.*` is the typed spelling of the same rule plus help text.
- **Source**: an element of a declaration's list: `Env "X"` (environment variable `X`), `Default "v"`
  (the constant `v`). The markers `Secret` and `SecretHashed` are list elements too, but they define
  nothing; they set the secret mode.
- **Key**: the name a variable is stored and read under. For `var` it is the op's name. For a
  `Var.*` declaration it is `cliArg`, else `name`, else the `varschema` field name (the 3.13
  `resolvedCliName`, `Vars.fs:111-112`).
- **Launch**: in `Run` (run, dry-run, dump), after `ApplyCommandLine` and after the `--help` exit,
  before the banner, `setup` and any target; in `XakeEngine.Start`, before `setup`.
- **Variable set**: the immutable map `key -> entry` built at launch (V-1). An entry holds the
  value (or none), its origin, its secret mode and whether a declaration exists for the key.
- **Origin**: where the winning value came from: `request`, `cli`, `script`, `env:<NAME>`,
  `default`.

## 3. Rules

### 3.1 The variable set and the chain

**V-1.** At launch Xake builds the variable set once. It holds every key that appears in `-d`,
`ExecOptions.Vars`, a `var` op or a `varschema` declaration. A key that no input names is absent,
and a read of it gives `None`. The set does not change during the run; in engine mode it lives as
long as the engine, with a per-demand overlay (G-2).

**V-2 (chain, first defined wins).** For a key `N` the value is taken from the first layer that
defines `N`, and no later layer is consulted:
1. request: the engine's `?vars` of the demand, the last pair for `N` wins;
2. cli: `-d N=v`, the last `-d` of `N` wins;
3. script: `ExecOptions.Vars`, then the constant `var "N" "v"` ops in script order, the last wins;
4. the declaration of `N`: its sources in list order.

**V-3 (environment read once).** Each `Env "X"` source is read from the process environment once,
at launch, and only when the layers above it have not defined the key. No variable reads the
environment after launch.

**V-4 (definedness).** `-d N=` and `var "N" ""` define `N` as the empty string. `Env "X"` defines
iff `X` is present in the environment, an empty value included (Windows has no empty variables).
`Default "v"` always defines. The evaluator neither trims nor interprets values; readers do.

**V-5.** Keys are compared ordinally (case-sensitive), as `Util.getVar` does today. The
case-insensitive `-d EXPLAIN_RULE` (`ExecCore.fs:208-210`) is unchanged.

### 3.2 `var` with a source list

**R-1.** `var "N" "v"` is unchanged: a script value, layer 3.

**R-2.** `var "N" [sources]` declares `N`; the list is layer 4. The sources are `Env "X"` and
`Default "v"`, tried in list order. `Secret` makes `N` a presence secret and `SecretHashed` an HMAC
secret (the 3.13 modes); a marker may stand anywhere in the list. With both markers, `SecretHashed`
wins (the 3.13 `Secrets.declare` rule).

**R-3.** The list is evaluated at launch (V-3), once, and its result is the value of layer 4.
A list that has no source that defines (no `Env` present, no `Default`) leaves `N` unset unless a
layer above defines it.

**R-4.** A later `var "N" [...]` replaces an earlier one. A constant `var "N" "v"` is layer 3, so it
wins over `var "N" [...]` whatever their order in the script.

### 3.3 `varschema`

**S-1 (a declaration is a source list).** `varschema` evaluates each `Var.*` field at launch exactly
as `var KEY [sources]`, where the sources are:
- `Env E` when the scope is not `ArgOnly`, with `E` = `envVar`, else the 3.13 derivation
  `toEnvName (name, else the field name)` (`Vars.fs:56-57,113`);
- then `Default d` when the field has `withDefault x`, with `d` the invariant string form of `x`;
- `Secret` / `SecretHashed` per the field's secret mode.
So `Var.string(envVar = "X") |> withDefault "d"` in `varschema` gives the same value, origin and
dependency as `var "N" [Env "X"; Default "d"]`. `EnvOnly` and `ArgAndEnv` evaluate alike; the scope
only shapes the `--help` header. `-d`, `var "N" "v"` and request values stand above the declaration
for every scope (V-2).

**S-2 (required).** A `required` field without `withDefault` whose key has no value after V-2 fails
the launch with E-3.

**S-3 (types).** At launch the value of a typed field (`int`, `bool`, any `'t`) is converted with
the 3.13 conversion (`Convert.ChangeType`, `Vars.fs:47-53`); a failure fails the launch with E-4.
A read whose entry has origin `default` returns the field's own typed default without parsing.

**S-4 (one declaration per key).** A key declared by `varschema` and by `var "N" [...]`, or by two
`varschema` fields (in one or several `varschema` ops), fails the launch with E-2.

**S-5 (constant plus declaration). [owner]** A constant `var "N" "v"` (or an `ExecOptions.Vars`
entry) for a key that `varschema` declares is not a conflict: it is the script value, layer 3, and it
wins over the declaration's sources. The declaration still applies its type check (S-3), its secret
mode (X-2) and its help text. Rationale: the 3.13 test "Var.string reads the -d value when the script
also has var" (`VarsTests.fs:579`) pins exactly this pairing, and a constant has no sources that
could disagree with the declaration's.

### 3.4 Reads and dependencies

**K-1 (`getVar`).** `getVar "N"` returns the set's value of `N` and records one `Var (N, value)`
(`SecretDep` for a secret, X-4). A declared key returns its evaluated value, its default included
(this removes the 3.13 trap where a `withDefault` was invisible to `getVar`).

**K-2 (declared `Var.*`).** `let! x = decl` (a `varschema` field, or any `Var.*` value whose key is
declared at launch by `varschema` or `var [...]`) is a lookup of the key in the set and records one
`Var (key, value)`. It never calls `getEnv` and never records `EnvVar`.

**K-3 (undeclared `Var.*`). [owner]** A `Var.*` read in a recipe whose key was not declared at launch
takes the set's value for its key (layers 1-3), else its own `withDefault` constant, else `None`
(a `required` field without default then fails the reading target with E-3). It does not read the
environment. If the declaration has an environment source, the read logs W-1 once per key per run.
It records `Var (key, set value)`, so `None` when the default applied: a changed default in the
code does not rebuild, as in 3.13.
Rationale for "set, then own default" over an error: (1) inline `Var.*` reads are common in 3.13
scripts and tests, and they keep working when `-d`/`var` give the value; (2) an error could only fire
at read time, deep in the build, not at launch, so it would not be fail-fast anyway; (3) W-1 names
the one thing that changed (the environment is no longer read) and how to fix it.

**K-4.** One read records one dependency per key per recipe, whatever the number of reads.

**K-5 (value, not source).** At the next run `Var (N, v)` is up to date iff the set's value of `N`
equals `v` (`DependencyAnalysis.fs:100`, unchanged). The origin is never recorded, so moving an equal
value between the environment, `-d`, `var` and a default does not rebuild. No `EnvVar` is recorded
for a declaration's `Env` source.

**K-6.** `EnvVar` records (from `getEnv`, and from 3.13 `Var.*` reads in old databases) are checked
against the live environment as today (`DependencyAnalysis.fs:97`).

### 3.5 Secrets

**X-1.** A key is secret iff its declaration has `Secret`/`SecretHashed` (list) or a secret mode
(`varschema`). At launch Xake declares it in the `Secrets` registry and registers its value with
`SecretMask` before anything is logged, the banner included.

**X-2 (own sources only).** A secret takes its value only from its declaration (layer 4). At launch,
a `-d` of the key or of any of its `Env` names fails with E-1a; a constant `var` or an
`ExecOptions.Vars` entry for the key fails with E-1b. A request value fails the demand (G-4). The
offending value is registered for masking before the error is printed.

**X-3.** A secret's value is `***` in the banner and in every log line (`MaskingLogger`, unchanged);
`--help` shows no default for it.

**X-4.** A read of a secret records `SecretDep (key, SecretArg, digest)` with the 3.13 digest of its
mode (`SecretPresence`: set/unset; `SecretHash`: salted HMAC), never the value. The check compares
it with the set's value (`DependencyAnalysis.fs:88-93`, the `SecretArg` branch, unchanged).

**X-5.** The 3.13 warning for an argument secret given by `-d` (`ScriptRunner.fs:42-47`) goes: X-2
makes it an error.

### 3.6 Libraries

**L-1.** Xake.Dotnet and Xake.Hermetic.Dotnet read build variables only from the variable set:
`getVar` for tracked reads, `ctx.Vars` for untracked ones. They MUST NOT read the process environment
for a variable of the table below (no `getEnv`, no `GetEnvironmentVariable`). An unset name takes the
library's fixed constant default. Libraries declare nothing: the script owns the rule for each name.

**L-2 (affected variables).** "set" means the variable set (`-d`, `var`, `?vars`, a declaration).

| Name | 3.13 / 3.14-pre | 3.14 | A CI that relied on the environment adds |
|---|---|---|---|
| `NUGET_PACKAGES` | set, else env `NUGET_PACKAGES` (raw, untracked, `DotNetFwk.fs:341,790`), else `~/.nuget/packages` | set, else `~/.nuget/packages` | `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]` |
| `NUGET_FETCH` | set, default on | unchanged | - |
| `NETFX`, `NETFX-TARGET`, `CSC_TOOLSET`, `FSCVER`, `FSHARP_CORE_VERSION`, `NETSTANDARD_LIBRARY_VERSION`, `NETSTANDARD_LIBRARY_REF_VERSION`, `NETFX_REFERENCE_ASSEMBLIES_VERSION`, `NETCORE_REF_VERSION` | set only | unchanged | - |
| `HERMETIC` | set only, default off | unchanged | - |
| `CSC_SERVER` / `XAKE_CSC_SERVER` | `CSC_SERVER` from the set (`on`/`off`/seconds), else env `XAKE_CSC_SERVER` (raw switch, `Csc.fs:31-35`), else server | `CSC_SERVER` from the set, else `XAKE_CSC_SERVER` **from the set** (same switch parser), else server | `var "XAKE_CSC_SERVER" [Env "XAKE_CSC_SERVER"]` or `-d XAKE_CSC_SERVER=0` |
| `CSC_JOBS` / `XAKE_CSC_JOBS`, `FSC_JOBS` / `XAKE_FSC_JOBS` | `*_JOBS` from `ctx.Vars`, else env `XAKE_*_JOBS` (`CompilerRunner.fs:140-146`); untracked | `*_JOBS` from `ctx.Vars` only, untracked | `var "CSC_JOBS" [Env "XAKE_CSC_JOBS"]` (likewise FSC) |
| `CI` (hermetic) | set (strict `on/off/true/false/yes/no/1/0`), else env `CI` (lenient, `getEnv`, `Lock.fs:1344-1354`) | set only (strict parser), unset = off | `var "CI" [Env "CI"]` (`CI=true` parses) |
| `LOCK_REFRESH` (hermetic) | set, else env (`Locks.fs:108-110`) | set only | `var "LOCK_REFRESH" [Env "LOCK_REFRESH"]` |
| `LOCK_STALE` (hermetic) | set only, default fail | unchanged | - |
| `SOURCE_DATE_EPOCH` (hermetic) **[owner]** | env only, raw (`Pack.fs:44`, `Sbom.fs:596`) | set only; the recipe-level callers pass it; context-free defaults use the 1980 epoch | `var "SOURCE_DATE_EPOCH" [Env "SOURCE_DATE_EPOCH"]` |

**L-3 (child processes).** A library that starts `dotnet restore`/`msbuild` passes the resolved
package folder as `NUGET_PACKAGES=<folder>` in the child's environment in every case, so an
environment value cannot redirect the child (today `Import.fs:1684` passes it only for a script
variable). `DotNetFwk.nugetRoot ()` and `Roots.nugetRoot ()` return `~/.nuget/packages` without
reading the environment.

**L-4 (hermetic policy). [owner]** `packageRootWithSource` returns `ScriptVariable` when
`NUGET_PACKAGES` is defined in the set, whatever its origin, and `UserProfile` otherwise;
`EnvironmentVariable` is no longer produced (the case stays, `[<Obsolete>]`). `HERMETIC=on` accepts
`ScriptVariable` (an `env:` origin included: the script chose that rule) and refuses `UserProfile`
with the 3.13 message (`HermeticMode.fs:155`). A blank value counts as unset.

**L-5.** `Csc.RunOptions.Default.Server` becomes `Shared None`; it no longer reads `XAKE_CSC_SERVER`
(`Csc.fs:166`). Inside a recipe the server is resolved from the set (L-2); a direct `Csc.run` outside
a recipe sets `Server` itself.

### 3.7 Banner and help

**B-1 (banner).** The banner's `vars:` section prints one line per key of the set, sorted ordinally:
`    NAME=value (origin)` with origin `cli`, `script`, `env:<NAME>` or `default`; a secret's value as
`***`; a declared key without value as `    NAME (not set)`. Shadowed entries are not printed (each
name once). The 3.13 label `(script/cli)` goes. The engine prints no banner (unchanged); the
`request` origin is kept in the set for engine demands.

**B-2 (help).** `--help` prints the "Script variables:" section: the `varschema` declarations in the
3.13 format (`XakeScriptBuilder.fs:122-144`), then each `var "N" [...]` rule as
`  -d N=<value> [string], env: X, Y, default: d`, with `(secret)` and no default for a secret.
Constant `var` ops are not listed. `--help` exits before launch evaluation: it reads no environment
and raises no E-* error.

### 3.8 Engine mode

**G-1.** `XakeEngine.Start` builds the base set once, reading the environment once. A change of the
environment after `Start` is not seen by variables until a new engine starts (`getEnv` in a recipe
stays live, N-3).

**G-2 (request on top).** `Demand(target, ?vars)` evaluates an overlay: `?vars` is layer 1 over the
base set (V-2), with origin `request`, converted per S-3 for typed declared keys. `makeCtx` puts the
request pairs first (`XakeScriptBuilder.fs:299` today appends them), and the test
`engine per-demand vars come after the script's vars` (`CommandLineTests.fs:548`) is inverted to
expect the request value.

**G-3.** Overlays are per demand: concurrent demands with different `?vars` do not see each other's
values. A demand without `?vars` uses the base set. The dependency check of a demand uses its overlay.

**G-4.** A `?vars` pair for a secret key fails that demand with E-1b before any target runs; an
E-4 conversion failure likewise. `Start` itself fails (raises `XakeException`) on any launch error.

## 4. Diagnostics

Launch errors are collected, printed together at `Error`, and fail the run with exit code 2 before
`setup` and any target (`XakeException` under `ThrowOnError`), as the 3.13 secret errors do
(`ScriptRunner.fs:226-230`). In engine mode they fail `Start`, or the demand (G-4).

- **E-1a** (3.13 text, `Types.fs:206`), a `-d` of a secret that has an `Env` source; `GIVEN` is the
  name given to `-d`, `ENV` the first `Env` source:
  `Variable 'GIVEN' is secret: secret variables come from the environment, set ENV instead of passing -d GIVEN=...`
- **E-1b**, any other value given to a secret from outside its declaration:
  `Variable 'N' is secret: its value comes only from its declaration's sources, not from WHERE`
  with `WHERE` one of `-d N=...` (secret without `Env` source), `var "N" "..."`, `ExecOptions.Vars`,
  `the ?vars of Demand`.
- **E-2** `Variable 'N' is declared twice: by A and by B; declare it once`, with `A`/`B` one of
  `varschema field F`, `var "N" [...]`.
- **E-3** (3.13 text, `Vars.fs:148`) `Required variable 'N' is not provided`. At launch for a
  `varschema` field (S-2); at read time for an undeclared `Var.*` read (K-3).
- **E-4** (3.13 texts, `Vars.fs:41,43`) `Failed to convert value 'v' for variable 'N' to type 'T'.`,
  for a secret `Failed to convert the value of secret variable 'N' to type 'T'.`
- **W-1** (once per key per run, at the read) `Variable 'N' is read through a declaration that is not in varschema: its environment variable ENV is not read; add it to varschema or use var "N" [Env "ENV"]`
- **C-1** The change reason of a `Var` stays the 3.13 text
  `Global script variable N was changed 'a'->'b'` (`DependencyAnalysis.fs:101`); secrets keep the
  3.13 masked reasons.

## 5. Data shapes

Public API (namespace `Xake`, auto-opened):

```fsharp
/// A source of a `var` rule, tried in list order; the markers define nothing.
type VarSource =
    | Env of string          // environment variable, read once at launch
    | Default of string      // constant
    | Secret                 // presence secret (3.13 SecretPresence)
    | SecretHashed           // HMAC secret (3.13 SecretHash)

/// Where the value of a variable came from (banner, diagnostics).
[<RequireQualifiedAccess>]
type VarOrigin = Request | Cli | Script | Env of string | Default
//  labels:      request | cli | script | env:<NAME>     | default

/// One entry of the variable set.
type VarEntry = { Value: string option; Origin: VarOrigin option; Secret: SecretMode; Declared: bool }

/// A declaration, written by `var [...]` and by `varschema`.
type VarRule = {
    Name: string; Sources: VarSource list; Required: bool
    ValueType: System.Type          // typeof<string> for `var [...]`; the field's 't for varschema
    DeclaredBy: string }            // "var" | "varschema field F" (E-2)

// RulesBuilder
[<CustomOperation("var")>] member AddVar(script, name: string, value: string)              // unchanged
[<CustomOperation("var")>] member AddVar(script, name: string, sources: VarSource list)   // new
```

`VarSource.Env` coexists with the `Xake.Env` module (`Env.isUnix` still resolves; checked with
`dotnet fsi`), and the overloaded custom operation compiles on SDK 10 (owner's check).

Records:
- `ExecOptions.Vars : (string * string) list`: **type unchanged**; it now holds only script values
  (`ExecOptions.Vars` entries and constant `var` ops).
- `ExecOptions.CliVars : (string * string) list` (new): the `-d` pairs, last first. `ApplyCommandLine`
  writes `-d` here instead of prepending to `Vars`; this is what keeps `cli` apart from `script`.
- `ExecOptions.VarRules : VarRule list` (new): `var [...]` and `varschema` both append here;
  `VarSchema : (string * VarHelp) list` stays for `--help` (`VarHelp` unchanged).
- `ExecContext.Vars : (string * string) list`: **type unchanged**; it is the set flattened, one pair
  per defined key with its final value (request pairs first in a demand). `Util.getVar`, the
  dependency check, `EXPLAIN_RULE` and `jobsLimit` keep working on it.
- `ExecContext.VarSet : Map<string, VarEntry>` (new): the set with origins and secret modes; the
  banner, K-2/K-3 and the hermetic policy (L-4) read it.

Source compatibility: code that builds `ExecOptions` or `ExecContext` with `{ X.Default with ... }`
compiles unchanged; a full record expression must add the new fields. Code that inspected
`options.Vars` after `ApplyCommandLine` for `-d` values reads `CliVars` instead. `getVar`, `var "N"
"v"`, `Var.*`, `varschema`, `withDefault`, `required`, `secret`, `secretHashed` keep their signatures.
No database change (N-2).

## 6. Compatibility

### 6.1 What an existing 3.13 / 3.14-pre script sees

- `var "N" "v"`, `-d`, `getVar` work as in 3.14-pre (#103): `-d` wins over `var`, the last `-d` wins.
- Engine `Demand(t, ?vars)` values now win over the script's `var` (they were shadowed).
- `varschema` fields are evaluated at launch: a missing `required` value or a bad typed value fails
  the run before any target (was: when a recipe read it). `getVar` of a declared key sees its
  environment value or default.
- A `Var.*` read inline in a recipe and not in `varschema` no longer reads the environment (W-1).
  `-d`/`var` values and its own default still apply.
- `Var.env`/`Var.secret` keys are now overridable by `-d` for non-secrets (3.13 ignored `-d`); for
  secrets `-d` is an error, including argument-scope secrets (3.13 warned).
- A target whose 3.13 record has `Var (N, None)` + `EnvVar (E, v)` (a `Var.*` read from the
  environment) rebuilds once when `N` is now declared in `varschema` (the set has `v`); an inline,
  undeclared read stays up to date while `E` is unchanged and records the 3.14 form on its next
  rebuild. `--resetdb` forces a clean state.
- The banner shows each name once with its real origin.
- Xake.Dotnet and the hermetic package no longer read the environment for build variables (L-2): a
  CI that exported `NUGET_PACKAGES`, `XAKE_CSC_SERVER`, `XAKE_*_JOBS`, `CI`, `LOCK_REFRESH` or
  `SOURCE_DATE_EPOCH` adds a `var "N" [Env "N"]` rule or passes `-d`.

### 6.2 Release note (3.14)

> **Variables are evaluated once, at launch, by rules the script states.**
> - New: `var "N" [Env "X"; Default "v"]` declares a variable by a list of sources tried in order;
>   `Secret` / `SecretHashed` in the list make it a secret. `var "N" "v"` is unchanged.
> - Precedence: engine `?vars` > `-d` (last wins) > `var "N" "v"` > the variable's own sources.
> - `varschema` evaluates `Var.*` declarations at launch: environment, default, required and type
>   errors are reported before any target runs; `getVar` sees declared values.
> - Moving an equal value between the environment, `-d` and `var` no longer rebuilds anything.
> - Secrets accept no value from `-d`, `var "N" "v"` or `?vars` (error).
> - **Breaking:** Xake.Dotnet no longer reads `NUGET_PACKAGES`, `XAKE_CSC_SERVER`, `XAKE_CSC_JOBS`,
>   `XAKE_FSC_JOBS` from the environment. Add e.g. `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]` to
>   the script, or pass `-d NUGET_PACKAGES=<dir>`. Child `dotnet` processes get the resolved package
>   folder explicitly. `Csc.RunOptions.Default` no longer reads `XAKE_CSC_SERVER`.
> - **Breaking:** a `Var.*` read in a recipe without `varschema` no longer reads the environment.
> - The banner shows each variable once, with its source (`cli`, `script`, `env:NAME`, `default`);
>   `--help` lists `var [...]` rules.
> - Xake.Hermetic.Dotnet 0.9 (on Xake 3.14) no longer reads `CI`, `LOCK_REFRESH` or
>   `SOURCE_DATE_EPOCH` from the environment: add `var "CI" [Env "CI"]` and the like on CI.

## 7. Implementation split

1. **PR 1, core (Xake 3.14)**: `VarSource`, `VarOrigin`, `VarEntry`, `VarRule`; the `var` overload;
   `CliVars` in `ApplyCommandLine`; launch evaluation in `Run`/`Start` (V, R, S, X rules);
   `resolveVar` becomes a set lookup (K-2, K-3, W-1); `checkSecretVars` replaced by X-2; banner B-1,
   help B-2; engine overlay G-1..G-4 and the inverted test. Tests §9.1-9.6.
2. **PR 2, Xake.Dotnet readers** (same `Xake` package, ships with PR 1 in 3.14): L-1..L-3, L-5, the
   `NUGET_PACKAGES`, `CSC_SERVER` pair and `*_JOBS` rows of L-2, `PackageRootSource` and
   `HERMETIC` policy L-4, docs (`csc-server.md`, the `HermeticMode.fs:155` hint). Tests §9.7.
3. **PR 3, Xake.Hermetic.Dotnet 0.9** on Xake `[3.14, 3.15)` (`src/hermetic/*.fsproj:84`): `CI`,
   `LOCK_REFRESH`, `SOURCE_DATE_EPOCH` per L-2, `Import.fs:1684` per L-3, docs. Tests §9.8.

## 8. Engine summary (long-lived)

`Start`: ApplyCommandLine → evaluate base set (env read once) → launch errors fail `Start` → setup.
`Demand(t, ?vars)`: overlay (G-2), secret/type errors fail the demand (G-4), targets run against the
overlay. `StopAsync`: teardown with the base set. Environment changes after `Start` are not seen.

## 9. Conformance tests

One line each: given / when / then (rules).

### 9.1 Chain and `var` lists
1. Given `var "X" [Env "A"; Default "d"]`, env `A=a`; when a recipe reads `getVar "X"`; then `Some "a"`, banner `X=a (env:A)` (R-2, V-2).
2. Given the same rule, `A` unset; then `Some "d"`, banner `X=d (default)` (R-2, V-4).
3. Given `var "X" [Env "A"; Env "B"; Default "d"]`, `A` unset, `B=b`; then `b (env:B)` (V-2).
4. Given `var "X" [Env "A"; Default "d"]`, env `A=` (Unix); then `Some ""` (V-4).
5. Given `var "X" [Env "A"]`, `A=a` and `-d X=c`; then `c (cli)` (V-2).
6. Given `-d X=1 -d X=2`; then `2 (cli)` (V-2).
7. Given `ExecOptions.Vars = [X,a]` and `var "X" "b"`; then `b (script)` (V-2).
8. Given `var "X" [Default "d"]` and `var "X" "s"`, in either order; then `s (script)` (R-4).
9. Given `var "X" [Default "a"]` then `var "X" [Default "b"]`; then `b (default)` (R-4).
10. Given `var "X" [Env "A"]`, `A` unset; then `getVar "X"` = `None`, banner `X (not set)` (R-3, B-1).
11. Given `var "X" [Env "A"]`, `A=a` at launch; when a recipe sets `A=z` in the process and another reads `getVar "X"`; then `a` (V-3).

### 9.2 `varschema`
12. Given `varschema {| X = Var.string(envVar = "A") |> withDefault "d" |}`; then tests 1-2 give the same values and origins with `X` as key (S-1).
13. Given `Var.string "buildConfig"` in `varschema`, env `BUILD_CONFIG=r`; then `r (env:BUILD_CONFIG)` (S-1).
14. Given `Var.arg "X"` in `varschema`, env `X=e`; then `X (not set)` (S-1).
15. Given `Var.env(envVar = "A")` as field `X`, `A=a` and `-d X=c`; then `c (cli)` (S-1, V-2).
16. Given `Var.string "R" |> required` in `varschema`, `R` unset; when the script runs; then E-3 at launch, exit 2, neither `setup` nor any target runs (S-2).
17. Given `Var.int "N"` in `varschema` and `-d N=x`; then E-4 at launch (S-3).
18. Given `Var.int "N" |> withDefault 5` in `varschema`, nothing set; then `let! n` = `5`, `getVar "N"` = `Some "5"`, banner `N=5 (default)` (S-3, K-1).
19. Given `varschema` field `X` and `var "X" [Default "d"]`; then E-2 at launch (S-4).
20. Given two `varschema` ops each with a field of key `X`; then E-2 (S-4).
21. Given `Var.string "X" |> withDefault "d"` in `varschema` and `var "X" "s"`; then `s (script)`, no error; with `Var.int "X"` and `var "X" "s"`, E-4 (S-5, S-3).
22. Given `--help` and a `required` field that is unset; then help is printed, exit 0, no E-3 (B-2).

### 9.3 Reads and dependencies
23. Given a `varschema` field `X` from env `A=v`; when a recipe reads `let! x = schema.X`; then the recorded deps contain `Var (X, Some "v")` and no `EnvVar` (K-2, K-5).
24. Given a target built with env `A=v` (`var "X" [Env "A"]`); when rerun with no `A` and `-d X=v`; then up to date (K-5).
25. Given the same target; when rerun with `-d X=w`; then rebuilt with C-1 `'Some "v"'->'Some "w"'` (K-5, C-1).
26. Given a `varschema` `withDefault "d"` read by a target; when rerun with `withDefault "e"`; then rebuilt (K-5, S-1).
27. Given an inline `Var.string(name = "X", envVar = "A") |> withDefault "d"`, not in `varschema`, `A=a`; then `d`, W-1 once per run, recorded `Var (X, None)` (K-3).
28. Given the inline read of 27 and `-d X=c`; then `c`, recorded `Var (X, Some "c")` (K-3).
29. Given an inline `Var.string "X" |> required`, nothing set; then the reading target fails with E-3; the launch does not (K-3).
30. Given a recipe reading `getVar "X"` twice and `let! x = schema.X`; then one `Var (X, _)` is recorded (K-4).
31. Given `getEnv "PATH"` in a recipe; then `EnvVar (PATH, _)` recorded and checked live (N-3, K-6).
32. Given a 3.13 database with `Var (X, None)` + `EnvVar (A, Some v)` and env `A=v`; when `X` is a `varschema` field of `Env "A"`; then rebuilt once, then up to date (K-5, §6.1).

### 9.4 Secrets
33. Given `var "T" [Secret; Env "A"]` and `-d T=s`; then E-1a naming `A`, exit 2, `s` masked in all output (X-2).
34. Given the same rule and `-d A=s`; then E-1a with `GIVEN` = `A` (X-2).
35. Given the same rule and `var "T" "s"` (resp. `ExecOptions.Vars [T,s]`); then E-1b with `var "T" "..."` (resp. `ExecOptions.Vars`) (X-2).
36. Given `Var.string "T" |> secret` (argument scope) in `varschema` and `-d T=s`; then E-1a (3.13: warning) (X-2, X-5).
37. Given `var "T" [Secret; Env "A"]`, `A=s`; then banner `T=*** (env:A)`, `s` in no log line, db holds `SecretDep (T, SecretArg, SecretPresent)` (X-1, X-3, X-4).
38. Given `[SecretHashed; Env "A"]` and a changed `A`; then rebuilt with the masked reason; with `[Secret; Env "A"]` up to date; with `A` unset, rebuilt (X-4).
39. Given `[Secret; SecretHashed; Env "A"]`; then the mode is HMAC (R-2).
40. Given `var "T" [Secret; Default "x"]`; then `--help` lists `T` with `(secret)` and no default (X-3, B-2).

### 9.5 Banner and help
41. Given `-d A=1`, `var "B" "2"`, `var "C" [Env "E"]` with `E=3`, `varschema` `D` (`withDefault 4`) and `F` (unset); then the vars section is exactly `A=1 (cli)`, `B=2 (script)`, `C=3 (env:E)`, `D=4 (default)`, `F (not set)` (B-1).
42. Given `-d X=1 -d X=2` and `var "X" "s"`; then one line `X=2 (cli)` (B-1).
43. Given `varschema` fields and `var "R" [Env "A"; Env "B"; Default "d"]` and `var "K" "v"`; then `--help` lists the fields, then `-d R=<value> [string], env: A, B, default: d`, and not `K` (B-2).

### 9.6 Engine
44. Given `xakeEngine { var "V" "s"; ... }`; when `Demand("t", ["V","r"])`, then a plain `Demand("t")`; then `r`, then `s` (G-2; inverts `CommandLineTests.fs:548`).
45. Given two concurrent demands with `["V","r1"]` and `["V","r2"]`; then each sees its own value (G-3).
46. Given `var "T" [Secret; Env "A"]`; when `Demand("t", ["T","x"])`; then the demand fails with E-1b `the ?vars of Demand`, no target runs (G-4).
47. Given `var "X" [Env "A"]`, `A=a` at `Start`; when `A` changes and `Demand` runs; then `a` (G-1).
48. Given a `required` unset `varschema` field; when `start`; then `Start` raises `XakeException` with E-3 (G-4).

### 9.7 Xake.Dotnet
49. Given env `NUGET_PACKAGES=/m`, no `var`/`-d`; then `packageRoot ()` = `~/.nuget/packages`, the child restore gets `NUGET_PACKAGES=~/.nuget/packages`, source `UserProfile` (L-1, L-3, L-4).
50. Given `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]`, env `/m`; then `/m`, source `ScriptVariable`, `HERMETIC=on` accepts it (L-4).
51. Given `HERMETIC=on` and `NUGET_PACKAGES` unset (resp. `-d NUGET_PACKAGES=`); then refused with the 3.13 message (L-4).
52. Given env `XAKE_CSC_SERVER=0` only; then the compiler server is used; with `-d XAKE_CSC_SERVER=0`, in-process; with also `-d CSC_SERVER=30`, keepalive 30 (L-2).
53. Given env `XAKE_CSC_JOBS=2` only; then no limit; with `var "CSC_JOBS" [Env "XAKE_CSC_JOBS"]`, limit 2 and no dependency recorded (L-2).
54. Given env `XAKE_CSC_SERVER=0`; then `Csc.RunOptions.Default.Server = Shared None` (L-5).
55. Given the sources of `src/dotnet`; then no `getEnv` or `GetEnvironmentVariable` names a variable of L-2 (L-1; a source scan test).

### 9.8 Xake.Hermetic.Dotnet
56. Given env `CI=true`, no `var`; then `underCi` = false; with `var "CI" [Env "CI"]`, true; with `-d CI=bogus`, the 3.13 error (L-2).
57. Given env `LOCK_REFRESH=all` only; then no refresh; with `var "LOCK_REFRESH" [Env "LOCK_REFRESH"]`, refresh (L-2).
58. Given env `SOURCE_DATE_EPOCH=1700000000` only; then pack and SBOM use 1980-01-01; with `var "SOURCE_DATE_EPOCH" [Env "SOURCE_DATE_EPOCH"]`, 2023-11-14 (L-2).
59. Given the sources of `src/hermetic`; then no `getEnv` or `GetEnvironmentVariable` names a variable of L-2 (L-1).

## 10. Points for the owner

1. **K-3** (requested decision): a `Var.*` read in a recipe that never went through `varschema` takes
   the set value, then its own constant default, never the environment, and warns W-1. The
   alternative was an error; it could only fire at read time, not at launch.
2. **S-5**: a constant `var "N" "v"` plus a `varschema` declaration of `N` is the script value
   feeding the declaration, not a conflict (E-2 only for `var "N" [...]` vs `varschema`).
3. **L-4**: `HERMETIC=on` accepts `NUGET_PACKAGES` from any origin once the script declares it,
   `env:` included (3.13 refused the environment); it refuses only the unset default.
4. **L-2, `SOURCE_DATE_EPOCH`**: it is a cross-tool reproducible-builds convention that is normally
   exported. Under L-1 it becomes set-only, which changes `Pack.Options.Default` and
   `Sbom.timestamp ()` (context-free today) into values passed by recipe-level callers.
5. **§6.1**: `Var.env` (non-secret) given by `-d` now takes the `-d` value (S-1 equivalence with a
   source list); 3.13 ignored it.
