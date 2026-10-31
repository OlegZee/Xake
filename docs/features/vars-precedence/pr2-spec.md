# Spec: PR 2 — library readers and the import of library declarations

Status: implemented in #112 (merged); the proposed conventions still await the owner's review (2026-10-30). It completes spec.md rev. 6 §3.6 (L-1..L-7) and
§3.10 (the import outline) for PR 2; the decisions of [brief.md](brief.md) ("Libraries", "Import
of library declarations") are final. MUST, MUST NOT, SHOULD, MAY are normative.

**Proposed, owner reviews** — every convention below was chosen by the PR 2 writer:

- the read operation's name and signature, `Var.read<'t> "KEY"` (I-1);
- the schema names `Xake.Dotnet.DotNetVars.Default` and `Xake.Hermetic.Dotnet.HermeticVars.Default` (I-5);
- schema fields named by their keys, verbatim (`NUGET_PACKAGES`, not `NugetPackages`) (I-6);
- every exported field is a `string`, argument-only, and carries the library's fixed default as
  `withDefault` where the library has one (I-6, I-7);
- the set of variables each schema exports (I-5);
- the text of the type-mismatch error E-11 (§3).

## 1. Scope

In scope: how Xake.Dotnet and Xake.Hermetic.Dotnet read their build variables (spec.md L-1..L-7),
the read operation they use, the schema records they export, and what importing one with
`varschema` changes. Out of scope: the variables of L-2 that keep a plain `getVar` (`NETFX`,
`NETFX-TARGET`, `CSC_TOOLSET`, `FSCVER`, `FSHARP_CORE_VERSION`, the reference-pack versions):
they are not exported and not read through `Var.read` (they MAY be added to the schema later, as
fields of the same shape).

## 2. Rules

### 2.1 The read

**I-1 (`Var.read`).** `Var.read<'t> (key: string) : Recipe<ExecContext, 't option>` is the read a
library uses for a build variable. It looks `key` up in `ctx.Engine.Options.Variables`:

- **declared** (the entry has a declaration, a rule or a field, the library's own exported field
  or the script's): the read trusts that declaration. It does not compare sources, default,
  scope, required flag, secret mode or external name with anything (no T-1); it checks only the
  type (I-3). The value is taken and recorded as by T-2: `getVar key` (one `Var (key, value)`, or
  `SecretDep (key, SecretArg, _)` for a secret), the declaration's typed default when the value
  is the entry's own and its origin is `default`, else the value converted to `'t` (already checked
  at launch, S-3); `None` when the key has no value.
- **not declared** (no entry, or an entry of input values only): the read is `getVar key`, the
  same value and the same dependency; a value is converted to `'t` with the 3.13 conversion, a
  failure fails the reading target with E-4. For `'t = string` this is exactly `getVar key`.

`Var.read` never reads the environment and never records `EnvVar`. T-4 (E-8) does not apply to it:
a library without the import still reads (brief: "Optional").

**I-2 (the library's fixed default).** `Var.read` returns `None` for an unset key; the library then
applies its fixed default itself (L-2..L-7), whether or not the key is declared. An imported field
that the script overrode without a default (`NUGET_PACKAGES = Var.string()`, environment unset) thus
still yields the library's default.

**I-3 (type).** If the stored declaration's `ValueType` differs from `'t`, the read fails the
reading target with E-11. This is the only check against the stored declaration.

**I-4 (untracked reads).** A library read that is not a dependency (`*_JOBS`, spec.md L-2) reads the
raw string from `ctx.Vars` (`Util.getVar`), whatever the declared type; no E-11 for it.

### 2.2 The exported schemas

**I-5 (names).** Each library exports one schema record type with a static `Default`:

| Library | Schema | Fields (keys) |
|---|---|---|
| Xake.Dotnet (`Xake` package) | `Xake.Dotnet.DotNetVars.Default` | `NUGET_PACKAGES`, `NUGET_FETCH`, `HERMETIC`, `CSC_SERVER`, `XAKE_CSC_SERVER`, `CSC_JOBS`, `FSC_JOBS` |
| Xake.Hermetic.Dotnet | `Xake.Hermetic.Dotnet.HermeticVars.Default` | `CI`, `LOCK_REFRESH`, `LOCK_STALE`, `SOURCE_DATE_EPOCH` |

The two schemas share no key; a script MAY import both (two `varschema` ops).

**I-6 (shape).** Every field is named by its key, verbatim, with no `name` (so the key is the field
name, A-1) and no `cliArg` (so `-d KEY=v` sets it, A-2). Every field is a `string`
(`OptionalVar<string>`, or `RequiredVar<string>` through `withDefault`): the library parses the
value itself (switches, numbers, paths), so a switch value is checked at the read (L-6), never at
launch. Every field is argument-only (`Var.arg<string>`): it has no `Env` source (L-1: libraries do
not read the environment).

**I-7 (defaults).** A field carries the library's fixed default as `withDefault` where the library has
a constant one: `NUGET_PACKAGES` `.packages` (resolved against the project root, L-4),
`NUGET_FETCH` `on`, `HERMETIC` `off`, `XAKE_CSC_SERVER` `on`, `CI` `off`, `LOCK_STALE` `fail`. The
others have no default (`CSC_SERVER` falls to `XAKE_CSC_SERVER`; `*_JOBS` unset is no limit;
`LOCK_REFRESH` unset refreshes nothing; `SOURCE_DATE_EPOCH` unset is the 1980 epoch). Each field
has a one-line description for `--help`.

### 2.3 Importing

**I-8 (the import is an option).** A script imports a schema with `varschema DotNetVars.Default`,
and changes fields with copy-and-update:
`varschema { DotNetVars.Default with NUGET_PACKAGES = Var.string() |> withDefault ".packages" }`
(the environment variable `XAKE__NUGET_PACKAGES` now feeds it, S-1: an `ArgAndEnv` field without
`envVar` reads the implicit env name of its key, spec.md P-6; amended 2026-10-31, rev. 6 read `NUGET_PACKAGES`;
`Var.string(envVar = "NUGET_PACKAGES")` keeps the plain name). Nothing is imported
automatically: no assembly scanning, no import by opening a namespace or calling a library function.

**I-9 (importing changes no value).** Importing a schema without overriding a field changes no value a
library reads: an unset field takes the library's default either way (I-2, I-7). What it changes:
the banner and `--help` list the fields (`NUGET_FETCH=on (default)`), `getVar` of a key sees the
default, and the dependency of a read records the default (`Var (NUGET_FETCH, Some "on")` instead
of `Var (NUGET_FETCH, None)`): a target built before the import rebuilds once (K-5).

**I-10 (definitions).** An imported field is a script definition of its key (S-4): importing a schema
and also defining one of its keys (`var "CI" [Env "CI"]`, `var "CI" "on"`, `ExecOptions.Vars`, another
schema with that key) fails the launch with E-2; importing the same schema twice does too. The
remedy is to override the field instead.

**I-11 (a declaration of the script's own).** A key the library reads MAY be declared by the script
without the import (a `var "N" [...]` rule or a field of the script's schema): `Var.read` trusts that
declaration as it trusts an imported one (I-1), and fails only on a type that differs (I-3). A rule
is a `string`, so every rule of a library key is read.

**I-12 (no rename).** A field that sets `name` declares another key; the library still reads its own
key and does not find it there (I-1, not declared). Renaming a library variable is not supported;
the script uses `cliArg` to give it another command-line name.

## 3. Diagnostics

- **E-11** (I-3; fails the reading target) `Variable 'N' is read as 'T' but is declared by DECL as 'U': declare it as 'T'`;
  `T`, `U` the full type names (as E-4), `DECL` = `varschema field F` or `var "N" [...]`.
- E-4 (I-1, a value of an undeclared key that does not convert to `'t`) and E-5 (L-6) keep their
  texts (spec.md §4). E-5 for the switches of PR 2: `NUGET_FETCH='v': expected on or off`,
  `XAKE_CSC_SERVER='v': expected on or off`, `CSC_SERVER='v': expected on, off or a number of
  seconds`, `CI='v': expected on or off` (3.13), `HERMETIC='v': expected on or off` (3.13),
  `LOCK_STALE='v': expected fail or warn` (3.13); `v` the trimmed value.

## 4. Conformance tests

One line each: given / when / then (rules). Numbered after spec.md §8 (1-68).

### 4.1 `Var.read`
69. Given no declaration of `K` and `-d K=v` (resp. nothing); when a recipe reads `Var.read<string> "K"`; then `Some "v"` and one `Var (K, Some "v")` (resp. `None`, `Var (K, None)`), as `getVar "K"` (I-1).
70. Given no declaration of `N` and `-d N=4` (resp. `-d N=x`); when `Var.read<int> "N"`; then `Some 4` (resp. the read fails with E-4) (I-1).
71. Given field `N = Var.int() |> withDefault 5`, nothing set; when `Var.read<int> "N"`; then `5` (the typed default); with `-d N=7`, `7` (I-1).
72. Given field `P = Var.string(envVar = "A") |> withDefault "d"` and `A=a`; when `Var.read<string> "P"`; then `Some "a"`, recorded `Var (P, Some "a")`, no `EnvVar` — no T-1 check against anything (I-1).
73. Given field `N = Var.int()` and `-d N=4`; when `Var.read<string> "N"`; then the read fails with E-11 `Variable 'N' is read as 'System.String' but is declared by varschema field N as 'System.Int32': declare it as 'System.String'` (I-3).
74. Given field `T = Var.string() |> secret`, `T` from env; when `Var.read<string> "T"`; then the value, recorded `SecretDep (T, SecretArg, _)` (I-1, X-4).
75. Given `var "K" [Env "A"; Default "d"]`, `A` unset; when `Var.read<string> "K"`; then `Some "d"`, recorded `Var (K, Some "d")` (I-1, I-11).

### 4.2 Schemas and import
76. Given the record `DotNetVars.Default` (resp. `HermeticVars.Default`); then its fields are exactly those of I-5, each argument-only, of type `string`, with no `name`/`cliArg`/`envVar`, the defaults of I-7 (I-5, I-6, I-7).
77. Given `varschema DotNetVars.Default`, nothing set; then a csc target resolves the package folder `<root>/.packages`, fetching on, hermetic off, the compiler server — the values without the import; the banner shows `NUGET_FETCH=on (default)` (I-8, I-9).
78. Given `varschema { DotNetVars.Default with NUGET_PACKAGES = Var.string() |> withDefault ".packages" }` and env `XAKE__NUGET_PACKAGES=/m`; then the package folder is `/m`, banner `NUGET_PACKAGES=/m (env:XAKE__NUGET_PACKAGES)` (I-8, P-6; amended 2026-10-31).
79. Given `varschema { DotNetVars.Default with CSC_JOBS = Var.string(envVar = "XAKE_CSC_JOBS") }` and env `XAKE_CSC_JOBS=2`; then the csc limit is 2, no dependency recorded (I-4, I-8).
80. Given `varschema DotNetVars.Default` and `var "NUGET_FETCH" "off"` (resp. `varschema DotNetVars.Default` twice); then the launch fails with E-2 naming `varschema field NUGET_FETCH` (I-10).
81. Given `varschema DotNetVars.Default` and `-d NUGET_FETCH=bogus`; then the launch succeeds and the reading target fails with E-5 `NUGET_FETCH='bogus': expected on or off` (I-6, L-6).
82. Given `varschema {| HERMETIC = Var.bool() |}` and `-d HERMETIC=true`; when a csc target reads `HERMETIC`; then it fails with E-11 (I-3, I-11).
83. Given `--help` with `varschema DotNetVars.Default`; then `-d NUGET_PACKAGES=<value> [string]` with the default `.packages` is listed and no env name (I-6, B-2).
84. Given `varschema { HermeticVars.Default with CI = Var.string() |> withDefault "off" }` and env `XAKE__CI=true`; then `underCi` = true; without the override, false (I-8, L-2, P-6; amended 2026-10-31).
