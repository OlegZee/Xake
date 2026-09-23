# Restoring the packages a lock names

`src/dotnet/Restore.fs`. Closes the gap `verify-dataengine.md` §5/§7 measured: a lock was a
complete *description* of what a compilation reads but only a partial *source* for obtaining
it. A compiler living in a package was restored (`ensureCompilerAvailable`); reference and
analyzer packages were not, so a package folder that did not already have them failed the
build with hundreds of `expected <sha256>, got missing` lines.

Nothing here resolves a version, reads `project.assets.json`, or walks a dependency graph.
The lock already says which package, at which version, holds which file — a reference under
the package folder is spelled `<root>/<id>/<version>/...`, so a missing file names its own
package. That is the whole mechanism.

## The surface

```fsharp
module Restore =
    type Options = { PackageRoot: string option; Enabled: bool }   // Default = { None; true }
    val packageRoot : Options -> string
    val into    : dir: string -> Recipe<ExecContext, Options>

    type Missing = { Id: string; Version: string; Sha512: string; Files: string list }
    val missing : Options -> Lock.Entry list -> Missing list       // File.Exists only
    val verify  : Options -> Missing list -> string list           // nupkg presence + sha512

    val download : Options -> (string * string) list -> Recipe<ExecContext, unit>
    val ensure   : Options -> Lock.Entry list -> Recipe<ExecContext, string list>
    val prepare  : Options -> Lock.Document -> Recipe<ExecContext, unit>
```

and, around it:

- `RunOptions` gains `Restore: Restore.Options`, so `CscLock.compileWith` carries the folder
  and the policy into the runner. `RunOptions.Default` is today's behaviour exactly.
- `Roots.nugetPackageRootToken`, `Roots.packageRootOverride dir` — the extra-root list that
  makes `Lock.loadWith` expand `$(NuGetPackageRoot)` against the same folder.
- `Roots.withExtra` changed in two ways (below).

A script that wants the build's dependencies in a folder of its own, in one place:

```fsharp
let restoreOptions = recipe {
    let! options = Restore.into ".packages"           // relative to the project root
    return options
}
let loadLock path = recipe {
    let! options = restoreOptions
    let! lock = Lock.loadWith (Roots.packageRootOverride (Restore.packageRoot options)) path
    return lock
}

command "restore" {                                    // populate it once, then cache it
    let! options = restoreOptions
    let! lock = loadLock "locks/MESCIUS.json"
    do! Restore.prepare options lock
}

// ... and in the compile rule
do! CscLock.compileWith { RunOptions.Default with Restore = options } entry
```

## Decisions

**One restore for the whole missing set, not one per package or per entry.** The synthesized
project carries one `PackageDownload` per missing package and `dotnet restore` runs once.
`PackageDownload` rather than `PackageReference` is the load-bearing choice: it fetches
exactly the listed version and nothing else — no dependency walk (the lock already names the
graph) and, crucially, **no framework compatibility check**, so a package that targets only
`net472` downloads from the `netstandard2.0` shim project just as well. Measured on the
fixture: one restore serves six lock entries that all want `NETStandard.Library 2.0.3`.
`DisableImplicitFrameworkReferences` keeps the SDK from adding `NETStandard.Library` to the
folder as a side effect of the shim's own TFM (without it, an unrelated 22 MB lands there).

**Once per process, and never twice at the same time.** Two guards, because they answer
different questions. A process-wide memo (folder + id + version, case-insensitive) is written
*after* a restore of that package completes, so a build with a hundred entries naming one
package launches one restore; a package that was already on disk is deliberately **not**
memoized, because re-checking it is two `File.Exists`. A `Resource` of quantity 1 per package
folder serializes concurrent compiles, and the recipe re-computes the missing set after
acquiring it — whoever held it may have restored exactly what this entry wanted. Same
mechanism, and the same reasoning, as `Project.withProjectLock`: a `Resource` is just a value,
so a library recipe creates and uses one with no script-side declaration, and `withResource`
yields the CPU slot while waiting.

**Nothing missing costs nothing.** `missing` is one `File.Exists` per distinct path the
entries name and no process, no NuGet, no network. A no-op build never reaches it at all (the
engine decides the target is up to date and `run` does not execute), so the no-op figure is
unchanged — 0.089 s on the fixture. On a full recompile the check is ~700 `File.Exists`
against the ~700 SHA-256 the hash check computes immediately afterwards: noise.

**The synthesized project lives under the build's project root** (`obj/xake/restore/`), not
in a temp directory. NuGet discovers `nuget.config` from the project's own directory, so a
lock whose packages come from a company feed is otherwise simply unrestorable — the dataengine
fixture has exactly that shape. The repository's msbuild customizations, which are written for
real projects and not for a download shim, are switched off on the command line instead:
`ImportDirectoryBuildProps`, `ImportDirectoryBuildTargets`, `ImportDirectoryPackagesProps`,
`ManagePackageVersionsCentrally` (the fixture has all four), plus `NuGetAudit=false`.

**Restore is on by default; a hermetic build opts out.** `Options.Enabled = true`. The
reasoning, since requirement 4 asks for it explicitly:

- it is what this codebase already did — `ensureCompilerAvailable` has restored a missing
  compiler package since 2026-09-23, and the argument for the compiler is the argument for
  its references;
- reaching the network cannot change *what* gets compiled. The lock fixes id and version,
  `PackageDownload` cannot float, the nupkg is checked against the lock's sha512, and the
  per-file SHA-256 check that follows fails the build if a single byte is not the recorded
  one. Restore here is a way of *obtaining* bytes the lock already identifies, not a way of
  *choosing* them;
- the case that needs it is the one this feature is for: a CI agent with a cold folder.

`Enabled = false` restores the pre-change failure exactly — every missing file listed with
`expected <sha256>, got missing` — plus one `Warning` naming the package count and the folder,
so the reason is visible instead of inferred. Verified on the fixture: 339 missing lines, and
the folder is never created.

**Verification stays at the level each layer can actually see.** This module checks the
package: present in the folder, and its `.nupkg.metadata` `contentHash` equal to the
`Sha512` the lock's package graph recorded. It does not re-hash files — the SHA-256 check in
`run` already does that for every reference and analyzer and remains the authority. A package
the lock's graph does not carry (a reference-assemblies package the toolchain restored, which
never appears in `project.assets.json`) has no recorded hash and is only checked for presence;
its files are still SHA-256-checked by the runner.

**`ensureCompilerAvailable` is now one case of the general mechanism.** The compiler's path
goes into the same missing-package scan as the references, so a toolset package is restored by
the same single `dotnet restore` as everything else. What is left in `ensureCompilerAvailable`
is the part that was never general: the diagnostics. An SDK under `$(DotnetRoot)/sdk/<version>`
is not a package and cannot be restored, and saying so — "install that SDK or re-import with
the installed one" — is worth a step of its own. `restoreToolsetCompiler` survives only for
`resolve`'s `toolset` operation, which composes a lock and therefore has no lock to read the
package out of; it is now a recipe over `Restore.download`.

**`Roots.withExtra` learned two things**, both needed for the folder to be one folder:

- an extra root with a built-in token now *replaces* the built-in instead of being refused.
  That is how `$(NuGetPackageRoot)` is pointed at the build's own folder when the lock is
  read, so the lock and the restore agree about where the packages are.
- a relative extra root is resolved against `projectRoot`, not the process's current
  directory. A script would otherwise write `Path.GetFullPath ".packages"` and silently
  reintroduce the cwd dependency stage A1 removed. `withExtra` stays a pure function of its
  two arguments; `Restore.into` is the recipe that supplies the project root from
  `ExecOptions.ProjectRoot`.

  The test `a declared root must be a well-formed, non-built-in, absolute token` in
  `ProjectImportTests.fs` asserted the two old behaviours and was split accordingly.

## Measured

Fixture: `git archive origin/develop` of `ar-net-core-dataengine` into a temp dir, three
projects × two brands, netstandard2.0, imported with `-t 1`, built with the package folder
pointed at `.packages/` inside the copy (macOS, 8 cores, SDK 8.0.x, `-p:NuGetAudit=false`).
The lock entries name 113–115 references each, essentially all of them from
`NETStandard.Library 2.0.3`.

| | |
|---|---|
| `locks`, cold, `-t 1` | 8.0 s |
| `build`, empty `.packages` (restore + 6 compiles) | **16.4 s**, exactly **one** `dotnet restore` for all 6 entries |
| `restore` alone (`Restore.prepare` over both locks), empty folder, warm HTTP cache | **0.78 s**, one restore, 22 MB |
| `build`, `.packages` full, outputs deleted | 5.9 s, **0** restores |
| `build`, nothing changed | **0.089 s**, 0 restores — unchanged |
| `build`, empty `.packages`, restore off | fails before any compiler runs: **339** `got missing` lines + 1 warning; the folder is never created |

**Byte-identity holds through the restored folder**: MESCIUS's three projects built from
`.packages/` compared with `dotnet build -c Release -t:Rebuild -p:TargetFramework=netstandard2.0
-p:Brand=MESCIUS -p:NuGetAudit=false -p:IntermediateOutputPath=obj/xake/netstandard2.0/MESCIUS/`
— **9/9 identical** (dll, pdb, xml).

Suite: 334 passed, 1 skipped (was 325 total); 8 new tests in `src/tests/RestoreTests.fs`
(missing-set grouping and casing, the restore project's text, the nupkg hash check including
"no hash recorded is not a failure", `into`'s project-root-relative folder, and three
`Integration` ones: a real restore into a scratch folder followed by a compile, the opt-out
reporting instead of fetching, and a wrong `Sha512` failing by name). Both projects build with
0 warnings on both TFMs.

## Left open

- **The composed `csc { toolset "x" }` path has no package folder of its own.** It always uses
  the machine's cache, because composed settings carry no folder — only a lock does, through
  `RunOptions.Restore`. Adding `packageroot` to `CscSettingsType` would close it; nothing
  needs it yet.
- **Only what a compilation reads is restored**, i.e. the packages behind `References`,
  `Analyzers` and `Compiler`. The lock's `Dependencies.Packages` graph carries more than that
  (runtime-only packages, build-time-only packages); a build that wants to *publish* from the
  folder, not just compile, would want the whole graph. `Restore.download` already takes an
  arbitrary `(id, version)` list, so that is a caller-side change.
- **A restore failure is reported per package, not per feed.** `dotnet restore`'s own output
  goes to the log at `Verbose`; the failure message names the packages and the exit code. A
  missing private-feed credential therefore reads as "restoring N package(s) … failed with
  exit code 1" with the detail one level down.
- **The nupkg hash is checked, the nupkg is not re-hashed.** `verify` compares the lock's
  `Sha512` with the `contentHash` NuGet itself wrote into `.nupkg.metadata`; it does not
  recompute SHA-512 over the `.nupkg` file. Someone who can write into the package folder can
  write both. This is a package-level sanity check sitting in front of the per-file SHA-256
  check, which is the one that actually gates the compilation.
- **Test-order fragility found, worked around, not fixed**: `DotNetFwk.locateFramework`
  memoizes, and `FromLockTests` points `NUGET_PACKAGES` at a scratch folder while it runs, so
  whichever fixture warms the memo first decides where `netstandard.dll` is said to live.
  `RestoreTests` falls back to the machine's cache rather than inheriting a stale answer. The
  real fix is a memo that is not process-global, or a test that does not mutate the
  environment.
