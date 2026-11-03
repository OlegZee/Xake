# Brief: declarative fsc/csc build from project descriptions and locks

Status: draft rev. 3 for the user's review, 2026-10-11, after the Fable review ([review.md](review.md))
and the user's answers. Sources: [interview.md](interview.md) (authoritative; "Qn", "R1"/"R2" and "FR"
(answers to the Fable review) below point to its entries), [digest.md](digest.md) (facts about PR #42),
drafts in [drafts/](drafts/) with the gap list [drafts/gaps.md](drafts/gaps.md).
Decisions here are not to be re-litigated during implementation; open topics are listed at the end.

## 1. Goal and scope

Xake's own system for a meaningful, declarative build description on the fsc/csc tasks (Q3):

- (a) the script plus a per-project description (`xake.project.json`), data only;
- (b) generation of lock files from the descriptions;
- (c) the build from the locks.

The fork (Q3): **now** this declarative system; **later**, separately, hermetic builds of existing
msbuild projects without fsc/csc (the msbuild-import route). Both meet at (c): the lock format and the
build-from-lock recipes are shared. The core deliverable is one piece of work: csc/fsc able to load a
project description, on top of the existing lock machinery (Q5.1, Q5.6). Xake's self-build and the
the trial product two-brand build are its two consumers.

Out of scope: byte identity with `dotnet build` (Q3); hermetic tests (`dotnet test` stays, Q4.4);
VS integration and other exotics (Q5.2); publishing to nuget.org during the work (Q6); package
resolution inside fsc/csc (FR); any Xake engine change.

PR #42 (`pr/build-fsc-lock`) stays on its branch, neither merged nor closed (Q8).

## 2. Decisions by topic

Description (a)
- Bazel-like: what the project is, changes rarely; packages pinned only when needed; details may live
  in the script; the recipe holds no list of libraries, sources or defines (Q1).
- Data only; ML-like typed format is the aim, JSON + schema to start (Q1.2, Q2).
- msbuild project files stay primary; the description is a projection, rewritten only when its content
  changes (Q2). Import uses msbuild; CI never runs it (Q4 clarifier).
- No placeholders. A variant msbuild evaluates differently (a brand) is a separate description,
  imported with that variant's global properties (FR clarified 2).
- C# sources: the description keeps msbuild's globs as Xake masks (FR 3; see Build).

Lock (b)
- Two layers: descriptive (human) and executive (machine); both committed, reviewed via git diff (Q3).
- One lock per project x configuration (x brand, by path prefix); reference unification is msbuild's
  job, not ours (R1.3). Path `locks/<name>/<cfg>.json`, per brand `<brand>/locks/<name>/<cfg>.json`.
- Generation of A's lock picks each referenced project's configuration and TFM (nearest match,
  fallback when B lacks A's configuration) and records the choice as B's output path in A's lock (R2).
- Packages are resolved by msbuild restore at lock generation (existing `Restore.download` +
  `Nuget.readAssets`) and recorded in the lock; fsc/csc never resolve packages (FR). A version the
  current lock holds is kept; refreshing floating versions is explicit, `LOCK_REFRESH=<ids|*>` (R2.2).
- **Lock format: no change, no version bump, no migration** (FR 5). Existing slots suffice:
  `Evaluation.ProjectRefs`; `Dependencies.References` (Path + Sha256; project-ref outputs unhashed,
  per the roadmap "in-lock project references must stay unhashed"); `Compilation.Options` simply
  without `-o` in Xake-generated locks; `Sources` holds the C# masks; `Generated` keeps a literal
  `$(Version)` token. The chosen config/TFM of a ref is its path. A future field is optional and
  additive. Details: gaps.md "Lock format: no change".

Generation is explicit, staleness is a warning (R2, FR clarified 1)
- `import` and `locks` are explicit commands; no rule produces a description or a lock, nothing
  regenerates a committed file on its own.
- Fingerprints: one committed file `xake.fingerprints`, one line per target, sorted (git-friendly):
  target, its hash, the hash of its inputs, the inputs. Written by `import` (inputs: every msbuild file
  read) and `locks` (inputs: the description and the refs' locks). Hashes over LF-normalised text
  (no false staleness from `autocrlf`). A Xake core *library* module, no engine change.
- `build` warns on a stale or hand-edited line and uses the committed files, the same as CI (R2.4);
  `check` fails instead (whether CI runs it is the user's call). A fresh clone needs no `.xake`.
- Next step, a separate later PR: a **`cached` rule decorator** attached with a pipe
  (`rule |> cached inputs` or similar), keyed by explicitly declared inputs, no source indexing; when
  the cached file exists the rule is not triggered and the file is handed back; staleness check
  optional; library functions plus at most a cache in the internal context, no engine change. It is
  the first instance of the open "rules over rules" topic (FR).

Build (c), layout, configurations
- Targets hold no data-processing code; layout is the script's, not hard-wired (Q7).
- Layout variant A: mask in the rule + `outOf cfg fwk name`, applied at (b) to ref paths and by
  `Lock.needAll`; duplication accepted (Q7 follow-up).
- Configuration is part of the target mask (Q7.3); Debug is not needed for every library (R1.1).
- The compile rule's target is the `.dll`; the `.xml` is a side output when the lock asks for doc
  (fixes the review's Debug-without-doc rebuild loop).
- C# masks are expanded at build through `getFiles` (a `GetFiles` dependency): an added `.cs` rebuilds
  without re-import. Where the expansion is an input of a fingerprinted/cached file, it is materialised
  and hashed (FR 3).
- Composed options carry `--deterministic+` and `--pathmap`; the HERMETIC gate runs inside
  `Lock.compileAs`.
- No new rules-with-arguments sugar now (Q7.2). The script keeps version(s), packed packages, metadata
  overrides, test commands, scenarios (Q7.4).

Packing (Q4, FR 4)
- nupkg from (c) binaries via `Pack.nupkg`, no second msbuild run; metadata in the description,
  overridable by the script; net462 is one more TFM.
- Acceptance: two clean builds **at the same version** give nupkgs equal by binary content of their
  entries (zip timestamps/order ignored). CAS possibly later; assembly versioning is open.

Feeds (Q6)
- Restore keeps using nuget.org; no publishing during the work; a local shared folder is both the
  publish target and a local feed; `HERMETIC` and `NUGET_FETCH` are independent axes.

the trial product (Q5, R1, FR)
- Branch `develop` of repo-a and repo-b (R1.5), as wide as possible,
  brands Acme and Globex, Debug + Release + basic pack; judged by script clarity and core use.
- Brands are a particular mechanism expressed by **target prefixes**: output paths `out/<brand>/...`,
  per-brand description and lock folders, commands `import::acme`, `build::globex`, `clean::acme`.
  Script knowledge, not the description's (R1.4).
- Import runs per brand with that brand's props: global property `Brand=<BRAND>` lets
  `src/Directory.Build.props` pick `src/Acme.props` / `src/Globex.props`. No brand rename by Xake.
- **Chosen location:** the trial project script, descriptions, locks and fingerprints live in an experiment
  folder beside the two the trial project repos (optionally its own git repo for reviewable diffs); nothing is
  committed into the trial project repos. Why: the experiment must not touch the trial project branches, and per-brand
  descriptions belong to this build. The schema's `project` field (project file relative to the
  description) allows a description outside the project's repo.

Not reopened: `Csc` record, `resolve` op, no hooks, `CSC_SERVER`, `NUGET_FETCH`, package
`Xake.Hermetic.Dotnet`.

## 3. The desired scripts

Drafts (not runnable; `// NEW` marks what is missing): [drafts/build.fsc.fsx](drafts/build.fsc.fsx)
(72 lines), [drafts/trial-build.fsx](drafts/trial-build.fsx) (78 lines),
[drafts/project.schema.json](drafts/project.schema.json),
[drafts/example-project.json](drafts/example-project.json). The heart of the Xake one:

```fsharp
let lockOf name cfg = $"locks/{name}/{cfg}.json"
let outOf cfg fwk (_: string) = $"out/{cfg}/{fwk}"
let versions = [ "src/hermetic/**", vars.HermeticVersion; "**", vars.Version ]

command "import" { do! Description.import projects Description.beside [] }                // NEW (msbuild)
command "locks"  { do! Lock.generateAll descriptions ["Release"] lockOf outOf }             // NEW
"check" => Fingerprints.check "xake.fingerprints"                                          // NEW

command "build" {
    do! Fingerprints.warn "xake.fingerprints"                                             // NEW
    do! Lock.needAll descriptions "Release" lockOf outOf                                  // NEW
}
target "out/(cfg:*)/(fwk:*)/(name:*).dll" {
    let! cfg = getRuleMatch "cfg"
    let! fwk = getRuleMatch "fwk"
    let! name = getRuleMatch "name"
    do! Lock.compileAs (lockOf name cfg) fwk versions                                     // NEW
}
```

Reading: `import` and `locks` are the only writers of committed files; `build` reads them, warns when a
fingerprint line is stale, and fails on a described project without a lock. The trial project script has the same
shape with the brand as a prefix of every path and command.

## 4. What is missing

Full table with semantics and sizes: [drafts/gaps.md](drafts/gaps.md) "NEW functions". In short:

- Xake core (library, no engine change): `Fingerprints.record/warn/check`.
- (a): the description model and schema; `fsc/csc { project d }` + `config`; import project file ->
  description with global properties, recording every msbuild file read.
- (b): packages via the existing msbuild restore with kept versions and `LOCK_REFRESH`;
  `Lock.generateAll` (topological, ref choice as unhashed paths, writes only on change).
- (c): `Lock.compileAs` (target output, C# masks via `getFiles`, HERMETIC gate, `$(Version)`) and
  `Lock.needAll`.
- Pack: `Package.nupkg`/`projects`/`push`, `Pack.compare` (binary content, same version).

## 5. PR plan (to `dev`, small, in order)

Each PR carries its tests (src/tests for core, src/hermetic.tests for the rest) and its docs
(docs/hermetic, cheatsheet, tasks.md / csc-syntax.md where touched).

1. **Hermetic: description model** (~200). Type, JSON read/write, schema under docs/schemas,
   `find`/`names`/`beside`. Depends on: none. Accept: example-project.json round-trips
   byte-identically; validation errors name the field; `save` leaves an unchanged file untouched;
   `find` records one input. Docs: schema page.
2. **Hermetic: `fsc/csc { project d }` + `config` + packages via msbuild restore** (~380). Depends on 1.
   Accept: hand-written descriptions of src/core and src/dotnet compile through `Lock.record`/`Lock.build`
   into loadable dlls; Debug vs Release presets match the SDK; packages come from the existing
   `Restore.download` + `Nuget.readAssets`, none resolved in fsc/csc; `--deterministic+`/`--pathmap`
   set; a C# fixture keeps its masks. Docs: csc-syntax.md, tasks.md.
3. **Hermetic: `Lock.generateAll`** (~200). Depends on 2. Accept: Xake's three projects get Release locks
   that `Lock.parse` of 0.2 reads unchanged (no format change); regenerating is byte-identical and
   writes nothing; a Debug A over a Release-only B gets B's Release output path, unhashed; a ref with
   another TFM gets the nearest; refs before referencers; a floating version stays until
   `LOCK_REFRESH` names it; `Options` has no `-o`. Docs: lock.md "Xake-generated locks".
4. **Hermetic: build from a lock into a layout** (~200). `Lock.compileAs`, `Lock.needAll`. Depends on 3.
   Accept: the PR #42 compile logic (`intermediate`/`unbuilt`/`produced`/`mapped`) is gone; HERMETIC gate
   fires inside; adding a `.cs` under a mask rebuilds; Debug without doc does not rebuild forever; a
   description without a lock fails naming `locks`; two clean builds give identical dlls. Docs: cheatsheet.
5. **Hermetic: import project file -> description** (~250). Depends on 1. Accept: importing Xake's
   projects reproduces PR 2's hand-written descriptions; re-import without changes writes nothing;
   global properties reach msbuild (a brand fixture yields its branded assembly name); the list of
   msbuild files read is returned for fingerprints. Docs: workflows.md.
6. **Core: fingerprints library + wiring** (~120 + wiring). Depends on 3, 5. Accept: one line per
   target, sorted, relative paths; touching a shared props file makes `build` warn once and use the
   committed file; a hand edit is reported; `check` fails; CRLF/LF checkouts hash equal; a fresh clone
   (no `.xake`) checks fine. Docs: cheatsheet.
7. **Self-build: build.fsc.fsx per the draft** (script + committed descriptions, locks, fingerprints).
   Depends on 4, 6. Accept: about one screen, no data-processing code in targets; `build` on a fresh
   clone needs no msbuild; CI runs `build` from the committed locks (NUGET_FETCH on). Docs: readme
   build section, session notes.
8. **Hermetic: pack** (~330). `Package.nupkg`/`projects`/`push`, `Pack.compare`, net462 as a second TFM.
   Depends on 1, 4. Accept: Xake and Xake.Hermetic.Dotnet nupkgs from (c) binaries copied to the local
   feed folder; two clean builds at the same version compare equal by entry content. Docs: pack page.
9. **the trial product experiment** (not a PR to `dev`; a report). Experiment folder beside the trial project repos,
   nothing committed there. Depends on 4-6 (8 for pack). Accept: both brands by prefix, Debug +
   Release, the 15 projects build from locks; basic packages per brand; report script size and core
   utilisation against `dotnet build`, and the verified/unverified engine points (gaps.md).
10. **Later, separate: `cached` rule decorator** (library, no engine change). Depends on 6. Accept:
    a decorated rule with an existing, fresh file does not run; a stale one warns (optional) and is
    handed back; a missing one runs and records its fingerprint line.

PR 5 can follow 1 in parallel with 2-4; 6's library part can start any time.

## 6. Open topics (not to be decided during implementation without the user)

- "Rules over rules" / frozen mode as a general mechanism; `cached` (PR 10) is its first instance:
  pipe syntax, where its cache lives in the context, and whether `import`/`locks` become cached rules.
- Assembly versioning: what the dll version attributes carry vs the package version (FR 4); CAS later.
- A mask in `Compilation.Sources` of a Xake-generated lock: confirm it counts as "no format change".
- Layout variant B: one mask as both rule pattern and generator; typing of captured parameters,
  possibly type providers (Q7 follow-up).
- An ML-typed description format beyond JSON + schema; dynamic loading of descriptions (Q1.2).
- Debug-only optional libraries / per-configuration optionality inside a description (Q1.3).
- Diamond refs: A(Debug) -> B(Release-only) -> C gives two C's in one build; warn in `Lock.generateAll`?
- Engine points to verify (gaps.md): `build::(b:*)` capture in phony patterns, filesets over `../`,
  lock tokenisation of paths outside `$(ProjectRoot)`.
- Smaller ones in gaps.md "Open questions left by rev. 3": ref-config fallback order, `versions`
  masks vs version per description, msbuild-only behaviour in the trial project, hermetic's reference to Xake.
