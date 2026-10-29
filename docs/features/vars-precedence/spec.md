# Spec: variable evaluation

Status: rev. 6 for the owner's approval, 2026-10-29. It formalizes [brief.md](brief.md), whose decisions are final. It
replaces rev. 5 ([archive/spec-rev5.md](archive/spec-rev5.md)) and revives S-1..S-4 of rev. 4
([archive/spec-rev4.md](archive/spec-rev4.md)); ids are kept where the rule is unchanged. Retired: rev. 5 Y-1..Y-3, X-6,
W-3; rev. 4 S-5, K-2, K-3, W-1. File:line references are to this branch (dev, Xake 3.15.0). MUST, MUST NOT, SHOULD, MAY
are normative. The tests of §8 cite every rule and diagnostic. No code before approval and the tests of §8.

## 1. Scope and non-goals

In scope: how a build variable gets its value (the chain), the `var` source-list overload, `varschema` as a declaration
evaluated at declaration, how a `Var.*` read in a recipe finds its value, second definitions, secrets, dependencies, the
banner, `--help`, the debug options dump, engine mode, and how Xake.Dotnet and Xake.Hermetic.Dotnet read their variables.

- **N-1** No lazy evaluation, no declaration during the run: every value of the variable set is known at launch. A
  recipe only reads. `getEnv` is the one live read of the environment (N-3).
- **N-2** No database format change: `Var`, `EnvVar`, `SecretDep` as in 3.13 (database 0.6).
- **N-3** `getEnv` is unchanged: an explicit, live read of the process environment that records `EnvVar`.
- **N-4** The environment used for tool discovery is not a build variable: `PATH`, `DOTNET_ROOT`, `DOTNET_HOST_PATH`,
  `ProgramFiles` (`DotNetFwk.fs:128,181,410-424`).
- **N-5** The options precedence of #103 is not changed. **N-6** No warning for unused `-d` names or `?vars`.
- **N-7** Out of scope: the canonical form of typed values (`-d N=04` then `-d N=4` rebuilds; the string is recorded),
  and the process-wide secrets registry by name (`Types.fs:142-154`).
- **N-8** `ExecContext` and the six compiler records are frozen until 4.0 (`RecordShapeTests.fs`): no new field.

## 2. Definitions

- **Input values**: pairs given from outside a declaration: engine `?vars` (request), `-d` (cli), `ExecOptions.Vars`
  entries and constant `var "N" "v"` ops (script).
- **Declaration**: a `var "N" [sources]` op (a **rule**) or a `Var.*` field of a `varschema` op (a **field**).
- **Source**: `Env "X"` (environment variable `X`) or `Default "v"` (the constant `v`). The markers `Secret` and
  `SecretHashed` are list elements that define nothing; they set the secret mode.
- **Script definition**: an `ExecOptions.Vars` entry, a constant `var` op, a rule or a field; each names one key.
- **Key** of a `Var.*` value: `CliArgName`, else `Name`, else `FieldName`, verbatim (3.13 `resolvedCliName`,
  `Vars.fs:111-112`). **Env name**: `EnvName`, else `toEnvName (Name, else FieldName)` (`Vars.fs:56-57,113`). `varschema`
  sets `FieldName` by reflection (`Vars.fs:176`); keys are always compared resolved, never by reference.
- **Var read**: `let! x = v` in a recipe for an `OptionalVar`/`RequiredVar` `v`, a field of the schema or an inline value
  (the `Bind` overloads, `Vars.fs:136-149`).
- **Op time**: when a `var` or `varschema` op is applied while the script expression is built.
- **Launch**: in `Run` (run, dry-run, dump), after `ApplyCommandLine` and the `--help` exit, before the debug options
  dump, the banner, `setup` and any target; in `XakeEngine.Start`, before `setup`.
- **Variable set**: the immutable map `key -> entry` built at launch (V-1): value (or none), origin, declaration.
- **Origin**: `request`, `cli`, `script` (the **input origins**), `env:<NAME>`, `default`.
- **Switch**: a library variable with a closed set of values: `HERMETIC`, `NUGET_FETCH`, `CI`, `CSC_SERVER`,
  `XAKE_CSC_SERVER`, `LOCK_STALE`.

## 3. Rules

### 3.1 The variable set and the chain

**V-1.** At launch Xake builds the variable set once. It holds every key named by `-d`, `ExecOptions.Vars`, a `var` op
(either form) or a `varschema` field. A key that nothing names is absent and reads as `None`. The set does not change
during the run; in engine mode it lives as long as the engine, with a per-demand overlay (G-2).

**V-2 (chain, first defined wins).** For a key `N` the value comes from the first layer that defines `N`:
1. request: the engine's `?vars` of the demand, the last pair for `N` wins;
2. cli: `-d N=v`, the last `-d` of `N` wins;
3. script: the `ExecOptions.Vars` entry or the constant `var "N" "v"` (at most one script definition, R-4);
4. the declaration of `N` (at most one, R-4): its sources in list order.
Layers 1-2 do not apply to a declaration that refuses them (S-5, X-2): that is an error, never a silent skip.

**V-3 (environment read once).** Each `Env "X"` source of a declaration is read once, at op time (R-3, S-6). No value of
the set reads the environment after that; a later change of the process environment is not seen by the set.

**V-4 (definedness).** `-d N=` and `var "N" ""` define `N` as `""`. `Env "X"` defines iff `X` is present, an empty value
included (Windows has no empty variables). `Default "v"` always defines. The evaluator neither trims nor interprets
string values; readers do (L-7). Only a field's type conversion interprets (S-3).

**V-5.** Keys are compared ordinally, as `Util.getVar` does; `-d EXPLAIN_RULE` (`ExecCore.fs:208-210`) is unchanged.

### 3.2 `var` with a source list

**R-1.** `var "N" "v"` keeps its meaning: a script value, layer 3.
**R-2.** `var "N" [sources]` is a rule for `N`, layer 4: `Env "X"` and `Default "v"` tried in list order. `Secret` makes
`N` a presence secret, `SecretHashed` an HMAC secret (the 3.13 modes); a marker may stand anywhere in the list; with both,
`SecretHashed` wins (3.13 `Secrets.declare`). An empty list is allowed and defines nothing.

**R-3.** The op evaluates the list at op time: the first defining source gives the rule's value and its origin
(`env:<X>` or `default`); none leaves the rule without value. Layers 1-3 are overlaid at launch (V-2).

**R-4 (one script definition per key).** A key MUST have at most one script definition; a second one, of any kind and in
any order, fails the launch with E-2: two rules, two constant `var` ops, a constant and a rule, an `ExecOptions.Vars`
entry and a `var` op of either form, two `ExecOptions.Vars` entries, and every pairing with a field (S-4). Repeated `-d`
(last wins) and `?vars` are not script definitions (V-2). The constant op no longer removes an earlier entry
(`XakeScriptBuilder.fs:156-157,225`).

### 3.3 `varschema`: a declaration evaluated at declaration

**S-1 (import).** `varschema` imports each `Var.*` field completely: key, env name, scope, secret mode, required flag,
default, type. A field is the rule `var KEY [sources]` with the sources
- `Env E` (E its env name) unless the scope is `ArgOnly`;
- then `Default d` when it has `withDefault x`, `d` = `Convert.ToString(x, CultureInfo.InvariantCulture)`;
- `Secret`/`SecretHashed` per its secret mode.
So `Var.string(envVar = "X") |> withDefault "d"` as field `N` gives the value, origin and dependency of
`var "N" [Env "X"; Default "d"]`. Its scope, required flag, type and secret mode stay in its declaration (§5).

**S-2 (required).** A `required` field without `withDefault` whose key has no value after V-2 fails the launch with E-3.
**S-3 (types).** At launch the winning value of a field of type `'t` other than `string` is converted with the 3.13
conversion (`Convert.ChangeType`, `Vars.fs:47-53`); a failure fails the launch with E-4. A value of origin `default` is
not parsed: a read returns the field's typed default itself (T-2).

**S-4 (one declaration per key).** A field and any other script definition of its key (another field in the same or
another `varschema` op, a rule, a constant `var`, an `ExecOptions.Vars` entry) is a second definition: E-2 (R-4).

**S-5 (scope).** `ArgAndEnv` and `ArgOnly` fields take layers 1-2. An `EnvOnly` field (`Var.env`, `Var.secret`) takes
its value only from its sources: `-d` of its key fails the launch with E-6 (E-1a when secret, X-2); `?vars` fails the
demand (G-4). The scope also shapes the `--help` header (B-2).

**S-6 (op time).** `varschema` evaluates every field when the op is applied: it sets `FieldName`, reads the environment
(V-3), declares the secret names in the `Secrets` registry (key unless `EnvOnly`, env name unless `ArgOnly`, as
`Vars.fs:188-189`) and registers a secret's value with `SecretMask`. It reports no error at op time: S-2, S-3 and X-2
are checked at launch on the winning value.

### 3.4 Reads and dependencies

**K-1 (`getVar`).** `getVar "N"` returns the set's value of `N` as is and records one `Var (N, value)` (`SecretDep` for a
secret, X-4); unchanged API. A declared key returns its declaration's value, its `Default` included.

**K-6 (`getEnv`).** `getEnv "X"` is the one explicit read of the environment: live, recording `EnvVar` (N-3), checked
live at the next run (`DependencyAnalysis.fs:97`). Nothing else reads the environment after op time.

**T-1 (declaration equality).** The declaration of a `Var.*` value is the `VarDecl` that S-1 builds from it with its
resolved key; a rule's is built from its list (scope `ArgAndEnv`, type `string`, `Required = false`, `DefaultValue` the
first `Default`). Two declarations are equal iff all of these are equal: `Key` (ordinal), `Sources` (the `Env`/`Default`
elements in order), `DefaultValue` (`Object.Equals`), `Scope`, `Required` (true only for `required` without default),
`Secret` (the mode), `ValueType` (`System.Type` equality). `DeclaredBy` and the description are not compared.

**T-2 (a Var read).** A Var read looks up its key in `ctx.Engine.Options.Variables` (§5). If the entry has a declaration
equal to the read's (T-1), the read
- takes the value `Util.getVar ctx.Vars key` (request pairs first in a demand) and records it through `getVar`: one
  `Var (key, value)`, or `SecretDep (key, SecretArg, digest)` for a secret;
- returns the declaration's typed default when that value is the entry's own and its origin is `default`; otherwise the
  value converted to `'t` (S-3; already checked at launch or at the demand, G-4); `None` for an unset `OptionalVar`;
- never calls `getEnv`, never records `EnvVar`, and ignores the read's own sources (they are equal anyway).

**T-3 (different declaration).** If the entry's declaration differs (T-1), the read fails the reading target with E-7.
**T-4 (no declaration).** If the key is absent or its entry has no declaration (only input values), the read fails the
reading target with E-8. It fires at read time: an inline read is invisible at launch.

**K-4.** One read records one dependency per key per recipe, whatever the number of reads (`getVar` and Var reads alike).
**K-5 (value, not source).** At the next run `Var (N, v)` is up to date iff the set's value of `N` equals `v`
(`DependencyAnalysis.fs:100`, unchanged). The origin is never recorded: moving an equal value between the environment
(through a declaration), `-d`, `var` and a `Default` does not rebuild. No `EnvVar` is recorded for an `Env` source.

### 3.5 Secrets

**X-1.** A key is secret iff its declaration has `Secret`/`SecretHashed` (rule) or a secret mode (field). At op time
Xake declares the key and its `Env` names in the `Secrets` registry and registers the value with `SecretMask` (S-6),
before anything is logged, the debug options dump and the banner included.

**X-2 (own sources only).** A secret takes its value only from its declaration, whatever its scope. At launch a `-d` of
its key or of any of its `Env` names fails with E-1a (E-1b when it has no `Env` source); a script value for the key is a
second definition (E-2); a request value fails the demand (G-4). The offending value is registered for masking first.

**X-3.** A secret's value is `***` in the banner, the debug options dump and every log line (`MaskingLogger`,
unchanged); `--help` shows no default for it. **X-7 (reads are not refused).** `getVar` and `getEnv` of a secret return
the real value: masking applies to output only, the dependency is presence or HMAC per mode (X-4).

**X-4.** A read of a secret (`getVar`, Var read) records `SecretDep (key, SecretArg, digest)` with the 3.13 digest of its
mode (presence: set/unset; hashed: salted HMAC), never the value, checked against the set (`DependencyAnalysis.fs:88-93`,
unchanged). `getEnv` of its env name records `SecretDep (name, SecretEnv, digest)`, checked live.

**X-5.** The read-time secret check of `resolveVar` (`Vars.fs:115-124`) and the launch check `checkSecretVars`
(`ScriptRunner.fs:23-52`) are replaced by X-2. The 3.13 warning for an argument-scope secret given by `-d` is retired:
it is E-1a now. A `Var.arg ... |> secret` thus takes its value only from its `withDefault`.

### 3.6 Libraries

**L-1.** Xake.Dotnet and Xake.Hermetic.Dotnet read build variables only from the variable set: `getVar` for tracked
reads, `ctx.Vars` for untracked ones. They MUST NOT read the process environment for a variable of L-2 (no `getEnv`, no
`GetEnvironmentVariable`) and MUST NOT use a Var read (it would fail with E-8). An unset name takes the library's fixed
default. Libraries declare nothing: the script owns the declaration of each name.

**L-2 (affected variables).** "set" = the variable set (`-d`, `var`, `?vars`, a declaration).

| Name | 3.15 | next minor | A CI that relied on the environment adds |
|---|---|---|---|
| `NUGET_PACKAGES` | set, else env (raw, untracked, `DotNetFwk.fs:341,790`), else `~/.nuget/packages` | set (relative to the project root, or absolute), else `<project root>/.packages` (L-4) | `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]` |
| `NUGET_FETCH` | set, default on; any unknown value is on (`DotNetFwk.fs:888`) | set, default on; strict switch (L-6) | - |
| `NETFX`, `NETFX-TARGET`, `CSC_TOOLSET`, `FSCVER`, `FSHARP_CORE_VERSION`, `NETSTANDARD_LIBRARY_VERSION`, `NETSTANDARD_LIBRARY_REF_VERSION`, `NETFX_REFERENCE_ASSEMBLIES_VERSION`, `NETCORE_REF_VERSION` | set only | unchanged | - |
| `HERMETIC` | set only, default off, strict (`HermeticMode.fs:59-66`) | unchanged; empty = off (L-7) | - |
| `CSC_SERVER` / `XAKE_CSC_SERVER` | `CSC_SERVER` from the set (else warning and server, `Csc.fs:44-63`), else env `XAKE_CSC_SERVER` (lenient, `Csc.fs:31-35`), else server | `CSC_SERVER` from the set (L-6), else `XAKE_CSC_SERVER` **from the set** (strict switch), else server | `var "XAKE_CSC_SERVER" [Env "XAKE_CSC_SERVER"]` or `-d XAKE_CSC_SERVER=0` |
| `CSC_JOBS` / `XAKE_CSC_JOBS`, `FSC_JOBS` / `XAKE_FSC_JOBS` | `*_JOBS` from `ctx.Vars`, else env `XAKE_*_JOBS` (`CompilerRunner.fs:138-150`); untracked; invalid warns | `*_JOBS` from `ctx.Vars` only, untracked; invalid warns (a limit, not a switch) | `var "CSC_JOBS" [Env "XAKE_CSC_JOBS"]` (likewise FSC) |
| `CI` (hermetic) | set (strict), else env (lenient, `getEnv`, `Lock.fs:1344-1354`) | set only, strict (L-6), empty or unset = off | `var "CI" [Env "CI"]` |
| `LOCK_REFRESH` (hermetic) | set, else env (`Locks.fs:106-110`); blank = unset | set only; blank = unset | `var "LOCK_REFRESH" [Env "LOCK_REFRESH"]` |
| `LOCK_STALE` (hermetic) | set only, default fail (`Locks.fs:498-505`) | unchanged; empty = fail (L-7) | - |
| `SOURCE_DATE_EPOCH` (hermetic) | env only, raw (`Pack.fs:44`, `Sbom.fs:596`) | set only; recipe-level callers pass it; context-free defaults use the 1980 epoch; empty or unparsable = 1980 epoch (3.13) | `var "SOURCE_DATE_EPOCH" [Env "SOURCE_DATE_EPOCH"]` |

**L-3 (child processes).** A library that starts `dotnet restore`/`msbuild` passes the resolved package folder as
`NUGET_PACKAGES=<folder>` to the child in every case, so an environment value cannot redirect it. `DotNetFwk.nugetRoot ()`
and `Roots.nugetRoot ()` (context-free) return `~/.nuget/packages` without reading the environment.

**L-4 (package folder and hermetic policy).** The package folder is the set's `NUGET_PACKAGES`, trimmed, resolved against
the project root (`DotNetFwk.fs:755-761`); a blank value counts as unset (as an empty switch, L-7); unset is
`<project root>/.packages`. Both are a folder of the build's own: `packageRootWithSource` returns `ScriptVariable` for
both, whatever the origin, and `HERMETIC=on` accepts it. `PackageRootSource` gets no new case (a published hermetic
package compares with `ScriptVariable`, `Import.fs:1684`, `Project.fs:475`, `Lock.fs:1550`); `EnvironmentVariable` and
`UserProfile` are no longer produced and become `[<Obsolete>]`.

**L-5.** `Csc.RunOptions.Default.Server` becomes `Shared None` and no longer reads `XAKE_CSC_SERVER` (`Csc.fs:166`). In a
recipe the server is resolved from the set (L-2); a direct `Csc.run` outside a recipe sets `Server` itself.

**L-6 (invalid switch values fail).** A switch value outside its set, from any origin, fails the reading target with
E-5. Values are trimmed and case-insensitive. `HERMETIC`, `NUGET_FETCH`, `CI`, `XAKE_CSC_SERVER`: `on`/`true`/`yes`/`1`,
`off`/`false`/`no`/`0`. `CSC_SERVER`: `on`, `off` or a non-negative integer (keepalive seconds). `LOCK_STALE`: `fail`,
`warn`. Libraries declare nothing, so the check is at the read.

**L-7 (empty switch = default).** An empty or blank switch value from any origin (`-d CI=`, `var "CI" ""`, a rule whose
`Env` is present and empty) means the default: `HERMETIC` off, `NUGET_FETCH` on, `CI` off, `CSC_SERVER` falls to
`XAKE_CSC_SERVER`, `XAKE_CSC_SERVER` server, `LOCK_STALE` fail. The dependency records the value as read (`""`).

### 3.7 Banner, help, debug dump

**B-1 (banner).** The `vars:` section prints one line per key of the set, sorted ordinally: `    NAME=value (origin)`
with origin `cli`, `script`, `env:<NAME>` or `default`; a secret as `***`; a declared key without value as
`    NAME (not set)`. Each name once; fields are keys like any other (V-1). The 3.13 label `(script/cli)` goes. The engine
prints no banner; the `request` origin exists for demands only.

**B-2 (help).** `--help` prints "Script variables:": the fields in the 3.13 format (`XakeScriptBuilder.fs:122-144`),
then each rule as `  -d N=<value> [string], env: X, Y, default: d`, with `(secret)` and no default for a secret.
Constant `var` ops are not listed. `--help` exits before launch: no launch error (E-1..E-4, E-6) is reported.

**B-3 (debug options dump).** The `Options: %A` dump at `Debug` (`ScriptRunner.fs:207-208`) runs after launch and shows
`Vars`, `CliVars` and `VarDecls` with their values; `***` for a secret key's value, a value registered with
`SecretMask`, and a secret declaration's `Default`/`DefaultValue`; `VarSchema` without secret defaults (3.13).

### 3.8 Engine mode

**G-1.** `XakeEngine.Start` overlays the `-d` layer and the script values on the declarations evaluated at op time and
runs the launch checks once; later environment changes are not seen by the set until a new script value is built.
`Start(options: EngineOptions)` uses `options.Variables` as given (empty by default).

**G-2 (request on top).** `Demand(target, ?vars)` overlays `?vars` as layer 1 (V-2, origin `request`): `makeCtx` puts
the request pairs first in `ctx.Vars` (`XakeScriptBuilder.fs:299` appends them; `CommandLineTests.fs:548` is inverted).
A request value of a typed field is converted per S-3.

**G-3.** Overlays are per demand: concurrent demands do not see each other's `?vars`; a demand without `?vars` uses the
base set; a demand's dependency check uses its overlay.

**G-4.** Before any target of the demand runs, a `?vars` pair for a secret fails it with E-1b, for an `EnvOnly` field
with E-6, an unconvertible value for a typed field with E-4. `Start` raises `XakeException` on any launch error.

## 4. Diagnostics

Launch errors are collected, printed together at `Error`, and fail the run with exit code 2 before `setup` and any
target (`XakeException` under `ThrowOnError`), as the 3.13 secret errors do (`ScriptRunner.fs:226-230`); in engine mode
they fail `Start` or the demand (G-4). No diagnostic prints a secret's value or a default.

- **E-1a** (3.13 text, `Types.fs:206`), `-d` of a secret with an `Env` source; `GIVEN` the name given to `-d`, `ENV` the
  first `Env` source: `Variable 'GIVEN' is secret: secret variables come from the environment, set ENV instead of passing -d GIVEN=...`
- **E-1b**, another input value for a secret; `WHERE` = `-d N=...` (no `Env` source) or `the ?vars of Demand`:
  `Variable 'N' is secret: its value comes only from its declaration's sources, not from WHERE`
- **E-2** (R-4, S-4) `Variable 'N' is defined twice in the script: by A and by B; define it once`; `A`, `B` the first
  two definitions in script order (`ExecOptions.Vars` first), each `ExecOptions.Vars`, `var "N" "..."`, `var "N" [...]`
  or `varschema field F`. One error per key.
- **E-3** (3.13 text, `Vars.fs:148`; S-2, at launch) `Required variable 'N' is not provided`
- **E-4** (3.13 texts, `Vars.fs:41,43`; S-3, at launch or demand) `Failed to convert value 'v' for variable 'N' to type 'T'.`;
  for a secret `Failed to convert the value of secret variable 'N' to type 'T'.`
- **E-5** (L-6; at the read) `NAME='v': expected ALLOWED`, `ALLOWED` = `on or off` (`HERMETIC`, `NUGET_FETCH`, `CI`,
  `XAKE_CSC_SERVER`), `on, off or a number of seconds` (`CSC_SERVER`), `fail or warn` (`LOCK_STALE`); the `HERMETIC`,
  `CI`, `LOCK_STALE` texts are 3.13 (`HermeticMode.fs:66`, `Lock.fs:1347`, `Locks.fs:505`).
- **E-6** (S-5, G-4) `Variable 'N' comes only from the environment variable ENV, not from WHERE`; `WHERE` = `-d N=...`
  or `the ?vars of Demand`.
- **E-7** (T-3; fails the reading target) `Variable 'N' is read through a Var declaration that differs from its
  declaration by DECL in FIELDS: read it through that declaration, or use getVar "N"`; `DECL` = `varschema field F` or
  `var "N" [...]`; `FIELDS` the differing ones of `sources` (or `DefaultValue`), `scope`, `required`, `secret mode`, `type`.
- **E-8** (T-4; fails the reading target) `Variable 'N' is read through Var but is not declared: declare it in
  varschema, or use getVar "N" or getEnv "ENV"`; `ENV` the read's env name; for an `ArgOnly` read the text ends at
  `or use getVar "N"`.
- **C-1** A `Var` change reason stays `Global script variable N was changed 'a'->'b'` (`DependencyAnalysis.fs:101`);
  secrets keep the 3.13 masked reasons.

## 5. Data shapes

```fsharp
// namespace Xake, auto-opened
type VarSource = Env of string | Default of string | Secret | SecretHashed   // tried in list order; markers define nothing
[<RequireQualifiedAccess>]
type VarOrigin = Request | Cli | Script | Env of string | Default          // request | cli | script | env:<NAME> | default
type VarDecl = {
    Key: string; Sources: VarSource list   // Env/Default only, list order
    DefaultValue: obj option; Scope: LookupScope; Required: bool; Secret: SecretMode
    ValueType: System.Type; DeclaredBy: string }   // "var \"N\" [...]" | "varschema field F" (E-2, E-7)
type VarEntry = { Value: string option; Origin: VarOrigin option; Decl: VarDecl option }
// RulesBuilder
[<CustomOperation("var")>] member AddVar(script, name: string, value: string)              // unchanged
[<CustomOperation("var")>] member AddVar(script, name: string, sources: VarSource list)   // new
```

`VarSource.Env` coexists with the `Xake.Env` module (`Env.isUnix` still resolves); the overloaded custom operation
compiles on SDK 10 (owner's check). Where things live (no field on `ExecContext`, N-8):
- `ExecOptions.Vars` (type unchanged): script values only, duplicates kept for E-2.
- `ExecOptions.CliVars : (string * string) list` (new): the `-d` pairs, last first; `ApplyCommandLine` writes `-d`
  here instead of prepending to `Vars`.
- `ExecOptions.VarDecls : VarEntry list` (new): the declarations in script order, each with its op-time value and
  origin (R-3, S-6). `VarSchema` stays for `--help` (`VarHelp` unchanged).
- `EngineOptions.Variables : Map<string, VarEntry>` (new): the base set built at launch, for every key; Var reads
  (T-2), the banner and G-4 reach it as `ctx.Engine.Options.Variables`. `toEngineOptions` (`ScriptRunner.fs:6-14`)
  fills it; `EngineOptions.Default` has `Map.empty`.
- `ExecContext.Vars` (type unchanged): the set flattened, one pair per defined key with its final value (request
  pairs first in a demand). `Util.getVar`, `getVar`, the dependency check, `EXPLAIN_RULE`, `jobsLimit` use it.

`EngineOptions` and `ExecOptions` are not in the frozen list (`RecordShapeTests.fs:16-24`, release-process decision B);
`src/hermetic` builds neither, so a published hermetic package keeps running. Source compatibility: `{ X.Default with ... }` compiles unchanged; a full record expression of
`ExecOptions`/`EngineOptions` adds the new fields; code that looked for `-d` values in `options.Vars` after
`ApplyCommandLine` reads `CliVars`. `getVar`, `getEnv`, `var "N" "v"`, `Var.*`, `varschema`, `withDefault`,
`required`, `secret`, `secretHashed` keep their signatures. No database change (N-2).

## 6. Compatibility

### 6.1 What an existing 3.15 script sees

- **An inline Var read fails** (E-8) unless its key is declared by an equal field or rule: a `Var.*` value read in a
  recipe without `varschema`, or with only `-d`/`var "N" "v"`/`ExecOptions.Vars` for its key (3.15 read the `-d` value,
  `VarsTests.fs:579`). An inline `Var.env`/`Var.secret` has no key without its field name, so it always fails. Fix: put
  it in `varschema` and read the field, or use `getVar`/`getEnv`.
- A read through a declaration that differs from the stored one fails (E-7): typically `Var.string "nugetPackages"`
  against `var "NUGET_PACKAGES" [...]`, or the same key with another default. Read the field itself.
- Fields are evaluated at launch: a missing `required` value or a bad typed value fails the run before any target
  (3.15: when a recipe read it); `getVar` of a field's key sees its environment value or default; the field shows in
  the banner and the debug dump: **mark a credential field (e.g. `Var.env "NUGET_KEY"`) `|> secret`.**
- `-d` of a `Var.env` key is an error (3.15 ignored it); `-d` of any secret is an error (3.15 warned for argument-scope
  secrets); a constant `var`/`ExecOptions.Vars` next to a field or rule of the same key is an error (3.15: the constant won).
- Libraries no longer read the environment for build variables (L-2): a CI that exported `NUGET_PACKAGES`,
  `XAKE_CSC_SERVER`, `XAKE_*_JOBS`, `CI`, `LOCK_REFRESH` or `SOURCE_DATE_EPOCH` adds `var "N" [Env "N"]` or passes `-d`.
  Unset `NUGET_PACKAGES` is `<project root>/.packages`, not `~/.nuget/packages`.
- Engine `Demand(t, ?vars)` values now win over the script's values (they were shadowed).
- Dependencies: a target whose record has `Var (N, None)` + `EnvVar (E, v)` from a 3.15 Var read rebuilds once (the
  read now records `Var (N, Some v)`); a secret field's record moves from `SecretDep (E, SecretEnv, _)` to
  `SecretDep (N, SecretArg, _)`, one rebuild. `--resetdb` forces a clean state.

### 6.2 Release note (next minor of Xake; next minor of Xake.Hermetic.Dotnet)

> **Variables are evaluated once, before the build, by declarations the script states.**
> - New: `var "N" [Env "X"; Default "v"]`, sources tried in order; `Secret` / `SecretHashed` make it a secret.
> - `varschema` fields are evaluated at declaration: environment, default, `required` and type errors stop the run
>   before any target. A `Var.*` field read in a recipe returns that value; `getVar` sees it; the banner lists it.
> - Precedence: engine `?vars` (now above the script) > `-d` (last wins) > the script's one definition of the name.
>   Moving an equal value between the environment, `-d` and `var` no longer rebuilds.
> - **Breaking:** a `Var.*` read in a recipe needs a declaration of its key in `varschema` (or an equal `var [...]`);
>   otherwise use `getVar` (script values) or `getEnv` (environment). A name defined twice in the script, `-d` of a
>   secret and `-d` of a `Var.env` name stop the run.
> - **Breaking:** Xake.Dotnet no longer reads `NUGET_PACKAGES`, `XAKE_CSC_SERVER`, `XAKE_CSC_JOBS`, `XAKE_FSC_JOBS`
>   from the environment (add `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]` or pass `-d`); unset `NUGET_PACKAGES` is
>   `<project root>/.packages`; an invalid `CSC_SERVER`, `NUGET_FETCH`, `XAKE_CSC_SERVER` is an error; an empty
>   on/off value is the default (`-d CI=` is off).
> - The banner shows each variable once with its source; `--help` lists `var [...]` rules; `-v` shows variables, masked.
> - Xake.Hermetic.Dotnet no longer reads `CI`, `LOCK_REFRESH`, `SOURCE_DATE_EPOCH` from the environment: add
>   `var "CI" [Env "CI"]` and the like on CI.

## 7. Implementation split

1. **PR 1, core** (next Xake minor): §5 types and fields; the `var` overload; `CliVars`; op-time evaluation in `var`
   and `varschema` (R-3, S-6); launch overlay and checks in `Run`/`Start` (V, R-4, S-2..S-5, X-1, X-2, E-1..E-4, E-6);
   Var reads in `Vars.fs` (`resolveVar`, `Bind`) per T-1..T-4; `checkSecretVars` replaced (X-5); B-1..B-3; engine
   G-1..G-4. Tests `CommandLineTests.fs:473,548` inverted, `VarsTests.fs`/`SecretVarsTests.fs` inline reads moved to
   `varschema` or `getVar`; `build.fsx` `NUGET_KEY` made `|> secret`. Tests §8.1-8.7.
2. **PR 2, library readers**: Xake.Dotnet (same `Xake` package; ships in the same Xake release as PR 1) L-1..L-7 for
   `NUGET_PACKAGES`, `NUGET_FETCH`, `HERMETIC`, the `CSC_SERVER` pair, `*_JOBS`; docs (`csc-server.md`, the
   `HermeticMode.fs:155` hint). Xake.Hermetic.Dotnet (its next minor): `CI`, `LOCK_REFRESH`, `LOCK_STALE`,
   `SOURCE_DATE_EPOCH`, `Import.fs:1684`/`Project.fs:475` per L-3. Its range already goes up to 4.0
   (`[3.14.0.45, 4.0)`); its floor rises to the Xake release with PR 1, because the remedy `var "CI" [Env "CI"]`
   needs the overload. The published hermetic 0.10 keeps working on that release (N-8, L-4). Tests §8.8-8.9.

## 8. Conformance tests

One line each: given / when / then (rules). "Launch fails": the error is printed, exit 2, neither `setup` nor any target
runs. "The read fails": the launch succeeds, other targets run, the reading target fails with the text.

### 8.1 Chain, `var` lists, second definitions
1. Given `var "X" [Env "A"; Default "d"]`; when a recipe reads `getVar "X"` with `A=a` (resp. `A` unset, resp. `A=` on Unix); then `Some "a"`, banner `X=a (env:A)` (resp. `X=d (default)`, resp. `Some ""`) (R-2, R-3, V-2, V-4, K-1).
2. Given `var "X" [Env "A"; Env "B"; Default "d"]`, `A` unset, `B=b`; then `b (env:B)`; with also `-d x=1`, `getVar "X"` is still `b` and `getVar "x"` is `1` (V-2, V-5).
3. Given `var "X" [Env "A"]`, `A=a` and `-d X=c` (resp. `-d X=1 -d X=2`, resp. only `var "X" "s"` and `-d X=c`); then `c (cli)` (resp. `2 (cli)`, `c (cli)`) (V-2, R-1).
4. Given `ExecOptions.Vars = [X,a]` and `var "X" "b"`; then launch fails with E-2 `by ExecOptions.Vars and by var "X" "..."` (R-4; inverts `CommandLineTests.fs:473`).
5. Given `var "X" [Default "d"]` and `var "X" "s"` in either order (resp. two rules, two constant `var "X"`, `ExecOptions.Vars = [X,a; X,b]`); then launch fails with E-2 naming the first two in script order (R-4).
6. Given second definitions of `X` and of `Y`, one with a secret value; then both E-2 are printed together and no value is printed (R-4, §4).
7. Given `var "X" [Env "A"]` (resp. `var "X" []`), `A` unset; then `getVar "X"` = `None`, banner `X (not set)` (R-2, R-3, B-1).
8. Given `var "X" [Env "A"]` and field `Y = Var.string(envVar = "A")`, `A=a` when the ops apply; when `A=z` is set before `Run` (resp. by a recipe); then `getVar "X"` and `getVar "Y"` are `a` (V-3, S-6).

### 8.2 `varschema`
9. Given `varschema {| X = Var.string(envVar = "A") |> withDefault "d" |}`; then test 1 gives the same values and banner lines (S-1, V-1, B-1).
10. Given field `buildConfig = Var.string()`, env `BUILD_CONFIG=r`; then `buildConfig=r (env:BUILD_CONFIG)` (S-1, §2 key).
11. Given field `X = Var.arg()`, env `X=e`; then `X (not set)`; with `-d X=c`, `c (cli)` (S-1, S-5).
12. Given field `N = Var.int() |> withDefault 5`, nothing set; then `let! n = schema.N` = `5`, `getVar "N"` = `Some "5"`, banner `N=5 (default)` (S-1, S-3, T-2, K-1).
13. Given field `R = Var.string() |> required`, `R` unset; then launch fails with E-3 (S-2).
14. Given field `N = Var.int()` and `-d N=x` (resp. env `N=x`); then launch fails with E-4; with env `N=x` and `-d N=4`, no error and `4` (S-3).
15. Given field `X` and `var "X" [Default "d"]` (resp. `var "X" "s"`, `ExecOptions.Vars [X,s]`, a field of key `X` in a second `varschema`); then launch fails with E-2 naming `varschema field X` (S-4).
16. Given field `X = Var.env(envVar = "A")` and `-d X=c`; then launch fails with E-6 `not from -d X=...`; with `-d A=c`, no error and `X` from env `A` (S-5).
17. Given `--help`, an unset `required` field and `-d N=x` for a `Var.int` field; then help is printed, exit 0, no error (B-2).

### 8.3 Var reads
18. Given field `X = Var.string(envVar = "A")`, `A=v`; when a recipe reads `let! x = schema.X`; then `Some "v"`, recorded `Var (X, Some "v")`, no `EnvVar` (T-2, K-5); given field `X = Var.string() |> withDefault "d"`; when a recipe reads the inline `Var.string "X" |> withDefault "d"`; then `d` (T-1 on the resolved key, not the reference).
19. Given field `X = Var.string(envVar = "A")`; when an inline `Var.string("X", envVar = "B")` is read; then the read fails with E-7 `in sources` (T-3).
20. Given field `N = Var.int()` read as `Var.string "N"` (resp. field `R = Var.string() |> required` read as `Var.string "R"`, resp. field `T = Var.string() |> secretHashed` read as `Var.string "T" |> secret`); then E-7 `in type` (resp. `in required`, `in secret mode`) (T-3).
21. Given `var "X" [Env "X"; Default "d"]`; when a recipe reads `Var.string "X" |> withDefault "d"` (resp. `Var.string "X"`); then `d`, recorded `Var (X, Some "d")` (resp. E-7 `by var "X" [...] in sources`) (T-1, T-2, T-3).
22. Given only `-d Y=1` (resp. `var "Y" "1"`, `ExecOptions.Vars [Y,1]`, nothing); when a recipe reads `Var.string "Y"`; then the read fails with E-8 `... or use getVar "Y" or getEnv "Y"`; for `Var.arg "Y"` the text ends at `getVar "Y"` (T-4).
23. Given env `X=a` and no definition of `X`; then `getVar "X"` = `None` and `getEnv "X"` = `Some "a"`, recorded `EnvVar (X, Some "a")`, checked live (K-1, K-6); a recipe reading `getVar "X"` twice and `let! x = schema.X` records one `Var (X, _)` (K-4).

### 8.4 Dependencies
24. Given a target built with env `A=v` and `var "X" [Env "A"]` read by `getVar "X"` (resp. field `X` of env `A` read by `schema.X`); when rerun with no `A` and `-d X=v` (resp. `-d X=w`); then up to date (resp. rebuilt with C-1 `'Some "v"'->'Some "w"'`) (K-5, C-1).
25. Given a target reading a rule `[Env "A"; Default "d"]` (resp. a field `withDefault "d"`), `A` unset; when rerun with `"e"`; then rebuilt (K-5); given a 3.15 database with `Var (X, None)` + `EnvVar (A, Some "v")` and env `A=v`, field `X` of env `A`; then rebuilt once, then up to date (K-5, §6.1).

### 8.5 Secrets
26. Given `var "T" [Secret; Env "A"]` and `-d T=s` (resp. `-d A=s`); then launch fails with E-1a, `GIVEN` = `T` (resp. `A`), `ENV` = `A`, `s` masked in all output (X-2).
27. Given `var "T" [Secret; Default "x"]` and `-d T=s`; then launch fails with E-1b `-d T=...` (X-2).
28. Given `var "T" [Secret; Env "A"]` and `var "T" "s"` (resp. `ExecOptions.Vars [T,s]`); then E-2, `s` masked (X-2, R-4).
29. Given field `T = Var.secret(envVar = "A")` and `-d T=s` (resp. `-d A=s`); then launch fails with E-1a (X-2, S-5).
30. Given field `T = Var.string() |> secret` and `-d T=s`; then launch fails with E-1a, `ENV` = `T`, no warning (X-2, X-5).
31. Given `var "T" [Secret; Env "A"]` (resp. field `T = Var.secret(envVar = "A")`), `A=s`; then banner `T=*** (env:A)`, `s` in no log line, a read records `SecretDep (T, SecretArg, SecretPresent)` (X-1, X-3, X-4, T-2).
32. Given `[SecretHashed; Env "A"]` and a changed `A`; then rebuilt with the masked reason; with `[Secret; Env "A"]` up to date; with `A` unset, rebuilt (X-4).
33. Given `[Secret; SecretHashed; Env "A"]`; then the mode is HMAC (R-2); given `var "T" [Secret; Default "x"]`, `--help` lists `T` with `(secret)` and no default (X-3, B-2).
34. Given `var "T" [Secret; Env "A"]`, `A=s`; when a recipe calls `getVar "T"` and `getEnv "A"`; then both return `s`, `s` in no log line, `SecretDep` recorded for each (X-7, X-4).
35. Given field `T = Var.int() |> secret` and env `T=x`; then launch fails with the secret E-4 text, `x` printed nowhere (S-3, X-3).

### 8.6 Banner, help, debug dump
36. Given `-d A=1 -d X=1 -d X=2`, `var "B" "2"`, `var "C" [Env "E"]` with `E=3`, field `D = Var.int() |> withDefault 4`, `var "F" [Env "G"]` with `G` unset; then the vars section is exactly `A=1 (cli)`, `B=2 (script)`, `C=3 (env:E)`, `D=4 (default)`, `F (not set)`, `X=2 (cli)` (B-1).
37. Given fields, `var "R" [Env "A"; Env "B"; Default "d"]` and `var "K" "v"`; then `--help` lists the fields, then `-d R=<value> [string], env: A, B, default: d`, and not `K` (B-2).
38. Given `-v Debug`, `-d P=1`, field `T = Var.secret(envVar = "A")` with `A=s`, `var "K" [Secret; Default "x"]`; then the dump shows `("P", "1")`, `VarDecls` with `***` for `T` and `K`, and neither `s` nor `x` (B-3, X-1, X-3).

### 8.7 Engine
39. Given `xakeEngine { var "V" "s"; ... }`; when `Demand("t", ["V","r"])`, then a plain `Demand("t")`; then `r`, then `s` (G-2; inverts `CommandLineTests.fs:548`).
40. Given two concurrent demands with `["V","r1"]` and `["V","r2"]`; then each sees its own value (G-3).
41. Given `var "T" [Secret; Env "A"]` (resp. field `E = Var.env()`, resp. field `N = Var.int()`); when `Demand("t", ["T","x"])` (resp. `["E","x"]`, `["N","x"]`); then the demand fails with E-1b `the ?vars of Demand` (resp. E-6, E-4), no target runs (G-4).
42. Given field `N = Var.int() |> withDefault 1`; when `Demand("t", ["N","7"])` and `t` reads `schema.N`; then `7` (G-2, T-2).
43. Given `var "X" [Env "A"]`, `A=a` when the script is built; when `A` changes and `Demand` runs; then `getVar "X"` = `a` and `getEnv "A"` the new value (G-1, V-3, K-6).
44. Given `xakeEngine { var "X" "a"; var "X" "b" }` (resp. an unset `required` field); when `start`; then `Start` raises `XakeException` with E-2 (resp. E-3) (G-4).
45. Given `XakeEngine.Start(EngineOptions.Default)`; when a recipe reads `Var.string "X"`; then the read fails with E-8 (G-1, T-4).

### 8.8 Xake.Dotnet
46. Given env `NUGET_PACKAGES=/m`, no `var`/`-d`; then `packageRoot ()` = `<root>/.packages`, the child restore gets `NUGET_PACKAGES=<root>/.packages`, source `ScriptVariable`, `HERMETIC=on` accepts it (L-2, L-3, L-4).
47. Given `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]`, env `/m`; then `/m`, `ScriptVariable`, accepted by `HERMETIC=on` (L-4).
48. Given `-d NUGET_PACKAGES=` (resp. `-d NUGET_PACKAGES=pkgs`); then `<root>/.packages` (resp. `<root>/pkgs`) (L-4).
49. Given env `XAKE_CSC_SERVER=0` only; then the compiler server is used; with `-d XAKE_CSC_SERVER=0`, in-process; with also `-d CSC_SERVER=30`, keepalive 30 (L-2).
50. Given `-d CSC_SERVER=bogus` (resp. `var "CSC_SERVER" [Env "S"]` with `S=bogus`, resp. `-d XAKE_CSC_SERVER=maybe`); then the compiling target fails with E-5 `CSC_SERVER='bogus': expected on, off or a number of seconds` (resp. same, resp. `expected on or off`) (L-6).
51. Given `-d CSC_SERVER=` and `-d XAKE_CSC_SERVER=0`; then in-process; given `-d XAKE_CSC_SERVER=` only, the server (L-7); given `-d NUGET_FETCH=bogus` (resp. `-d HERMETIC=bogus`); then E-5 at the read (resp. the 3.13 text); given `-d NUGET_FETCH=` (resp. `-d HERMETIC=`), fetching on (resp. off) (L-6, L-7).
52. Given env `XAKE_CSC_JOBS=2` only; then no limit; with `var "CSC_JOBS" [Env "XAKE_CSC_JOBS"]`, limit 2 and no dependency recorded; with `-d CSC_JOBS=x`, the 3.13 warning and no limit (L-2).
53. Given env `XAKE_CSC_SERVER=0`; then `Csc.RunOptions.Default.Server = Shared None` (L-5).
54. Given the sources of `src/dotnet`; then no `getEnv`, `GetEnvironmentVariable` or Var read names a variable of L-2 (L-1; a source scan).

### 8.9 Xake.Hermetic.Dotnet
55. Given env `CI=true`, no `var`; then `underCi` = false; with `var "CI" [Env "CI"]`, true; with `-d CI=bogus`, E-5 (L-2, L-6).
56. Given `-d CI=` (resp. `var "CI" [Env "CI"]` with env `CI=` on Unix, resp. `var "CI" " "`); then `underCi` = false, no error (L-7).
57. Given a stale lock and `-d LOCK_STALE=`; then it fails as with `fail`; with `-d LOCK_STALE=x`, the 3.13 E-5 text (L-6, L-7); given env `LOCK_REFRESH=all` only; then no refresh; with `var "LOCK_REFRESH" [Env "LOCK_REFRESH"]`, refresh (L-2).
58. Given env `SOURCE_DATE_EPOCH=1700000000` only; then pack and SBOM use 1980-01-01; with `var "SOURCE_DATE_EPOCH" [Env "SOURCE_DATE_EPOCH"]`, 2023-11-14 (L-2).
59. Given the sources of `src/hermetic`; then no `getEnv`, `GetEnvironmentVariable` or Var read names a variable of L-2 (L-1).
60. Given the published hermetic 0.10 against `dev` with PR 2 (CI job `hermetic-compat`), `NUGET_PACKAGES` unset, `HERMETIC=on`; then the child restore gets `<root>/.packages`, the policy accepts it, and `Record shapes frozen until Xake 4` passes unchanged (L-4, N-8, §5).

## 9. Open points

None.
