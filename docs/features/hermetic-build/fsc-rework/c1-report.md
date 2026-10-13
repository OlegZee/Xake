# C1 report: fileset `**` matching cost (G8)

Branch `pr/fileset-perf` (worktree `.claude/worktrees/pr/fileset-perf`, base `origin/dev` 0139682),
not pushed. Commits: `2f93bb3` test (benchmark + edge cases), `2b5bf39` perf (the fix).

## Root cause

`src/core/Path.fs:181-186` (before the fix): `matchImpl.maskToRegex` built a new
`Regex(..., RegexOptions.Compiled)` on every call, which costs about 1 ms. `matchPart`
(`Path.fs:194-195`) called it for each `DirectoryMask`/`FileMask` segment it compared. Fileset
excludes are applied per file (`Fileset.fs:107-109`, `Path.matchesPattern` on each listed file).
For `**/.*/**/*` the `Recurse` arm (`Path.fs:208`) tries the `.*` directory mask against every
folder below the base, so each file compiled one regex per folder level: about 1 ms per file.
`!bin/**/*` and `!obj/**/*` cost almost nothing because their leading literal `bin`/`obj` fails
before any mask is reached. The `**` matching is not exponential (O(depth^2) for this mask), and
nothing walks the tree again.

## Fix

`Path.fs`: `maskToRegex` now caches regexes per mask string in a
`ConcurrentDictionary<string, Regex>` (`GetOrAdd`). The regex text and options are the same as
before, so it matches the same names. It is thread-safe because a shared `Regex` may be used for
matching from several threads. The set of keys stays small: a script's mask segments plus the
rule patterns given to `matchGroups`.

Side effect: `Path.matchGroups` (`ExecCore.fs:55,62,75,219`, rule matching for each target) also
built a compiled regex on every call. It now reuses the cached one. That may also cut some of
the per-target no-op cost (G9/C0). I did not measure it separately.

## Numbers (Debug build, quiet machine; mean of 5 expansions after a warm-up)

Benchmark `benchmark: '**/*.cs' with dot-folder, bin and obj excludes` (Explicit, in
`src/tests/FilesetTests.fs`): 1360 files in 60 leaf folders 3 levels deep, plus `.git`, `Area1/.vs`,
`bin` and `obj`.

| masks | files | before | after |
|---|---|---|---|
| `**/*.cs` | 1360 | 3.8 ms | 3.9 ms |
| `+ !**/.*/**/*` | 1340 | 1249.8 ms | 7.3 ms |
| `+ !bin/**/* !obj/**/*` | 1340 | 13.6 ms | 9.4 ms |
| all four | 1320 | 1250.3 ms | 12.6 ms |

the trial project Layout (read-only `dotnet fsi` snippet in `/tmp/c1-bench/ar-rendering.fsx`, referencing the
worktree's `Xake.dll`, based at `repo-b/src/Layout`, `**/*.cs !bin/**/* !obj/**/*`):

| | files | before | after |
|---|---|---|---|
| without `!**/.*/**/*` | 1308 | 15.7 ms | 15.3 ms |
| with `!**/.*/**/*` | 1308 | 1652.6 ms | 14.1 ms |

The acceptance target (< 50 ms) is met. I did not measure the trial project Release clean run (target 33 -> ~21 s).
It needs a locally packed Xake, because the hermetic package references the published `Xake [3.7.0.26, 3.8)`. That run belongs to round E.

## Semantics guarantees and the tests that pin them

The regex construction is unchanged and only memoized, so every mask matches as it did before. The new
tests in `src/tests/FilesetTests.fs` run on a real temporary tree. They passed both before and after the fix:
- `'**/.*/**/*' excludes files below any dot folder and nothing else`: excludes `a/.git/x.cs`,
  `.hidden/y.cs` and `a/b/.vs/deep/z.cs`; keeps `a/b.cs`, `a.b/f.cs` and `top.cs`.
- `'**/.*/**/*' exclusion keeps the order of includes and excludes`: a later include brings a file back.
- `'dir/**/*' excludes every file below dir, 'dir/**' excludes none (current semantics)`: a mask
  ending in `**` parses to a folder `Recurse` and matches no file. This is existing behaviour, pinned and not changed.
  It is worth knowing for `Import` (msbuild's `bin/**` has to become `bin/**/*`, as it already does).
- `directory masks in excludes follow the OS case rules`: `d*` skips `Dir2` on Unix and matches it on Windows.
- `excludes match the same after a mask was used many times`: the cached regex gives the same result as the first use.

## Build and test

- `dotnet build src/core --no-incremental`: `0 Warning(s)` / `0 Error(s)` (net462 and netstandard2.0).
- `dotnet test src/tests`: `Passed!  - Failed:     0, Passed:   329, Skipped:     1, Total:   330, Duration: 28 s - tests.dll (net8.0)`
- `dotnet test src/hermetic.tests`: `Passed!  - Failed:     0, Passed:   206, Skipped:     0, Total:   206, Duration: 1 m 9 s - hermetic.tests.dll (net8.0)`.
  This suite builds on the published `Xake` package, not on `src/core`, so it does not exercise
  the change. It only confirms that nothing downstream in the worktree broke.

## Risks

- The cache is unbounded. Its keys are masks from scripts and rules, which are few in practice. A
  program that generates many distinct masks would keep their regexes alive for the whole process.
- The users' docs are unchanged: matching behaviour is the same, only faster.
