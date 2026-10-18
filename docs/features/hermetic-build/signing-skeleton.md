# Signing skeleton: plan and acceptance criteria

Working notes split out of the product page [docs/hermetic/signing.md](../../hermetic/signing.md)
(section 5 there, 2026-09-24). Historical: the skeleton landed and its six tests live in
`src/hermetic.tests/SignTests.fs`.

## Skeleton plan

**Files to add** (nothing else changes; `Verify`, `StrongName` and `Pack` are used as they are):

- `src/hermetic/Sign.fs` — the surface of section 2. After `Pack.fs` in the compile order
  (`Xake.Hermetic.Dotnet.fsproj`), since the nupkg path uses `Pack.list`/`Pack.zip`.
- `src/hermetic.tests/SignTests.fs` — fixture `Sign delegated`, in the style of `VerifyTests.fs`
  (fixtures start from a real managed dll, this test assembly's own `Xake.Hermetic.Dotnet.dll`, copied
  to a temp file).

**The fake signer.** Honest and cheap: it builds a *real* `WIN_CERTIFICATE` structure with a
*fake* payload, so everything the PE parser and the pipeline see is genuine and only the
cryptography is not.

- PE: append an 8-byte-aligned `WIN_CERTIFICATE` — `dwLength`, `wRevision = 0x0200`,
  `wCertificateType = 0x0002` (PKCS#7) — whose body is ASCII
  `{"authenticodeHash":"…","certificate":"…","timestamp":"…"}`; point data directory 4 at it
  (offset + size); recompute `CheckSum` with `StrongName.checksum`. This is exactly the patch
  `VerifyTests.fs` already applies by hand to test `authenticodeHash`, promoted to a function —
  which is the evidence that `Verify` sees it as a certificate table.
- Nupkg: rewrite with `Pack.zip` plus a `.signature.p7s` entry carrying the same JSON, under
  `Pack.Options.Default` so the unsigned bytes stay deterministic. *(landed)* Not "read with
  `Pack.list`" as this note first said: `Pack.list` lists the central directory (name,
  size, crc, timestamp) and cannot hand back an entry's *bytes*, which is what re-zipping needs.
  The signer explodes the package into a temp directory with `System.IO.Compression`'s
  `ZipArchive` (read-only, already used by `PackTests`) and feeds those files to `Pack.zip`.
  `Pack.list` is what the *test* compares with, entry by entry.

It is not a valid signature and the module says so in one line: nothing verifies it, it exists
so the rule, the executor, the store and the verification step can be exercised end to end.

**Acceptance criteria.**

1. `Sign.fakeSigner` on a copied dll: `Verify.authenticodeHash` unchanged, `Hash.sha256`
   changed, `Verify.layout` reports `CertTable = Some _`.
2. The delegated rule, run through `xakeScript` on two targets sharing one input image: the
   signer is invoked **once** (a counter), both targets are written, and the second is served
   from the store — the miss/dedup/hit sequence `DelegatedTests.fs` already asserts for the
   hook.
3. A second `xake` run in the same folder re-serves both from the store with zero signer calls.
4. The dispatch `Resource` caps concurrent signer calls below the number of targets while the
   run is *not* bounded by `Threads` (the `delegated-tests.fsx` shape: `THREADS=2`, budget 4,
   8 targets, peak signer concurrency 4). *(landed)* One trap when writing this as a test: each
   entry of `ExecOptions.Targets` is a target *group*, and groups run one after another
   (`ScriptRunner.targetLists`), so the eight targets go in as one `";"`-joined entry —
   otherwise the run is serial and the peak is 1, which says nothing about the budget.
5. Nupkg path: `Pack.list` on the signed package lists `.signature.p7s`, every other entry
   is byte-identical to the unsigned package, and two signings of the same nupkg produce the
   same identity key.
6. `Sign.identity` changes when the certificate, the timestamp server or the hash algorithm
   changes, and does not change when only the output's wall-clock time would.

*(landed)* All six are `src/hermetic.tests/SignTests.fs`, fixture `Sign delegated`, one test each. The
signer under test is `Sign.fakeSigner` wrapped in a call counter (and, for criterion 4, the
concurrency meter `DelegatedTests` uses). The executor records the input as a `FileDep` and
never runs the body, so — as [docs/delegated.md](../../delegated.md) requires of any delegated body — whatever
builds the unsigned input must be `need`ed by the *enclosing* rule, not by the signing rule.

Tracker: this replaces the `sign as delegated rule (later)` line in slice 3.
