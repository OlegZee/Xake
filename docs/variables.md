# Working with variables

Build variables carry the knobs of a build: a configuration, a version, a package folder, a
token. A script declares them, Xake computes every value **once at launch**, and recipes only
read. This page is the whole story: examples first, then the reference. Behaviour is that of
Xake 4.0.

## Examples

A constant, a rule with sources, a secret, typed fields:

```fsharp
open Xake

let vars = {|
    config = Var.string(cliArg = "config") |> withDefault "Debug"   // -d config=Release
    jobs   = Var.int()                                              // int option
    token  = Var.secret<string>() |> required                       // XAKE__TOKEN only, never logged
|}

do xakeScript {
    varschema vars                                          // declares the fields; --help lists them
    var "VERSION" "1.0.0"                                   // a constant
    var "OUT" [Cli "out"; Default "bin"]                    // -d out=..., else $XAKE__OUT, else "bin"
    var "FEED" [Env "ACME_FEED"; Default "https://feed.example/v3"]  // explicit env name

    rules [
        "main" => recipe {
            let! config = vars.config                       // a Var read, by declaration
            let! version = getVar "VERSION"                 // the variable set
            let! home = getEnv "HOME"                       // the live environment
            do! trace Message "%s %A %A" config version home
        }
    ]
}
```

```bash
dotnet fsi build.fsx -- -- main -d config=Release -d VERSION=2.0.0
XAKE__TOKEN=s3cret dotnet fsi build.fsx -- -- main
dotnet fsi build.fsx -- -- --help        # lists the fields and rules, evaluates nothing
```

The same value reached through the environment, `-d` or the script does not rebuild anything:
a read records the final value, not where it came from.

## The two ways to declare

**A script constant**, `var "N" "v"`, is a value the script states. `ExecOptions.ScriptVars` entries
(the options given to `xake`/`xakeArgs`) are constants too.

**A rule**, `var "N" [sources]`, says where a value comes from. The sources are tried in list
order, the first defined wins:

| Source | Meaning |
|---|---|
| `Env "X"` | the environment variable `X`, when present (an empty value counts) |
| `Default "v"` | always defined |
| `Cli "ext"` | not a value: the name's command-line name is `ext` (`-d ext=...`) |
| `Secret` / `SecretHashed` | not a value: the name is a secret ([Secrets](#secrets)) |

A rule with no defining source leaves the name unset (`getVar` returns `None`). Markers may
stand anywhere in the list.

**`varschema`** declares typed fields, `Var.string`/`int`/`bool`/`create<'t>`, each the rule of
its sources plus a type, a `required` flag and a secret mode. `varschema` imports every `Var.*`
field of a record or anonymous record completely, so the same record serves `--help`, the launch
evaluation and the reads.

| Field | Reads |
|---|---|
| `Var.string/int/bool/create(name, cliArg, envVar, description)` | `-d` and the environment |
| `Var.env<'t>(envVar, description)` | the environment only; `-d` of it stops the run |
| `Var.arg<'t>(cliArg, description)` | `-d` only, no environment source |
| `Var.secret<'t>(envVar, description)` | the environment only, a secret |

Modifiers: `required` (the run stops if the field has no value), `withDefault v` (a typed
default, returned as is), `describe "text"` (the description in `--help`), `secret`,
`secretHashed`. A value that does not convert to the field's type (`int`, `bool`) stops the run.

### Keys and names

A declaration has three names:

- the **key**: the field's `name`, else its field name; for a rule the name given to `var`. It is
  what `getVar`, the banner, per-demand variables and the dependency records use;
- the **command-line name**: `cliArg` / `Cli "ext"`, else the key. It is what `-d` uses;
- the **environment name**: `envVar` / `Env "X"`, else the implicit name below.

With `p = Var.string(cliArg = "pkg")`, `-d pkg=v` sets the variable `p` and `getVar "p"` reads
it; `-d p=v` stops the run (E-9). A rule with two `Cli` sources (`var "P" [Cli "a"; Cli "b"]`)
is E-10; a rule and a field of one key are a second definition (E-2).

## The environment source `XAKE__<KEY>`

Every declaration that names no environment variable of its own reads the environment variable
`XAKE__<KEY>` first, before its `Default`. The name is the key in UPPER_SNAKE: a `_` between a
lower-case and an upper-case letter, then upper case, and every character other than `A-Z`,
`0-9` and `_` replaced by `_`. Only a lower-case letter followed by a capital splits (the 3.15
rule): `config2Path` gives `XAKE__CONFIG2PATH`, not `XAKE__CONFIG2_PATH`.

| Declaration | Reads |
|---|---|
| `var "CONFIG" [Default "Debug"]` | `XAKE__CONFIG`, then `Debug` |
| `var "CONFIG" []` | `XAKE__CONFIG` |
| `buildConfig = Var.string()` | `XAKE__BUILD_CONFIG` |
| `var "netfx-target.v2" []` | `XAKE__NETFX_TARGET_V2` |
| `var "VERSION" "1.0.0"` (a constant, also an `ExecOptions.ScriptVars` entry) | `XAKE__VERSION`, else `1.0.0` |
| `var "X" [Env "A"; Default "d"]`, `Var.string(envVar = "A")` | `A` only: an explicit env name replaces the implicit source |
| `Var.arg()` | no environment source at all |

Rules of the prefix:

- **Only declared names get one.** Xake does not scan the environment for `XAKE__*`: an
  `XAKE__Z` that no declaration or constant names is not in the variable set. `getVar` of a
  name that nothing in the script defines finds only `-d` and engine values.
- **A constant stays a constant** for everything else: it is not a declaration for a `Var` read,
  `--help` does not list it, it is not a secret. The environment only overrides its value.
- **Collisions are errors.** Two keys with the same implicit name (`a-b` and `A_B`, `fooBar` and
  `FOO_BAR`, a constant `x` and a rule `X`) stop the run (E-12); constants and `ExecOptions.ScriptVars`
  entries count. Declarations with an explicit env name or no env source (`Var.arg`) take no
  part, except a rule whose explicit `Env` is its own implicit name.
- **Secrets travel this way.** `var "T" [Secret]` reads `XAKE__T`; `-d T=...` and
  `-d XAKE__T=...` are E-1a ("set XAKE__T instead").
- The banner shows the source as `env:XAKE__<KEY>`; `--help` shows the environment name of
  every rule and field, implicit or explicit.

## When values are computed

Once, at launch: after the command line is applied, before `setup` and the first target, for a
run, `--dryrun`, `--dump` and an engine `start`. `--help` evaluates nothing. A missing `required`
value, a value of the wrong type, a second definition of a name, a `-d` of a secret all stop the
run **before any target**, with every error collected and printed together (exit code 2,
`XakeException` with `ThrowOnError`).

The value of a name is the first defined of:

1. the engine's per-demand variables, `Demand(target, vars)` (engine mode only);
2. `-d NAME=value` (the last `-d` of a name wins);
3. the script's one definition: the constant `var "N" "v"` or the `ExecOptions.ScriptVars` entry,
   unless `XAKE__N` is present;
4. the declaration's sources in list order (the implicit `XAKE__<KEY>` first, unless the
   declaration names an `Env` itself).

A name is **defined once** in the script: two `var` ops, a `var` and an `ExecOptions.ScriptVars`
entry, a `varschema` field and either of them, are E-2. `-d` and per-demand variables are not
script definitions.

## Reading

| Read | Returns | Dependency recorded |
|---|---|---|
| `let! v = getVar "N"` | the value from the variable set (`string option`) | `Var (N, value)` |
| `let! v = getEnv "X"` | the live environment variable `X` | `EnvVar (X, value)` |
| `let! v = vars.field` | the field's typed value (`'t option`, or `'t` after `required`/`withDefault`) | `Var (key, value)`; a secret: `SecretDep` |
| `let! v = Var.read<'t> "N"` | the value of a name, for a library | as `getVar` |

**A `Var` read needs a declaration.** `vars.field` returns the stored value of an *equal*
declaration of its key. Without any declaration the reading target fails (E-8); with a
different one (another env name, default, type, required flag, secret mode, command-line name)
it fails too (E-7). It reads no environment itself. Use `getVar` for script values and `getEnv`
for the environment.

**`Var.read<'t> "NAME"`** is the read for library code. A declared name is read through the
script's declaration, whatever its sources and default; only the type is checked (E-11). An
undeclared one is `getVar`. It never reads the environment.

### Importing a library's declarations

Libraries read their variables without declarations. To list them in `--help` and the banner,
import their schema, and override a field by copy-and-update:

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

xakeScript {
    varschema DotNetVars.Default                       // NUGET_PACKAGES, NUGET_FETCH, ... (strings, -d only)
    varschema { HermeticVars.Default with CI = Var.string(envVar = "CI") |> withDefault "off" }
    ...
}
```

Importing changes no value; a target built before the import rebuilds once (its dependency
records the library's default).

## Dependencies: what rebuilds

- A read records one `Var (name, value)`: the **final value**, never the source. Moving an equal
  value between `XAKE__N`, `-d` and `var` rebuilds nothing; a changed value rebuilds the targets
  that read it ("Global script variable N was changed 'a'->'b'").
- Repeated reads of one name in a recipe record one dependency (the check is a `List.contains`
  over the target's dependencies, O(deps) per read).
- `getEnv` records `EnvVar`; a secret records its presence or digest, never the value.
- Variables a library reads without recording (`*_JOBS`) change no output and never rebuild.

## Secrets

A token must not end up in the database or the log. Mark the name secret: `Secret` /
`SecretHashed` in a rule, `Var.secret`, `|> secret` or `|> secretHashed` on a field. The
declaration decides, for the whole run: its key and environment names are secret everywhere,
also for a plain `getEnv`.

- **Where the value comes from.** Only its own sources: `XAKE__<KEY>` (the normal way) or an
  explicit `Env`. `-d` of a secret, under its key, command-line name or any env name, stops the
  run (E-1a: "set XAKE__T instead of passing -d"; E-1b for a secret with no env source, i.e.
  `Var.arg ... |> secret`); a per-demand value of a secret name is E-1b; a script constant or
  `ExecOptions.ScriptVars` entry of a secret name is a second definition (E-2).
- **Masking.** Every logger Xake creates (console, `filelog`, `CustomLogger`, a custom `ILogger`
  given in the options) replaces each secret value of **4 or more characters** with `***`: the
  banner, the Debug dump, change reasons, `--dryrun`, `--dump`, tool output, errors. A value
  shorter than 4 characters, a value a tool transforms (base64, URL-encoded) and anything
  written outside the logger (a file the script writes) are not masked.
- **The database** keeps only whether the variable was set (`secret`: a rotated token rebuilds
  nothing; unset to set rebuilds), or a salted HMAC-SHA256 of the value (`secretHashed`: a
  changed value rebuilds the readers). The change reason names the variable only.
- `getVar`/`getEnv` of a secret return the real value; masking applies to output.
- Children inherit the environment, so an environment secret reaches tools as it is; prefer it
  to command-line arguments, which tools echo.
- A conversion error of a secret does not quote the value; `--help` shows "(secret)" and never a
  default.

## The banner

At `Info` (in `build.log`) the run banner lists each variable once with its source:

| Source | Meaning |
|---|---|
| `cli` / `cli:<name>` | `-d`, under the key / under another command-line name |
| `script` | a constant or `ExecOptions.ScriptVars` entry |
| `env:<NAME>` | an environment variable, e.g. `env:XAKE__CONFIG` |
| `default` | a `Default` source |
| `request` | a per-demand value (engine mode) |
| `(not set)` | declared, no value |

Secrets print as `***`.

## Library variables

Xake.Dotnet and Xake.Hermetic.Dotnet read **only the variable set**, never the environment. A
CI that wants the environment declares it: `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]`.
Switch values are strict (anything but the listed ones fails the reading target); an empty
value is the default.

| Variable | Meaning | Values, default |
|---|---|---|
| `NUGET_PACKAGES` | the build's package folder (relative to the project root, or absolute) | default `<project root>/.packages` |
| `NUGET_FETCH` | allow downloads | `on`/`off`, `true`/`false`, `yes`/`no`, `1`/`0`; default on |
| `HERMETIC` | the hermetic gate | `on`/`off`; default off |
| `CSC_SERVER` | compiler server | `on`, `off`, keepalive seconds |
| `XAKE_CSC_SERVER` | CI switch for the server (`CSC_SERVER` wins) | `on`/`off` and the spellings above |
| `CSC_JOBS` / `FSC_JOBS` | max concurrent compilers | non-negative integer; `0`/unset: unlimited |
| `CI` | a missing lock fails instead of being recorded | `on`/`off` spellings; empty: off |
| `LOCK_REFRESH` | ids to re-resolve in `update-locks` | list or `*` |
| `LOCK_STALE` | a stale description | `fail` (default), `warn` |
| `SOURCE_DATE_EPOCH` | the timestamp of `Pack.Options.current ()` | unix seconds |

To take a value from the environment under its own name, declare it explicitly
(`var "CSC_JOBS" [Env "XAKE_CSC_JOBS"]`, `var "CI" [Env "CI"]`). See
[dotnet-build.md](dotnet-build.md#where-packages-go), [csc-server.md](csc-server.md) and
[hermetic/](hermetic/index.md).

## Pinning values: tests and embedding

A test, or a program that embeds Xake, may want variable values that nothing outside the
program can change. Two `ExecOptions` flags close the outside layers; neither has a
command-line flag (they are set in code):

- `IgnoreCommandLine = true` skips the command line: no `-d`, no options, no targets from it.
  `xakeEngine` sets it.
- `IgnoreEnvironment = true` (default `false`) stops every declaration from reading the
  environment at launch: neither `XAKE__<KEY>` nor an explicit `Env "X"` / `envVar = "X"`. A
  value then comes from the per-demand values, `-d` (unless `IgnoreCommandLine`), the constant,
  then `Default` / the typed default; a `required` declaration without one stops the run (E-3).
  A secret or `Var.env` field that only the environment could set is unset (no error by itself;
  `-d` of it still stops the run). The collision check E-12 is skipped. `getEnv` in a recipe
  still reads the environment: it is an explicit read, not a declaration source. The banner
  shows `environment:  ignored (IgnoreEnvironment)` and no `env:` origin. `xakeEngine` leaves it
  `false`.

```fsharp
do xakeArgs [] { ExecOptions.Default with IgnoreCommandLine = true; IgnoreEnvironment = true } {
    var "CONFIG" [Env "BUILD_CONFIG"; Default "Release"]   // always "Release"
    ...
}
```

Per-demand values of an engine (`Demand(target, ["N", "v"])`) still win over everything: they
are the program's own input. A single field can also be closed to the environment on its own:
`Var.arg` has no environment source at all (only `-d` and its default).

## For tool authors

| Member | Holds |
|---|---|
| `ExecOptions.ScriptVars` | script constants only (`(name * value) list`); `Vars` before 4.0 |
| `ExecOptions.CliVars` | the `-d` pairs (they no longer go into the script values) |
| `ExecOptions.VarDecls` | the declarations (`VarDecl`: key, command-line name, sources, default, scope, required, secret mode, type, declared-by) |
| `ExecOptions.VarDefinedBy` | where each `var`/`varschema` definition came from, for E-2 |
| `ExecOptions.IgnoreEnvironment` | `true`: no declaration reads the environment at launch (see above) |
| `EngineOptions.Variables` | the variable set built at launch: `Map<string, VarEntry>` (value, `VarOrigin`, declaration) |
| `ExecContext.Vars` | the `(name, value)` pairs a recipe reads |

New types: `VarSource`, `VarOrigin`, `VarDecl`, `VarEntry`. Code that builds `ExecOptions` or
`EngineOptions` with a full record expression breaks (use `with`). `XakeEngine.Start` raises
`XakeException` on a launch error; a per-demand value for a secret, an environment-only or a
mistyped variable fails the demand before any target.

## Errors

Launch errors are collected, printed together at `Error`, and fail the run (exit code 2) before
`setup` and any target. A diagnostic never prints a secret's value or a default.

| Id | Text | What to do |
|---|---|---|
| E-1a | `Variable 'N' is secret: secret variables come from the environment, set ENV instead of passing -d N=...` | pass it as `XAKE__<KEY>` or its `Env` |
| E-1b | `Variable 'N' is secret: its value comes only from its declaration's sources, not from WHERE` | remove the `-d` (secret without env source) or per-demand value |
| E-2 | `Variable 'N' is defined twice in the script: by A and by B; define it once` | keep one definition |
| E-3 | `Required variable 'N' is not provided` | pass it or give a default |
| E-4 | `Failed to convert value 'v' for variable 'N' to type 'T'.` | fix the value |
| E-5 | `NAME='v': expected ALLOWED` (reading target) | use an allowed switch value |
| E-6 | `Variable 'N' comes only from the environment variable ENV, not from WHERE` | set the environment variable |
| E-7 | `Variable 'N' is read through a Var declaration that differs from its declaration by DECL in FIELDS: read it through that declaration, or use getVar "N"` | read through the script's field |
| E-8 | `Variable 'N' is read through Var but is not declared: declare it in varschema, or use getVar "N" or getEnv "ENV"` | declare it, or use `getVar` |
| E-9 | `-d KEY: the variable is set on the command line as EXT; use -d EXT=...` | use the command-line name |
| E-10 | `Variable 'N' has more than one command-line name: A and B` | align `cliArg` / `Cli` |
| E-11 | `Variable 'N' is read as 'T' but is declared by ... as 'U': declare it as 'T'` (`Var.read`) | read it as the declared type |
| E-12 | `Variables 'A' and 'B' both read the environment variable XAKE__N; rename one, or give one an explicit Env source` | rename a key or give one an `Env` |

## Breaking changes since 3.15

| 3.15 | 4.0 |
|---|---|
| a `Var.*` read worked without a declaration and read the environment | declare the key in `varschema` (E-8); use `getEnv` for the environment |
| field key = `cliArg` | key = `name`, else the field name; `cliArg` is only the `-d` name |
| a field without `envVar` read `BUILD_CONFIG` | reads `XAKE__BUILD_CONFIG`; keep the old name with `envVar = "BUILD_CONFIG"`, or export the new one |
| a constant `var` ignored the environment | `XAKE__<NAME>` overrides it |
| the later `var` replaced the earlier; `var` beat `ExecOptions.Vars` | a second definition stops the run (E-2) |
| `-d` of a secret warned; `-d` of `Var.env` was ignored | both stop the run; pass secrets as `XAKE__<KEY>` |
| `-d` pairs went into `ExecOptions.Vars` | into `ExecOptions.CliVars` |
| `ExecOptions.Vars` | `ExecOptions.ScriptVars` (`{ ExecOptions.Default with ScriptVars = [...] }`) |
| per-demand variables were shadowed by the script | they win |
| Xake.Dotnet read `NUGET_PACKAGES`, `XAKE_CSC_SERVER`, `XAKE_CSC_JOBS`, `XAKE_FSC_JOBS` from the environment | `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]`, `var "CSC_JOBS" [Env "XAKE_CSC_JOBS"]`, ... or `-d` |
| package folder: `NUGET_PACKAGES`, else `~/.nuget/packages` | `<project root>/.packages` |
| a bogus `NUGET_FETCH` / `CSC_SERVER` was a warning or "on" | the reading target fails |
| hermetic read `CI`, `LOCK_REFRESH`, `SOURCE_DATE_EPOCH` from the environment | `var "CI" [Env "CI"]`, or `-d` |
| banner labelled variables `script/cli` | one source per variable (see above) |

A target recorded by 3.15 (a `Var` read, a secret) rebuilds once; the database format is
unchanged (0.6). Release notes: [release-notes.md](release-notes.md).
