# Documentation index

The map of `docs/`. Read this first, then open only what the task needs. Dates are the last
commit touching the file; lengths are approximate line counts. Index written 2026-10-11.

## What the repository is

- Xake is an F# build tool in the spirit of Shake: build scripts are F# (`xakeScript { }`,
  `recipe { }`), with dependency tracking, incremental rebuilds and parallel execution.
- Package `Xake` (3.6.0.24 on nuget.org) carries the engine (`src/core`) and the .NET tasks
  `csc`, `fsc`, `msbuild`, `resgen` (`src/dotnet`, namespace `Xake.Dotnet`).
- Package `Xake.Hermetic.Dotnet` (0.2.0.25, preview, `src/hermetic`) adds the lock: a JSON file
  in git recording what the compiler is handed plus SHA-256 of every outside input; also
  restore, SBOM, verify, deterministic pack, signing skeleton.
- Xake builds itself: `build.fsx` (msbuild-based, build of record) and `build.fsc.fsx` (fsc from
  a lock; reworked in open PR #42, branch `pr/build-fsc-lock`).
- Work goes on feature branches, PRs to `dev` on OlegZee/Xake; feature working notes live in
  `docs/features/<name>/`.

## The three areas

| Area | Index | What is there |
|---|---|---|
| Base docs | this page, below | engine, tasks, .NET toolchain, process, roadmap, cross-feature session notes |
| Hermetic product docs | [hermetic/index.md](hermetic/index.md) | user-facing reference of `Xake.Hermetic.Dotnet`: lock, restore, SBOM, verify, pack, signing, mode, workflows |
| Feature working notes | [features/hermetic-build/index.md](features/hermetic-build/index.md) | session log, tracker, brief, design notes, experiments, proving scripts of `feature/hermetic-build` |
| Windows findings | [features/hermetic-windows/brief.md](features/hermetic-windows/brief.md) | the seven remarks from the Windows run of the consumer solution (2026-10-26): analyses, brief with the owner's decision list, PR split; branch `feature/hermetic-windows` |

## Key cross-cutting pages

| File | Why it matters | Lines | Status |
|---|---|---|---|
| [session.md](session.md) | cross-feature engine facts, release notes, traps; read at every session start (CLAUDE.md) | ~190 | current; its `build.fsc.fsx` section describes the pre-#42 form |
| [roadmap.md](roadmap.md) | agreed-but-unscheduled items: principles, engine, Xake.Dotnet, Xake.Hermetic.Dotnet backlog | ~110 | current (2026-10-11) |
| [tasks.md](tasks.md) | reference of all built-in tasks: `sh`, copy, rm, `csc`/`fsc`/`resolve`/`Csc.run` | ~535 | current (2026-10-11) |
| [hermetic/README.md](hermetic/README.md) | entry page of `Xake.Hermetic.Dotnet` 0.2: what is new, the pieces, two ways in | ~170 | current |
| [hermetic/workflows.md](hermetic/workflows.md) | team flows A-G: composed lock, imported project, fresh machine, CI, release, offline | ~750 | current; header still names 0.1 versions |
| [hermetic-architecture.md](hermetic-architecture.md) | maintainers' view of the hermetic package: model, module map, lock format, evidence, limits | ~980 | current (2026-10-11) |
| [hermetic-guide.md](hermetic-guide.md) | task-oriented user guide, first lock to signing, for 0.2 | ~760 | current, draft |
| [hermetic/hermetic-mode.md](hermetic/hermetic-mode.md) | `HERMETIC=on`: the invariant, prerequisites, messages, pending parts | ~180 | current (shipped in 3.6 / 0.2) |
| [features/hermetic-build/workflows-ru.md](features/hermetic-build/workflows-ru.md) | **RU.** owner's guide to the same flows, with the hermetic mode and a "not done yet" list | ~610 | current as of 3.5 / 0.2 previews (2026-10-09) |

## Base docs

### Getting started and task reference

| File | What it is | Read when | Lines | Date | Status |
|---|---|---|---|---|---|
| [overview.md](overview.md) | short intro: minimal script, how to run, baseline | first contact with Xake | ~100 | 2026-09-05 | current |
| [cheatsheet.md](cheatsheet.md) | one-page syntax: entry point, `<==`/`<<<`, rules, vars | writing or reading any build script | ~130 | 2026-10-06 | current |
| [tasks.md](tasks.md) | reference of built-in tasks incl. the .NET ones | using a task or its settings | ~535 | 2026-10-11 | current |
| [captureOutput.md](captureOutput.md) | `captureOutput` op of `sh`: route output to file, log, handler | capturing shell output | ~85 | 2026-05-04 | current |

### .NET toolchain

| File | What it is | Read when | Lines | Date | Status |
|---|---|---|---|---|---|
| [dotnet-build.md](dotnet-build.md) | toolchain discovery, host vs target runtime, `NETFX`/`NETFX-TARGET`, providers | touching `DotNetFwk.fs` or targeting net4x / netN.0 | ~465 | 2026-10-11 | current |
| [csc-syntax.md](csc-syntax.md) | `csc {}` in detail: composed mode, `Csc` record, `resolve`, `Csc.run` | working on csc/fsc settings or the runner | ~280 | 2026-10-10 | current |
| [csc-server.md](csc-server.md) | `csc` through the Roslyn compiler server: `/shared`, `CSC_SERVER`, `XAKE_CSC_SERVER`; `CSC_JOBS`/`FSC_JOBS` | compile speed or CI server switch | ~155 | 2026-09-30 | current |

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
| [devprocess.md](devprocess.md) | branches, build/test, packaging and publishing, dependency floor, release | building, packing, releasing, bootstrapping a dev `#r` | ~270 | 2026-10-02 | current (PR #42 updates it) |
| [session.md](session.md) | cross-feature session notes: release facts, traps, how to verify | every session start | ~190 | 2026-10-04 | current (PR #42 updates it) |
| [roadmap.md](roadmap.md) | cross-feature backlog with principles | planning the next work | ~110 | 2026-10-11 | current (PR #42 updates it) |

### Hermetic, top level

| File | What it is | Read when | Lines | Date | Status |
|---|---|---|---|---|---|
| [hermetic-architecture.md](hermetic-architecture.md) | architecture of `Xake.Hermetic.Dotnet` for reviewers | design questions about the lock, its seams with `Xake` | ~980 | 2026-10-11 | current |
| [hermetic-guide.md](hermetic-guide.md) | user guide of the hermetic package, 0.2 | learning the user-facing flow end to end | ~760 | 2026-10-11 | current, draft |

## Sub-indexes

- [hermetic/index.md](hermetic/index.md): product reference pages of `Xake.Hermetic.Dotnet`.
- [features/hermetic-build/index.md](features/hermetic-build/index.md): working notes of the
  feature branch, with a column saying which ones the `build.fsc.fsx` task needs.
