# Ideas

## Rule-level combinators via `mapRecipe`

`mapRecipe` transforms the recipe inside any `Rule` variant, enabling composable
`Rule -> Rule` decorators piped with `|>`. Works uniformly with `=>`, `target`, `command`.

### Shipped

- `requiring resource` — wraps recipe in a resource lock (1 unit, CPU-slot yielding).

### Planned

- **`onError`** — rule-level error handler. Wraps recipe in `tryWithF`. Possible signatures:
  - `onError (exn -> unit)` — log/swallow, rule returns unit
  - `onError (exn -> Recipe<ExecContext, unit>)` — recovery recipe (retry, fallback)
  - `retryOn n` — retry the recipe up to N times on failure

### Possible future combinators

- `withTimeout duration` — cancel recipe after a deadline
- `logged label` — trace start/end/duration around the recipe
- Combinators compose naturally: `"deploy:*" => recipe { ... } |> requiring lock |> retryOn 3`

## Full framework targeting without a Framework installation

`DotNetFwk.sdkImpl` locates the Roslyn compiler shipped with the .NET SDK and the reference
assemblies from the `Microsoft.NETFramework.ReferenceAssemblies.*` package (restored on first
use), so binaries for full framework can be produced on any OS — no Framework installation and
no registry involved. Framework lookup order: the registry wins on Windows, the SDK provider is
the fallback there and the default on Unix; `sdk-net462` forces it, `mono-*` still selects mono.

### Shipped

- `csc` for net4x — verified end to end on macOS, see `samples/fullframework.fsx`: the produced
  exe is PE32, CLR v4.0.30319, references mscorlib 4.0.0.0.
- `fsc` option syntax on Unix — a leading `/` is a path there, so long options are passed as
  `--nologo`, `--target:`, `--out:`, `--define:`, `--resource:` and references as `-r:`.

### Planned

- **`fsc` for net4x** — currently stops at `error FS0074: ... You must add a reference to
  assembly 'netstandard'`: the FSharp.Core shipped with the SDK targets netstandard2.0, so a
  net4x compile also needs the facade from `NETStandard.Library.NETFramework`. Restore that
  package the same way the reference assemblies are restored and add it to `AssemblyDirs`.
- `resgen` and `msbuild` tasks are untested against the SDK provider (msbuild is launched
  through a generated wrapper script).
- Decide the fate of the `net462` leg of Xake's own `TargetFrameworks`: it only enables hosting
  Xake inside a .NET Framework process, it is never built by `build.fsx`, and it is what makes
  `dotnet pack` crash on macOS (`OverflowException` in the MSBuild `Copy` task).
