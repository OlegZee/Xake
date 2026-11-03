# Xake tasks

The examples below use current API style:

```fsharp
#r "nuget: Xake"
open Xake
open Xake.Tasks
```

## Shell tasks

### sh recommended

`sh` fails on non zero exit code by default.

```fsharp
do! sh "dotnet build src/core -c Release" { () }
```

`{ () }` is the empty settings block. The shorter `{}` also works, but only from F# 9 onward;
`{ () }` compiles on every SDK the package supports.

With arguments and working directory:

```fsharp
do! sh "dotnet" {
  args ["test"; "src/tests"; "-c"; "Release"]
  workdir "."
  failonerror
}
```

Capture output:

```fsharp
let! code, lines = sh "dotnet --list-sdks" { resultAndOutput }
```

## Copy tasks

Copy by file mask:

```fsharp
do! cp {file "bin/*.dll"; todir "deploy"}
```

Copy directory tree:

```fsharp
do! cp {dir "bin"; todir "deploy"}
```

Copy from fileset:

```fsharp
do! cp {
  files (fileset {
    basedir "bin"
    includes "*.dll"
    includes "*.exe"
  })
  todir "deploy"
}
```

`dir`, `file` and `files` may repeat and combine in one `cp`; sources copy in order (a `file`
source is always flattened):

```fsharp
do! cp {
  dir "bin"
  dir "content"
  file "README.md"
  todir "deploy"
}
```

A fileset or a string on a line of its own is a source too: a fileset as `files`, a string as
`file` (flattened). Entries and operations mix in any order:

```fsharp
do! cp {
  !!"bin/*.dll" -- "bin/*.Tests.dll"
  "README.md"
  todir "deploy"
}
```

Other copy helpers:

```fsharp
do! copyFile "src/App.config" "out/App.config"
"out/config.json" ..> copyFrom "src/config.json"
```

## Remove tasks

`del` is an alias for `rm`.

Delete the files of a fileset, written on its own line:

```fsharp
do! rm { !!"out/*.*" }
do! rm { !!"out/*.*" -- "out/keep.txt" }
```

Delete file or mask:

```fsharp
do! rm {file "temp/*.tmp"}
do! rm { "temp/*.tmp" }      // the same: a string entry is a file mask
```

Delete directory:

```fsharp
do! rm {dir "out"}
```

Delete files from fileset:

```fsharp
do! rm {
  files (fileset {
    basedir "out"
    includes "**/*.cache"
  })
  verbose
}
```

`dir`, `file` and `files` may repeat and combine in one `rm`, together with fileset and string
entries, in any order; targets apply in order, and a fileset that matches nothing is not an error:

```fsharp
do! rm {
  dir "out"
  dir "obj"
  file "*.tmp"
  !!"logs/**/*.log" -- "logs/keep/**"
  "build.cache"
  verbose
}
```

A string entry is a file mask (as `file`), never a directory: use `dir` to delete a folder.

## .NET tasks

The `csc`, `fsc`, `msbuild`, `resgen` and `resourceset` builders, their script variables
(`NETFX`, `NETFX-TARGET`, `CSC_SERVER`, `CSC_JOBS`/`FSC_JOBS`, `CSC_TOOLSET`, `NUGET_PACKAGES`,
`NUGET_FETCH`, `HERMETIC`, ...) and the hermetic library (locks, restore, SBOM, verify, pack,
signing) are not part of the `Xake` package from 4.0: they are the `Xake.Dotnet` package,
built in its own repository, [OlegZee/Xake.Dotnet](https://github.com/OlegZee/Xake.Dotnet), with their reference and
samples.

```fsharp
#r "nuget: Xake.Dotnet"   // brings the Xake package with it
open Xake
open Xake.Dotnet
```

## Inner recipe helpers

Common helpers used inside `recipe`:

- `need`
- `needFiles`
- `dependsOn`
- `trace`
- `getVar`
- `getEnv`
- `getCtxOptions`
- `getTargetFile`
- `getTargetFullName`
- `hasRule` (whether a rule would build a target, without building it)

Example:

```fsharp
recipe {
  do! dependsOn !! "src/**/*.fs"
  let! cfg = getVar "Config"
  do! trace Info "Config: %A" cfg
}
```

## Script variables

Every variable value is computed once, at launch: after the command line is applied and before
`setup` and the first target (run, `--dryrun`, `--dump`, engine `start`). A recipe only reads. A
read records the final value as one `Var (name, value)` dependency, never where it came from, so
moving an equal value between the environment, `-d` and the script does not rebuild.

The value of a name comes from the first of (first defined wins):

1. the engine's per-demand `Demand(target, vars)` (engine mode only);
2. `-d NAME=value` on the command line (the last `-d` of a name wins);
3. the script's one value: `var "NAME" "value"` or an `ExecOptions.ScriptVars` entry, unless the
   environment variable `XAKE__NAME` is present (it overrides the constant);
4. the name's declaration: a `var` rule or a `varschema` field, its sources in list order, the
   implicit `XAKE__NAME` first unless the declaration names an environment variable itself.

A **`var` rule** declares where a value comes from:

```fsharp
xakeScript {
    var "CONFIG" "Release"                                            // a constant
    var "NUGET_PACKAGES" [Env "XX_NUGET_PACKAGES"; Default ".packages"]  // first defined source wins
    var "OUT" [Cli "out"; Env "OUT_DIR"; Default "bin"]              // set with -d out=..., not -d OUT=...
    var "TOKEN" [Secret; Env "ACME_API_TOKEN"]                       // a secret, see below
    ...
}
```

`Env "X"` defines the name when the environment variable `X` is present (an empty value
included), `Default "v"` always. `Cli "ext"` gives the name another command-line name; `Secret`
and `SecretHashed` make it a secret. These markers define nothing and may stand anywhere in the
list. A rule with no defining source leaves the name unset (`getVar` returns `None`).

**The implicit environment source.** A declaration that names no environment variable reads
`XAKE__<NAME>` first, `NAME` being its key in UPPER_SNAKE with any character other than a letter,
digit or `_` replaced by `_`: `var "CONFIG" [Default "Debug"]` takes `XAKE__CONFIG`, then
`Debug`; `var "netfx-target" []` takes `XAKE__NETFX_TARGET`; a constant `var "VERSION" "1.0"` is
overridden by `XAKE__VERSION`. An explicit `Env "X"` (or `envVar = "X"` on a field) replaces it.
Only declared names get one: a `XAKE__FOO` that no declaration or constant names is ignored. Two
keys of one implicit name (`a-b` and `A_B`) stop the run.

A **name is defined once** in the script: two `var` ops of a name (either form), a `var` op and an
`ExecOptions.ScriptVars` entry, or a `varschema` field and either of them stop the run before any target
(`Variable 'N' is defined twice in the script: by A and by B; define it once`). `-d` and the
engine's per-demand variables are not script definitions.

**Typed variables** are declared in `varschema`; a field is the rule of its sources:
`Var.string(envVar = "X") |> withDefault "d"` as field `N` is `var "N" [Env "X"; Default "d"]`,
plus its type, `required` flag and secret mode:

```fsharp
let vars = {|
    config = Var.string(cliArg = "config") |> withDefault "Debug"   // -d config=..., env XAKE__CONFIG
    jobs   = Var.int(envVar = "XAKE_JOBS")                          // 't option
    feed   = Var.env<string>(envVar = "PACKAGE_FEED") |> required   // environment only
|}

xake ExecOptions.Default {
    varschema vars          // declares them; names, types, defaults and descriptions in --help
    phony "main" (recipe {
        let! config = vars.config
        ()
    })
}
```

- `Var.create`/`string`/`int`/`bool` take `-d` and the environment; `Var.env` only the
  environment (`-d` of it stops the run), `Var.arg` only `-d`. `describe` adds a description.
- The **key** of a field (what `getVar`, the banner and per-demand variables use) is its `name`,
  else the field name. Its **command-line name** is `cliArg`, else the key: with
  `p = Var.string(cliArg = "pkg")`, `-d pkg=v` sets `p`, and `-d p=v` stops the run
  (`-d p: the variable is set on the command line as pkg; use -d pkg=...`). The environment name
  is `envVar`, else `XAKE__` + the key in UPPER_SNAKE_CASE (`buildConfig` ->
  `XAKE__BUILD_CONFIG`; 3.15 read `BUILD_CONFIG`). `Var.arg` reads no environment variable.
- A `required` field without a value, or a value that does not convert to the field's type,
  stops the run before any target. `withDefault` gives a value instead; a typed default is
  returned as is.
- **A `Var` read needs a declaration.** `let! x = vars.config` returns the value of the key from
  the variable set; it reads no environment itself. A `Var.*` value read in a recipe without a
  `varschema` field (or `var` rule) of its key fails that target ("... is read through Var but is
  not declared: declare it in varschema, or use getVar ..."); one whose declaration differs from
  the stored one (another env name, default, type, ...) fails too. Read script values with
  `getVar "N"` and the environment, live, with `getEnv "X"` (recorded as `EnvVar`).

The run banner (at `Info`, in `build.log`) lists each variable once with its source: `cli` (or
`cli:<name>` when given under another command-line name), `script`, `env:<NAME>` (e.g.
`env:XAKE__CONFIG`), `default`, or
`(not set)`; secrets as `***`. `--help` lists the `varschema` fields and the `var` rules, with
their command-line names and environment names (implicit or explicit), without evaluating
anything.

### Secret variables

See also [variables.md](variables.md#secrets).

A token or license key must not end up in the build database (`.xake`) or the log. Mark the
variable secret:

```fsharp
let vars = {|
    AcmeApiToken = Var.secret<string>(envVar = "ACME_API_TOKEN", description = "API token") |> required
    LicenseKey   = Var.string(cliArg = "licenseKey", envVar = "LICENSE_KEY") |> secret
|}
// or as a rule: var "ACME_API_TOKEN" [Secret; Env "ACME_API_TOKEN"]
// or with the implicit source: var "TOKEN" [Secret] reads XAKE__TOKEN
```

- `Var.secret<'t>(envVar = ...)` reads the environment only; `|> secret` works on any var
  (before or after `required`/`withDefault`); in a `var` rule, `Secret` or `SecretHashed`.
- A secret takes its value only from its own sources: `-d` of it, under its key, its
  command-line name or one of its environment names, stops the run ("secret variables come from
  the environment, set ACME_API_TOKEN instead of passing -d ..."), and so does a script value of
  its name. `Var.arg ... |> secret` thus takes its value only from `withDefault`.
- The database keeps whether the variable was set, never the value (presence only): a rotated
  token rebuilds nothing, unset to set (or back) rebuilds. `|> secretHashed` keeps a salted
  HMAC-SHA256 of the value instead (a random salt stored with the digest), so a changed value
  rebuilds the targets that read it. The change reason names the variable only: "Secret variable
  ACME_API_TOKEN changed" / "... is now set" / "... is now unset".
- `getVar` and `getEnv` of a secret return the real value; masking applies to output only. Every
  logger Xake creates (console, `filelog`, `CustomLogger`, and a custom `ILogger` given in
  the options) replaces each secret value of 4 characters or more with `***`: the banner, the
  `Debug` options dump, change reasons, `--dryrun`, `--dump`, the `shell` settings dump at Debug,
  tool output, errors. A conversion error (a `Var.secret<int>` that does not parse) does not quote
  the value. A value a tool transforms (base64, URL-encoded) is not recognised; values shorter than
  4 characters are not masked.
- `--help` lists a secret with "(secret)", its name, type and description, never a default.
- The name decides: a declared secret's key and environment names are secret for the whole run,
  so a plain `getEnv "ACME_API_TOKEN"` is recorded and masked as a secret too. Declaring secrets
  also turns a clear value an older Xake recorded under that name into a digest when the database
  is opened (the file is rewritten).
- Child processes inherit the environment, so an environment secret reaches tools as it is. A
  value reaches a tool otherwise only when the script passes it, e.g.
  `sh "tool" { envs ["ACME_API_TOKEN", token] }`. Prefer the environment over command-line
  arguments or msbuild `-p:` properties, which tools echo in their own output.

The database format is 0.6 with this change; 0.4 and 0.5 files are read and upgraded on open. An
older Xake meeting a 0.6 database recreates it (a full rebuild).

## Setup and teardown

```fsharp
do xakeScript {
    setup ["banner"]
    teardown ["cleanup"]
    command "banner" {
        let! mode = getVar "MODE"
        do! trace Message "building in %A mode" mode }
    ...
}
```

`setup` targets run sequentially, in the order given, before the first target (command line mode:
once options and variables are final; engine mode: once at start). A failing setup target is
logged, the build is skipped, `teardown` still runs and the run fails (exit code 2, or
`XakeException` with `ThrowOnError`). `--dryrun` and `--dump` do not run setup (the dry run lists
the setup targets).

The ops differ in one respect: several `setup` ops **accumulate** (`setup ["a"]` then `setup ["b"]`
runs `a`, `b`), while a later `teardown` op **replaces** the earlier list.

Setup targets go through the normal up-to-date check. A target that reads variables records `Var`
dependencies and would be skipped on the next run as up to date. Declare such targets with
`command "name" { ... }`, which is a phony rule that always reruns (it records `alwaysRerun()` for
you); with `phony` put `alwaysRerun()` inside the recipe.

## Notes

- Prefer `Xake.Tasks` namespace in new scripts
- Prefer `sh`, `cp`, and `rm` builders over older legacy APIs
- Prefer typed variables with `Var.*` and `varschema` for better help output
