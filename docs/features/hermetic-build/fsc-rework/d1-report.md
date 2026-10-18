# D1 report: extra roots for generated locks, dotnet-sdk prerequisite, A1 critic fixes

Branch `pr/lock-roots` (worktree `.claude/worktrees/pr/lock-roots`), stacked on `pr/batch-restore` c2b5eb2.
6 commits 79f24eb..550bac3, not pushed. No `src/core` change. The lock format is unchanged.

| commit | item |
|---|---|
| 79f24eb | G10 extra roots + pathmap per root |
| c9b762c | G11 (test only, see below) |
| 44d7c7e | A1 critic F1, F3, the WarningsAsErrors nit (docs) |
| ff683b0 | self-build locks + `xake.fingerprints` regenerated |
| 4d7841b | docs: lock.md (roots), hermetic-mode.md (generated locks under the mode) |
| 550bac3 | critic-b: required fix + nits 1, 2, 4 (see "After the critic") |

## Roots API (G10)
`Locks.generateWith roots`, `compileAsWith roots`, `needAllWith roots`, `restoreAllWith roots`, where `roots` is
`(string * string) list` in `Roots.make` form: `[ "$(Sibling)", "../sibling-repo" ]` (relative to the project root,
or absolute). The old functions are the `With []` forms, with unchanged signatures. `generateWith` writes with
`Roots.currentWith roots`, reads the referenced locks with `Lock.loadWith roots`, and adds one
`pathmap:<root>/=/<Name>/` per extra root in front of the project root's `/_/`. In `Lock.fs`, the new
`Lock.recordedWith extraRoots` and `Lock.compileFromWith extraRoots` make HERMETIC count a path under an extra root as
"under the project root", at record time and at replay (existing `recorded`/`compileFrom` = `[]`).
Test: a C# project in `../Sibling` with a mask and a key file. Its lock names `$(SiblingRoot)/P/**/*.cs`,
`/keyfile:$(SiblingRoot)/P/key.snk`, `Directory` `$(SiblingRoot)/P` and both pathmaps, and no absolute path. The
`CallerFilePath` in the dll is `/SiblingRoot/P/A.cs`. A copy of both folders at another path builds from the same
lock into **identical bytes**. Not checked: the trial project probe itself (it uses the published package; round E).

## G11: no code change was needed
`Locks.generate` already records the prerequisite: it calls `Lock.recorded` → `withPrerequisites true`, exactly like
`Lock.record`, and that includes the unpinned warning. The new test passes on the existing code: with an exact pin and
`HERMETIC=on` + `NUGET_PACKAGES` + `CSC_TOOLSET`, `update-locks` and `build` succeed for F# (fsc is covered by
`dotnet-sdk`) and for C# (toolset csc). Without a pin the generation warns and records no prerequisite. the trial project's
refusal had two causes. Its pin is `8.0.100 latestFeature`, which is not exact, so no prerequisite can exist. And it
uses the SDK's csc with no `CSC_TOOLSET`. By design (hermetic-mode.md) a composed csc needs the toolset, and the SDK
csc is covered only for *imported* entries. Without `CSC_TOOLSET` a generated C# lock now fails at `update-locks`
with message 3 (asserted). **Question 1:** should a generated C# lock's SDK csc count as "chosen by msbuild"
(covered by the prerequisite, like an import)? Today it does not. Changing that means marking generated entries,
e.g. `Evaluation.Project` = the description path.

## A1 critic
- F1: `everett` compares the folder's own span. `a/_/x.txt` and `a/-/x.txt` now give `Root.a.__.x.txt` (unit cases
  added).
- F3: Import names resources as msbuild does, checked against `dotnet build` in the fidelity fixture (manifest lists
  equal). `Link="Shared\_\Linked.txt"` gives `Acme.Fid.Shared.__.Linked.txt`, which also confirms F1 in msbuild. A resx
  with `ManifestResourceName="Custom.Other"` gives `Custom.Other.resources`. A plain file with `ManifestResourceName`
  gives just **the file name** (`named.txt`), not the MRN: msbuild sets no LogicalName for it, and the result was
  measured, not guessed.
- Nit: description.md says the `WarningsAsErrors`/`WarningsNotAsErrors` lists are not imported. It also documents
  Link/MRN and the `__` rule.

## Self-build locks (scratch script `/Users/olegz/.claude/jobs/ca6a2625/tmp/d1/regen-locks.fsx`, build.fsc.fsx's rule)
Diff: `Directory` `$(ProjectRoot)` → `$(ProjectRoot)/src/{core,dotnet,hermetic}` (6 entries). Xake.Hermetic.Dotnet
net462 gains a hashed `System.Windows.Forms.dll` from `microsoft.netframework.referenceassemblies.net462`, from the
Xake package's `frameworkAssemblies` (G6). No transitive outputs change (Xake.Dotnet → Xake is one level) and there
is no nowarn change (F#). `xake.fingerprints`: 3 lock lines. A rerun after the critic fixes writes nothing.
`build.fsc.fsx` itself follows after a 0.3.x release (it `#r`s the published 0.3.0.28).

## Build and tests (verbatim)
- `dotnet build src/hermetic --no-incremental`: `0 Warning(s)` `0 Error(s)` (netstandard2.0 + net462)
- `Passed!  - Failed:     0, Passed:   226, Skipped:     0, Total:   226, Duration: 1 m 18 s - hermetic.tests.dll (net8.0)`

## Deviations / questions
- G11 needed no code, only a test (above). Question 1 is open.
- `versions` masks (`$(Version)`) see a sibling-root project by its absolute folder. No mask form for extra roots yet.
- The F# "another checkout folder" 59-byte difference (lock.md) did not show for this C# case: the bytes were identical.

## After the critic (critic-b.md)
- Required: `needAll`'s union `Restore.ensure` is wrapped in a try/with. A failure (or its problems list) becomes a
  warning ("fetching the packages of the Release locks at once failed; each compile restores its own") and the build
  continues to `need targets`. Test: Cs's lock names a missing package. B builds, Cs fails with the missing package
  named, and the warning is logged. (Xake has no `-k`; the engine already runs the independent compile.)
- Nit 1: `flowingPackages` uses `restoreInput` on both sides, so a reference's implicit `System.ValueTuple` flows,
  and a referrer that has it implicitly gets it once. Unit test added.
- Nit 2: framework-assembly/global refs are deduplicated case-insensitively. This deviates from "OS path
  comparison": these are assembly names, which are case-insensitive in .NET on every OS. Ordinal on Unix would not
  fix `WindowsBase`/`windowsbase`.
- Nit 4 (the coordinator's "nit 3"): `prefetch` takes the per-key `Resource`s in sorted key order and re-checks the
  cache inside. No deadlock: single restores hold one key only.
