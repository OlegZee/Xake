# Typed Variables

Xake provides a typed variable system for passing values into recipes from CLI arguments
or environment variables.

## Declaring Variables

At the top of your script, declare variables as an anonymous record:

```fsharp
let buildVars = {|
    Config   = Var.string(description = "build configuration") |> withDefault "Debug"
    Version  = Var.string()                                    |> required
    Threads  = Var.int()                                       |> withDefault 4
    Verbose  = Var.bool()
    ApiKey   = Var.string()                                    |> required
|}
```

Typed convenience factories (`Var.string`, `Var.int`, `Var.bool`) save you writing the
generic type parameter in the common cases. For other types use `Var.create<'t>`:

```fsharp
// logical name drives both CLI lookup and env derivation:
Var.create<'t>(name = "cfg", envVar = "BUILD_CFG", description = "build config") |> withDefault "Debug"

// separate logical name and explicit CLI key:
Var.create<'t>(name = "logicalName", cliArg = "externalName", description = "build config") |> withDefault "Debug"
```

All named parameters on every factory are optional.

## Factory Methods

| Method | Named params | Returns |
|--------|-------------|---------|
| `Var.create<'t>` | `?name`, `?cliArg`, `?envVar`, `?description` | `OptionalVar<'t>` |
| `Var.string` | `?name`, `?cliArg`, `?envVar`, `?description` | `OptionalVar<string>` |
| `Var.int` | `?name`, `?cliArg`, `?envVar`, `?description` | `OptionalVar<int>` |
| `Var.bool` | `?name`, `?cliArg`, `?envVar`, `?description` | `OptionalVar<bool>` |
| `Var.env<'t>` | `?envVar`, `?description` | `OptionalVar<'t>` |
| `Var.arg<'t>` | `?cliArg`, `?description` | `OptionalVar<'t>` |

## Default Behaviour

Each variable declared with `Var.create` (or a typed shorthand) resolves from two sources
in priority order:

1. **CLI arg** — `-d <cliName>=value`
2. **Environment variable** — logical name converted to `UPPER_SNAKE_CASE`

The names used for lookup are determined as follows:

| Parameter supplied | CLI lookup key | Env var derived from |
|--------------------|---------------|----------------------|
| neither `name` nor `cliArg` | field name | field name |
| `name` only | `name` value | `name` value |
| `cliArg` only | `cliArg` value | field name |
| `name` + `cliArg` | `cliArg` value | `name` value |

The UPPER_SNAKE_CASE conversion applies to whichever name drives env derivation:

```
buildConfig -> BUILD_CONFIG
apiKey      -> API_KEY
threads     -> THREADS
```

### `name` vs `cliArg`

Use `name` when you want a logical alias that controls both the CLI key and the derived
env var name independently of the record field name:

```fsharp
let vars = {|
    config = Var.create<string>(name = "buildConfig")   // CLI: -d buildConfig=... ; env: BUILD_CONFIG
|}
```

Use `cliArg` when you only want to override the CLI key — env derivation still comes from
`name` (if given) or the field name:

```fsharp
let vars = {|
    // CLI: -d externalName=... ; env: LOGICAL_NAME (derived from name)
    config = Var.create<string>(name = "logicalName", cliArg = "externalName")
|}
```

## Scoped Lookup

Use `Var.env` or `Var.arg` when you want to restrict the lookup to a single source.

### `Var.env` — environment variable only

CLI args are never consulted. Useful for secrets that must not be accidentally passed
via the command line.

```fsharp
let apiToken = Var.env(envVar = "API_TOKEN", description = "API secret token") |> required
// auto-derives env name from record field name:
let buildMode = Var.env()  // reads BUILD_MODE (derived from field name "buildMode")
```

### `Var.arg` — CLI arg only

Environment variables are never consulted. Useful for flags that should be explicit
build-time overrides.

```fsharp
let outputPath = Var.arg(cliArg = "path", description = "output directory") |> withDefault "/tmp"
let jobCount   = Var.arg<int>(cliArg = "jobs") |> withDefault 1
```

## Optional vs Required

The result type in a recipe depends on how the variable is declared:

| Declaration                            | Result in recipe  |
|----------------------------------------|-------------------|
| `Var.string()`                         | `string option`   |
| `Var.string() \|> required`            | `string`          |
| `Var.string() \|> withDefault "Debug"` | `string`          |
| `Var.create<int>() \|> withDefault 4`  | `int`             |
| `Var.create<bool>()`                   | `bool option`     |

A variable without `required` or `withDefault` is optional — its recipe result is
`'t option`. Adding either makes it required and the result is `'t` directly.

## Combinators

```fsharp
// closing combinators — determine result type, must appear last
Var.string() |> required              // throws at execution time if no value found
Var.string() |> withDefault "Debug"   // always has a value

// modifiers — appear before the closing combinator
Var.string() |> describe "..." |> withDefault "x"   // shown in --help output
```

| Combinator              | Purpose                                     |
|-------------------------|---------------------------------------------|
| `describe "text"`       | Add description shown in `--help` output    |
| `required`              | Throw at execution time when value absent   |
| `withDefault value`     | Return `value` when CLI and env are absent  |

Note: the `description` named parameter on factory methods is equivalent to piping through
`describe` — use whichever reads more naturally.

## Registering with xakeScript

```fsharp
do xakeScript {
    varschema vars

    rules [
        "build" ..> recipe {
            let! config  = vars.Config    // string
            let! threads = vars.Threads   // int
            let! verbose = vars.Verbose   // bool option
            let! version = vars.Version   // string
        }
    ]
}
```

## CLI Usage

```
dotnet fsi build.fsx -- -d Config=Release
dotnet fsi build.fsx -- -d Threads=8 -d Config=Release
```

## Help Output

Running with `--help` lists all declared variables under a "Script variables:" heading.
The format per variable is:

```
Script variables:
  -d Config=<value> [string], default: "Debug" — build configuration
  -d Version=<value> [string] (required)
  -d Threads=<value> [int], default: 4
  -d Verbose=<value> [bool]
  -d ApiKey=<value> [string] (required)
```

Fields shown (when present):
- `(required)` — variable has no default and is not optional
- `, env: NAME` — a custom env var name was supplied via `envVar`
- `, default: VALUE` — value returned when CLI and env are absent
- ` — description` — text from `description` param or `describe` combinator
