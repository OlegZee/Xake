# E5 -- shipped `MESCIUS.ActiveReports.Core.Data.DataEngine` vs local tag build

## Versions / tags

- nuget.org id `mescius.activereports.core.data.dataengine`, newest listed version: `5.4.0`
  (the `grapecity.*` id no longer resolves on the flatcontainer -- 404).
- Tag naming in `~/Projects-work/ar/ar-net-core-dataengine`: plain semver, no `v` prefix
  (`5.4.0`, prereleases as `5.4.0-alpha-1877` etc). Exact tag `5.4.0` exists and matches the
  shipped version -- no fallback needed.
- Tag `5.4.0` HEAD: `0e49450bb7f10a9430078efe4aed0a7646bb8fd0` (2026-05-18).

## What was compared

- Downloaded `mescius.activereports.core.data.dataengine.5.4.0.nupkg`, unzipped: `lib/net472/`
  and `lib/netstandard2.0/`, each `MESCIUS.ActiveReports.Core.Data.DataEngine.dll`, 378424 bytes.
- Built the tag in a detached worktree: `dotnet build src/DataEngine/DataEngine.csproj -c
  Release -p:Brand=MESCIUS -p:NuGetAudit=false`. `global.json` wants SDK `8.0.100` with
  `rollForward: latestFeature`; installed SDKs are `8.0.425` and `10.0.401` -- rollForward
  picked `8.0.425` and the build succeeded clean for both TFMs, 0 warnings/errors.
- Local DLLs: 365056 bytes (both TFMs).

## `Verify` output (netstandard2.0; net472 is the same pattern)

```
sha256 A (shipped)        = 9862a7575c9c386073bb26f6bbe42f0d949a27f502ddc9d3b03d501992449011
sha256 B (local)          = cadf6f1b8e1c856ad4f363443d91b583850796d9249c37a8d9f22e0d1c954e4c
authenticodeHash A        = b6a1fe51b6d254f2e50372042de0384b2d27e2d8d879608d2f39d35235f9dc56
authenticodeHash B        = 93218e1bf47ecc5ba05a5a1d51787363b2fc8b50ee6562f281c1d23084971f50
verdict: content differs (143 ranges)
```

`authenticodeHash` differs -- so this is *not* case (a) (same build, only signed). Field
breakdown of the 143 ranges: `TimeDateStamp` x1, `CheckSum` x1, `CertificateTable` x1,
`Content` x140, all 1-7 bytes long and scattered through the file. net472 gives the identical
pattern (140 Content ranges).

Size check: shipped 378424 - local 365056 = 13368 bytes, which is exactly the certificate
table (confirmed with a PE data-directory read: shipped entry 4 = offset 366592, size 11832,
local entry 4 = 0,0 -- unsigned) plus file-alignment padding. So **with the signature stripped
out, the two images are the same size** -- no code was inserted, moved, or removed.

`strings -n 6 <dll> | grep -ci babel`: **0** in both shipped and local. No obfuscator traces.

`AssemblyInformationalVersionAttribute` value in both shipped and local:
`5.4.0+0e49450bb7f10a9430078efe4aed0a7646bb8fd0` -- **the exact commit sha of the tag HEAD
built locally**. So the shipped dll was built from this same commit, not a different/later one.

## Interpretation: outcome (b)

Same size, same commit sha baked into `AssemblyInformationalVersion`, no obfuscator strings,
but the Authenticode hash (i.e. content modulo signature) differs in 140 small (1-7 byte)
scattered spots plus the `TimeDateStamp` field. This is **not** obfuscation (Babel or
otherwise) -- an obfuscator changes IL substantially and would show up as large content
blocks, not scattered few-byte diffs at constant size. It is also not "totally different"
(outcome c) -- structure, size and declared provenance all match.

Most consistent explanation: **non-deterministic or non-matching-toolchain compilation** of
otherwise identical source -- e.g. a Roslyn/SDK version different from the one used on
MESCIUS's build machine (only `8.0.425`/`10.0.401` are installed here; `global.json` pins
`8.0.100` and rolled forward), and/or the MVID and embedded absolute source paths differing
between build machines (no `/pathmap` and no proof `Deterministic=true` was pinned to the same
compiler build here as in CI). The repo's `Directory.Build.props` does not set
`Deterministic` explicitly at either level checked.

## What E5 means for the product pitch (brief S8b auditor mode)

**Today: no.** A customer cannot yet point this DLL at that tag and get a clean "yes, this is
the shipped bits" -- `Verify.authenticodeHash` differs, so the strongest claim achievable today
is "same commit, same size, no code additions" (which is already meaningfully more than nothing
-- rules out both a substituted binary and obfuscation-scale tampering), not bit-identical
reproduction.

To make an exact match possible, the build needs to record and pin, per release:
- the exact SDK/Roslyn compiler build (not just `global.json`'s `rollForward`, which allows
  drift) -- e.g. via `<RestorePackagesWithLockFile>`-style pinning of the compiler toolset feed;
- `/pathmap` (or `Deterministic=true` plus `ContinuousIntegrationBuild=true`, which maps source
  paths to a fixed root) so embedded source paths don't depend on the build machine's checkout
  location;
- if Babel (or any obfuscator) enters the real pipeline for other assemblies, a recorded,
  fixed seed, since obfuscators are the one step that is inherently non-reproducible without one;
- signing as a strictly separate, recorded step after a reproducible build, so `authenticodeHash`
  (which already excludes the signature) is the number customers verify against a CI-published
  value, not something they must approximate by rebuilding and hoping the toolchain matches.

With those four pieces, `Verify.authenticodeHash` becomes the auditor's one-line check: build
from the tag, hash, compare to the number MESCIUS publishes alongside the release.

## Follow-up: isolating the 140 differences (2026-09-24)

Fresh worktree at tag `5.4.0` (`de-tag2`), netstandard2.0, `-p:Brand=MESCIUS`, all builds clean.

**Embedded paths -- none to read.** The shipped dll has no CodeView/PDB debug directory entry
at all (`RSDS` signature absent) and `strings -n 4` finds zero `.cs`, `.pdb`, backslash, or
`/_/`-style paths anywhere in it -- only DigiCert CRL/OCSP URLs from the signature. So there is
no literal build-root string to read off the shipped binary or to pathmap against; the shipped
build emits no debug info into the DLL, embedded or referenced. This doesn't rule out source
paths still feeding the deterministic hash (Roslyn folds resolved source paths into the
checksum even with no `/debug` output), it just means hypothesis 1 can't be confirmed by
inspection -- only by matching build flags blind.

| Build | Flags (on top of the plain build) | vs shipped: ranges | notes |
|---|---|---|---|
| A | none (SDK 8.0.425 via `global.json` rollForward) | 143 (140 Content + TimeDateStamp + CheckSum + CertificateTable) | baseline, reproduces prior result exactly |
| B | `-p:ContinuousIntegrationBuild=true` | 143, same shape | **no change** vs shipped |
| D | `global.json` pinned to SDK 10.0.401 (no rollForward) | 202 | *worse*, not better |

Pairwise, to calibrate what each knob actually moves:
- **A vs B** (CI-build flag on/off, same compiler): 18 ranges, concentrated at
  `DebugDirectory/PDB id (MVID/GUID)` and a contiguous ~180+23+41+25-byte block right after the
  strong-name signature (the embedded PDB-checksum/path debug-directory record itself) plus one
  `StrongNameSignature` range. So `ContinuousIntegrationBuild=true` does exactly what it's
  documented to do -- it changes the deterministic MVID/PDB-checksum path-dependent bytes -- but
  that change doesn't move the count vs shipped at all, meaning **the shipped build's 143-range
  signature isn't explained by a bare/CI-path difference** in isolation.
- **A vs D** (SDK 8.0.425 vs 10.0.401, same source, same flags): **2783 ranges**, including
  contiguous blocks of 1582 and 1150 bytes -- a completely different magnitude and shape from
  the 140 scattered 1-7 byte ranges seen against shipped. A different Roslyn generation moves
  large contiguous regions (method bodies, attribute blobs), not a handful of isolated bytes.

**Conclusion.** The shipped dll's diff signature (140 small, scattered, 1-7 byte `Content`
ranges at constant size) does not match what a different compiler generation produces (A vs D:
large contiguous blocks, 2783 ranges) and is not fixed by toggling `ContinuousIntegrationBuild`
alone (B: same 143 vs shipped). That leaves **the same compiler generation as ours (8.0.425-era
Roslyn, not 10.0.401-era) but a different, unrecorded build root/pathmap state** as the best
remaining explanation for the 140 -- consistent with the original hypothesis, just not
independently provable here because the shipped dll carries no debug directory to read the
actual root back out of.

**What a customer would need from MESCIUS** to actually close this gap: (1) the SDK/Roslyn
version pinned for the release build (a `global.json` without `rollForward`, or the exact SDK
docker tag/CI image), since even being one feature band off changes thousands of bytes; (2)
whether `ContinuousIntegrationBuild=true`/`Deterministic=true` was set, and (3) the pathmap
value or build-root convention used (e.g. a fixed CI checkout path), since neither is
recoverable from the shipped artifact itself -- it ships with no PDB and no embedded path to
reverse-engineer from.

Cleanup: worktree `de-tag2` removed (`worktree remove --force` + `prune`), source repo
`~/Projects-work/ar/ar-net-core-dataengine` worktree list back to just the primary checkout.
Xake checkout (`git status --short`) was clean before and after this session's worktree/build
commands.

## Follow-up 2: no debug directory (2026-09-24)

Fresh worktree `de-tag3` at tag `5.4.0` (user checkout clean before/after). netstandard2.0.

| Build | Flags added | Size | vs shipped hash | ranges |
|---|---|---|---|---|
| control | none (= old baseline A) | 365056 | differs | 143 |
| E | `-p:DebugType=none -p:DebugSymbols=false` | 364544 | differs | 127 |
| F | E + `ContinuousIntegrationBuild=true` | 364544, byte-identical to E | differs, same as E | 127 |
| G | E + `Deterministic=true -p:PathMap=...` | 364544, byte-identical to E/F | differs, same as E | 127 |

`DebugType=none` matches shipped's "no CodeView/RSDS debug directory" trait and removes 16
ranges (a debug-directory/PDB-checksum cluster near `0x058eec`). CI-build and
Deterministic+PathMap add nothing further: E/F/G are byte-for-byte identical.

**Correction to Follow-up 1.** Its "140 small (1-7 byte) scattered ranges" claim only reflected
the tool's first-20-truncated output. Dumping all ranges (`dumpall.fsx`, scratch, not
committed) shows: small header-only diffs up to offset `0x28b`, then large contiguous blocks
for the rest of the file -- runs up to 34.8KB, 30.5KB, and one 148986-byte (145KB) block,
totaling most of a ~365KB file. This rules out "same compile, different MVID" -- an
MVID/pathmap delta cannot move 145KB of contiguous bytes. Control and E/F/G show the identical
big-block pattern against shipped; they differ from each other only in the small
debug-directory cluster DebugType=none removes.

**Anatomy of the first 5 ranges** (all PE/COFF header, all downstream of section-size
differences, none GUID-heap or signature bytes): `0x88` `TimeDateStamp`; `0x9d`/`0xa8`/`0xb1`
Optional Header size/data-directory RVA fields (shift because `.text` size differs);
`0xd8` `CheckSum`.

**Cake/pipeline check:** grep for DebugType/DebugSymbols/pathmap/ContinuousIntegrationBuild/
Deterministic across their `build.cake`, `verify-versions.cake`, `Directory.Build.props`
(root + src) found zero matches -- they set none of these, so "Build H" = the `control` row.

**Conclusion.** Outcome (a) is **not reached, and worse than previously believed**: the
shipped DLL diverges from every local variant by ~145KB of large content blocks, an order of
magnitude past anything these flags move. Best remaining explanation: a different Roslyn/SDK
build (Follow-up 1's own A-vs-D experiment showed a different compiler generation produces
exactly this large-contiguous-block shape) not available locally, or source differing from the
tag despite the matching commit-sha version string. `DebugType=none` is worth keeping (matches
shipped's no-debug-directory trait, removes 16 ranges) but is not sufficient; closing this
needs MESCIUS's exact SDK/Roslyn build, which isn't recoverable from the artifact.

Cleanup: worktree `de-tag3` removed; source repo back to just the primary checkout. Xake
checkout untouched.
