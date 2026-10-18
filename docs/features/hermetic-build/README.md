# hermetic-build: what is in this folder

Read `session.md` first (where things stand, the next step), then `tracker.md` (work items).
`brief.md` holds the decisions; do not re-litigate them.

## Working state
| File | What |
|---|---|
| `session.md` | state and next step; traps; "What landed" per day |
| `tracker.md` | one-line work items with status, by slice |
| `brief.md` | the product brief stub: positioning (the customer-specific brief is kept privately) |

Product documentation (the lock, restore, SBOM, verify, pack, strong name, signing) moved to
[`docs/hermetic/`](../../hermetic/README.md) (E5); `csc-syntax.md` and `csc-server.md` are base
docs in `docs/`. Links to the moved files below point there; a bare `restore.md`, `nuget-sbom.md`, `verify.md`,
`pack.md`, `strongname.md` or `signing.md` in the older notes means the file in `docs/hermetic/`, and
`csc-syntax.md`/`csc-server.md` the ones in `docs/`.

## Design notes (one topic each)
| File | Topic |
|---|---|
| `api.md` | the public surface of `Xake.Dotnet` at the time (dated snapshot), one table per module, awkward names flagged |
| [`csc-syntax.md`](../../csc-syntax.md) | the `csc {}` task: composed settings, compiler sources, the runner `Csc.run`; the lock parts (`Lock.*`, `Project.import`, the file format) are in [`hermetic/lock.md`](../../hermetic/lock.md) |
| `signing-skeleton.md` | the signing skeleton's plan and acceptance criteria, split out of `docs/hermetic/signing.md` |
| `lock-from-settings.md` | how a lock is obtained from composed settings: nine scenarios, migration (§9), the open update-mechanism question |
| `conceptual-review.md` | audit of slice 1 against Xake's model: no engine drift; two blurred seams |
| [`csc-server.md`](../../csc-server.md) | `csc` through the Roslyn compiler server: `/shared`, `CompilerServer`, `noserver`/`keepalive`, `XAKE_CSC_SERVER` |
| `import-race.md` | two brands importing one project concurrently: the race, the dead ends, the `Resource` fix; the assets copy stopgap, gone with the lock split |
| [`restore.md`](../../hermetic/restore.md) | `Restore`: the packages a lock names, one `dotnet restore` via `PackageDownload`, into a folder the build chooses (a build agent's cache) |
| [`nuget-sbom.md`](../../hermetic/nuget-sbom.md) | `Nuget` (assets graph read at import into the lock, cache metadata) and `Sbom` (CycloneDX 1.6 from the lock entry, deterministic, per assembly and per package) |
| [`verify.md`](../../hermetic/verify.md) | `Verify`: Authenticode PE hash, labelled byte differences |
| `the repo-a verification notes` | the self-verification scenario on repo-a: 2 brands x 2 TFMs, 36/36 byte-identical, timings against stock restore+build, the concurrent-import defect, restoring packages from the lock |
| [`strongname.md`](../../hermetic/strongname.md) | `StrongName`: PE stamp, checksum, strong-name re-sign reproducing csc |
| [`pack.md`](../../hermetic/pack.md) | `Pack`: deterministic zip and nupkg |
| [`signing.md`](../../hermetic/signing.md) | `Sign`: Authenticode/nupkg signing as a delegated rule, identity key, the fake-signer skeleton |
| `overview-ru.md` | the branch in Russian, with a staged-release recommendation |

## Scripts (run against `.bootstrap/`, see `build.fsc.fsx` for the staging)
| File | What |
|---|---|
| `the repo-a verification script` | repo-a across both TFMs and both brands: `locks`, `build`, `sbom`, `show` |
| `the repo-a verification runner` | the whole scenario of `the repo-a verification notes` end to end, with timings |

Inspection artifacts of the trial project (locks, comparisons, SBOMs) are kept privately;
`samples/hermetic/toolset/` is the fixture pinning the compiler via a package.
