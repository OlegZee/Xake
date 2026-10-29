# spec rev. 4 traceability and "Var untouched" delta

Sources: analysis.md (A), after-103.md (B), spec.md rev. 4 (S), concept.md §8; code `Vars.fs:105-133`
(resolveVar), `ScriptRunner.fs:27-52,103-106`, `ScriptFuncs.fs:116-123`, `DependencyAnalysis.fs:88-101`.

## Task 1. Every defect vs spec rev. 4

| # | Defect | Source | Rev. 4 |
|---|---|---|---|
| 1 | `var` deletes `-d` (hard pin) | A §A dissonance | done #103; §6.1 b1, tests 5, 7 |
| 2 | duplicate `-d` first-wins | A §A.5 | done #103; V-2, test 6 |
| 3 | `ExecOptions.Vars` beats `-d` | A §A.5 | done #103; V-2 (no rev. 4 test of `-d` over ExecOptions.Vars; CommandLineTests:465 exists) |
| 4 | `want` yields, other ops do not | A dissonance, Q2 | out of scope N-5 (#103) |
| 5 | docs "`var` (or `-d`)" false | A dissonance | done #103 |
| 6 | engine `?vars` appended, cannot override | A §A.5, B §2 (b) | G-2, tests 44-45 |
| 7 | `varschema withDefault` shown in help, invisible to `getVar` | A readers, B §2 (c) | K-1, test 18 |
| 8 | `Var.env` silently ignores `-d` | A readers, B §2 | S-1 (now `-d` wins), test 15, §10.5 |
| 9 | `Var.string` ranks `var` above env | A dissonance | by design V-2; tests 5, 21 |
| 10 | env fallback differs per library var (NUGET_FETCH none, CSC_SERVER other name) | A Q3, B §5.3 | L-1/L-2 (all set-only) |
| 11 | `CSC_SERVER` env under `XAKE_CSC_SERVER`, raw read | A table, B Q-D | L-2 row, L-5; tests 52, 54 |
| 12 | `Csc.RunOptions.Default` reads `XAKE_CSC_SERVER` | (S only) | L-5, test 54 |
| 13 | `-d CSC_SERVER=bogus` warns, rev. 3 wanted error | B §4 D-9, test 36 | **MISSING** (L-2 says "same switch parser", silent) |
| 14 | `NUGET_PACKAGES` env fallback raw `%`, untracked | B §3 Q-5, §5.2 | L-2 removes the env read; tests 49, 55 |
| 15 | `nugetRoot()`/`Roots.nugetRoot()` read env; child restore gets folder only for script var (`Import.fs:1684`) | B §3 | L-3, test 49 |
| 16 | HERMETIC refuses env-sourced NUGET_PACKAGES; policy does a second env read (P-1) | A opt. 4, B §4 P-1/P-2 | L-4, tests 50-51 |
| 17 | `.packages` as NUGET_PACKAGES default | B §4 P-2, Q3, rev3 t26 | dropped silently; default stays `~/.nuget/packages` (L-2). Not stated as out of scope |
| 18 | `Var.env` workaround cannot feed library (value only in recipe) | A opt. 4 | R-2 `var [Env; Default]`, tests 1-2, 50 |
| 19 | `-d CI=` fails "CI='': expected on or off", env `CI=` is off | B §4 D-10, rev3 t37 | **MISSING**. Worse under L-2: `var "CI" [Env "CI"]` with env `CI=` gives `Some ""` (V-4) and the strict parser fails. Test 56 covers only `true`/`bogus` |
| 20 | `CI`, `LOCK_REFRESH` env fallback | A table | L-2, tests 56-57 |
| 21 | `SOURCE_DATE_EPOCH` raw env, context-free | B Q-D | L-2 [owner], §10.4, test 58 |
| 22 | `CSC_JOBS`/`FSC_JOBS` raw `ctx.Vars` read + `XAKE_*_JOBS` env, untracked | B §1.2, Q-D | L-2 (env dropped, stays untracked deliberately), test 53 |
| 23 | `EXPLAIN_RULE` raw, case-insensitive | B §1.2 | V-5 unchanged (deliberate) |
| 24 | `checkSecretVars` raw read of input layer | B §1.2 | replaced by X-2 (§7 PR 1) |
| 25 | `Var` = factory + dependency case + `ExecOptions.Vars` | B §1.3 | terminology only (§2 Definitions); names kept (§5 "keep their signatures"). Not declared out of scope |
| 26 | deps per source (`Var(N,None)`+`EnvVar`), env<->`-d` move rebuilds | B §1.3, §3 | K-5, tests 24-25, 32 |
| 27 | a default is never recorded | B §1.3 | K-1/K-5 for varschema (test 26); inline K-3 still `Var(X,None)` (test 27, deliberate) |
| 28 | secrecy registry process-wide by name; `getVar` consults it | B §1.4 | **MISSING** (X-1 keeps the registry; harmless per B) |
| 29 | refuse `getVar`/`getEnv` of a secret (E-7) | B Q4, rev3 t27 | not adopted: K-1/X-4 record SecretDep. No explicit decision line |
| 30 | banner = raw list, shadowed duplicates | B §1.5, §2 C | B-1, test 42 |
| 31 | banner label `script/cli` for all | B §1.5, P-3 | B-1, test 41 |
| 32 | Debug `Options: %A` dump blanks `Vars = []` | A §C | **MISSING** (minor; banner supersedes) |
| 33 | banner/secret registration ordering | A §C | X-1, test 37 |
| 34 | `Var.int` canonical form ("04" vs "4" rebuilds) | B §4 rev3 t13 | **MISSING** (K-5 compares strings; not addressed) |
| 35 | required check at read, not launch | B §4 t35 | S-2, tests 16, 48; inline K-3 test 29 |
| 36 | type conversion fails at read | (A/B implied) | S-3, test 17 |
| 37 | W-1 (`Var.env` given `-d`) | B §5.5 | redefined: W-1 = inline Var.* not read from env; test 27 |
| 38 | W-2 unused `-d` | B §5.5, rev3 t30 | out of scope N-6 |
| 39 | ArgOnly secret by `-d` only warns | A readers | X-2/X-5, test 36 |
| 40 | env-only secret `-d` of env name | A readers | X-2, test 34 |
| 41 | NUGET_FETCH/NETFX/HERMETIC no env (Q3) | A Q3, rev3 t38 | L-2 "unchanged" (set only; script adds rule) |
| 42 | doc rule "read declared names through declaration" | B §5.5 | moot by K-1 |
| 43 | `getEnv` live vs snapshot (Q-E) | B §3 | N-3 (getEnv live), V-3/G-1 (vars once) |
| 44 | library vars absent from `--help`/banner (Q2) | B Q2 | partly: shown only when the script names them; L-1 "libraries declare nothing" |
| 45 | hermetic package pinned to `[3.13, 3.14)` | B §5.6 | §7 PR 3 |
| 46 | `teardown` replaces; `setup` | A §B | done #101 |
| 47 | env change after engine `Start` | (S) | G-1, test 47 |
| 48 | `build.fsc.fsx:34` pattern | A dissonance | works since #103; nothing in S |

Net MISSING: 13 (bogus CSC_SERVER), 19 (`-d CI=` / env `CI=` empty, now also via rule), 28 (name registry),
32 (Debug dump), 34 (`Var.int` canonical). Silently dropped without a cite: 17 (`.packages`), 29 (E-7).

## Task 2. "Var untouched"

Assumed: `Var.*` and `varschema` exactly 3.13; `var "N" [sources]`, set built at launch and flattened into
`ExecContext.Vars`, `?vars` on top, `-d` of a secret = launch error, banner origins, libraries set-only,
L-4 lifted, SOURCE_DATE_EPOCH per L-2, every second definition reported.

### Rules / diagnostics / tests that go or change

| Item | Under "Var untouched" |
|---|---|
| §2 Declaration/Key | only `var "N" [...]`; varschema key used for help and secrets only |
| V-1 | drop "or a varschema declaration" |
| V-2 layer 4 | `var [...]` rules only |
| R-4 | "later replaces" becomes a reported second definition (owner: visible and reported); also `var "N" "v"` vs `ExecOptions.Vars`, and `var "N" "v"` vs `var "N" [...]`. Level (warning/error) to be set. Tests 7, 8, 9 change to expect the report |
| S-1, S-2, S-3, S-5 | removed |
| S-4 / E-2 | only `var [...]` twice (or merged into the R-4 report). varschema + `var [...]` same key: no conflict (Var reads the set first); test 19, 20 removed/changed |
| K-1 | default visible to `getVar` only for `var [...]`; test 18 removed |
| K-2, K-3, W-1 | removed; tests 23, 27, 28, 29 removed (28 holds trivially in 3.13) |
| K-4, K-5 | only `getVar` and set-sourced values; test 30 drop the `schema.X` half; tests 24-25 hold for `var [...]` only; 26, 32 removed |
| E-3, E-4 at launch | removed (back to read time, 3.13 texts); tests 16, 17, 21, 48 removed; G-2 "converted per S-3" and G-4 E-4 part drop |
| X-1 | list markers declare at launch; varschema still declares at varschema time (3.13) |
| X-2 / E-1a / E-1b / X-5 | stay as a change of `checkSecretVars` (ArgOnly/ArgAndEnv secret `-d` = error, test 36). It MUST check the inputs (`CliVars`, script `Vars`, `?vars`), not the flattened set (see conflict below) |
| X-4 | varschema secrets record SecretDep via `getEnv`/`getVar` as 3.13; tests 37-39 only for list secrets |
| B-1 | banner lists set keys only; varschema fields without `-d`/`var` do not appear. Test 41 loses `D=4 (default)`, `F (not set)` |
| B-2 | unchanged (test 22, 43 stay) |
| §5 | `VarRule.ValueType`, `Required`, `DeclaredBy "varschema field F"` go; `VarEntry.Declared` = has a `var [...]` rule |
| §6.1 | drop bullets 3 (varschema at launch), 4 (inline Var no env), 5 (Var.env takes `-d`; secrets part stays), 6 (3.13 db rebuild) |
| §6.2 | drop "varschema evaluates...", "Breaking: Var.* without varschema"; narrow "equal value no longer rebuilds" to `getVar`/`var [...]` reads |
| §10 | 1 (K-3), 2 (S-5), 5 (Var.env `-d`) gone; 3 (L-4) and 4 (SOURCE_DATE_EPOCH) decided |

### Defects that stay open because they live in Var

| Defect (Task 1 #) | Repro | Verdict |
|---|---|---|
| 7 withDefault invisible to `getVar` | `varschema {\| X = Var.string() \|> withDefault "d" \|}`; recipe `getVar "X"` = None, `--help` says default d | acceptable: B classed it (c); `var "X" [Default "d"]` is the fix; needs a doc line |
| 8 Var.env ignores `-d` | `Var.env(envVar="A")`, env A=a, `-d A=x` -> a | acceptable (scope says so), but now inconsistent with `var [...]` where `-d` wins; doc line |
| 26 per-source deps | `Var.string "X"`, build with env X=a; rerun with no env and `-d X=a` -> rebuild (`Var(X,None)`->`Some a`) | **real gap** vs the decided value-based rule, confined to Var env reads; workaround: `var "X" [Env "X"]` so Var hits the set |
| launch-time constraint | recipe 1 does `Environment.SetEnvironmentVariable("A","z")`, recipe 2 `let! a = Var.string(envVar="A")` -> z; engine: env changed after `Start` is seen by Var, not by `var [...]` (G-1 half true) | **real gap** vs "everything evaluated at launch"; rare in practice; G-1 must be worded "variables of the set" |
| 35/36 fail-fast | `Var.int "N"`, `-d N=x`: fails in the reading target after others ran | acceptable (3.13) |
| 34 canonical int | unchanged | was MISSING anyway |
| 28 name registry | unchanged | harmless |
| 44 banner incomplete | `varschema {\| Config = Var.string() \|}`, env CONFIG=Release: banner has no CONFIG line | gap vs "banner shows the real source" for Var-only names; acceptable if the owner reads B-1 as "the variable set" |
| inline secret `-d` | `let! t = Var.secret(envVar="T")` not in varschema, `-d T=x`: error only when that target runs (`Vars.fs:117-124`) | gap vs "`-d` of a secret = launch error"; covers only varschema/list secrets |

### Var reading a name a `var [Env ..]` rule resolved

`resolveVar` (`Vars.fs:125-129`): `cliVal = getVar resolvedCliName` (skipped for EnvOnly); env only when `cliVal = None`.
With the set flattened into `ctx.Vars` (S §5):
- **`Var.string/int/bool/create`, `Var.arg`, same key**: consistent. The rule's value is found as "cli", wins over
  Var's env fallback, records one `Var(N, Some v)` and no `EnvVar` (value-based). `?vars`/`-d` above the rule also win.
- **Rule gives no value** (Env absent, no Default): N is absent, so Var falls to its own env (`envVar` or
  `toEnvName N`), live. Same result only if that is the rule's `Env` name.
- **Key mismatch**: Var's key is `cliArg`, else `name`, else the field name, *not* upper-snaked:
  `Var.string "nugetPackages"` reads `getVar "nugetPackages"` (absent), then env `NUGET_PACKAGES` raw, while the
  library uses `var "NUGET_PACKAGES" [Env "XX"; Default ".packages"]`. Different values. Needs a doc rule: same key.
- **`Var.env` (EnvOnly)**: never consults the set; reads raw env. Differs whenever the rule's `Default` applies,
  the rule's `Env` name differs, or `-d`/`var` override.
- **`Var.secret` with the key of a `var "T" [Secret; Env "A"]` rule: breaks.** The EnvOnly secret check
  (`Vars.fs:117-124`) looks for `[T; A]` in `ctx.Vars`; the flattened set holds `T` (the rule's own value),
  so the read fails with "Variable 'T' is secret ... instead of passing -d T". Same for `checkSecretVars` if
  it is fed the flattened set. Fix: the check runs against the inputs at launch only, and resolveVar's check
  is removed or made to ignore set entries of `var [...]` origin, which touches Var.
