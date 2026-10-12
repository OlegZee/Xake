# Can the goals be reached with a standard tool (Paket, NuGet lock)?

Status: analysis for the owner, 2026-10-12. Read-only research; no decision. Goals are those of
[brief.md](brief.md) §1-§5 and the decisions in [interview.md](interview.md) (Q3, Q6, FR, phony
lock rule, fsc identity by SDK prerequisite). Facts about Paket and NuGet carry a URL; "(inferred)"
marks a conclusion drawn from source or docs rather than stated there. Paket facts are as of
Paket 11.0.1 / 12.0.0-alpha001 (October 2026).

Context worth remembering: Xake itself used Paket until 2018 and removed it (commit 894fe73,
"removed paket when not required"). The product brief already places Paket as "versions +
transitive, for restore; not hermetic; no SBOM" ([../brief.md](../brief.md) §7). This page checks
that one-liner in detail.

## 1. What Paket provides, and what it does not

**Lock.** `paket.lock` pins the resolved version of every direct and transitive package, each
package's own dependency constraints, the `remote:` source per group, group options, framework
restrictions per dependency, and git/github dependencies by commit
(https://fsprojects.github.io/Paket/lock-file.html; a real one:
https://github.com/fsprojects/Paket/blob/master/paket.lock). It records **no package content hash**:
the format has no sha512 field; hashes in the lock were requested in 2014-2015 and never added
(https://github.com/fsprojects/Paket/issues/27, https://github.com/fsprojects/Paket/issues/1287);
verifying a cached nupkg from another source is still an open issue
(https://github.com/fsprojects/Paket/issues/1546). `http` file dependencies are pinned by URL only,
no hash (https://fsprojects.github.io/Paket/http-dependencies.html).

**Resolution policy.** One resolution per *group* for the whole repository (all projects of a group
share one version of a package; https://fsprojects.github.io/Paket/groups.html). Options:
`framework:` restriction, `strategy: min|max`, `lowest_matching`, `redirects`,
`storage: packages|none|symlink` (https://fsprojects.github.io/Paket/dependencies-file.html).
`paket install` keeps every locked version whose `paket.dependencies` line did not change;
`paket update [--filter <regex>] [--group]` re-resolves explicitly; `paket outdated` lists what
would move (https://fsprojects.github.io/Paket/paket-install.html,
https://fsprojects.github.io/Paket/paket-update.html,
https://fsprojects.github.io/Paket/paket-outdated.html). This is the same contract as our kept
versions + `LOCK_REFRESH=<ids|*>` (R2.2), and it is Paket's strongest match with the brief.

**Restore and offline.** `paket restore` needs the lock and never resolves. Per package it tries,
in order, the NuGet global folder (an existing non-empty nupkg is accepted with no further check),
the SDK fallback folder, the configured caches, then the feed
(https://github.com/fsprojects/Paket/blob/master/src/Paket.Core/Dependencies/NuGet.fs,
https://fsprojects.github.io/Paket/paket-restore.html). So it works offline on a warm cache
(inferred), but checks **existence and non-zero length only**; offline failures are still reported
for github deps and the msbuild restore (https://github.com/fsprojects/Paket/issues/3432,
https://github.com/fsprojects/Paket/issues/3393).

**Local packages.** `cache ./nupkgs versions: current|all` keeps every used nupkg in a folder
(in-repo or a share), the cache is an implicit source, and a package that vanished from the feed
still restores and updates from it (https://fsprojects.github.io/Paket/caches.html). A local folder
is an ordinary `source`. `paket.local` overrides a package with a folder (one exact version,
the nupkg must already be there) or a git repo with a `build:` command, affects `restore` only,
warns that the build may not be reproducible, must not be committed, and its page says it works
with old-style projects only; with `storage: none` it fails as unsupported
(https://fsprojects.github.io/Paket/local-file.html, NuGet.fs above). `git`/`github` dependencies
pin source files or whole repos by commit into `paket-files/`, optionally exposing a repo's built
nupkgs as a source (https://fsprojects.github.io/Paket/git-dependencies.html,
https://fsprojects.github.io/Paket/github-dependencies.html).

**Consuming the resolution without msbuild.** SDK projects consume Paket through
`Paket.Restore.targets` (msbuild; writes `obj/*.paket.resolved`, `*.paket.references.cached`,
`*.paket.props`; https://github.com/fsprojects/Paket/blob/master/src/Paket.Core/Installation/RestoreProcess.fs).
Without msbuild there are two routes:
- `paket generate-load-scripts --framework <tfm> --group <g> --type fsx` writes
  `.paket/load/<tfm>/<group>.group.fsx` with `#r` per assembly and `#load` of package scripts
  (https://fsprojects.github.io/Paket/paket-generate-load-scripts.html). It is built on
  `getLegacyReferences`, i.e. **`lib/` implementation assemblies, not `ref/`**
  (https://github.com/fsprojects/Paket/blob/master/src/Paket.Core/PaketConfigFiles/DependencyCache.fs,
  https://github.com/fsprojects/Paket/blob/master/src/Paket.Core/PaketConfigFiles/InstallModel.fs).
  Fine for `dotnet fsi`, wrong as a compile reference list for packages that ship `ref/`.
- Paket.Core API from an F# script: `Dependencies.Locate`, `GetLockFile`, `GetInstalledPackages`,
  `GetDirectDependencies`, `GetInstalledPackageModel`, `GetLibraries` (again `lib/` only)
  (https://github.com/fsprojects/Paket/blob/master/src/Paket.Core/PublicAPI.fs). A per-TFM
  compile list with `ref/` preference needs the lower-level `InstallModel.getCompileReferences
  profile model` on each package model (public module function, not a documented API; inferred).
  CLI: `find-refs`, `show-installed-packages` give package lists, not assembly paths
  (https://fsprojects.github.io/Paket/paket-find-refs.html,
  https://fsprojects.github.io/Paket/paket-show-installed-packages.html).

**Toolchain, integrity, provenance.** Paket pins nothing but itself (the tool manifest / `version`
line): no SDK, no compiler, no build tool (release notes and docs say nothing of it;
https://github.com/fsprojects/Paket/blob/master/RELEASE_NOTES.md). No hermetic/integrity mode, no
SBOM, no binary verification; package signatures are not verified
(https://github.com/fsprojects/Paket/issues/3832).

**Pack.** `paket pack` with `paket.template` (metadata, `files`, dependencies), `--lock-dependencies`
(exact versions from the lock), `--minimum-from-lock-file`, `--pin-project-references`,
`--include-referenced-projects`, `--interproject-references`, `--version`
(https://fsprojects.github.io/Paket/paket-pack.html,
https://fsprojects.github.io/Paket/template-files.html). The writer uses `ZipArchive.CreateEntry*`
with a fixed `.psmdcp` id, so entries carry file or current timestamps: not byte-reproducible
without post-processing (https://github.com/fsprojects/Paket/blob/master/src/Paket.Core/Packaging/NupkgWriter.fs;
inferred).

**Status.** 11.0.0 (2026-10-05) and 11.0.1 (2026-10-11) after a gap since 10.3.1 (2026-01-20);
11 targets net10.0 and deprecates `paket.exe` and the bootstrapper; 12.0.0-alpha001 ships only as
a .NET tool on the .NET 10 runtime, Paket.Core netstandard2.0 only, and warns the next `update` may
rewrite framework restrictions (https://github.com/fsprojects/Paket/releases, RELEASE_NOTES.md
above). Recent commits come mostly from one maintainer; about 684 open issues
(https://github.com/fsprojects/Paket/commits/master). No support for Central Package Management:
Paket replaces `PackageReference` resolution rather than complementing it (inferred; nothing in
docs or issues). Install as a local tool: `dotnet tool install paket`
(https://fsprojects.github.io/Paket/installation.html).

**NuGet's own lock, the "standard" alternative.** `packages.lock.json` per project
(`RestorePackagesWithLockFile`, `--use-lock-file`), locked mode (`--locked-mode`,
`RestoreLockedMode`), `--force-evaluate` to re-resolve floating versions; per TFM: `type`
Direct/Transitive/Project, `requested`, `resolved`, `contentHash` (base64 SHA-512), dependencies.
A library's lock does not constrain its consumers; SDK implicit references make it SDK-sensitive
(docs recommend `rollForward: disable`); package pruning on net10 changes contents
(https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies).
A content mismatch fails restore (NU1403;
https://devblogs.microsoft.com/nuget/enable-repeatable-package-restores-using-a-lock-file/); for a
signed package the hash excludes the signature
(https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.Packaging/PackageArchiveReader.cs).
`packageSourceMapping` is ignored for a package already in the global folder
(https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping). Signature
verification on restore: Windows always, Linux by default since SDK 8, macOS off
(https://learn.microsoft.com/en-us/dotnet/core/tools/nuget-signed-package-verification).
Deterministic pack by default only from .NET 11, timestamps via `DeterministicTimestamp` /
`SOURCE_DATE_EPOCH` (https://learn.microsoft.com/en-us/nuget/create-packages/deterministic-packages).
No SBOM from NuGet (open epic https://github.com/NuGet/Home/issues/12497). Note our lock's
`Packages[].Sha512` is that same `contentHash`, read from `.nupkg.metadata` (lock.md "Packages").

## 2. Goal by goal

**(a) Per-project description.** `paket.references` is a list of package ids (per group) per
project; `paket.dependencies` holds sources and constraints repo-wide; `paket.template` holds pack
metadata. Missing entirely: sources/C# masks, defines, compiler options, frameworks per project
(Paket's `framework:` restricts resolution, it does not say what a project targets), project
references, configuration presets, analyzers, resources, brand variants. Covered: package ids and
version constraints (which `xake.project.json` already holds, imported from the fsproj), pack
metadata (overlaps with the description's `package` block). Paket would split the description
into three files of two tools, contradicting "data only, one description, imported from the
fsproj" (Q1, Q2) unless import also generated `paket.references`.

**(b) The lock.** Ours is per project x configuration and records the compile input: command line,
sources, references and analyzers by SHA-256, compiler/SDK prerequisite, generated sources,
evaluation inputs, project-ref output paths, and the package graph with SHA-512. `paket.lock` is
per repo/group and records the package graph only, without hashes. It could be the *resolver's*
lock upstream of ours: `Locks.generate` reads versions from `paket.lock` instead of a restore
graph, while `Entry.Packages` keeps id/version/Direct/DependsOn from Paket and Sha512 from the
cache. It cannot *replace* the package section, because (1) no hashes; (2) the lock format is
frozen (FR 5), so the section stays and would be filled from a second committed file; (3) Paket
unifies versions per group across projects, while our per-project restore lets two projects
differ (R1.3 leaves unification to msbuild) - a behaviour change, arguably welcome, but a change.
Kept versions and explicit refresh: Paket's `install`/`update --filter` is the same semantics as
`LOCK_REFRESH`, already implemented and liked (implementation session 1).

**(c) Build from the lock without msbuild.** Already done without Paket: `Locks.compileAs`
replays the lock, restoring by exact id/version (PackageDownload) and checking hashes; no msbuild
(pr7-report "Fresh-clone proof"). Paket's restore could fetch the same packages, but the lock
already names every file, so the resolver is not needed at (c). For (b), `generate-load-scripts`
is a dead end as a compile reference list (`lib/` not `ref/`, fsx-oriented `#load`s, per group
not per project); Paket.Core's `InstallModel.getCompileReferences` could give the list, at the
cost of a Paket.Core dependency in `Xake.Hermetic.Dotnet` (netstandard2.0 in 12.x, so loadable from
our net462/netstandard2.0 package, inferred) and of re-implementing what `Nuget.compileItems` does
today from `project.assets.json`. Project references and output layout stay ours either way.

**Package resolution outside fsc/csc (FR).** Paket instead of `Described.restore` (dotnet restore
of a generated PackageReference project, PR 2):
- For: faster, no msbuild process per project x TFM at lock time (inferred); one repo-wide
  transitive pin; `framework:` restriction; per-id refresh built in; `cache` for vendoring.
- Against: a second resolver whose answers differ from what msbuild/NuGet would give the same
  fsproj (the fsproj stays primary, Q2, and its import reads `PackageReference`); SDK-implicit
  packages (FSharp.Core, NETStandard.Library, System.ValueTuple on net4x) must be added by us
  anyway (PR 2 q3 already adds them in `Described`); no CPM; one maintainer, a major version in
  flight that rewrites restrictions; nothing gained offline at (c), which never resolves.

**Local feed / no publishing (Q6).** Our decision: a local shared folder is both publish target and
feed. NuGet does this natively (a folder `<add>` in `nuget.config`, `RestoreAdditionalProjectSources`,
or `#i "nuget: <folder>"` in `dotnet fsi`); Paket does it with `source ../nuget-local`. Paket's
`cache` adds what we lack, a vendored package folder that keeps nupkgs that left the feed (the
product brief's rung 6, "no vendored package source mode" in hermetic-architecture.md §1), but
flow F already gets an offline build from a pre-filled `NUGET_PACKAGES` folder plus
`NUGET_FETCH=off`. Bootstrap of the self-build: the script's `#r "nuget: Xake"` is resolved by
`dotnet fsi`'s NuGet manager, not Paket; `paket.local` cannot help (`restore` only, SDK-style
limitation, not committed). The decided route (intermediate release `v3.7.0`/`hermetic-v0.3.0`,
then `#r nuget`) needs no Paket; a pre-release in the local folder via `#i "nuget:"` is the NuGet
equivalent.

**Pack (PR 8).** `paket pack` from a `paket.template` with a `files` section pointing at (c)'s
`out/Release/<tfm>` binaries and `--lock-dependencies` would produce the nupkgs without msbuild.
Entry-content equality (the acceptance) is plausible since timestamps are ignored by it (inferred,
untested). It duplicates `Pack.nupkg` (deterministic zip, sorted entries, fixed timestamps,
content-derived ids, pack.md), adds a template file per package beside the description's `package`
block, and gives no SBOM entry or signing step. Dependency ranges from the lock: ours come from
the description/lock as well.

**SBOM, verify, signing, HERMETIC gate, fingerprints, `cached`.** Paket offers none of them
(see §1 "Toolchain, integrity, provenance"); NuGet offers none either beyond the lock's
`contentHash` and restore-time signature verification. All stay ours.

**ActiveReports.** Both repos use `PackageReference` with Central Package Management
(dataengine) and a private feed with `LocalBuild=true` swapping packages for sibling
`ProjectReference`s (page) ([../brief.md](../brief.md) §8b). Nothing may be committed into the AR
repos (brief §2), so Paket could live only in the experiment folder, as a second statement of
versions already in `Directory.Packages.props` - the two-sources-of-truth problem Q3 rejected.
Groups could model the brand's package identity switch (`DS.Documents` vs `GcDocs`), but brands
are already per-brand imports and locks by prefix; `LocalBuild` is handled by import + extra
roots. Converting AR to Paket (`convert-from-nuget`) is out of the experiment's remit: dead end.

**Operational cost.** Xake repo: a `dotnet tool` manifest, `paket.dependencies`, `paket.lock`,
`paket.references` per project, `Paket.Restore.targets` under `.paket/`, conversion of
`PackageReference` in five fsproj (or Paket kept beside them for the description route only),
CI restore step, and re-learning a tool the repo dropped in 2018. Customer repos: Paket is an
all-or-nothing replacement of their package management; for a C# shop with CPM and source mapping
that is a migration, not an option the hermetic package can require.

## 3. Summary table

| Goal | Paket | NuGet lock | What stays ours |
|---|---|---|---|
| (a) description: packages + constraints | partially (`paket.references` + `.dependencies`) | no (fsproj) | sources, masks, defines, options, TFMs, project refs, presets, brands |
| (a) pack metadata | partially (`paket.template`) | no (fsproj) | the description's `package` block |
| (b) version pinning, transitive graph | yes (repo-wide per group) | yes (per project x TFM) | per-project x configuration entries |
| (b) package content hash | no | yes (`contentHash`, SHA-512) | already in `Packages[].Sha512` |
| (b) kept versions + explicit refresh | yes (`install` / `update --filter`) | partially (`--force-evaluate`, all floating) | `LOCK_REFRESH` exists |
| (b) compile inputs by hash, compiler/SDK, generated, options | no | no | the whole lock |
| (c) build without msbuild | partially (fsx load scripts, `lib/` only) | no | `Locks.compileAs`, layout, project refs |
| restore offline / local feed | yes (cache, folder source; existence check only) | yes (folder source, hash on restore) | flow F, `NUGET_FETCH` |
| vendored package folder | yes (`cache`) | no | not built |
| bootstrap of the self-build | no (fsi uses NuGet) | n/a | intermediate release / `#i "nuget:"` |
| deterministic nupkg | no (timestamps) | .NET 11+ only | `Pack.nupkg` |
| SBOM, verify, signing, HERMETIC, fingerprints, `cached` | no | no | all |
| ActiveReports (CPM, source mapping, no commits) | no (replaces their package management) | is what they use | import + per-brand locks |

## 4. Three strategies and their effect on the PR plan

1. **Paket replaces our package layer.** `Described.restore` (merged in PR 2) and the graph read in
   `Locks.generate` (PR 3) are rewritten to read `paket.lock` and compute per-TFM references via
   Paket.Core; descriptions' `packages` move to or are mirrored in `paket.references`; import (PR 5)
   must generate Paket files or the fsproj stops being primary; `Packages[].Sha512` still read from
   the cache. PR 7 gains Paket files and a tool step; PR 8 may use `paket pack` or keep
   `Pack.nupkg`; PR 9 cannot use it (AR stays NuGet), so two package paths coexist. Gains: fast
   resolution, repo-wide unification, vendoring. Cost: rework of merged code, a new dependency with
   a bus factor of one and a breaking major in flight, and the second-source-of-truth problem.
2. **Paket only as a local cache/feed for bootstrapping.** The bootstrap is already decided
   (intermediate release, then `#r nuget`), and fsi's `#r "nuget:"` does not go through Paket, so
   Paket would sit beside `build.fsx`/`build.fsc.fsx` only to keep a `cache` folder. No PR changes;
   a folder in `nuget.config` or `#i "nuget:"` gives the same without a tool. Near dead end; the
   residual value is the `cache` vendoring idea, which could be a Xake feature (copy the nupkgs the
   locks name into a folder) without Paket.
3. **No Paket; NuGet `packages.lock.json` for the package graph.** Smallest change: the generated
   restore project of `Described.restore` gets `RestorePackagesWithLockFile` and a committed lock
   path under `locks/` (or `--locked-mode` when present). It would hand "kept versions" to NuGet,
   but `--force-evaluate` refreshes all floating versions, not by id, so `LOCK_REFRESH=<ids>` still
   needs our logic; it duplicates `Entry.Packages` (same hash) in a second committed file per
   project x TFM; and CI never restores at (c), so locked mode on CI buys nothing for the self-build.
   Its real use is the later msbuild-import fork (customer repos already on NuGet), where it is the
   customer's own file and our import can read it. PR plan unchanged now.

## 5. Open questions

- Is repo-wide version unification (Paket groups, or CPM transitive pinning) wanted for Xake's own
  projects, or is per-project resolution (current) the intent? R1.3 left it to msbuild.
- Should a vendored package folder (Paket's `cache`, rung 6) become a goal? If so, as a Xake
  function over the locks, or by adopting a tool?
- For the later msbuild-import fork: read the customer's `packages.lock.json` (when present) as an
  extra cross-check of `Entry.Packages`?
- `paket pack` vs `Pack.nupkg`: the acceptance compares entry content only; is a non-deterministic
  zip envelope acceptable at all, given the SBOM/compliance positioning?
- Not verified here: Paket.Core's `getCompileReferences` against our `Nuget.compileItems` on the
  same packages; `paket.local` with SDK-style projects in 11.x (docs say no); Paket's handling of
  FSharp.Core implicit references in SDK projects.
