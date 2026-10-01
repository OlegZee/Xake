# Roadmap

Ideas and larger items that are agreed in direction but not scheduled. Working state of a
feature lives in `docs/features/<name>/`; this page is the cross-feature list.

## Engine

### Rules with arguments (idea, 2026-10-02)

Every rule that uses its pattern's capture groups starts with a row of `let! x = getRuleMatch "x"`
and, for multi-file targets, a list pattern over `getTargetFiles()`. Proposal: a rule form that
binds the capture groups **positionally**, in the order they appear in the pattern, to the
parameters of a lambda, and the target file(s) after them:

```fsharp
"out/(fwk:*)/(lib:*).dll" ==> fun (fwk, lib) out -> recipe { ... }

["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"] ==> fun (fwk, lib) (dll, doc) -> recipe { ... }
```

The pattern parser knows the number of groups, so an arity mismatch fails when the script is
loaded, not inside the recipe. Tuple overloads for 1–4 groups and 1–4 target files via static
members; the operator is sugar over them. Group names stay as documentation and keep working
through `getRuleMatch` for existing scripts. `build.fsc.fsx` is the first customer.

## Xake.Dotnet

### B6: `Fsc` through the shared runner (planned after 3.4)

`fsc {}` has no resolved record, no `resolve` operation and no hash-checked runner, so there is
no `fsc { lock }` and `build.fsc.fsx` (the build of record) is hermetic only through its kept
msbuild evaluations and `--deterministic+`. Plan: an `Fsc` record next to `Csc` with
`ofSettings`/`run`/`compile`, a private runner core shared with `Csc.run`, `fsc { ...; resolve }`,
then `fsc { lock }` as an extension in Xake.Hermetic.Dotnet, `Project.import` for `.fsproj`
(retiring `Fsproj`), and `build.fsc.fsx` rewritten on a lock. Limits to document: the F#
compiler is not a NuGet package, so the only reproducible compiler path for fsc is the SDK pinned
by `global.json`; the net462 leg still comes from `dotnet pack`.

## Xake.Hermetic.Dotnet

From `docs/hermetic/workflows.md` ("Gaps"), in priority order:

1. A missing lock must fail on CI instead of being recorded (`Lock.Options.RecordMissing`,
   off when `CI` is set).
2. `csc { lock }` cannot take `Restore.Options` (package root, no network).
3. Reference assemblies of a composed block are downloaded outside the restore mechanism.
4. The sha512 of a package already present in the package folder is not verified.
5. Import as a file rule re-imports on every fresh clone; `update-locks` is the pattern.
6. No document-level `Lock.diff`; no one-call build of a whole imported lock; drift messages
   print absolute paths; `Lock.verify` on a missing lock says only "Neither rule nor file".
7. SBOM default internal prefixes are a customer's; the default should be empty.
8. The six SBOM questions in `docs/hermetic/nuget-sbom.md`; `sign {}` shadows `sign`;
   `StrongName.signFile` does not recompute the PE checksum; no real signer ships.
