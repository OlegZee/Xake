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
| `csc-syntax.md` | the `csc {}` task today: composed settings, `CscLock.compile`, one runner; `Project.import`; `Lock.Entry` (Evaluation / Compilation / Dependencies, section markers, the round-trip check, packages); compiler sources; recording a lock from settings |
| `lock-from-settings.md` | how a lock is obtained from composed settings: nine scenarios, migration (§9), the open update-mechanism question |
| `conceptual-review.md` | audit of slice 1 against Xake's model: no engine drift; two blurred seams |
| `import-race.md` | two brands importing one project concurrently: the race, the dead ends, the `Resource` fix; the assets copy stopgap, gone with the lock split |
| `restore.md` | `Restore`: the packages a lock names, one `dotnet restore` via `PackageDownload`, into a folder the build chooses (a build agent's cache) |
| `nuget-sbom.md` | `Nuget` (assets graph read at import into the lock, cache metadata) and `Sbom` (CycloneDX 1.6 from the lock entry, deterministic, per assembly and per package) |
| `verify.md` | `Verify`: Authenticode PE hash, labelled byte differences |
| `verify-dataengine.md` | the self-verification scenario on dataengine: 2 brands x 2 TFMs, 36/36 byte-identical, timings against stock restore+build, the concurrent-import defect, restoring packages from the lock |
| `strongname.md` | `StrongName`: PE stamp, checksum, strong-name re-sign reproducing csc |
| `pack.md` | `Pack`: deterministic zip and nupkg |
| `signing.md` | `Sign`: Authenticode/nupkg signing as a delegated rule, identity key, the fake-signer skeleton |
| `e3-restore-stability.md` | day-zero E3: is restore reproducible from the repository alone |
| `e5-shipped-vs-local.md` | day-zero E5: the shipped DataEngine 5.4.0 against the tag built here |

## Scripts (run against `.bootstrap/`, see `build.fsc.fsx` for the staging)
| File | What |
|---|---|
| `import.fsx` | dataengine: locks per brand, `build` from the locks, `show` |
| `import-page.fsx` | page + dataengine as siblings: 15 projects per lock, `build`, `sbom` |
| `verify-shipped.fsx` | `Verify` of two assemblies from the command line |
| `verify-dataengine.fsx` | dataengine across both TFMs and both brands: `locks`, `build`, `sbom`, `show` |
| `verify-dataengine.sh` | the whole scenario of `verify-dataengine.md` end to end, with timings |

Inspection artifacts (locks, comparisons, SBOMs) live in `samples/hermetic/{dataengine,page}/`;
`samples/hermetic/toolset/` is the fixture pinning the compiler via a package.
