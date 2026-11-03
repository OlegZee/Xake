# Documentation index

The map of `docs/`. Read this first, then open only what the task needs. Dates are the last
commit touching the file; lengths are approximate line counts. Index written 2026-10-11.

## What the repository is

- Xake is an F# build tool in the spirit of Shake: build scripts are F# (`xakeScript { }`,
  `recipe { }`), with dependency tracking, incremental rebuilds and parallel execution.
- Package `Xake` (3.15.0 on nuget.org; `dev` is the 4.0 line) is the engine alone (`src/core`).
- The .NET tasks `csc`, `fsc`, `msbuild`, `resgen` (namespace `Xake.Dotnet`) and the hermetic
  library (namespace `Xake.Hermetic.Dotnet`: locks, restore, SBOM, verify, deterministic pack,
  signing) left this repository at 4.0: they are the one package `Xake.Dotnet`, built in
  [OlegZee/Xake.Dotnet](https://github.com/OlegZee/Xake.Dotnet), with their docs and samples. Their design records stay here
  under `docs/features/` (hermetic-build, hermetic-windows, release-process, dotnet-split).
- Xake builds itself with `build.fsx` (`dotnet build`, the tests, `dotnet pack`).
- Work goes on feature branches, PRs to `dev` on OlegZee/Xake; feature working notes live in
  `docs/features/<name>/`.

## The three areas

| Area | Index | What is there |
|---|---|---|
| Base docs | this page, below | engine, tasks, process, roadmap, cross-feature session notes |
| .NET tasks and hermetic docs | [OlegZee/Xake.Dotnet `docs/`](https://github.com/OlegZee/Xake.Dotnet/tree/dev/docs) | toolchain discovery, `csc`/`fsc` syntax, compiler server, descriptions, the hermetic reference, guide and architecture (moved at 4.0) |
| Feature working notes | [features/hermetic-build/index.md](features/hermetic-build/index.md) | session log, tracker, brief, design notes, experiments, proving scripts of `feature/hermetic-build` (the record; the code is in Xake.Dotnet now) |
| Dotnet split | [features/dotnet-split/session.md](features/dotnet-split/session.md) | moving `Xake.Dotnet` and the hermetic library to their own repository and package (4.0) |
| Variables (4.0) | [features/vars-precedence/session.md](features/vars-precedence/session.md) | declarations evaluated at launch, `var` rules, `Var.read`, library variable sets: spec, briefs, PR notes; implemented in #111 and #112 (user reference: [tasks.md](tasks.md#script-variables)) |
| Release process | [features/release-process/session.md](features/release-process/session.md) | versioning, the published version is the tag, hermetic on the in-repo Xake until 4.0, the split of `Xake.Dotnet` (D) at 4.0 |
| Rule specificity | [features/rule-specificity/session.md](features/rule-specificity/session.md) | rule-matching working notes |
| Windows findings | [features/hermetic-windows/brief.md](features/hermetic-windows/brief.md) | closed 2026-10-27 (#93-#97, Xake 3.13.0, Xake.Hermetic.Dotnet 0.8.0): the seven remarks from the Windows run of the consumer solution (2026-10-26), analyses, brief with the owner's decision list, PR split; branch `feature/hermetic-windows` |

## Key cross-cutting pages

| File | Why it matters | Lines | Status |
|---|---|---|---|
| [session.md](session.md) | cross-feature engine facts, release notes, traps; read at every session start (CLAUDE.md) | ~190 | current |
| [roadmap.md](roadmap.md) | agreed-but-unscheduled items: principles, engine backlog | ~110 | current |
| [tasks.md](tasks.md) | reference of the built-in tasks: `sh`, copy, rm, files, variables | ~535 | current |

## Base docs

### Getting started and task reference

| File | What it is | Read when | Lines | Date | Status |
|---|---|---|---|---|---|
| [overview.md](overview.md) | short intro: minimal script, how to run, rule styles, the 4.0 pattern grammar (patterns, masks, paths) | first contact with Xake | ~100 | 2026-09-05 | current |
| [typed-targets.md](typed-targets.md) | `Xake.Typed` (4.0): rule patterns with record groups, generated paths, `Command`, migration; a test compiles its snippets | writing rules with groups | ~430 | 2026-11-03 | current |
| [cheatsheet.md](cheatsheet.md) | one-page syntax: entry point, `<==`/`<<<`, rules, variables (`var`, `varschema`, secrets) | writing or reading any build script | ~130 | 2026-10-06 | current |
| [tasks.md](tasks.md) | reference of the built-in tasks (the .NET ones are in Xake.Dotnet) | using a task or its settings | ~535 | 2026-11-03 | current |
| [release-notes.md](release-notes.md) | what changed per `Xake` release; the `4.0.0 (unreleased)` section holds the variable rework and its migration lines | upgrading, writing a release | ~300 | 2026-10-31 | current |
| [variables.md](variables.md) | working with variables: `var`, `varschema`, `XAKE__` environment source, secrets, library variables, errors, 3.15 -> 4.0 migration | declaring or reading build variables | ~330 | 2026-10-31 | current |
| [captureOutput.md](captureOutput.md) | `captureOutput` op of `sh`: route output to file, log, handler | capturing shell output | ~85 | 2026-05-04 | current |

### Engine

| File | What it is | Read when | Lines | Date | Status |
|---|---|---|---|---|---|
| [implnotes.md](implnotes.md) | high-level internals: rebuild conditions, dependency storage, scheduling | first change to the engine | ~80 | 2026-09-05 | current |
| [delegated.md](delegated.md) | Resources, detached execution, the delegated-rule hook | distributed or concurrency-limited rules, signing as delegated rule | ~205 | 2026-06-02 | current |
| [resources.md](resources.md) | design record: the hold-and-wait deadlock of `need` under a resource; the guard, `allowNeed`, `CSC_JOBS`/`FSC_JOBS` | `withResource`/`requiring` changes, a hang at zero CPU | ~390 | 2026-10-20 | current |
| [ideas.md](ideas.md) | rule combinators via `mapRecipe`, full-framework targeting notes | looking for engine ideas | ~50 | 2026-09-05 | partly superseded by roadmap.md |

### Process, state, plans

| File | What it is | Read when | Lines | Date | Status |
|---|---|---|---|---|---|
| [devprocess.md](devprocess.md) | branches, build/test, packaging and publishing, release | building, packing, releasing, bootstrapping a dev `#r` | ~270 | 2026-11-03 | current |
| [session.md](session.md) | cross-feature session notes: release facts, traps, how to verify | every session start | ~190 | 2026-11-03 | current |
| [roadmap.md](roadmap.md) | cross-feature backlog with principles | planning the next work | ~110 | 2026-11-03 | current |
| [fingerprints.md](fingerprints.md) | the `Fingerprints` module: a committed file recording how generated files were produced; stale/hand-edited/missing checks | committing generated files | ~70 | 2026-11-03 | current |

## Sub-indexes

- [features/hermetic-build/index.md](features/hermetic-build/index.md): working notes of the
  hermetic feature branch (the record; the code and product docs are in OlegZee/Xake.Dotnet).
