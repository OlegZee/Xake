# Brief: Xake as a hermetic, dependency-controlled .NET build

Status: discussion draft, 2026-09-19. Nothing here is committed to. Facts about the code are
from branch `feature/hermetic-build`; everything else is a hypothesis to argue about.

## 1. The idea in one paragraph

A .NET build today is msbuild plus NuGet: restore talks to the network, the package cache is a
mutable global directory, `obj/` carries hidden incremental state, and the SBOM is produced
*afterwards* by a separate tool reading `project.assets.json` — a statement about what restore
resolved, not about what the compiler was handed. Xake's engine already knows, per compiler
invocation, exactly which files went in. That makes it the one place where "what did we ship
and what is it made of" can be answered from evidence rather than reconstruction. The product
idea: **Xake as the hermetic build for .NET, where the dependency graph is a first-class,
reviewable, attestable artifact** — for reproducibility, for SBOM, for compliance.

## 2. Why now

- Regulation is pulling: EU Cyber Resilience Act (SBOM and vulnerability handling obligations
  phase in 2026–2027), NIS2, US EO 14028 / NTIA "minimum elements", SLSA provenance levels
  becoming a procurement checkbox. Buyers of software increasingly ask for an SBOM *and* for
  evidence it matches the binary.
- .NET's own answer is thin: `RestorePackagesWithLockFile` locks versions, not compile inputs;
  `sbom-tool` and `CycloneDX dotnet` are post-hoc; `--deterministic` gives byte-identical
  output only if the inputs are the same, and nothing in the stock toolchain proves they were.
- The hermetic-build branch shows the engine is already most of the way there (see §4).

## 3. Who would want this

- Teams shipping .NET to regulated buyers (fintech, medtech, public sector, defence
  contractors) who must produce SBOMs and answer "is this binary built from these sources".
- F# shops (Xake's natural home) — small, but they already accept an F# build script.
- Platform/DevEx teams that want dependency changes to show up as a **reviewable diff in a PR**
  rather than as a silent restore.
- Not a target: a C# team happy with `dotnet build` and no compliance pressure. They have no
  reason to adopt an F#-scripted build tool.

## 4. What exists today (evidence, not promise)

- **Compilation without msbuild.** `build.fsc.fsx` compiles both assemblies with the `fsc`
  task; msbuild is asked once per project what to compile/reference/define and the answer is a
  kept file (`projects/<fwk>/<lib>.json`), tokenized so it is byte-identical on every machine
  and tracked in git. It behaves as a lockfile *for the compilation*: a diff there is a real
  change of inputs. Second build 38 ms, msbuild not started.
- **Every input is a tracked dependency.** The fsc task records each source and each referenced
  assembly as a `FileDep`; the engine's whole job is deciding what changed. One target executes
  once per run (fixed on this branch).
- **Deterministic output** (`--deterministic+`, portable PDB).
- **The full dependency map is already computed and then discarded.** msbuild's dump carries
  per reference the package id/version/source-type/project-of-origin; `project.assets.json`
  carries the whole package graph with sha512, direct vs transitive, who pulled whom; the
  cache holds feed URL, license expression, repository URL and commit per package. Joining
  them is a parsing job, not research (the implementation sketch from earlier today is in git
  history of this file's first version; it is deliberately not in this brief).

## 5. What is *not* hermetic yet — the honest list

| Gap | Why it matters | How hard |
|---|---|---|
| **Restore.** `evaluate` passes `-restore`; nothing owns the package files. Wipe the cache and the build dies with "neither rule nor file found". | Network at build time; cache is mutable shared state. | Small: the evaluation rule can depend on the package files it resolved, so a missing package re-runs restore. Real hermeticity (no network) needs an offline/vendored package source. |
| **msbuild is still the evaluator.** Conditions, imports, implicit defines come from the SDK's targets. | Output depends on SDK version; the "lockfile" locks the *result* of evaluation, not the evaluator. | Accept and record: pin SDK via `global.json`, write SDK version + hash of the targets into the kept file. Replacing msbuild's evaluation is not a goal. |
| **Package content is trusted by path.** The engine compares timestamps, not hashes. | A swapped dll with an old timestamp is invisible. | Medium: verify sha512 from the kept file against the cache at build time (a check, not a new dependency kind); a content-hash `Dependency` would need a DB version bump. |
| **`dotnet test` / `dotnet pack` shell out to the SDK.** | The test project and the nupkg (its `net462` asset) are msbuild's. | Test: could compile with fsc like the rest. Pack: nuspec+zip is doable; the net462 leg needs an FSharp.Core with a net4x assembly the pinned package lacks. |
| **The compiler itself.** `fsc`/`csc` come from whatever SDK is on PATH. | Different compiler, different bytes, even with `--deterministic`. | Record compiler version + hash in the attestation; optionally pin the compiler as a package. |
| **No provenance artifact.** The database knows what was built from what; nothing exports it. | The SBOM/attestation is the deliverable buyers ask for. | Small once the map is kept: CycloneDX/SPDX from the kept file + output hash; in-toto/SLSA statement later. |

## 6. Product concept — capability ladder

Each rung is useful alone and is a candidate MVP boundary.

1. **Compile lock.** The kept evaluation, tracked in git, reviewed in PRs. Exists.
2. **Dependency map.** References attributed to packages/projects; package graph with hashes,
   licenses, sources, direct/transitive, kept in the same file. Restore owned by the build.
3. **SBOM emission.** CycloneDX (and/or SPDX) per assembly, derived from (2) plus the built
   binary's hash — "what the compiler saw", not "what restore resolved". Ships next to the dll
   and into the nupkg.
4. **Policy.** Fail the build on: license outside an allowlist, package outside a pinned set,
   hash mismatch against the lock, floating versions, sources other than the approved feed.
   This is where "compliance" stops being a report and becomes a gate.
5. **Provenance / attestation.** A signed statement (in-toto / SLSA style) tying sources at
   commit X + toolchain Y + inputs Z to output hash W, produced by the same engine that did the
   build. Reproduce-and-compare as a CI job.
6. **Offline / vendored mode.** Packages from a directory in the repo or an internal feed
   only; the build refuses the network. Full hermeticity.

## 7. Positioning against the alternatives

| | Hermetic | Dependency lock of compile inputs | SBOM from evidence | F#/.NET native | Weight |
|---|---|---|---|---|---|
| `dotnet build` + lock file + sbom-tool | no | versions only | post-hoc | yes | light |
| FAKE / Cake / Nuke | no (orchestrate msbuild) | no | no | yes | light |
| Paket | no | versions + transitive, for restore | no | yes | light |
| Bazel + rules_dotnet | yes | yes | via ecosystem | weak F#, alien to .NET devs | heavy |
| Nix | yes | yes (derivations) | possible | awkward | heavy |
| **Xake (proposed)** | mostly (§5) | yes, reviewable in git | yes, from the engine | yes | light |

The gap Xake would occupy: **Bazel-grade answers at FAKE-grade weight, for .NET.** The pitch
is not "another build tool"; it is "the build that can testify".

## 8. The big product question: script-first or drop-in?

- **Script-first (today).** Users write `xakeScript { ... }` in F#. Maximum control; small
  audience (F# shops); C# teams will not come.
- **Drop-in tool.** `dotnet xake --hermetic` (or `xake build MySolution.sln`) that reads the
  existing `.sln`/`.csproj`, produces the compile lock, drives `csc`/`fsc` itself, emits SBOM
  and attestation — no build script to write. The engine and `Fsproj.evaluate` are the same;
  the script is generated. Much larger audience; the compliance story sells itself to people
  who never want to see F#.
- A middle path: the drop-in tool for the 90% case, the script as the escape hatch. Worth
  deciding before investing in rung 4+ — policy and attestation are what a compliance buyer
  pays for, and they will not write a build script to get them.

## 8a. First customer: ActiveReports for .NET

Named by the user on 2026-09-19. What follows is what that choice implies; the facts about
the product's build are assumptions to verify with the team, marked (?).

**Profile.** A commercial reporting component from MESCIUS, sold as NuGet packages and
installers to thousands of enterprise customers, many of them regulated. Large, C#-first
codebase (?), many shipped assemblies across several TFMs (net462, netstandard2.0, net6/8) (?),
strong-name and Authenticode signing, possibly obfuscation (?), bundled third-party code
(PDF, fonts, compression, Office formats) (?), long-lived release branches with hotfixes.

**Why it is a strong first customer.**
- It is a *manufacturer* under the Cyber Resilience Act and a *supplier* to every one of its
  customers' SBOMs. One SBOM produced here propagates into thousands of downstream SBOMs.
  Customers already ask for it; the demand does not have to be created.
- "Attributing assemblies" is their actual daily question: which shipped dll contains which
  internal project and which third-party component under which license — for the SBOM, for
  THIRD-PARTY-NOTICES, for legal review of a bundled library. Today that is answered by hand
  or by tribal knowledge (?).
- Hotfix releases on old branches need "rebuild tag X, prove the same bytes except the fix".
  Reproduce-and-compare is a direct fit.
- A component vendor's build is the hard case (many projects, many TFMs, signing, packaging).
  If the approach survives it, it survives anything smaller.

**What it decides.**
- **§8 is settled: drop-in, not script-first.** Nobody rewrites a build of that size in an
  F# DSL. The tool must read the existing `.sln`/`.csproj` and drive `csc` itself. The engine,
  `Fsproj.evaluate` (rename: it is not F#-specific) and the `csc` task are the reusable parts;
  the script is generated or implicit.
- **Compile lock and SBOM must scale to hundreds of projects.** One msbuild evaluation per
  project per TFM, cached and parallel, is the cost model; `projects/<fwk>/<lib>.json` × 300
  files in git is a lot of diff noise — a single lock per solution or per TFM may be the
  better unit. To measure on the real repo before deciding.
- **Signing, obfuscation and packaging are inside the hermetic boundary**, or the attestation
  only covers unsigned intermediates nobody ships. Authenticode and strong-name signing as
  Xake tasks, obfuscator as a task, nupkg/installer assembly as tasks — each with tracked
  inputs. This is rung 5's real scope for them.
- **Multi-TFM including net462 is mandatory.** The known limitation (fsc on macOS cannot target
  net4x) is an F# problem; for csc the reference assemblies come from a package and it works.
  Windows build agents are a given for them anyway (?).
- **Third-party attribution beyond NuGet.** Bundled sources or checked-in binaries (?) do not
  appear in `project.assets.json`; the map needs a way to declare them (a manifest next to the
  binary: name, version, license, origin) so the SBOM is complete rather than "NuGet only".
- **Policy gates they would use first:** license allowlist for shipped assemblies; no
  package from a feed other than the internal mirror; no floating versions; hash match with the
  lock. These are their release checklist today, done by a person (?).

**The smallest thing worth showing them.** Point the tool at one shipped assembly's project
(one TFM), produce: the compile lock, the CycloneDX SBOM of that assembly with license and
origin per component, and a reproduce-and-compare run on the tag of the last release. If
the SBOM lists something their legal team did not know was in the dll, the product sells
itself in the meeting. If it lists exactly what they already knew, the pitch becomes "now it
is automatic and provable", which is weaker but still real.

**What to find out first (?)-list.** Build system today (plain solutions, custom msbuild
targets, FAKE/Cake, TeamCity/Azure DevOps scripts). Number of projects and TFMs. What is
bundled from outside NuGet. Whether they are already producing an SBOM and how. Who asks for
it and what format (CycloneDX vs SPDX; customers in US government lean SPDX). Whether builds
happen on Windows only. Whether reproducibility of the currently shipped binaries holds at all
(a quick `dotnet build` twice and compare is a 10-minute experiment).

## 8b. ActiveReports facts and the resolution model (2026-09-21)

**Facts from the user.** Built with Cake today. 15 projects, 3 brands, 2 TFMs. Interest is in
*reference resolution*: `dotnet restore` should stay the source of truth, followed by a lock and
reproducibility. And: import the script rather than emulate `dotnet build`.

**Scale.** 15 × 2 TFM × 3 brands = at most 90 evaluations, ~15 KB each if kept per project;
1.3 MB of json in git, changing only when dependencies change. Small enough that the unit of
the lock can be chosen for readability, not size. Brand is presumably an msbuild property
(`-p:Brand=…`) (?) — it becomes a third dimension of the evaluation key, like TFM.

**Resolution model: restore decides, the lock remembers, the build verifies.**

1. **Package level — NuGet's own lock, not ours.** `RestorePackagesWithLockFile=true` gives
   `packages.lock.json` per project: direct and transitive versions with content hashes, per
   TFM. `RestoreLockedMode=true` on CI fails the restore if the lock would change. Restore
   also verifies the downloaded package against the hash. This exists, is free, and is the
   "restore as source of truth, then lock" the user asked for. Xake does not re-implement
   resolution; it *requires* the lock and treats it as an input.
2. **Assembly level — the compile lock (Xake's).** After restore, one msbuild evaluation per
   (project, TFM, brand) answers what the compiler is handed: sources, references attributed to
   package/project, defines, properties. Kept, tokenized, in git. This is the level
   `packages.lock.json` does not reach: which *assembly* from which package for which TFM,
   which reference-assembly set, which generated files.
3. **Evaluation inputs are tracked, not just the project file.** Ask msbuild for
   `MSBuildAllProjects` (or diff `-preprocess` output) and record every imported file —
   `Directory.Build.props/targets`, `*.nuget.g.props`, `packages.lock.json`, the SDK's own
   targets — as dependencies of the evaluation rule. Then a `Directory.Build.props` edit or an
   SDK upgrade re-evaluates, and nothing else does. This closes the "msbuild is the evaluator"
   gap from §5 to the extent it can be closed: the evaluator's inputs are part of the lock.
4. **Build = verify + compile.** With both locks present: check cache package hashes against
   `packages.lock.json` (`.nupkg.metadata` `contentHash`), check the compile lock is not stale
   against its recorded inputs, compile with `csc` from the lock. No msbuild, no network. If the
   cache is missing a package, run restore in locked mode (owned step), not a resolution.
5. **Reproducibility = locks + hashed cache + recorded toolchain + `--deterministic`.** Record
   SDK version, compiler version and hash in the compile lock. Proof is a reproduce-and-compare
   CI job, not a claim.

**What is tracked how — sources by time, libraries by hash.** The engine's `FileDep` is a
timestamp; that is the right identity for *sources*, whose real identity is the git commit.
It is the wrong identity for *libraries*: a reference assembly swapped under an old timestamp
is invisible, and reproducibility is a claim about bytes, not dates. So the lock and the
build treat the two differently:

| Input | In the lock | Tracked by | Verified at build |
|---|---|---|---|
| Source files, generated `AssemblyInfo`, `.resx` | paths only | engine, `FileDep` (timestamp) | no — the commit is the identity; the attestation records commit + tree |
| Package assemblies, analyzers, generators, reference assemblies | path **+ package id/version + SHA-256 of the file**; package SHA-512 from `packages.lock.json` | engine `FileDep` for staleness, **and** the recipe re-hashes each reference before compiling | yes: hash ≠ lock → fail (policy `HashMatch`); file missing → restore in locked mode, then re-check |
| The compiler (SDK or Toolset package) | version + SHA-256 of `csc.dll` | recipe | yes |
| msbuild imports (`Directory.Build.props`, SDK targets) | paths + SHA-256 | engine `FileDep` on each | at import: a hash change re-imports; recorded so the lock says which evaluator produced it |
| Babel rules, seed, maps; signing certificate | hashes / thumbprint / token | recipe | yes |

Cost: ~100 reference dlls per project, a few MB, SHA-256 at memory speed — milliseconds, not
worth an engine cache. A content-hash `Dependency` kind in the engine stays optional: it would
let a fresh restore on a new agent *not* count as a change (timestamps new, bytes same), a
nicety; the reproducibility guarantee lives in the recipe's check, not in the engine.

**"Import the script, do not emulate `dotnet build`."** Read as: the `.csproj` and everything
it imports *is* the build script; Xake must not re-implement its semantics (the branch already
does this: msbuild evaluates, Xake executes). Going further, `msbuild -preprocess` produces the
whole imported project as one file — a literal import of the script, usable as the lock of the
evaluator itself. Whether the user also meant importing the *Cake* script is an open question
(§10.6); the migration story below does not need it.

**Migration path that fits Cake — beside, not instead (decision 2026-09-21).** The Cake
pipeline keeps building and shipping with `dotnet build`. Xake runs *next to it* as the
auditor: imports the locks, rebuilds every assembly from them with its own `csc` invocation,
and compares byte for byte with what msbuild produced in the same run — then emits the SBOM
and the attestation for the binaries that actually ship. Zero risk to their release; the
`cmp` line of the demo *is* the product. If and when the audited build has been identical
for long enough, flipping which output ships is a one-line change in Cake, and signing and
packaging can follow one task at a time — or never.

## 8c. Import from the solution and the csproj files (confirmed 2026-09-21)

The user's "import the script" means: the `.sln` and the `.csproj` files are the build
definition, and the tool imports them. Two layers.

**Solution layer.** Parse the `.sln` (a small line format; `.slnx` is XML; `.slnf` filters):
the project set, the *solution configurations* — three brands are most likely three solution
configurations or a property they map to (?) — the per-configuration build/skip flags, and
explicit `ProjectDependencies`. The `ProjectReference` graph comes from the project layer and
gives the build order; Xake's engine turns it into `need`s and parallelism for free. No
dependency on `Microsoft.Build` assemblies: a hand parser, like the json one.

**Project layer — take the compiler's command line from msbuild, do not reconstruct it.**
Verified in SDK 10.0.401: `Microsoft.CSharp.Core.targets` (and `Microsoft.FSharp.Targets`)
run the compiler task with `ProvideCommandLineArgs="$(ProvideCommandLineArgs)"` and
`SkipCompilerExecution="$(SkipCompilerExecution)"`, and export the result as the
`CscCommandLineArgs` / `FscCommandLineArgs` item list. This is the *design-time build* Visual
Studio itself performs to learn the compiler switches (comment at line ~190 of the targets).
So the import is:

```
dotnet msbuild Proj.csproj -restore -p:Configuration=Release -p:TargetFramework=net8.0 -p:Brand=X
    -p:ProvideCommandLineArgs=true -p:SkipCompilerExecution=true -p:BuildProjectReferences=false
    -t:CoreCompile   -getItem:CscCommandLineArgs -getItem:ReferencePath;...  -getProperty:...
```

and the kept lock is the exact `csc` invocation `dotnet build` would have made — analyzers and
source generators, `/keyfile`, `/nullable`, `/langversion`, `/warnaserror`, embedded resources,
generated `AssemblyInfo`/TFM attributes, `/deterministic`, `/pathmap` — everything, with paths
tokenized. Nothing is emulated; the branch's current approach (pick `Compile` and
`ReferencePath` items and rebuild the switches) becomes a special case and can go.
To verify on a real project: that `GenerateAssemblyInfo` and the TFM-attribute target fire
under `-t:CoreCompile` (they hook `BeforeTargets="CoreCompile"`), and whether the `-getItem`
output carries the args in order.

**What `dotnet build` does besides `csc`, and who owns it.** `CoreBuildDependsOn` in
`Microsoft.Common.CurrentVersion.targets:941-959` is the full list; each entry is either owned
by a Xake task, recorded as an evaluation-time step, or left to msbuild — explicitly:

| Step | Owner in the hermetic build |
|---|---|
| `PrepareForBuild`, `ResolveReferences`, `ResolveKeySource` | evaluation time (msbuild), result in the lock |
| `PrepareResources` (resgen: `.resx` → `.resources`) | Xake `resgen` task (exists), its outputs are csc inputs |
| `Compile` | Xake `csc` task with the imported command line |
| `CreateSatelliteAssemblies` (localized resource dlls) | Xake task to write (AL or csc-with-resources); a component vendor ships these |
| `GenerateSerializationAssemblies`, `GenerateManifests`, `UnmanagedRegistration` | out of scope unless the projects use them (?) |
| `PreBuildEvent` / `PostBuildEvent`, custom targets | surfaced at import and **re-implemented as separate Xake recipes with declared inputs and outputs** — never executed as opaque shell inside the hermetic build (decision 2026-09-21; none are known in ActiveReports, E2 confirms) |
| `PrepareForRun` (copy to `bin/`) | Xake's own layout rule |
| `IncrementalClean` | not needed; the engine tracks outputs |

**Evaluation inputs.** `MSBuildAllProjects` (the SDK itself uses it as `Inputs` of its
incremental targets, lines 2571/2873/3463) lists every imported file; recorded as dependencies
of the import rule so a `Directory.Build.props` or SDK change re-imports and nothing else does.

**Result of an import.** A lock directory with **one file per (TFM, brand)** holding a
section per project — six files for ActiveReports (decision 2026-09-21: smaller files, keyed
by what actually varies; a per-project split would be 90 files of mostly identical reference
lists) and a *generated, editable* Xake script
that wires the locks, the resgen/csc/satellite steps and the layout. The script is the escape
hatch; day one nobody edits it. Cake's `Build` task calls the tool.

## 8d. Brands live in a separate `Directory.*.props` (2026-09-21)

The brand configuration is a props file imported by the projects, not a solution
configuration. Consequences:

- **It is an evaluation input, and the `MSBuildAllProjects` tracking (§8c) catches it** — the
  import rule depends on the file, a brand edit re-imports, nothing else does. The lock
  records the file's hash, so "which props was in effect" is part of the evidence.
- **Confirmed: property-switched** — one props conditioned on `$(Brand)`, passed as
  `-p:Brand=X`. The lock key is (project, configuration, TFM, brand); the file-switched case
  below is kept only as the pattern to avoid.
- **How the brand is *selected* decides the lock key.** Two cases:
  - *Property-switched*: one props with conditions on `$(Brand)`, Cake passes `-p:Brand=X`.
    The import runs per brand with the same switch, three locks coexist side by side, keyed
    (project, configuration, TFM, brand). Clean.
  - *File-switched*: Cake generates or copies a different props per brand before building.
    Then the input is mutable and outside git in its effective form; the import must run
    per variant, key the lock by brand explicitly anyway, and store the effective props
    content (or hash) inside the lock — otherwise reproducibility of "brand X, tag Y" has a
    hole exactly where the brand is. Recommendation if this is the case: convert to
    property-switched as part of the import; it is a mechanical change to the props.
- **The lock diff between brands *is* the brand delta**, reviewable: whatever the props sets —
  `AssemblyName`/`RootNamespace` prefix, product and company attributes, `BRAND_X` defines,
  version, signing key, icon and resources, package id (?) — lands in the csc command line
  and shows up as the only lines that differ between `…/X/Proj.json` and `…/Y/Proj.json`.
  That is a free audit of "what does brand X change" that nobody has today.
- **Signing key per brand** (?) makes `ResolveKeySource` and the key file per-brand inputs;
  keys are not in git, so the lock records the key's public token, not the file.

## 8e. SBOM and formats (2026-09-21)

**The format question is already answered by the company.** MESCIUS publishes SBOMs for
Document Solutions (DsExcel .NET at least) in **CycloneDX 1.7**, per release, *on request via
Support*, covering third-party and transitive dependencies, MESCIUS supporting libraries and
platform .NET packages. No self-service download, no mention of tooling, VEX or signing.
Whatever ActiveReports does, it will match that: CycloneDX JSON. SPDX only if a specific
customer (US federal) demands it — converters exist, do not author two formats.

**The requirement floor** (CRA Annex VII technical documentation; BSI TR-03183-2 as the de
facto rulebook; enforcement December 2027): per component *name, version, supplier, purl or
CPE, hash, license, dependency relationships*; transitive dependencies included; CycloneDX
1.6+ or SPDX 3.0.1+; machine-readable. Every field is in the data the build already has
(§4): name/version/purl and dependency graph from `project.assets.json`, sha512 from the same,
license/authors/repository from the nuspec, supplier from nuspec `authors` (with a curated
override table, because `authors` is free text).

**What the current "on request" SBOM most likely is (?)**: a post-hoc run of `CycloneDX
dotnet` or `sbom-tool` over the solution, done by a person per release. Its gaps are the
product's opening:

| Gap in a post-hoc SBOM | What the build-produced one adds |
|---|---|
| Per solution, not per shipped artifact | One SBOM per shipped **assembly** and per shipped **nupkg**, brand-specific (3 brands → 3 sets, names differ) |
| Package-level only: says `System.Memory 4.5.4`, not which dll | Nested `file` components per package with the hash of each dll actually linked — the "from evidence" layer |
| Unverifiable against the binary | `metadata.component.hashes` = hash of the shipped dll; a customer can check the SBOM matches what they hold |
| No toolchain | CycloneDX `formulation` (1.5+): SDK version, csc version+hash, analyzers/source generators as build-time components (`scope: excluded`), the compile lock reference — the "build that testifies" |
| NuGet-only | Non-NuGet components (bundled sources, fonts, native libs, checked-in binaries) declared **as msbuild items in the csproj**, e.g. `<SbomComponent Include="zlib" Version="1.3.1" License="Zlib" Supplier="..."/>`, read at import with `-getItem` — the declaration lives with the code, is reviewed with it, and travels through the same import |
| Manual, per request | Produced by every release build, deterministic (sorted, timestamp from the commit / `SOURCE_DATE_EPOCH`, `serialNumber` derived from content), so two builds of a tag give byte-identical SBOMs too |
| Internal libraries as opaque entries | "MESCIUS supporting libraries" are project references or sibling products: components with supplier MESCIUS and `bom-ref`s that compose across products (ActiveReports' SBOM can *reference* DsPdf's, if they depend on it (?)) |

**Design choices for the emitter.**
- Primary components = NuGet packages with `purl: pkg:nuget/<Id>@<Version>` — that is what
  every downstream scanner (Dependency-Track, Grype, Snyk) matches on. Assembly-level
  evidence goes *under* them, never instead of them.
- `dependencies[]` from the restore graph; root = the shipped component; direct vs transitive
  visible from the graph shape, plus `scope`.
- Compile-time-only inputs (analyzers, generators, reference assemblies) are not shipped code:
  `scope: excluded` in `components` and full detail in `formulation`, so a scanner does not
  raise false positives on `Microsoft.CodeAnalysis`.
- Per-nupkg SBOM = union of its assemblies' SBOMs with the nupkg hash as root; the assembly
  SBOMs stay available for customers who take dlls, not packages (installer users).
- Signing the SBOM (JSF inside CycloneDX, or a detached cosign attestation) is rung 5, not
  now; but the document must be deterministic *now* or signing later is pointless.
- Where it ships is MESCIUS's call, not the tool's: inside the nupkg (`_manifest/` is the
  Microsoft `sbom-tool` convention; CycloneDX has none), on the docs site, or on request as
  today. The tool emits `out/<brand>/<fwk>/<asm>.cdx.json` and `out/<brand>/<pkg>.cdx.json`.

**Policy gates that fall out of the same data** (rung 4, their release checklist today (?)):
license allowlist for shipped components; supplier/feed allowlist (only the internal mirror);
no component without a hash; no floating version; every non-NuGet binary declared.

**VEX and vulnerability handling** are downstream of the SBOM and out of the build tool's
scope. The right integration is: the build publishes the SBOM to Dependency-Track (or
whatever MESCIUS security uses); purls make the matching work. Worth a sentence in the pitch,
not a feature.

## 8f. The boundary: signing, obfuscation, packaging (2026-09-21)

**Facts.** ActiveReports ships as a graph of NuGet packages on nuget.org (`MESCIUS.ActiveReports`
and its `MESCIUS.ActiveReports.*` dependencies, all pinned within a major) plus a Windows
installer. Assemblies are strong-named (`PublicKeyToken=cc4967777c49a3ff`). Obfuscation: not
established (?). In .NET today Authenticode and NuGet signing on CI go through the
`dotnet sign` CLI against Azure Trusted Signing or Key Vault: the tool sends a *digest*, the
key never leaves the HSM, and Authenticode signing is Windows-only — "build on one agent, sign
on another" is the recommended shape. Authenticode carries an RFC 3161 timestamp
countersignature, so a signed file is never byte-reproducible; the reproducible-builds practice
(Tor Browser) is *strip the signature and compare*.

**Three rings, three kinds of promise.**

| Ring | Steps | Promise | Owner |
|---|---|---|---|
| 1. Compile | csc with the imported command line, `/keyfile` strong-naming inside csc (RSA over the hash: deterministic), resgen, satellite assemblies | **byte-identical** on rebuild of the tag | Xake, hermetic |
| 2. Transform | obfuscation, if any (?); IL merging, if any | **deterministic if the tool is**; otherwise "produced from attested input by tool T (version+hash) with config C (hash)" | Xake task, tracked inputs; the tool decides how strong the promise is |
| 3. Sign & package | Authenticode, NuGet author signature, nupkg assembly, installer | **identical modulo signatures**; the signature is itself the evidence, binding the signer's identity to the attested hash | Xake orchestrates; the signer is an external oracle over a hash |

**The claim the attestation makes**, end to end: *the signed dll a customer holds, with its
signature removed, equals the reproducible build of tag X under lock L; it was signed by
certificate C at time T.* That is exactly what the hotfix scenario needs ("rebuild 18.2.3,
prove only the fix changed") and what a compliance buyer can verify without trusting the
vendor's CI.

**Hashes to record, and why two.** The SBOM component hash is the raw SHA-256 of the file
that ships (signed). The attestation additionally records the **Authenticode PE hash** —
computed excluding the checksum and certificate table — which is *the same before and after
signing*. So verification needs no stripping: `signtool verify` / any PE parser yields the
hash, and it must equal the one in the attestation and the one produced by the reproduce job.
For nupkg, the same idea with the package content minus `.signature.p7s`.

**Ordering constraint.** Obfuscation invalidates the strong name; the order is compile →
obfuscate → re-strong-name (`sn -R`, deterministic) → Authenticode → pack → NuGet-sign. Ring 2
therefore sits *inside* the reproducibility claim only if the obfuscator is deterministic
(seeded renaming, fixed output order — most commercial tools have a switch; to verify for
theirs (?)). If it is not, the attestation's byte-identity stops at ring 1 and ring 2 is
attested as a tracked transform. Say which one honestly; do not average.

**Packaging.** `dotnet pack` output is a zip whose entry timestamps and order are not
guaranteed stable (?); owning the pack step (nuspec in, deterministic zip out: sorted entries,
fixed timestamps from the commit) is small and removes a whole class of "why do two packs
differ". The per-nupkg SBOM (§8e) is produced by the same rule. NuGet author signing then
adds `.signature.p7s`, again as an oracle over the package hash.

**Windows-only signing and the two-agent shape** map onto something Xake already has: the
*delegated execution* model (`docs/delegated.md`, PR #15) — a rule whose body runs on another
executor. The sign rule is delegated to the Windows signer, takes the attested hashes as input,
returns signed files, and the next rule *verifies* Authenticode hash equality before anything
is published. Signing keys never touch the build agent; the lock records public key tokens and
certificate thumbprints, never key material.

**Policy gates this ring adds** (rung 4): signature present on every shipped file; signer
certificate in an allowlist; Authenticode hash equals the attested hash; timestamp
countersignature present; strong-name token equals the expected one per brand.

**Installer.** Out of the boundary for now. Its SBOM is the union of what it carries plus the
installer toolchain; making a WiX/MSI build deterministic is real work with a separate payoff.
Record the installer's inputs (the signed files' hashes) so the chain is unbroken, and stop
there.

**Decision proposed.** Rings 1–3 for assemblies and nupkgs are inside the boundary; ring 2's
strength depends on the obfuscator; the installer is outside but its inputs are attested. The
first thing to find out from the team: is there an obfuscator, and does it have a
deterministic mode.

## 8g. Obfuscator: Babel Obfuscator (confirmed 2026-09-21)

Ring 2 is Babel (babelfor.net). What the documentation establishes:

- **Deterministic mode exists**: `--seed <hex>` initialises Babel's random generator "to have
  a deterministic obfuscation". So ring 2 *can* be inside the byte-identity claim; the seed
  becomes part of the lock (it is not a secret — the strength of obfuscation does not rest on
  it), and the reproduce job passes the same one. To verify: obfuscate twice with the same seed
  and same input, compare bytes; and check whether Babel embeds any timestamp of its own.
- **Babel re-signs the strong name itself** (`.snk`/`.pfx`/key container options for the
  obfuscated assembly and its satellite dlls), so the separate `sn -R` step in §8f disappears;
  the order is compile → Babel (obfuscate + re-sign) → Authenticode → pack → NuGet-sign.
- **Cross-platform CLI** (Windows, macOS, Linux): ring 2 does not force a Windows agent; only
  Authenticode does.
- **Cross-assembly renaming uses XML map files** as input and output per assembly. They are
  inputs and outputs of the rule like any other: tracked, hashed, and the *output* map is
  itself a provenance artifact (symbol de-obfuscation for crash reports) that belongs next to
  the attestation, not in the SBOM.
- Exact flags (command-line reference): `--randomseed <hex>`, `--output <file>`,
  `--keyfile <snk|pfx>` / `--keyname <container>` / `--keypwd`, `--rules <xml>` (repeatable,
  ordered), `--mapin <xml>` (repeatable), `--mapout [xml]`. No documented option about
  embedded timestamps — experiment 4 in §8h settles whether there are any.
- Build-server integration is an **Ultimate-edition** feature — a licensing fact to confirm
  with the team, not a technical one.

Babel's version and its config (rules XML, seed, map files) are recorded in the compile lock's
`formulation`; a Babel upgrade is then a visible lock change, as it should be.

## 8h. Minimal demonstration (2026-09-21)

Two layers: day-zero experiments that need no Xake code and can kill or confirm hypotheses in
an afternoon on their repo, then a demo built on the branch. Ordered by how much a negative
answer changes the plan.

**Day zero, on their repository, no code written.**

| # | Experiment | Answers | If negative |
|---|---|---|---|
| 1 | `dotnet build` one shipped project twice (`Deterministic=true`, `PathMap` set), compare the dlls | Is their code + stock toolchain reproducible at all? | Find the source (generators, embedded timestamps, resource ordering) — this becomes the first deliverable and is valuable on its own |
| 2 | Design-time import on one csproj × 2 TFM × 3 brands: `-p:ProvideCommandLineArgs=true -p:SkipCompilerExecution=true -t:CoreCompile -getItem:CscCommandLineArgs` | Is the command line complete (AssemblyInfo, TFM attributes, resources, analyzers, keyfile)? What is the brand delta? | Add the missing targets to the import; if something only exists as a custom target, that is a pre/post-build-event style hole to surface |
| 3 | Enable `RestorePackagesWithLockFile`, restore, look at `packages.lock.json` size and stability across two restores | Is NuGet's lock usable as the package-level truth here? Floating versions present? | Pin versions first; the lock is the tool that finds them |
| 4 | Babel with `--seed` twice on the same input, compare | Is ring 2 inside byte-identity? | Ring 2 attested as a tracked transform; the claim stops at ring 1 (still valuable) |
| 5 | Take the shipped `MESCIUS.ActiveReports` nupkg from nuget.org, extract a dll, compute its Authenticode hash; build the same tag locally with `dotnet build`, compare | Can the shipped binary be reproduced *today*? | Almost certainly no — and the *reasons* are the product pitch, item by item |

Experiments 1, 2 and 5 are the ones to run first; 3 and 4 are cheap follow-ups.

**The demo, on the branch (order of building).**

1. **Identical bytes, no msbuild at compile time.** Import one shipped assembly's project
   (pick the one with the most third-party dependencies — an exporter, PDF or Excel (?)), one
   brand, `net8.0`. `xake build` runs the imported csc command line; the output is compared
   byte for byte with `dotnet build`'s. Same command line, deterministic compiler: they *must*
   match, and when they do the point is made in one line of `cmp`. Second run: 40 ms, nothing
   executed.
2. **The lock diff between brands.** Same project, three brands, three kept files; `git diff`
   between two of them shows the brand delta and nothing else. Nobody in the room has seen
   that before.
3. **The SBOM, compared with the one they hand out today.** CycloneDX from the build, with
   license, supplier, purl, hash, dependency graph; set next to the current on-request SBOM
   for the same release. Every discrepancy is either their bug or ours; both outcomes are
   interesting. Include the assembly-level evidence and `formulation` so the difference in
   kind is visible, not just in count.
4. **Reproduce-vs-shipped verdict.** Check out the last release tag, build through the lock,
   compare the Authenticode hash with the dll from nuget.org. Green: "we can prove 20.1.x came
   from its tag". Red: the list of reasons why not, which is the roadmap.
5. Only if 1–4 land: Babel with seed as a Xake rule (ring 2), and the pack rule producing a
   deterministic nupkg with its SBOM inside.

**What to bring into the room.** Four artifacts, one screen each: the `cmp` line, the brand
diff, the SBOM side by side, the reproduce verdict. Plus one honest slide: what stays outside
the boundary (installer, Authenticode timestamp, the SDK as evaluator) and what that means.

**Effort guess for the demo** (not the product): experiments — one afternoon with someone who
has the repo; items 1–2 — the branch already does this for F#, the C# path is the design-time
import plus the `csc` task, roughly a week; item 3 — the emitter, a few days; item 4 —
an afternoon once 1 works. Item 5 is a second sprint. Numbers to be replaced by real ones
after the experiments.

## 8i. Day-zero experiment scripts (2026-09-21)

To run on the ActiveReports checkout by someone with access. Placeholders in caps:
`PROJ` (one shipped project's csproj), `BRANDPROP`/`BRAND1..3` (the property and its values),
`TFM1`/`TFM2`, `TAG` (last release tag), `PKGVER` (its nuget.org version). Everything writes
under `./_exp/`; nothing touches the repo. bash on macOS/Linux; Windows notes inline.

**Preparation.**

```bash
mkdir -p _exp && cd _exp
dotnet --version                                  # record SDK; global.json?  cat ../global.json
git -C .. rev-parse HEAD > head.txt
```

**E1 — is one project reproducible with the stock toolchain?**

```bash
P=../PROJ
for i in 1 2; do
  rm -rf $(dirname $P)/obj $(dirname $P)/bin
  dotnet build $P -c Release -p:TargetFramework=TFM1 -p:BRANDPROP=BRAND1 \
    -p:Deterministic=true -p:ContinuousIntegrationBuild=true \
    -p:PathMap="$(cd .. && pwd)=/src" -o e1_$i --nologo -v:q
done
for f in e1_1/*.dll; do
  b=$(basename $f); cmp -s $f e1_2/$b && echo "SAME  $b" || echo "DIFF  $b"
done
# where they differ (needs `pip install diffoscope` or the docker image):
# diffoscope e1_1/X.dll e1_2/X.dll | head -80
# Usual culprits: a source generator emitting time/guids, AssemblyVersion with '*',
# resources written in hash order, `[assembly: AssemblyInformationalVersion]` with a build date.
```

Negative here does not block anything; it is the first work item.

**E2 — the design-time import: is the csc command line complete, what is the brand delta?**

```bash
P=../PROJ
for tfm in TFM1 TFM2; do for brand in BRAND1 BRAND2 BRAND3; do
  dotnet msbuild $P -restore -nologo -v:q \
    -p:Configuration=Release -p:TargetFramework=$tfm -p:BRANDPROP=$brand \
    -p:BuildProjectReferences=false -p:ProvideCommandLineArgs=true -p:SkipCompilerExecution=true \
    -p:IntermediateOutputPath=obj/xake/$tfm/$brand/ \
    -t:CoreCompile \
    -getItem:CscCommandLineArgs,ReferencePath,EmbeddedResource,Analyzer \
    -getProperty:AssemblyName,DefineConstants,ProjectAssetsFile,MSBuildAllProjects,SignAssembly,AssemblyOriginatorKeyFile,PublicSign \
    -getResultOutputFile:e2_${tfm}_${brand}.json
done; done

# what csc would be handed, one switch per line, absolute paths made relative
python3 - <<'EOF'
import json,glob,os,re
root=os.path.abspath('..')
for f in sorted(glob.glob('e2_*.json')):
    d=json.load(open(f)); args=[a['Identity'] for a in d['Items']['CscCommandLineArgs']]
    open(f.replace('.json','.args'),'w').write('\n'.join(a.replace(root,'$(Root)') for a in args)+'\n')
    print(f, len(args),'switches;', sum(1 for a in args if not a.startswith('/')),'source files')
EOF
diff e2_TFM1_BRAND1.args e2_TFM1_BRAND2.args      # the brand delta, and nothing else?
diff e2_TFM1_BRAND1.args e2_TFM2_BRAND1.args      # the TFM delta
grep -c 'AssemblyInfo.cs\|AssemblyAttributes.cs' e2_TFM1_BRAND1.args   # generated attribute files fired?
grep -o '/keyfile:[^ ]*\|/publicsign[^ ]*\|/analyzer:[^ ]*' e2_TFM1_BRAND1.args | sort | uniq -c | head
python3 -c "import json;d=json.load(open('e2_TFM1_BRAND1.json'));print(d['Properties']['MSBuildAllProjects'].replace(';','\n'))"   # the evaluator's inputs
```

Look for: every `.cs` you expect, the `.resources` inputs (if none, `PrepareResources` must be
added to `-t:`), `/keyfile` or `/publicsign`, analyzers from packages, `/deterministic+`,
`/pathmap`. Anything a custom target adds *after* CoreCompile will not be here — that is the
pre/post-build-event hole; `grep -n 'AfterTargets=\"\(Build\|CoreCompile\)\"\|PostBuildEvent\|Exec ' ../**/*.csproj ../**/*.props ../**/*.targets` finds them.

**E3 — NuGet's lock as the package-level truth.**

```bash
P=../PROJ
dotnet restore $P -p:RestorePackagesWithLockFile=true --force-evaluate
cp $(dirname $P)/packages.lock.json lock1.json
dotnet restore $P -p:RestorePackagesWithLockFile=true --force-evaluate
diff lock1.json $(dirname $P)/packages.lock.json && echo "lock stable"
grep -n '"requested": "\[\?[0-9.]*,\|\*' lock1.json | head      # floating / range versions
dotnet restore $P -p:RestorePackagesWithLockFile=true -p:RestoreLockedMode=true   # what CI would run
wc -l lock1.json; git -C .. status --short | head              # noise: how many lock files would land in git
```

**E4 — is Babel deterministic with a seed?**

```bash
IN=e1_1/ASSEMBLY.dll
for i in 1 2; do
  babel $IN --output e4_$i/ASSEMBLY.dll --randomseed 0123ABCD --rules ../BABEL_RULES.xml \
        --keyfile PATH_TO_SNK --mapout e4_$i/map.xml
done
cmp e4_1/ASSEMBLY.dll e4_2/ASSEMBLY.dll && echo "babel deterministic" || diffoscope e4_1/ASSEMBLY.dll e4_2/ASSEMBLY.dll | head -60
cmp e4_1/map.xml e4_2/map.xml
sn -v e4_1/ASSEMBLY.dll        # Windows / mono: strong name re-signed and valid?
```

The exact Babel invocation for their project lives in their Cake script — copy it from there
and add `--randomseed`. On Windows `babel.exe`; the flags are the same.

**E5 — can the shipped binary be reproduced today?**

```bash
# the shipped bits
curl -sL -o shipped.nupkg https://api.nuget.org/v3-flatcontainer/mescius.activereports/PKGVER/mescius.activereports.PKGVER.nupkg
mkdir shipped && unzip -q shipped.nupkg -d shipped && ls shipped/lib/

# authenticode hash, cross-platform (pip install signify)
python3 - <<'EOF'
import glob,hashlib
from signify.authenticode import SignedPEFile
for f in sorted(glob.glob('shipped/lib/TFM1/*.dll')):
    with open(f,'rb') as h:
        pe=SignedPEFile(h); print(pe.get_fingerprint(hashlib.sha256).hex(), f)
        for s in pe.signed_datas:
            print('   signer:', s.signer_info.issuer, 'ts:', getattr(s.signer_info.countersigner,'signing_time',None))
EOF
# Windows alternative: signtool verify /v /pa /all shipped\lib\TFM1\X.dll   -> "Hash of file (sha256)"

# the same tag, built locally the way Cake builds it (whatever their Build task runs), then:
git -C .. worktree add ../_tag TAG && (cd ../_tag && dotnet build SLN -c Release -p:BRANDPROP=BRAND1 -o ../_exp/e5)
python3 - <<'EOF'
import glob,hashlib
from signify.authenticode import SignedPEFile
for f in sorted(glob.glob('e5/*.dll')):
    with open(f,'rb') as h: print(SignedPEFile(h).get_fingerprint(hashlib.sha256).hex(), f)  # unsigned: same formula, no cert table
EOF
# equal hashes => the shipped dll IS the local build modulo signature. Unequal => diffoscope the
# unsigned local dll against `osslsigncode remove-signature -in shipped.dll -out stripped.dll`.
```

Expect red on E5 unless E1 was green *and* Babel ran with a fixed seed *and* the same SDK;
the diff is the list of reasons and goes straight into the pitch.

**What to send back.** `head.txt`, the `SAME/DIFF` lines from E1, the three `.args` files and
the two diffs from E2, `lock stable` or the diff from E3, the two `cmp` results from E4, and
both hash tables from E5. That is enough to replace every (?) in §8a–8g and to size the demo.

## 11. Delivery: types and recipes in Xake.Dotnet, the fsx is only wiring (2026-09-21)

Decision (user): **reuse goes through types and recipes in the `Xake.Dotnet` assembly, not
through scripts.** No `#load` library-in-disguise; the fsx is the consumer and the integration
test, exactly the relationship `build.fsc.fsx` has with `Fsproj.evaluate` on this branch. So
the sequence is not "script first, extract later" but **library-first in small pieces, each
proven by the script the same day**: a piece is prototyped inline in the fsx only until it
works once, then moves into the assembly with a test, and the fsx line becomes a call.

**Yes, the whole demo (§8h) can be driven by one fsx** — because everything it needs is either
in the engine already or becomes a `Xake.Dotnet` type/recipe below. The script ends up looking
like `build.fsc.fsx`: variables, a handful of rules, calls into the library.

**API surface to grow in `Xake.Dotnet`** (names to be argued about; shapes matter more):

| Module | Types | Recipes / functions | Notes |
|---|---|---|---|
| `Sln` | `Solution = { Projects; Configurations; Dependencies }` | `Sln.parse : string -> Solution` (pure) | `.sln` hand parser; `.slnx` later |
| `Project` (generalises `Fsproj`) | `ImportOptions = { Project; Configuration; Framework; Properties; Output }`; `Lock = { Compiler: Csc\|Fsc; Args: string list; Sources; References: Reference list; Packages: Package list; Imports: string list; Properties }` | `Project.import : ImportOptions -> Recipe<unit>` (design-time msbuild, writes the lock, `needFiles` on `Imports`, `packages.lock.json` and package references); `Lock.read`, `Lock.write` (tokenized, deterministic) | `Fsproj.evaluate` becomes the F# case of this or is retired; target/item lists are parameters |
| `csc {}` / `fsc {}` (existing tasks, extended) | `CscSettingsType` gains `Invocation: string list option` and `Toolset: CompilerSource` | **the compiler is only ever run through the `csc` recipe.** Two additions: a *verbatim* mode — `csc { invocation lock.Args }` emits exactly the imported command line (no composed `/out`, `/target`, `/r:`), derives the inputs from the args (sources, `/reference:`, `/analyzer:`, `/keyfile:`, `/resource:`, `/additionalfile:`) and `needFiles` them before running; and a *compiler source* — today `DotNetFwk` always takes `Roslyn/bincore` from the SDK (`sdkFwkInfo`, "the compilers always come from the SDK"), `cscpath` being the only override; add `Microsoft.Net.Compilers.Toolset` from the NuGet cache (`tasks/netcore/bincore/csc.dll` under `dotnet`, `tasks/net472/csc.exe` on Framework) as a first-class source, version pinned and hashed in the lock | closes the "compiler from PATH" gap in §5: the compiler becomes a package in the lock. If the csproj already references the toolset package, the import reads `CscToolPath`/`CscToolExe` and the lock names it; otherwise the SDK's compiler version+hash is recorded |
| `Nuget` | `Assets = { Packages; Graph; Direct }`; `Package` (id, version, sha512, source, license, supplier, repository) | `Nuget.readAssets`, `Nuget.readCache` (`.nupkg.metadata`, nuspec) | uses the internal `Json` module — no `System.Text.Json`, the assembly stays dependency-free on netstandard2.0 |
| `Sbom` | `Component`, `Bom` | `Sbom.cycloneDx : Bom -> string` (pure, deterministic order); `Sbom.forAssembly : Lock -> File -> Bom`; `Sbom.forPackage` | `SbomComponent` msbuild items arrive through `Lock` |
| `Verify` | — | `Verify.sha256`, `Verify.authenticodeHash` (PE minus checksum and cert table), `Verify.compare` | pure |
| `Policy` | `Rule = LicenseAllow of ... \| FeedAllow of ... \| NoFloating \| HashMatch \| Declared` | `Policy.check : Rule list -> Lock -> Violation list`; `Policy.enforce : ... -> Recipe<unit>` | rung 4 |
| `Babel`, `Pack`, `Sign` | settings records, CE builders like `csc {}` | recipes wrapping the CLIs with tracked inputs; `Sign` as a `delegated` rule | ring 2–3; `Pack` writes a deterministic zip |

Everything with a `Recipe` return records its own dependencies, so a script author gets
incremental behaviour without thinking about it — the same contract `Fsproj.evaluate` has now.

**Order of building, matched to the demo:** `Project.import` + `Lock` + `csc { invocation }`
(demo item 1 and 2) → `Nuget` + `Sbom` (item 3) → `Verify` (item 4) → `Babel`/`Pack`/`Sign`
(item 5). `Sln` can wait until more than one project is imported; the first demo names its
project explicitly, as `build.fsc.fsx` does.

**What this changes versus §8c.** The "generated Xake script" of the drop-in tool is then a
short fsx over these types, and the compiled tool is the same calls with a CLI in front — one
code path, three consumers (self-hosting build, the fsx, the tool).

**Constraints.** `dotnet fsi` recompiles the fsx per run — fine here, the heavy code is in
the assembly anyway. The fsx needs a released Xake with this branch, or `.bootstrap/` staging
as today; releasing first is cheaper. Tests live in `src/tests` from the first piece: the
design-time import gets a fixture like `reads the project msbuild evaluated`, pure modules get
unit tests, and the fsx run is the integration test.

## 12. What it takes to start (2026-09-21)

State of the repository: `v3.3.0` (PR #16 — `Xake.Dotnet` inside the `Xake` package, .NET 8
baseline) is on nuget.org as `3.3.0.20`. This branch (`feature/hermetic-build`, 6 commits:
`Fsproj.evaluate`, kept evaluations, the one-execution-per-run pool fix) is **not** released;
anything built on it runs from `.bootstrap/` until it is.

**Our side, in order.**

1. **Release the branch.** PR `feature/hermetic-build` → `dev`, then `dev` → `master`, tag
   `v3.4.0`. Afterwards `build.fsx`'s `#r "nuget: Xake, 3.0.1"` and `samples/gettingstarted.fsx`
   move to the new version, `build.fsc.fsx` drops its `.bootstrap/` lines and becomes the build
   of record (the plan in `docs/session.md`). Half a day; it unblocks every fsx after it.
2. **A C# fixture in this repo.** A small `samples/hermetic/` C# project with a package
   reference, an `.resx`, an analyzer package, `SignAssembly` with a test key and two brands
   via a props file — so the import, the lock and `csc { invocation }` are proven and tested
   here, without access to ActiveReports. The integration test compares `dotnet build`'s dll
   with Xake's byte for byte; that is demo item 1 in miniature.
3. **First library slice** (§11 order): `Project.import` (design-time msbuild for C#, lock per
   TFM+brand, hashes for libraries, `needFiles` on imports), `Lock` read/write, `csc {}`
   verbatim mode with hash verification and the Toolset compiler source. Tests in `src/tests`.
   Then `Nuget` + `Sbom`, then `Verify`.
4. **The fsx** over those types, run first against the fixture, then against ActiveReports.

**Their side, what to ask for now.**

- One person with a checkout and an afternoon to run §8i (E1, E2, E5 first) and send the
  outputs back; or read access to the repository so we run them.
- The Cake script (to see the exact `dotnet build`, Babel and signing invocations), the brand
  props file and the property name, the list of shipped assemblies per package.
- A copy of the SBOM they currently hand out for one release, for demo item 3.
- Babel edition and licence (Ultimate needed for build-server use), rules XML and whether a
  seed is set today.
- Which SDK version their CI pins (`global.json`) and whether it builds on Windows only.
- Not yet needed: signing keys, a Windows signing agent, installer sources.

**Not needed to start:** a product name, the CLI tool, the `.slnx` parser, the installer.

## 8j. Day-zero results on the real repositories (2026-09-22)

Scope set by the user: `ar-net-core-dataengine` and `ar-net-core-page` (siblings under
`~/Projects-work/ar/`), Cake scripts in `scripts/`, Babel without a licence, Xake referenced
via `#r` on `.bootstrap/` — no release.

**What the repositories are.**

| | dataengine | page |
|---|---|---|
| shipped projects | 3 (`DataEngine`, `ExpressionInfo`, `VBFunctionLib`) | 12 (`Rdl`, `Rendering`, `Scripting`, `Drawing.Gc/Gdi`, 7 exporters) |
| TFMs | `netstandard2.0;net472` | same; `NetCoreOnly=true` narrows to netstandard |
| brands | `MESCIUS` / `GCCN`, `-p:Brand`, `src/<Brand>.props` imported by `src/Directory.Build.props` | same, plus the brand switches **third-party package identity**: `GcPrefix` = `DS.Documents` vs `GcDocs` (both on nuget.org, 9.2.1 present) and `DataEnginePrefix` for the dataengine packages |
| packages | Central Package Management (`Directory.Packages.props`), nuget.org only for `src` | nuget.org + private feed `ar-core-dev` (dataengine packages, `Babel.*`), token `AA_AR_TOKEN`; **`LocalBuild=true` swaps the dataengine packages for `ProjectReference`s into the sibling repo** — the two-repo scope works through that switch |
| strong name | `.keys/*.snk` committed, `SignAssembly`, csc `/keyfile` | same |
| SBOM today | none | **CycloneDX already in the build**: `CycloneDX.MSBuild` 1.3.2 + `dotnet cyclonedx` 6.2.0, spec 1.6, per project per TFM, `-dpr` to respect the brand, `GenerateSbom=true` from Cake; `MergeCycloneDxSbom.ps1` merges `oss-components.cdx.json` (Rendering has one) — the "non-NuGet manifest" of §8e already exists in their format; net472 restored/built separately because CycloneDX broke on it |
| obfuscation | Babel 10.9.0.0 as a dotnet tool from the private feed; `--project ObfuscationConfig.babel` (re-signs with the snk, `FlattenNamespaces`, control-flow `goto`, no map file), `--rules`, `--flatns`, in place; **no seed**; only on non-feature branches | same |
| signing | `AzureSignTool` via `certificateSignCommand` (Windows) | not in this repo |
| custom targets | `SetNuspecProperties` (pack), inline `RoslynCodeTaskFactory` task | + `AddInternalsVisibleTo` (`BeforeTargets=BeforeCompile`), CycloneDX targets `AfterTargets=Build`; **no pre/post-build events** |
| SDK | `global.json` 8.0.x, latestFeature; 8.0.425 installed here | same |

**E1 — reproducibility with the stock toolchain: green.** `origin/develop` of dataengine,
two clean `dotnet build` runs (obj/bin removed between), all three `netstandard2.0` dlls
byte-identical. (The user's checkout is on `feature/summary-docs-iteration-2`, whose HEAD
does not compile — `Variant.DateToString` was lost in 0a58673; the experiments run on a
`git archive origin/develop` copy under the job's tmp dir, nothing touched in the checkout.)

**E2 — design-time import: complete, and the brand delta is exactly what it should be.**
`-p:ProvideCommandLineArgs=true -p:SkipCompilerExecution=true -t:PrepareResources;Compile`:

| | DataEngine | Rdl (page, `LocalBuild=true`) |
|---|---|---|
| csc args | 217 (144 switches, 73 sources) | 597 (169 switches, 428 sources) |
| references | 115: 113 from `netstandard.library/2.0.3`, 2 project refs | 123: netstandard + `system.text.json` 8.0.5 and its closure, 2 **cross-repo** project refs into dataengine |
| analyzers | 2 (NetAnalyzers) | 3 |
| brand diff | 4 lines: `/doc`, `/out`, two project-reference names | same shape plus the resgen output and generated files under the per-brand obj |

The command line carries everything: `/noconfig /nostdlib+ /keyfile /deterministic+ /debug:portable /optimize+ /warnaserror+ /langversion:12 /highentropyva+ /filealign:512`, `/embed` of the generated `AssemblyInfo.cs` and TFM attributes, `/analyzerconfig` (`.editorconfig` + generated), `/sourcelink`, `/resource:` for 15 embedded xsd/bmp and the resgen'd `.resources`. `CscToolPath` is empty: the SDK's compiler.

**Traps found, all of them design input for `Project.import`:**

1. **`MSBuildAllProjects` is useless on SDK 8** — it held one file. `msbuild -pp:` (preprocess)
   is the way: 118 imports for DataEngine, **11 outside the SDK** (root/src/brand props,
   `Directory.Build.targets`, `Directory.Packages.props`, `nuget.g.props/targets`,
   `SauceControl.InheritDoc` props/targets, `NETStandard.Library.targets`). Those 11 plus the
   SDK version are the evaluator's inputs for the lock.
2. **`obj/` is shared between brands**: the GCCN import overwrote MESCIUS's generated
   `AssemblyInfo.cs`. Import per brand needs its own `IntermediateOutputPath`
   (`obj/xake/<tfm>/<brand>/`) — same trick `Fsproj.evaluate` already uses for F#.
3. **Sources and obj paths are project-relative** (`Aggregates/Aggregate.cs`,
   `obj/.../X.dll`): the verbatim `csc` invocation runs with cwd = project dir, or the lock
   absolutizes and tokenizes. Tokenizing is the better lock; `$(Root)` covers both repos as
   siblings.
4. **Generated inputs depend on the commit by design**: `AssemblyInfo.cs` carries
   `AssemblyInformationalVersion("5.0.2+<sha>")` (`IncludeSourceRevisionInInformationalVersion`
   defaults to true in dataengine; page sets it false) and `sourcelink.json` maps the repo path
   to a Bitbucket URL at `<sha>`. Reproducing a tag therefore means importing *at that tag*;
   the lock must store these generated files' content (they are small) or regenerate them from
   the recorded commit. Also `.resources` from resgen and the `GeneratedMSBuildEditorConfig`.
5. **NuGet audit reaches the private feed** and `TreatWarningsAsErrors` makes NU1900 fatal
   without the token: pass `-p:NuGetAudit=false` at import (or have the token). Audit is not
   our concern; the SBOM downstream is.
6. **`-t:Compile`, not `CoreCompile`**: page's `AddInternalsVisibleTo` hooks `BeforeCompile`,
   which only runs under `Compile`. Verified: the InternalsVisibleTo attributes are in the
   generated `AssemblyInfo.cs`.
7. `SauceControl.InheritDoc` post-processes the XML doc file after compile — a non-csc step,
   but it touches only the `.xml`, not the dll. Own it as a recipe or drop it from the audited
   set; the dll comparison is unaffected.

**E4 — Babel with `--randomseed`: deterministic except the PE timestamp (and, on the
evaluation build, the expiry constants).** Babel 10.9.0 installed from the private feed
(`AA_AR_TOKEN` via `source ~/set-secrets.sh` *from the project folder* — the script looks for
`.env` upward from cwd). The `.babel` project's `KeyOriginatorFile` is a backslash-relative
path that does not resolve on macOS; `--keyfile <abs>` on the command line fixes it. Results on
`MESCIUS.ActiveReports.Core.Data.DataEngine.dll` (392 KB in, 375 KB out):

| | differing bytes | what they are |
|---|---|---|
| same seed, two runs | **160 bytes in 12 ranges** | COFF `TimeDateStamp` (1 byte), PE `CheckSum` (3), five `ldc.r8` constants = the evaluation licence's expiry date as an OLE date, two seconds apart (5 × 5), four single bytes in the same injected check, and the 128-byte **strong-name signature** that follows from the rest |
| no seed, two runs | 312 044 bytes in 311 ranges, sizes differ | renaming is random: the seed is not optional |
| same seed, map files | identical | the rename map is fully determined by the seed |

`SOURCE_DATE_EPOCH` is **not** honoured (timestamp still wall-clock). So with a real licence
the only non-determinism left is the PE timestamp and checksum, and the signature over them.
Two ways to close it, in order of preference: (1) ask babelfor.net for a deterministic
timestamp option (Roslyn's `/deterministic` hashes the content into that field — the precedent
to cite); (2) normalise ourselves after Babel: set `TimeDateStamp` to a value derived from the
commit or the content, recompute `CheckSum`, and **re-sign the strong name** — the `.snk` is
in the repository, so a `StrongName.sign` in `Xake.Dotnet` (RSA over the PE hash excluding
checksum, cert table and the signature blob; ~60 lines with `System.Security.Cryptography`)
makes ring 2 byte-identical without waiting on the vendor. Until either lands, the auditor
compares ring-2 outputs *modulo timestamp, checksum and signature* and says so.

**Consequences for the plan.** The auditor mode (§8b) is directly buildable: the import is
complete, the baseline is reproducible, cross-repo project references resolve. The SBOM demo
becomes a comparison with an SBOM the build *already* produces, not with a manual one — the
argument is attribution and evidence (per-dll hashes, `formulation`, brand-specific package
identity), not existence. Babel has no seed today; with `--randomseed` it is deterministic up to the PE timestamp (E4
above).

**Token:** `source ~/set-secrets.sh` from inside the project folder loads `AA_AR_TOKEN` (the
script walks up from cwd to find `.env`); with it Babel installs and NuGet audit passes.

## 9. Risks and doubts

- **Market.** Compliance buyers want a checkbox; incumbents (Microsoft's `sbom-tool`, Snyk,
  Sonatype, GitHub dependency graph) already tick it, badly but sufficiently. The "from
  evidence, not reconstruction" argument has to matter to someone with budget.
- **msbuild stays underneath.** The story is "hermetic compilation, recorded evaluation", not
  "no msbuild". Overclaiming will be noticed by exactly the people this targets.
- **Reproducibility across SDKs** is fragile; a reproduce-and-compare CI job is the only honest
  proof, and it has to hold on Linux CI as well as macOS.
- **Two products in one repo.** The general build engine and the compliance toolchain have
  different users; the risk is doing both half-way.
- **Maintenance surface.** Each SBOM format, each attestation scheme, each package metadata
  field is a small forever cost.

## Sources (web, 2026-09-21)

- MESCIUS DsExcel .NET SBOM page (CycloneDX 1.7, on request):
  https://developer.mescius.com/document-solutions/dot-net-excel-api/docs/online/getting-started/software-bill-of-materials
- CRA / BSI TR-03183-2 field floor and format versions: https://craevidence.com/cra-compliance/sbom/cyclonedx-vs-spdx
- Format landscape 2026: https://sbomify.com/2026/01/15/sbom-formats-cyclonedx-vs-spdx/ ,
  https://www.interlynk.io/resources/cyclonedx-vs-spdx-sbom-format
- .NET tooling landscape: https://sbomify.com/guides/dotnet/ ,
  https://andrewlock.net/creating-a-software-bill-of-materials-sbom-for-an-open-source-nuget-package/

- ActiveReports distribution and strong name: https://www.nuget.org/packages/MESCIUS.ActiveReports ,
  https://developer.mescius.com/activereportsnet/docs/devops/install-activereports ,
  https://learn.microsoft.com/en-us/answers/questions/1608533/could-not-load-file-or-assembly-grapecity-activere
- `dotnet sign` CLI and Trusted Signing: https://github.com/dotnet/sign ,
  https://weblog.west-wind.com/posts/2026/Mar/02/Azure-Trusted-Signing-Revisited-with-Dotnet-Sign
- Signed software vs reproducibility: https://reproducible-builds.org/events/athens2015/signed-software/ ,
  https://blog.trailofbits.com/2020/05/27/verifying-windows-binaries-without-windows/
- Obfuscation vs strong names: https://www.preemptive.com/blog/support-corner-dotfuscator-and-strong-named-assemblies/
- Babel Obfuscator: command-line reference (seed, key re-signing) https://docs.babelfor.net/obfuscator/command-line/reference ,
  build servers https://docs.babelfor.net/obfuscator/examples/build-servers ,
  cross-assembly renaming https://docs.babelfor.net/obfuscator/symbols-renaming/cross-assembly-renaming

## 10. Open questions for the discussion

1. ~~Who is the first customer~~ — ActiveReports for .NET (§8a). Drop-in it is.
2. ~~Tool, library or service~~ — decided 2026-09-21: **the Xake script over `Xake.Dotnet`
   types for now**; a CLI tool on top later, with two purposes: (a) simple access and a command
   line for people who will not write F#, (b) a surface for new tools (audit, SBOM, policy) that
   do not need a script at all.
3. How far up the ladder (§6) is the smallest thing worth showing someone outside? Rungs 2–3
   are a weekend on the existing branch; rung 4 is the first thing that gates a release.
   (Partly answered by §8h: the four demo artifacts.)
4. ~~Replace or beside~~ — **beside** (2026-09-21): the auditor that rebuilds from the lock and
   proves `dotnet build`'s output; see the migration paragraph in §8b.
5. ~~"Import the script"~~ — the solution and the csproj files (§8c). Not Cake.
6. Name and framing: hermetic build / supply-chain build / "build that testifies" — which one
   does a compliance officer repeat to their boss?
