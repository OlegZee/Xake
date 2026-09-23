# Signing as a delegated rule (brief §8f ring 3, §11 `Sign` row)

**Status: skeleton landed (2026-09-23)** — `src/dotnet/Sign.fs` + `src/tests/SignTests.fs`
(six tests, one per acceptance criterion in §5). Where the code and this note disagreed, the note
has been corrected below and the deviation marked *(landed)*.

Decision (user, 2026-09-24): **design signing now as a delegated rule, land a skeleton and a
test against a fake signer.** The real tool — Windows `signtool`, `dotnet sign` against Azure
Trusted Signing, or an HSM agent — is not available in this repository and is not a
prerequisite for the design. This note fixes the shape so the real signer becomes a function
substitution, not a redesign.

## 1. What is signed, when, and why it is the delegated case

The ordering constraint is brief §8f's: compile → obfuscate (Babel) → re-strong-name →
**Authenticode** → pack → **NuGet-sign**. So there are exactly two signing steps, both at the
end of the chain, both after everything that this branch already makes reproducible:

| Step | Input | Output | Produced by |
|---|---|---|---|
| Authenticode | a PE file that is final except for its signature — after Babel and `StrongName.normalise` | the same PE with a certificate table appended (and `CheckSum` refreshed) | `Sign` on a `.dll`/`.exe` target |
| NuGet author signature | the deterministic `.nupkg` from `Pack.nupkg` | the same zip plus a `.signature.p7s` entry | `Sign` on a `.nupkg` target |

Authenticode must come **after** the strong name and **before** pack: the strong-name hash
deliberately excludes the certificate table (`strongname.md`), so signing does not break the
strong name, but the reverse order would re-sign over the certificate table and invalidate it.
The nupkg signature comes last because it covers the package's finished bytes.

Why this rule and no other in the pipeline is delegated:

- **The key is somewhere else.** The whole point of the two-agent shape (§8f) is that the
  private key never reaches the build agent: the signer receives a digest, or the file, and
  returns bytes. Authenticode signing is additionally Windows-only, so "build on one agent,
  sign on another" is the recommended shape regardless of policy.
- **It is I/O-bound waiting.** A sign call is a round trip to an HSM or a cloud service plus an
  RFC 3161 timestamp round trip. A delegated rule is run detached by the engine and is never
  bounded by the local CPU pool (`docs/delegated.md`); its concurrency is whatever dispatch
  `Resource` the executor applies — which matters, because signing services rate-limit.
- **Its output is not deterministic in bytes.** The RFC 3161 timestamp countersignature
  embeds the signing moment, so signing the same input twice gives two different files. That
  breaks the identity check every other rule on this branch uses. `sha256` of a signed file is
  evidence *about one signature*, never a reproducibility check. The reproducibility check is
  `Verify.authenticodeHash`, which is the same before and after signing. This is why the
  executor's identity key is built from the Authenticode hash of the **input**, not from the
  output bytes, and why the store is keyed the same way: a cached signature is a *valid*
  signature of that image, not a byte-reproduction of a previous run.

## 2. The library surface: `Sign` in `Xake.Dotnet`

`src/dotnet/Sign.fs`, following `Pack`/`Verify`/`StrongName`: a settings record, a CE builder
like `csc {}`, and pure helpers where the work is pure.

```fsharp
namespace Xake.Dotnet
module Sign =

    type Algorithm = Sha256 | Sha384 | Sha512

    /// How the signer names the key. Never key material -- a reference the agent resolves.
    type Certificate =
        | Thumbprint of string                        // cert store / pfx thumbprint
        | TrustedSigning of account: string * profile: string
        | KeyId of string                             // an HSM label or agent-side alias

    /// What is signed: decided from the extension by `kindOf`.
    type Kind = PeImage | Nupkg

    /// What is handed to the signer, and what the identity key is computed from.
    type Request = {
        File: string                                  // the file to sign (the rule's input)
        Kind: Kind
        Certificate: Certificate
        TimestampServer: string option                // RFC 3161 URL; None = no countersignature
        Hash: Algorithm
        Description: string option
    }

    /// The only thing a real deployment replaces. Returns the SIGNED BYTES.
    type Signer = Request -> Async<byte[]>

    /// Content-addressed delivery of signed bytes, keyed by identity (section below).
    type Store = {
        TryFetch: string -> Async<byte[] option>
        Publish: string -> byte[] -> Async<unit> }

    type Settings = {
        Target: string                                // rule mask for the signed output
        Input: string -> string                       // signed target path -> file to sign
        Certificate: Certificate
        TimestampServer: string option
        Hash: Algorithm
        Description: string option }

    val Settings.Default : Settings
    val sign : SignSettingsBuilder                    // Sign.sign { target ...; input ...; certificate ...; timestamp ...; hashalg ...; description ... }

    val kindOf     : string -> Kind                   // by extension: .nupkg -> Nupkg, else PeImage
    val imageHash  : string -> string                 // authenticodeHash for a PE, sha256 for a nupkg
    val request    : Settings -> string -> Request    // settings -> input path -> what the signer gets
    val identity   : Settings -> string -> string     // settings -> input path -> identity key
    val isSigned   : string -> bool                   // cert table / .signature.p7s present (section 3)
    val verifySameImage : string -> string -> bool    // input -> signed -> the section 3 check
    val rule       : Settings -> Signer -> ExecContext Rule           // the plain, local rule
    val executor   : Settings -> Signer -> Store -> Resource -> DelegatedExecutor<ExecContext>
    val directoryStore : string -> Store              // a directory as the shared store
    val fakeSigner : Signer                           // tests and dry runs; section 5
```

*(landed)* Three naming facts the F# compiler settled, not the design:

- **the builder is `Sign.sign`, never a bare `sign`** — `FSharp.Core` already defines `sign`
  (the sign of a number), and an unqualified `sign { ... }` binds to that and fails to compile;
- the cases are likewise qualified in a script — `Sign.Thumbprint`, `Sign.Sha256`, `Sign.KeyId`.
  Putting them in an `[<AutoOpen>]` module the way `csc`'s settings types are was tried and
  reverted: `Sha256`/`Request`/`Kind` are common enough names that auto-opening them broke an
  unrelated test file. A script that wants the short names writes `open Xake.Dotnet.Sign`;
- `Description` is **not** part of the identity key — it is cosmetic metadata, and including it
  would re-sign every artifact on a description edit. Everything else the note lists is.

`Settings.Input` is a function, not a path, because the rule is a mask: the signed target
`signed/X.dll` has to name its unsigned source `out/X.dll`, and that mapping is the script's,
not the library's. Putting it in the settings record means **the executor and the rule body
derive the input the same way from one value** — the executor needs it before the body runs,
to compute the identity key.

**Identity key.** `Sign.identity settings input` =
`sha256( authenticodeHash(input) | certificate-id | timestamp-server | hash-algorithm )` for a
PE file, with `sha256(input)` in place of the Authenticode hash for a `.nupkg` (the input nupkg
is unsigned by construction — `Pack.nupkg` writes no `.signature.p7s`, so there is nothing to
exclude). The key therefore says "this exact image, signed by this key, under this policy", and
nothing about wall-clock time. Two different targets that happen to hold the same image share
one signature; a changed cert or timestamp server is a different key and re-signs.

**Dedup and delivery** follow `delegated-proc.fsx` exactly: a `ConcurrentDictionary<string,
Lazy<Task<BuildResult>>>` of in-flight keys, a store lookup first, `Resource.withAcquired
budget 1` around the dispatch (never `Scheduler.withYieldedSlot` — the engine already detached
the executor; see `docs/session.md`). On a hit the executor writes the stored bytes to the
target and returns a synthesized `BuildResult`; on a miss it calls the `Signer`, publishes the
bytes to the store, writes them to the target, returns. **The executor never calls the body
thunk** — the work by definition happens elsewhere. The body of `Sign.rule` exists so the same
settings can drive an ordinary, non-delegated rule on an agent that *does* hold the key; that
is the one configuration where the local body is the signer.

Only the `BuildResult` goes into `.xake`; the signed bytes travel through the store. That is
the documented split in `docs/delegated.md` ("artifact bytes are delivered out of band").

**Script wiring** (the style of `import-page.fsx`: variables, a handful of rules, calls into
the library):

```fsharp
let signing =
    Sign.sign {
        target "signed/(name:*).(ext:dll|exe|nupkg)"
        input (fun t -> "out" </> Path.GetFileName t)
        certificate (Sign.Thumbprint "cc4967777c49a3ff...")
        timestamp "http://timestamp.digicert.com"
        hashalg Sign.Sha256
    }

let signer = Sign.fakeSigner                                  // the real one on the signing agent
let store  = Sign.directoryStore ".xake/signstore"
let budget = Resource.newResource "sign" 4                    // the service rate limit, not the core count

do xakeScript {
    rules [
        // sugar for: (Sign.rule signing signer) |> delegated (Sign.executor signing signer store budget)
        (Sign.rule signing signer) |> delegated (Sign.executor signing signer store budget)

        "release" <== [ "signed/Rdl.dll"; "signed/MESCIUS.ActiveReports.Core.Rdl.nupkg" ]

        // the gate: nothing is published until the signed file is provably the same image
        "verify-signed" => recipe {
            do! need [ "signed/Rdl.dll" ]
            let unsigned = "out/Rdl.dll"
            if Verify.authenticodeHash "signed/Rdl.dll" <> Verify.authenticodeHash unsigned then
                failwith "signed file is not the attested image"
        }
    ]
}
```

The script author still writes `need ["signed/X.dll"]`. Distribution stays a property of the
rule, as `docs/delegated.md` requires.

## 3. Verifying the signed output back, and the SBOM story

Three checks, all already available, none of which needs a certificate or the network:

1. **`Verify.authenticodeHash signed = Verify.authenticodeHash input`.** The identity check.
   Equality means the signed file is the attested image, signature aside — the claim §8f makes
   end to end ("the signed dll a customer holds, with its signature removed, equals the
   reproducible build of tag X under lock L").
2. **A certificate table is present.** `Verify.layout` reports `CertTable = Some (offset,
   size)` for a signed PE; for a nupkg, `Pack.entries` lists `.signature.p7s`. `layout` is
   `internal`, so a script cannot ask it directly. *(landed)* This is `Sign.isSigned : string ->
   bool` rather than the `Verify.isSigned` the note first proposed — one function covering both
   kinds, next to the `kindOf` that decides which is which, instead of a PE-only predicate in
   `Verify` plus a second one for packages. (1) + (2) together are `Sign.verifySameImage input
   signed`, the check a `verify-signed` gate calls.
3. **`Verify.compare input signed |> Verify.verdict`.** Expected labels: `CheckSum` (the
   signer refreshes it) and `CertificateTable`.

Trap found while designing this, now **half fixed**: the certificate table is *appended*, so
the two files differ in length, and `compare` used to report the whole size mismatch as one
trailing range labelled `"Content"` — `verdict` then said "content differs" for a correctly
signed file, because the labels come from file `a`'s layout and file `a` (unsigned) has no
certificate table to name the tail with. `compare` now labels the trailing range from file `b`'s
own layout when `b` is the longer file, so a signed-vs-unsigned pair reads `identical except:
CheckSum, CertificateTable`.

*(landed)* What remains: a certificate table has to start 8-byte aligned, so when the unsigned
file's length is not already a multiple of 8 the signer inserts padding *between* the two, and
that padding is a short unlabelled `"Content"` range which flips `verdict` back to "content
differs". PE files are file-aligned (512 bytes) in practice, so this does not bite the real
pipeline; `SignTests` asserts the exact label list only when the input is already aligned. The
general fix — label the gap between `a`'s end and `b`'s certificate table as part of
`CertificateTable` — stays with the open `Verify.verdict` item in the tracker.

**SBOM.** §8f's "two hashes" holds unchanged: the component hash in the SBOM is the raw
SHA-256 of the file that **ships** (signed), and the attestation additionally records the
Authenticode hash, which is what a reproduce job can regenerate. `Sbom.forAssembly` /
`Sbom.forPackage` therefore run on the **signed** artifact — after this rule, not before — and
the signed file's `authenticodeHash` ties it back to the reproducible ring-1/ring-2 output. The
lock records public key tokens and certificate thumbprints; never key material.

## 4. Out of scope now; what the user must provide

- **Any real signing tool.** No `signtool`, `dotnet sign`, `osslsigncode` or Key Vault client
  is written or invoked. `Signer` is the seam; one adapter per tool, later.
- **Signature validation.** Chain building, revocation, timestamp validation — `Verify`'s
  stated limitation, and it stays. "Is this signature trustworthy" is `signtool verify`'s job.
- **The policy gates of §8f rung 4** (signer in an allowlist, countersignature present,
  strong-name token per brand) — those belong to `Policy`, which does not exist yet.
- **Installer signing** — outside the boundary (§8f).
- **The user must provide**, when the real signer lands: a certificate or Trusted Signing
  profile, a Windows (or service) agent that can reach the key, network access to the RFC 3161
  timestamp server, and a store location both agents can read and write.

## 5. Skeleton plan

**Files to add** (nothing else changes; `Verify`, `StrongName` and `Pack` are used as they are):

- `src/dotnet/Sign.fs` — the surface of section 2. After `Pack.fs` in the compile order
  (`Xake.Dotnet.fsproj`), since the nupkg path uses `Pack.entries`/`Pack.zip`.
- `src/tests/SignTests.fs` — fixture `Sign delegated`, in the style of `VerifyTests.fs`
  (fixtures start from a real managed dll, this test assembly's own `Xake.Dotnet.dll`, copied
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
  `Pack.defaultOptions` so the unsigned bytes stay deterministic. *(landed)* Not "read with
  `Pack.entries`" as this note first said: `Pack.entries` lists the central directory (name,
  size, crc, timestamp) and cannot hand back an entry's *bytes*, which is what re-zipping needs.
  The signer explodes the package into a temp directory with `System.IO.Compression`'s
  `ZipArchive` (read-only, already used by `PackTests`) and feeds those files to `Pack.zip`.
  `Pack.entries` is what the *test* compares with, entry by entry.

It is not a valid signature and the module says so in one line: nothing verifies it, it exists
so the rule, the executor, the store and the verification step can be exercised end to end.

**Acceptance criteria.**

1. `Sign.fakeSigner` on a copied dll: `Verify.authenticodeHash` unchanged, `Verify.sha256`
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
5. Nupkg path: `Pack.entries` on the signed package lists `.signature.p7s`, every other entry
   is byte-identical to the unsigned package, and two signings of the same nupkg produce the
   same identity key.
6. `Sign.identity` changes when the certificate, the timestamp server or the hash algorithm
   changes, and does not change when only the output's wall-clock time would.

*(landed)* All six are `src/tests/SignTests.fs`, fixture `Sign delegated`, one test each. The
signer under test is `Sign.fakeSigner` wrapped in a call counter (and, for criterion 4, the
concurrency meter `DelegatedTests` uses). The executor records the input as a `FileDep` and
never runs the body, so — as `docs/delegated.md` requires of any delegated body — whatever
builds the unsigned input must be `need`ed by the *enclosing* rule, not by the signing rule.

Tracker: this replaces the `sign as delegated rule (later)` line in slice 3.
