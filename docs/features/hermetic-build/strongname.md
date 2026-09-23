# StrongName

PE timestamp normalisation and CLR strong-name (re-)signing, pure `System.Security.Cryptography`
(netstandard2.0 + net462). Built for E4 (brief §8j): Babel is deterministic with `--randomseed`
except the PE `TimeDateStamp`, `CheckSum` and the 128-byte strong-name signature after it.
`StrongName.normalise` closes that gap without waiting on the vendor.

## Functions

- **`stamp`** -- sets `TimeDateStamp`, then recomputes `CheckSum`.
- **`checksum`** -- the standard PE checksum ("MapFileAndCheckSum"): a 16-bit ones'-complement
  sum over the file, `CheckSum` itself excluded from the sum, plus the file length. Verified
  against `System.Reflection.Metadata`'s own `PEBuilder.CalculateChecksum` (dotnet/runtime) and
  empirically against real `csc` output.
- **`readSnk`/`writeSnk`** (`writeSnk` internal) -- CAPI `PRIVATEKEYBLOB`/`PUBLICKEYBLOB` <->
  `RSAParameters`, `.snk` only. `writeSnk` fabricates throwaway test keys without `sn.exe`; a key
  it writes, handed to `csc /keyfile:`, produces a signature that decodes correctly under the
  same `RSAParameters` -- the encoding is checked against a real compiler, not just itself.
- **`sign`/`verify`** -- re-sign or check the strong-name signature; `sign` also sets
  `COMIMAGE_FLAGS_STRONGNAMESIGNED` (0x8) in the CLI header `Flags`.
- **`signFile`/`normalise`** -- file wrappers; `normalise` is stamp + sign + a final `checksum`.

## The hash-exclusion rule (verified against csc byte for byte)

The brief's working hypothesis -- exclude `CheckSum`, the cert-table directory entry, the cert
table and the signature blob uniformly, from an otherwise-untouched file -- does **not**
reproduce `csc`'s signature. What does (found by decoding `csc`'s own signature with the public
key, `S^e mod n`, to read off the literal SHA-1 it signed) is exactly
`System.Reflection.Metadata.PEBuilder.GetContentToSign`, what Roslyn's
`SigningUtilities.CalculateRsaSignature` is called with via `DesktopStrongNameProvider`:

1. The PE header (DOS/COFF/optional + section table) up to but **excluding** its file-alignment
   padding -- cut from the stream, not hashed as zero -- with `CheckSum` and the Certificate
   Table directory entry **zeroed** first: neither has a real value at sign time (`CheckSum` is
   computed only afterwards; a cert table is added later still, by Authenticode, deliberately
   outside this hash so Authenticode-signing afterwards doesn't break the strong name).
2. Every section verbatim, with alignment padding, `StrongNameSignature` **excluded** from the
   stream entirely (not zeroed -- it sits inside section data, so the real algorithm cuts it out).

Both "zeroed" and "excluded" were tried per field; this combination reproduced `csc`'s signature
exactly (`StrongNameTests.fs`). The signature is written in CAPI little-endian order -- the
reverse of `RSA.SignHash`'s output -- matching Roslyn's own `Array.Reverse`.

## Ordering, and hash algorithm

`CheckSum` covers the whole final file including the signature just written, so it is computed
**after** signing: `normalise` stamps the timestamp only, signs, then checksums -- `stamp`
(which recomputes `CheckSum` itself) followed by `sign` would leave `CheckSum` stale.

ECMA-335 leaves the hash algorithm to the Assembly-table `HashAlgId` (default `0x8004` = SHA-1).
This module hard-codes SHA-1 -- `csc`'s default, what E4's Babel key uses -- and does not read
`HashAlgId` from metadata; a SHA-256-required assembly would need a parameter this lacks.

## E4 use

Babel with a fixed `--randomseed` and `--keyfile <snk>` still reintroduces a wall-clock
`TimeDateStamp` (`SOURCE_DATE_EPOCH` is not honoured). `StrongName.normalise babelOut
timeDateStamp snkPath`, timestamp derived from the commit or content, makes two runs
byte-identical: `Verify.compare` should then report exactly `TimeDateStamp`, `CheckSum`,
`StrongNameSignature` and nothing else.

## Not covered, and security

Delay-signing; public-key-token computation; `.pfx`/certificate-store keys (`.snk` only);
SHA-256 strong-name hashing. A `.snk` with a real private key (`isPrivate = true`) is a secret
-- unlike the throwaway `RSA.Create(1024)` keys `StrongNameTests.fs` generates for itself.
