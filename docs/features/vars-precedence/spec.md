# Spec: variable evaluation

Status: rev. 3 for the owner's approval, 2026-10-28. Rev. 1 and rev. 2 were reviewed in PR #102.
Rev. 2 had M-3 without a combinator and `SecretDep` without a kind. Rev. 3 replaces registration at
task construction with the `defaulted` flag (DEP-1, DEP-5, database 0.7), makes secrets readable
only through their declaration (S-6), sets the `NUGET_PACKAGES` default to `.packages`, makes the
library variables `ArgAndEnv`, models `CSC_SERVER` as it is in 3.13, and adds §8.1.

This spec formalizes the owner's decisions P1-P11 of 2026-10-28 and the two reviews. They are not
to be reopened. It supersedes the "Options" of [analysis.md](analysis.md); the analysis stays the
record of 3.13 behaviour (file:line references against origin/dev 92ef634; the banner and `setup`
of #101, 4ef149d, are taken into account here). Where the decisions left a gap, the rule is marked
**[gap]** and listed in §10. Keywords MUST, MUST NOT, SHOULD, MAY are normative. Every rule has an id
that the conformance tests of §9 reference. Per the owner's rule, no code before this spec is
approved and the tests of §9 exist.

## 1. Goal, principle, non-goals

**G-1 (Evaluator, P1).** A variable is a named value computed **once per run** from its sources by
one evaluator, the `VarStore` of the run. `Var.*` values are *declarations* (how to compute the
value), not readers. Every reader (`let! x = someVar`, `getVar "X"`, a library, the banner, the
dependency check) MUST obtain the value from the store; two readers of one name in one run MUST see
the same value.

**G-2.** What the build tracks is "the value of X changed", never "where X was read from": a change
of source with an equal value MUST NOT rebuild anything; a change of value MUST rebuild the targets
that read X.

Non-goals:
- **N-1** No auto-discovery of declarations (no reflection over loaded assemblies, no registration
  at task construction): a declaration is known to the store only once a reader or `varschema`
  hands it over (D-1).
- **N-2** No environment lookup for names nobody declared: `getVar "X"` alone never consults the
  environment (the weak declaration is `ArgOnly`, S-4).
- **N-3** No source in dependencies: the database never records which of `Request`, `Cli`,
  `Script`, `Env` a value came from (the `defaulted` flag of DEP-1 is not a source).
- **N-4** `getEnv` is unchanged except for secrets (S-6): it reads the process environment (live,
  not the snapshot) and records `EnvVar`; it is not a variable read.

## 2. Model

### 2.1 Declarations

**M-1.** A declaration is the untyped record

```fsharp
type VarDecl = {
    Name        : string           // the store key; also the -d / var / ?vars key
    Scope       : LookupScope      // ArgAndEnv | ArgOnly | EnvOnly
    EnvName     : string           // ignored when Scope = ArgOnly
    Default     : string option    // the string form of the typed default (P8)
    Required    : bool
    Secret      : SecretMode       // NotSecret | SecretPresence | SecretHash
    TypeName    : string           // "string", "int", "bool", or the type's name
    Canonical   : string -> string // format (parse v); raises on a value that does not parse
    Description : string option
    Owner       : DeclOwner }      // Script | Library of string
```

The typed `OptionalVar<'t>` / `RequiredVar<'t>` keep their API and expose `.Decl : VarDecl`
(`Parse : string -> 't`, `Format : 't -> string` stay on the typed value).

**M-2.** `Name` is the explicit `name`, else `cliArg`, else the `varschema` field name. `EnvName`
defaults to the 3.13 derivation `toEnvName Name` (camelCase → UPPER_SNAKE), which is `Name` itself
for an UPPER_SNAKE name; `envName "Y"` (new combinator) sets it explicitly.

**M-3.** `Owner` is a plain field, `Script` by default; a library module sets
`Owner = Library "<package>"` on the declarations it builds (no public combinator). It only selects
the source label of the default: `Default` for `Script`, `LibraryDefault` for `Library`.

### 2.2 Sources and the chain

**M-4.** `VarSource = Request | Cli | Script | Env | Default | LibraryDefault`, in this order of
precedence.

**M-5 (chain B, P2).** The value of a declaration `d` is

```fsharp
let evaluate (d: VarDecl) : (string * VarSource) option =
    [ Request, request.TryFind d.Name                       // engine mode ?vars
      Cli,     cli.TryFind d.Name                           // -d, last occurrence
      Script,  script.TryFind d.Name                        // var op / ExecOptions.Vars, last in script order
      Env,     (if d.Scope = ArgOnly then None else envSnapshot.TryFind d.EnvName)
      (if d.Owner = Script then Default else LibraryDefault), d.Default ]
    |> List.filter (fun (src, _) -> d.Scope <> EnvOnly || not (List.contains src [Request; Cli; Script]))   // M-9
    |> List.tryPick (fun (src, v) -> v |> Option.map (fun v -> v, src))
```

The first source that *defines* the name wins; no later source is consulted.

**M-6 (definedness).** A source defines a name iff it has an entry for it:
- `-d X=` (and `-d X` with no separator) defines X as the empty string;
- `var "X" ""` defines X as the empty string;
- an environment variable defines X iff it is present in the snapshot (an empty value defines `""`
  on Unix; on Windows an empty variable does not exist);
- a declaration without `withDefault` does not define.
The evaluator MUST NOT trim or interpret values; trimming and parsing are the reader's (`Canonical`).

**M-7 (duplicates).** Within `Cli` the last `-d X=` on the command line wins. Within `Script` the
last write wins, where `ExecOptions.Vars` entries come first and `var` ops follow in script order.
Within `Request` the last pair wins.

**M-8.** `var "X" v` (Script, layer 3) and `withDefault v'` on a declaration of X (layer 5/6) are
different layers and MAY coexist; Script wins. The error "two defaults" exists only between two
strong declarations of X with different `Default` (D-6).

**M-9 (EnvOnly).** For `Scope = EnvOnly`, `Request`, `Cli` and `Script` are not sources. An entry
for the name (or for `EnvName`) in one of them:
- for a secret declaration MUST fail the run (exit 2, E-3), as in 3.13;
- otherwise MUST be ignored with warning W-1, once per run per name.

**M-10 (ArgOnly).** For `Scope = ArgOnly`, `Env` is never consulted.

**M-11.** A typed reader converts the winning string with `Parse`; a parse failure fails the reader
(the 3.13 messages of `Vars.fs:36-52`, the value omitted for a secret). A `RequiredVar` without a
default whose evaluation is `None` fails with E-6.

### 2.3 Worked examples

`NUGET_PACKAGES` declared by `DotNetVars.packages` (`ArgAndEnv`, env `NUGET_PACKAGES`, library
default `.packages`, resolved against the project root by the reader):

| `-d` | `var` | env | value | source |
|---|---|---|---|---|
| `/ci/pkgs` | `pkgs` | `/m/pkgs` | `/ci/pkgs` | cli |
| (none) | `pkgs` | `/m/pkgs` | `pkgs` | script |
| (none) | (none) | `/m/pkgs` | `/m/pkgs` | env |
| (none) | (none) | (none) | `.packages` | library default |
| `NUGET_PACKAGES=` | `pkgs` | `/m/pkgs` | `""` | cli |

The last row: `-d X=` defines X as empty, so neither `var` nor the environment is consulted. The
reader treats a blank value as "the machine folder" (`~/.nuget/packages`, the 3.13 meaning), which
`HERMETIC=on` refuses (P-2). `-d NUGET_PACKAGES=a -d NUGET_PACKAGES=b` yields `b` (M-7).

`-d AAA=hello`, a recipe reads `let! a = Var.string "AAA" |> withDefault "x"` → `hello (cli)`;
without `-d` and with `AAA` absent from the environment → `x (default)`; with `AAA=env` →
`env (env)`. `getVar "AAA"` in another recipe of the same run returns the same value, because the
strong declaration was registered first (otherwise D-7 applies).

Secret: `token = Var.secret "API_TOKEN"` (`EnvOnly`, `SecretPresence`). Environment `API_TOKEN=s3`
→ `s3 (env)`, masked everywhere, recorded as a digest (DEP-4). `-d API_TOKEN=s3` → the run fails
with E-3 before the banner, the value registered for masking first. `getVar "API_TOKEN"` or
`getEnv "API_TOKEN"` after the declaration is known → E-7 (S-6).

## 3. `VarStore`: API and lifecycle

**S-1 (P3).** `ExecContext.Vars : (string * string) list` is removed; `ExecContext.Variables :
VarStore` replaces it:

```fsharp
type VarSource = Request | Cli | Script | Env | Default | LibraryDefault

type VarStore =
    abstract Evaluate    : VarDecl -> (string * VarSource) option  // registers d (D-1 b), memoized
    abstract TryGet      : string  -> (string * VarSource) option  // by the registered declaration, else weak (S-4)
    abstract Declared    : unit -> VarDecl list                    // registered strong declarations
    abstract WithRequest : (string * string) list -> VarStore      // engine-mode overlay (S-7)
```

`Evaluate` and `TryGet` record nothing; recording is the job of the recipe-level readers (DEP-1).

**S-2 (inputs).** The store is built once per run, in `Run` (CLI and dry-run/dump) or in
`XakeEngine.Start` (engine mode), from immutable inputs: the `-d` list, the script values
(`ExecOptions.Vars` then `var` ops), the `varschema` declarations, and an **environment snapshot
taken once** at construction. The store MUST NOT read the live environment afterwards.
`ExecOptions.Vars` stays as the script-values input only; the CLI list is kept apart (O-1).

**S-3 (at `Run`, before the banner).** In this order: register the `varschema` declarations (D-1 a,
conflicts D-6); evaluate each of them, registering secret values for masking (S-6); fail on E-3
and on E-6 for a `Required` schema declaration without value; print `--help` (which exits) or the
banner (P-3). Names first declared later evaluate lazily.

**S-4 (weak declaration).** `TryGet name` with no registered declaration evaluates the implicit
weak declaration `{ Name = name; Scope = ArgOnly; Default = None; Secret = NotSecret;
TypeName = "string"; Canonical = id; Owner = Script }`. It does not register a strong declaration.

**S-5 (memo, P4).** The store memoizes per name for one run (the lifetime of the `getChangeReasons`
memo and of `Scheduler.newRun`; in engine mode, the engine's lifetime, S-7): the result, `None`
included, is computed once with `ConcurrentDictionary.GetOrAdd`; the declaration of a name is fixed
atomically at its first strong registration; later registrations are compared against it (D-6).
Evaluation is pure over the S-2 inputs, so a race that evaluates twice yields equal results.

**S-6 (secrets).** A secret is readable only through its declaration.
- A value whose declaration is secret MUST be registered with `SecretMask` on its first evaluation,
  before it is returned to anyone; for schema declarations that is before the banner.
- A weak read `getVar "X"` where `X` is the `Name` of a secret declaration known to the run's store
  (schema, or registered by an earlier read), and `getEnv "Y"` where `Y` is the `EnvName` (or `Name`)
  of such a declaration, MUST fail with E-7 instead of returning the value. A read before the
  declaration is known is not detected; `varschema` makes the declaration known from `Run`.
- The 3.13 global `Secrets.declare` registry becomes the store's: secrecy is a property of the run's
  declarations, not of the process.

**S-7 (engine mode).** `XakeEngine` builds one base store at `Start`. `Demand(target, ?vars)` uses
`base.WithRequest vars`: an overlay whose `Request` layer is `vars`, with its own memo for the names
`vars` defines and delegating every other name (and every registration) to the base. Two demands with
different `?vars` MUST NOT see each other's request values. `StopAsync` (teardown) uses the base
store. Unused `?vars` are not reported.

**S-8 (dependency checks).** `DependencyAnalysis` MUST consult the run's store (S-5), never a list
(DEP-5).

## 4. Declarations

### 4.1 Entry paths (P5)

**D-1.** A strong declaration enters the store in one of two ways; a third, weak, one never registers:
- (a) `varschema` at `Run` (S-3). `varschema` accepts the 3.13 record form and, new, a
  `VarDecl list` (so `varschema DotNetVars.all` works);
- (b) at the point of use: a recipe binds a typed declaration (`let! p = DotNetVars.packages`) or
  calls `ctx.Variables.Evaluate decl`. The declaration is an ordinary F# value carried by the reader;
- (c) weak read: `getVar "X"` (S-4).

**D-2 (no discovery, N-1).** The banner lists what is declared at `Run` (D-1 a) plus the undeclared
names defined by `-d`/`var` (P-3). A name whose first strong declaration arrives later is logged
once at `Info` on its first evaluation, text L-1.

**D-3 (library reads).** A library MUST read its variables through its own declarations; a script
reads them through the exposed values (`let! p = DotNetVars.packages`). A library MUST NOT call
`getVar` for a name it declares.

**D-4a (recommended practice).** A script SHOULD add the library declarations it uses to its schema,
`varschema DotNetVars.all` (or a subset), so that they appear in `--help` and in the banner from
`Run`, and so that the dependency check judges them by their declaration (DEP-5). This is a
recommendation: a script without it stays correct for defaulted values (DEP-5); see DEP-5 c for the
one case it does not cover.

### 4.2 Typed defaults (P8)

**D-4.** `withDefault x` stores `x` and its string form `Format x` in `Default`. `Format` defaults to
`string x` (`ToString`, invariant culture); `Var.create` gains `?parse` and `?format` (default parse:
3.13 `Convert.ChangeType`, invariant culture).

**D-5.** When a declaration with a default is registered, the store MUST check
`Parse (Format x) = x`; a violation fails (startup for schema, first evaluation otherwise) with E-5.
`Canonical v = Format (Parse v)`.

### 4.3 Conflicts (P6)

**D-6.** Two strong declarations of one name are *equal* iff they have the same `Scope`, `EnvName`
(when not `ArgOnly`), `Default`, `Secret` and **[gap]** `TypeName`. `Description`, `Required` and
`Owner` do not take part (the first registered `Owner` labels the default). A registration that is
not equal to the fixed one fails with E-1, naming both. Two equal declarations are one.

**D-7 (read before declaration).** A weak read of X while X has no strong declaration marks X "read
before its declaration" and memoizes the weak value. When a strong declaration of X arrives, the
store evaluates X by it and compares with the value already returned: equal (string, source
ignored) → no message; different → E-2. No name ever has two values in one run, in any order of
reads. Reads by the banner, `--help` and the dependency check (DEP-5) do not mark.

### 4.4 Library declarations

**D-8.** Every library variable is `ArgAndEnv` with `EnvName = Name` (CI and developer machines set
the environment; one rule for all), except the compiler-server pair of D-9. This widens 3.13, where
most of them were `getVar`-only (release note, V-3). Parsing lives in the declaration, so every
source is parsed alike (G-1). `HERMETIC` lives in Xake.Dotnet (`HermeticMode.fs:75`), hence in
`DotNetVars`.

| Module.value | Name | Scope | EnvName | Default (owner lib) | Notes |
|---|---|---|---|---|---|
| `DotNetVars.packages` | `NUGET_PACKAGES` | ArgAndEnv | `NUGET_PACKAGES` | `.packages` | relative → project root; `~/` → user profile; blank → machine folder |
| `DotNetVars.fetch` | `NUGET_FETCH` | ArgAndEnv | `NUGET_FETCH` | `on` | off/false/no/0 = off |
| `DotNetVars.netfx` | `NETFX` | ArgAndEnv | `NETFX` | none | |
| `DotNetVars.netfxTarget` | `NETFX-TARGET` | ArgAndEnv | `NETFX-TARGET` | none | also read by `Described.fs:626`; POSIX shells cannot export a name with `-` |
| `DotNetVars.cscServer` | `CSC_SERVER` | ArgOnly | - | none | D-9 |
| `DotNetVars.cscServerEnv` | `XAKE_CSC_SERVER` | EnvOnly | `XAKE_CSC_SERVER` | `on` | D-9 |
| `DotNetVars.cscToolset` | `CSC_TOOLSET` | ArgAndEnv | `CSC_TOOLSET` | none | |
| `DotNetVars.fsharpCoreVersion` | `FSHARP_CORE_VERSION` | ArgAndEnv | `FSHARP_CORE_VERSION` | none | |
| `DotNetVars.fscVer` | `FSCVER` | ArgAndEnv | `FSCVER` | none | |
| `DotNetVars.netstandardLibraryVersion` | `NETSTANDARD_LIBRARY_VERSION` | ArgAndEnv | same | none | built-in pick stays in the reader |
| `DotNetVars.netstandardLibraryRefVersion` | `NETSTANDARD_LIBRARY_REF_VERSION` | ArgAndEnv | same | none | |
| `DotNetVars.referenceAssembliesVersion` | `NETFX_REFERENCE_ASSEMBLIES_VERSION` | ArgAndEnv | same | none | |
| `DotNetVars.netcoreRefVersion` | `NETCORE_REF_VERSION` | ArgAndEnv | same | none | |
| `DotNetVars.hermetic` | `HERMETIC` | ArgAndEnv | `HERMETIC` | `off` | 3.13 had no env fallback on purpose |
| `HermeticVars.ci` | `CI` | ArgAndEnv | `CI` | none | D-10 |
| `HermeticVars.lockRefresh` | `LOCK_REFRESH` | ArgAndEnv | `LOCK_REFRESH` | none | blank = unset (reader) |
| `HermeticVars.lockStale` | `LOCK_STALE` | ArgAndEnv | `LOCK_STALE` | `fail` | fail/warn |

`DotNetVars.all` / `HermeticVars.all : VarDecl list` list every row of the module. Sketch:

```fsharp
module DotNetVars =
    let packages =
        Var.string "NUGET_PACKAGES" |> withDefault ".packages" |> owned
        |> describe "Package folder; relative to the project root"
    let cscServer = Var.create<CscServerSetting>("CSC_SERVER", parse = CscServerSetting.parse,
                                                format = CscServerSetting.format) |> argOnly |> owned
    let cscServerEnv = Var.create<bool>("XAKE_CSC_SERVER", parse = Switch.parseLenient) |> envOnly
                       |> withDefault true |> owned
    // ...       (owned: internal helper setting Owner = Library "Xake", M-3)
    let all : VarDecl list = [ packages.Decl; fetch.Decl; cscServer.Decl; cscServerEnv.Decl; (* ... *) ]
```

`DotNetFwk.packageRoot` reads `let! p = DotNetVars.packages` and resolves it; `packageRootWithSource`
returns the store's `VarSource` instead of `PackageRootSource` (P-1).

**D-9 (compiler server, the 3.13 semantics kept).** In 3.13 (`Csc.fs:30-63`, `docs/csc-server.md`)
the two names carry different things: `CSC_SERVER` (script variable, `-d`/`var`) is one value with
three meanings, `on` | `off` | a number of keepalive seconds (implying the server), case-insensitive;
`XAKE_CSC_SERVER` (environment only) is a switch: `0`/`false`/`off`/`no` → in-process, anything else
→ server. Neither is a plain bool or int, so they are modelled as two declarations:
- `cscServer`: `CSC_SERVER`, `ArgOnly`, type `CscServerSetting = On | Off | Keepalive of int`;
  `parse` accepts `on`, `off` and a non-negative integer (trimmed, case-insensitive); any other
  value is a parse error (M-11), no longer the 3.13 warning-and-server guess;
- `cscServerEnv`: `XAKE_CSC_SERVER`, `EnvOnly`, bool with the 3.13 lenient switch parser, library
  default `on`.
The reader's order is unchanged: the target's own `noserver`/`keepalive`, then `cscServer`, then
`cscServerEnv`. `Csc.RunOptions.Default` (`Csc.fs:166`), used by a direct `Csc.run` outside any
recipe, keeps reading `XAKE_CSC_SERVER` from the environment; it has no store and records nothing.

**D-10 (`CI`).** One lenient parser for every source: unset, `""`, `0`, `false`, `off`, `no`
(trimmed, case-insensitive) → off; any other value → on. This is the 3.13 environment rule; a
`-d CI=bogus`, an error in 3.13, is now on.

## 5. Dependencies and database (P7)

**DEP-1.** Every read from inside a recipe (`getVar`, a bound `Var.*`, a library read through its
declaration) MUST record one dependency `Var (name, value, defaulted)`:
- `value : string option` is `None` when undefined, otherwise the **canonical** form `Canonical v`
  (D-5; `v` itself for weak reads);
- `defaulted : bool` is true iff the winning layer was `Default` or `LibraryDefault`.
Two reads of one name in one recipe record one dependency.

**DEP-2.** No source MUST be recorded: `defaulted` only says "no source defined the name, the
declaration's default applied"; whether the value came from `Request`, `Cli`, `Script` or `Env` is
never recorded. The evaluator MUST NOT write `EnvVar`; `getEnv` keeps writing `EnvVar` (N-4).

Rationale (DEP-1, DEP-2): the dependency carries the value, not the source, because a declaration
can change between runs (scope, env name, default). The previous value then legitimately "falls
off" and the target rebuilds; recording the source would make such a change invisible or yield a
wrong "unchanged". `defaulted` exists because the default belongs to a declaration the next run's
check may not know yet (a library declaration is registered only when a recipe reads it, D-1 b).

**DEP-3.** Reads outside recipes (banner, `--help`, S-3 checks, the dependency check itself) MUST
NOT record anything.

**DEP-4 (secrets).** A read of a secret records `SecretDep (name, digest)` (no source kind) with the
3.13 digest rules (`SecretPresence`: set/unset; `SecretHash`: salted HMAC), never the value.

**DEP-5 (check).** At the next run, `Var (name, recorded, defaulted)` is judged by the registered
declaration of `name` if any (schema, or read earlier in this run), else by the weak one (S-4):
- (a) if some source (`Request`, `Cli`, `Script`, `Env`, as the declaration's scope allows)
  defines the name → up to date iff the canonical value equals `recorded`;
- (b) if no source defines it and `defaulted = true` → with a registered declaration, up to date iff
  its default (canonical) equals `recorded`, so a changed default rebuilds; with the weak
  declaration, up to date (the default would apply again);
- (c) if no source defines it and `defaulted = false` → up to date iff `recorded = None`.
Otherwise the change reason is C-1. `SecretDep (name, d)` is judged against the same evaluation
through `Secrets.changeReason`. A check-time evaluation by the weak declaration MUST NOT mark D-7.
Note on (a)/(c) with the weak declaration: it is `ArgOnly`, so a library value that came from the
environment (`defaulted = false`, recorded `Some v`) is judged `None` and the target rebuilds on
every run unless the declaration is registered before the check, i.e. `varschema` (D-4a, Q-5).

**DEP-6 (database 0.7, compatibility).** The database version becomes **0.7** for the third field of
`Var`. `SecretDep` loses its source kind in the type; the 0.7 pickle writes it without. Records of
0.4-0.6 load as is: an old `Var (n, v)` loads as `Var (n, v, false)`; an old `EnvVar (n, v)`
(including those 3.13 `Var.*` wrote for env-sourced values) is still checked against the current
environment, so an unchanged environment does not rebuild and no migration is needed; an old
`SecretDep` with either kind is checked by its digest only, against the store value of the name.
The first rebuild of such a target writes the new form. 3.13 cannot read a 0.7 database.

## 6. Options, policies, banner

### 6.1 Options precedence (P9)

**O-1.** `xakeArgs` MUST keep the options parsed from the command line apart from the initial
`ExecOptions` and apply them in `Run` **over** the result of the script ops, field by field.
`IgnoreCommandLine = true` keeps skipping the CLI. `-nologo` and `--help` keep acting at parse time.

**O-2.** Resulting precedence (higher wins):

| Setting | CLI | Script op | Initial `ExecOptions` |
|---|---|---|---|
| project root | `-r` | `rootdir` | `ProjectRoot` |
| console level | `-ll` | `consolelog` | `ConLogLevel` |
| file log name | `-fl` | `filelog name` | `FileLog` |
| file log level | `-fll` | `filelog _ level` (default `Chatty`) | `FileLogLevel` |
| targets | targets on the CLI | `want` | `Targets` |
| threads | `-t` | - | `Threads` |
| dry run, dump, reset db, progress | switches set them | `dryrun` sets dry run | fields |
| variables | `-d` (M-5 layer 2) | `var` (layer 3) | `Vars` (layer 3, before `var`) |

`-fl` without `-fll` keeps the level of the op (or `ExecOptions`), and vice versa. `wantOverride`
replaces the targets whatever the CLI says; it stays the only hard override. Boolean switches
(`--dryrun`, `noPersist`) combine by OR.

### 6.2 Policies (P10)

**P-1.** A library policy that needs to know *where* a value came from MUST use the `VarSource`
from `Evaluate`/`TryGet`, not a second read of the environment.

**P-2.** `HERMETIC=on` MUST refuse a package folder whose `NUGET_PACKAGES` source is `Env`, or whose
value is blank (the machine folder), with the 3.13 message of `HermeticMode.fs:155`; every other
source, the project-local default `.packages` included, is accepted. Rationale: the lock must be
reproducible from the build's own settings, and a machine-level profile folder is not one. The
source is a property of the run, not recorded (DEP-2).

**P-3 (banner).** The banner's vars section prints, sorted by name, one line per name declared at
`Run` and per name defined by `-d` or `var` (undeclared ones with the value of their highest
defining layer, not evaluated, so the banner never triggers D-7):
`    NAME=value (source)` with `source` ∈ `request | cli | script | env | default | library default`;
a secret's value as `***`; a declared name without value as `    NAME (not set)`. The 3.13 label
`(script/cli)` goes away.

## 7. Diagnostics (exact texts)

Errors fail the run with exit 2 (`XakeException` under `ThrowOnError`); at `Run` they are printed
before the banner; at first evaluation they fail the reading target.

- **E-1** `Variable 'X' is declared twice with different settings: <a> and <b>; declare it once (or identically)`
  where `<a>`/`<b>` render as `scope=ArgAndEnv env=X default=v secret=no type=string (<owner>)`.
- **E-2** `Variable 'X' was read before its declaration (getVar gave 'a' (cli), the declaration gives 'b' (env)); read it through <decl> or declare it in varschema`
  where `<decl>` is the declaring value if known (e.g. `DotNetVars.packages`), else `its declaration`.
  Secret values are `***`.
- **E-3** (3.13 text kept) `Variable 'X' is secret: secret variables come from the environment, set ENV instead of passing -d X=...`
- **E-5** `Variable 'X': the default 'v' does not survive format/parse (got 'w'); fix the declaration's format`.
- **E-6** (3.13 text kept) `Required variable 'X' is not provided`.
- **E-7** `X is a secret; read it through its declaration` (from `getVar "X"` or `getEnv "X"`, S-6).
- **W-1** `Variable 'X' is read from the environment only (ENV); its -d/var value is ignored`.
- **W-2** (end of a run in `Run` mode) `-d X was not used by the build` (one line per name; not in
  dry-run/dump, not for engine `?vars`; a name read only by the banner counts as unused).
- **W-3** (3.13 text kept) the warning for an `ArgAndEnv` secret given by `-d`.
- **L-1** (`Info`, once per name) `NAME=value (source)`, value masked for a secret, e.g.
  `NUGET_PACKAGES=.packages (library default)`.
- **C-1** (change reason) `Variable X changed from 'a' to 'b'`; `<unset>` for `None`,
  `'v' (default)` for a defaulted value. For readability only, the check MAY name the scope of the
  declaration in force at check time: `Variable X (environment) changed ...` for `EnvOnly`,
  `(argument)` for `ArgOnly`, nothing for `ArgAndEnv`; it says nothing about the source of either
  value. For a secret the 3.13 `Secret variable X is now set|is now unset|changed`. Replaces
  `Global script variable %s was changed '%A'->'%A'` (`DependencyAnalysis.fs:101`).

## 8. Compatibility and versioning (P11)

### 8.1 What an existing 3.13 script sees

- `var "X" v` keeps working; it is now overridden by `-d X=...` (and engine `?vars`).
- `getVar "X"` is unchanged for ordinary names: same value, still a dependency.
- Tasks of a script without `varschema` do not rebuild because of the upgrade: values the library
  defaults are recorded `defaulted` and stay up to date (DEP-5 b). Exception: a library value taken
  from the environment rebuilds on every run until `varschema DotNetVars.all` is added (DEP-5, Q-5).
- `consolelog`, `filelog`, `rootdir` yield to `-ll`, `-fl`/`-fll`, `-r`.
- `NUGET_PACKAGES` unset now means `.packages` under the project root, not `~/.nuget/packages`: the
  first build restores into the project. `-d NUGET_PACKAGES=` keeps the machine folder.
- Library variables (`NUGET_FETCH`, `NETFX`, `HERMETIC`, `CSC_TOOLSET`, `LOCK_STALE`, ...) now also
  read the environment variable of the same name.
- `CSC_SERVER` with a value other than `on`/`off`/a number fails instead of warning.
- A secret can no longer be read with `getVar`/`getEnv` once its declaration is known (E-7).
- `EnvVar` is no longer written for variables; the database becomes 0.7 (3.13 cannot read it back).

### 8.2 Versions and release note

**V-1.** Xake **3.14**, breaking: `ExecContext.Vars` removed (`ExecContext.Variables : VarStore`);
`VarStore`, `VarSource`, `VarDecl` (with `Owner`), `envName`, `Var.create ?parse ?format`,
`varschema` over `VarDecl list` added; `Var` dependency gains `defaulted`; `SecretDep` without
source kind; database 0.7 (reads 0.4+); CLI over ops for options (O-2). `Util.getVar` (internal)
goes. Xake.Dotnet (in the Xake package) reads through `DotNetVars`.

**V-2.** Xake.Hermetic.Dotnet 0.9 references Xake `[3.14, 3.15)` and reads `CI`, `LOCK_REFRESH`,
`LOCK_STALE` through `HermeticVars`, `NETFX-TARGET` and `HERMETIC` through `DotNetVars`.

**V-3 (release note).** The items of §8.1, plus: `-d X=1 -d X=2` gives `2` (was `1`) and
`ExecOptions.Vars` no longer beats `-d`; `varschema ... withDefault` defaults are now values seen by
`getVar` once declared; `Var.env` given by `-d` warns (W-1) instead of silently ignoring; the banner
shows the real source and unused `-d` names are reported (W-2); `CI` from `-d`/`var` is parsed as
the environment value was (D-10); Xake.Dotnet behaviour change: the `NUGET_PACKAGES` default and
`HERMETIC=on` accepting it (P-2); scopes widened to `ArgAndEnv` (D-8).

**V-4 (migration).** `var` stays the way to set a value in the script. `getVar "NUGET_PACKAGES"` in
a script recipe keeps working but may hit E-2 when the library reads the name through its
declaration with a different result; read `DotNetVars.packages` instead. Add
`varschema DotNetVars.all` (D-4a): the library variables show in `--help` and the banner, and their
env values are judged correctly by the check. `ctx.Vars` users switch to `ctx.Variables.TryGet`;
`getVar`/`getEnv` of a secret switch to the secret's declaration.

## 9. Conformance tests

Each line: given / when / then, with the rules it covers.

1. Given `-d X=a` and `Var.string "X"`; when one recipe binds it and another calls `getVar "X"`; then both get `a` (G-1, D-7).
2. Given `-d X=a` and `var "X" "b"`; then `a (cli)` (M-5).
3. Given `var "X" "b"` and env `X=e`, `Var.string "X"`; then `b (script)` (M-5).
4. Given env `X=e`, `Var.string "X" |> withDefault "d"`; then `e (env)`; without env, `d (default)` (M-5).
5. Given env `X=e`, `Var.arg "X"`; then `None` (M-10).
6. Given `-d X=v`, `Var.env "X"`; then `None` and W-1 once; with `Var.secret "X"` the run fails with E-3 and `v` is masked (M-9).
7. Given `-d X=1 -d X=2`; then `2` (M-7).
8. Given `ExecOptions.Vars = [X,a]`, `var "X" "b"`, `var "X" "c"`; then `c (script)` (M-7).
9. Given `-d X=`; then `Some "" (cli)`, recorded `Var (X, Some "", false)` (M-6, DEP-1).
10. Given X undefined and `getVar "X"`; then `Var (X, None, false)`; next run with `-d X=a` rebuilds with C-1 `from <unset> to 'a'` (DEP-5 a).
11. Given a target built with `-d X=a`; when rerun with `var "X" "a"` and no `-d`; then up to date (G-2, DEP-2).
12. Given a target built with `-d X=a`; when rerun with `-d X=b`; then rebuilt, C-1 (DEP-5 a).
13. Given `Var.int "N"` read with `-d N=04`; when rerun with `-d N=4`; then up to date, recorded `Some "4"` (D-5, DEP-1).
14. Given `Var.string "S" |> secretHashed` from env; then `SecretDep (S, SecretHmac _)`, value never in the db; a changed value rebuilds with the masked reason (DEP-4).
15. Given a 0.6 database with `EnvVar (X, Some e)` and env unchanged; then up to date, no migration; env changed → rebuilt (DEP-6).
16. Given a 0.6 database with `Var (X, Some a)`; then it loads as `Var (X, Some a, false)` and `-d X=a` keeps it up to date (DEP-6).
17. Given a 0.6 database with `SecretDep (S, SecretEnv, d)` and an unchanged value; then up to date by digest only (DEP-6).
18. Given `Var.string "X" |> withDefault "a"` in `varschema` and `... withDefault "b"` read by a recipe; then E-1 naming both (D-6).
19. Given `getVar "X"` read first (from `-d X=a`), then a strong `Var.string "X" |> withDefault "d"`; then no error (D-7).
20. Given `getVar "X"` read first (→ `None`), then a strong declaration with default `d`; then E-2 (D-7).
21. Given nothing defines `NUGET_PACKAGES`; when a recipe binds `DotNetVars.packages`; then `.packages (library default)`, `Var (NUGET_PACKAGES, Some ".packages", true)` and L-1 once (D-1 b, D-2, M-3).
22. Given a target that read `DotNetVars.packages` defaulted in run 1, no `varschema`; when run 2 starts with nothing defining the name; then up to date by the weak declaration (DEP-5 b).
23. Given the same target and `varschema` with `NUGET_PACKAGES` declared with another default; then rebuilt (DEP-5 b, DEP rationale).
24. Given the same target and run 2 with `-d NUGET_PACKAGES=p`; then rebuilt (DEP-5 a).
25. Given env `NUGET_PACKAGES=/m` only; when a script recipe calls `getVar "NUGET_PACKAGES"` (→ `None`) before a library read (→ `/m`); then E-2 (D-7).
26. Given `HERMETIC=on` and `NUGET_PACKAGES` from env (resp. `-d NUGET_PACKAGES=`, resp. library default `.packages`, resp. `-d p`); then refused, refused, accepted, accepted (P-2).
27. Given `Var.secret "T"` in `varschema`; when a recipe calls `getVar "T"` or `getEnv "T"`; then E-7 (S-6).
28. Given `Var.secret "T"` in `varschema` and env `T=s`; then `s` is masked in the banner and every log line, L-1 and C-1 included (S-6).
29. Given `-d A=1`, `var "B" "2"`, env `C=3`, `varschema` declaring C (`ArgAndEnv`) and D (default 4); then the banner shows `A=1 (cli)`, `B=2 (script)`, `C=3 (env)`, `D=4 (default)` (P-3).
30. Given `-d UNUSED=1` that nothing reads; then W-2 at the end of the run; engine `?vars` never warn (D-2, S-7).
31. Given `consolelog Quiet` and `-ll Loud`; then `Loud`; `filelog "a.log" Diag` with `-fl b.log` gives `b.log` at `Diag`; `rootdir "x"` with `-r y` gives `y` (O-1, O-2).
32. Given `wantOverride ["t"]` and CLI target `u`; then `t`; `want ["t"]` with CLI `u` gives `u` (O-2).
33. Given an engine with `var "X" "s"`; when `Demand("t", ["X","r1"])`, concurrently `Demand("t2", ["X","r2"])` and a plain `Demand("t3")`; then `r1 (request)`, `r2 (request)`, `s (script)` (S-7).
34. Given `Var.create<T>(format = f, parse = p) |> withDefault x` with `p (f x) <> x`; then E-5 (D-5).
35. Given `Var.string "R" |> required` in `varschema` and R undefined; then E-6 before the banner; the same declaration only read in a recipe fails that target (S-3, M-11).
36. Given `-d CSC_SERVER=30` (resp. `off`, resp. `bogus`, resp. env `XAKE_CSC_SERVER=no`, resp. nothing); then keepalive 30 (resp. in-process, resp. parse error, resp. in-process, resp. server) (D-9).
37. Given env `CI=True` (resp. `-d CI=0`, resp. `-d CI=` , resp. `var "CI" "yes"`); then on (resp. off, off, on) (D-10).
38. Given env `NUGET_FETCH=off` and no `-d`; then fetching is off (D-8).
39. Given `getEnv "PATH"` in a recipe; then `EnvVar (PATH, _)` is recorded and no variable is registered (N-4, DEP-2).

## 10. Gaps filled, open questions

Gaps filled here (owner to confirm or change):
1. D-6: `TypeName` is part of declaration equality (`Var.int "N"` vs `Var.string "N"` conflict).
2. O-2: per-field CLI overlay for `filelog`; booleans by OR.
3. P-3: the banner shows undeclared `-d`/`var` names without evaluating them, so it never triggers D-7.
4. DEP-5 b with a registered declaration compares its default with the recorded value, so a changed default rebuilds.
5. S-6: a secret read via `getVar`/`getEnv` before its declaration is known is not detected; secrecy moves from the process-wide registry to the run's store.
6. D-9: `CSC_SERVER` values `on`/`off`/seconds and `XAKE_CSC_SERVER` switch kept as two declarations (3.13 semantics), the first `ArgOnly` and the second `EnvOnly` as in 3.13; an invalid `CSC_SERVER` is a parse error.

Decided by the owner since rev. 1 (for the record): M-2, M-3 (plain `Owner`), no registration at
task construction, the `defaulted` flag and database 0.7, `SecretDep` without kind, secrets only via
declarations (former Q-4), `NUGET_PACKAGES` default `.packages` and the simpler P-2, all library
variables `ArgAndEnv` (former Q-3), `CI` lenient (former Q-1), no warning for unused `?vars` (former Q-2).

Open questions:
- **Q-5** A library value taken from the environment, in a script without `varschema`, is judged by
  the weak (`ArgOnly`) declaration at the next check and rebuilds on every run (DEP-5 note).
  Options: (a) accept it; `varschema DotNetVars.all` becomes the documented requirement for env-fed
  library variables; (b) judge an undeclared name at check time by `ArgAndEnv` with `EnvName = Name`
  (matches every library declaration of D-8 except the D-9 pair, but a plain `getVar "X"` recorded
  `None` would then rebuild on every run while env `X` is set); (c) the library exposes its
  declarations to the check (contradicts N-1).
