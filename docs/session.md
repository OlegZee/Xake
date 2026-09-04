# Session notes

## Current work: modernizing `src/dotnet` (branch `feature/rulesless-syntax`)

`src/dotnet` (Xake.Dotnet) was merged in from a separate repository and had not caught up with
the engine. It has now been reworked to reuse the engine's APIs and match its style. What is
worth knowing before touching it again:

- **`src/dotnet` no longer contains engine code.** Its `ProcessExec.fs` and `CommonLib.fs`
  copies are gone; it uses the engine's via `InternalsVisibleTo("Xake.Dotnet")` declared in
  `src/core/Xake.fsproj`. If assembly signing is ever added, that attribute needs the public
  key or Xake.Dotnet silently loses access to the internals.
- **`pexecSync` exists for `DotNetFwk` only.** Framework and tool probing happens in plain
  memoized functions with no async context; everything inside a recipe uses the `shell`/`sh`
  task. `Async.RunSynchronously` is safe there (recipes run via `Async.StartAsTask` with no
  `SynchronizationContext`) but it holds a CPU slot, which is why it is confined to the
  memoized lookups.
- **`#if NET46` was dead code** -- the projects target `net462`, for which the SDK defines
  `NETFRAMEWORK`/`NET462`/`NET46_OR_GREATER` but *not* `NET46`. Use `NETFRAMEWORK`. Verify any
  new conditional empirically; a wrong one fails silently by compiling nothing.
- **resx compilation needs `System.Windows.Forms`** (`ResXResourceReader`), which is not one of
  the SDK's implicit net462 references. It is referenced explicitly for that TFM only.
- **Compiler diagnostics used to be invisible.** Roslyn writes them to stdout, which the old
  `_system` logged at `Verbose`, and `levelFromString` only recognized diagnostics carrying a
  source position -- so `error FS0084: ...` was missed too. Both are fixed; keep
  `Impl.levelFromString` covering both diagnostic shapes.

### Known limitations, not defects of this work

- **`fsc` cannot target full framework on macOS through the SDK.** It needs a
  net462-compatible `FSharp.Core`, and the SDK only supplies the netstandard2.0 one, which
  drags in a `netstandard` facade that the net4x reference-assembly packages do not carry.
  Hence there is no `fsc` end-to-end test; `samples/features.fsx` works around it by
  referencing an `FSharp.Core.dll` of its own.
- **A target requested twice in one recipe is built twice.** `need ["x"]` followed by
  `needFiles` on the same `x` rebuilds it, because the worker pool drops its dedup entry when
  the first request completes. Reproducible with no .NET task involved (engine-level, in
  `WorkerPool.fs`). This is why the csc test asserts on its output file rather than an
  execution count.
- `build.fsx` builds `netstandard2.0` only; the `net462` asset comes from `dotnet build` /
  `dotnet pack`.

## How to verify changes here

```bash
dotnet build src/core -c Release && dotnet build src/dotnet -c Release   # both TFMs
dotnet test src/tests                                                    # 233 passed, 1 skipped
dotnet test src/tests --filter 'Category=Integration'                    # real csc invocation
dotnet fsi build.fsx -- -- build test                                    # self-hosting
dotnet fsi samples/fullframework.fsx                                     # end-to-end csc
```

`samples/*.fsx` reference `out/netstandard2.0/*.dll`, so run the self-hosting build first.

## Earlier work: delegated execution (merged, PR #15)

The delegated/distributed execution model (`Resource`, `runDetached`, `delegated`) is designed
in [`docs/delegated.md`](delegated.md). Two facts from that work still bite:

- Delegated rules are never bounded by the local CPU pool -- the engine runs executor and body
  detached, so `runDetached` inside a delegated body is redundant.
- `Scheduler.withYieldedSlot` assumes the caller holds exactly one CPU slot. Calling it without
  one releases a phantom permit and silently disables the `Threads` limit. Inside a
  `DelegatedExecutor` (already detached) use `Resource.acquire`/`withAcquired` instead.
