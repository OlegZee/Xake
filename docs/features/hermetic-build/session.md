# Session state: hermetic-build

## 2026-10-22 — release prep for hermetic 0.7.0 on `pr/hermetic-xake-3.11` (from `dev` 29d0d14)

#85 merged (29d0d14). Its CI needed two fixes: `xake.fingerprints` after the 0.7.0 version bump
(the fsproj is an input of the description), and `build.yml` runs one `build` job on SDK 10.0.x:
the 8.0.x leg rolled forward (`global.json` `latestMajor`) to the image's SDK 10.0.401 and only
duplicated it. 0.6.0 was never released; 0.7.0 carries it. Before the tag, the `Xake` range is
`[3.11.0.40, 3.12)` (was `[3.10.0.35, 3.11)`): Xake 3.11.0 (tag on #84) has the compiler runner
that quotes an SDK path with a space, and `Tool.quoteArgument`, which replaces hermetic's copy
`ToolArgs.quote`. 3.11 adds a field only to the runner's internal plan record, none to a record
hermetic builds. The hermetic lock is refreshed with `-d LOCK_REFRESH=Xake` (a plain
`update-locks` keeps 3.10.0.35, and the fsc self-build would then miss `quoteArgument`); the
test fixture description of src/hermetic moves from `[3.9.0.30, 3.10)` to the new range for the
same reason. hermetic tests 302 passed; `build.fsc.fsx build check` pass; nuspec depends on
`Xake [3.11.0.40, 3.12.0)`. **Exact next step**: merge this PR, tag `hermetic-v0.7.0` on `dev`,
then switch the consumer's `.xml` rule to `Locks.doc`.

PR, branch, worktree:

| PR | Branch | Worktree | Base | Status |
|---|---|---|---|---|
| #84 SDK and rsp paths quoted | `pr/sdk-path-spaces` | `.claude/worktrees/pr+sdk-path-spaces` | `dev` 62d9b1a | merged (0d65475); worktree to remove |
| #85 Locks.doc, compile checks its target | `pr/locks-compile-targets` | `.claude/worktrees/pr/locks-compile-targets` | `dev` 0d65475 | merged (29d0d14); worktree to remove |
| Xake floor 3.11 for hermetic 0.7.0 | `pr/hermetic-xake-3.11` | `.claude/worktrees/pr/hermetic-xake-3.11` | `dev` 29d0d14 | draft |

## 2026-10-22 — `Locks.compile` per target kind on `pr/locks-compile-targets` (from `dev` 0d65475)

[locks-compile-targets.md](locks-compile-targets.md) (reviewed, approved by the owner) implemented, hermetic 0.7.0: public `Locks.doc settings lockPath fwk` is the `.xml` rule, and **`compile` on an `.xml` target is a hard error** (xake-ar's `build.fsx` has the one consumer rule: `"out/(b:*)/(cfg:*)/(fwk:*)/(asm:*).xml" ..> doc`, `doc` calling `Locks.doc`, to switch after the release). `compile` checks its target against the entry's output (`TargetFileName`, else `<Name>.dll`/`.exe`); `compileDll` is `compileAssembly`; the `$(Version)` folder comes from the lock's `Directory` (root-relative `""` fixed), and version masks expand through the script's roots (`$(Sibling)/**`). The release notes entry is in `docs/hermetic/README.md` (where hermetic's notes live). **Exact next step**: review and merge the draft PR, release `hermetic-v0.7.0`, then switch xake-ar's `.xml` rule.

## 2026-10-22 — SDK path with a space fixed on `pr/sdk-path-spaces` (from `dev`)

Hermetic compiles failed with the SDK under `C:\Program Files\dotnet` (`sdk-path-spaces.md`
has the digest and the plan). `CompilerRunner` quotes the compiler dll and `@rsp` and starts the
full `dotnet(.exe)` path; fsc's response file is written verbatim (it rejects quoted switch
lines), csc's stays argv-quoted; the restores, `runMsbuild` and the `msbuild` task quote their
arguments. The quoter is public `Xake.Dotnet.Tool.quoteArgument`; the hermetic package keeps a
copy (`ToolArgs.quote`) until its `Xake` floor carries it, so the compile fix reaches hermetic
users with the next `Xake` release. **Merged** (#84); a Windows
run with the default SDK location still confirms it.

PR, branch, worktree:

| PR | Branch | Worktree | Base | Status |
|---|---|---|---|---|
| #84 SDK and rsp paths quoted | `pr/sdk-path-spaces` | `.claude/worktrees/pr+sdk-path-spaces` | `dev` 62d9b1a | merged; tests 405/1 skipped, hermetic 295; `build.fsc.fsx check` + `build` pass |


## 2026-10-21 — SDK-free locks implemented (#83, draft)

Xake side of [sdk-free-locks.md](sdk-free-locks.md) steps 1-3 on `pr/sdk-free-locks`: a recorded entry's `dotnet-sdk` prerequisite follows its content; hermetic 0.6.0. Findings in that note's status: an installed targeting pack is not byte-identical to the nuget.org package (signed; facades and source generators are other builds), so a lock recorded from `packs/` still needs that pack; the toolset package 5.9.0 is another Roslyn build than SDK 10.0.402's. **Exact next step**: the owner decides where a `pack:` hash comes from, then review and merge #83.

## 2026-10-18 — cycle 3 opened on `pr/binding-redirects` (from `dev`)

The consumer-side "four cells 92 s against 69 s" was a cold compiler server, not an engine regression: warm runs are 69.6–71.8 s, CPU 725–750 s = 4 × a single cell, server 621–644 s; `build-par` 76 s (CPU-bound). `binding-redirects.md` is the S55 design: the import already runs `GenerateBindingRedirects`, the gap is recording the finished `.exe.config` (apps) and `.dll.config` (test libraries) instead of the pre-redirect file; no build-side change. **Exact next step**: the owner answers Q1–Q6 in that note, then implement on this branch (~150–250 lines incl. tests), PR to `dev`, 0.4.3.

## 2026-09-29 — extraction plan revised, spike done

`extraction-plan.md` revised with the user: the resolved compilation is the `Csc` record, obtained
from `csc { ...; resolve }` (a final operation, `Run` overloaded like `ShellBuilder`); the seam is
`Csc.run` with **no hooks**, hermetic steps (restore, revision token, compiler-missing messages,
lock gate) run in `Lock.build` *before* `Csc.run`; `CSC_SERVER` script variable next to `NETFX`.
Spike (scratch, not in the repo): extension custom op + extension `Run` from another assembly
**works** on F# 8, option field equality fine, but `Csc.run` **cannot** resolve while the
function `Csc` exists (FS0039, binds to the value). Plan §5 has the a/b/c choice.

The user chose **a**: retire `Csc settings` in favour of `Csc.compile settings` (lands with B2).

B1 landed the same day (pure file split, suite 358/1). **Exact next step**: B2, see the tracker.

## 2026-09-23 — the R1–R4 run (four subagents, merged)

Where things stand: `feature/hermetic-build` at the merge of `wt/sbom-scope`; suite **358 passed,
1 skipped**, both libraries 0 warnings. What landed today:

- **R1** `conceptual-review.md` §0 status table + inline notes. Core files touched by the branch
  are four (`ExecCore`, `WorkerPool`, `Path`, `Database`), all bug fixes. Still open from the
  review: `Generated`/`.resources` as engine targets (the MSB3577 interop item is its trigger),
  the duplicate `needFiles` in `resolve` (`Dotnet.csc.fs:484`), no lock runner for `fsc`.
- **R2** package-scope SBOM (`Sbom.forPackageScoped`, `Nuget.parseNuspec`,
  `Verify.sbomPackageScope`) and `Sbom.PackageScopeOptions` — `nuget-sbom.md` has the rules and
  **six questions the user must answer** ("Left for a human decision"). Restore-scope
  `forAssembly` output is byte-unchanged; scripts still call it.
- **R3** `csc` through the Roslyn compiler server (`csc-server.md`): `/shared` appended by `run`
  only, locks unchanged, byte-identical proven, 2–4x on small compiles. Verified on macOS only.
- **R4** `overview-ru.md` — the branch in Russian and a staged-release recommendation.

`the repo-a verification runner` re-run with the server on (SDK 8.0.425): 36/36 byte-identical, 36/36
deterministic, 12/12 SBOMs; `/shared` in all 12 traced command lines; build phase 3.43 s vs
4.44 s in-process (`the repo-a verification notes` §9). `.bootstrap/` is staged from commit 894229a.

**Exact next step**: the user reads `overview-ru.md` and answers the six SBOM questions; then
start the release staging (the page proof with `/shared` is the other option, but see the
MSB3577 item — it needs a pristine page copy). Worktrees `.claude/worktrees/{sbom-scope,csc-shared}` were removed
after the merge.


Updated 2026-09-24 late night (the `Sign` skeleton and its six tests; before that the
`ProjectRefs` fix, the page re-proof, Stage C `csc { lock }`; before that Stage B, the lock
split; before that: stages A1/A2, the autonomous night run, page run, extra roots, Toolset
compiler source, resgen, SDK pin check, import, fromlock mode, one runner, byte-identical proof). Names in the history
below are as they were then: `fromlock`/`FromLock` became `CscLock.compile` (stage A2),
`Lock.Project`/`Lock.File`/`Lock.project` became `Lock.Entry`/`Lock.Document`/`Lock.entry`
(stage B), the cwd-based `Fsproj.roots`/`withRoots`/`write`/`parse` and `Lock.read`/`write`/`parse`
became `Roots.*` and the `Lock.load`/`save` recipes (stage A1).

`brief.md` next to this file (committed by the user on 2026-09-22, together with the
`samples/hermetic/repo-a/` inspection artifacts) is the working brief:
positioning, decisions, the design of the library surface (§11), and the day-zero results on
the two the trial product repositories (§8j). Everything decided so far is there; do not re-litigate.

`lock-from-settings.md` analyses how a lock is obtained from composed `csc {}` settings (nine
scenarios; migration of an existing tuned block is §9; the update mechanism is an open question,
a global `UPDATE_LOCKS` variable was rejected). `csc-syntax.md` documents the `csc {}` task as
it stands on this branch: composed settings, `CscLock.compile` replaying an imported lock,
`lock "path"` locking composed settings, one runner.

State (2026-09-24, end of day): **slice 1 and slice 2 are closed; slice 3 is StrongName + Pack +
Sign-skeleton done, and Babel (the tool and its licence) is the only piece left waiting on the
user.** Suite **331 passed, 1 skipped**. `repo-a` is still 18/18 and `page` still 90/90
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

State (2026-09-23): the **two-framework verification matrix** on repo-a is green --
netstandard2.0 + net472 x Acme + Globex, 36/36 byte-identical, and it found one defect
(concurrent import of one project for two frameworks, tracker). `verify-repo-a.{fsx,sh,md}`.

State (2026-09-24 evening): **the lock split is done** -- see "stage B" below; the older
state paragraph follows for history.

State: **slice 1's core is done and proven** — `Project.import`, `Lock`, `csc { fromlock }`,
and `import.fsx build` compiles repo-a develop (3 projects × 2 brands) **byte-identical to
`dotnet build`**, 18/18 files (dll, pdb, xml). All uncommitted. The **one-resolved-form refactor of `csc` is done** (2026-09-22, late):
`Dotnet.csc.fs` is now `run` (the only runner: generated files, output dirs, hash check,
`needFiles` on `CscArgs.inputs`, rsp with `/noconfig` outside, compiler selection, `shell`),
`resolve` (the composed settings turned into a `Lock.Project` (renamed to `Lock.Entry` in stage B) with the exact argument list the
old code produced, `/noconfig` first when the target framework asks for it), and `Csc` choosing
between `FromLock` (gone in stage A2: `CscLock.compile`) and `resolve`. Env vars and resx temp files are `run` parameters, not lock
fields -- the lock's shape is the file format. Two behaviour changes for the composed mode,
both intended: it creates the output directory (before, `samples/fullframework.fsx` needed
`samples/temp/` to exist), and it `needFiles` the framework references too. Trap: `samples/*.fsx`
load Xake from `out/`, so run `dotnet fsi build.fsx -- -- build` before judging them; a stale
`out/` made the refactor look like it had not fixed the directory issue. **State at the end of the autonomous night run (2026-09-24 morning).** 24 commits since
`f2c5a2c`, one per stage, each green (0 warnings both TFMs; suite 309 passed, 1 skipped;
repo-a 18/18 and page 90/90 byte-identical from clean locks on the final assemblies).
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
Fixture as before: `git archive origin/develop` of `~/path/to/repo-a`
into the job tmp dir (its checkout is on a broken feature branch), then `repo-b`.
Always `-p:NuGetAudit=false` (the import sets it) or load the feed token with `cd <ar project
dir> && source ~/set-secrets.sh` (never print it). Xake stays referenced via `#r` on
`.bootstrap/` — no release.

### What landed (2026-09-23, later): one lock per variant, and the lock restores its own packages

Two workstreams run in parallel and merged here. Both were re-verified in this session, not
taken on report; the numbers below are from `the repo-a verification runner` on a clean fixture with the
merged code. Suite **338 passed, 1 skipped**, 0 warnings on both TFMs.

**1. One lock per variant, every framework inside** (`Lock.Entry.Framework` next to `Name`,
`Lock.Document.Framework` gone, `ImportOptions.Frameworks: string list`,
`Lock.entryFor framework name doc`; `Lock.entry` still works while the name is unique and
fails naming the frameworks when it is not). An old document-level `"Framework"` is read and
distributed into the entries; the new shape is always what gets written. repo-a: 2 locks
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

**Measured** (repo-a, 2 brands x netstandard2.0 + net472 = 12 assemblies):

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

**Trap that cost an hour, now in `the repo-a verification notes` §8.** A leftover `bin/Release` in the
fixture makes the import *hash* the project-reference paths (they exist), and a script that
decides "this one is mine to rebuild" by "the recorded hash is empty" then compiles against
that stale artifact. Only the projects that have project references differ from the baseline,
which reads exactly like a regression in the compiler path and is not one. The script now
decides by name. Second trap: the page baseline must be the recipe recorded in
`samples/hermetic/page/compare.txt`, property for property -- adding `-p:NetCoreOnly=true` to
it (a property the *import* passes) fails 24 of 30 baseline builds with `MSB3577`.

**page was re-checked, partly.** On the new one-lock-per-variant format the page fixture (12
page projects + 3 repo-a, `LocalBuild=true`, two brands) imports in 48 s into 15 entries
per lock, every entry with its package graph, and builds all 30 outputs in 88 s. Its
**byte-identity was not re-established**: in a tree Xake has built in, the recorded baseline
recipe fails for 12 of 15 projects with `MSB3577` naming a `.resources`. A pristine copy runs
the recipe fine, and deleting the `.resources` Xake writes into the intermediate directory
rescues a single project but not the full sequence -- not fully diagnosed, tracker item, and
interop rather than test hygiene. Page's last byte proof stays the 90/90 of 2026-09-24.

**Merge note**: the two workstreams overlapped in `Dotnet.csc.fs` (adding
`Lock.Entry.Framework` breaks its record constructions) and `ProjectImportTests.fs`; both
three-way merged without conflicts. `RestoreTests.fs` needed the new field.

### What landed (2026-09-23): the two-framework verification matrix on repo-a

The first run of the matrix the customer actually ships: **both brands x both legs of
`<TargetFrameworks>netstandard2.0;net472</TargetFrameworks>`**, 12 assemblies. net472 had never
been imported or compiled by this branch before. New files: `the repo-a verification script` (4 locks,
12 compiles, 12 SBOMs), `the repo-a verification runner` (the scenario end to end with timings),
`the repo-a verification notes` (the write-up; §6 the defect, §7 the restore question).

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
  `targetfwk` or "", `Configuration` "", `Properties` [] -- since `ab978d3` the framework is
  the entry's `Entry.Framework` and the document has no `Framework` field), compile the *rehashed* entry;
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

The page fixture -- 12 page projects + 3 repo-a, netstandard2.0, brands Acme/Globex,
`LocalBuild=true`, multi-target projects with reference aliases -- had not been re-run since
stage B. Re-run from clean (`rm -rf locks out .xake import.log`, all `obj/xake` in both
repositories): `import-page.fsx build` **77.4 s** wall (both locks imported concurrently,
16.3 s each, then 30 compiles), `sbom` **0.9 s** for 30 `cdx.json`. Baseline exactly as
`compare.txt` records it -- in place, `-t:Rebuild`, same `IntermediateOutputPath`,
`-p:NuGetAudit=false`, per brand, 30 sequential builds, 472 s -- then `cmp` on every
dll/pdb/xml: **90/90 byte-identical**, unchanged from before the split. Locks are 858 KB /
6682 lines per brand (were 1.03 MB / 8015 lines flat). Every one of the 15 entries in both
locks has a non-empty package graph, repo-a's three single-`TargetFramework` projects
included (`packages 4 (3 direct)` each) -- the `Nuget.frameworkFullName` fix holds on this
fixture too. SBOMs: all 30 have non-empty components with purls; regenerating the whole `out/`
tree a second time is **30/30 byte-identical**. The purl count of each project now equals its
lock entry's `packages N` -- the SBOM is a straight projection of the lock's graph. Two
comparison-visible changes: package ids keep their original NuGet casing (they come from the
restore graph, not the cache's lowercase directory names), so the old casing difference against
CycloneDX.MSBuild is gone; and Model/Acme went 9 -> 12 purls, which moves
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
Acme lock 35 `ds.documents` / 0 `gcdocs`, Globex the reverse. One fixture artifact worth
knowing: repo-a's `bin/Release` outputs were left on disk, so the import hashed the two
project-reference entries and this run's SBOMs carry SHA-256 on those file components where
the previous run's had none -- evidence-at-import-time, not a rule change. Artifacts replaced
in `samples/hermetic/page/`: `Acme.json`, `Globex.json`, `compare.txt`, `sbom-compare.txt`,
`Model-Acme-ours.cdx.json`. CycloneDX.MSBuild (Part 2) was not re-run; the stored
`Model-Acme-theirs.cdx.json` from 2026-09-23 is what the purl diff uses.

### What landed (2026-09-24, evening): stage B -- the lock split

Decisions were the user's (2026-09-24); the shape is in `csc-syntax.md` ("Where a `Lock.Entry`
comes from"). Commits `aa5ddc8` (library + tests), `94f8087` (scripts, fixtures, proof), then
the docs.

- **`Lock.Entry = { Name; Evaluation; Compilation; Dependencies }`**, `Lock.Document` with
  `Entries`, `Lock.entry name doc`. (`Framework` joined `Name` in `ab978d3`, with
  `Lock.entryFor framework name doc`.) `Evaluation`: `Project`, `ProjectRefs`, `Imports`, `Sdk`,
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
- **Bug found on the fixture**: repo-a's first Stage B lock had `packages 0` -- a
  single-`TargetFramework` project's `project.assets.json` keys `targets` by the full framework
  name (`.NETStandard,Version=v2.0`), and `Nuget.readAssets` only matched the alias (page's
  multi-target projects use the alias, which is why the page SBOM had worked). `Nuget.frameworkFullName`
  + a test. So the page SBOM comparison never exercised a single-target assets file; repo-a's
  SBOM would have had no packages.
- **Proof**: clean fixture (`rm -rf locks src/*/obj .xake`), `import.fsx build` 9.5 s both
  brands, round-trip check held on all 6 entries; in-place `dotnet build -t:Rebuild` baseline
  per brand; **18/18 byte-identical**. Locks 889 lines / 113 KB per brand (were 1135 / 153 KB
  flat), `samples/hermetic/repo-a/` replaced (locks, `brand-diff.txt` 152 lines,
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

- `run` takes `RunOptions = { FailOnError; CscPath }` (since extended with `Restore` and
  `Server`) instead of the whole settings record;
  `CscSettingsType.FromLock` and the `fromlock` operation are gone, replaced by the replay
  entry point `CscLock.compile project` (and `compileWith options project`) next to
  `CscLock.resolve`. `Csc settings` is one path again: resolve, then run.

### What landed (2026-09-24): stage A1 -- helpers out of `Fsproj`, project root from the engine

- `src/dotnet/Json.fs` (`module internal Json`) and `src/dotnet/Roots.fs` (`Roots.nugetRoot`/
  `dotnetRoot`/`builtinTokens`/`builtin`/`withExtra`, internal `tokenize`/`tokenizeAll`/`expand`,
  plus the recipes `current`/`currentWith`; `nugetPackageRootToken`/`packageRootOverride` came
  later with `Restore`) compile before `Fsproj.fs`; `$(ProjectRoot)` now comes from
  `ExecOptions.ProjectRoot`, so `Lock.load`/`loadWith`/`save`/`saveWith` and `Fsproj.load`
  replace the cwd-based `Lock.read`/`write`/`parse` and `Fsproj.roots`/`withRoots`/`write`/`parse`.

### What landed (2026-09-23, night): page with `LocalBuild=true`, extra roots

- **Extra roots, explicit**: `ImportOptions.Roots` (token → absolute path) and
  `Lock.writeWith/parseWith/readWith roots`; `Fsproj.withRoots` (now `Roots.withExtra`, stage A1) validates the token shape and
  keeps longest-root-first. Decision (user): one token per sibling repository, no shared parent
  root — `$(RepoARoot)` for page.
- **`import-page.fsx`**: cwd = the page copy, 15 projects per lock so the cross-repo project
  references resolve inside the lock; a second, absolute-path rule for repo-a's outputs
  because **file targets with `..` do not match** (engine gap, tracker). Locks ~1 MB each,
  16 s per brand, `build` of 30 outputs 42–47 s. The private feed needed no token (cached
  credentials); `NuGetAudit=false` sufficed.
- **Two bugs found live**: `-getItem`'s `FullPath` for an `<EmbeddedResource Update=...>` item
  resolves against the *process* cwd, not the project dir — `parseImport` now combines
  `Identity` with the project directory; and the csc runner had no working directory, so a
  relative `<include>` in doc comments failed (CS1589) — `run` now sets `workdir
  project.Directory` (today `entry.Compilation.Directory`).
- **Result**: repo-a's 18 files identical again; page's xml identical; page's dll identical
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
(the DU is `Lock.SdkPin` since stage B, recorded as `Evaluation.SdkPin`/`Evaluation.Sdk` rather
than in the property bag)
(walks up for `global.json`; absent `rollForward` is `latestPatch`; only `disable` counts as
pinned). `parseImport` takes it and records `Properties["SdkPin"]` and `["NETCoreSdkVersion"]` —
free map, no lock format change. `import` warns per project when not pinned, or pinned but a
different SDK ran. repo-a: `8.0.100 rollForward:latestFeature`, ran 8.0.425 — six warnings.
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
- **`Lock.Project.Resources: (resx * .resources) list`** (now `Lock.Entry.Compilation.Resources`), from `EmbeddedResource` items: after
  `PrepareResources` msbuild exposes `OutputResource` (exact path), `ManifestResourceName`,
  `Type`, `WithCulture` — `parseImport` prefers `OutputResource`. `run` gets a step before the
  hash check: `needFiles` each resx, regenerate the `.resources` when missing or older (later: only when missing -- the engine
  decides staleness, conceptual-review.md 2.3).
- **Bug caught by the fixture**: `parseImport`'s `Generated` filter took every input under the
  intermediate dir that existed, which after `PrepareResources` includes the binary `.resources`
  — read as text, written back mangled by `run`. Resources are now computed first and excluded
  from `Generated`. repo-a never showed it (no resx).
- Tests: `ResxTests.fs` (3, one Integration that runs `dotnet build` for the baseline). Suite:
  252 passed, 1 skipped. repo-a still 18/18 identical, locks regenerated with `Resources`.

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
  the newest SDK (10.0.401 here), so the fixture is evaluated by SDK 10 while repo-a's is 8.
- **`csc { toolset "4.12.0" }`** in the composed mode: `csc.dll` from
  `microsoft.net.compilers.toolset/<version>/tasks/netcore/bincore` in the NuGet cache, restored
  through `DotNetFwk.sdkImpl.restorePackage` (made internal, with `nugetRoot`; today
  `Restore.download`) when missing;
  references and env vars still from `DotNetFwk.locateFramework`; `run` executes it through the
  existing `dotnet <dll>` branch. `cscpath` still overrides everything.
- **`CscArgs` bug found by the fixture**: netstandard2.0 projects embed
  `.NETStandard,Version=v2.0.AssemblyAttributes.cs` via `/embed:"..."` — a *quoted* list item
  with a comma inside, and an `=` inside a path. The list splitter is now quote-aware
  (`splitList`/`quoteIfNeeded`) and `alias=` is only recognized before the first `/`. repo-a
  did not show it (SDK 8 emits no `/embed` for it); still 18/18 identical after the fix.
- Fixture `samples/hermetic/toolset/` (the in-repo C# fixture of brief §12, first piece),
  `samples/hermetic/README.md`, `src/tests/ToolsetTests.fs` (3 Integration tests: import names
  the package compiler, `fromlock` compiles with it, composed `toolset` compiles with it — each
  with its own `Variant`, since the design-time build's `obj/xake` lives in the fixture dir, not
  the test sandbox). Suite: 249 passed, 1 skipped. `csc-syntax.md` has a "Compiler sources"
  section.

### What landed (2026-09-22, late): `csc { fromlock }` and the end-to-end proof

- **`compileFromLock`** (`Dotnet.csc.fs`; today the private `run`, reached through `CscLock.compile`), reached through `CscSettingsType.FromLock:
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
- Inspectable artifacts in `samples/hermetic/repo-a/` (untracked): both locks, `brand-diff.txt`,
  `generated/`, `console.txt`, `compile-console.txt`, `compare.txt`. The repo-a source is
  not copied there.

### What landed (2026-09-22, afternoon): `Project.import`, `Lock`, `CscArgs`

All in `src/dotnet/Project.fs`, after `Fsproj.fs` (it reuses `Fsproj.Json`, `roots`, `expand` -- `Json` and `Roots` modules since stage A1).

- **`CscArgs`** — the compiler command line as data. `parse` tells a switch from a source
  (an absolute Unix path also starts with `/`: a switch has no second slash before the colon);
  `shapes` knows which switches name files and how (`Path`, comma-separated `PathList` with
  optional `alias=`, `PathFirst` for `/resource:file,name`); `inputs`, `outputs`, `sources`,
  `switchValues`, `mapPaths`, `absolutize` (also folds `..`, so the SDK's
  `targets/../analyzers/x.dll` and its real path are one file). Both the import and the
  future `fromlock` mode read arguments through it — keep the switch tables here only.
- **`Lock`** — `Lock.File = { Framework; Configuration; Properties; Projects }` (today
  `Lock.Document = { Configuration; Properties; Entries }`, framework per entry), one file per
  (TFM, variant), one `Lock.Project` per project: `Args` verbatim with absolute paths,
  `References`/`Analyzers`/`Imports` as `{ Path; Sha256 }` (empty hash = did not exist at
  import, i.e. a project reference's output), `Compiler = { Tool; Path; Sha256; Sdk }`,
  `Generated` = the files msbuild wrote under the intermediate dir that are compiler inputs
  (AssemblyInfo.cs, TFM attributes, GeneratedMSBuildEditorConfig) *with content*, and a short
  `Properties` whitelist. `write`/`parse`/`read`/`project` (today `writeWith`/`parseWith`/`readWith`, the recipes
  `load`/`save`, and `entry`/`entryFor`). Sources and `/out` are computed
  members, not stored twice. `write` tokenizes with `tokenizeAll`, a regex replace of a root
  followed by `/ = , ;` or the end — a prefix replace missed `/pathmap:<root>=/_/`.
- **`Fsproj.roots ()` (today `Roots.builtin projectRoot`) gained `$(DotnetRoot)`** (from `DotNetFwk.sdkImpl.dotnetRoot`, made
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
- **Measured on repo-a develop** (3 projects, 2 brands, from `import.fsx` in this folder,
  run with cwd = the repo copy so `$(ProjectRoot)` is that repo): 3 s for both locks cold,
  msbuild node reuse doing most of it; no-op 25 ms; `touch src/Acme.props` re-imports the
  Acme lock only. Each lock is 153 KB / 1135 lines, every path tokenized (`$(ProjectRoot)`,
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
on; without the pin (or with `rollForward: latestFeature`, repo-a's case) the `Compiler`
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
  the build actually consumes (`Fsproj.write`, today internal `writeWith`), with paths written against `$(NuGetPackageRoot)`
  and `$(ProjectRoot)`.
- **`Fsproj.parse`** (today `parseWith roots` / the recipe `load`) reads that kept form (`parseEvaluation` reads msbuild's own). Sources are `CompileBefore @ Compile @ CompileAfter` — for
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
mkdir -p .bootstrap && cp out/netstandard2.0/*.dll .bootstrap/          # Xake, Xake.Dotnet, Xake.Hermetic.Dotnet
dotnet fsi build.fsc.fsx -- -- build test
```

After the release both `#r` lines become `#r "nuget: Xake, <version>"` and the staging goes away.
`dotnet test` and `dotnet pack` still shell out to the SDK: the test project is msbuild's, and the
nupkg carries a `net462` asset fsc cannot produce here (see the limitation below).

## How to verify the fsc build

```bash
dotnet fsi build.fsx -- -- build                                         # dotnet build, produces out/
# and the fsc build, which needs its bootstrap staged first
mkdir -p .bootstrap && cp out/netstandard2.0/*.dll .bootstrap/          # Xake, Xake.Dotnet, Xake.Hermetic.Dotnet
rm -rf out .xake && dotnet fsi build.fsc.fsx -- -- build test
dotnet fsi build.fsc.fsx -- -- build                                     # no-op, no msbuild run
```

## B2 landed (2026-09-29): the resolved compilation as a public API

`Csc.fs` (new, after `Restore.fs`/`CscArgs.fs`) holds the settings (`CscSettingsType` without
`Lock`, `Server: CompilerServer option`), `RunOptions { FailOnError; CscPath; Server;
Environment }`, `CompilerServer.resolve`, the `Csc` record and `module Csc` (`ofArgs`, `args`,
`ofSettings`, `runOptions`, `run`, `compile`, `rehash`, `mapPaths`, `mapText`, `diffList`,
`sha256`, `hashed`, `compilerVersion`, `isMarker`). `Dotnet.csc.fs` is the builder only
(`resolve` + two `Run` overloads). `Lock.fs` has `Entry = { Csc; Evaluation; Packages }`,
`Options { Run; Restore }`, `build(With)`, `compile(With)`, `record`, `verify`,
`restoreRequest`, `restore` (was `Restore.prepare`), and `csc { lock }` as an extension.
`Restore.ensure/missing` take a `Restore.Request { Packages; Paths }`. Lock JSON byte-identical:
golden test against 99faffc's writer, and the repo-a locks `cmp`-identical old vs new.
`the repo-a verification runner`, run from a temp copy with its own `.bootstrap`: 36/36 byte-identical
vs `dotnet build`, 36/36 deterministic, 12/12 SBOMs, 36/36 from a fresh package folder.

Judgment calls:
- `Hashed`, `Reference`, `Compiler`, `Dependencies`, `Csc` and `Package` are namespace-level types
  (their field labels must be in scope for record literals); `Package` lives in `Restore.fs`.
  `Lock.Hashed/Reference/Compiler/Package` stay as abbreviations. The settings types sit in
  `[<AutoOpen>] module CscTypes`.
- `Csc.ofArgs : string list -> Csc` (empty name/framework/directory, compiler `{ Tool = "csc" }`
  with the other fields empty); `Csc.args c = c.Args`.
- `Lock.Entry` has no members any more (`Args`, `Output`, `Sources` are on `entry.Csc`).
  `Lock.ofCsc` and `Lock.Evaluation.Empty` are new. `Lock.sha256/hashed/compilerVersion` are
  gone (now `Csc.*`), and `Lock.diffList` forwards to `Csc.diffList`.
- `Csc.runOptions settings` is public: it is the helper `Csc.compile` and `csc { lock }` share. It
  reads `NETFX`/`NETFX-TARGET` a second time, which records the dependency twice and is
  harmless.
- When `CscPath` is not set, `Csc.run` fails with a plain "'X': the compiler P does not exist"
  if the compiler is missing. The SDK/package-specific messages only come from `Lock.compile`.
- `Lock.build`/`Lock.compile` resolve the server with `CompilerServer.resolve None`, and
  `buildWith`/`compileWith` take the options as given. `CSC_SERVER` goes through `getVar`, so it
  is a tracked dependency and changing it rebuilds (same as `NETFX`). An empty or unknown value
  logs a warning and means `on`.
- The "compiling 'X'" trace now comes after the restore and revision steps, because it moved
  into `Csc.run`.
- `CscLockTests` builds through `Lock.buildWith` + `Csc.runOptions`, the same path the `lock`
  sugar takes. The first test in that file uses `csc { ...; lock path }` literally. The two
  "no evaluation" asserts in the `ofSettings` test now check `Lock.ofCsc c` against
  `Evaluation.Empty`.
- `src/dotnet/readme.md` never called `Csc settings`, so it is unchanged. `samples/book/intro.fsx`
  and the three `docs/features/hermetic-build/*.fsx` are updated. `.bootstrap/` is untouched, so
  the updated fsx need a re-staged bootstrap.

## B3-B5 (2026-09-29): base files off the hermetic and internal names

New `Tool.fs` (first in compile order; `diagnosticLevel`, `failOnExitCode`, with `Impl.levelFromString`/`failOnExitCode` as forwarders) and `Hash.fs` (`Hash.sha256`, "" for a missing file, as the lock always had it; `Verify.sha256` used to throw on a missing file and now returns "" -- `Csc.sha256`/`Verify.sha256` stay as forwarders because tests call them). `DotNetFwk` gained public `nugetRoot`, `dotnetRoot`, `normalizedPackageRoot`, `downloadPackages` (the one `dotnet restore` of a synthesized `PackageDownload` project, moved out of `Restore.download`) and `restorePackage`; `csc { toolset }` uses `restorePackage None`, and the "already restored" check is now the package directory, not `csc.dll`. `CscArgs` helpers are public, not folded (only `Csc.fs` uses them). 362 passed, 1 skipped, 0 warnings after each step.

Corrections after B3–B5 (orchestrator, 2026-09-29): `Hash.sha256` throws on a missing file
again (a verification must not produce "" by accident); the "" convention stays in
`Csc.sha256` only, and the lock/import/SBOM call sites that hash possibly-missing files use
that one. `csc { toolset }` checks `csc.dll` inside the restored folder and fails with a
"partial restore" message instead of trusting the folder.

## B7 (2026-09-29): one resx path -- decided against, documented

`Impl.compileResx` has two bodies on purpose. net462 uses `ResXResourceReader` (typed values, `ResXFileRef`, and the 4.0.0.0 -> 2.0.0.0 type-name rewrite); netstandard2.0 uses `Resx.compile`, which reads only plain `<data><value>` strings and throws on any entry with `type`/`mimetype` or no `value`. Routing net462 through `Resx.compile` would drop typed and file-ref resources (images, icons, binary) that work there today, and `System.Windows.Forms` could only be removed at that price. So the reference stays, and the reason is a doc comment above `Impl.compileResx`. Evidence: `ResxTests` compares the regenerated `.resources` byte for byte with msbuild's, but it runs on net8.0 and therefore exercises the netstandard2.0 build only; the net462 path cannot be run on this macOS box (no Mono/msbuild), so no byte-identity claim is made for it. To unify later, `Resx.read` must support typed values and `ResXFileRef`, with a test for each against msbuild.

## B8 (2026-09-29): one `needFiles` for the inputs; the no-`targetfwk` default investigated

**Part 1, done.** `Csc.ofSettings` no longer `needFiles` sources, references and plain resource files; `Csc.run` does, once, through `needFiles (CscArgs.inputs args)`. Reason: every path that compiles goes through `run` (`csc { }` via `Csc.compile`, `csc { ...; lock }` via `Lock.buildWith` -> `compileWith` -> `run`, and `Lock.compile`, which never calls `ofSettings`), and `run` demands a superset (sources, `/reference`, analyzers, resources, plus the framework's global references), so a `need` in `ofSettings` could only ever repeat it. Keeping it in `ofSettings` instead would have left `Lock.compile` untracked, so `run` is the only place that can hold the single copy. The cost: a bare `csc { ...; resolve }` that is never run now records no `FileDep` -- acceptable because `resolve` reads no file bytes, it only names them; a caller that hashes them (`Lock.record`/`rehash` on a resolved value) should `needFiles` what it hashes (not done here, `Lock.fs` was out of scope for this task). The incremental tests (source edit reruns, untouched build skipped) stay green: 362 passed, 1 skipped, 0 warnings.

**Part 2, investigated, not implemented (behaviour decision).** With neither `targetfwk` nor `NETFX-TARGET`, `frameworkOf` returns a null target framework, so `ofSettings` passes `settings.RefGlobal` only, with no `mscorlib.dll`, no `/nostdlib` and no `/noconfig`: the compiler falls back to the `csc.rsp` next to it. On Windows the .NET Framework `csc.exe` ships a `csc.rsp` with ~38 bare `System*.dll` references resolved from the CLR directory, so it works there. The SDK's `netcore` csc (`csc.dll`, what macOS/Linux run) ships no `csc.rsp`, so nothing references `mscorlib`/`System.Runtime` and the compile fails with missing-type errors (rsp facts read from the Roslyn 4.12.0 toolset package in the NuGet cache; the failure itself not re-run). The compiler's env vars still come from `locateFramework None`, which probes 4.0/3.5/3.0/2.0, so it can even fail to find a framework at all. Smallest fix: in `frameworkOf`, when nothing names a framework, default to the SDK framework of the running `dotnet` (an `sdk-` moniker through the existing `sdkImpl`, so the reference set comes from the SDK's own reference pack instead of `csc.rsp`), then the existing `Some tgt` branch adds `mscorlib`/`/nostdlib`/`/noconfig` unchanged. Open question for the user: this silently changes the meaning of an unqualified `csc` on Windows (Framework's rsp today, the SDK's reference pack after) -- so either default only where the rsp cannot help (`Env.isUnix`, non-Windows), or everywhere; and whether that should instead be an error asking for `targetfwk`.

## B9 (2026-09-29): base docs and samples match the API

`docs/tasks.md` now lists every `csc {}` operation as `Dotnet.csc.fs` defines it, `Csc.compile` (the function `Csc settings` is gone, one line says so), `csc { ...; resolve }` / `Csc.ofSettings` / `Csc.run`, the `RunOptions` fields, `CSC_SERVER` in the script-variable table and the four-step precedence (target op, `CSC_SERVER`, env `XAKE_CSC_SERVER`, server) read from `CompilerServer.resolve`. `lock` gets one sentence and a pointer: it lives in `Lock.fs` (`CscLockBuilder`), not in the base builder. The composed-mode snippets of tasks.md (`Csc.compile`, `resolve` + `Csc.run`, `keepalive`, `CSC_SERVER`) were compiled and run in a scratch script. The document the task called `docs/csc-syntax.md` is `docs/features/hermetic-build/csc-syntax.md`; its stale names are fixed (`CscLock.*` -> `Lock.compile`/`record`/`verify`, `Lock.Compilation.ofArgs` -> `Csc.ofArgs`, `Lock.Entry` fields -> `entry.Csc.*`, `CscSettingsType.Lock` and `Csc settings` removed), and the runner's step list is split at the seam: `Lock.compileWith` does restore, missing-compiler message and `$(SourceRevisionId)`; `Csc.run` does the rest (its steps read from the code, including that `RunOptions` has no `Restore` field). The lock sections were not restructured (E5). Not touched, still stale: `csc-server.md` (mentions `RunOptions.Restore`, `CscLock.resolve`, `Compilation.Directory`), `lock-from-settings.md` and other working notes. `samples/features.fsx` and `samples/book/intro.fsx` needed no change (`intro.fsx` already says `Csc.compile` but references 1.1.x packages, which it could not run against). New `samples/toolset.fsx` (`toolset "4.11.0"`, restored from nuget.org on first run) runs on this machine; both it and `fullframework.fsx` produced their exe. Passing `-ll` to a script through `dotnet fsi x.fsx -- -ll Diag` showed no extra output here, so the sample does not mention a verbosity switch.

After B8 (orchestrator): `Lock.record` now `needFiles` what it hashes (references, analyzers,
compiler, imports), so a record-only target reruns when those change; the compile-side
`needFiles` stays in `Csc.run`. Still stale after B9: `csc-server.md` (`RunOptions.Restore`,
`CscLock.resolve`, `Compilation.Directory`) and `lock-from-settings.md`; E5 moves them anyway.

**Where things stand (2026-09-29, end of the run)**: B1–B5, B7–B9 landed (commits 99faffc,
750dbb1, 646decc, ecc52df); suite 362 passed, 1 skipped; both libraries 0 warnings. Open
before B10 (API polish) can start: the user's approval of §3 principles and the §5 questions
3–7 (Fsproj to hermetic, independent versions, B6 timing, `sign {}` returning a rule, the six
SBOM questions in `nuget-sbom.md`). B8 part 2 (default framework when no `targetfwk`) is also
the user's call. **Exact next step**: get those answers, then B10, then E1–E7.

## B10 (2026-09-30): API polish, §3 renames applied

Done: `Lock.readWith/writeWith/parseWith` -> `Lock.read roots path` / `format roots doc` / `parse roots text`; `Fsproj.parseWith/writeWith` -> `Fsproj.parse/format` (module marked "retired after B6"); `Roots.withExtra` -> `Roots.make`; `Project.packages` -> `Lock.packagesOf`; `Verify.sbomPackageScope(With)` -> `Sbom.checkPackageScope options` (one function, options explicit, the defaulted variant dropped: pure takes everything); `Sbom.defaultPackageScope` -> `Sbom.PackageScopeOptions.Default`; `Pack.defaultOptions` -> `Pack.Options.Default`; `Pack.entries` -> `Pack.list`; `Verify.sha256` removed (callers use `Hash.sha256`; `Csc.sha256` stays, it is the "" convention); `CscImpl` -> `CscBuilder` (nothing referenced it by name). Both `Default`s are now static properties, so `SOURCE_DATE_EPOCH` is read on each access instead of once at module init. Three calls that did not follow the table literally: (1) `Git.tokenize` is **text-level** (`Git.tokenize sha text`, plus `Git.revisionToken`), because `Git.fs` compiles before `Lock.fs` (Lock needs `Git.headSha`), so `Git` cannot name `Lock.Entry`; a whole entry is `entry |> Lock.mapText (Git.tokenize sha)`. (2) `sign {}` returns `Sign.rule settings signer`, and `rule |> delegated (Sign.executor settings signer store budget)` when `store` and `budget` are given (both or neither; no `signer` fails at construction). A bare `sign` now compiles (it shadows FSharp.Core's numeric `sign` where `Xake.Dotnet` is open; the old note in `signing.md` saying it could not was true only while the builder sat in the non-AutoOpen `Sign` module). A variable named like an operation (`signer`, `store`, `budget`) cannot be passed to it inside the block (FS3095), hence `theSigner` etc. in SignTests. (3) `Restore.ensure`/`missing` unchanged: `prepare` was already gone after B2, `Restore.ensure opts request` (reports) + `Lock.restore opts doc` (fails) + `Lock.restoreRequest` are one act each; `Restore` compiles before `Lock`, so it takes a `Restore.Request`, not `doc.Entries`. `Fsproj.parseWith`/`writeWith` survive as internal forwarders only because `DotnetTasksTests.fs` was being edited concurrently; delete them once it calls `parse`/`format`. `api.md` is a dated snapshot and keeps the old names (a note at the top says so); `tracker.md`, `conceptual-review.md`, older sections here and the §3 table keep them as history. `FscImpl`/`ResgenImpl`/`MsbuildImpl` (base, 3.3) not renamed. 364 passed, 1 skipped (+1 `sign {}` shape test, +1 from the concurrent DotnetTasksTests work), 0 warnings.

## E5 (2026-09-30): product docs for `Xake.Hermetic.Dotnet`

`docs/hermetic/` now holds the package's documentation: `README.md` (index, 0.1.0 preview, the two `csc { lock }` / `Lock.build` entry points), `lock.md` (new: the lock format, the `Lock.*` API table, `Project.import`, `csc { lock }`, extracted from the old `csc-syntax.md`), and `restore.md`, `nuget-sbom.md`, `verify.md`, `pack.md`, `strongname.md`, `signing.md` moved with `git mv`. The base half of `csc-syntax.md` (composed mode, compiler sources, `resolve`, `Csc.run` and its steps) is `docs/csc-syntax.md`; `csc-server.md` moved to `docs/csc-server.md` and lost its stale names (`RunOptions.Restore`, `CscLock.resolve`, `Compilation.Directory`, `Entry.Args`; it now names `CSC_SERVER` and `CompilerServer option`). `signing.md` §5 (skeleton plan, acceptance criteria) is `signing-skeleton.md` here. Every fsharp example that runs opens `Xake.Hermetic.Dotnet` next to `Xake.Dotnet`; signature listings do not. Names were read from `src/hermetic/*.fs` and `src/dotnet/*.fs`: `restore.md` claimed `RunOptions.Restore` and `Restore.prepare` (now `Lock.Options.Restore` and `Lock.restore`), `Restore.missing` takes a `Restore.Request` (`Lock.restoreRequest`), `csc { toolset }` restores through `DotNetFwk.restorePackage`, the SBOM root hash is `Csc.sha256` and its in-memory paths are `entry.Csc.*` / `entry.Packages`. Older notes (session history, tracker, conceptual-review) still say bare `restore.md`, `csc-syntax.md` and so on; the folder `README.md` maps them. Not touched, not Markdown: `samples/hermetic/**/*.txt` mention `docs/features/hermetic-build/csc-syntax.md` and `import-page.fsx`; `api.md` is a dated snapshot with old names. **Exact next step**: E6 build scripts once E1-E4 land, then E7/E8.

## E1–E4 (2026-09-30): `Xake.Hermetic.Dotnet` is its own project and package

Moved with `git mv` from `src/dotnet` to `src/hermetic`: Json, Roots, Nuget, Restore, Fsproj, Git, Lock, Project, Sbom, Verify, StrongName, Pack, Sign (that compile order; the compiler accepted it as listed in the plan with Restore ahead of Fsproj). Tests moved to `src/hermetic.tests/`: ProjectImport, LockDiff, FromLock, CscLock, Nuget, Restore, Sbom, Verify, StrongName, Pack, Sign. Results: both libraries and both test projects 0 warnings; src/tests 251 passed 1 skipped, hermetic.tests 113 passed (= 364/1); Integration 11 + 24 = 35; `the repo-a verification runner` from a temp copy with a `.bootstrap` of the three new dlls: 36/36 identical, 36/36 deterministic, 12/12 SBOMs, 36/36 from a fresh folder.

Judgment calls:
- **Namespace opens**: every moved file is `namespace Xake.Hermetic.Dotnet`; `open Xake.Dotnet` added only to the 9 files that name base types (Json, Nuget, Git, Pack have none). `Lock.Package` pointed at `Xake.Dotnet.Package` fully qualified and now names `Xake.Hermetic.Dotnet.Package` (it lives in Restore.fs). Two tool-identity strings changed with the namespace: nupkg `lastModifiedBy` is `Xake.Hermetic.Dotnet.Pack`, the fake signer's field is `Xake.Hermetic.Dotnet.Sign.fakeSigner`. **Not changed, flag for the user**: `Sbom`'s tool component is still named `"Xake"` but its version comes from `GetExecutingAssembly()`, which is now Xake.Hermetic.Dotnet (0.1.0), not the Xake version.
- **Core reference**: the claim holds -- without `src/core` (`PrivateAssets=all`) the build fails with `Xake.Tasks`/`Recipe` undefined, because Xake.Dotnet's own core reference does not flow.
- **Version mechanism**: there is no `VERSION` file; src/dotnet has no `<Version>` at all and gets it from `/p:Version` (build.fsx, publish.yml). The new project has `<Version>0.1.0</Version>` so a bare `dotnet pack` is right; `/p:Version` still overrides it.
- **Dependency range trick**: `dotnet pack` writes the ProjectReference as `Xake >= 1.0.0` (Xake.Dotnet's PackageVersion, unset) -- and with a global `/p:Version=X` it would write X, the hermetic version, since the global property flows into the reference. The target `XakeDependencyRange` (`AfterTargets="_GetProjectReferenceVersions"`) sets `ProjectVersion` of the Xake.Dotnet item to `$(XakeDependencyRange)` = `[3.4.0, 4.0)`; NuGet parses it as a range and writes `[3.4.0, 4.0.0)`. Checked with and without `/p:Version=0.1.0.7`. It relies on NuGet's private item name `_ProjectReferencesWithVersions` (same in SDK 8.0.425 and 10.0.401); if a future SDK renames it the nuspec silently falls back to `>= 1.0.0`, so E6/E7 should assert the range on the packed nuspec.
- **E3**: the only internal the new assembly needed from the base was `DotNetFwk.restoreProjectText` (used by `Restore.projectText`); made public. IVT on Xake.Hermetic.Dotnet for **both** `hermetic.tests` (parsers, PE `Layout`, `Json`, `withProjectLock`, ...) and `tests`, because `DotnetTasksTests` in src/tests uses internal `Fsproj.parseEvaluation`/`format`.
- **Tests that needed changing**: the moved tests used base/engine internals no longer visible to the new assembly: `Impl.endsWith` -> `String.EndsWith(..., OrdinalIgnoreCase)` (FromLock, Restore, StrongName), `DotNetFwk.sdkImpl.dotnetRoot` -> public `DotNetFwk.dotnetRoot` (FromLock), `ProcessExec.pexecSync` -> a plain `System.Diagnostics.Process` (StrongName). No IVT added to core or Xake.Dotnet. Every test file got `open Xake.Hermetic.Dotnet`; `open Xake.Dotnet.Sbom` -> `open Xake.Hermetic.Dotnet.Sbom`.
- **Staying tests use hermetic names**: DotnetTasks (Fsproj, Roots), Toolset and Resx (`Project.import`, `Lock.*`), CscServer (`Verify.compare`) stay in src/tests as instructed, so src/tests now also references src/hermetic. Moving or rewriting them would give the base suite independence; not done.
- **Shared helpers linked**, not copied: `Common.fs` and `XakeTestBase.fs` via `<Compile Include="..\tests\..." Link=...>`; the hermetic tests' scratch folders land in `src/tests/~testout~` (the linked file's `__SOURCE_DIRECTORY__`), already gitignored. No test data files existed (no `Content`/`None` items).
- `xake.sln` lists only core and tests (not even src/dotnet), left alone. The four working fsx scripts here (`import.fsx`, `import-page.fsx`, `the repo-a verification script`, `verify-shipped.fsx`) got `#r ".bootstrap/Xake.Hermetic.Dotnet.dll"` + `open Xake.Hermetic.Dotnet`; the repo's `.bootstrap/` is not re-staged (still the old two dlls), so they need a re-stage before running from here.

**Exact next step**: E6 (build.fsx third library, non-overlapping pack targets; build.fsc.fsx), then E7.

## E1-E4 follow-up (2026-09-30): base tests independent of `Xake.Hermetic.Dotnet`

`src/tests/tests.fsproj` no longer references `src/hermetic`, and the hermetic fsproj's `InternalsVisibleTo("tests")` is gone (only `hermetic.tests` remains). Moved to `src/hermetic.tests`: the `Fsproj` evaluation test from DotnetTasksTests (`FsprojTests.fs`), the two `Project.import` cases of ToolsetTests (`ToolsetImportTests.fs`), and the `Project.import` + `Lock.compile` resx byte-identity case (`ResxImportTests.fs`). Stayed in base: the composed `csc { toolset }` test (already `csc {}` only), the two Resx reader tests, CscServerTests (`Verify.compare` did no masking, so it is now a plain `File.ReadAllBytes` equality). Added one base test, `Resx.compile writes the same bytes as msbuild` (msbuild baseline vs `Resx.compile`, no lock), so B7's byte identity is checked without the hermetic package. Counts: base 248 passed 1 skipped, hermetic.tests 117 = 365/1 (364 + the new base resx test). `Verify.fs`'s doc comment still says `InternalsVisibleTo("tests")` (file outside this task's scope).

SBOM: `metadata.tools.components[0]` is now named `Xake.Hermetic.Dotnet` (version from the same assembly as before). SBOM output changed in this field only. There is no `vendor` field in the emitted entry, so nothing to keep. SbomTests asserts the name.

## E6–E7 (2026-09-30): build scripts and CI for two packages

**build.fsx.** `Library` gained `Package` (which package, hence which version var); the third
library is `Xake.Hermetic.Dotnet` (`src/hermetic`, needs Xake + Xake.Dotnet). Second var
`HermeticVersion` (`-d HermeticVersion=`, `$HERMETIC_VERSION`, the field-name-to-env rule of
`Var.create`), defaulting to the literal `<Version>` in the fsproj (regex, so the two cannot
drift). Three decisions:
- **`dotnet build -p:BuildProjectReferences=false`** for every library. A global `/p:Version`
  flows into project references: building src/hermetic at 0.1.0 would rebuild Xake and
  Xake.Dotnet at 0.1.0 and copy them over `out/`. Each library `need`s its references first, so
  msbuild never has to build them.
- **Pack layout `out/pkg/<id>/<id>.<ver>.nupkg`, one rule per package** (both, not either): a
  single `out/Xake.(ver:*).nupkg` matches `Xake.Hermetic.Dotnet.0.1.0.nupkg` with
  ver=`Hermetic.Dotnet.0.1.0`, and the per-id folder is also what lets CI push
  `out/pkg/<id>/...` without the other package. `pack` needs both in one `need` (the engine in
  build.fsx's bootstrap is 3.0.1, which re-runs a target asked for twice in sequence -- the
  fixed "one target, one execution" bug -- so one list, not two `need`s).
- **The hermetic pack compiles against Xake at Xake's version**: it `need`s the Xake nupkg
  (which also serializes the two packs, they share `src/core/obj` and `src/dotnet/obj`), runs
  `dotnet build src/dotnet -c Release /p:Version=<Version>`, then
  `dotnet pack src/hermetic /p:Version=<HermeticVersion> -p:BuildProjectReferences=false`.
  Checked with PEReader: `Xake.Hermetic.Dotnet 0.1.0.0 -> Xake 3.4.0.0, Xake.Dotnet 3.4.0.0` for
  `-d Version=3.4.0-rc1 -d HermeticVersion=0.1.0-rc1`. Without it the hermetic assembly would
  reference Xake.Dotnet at *its own* version.
- **Nuspec assertion**: after pack, `checkXakeRange` opens the nupkg (`ZipFile`), reads the
  root `.nuspec`, requires every `<dependency id="Xake">` to be `[3.4.0, 4.0.0)`; otherwise it
  deletes the nupkg and fails. Negative test on a hand-edited nupkg (`1.0.0`): both TFM groups
  reported.

Runs (SDK 10.0.401, working tree with the concurrent test moves): `build test` green,
src/tests 248 passed 1 skipped, hermetic.tests 117 passed; `pack` (defaults) ->
`out/pkg/Xake/Xake.0.0.1.nupkg`, `out/pkg/Xake.Hermetic.Dotnet/Xake.Hermetic.Dotnet.0.1.0.nupkg`;
`VERSION=3.4.0.99 HERMETIC_VERSION=0.1.0.99 ... pack` -> `Xake.3.4.0.99.nupkg`,
`Xake.Hermetic.Dotnet.0.1.0.99.nupkg`. Hermetic nupkg holds only its own dll/xml per TFM.

**build.fsc.fsx.** Third library, `#r ".bootstrap/Xake.Hermetic.Dotnet.dll"`, `open
Xake.Hermetic.Dotnet` (`Fsproj`), `versionOf name` for the evaluation's `Version`, `test` runs
both projects, and the same pack/push rules as build.fsx. `.bootstrap/` re-staged from
`build.fsx build` (`cp out/netstandard2.0/*.dll .bootstrap/`, FSharp.Core.dll kept): Xake.dll
589312, Xake.Dotnet.dll 219648, Xake.Hermetic.Dotnet.dll 400384 bytes, the same sizes as the
E1–E4 staging.

**Trap found: `Fsproj.evaluate` on a project with two ProjectReferences.** With `-restore`,
msbuild's `-getItem` reports `FullPath` against the *current directory*: src/hermetic's sources
came back as `<repo>/Json.fs`, its project references as `/Users/olegz/projects/dotnet/...`
(untokenized, machine-specific). Reproduced by hand on SDK 8.0.425 and 10.0.401; without
`-restore` the paths are right; removing either ProjectReference makes them right; static-graph
restore crashes (NRE in `MSBuildStaticGraphRestore`) with the global `TargetFramework`;
`RestoreRecursive=false`/`RestoreDisableParallel` do not help. src/ was off limits for this run,
so build.fsc.fsx carries `repairEvaluation`: a Sources/ProjectRefs entry that does not exist is
re-rooted under the project directory, in place in the text, so the kept json is what
`evaluate` would write without the defect (16 paths for src/hermetic, 0 for the other two). The
real fix is in `Fsproj.evaluate`: run `-t:Restore` as its own msbuild call, then the
`-getItem` call without `-restore`; it has to land before 0.1.0 is published (tracker).

**fsc build proof.** `rm -rf out .xake && dotnet fsi build.fsc.fsx -- -- build`: 3 evaluations
+ 3 compilations in 5.8 s, `projects/netstandard2.0/Xake.Hermetic.Dotnet.json` new and
`Xake.Dotnet.json` smaller (the moved files; both are tracked outputs of this script, left in the
working tree); second run all skipped, 0.7 s, no msbuild; clean rebuild byte-identical to the
first (deterministic). `build test` via build.fsc.fsx: 248+1 / 117. **Against `dotnet build`
it is not byte-identical, and was not before this change**: every dll is 512 bytes smaller and
`verify-shipped.fsx` says "content differs" (Xake.dll 5817 ranges / 454 KB, Xake.Dotnet 2594 /
152 KB, Xake.Hermetic.Dotnet 5445 / 274 KB); the `.xml` docs are identical. Xake.dll's sources
did not change, so this is the fsc command line (the SDK passes relative source paths,
`--highentropyva+`, `--targetprofile:netstandard`, its own pdb path; F# embeds file names in its
signature/optimization resources), not E6. B6's "byte-identical" is against the fsc build's own
earlier output.

**E7.** `publish.yml`: tags `v*` and `hermetic-v*`; a `case` on the prefix outputs `package`,
`variable` (`VERSION` / `HERMETIC_VERSION`) and `version` = tag minus prefix + `.<run>`, anything
else fails the job; `build test pack` with that variable exported; push
`out/pkg/<package>/<package>.<version>.nupkg --skip-duplicate`. The other package still packs
at its default version and is not pushed; in a hermetic release that means Xake packs at 0.0.1
and the hermetic assembly references `Xake 0.0.1.0` (below any published 3.x, so it binds;
set `VERSION` too if that matters). `build.yml` runs `build test pack` on 8.0.x and 10.0.x, so
the range assertion runs on the floor SDK as well. YAML parsed with Ruby's `YAML` (no PyYAML,
no actionlint here); the tag step was run locally for `v3.4.0`, `hermetic-v0.1.0`, `foo`.
Not run: the workflows themselves.

**Exact next step**: fix `Fsproj.evaluate` (separate restore call) and drop `repairEvaluation`;
then release Xake 3.4.0 (tag `v3.4.0`) and Xake.Hermetic.Dotnet 0.1.0 (tag `hermetic-v0.1.0`),
then E8 (extraction-plan.md §4 lists the exact `#r` lines).

### Fsproj.evaluate fixed (2026-09-30, later)

Root cause as observed: `dotnet msbuild -restore ... -getItem` on src/hermetic (two
ProjectReferences) reports `FullPath` against the current directory (`/private/tmp/repro/Json.fs`
when run from /tmp/repro; the project-reference paths likewise), reproduced on SDK 10.0.401.
Fix: `Fsproj.evaluate` runs `dotnet msbuild <proj> -t:Restore <same -p: properties>` first, then
the unchanged item query without `-restore`; by hand and through `build.fsc.fsx` the paths are
project-relative and tokenized. `repairEvaluation` is gone from `build.fsc.fsx`; from clean
(`rm -rf out obj/xake src/hermetic/obj/xake projects/netstandard2.0/*.json`)
`dotnet fsi build.fsc.fsx -- -- build test` is green and `git diff --stat projects/` is empty.
New test in FsprojTests.fs (Main with two sibling ProjectReferences, cwd elsewhere, fails
without the fix): hermetic.tests 118 passed, `dotnet build src/hermetic -c Release` 0 warnings.
**Trap**: `build.fsc.fsx` runs against `.bootstrap/Xake.Hermetic.Dotnet.dll` (ignored, frozen);
the fix only takes effect there after `cp src/hermetic/bin/Release/netstandard2.0/Xake.Hermetic.Dotnet.dll .bootstrap/`
(done locally). publish.yml: the `hermetic-v*` build step also exports `VERSION=3.4.0`.
**Exact next step**: release Xake 3.4.0 (tag `v3.4.0`) and Xake.Hermetic.Dotnet 0.1.0 (tag
`hermetic-v0.1.0`), then E8.

## 2026-09-30 — review stack opened on OlegZee/Xake (the working repo; upstream FakeBuild lags)

The user wants per-topic PRs reviewed one by one, then a release, then the rest. The branch
history stays here; each PR is one commit of final file states, stacked, draft:
#17 → #18 → #19 engine fixes (one per commit) → #20 `pr/base-csc` (src/dotnet, base tests,
base docs) → #21 `pr/hermetic-a-lock` → #22 `pr/hermetic-b-sbom-verify` (Pack moved here: SBOM
tests need `Pack.nupkg`) → #23 `pr/hermetic-c-pack-sign` → `pr/build-ci` → `pr/hermetic-docs`
(two new documents, `docs/hermetic-architecture.md` and `docs/hermetic-guide.md`). Fixes found
while writing the docs went into the stack and were cherry-picked here (850f6ce): `targetfwk`
message example, `needFiles` before the hash check in `Csc.run` (plus the missing-file report
before it, via `ExecCore.locateRule` through IVT), `Csc.managedCompiler` (csc.dll instead of
the launcher), `Sbom.toolName`. Working notes and `samples/hermetic` stay on this branch only.

**Procedure after each merge into dev** (squash): the next PR is retargeted by GitHub; merge
`dev` back into this branch to keep history aligned. Release order: merge through #20, tag
`v3.4.0`; merge the rest, tag `hermetic-v0.1.0`; then E8 (bootstrap bump) as one more PR.
The PR worktree is `.claude/worktrees/pr-engine`.

## 2026-10-01 — #17–#19 merged by the user; hermetic goes on the released Xake

The user merged the engine PRs and rebased the rest of the stack himself (squash + stack:
after each merge the next branch must be rebased onto `dev`, the ones above onto it; rebase and
force-push are denied to the assistant by the session's safety rules, merges are allowed).
Decision: `Xake.Hermetic.Dotnet` stays in this repo (`src/hermetic`) but references the
**published** `Xake` package (`PackageReference [3.4.0, 4.0)`), not `src/dotnet`. So #21 is
reworked after `v3.4.0` is on nuget.org (the published version is `3.4.0.<run>`; the range
covers it); #24 loses the range check and `BuildProjectReferences=false`. During development a
local feed (`out/pkg`) in a `nuget.config` can stand in for nuget.org.
**Exact next step**: the user reviews/merges #20 and tags `v3.4.0`; then push the prepared
PackageReference rework to `pr/hermetic-a-lock`, rebase #22–#25, and bump `build.fsx` to
`#r "nuget: Xake, 3.4.0"`.

## 2026-10-01 — released

Both packages are public: `Xake 3.4.0.21` and `Xake.Hermetic.Dotnet 0.1.0.22` (publish appends the
run number; dependency ranges and `#r` lines use the exact published version to avoid NU1603).
All nine PRs are merged into `dev` with merge commits; `dev` is merged back here (4607e38).
`src/hermetic` depends on the published `Xake` package (`[3.4.0.21, 4.0)`), never on `src/dotnet`.
**In flight**: E8 as a PR to `dev` (`build.fsc.fsx` on the packages, `.bootstrap/` retired) and the
feature fsx scripts here. **Then**: the backlog in the tracker; B6 is the big one, the SBOM
questions need the user.

## 2026-10-02 — scenarios reviewed, four PRs to dev

`docs/hermetic/workflows.md` (developer / import / teammate / CI / release / offline, with gaps)
and `docs/roadmap.md` (rules with arguments idea, B6, hermetic gaps) written here and sent to
`dev` in #28. The gaps became PRs: #29 reference packs through `DotNetFwk.restorePackage`
(base; hermetic sees it after the next Xake release), #30 `Lock.Options.RecordMissing` (off
when `CI` is set, `LOCK_RECORD_MISSING` overrides), `packageroot`/`norestore` after `lock`,
tokenized drift messages, #31 sha512 check for present packages. E8 (#27) merged: both build
scripts bootstrap from nuget.org, `.bootstrap/` retired; the feature fsx scripts too.
**Exact next step**: after the user merges #28–#31, merge `dev` here, fix the two stale Gaps
lines in `workflows.md`, then start B6 from fresh `dev`: design note first (`Fsc` record,
hashing `fsc.dll` from the SDK, `--` argument form, shared runner core with `Csc.run`).

## 2026-10-04 — B6 base PRs, review on #32

Design approved (`fsc-design.md` §10). #32 `pr/fsc-record` (Fsc record, FscArgs, resolve,
`Fsc.compile`) and #33 `pr/fsc-runner` (CompilerRunner.RunPlan as data, `Fsc.run`) are open.
Review on #32 reversed the 2026-09-30 "targetfwk required" decision: **`targetfwk` is optional,
default = the probed SDK's own `netN.0`** (from `Microsoft.NETCoreSdk.BundledVersions.props`),
for csc and fsc alike; composed mode now compiles for `netN.0` with references from the SDK's
`packs/Microsoft.NETCore.App.Ref` (no download). fsc on `netN.0` references the SDK's
FSharp.Core unless `ref` names one. `samples/features.fsx` runs on macOS with just the SDK.
Tests: 271/1 on #32, 274/1 on #33. **Next**: the user merges #32, #33, releases Xake 3.5;
then hermetic PRs D (union in `Lock.Entry`, `fsc { lock }`, fsproj import) and E
(`build.fsc.fsx` on one imported lock, `Fsproj` deleted), release 0.2 on `[3.5.0.<run>, 4.0)`.

## 2026-10-04 — Xake 3.5 released; hermetic PR D in flight

#32 and #33 merged (merge commits), `v3.5.0` → `Xake 3.5.0.23` on nuget.org. Along the way:
`targetfwk` optional with the SDK default (review on #32), composed `netN.0` support from the
SDK's targeting pack, fsc for .NET Framework from any host (implicit `netstandard.dll` facade:
the SDK's net461 one for net462 and below, the reference pack's `Facades/` for net471+; the
`NETStandard.Library` ref stub is NOT a facade and breaks on `lazy`/attributes), default
`FSharp.Core` package (`FSHARP_CORE_VERSION`). `dev` merged here (7f68499).
**In flight**: PR D `pr/hermetic-fsc` (0.2 on `[3.5.0.23, 4.0)`, union `Compilation` in
`Lock.Entry`, `fsc { lock }`, fsproj import, `Fsproj` deleted). **Then** PR E: `build.fsc.fsx`
on one imported lock, then `hermetic-v0.2.0`.

## 2026-10-07 — HERMETIC mode decided; review stack in flight

Later the same day: `NUGET_RESTORE` renamed to **`NUGET_FETCH=on|off`** on the user's decision
("restore" suggested a project restore, which only happens at import; the switch governs
`DotNetFwk.downloadPackages`, the shim `PackageDownload` project; `NORESTORE=true|false`
rejected as a negative boolean keeping the misleading word). **#38 squash-merged to `dev`**
(6c20185). Follow-up for hermetic 0.2.x: `norestore` after `lock` → `nofetch`,
`Restore.Options.Enabled` reads `NUGET_FETCH`; `docs/hermetic/*`, `src/hermetic/Restore.fs`
still say "restore off". #37 rebuilt on `dev` as one commit (c43c12e: the package folder threaded through
`netcoreRefDir`/`locateOrRestore`/`packageDir`; the pinned-packs test's two `File.Delete` split
into separate `try`s — the first threw on a missing `pinned/` and the database was never
cleared) and **merged by the user (4ef64e1)**. The review queue is empty.
**Next**: `HERMETIC=on` code per `hermetic-mode.md` (after the user reads the note), then the
hermetic 0.2.x follow-ups (`nofetch`, `Restore.Options.Enabled` on `NUGET_FETCH`, hermetic
reading `DotNetFwk.packageRoot ()`), PR E (`build.fsc.fsx` on one imported lock), `hermetic-v0.2.0`.

Open PRs to dev: #34 (hermetic 0.2 on Xake 3.5, F# in the lock), #35 (runtimeconfig only as a
declared multi-file target, the user's model), #36 (book sample, superseded by #35), #37
(targeting packs restored from NuGet; `NETCORE_REF_VERSION` as a list, one per major.minor, pinned
TFMs always from NuGet; the `NETCORE_REF` switch was tried and dropped), #38 (`NUGET_PACKAGES` and
`NUGET_RESTORE` as script variables). #37/#38 conflict in `DotNetFwk.fs`; fix after the first merge.
Found: the released hermetic 0.1.0.22 fails against the new base (`Csc` gained `RuntimeConfig`):
F# records are not binary-compatible; proposed minor-range dependency (roadmap).
**Decided**: a global `HERMETIC=on` mode, independent of `CI`: no path outside `$(ProjectRoot)` /
`$(NuGetPackageRoot)` except declared **prerequisites** (today: the SDK by exact pin, which is the
fsc compromise level); requires `NUGET_PACKAGES`, `CSC_TOOLSET`, a `NETCORE_REF_VERSION` entry per
`netN.0` target used; does not touch restore or the compiler server. Design note
`hermetic-mode.md` and a key section in `workflows-ru.md` in flight; code after the user reads
the note. Lesson recorded in memory: a question is a prompt to think, not to change a PR.

## 2026-10-09 — HERMETIC mode PRs

Decisions: prerequisites written always; `packageroot` removed; `norestore` → `nofetch`;
only the script variable `NUGET_PACKAGES` satisfies the mode; imports may use an exact SDK pin or
a Toolset PackageReference in the project (deferred). Open PRs: #39 (base: netstandard2.1 and the
net461 facade from byte-identical packages, FSharp.Core package for netN.0 under
`FSHARP_CORE_VERSION`), #40 stacked on #39 (base: `HERMETIC`, `HermeticMode.check/enforce`,
`DotNetFwk.sdkPrerequisite/globalJsonPin/packageRootWithSource`), #41 (hermetic 0.2:
`Entry.Prerequisites`, replay check, `nofetch`). **Next**: merge #39, #40 → Xake 3.6; merge #41;
wire the hermetic side to the 3.6 helpers (lock-side gate, messages 9–11, base docs cleanup of
`packageroot`/`norestore`); PR E (`build.fsc.fsx` on one imported lock); `hermetic-v0.2.0`.

## 2026-10-11 — Xake 3.6 and Xake.Hermetic.Dotnet 0.2 released

`v3.6.0` → `Xake 3.6.0.24`; `hermetic-v0.2.0` → `Xake.Hermetic.Dotnet 0.2.0.25` (depends on
`Xake [3.6.0.24, 3.7)`, the minor-range rule). 0.2: F# in the lock, `fsc { lock }`, fsproj import,
`Entry.Prerequisites` (dotnet-sdk, always written), `nofetch`, no `packageroot`, the lock-side
`HERMETIC=on` gate, base helpers wired, `docs/hermetic/hermetic-mode.md` as a product page.
**In flight**: #42 (`build.fsc.fsx` on one imported lock `locks/xake.json`, exact SDK pin
10.0.401 in `global.json`, CI legs `pinned` + `floor`, Xake itself builds under `HERMETIC=on`),
being finished on the published packages. **After that**: the backlog in the roadmap: netN.0
imports under the mode (`packs/`), SDK restore, F# compiler toolset, the six SBOM questions,
`sign` shadowing, English docs simplification (nine items from workflows-ru).

## 2026-10-11 (later) — build.fsc.fsx rework: interview done, brief written

Session run as orchestrator + subagents per `next-session.md`. Working files in
[fsc-rework/](fsc-rework/): `digest.md` (what PR #42's script does, its boilerplate),
`interview-plan.md`, **`interview.md`** (every answer and decision, authoritative),
`drafts/` (desired one-screen `build.fsc.fsx`, `trial-build.fsx`, description schema + example,
`gaps.md`), `review.md` (independent review on Fable), **`brief.md`** (decisions, desired
scripts, gaps, PR plan, open topics).

Key decisions (details in interview.md):
- Fork: **now** Xake's own declarative build on the fsc/csc tasks: (a) script + per-project
  descriptions (`xake.project.json`, data, JSON with schema to start), (b) lock generation,
  (c) build from locks. **Later, separately**: hermetic builds of existing msbuild projects.
  Lock format and build-from-lock recipes shared; the lock format does not change.
- fsproj stays primary; import (msbuild, SDK OK) and lock generation are explicit commands,
  never on CI; build uses committed files and only warns when they are stale. Next step: a
  `cached` rule decorator (pipe) keyed by declared inputs, fingerprints in one committed file,
  one line per target. Frozen mode only as a general "rules over rules" mechanism.
- Byte identity with `dotnet build` is not a goal. Package resolution stays with msbuild (not
  inside fsc/csc). C# sources kept as globs, expanded at build via Xake's file mask.
- Pack: nupkg from layer (c) binaries via `Pack.nupkg`, metadata in the description with script
  overrides; `dotnet test` stays; nupkg compared by binary content; assembly versioning open.
- No publishing to nuget.org during the work: a local shared folder is publish target and
  local feed; restore from nuget.org stays; `HERMETIC` and `NUGET_FETCH` are independent.
- Targets carry no data-processing code; Debug/Release in the target mask (per-config rules
  allowed, Debug not for every library); output layout variant A (`outOf cfg fwk name`), the
  lock of A records the chosen config/TFM of each referenced project; Layout variant B (one
  mask, typing, type providers) later.
- Brands: target prefixes (paths, `clean::acme`), script knowledge; import per brand,
  descriptions and locks stored per brand. the trial product experiment: repo-a + page,
  `develop`, Acme + Globex, Debug + Release + basic pack; judged by clarity and core use.
- **PR #42 stays on its branch** (not merged, not closed).

**Next**: implementation per `fsc-rework/brief.md`, in a fresh session:
[next-session-2.md](next-session-2.md).

PR / branch / worktree: none open from this session (docs only, on `feature/hermetic-build`).

## 2026-10-11 (implementation session 1) — PR plan started

Orchestrator session per `next-session-2.md`. The user confirmed the PR plan of
`fsc-rework/brief.md` §5 unchanged and the order: PR 1 first, PR 5 right after (branched from
PR 1's branch), then PR 2-4. Subagent reports land in `fsc-rework/pr<N>-report.md`.

| PR | Branch | Worktree | Base | Status |
|---|---|---|---|---|
| 1 description model | `pr/description-model` | `.claude/worktrees/pr/description-model` | `origin/dev` bb0e990 | **merged** (squash, 4b93f3b); worktree/branch removed 2026-10-12 on the user's side; report `fsc-rework/pr1-report.md` |
| 5 import -> description | `pr/project-import` | `.claude/worktrees/pr/project-import` | `pr/description-model` 15eb8f0 (stacked on #44) | **merged** (squash, d82263c); removed 2026-10-12; report `fsc-rework/pr5-report.md` |
| 2 `fsc/csc { project d }` + `config` + packages | `pr/csc-project` | `.claude/worktrees/pr/csc-project` | `pr/description-model` 15eb8f0 (stacked on #44) | **merged** (squash, 867f362); removed 2026-10-12; report `fsc-rework/pr2-report.md` |
| 3 `Lock.generateAll` | `pr/lock-generate` | `.claude/worktrees/pr/lock-generate` | `pr/csc-project` f450a1b (stacked on #46) | **merged** (squash, 9d54e99) as the phony-rule rework (`Locks.generate`, `lock::(name:*)::(cfg:*)` + `update-locks`); removed 2026-10-12; research `fsc-rework/engine-locks-rule.md`; report `fsc-rework/pr3-report.md` |
| 4 build from a lock into a layout | `pr/build-from-lock` | `.claude/worktrees/pr/build-from-lock` | `pr/lock-generate` 434743c (stacked on #48) | **merged** (squash, 8b1a76a); removed 2026-10-12; report `fsc-rework/pr4-report.md` |
| 6b core fixes (Fingerprints concurrency, warn level) | `pr/fingerprints-fixes` | `.claude/worktrees/pr/fingerprints-fixes` | `origin/dev` 8b1a76a | **merged** (#51, 4ef2579); removed 2026-10-12 |
| 6c library PR before the release (fsc identity by SDK, `compileAs` restore-order fix, net462 reference, 59-byte note) | `pr/fsc-identity` | `.claude/worktrees/pr/fsc-identity` | `origin/dev` 8b1a76a | **merged** (#52, 35cfe56); removed 2026-10-12 |
| 6d hermetic 0.3.0 on Xake 3.7.0.26 | `pr/hermetic-0.3` | `.claude/worktrees/pr/hermetic-0.3` | `origin/dev` 35cfe56 | **merged** (#53, 7e5e29f); removed 2026-10-12 |
| 7 self-build `build.fsc.fsx` | `pr/build-fsc` | `.claude/worktrees/pr/build-fsc` | `origin/dev` 8b1a76a | **merged** (#50); bootstraps from Xake 3.7.0.26 + hermetic 0.3.0.28, own `.xake.fsc`, locks regenerated, CI job `build-fsc` on ubuntu; report `fsc-rework/pr7-report.md`; six new questions in `questions.md` (bootstrap via build.fsx dlls, macOS-only locks, HERMETIC off, 59-byte cross-folder diff) |
| #82 rm/cp many targets | `pr/rm-multi-targets` | `.claude/worktrees/pr/rm-multi-targets` | `origin/dev` 5e08a38 | **merged** 2026-10-21 (62d9b1a) |
| #83 SDK-free locks (hermetic 0.6.0) | `pr/sdk-free-locks` | `.claude/worktrees/pr/sdk-free-locks` | `origin/dev` 62d9b1a | open, draft; findings in `sdk-free-locks.md` |

Merged: #44-#49 (all of PRs 1-6 minus the fingerprints wiring, which PR 7 does in the script). After every squash merge the
remaining stacked branches need `git merge origin/dev` (the squash commit conflicts with the
original commits on `Compile Include` lines and add/add docs; content is identical, take the
branch side), plus a fix where `dev` moved under the fixtures (e.g. a new core source file). After each merge: hand the user the removal command for that worktree and branch,
then merge `dev` into the remaining stacked branches (no rebase). Stacked branches will show the
squashed commits as already-applied content; `git merge dev` resolves cleanly in the normal case.

Open questions from all six subagents, grouped per PR: [fsc-rework/questions.md](fsc-rework/questions.md)
(none decided; the code follows the provisional choice named there).

**Release log**: `v3.7.0` tagged 2026-10-12, publish run 38215257067 succeeded, nuget.org version `3.7.0.26`. `hermetic-v0.3.0` tagged 2026-10-12 (first attempt had a trailing comma in the tag name and failed; retagged), run 38307870186 succeeded, version `0.3.0.28`.

**Next (updated 2026-10-12, later)**: intermediate release sequence confirmed by the user: merge #51 and the `pr/fsc-identity` PR -> tags `v3.7.0` then (hermetic `<Version>` 0.3.0) `hermetic-v0.3.0` (user's action) -> rework #50 to `#r nuget` the released packages, regenerate the locks (now OS-portable), drop the CI bootstrap step, CI on ubuntu. Earlier: #50 under review; PR 8 (pack) can start from `dev` after #50, but its
nupkg-equality acceptance depends on PR 7 q6 (cross-folder 59-byte diff) and the versioning answers.
Earlier plan: (1) the user answers `fsc-rework/questions.md` and reviews/merges the stack; (2) then
PR 7 (`build.fsc.fsx` per the draft + committed descriptions, locks, fingerprints, wiring of
`Fingerprints.record` into `import`/`locks`) on a branch from `dev` once #44-#49, #45, #47 are in;
(3) PR 8 pack can start from `pr/build-from-lock` any time, but better after the answers on
metadata (PR 5 q4) and versioning (PR 2 q5, PR 4 q1).
| 6 fingerprints library (core) | `pr/fingerprints` | `.claude/worktrees/pr/fingerprints` | `origin/dev` bb0e990 | **merged** (squash, c90ef1d); wiring later; removed 2026-10-12; report `fsc-rework/pr6-report.md` |


Worktree cleanup 2026-10-12: all `pr/*` worktrees and local branches of #44-#53 removed; origin
branches already deleted by the merges. Left: `pr/build-fsc-lock` (#42, kept open by decision).

## 2026-10-12 (later) — the trial product experiment started (plan item 9)

Experiment folder `/path/to/exp-repo/` beside the trial project repos, with detached
worktrees of both repos at `origin/develop` (`repo-a` 25b7e48, `repo-b`
808ef9f2); the user's own checkouts are untouched (repo-a sits on his feature branch). The
subagent writes `build.fsx` there (brand as a prefix of every path and command), imports per
brand, generates Debug + Release locks, builds from the locks, measures against `dotnet build`,
tries a basic pack with `Pack.nupkg`, and reports to `fsc-rework/trial-experiment.md` plus the script
copy `trial-build.fsx` in this folder. Packages: Xake 3.7.0.26, Xake.Hermetic.Dotnet 0.3.0.28.

To remove the experiment worktrees later (user's action):
`git -C /path/to/repo-a worktree remove /path/to/exp-repo/repo-a`
and the same for page.

**Result (2026-10-13)**: `fsc-rework/trial-experiment.md` + `trial-build.fsx`. Both brands, Debug + Release,
all 15 projects (120 dlls) build from committed locks; fresh-clone run with no msbuild; packs
byte-identical; but only with six script workarounds (G1-G7) and the script is 179 lines vs the
draft's 78. Xake clean build 33 s at 3.0 cores vs msbuild 13 s (Debug, 9 cores) / 27 s (Release,
6 cores); no-op 7 s vs 2 s; the `!**/.*/**/*` exclude mask alone costs 1.4 s per expansion.
Thirteen gaps G1-G13 listed in the report with fix sizes. Not an engine point verified: lock
tokenisation outside `$(ProjectRoot)` (G10). Next: the user picks which gaps to fix (library
G1-G7, G12; engine performance G8-G9; hermetic G10-G11; import idempotence G13).

## 2026-10-13 (night) — autonomous run of the fixes plan

The user (going to sleep): run the improvements locally, merge nothing into `dev`, commit as
needed, use local builds for the trial project experiment, a critic subagent judges each change, by morning:
all improvements with measurements, profiles (Gantt and other charts) and conclusions.

Branches/worktrees (all from `origin/dev` 0139682 unless stacked), nothing pushed yet:

| round | branch | worktree | status |
|---|---|---|---|
| C0 stats from `.xake` | (experiment only) | `/path/to/exp-repo/tools/xakestats.fsx` | done: `fsc-rework/trial-stats.md`; db readable by reflection; sequential 27.6 s util 3.5, avg concurrency 1.0 (chain), peak 8; parallel four graphs 218 s, completed (no hang this time), peak 11, 116 of 218 s with no file target active; Excel targets 3 s -> 33 s own under load |
| A1 import/project fidelity (G1-G4, G13) | `pr/import-fidelity` | `.claude/worktrees/pr/import-fidelity` | done (b165cb4): resx via `project`, metadata case, `options.noWarn`, `assemblyAttributes`, re-import clean; 211 tests; two leftovers for B1: stale `.resources` in the runner (workaround `Described.refreshResources`), backslashes in generated source turned to `/` by the lock writer; report `a1-report.md` |
| C1 fileset perf (G8) | `pr/fileset-perf` | `.claude/worktrees/pr/fileset-perf` | done (2b5bf39): `Path.maskToRegex` compiled a `RegexOptions.Compiled` regex per call (~1 ms) for every mask segment per file; now cached per mask string; the trial project Layout masks 1653 ms -> 14 ms, benchmark 1250 -> 7-13 ms; 329 tests; also stops `locateRule` recompiling; report `c1-report.md` |
| G16 hang repro/fix | `pr/pool-hang` | `.claude/worktrees/pr/pool-hang` | done, NOT reproduced (20 cases + 2 stress pass; join cannot deadlock: `withYieldedSlot`); premise wrong: the `.resources` targets had completed, the stuck tasks were two net472 Excel compiles inside `CompilerRunner.run` before `[shell] starting`; hypotheses: a silently dead MailboxProcessor (pool or db agent), something in CompilerRunner; next hang: `dotnet-stack report -p <pid>`; branch holds the repro tests only; report `g16-report.md` |
| C2a rule-match regex cache + observable agent errors | `pr/rule-match-cache` | `.claude/worktrees/pr/rule-match-cache` | done (6d4eea7 + merge 2343ab4 of `pr/fileset-perf`): `locateRule` 30 rules x 5000 calls 94 s -> 0.17 s; trial-project-like 3000 needs 10.1 s -> 0.03 s; `Compiled` kept (noise); pool/db agents handle failures (pool failure fails the build, db store failure logged, kept in memory); 327 tests; critic running; report `c2a-report.md` |
| B1 refs/framework asm/dir (G5-G7, G12, G14) | `pr/lock-fidelity` | `.claude/worktrees/pr/lock-fidelity`, stacked on A1 b165cb4 | done (9bf45d6): transitive refs from refs' locks, `Described.restoreWith descriptionOf` for the direct route, `frameworkAssemblies`, `Directory` = project folder, separate `.xml` rule (two-target form would rebuild forever), runner recompiles `.resources` on change, writer keeps `\` in generated source; 218 + 324 tests; questions: closure helper for the direct route, a Xake 3.7.x patch so `refreshResources` can go, regenerate self-build locks; report `b1-report.md` |
| B2.1 batch restore (G15) | `pr/batch-restore` | `.claude/worktrees/pr/batch-restore`, stacked on B1 9bf45d6 | done (c2b5eb2): per-build restore cache keyed by (fwk, package folder, package set), `Described.prefetch`/`restoreAll`, `Locks.restoreAll`, `needAll` prefetches missing packages, `Described.closure`; self-build update-locks 6 restores -> 1, 2.4 s -> 0.9 s; 222 tests; report `b21-report.md` |
| D1 roots/pathmap, sdk prerequisite (G10, G11) + A1 critic fixes | `pr/lock-roots` | `.claude/worktrees/pr/lock-roots`, stacked on B2.1 c2b5eb2 | done (550bac3): `generateWith/compileAsWith/needAllWith/restoreAllWith roots`, one pathmap per root, HERMETIC treats extra roots as project root; G11 needed no code (test only: generated locks already record the prerequisite; the trial project refused because its pin is `latestFeature` and it uses the SDK csc); A1 critic F1/F3 applied; self-build locks regenerated (`Directory`, net462 `System.Windows.Forms.dll` from the Xake package's framework assemblies); critic-b fix + nits applied; 226 tests; open q: should a generated C# lock's SDK csc be covered by the prerequisite as for imports; report `d1-report.md` |
| critic | - | read-only | core: all SHIP, db guard with fix F1 (atomic record write) -> sent back to the C2a author; A1: all SHIP with fixes: F1 `_` folder -> `__` only for the first folder in the manifest name, F2 backslash in attribute args (done by B1's writer fix), F3 `Link`/`ManifestResourceName` metadata ignored silently -> to apply at the top of the stack (D1) |
| the trial project rerun on local packages | experiment; integration worktree `.claude/worktrees/night-integration` (detached 74bf4d9 = `pr/lock-roots` + `pr/rule-match-cache`, not a branch) | `exp-repo/` | done: `trial-rerun.md`, scripts `trial-build-2.fsx` (124 lines, 92 + pack, zero workarounds) and `trial-direct-2.fsx` (43); clean acme Release 33.4 -> 6.9 s (util 8.3), no-op 7.1 -> 0.9 s, four configs `<==` 224 -> 19.7 s (util 14.4), fresh clone 141 -> 26.2 s, direct 57 -> 8.5 s, update-locks 60 locks 3.5 s; msbuild 24 s / 80 s sequential (its concurrent run is invalid: shared bin/obj fail); Gantt: critical path = wall, peak 36, mean 16; no hang in 9 runs; still open: HERMETIC on generated C# locks (SDK csc not covered), closure example naming (`refs` is an op name), G10 with sibling repos not exercised |
| report HTML + md | docs | `fsc-rework/night-report-2026-10-13.html` / `.md` | done; docs fix 27e4c6e on `pr/lock-roots` |

**Morning hand-over (2026-10-13)**: read `fsc-rework/night-report-2026-10-13.html` (charts) or `.md`.
Merge order (all local, nothing pushed): core `pr/fileset-perf` then `pr/rule-match-cache`;
hermetic `pr/import-fidelity` -> `pr/lock-fidelity` -> `pr/batch-restore` -> `pr/lock-roots`;
`pr/pool-hang` holds only the G16 repro tests (optional); `night-integration` is a detached
worktree (`git worktree remove .claude/worktrees/night-integration`), not a branch. After the
merges: tags `v3.7.1` and `hermetic-v0.3.1`, then `build.fsc.fsx` and the trial project scripts move from the
`-night` feed (`/path/to/exp-repo/feed/`) to nuget.org. Decisions pending:
HERMETIC on generated C# locks (SDK csc prerequisite, d1-report q1), pack (PR 8) answers, G16.

**PR 0 done (2026-10-15)**: `origin/dev` merged into `feature/hermetic-build` (e6acb95, not pushed;
dev brought PRs #44-#54 incl. #54 `ArtifactDep2`); conflicts `.gitignore` (union:
`.claude/worktrees/` + `.packages/`) and `docs/roadmap.md` (dev's "B6 done" heading kept, the
feature's fsc-design.md reference re-added); `build test` passes, tests 333 passed + 1 skipped
(334), hermetic.tests 206 passed; no hermetic test change was needed for `ArtifactDep2`. Night
base 74bf4d9 dry-run (`git merge-tree`) on top of e6acb95: no conflicts (28 commits, 39 files).

## 2026-10-16 — PR sequence 0–4 done

Principle: no msbuild emulation; the locks path (everything the build needs is recorded in the lock, msbuild is only an importer).

- PR 0: merge dev (e6acb95, incl. #54 ArtifactDep2).
- PR 1 `pr/dotnet-resx`: draft #55 to dev (ResxFormat, csc crash report, platform monikers); behaviour change: preserialized resx needs System.Resources.Extensions on net462 too.
- PR 2 `pr/resolved-inputs`: `Import.record`, lock format 2 with `Recorded`, portable identities, critic fixes B1/S2–S12, hermetic 250, the trial project 172/172 metadata parity, re-import stable, locks −25%.
- PR 3 `pr/apps`: App section, apphost = SDK, deps/runtimeconfig/config from msbuild, hermetic 258, the trial project 177/177.
- PR 4 `pr/deploy`: `Locks.runFolderWith`, run folders of hard links, 0 extra / 0 conflicts on the trial project, hermetic 267.

Branch chain and worktrees: `feature/hermetic-build` ← `pr/resolved-inputs` ← `pr/apps` ← `pr/deploy` (all pushed except the feature branch; the draft PR for PR 2 is not opened because `feature/hermetic-build` is not pushed). Commands: `git push origin feature/hermetic-build`, then `gh pr create --draft --base feature/hermetic-build --head pr/deploy --repo OlegZee/Xake` for the whole stack, or one PR per branch.

Night branches parked: `pr/apphost-resx` (docs: triage, decisions-ru, group1-ru, critics), `pr/win-import-3`, `night-2`, `pr/wpf-exe-satellites` (WPF/satellites), `pr/s66-repro` (superseded).

Open: binding redirects S55, satellites, `Project.import` legacy, Windows/Linux unverified, HERMETIC=on on the trial project, `LOCK_STALE` switch name, SDK-pin warning 85× on the trial project, 34–38 s build (analyzers).

**Exact next step**: owner reviews/merges #54 and #55 into dev → release Xake 3.8; pushes the feature branch and opens the stack PRs; meanwhile `exp-repo` moves to 0.4.2-night (in progress).

## 2026-10-18 — scrub done; PR 5 (core bump and the trial project fixes)

**Scrub**: the repository history was rewritten to remove customer-specific material; the branch
`pr/ar-full` is deleted; the customer material (trial project scripts, logs, full reports) lives
in a private experiment repository; draft #60 (`pr/scrub-docs`) fixes the docs on `dev`. The PR
reports in `fsc-rework/` are neutral summaries. The decisions of 2026-10-15 are restored in
`fsc-rework/interview.md` ("Decisions of 2026-10-15"), with the principle *no msbuild emulation*.

**Stack**: #56 (PR 2, recorded inputs) and #57 (PR 3, applications) merged into
`feature/hermetic-build`; #58 (PR 4, run folders, `pr/deploy`) open; PR 5 #61 `pr/hermetic-38`
(worktree `.claude/worktrees/pr/hermetic-38`) stacked on `pr/deploy`, draft against it. Core:
Xake 3.8.0.29 is on nuget.org (#54 `ArtifactDep2`, #55 `ResxFormat` and the csc crash report);
#59 (project descriptions in core, 3.9) is open and not released -- moving hermetic onto it is a
later PR.

PR 5 content:
- `src/hermetic` on `Xake [3.8.0.29, 3.9)` (the next minor stays the upper bound: F# records are
  not binary-compatible across minors). The S66 workaround (file dependencies on the closure's
  write times in `Locks.runFolderWith`) is removed, `ArtifactDep2` covers it;
  `Described.refreshResources` stays (3.8's runner still compiles a missing `.resources` only;
  the night runner fix did not land in core -- a core follow-up). Hermetic did
  not link its own `ResxFormat`: nothing to delete there. `DescriptionTests` match `ArtifactDep2`.
- S67 `Locks.check`/`checkWith`/`staleWith`/`staleOf` were already public (critic round);
  documented with an example in lock.md.
- S68 `Pack.fromNuspec` + `Pack.tokensOf`/`expand`/`libOf`: the minimal pack from a project's
  own nuspec, tokens from the description plus the script's.
- S70 host path in descriptions and the one-per-import SDK pin warning: fixed in the critic
  round, covered by `RecordTests` (verified).
- S72 AD0001 under the compiler server: the replay passes msbuild's own `/analyzer:` and
  `/analyzerconfig:` set (recorded); msbuild also shares one server across `-m` nodes, Xake
  runs more compilations at once; not reproduced. The compile writes an error log, and a
  failure whose log names a crashed analyzer is reported with the analyzer and retried once
  without the server (test: an analyzer that throws only inside `VBCSCompiler`).
- Satellites: deferred by decision (note already in the release notes).
- Self-build: `build.fsc.fsx` bootstraps from Xake 3.8.0.29 (hermetic 0.3.0.28 still, NU1608);
  `src/hermetic/xake.project.json` re-imported, the locks regenerated with this branch's hermetic
  (`-d LOCK_REFRESH=Xake`, a temporary `#r` on the built dll, as docs/session.md allows): Xake
  3.8.0.29 and its new dependencies (System.Resources.Extensions 8.0.0, System.Memory 4.5.5),
  `"Format": 2` written; the published 0.3 bootstrap reads them (`build check` green).

## 2026-10-18 — PR 6: hermetic on core Xake 3.9 (`pr/hermetic-39`)

Xake 3.9.0 is on nuget.org as **3.9.0.30** (#59: the project description in core). PR 6 moves
the hermetic package onto it; `dev` (#55, #59, #60) merged into the branch first (conflicts:
`Resx.fs` taken from dev, `xake.fingerprints` regenerated).

- `src/hermetic` on `Xake [3.9.0.30, 3.10)` (owner's upper bound; the record risk is noted in the
  fsproj and the release note), `<Version>` 0.4.0.
- Deleted: `src/hermetic/Description.fs`, `docs/hermetic/xake.project.schema.json`; `Json.fs`
  renamed `ToolJson.fs` (internal; the core's `Json` is internal to Xake.Dotnet, the package
  still parses msbuild output, assets and metadata). Scripts and tests `open Xake.Dotnet` for
  `Description`/`ProjectPlan`.
- `Described.fs` shrank to what only a lock/restore needs: the duplicated helpers
  (`frameworkDefines`, `baseFramework`, `implicitFrameworkReferences`, `sdkNoWarn`,
  `manifestName`, `rootNamespace`, `generatedKey`, `itemsFor`, framework pick) are
  `ProjectPlan`'s. The plan honours `frameworkReferences` (shared framework packs).
- Builder: the core's intrinsic `project` is the setting; this package adds `config`,
  `packages` (on the settings and on `CscDescribed`/`FscDescribed`), `lock`/`nofetch`/`resolve`.
  `lock` right after `project` routes to the package's composition (the `CscLocked`/`FscLocked`
  builder modules moved from `Lock.fs` to `Described.fs`). `csc { project p }` alone is the
  core's composition. `Spec` lost `Path` (it is `settings.Project`).
- Import writes `frameworkReferences` (msbuild `FrameworkReference` items, implicit ones
  included, `Microsoft.NETCore.App` left out, WindowsDesktop profiles folded).
- `Described.refreshResources` stays: 3.9's `CompilerRunner` still compiles a `.resources` only
  when missing (core follow-up).
- Self-build: `build.fsc.fsx` on `Xake 3.9.0.30` + hermetic `0.3.0.28`: `import`, `build`,
  `check` work; `update-locks` does not (0.3 calls the 3.8 `FscSettingsType` constructor).
  Locks regenerated with this branch's hermetic through a temporary `#r` (`-d
  LOCK_REFRESH=Xake`): the hermetic lock now names Xake 3.9.0.30.
- Tests: `build.fsx build test` green, tests 368 passed 1 skipped, hermetic.tests 286 passed;
  `build.fsc.fsx build check` green.

**Next**: merge PR 6 into the feature branch; feature → dev; tag `hermetic-v0.4.0`; then bump
`build.fsc.fsx` to `Xake.Hermetic.Dotnet 0.4.0.<run>`.
