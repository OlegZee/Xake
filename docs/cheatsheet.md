# Xake cheatsheet

## Package reference

```fsharp
#r "nuget: Xake"
open Xake
open Xake.Tasks
```

## Script entry point

```fsharp
do xakeScript {
	"main" <== ["build"; "test"]
}
```

Rules go straight in the script body. The older `rules [ ... ]` wrapper still works, and both
forms may be mixed.

## Setup and teardown

```fsharp
do xakeScript {
	setup ["login"]        // runs before the build; several setup ops accumulate
	teardown ["logout"]    // runs after the build; a later teardown op replaces the earlier one
	command "login" { do! trace Message "logging in" }   // `command` always reruns
	...
}
```

A setup failure skips the build (teardown still runs, exit code 2). Declare setup
targets that must run every time with `command` (or `alwaysRerun()` in a `phony`). See [tasks.md](tasks.md#setup-and-teardown).

## Dependency operators

`<==` demand targets in parallel:

```fsharp
"main" <== ["build"; "test"]
```

`<<<` demand targets in sequence:

```fsharp
"deploy" <<< ["build"; "test"; "package"]
```

`=>` plain phony rule (to be phased out in favour of `Command`, see
[typed-targets.md](typed-targets.md#the-future-of-)):

```fsharp
"clean" => rm {dir "out"}
```

## Preferred rule forms

```fsharp
do xakeScript {
	command "build" {
		do! sh "dotnet build src/core -c Release" { () }
	}

	target "out/version.txt" {
		do! writeText "1.0.0"
	}

	targets ["out/a.dll"; "out/a.xml"] {
		do! sh "dotnet build src/core -c Release -o out" { () }
	}

	// rules can also be generated
	for name in ["a"; "b"] do
		$"clean-{name}" => rm { dir $"out/{name}" }
}
```

## Recipe helpers

```fsharp
recipe {
	do! need ["out/a.dll"]
	let! files = getFiles <| fileset { includes "src/**/*.fs" }
	do! needFiles files
	do! trace Info "Done"
}
```

Capture groups of the rule's pattern (file or phony): `let! name = getRuleMatch "name"` (`""` when
absent), `let! all = getRuleMatches ()` (a map). Also `getVar`, `getEnv`, `getCtxOptions`,
`getTargetFile` (file rules only), `getTargetFullName`, `alwaysRerun ()`.
`let! known = hasRule "out/a.dll"`: whether a rule would build a target (resolved as `need`
resolves it), without building it.

## Script variables

Full reference: [variables.md](variables.md).

```fsharp
let vars = {|
	config = Var.string(cliArg = "config") |> withDefault "Debug"          // -d config=Release
	token  = Var.secret<string>(envVar = "ACME_API_TOKEN") |> required    // env only, never logged
	key    = Var.env<string>(envVar = "LICENSE_KEY") |> secretHashed      // rebuild when it changes
|}
xake ExecOptions.Default {
	varschema vars                                   // declares them; --help lists them
	var "VERSION" "1.0.0"                            // a constant; $XAKE__VERSION, then -d VERSION=... win
	var "PKGS" [Cli "pkgs"; Env "PKGS_DIR"; Default ".packages"]  // a rule: -d pkgs=..., else env, else default
	var "OUT" [Default "bin"]                        // no Env: reads $XAKE__OUT, else "bin"
	phony "main" (recipe { let! token = vars.token in () })
}
```

Values are computed once at launch: `-d` > the script's `var`/`ExecOptions.ScriptVars` > the
declaration's sources. A name is defined once in the script (a second definition stops the run).
A `Var` read (`let! x = vars.config`) needs a `varschema` field of its key; otherwise use
`getVar "N"` (the variable set) or `getEnv "X"` (the environment). A field's key is its `name`,
else the field name; `cliArg` is only its `-d` name. A declaration without an explicit `Env` /
`envVar` reads `XAKE__<KEY in UPPER_SNAKE>` first (`buildConfig` -> `XAKE__BUILD_CONFIG`), and
`XAKE__<NAME>` overrides a constant `var`; `Var.arg` reads no environment.

Libraries (such as Xake.Dotnet) read only this variable set, never the environment:
`var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]` takes it; a library may export a schema of what it
reads for `varschema` ([variables.md](variables.md)).

A secret: the database keeps whether it is set (`secret`) or a salted hash (`secretHashed`),
never the value; every log masks it as `***`; `-d` of a secret stops the run. See
[tasks.md](tasks.md#secret-variables).

## Shell task

```fsharp
do! sh "dotnet" {
	args ["test"; "src/tests"; "-c"; "Release"]
	failonerror
}
```

## .NET tasks

`csc`, `fsc`, `msbuild`, `resgen` and the hermetic locks are the `Xake.Dotnet` package
(`#r "nuget: Xake.Dotnet, 4.0.0"`, `open Xake.Dotnet`), from its own repository,
[OlegZee/Xake.Dotnet](https://github.com/OlegZee/Xake.Dotnet), which has their reference.

## Fingerprints of committed generated files

```fsharp
command "import" { do! Fingerprints.record "xake.fingerprints" "desc/a.json" ["a.fsproj"] }
command "build"  { do! Fingerprints.warn "xake.fingerprints" }   // warns on stale/hand-edited
"check" => Fingerprints.check "xake.fingerprints"                // fails instead
```

See [fingerprints.md](fingerprints.md).

## Patterns and masks (4.0)

Rule patterns (`..>`, `*..>`, `target`, `command`) and fileset masks (`!!`, `++`, `--`) share one
grammar; details in the [overview](overview.md#patterns-masks-and-paths).

```fsharp
"out/(cfg:*)/(name:*).dll" ..> recipe { let! cfg = getRuleMatch "cfg" in () }   // groups: rules only
"out/(fwk:*)/(ext:{dll,xml})" ..> recipe { () }       // alternatives {a,b}; classes [abc] [!a] [a-z]
!! "src/**/*.fs" -- "src/**/obj/"                     // ** = zero or more directories; trailing / = directories
!! @"src/Foo \(x86\)/*.cs"                            // \ escapes ( ) [ ] { } | ; write patterns as @"..."
!! (PatternAst.escapePath realDir + "/*.dll")         // a real path as a mask
```

| Mask | Selects |
|---|---|
| `dir/*` | files directly in `dir` |
| `dir/**` or `dir/**/*` | every file below `dir` |
| `dir/**/` | directories below `dir` |

Always `/` as the separator. Unescaped `( ) [ ] { } |` and a group in a mask are errors with an
`XPnnn` code (table: pattern spec 7.2); `\` as a separator and `[c]` warn. In a rule `**/x.dll`
also matches `x.dll`; `*/**/x.dll` requires a directory.

## Typed targets (4.0)

`open Xake` first, then `open Xake.Typed`. Details: [typed-targets.md](typed-targets.md).

```fsharp
type AsmG = { cfg: string; name: string }
let Asm = Target<AsmG> "out/(cfg:*)/(name:*).dll"       // fields = the pattern's groups
let Clean = Command<unit> "clean"                       // always runs
let rules = [
    Asm.Rule <| fun m -> recipe { do! trace Message "%s %s" m.cfg m.name }   // or Asm.rule { let! m = matched ... }
    Clean.Rule <| fun () -> recipe { () }
]
let wanted = need [ Asm.Format { cfg = "Release"; name = "Core" } ]          // the path, from the pattern
```

`Target<G, O>` names several outputs (`X.Make m`), `X.For [ ... ]` lists paths, `X.Glob` gives a
`Fileset`. A record that disagrees with its pattern fails when the script starts.

## Filesets

```fsharp
let src = !! "src/**/*.fs"
let srcInCore = !! "**/*.fs" @@ "src/core"
```

## CLI invocation

Default target:

```bash
dotnet fsi build.fsx
```

Options and targets:

```bash
dotnet fsi build.fsx -- -- clean build test
```

Variable:

```bash
dotnet fsi build.fsx -- -- build -d Version=1.2.3
```

The command line wins over the script: `-d` beats `var` (and `ExecOptions.ScriptVars`), `-ll` beats
`consolelog`, `-fl` and `-fll` each beat their half of `filelog`, `-r` beats `rootdir`, and targets
named on the command line beat `want` (always: there is no hard override; `want` itself beats
`ExecOptions.Targets`). The script ops act as defaults. `IgnoreCommandLine = true` skips the
command line; `IgnoreEnvironment = true` stops declarations from reading the environment
(tests, embedding; see [variables.md](variables.md#pinning-values-tests-and-embedding)).

Dry run:

```bash
dotnet fsi build.fsx -- -- build --dryrun
```

