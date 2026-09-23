# Session state: hermetic-build

## 2026-09-23 — the R1–R4 run (four subagents, merged)

Where things stand: `feature/hermetic-build` at the merge of `wt/sbom-scope`; suite **358 passed,
1 skipped**, both libraries 0 warnings. What landed today:

- **R1** `conceptual-review.md` §0 status table + inline notes. Core files touched by the branch
  are four (`ExecCore`, `WorkerPool`, `Path`, `Database`), all bug fixes. Still open from the
  review: `Generated`/`.resources` as engine targets (the MSB3577 interop item is its trigger),
  the duplicate `needFiles` in `resolve` (`Dotnet.csc.fs:404`), no lock runner for `fsc`.
- **R2** package-scope SBOM (`Sbom.forPackageScoped`, `Nuget.parseNuspec`,
  `Verify.sbomPackageScope`) and `Sbom.PackageScopeOptions` — `nuget-sbom.md` has the rules and
  **six questions the user must answer** ("Left for a human decision"). Restore-scope
  `forAssembly` output is byte-unchanged; scripts still call it.
- **R3** `csc` through the Roslyn compiler server (`csc-server.md`): `/shared` appended by `run`
  only, locks unchanged, byte-identical proven, 2–4x on small compiles. Verified on macOS only.
- **R4** `overview-ru.md` — the branch in Russian and a staged-release recommendation.

`verify-dataengine.sh` re-run with the server on (SDK 8.0.425): 36/36 byte-identical, 36/36
deterministic, 12/12 SBOMs; `/shared` in all 12 traced command lines; build phase 3.43 s vs
4.44 s in-process (`verify-dataengine.md` §9). `.bootstrap/` is staged from commit 894229a.

**Exact next step**: the user reads `overview-ru.md` and answers the six SBOM questions; then
start the release staging (the page proof with `/shared` is the other option, but see the
MSB3577 item — it needs a pristine page copy). Worktrees `.claude/worktrees/{sbom-scope,csc-shared}` were removed
after the merge.


Updated 2026-09-24 late night (the `Sign` skeleton and its six tests; before that the
`ProjectRefs` fix, the page re-proof, Stage C `csc { lock }`; before that Stage B, the lock
split; before that: stages A1/A2, the autonomous night run, page run, extra roots, Toolset
compiler source, resgen, SDK pin check, import, fromlock mode, one runner, byte-identical proof).

`brief.md` next to this file (committed by the user on 2026-09-22, together with the
`samples/hermetic/dataengine/` inspection artifacts) is the working brief:
positioning, decisions, the design of the library surface (§11), and the day-zero results on
the two ActiveReports repositories (§8j). Everything decided so far is there; do not re-litigate.

`lock-from-settings.md` analyses how a lock is obtained from composed `csc {}` settings (nine
scenarios; migration of an existing tuned block is §9; the update mechanism is an open question,
a global `UPDATE_LOCKS` variable was rejected). `csc-syntax.md` documents the `csc {}` task as
it stands on this branch: composed settings, `CscLock.compile` replaying an imported lock,
`lock "path"` locking composed settings, one runner.

State (2026-09-24, end of day): **slice 1 and slice 2 are closed; slice 3 is StrongName + Pack +
Sign-skeleton done, and Babel (the tool and its licence) is the only piece left waiting on the
user.** Suite **331 passed, 1 skipped**. `dataengine` is still 18/18 and `page` still 90/90
byte-identical on the split locks (`Lock.Entry` in three sections); page's 30 SBOMs regenerate
**30/30 byte-identical**. The older state paragraphs follow for history.

State (2026-09-23, evening): the **SBOM package-scope RFC** from SDP
(`SDP/.memory-bank/framework/docs/6-release/sbom-package-scope-nuget.md`, PDR-0010 draft,
comments to 2026-10-07) says a package SBOM must describe what the customer receives, not the
restore graph -- which is what `Sbom.forAssembly` emits. Summary and principles in
`nuget-sbom.md` ("Package scope"), nine items in tracker.md. Our two advantages there: the lock
records what the compiler was actually handed (so merged/vendored code is a reference we know,
which is the registry's hardest input), and `Pack.entries` reads a built nupkg back (so the
physical inventory is verifiable, not asserted).

State (2026-09-23, later): one lock per variant with every framework inside, and `Restore` --
the lock now obtains the packages it names, into a folder the build chooses. 36/36
byte-identical, 36/36 also when built against a freshly restored package folder.

State (2026-09-23): the **two-framework verification matrix** on dataengine is green --
netstandard2.0 + net472 x MESCIUS + GCCN, 36/36 byte-identical, and it found one defect
(concurrent import of one project for two frameworks, tracker). `verify-dataengine.{fsx,sh,md}`.

State (2026-09-24 evening): **the lock split is done** -- see "stage B" below; the older
state paragraph follows for history.

State: **slice 1's core is done and proven** — `Project.import`, `Lock`, `csc { fromlock }`,
and `import.fsx build` compiles dataengine develop (3 projects × 2 brands) **byte-identical to
`dotnet build`**, 18/18 files (dll, pdb, xml). All uncommitted. The **one-resolved-form refactor of `csc` is done** (2026-09-22, late):
`Dotnet.csc.fs` is now `run` (the only runner: generated files, output dirs, hash check,
`needFiles` on `CscArgs.inputs`, rsp with `/noconfig` outside, compiler selection, `shell`),
`resolve` (the composed settings turned into a `Lock.Project` with the exact argument list the
old code produced, `/noconfig` first when the target framework asks for it), and `Csc` choosing
between `FromLock` and `resolve`. Env vars and resx temp files are `run` parameters, not lock
fields -- the lock's shape is the file format. Two behaviour changes for the composed mode,
both intended: it creates the output directory (before, `samples/fullframework.fsx` needed
`samples/temp/` to exist), and it `needFiles` the framework references too. Trap: `samples/*.fsx`
load Xake from `out/`, so run `dotnet fsi build.fsx -- -- build` before judging them; a stale
`out/` made the refactor look like it had not fixed the directory issue. **State at the end of the autonomous night run (2026-09-24 morning).** 24 commits since
`f2c5a2c`, one per stage, each green (0 warnings both TFMs; suite 309 passed, 1 skipped;
dataengine 18/18 and page 90/90 byte-identical from clean locks on the final assemblies).
Slice 1 closed; slice 2 done (`Nuget`, `Sbom` with `forAssembly`/`forPackage`, `Verify`,
E2E SBOM on page compared with theirs); slice 3 half done (`StrongName` re-sign reproducing
csc, `Pack` deterministic nupkg; Babel and delegated signing need the user); day zero closed
(E3, E5 with two follow-ups); engine fixes (`..` targets, database open retry); import fixes
(CoreCompile forced, race serialized, sourcelink captured, sha tokenized, SDK pin check).
`README.md` in this folder indexes the notes.

**Queue -- decisions only the user can make** (nothing else is blocked):
1. ~~Lock split~~ -- decided 2026-09-24 and done the same day (Stage B, below); the renames
   with it. Left from the conceptual review: item 8 (`Generated`/`.resources` as targets via
   a rule factory), only if a second consumer appears.
2. ~~The lock update mechanism for locks recorded from composed settings~~ -- decided
   2026-09-24 and built the same night (Stage C, below): strict by default, the update an
   explicit target of the script's own.
3. ~~Signing as a delegated rule~~ -- decided 2026-09-24: design it now, land a skeleton against
   a fake signer (`signing.md`, `src/dotnet/Sign.fs`, `src/tests/SignTests.fs`, 6 tests). What
   is left is not a design question: a real `Signer` (signtool / Trusted Signing / HSM), a
   certificate, and the agent that holds the key -- all the user's to supply.
4. Babel recipe (tool from the private feed, licence).
5. Release of the branch (`.bootstrap/` staging until then).
Also worth a look: `conceptual-review.md`, `e5-shipped-vs-local.md` (a shipped dll cannot be
reproduced here -- different compiler build), `import-race.md`, `signing.md`.

**Next step if nothing else is decided**: no single item is next by necessity; candidates,
roughly in reach order --
(a) the third consumer brief §8c asks for: a generated fsx (or a small CLI) driven off the
`Lock`/`CscLock` types, now that the lock's shape (`Lock.Entry`, structured `Options`) is settled
by the split;
(b) `fsc` through the same `Lock.Entry`/`run` shape `csc {}` uses, retiring `Fsproj.evaluate`
(conceptual-review.md §2.6 -- `fsc` has no runner over a lock yet, which is the one thing
keeping `build.fsc.fsx` on its own path);
(c) the alignment-padding label in `Verify.compare` (tracker, signing.md §3): a short unlabelled
`"Content"` range appears when the signed input's length is not already 8-byte aligned;
(d) migrating `build.fsc.fsx` to `Project.import` once (b) gives `fsc` a runner, retiring the
`Fsproj.evaluate` special case for good.
Fixture as before: `git archive origin/develop` of `~/Projects-work/ar/ar-net-core-dataengine`
into the job tmp dir (its checkout is on a broken feature branch), then `ar-net-core-page`.
Always `-p:NuGetAudit=false` (the import sets it) or load the feed token with `cd <ar project
dir> && source ~/set-secrets.sh` (never print it). Xake stays referenced via `#r` on
`.bootstrap/` — no release.

### What landed (2026-09-23, later): one lock per variant, and the lock restores its own packages

Two workstreams run in parallel and merged here. Both were re-verified in this session, not
taken on report; the numbers below are from `verify-dataengine.sh` on a clean fixture with the
merged code. Suite **338 passed, 1 skipped**, 0 warnings on both TFMs.

**1. One lock per variant, every framework inside** (`Lock.Entry.Framework` next to `Name`,
`Lock.Document.Framework` gone, `ImportOptions.Frameworks: string list`,
`Lock.entryFor framework name doc`; `Lock.entry` still works while the name is unique and
fails naming the frameworks when it is not). An old document-level `"Framework"` is read and
distributed into the entries; the new shape is always what gets written. dataengine: 2 locks
of 166 KB instead of 4 of 113/52 KB.

The import is now three msbuild phases per project, all inside that project's
`withProjectLock`: **`-t:Restore` with no `TargetFramework` and `-p:RestoreRecursive=false`**,
then a design-time build per framework with **no `-restore`**, then `-pp` per framework.
That removes the concurrent-import defect by construction: without `TargetFramework` the
assets file holds every target, and without the recursive walk a project's restore stops
rewriting the assets files of the projects it references (verified by hand -- with the flag
the referenced projects' `obj/` are not even created). Six cold concurrent imports: no
`NETSDK1005`, every entry with its package graph, and the lock **byte-identical** between
independent cold imports. `Nuget.readAssets` now fails loudly when the framework has no target
instead of returning an empty graph -- that silence was what produced `packages 0` entries.

Added on top: **a project only gets entries for the frameworks it declares.** `Frameworks` is
the matrix asked for, not a claim that every project has every leg of it; the restore run --
the one phase that is not per framework -- also reports the project's `TargetFrameworks`, and
the import takes the intersection (`Project.frameworksToImport`, pure, unit-tested), tracing
what it skips. Without it, a repository that multi-targets its projects differently walks
into the very `NETSDK1005` this design removes.

**2. `Restore`** (`src/dotnet/Restore.fs`, design note `restore.md`). The lock was a complete
description of what a compilation reads but only a partial source for obtaining it. Now a
missing reference names its own package (the path under the package folder is
`<root>/<id>/<version>/...`), and **one `dotnet restore` of a synthesized project with one
`PackageDownload` per missing package** fetches the whole set -- exact version, no dependency
walk, no framework-compatibility check. Guards: a process-wide memo written after a restore
completes, plus a `Resource` per package folder with a re-check inside. On by default with an
explicit `Enabled = false` opt-out that reproduces the old failure exactly.

**The package folder is the build's to choose** (`Restore.into ".packages"`,
`Roots.packageRootOverride`, `RunOptions.Restore`): the same folder expands
`$(NuGetPackageRoot)` when the lock is read and receives the download, so a build agent caches
one directory next to the checkout. `Roots.withExtra` accordingly lets an extra root replace a
built-in token and resolves a relative root against the project root.

**Measured** (dataengine, 2 brands x netstandard2.0 + net472 = 12 assemblies):

| | |
|---|---|
| byte-identical to `dotnet build -t:Rebuild` | **36/36** |
| deterministic on recompile | **36/36** |
| **identical when built against a freshly restored package folder** | **36/36** |
| SBOMs regenerated | **12/12** |
| import, cold, concurrent | 13.4 s wall / 12.2 s engine (was 18.0 s and needed `-t 1`) |
| build from the locks | 7.3 s; no-op 0.12 s; one source touched 7.6 s |
| `restore` into an empty folder | 2.0 s, 153 MB, compiles nothing |
| build against an empty folder | 9.5 s (one restore, then 12 assemblies) |
| stock .NET for comparison | restore 0.8 s + `dotnet build` 2.5 s per brand |

**Trap that cost an hour, now in `verify-dataengine.md` §8.** A leftover `bin/Release` in the
fixture makes the import *hash* the project-reference paths (they exist), and a script that
decides "this one is mine to rebuild" by "the recorded hash is empty" then compiles against
that stale artifact. Only the projects that have project references differ from the baseline,
which reads exactly like a regression in the compiler path and is not one. The script now
decides by name. Second trap: the page baseline must be the recipe recorded in
`samples/hermetic/page/compare.txt`, property for property -- adding `-p:NetCoreOnly=true` to
it (a property the *import* passes) fails 24 of 30 baseline builds with `MSB3577`.

**page was re-checked, partly.** On the new one-lock-per-variant format the page fixture (12
page projects + 3 dataengine, `LocalBuild=true`, two brands) imports in 48 s into 15 entries
per lock, every entry with its package graph, and builds all 30 outputs in 88 s. Its
**byte-identity was not re-established**: in a tree Xake has built in, the recorded baseline
recipe fails for 12 of 15 projects with `MSB3577` naming a `.resources`. A pristine copy runs
the recipe fine, and deleting the `.resources` Xake writes into the intermediate directory
rescues a single project but not the full sequence -- not fully diagnosed, tracker item, and
interop rather than test hygiene. Page's last byte proof stays the 90/90 of 2026-09-24.

**Merge note**: the two workstreams overlapped in `Dotnet.csc.fs` (adding
`Lock.Entry.Framework` breaks its record constructions) and `ProjectImportTests.fs`; both
three-way merged without conflicts. `RestoreTests.fs` needed the new field.

### What landed (2026-09-23): the two-framework verification matrix on dataengine

The first run of the matrix the customer actually ships: **both brands x both legs of
`<TargetFrameworks>netstandard2.0;net472</TargetFrameworks>`**, 12 assemblies. net472 had never
been imported or compiled by this branch before. New files: `verify-dataengine.fsx` (4 locks,
12 compiles, 12 SBOMs), `verify-dataengine.sh` (the scenario end to end with timings),
`verify-dataengine.md` (the write-up; §6 the defect, §7 the restore question).

- **36/36 byte-identical** to `dotnet build -c Release -t:Rebuild` per (framework, brand) in
  the same directory with the same `IntermediateOutputPath` -- dll, pdb and xml of 3 projects
  x 2 brands x 2 frameworks. **36/36 deterministic** when the same locks are compiled again,
  **12/12** SBOMs byte-identical when regenerated.
- The two frameworks differ in the lock exactly where they should: 113-115 references
  (`netstandard.library` 2.0.3) and 12 defines against 10-12 references
  (`Microsoft.NETFramework.ReferenceAssemblies.net472` 1.0.3) and 17 defines; same sources,
  same compiler, 4 packages each. Locks 113 KB (netstandard) / 52 KB (net472) per brand.
- **Timings** (one run, 8-core macOS): import cold 18.0 s (`-t 1`; ~9 s concurrent), build from
  locks 8.2 s with zero msbuild and zero restore, no-op 0.15 s (engine), touch-a-source 8.3 s
  (12/12 recompile -- VBFunctionLib is at the bottom of the graph), 12 SBOMs 0.23 s. Stock
  .NET on the same fixture: first restore 5.6 s / 156 MB / 6 packages, warm restore 0.7 s,
  `dotnet build` per brand (both frameworks, clean obj) 2.4-2.5 s, no-op 1.0 s, touch 2.3 s.
  For the same 12 assemblies: ~6.4 s of stock restore+build (warm) against 9.2 s from
  committed locks with no msbuild, no restore and no network -- and a SHA-256 check of every
  referenced file. Per assembly Xake is ~1.7x slower for one reason: no `/shared`, so every
  compile is a fresh `dotnet csc.dll` process while msbuild reuses `VBCSCompiler`.
- **Defect (tracker)**: two frameworks of one multi-targeted project cannot be imported
  concurrently. The global `-p:TargetFramework=X` makes restore write an assets file with only
  X's target, and `-restore` walks the project graph, so a project's assets file is rewritten
  by its *dependents'* imports -- which `withProjectLock` (per project) does not cover. Every
  concurrent run inspected showed it: two failed with `NETSDK1005 ... doesn't have a target
  for '<fwk>'`, two silently recorded an entry with `packages 0` (an SBOM missing its package
  components, with no warning). `-t 1` is the workaround; three candidate fixes are in the
  tracker. `Nuget.readAssets` returning an empty graph instead of failing is the second half.
- **Restore from the lock** (the open question it raises): an empty `NUGET_PACKAGES` fails
  cleanly before the compiler runs, 256 x `expected <sha256>, got missing`. The compiler is
  restored when it is a package; references are not, although the lock carries
  `Dependencies.Packages` with id/version/sha512 and reference paths that spell id and version,
  and `DotNetFwk.sdkImpl.restorePackage` already exists. Tracker item.

### What landed (2026-09-24, night): stage C -- `csc { lock "path" }`

The user's decision on the open update question (2026-09-24): **strict by default, the update
explicit, no engine mode, no global variable.** Built as migration path A of
`lock-from-settings.md` §9; the semantics and the script pattern are in `csc-syntax.md`,
"Locking composed settings: `lock`".

- **`CscSettingsType.Lock: string option`**, custom operation `lock "path"` (project-root
  relative like any target path, or absolute). `Csc` resolves as always, then: no lock file --
  `Lock.rehash` and `Lock.save` a one-entry `Lock.Document` (`Framework` = the settings'
  `targetfwk` or "", `Configuration` "", `Properties` []), compile the *rehashed* entry;
  lock present and `Lock.diff recorded resolved` empty -- compile the **recorded** entry, so
  its hashes gate the build; different -- fail with the diff and the two update routes named.
  `nofailonerror` downgrades the failure to a warning and compiles the resolved entry.
- **`CscLock.record path settings`** (resolve, rehash, overwrite; what an `update-locks` phony
  calls) and **`CscLock.verify path settings : Recipe<string list>`** (the diff alone, nothing
  written, nothing compiled -- scenario 3). `CscLock.compile` still serves imported locks.
- **The lock is not an engine target on this path** -- written from inside the compile recipe,
  which is what lets a tuned `csc { }` block stay in place. 1b (lock as a file target) is
  unchanged for scripts that prefer it. **Path B (`cscSettings {}`) is not built.**
- **One library change outside the task**: `Lock.diff`'s `diffCompiler` now skips `Sha256` when
  either side is empty, the rule `diffHashed` already followed (empty means "not computed").
  Without it every diff of a recorded lock against freshly resolved settings reported the
  compiler hash as a difference, and the strict path could never have matched.
- **Tests**: `src/tests/CscLockTests.fs` (6, Integration) -- record + compile, second build
  leaves the lock byte-identical (bytes and mtime), an added source fails with the path and the
  word "lock" in the message, `CscLock.record` overwrites and the next build passes, a
  reference built by the test itself and tampered after recording fails the hash check
  ("expected ... got ..."), `verify` reports the difference without writing. Suite **324
  passed, 1 skipped** (was 318/1); both projects 0 warnings on both TFMs.
- Trap: inside `module CscLock`, `resolve` is the module's own (the entry alone), not the outer
  private one that also returns the framework env vars -- the shadowing is silent until the
  tuple pattern fails to typecheck. And `recipe` has no `ReturnFrom`, so `return!` does not
  compile: bind and return.

### Page re-proof after the lock split (2026-09-24)

The page fixture -- 12 page projects + 3 dataengine, netstandard2.0, brands MESCIUS/GCCN,
`LocalBuild=true`, multi-target projects with reference aliases -- had not been re-run since
stage B. Re-run from clean (`rm -rf locks out .xake import.log`, all `obj/xake` in both
repositories): `import-page.fsx build` **77.4 s** wall (both locks imported concurrently,
16.3 s each, then 30 compiles), `sbom` **0.9 s** for 30 `cdx.json`. Baseline exactly as
`compare.txt` records it -- in place, `-t:Rebuild`, same `IntermediateOutputPath`,
`-p:NuGetAudit=false`, per brand, 30 sequential builds, 472 s -- then `cmp` on every
dll/pdb/xml: **90/90 byte-identical**, unchanged from before the split. Locks are 858 KB /
6682 lines per brand (were 1.03 MB / 8015 lines flat). Every one of the 15 entries in both
locks has a non-empty package graph, dataengine's three single-`TargetFramework` projects
included (`packages 4 (3 direct)` each) -- the `Nuget.frameworkFullName` fix holds on this
fixture too. SBOMs: all 30 have non-empty components with purls; regenerating the whole `out/`
tree a second time is **30/30 byte-identical**. The purl count of each project now equals its
lock entry's `packages N` -- the SBOM is a straight projection of the lock's graph. Two
comparison-visible changes: package ids keep their original NuGet casing (they come from the
restore graph, not the cache's lowercase directory names), so the old casing difference against
CycloneDX.MSBuild is gone; and Rdl/MESCIUS went 9 -> 12 purls, which moves
`Microsoft.NETCore.Platforms` into the common set and adds the two build-time-only packages
`CycloneDX.MSBuild` / `SauceControl.InheritDoc` (scope `excluded`) to the ours-only list. The
remaining theirs-only entries (`Microsoft.NETFramework.ReferenceAssemblies.net472`,
`System.ValueTuple`) are in the *net472* leg of `project.assets.json`, which the
`NetCoreOnly=true` import never evaluates -- verified in the assets file. One pre-existing defect surfaced while checking the new locks (not a regression -- 52
occurrences per lock, byte-identical spellings before and after the split): `Evaluation.ProjectRefs`
records `<ProjectReference>` paths resolved against the *process* cwd instead of the referencing
project's directory, so they point at files that do not exist and are therefore un-tokenized
absolute paths. Same class as the `<EmbeddedResource Update=...>` bug `parseImport` already
fixes; the fix was never applied to `ProjectRefs`. Nothing reads `ProjectRefs` (the compile
replays `Dependencies.References`), so builds and SBOMs are unaffected -- tracker item, not a
blocker. Race check clean:
MESCIUS lock 35 `ds.documents` / 0 `gcdocs`, GCCN the reverse. One fixture artifact worth
knowing: dataengine's `bin/Release` outputs were left on disk, so the import hashed the two
project-reference entries and this run's SBOMs carry SHA-256 on those file components where
the previous run's had none -- evidence-at-import-time, not a rule change. Artifacts replaced
in `samples/hermetic/page/`: `MESCIUS.json`, `GCCN.json`, `compare.txt`, `sbom-compare.txt`,
`Rdl-MESCIUS-ours.cdx.json`. CycloneDX.MSBuild (Part 2) was not re-run; the stored
`Rdl-MESCIUS-theirs.cdx.json` from 2026-09-23 is what the purl diff uses.

### What landed (2026-09-24, evening): stage B -- the lock split

Decisions were the user's (2026-09-24); the shape is in `csc-syntax.md` ("Where a `Lock.Entry`
comes from"). Commits `aa5ddc8` (library + tests), `94f8087` (scripts, fixtures, proof), then
the docs.

- **`Lock.Entry = { Name; Evaluation; Compilation; Dependencies }`**, `Lock.Document` with
  `Entries`, `Lock.entry name doc`. `Evaluation`: `Project`, `ProjectRefs`, `Imports`, `Sdk`,
  typed `SdkPin option` (the DU moved from `Project` into `Lock`; `Lock.sdkPinText`/`parseSdkPin`
  keep the old text form in the file), `Properties` without `SdkPin`/`NETCoreSdkVersion`.
  `Compilation`: `Directory`, `Options`, `Defines`, `Sources`, `Generated`, `Resources`.
  `Dependencies`: `Compiler { Tool; Path; Sha256; Version }`, `References { Path; Sha256; Alias }`,
  `Analyzers`, `Packages`. `Args`/`Sources`/`Output` are members. A composed (`resolve`) entry
  has an empty-valued `Evaluation` and `SdkPin = None`.
- **Structured `Options` with section markers** (`@Sources`, `@References`, `@Analyzers`,
  `@Defines` at the position each block had). `Lock.Compilation.ofArgs` factors, `Entry.Args`
  rebuilds, and `parseImport` **fails with a `Lock.diffList`** when the rebuilt list is not
  msbuild's. Markers rather than a canonical order because the real locks have
  `/warnaserror+:NU1605` after the sources and block order differs between Roslyn versions.
  `resolve` goes through the same path and now spells `/reference:` (was `/r:`).
- **`Compiler.Version`** is the compiler's own product version (`FileVersionInfo`, cut at `+`);
  for the SDK's native `csc` launcher (what `locateFramework` returns on macOS) the `csc.dll`
  next to it is read -- the launcher itself has no version resource, which is how the first
  test run caught it.
- **Package graph in the lock**: `Project.packages cacheRoot assets` inside `withProjectLock`
  right after the design-time build; `Lock.Package = { Id; Version; Sha512; Direct; DependsOn }`.
  The assets copy and the `ProjectAssetsFile` property hack (import-race.md) are gone.
  `Sbom.forAssembly cacheRoot entry assemblyPath` reads the lock only (supplier/license still
  from the cache; the package hash is the lock's, the cache is not re-consulted). `Nuget.fs`
  compiles before `Project.fs`.
- **Bug found on the fixture**: dataengine's first Stage B lock had `packages 0` -- a
  single-`TargetFramework` project's `project.assets.json` keys `targets` by the full framework
  name (`.NETStandard,Version=v2.0`), and `Nuget.readAssets` only matched the alias (page's
  multi-target projects use the alias, which is why the page SBOM had worked). `Nuget.frameworkFullName`
  + a test. So the page SBOM comparison never exercised a single-target assets file; dataengine's
  SBOM would have had no packages.
- **Proof**: clean fixture (`rm -rf locks src/*/obj .xake`), `import.fsx build` 9.5 s both
  brands, round-trip check held on all 6 entries; in-place `dotnet build -t:Rebuild` baseline
  per brand; **18/18 byte-identical**. Locks 889 lines / 113 KB per brand (were 1135 / 153 KB
  flat), `samples/hermetic/dataengine/` replaced (locks, `brand-diff.txt` 152 lines,
  `compare.txt`).
- **Tests**: 318 passed, 1 skipped (was 309). New pure tests: `ofArgs`/`Args` round trip on a
  realistic list (alias, quoted comma path, `/define:`, a switch after the sources), the
  round-trip failure diff (two `/define:` switches), packages from an assets fixture, `diff` on
  packages, `SdkPin` text round trip, flat-lock refusal, single-target assets key. Both projects
  0 warnings, `build.fsc.fsx build test` green on the re-staged `.bootstrap/`.
- **Format**: `"Entries"` (was `"Projects"`); a flat lock is refused with "lock written by an
  older Xake; re-import". `Alias` is written only when non-empty; `SdkPin` is `""` for `None`.
- Traps: `base` is an F# keyword (a test binding named `base` fails to parse); a python splice
  anchored on doc text that also appears in the new module cut the wrong region -- anchor on
  the `module X =` header.

### What landed (2026-09-24): stage A2 -- the lock is not a setting

- `run` takes `RunOptions = { FailOnError; CscPath }` instead of the whole settings record;
  `CscSettingsType.FromLock` and the `fromlock` operation are gone, replaced by the replay
  entry point `CscLock.compile project` (and `compileWith options project`) next to
  `CscLock.resolve`. `Csc settings` is one path again: resolve, then run.

### What landed (2026-09-24): stage A1 -- helpers out of `Fsproj`, project root from the engine

- `src/dotnet/Json.fs` (`module internal Json`) and `src/dotnet/Roots.fs` (`Roots.nugetRoot`/
  `dotnetRoot`/`builtinTokens`/`builtin`/`withExtra`/`tokenize`/`tokenizeAll`/`expand`, plus the
  recipes `current`/`currentWith`) compile before `Fsproj.fs`; `$(ProjectRoot)` now comes from
  `ExecOptions.ProjectRoot`, so `Lock.load`/`loadWith`/`save`/`saveWith` and `Fsproj.load`
  replace the cwd-based `Lock.read`/`write`/`parse` and `Fsproj.roots`/`withRoots`/`write`/`parse`.

### What landed (2026-09-23, night): page with `LocalBuild=true`, extra roots

- **Extra roots, explicit**: `ImportOptions.Roots` (token → absolute path) and
  `Lock.writeWith/parseWith/readWith roots`; `Fsproj.withRoots` validates the token shape and
  keeps longest-root-first. Decision (user): one token per sibling repository, no shared parent
  root — `$(DataEngineRoot)` for page.
- **`import-page.fsx`**: cwd = the page copy, 15 projects per lock so the cross-repo project
  references resolve inside the lock; a second, absolute-path rule for dataengine's outputs
  because **file targets with `..` do not match** (engine gap, tracker). Locks ~1 MB each,
  16 s per brand, `build` of 30 outputs 42–47 s. The private feed needed no token (cached
  credentials); `NuGetAudit=false` sufficed.
- **Two bugs found live**: `-getItem`'s `FullPath` for an `<EmbeddedResource Update=...>` item
  resolves against the *process* cwd, not the project dir — `parseImport` now combines
  `Identity` with the project directory; and the csc runner had no working directory, so a
  relative `<include>` in doc comments failed (CS1589) — `run` now sets `workdir
  project.Directory`.
- **Result**: dataengine's 18 files identical again; page's xml identical; page's dll identical
  in size and differing only in the deterministic-hash fields (timestamp, checksum, MVID,
  strong-name signature, PDB GUID), pdb differing more. Some input differs from msbuild's.
  Root cause open — first item of the autonomous queue.
- **Race**: two brands importing one project concurrently share `obj/project.assets.json`;
  with brand-dependent package ids the wrong reference was recorded once. Locks were then built
  one target at a time. `BaseIntermediateOutputPath` per variant broke ResxTests and was
  reverted. Open, tracker.
- Artifacts in `samples/hermetic/page/`.

### What landed (2026-09-23, later): the SDK pin check

`Project.sdkPin projectDir` → `NoGlobalJson | Pinned v | RollsForward (v, policy) | NoVersion file`
(walks up for `global.json`; absent `rollForward` is `latestPatch`; only `disable` counts as
pinned). `parseImport` takes it and records `Properties["SdkPin"]` and `["NETCoreSdkVersion"]` —
free map, no lock format change. `import` warns per project when not pinned, or pinned but a
different SDK ran. dataengine: `8.0.100 rollForward:latestFeature`, ran 8.0.425 — six warnings.
Suite: 257 passed, 1 skipped. Five pure tests in `ProjectImportTests.fs`.

### What landed (2026-09-23, later): resgen for `fromlock`

- **`Resx.fs`** (compiled right after `ResourceFileset.fs`, because `Impl.compileResx` in
  `DotnetTasks.fs` uses it): `Resx.read` parses string entries in document order with
  `XmlDocument`, keeping `xml:space="preserve"` whitespace; `Resx.compile` writes them with
  `System.Resources.ResourceWriter`. No `#if`: netstandard2.0 has both. Typed entries
  (`type=`/`mimetype=`) and `ResXFileRef` are refused with the entry name — the real resx files
  (page's four) are strings only; the `Color1`/`Bitmap1`/`Icon1` hits in them are the standard
  header *comment*. Output is **byte-identical to msbuild's GenerateResource** (test with
  multi-line, unicode incl. emoji, empty value). The `resgen` task and `Impl.compileResx` no
  longer fail on netstandard for string resx.
- **`Lock.Project.Resources: (resx * .resources) list`**, from `EmbeddedResource` items: after
  `PrepareResources` msbuild exposes `OutputResource` (exact path), `ManifestResourceName`,
  `Type`, `WithCulture` — `parseImport` prefers `OutputResource`. `run` gets a step before the
  hash check: `needFiles` each resx, regenerate the `.resources` when missing or older.
- **Bug caught by the fixture**: `parseImport`'s `Generated` filter took every input under the
  intermediate dir that existed, which after `PrepareResources` includes the binary `.resources`
  — read as text, written back mangled by `run`. Resources are now computed first and excluded
  from `Generated`. dataengine never showed it (no resx).
- Tests: `ResxTests.fs` (3, one Integration that runs `dotnet build` for the baseline). Suite:
  252 passed, 1 skipped. dataengine still 18/18 identical, locks regenerated with `Resources`.

### What landed (2026-09-23): the Toolset compiler source

- **Brief §11 assumed the import reads `CscToolPath`/`CscToolExe` — it cannot.** The
  `Microsoft.Net.Compilers.Toolset` package never sets them; it redirects `CSharpCoreTargetsPath`
  and the `Csc` task assembly to its own `tasks/netcore/`, and the task's tool path defaults to
  the `bincore` next to whatever targets file drives it. `parseImport` therefore derives the
  compiler from `CSharpCoreTargetsPath`'s directory (`GetFullPath` folds the `build/../tasks`):
  the SDK's `Roslyn/bincore/csc.dll` for an unpinned project, the package's for a pinned one.
  `RoslynTargetsPath` always reports the SDK's Roslyn and is only the fallback.
- **On SDK 9+/10 the package is silently ignored** unless the project also sets
  `<RoslynCompilerType>Toolset</RoslynCompilerType>` — `Microsoft.NET.Sdk.BeforeCommon.targets`
  otherwise reassigns `CSharpCoreTargetsPath` back. The fixture sets it. Worth checking on any
  customer project that pins the toolset. Note the Xake repo's `global.json` rolls forward to
  the newest SDK (10.0.401 here), so the fixture is evaluated by SDK 10 while dataengine's is 8.
- **`csc { toolset "4.12.0" }`** in the composed mode: `csc.dll` from
  `microsoft.net.compilers.toolset/<version>/tasks/netcore/bincore` in the NuGet cache, restored
  through `DotNetFwk.sdkImpl.restorePackage` (made internal, with `nugetRoot`) when missing;
  references and env vars still from `DotNetFwk.locateFramework`; `run` executes it through the
  existing `dotnet <dll>` branch. `cscpath` still overrides everything.
- **`CscArgs` bug found by the fixture**: netstandard2.0 projects embed
  `.NETStandard,Version=v2.0.AssemblyAttributes.cs` via `/embed:"..."` — a *quoted* list item
  with a comma inside, and an `=` inside a path. The list splitter is now quote-aware
  (`splitList`/`quoteIfNeeded`) and `alias=` is only recognized before the first `/`. dataengine
  did not show it (SDK 8 emits no `/embed` for it); still 18/18 identical after the fix.
- Fixture `samples/hermetic/toolset/` (the in-repo C# fixture of brief §12, first piece),
  `samples/hermetic/README.md`, `src/tests/ToolsetTests.fs` (3 Integration tests: import names
  the package compiler, `fromlock` compiles with it, composed `toolset` compiles with it — each
  with its own `Variant`, since the design-time build's `obj/xake` lives in the fixture dir, not
  the test sandbox). Suite: 249 passed, 1 skipped. `csc-syntax.md` has a "Compiler sources"
  section.

### What landed (2026-09-22, late): `csc { fromlock }` and the end-to-end proof

- **`compileFromLock`** (`Dotnet.csc.fs`), reached through `CscSettingsType.FromLock:
  Lock.Project option` / the `fromlock` custom operation. Replays `project.Args` verbatim:
  writes back `Generated` files that are missing or differ (the lock is the source of truth),
  creates output directories, verifies SHA-256 of every hashed reference, analyzer and the
  compiler (all mismatches reported at once, "missing" when absent, then fails), `needFiles` the
  `CscArgs.inputs`, and runs the compiler: `cscpath` if set, else `dotnet <csc.dll>` when the
  lock's compiler path ends in `.dll`, else the path itself. `/noconfig` stays on the command
  line — inside an rsp csc warns CS2023 and ignores it. Everything else goes through the rsp
  with `Impl.escapeArgument`.
- **`Lock.mapPaths f project`** rewrites every path (Args, References, Analyzers, Generated keys);
  a reference whose path changed loses its hash. The script uses it to point the unhashed
  project references (`src/<Other>/bin/Release/...`, msbuild's own output) at the lock's own
  `/out` for that project, and `need`s those first — so the build order comes from the lock.
- **`import.fsx build`**: rule `src/(proj:*)/obj/xake/(fwk:*)/(brand:*)/(name:*).dll` reads the
  lock, maps the project references, `csc { fromlock }`; `build` reads every lock and needs
  every `Output`. `need` takes paths relative to `ProjectRoot`, so the absolute `Output` is
  relativized with `Path.GetRelativePath`.
- **Proof**: clean `build` 4.6 s for 6 assemblies; no-op 50 ms; `touch` a VBFunctionLib source →
  6 dlls recompile (both brands, all dependents), locks untouched, 4.9 s. Then `dotnet build`
  in the *same* directory with the same `IntermediateOutputPath`, and `cmp`: **all 18 files
  identical**, generated inputs identical too. A first comparison against a baseline built in a
  sibling copy differed by a few hundred bytes per dll — csc embeds absolute source paths in
  the PDB and, via `/deterministic`, in the PE — which is why the baseline must be built in
  place (or both builds must use `/pathmap`). Worth remembering for the auditor mode: a
  reproduction on another machine needs `/pathmap` or the same checkout path.
- **Tests**: `FromLockTests.fs` (3, two Integration). Suite: 246 passed, 1 skipped. Both
  projects 0 warnings. `DotNetFwk.locateFramework (Some "netstandard2.0")` returns the SDK's
  native `csc` launcher, not `csc.dll`, so the test takes `csc.dll` next to it — and it is the
  *latest* SDK (10.0.401 here), while the imported lock names 8.0.425 via `global.json`.
- Inspectable artifacts in `samples/hermetic/dataengine/` (untracked): both locks, `brand-diff.txt`,
  `generated/`, `console.txt`, `compile-console.txt`, `compare.txt`. The dataengine source is
  not copied there.

### What landed (2026-09-22, afternoon): `Project.import`, `Lock`, `CscArgs`

All in `src/dotnet/Project.fs`, after `Fsproj.fs` (it reuses `Fsproj.Json`, `roots`, `expand`).

- **`CscArgs`** — the compiler command line as data. `parse` tells a switch from a source
  (an absolute Unix path also starts with `/`: a switch has no second slash before the colon);
  `shapes` knows which switches name files and how (`Path`, comma-separated `PathList` with
  optional `alias=`, `PathFirst` for `/resource:file,name`); `inputs`, `outputs`, `sources`,
  `switchValues`, `mapPaths`, `absolutize` (also folds `..`, so the SDK's
  `targets/../analyzers/x.dll` and its real path are one file). Both the import and the
  future `fromlock` mode read arguments through it — keep the switch tables here only.
- **`Lock`** — `Lock.File = { Framework; Configuration; Properties; Projects }`, one file per
  (TFM, variant), one `Lock.Project` per project: `Args` verbatim with absolute paths,
  `References`/`Analyzers`/`Imports` as `{ Path; Sha256 }` (empty hash = did not exist at
  import, i.e. a project reference's output), `Compiler = { Tool; Path; Sha256; Sdk }`,
  `Generated` = the files msbuild wrote under the intermediate dir that are compiler inputs
  (AssemblyInfo.cs, TFM attributes, GeneratedMSBuildEditorConfig) *with content*, and a short
  `Properties` whitelist. `write`/`parse`/`read`/`project`. Sources and `/out` are computed
  members, not stored twice. `write` tokenizes with `tokenizeAll`, a regex replace of a root
  followed by `/ = , ;` or the end — a prefix replace missed `/pathmap:<root>=/_/`.
- **`Fsproj.roots ()` gained `$(DotnetRoot)`** (from `DotNetFwk.sdkImpl.dotnetRoot`, made
  internal): compiler and analyzer paths live there. The kept `projects/*.json` did not change
  shape (nothing of Xake's references the SDK dir).
- **`Project.import options`** — per project, two msbuild runs: the design-time build
  (`-restore -p:ProvideCommandLineArgs=true -p:SkipCompilerExecution=true
  -p:BuildProjectReferences=false -p:NuGetAudit=false -p:IntermediateOutputPath=obj/xake/<fwk>/<variant>/
  -t:PrepareResources;Compile -getItem:CscCommandLineArgs,ReferencePath,Analyzer,ProjectReference
  -getProperty:...`) and `-pp` for the imports. `parseImports` takes the path line above each
  `====` banner rule. Imports under `NetCoreRoot` are dropped (the SDK version stands for them),
  and so are those under `BaseIntermediateOutputPath` (restore's `nuget.g.props/targets`); the
  rest are `needFiles`'d, which is what makes a brand-props edit re-import that brand only.
  `ImportOptions.Variant` names the obj subtree; the brand goes there.
- **Measured on dataengine develop** (3 projects, 2 brands, from `import.fsx` in this folder,
  run with cwd = the repo copy so `$(ProjectRoot)` is that repo): 3 s for both locks cold,
  msbuild node reuse doing most of it; no-op 25 ms; `touch src/MESCIUS.props` re-imports the
  MESCIUS lock only. Each lock is 153 KB / 1135 lines, every path tokenized (`$(ProjectRoot)`,
  `$(NuGetPackageRoot)`, `$(DotnetRoot)`). The brand diff is 92 lines and is exactly the
  brand delta: assembly names, `/out`/`/doc`, the two project-reference paths, the generated
  files' paths and content. The `git archive` copy has no `.git`, so no `/sourcelink` or
  `/embed` appears — those show up on a real checkout (brief §8j trap 4).
- **Tests**: `src/tests/ProjectImportTests.fs`, 6 tests, no msbuild needed. Suite: 243 passed,
  1 skipped. Both projects build with 0 warnings.

**Lock stability (discussed 2026-09-22).** As written, the lock changes for three reasons: source
layout (every PR, that is its nature as a compile manifest), SDK patches (monthly, and per
developer under `latestFeature`), and — on a real checkout — every commit, because
`AssemblyInfo.cs` in `Generated` carries the commit sha. The user wants the file split (compiler,
sources, dependencies, framework apart); decided to **defer** until locks are diffed for real,
since the in-memory `Lock.Project` stays the unit and only `write`/`parse` would change. The
sha tokenization is due when the script first runs on a live repository. Items in tracker.md.
Two more decisions from the same discussion (the first reworded 2026-09-23): a well-formed project
pins the SDK exactly with `global.json`, and then a `Compiler` change is a deliberate, reviewed
event -- but that is a *recommendation* the tool checks and warns about, not something it relies
on; without the pin (or with `rollForward: latestFeature`, dataengine's case) the `Compiler`
section drifts per SDK patch and per developer. And when the split
happens the lock should become **structured** (fields for sources, references, defines,
options) rather than the raw `csc` argument list — §8c's "do not reconstruct" stays honoured by
a round-trip check at import: the command line rebuilt from the structure must equal msbuild's.

Traps from this step:

- `dotnet test --filter` rejects a value with a space (`Name~"Project import"` fails in
  msbuild property parsing); use `Name~import`.
- A `let mutable` local cannot be captured inside a `recipe { for ... do! }` loop — use a
  `ResizeArray`.
- The design-time build's `IntermediateOutputPath` must end with `/`, and the generated files
  land under it, but restore's `nuget.g.*` land under `BaseIntermediateOutputPath` (`obj/`),
  so filtering on the intermediate dir alone keeps them.

## Branch notes: the fsc-based build script (moved from docs/session.md)

`build.fsx` is untouched and still the build of record. Next to it, `build.fsc.fsx` compiles both
assemblies with the `fsc` task — msbuild compiles nothing — and is meant to replace it after the
next release.

**The division of labour**: msbuild is the only thing that reads a project file correctly, so it
is asked, once per project, what to compile, what to reference and what to define; the answer is
cached in a file rule; the compilation itself is the `fsc` task's, driven by explicit arguments.

- **`Fsproj.evaluate`** (`src/dotnet/Fsproj.fs`) runs
  `dotnet msbuild -restore -t:PrepareForBuild;GenerateAssemblyInfo;ResolveReferences` with
  `-getItem`/`-getProperty` and writes msbuild's json to a file. Every part of that target list
  earns its place: `PrepareForBuild` triggers `AddImplicitDefineConstants` (that is where
  `NETSTANDARD2_0` and the `_OR_GREATER` chain come from), `GenerateAssemblyInfo` writes the
  attributes file, `ResolveReferences` produces the reference list. `BuildProjectReferences=false`
  is not optional: without it msbuild builds the referenced project, which is the thing being
  avoided.
  What msbuild writes is *dumped*, not kept: every metadata field of every item, 200 KB and 3600
  lines per project of which one field is read. `evaluate` rewrites it into the ~15 KB of lists
  the build actually consumes (`Fsproj.write`), with paths written against `$(NuGetPackageRoot)`
  and `$(ProjectRoot)`.
- **`Fsproj.parse`** reads that kept form (`parseEvaluation` reads msbuild's own). Sources are `CompileBefore @ Compile @ CompileAfter` — for
  F# the SDK puts the generated `AssemblyInfo.fs` in **`CompileBefore`** (see
  `FSharp/Microsoft.FSharp.Overrides.NetSdk.targets`), not `Compile`, so it lands first, which is
  what makes `InternalsVisibleTo("Xake.Dotnet")` reach the compiler. The json parser is
  hand-written: `System.Text.Json` is a package dependency on netstandard2.0 and would land on
  every consumer of Xake.
- **The kept evaluation is tracked in git** (`projects/<fwk>/<lib>.json`) and behaves as a
  lockfile for the compilation: because of the tokens, a regeneration on another machine is
  byte-identical, so a diff there means the project really changed. It is produced by a plain
  file rule that depends on the `.fsproj` and (through the recipe) on the `Version` var. Second
  build: 38 ms, msbuild not started. Touch a source file: recompiled, msbuild still not started.
  `clean` does not touch it.
- **`ReferencePath` points a project reference at that project's own `bin/`**, so the script
  filters those out and substitutes its own `out/<fwk>/<name>.dll`. It does *not* `need` them:
  the `fsc` task already `needFiles` everything it references, and asking once is the way to
  write it. Back when a second request rebuilt the target, that one redundant `need` cost 3 of
  the 7 seconds a clean build took — which is what uncovered the engine bug below.
- **The `fsc` task stayed thin** — the whole diff against `dev` is `doc`, netstandard targeting
  (`DotNetFwk.sdkImpl`, see docs/dotnet-build.md) and a `define` fix: fsc reads `--define:A;B` as
  one symbol named `A;B`, so the task emits one switch per symbol.
- **One target, one execution per run** (`WorkerPool.fs`). A target asked for a second time
  after the first request had finished used to be built again, and it took two mechanisms to
  get there: the pool deduped only *in-flight* requests (dropping the entry on completion), and
  the "does it need rebuilding" verdict is memoized for the whole run
  (`getChangeReasons ctx |> memoizeRec`, `ExecCore.fs`) — that memo is how the recursive graph
  analysis ties its knot, not an optimization one can drop — so the second request never asked
  the database again and got the pre-build "Not built yet" answer.

  The pool now keeps two maps: `running`, shared by whoever asks whenever they ask, and
  `finished`, which serves the rest of the run and is emptied when the next one starts.
  `Scheduler.newRun` marks that boundary and is posted exactly where the memo is created — in
  `runBuild.runTargets` (a target group) and in `demandTarget` (one `Demand`). So within a run a
  target executes once; across runs the database decides again, which is what makes a second
  `Demand` notice a changed variable; and a task already going when a run begins still serves
  it, as it always did for concurrent demands. A run-numbered variant was tried first and
  dropped: same size, more to explain. Covered by `builds a target requested twice in one run
  only once` and `builds a file needed and then needFiled only once`.

Where the time goes, measured with everything cold (`obj/`, `bin/`, `out/`, `.xake` removed):

| | `build.fsx` (dotnet build) | `build.fsc.fsx` |
|---|---|---|
| everything cold | 4.6 s | 4.9 s |
| only `out/` removed | 2.1 s — msbuild's `obj/` is still warm, so it copies rather than compiles | 4.9 s — it has no such cache, it recompiles |
| nothing changed | 0.7 s | 0.7 s |
| a source file touched | — | 4.0 s, no msbuild run |
| a `.fsproj` touched | — | 4.5 s, one msbuild run, `projects/` unchanged |

The middle row is the whole of the difference: msbuild keeps its own incremental state in `obj/`
and `bin/`, and `dotnet build --output out` then degenerates into a copy. Comparing that against
a real compilation is what made the fsc build look twice as slow.

Traps worth remembering:

- A list expression that mixes literals with a `for` comprehension turns the literals into
  statements and **silently drops them** (`FS0020`); the msbuild command line lost every flag
  that way. Use `@` between lists, or `yield` on every element.
- A triple-quoted string whose closing `"""` sits left of the enclosing offside line breaks the
  parse of everything after it (`FS0010: Unexpected identifier in member definition`). Indent the
  literal into the block.

**Bootstrapping it** needs a *frozen copy* of the assemblies — the script overwrites `out/`, and
overwriting an assembly fsi has loaded kills the run with a `BadImageFormatException`:

```bash
dotnet fsi build.fsx -- -- build
mkdir -p .bootstrap && cp out/netstandard2.0/*.dll .bootstrap/
dotnet fsi build.fsc.fsx -- -- build test
```

After the release both `#r` lines become `#r "nuget: Xake, <version>"` and the staging goes away.
`dotnet test` and `dotnet pack` still shell out to the SDK: the test project is msbuild's, and the
nupkg carries a `net462` asset fsc cannot produce here (see the limitation below).

## How to verify the fsc build

```bash
dotnet fsi build.fsx -- -- build                                         # dotnet build, produces out/
# and the fsc build, which needs its bootstrap staged first
mkdir -p .bootstrap && cp out/netstandard2.0/*.dll .bootstrap/
rm -rf out .xake && dotnet fsi build.fsc.fsx -- -- build test
dotnet fsi build.fsc.fsx -- -- build                                     # no-op, no msbuild run
```
