# Gaps: what the drafts need that Xake / Xake.Hermetic.Dotnet lack

Drafts (rev. 3, after review rounds 1-2 and the Fable review): [build.fsc.fsx](build.fsc.fsx) (Xake),
[trial-build.fsx](trial-build.fsx) (the trial product), [project.schema.json](project.schema.json) +
[example-project.json](example-project.json) (src/dotnet). Decisions: [../interview.md](../interview.md);
summary and PR plan: [../brief.md](../brief.md).

Layers: (a) script + descriptions, (b) lock generation, (c) build from locks. No engine change in this
plan. Sizes are rough production code, tests extra.

## The shape rev. 3 settles on

- (a) a description per project (Xake: `src/<dir>/xake.project.json`; the trial project: `<brand>/projects/<name>.json`
  in the experiment folder), data only, no placeholders, msbuild's values under the global properties the
  script passes. Written only by the explicit `import` command; a file is rewritten only on content change.
- (b) a lock per project x configuration (x brand by prefix): `locks/<name>/<cfg>.json`,
  `<brand>/locks/<name>/<cfg>.json`. Written only by the explicit `locks` command, referenced projects
  first. Packages are resolved by msbuild restore (existing `Restore.download` + `Nuget.readAssets`),
  never by fsc/csc; a version the current lock already holds is passed to restore as exact, unless
  `LOCK_REFRESH=<ids|*>` names it (npm install vs npm update).
- Staleness: `import` and `locks` write one line per written file into the committed
  `xake.fingerprints`; `build` warns on a stale line and uses the committed files, as CI does; `check`
  fails instead. No rule produces a description or a lock in this plan.
- (c) the compile rule reads only a lock; the output is the rule's target, refs are the lock's unhashed
  reference paths (written at (b) through the script's `outOf`), so the config/TFM choice is read from
  the lock. This recipe is the one the later msbuild-import system shares.
- Brands (the trial project): target prefixes. Import per brand with `Brand=<BRAND>`, so msbuild itself applies
  `src/<BRAND>.props`; descriptions, locks and outputs are stored per brand; commands carry the brand
  (`build::acme`, `clean::globex`).

### Lock format: no change

No new field, no version bump, no migration; existing 0.2 locks keep reading. Xake-generated locks use
existing slots:

- `Evaluation.ProjectRefs`: the referenced projects (project files), as msbuild-imported locks have them.
- `Dependencies.References`: package and framework assemblies with `Sha256`; a project ref's output as
  its layout path (`out/Release/netstandard2.0/Xake.dll`) with an empty `Sha256` (roadmap: in-lock
  project references stay unhashed). The path is the chosen config/TFM.
- `Compilation.Options`: the composed options without `-o`/`--out`; `Lock.compileAs` adds the target.
- `Compilation.Sources`: F# files in order; for C#, the description's masks verbatim (see below).
- `Generated`: AssemblyInfo with a literal `$(Version)` token, resolved at build; the lock content
  does not depend on the version.

If a field ever becomes necessary, it is optional and additive (read as absent by older readers).

### C# sources: masks, expanded at build

The description keeps msbuild's globs (`**/*.cs` minus excludes) and the lock copies them. `Lock.compileAs`
expands them with `getFiles`, which records a `GetFiles` dependency, so an added or removed `.cs` rebuilds
the assembly without a re-import and without the description going stale. When the expansion is an input
of a fingerprinted or (later) `cached` file, the sorted expansion is materialised and hashed as part of
that file's inputs. msbuild-imported locks keep their explicit lists.

### Lock path: `locks/<name>/<cfg>.json` (per brand: `<brand>/locks/...`)

- Central, not beside the description: a lock belongs to the build that consumes it (the trial project builds the
  repo-a projects with its own brand and configuration choice).
- Folder per project; the brand is a path prefix, not a file-name suffix (target-prefix convention).
- `<name>` is the description's `name` (project file name), unique within a build.

### the trial project descriptions live in the experiment folder

The trial project script, descriptions, locks and fingerprints sit in one experiment folder beside the two the trial project
repos; nothing is committed into the trial project repos. The folder may be its own git repository so that `import`
and `locks` diffs stay reviewable. Reason: the experiment must not touch the trial project's branches, and per-brand
descriptions belong to this build, not to the trial project projects. The schema's `project` field (project file
relative to the description) makes this possible; Xake's own descriptions stay beside the projects.

Facts (read-only, `git show`): `develop` is missing locally in repo-a, so
`origin/develop` (2db7eac, 2026-09-16) was used; repo-b `develop` = `origin/develop`
(a73772b, 2026-09-18). The brand props are at `src/Acme.props` and `src/Globex.props` in both repos,
selected by `$(Brand)` in `src/Directory.Build.props` (Acme when empty). Every assembly is
`<Brand>.Reports.Core.<X>`. Versions: root `Directory.Build.props` `VersionPrefix` 4.6.0
(page), 5.5.1 (repo-a). Release-only `CycloneDX.MSBuild`, `SauceControl.InheritDoc`.

## NEW functions

| # | Name | Semantics (one line) | Layer | Size |
|---|---|---|---|---|
| 1 | `Description` type, `load`/`save`, schema; `find fileset name`, `names`, `beside` | parse/validate; `save` writes only on content change; `find` reads only the matching file (one input); `beside` = `<project dir>/xake.project.json` | a | ~200 |
| 2 | `fsc { project d }` / `csc { project d }`, `config cfg` | description -> settings for one TFM: sources (C# masks kept), defines, grefs, resources, options, `byFramework`, generated AssemblyInfo (`$(Version)` token, internalsVisibleTo); Debug/Release presets as the SDK sets them; `--deterministic+`, `--pathmap` | a->b | ~280 |
| 3 | packages via msbuild restore | description packages -> restore project (`DotNetFwk.restoreProjectText`, `Restore.download`) -> `Nuget.readAssets` -> `References` + `Packages`; current lock versions passed as exact unless `LOCK_REFRESH` names them | b | ~100 |
| 4 | `Lock.generateAll descs cfgs lockOf outOf` | topological order over `projectRefs`; per project x config one lock, an entry per TFM; each ref's config (same, else Release, else the only one) and TFM (nearest) chosen from its lock, written as `outOf cfg fwk name` path, unhashed; transitive packages from the ref's lock; writes only on change; fingerprint line | b | ~200 |
| 5 | `Lock.compileAs lock fwk versions` | entry for `fwk`, output = the rule's target (+ `.xml` side output when doc), `need` the unhashed ref paths, expand C# masks via `getFiles`, HERMETIC gate inside, package root from `NUGET_PACKAGES`, `$(Version)` from the first matching `versions` mask over the project path; then `Lock.compile` | c | ~160 |
| 6 | `Lock.needAll descs cfg lockOf outOf` | `need` the dll of every entry of every described project's lock; a description without a lock fails naming `locks` | c | ~40 |
| 7 | `Description.import projects descOf props` | msbuild evaluation (+ design-time build) per project with global properties -> description via `save`; every msbuild file read becomes an input of its fingerprint line | a | ~250 |
| 8 | `Fingerprints.record/warn/check file` (Xake core, library) | one committed text file, one line per target, sorted: `<target> <sha256> <inputs sha256> <inputs...>`; hashes over LF-normalised text; `warn` prints one line per stale or hand-edited target, `check` fails | lib | ~120 |
| 9 | `Package.nupkg desc metadata ver layout`, `Package.projects`, `Package.push feed nupkgOf versions descs` | nuspec from the description (+ overrides), dependency groups per TFM, `lib/<tfm>/` (bundled refs), icon, readme; `push` needs every packed project's nupkg and copies it into the feed folder | pack | ~280 |
| 10 | `Pack.compare a b` | same version required; entries compared by content bytes only (zip timestamps and order ignored) | pack | ~50 |

Unverified in the engine: `::` in a phony pattern with a capture (`build::(b:*)`; `Path.matchGroups`
on command names); filesets over `../` (the trial project `projects`); lock tokenisation of paths outside
`$(ProjectRoot)` (may need an extra root, ~30 lines).

## Next step, separate PR: `cached` rule decorator

`rule |> cached inputs` (syntax open): the rule's file is kept in the repo, keyed by explicitly declared
inputs (a fingerprint line in the same `xake.fingerprints`); no source indexing. When the file exists the
rule's recipe is not triggered and the file is handed back; an optional staleness check warns. Library
functions plus at most a cache in the internal context, no engine change (`DelegatedRule` in Types.fs is
a precedent of wrapping a rule). First instance of the open "rules over rules" topic; it would let
`import`/`locks` become rules again (visible inputs, lock ordering for free) without running on `build`.

## Open questions left by rev. 3

1. Ref config fallback order (same -> Release -> only one): a script option, or fixed?
2. `versions` as a mask list over project paths vs a version per description.
3. msbuild-only behaviour (InheritDoc, CycloneDX, analyzers, strong-name keys, `TreatWarningsAsErrors`)
   for the trial project's basic pack: which are in scope.
4. Hermetic's reference to Xake: package ref to the published Xake vs project ref (nuspec of
   Xake.Hermetic.Dotnet).
5. A mask in `Compilation.Sources` of a Xake-generated lock: confirm it counts as "no format change"
   (shape unchanged, readers must expand).
