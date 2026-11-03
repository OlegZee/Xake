# PR 4 report: build from a lock into a layout (`Locks.compileAs`, `Locks.needAll`)

Branch `pr/build-from-lock` (worktree `.claude/worktrees/pr/build-from-lock`, on `pr/lock-generate` 434743c),
3 commits, not pushed: 8f424e8 code, 92bf454 tests + fixture, 0bd5848 docs.

## What landed

- `src/hermetic/Locks.fs` (+157 with doc comments), module `Locks`:
  - `compileAs : string -> string -> (string * RequiredVar<string>) list -> Recipe<ExecContext, unit>` (`lockPath fwk versions`)
  - `needAll : Fileset -> string -> (string -> string -> string) -> (string -> string -> string -> string) -> Recipe<ExecContext, unit>` (`descriptions cfg lockOf outOf`)
  - `versionFor : (string * 'v) list -> string -> 'v option` (pure, the mask choice), `VersionToken = "$(Version)"`
- `src/hermetic/Lock.fs` (+6): `Lock.compileFrom lockPath options entry` = the private `compileEntry (Some lockPath)`,
  i.e. `compileWith` with the lock path the HERMETIC refusal names. `Lock.build`/`Described.buildCsc` not forked or changed.
- `src/hermetic.tests/BuildFromLockTests.fs` (7 tests: 1 pure, 6 Integration), rule shape as the draft
  (`command "build" { Locks.needAll ... }`, `target "out/(cfg:*)/(fwk:*)/(name:*).dll" { ... Locks.compileAs ... }`).
- Docs: `docs/hermetic/lock.md` section "Building from Xake-generated locks" + 3 API rows; `docs/cheatsheet.md` lines.

## `-o` and `--doc:`

`compileAs` takes the rule's target (`getTargetFile`), checks its file name is the entry's assembly name, removes any
output switch and inserts `--out:<target>` (`/out:` for C#) where `--doc:` is, else before the first section marker.
A `--doc:`/`/doc:` the lock carries is re-targeted to `<target>.xml`. The `.xml` is never a target, so a Debug lock
without doc expects nothing extra: test "Debug ... second build does nothing" (dll timestamps unchanged).

## Dependencies

- The lock: `Lock.load` (needFiles).
- Every reference, hashed and unhashed, is `needFiles`d before the compile: a project-ref output
  (`$(ProjectRoot)/out/Release/netstandard2.0/B.dll`, unhashed in the lock) is built first by the same layout rule,
  and its change rebuilds the referencer (test: editing B rebuilds B then A).
- C# masks: `Described.expandCsc` (`getFiles` -> `GetFiles` dependency); an added `.cs` rebuilds, lock unchanged.
- The runner adds sources, the compiler, generated files.

## `$(Version)`

Only when some `Generated` content contains `$(Version)`: the project path is `<folder>/xake.project.json` relative to
the project root, folder = where the first non-generated source lies (the wildcard-free part of a C# mask); the first
`versions` mask matching it (a trailing `**` covers every file below) gives a `RequiredVar<string>`, bound inside the
recipe, so the variable is a dependency and a new value rebuilds. No match fails naming the masks. The lock keeps the
token. Locks generated today carry no token (PR 2 generates no version attributes; brief §6 open), so the test
injects a `Generated` file into a lock.

## PR #42 logic gone

No `intermediate`/`unbuilt`/`produced`/`mapped`, no `Lock.mapPaths` re-pointing: the lock's recorded ref paths are the
layout paths, needed as they are.

## Deviations

1. `versions` is `(mask * RequiredVar<string>) list`, not `(mask * string)`: the draft writes `vars.Version` (a
   `RequiredVar`), and a plain string computed outside the recipe would not rebuild on a version change.
2. Fixture `fixtures/src/hermetic/xake.project.json` gains `byFramework.net462.references: ["System.IO.Compression"]`:
   msbuild compiles hermetic net462 against the SDK's netstandard facades (`Microsoft.NET.Build.Extensions/net461/lib/
   System.IO.Compression.dll`), which `Described` does not expand; without it Sbom.fs/Sign.fs fail (`ZipArchive`).
3. `needAll` needs the description's frameworks (per the task) and only checks the lock exists; a framework missing
   from the lock fails in `compileAs`, naming the lock's frameworks.
4. The rule's `name` capture is used as the lock name: correct while description name = assembly name (true for Xake).

## Commands

- `dotnet build src/hermetic --no-incremental`: `Build succeeded. 0 Warning(s) 0 Error(s)` (net462 + netstandard2.0)
- `dotnet test src/hermetic.tests`: `Passed!  - Failed: 0, Passed: 196, Skipped: 0, Total: 196, Duration: 50 s` (189 + 7)
- `dotnet test src/tests`: not run, nothing shared touched (no src/core, src/dotnet change).
- The Xake three-projects test compiles 6 assemblies from the generated locks in about 9 s.

## Questions for the user

1. `versions` typed as `RequiredVar<string>` (as the draft writes it): OK, or also a plain-string form?
2. "Project path" for `versions` = folder of the first source + `/xake.project.json`: acceptable, or should the lock
   (or `compileAs`) get the description path? (The lock has no slot for the own project file of a composed entry.)
3. netstandard facade expansion for net462 (deviation 2): fix in `Described` (a later PR) or keep it in descriptions?
4. Rule `name` = lock name = assembly name: fine for now, or should `compileAs` map assembly -> project?

## Follow-up after the #48 rework (2026-10-11)

Branch `pr/build-from-lock` (worktree `.claude/worktrees/pr/build-from-lock`), not pushed. Commits:
- `43b18f2` merge of `pr/lock-generate` (8985cde). Conflicts in `Locks.fs` (dropped `generateAll`, kept
  `VersionToken`/`compileAs`/`needAll`), `docs/cheatsheet.md` and `docs/hermetic/lock.md` (API table: #48's
  `Locks.generate` row + PR 4's `compileAs`/`needAll`/`compileFrom` rows). `needAll`'s missing-lock error now
  says "generate the locks first (e.g. the script's `update-locks`, over Locks.generate)".
- `7660eba` the canonical `lockRule` / `updateLocks` moved from `LocksTests.fs` into a shared test module
  `src/hermetic.tests/LockRule.fs` (`LockRule.lockRule lockOf outOf descriptionOf refCfg ran`,
  `LockRule.updateLocks configs projects`); `LocksTests` binds thin wrappers, behaviour unchanged.
- `d8b91d8` `BuildFromLockTests.fs`: every fixture's locks come from `update-locks` over the `lock::` rule
  (A,B in Release+Debug; Cs; B; Xake's three by name -> `desc/<dir>/xake.project.json`); the missing-lock
  test asserts "`update-locks`". New test "a fresh clone builds from the committed locks without running a
  lock rule": `update-locks` runs B then A; `.xake` and `out/` deleted; `build` (needAll + compileAs, lock rule
  and update-locks in the script) run twice: zero lock-rule runs, both dlls built, second run rebuilds
  nothing, lock texts and timestamps unchanged.
- Docs: lock.md "Building from Xake-generated locks" opens with the whole story (`update-locks` -> committed
  locks -> `build` via `Locks.needAll` + `out/(cfg:*)/(fwk:*)/(name:*).dll` with `Locks.compileAs`; build never
  runs a lock rule, so a fresh clone/CI builds from what was committed); `generateAll`/`locks` command
  mentions replaced. Cheatsheet: #48's lock-rule lines then "Build from the committed locks".

`Evaluation.ProjectRefs`: verified PR 4 source does not read it (`compileAs` uses the unhashed reference
paths; `Described.fs` reads the description's `ProjectRefs`, which were names already).

Results: `dotnet build src/hermetic --no-incremental`: `Build succeeded.` 0 Warning(s) 0 Error(s) (net462 +
netstandard2.0). `dotnet test src/hermetic.tests`: `Passed! - Failed: 0, Passed: 202, Skipped: 0, Total: 202`
(BuildFromLock fixture alone: 8/8).

Left: nothing new; the PR 4 open questions above stand. The PR description of #49 still mentions
`generateAll` if it did before -- update it when pushing.
