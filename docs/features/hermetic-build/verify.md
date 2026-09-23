# Verify

Pure PE/CLR inspection for comparing what shipped against what a local build produces. No
network, no signing tool -- it measures, it does not sign or validate a signature.

## Functions

- **`Verify.sha256 : path -> string`** -- lowercase hex SHA-256 of the whole file. Two files
  with the same value are byte-identical; anything at all (a timestamp, a re-signature, one
  flipped bit) changes it.

- **`Verify.authenticodeHash : path -> string`** -- the Authenticode PE image hash: SHA-256
  over the file with three things excluded -- the `CheckSum` field, the Certificate Table
  data-directory entry, and the certificate table blob itself (sections hashed in file-offset
  order, trailing data after the last section included unless it is the certificate table).
  This is exactly what a signing tool hashes before embedding a signature, and what
  `signtool verify /v` reports as "Hash of file (sha256)". Works on PE32 and PE32+ alike.

  What it is for (brief §8i/§8j, E5): a shipped, Authenticode-signed dll differs from a local
  unsigned build in at least the checksum and the certificate table, so `sha256` never matches
  even when the code is identical. Equal `authenticodeHash` values mean **the shipped dll is
  the local build, modulo signature** -- the reproducibility question E5 asks, answered without
  a signing tool or a private key.

- **`Verify.compare : a -> b -> Difference list`** -- byte-diffs two files and returns the
  differing ranges (`{ Offset; Length; Field }`), gaps under 4 bytes bridged into one range,
  each labelled by the structure it falls in (per file `a`'s layout): `"TimeDateStamp"`,
  `"CheckSum"`, `"CertificateTable"`, `"DebugDirectory/PDB id (MVID/GUID)"`,
  `"StrongNameSignature"`, or `"Content"` for anything else. `[]` means identical. A size
  mismatch is reported as one or more trailing ranges: when `b` is the longer file, the extra
  bytes exist only in `b`, so that tail -- and any recognised field inside it -- is labelled
  from file `b`'s own layout instead of `a`'s; this is what makes a signed-vs-unsigned
  comparison read as an appended `"CertificateTable"` instead of `"Content"` (`a`, unsigned, has
  no certificate table to name the tail with, but `b` does). Every other size mismatch,
  including `a` the longer file, keeps the old single trailing `"Content"` range.

- **`Verify.verdict : Difference list -> string`** -- one line: `"identical"`, or
  `"identical except: <fields>, <N> bytes"` when every range is a known, non-content field, or
  `"content differs: <N> ranges, <N> bytes, largest <N> bytes at 0x<hex offset> (<field>)"` when
  at least one range is unlabelled content. `<N> bytes` is always the sum of every differing
  range's length, formatted with a space every three digits (e.g. `148 213`); the `largest`
  clause names the single biggest range so it cannot hide behind a range count -- e.g.
  `content differs: 143 ranges, 148 213 bytes, largest 145 408 bytes at 0x2a40 (Content)`.

## Reading the field labels

- **`TimeDateStamp` + `CheckSum` (+ often `StrongNameSignature`)** -- the classic "re-stamped,
  not rebuilt" signature: a tool re-linked or re-signed the same IL without touching source or
  compiler inputs. This is exactly what an obfuscator like Babel does (brief §8i, E4): it
  writes a new timestamp, the checksum moves because the bytes moved, and the strong-name
  signature is recomputed over the renamed/rewritten IL. Seeing only these three fields differ
  between two Babel runs (with a fixed `--randomseed`) is the "Babel is deterministic" result;
  seeing them differ between the shipped dll and a local rebuild (E5) is the good case --
  `authenticodeHash` should then match.
- **`CertificateTable`** -- one file is signed and the other is not, or they carry different
  signatures. Expected whenever comparing a shipped dll to an unsigned local build; use
  `authenticodeHash` instead of `sha256` to see past it.
- **`DebugDirectory/PDB id (MVID/GUID)`** -- a different PDB was embedded or referenced
  (rebuilding regenerates the module's MVID unless the build is deterministic end to end).
- **`Content`** -- IL, metadata, or resources actually differ. This is the interesting case:
  a source change, a different compiler/SDK version, non-deterministic codegen (ordering,
  generated GUIDs, `AssemblyVersion` wildcards), or a `PathMap`/embedded-source difference.

## Limitations

- No signature *validation*: `authenticodeHash` computes the same hash a signature is made
  over, but never checks a certificate chain, timestamp, or that the embedded signature
  actually matches the hash. That is `signtool verify` or `osslsigncode verify`'s job --
  reach for one of those when the question is "is this signature trustworthy," not "did the
  bits change."
- `compare`'s field labels come from file `a`'s layout, except a trailing tail that only exists
  in a longer `b` (labelled from `b`'s layout, see above); if the two files disagree sharply on
  section layout otherwise (e.g. comparing across very different SDK versions), a label can be
  approximate near a boundary.
- Everything is read fully into memory (`File.ReadAllBytes`) -- fine for assemblies, not
  meant for arbitrary large binaries.
