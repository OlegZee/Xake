# Roadmap

Ideas and larger items that are agreed in direction but not scheduled. Working state of a
feature lives in `docs/features/<name>/`; this page is the cross-feature list.

## Principles

- **Hosts: the .NET SDK only; targets: every platform.** No backports of the engine or the
  tasks to the full framework or mono as build hosts. Composed `csc {}`/`fsc {}` must produce
  `netN.0`, `netstandard2.x` and `.NET Framework 4.x` outputs from any host (macOS, Linux,
  Windows) with no manual setup: reference packs and facades restored or taken from the SDK.
  Verified 2026-10-04 on macOS: csc net462 works; fsc net462 needs an implicit `netstandard.dll`
  facade and a default `FSharp.Core` (fix in 3.5).

## Engine

### Rules with arguments (idea, 2026-10-02)

Every rule that uses its pattern's capture groups starts with a row of `let! x = getRuleMatch "x"`
and, for multi-file targets, a list pattern over `getTargetFiles()`. Proposal: a rule form that
binds the capture groups **positionally**, in the order they appear in the pattern, to the
parameters of a lambda, and the target file(s) after them:

```fsharp
"out/(fwk:*)/(lib:*).dll" ==> fun (fwk, lib) out -> recipe { ... }

["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"] ==> fun (fwk, lib) (dll, doc) -> recipe { ... }
```

The pattern parser knows the number of groups, so an arity mismatch fails when the script is
loaded, not inside the recipe. Tuple overloads for 1–4 groups and 1–4 target files via static
members; the operator is sugar over them. Group names stay as documentation and keep working
through `getRuleMatch` for existing scripts. `build.fsc.fsx` is the first customer.

## Xake.Dotnet

### B6: `Fsc` through the shared runner (designed 2026-10-04, `docs/features/hermetic-build/fsc-design.md`; breaking for fsc in 3.5: `Fsc settings` -> `Fsc.compile`, `targetfwk` required)

`fsc {}` has no resolved record, no `resolve` operation and no hash-checked runner, so there is
no `fsc { lock }` and `build.fsc.fsx` (the build of record) is hermetic only through its kept
msbuild evaluations and `--deterministic+`. Plan: an `Fsc` record next to `Csc` with
`ofSettings`/`run`/`compile`, a private runner core shared with `Csc.run`, `fsc { ...; resolve }`,
then `fsc { lock }` as an extension in Xake.Hermetic.Dotnet, `Project.import` for `.fsproj`
(retiring `Fsproj`), and `build.fsc.fsx` rewritten on a lock. Limits to document: the F#
compiler is not a NuGet package, so the only reproducible compiler path for fsc is the SDK pinned
by `global.json`; the net462 leg still comes from `dotnet pack`.

## Xake.Dotnet, later

### SDK restore by the lock

An fsc lock names an SDK's `fsc.dll` that NuGet cannot restore. The official `dotnet-install`
script installs an exact version into a private folder without admin rights, and Microsoft
publishes release hashes: a "restore SDK" step into the project's `DOTNET_ROOT` would make the
SDK as restorable as a package.

### An F# compiler `toolset`

`FSharp.Compiler.Service` is a current NuGet package carrying the whole compiler; `fsc.dll` is a
thin host over it. A small host shipped with Xake.Dotnet would give `fsc` a restorable
`toolset` like csc's `Microsoft.Net.Compilers.Toolset`.

### Small base fixes found while writing workflows-ru (2026-10-04)

- Composed `csc {}` does not pass `/deterministic` (fsc `--deterministic+`): lock-built outputs
  are not byte-reproducible unless the script adds it. Make it the default in composed mode.
- `norestore`/`packageroot` do not cover `toolset`/`CSC_TOOLSET`, the framework reference
  packages or fsc's default `FSharp.Core`: those resolve through `restorePackage None` into the
  machine cache before the lock is read. Offline builds need them routed through the same
  options.
- With the `netN.0` default the lock names the SDK's targeting pack under `$(DotnetRoot)`, so
  `CSC_TOOLSET` alone does not make a lock portable; `global.json` is still required. Document
  loudly; the "SDK restore" idea above is the real fix.

## Xake.Hermetic.Dotnet

From `docs/hermetic/workflows.md` ("Gaps"), in priority order:

1. *(done in PR #30)* A missing lock fails under CI (env `CI`, or `-d CI=on|off`) instead of being
   recorded.
2. `csc { lock }` cannot take `Restore.Options` (package root, no network).
3. Reference assemblies of a composed block are downloaded outside the restore mechanism.
4. The sha512 of a package already present in the package folder is not verified.
5. Import as a file rule re-imports on every fresh clone; `update-locks` is the pattern.
6. No document-level `Lock.diff`; no one-call build of a whole imported lock; drift messages
   print absolute paths; `Lock.verify` on a missing lock says only "Neither rule nor file".
7. SBOM default internal prefixes are a customer's; the default should be empty.
8. The six SBOM questions in `docs/hermetic/nuget-sbom.md`; `sign {}` shadows `sign`;
   `StrongName.signFile` does not recompute the PE checksum; no real signer ships.
