# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Session Context

Always read `docs/session.md` at the start of every session, unconditionally.

## Project Overview

Xake is an F# build utility inspired by Shake. It uses F# as a full programming language to define build rules with dependency tracking, incremental builds, and parallel execution. Xake is self-hosting — it builds itself using its own build script (`build.fsx`).

## Build & Test Commands

```bash
# Build and test (primary command)
dotnet fsi build.fsx -- -- build test

# Build core library only
dotnet build src/core

# Run all tests
dotnet test src/tests

# Run tests matching a filter
dotnet test src/tests --filter "Name~\"some pattern\""

# Build + filtered test via build script
dotnet fsi build.fsx -- -- build test -d FILTER=TestName

# Clean
dotnet fsi build.fsx -- -- clean
```

Requires .NET SDK 8.0+ (see `global.json`).

## Architecture

### Core Concepts

- **Target**: What a rule produces — either a `FileTarget` (file path) or `PhonyAction` (named action)
- **Rule**: Maps a target pattern to a Recipe. Types: `FileRule`, `MultiFileRule`, `PhonyRule`, `FileConditionRule`
- **Recipe**: Async-like computation expression (`recipe { ... }`) that builds a target and tracks dependencies
- **Dependency**: Tracked inputs — `FileDep`, `ArtifactDep`, `EnvVar`, `Var`, `AlwaysRerun`, `GetFiles`

### Execution Flow

```
Program.fs (CLI parsing) → ExecCore.runScript() → DependencyAnalysis →
WorkerPool (parallel execution) → Rule matching & Recipe execution → Database.fs (persist state)
```

### Key Source Files

| File | Role |
|------|------|
| `src/core/ExecCore.fs` | Main execution engine: rule matching, recipe execution, dependency tracking |
| `src/core/Database.fs` | Build state persistence for incremental builds |
| `src/core/ExecTypes.fs` | Configuration types (`ExecOptions`) and execution context |
| `src/core/DependencyAnalysis.fs` | Topological sorting and execution order |
| `src/core/WorkerPool.fs` | Thread pool for parallel rule execution |
| `src/core/Program.fs` | CLI argument parsing |
| `src/core/XakeScript.fs` | `xakeScript` computation expression builder |
| `src/core/RecipeBuilder.fs` | `recipe` computation expression builder |
| `src/core/Fileset.fs` | Ant-style file pattern matching with named capture groups |
| `src/core/Types.fs` | Domain types (Target, Rule, Recipe, Dependency, BuildResult) |

### Project Layout

- `src/core/` — Core library (`Xake.fsproj`), targets net462 + netstandard2.0
- `src/tests/` — NUnit tests, targets net9.0
- `build.fsx` — Self-hosting build script
- `docs/` — Documentation (overview, implementation notes, dev process)
- `samples/` — Example Xake scripts
- `.xake` — Binary build database file (do not commit)

## F# Patterns Used

- **Computation expressions** for both build scripts (`xakeScript { ... }`) and build actions (`recipe { ... }`)
- **Discriminated unions** extensively for Target, Rule, Dependency types
- **Pattern matching** on file paths with Ant-style globs and named capture groups (e.g., `"(dir:*)/file.(ext:*)"`)
- OS-aware path comparison (case-insensitive on Windows, ordinal on Unix)

## Development Workflow

- Feature branches merge to `dev` via PR with squash
- Releases: merge `dev` to `master`, tag with `v` prefix (triggers NuGet publish via GitHub Actions)
