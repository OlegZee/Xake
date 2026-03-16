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
Var.create<'t>(cliArg = "cfg", envVar = "BUILD_CFG", description = "build config") |> withDefault "Debug"
```

All named parameters on every factory are optional.

## Factory Methods

| Method | Named params | Returns |
|--------|-------------|---------|
| `Var.create<'t>` | `?cliArg`, `?envVar`, `?description` | `OptionalVar<'t>` |
| `Var.string` | `?cliArg`, `?envVar`, `?description` | `OptionalVar<string>` |
| `Var.int` | `?cliArg`, `?envVar`, `?description` | `OptionalVar<int>` |
| `Var.bool` | `?cliArg`, `?envVar`, `?description` | `OptionalVar<bool>` |
| `Var.env<'t>` | `?envVar`, `?description` | `OptionalVar<'t>` |
| `Var.arg<'t>` | `?cliArg`, `?description` | `OptionalVar<'t>` |

## Default Behaviour

Each variable declared with `Var.create` (or a typed shorthand) resolves from two sources
in priority order:

1. **CLI arg** — `-d FieldName=value`
2. **Environment variable** — field name converted to `UPPER_SNAKE_CASE`

```
Config   -> CONFIG
ApiKey   -> API_KEY
Threads  -> THREADS
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
