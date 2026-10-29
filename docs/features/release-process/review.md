# Release process review: Xake and Xake.Hermetic.Dotnet

Status: **analysis for a decision**, 2026-10-29. Nothing here is decided; the decision it asks
for is in the last section. Data: git tags and history up to `hermetic-v0.9.0` (02de528),
nuget.org flat-container indexes, `gh run list` of `publish.yml`; every number below is
reproducible from those (commit hashes are given).

## 1. The procedure today

A hermetic release that moves to a new Xake minor (0.9.0 on Xake 3.14, PR #104) took:

1. find the published Xake version on nuget.org (`v3.14.0` went out as `3.14.0.45`:
   `publish.yml` appends the run number);
2. the range `[3.14.0.45, 3.15)` in four places: the fsproj, the imported description
   `src/hermetic/xake.project.json`, the test fixture copy of it, a test assertion;
3. `build.fsc.fsx update-locks -d LOCK_REFRESH=Xake`, then revert the core locks the same
   command rewrote for an unrelated reason (the local SDK patch);
4. `build.fsc.fsx import`, because a hand edit of the description fails the fingerprint check;
5. versions in the package readme, the fsproj comment (a second changelog by now),
   `docs/session.md`, release notes;
6. merge, tag `hermetic-v0.9.0`, wait for the publish run;
7. later, a bootstrap PR for `build.fsx` / `build.fsc.fsx` with both `X.Y.Z.<run>` versions.

The package code did not change at all in 0.9.0 (only tests moved from `wantOverride` to
`want`). The release existed because the range said `< 3.14`.

## 2. Where the steps come from

Three policies, not the release itself, generate most of the work:

| Policy | Steps it causes |
|---|---|
| **P1** the run number is part of the version (`3.14.0.45`) | 1, the exact floors in 2, the `X.Y.Z.<run>` in 7; a floor of `3.14.0` would warn NU1603 on every restore |
| **P2** the upper bound is the next Xake minor (F# record binary compatibility) | a hermetic release for every Xake minor: 2-5 each time |
| **P3** `Xake.Dotnet` ships inside the `Xake` package, hermetic is a consumer of the published `Xake` | "release Xake first", the local-feed workflow (`NUGET_SOURCE`) while the base is unreleased, two tags and two runs per paired release |

The rest is duplication that a script or a single source would remove: the range copies (2),
the readme versions and the fsproj comment (5), the lock refresh touching locks it should not (3).

## 3. What the history says

### 3.1 Releases (3.4.0 to 3.14.0, 2026-10-01 .. 10-29)

- Xake: 13 releases, all published. Hermetic: 12 tags, 11 published (`hermetic-v0.4.0` was
  cancelled after a 1 h 57 min CI hang, run 31; 0.4.1 the same night; 0.6.0 never tagged,
  shipped inside 0.7.0).
- **Every hermetic release came 0 or 1 day after an Xake release: 12 of 12** (8 the same day,
  4 the next). Twice the two tags sit on one commit (v3.9.1 / 0.4.2, v3.10.1 / 0.5.2).
- Hermetic-only releases with the range unchanged: 4 (0.4.1, 0.4.2, 0.5.1, 0.5.2), all
  between 10-18 and 10-21.
- Publish runs since 3.4: 26, of which 1 failed (a trailing comma in the tag
  `hermetic-v0.3.0,`) and 1 was cancelled (the hang). **The run-number suffix was never used to
  republish a version**; the cancelled 0.4.0 was superseded by 0.4.1.
- Bootstrap-bump PRs: 4 (#67, #69, #74, #97), none yet for 3.14.0.45 / 0.9.0.46.

### 3.2 Why each hermetic range moved

| Hermetic | On Xake | Needed new API from | Record it builds changed | Verdict |
|---|---|---|---|---|
| 0.2.0 | 3.6 | `src/dotnet` (`HermeticMode`, `DotNetFwk`) | `Csc`/`Fsc` +`RuntimeConfig` | real |
| 0.3.0 | 3.7 | nothing (3.7 added only `Fingerprints`, unused) | none | **policy only** |
| 0.4.0 | 3.9 | `src/dotnet` (`Description`, `ProjectPlan`) | `CscSettingsType`/`FscSettingsType` +`Project` | real |
| 0.5.0 | 3.10 | — | `Throttle` in settings/run options; `ExecContext` +`HeldResources` | real (break) |
| 0.7.0 | 3.11 | `src/dotnet` (`Tool.quoteArgument`) | none (only the internal `RunPlan`) | real (API) |
| 0.8.0 | 3.13 | `src/dotnet` (`Tool.diagnosticLevel`, `DiagnosticLog`, `failOnExitCodeWith`) | none it builds | real (API) |
| 0.9.0 | 3.14 | nothing | none it builds | **policy only** |

Also: **3.12 had no hermetic release, so hermetic users could not take 3.12 for three days**
(10-24 .. 10-27, until 0.8.0 skipped to 3.13). 3.5 and 3.8 had none either.

Two observations carry the rest of the review:

- **Every API hermetic needed came from `src/dotnet`, never from the engine** (4 of 4).
- **Binary breaks have stopped.** Records hermetic builds (`CscSettingsType`, `FscSettingsType`,
  `Csc`, `Fsc`, `RunOptions`, `FscRunOptions`; and from the engine only `ExecContext`, by one
  copy-and-update in `src/hermetic/Lock.fs:1598`) last changed in 3.10. The breaks flagged in
  the 3.13 and 3.14 release notes (`OptionalVar.Secret`, `VarHelp.IsSecret`, `ExecOptions.Setup`)
  are in records hermetic does not construct, so by its records 0.8.0 would have run on 3.14
  (inferred from the record diffs, not run).

### 3.3 What changes in an Xake release

| | engine only | `src/dotnet` only | both |
|---|---|---|---|
| releases 3.4..3.14 | 3 (3.7, 3.10.1, 3.12) | 4 (3.5, 3.6, 3.9, 3.11) | 6 |

`src/dotnet` changed in 10 of 13 releases — more often than the engine — and its large
additions were hermetic's needs: `HermeticMode`, `Description`/`ProjectPlan`, the `Tool`
diagnostics. The compiler records grew in 5 releases (3.4, 3.5, 3.6, 3.9, 3.10), none since.

### 3.4 Where the original decisions came from

- `Xake.Dotnet` was a separate repository and package (1.x era); it was merged into the `Xake`
  package in 3.3.0 (7f6a11d, PR #16) so one `#r` gives both and the second build machinery goes
  away. It reaches two engine internals through `InternalsVisibleTo("Xake.Dotnet")`:
  `ProcessExec.pexecSync` (5 call sites) and `CommonLib.memoize` (2), all in `DotNetFwk.fs`.
- Independent versions were chosen in `docs/features/hermetic-build/extraction-plan.md` §4:
  hermetic at 0.x on `Xake [3.4.0, 4.0)`, so that hermetic-only changes do not force Xake
  releases; lockstep "only if IVT turns out to be needed". On 2026-10-01 the hermetic package
  was switched to the *published* `Xake`.
- The `4.0` upper bound lasted until bb0e990 (2026-10-11), when 3.6's `RuntimeConfig` broke
  hermetic 0.1 at run time; since then the bound is the next minor.

## 4. Options

### A. Publish exactly `X.Y.Z` (drop the run number)

Floors equal tags, no nuget.org lookup, no NU1603 trap, bootstrap versions are guessable.
Costs nothing that was used: no version was ever republished, a failed release took the next
patch. Next versions sort correctly after the current four-part ones (`3.15.0` > `3.14.0.45`,
`0.10.0` > `0.9.0.46`). Independent of every other option.

### B. Upper bound back to `4.0`, guarded by a rule and CI

- Rule: until 4.0, no new fields in the records hermetic builds; new settings go through new
  functions or builder operations. The engine side shrinks to nothing if `Lock.fs:1598` uses a
  helper (e.g. `ExecContext.withLogger`) instead of a record copy.
- Guard: a CI job that runs `src/hermetic.tests` against `src/core` + `src/dotnet` of `dev`
  (catches a break before it is released) and one against the newest published `Xake`.
- History: would have removed 0.3.0 and 0.9.0 and the 3.12 lockout; 0.2.0, 0.4.0, 0.5.0 were
  real breaks that the rule would have had to prevent, all before 3.10.
- Leaves P3: whenever hermetic needs new `src/dotnet` API (4 of 7 range moves), still "release
  Xake first", local feed, two tags.

### C. Lockstep: one tag publishes both packages

Removes P1-P3 at once: hermetic built from the project references, exact dependency on the same
release. History says it would have cost 4 extra Xake patch releases (0.4.1, 0.4.2, 0.5.1,
0.5.2; two of them already shared a commit with an Xake patch). The cost grows as the engine
stabilizes: most releases would be hermetic changes shipped as an unchanged Xake. The 0.x
"preview" signal needs another carrier (readme, or a prerelease suffix).

### D. Move `Xake.Dotnet` out of the `Xake` package, into hermetic's release line

The coupling the data shows is `src/dotnet` <-> hermetic, not engine <-> hermetic. Releasing
`Xake.Dotnet` and `Xake.Hermetic.Dotnet` together (one tag, two packages or one) puts the
records and the APIs hermetic needs inside one release; hermetic and `Xake.Dotnet` depend on
the engine only, and the engine's surface for them is small and stable (`ExecContext`, recipes,
`Tool`-level plumbing).

- History: 6 of the 7 range moves would have been one release instead of two; the exception is
  0.5.0, whose break was the engine's `ExecContext.HeldResources` (B's rule covers that record).
  0.3.0, 0.9.0 and the 3.12 lockout disappear; engine releases (3.7, 3.10.1, 3.12, and the coming vars
  evaluator / pattern module) touch nothing .NET.
- Costs:
  - users: `#r "nuget: Xake"` no longer brings `csc`/`fsc`. Either a breaking 4.0, or the engine
    becomes `Xake.Core` and `Xake` a meta-package depending on `Xake.Core` + `Xake.Dotnet` (no
    script changes; the meta-package needs a release whenever its ranges must move);
  - `InternalsVisibleTo`: `pexecSync` and `memoize` become public (or `Xake.Dotnet` keeps its
    own small copies, which 3.3 deliberately removed);
  - moving the `src/dotnet` tests out of `src/tests`, `build.fsx` / `build.fsc.fsx`, docs, CI;
  - `Xake.Dotnet` users without hermetic get releases driven by hermetic work (that is where
    `src/dotnet` changes come from anyway).
- If the owner's expectation holds that the .NET part is stable, D's benefit shrinks to what B
  already gives; if hermetic keeps asking `src/dotnet` for new API, D is the only option that
  removes "base first".

### E. Script what remains (any option)

One command (e.g. `build.fsc.fsx bump-base`): read the Xake version, set the fsproj range,
`import`, refresh only hermetic's lock. Tests read the range from the fsproj; readmes use
unversioned `#r`; the base history moves from the fsproj comment to release notes. Optionally a
workflow that opens the bootstrap PR after a publish run.

## 5. Recommendation

1. **Now: A + B + E.** Cheap, no user-facing change, and by the history would have removed two
   of eleven hermetic releases, the three-day lockout on 3.12 and every nuget.org lookup.
   B's rule is credible because the compiler records have not changed since 3.10.
2. **Reconsider D** at the next point where hermetic needs new `src/dotnet` API, with the
   meta-package variant so no script breaks. That moment is the evidence that the .NET part is
   still moving with hermetic; if it does not come, A + B is enough.
3. **Not C**: with a stabilizing engine it turns most hermetic changes into empty Xake releases.

## 6. Decision needed

- A (exact `X.Y.Z`): yes / no.
- B: the rule "no new fields in `CscSettingsType`, `FscSettingsType`, `Csc`, `Fsc`,
  `RunOptions`, `FscRunOptions`, `ExecContext` until 4.0" — accepted?
- D: now, at the next `src/dotnet` need, or not at all; and if it comes, meta-package `Xake` or
  a breaking 4.0.
