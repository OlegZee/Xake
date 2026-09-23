# hermetic-build: what is in this folder

Read `session.md` first (where things stand, the next step), then `tracker.md` (work items).
`brief.md` holds the decisions; do not re-litigate them.

## Working state
| File | What |
|---|---|
| `session.md` | state and next step; traps; "What landed" per day |
| `tracker.md` | one-line work items with status, by slice |
| `brief.md` | the product brief: positioning, ActiveReports facts, design of the library (§11), day-zero results (§8j) |

## Design notes (one topic each)
| File | Topic |
|---|---|
| `csc-syntax.md` | the `csc {}` task today: composed settings, `CscLock.compile`, one runner; `Project.import`; `Lock`; compiler sources; recording a lock from settings |
| `lock-from-settings.md` | how a lock is obtained from composed settings: nine scenarios, migration (§9), the open update-mechanism question |
| `conceptual-review.md` | audit of slice 1 against Xake's model: no engine drift; two blurred seams |
| `import-race.md` | two brands importing one project concurrently: the race, the dead ends, the `Resource` fix |
| `nuget-sbom.md` | `Nuget` (assets graph, cache metadata) and `Sbom` (CycloneDX 1.6, deterministic, per assembly and per package) |
| `verify.md` | `Verify`: Authenticode PE hash, labelled byte differences |
| `strongname.md` | `StrongName`: PE stamp, checksum, strong-name re-sign reproducing csc |
| `pack.md` | `Pack`: deterministic zip and nupkg |
| `e3-restore-stability.md` | day-zero E3: is restore reproducible from the repository alone |
| `e5-shipped-vs-local.md` | day-zero E5: the shipped DataEngine 5.4.0 against the tag built here |

## Scripts (run against `.bootstrap/`, see `build.fsc.fsx` for the staging)
| File | What |
|---|---|
| `import.fsx` | dataengine: locks per brand, `build` from the locks, `show` |
| `import-page.fsx` | page + dataengine as siblings: 15 projects per lock, `build`, `sbom` |
| `verify-shipped.fsx` | `Verify` of two assemblies from the command line |

Inspection artifacts (locks, comparisons, SBOMs) live in `samples/hermetic/{dataengine,page}/`;
`samples/hermetic/toolset/` is the fixture pinning the compiler via a package.
