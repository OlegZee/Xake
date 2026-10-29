# vars-precedence: PR 1 (core) implementation notes

Branch `feature/vars-core`. Where the spec (rev. 6 + names amendment) was ambiguous or did not fit
the code, the reading closest to brief.md was taken; each point is listed here for the owner.

## Names amendment (owner, 2026-10-29), folded into PR 1

Recorded here as asked; spec.md carries it as §3.9 (A-1..A-4), R-5, E-9, E-10 and tests 61-68.

- The key of a `Var.*` value is `Name`, else `FieldName`; `cliArg` is no longer part of it. The
  external (`-d`) name is `cliArg`, else the key. A rule names it with `Cli "ext"` (at most one,
  position irrelevant).
- `VarSource.Cli of string`, `VarOrigin.Cli of string` (the name given), `VarDecl.CliName`.
- `-d EXT=v` sets the key; `-d KEY=v` for a declaration whose external name differs is E-9
  (`-d KEY: the variable is set on the command line as EXT; use -d EXT=...`); two `Cli` sources are
  E-10 (`Variable 'N' has more than one command-line name: A and B`). `-d` of a secret is E-1a/E-1b
  under the key, the external name or an env name.
- The banner shows `cli:EXT` when the external name differs from the key, `cli` otherwise;
  `--help` shows the external name with `(key N)` after the type (`  -d pkg=<value> [string] (key P)`).
- T-1 compares the external name; E-7 calls that field `command-line name`.
- Compatibility: a 3.15 field with `cliArg` was keyed by it; `getVar "<cliArg>"` now reads nothing,
  the script reads `getVar "<Name or field name>"`.

## Deviations and readings

1. **`ExecOptions.VarDefinedBy : (string * string) list` (new field, not in §5).** E-2 must name the
   first two definitions in script order, `ExecOptions.Vars` entries first, and tell
   `ExecOptions.Vars` from a constant `var` op and order a constant op against a rule. Both write
   elsewhere (`Vars`, `VarDecls`), so the ops also record `(key, label)` here in script order; the
   `ExecOptions.Vars` entries are those of `Vars` not accounted for by a constant op. `ExecOptions`
   is not a frozen record.
2. **X-1 vs S-6 (which names a secret field declares).** S-6 says "key unless `EnvOnly`" (3.13), but
   test 31 expects a read of a `Var.secret` field to record `SecretDep (T, SecretArg, _)`, which needs
   the key declared. The launch declares the key, the external name and every `Env` name of a secret
   declaration, whatever its scope (X-1).
3. **A rule with two `Cli` sources** keeps its `Cli` markers in `VarDecl.Sources` (only then), so the
   launch can report E-10 with both names; otherwise `Sources` holds `Env`/`Default` only.
4. **E-8 for an inline `Var.arg(cliArg = "Y")`** (test 22): under the names amendment such a value has
   no key (no `name`, no field), so the message names it by its command-line name.
5. **Test 1, `A=` on Unix**: .NET removes a variable set to `""` in-process, so the empty-value case is
   asserted only where the variable stays defined (it does not on macOS/.NET 8 here).
6. **Test 17 (`--help`)**: `Run` calls `exit 0` after printing help, which would end the test host;
   the test applies the command line through the builder and checks `formatHelp` (what `Run` prints).
7. **Test 38 (debug dump)**: secret values in the tests are 4+ characters (the masking minimum); the
   dump additionally replaces secret values by `***` and drops a secret declaration's defaults.
8. **K-4 (one dependency per read)**: `recordDependency` skips a `Var`/`EnvVar`/`SecretDep` equal to one
   already recorded by the recipe.
9. **Request pairs**: `makeCtx` puts the demand's `?vars` first, reversed, so the last pair of a name
   wins (V-2 layer 1).
10. **Existing tests changed** (spec §7): inline Var reads in VarsTests/SecretVarsTests moved to
    `varschema`; "required fails at execution time" now fails at launch; `Var.env` with `-d` is E-6;
    "EnvVar dependency IS recorded" inverted (a Var read records `Var (key, value)` only); the 3.13
    argument-secret warning test is now E-1a; the inline `Var.secret` refusal test is now E-8;
    secret reads record `SecretDep (key, SecretArg, _)`; CommandLineTests 473/548 inverted, `-d`
    parsed into `CliVars`, banner labels `cli`/`script`/`env:X`. Two duplicated VarsTests were merged
    into their varschema twins.
