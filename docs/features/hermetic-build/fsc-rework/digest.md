# build.fsc.fsx rework: digest of PR #42

Facts only, gathered 2026-10-11 from `origin/pr/build-fsc-lock` for the interview on the user's
eight remarks (interview-plan.md). Line numbers are of `build.fsc.fsx` in that branch (289 lines).

## 1. What the PR changes

`git diff origin/dev origin/pr/build-fsc-lock --stat`: 13 files, +1002/-635. `build.fsc.fsx`
(251 lines touched), new `locks/xake.json` (726 lines), the three `projects/netstandard2.0/*.json`
of the old composed shape deleted (463 lines), `global.json` pinned to `10.0.401` with
`rollForward: disable`, `build.yml` matrix `pinned`/`floor`, CLAUDE.md, devprocess, roadmap,
session.

**CI does not run `build.fsc.fsx`.** Both `build.yml` legs and `publish.yml` run
`dotnet fsi build.fsx -- -- build test pack` (`dotnet build`, msbuild). The `pinned` leg only
means `build.fsx` runs on the pinned SDK. `check-locks` is not run on CI either. The hermetic
script is the "build of record" in docs (devprocess.md:66-105), but nothing in CI executes it.

`build.fsx` (201 lines) has the same shape: `Library` record (Name, Dir, Needs, Package),
one rule `out/(fwk:*)/(lib:*).dll|xml` that runs `dotnet build` per library, the same
pack/push targets. It still bootstraps on `Xake 3.4.0.21`.

## 2. Target by target

| Lines | Target | Inputs | Output / effect |
|---|---|---|---|
| 1-27 | header comment | | 27 lines of prose: lock, update/check, SDK pin, bootstrap, versions, test/pack still SDK |
| 28-29 | bootstrap | `#r "nuget: Xake, 3.6.0.24"`, `Xake.Hermetic.Dotnet, 0.2.0.25` | from nuget.org |
| 37-73 | helpers | regex over fsproj `<Version>` (38-40), `vars` (42-53), `frameworks` (56), `libraries` name+fsproj (59-62), `lockPath` (65), `binaries` (68-73) | |
| 80-89 | `importInto output` | the three fsproj, `netstandard2.0`, Release | `Project.import` into `ProjectRoot/output` |
| 94-105 | `checkXakeDependency nupkg` | a nupkg | opens zip, parses nuspec XML, checks `Xake` dep present / `Xake.Dotnet` absent |
| 116-126 | package names, `packagePath`, `restoreSource`, `dotnet` | | |
| 134-135 | script vars | | `HERMETIC=on`, `NUGET_PACKAGES=.packages` |
| 138 | `main` | | `build`, `test` |
| 140 | `build` | | `<== binaries` (6 files: 3 libs x dll/xml, netstandard2.0 only) |
| 141 | `clean` | | `rm out` |
| 143-149 | `test` | FILTER | `dotnet test src/tests`, `dotnet test src/hermetic.tests` (msbuild builds everything again) |
| 154 | `update-locks` | fsproj, global.json, feeds | `importInto locks/xake.json` (restore + design-time build per project) |
| 157-176 | `check-locks` | as above | imports into `obj/xake/check/xake.json`, entry-by-entry `Lock.diffText`, fails if stale (20 lines, a copy of workflows.md §B sample) |
| 180-220 | `out/(fwk:*)/(lib:*).dll` + `.xml` | `locks/xake.json`, package folder, built referenced libs | `Lock.loadWith` + `entryFor` + path remap + `HermeticMode.enforce` + `Lock.compileWith` |
| 225-234 | `pack`, `pack-hermetic` | `VERSION`, `HERMETIC_VERSION` | `need` the nupkg paths |
| 236-244 | `out/pkg/Xake/Xake.(ver:*).nupkg` | | `dotnet pack src/dotnet /p:Version=ver` |
| 246-265 | `out/pkg/Xake.Hermetic.Dotnet/...(ver:*).nupkg` | NUGET_SOURCE | `dotnet pack src/hermetic` + `checkXakeDependency`, deletes nupkg on failure |
| 269-287 | `push`, `push-hermetic` | NUGET_KEY | `dotnet nuget push` to nuget.org v2 endpoint |

## 3. The lock (`locks/xake.json`, 726 lines)

- Top level: `Configuration` ("Release"), `Properties` ({}), `Entries` (3: Xake, Xake.Dotnet,
  Xake.Hermetic.Dotnet, all netstandard2.0).
- Per entry: `Name`, `Framework`,
  `Evaluation` {Project, ProjectRefs, Imports [path+sha256 of every msbuild file read], Sdk,
  SdkPin "exact 10.0.401", Properties (AssemblyName, Version, InformationalVersion
  `1.0.0+$(SourceRevisionId)`, IntermediateOutputPath, TargetPath, ...)},
  `Compilation` {Directory, Options (msbuild's fsc line with `@Defines`/`@References`/`@Sources`
  placeholders), Defines (12), Sources (35 / 18 / 14, generated AssemblyInfo first), Generated
  (content of generated files), Resources},
  `Dependencies` {Compiler (`$(DotnetRoot)/sdk/10.0.401/FSharp/fsc.dll` + sha256 + version),
  References (114 / 118 / 119, path + sha256), Analyzers (0), Packages (3 / 10 / 11: Id,
  Version, Sha512, Direct, DependsOn), Prerequisites (dotnet-sdk 10.0.401, pin global.json)}.
- Hashed: every msbuild import, the compiler, every reference (SHA-256), every package
  (SHA-512 of nupkg). Not hashed: sources (they are the repo), project-reference outputs (empty).
- Paths are tokenized: `$(ProjectRoot)`, `$(NuGetPackageRoot)`, `$(DotnetRoot)`.
- The assembly version is baked in at import: Xake and Xake.Dotnet carry `1.0.0` (their fsproj
  declares no `<Version>`), hermetic `0.2.0`. `VERSION` (45) no longer reaches the dlls; only
  `dotnet pack` uses it (comment at 43-44).
- It is a machine record: ~240 lines per project, ~115 of them reference lines. Source list,
  package ids/versions and project refs are inside it, but interleaved with hashes and
  SDK-internal paths.

## 4. Boilerplate inventory (remark 1)

| Lines | Snippet | What it would ideally be |
|---|---|---|
| 1-27 | header prose | a few lines; most of it explains workarounds (bootstrap, version suffix, net462) |
| 37-40 | `projectVersion` regex over fsproj | a fact read from the project / description, not a regex in the script |
| 56-73 | `frameworks`, `libraries`, `lockPath`, `binaries` comprehension | derivable from the lock itself (its entries name lib + framework) |
| 80-89 | `importInto` with `getCtxOptions` + `Path.Combine` | `Project.import` accepting a project-relative `Output` |
| 140 | `"build" <== binaries` | "build all of this lock" out of the box (roadmap gap 6: "no one-call build of a whole imported lock") |
| 157-176 | `check-locks` (20 lines) | a library call (roadmap gap 6: "no document-level `Lock.diff`"); same code is in workflows.md §B |
| 180-220 | the compile rule (40 lines) | `Lock.loadWith` + `entryFor` + remap + `need` + `enforce` + `compileWith` is the same in import.fsx, the repo-a verification script (114-146), import-page.fsx; a library rule/function "compile entry X of lock L into layout Y" |
| 189-190, 217-218 | package-root plumbing (`DotNetFwk.packageRoot`, `Roots.packageRootOverride`, `Restore.Options` with `PackageRoot`) | implied by `NUGET_PACKAGES` already set at 135 |
| 214-216 | `HermeticMode.enforce` by hand | comment says `Lock.compile` 0.2 does not gate yet; should be inside `Lock.compile` |
| 94-105 | `checkXakeDependency` (zip + XDocument) | exists only because `dotnet pack` decides the nuspec; with a script-owned nuspec it disappears |
| 112-118, 236-265 | package-per-folder layout + two pack targets | comment 112-115: one mask `out/(id:*).(ver:*).nupkg` is ambiguous (`Xake.Hermetic.Dotnet.0.1.0` vs Xake `Hermetic.Dotnet.0.1.0`), so ids are hard-coded into two rules |
| 225-234, 269-287 | `pack`/`pack-hermetic`, `push`/`push-hermetic` pairs | one rule per package with an argument |
| 181-183 | `getRuleMatch "fwk"`, `"lib"`, `getCtxOptions` | roadmap "Rules with arguments" (positional binding of capture groups) |

## 5. Path manipulations (remark 2)

| Lines | Computes | Xake today | Gap |
|---|---|---|---|
| 72 | `$"out/{fwk}/{name}.{ext}"` | string interpolation; fine | none |
| 87 | `Path.Combine(ProjectRoot, output)` | `</>` (PathExt.fs:14) is the same `Path.Combine` | `Project.import` writes `Output` relative to the process cwd, not the project root (workflows.md §B) |
| 162 | `Path.Combine(ProjectRoot, p)` for `Lock.read` | same | `Lock.read` is pure and takes an absolute path; `Lock.load` is the recipe that resolves |
| 184 | `ours lib ext` = absolute `ProjectRoot/out/fwk/lib.ext` | none for "absolute path of a target in this build" | the rule's own target is available via `getTargetFiles()`, but the *other* libs' outputs need construction |
| 198 | `entry.Output` -> `Path.GetDirectoryName` (msbuild intermediate dir) | none | knowledge of msbuild's layout inside the script |
| 199-202 | project references = references with empty `Sha256` | none | the repo-a verification script:128-136 explicitly rejects this test (a stale `bin/` makes the import hash it; decide by name); roadmap item "in-lock project references must stay unhashed" (2026-10-11) is the same issue |
| 203-206 | `produced`: same dir as intermediate, file name = lib, ext in dll/xml/pdb | none | |
| 207-211 | `Lock.mapPaths`: unbuilt ref -> `out/fwk/<ref>.dll`, produced -> `out/fwk/<lib>.<ext>`, else unchanged | `Lock.mapPaths f` is the only tool | no "relocate outputs" / "redirect project refs" helper |
| 213 | `Path.GetRelativePath(ProjectRoot, ours ...)` for `need` | `need` accepts absolute paths too (import-page.fsx comment) | relativizing is unnecessary |
| 236, 246 | `$"out/pkg/{id}/{id}.(ver:*).nupkg"` | rule patterns with capture groups | ambiguous masks for dotted ids |

Xake facilities available: `Path.matches`/`matchGroups` (Path.fs:259-266) and `fileset`
(`basedir`, `includes`, `excludes`, `!!`, `@@`), `</>`, `-.`, `<.>` (PathExt.fs), named capture
groups in rule patterns, `..` in patterns (fixed in engine, import-page.fsx:34-46). None of
them knows the lock's path vocabulary (`$(ProjectRoot)` tokens, msbuild's `obj/` layout).

## 6. Data manipulation inside the compile rule (remark 3)

- 191 `Lock.entryFor framework name lock`: entry lookup.
- 198 `intermediate`: msbuild's `-o:` directory from `entry.Output`.
- 199-202 `unbuilt`: list of project-reference paths (empty hash).
- 203-206 `produced`: predicate "this path is msbuild's output of this lib".
- 207-211 `mapped`: the entry with every path rewritten by those two classifications.
- 213 `need`: built refs derived from `unbuilt`.
- 216 `Fsc.hermeticInputs mapped.Fsc`: inputs extracted for the gate.
- 217-218 `Lock.Options` record update nested inside `Restore.Options` record update.
- Also 165-172 (`check-locks`): manual join of two entry lists by `(Name, Framework)`, three
  comprehensions to produce `+`/`-`/`~` lines.

## 7. Packing (remark 5)

Now: `dotnet pack src/dotnet` and `dotnet pack src/hermetic` (236-265). Each is a full msbuild
restore + build of both TFMs (net462 and netstandard2.0), from nuget.org, ignoring
`out/netstandard2.0/*.dll` built by `build`. The hermetic package compiles against the
*published* `Xake` (`PackageReference Include="Xake" Version="[3.6.0.24, 3.7)"`). So the
shipped nupkg contains msbuild binaries, not the hermetic ones; `test` likewise tests msbuild
binaries.

The `Xake` nupkg holds two assemblies per TFM: Xake.Dotnet + the core via
`BuildOutputInPackage` of the `PrivateAssets="all"` project reference (src/dotnet fsproj:64,76).
Metadata in fsproj: PackageId, Authors, Description, ProjectUrl, Tags, `PackageLicenseExpression
MIT`, `PackageIcon Icon.png`, `PackageReadmeFile readme.md` (packed from repo root /
src/hermetic), dependencies (FSharp.Core; Xake for hermetic). Core fsproj has
`IsPackable=false`.

`Xake.Hermetic.Dotnet.Pack` (pack.md): `Pack.nupkg output nuspecPath (files: Entry list)
options` -- a byte-reproducible zip (sorted entries, fixed timestamp or `SOURCE_DATE_EPOCH`,
content-derived psmdcp GUID). The caller supplies the `.nuspec` file and every `{Path; Source}`
entry; it does not generate a nuspec, does not compute dependencies, no `.snupkg`, no signing
(delegated `Sign`, skeleton only). `Sbom.forPackage` hashes the result.

So a hermetic nupkg needs: a nuspec (id, version, metadata, dependency groups per TFM) written
by someone, the dlls/xml per `lib/<tfm>/`, icon + readme entries. The blocker is net462: the
lock imports `netstandard2.0` only (`frameworks`, 56) and the comment at 55 says net462 comes
from `dotnet pack`. repo-a's import already locks `net472` beside netstandard2.0
(the repo-a verification script:63), so a net462 entry is an import option, not a missing feature;
whether its references come from packages under `HERMETIC=on` is not checked here.

## 8. nuget.org touch points (remark 8)

| Where | What |
|---|---|
| 28-29 | `#r "nuget: Xake, 3.6.0.24"`, `Xake.Hermetic.Dotnet 0.2.0.25`: fsi restores from nuget.org (or the folder if present) |
| 154, 159 | `Project.import`: msbuild restore of the three projects from feeds |
| 189-218 | replay: `Restore.ensure` downloads what `.packages` lacks (FSharp.Core, NETStandard.Library, the published Xake for the hermetic entry) |
| 147-148 | `dotnet test`: full restore |
| 238, 253 | `dotnet pack`: full restore; hermetic restores published Xake (NUGET_SOURCE adds a feed, 50-52) |
| 274, 284; publish.yml | push to nuget.org |
| build.yml | `setup-dotnet` downloads SDKs from Microsoft (not nuget.org) |

What exists (restore.md, workflows.md §F): `NUGET_FETCH=off` (no network for every lock's
restore; fails naming missing packages), `nofetch` per block, `Restore.into dir` +
`Lock.restore options doc` to fill a folder once on a connected machine, then carry it. Restore
downloads go through `dotnet restore` with the user's `nuget.config`, so a private feed is a
`nuget.config` matter. Only what a compilation reads is restored, not the whole graph (restore.md
"Left open"). Breaks offline: `Project.import`/`check-locks` (msbuild restore), `#r "nuget:"`
unless the packages are in the folder. No mechanism for a repo-local or sibling-repo package
folder beyond pointing `NUGET_PACKAGES` at it. The `X.Y.Z.<run>` versions (header 22-24) are a
publish.yml artefact.

## 9. Import and a committed description (remarks 4, 7)

Current model: msbuild project files are primary; `update-locks` is a repeatable deliberate step
after any fsproj/props/package/global.json change; the lock is committed and reviewed as a diff
(workflows.md §B "When to re-import"). The lock is the only description; it is machine-shaped
(§3). There is no human-readable per-project file, and nothing generates F# code.

Import mechanics (fsc-design §6): `Project.import` runs a design-time build
(`-t:PrepareResources;Compile`, `ProvideCommandLineArgs=true`, `SkipCompilerExecution=true`)
and reads `FscCommandLineArgs`/`CscCommandLineArgs`, the compiler path property, `-pp` imports,
`project.assets.json` packages, generated files. Output per (project, framework): the full
command line split into options/defines/sources, references with hashes, packages graph,
generated files' content. What it knows that a description could hold: source list in order,
direct package ids + versions (`Direct: true`), project refs (`Evaluation.ProjectRefs`),
defines, TFM, assembly name, version, `Properties` passed in (e.g. `Brand`), `Variant`.

A composed alternative already exists (lock.md table): `fsc { src ...; targetfwk; ref ...;
resolve }` -> `Lock.record`/`Lock.build`/`Lock.verify` (workflows-ru §4, Scenario A): the script
is the description and the lock is derived from it. The old `projects/netstandard2.0/*.json`
(deleted by this PR) were an earlier per-project data description read by the retired `Fsproj`.

## 10. the trial product (remark 6)

- `the repo-a verification script`: 215 lines; 3 C# projects, 2 frameworks (netstandard2.0, net472),
  2 brands (Acme, Globex) -> 2 locks, 12 assemblies, SBOMs. Rules: `locks/(brand:*).json`
  (import with `Properties = ["Brand", b]`, `Variant = b`), compile rule at 114-146 (same remap
  logic, by name), sbom rule.
- `import-page.fsx`: 235 lines; page's 12 projects + repo-a's 3 across sibling repos,
  `-p:LocalBuild=true`, extra root `$(RepoARoot)`, two compile rules (own tree and
  `../repo-a/...`), netstandard2.0 only.
- Both are verification scripts (proving byte identity, repo-a 36/36), not product builds:
  no pack, no tests, no versioning, no signing.
- On this machine `~/path/to/` has: repo-a, -document, -page, repo-docs,
  repo-installer, repo-web-designer, repo-web-viewer, repo-win, repo-win-qa,
  ar-web-designer, ar-web-designer-qa-automation, arsamples, sample-ar-webapi. repo-a has
  `Directory.Packages.props` (central package versions), `NuGet.config`, `global.json`; page has
  `NuGet.config`, `NuGet.Local.config`, a `packages/` folder, `CycloneDX.targets`,
  bitbucket-pipelines.yml.
- A two-vendor product build would add per brand: lock per brand (exists), pack per brand,
  signing, versioning, cross-repo package vs project references (`LocalBuild`).

## 11. Roadmap items that bear on this

- Engine, "Rules with arguments" (2026-10-02): positional binding of capture groups to lambda
  parameters, `"out/(fwk:*)/(lib:*).dll" ==> fun (fwk, lib) out -> recipe {...}`, arity checked
  at load; `build.fsc.fsx` named as first customer.
- Hermetic gap 6: no document-level `Lock.diff`; no one-call build of a whole imported lock;
  drift messages print absolute paths.
- Hermetic gap 5: import as a file rule re-imports on every fresh clone; `update-locks` is the
  pattern.
- Small: in-lock project references must stay unhashed (2026-10-11).
- SDK restore by the lock (`dotnet-install` into a private `DOTNET_ROOT`) and an F# compiler
  `toolset` over `FSharp.Compiler.Service`: the only ways to drop the exact SDK pin.
- Binary compatibility between packages: hermetic depends on base within one minor (now
  `[3.6.0.24, 3.7)`).
- restore.md "Left open": only compile-read packages are restored, not the full graph (matters
  for packing/publishing from a folder).

## 12. What Xake itself should be able to do (candidate gaps)

1. Build every entry of an imported lock in one call / one rule, with project refs wired.
2. Relocate a lock entry's outputs to the script's layout and redirect in-lock project
   references (today `intermediate`/`unbuilt`/`produced`/`mapped`, 198-213), deciding refs by
   name, not empty hash.
3. Run the `HERMETIC` gate inside `Lock.compile` (214-216).
4. Take the package root from `NUGET_PACKAGES` without the script threading it (189-190, 218).
5. `check-locks` as a library call (document-level diff, tokenized paths).
6. `Project.import` / `Lock.read` taking project-relative paths.
7. Rule capture groups bound as arguments (roadmap).
8. Rule patterns that disambiguate dotted package ids (112-115).
9. Nuspec generation from project metadata and the lock's direct packages, and a pack rule over
   hermetic binaries (`Pack.nupkg` takes a ready nuspec).
10. net462 in the self-build lock (import option exists; package-only references under
    `HERMETIC=on` unverified).
11. A human-readable per-project description (sources, packages, refs, defines, variants) and
    its relation to the lock: does not exist.
12. Fully offline path: bootstrap of Xake itself, test projects and import without nuget.org;
    full-graph restore for packing.
13. A test path over hermetic binaries (`dotnet test` rebuilds through msbuild).
14. CI that actually runs `build.fsc.fsx` (and `check-locks`).
