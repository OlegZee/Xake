# PR 6 report: Fingerprints library (library part only)

Worktree `.claude/worktrees/pr/fingerprints`, branch `pr/fingerprints` (from `origin/dev` bb0e990), not pushed.
Commits: 0b38e37 core module, 85299ea tests, ef3d83e docs.

## What landed
- `src/core/Fingerprints.fs` (~200 lines incl. doc comments), compiled after `Vars.fs`;
  `namespace Xake`, `[<RequireQualifiedAccess>] module Fingerprints`. No engine file touched.
- `src/tests/FingerprintsTests.fs` (9 tests), `docs/fingerprints.md`, `docs/cheatsheet.md` entry.

API:
```fsharp
type Line   = { Target: string; TargetHash: string; InputsHash: string; Inputs: string list }
type Status = Fresh | Stale | HandEdited | StaleAndHandEdited | MissingTarget
record : file -> target -> inputs: string list -> Recipe<ExecContext, unit>
warn   : file -> Recipe<ExecContext, unit>   // ctx.Logger Warning per non-fresh line, continues
check  : file -> Recipe<ExecContext, unit>   // failwith listing every non-fresh line
// pure: hashBytes, hashFile, normalizeCrLf, relativePath, normalizeInputs, inputsHash, compute root target inputs,
//       parse, format, load root file, save root file lines (bool: changed), upsert, recordIn root file target inputs (bool),
//       status root line, verify root file : (Line * Status) list, describe
```

## File format
One line per target, sorted by target (ordinal), LF, final newline, TAB-separated:
`target TAB target-sha256 TAB inputs-sha256 TAB input1 TAB input2 ...`
```
desc/a.json	3f0c...e1	9b2d...07	Directory.Build.props	src/a/a.fsproj
locks/a/Release.json	77aa...4c	c1e9...5d	desc/a.json
```
Paths relative to the project root (`ctx.Options.ProjectRoot` in recipes, explicit `root` in pure code),
`/` separators; a path outside the root stays absolute. Inputs de-duplicated and sorted (ordinal).

## Hashing rules
- File hash: SHA-256 lower-case hex of raw bytes with every CRLF pair replaced by LF; no UTF-8 detection,
  binary files take the same rule; lone CR and BOM hashed as is.
- Inputs hash: SHA-256 of UTF-8 `path TAB file-hash LF` over the sorted inputs; a missing input contributes
  `missing` (so a deleted input makes the line stale; a renamed input changes the hash).
- No database use: only the fingerprint file and the files it names are read.

## Deviations
- gaps.md row 8 shows space-separated fields; TAB used instead so paths with spaces stay unambiguous.
- `record` takes `inputs: string list` and normalises/sorts them itself; it also logs Info when the file changed.
- "Once per build" holds per `warn` call (one warning per target); calling `warn` twice in a run warns twice.
- A missing fingerprint file is treated as empty (warn/check pass silently).
- Statuses are split finer than "stale/hand-edited": both at once and missing target are their own cases.

## Build / test
- `dotnet build src/core --no-incremental` -> `0 Warning(s)`, `0 Error(s)` (net462 + netstandard2.0).
- `dotnet test src/tests` -> `Passed!  - Failed: 0, Passed: 323, Skipped: 1, Total: 324` (net8.0);
  Fingerprints filter alone: 9 passed.
- Tests use per-test temp folders under `Path.GetTempPath()` (`xake-fp-<guid>`), not cleaned up.

## Remaining for the wiring step (after PRs 3 and 5)
- `import`: call `Fingerprints.record "xake.fingerprints" desc imported.Inputs` per written/unchanged description
  (record even when `Written = false`, it is a no-op if nothing changed).
- `locks`: record each lock with the description path plus the refs' lock paths.
- Self-build script (PR 7): `"check" => Fingerprints.check ...`, `do! Fingerprints.warn ...` in `build`.
- Acceptance "touching a shared props file makes `build` warn once and use the committed file" needs the wiring.
- Fingerprint file path: if the hermetic side wants one file per brand, pass `<brand>/xake.fingerprints`.

## Questions for the user
1. TAB-separated fields (vs spaces in gaps.md) acceptable?
2. Should a missing `xake.fingerprints` make `check` fail (currently passes silently)?
3. Should `check` also fail on a described project without a fingerprint line, or is that left to `Lock.needAll`?
