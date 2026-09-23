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
