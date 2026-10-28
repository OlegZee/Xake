# Concept: variables evaluated at launch

Status: concept for the owner's decision, 2026-10-28. Not a spec. It replaces the approach of
[spec.md](spec.md) rev. 3, which the owner rejected. Rev. 3 evaluated values lazily, at the point of
use, so the dependency check could meet a name whose declaration it did not have (its Q-5). The
defaulted flag, persisted declarations and registration at task construction were patches for that.
The 3.13 behaviour is in [analysis.md](analysis.md); file:line references point at 965c874.

## 1. The constraint and what it changes

**Constraint (owner, 2026-10-28).** Every variable value is computed before the script starts, at
launch. Anything a recipe or a library will need during the run is known and evaluated then. Nothing
is evaluated lazily at the point of use, and no declaration appears during the run.

Consequences that hold for every variant below:

1. **The store is an immutable map.** At launch the engine builds `name -> (value, source)` for
   every declared name, plus the raw values of undeclared names given by `-d`/`var`. A read is a
   lookup. Spec rev. 3's memo (S-5), its "read before declaration" (D-7, E-2), the late-declaration
   log (L-1) and the `defaulted` flag (DEP-1) have no reason to exist.
2. **The environment is snapshotted once**, when the map is built. A value evaluated at launch and
   one evaluated "late" from the same snapshot would be equal anyway. What launch evaluation adds
   is that the **set of declarations** is fixed before the first target is checked.
3. **The dependency check is exact.** A recorded `Var (name, value)` is compared with the map's
   canonical value for the name. Any declared name is in the map. Any undeclared name gets its
   weak value (`-d`/`var` only, else `None`). Both are known before the first check, so Q-5
   disappears.
4. **The banner and `--help` are complete by construction**, because they print the map.
5. **Launch-time policy checks become possible.** For example, `HERMETIC=on` with an
   environment-sourced `NUGET_PACKAGES` can fail before any target runs, not inside the first
   compile.

The only hard question left is **which library declarations exist at launch**. Library reads sit
inside recipes, and recipes are closures that run only when a target is demanded (`DotNetFwk.fs:758`,
`Csc.fs:49`, `Lock.fs:1344`, `Locks.fs:108`). A task record such as `Csc {...}` is built inside the
recipe too, so nothing about it is visible at launch. The variants answer this question in
different ways.

Taken as given for all variants (the owner's earlier decisions):
- the chain for one declared name: `?vars` > `-d` (last wins) > script value (`var` /
  `ExecOptions.Vars`, last wins) > environment (by scope) > declaration default. The first source
  that defines the name wins, and scope filters the sources;
- `getVar "X"` and `let! x = decl` return the same value;
- the dependency is `Var (name, canonical value)`. A change of value rebuilds; a change of source
  with an equal value does not;
- secrets are readable only through their declaration; the database keeps a digest (`SecretDep`),
  never the value.

Library variables in scope (spec §4.4 and the code). Read through `getVar` in 3.13: `NUGET_PACKAGES`
(env fallback), `NUGET_FETCH`, `NETFX`, `NETFX-TARGET`, `CSC_SERVER` (env pair `XAKE_CSC_SERVER`),
`CSC_TOOLSET`, `FSHARP_CORE_VERSION`, `FSCVER`, `NETSTANDARD_LIBRARY_VERSION`,
`NETSTANDARD_LIBRARY_REF_VERSION`, `NETFX_REFERENCE_ASSEMBLIES_VERSION`, `NETCORE_REF_VERSION`,
`HERMETIC`, `CI` and `LOCK_REFRESH` (both with env fallback), `LOCK_STALE`. Read untracked:
`CSC_JOBS`/`XAKE_CSC_JOBS` and `FSC_JOBS`/`XAKE_FSC_JOBS` (`CompilerRunner.fs:136-150`, from
`ctx.Vars` directly); `SOURCE_DATE_EPOCH` (`Sbom.fs:596`, `Pack.fs:44`) and `XAKE_CSC_SERVER` in
`Csc.RunOptions.Default` (`Csc.fs:32`), both from the raw environment.

## 2. Variant 1: the database remembers declarations

### 2.1 What is stored, and where

Two placements were considered:
- **(i) Declaration inside each dependency**, as `Var (name, value, decl)`. To evaluate at launch,
  the engine would scan every record's dependencies and reconcile declarations of different ages:
  target A built under an old default and target B under a new one. It also repeats the declaration
  in every record. Rejected.
- **(ii) A variable table in the database** (chosen). Each library declaration met during a run is
  stored once, keyed by name, last write wins: name, scope, env name, default (canonical string;
  a digest for a secret), type name, secret mode, owner (`Library "<package>"`), and the
  `Canonical` function's identity, taken as owner and type. Script declarations are **not** stored,
  because the script supplies them on every launch. Storing them would only make two scripts
  sharing a database overwrite each other. The dependency stays `Var (name, canonical value)`.

The 0.6 file is a header followed by a stream of `BuildResult` records (`Database.fs:46-130,
164-175`). The table needs a tagged record stream (`Result | Decl`) and **database 0.7**, which
3.13 cannot read. Compaction rewrites the table with the records.

### 2.2 Launch algorithm

1. Parse the CLI into the `-d` layer and run the script ops into the script layer and the script
   declarations (`varschema`). Take the environment snapshot.
2. Open the database and load the table T and the records.
3. Build the effective declaration set E:
   a. every script declaration;
   b. every T entry whose name the script does not declare. A T entry (owner Library) for a name
      that the script also declares, with different fields, is an error at launch: two owners
      disagree, and a re-run cannot fix that;
   c. a **weak** declaration (`ArgOnly`, no default) for every name that appears in a recorded
      `Var` dependency and is in neither (a) nor (b). This covers old databases and plain `getVar`.
4. Evaluate every declaration in E through the chain, giving the map. Register secret values for
   masking. A required script declaration without a value fails here.
5. Print the banner (or `--help`, which exits).
6. Run. A strong read passes its declaration `d` for name `n`:
   - `n` in E with an equal declaration: return the map value;
   - `n` in E from the script, declaration differs: error (the library and the script disagree).
     This is not a premise stop, because a re-run would hit it again;
   - `n` in E from T or weak, declaration differs, or `n` not in E: **premise stop**. Write `d` to
     T at once (durable), fail the reading target with "declarations changed since the last run
     (`n`: <field> was <a>, is <b>); the database is updated, run again". Start no new targets,
     let in-flight ones finish and store their results, run teardown, and exit with a dedicated
     code (proposal: 3).
   A weak read (`getVar "n"`) returns the map value if `n` has a declaration in E, otherwise the
   raw `-d`/`var` value, otherwise `None`. It never stops.
7. At exit, persist T, which may also gain declarations met by targets that finished after the
   stop.

**"Declaration differs", field by field:** `Scope`; `EnvName` (unless `ArgOnly`); `Default` in its
canonical string form (a digest for a secret); `Secret` mode; `TypeName`; owner, which stands for
the parser. `Description` and `Required` do not count: they change neither the value nor its
canonical form. A weak entry differs from every strong declaration that would give another value,
so in practice from every strong one.

Why a stop is needed at all: targets checked earlier in this run were judged against the map value
of the stale declaration. If the new declaration gives another value, those targets were wrongly
called up to date.

### 2.3 First run, convergence

**First run (empty database).** T is empty, so the first library read of every name is "not in E".
Under the strict constraint the run stops at the first library read. With fail-fast, the run only
gets as far as the reads already in flight, so the second run learns only those declarations and
may stop again at the next library module. Convergence takes **as many runs as there are distinct
"first library reads" on the path**, not one step. Two ways to get one step:
- **(a) Sibling lists.** Each library declaration carries its module's `all` list (`Siblings`), and
  a stop writes the whole list to T. Each library then converges in one step. This is variant 2a's
  export list, used by the database instead of the script.
- **(b) Relaxed first sight.** A name that is in neither T nor any recorded dependency cannot
  affect any up-to-date decision already made in this run, because no record refers to it. So it
  could be evaluated at the read (from the launch snapshot, with the same result), written to T,
  and the run would go on. Only a differing declaration would stop. This is safe, but it breaks
  the letter of the constraint: the banner of that run is incomplete.

**Fresh-clone CI.** A CI job on a fresh clone has no `.xake`, so **every CI run is a first run**.
Strict V1 then stops every CI job (with (a): once per library, and it never converges, because the
next job starts empty again). It works only with (b), or with the database cached or committed
(it is binary, `CLAUDE.md` says do not commit it). The same holds for `noPersist` (`noopDb`) and
for `xakeEngine`, which is `NoPersist = true` by default (`Program.fs:160`).

**Automatic restart.** E and the map are immutable and the stop leaves T updated, so the engine
could restart in-process (new context, same targets) instead of exiting. Targets that finished are
already stored, so a restart is cheap. Bounding the restarts by the number of distinct declaration
changes makes the stop invisible to the user apart from time. This fixes interactive use and
fresh-clone CI (an in-memory T survives the restart). It is extra machinery: Ctrl+C, teardown and
logs then span several passes.

### 2.4 Secrets, engine mode, banner, help, compatibility

- **Secrets.** T stores the declaration and a digest of a secret default, never a value. Every
  secret remembered in T is masked from launch, and `getVar`/`getEnv` of it is refused from the
  first read, which closes spec rev. 3's S-6 gap. A secret first met during the run hits a stop
  (strict) or (b).
- **Engine mode.** The base map is built at `Start` from E. `Demand(t, ?vars)` re-evaluates E with
  the request layer: pure, about 20 names, cheap. A premise stop fails the demand. T is updated,
  and the engine rebuilds its base map once in-flight demands drain. With the default
  `NoPersist = true`, T lives in memory only, so every new engine process starts empty.
- **Banner and `--help`.** Complete for E. `--help` lists the remembered library variables as
  "from the last run" and has no library section before the first run.
- **Old databases (0.4-0.6).** No table. Step 3c gives every recorded name a weak declaration. The
  first library read with a strong declaration differs from it, causing a stop and a re-run, after
  which targets whose value changed rebuild (3.13 recorded `Var (NUGET_PACKAGES, None)`, and 3.14
  gives `.packages`). A 3.13 `EnvVar` (from `CI`, `LOCK_REFRESH`, `Var.*`) stays checked against
  the environment.
- **Old scripts and the consumer's CI.** Nothing to edit. `var`, `-d` and `varschema` keep working,
  and `-d NUGET_PACKAGES=...` from a job reaches the library through the `-d` layer. This is V1's
  real strength. The cost is the first-run behaviour above, which on a fresh clone is every run.
- **Hermetic.** P-2 works by the source in the map. A launch-time `HERMETIC=on` check is possible
  only once T holds `NUGET_PACKAGES`.

### 2.5 Failure modes

- *A script that changes its declarations often.* Script declarations are not in T, so no stop
  follows. Targets rebuild by value as usual.
- *Two scripts sharing a database* (same `DbFileName`). Script declarations are not stored, so they
  do not clash. Two library versions do clash: each script's run finds the other's declaration in
  T, stops, and writes its own. This ping-pong never converges. The repo already avoids a shared
  database (`build.fsc.fsx` uses `.xake.fsc`), but nothing enforces it.
- *A library upgrade that changes a default.* One stop, one re-run, and the targets that read the
  value rebuild. Correct, but a stop for an expected upgrade.
- *A deleted or reset database* (`--reset`, `rm .xake`). Back to the first run.
- *A declaration whose default depends on the machine* (OS, SDK). Differs per machine, so a database
  moved between machines stops.

**Cost: L.** Database 0.7 with a table, the stop protocol and exit code (or the in-process restart),
convergence handling, the engine refresh, tests for every path above.

## 3. Variant 2: libraries do not read variables implicitly

### 3.1 (2a) Explicit import

A library that reads variables publishes its declarations as values with an `all` list
(`DotNetVars.all`, `HermeticVars.all`; the table of spec §4.4 stays as a list of declarations). The
script imports them: `varschema DotNetVars.all`, or a dedicated `import` op with the same meaning.
The library reads only through its declaration values (`let! p = DotNetVars.packages`), and the
store serves only names it has a declaration for.

**Launch algorithm.**
1. CLI layer, script layer, environment snapshot.
2. Collect every declaration: the script's `varschema` records and the imported `VarDecl` lists.
   Two different declarations of one name (e.g. the script redeclares `NUGET_PACKAGES` with another
   default) fail at launch, naming both. The script sets a library variable's *value* with `var`;
   it does not redeclare it.
3. Evaluate all of them into the map, register secrets, check required script declarations, and
   optionally run library policy checks (P-2).
4. Banner or `--help`, with the library variables in a section per owner.
5. Run. A strong read with declaration `d`: name in the map with an equal declaration returns the
   value. A name that is **not imported** fails the reading target with "`NUGET_PACKAGES` is read
   by Xake.Dotnet but not imported; add `varschema DotNetVars.all` to the script". A weak
   `getVar "X"` returns the map value if X is declared, else the raw `-d`/`var` value, else `None`.

**Detection: at launch or at use?** At launch the engine cannot know which library functions the
recipes will call, for the reason in §1. A static "I need these" list registered by the library
would be filled by F# module initialisation, which also runs lazily, at first access during the
run. So the check happens at first use. It is **deterministic**, though: it fails on every run,
the first one included, regardless of the database, like a type error. It never declares anything
mid-run; it refuses. Once the script imports a module, every name that module can read is in the
map. The only remaining failure is "module not imported at all", and it shows at the first library
call of the first build. A launch-time check is possible only for what the script lists itself:
for example, the hermetic package's script-level entry points could insist on `HermeticVars`
having been imported.

**Dependencies.** `Var (name, canonical value)`, unchanged in shape. The check compares it with the
map, or with the weak value for an undeclared name. A record that names an unimported library
variable cannot come from 3.14, because that read failed. One from 3.13 gets the weak value, and
the read then fails anyway. **The database stays 0.6**, unless the `SecretDep` kind change is
kept, which is optional. No `defaulted` flag, no table.

**An existing 3.13 script.** The first library read fails until the script adds
`varschema DotNetVars.all` (and `HermeticVars.all` for the hermetic package). That is a one-line,
mechanical, breaking change for every script that uses Xake.Dotnet tasks. Values then change
legitimately in some places (the `NUGET_PACKAGES` default `.packages`, env fallbacks per spec D-8),
and those targets rebuild once by value. Xake.Dotnet is a separate assembly (`src/dotnet/
Xake.Dotnet.fsproj`), so core cannot import its declarations by default.

**The consumer's CI.** `-d NUGET_PACKAGES=...` from the job reaches the library through the `-d`
layer once the import line is in. Nothing changes in the job.

**Secrets.** Imported or script secrets are all known at launch: masked before the banner, with
`getVar`/`getEnv` of them refused from the first read (closes S-6's gap). **Engine mode.** Base map
at `Start`; per demand, the declarations are re-evaluated with the `?vars` layer. No database
involvement, so it works with `NoPersist`. **Hermetic.** P-2 by the source in the map, checkable at
launch.

**Sharp edges.** The 3.13 break above. A script that imports a subset and misses a name gets the
error at use, so the `all` lists should be the documented unit. A library that adds a variable in
a minor release is already covered by `all`, but a script importing a hand-picked subset breaks.

**Cost: M.** Store and import op, `DotNetVars`/`HermeticVars` modules, readers moved onto them, the
not-imported error, banner and help sections. No database change.

### 3.2 (2b) No variables in libraries

Xake.Dotnet and the hermetic package read no variables. Every setting is an explicit parameter:

| Variable | Becomes |
|---|---|
| `NUGET_PACKAGES` | a package-root setting used by restore, framework resolution, toolset, fsc's FSharp.Core: one global value, not per task |
| `NUGET_FETCH` | a fetch flag on the same global settings (and on `restorePackage`) |
| `NETFX`, `NETFX-TARGET` | the framework fields of `Csc`/`Fsc`/msbuild/`ProjectPlan` (exist per task), with a global default |
| `CSC_SERVER` | the existing `noserver`/`keepalive` task fields, with a global default; `XAKE_CSC_SERVER` stays a raw environment switch or goes |
| `CSC_TOOLSET`, `FSHARP_CORE_VERSION`, `FSCVER` | task fields of `Csc`/`Fsc` |
| `NETSTANDARD_*`, `NETCORE_REF_VERSION` | framework-resolution settings, global |
| `HERMETIC` | a policy setting, global |
| `CI`, `LOCK_REFRESH`, `LOCK_STALE` | parameters of `Locks.generate` / `buildWith` |
| `CSC_JOBS`, `FSC_JOBS` | the existing `Throttle` option |

The global ones need a home. An `ExecOptions` field puts Dotnet-specific fields into core, which
inverts the layering. A generic `Settings` bag on the context is variables under another name. A
`DotnetSettings` record installed by a Xake.Dotnet script op is the cleanest:

```fsharp
let v = {| Packages = Var.string "NUGET_PACKAGES" |> withDefault ".packages"
           Netfx    = Var.string "NETFX" |}
xake ExecOptions.Default {
    varschema v
    dotnet (fun get -> { DotnetSettings.Default with PackageRoot = get v.Packages; Netfx = get v.Netfx })
    ...
}
```

The op runs at launch against the map, so the constraint holds.

**Launch algorithm.** As 2a steps 1-4 for the script's own declarations, then the settings ops
build the library settings from the map. During the run nothing is read by name.

**Dependencies.** A parameter is not tracked by itself: today the `getVar` inside the library
records the `Var`. The library would have to record each setting it uses as a pseudo-dependency
(`Var ("dotnet.packageRoot", v)`, value-based). Otherwise changing `NETFX` would no longer rebuild.

**A 3.13 script, the consumer's CI.** Breaks wherever a library variable was set by `var`/`-d`. The
values silently stop arriving (`-d NETFX=...` reaches nothing). The unused-`-d` warning (spec W-2)
is the only signal. The consumer's job keeps its `-d NUGET_PACKAGES=...` only if the script
declares the variable and forwards it into the settings. Every script repeats the boilerplate.

**Secrets.** No change (library variables are not secrets). **Engine mode.** `?vars` reach a
library only if the settings op is re-run per demand, so per-request library settings need the
op's function kept and re-applied per request. **Hermetic.** "The build's own settings" becomes
literal: whatever the script passes. P-2 cannot tell an env-sourced package folder from the
script's, unless the script hands over the source too, so the policy moves into the script or the
settings carry `(value, source)`.

**Sharp edges.** Dependency tracking has to be re-created by hand for every setting. Every
consumer's script and CI must change. The library's documentation stops being "set NETFX" and
becomes "pass `Netfx`". **Cost: L.**

### 3.3 (2c) Libraries read the environment only

All library settings come from the environment. `-d` and `var` do not reach them. A script drives
them with a `setenv` op, applied at launch, or with `Environment.SetEnvironmentVariable` before the
builder.

**Launch algorithm.** 1. CLI and script layers. 2. Apply `setenv` to an environment overlay.
3. Snapshot the environment, overlay included. 4. The map holds script declarations as before;
library names need no declaration, because with a single source the value is just "the snapshot
entry, else the library's built-in default". 5. Run: library reads are snapshot lookups recording
`EnvVar (name, value)`.

**Dependencies.** `EnvVar (name, value)` against the snapshot. Every name has a value or `None`
without any declaration, so Q-5 cannot arise. This is the only variant where the check needs no
declaration at all.

**A 3.13 script, the consumer's CI.** `var "NUGET_PACKAGES" ".packages"` (`build.fsc.fsx:34`, and
the consumer's pattern) and `-d NUGET_PACKAGES=...` stop working. The job sets `NUGET_PACKAGES` in
its environment, or the script forwards it with `setenv`. `NETFX-TARGET` cannot be exported from a
POSIX shell (a name with `-`), so it needs a rename, another break.

**Secrets.** Already environment-only, so unaffected. **Engine mode.** Loses per-request library
settings: the environment belongs to the process and cannot differ between concurrent demands.

**Hermetic.** `HERMETIC` from the environment contradicts its 3.13 design ("no environment
fallback; the content rule belongs to the build definition", `HermeticMode.fs:69-71`), so it would
need an exception. P-2 refuses an env-sourced package folder as "the machine's". If every library
setting comes from the environment, the build's own folder (`setenv`) is distinguishable from the
machine's only when `setenv` is an overlay that keeps its source. With
`Environment.SetEnvironmentVariable` the source is lost, and the variable also leaks into every
child process (`dotnet restore`, msbuild, `VBCSCompiler`). Reproducibility suffers: the settings
are in the job's environment, not in the command line or the script.

**Sharp edges.** The `-d`/`var` break, the `NETFX-TARGET` name, `HERMETIC`, engine mode. **Cost:
S-M** in the engine, M with the library changes and documentation.

## 4. A hybrid, and why it collapses into 2a

The obvious hybrid is "2a for the launch-time contract, the database only as a cache of *values*
for change detection, never of declarations". The `Var (name, canonical value)` dependency already
is exactly that cache, so the hybrid is 2a.

Two variations were considered:
- **2a + V1's table as a safety net for forgotten imports.** It brings back the database-history
  dependence and the fresh-clone problem, only to avoid a one-line fix that the error message
  names. Not worth it.
- **2a with a transition mode in 3.14.** An unimported library read warns, is evaluated at use from
  the launch snapshot, and becomes an error in 3.15. This is the lazy evaluation the constraint
  forbids, and it brings Q-5 back for that window. It is listed only as a question for the owner
  (Q-B).

## 5. Comparison

| | V1 table | 2a import | 2b parameters | 2c env only |
|---|---|---|---|---|
| Declarations known at launch | from the last run; never on a fresh clone | always, from the script | script's only; libraries have none | none needed |
| First run / fresh-clone CI | stop (or relaxed first sight) | normal | normal | normal |
| One-step convergence | no, unless sibling lists or restart | n/a | n/a | n/a |
| 3.13 script | unchanged | one line to add, else error at first library read | rewrite settings, values silently lost | `var`/`-d` of library names lost |
| Consumer CI `-d NUGET_PACKAGES` | works | works after the import line | works only if the script forwards | must become an env var |
| Dependency check | exact once T is warm | exact | exact only with pseudo-dependencies | exact (`EnvVar`) |
| Database | 0.7 with a table | 0.6 (unchanged) | 0.6 | 0.6 |
| Secrets known at launch | remembered ones only | all | all (script) | n/a (env) |
| Engine mode `?vars` | works; `NoPersist` default has no T | works | needs the op per request | no per-request library settings |
| Hermetic P-2 by source | yes (once T knows the name) | yes, at launch | moves to the script | only with a source-keeping overlay |
| Build semantics depend on `.xake` | yes | no | no | no |
| Cost | L | M | L | S-M (+ docs, renames) |

## 6. Recommendation: 2a, explicit import

1. **The declaration set is a property of the script source, not of the database's history.** So
   launch completeness holds by construction on every run: the first one, a fresh-clone CI job,
   `noPersist`, a new engine process. V1 meets the constraint only on a warm database. On the CI
   machine of a fresh clone that is never, and V1 either stops every job or falls back to
   evaluating at use, which is what the constraint forbids.
2. **It removes machinery instead of adding it.** No database format change, no table, no stop and
   re-run protocol, no `defaulted` flag, no read-before-declaration error, no late-declaration log.
   The `Var (name, value)` dependency keeps its shape, and the check becomes exact (Q-5 gone). The
   one new failure, "not imported", is deterministic and names its own fix.

Also in favour: the consumer's CI keeps `-d` (unlike 2b/2c); P-2 and other policies can run at
launch; engine mode needs no database. Against: a one-line breaking change for every script that
uses Xake.Dotnet tasks, detected at the first library call rather than before the first target.
That fits a 3.14 release note next to the other 3.14 breaks (spec §8). 2b and 2c each lose `-d`
for library settings and give up either dependency tracking (2b) or the "build's own settings"
policy (2c), and V1's only advantage, no script edit, does not survive the fresh-clone case.

What carries over from spec rev. 3 unchanged: the chain (M-5 to M-10), typed defaults and
canonical values (D-4, D-5), conflict equality (D-6, now at launch), the library declaration table
(§4.4, including the `CSC_SERVER` pair and lenient `CI`), options precedence (§6.1), the banner
format (P-3) and the diagnostics E-1, E-3, E-5, E-6, E-7, W-1 to W-3. What goes: S-5's memo, D-7,
E-2, L-1, the `defaulted` flag, database 0.7 for `Var`, DEP-5 b/c, Q-5.

## 7. Questions only the owner can answer

- **Q-A** Does a deterministic "not imported" error at the first library read satisfy "no
  declarations appearing during the run"? It refuses rather than declares, but it fires during the
  run, not before the first target.
- **Q-B** In 3.14, a hard error for unimported library reads, or one release of warn-and-evaluate
  (§4), knowing that window breaks the constraint and brings Q-5 back?
- **Q-C** Import granularity: only whole modules (`DotNetVars.all`), or subsets allowed, at the risk
  of an error when a library minor release reads a new name?
- **Q-D** Do the untracked settings (`CSC_JOBS`/`FSC_JOBS` and their `XAKE_*` env pairs,
  `SOURCE_DATE_EPOCH`, the raw `XAKE_CSC_SERVER` in `Csc.RunOptions.Default`) join the import
  model, or stay raw environment reads outside the store?
- **Q-E** Should `getEnv` also read the launch snapshot (it is live today, spec N-4)? Otherwise it
  is the one value that can still change during a run.
- **Q-F** If V1 is preferred anyway: strict stop, or "relaxed first sight" (§2.3 b), and exit with a
  re-run code or restart in-process?

## 8. Variant 3: the script states how a value is computed (owner's direction, 2026-10-28)

Written after #103 merged into this branch; see [after-103.md](after-103.md). `ExecOptions.Vars`
(the input values) and `Var.*` (typed declarations) are separate concepts. The script owns the
rule for each name; libraries only read names.

```fsharp
xake {
    var "NUGET_PACKAGES" [Env "XX_NUGET_PACKAGES"; Default ".packages"]
    var "CONFIG" "Release"          // unchanged form: a constant
}
```

- **Syntax: a list of sources, first defined wins.** The list is the chain itself. `var` is
  overloaded on `string` and on the list; checked on the SDK 10 compiler (an overloaded custom
  operation compiles and resolves). The record form (`{ Env = Some ...; Default = Some ... }`) is
  longer at the call site and no simpler to build.
- **Evaluated when the op is applied**, i.e. at launch: the environment is read once, the result
  enters `ExecOptions.Vars` as the script value. `-d` is prepended by `ApplyCommandLine` (#103) and
  still wins; engine `?vars` must go on top as well (fix the order at `XakeScriptBuilder.fs:298`).
- **One dependency per read: `Var (name, final value)`.** Moving an equal value between the
  environment and `-d` does not rebuild; no `EnvVar` is recorded for a rule's environment source.
- **Libraries read only the variable set (owner: option б).** No implicit environment fallback in
  Xake.Dotnet / Xake.Hermetic.Dotnet; an unset name takes the library's fixed default, no
  environment read. Breaking: a CI that set `NUGET_PACKAGES` only in the environment adds a `var`
  rule (or `-d`). Release note required.
- **The banner shows the real source (owner: needed):** `cli`, `request`, `script`,
  `env:<NAME>`, `default`. Needs the source kept per name next to the value; the shape (a new
  `ExecOptions` field vs a richer `Vars` element) is an implementation decision.
- **`Var.*` stays** as typed reading (int/bool) and as an alternative description; no modularity
  is required of it.

**Secrets (owner, 2026-10-28): a separate element of the list**, e.g.
`var "TOKEN" [Secret; Env "CI_TOKEN"]`; `SecretHashed` likewise (the two existing modes,
presence and HMAC). The mask and the dependency rules are those of 3.13.

**`Var.*` (owner, 2026-10-28): `varschema` takes the declaration completely.** Its env name, scope,
default and secret mode are evaluated by `varschema` at launch, exactly like a source list:
`Var.string(envVar = "X") |> withDefault "d"` ≡ `var "N" [Env "X"; Default "d"]`. The fallback
becomes a declaration, not an instruction run on each read: `let! x = schema.Field` in a recipe is a
lookup in the variable set, one `Var (name, value)` dependency, no `getEnv` (today
`Vars.fs:105-133` reads the environment on every read and records `EnvVar`). `Var.*` is the typed
spelling of the same rule plus `--help` text.

Points for spec rev. 4: a `Var` read in a recipe whose declaration never went through `varschema`
(proposed: variable set only, then its own constant default, no environment read, consistent with
option б); the same name declared by both `var [...]` and `varschema` (error vs last wins);
`-d` of a secret (3.13 refuses it for env-only secrets).
