# Extraction plan: `Xake.Hermetic` out of `Xake.Dotnet`

Written 2026-09-29 after the user's decision: hermetic functionality gets its own namespace and
NuGet package; the pre-existing tasks (`csc`, `fsc`, `resgen`, `msbuild`, resx) stay in
`Xake.Dotnet` (shipped inside the `Xake` package) and are extended; base improvements come
first, the extraction second. The API must be "предельно просто, логично и понимаемо".
Everything below is a proposal until the user approves §3's principles and §5's questions.

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
| `Project.fs`: `Lock` types `Hashed`, `Reference`, `Compiler`, `Compilation`, `Dependencies` (minus `Packages`), `ofArgs`, `Args`, `hashed`, `compilerVersion`, `mapPaths` | Xake.Dotnet (`Compilation.fs`) | the resolved compilation, the runner's input |
| `Project.fs`: `Lock` rest (`Evaluation`, `SdkPin`, `Package`, `Entry`, `Document`, read/write/load/save, `diff`, `rehash`, `entry`/`entryFor`, `mapText` over evaluation) | Xake.Hermetic (`Lock.fs`) | the file format and provenance |
| `Project.fs`: `Git` | Xake.Hermetic (`Git.fs`) | revision token only matters for imported locks |
| `Project.fs`: `Project` (import) | Xake.Hermetic | |
| `Json.fs` | Xake.Hermetic (internal) | after `Fsproj` moves, **no base module uses Json**; no sharing needed |
| `Roots.fs` | Xake.Hermetic | tokenization is lock-format business |
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
`$(SourceRevisionId)` step, the SDK/package-specific "compiler missing" messages, `Evaluation`,
`Packages`.

| Mechanism | How | Cost | Verdict |
|---|---|---|---|
| **A. Record + runner + hooks** | base: `Compilation` record, `Compilation.ofCsc`, `Compilation.run options c`; `RunOptions.Prepare : (Compilation -> Recipe<Compilation>) list` run first, in order. Hermetic: `Lock.Entry = { Compilation; Evaluation; Packages }`, and hooks `Restore.step opts`, `Git.revisionStep`, `Lock.gate path` | M: types move, `entry.Dependencies.X` becomes `entry.Compilation.Dependencies.X` in callers/tests; the JSON layout stays byte-identical (the writer flattens) | **recommended** |
| A-lite | move the whole `Lock.Entry` type into base unchanged, hooks as in A | S | fallback: base would carry provenance fields it never fills |
| B. `ICompilationSource` | `csc` accepts an interface that yields a compilation | M | rejected: OO for one method; still needs the record in base |
| C. hermetic has its own runner | copy `run` | S now, L forever | rejected: breaks "one runner" |

`csc { lock "path" }` stays spelled the same: base settings get `Prepare` (the hook list), and
Xake.Hermetic defines `lock` as a builder **type extension** (`type CscSettingsBuilder with
[<CustomOperation("lock")>] ...`) that appends `Lock.gate path`. Spike first (S): confirm an
extension custom operation works on F# 8 and that a function-typed field does not break code
comparing settings (`=`). If either fails, use `Csc (settings |> Lock.gated "path")` instead.

## 2. Base improvements (Xake / Xake.Dotnet), ordered, PR-sized

| # | Task | Goal / files | Acceptance | Size |
|---|---|---|---|---|
| B1 | Split `Project.fs` | `CscArgs.fs`, `Compilation.fs` (the types), `Git.fs`, `Lock.fs`, `Project.fs`; no API change | build 0 warnings; suite green; locks byte-identical | S |
| B2 | Resolved compilation as public API | `Compilation` record, `Compilation.ofCsc`, `Compilation.run`, `RunOptions { FailOnError; CompilerPath; Server; Prepare }`; restore / revision / lock gate become hooks (still in the same assembly); `Lock.Entry` wraps `Compilation` | `CscLockTests`, `FromLockTests`, `RestoreTests` green; dataengine 36/36 byte-identical; `Dotnet.csc.fs` names no `Restore`/`Git`/`Lock.Document` | M |
| B3 | Composed `toolset` off `Restore` | `restoreToolsetCompiler` -> `DotNetFwk.restorePackage` | `ToolsetTests` green; base compiles with Restore.fs excluded | S |
| B4 | One hash function | `Hash.sha256` in base; `Verify.sha256`/`Lock.sha256` go | grep finds one implementation | S |
| B5 | Public helpers instead of internals | `Tool.diagnosticLevel`, `Tool.failOnExitCode`, `DotNetFwk.nugetRoot/dotnetRoot/restorePackage`; `CscArgs` helpers public or folded into `Compilation.ofArgs` | hermetic files compile using public names only | S |
| B6 | `fsc` through the same runner (review §2.6) | `Compilation.ofFsc`; `run` dispatches on `Compiler.Tool` (fsc: `--` options, no rsp `/noconfig` rule, no server) | `build.fsc.fsx` builds byte-identical dlls to today; integration fsc test | L |
| B7 | One resx path | `Impl.compileResx` uses `Resx.compile` on both TFMs (drops `System.Windows.Forms` for net462) or documents why not | `ResxTests` byte-identical against msbuild on both TFMs | S |
| B8 | Composed-mode debts | drop the duplicate `needFiles` in `resolve` (review §2.4); optionally the "no `targetfwk` = no references" default (session.md) | suite green | S |
| B9 | Docs and samples | `docs/tasks.md` csc/fsc sections (`toolset`, `noserver`, `keepalive`, `Compilation.run`), `samples/fullframework.fsx`, a toolset sample, `src/dotnet/readme.md` | samples run from `out/` | S |
| B10 | API polish (§3) | renames in base and hermetic | §3 table applied; docs updated | M |

B6 can go after the release if time is short, provided B2 already uses compiler-neutral names
(`CompilerPath`, `Compiler.Tool`), so that adding fsc is not a breaking change.
Release `Xake 3.4.0`, the `build.fsx`/`samples/gettingstarted.fsx` bump and the `build.fsc.fsx`
bootstrap come **after** §4: until the extraction the hermetic code is in the assembly that ships.

## 3. API polish

### Principles (for the user to approve)

1. **One concept, one module, named by the noun**: `Compilation`, `Lock`, `Project`, `Restore`,
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
| `Lock.Entry` (runner input) | `Compilation` (base record) | what the compiler is handed; not lock-specific | free |
| `Lock.Compilation` (section) | `Compilation.Invocation` | frees the noun; brief §11 used "invocation"; JSON key stays `"Compilation"` | free |
| `Lock.Dependencies` | `Compilation.Dependencies` (minus `Packages`) | runner verifies these hashes | free |
| `Lock.Hashed` / `Lock.Reference` | `HashedFile` / `Reference` | an adjective as a type name reads oddly | free |
| `Lock.Entry` (hermetic) | `Lock.Entry = { Compilation; Evaluation; Packages }` | a lock entry = compilation + provenance | free |
| `Lock.Document` | keep | `Lock.File` would clash with `Xake.File` | - |
| `CscLock.resolve settings` | `Compilation.ofCsc settings` | a module can't be named `Csc` (FS0039), but `Compilation.ofCsc` works; `ofFsc` later | free |
| `CscLock.compile` / `compileWith` | `Lock.compile` / `Lock.compileWith` | replay of a lock entry (restore + revision hooks included) | free |
| (runner) `run` private | `Compilation.run options c` | the one runner, public | free |
| `CscLock.record path settings` | `Lock.record path (Compilation.ofCsc settings)` | compiler-neutral, composable | free |
| `CscLock.verify path settings` | `Lock.verify path (Compilation.ofCsc settings)` | same | free |
| `csc { lock "path" }` | keep the spelling, defined in Xake.Hermetic | reads well; spike needed (§1) | free |
| `CscSettingsType.Lock` | `CscSettingsType.Prepare` (hook list) | base can't know locks | free (field is new) |
| `RunOptions.CscPath` | `RunOptions.CompilerPath` | fsc shares the runner | free |
| `RunOptions.Restore` | gone; `Restore.step opts` hook | restore is hermetic | free |
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
| `Csc`, `CscSettings`, `csc {}` ops, `Fsc`, `fsc {}` | keep | released in 3.3 | breaking if changed |

After 3.4.0 / Xake.Hermetic 0.1: the base names above (`Compilation`, `RunOptions`,
`Compilation.run/ofCsc`, `Hash`, `Tool`) become breaking to change. The hermetic ones may still
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

`Lock.buildWith { RunOptions; ExtraRoots; OutputOf }` covers cross-lock references
(page -> dataengine) and a custom layout. It also removes the `Sha256 = ""` convention from
scripts.

## 4. The extraction itself (after B1-B10)

| # | Step | Detail |
|---|---|---|
| E1 | New project | `src/hermetic/Xake.Hermetic.fsproj`: `net462;netstandard2.0`, `FSharp.Core 8.0.100` pinned, `DisableImplicitFSharpCoreReference`, `GenerateDocumentationFile`. `ProjectReference` to `src/dotnet` (becomes nuspec dependency `Xake`), **plus** `src/core` with `PrivateAssets="all"`: `Xake.Dotnet`'s own core reference is `PrivateAssets=all`, so it does not flow. Namespace `Xake.Hermetic` in every moved file |
| E2 | Package metadata | `PackageId Xake.Hermetic`, description, tags (`reproducible;lockfile;sbom;cyclonedx;hermetic`), MIT, `Icon.png`, `src/hermetic/readme.md` (git-tracked casing; see session.md) |
| E3 | `InternalsVisibleTo` | none between packages (B5 made the needed helpers public); `tests` on `Xake.Hermetic`; nothing new in core |
| E4 | Tests | `src/hermetic.tests/` (net8.0) referencing all three; links `Common.fs`/`XakeTestBase.fs`. Moves: ProjectImport, LockDiff, FromLock, CscLock, Nuget, Restore, Sbom, Verify, StrongName, Pack, Sign. Stay: DotnetTasks, Toolset, CscServer, Resx and the engine tests |
| E5 | Docs | product docs to `docs/hermetic/` (lock format and `Lock.*` from csc-syntax.md, restore, nuget-sbom, verify, pack, strongname, signing, csc-server stays in base docs); `csc-syntax.md` keeps only composed mode + `Compilation.run`; `docs/features/hermetic-build/` stays the working folder (brief, session, tracker, experiments) |
| E6 | Build scripts | `build.fsx`: add `{ Name = "Xake.Hermetic"; Dir = "src/hermetic"; Needs = ["Xake"; "Xake.Dotnet"] }`; pack rules must not overlap: `out/Xake.(ver:*).nupkg` also matches `Xake.Hermetic.0.1.0.nupkg` (and the last matching rule wins), so pack into `out/pkg/<id>/` or name each target explicitly. `build.fsc.fsx`: third library, `open Xake.Hermetic` for `Fsproj`/`Project` |
| E7 | CI publish | independent tags: `v*` -> `Xake`, `hermetic-v*` -> `Xake.Hermetic`; one workflow, a step picks the project by tag prefix; `VERSION` + run number as today; `dotnet nuget push out/pkg/<id>/*.nupkg --skip-duplicate` |
| E8 | Bootstrap | after both land on nuget.org: `build.fsx` `#r "nuget: Xake, 3.4.0"`; `build.fsc.fsx` `#r "nuget: Xake, 3.4.0"` + `#r "nuget: Xake.Hermetic, 0.1.0"`, `.bootstrap/` retired; `samples/gettingstarted.fsx` bumped; `import*.fsx`/`verify-*.fsx` reference both packages |

**Versioning: independent (recommended).** Xake stays semver 3.x; Xake.Hermetic starts at
`0.1.0` (preview, allowed to break) and depends on `Xake [3.4.0, 4.0)`. That range only holds
because there is no IVT, so B5 is what makes independent versions possible. Lockstep (one tag,
two packages, same version) is simpler CI but would force Xake version bumps for hermetic-only
changes; choose it only if IVT turns out to be needed.

## 5. Risks, open questions, order

**Risks**
- An extension custom operation for `lock` may not work on the F# 8 floor. Fallback:
  `Lock.gated "path" settings`.
- A function-typed `Prepare` field removes structural equality from `CscSettingsType`/`RunOptions`.
  Mitigation: wrap it in a `Hooks` type with `[<ReferenceEquality>]`/custom equality.
- B2's churn in tests and scripts (`entry.Dependencies` -> `entry.Compilation.Dependencies`).
  The lock JSON stays byte-identical; prove it on dataengine (36/36) before merging.
- `build.fsc.fsx`, the build of record, starts depending on Xake.Hermetic (dogfooding, but a
  bootstrap cycle). Keep `build.fsx` on base only.
- B6 (fsc) is the biggest item and blocks retiring `Fsproj`. Doing it after the release is fine
  if B2's names are neutral.

**Questions for the user**
1. Package name: `Xake.Hermetic`? Or `Xake.Dotnet.Hermetic` / `Xake.Evidence`?
2. Seam A (`Compilation` + `run` + `Prepare` hooks), or the cheaper A-lite (whole `Entry` in base)?
3. `Fsproj` to hermetic (so Json and Roots are hermetic-only)?
4. Independent versions, with Xake.Hermetic at 0.x?
5. Is fsc through the runner (B6) required for 3.4.0, or can it follow?
6. `sign {}` returning a rule: yes?
7. The six SBOM questions (`nuget-sbom.md`) decide the `Sbom.for*` names.

**Suggested order and rough effort**

| Phase | Items | Effort |
|---|---|---|
| 1 | B1, spike (extension op + equality) | 0.5 day |
| 2 | B2, B3, B4, B5 | 2-3 days |
| 3 | B10 API polish (after approval of §3) | 1-1.5 days |
| 4 | B7, B8, B9 | 1 day |
| 5 | E1-E7 extraction | 1.5-2 days |
| 6 | Release Xake 3.4.0 + Xake.Hermetic 0.1.0, then E8 | 0.5 day |
| 7 | B6 fsc through the runner, then retire `Fsproj` into `Project.import` | 2-3 days |
