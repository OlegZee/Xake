# Extraction plan: `Xake.Hermetic` out of `Xake.Dotnet`

Written 2026-09-29 after the user's decision: hermetic functionality gets its own namespace and
NuGet package; the pre-existing tasks (`csc`, `fsc`, `resgen`, `msbuild`, resx) stay in
`Xake.Dotnet` (shipped inside the `Xake` package) and are extended; base improvements come
first, the extraction second. The API must be "предельно просто, логично и понимаемо".
Everything below is a proposal until the user approves §3's principles and §5's questions.

**Revised 2026-09-29 (discussion with the user):** the resolved compilation is named `Csc`, not
`Compilation`; the object is obtained from the existing `csc {}` builder through a final
`resolve` operation instead of a second builder; the seam has **no hook list**; compiler-server
use is a script-level variable `CSC_SERVER`, not only a per-target switch. The spike (§5) is the
first item of phase 1.

## 0. Facts this plan rests on

- `Xake.Dotnet` is **not** a package: `src/dotnet/Xake.Dotnet.fsproj` has `PackageId=Xake` and
  bundles `Xake.dll` (core is `IsPackable=false`). So "the base" = package `Xake` = two dlls.
- Last release is `v3.3.0`. **Every file below marked "new" is unreleased** (`git diff v3.3.0`):
  Json, Roots, Fsproj, Nuget, Project, Restore, Resx, Sbom, Verify, StrongName, Pack, Sign, and
  most of `Dotnet.csc.fs` (670 lines added). Renaming them costs nothing today.
- The engine (`src/core`) changed in four files, all bug fixes; it knows nothing of locks.
- The hermetic modules use only `Xake.Dotnet` internals, never engine internals:
  `Impl.levelFromString`, `Impl.failOnExitCode`, `DotNetFwk.sdkImpl.{nugetRoot,dotnetRoot}`,
  `CscArgs.{splitList,quoteIfNeeded,aliasSplitIndex}`, `Json`.

## 1. Inventory and dependency direction

Direction: `Xake.Hermetic -> Xake.Dotnet -> Xake (engine)`. Nothing in Xake.Dotnet may name a
hermetic type.

| Source (today) | Goes to | Notes |
|---|---|---|
| `src/core/*` | Xake | unchanged |
| `DotNetFwk.fs` | Xake.Dotnet | make `nugetRoot`, `dotnetRoot`, `restorePackage` public (hermetic needs them) |
| `ResourceFileset.fs`, `DotnetTasks.fs` | Xake.Dotnet | `Impl.levelFromString`/`failOnExitCode` -> public `Tool` module (B5) |
| `Resx.fs` (new) | Xake.Dotnet | used by `DotnetTasks`, `resgen`, the runner |
| `Dotnet.resgen.fs`, `Dotnet.Msbuild.fs` | Xake.Dotnet | unchanged |
| `Dotnet.fsc.fs` | Xake.Dotnet | later through the runner (B6) |
| `Dotnet.csc.fs` | **split** | settings, builder, `CompilerServer`, `RunOptions`, `resolve`, `run` -> Xake.Dotnet; `CscLock`, the `lock` gate, `failStep` messages about lock/SDK/package -> Xake.Hermetic |
| `Project.fs`: `CscArgs` | Xake.Dotnet (`CscArgs.fs`) | the switch table the runner needs (`inputs`/`outputs`) |
| `Project.fs`: `Lock` types `Hashed`, `Reference`, `Compiler`, `Compilation`, `Dependencies` (minus `Packages`), `ofArgs`, `Args`, `hashed`, `compilerVersion`, `mapPaths`, `mapText`, `sha256` | Xake.Dotnet (`Csc.fs`) | the resolved compilation (`Csc` record), the runner's input |
| `Project.fs`: `Lock` rest (`Evaluation`, `SdkPin`, `Package`, `Entry`, `Document`, read/write/load/save, `diff`, `rehash`, `entry`/`entryFor`) | Xake.Hermetic (`Lock.fs`) | the file format and provenance |
| `Project.fs`: `Git` | Xake.Hermetic (`Git.fs`) | revision token only matters for imported locks |
| `Project.fs`: `Project` (import) | Xake.Hermetic | |
| `Json.fs` | Xake.Hermetic (internal) | after `Fsproj` moves, **no base module uses Json**; no sharing needed |
| `Roots.fs` | Xake.Hermetic; `Roots.dotnetRoot` -> `DotNetFwk.dotnetRoot` | tokenization is lock-format business; the runner only needs the dotnet root |
| `Fsproj.fs` | Xake.Hermetic | a "kept evaluation" (brief §6 rung 1); retire it into `Project.import` for fsproj after B6 |
| `Nuget`, `Restore`, `Sbom`, `Verify`, `StrongName`, `Pack`, `Sign` | Xake.Hermetic | `Verify.sha256` -> base `Hash.sha256` (duplicate of `Lock.sha256`) |

If the user wants `Fsproj` in the base instead, Json and Roots have to go to the base too
(internal, linked into both projects via `<Compile Include="..\dotnet\Json.fs" Link=...>`).
**Recommendation: hermetic.**

### Package name

| Candidate | Trade-off |
|---|---|
| **`Xake.Hermetic`** / `Xake.Hermetic` | short, matches the brief, branch and docs; "hermetic" oversells a little (brief §5: "mostly") |
| `Xake.Dotnet.Hermetic` / same | honest that it is .NET-only; long, and `open Xake.Dotnet.Hermetic` in every script |
| `Xake.Evidence` (or `Xake.Provenance`) | sells the pitch ("the build that can testify"); vague for restore/pack/sign |

**Recommend `Xake.Hermetic`.** Xake is .NET-first anyway, so "Dotnet" in the name adds little.

### The seam: what `csc {}` keeps and what moves

`csc {}` keeps: composed settings, `resolve`, `RunOptions`, the one runner (generated files,
output dirs, resx `(resx, .resources)` pairs, hash check of non-empty hashes, `needFiles`, rsp,
compiler selection, `/shared`), and `toolset` (restored via `DotNetFwk.restorePackage`).
What moves: `CscLock.*`, the `lock` operation's semantics, `Restore.ensure`, the
`$(SourceRevisionId)` step, the SDK/package-specific "compiler missing" messages
(`ensureCompilerAvailable`), `Evaluation`, `Packages`.

**Decision (2026-09-29): the seam is one record and one function, no hooks.** Today's `run`
contains exactly three hermetic pieces, and all three happen *before* the compiler is invoked:
`Restore.ensure` (before the hash check), the `$(SourceRevisionId)` substitution via
`Git.headSha`, and `ensureCompilerAvailable`. So the hermetic side does its work on the
resolved object and then hands it to the base runner; the base never calls back:

```fsharp
// base (Xake.Dotnet)
type Csc = { Name; Framework; Directory; Options; Defines; Sources; Generated; ...; Dependencies }
    with static member ofSettings : CscSettingsType -> Recipe<Csc>     // today's private `resolve`
         static member run        : RunOptions -> Csc -> Recipe<unit>   // today's private `run`, minus the three steps

// hermetic (Xake.Hermetic)
let! c = csc { src ...; ref ...; resolve }          // the object, nothing compiled
do! Lock.build "locks/app.json" c                    // gate against the lock, Restore.ensure,
                                                     // revision token, compiler-missing messages, then Csc.run
```

The hash check stays in the base: it is what makes a replayed `Csc` trustworthy with or without
a lock. `Restore.ensure` runs before it on the hermetic side, which is the order `run` has today.

**`resolve` as the last operation of `csc {}`.** The type a computation expression returns is
fixed by `Run`, but a custom operation may change the state type, and `Run` may be overloaded on
it:

```fsharp
type CscRequest = private CscRequest of CscSettingsType   // marker: "resolve, do not run"
[<CustomOperation("resolve")>] member _.Resolve(s: CscSettingsType) = CscRequest s
member _.Run(s: CscSettingsType) = Csc s                   // Recipe<unit>, the 3.3 contract
member _.Run(CscRequest s)       = Csc.ofSettings s         // Recipe<Csc>
```

`resolve` must be the last operation: the ones after it are typed on `CscSettingsType` and stop
compiling (a compile error, documented). A flag inside the settings would not do: `Run` would
then always return `Recipe<Csc>`, and `do! csc { ... }` from 3.3 would no longer type-check.
Outside a file rule `resolve` needs an explicit `out` (as `Csc.ofSettings` falls back to
`getTargetFile()`), same semantics as today, to be documented.

**`csc { lock "path" }`** stays as sugar *if* the spike shows an extension custom operation
plus an extension `Run` overload work on F# 8: Xake.Hermetic maps `CscSettingsType` to its own
marker type and its `Run` calls `Lock.build`. If not, the lock is always explicit through
`Lock.build`, and that is not a blocker. Either way the base `CscSettingsType` has **no `Lock`
field and no `Prepare` field**.

| Mechanism | Verdict |
|---|---|
| **Record + `resolve` + `Csc.run`, no hooks** | **adopted** (the user, 2026-09-29) |
| A. Record + runner + `Prepare` hooks | superseded: hooks existed only to let the base call hermetic steps; with the object in the script's hands, the hermetic side calls the base instead |
| A-lite (whole `Lock.Entry` in base) | rejected: base would carry provenance fields it never fills |
| B. `ICompilationSource` | rejected: OO for one method |
| C. hermetic has its own runner | rejected: breaks "one runner" |

**Why `Csc` and not `Compilation`.** A type and a let-bound function may share a name (`type
Csc` next to the released `let Csc settings`); a *module* may not (FS0039, verified). Hence the
operations are static members of the record, `Csc.run`, `Csc.ofSettings`, which read the same at
the call site. The name is compiler-specific on purpose: B6 gives fsc its own `Fsc` record and
the shared runner core becomes private, instead of one abstract record with a `Compiler.Tool`
dispatch. Two concrete objects for the user, the commonality hidden.

### Compiler server as a script-level setting

Today: env `XAKE_CSC_SERVER` plus per-target `noserver`/`keepalive`. The user wants one place to
turn it on or off. The toolchain already has that convention: `NETFX`, `NETFX-TARGET`, `FSCVER`
are script variables read with `getVar`, set in `xakeScript { var ... }` or with `-d`. Add
`CSC_SERVER` with the values `on`, `off`, or a number (keepalive seconds). `CscSettingsType.Server`
becomes `CompilerServer option` (`None` = inherit); resolution moves out of
`CscSettingsType.Default` (which has no context) into `Csc`. Precedence: the target's operation,
then `CSC_SERVER`, then env `XAKE_CSC_SERVER`, then `Shared None`.

## 2. Base improvements (Xake / Xake.Dotnet), ordered, PR-sized

| # | Task | Goal / files | Acceptance | Size |
|---|---|---|---|---|
| B1 | Split `Project.fs` | `CscArgs.fs`, `Git.fs`, `Lock.fs`, `Project.fs`, one module per file, bodies untouched; no API change (`Csc.fs` with the types is B2's, since moving them out of `Lock` is an API change) | build 0 warnings; suite green; locks byte-identical | S |
| B2 | Resolved compilation as public API | `Csc` record, `Csc.ofSettings`, `Csc.run`, `RunOptions { FailOnError; CscPath; Server }`; `csc { ...; resolve }` (overloaded `Run`); restore / revision / compiler-missing messages / lock gate move *out of* `run` into `Lock.build` (still in the same assembly for now); `Lock.Entry` wraps `Csc`; `CSC_SERVER` variable | `CscLockTests`, `FromLockTests`, `RestoreTests`, `CscServerTests` green; dataengine 36/36 byte-identical; `Dotnet.csc.fs` names no `Restore`/`Git`/`Lock.Document` | M |
| B3 | Composed `toolset` off `Restore` | `restoreToolsetCompiler` -> `DotNetFwk.restorePackage` | `ToolsetTests` green; base compiles with Restore.fs excluded | S |
| B4 | One hash function | `Hash.sha256` in base; `Verify.sha256`/`Lock.sha256` go | grep finds one implementation | S |
| B5 | Public helpers instead of internals | `Tool.diagnosticLevel`, `Tool.failOnExitCode`, `DotNetFwk.nugetRoot/dotnetRoot/restorePackage`; `CscArgs` helpers public or folded into `Csc.ofArgs` | hermetic files compile using public names only | S |
| B6 | `fsc` through the same runner (review §2.6) | `Fsc` record with `Fsc.ofSettings`/`Fsc.run`, `fsc { ...; resolve }`; a private runner core shared with `Csc.run` (fsc: `--` options, no rsp `/noconfig` rule, no server) | `build.fsc.fsx` builds byte-identical dlls to today; integration fsc test | L |
| B7 | One resx path | `Impl.compileResx` uses `Resx.compile` on both TFMs (drops `System.Windows.Forms` for net462) or documents why not | `ResxTests` byte-identical against msbuild on both TFMs | S |
| B8 | Composed-mode debts | drop the duplicate `needFiles` in `resolve` (review §2.4); optionally the "no `targetfwk` = no references" default (session.md) | suite green | S |
| B9 | Docs and samples | `docs/tasks.md` csc/fsc sections (`toolset`, `noserver`, `keepalive`, `CSC_SERVER`, `resolve`, `Csc.run`), `samples/fullframework.fsx`, a toolset sample, `src/dotnet/readme.md` | samples run from `out/` | S |
| B10 | API polish (§3) | renames in base and hermetic | §3 table applied; docs updated | M |

B6 can go after the release: `Fsc` is a new record next to `Csc`, so adding it later is not a
breaking change.
Release `Xake 3.4.0`, the `build.fsx`/`samples/gettingstarted.fsx` bump and the `build.fsc.fsx`
bootstrap come **after** §4: until the extraction the hermetic code is in the assembly that ships.

## 3. API polish

### Principles (for the user to approve)

1. **One concept, one module, named by the noun**: `Csc`, `Lock`, `Project`, `Restore`,
   `Sbom`, `Pack`, `Sign`. No `Impl`/`CscImpl`/`CscLock` names that exist only because of
   history or an F# name clash.
2. **Pure vs recipe by verb**: pure functions `parse`/`format`/`diff`/`map*` take everything
   explicitly (roots included); recipes `load`/`save`/`import`/`compile`/`build` read the build
   context (project root, `need`s). `xWith` always means "`x` with one explicit argument that
   `x` defaults", never "the pure variant".
3. **Defaults are `T.Default`**, never a `defaultOptions` value.
4. **A builder named after a task returns what goes into `rules`/`recipe`**: `csc {}` gives a
   recipe, so `sign {}` gives a rule. Settings-only builders get a `...Settings` name.
5. **Break now, not after release**: every hermetic name is free until the first
   `Xake.Hermetic` release. The 3.3 surface (`Csc`, `CscSettings`, `CscSettingsType` fields,
   `csc`/`fsc`/`resgen`/`msbuild` operations, `cscpath`) stays source-compatible.

### Renames

| Old | Proposed | Reason | Free? |
|---|---|---|---|
| `Lock.Entry` (runner input) | `Csc` record + module `Csc` (`ofSettings`, `run`, `compile`), option a chosen | what the compiler is handed; not lock-specific. Spike: nothing is reachable through `Csc.` while the function `Csc` exists | a, one break: `Csc settings` -> `Csc.compile settings` |
| `Lock.Compilation` (section) | flattened into `Csc` (fields `Directory`, `Options`, `Defines`, `Sources`, `Generated`, ...) | one record for what the compiler is handed; JSON key `"Compilation"` stays, the writer regroups | free |
| `Lock.Dependencies` | `Csc.Dependencies` (minus `Packages`) | runner verifies these hashes | free |
| `Lock.Hashed` / `Lock.Reference` | `HashedFile` / `Reference` | an adjective as a type name reads oddly | free |
| `Lock.Entry` (hermetic) | `Lock.Entry = { Csc; Evaluation; Packages }` | a lock entry = compilation + provenance | free |
| `Lock.Document` | keep | `Lock.File` would clash with `Xake.File` | - |
| `CscLock.resolve settings` | `csc { ...; resolve }` (a final operation, `Run` overloaded to return `Recipe<Csc>`); `Csc.ofSettings` for record-syntax settings | no second builder; `resolve` is what the code has always called this step | free |
| `CscLock.compile` / `compileWith` | `Lock.compile` / `Lock.compileWith` | replay of a lock entry (restore + revision hooks included) | free |
| (runner) `run` private | `Csc.run options c` | the one runner, public; hash check inside, restore/revision/lock outside | free |
| `CscLock.record path settings` | `Lock.record path c` with `c` from `csc { ...; resolve }` | composable | free |
| `CscLock.verify path settings` | `Lock.verify path c` | same | free |
| `csc { lock "path" }` | keep the spelling, defined in Xake.Hermetic, if spike item 2 passes; else `Lock.build "path" c` only | reads well; not a blocker | free |
| `CscSettingsType.Lock` | gone (no replacement field) | base can't know locks; no hooks needed once the object is in the script's hands | free (field is new) |
| `RunOptions.CscPath` | keep | the record is csc's; `Fsc` gets its own options in B6 | - |
| `RunOptions.Restore` | gone; `Restore.ensure` is called by `Lock.build` before `Csc.run` | restore is hermetic | free |
| `Lock.readWith roots path` / `writeWith` / `parseWith` | `Lock.read roots path` / `Lock.format roots doc` / `Lock.parse roots text` | principle 2: pure never takes `With` | free |
| `Lock.load` / `loadWith extra` / `save` / `saveWith` | keep | already fit principle 2 | - |
| `Fsproj.parseWith` / `writeWith` | `Fsproj.parse` / `Fsproj.format` | same | free |
| `Fsproj` module | keep, marked "retired after B6" | `Project.import` for fsproj replaces it | free |
| `Roots.current` / `currentWith extra` / `builtin` / `withExtra` | keep `current`/`currentWith`; `withExtra` -> `Roots.make root extra` | `withExtra` is pure with a context-looking name | free |
| `Project.packages cacheRoot assets` | `Lock.packagesOf` | produces `Lock.Package`s | free |
| `Project.tokenizeRevision` | `Git.tokenize sha entry` | lives with `Git.headSha` | free |
| `Restore.prepare opts doc` / `ensure opts entries` | `Restore.ensure opts doc.Entries` only (fails) + `Restore.missing` | two near-synonyms for one act | free |
| `Verify.sbomPackageScopeWith` | `Sbom.checkPackageScope` | Verify must not depend on Sbom | free |
| `Sbom.defaultPackageScope` | `Sbom.PackageScopeOptions.Default` | principle 3 | free |
| `Sbom.forPackage` / `forPackageScoped(With)` | pending the six SBOM questions (`nuget-sbom.md`) | the RFC-conformant one should get the short name | free |
| `Pack.defaultOptions` | `Pack.Options.Default` | principle 3 | free |
| `Pack.entries zip` | `Pack.list zip` | reads a package back; `Entry` is also a type there | free |
| `Sign.sign {}` (returns `Settings`) | `sign { ...; signer s }` -> `Rule` (AutoOpen); `Sign.Settings` for record syntax | principle 4; no `Sign.sign` stutter | free |
| `Verify.sha256` / `Lock.sha256` | `Hash.sha256` (base) | duplicate | free |
| `CscSettingsType.Server : CompilerServer` | `CompilerServer option`, `None` = inherit from `CSC_SERVER` / env | one place to switch the server for the whole script | free (field is new) |
| `Roots.dotnetRoot` | `DotNetFwk.dotnetRoot` | the runner's compiler-missing message needs it; `Roots` is hermetic | free |
| `Csc`, `CscSettings`, `csc {}` ops, `Fsc`, `fsc {}` | keep | released in 3.3 | breaking if changed |

After 3.4.0 / Xake.Hermetic 0.1: the base names above (`Csc`, `RunOptions`,
`Csc.run/ofSettings`, `resolve`, `CSC_SERVER`, `Hash`, `Tool`) become breaking to change. The hermetic ones may still
change while the package is 0.x.

### The fsx wiring as one call

`import.fsx`'s compile rule (load lock, find entry, map unhashed project references to the
`/out:` of the entry with that name in the same lock, `need` them, compile) is generic when all
projects share one lock:

```fsharp
target "src/(proj:*)/obj/xake/(fwk:*)/(brand:*)/(name:*).dll" {
    let! m = getRuleMatches()
    do! Lock.build (lockFile m.["brand"]) m.["fwk"] m.["name"]
}
```

`Lock.build path c` is the same function for a composed `Csc` (gate, restore, revision,
`Csc.run`). `Lock.buildWith { RunOptions; ExtraRoots; OutputOf }` covers cross-lock references
(page -> dataengine) and a custom layout. It also removes the `Sha256 = ""` convention from
scripts.

## 4. The extraction itself (after B1-B10)

| # | Step | Detail |
|---|---|---|
| E1 | New project | `src/hermetic/Xake.Hermetic.fsproj`: `net462;netstandard2.0`, `FSharp.Core 8.0.100` pinned, `DisableImplicitFSharpCoreReference`, `GenerateDocumentationFile`. `ProjectReference` to `src/dotnet` (becomes nuspec dependency `Xake`), **plus** `src/core` with `PrivateAssets="all"`: `Xake.Dotnet`'s own core reference is `PrivateAssets=all`, so it does not flow. Namespace `Xake.Hermetic` in every moved file |
| E2 | Package metadata | `PackageId Xake.Hermetic`, description, tags (`reproducible;lockfile;sbom;cyclonedx;hermetic`), MIT, `Icon.png`, `src/hermetic/readme.md` (git-tracked casing; see session.md) |
| E3 | `InternalsVisibleTo` | none between packages (B5 made the needed helpers public); `tests` on `Xake.Hermetic`; nothing new in core |
| E4 | Tests | `src/hermetic.tests/` (net8.0) referencing all three; links `Common.fs`/`XakeTestBase.fs`. Moves: ProjectImport, LockDiff, FromLock, CscLock, Nuget, Restore, Sbom, Verify, StrongName, Pack, Sign. Stay: DotnetTasks, Toolset, CscServer, Resx and the engine tests |
| E5 | Docs | product docs to `docs/hermetic/` (lock format and `Lock.*` from csc-syntax.md, restore, nuget-sbom, verify, pack, strongname, signing, csc-server stays in base docs); `csc-syntax.md` keeps only composed mode + `resolve` + `Csc.run`; `docs/features/hermetic-build/` stays the working folder (brief, session, tracker, experiments) |
| E6 | Build scripts | `build.fsx`: add `{ Name = "Xake.Hermetic"; Dir = "src/hermetic"; Needs = ["Xake"; "Xake.Dotnet"] }`; pack rules must not overlap: `out/Xake.(ver:*).nupkg` also matches `Xake.Hermetic.0.1.0.nupkg` (and the last matching rule wins), so pack into `out/pkg/<id>/` or name each target explicitly. `build.fsc.fsx`: third library, `open Xake.Hermetic` for `Fsproj`/`Project` |
| E7 | CI publish | independent tags: `v*` -> `Xake`, `hermetic-v*` -> `Xake.Hermetic`; one workflow, a step picks the project by tag prefix; `VERSION` + run number as today; `dotnet nuget push out/pkg/<id>/*.nupkg --skip-duplicate` |
| E8 | Bootstrap | after both land on nuget.org: `build.fsx` `#r "nuget: Xake, 3.4.0"`; `build.fsc.fsx` `#r "nuget: Xake, 3.4.0"` + `#r "nuget: Xake.Hermetic, 0.1.0"`, `.bootstrap/` retired; `samples/gettingstarted.fsx` bumped; `import*.fsx`/`verify-*.fsx` reference both packages |

**Versioning: independent (recommended).** Xake stays semver 3.x; Xake.Hermetic starts at
`0.1.0` (preview, allowed to break) and depends on `Xake [3.4.0, 4.0)`. That range only holds
because there is no IVT, so B5 is what makes independent versions possible. Lockstep (one tag,
two packages, same version) is simpler CI but would force Xake version bumps for hermetic-only
changes; choose it only if IVT turns out to be needed.

## 5. Risks, open questions, order

**Spike, run 2026-09-29** (scratch projects, F# 8 `LangVersion`, FSharp.Core 8.0.100; a spike
is a throwaway experiment whose only output is a yes/no per question):

1. **`type Csc` next to `let Csc`: NO.** `Csc.ofSettings` / `Csc.run` give FS0039 in both
   definition orders and with the members added by a type extension: in expression position
   `Csc.x` binds to the function value, never to the type. So the record may be *called* `Csc`,
   but nothing can be reached through `Csc.` while the function exists. Decision for the user,
   see below.
2. `Run` overloaded on the state type: already proven by `ShellBuilder`
   (`src/core/Tasks/Shell.fs`). The spike confirmed it again: `csc { ...; resolve }` returns the
   record, and an operation after `resolve` fails with a readable FS0193 ("`Request` is not
   compatible with `Settings`").
3. **Extension custom operation plus extension `Run` overload from another assembly: YES.**
   `csc { src ...; out ...; lock "l.json" }` picks the hermetic `Run` when the hermetic module is
   opened; without `open` the error is FS0708. So `csc { lock "path" }` can live in Xake.Hermetic
   with the marker type and its `Run` there too.
4. `CompilerServer option` in settings: structural equality holds; no test compares
   `CscSettingsType` values.

**Item 1, the choice.** The function `Csc settings` (record-syntax entry, 3.3 API) is used by
three tests, `samples/book/intro.fsx` and `src/dotnet/readme.md`; `Fsc`/`ResGen`/`MSBuild` are
its siblings. Options:

| Option | API | Cost |
|---|---|---|
| **a. Retire the function, module `Csc` (chosen by the user, 2026-09-29)** | `type Csc` record + `[<ModuleSuffix>] module Csc` with `ofSettings`, `run`, and `compile settings` (= `ofSettings` then `run`, the replacement for `Csc settings`) | one 3.3 break: `do! Csc settings` -> `do! Csc.compile settings`; 3 tests, 1 sample, readme. `Fsc` follows at B6 for symmetry |
| b. Keep the function, another record name | `CscInvocation` / `Compilation` with the same members | no break; the name the user disliked comes back in some form |
| c. Keep both, members unreachable by `Csc.` | free functions `runCsc`, `cscOfSettings` | no break; the names are the worst of the three |

**Risks**
- Spike item 1 failed (see above): the record cannot expose members through `Csc.` while the
  function `Csc` exists. The user chose **a** (2026-09-29): `Csc settings` -> `Csc.compile settings`.
- B2's churn in tests and scripts (`entry.Dependencies` -> `entry.Csc.Dependencies`).
  The lock JSON stays byte-identical; prove it on dataengine (36/36) before merging.
- `build.fsc.fsx`, the build of record, starts depending on Xake.Hermetic (dogfooding, but a
  bootstrap cycle). Keep `build.fsx` on base only.
- B6 (fsc) is the biggest item and blocks retiring `Fsproj`. Doing it after the release is fine:
  `Fsc` is additive.
- The one genuinely shared piece: `toolset` needs a NuGet package download in the base, which
  today lives in hermetic `Restore`. B3 moves the minimal download to `DotNetFwk.restorePackage`
  and `Restore` reuses it, not the other way round.

**Questions for the user**
1. Package name: `Xake.Hermetic`? Or `Xake.Dotnet.Hermetic` / `Xake.Evidence`?
2. ~~Seam~~ decided 2026-09-29: `Csc` record, `csc { ...; resolve }`, `Csc.run`, no hooks;
   `CSC_SERVER` script variable.
3. `Fsproj` to hermetic (so Json and Roots are hermetic-only)?
4. Independent versions, with Xake.Hermetic at 0.x?
5. Is fsc through the runner (B6) required for 3.4.0, or can it follow?
6. `sign {}` returning a rule: yes?
7. The six SBOM questions (`nuget-sbom.md`) decide the `Sbom.for*` names.

**Suggested order and rough effort**

| Phase | Items | Effort |
|---|---|---|
| 1 | ~~spike~~ done 2026-09-29 (§5); B1 | 0.5 day |
| 2 | B2, B3, B4, B5 | 2-3 days |
| 3 | B10 API polish (after approval of §3) | 1-1.5 days |
| 4 | B7, B8, B9 | 1 day |
| 5 | E1-E7 extraction | 1.5-2 days |
| 6 | Release Xake 3.4.0 + Xake.Hermetic 0.1.0, then E8 | 0.5 day |
| 7 | B6 `Fsc` through the shared runner core, then retire `Fsproj` into `Project.import` | 2-3 days |
