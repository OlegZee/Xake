# vars-precedence after #103 / #101: what is left

Read-only analysis on eec2b2c (dev 7f16126 #103 + 4ef149d #101 merged). References are to today's tree.

## 1. Var vs Options.Vars in the code: is the split real?

Mostly yes; it is already two layers.

- **Input layer**: `ExecOptions.Vars` / `ExecContext.Vars : (string*string) list` (`ExecTypes.fs:39`).
  It has three writers: `-d` (prepended, `XakeScriptBuilder.fs:51`), `var` (removes the name, then
  prepends, `:156-157,225`) and the initial `ExecOptions.Vars`. It is final before the run starts:
  `ApplyCommandLine` folds the CLI in `Run`/`Start` (`:163-174,207,282`), and the context
  copies it unchanged. Lookup is first-match `Util.getVar` (`ExecTypes.fs:67`).
  `getVar "X"` (`ScriptFuncs.fs:116-123`) reads it raw and records `Var (X, raw)`.
- **Declaration layer**: `Var.string/int/env/arg/secret` (`Vars.fs:61-87`, `OptionalVar`/`RequiredVar`)
  hold the name, the cliArg key, the env name, the scope, the default and the secrecy. `resolveVar`
  (`Vars.fs:110-132`) **consumes** the input layer as its "cli" source (`getVar cliName`, `:125`),
  then reads env (`getEnv`, `:129`), then the default (`:146-147`). `varschema` (`:155-206`) only
  records help metadata and registers secrets.

Where the code still conflates the two:
1. **Libraries have no declarations.** Xake.Dotnet and the hermetic package call raw `getVar` on
   the input layer and hand-roll fallbacks per name: `DotNetFwk.fs:758,786-795` (NUGET_PACKAGES: input >
   env > ~/.nuget), `:888` (NUGET_FETCH: input only), `Csc.fs:30-63` (CSC_SERVER: input > env
   *XAKE_CSC_SERVER*), `HermeticMode.fs:75` (HERMETIC: input only), `Lock.fs:1344-1351` and
   `Locks.fs:108-109` (CI, LOCK_REFRESH: input > env), and `getVar` only for NETFX, NETFX-TARGET,
   CSC_TOOLSET, FSCVER, FSHARP_CORE_VERSION, NETSTANDARD_*, NETCORE_REF_VERSION and LOCK_STALE. Each
   library variable is a "declaration" that is spread over a `getVar` call site.
2. **Some code reads the input layer outside `getVar`, so nothing is tracked**: `CompilerRunner.fs:136-150`
   (CSC_JOBS/FSC_JOBS), `ExecCore.fs:208-210` (EXPLAIN_RULE, case-insensitive) and
   `ScriptRunner.fs:27-52` (`checkSecretVars`).
3. **One name, three meanings.** `Var` is the factory (`Vars.fs:61`), the dependency case `Var of
   string * string option` (`Types.fs:79`) and `ExecOptions.Vars`. The `Var` dependency is in fact
   "an input-layer entry". A declaration that resolves from the environment records `Var(name, None)` +
   `EnvVar(env, v)` instead (`Vars.fs:125-129`), so **dependencies are per source, not per
   value**. The default is not recorded at all.
4. **Secrecy is a declaration property that is stored process-wide by name** (`Types.fs:142-154`,
   `Secrets.declare` at `varschema` time, `Vars.fs:188-189`). The input-layer reader `getVar`
   consults it (`ScriptFuncs.fs:120-121`). This leak is harmless (it only masks more).
5. **The banner's "vars" section is the raw input list** (`ScriptRunner.fs:103-106`). It contains
   shadowed duplicates, and every entry is labelled `script/cli`. Resolved declaration values are
   not shown.

## 2. analysis.md, item by item

| Item | Class | Evidence / remaining failure |
|---|---|---|
| `var` deletes `-d` (hard pin) | **a** | `-d` is prepended after the ops (`XakeScriptBuilder.fs:51,170`). Tests: `CommandLineTests.fs:447,567`, `VarsTests` "Var.string reads the -d value when the script also has var" |
| Duplicates first-wins (`-d A=1 -d A=2` → 1) | **a** | Prepending makes the last `-d` first. Test: `CommandLineTests.fs:482` |
| `ExecOptions.Vars` beats `-d` | **a** | Test: `CommandLineTests.fs:465`. `var` > ExecOptions.Vars: `:473` |
| `want` vs other ops inconsistent | **a** | `-ll/-fl/-fll/-r` win per field (`CommandLineTests.fs:500-533,600-615`). `wantOverride` is removed and `want` yields (`:541`) |
| Docs "var ... (or -d ...)" false | **a** | Now true. `build.fsc.fsx:34` comment, `HermeticMode.fs:155` text are correct |
| Consumer CI `-d NUGET_PACKAGES=` over script `var` | **a** | Same mechanism. With `HERMETIC=on` the source is `ScriptVariable` (`DotNetFwk.fs:791`, `HermeticMode.fs:178`), so it is accepted |
| Engine `?vars` cannot override script vars | **b** | `makeCtx`: `Vars = vars @ extraVars` (`XakeScriptBuilder.fs:299`). Repro: `xakeEngine { var "V" "s"; ... start }`, then `Demand("t", ["V","r"])` → `getVar "V"` = `s`. The test `CommandLineTests.fs:548` **pins** this, against the decided chain `?vars` > all |
| `varschema withDefault` invisible to `getVar` | **c** | The default belongs to the declaration, and `getVar` is the input lookup. It is a trap only if a recipe reads a declared name with `getVar`: document "read declared names through the declaration" |
| `Var.env` silently ignores `-d` | **c**, nicety | The declaration's scope says so. A W-1 warning is optional |
| `Var.string` ranks `var` above env | **a/c** | That is exactly the decided chain (`-d` > script > env > default). The cliArg source is the whole input layer |
| Env fallback differs per library var | **b/d** | NUGET_FETCH, HERMETIC and NETFX have no env fallback. CSC_SERVER falls back to `XAKE_CSC_SERVER`. Analysis Q3 is still unanswered (see §5) |
| Option 1 (`-d` wins) | **a** | Implemented as recommended. Options 2/3/4: **c** |
| Q2 other ops yield to CLI | **a** | #103 |
| Q4 banner level, Q5 setup | **a** | #101: `setup` op `XakeScriptBuilder.fs:275-277`, `runSetupAsync` `ScriptRunner.fs:72-77`, tests `CommandLineTests.fs:304-390` |
| C: run parameters in the log | **a**, residue **b** | The banner is at Info after `checkSecretVars` (`ScriptRunner.fs:221-223`). Residue: duplicates and the single label. Repro: `-d X=1 -d X=2` + `var "X" "s"` prints three `X=… (script/cli)` lines, and only the first is effective |

## 3. concept.md

| Element | Class | Why |
|---|---|---|
| Hard constraint "values computed before the run" | **a for the input layer** | The input list is immutable from `Run` on. Declarations are pure functions of (input list, env, default). Evaluating one at use from a fixed list gives the launch value, unless the process env changes mid-run (Q-E) |
| Store as an immutable map / env snapshot | **c** | The list already is one. A snapshot matters only for Q-E |
| Q-5 (env-sourced library value rebuilds every run) | **c** | It arose only because rev. 3 recorded the *resolved* value under the declaration's name. Today the env part is recorded as `EnvVar(env, v)` (`Lock.fs:1350`, `Vars.fs:129`) and checked against env without any declaration (`DependencyAnalysis.fs:97`). Exception: NUGET_PACKAGES' env fallback is read with raw `%"NUGET_PACKAGES"` (`DotNetFwk.fs:340-345` `nugetRoot`, `:791`), so it is **not tracked**: a changed env package folder does not rebuild. That is **b**, a one-line fix (`getEnv`) |
| Value-based dep "source change with equal value doesn't rebuild" | **d** | It does not hold today across the input/env boundary. Repro: build with env `X=a` (`Var.string "X"`), then rerun with `-d X=a` and no env → `Var(X,None)`→`Some a` → rebuild once. It holds within the input layer (`-d` vs `var`, spec test 11). Accepting a one-off rebuild on a source move keeps the code declaration-free |
| Library declaration set at launch (§1 "hard question") | **c** | Needed only for value-based deps and a complete banner. With per-source deps the check needs no declarations |
| Variant 1 (db table) | **c** | It solves Q-5, which does not exist under per-source deps |
| Variant 2a (import `DotNetVars.all`) | **d** | Its only remaining benefits are library vars in `--help`/banner, launch-time P-2, and uniform env fallback. Its cost is a breaking line in every script. Worth it only if the owner wants the banner/help to list library variables |
| Variant 2b (params), 2c (env only) | **c** | Both lose `-d` for library settings, which #103 just made work |
| Hybrid §4 | **c** | |
| Q-A, Q-B, Q-C, Q-F | **c** | All are about 2a/V1 |
| Q-D untracked settings (JOBS, SOURCE_DATE_EPOCH, raw XAKE_CSC_SERVER) | **d** | Today they are deliberately untracked (`CompilerRunner.fs:136-139`). Just confirm |
| Q-E `getEnv` live vs snapshot | **d** | It is live. That matters only if a recipe changes the env mid-run |

## 4. spec.md rev. 3: still-relevant P-rules and conformance tests

- **O-1/O-2** (options precedence): **a** (#103). The `wantOverride` clause is moot: the op is gone.
- **P-1** (policy uses the source, not a second env read): **c**. `packageRootWithSource` does a
  second read (`DotNetFwk.fs:791`), and it is correct as is.
- **P-2** (HERMETIC refuses an env/blank NUGET_PACKAGES): **a** in its 3.13 form
  (`HermeticMode.fs:153-155,178`). The `.packages` default that rev. 3 bundled with it is **d**.
- **P-3** (banner sources): **b, minor**. The fix needs ApplyCommandLine to keep the `-d` names
  apart, or to compare the lists before and after, to label `cli`/`script` and drop shadowed entries.
- **S-1..S-8, D-1..D-7, DEP-1..6, M-1..M-3, E-1, E-2, E-5, L-1, V-1 store/db parts**: **c**. These are rejected or moot.
- **D-8** (all library vars ArgAndEnv): **d** (= analysis Q3). **D-9** (CSC_SERVER pair kept): **a**
  (unchanged since 3.13). The only delta is that `bogus` warns (`Csc.fs:60-61`) where rev. 3 wanted an error: **d**.
  **D-10** (CI lenient): **b, minor**. `-d CI=` gives `Some ""` and fails with "CI='': expected on or off"
  (`Lock.fs:1347`), while env `CI=` is treated as off.

Conformance tests (§9):
- **a**: 2, 7, 8, 11, 31, 32 (want half). Already true in 3.13: 3, 4, 5, 9, 10, 12 (C-1 wording aside), 15, 28, 39.
- **b**: 33 (engine `?vars`), 29 (banner labels, minor), 37 (`-d CI=` edge).
- **c**: 14 and 16-25 (defaulted, db 0.7, D-7/E-2, DotNetVars), 34 (E-5).
- **d**: 1 (getVar == decl only when the value is in the input layer), 6 (W-1), 13 (`Var.int` canonical
  "04" vs "4": today it rebuilds), 26 (`.packages` default), 27 (refuse getVar of a secret), 30 (W-2
  unused `-d`), 35 (required check at launch), 36 (`bogus`), 38 (NUGET_FETCH env).

## 5. What remains, minimal

None of the concept variants (1/2a/2b/2c) is needed. #103 already made the input layer behave as decided.
Treating declarations as readers *of* that layer, with per-source dependencies (`Var` for the input
entry, `EnvVar` for the env entry), satisfies the launch-time constraint and the dependency check
without any declaration registry. What is left is small and local:

1. **Engine `?vars` on top**: prepend the request vars instead of appending them (`XakeScriptBuilder.fs:299`),
   and flip `CommandLineTests.fs:548`. This is a decided item that is still open; it is a behaviour change for engine callers.
2. **Track NUGET_PACKAGES' env fallback**: read it with `getEnv` instead of raw `%` in
   `packageRoot`/`packageRootWithSource` (`DotNetFwk.fs:340-345,758-795`), so a changed env
   folder rebuilds. It is the only library env read that is not tracked.
3. **Library env fallback policy** (owner, analysis Q3 / spec D-8): either leave it per name, or add one helper in
   Xake.Dotnet (`input > env same name`) for NUGET_FETCH, NETFX, CSC_TOOLSET, and so on. HERMETIC stays
   input-only by design (`HermeticMode.fs:69-71`). The CSC_SERVER/XAKE_CSC_SERVER pair stays as in 3.13.
   No declarations or imports are needed for this.
4. **Banner** (#101 residue): show the effective entry per name only, labelled `cli` or `script`
   (`ScriptRunner.fs:103-106`). The ApplyCommandLine fold already knows which names came from `-d`.
5. **Optional niceties**, each independent: W-1 (`Var.env` given `-d`), W-2 (unused `-d`), `CI` with
   `-d CI=` treated as off (`Lock.fs:1344-1347`), and a doc rule "a declared name is read through its
   declaration, not `getVar`".
6. **Hermetic package**: it still references Xake `[3.13.0.43, 3.14)` (`src/hermetic/*.fsproj:84`), so it gets
   #103's precedence only when it moves to 3.14. Its reads (CI, LOCK_REFRESH: input > env, tracked) need no change.

Questions for the owner:
- (Q1) Is the value-based decision satisfied by "equal value within the input layer does not rebuild", with a
  one-off rebuild when a value moves between env and `-d`/`var`? If yes, 2a/V1 and the whole store are unnecessary.
  If strict value-basing across sources is required, declarations must be known at check time, and that is 2a.
- (Q2) Should library variables appear in `--help`/banner? This is the only other thing 2a would buy.
- (Q3) Env fallback per library variable (item 3), and whether `.packages` becomes the NUGET_PACKAGES default.
- (Q4) Should secrets be refused through `getVar`/`getEnv` (spec E-7), or is the current mask-by-name enough?
