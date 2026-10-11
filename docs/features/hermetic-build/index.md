# feature/hermetic-build: index of the working notes

Working state of branch `feature/hermetic-build` (local, 51 commits ahead of
`origin/feature/hermetic-build`, not pushed). Up: [../../index.md](../../index.md). Product docs
moved to [../../hermetic/](../../hermetic/index.md); a bare `lock.md`, `restore.md`, `pack.md`
etc. in older notes means the file there. Lengths are approximate line counts.

Column **fsc task**: does a fresh session reworking `build.fsc.fsx` (PR #42) need this file.
Kickoff for that session: [next-session.md](next-session.md).

## Working state

| File | What it is | Read when | fsc task | Lines | Date | Status |
|---|---|---|---|---|---|---|
| [next-session.md](next-session.md) | **RU.** kickoff for the `build.fsc.fsx` rework session: goal, state, user's remarks, reading order, interview plan | starting that session | **yes, first** | ~140 | 2026-10-11 | current |
| [session.md](session.md) | session log, mixed order: 09-29 and 09-23 at top, then branch notes on the fsc build (line ~610), then B2..E7 and dated entries to 2026-10-11 at the end | where things stand, next step | **yes: last two sections only** (2026-10-04, 2026-10-11); "Branch notes: the fsc-based build script" (~610-715) only if the old `Fsproj` design comes up | ~1060 | 2026-10-11 | current at the end, history above |
| [tracker.md](tracker.md) | one-line work items by slice, status headers per day | checking whether an item was done or dropped | no | ~170 | 2026-10-02 | stale since 2026-10-02; session.md is ahead |
| [README.md](README.md) | older folder map: working state, design notes, scripts | (replaced by this index) | no | ~50 | 2026-09-30 | superseded by index.md |
| [brief.md](brief.md) | product brief: positioning, the trial product facts, library design (§11), day-zero results | product or customer questions; decisions live here | no; only if the the trial product experiment needs customer facts (brands, repos) | ~955 | 2026-09-30 | historical draft, decisions binding |

## Design notes

| File | What it is | Read when | fsc task | Lines | Date | Status |
|---|---|---|---|---|---|---|
| [fsc-design.md](fsc-design.md) | B6 design: `Fsc` record through the shared runner, `fsc { lock }`, fsproj import, user's decisions (§10) | F# compile or import mechanics | only §6 (hermetic side), if import mechanics come up | ~415 | 2026-10-04 | implemented in 3.6 / 0.2; historical |
| [hermetic-mode.md](hermetic-mode.md) | design note of `HERMETIC=on` with the implementation plan | history of the mode's decisions | no; read `docs/hermetic/hermetic-mode.md` instead | ~230 | 2026-10-09 | superseded by docs/hermetic/hermetic-mode.md |
| [extraction-plan.md](extraction-plan.md) | plan to split `Xake.Hermetic` out of `Xake.Dotnet`, `Csc` record, `resolve`, no hooks | history of the package split | no | ~330 | 2026-10-01 | done; historical |
| [api.md](api.md) | public surface snapshot of 2026-09-29 with awkward names flagged | history of renames | no | ~390 | 2026-09-30 | historical snapshot, names outdated |
| [lock-from-settings.md](lock-from-settings.md) | how a lock comes from composed `csc` settings: nine scenarios, migration | history of `csc { lock }` | no | ~315 | 2026-09-30 | historical, shipped as `csc { lock }` |
| [conceptual-review.md](conceptual-review.md) | audit of slice 1 against Xake's model, status table | history | no | ~375 | 2026-09-29 | historical |
| [signing-skeleton.md](signing-skeleton.md) | plan and acceptance criteria of the signing skeleton | signing work | no | ~65 | 2026-09-30 | historical, landed |
| [import-race.md](import-race.md) | two brands importing one project concurrently: race, dead ends, per-project lock fix | concurrent `Project.import` problems | no; only if parallel import of variants comes up | ~90 | 2026-09-29 | historical, fixed |

## Experiments and overviews

| File | What it is | Read when | fsc task | Lines | Date | Status |
|---|---|---|---|---|---|---|
| [workflows-ru.md](workflows-ru.md) | **RU.** owner's guide: why, hermetic mode (§2), what is in the repo (§3), scenarios A-F, what is not done | explaining flows in Russian | **yes: §2-4** | ~610 | 2026-10-09 | current as of 3.5 / 0.2 previews |
| [overview-ru.md](overview-ru.md) | **RU.** branch overview and odds of release, 2026-09-23 | history | no | ~265 | 2026-09-30 | historical |
| [the repo-a verification notes](the repo-a verification notes) | self-verification on repo-a: 2 brands x 2 TFMs, byte identity, timings | proof on a real customer repo | no; only as a reference for the the trial product experiment | ~395 | 2026-10-02 | historical measurements, 3.4 / 0.1 |
| [e3-restore-stability.md](e3-restore-stability.md) | day-zero E3: restore reproducibility of CoreLib and Page | restore stability questions | no | ~120 | 2026-09-23 | historical |
| [e5-shipped-vs-local.md](e5-shipped-vs-local.md) | day-zero E5: shipped CoreLib 5.4.0 vs local tag build | shipped-vs-local verification | no | ~185 | 2026-09-23 | historical |

## Proving scripts (fsx / sh)

| File | What it is | Read when | fsc task | Lines | Date | Status |
|---|---|---|---|---|---|---|
| [import.fsx](import.fsx) | imports repo-a's C# projects into one lock per brand; `locks`, `show`, `build` | example of an import-based script | only if comparing script shapes for the trial product (remark 6) | ~130 | 2026-10-02 | on 3.4 / 0.1 API |
| [import-page.fsx](import-page.fsx) | page + repo-a as sibling repos, 15 projects per lock, extra root `$(RepoARoot)` | cross-repo import | only if the two-repo the trial product experiment comes up | ~235 | 2026-10-02 | on 3.4 / 0.1 API |
| [the repo-a verification script](the repo-a verification script) | repo-a across both TFMs and brands: `locks`, `build`, `sbom`, `restore`, `show` | the widest existing import script | only as a size/shape reference for remark 6 | ~215 | 2026-10-02 | on 3.4 / 0.1 API |
| [the repo-a verification runner](the repo-a verification runner) | end-to-end scenario of the repo-a verification notes with timings | rerunning the proof | no | ~190 | 2026-10-02 | on 3.4 / 0.1 |
| [verify-shipped.fsx](verify-shipped.fsx) | `Verify` of two assemblies from the command line | comparing two dlls | no | ~30 | 2026-10-02 | on 3.4 / 0.1 |
