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

### B6: `Fsc` through the shared runner (done; one idea left)

Done in Xake 3.5/3.6 and Xake.Hermetic.Dotnet 0.2: the `Fsc` record next to `Csc` with
`ofSettings`/`run`/`compile` over the runner core shared with `Csc.run`, `fsc { ...; resolve }`,
`fsc { lock }`, `Project.import` for `.fsproj` (`Fsproj` retired), and `build.fsc.fsx` on one
imported lock (`locks/xake.json`, `update-locks`/`check-locks`, `HERMETIC=on`), with `global.json`
pinning the SDK exactly since the F# compiler is the SDK's `fsc.dll`. The net462 leg still comes
from `dotnet pack`.

Left, as an idea: **an F# toolset** -- a compiler host over the `FSharp.Compiler.Service`
package as a `toolset` for fsc, so the F# compiler becomes a restorable package like csc's
`Microsoft.Net.Compilers.Toolset` and the exact SDK pin (with its cost to contributors) is no
longer the only reproducible path. The alternative is an "SDK restore" step (`dotnet-install`
into the project's own `DOTNET_ROOT`).

## Xake.Hermetic.Dotnet

From `docs/hermetic/workflows.md` ("Gaps"), in priority order:

1. *(done in PR #30)* A missing lock fails under CI (env `CI`, or `-d CI=on|off`) instead of being
   recorded.
2. `csc { lock }` cannot take `Restore.Options` (package root, no network).
3. Reference assemblies of a composed block are downloaded outside the restore mechanism.
4. The sha512 of a package already present in the package folder is not verified.
5. Import as a file rule re-imports on every fresh clone; `update-locks` is the pattern.
6. No document-level `Lock.diff`; no one-call build of a whole imported lock; drift messages
   print absolute paths; `Lock.verify` on a missing lock says only "Neither rule nor file".
7. SBOM default internal prefixes are a customer's; the default should be empty.
8. The six SBOM questions in `docs/hermetic/nuget-sbom.md`; `sign {}` shadows `sign`;
   `StrongName.signFile` does not recompute the PE checksum; no real signer ships.
