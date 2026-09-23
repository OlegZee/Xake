# Deterministic pack (`Pack.fs`, brief §8f/§11)

A `.nupkg` is a zip. `dotnet pack` does not produce the same bytes twice:

- **Entry timestamps are wall-clock** — each `ZipArchiveEntry.LastWriteTime` is the moment
  `pack` ran, not a function of the inputs.
- **`[Content_Types].xml`, `_rels/.rels` and the `.psmdcp`** (OPC "core properties" part) carry
  a **random GUID** for the psmdcp file name and a `dcterms:created` timestamp — two packs of
  identical inputs get different names and different bytes there.
- Entry **order** follows msbuild's item enumeration, not a fixed rule.

None of this is a bug in `dotnet pack` — nobody promised reproducibility — but it means "did
this release rebuild to the same package" cannot be answered by comparing bytes.

## What `Pack.nupkg`/`Pack.zip` fix, and how

- **Sorted entries** — every entry list is sorted by path (ordinal) before writing, so input
  order never affects output.
- **Fixed timestamp** — every entry gets one DOS timestamp from `Options.Timestamp`:
  `defaultOptions` is 1980-01-01 (the DOS epoch), or `SOURCE_DATE_EPOCH` (unix seconds, the
  [reproducible-builds.org](https://reproducible-builds.org/specs/source-date-epoch/)
  convention) when set, clamped up to 1980 if earlier. File mtimes are never consulted.
- **A content-derived psmdcp GUID** — `Guid(sha256(id, version, sorted "path:hash" pairs of
  every packed file)[0..15])` instead of `Guid.NewGuid()`. Same inputs, same GUID, same bytes;
  change one byte of one file and the GUID changes visibly.
- **No creation date** — the psmdcp XML never has `dcterms:created`.
- **No extra fields, no comments** — so `entries` can assume the end-of-central-directory
  record is exactly the file's last 22 bytes.

Two calls with the same entries and `Options` produce byte-identical output, regardless of
input order or source-file mtimes.

## Why hand-rolled instead of `ZipArchive`

`ZipArchiveEntry.Crc32` — needed by `Pack.entries` for tests and `Verify`-style comparison — is
**not part of the netstandard2.0/net462 API surface** this assembly targets: confirmed by
compiling a throwaway project against both TFMs, where `entry.Crc32` fails with `FS0039` on
each. Since the central directory needs a hand parser regardless, the writer is hand-rolled
too — one code path, and it avoids `ZipArchive`'s platform-dependent bits ("version made by"
host byte, Unix permission bits from `CreateEntryFromFile`) that would make a package differ by
build OS. `Pack` always stamps "version made by" host-neutral and external attributes zero.

Compression uses `System.IO.Compression.DeflateStream`, present and sufficient on both target
frameworks — no `ZipArchive` type, and so no extra `net462` reference, was ever needed. CRC-32
is the standard table-driven implementation, used both writing and reading.

## `SOURCE_DATE_EPOCH`

When set, `Pack.defaultOptions` uses it (clamped to 1980) instead of the DOS epoch, so a CI
pipeline pinning it to the commit time gets that time baked into every entry without losing
byte-identity across reruns of the same commit.

## What is not done

- **Signing** — NuGet author signing (`.signature.p7s`, e.g. via `dotnet nuget sign`) is a
  later, delegated rule (brief §8f): sign after pack, verify the signer was handed the same
  hash `Pack.nupkg` produced.
- **`.nupkg.metadata` and symbols packages** (`.snupkg`) — not produced here.

## The SBOM per package

`Sbom.forPackage` (brief §8e) hashes this module's output — the finished `.nupkg` bytes — as
the root component of the package-level CycloneDX document, the way `Sbom.forAssembly` hashes a
compiled dll. Because `Pack.nupkg` is a pure function of its inputs, its output hash is
reproducible right along with the package: two identical builds of the same tag give the same
nupkg *and* the same SBOM for it.
