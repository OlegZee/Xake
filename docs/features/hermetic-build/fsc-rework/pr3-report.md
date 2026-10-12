# PR 3 report: lock generation as a script rule (`Locks.generate`), rev. 2

Branch `pr/lock-generate` (worktree `.claude/worktrees/pr/lock-generate`), on aad409f (origin/dev merged).
Rev. 2 commits, not pushed: 867e970 `Locks.generate` (drop `generateAll`/`ordered`), 834b906 tests, 8985cde docs.

## History

Rev. 1 (434743c and before) had `Locks.generateAll descriptions configs lockOf outOf`: a library loop over
descriptions x configurations with a topological sort (`Locks.ordered`). Per the user's decision
(interview.md "Implementation session 1", engine-locks-rule.md variant 2) the loop and the sort moved into
the script as a phony rule; the library keeps the one-lock primitive. `Lock.recorded` (7d7b153) is unchanged.

## API now (`src/hermetic/Locks.fs`, module `Locks`)

- `generate : lockOf:(string -> string -> string) -> outOf:(string -> string -> string -> string) -> config:string -> descriptionPath:string -> Recipe<ExecContext, unit>`
  -- one description x one configuration: all its frameworks, `Described` compose + restore, ref choice,
  kept versions + `LOCK_REFRESH`, no `-o`, `Lock.recorded`, written only on change. Loads the description
  itself; its own lock goes to `lockOf d.Name config`.
- Pure, public, unchanged: `nearestFramework target available`, `refConfig config hasLock`,
  `keptVersions refresh kept declared`.

## The canonical rule (docs/hermetic/lock.md, used verbatim in the tests)

```fsharp
let lockOf name cfg = $"locks/{name}/{cfg}.json"
let outOf cfg fwk (_: string) = $"out/{cfg}/{fwk}"
let descriptionOf name = $"src/{name}/xake.project.json"
"lock::(name:*)::(cfg:*)" => recipe {
    let! name = getRuleMatch "name"
    let! cfg  = getRuleMatch "cfg"
    let! d = Description.load (descriptionOf name)
    do! need [ for r in d.ProjectRefs -> $"lock::{r}::{cfg}" ]
    do! alwaysRerun ()
    do! Locks.generate lockOf outOf cfg (descriptionOf name) }
"update-locks" <== [ for n in projects do for cfg in ["Release"] -> $"lock::{n}::{cfg}" ]
```
Engine-forced deviation: `alwaysRerun ()` takes unit. `<==` is right for the aggregate: it is a phony with
`need` + `alwaysRerun` (ScriptFuncs.fs `(<==)`); `command` would work too. Argument order: `lockOf outOf`
(the layout, partially applicable once) then `cfg descriptionPath`.

## How refs' locks are located

`lockOf` is a parameter (not `lockPath`): the primitive needs the refs' lock paths (`lockOf B c` for the
ref-config fallback), and its own path follows from it. Deriving refs' paths from the pattern of a lock
path would need to know which segments are name and config (a string replace breaks when the name
appears elsewhere, e.g. a project called `Release`); the script already has `lockOf`. A ref lock is read
with `Lock.load` (`needFiles`, JustFile since no file rule matches); missing in every config fails:
"'A': projectRef 'B' has no lock for ...; generate the lock of 'B' first (... e.g. via `update-locks`)".

Behaviour change: `Evaluation.ProjectRefs` now holds B's **name** (as the description names it). Rev. 1 put
B's project file when B was in the run's description set; without the set the primitive cannot know B's
description path short of another `descriptionOf` parameter. The format is unchanged; PR 4 src does not read it.

## What the tests prove (`src/hermetic.tests/LocksTests.fs`, 3 pure + fixture check + 5 Integration)

All integration tests run a small `xakeScript` with the rule above (a counter records `name::cfg` per run).
- Xake's three projects via `update-locks`: Xake first, each once; locks read back byte-identically, no
  `-o`, tokenized pathmap, ref `out/Release/<fwk>/Xake.dll` unhashed, `ProjectRefs = ["Xake"]`; second run
  with a ref output on disk rewrites nothing (timestamps equal).
- `update-locks` twice over App1, App2 -> Base: run 1 Base once (first) though two need it; run 2 every rule
  runs again once (`alwaysRerun`) and no lock is rewritten.
- **Fresh clone**: locks generated, `.xake` asserted present then deleted; a `build` phony that `Lock.load`s
  both locks, run twice with the lock rule in the script: zero lock-rule runs, locks read, files and
  timestamps unchanged.
- Debug App over Release-only Lib (rule variant needing refs in Release): `Locks.generate` without Lib's lock
  fails naming Lib; then `lock::App::Debug` runs Lib::Release, App::Debug, no Lib Debug lock; App net462
  gets `out/Release/netstandard2.0/Lib.dll`, `--optimize-`.
- `LOCK_REFRESH` via `update-locks`: 8.0.100 kept when the description floats (also with
  `LOCK_REFRESH=Other.Package`, not rewritten); `LOCK_REFRESH=FSharp.Core` floats (needs nuget.org).

## Removed / renamed symbols (for PR 4, #49, `pr/build-from-lock`)

- Removed `Locks.generateAll` (used in BuildFromLockTests.fs:12,73,201, docs/cheatsheet.md:102,
  docs/hermetic/lock.md:56,996-1048, docs/fingerprints.md:57 on that branch).
- Removed `Locks.ordered` (and its test "referenced projects come first").
- `Locks.generate` was private with another signature; now public as above. Nothing renamed otherwise.
- PR 4 tests can replace `rules [ label => Locks.generateAll descriptions configs lockOf outOf ]` by the lock
  rule plus `"update-locks" <== [...]` and `wantOverride ["update-locks"]` (see `lockRule` in LocksTests.fs).

## Commands

- `dotnet build src/hermetic --no-incremental`: `Build succeeded. 0 Warning(s) 0 Error(s)` (net462 + netstandard2.0)
- `dotnet test src/hermetic.tests`: `Passed! - Failed: 0, Passed: 194, Skipped: 0, Total: 194`
- No `src/core` change; `src/tests` not run.

## Remaining questions

1. `Evaluation.ProjectRefs` = B's name (simplest) vs a `descriptionOf` parameter to keep project files?
2. With the canonical rule, B's lock in A's config always exists, so `refConfig`'s Release/Debug fallback only
   matters when a script needs refs in another config: keep the fallback, or drop it (brief §6)?
3. Docs mention `docs/fingerprints.md` wiring now as `update-locks`; whether the lock rule records
   fingerprints is left open.
