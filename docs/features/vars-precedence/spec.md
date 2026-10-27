# Spec: variable evaluation

Status: draft for the owner's approval, 2026-10-28. Formalizes the owner's decisions P1-P11 of
2026-10-28 (not to be reopened) and supersedes the "Options" of [analysis.md](analysis.md); the
analysis stays the record of 3.13 behaviour (file:line references there are against origin/dev
92ef634; the banner and `setup` of #101, 4ef149d, are taken into account here). Where the decisions
left a gap, the rule is marked **[gap]** and listed in section 10 with the alternative not taken.
Keywords MUST, MUST NOT, SHOULD, MAY are normative. Every rule has an id (`M-3`, `S-5`, ...) that the
conformance tests in section 9 reference. Per the owner's rule, no code before this spec is approved
and the tests of section 9 exist.

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
- **N-1** No auto-discovery of declarations (no reflection over loaded assemblies): a library
  declaration is known to the store only once a reader or `varschema` hands it over (D-1).
- **N-2** No environment lookup for names nobody declared: `getVar "X"` alone never consults the
  environment (the weak declaration is `ArgOnly`, S-4).
- **N-3** No source in dependencies: the database never records which layer a value came from.
- **N-4** `getEnv` is unchanged: it reads the process environment (live, not the snapshot) and
  records `EnvVar`; it is not a variable read.

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

**M-2 [gap].** `Name` is the explicit `name`, else `cliArg`, else the `varschema` field name (3.13
had `Name` and `CliArgName`; the CLI name was the effective key and stays so). `EnvName` defaults to
the 3.13 derivation `toEnvName Name` (camelCase → UPPER_SNAKE), which is `Name` itself for an
UPPER_SNAKE name; `envName "Y"` (new combinator) sets it explicitly.

**M-3 [gap].** `Owner` is `Script` unless the declaration is built with `inLibrary "<package>"`
(new combinator, used by library declaration modules such as `DotNetVars`). It only selects the
source label of the default: `Default` for `Script`, `LibraryDefault` for `Library`.

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
- an environment variable defines X iff it is present in the snapshot; an empty value set in the
  environment defines X as `""` on Unix; on Windows an empty variable cannot exist (unset);
- a declaration without `withDefault` does not define.
The evaluator MUST NOT trim or interpret values; trimming and parsing are the reader's (`Canonical`).

**M-7 (duplicates).** Within `Cli` the last `-d X=` on the command line wins. Within `Script` the
last write wins, where `ExecOptions.Vars` entries come first and `var` ops follow in script order
(so `var` overrides an initial `ExecOptions.Vars` entry). Within `Request` the last pair wins.

**M-8.** `var "X" v` (Script, layer 3) and `withDefault v'` on a declaration of X (layer 5/6) are
different layers and MAY coexist; Script wins. The error "two defaults" exists only between two
strong declarations of X with different `Default` (D-6).

**M-9 (EnvOnly).** For `Scope = EnvOnly`, `Request`, `Cli` and `Script` are not sources. An entry
for the name (or for `EnvName`) in one of them:
- for a secret declaration MUST fail the run (exit 2, message E-3), as in 3.13;
- otherwise MUST be ignored with warning W-1, once per run per name.

**M-10 (ArgOnly).** For `Scope = ArgOnly`, `Env` is never consulted, even if the snapshot has
`EnvName`.

**M-11.** A typed reader converts the winning string with `Parse`; a parse failure fails the reader
(the 3.13 messages of `Vars.fs:36-52`, the value omitted for a secret). A `RequiredVar` without a
default whose evaluation is `None` fails with E-6.

### 2.3 Worked examples

`NUGET_PACKAGES` declared by `DotNetVars.packages` (`ArgAndEnv`, env `NUGET_PACKAGES`, library
default `~/.nuget/packages`); `build.fsc.fsx` has `var "NUGET_PACKAGES" ".packages"`.

| `-d` | `var` | env | value | source |
|---|---|---|---|---|
| `/ci/pkgs` | `.packages` | `/m/pkgs` | `/ci/pkgs` | cli |
| `/ci/pkgs` | (none) | (none) | `/ci/pkgs` | cli |
| (none) | `.packages` | `/m/pkgs` | `.packages` | script |
| (none) | (none) | `/m/pkgs` | `/m/pkgs` | env |
| (none) | (none) | (none) | `~/.nuget/packages` | library default |
| `NUGET_PACKAGES=` | `.packages` | `/m/pkgs` | `""` | cli |

The last row: `-d X=` defines X as empty, so neither `var` nor the environment is consulted. The
library reader treats a blank value as unset and uses the machine folder (3.13 behaviour of
`packageRoot`), so `HERMETIC=on` refuses it (P-2). `-d NUGET_PACKAGES=a -d NUGET_PACKAGES=b` yields
`b` (M-7).

`-d AAA=hello`, a recipe reads `let! a = Var.string "AAA" |> withDefault "x"` → `hello (cli)`;
without `-d` and with `AAA` absent from the environment → `x (default)`; with `AAA=env` in the
environment → `env (env)`. `getVar "AAA"` in another recipe of the same run returns the same value,
because the strong declaration was registered first (otherwise D-7 applies).

Secret: `token = Var.secret "API_TOKEN"` (`EnvOnly`, `SecretPresence`). Environment `API_TOKEN=s3`
→ `s3 (env)`, masked everywhere, recorded as a digest (DEP-4). `-d API_TOKEN=s3` → run fails with
E-3 before the banner; the value is registered for masking first.

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
(`ExecOptions.Vars` then `var` ops), the declarations of `varschema`, and an **environment snapshot
taken once** at construction. The store MUST NOT read the live environment afterwards.
`ExecOptions.Vars` stays as the script-values input (and only that: the CLI list is kept apart, O-1).

**S-3 (at `Run`, before the banner).** In this order: register the `varschema` declarations (D-1 a,
conflicts D-6); evaluate each of them, registering secret values for masking (S-6); fail on E-3
(EnvOnly secret given by `-d`/`var`) and on E-6 for a `Required` schema declaration without value;
print `--help` (which exits) or the banner (P-3). Names first declared later evaluate lazily.

**S-4 (weak declaration).** `TryGet name` with no registered declaration evaluates the implicit
weak declaration `{ Name = name; Scope = ArgOnly; Default = None; Secret = Secrets.modeOf name;
TypeName = "string"; Canonical = id; Owner = Script }`. It does not register a strong declaration.

**S-5 (memo, P4).** The store memoizes per name for one run (the lifetime of the `getChangeReasons`
memo and of `Scheduler.newRun`; in engine mode, the engine's lifetime, S-7): the result, `None`
included, is computed once with `ConcurrentDictionary.GetOrAdd`; the declaration of a name is fixed
atomically at its first strong registration (`GetOrAdd` on the declaration table), later
registrations are compared against it (D-6). Evaluation is pure over S-2 inputs, so a race that
evaluates twice yields equal results.

**S-6 (secrets).** A value whose declaration is secret MUST be registered with `SecretMask` on its
first evaluation, before it is returned to anyone; for schema declarations that is before the
banner. `Secrets.declare` is called for `Name` and (unless `ArgOnly`) `EnvName` at registration.

**S-7 (engine mode).** `XakeEngine` builds one base store at `Start`. `Demand(target, ?vars)` uses
`base.WithRequest vars`: an overlay whose `Request` layer is `vars`, with its own memo for the names
`vars` defines and delegating every other name (and every registration) to the base. Two demands with
different `?vars` MUST NOT see each other's request values; a declaration registered through an
overlay is registered in the base. `StopAsync` (teardown) uses the base store.

**S-8 (dependency checks).** `DependencyAnalysis` MUST consult the run's store (S-5), never a list
(DEP-5).

## 4. Declarations

### 4.1 Entry paths (P5)

**D-1.** A strong declaration enters the store in one of three ways:
- (a) `varschema` at `Run` (S-3). `varschema` accepts the 3.13 record form and, new, a
  `VarDecl list` (so `varschema DotNetVars.all` works);
- (b) at the point of use: a recipe binds a typed declaration (`let! p = DotNetVars.packages`) or
  calls `ctx.Variables.Evaluate decl`. The declaration is an ordinary F# value carried by the reader;
- (c) implicitly, as a *weak* read: `getVar "X"` (S-4). A weak read never registers a strong
  declaration.

**D-2 (no discovery, N-1).** The banner lists what is declared at `Run` (D-1 a) plus the undeclared
names defined by `-d`/`var` (P-3). A name whose first strong declaration arrives later is logged
once at `Info` on its first evaluation, text L-1.

**D-3 (library reads).** A library MUST read its variables through its own declarations; a script
reads them through the exposed values (`let! p = DotNetVars.packages`) or puts them in the banner and
`--help` with `varschema DotNetVars.all`. A library MUST NOT call `getVar` for a name it declares.

### 4.2 Typed defaults (P8)

**D-4.** `withDefault x` stores `x` and its string form `Format x` in `Default`. `Format` defaults to
`string x` (`ToString`, invariant culture); `Var.create` gains `?parse` and `?format` (default parse:
3.13 `Convert.ChangeType`, invariant culture).

**D-5.** When a declaration with a default is registered, the store MUST check
`Parse (Format x) = x`; a violation fails the run (startup for schema, first evaluation otherwise)
with E-5. `Canonical v = Format (Parse v)`.

### 4.3 Conflicts (P6)

**D-6.** Two strong declarations of one name are *equal* iff they have the same `Scope`, `EnvName`
(when not `ArgOnly`), `Default`, `Secret` and **[gap]** `TypeName`. `Description`, `Required` and
`Owner` do not take part (the first registered `Owner` labels the default). A registration that is
not equal to the fixed one fails with E-1, naming both, at `Run` for `varschema`, at the
registration otherwise. Two equal declarations are one.

**D-7 (read before declaration).** A weak read of X (D-1 c) while X has no strong declaration
marks X "read before its declaration" and memoizes the weak value. When a strong declaration of X
arrives, the store evaluates X by it and compares with the value already returned:
equal (string, source ignored) → the strong declaration is fixed, no message; different → E-2. No
name ever has two values in one run, in any order of reads. Reads by the banner, `--help` and the
dependency check (DEP-5) do not mark.

### 4.4 Library declarations to migrate

Rule **D-8 [gap]**: each library variable keeps exactly its 3.13 sources (scope and env name); widening
a scope is a separate decision (Q-3). Parsing moves into the declaration so that every source is
parsed alike (G-1).

| Module.value | Name | Scope | EnvName | Default (owner) | Notes |
|---|---|---|---|---|---|
| `DotNetVars.packages` | `NUGET_PACKAGES` | ArgAndEnv | `NUGET_PACKAGES` | `~/.nuget/packages` (lib) | relative → under the project root; `~/` → user profile; reader's job |
| `DotNetVars.fetch` | `NUGET_FETCH` | ArgOnly | - | `on` (lib) | off/false/no/0 = off |
| `DotNetVars.netfx` | `NETFX` | ArgOnly | - | none | |
| `DotNetVars.netfxTarget` | `NETFX-TARGET` | ArgOnly | - | none | also read by Xake.Hermetic.Dotnet (`Described.fs:626`) |
| `DotNetVars.cscServer` | `CSC_SERVER` | ArgAndEnv | `XAKE_CSC_SERVER` | `on` (lib) | custom type, D-9 |
| `DotNetVars.cscToolset` | `CSC_TOOLSET` | ArgOnly | - | none | |
| `DotNetVars.fsharpCoreVersion` | `FSHARP_CORE_VERSION` | ArgOnly | - | none | |
| `DotNetVars.fscVer` | `FSCVER` | ArgOnly | - | none | |
| `DotNetVars.netstandardLibraryVersion` | `NETSTANDARD_LIBRARY_VERSION` | ArgOnly | - | none | built-in pick stays in the reader |
| `DotNetVars.netstandardLibraryRefVersion` | `NETSTANDARD_LIBRARY_REF_VERSION` | ArgOnly | - | none | |
| `DotNetVars.referenceAssembliesVersion` | `NETFX_REFERENCE_ASSEMBLIES_VERSION` | ArgOnly | - | none | |
| `DotNetVars.netcoreRefVersion` | `NETCORE_REF_VERSION` | ArgOnly | - | none | |
| `DotNetVars.hermetic` | `HERMETIC` | ArgOnly | - | `off` (lib) | lives in Xake.Dotnet (`HermeticMode.fs:75`), not in the hermetic package **[gap]** |
| `HermeticVars.ci` | `CI` | ArgAndEnv | `CI` | none | parse: Q-1 |
| `HermeticVars.lockRefresh` | `LOCK_REFRESH` | ArgAndEnv | `LOCK_REFRESH` | none | blank = unset (reader) |
| `HermeticVars.lockStale` | `LOCK_STALE` | ArgOnly | - | `fail` (lib) | fail/warn |

`DotNetVars.all` / `HermeticVars.all : VarDecl list` list every row of the module. Sketch:

```fsharp
module DotNetVars =
    let packages =
        Var.string "NUGET_PACKAGES" |> withDefault "~/.nuget/packages" |> inLibrary "Xake"
        |> describe "Package folder; relative to the project root"
    let cscServer =
        Var.create<CompilerServerSetting>("CSC_SERVER", parse = CscServerSetting.parse, format = CscServerSetting.format)
        |> envName "XAKE_CSC_SERVER" |> withDefault CscServerSetting.On |> inLibrary "Xake"
    // ...
    let all : VarDecl list = [ packages.Decl; fetch.Decl; cscServer.Decl; (* ... *) ]
```

`DotNetFwk.packageRoot` reads `let! p = DotNetVars.packages` and resolves it; `packageRootWithSource`
returns the store's `VarSource` instead of `PackageRootSource` (P-1).

**D-9 [gap].** `CSC_SERVER` and `XAKE_CSC_SERVER` had different parsers (3.13: `1` was a keepalive
of 1 s from `-d`, "on" from the env). One parser for both: `on|true|yes` → server; `off|false|no|0`
→ in-process; a positive integer → keepalive seconds; anything else → warning W-4 and server. A
target's own `noserver`/`keepalive` still wins over the variable (it is not a variable source).

## 5. Dependencies and database (P7)

**DEP-1.** Every read from inside a recipe (`getVar`, a bound `Var.*`, a library read through its
declaration, `ctx.Variables` through the recipe-level helpers) MUST record one dependency:
`Var (name, value)` where `value : string option` is `None` when undefined and otherwise the
**canonical** form `Canonical v` (D-5; `v` itself for weak reads). Two reads of one name in one
recipe record one dependency.

**DEP-2.** The source MUST NOT be recorded. The evaluator MUST NOT write `EnvVar`; `getEnv` keeps
writing `EnvVar` (N-4).

**DEP-3.** Reads outside recipes (banner, `--help`, S-3 checks, the dependency check itself) MUST
NOT record anything.

**DEP-4 (secrets).** A read of a secret records `SecretDep (name, SecretArg, digest)` with the 3.13
digest rules (`SecretPresence`: set/unset; `SecretHash`: salted HMAC), never the value.

**DEP-5 (check).** At the next run `Var (name, recorded)` is up to date iff
`store.TryGet name |> Option.map (fst >> canonical)` equals `recorded`, where `TryGet` uses the
registered declaration of the name, else the persisted declaration (DEP-7), else the weak one;
otherwise the change reason is C-1. `SecretDep (name, SecretArg, d)` is checked against the same
store value through `Secrets.changeReason`.

**DEP-6 (database, compatibility).** The `Var` case already carries `string * string option` in
the 0.6 pickle (`Database.fs:92`); its shape does not change. The database version becomes **0.7**
(for DEP-7). Records of 0.4-0.6 load as is: an old `EnvVar (n, v)` (including those 3.13 `Var.*`
wrote for env-sourced values) is still checked against the current environment, so an unchanged
environment does not rebuild and no migration is needed; an old `SecretDep (n, SecretEnv, d)` is
still checked against the environment. The first rebuild of such a target writes the new form.

**DEP-7 [gap] (persisted declarations).** A library declaration is registered only when a recipe
reads it (D-1 b), but the dependency check of the next run happens before any recipe runs. With
only the weak declaration, `Var ("NUGET_PACKAGES", Some "~/.nuget/packages")` would be judged against
`None` and rebuild on every run. Therefore the database MUST keep, per name, the last strong
declaration seen in a run (`Scope`, `EnvName`, `Default`, `Secret`, `TypeName`, `Owner`; not the
parser: for a typed name not yet registered, the check compares the raw string, so a respelled
equal value, `04` for `4`, rebuilds once); the store uses it for checks of a
name not yet declared in this run. Such a check-time evaluation MUST NOT register a declaration nor
mark D-7, and is memoized apart from the run's values. A declaration changed in the script between
runs is seen by the check only once it is registered (the build script is not a dependency, as
today).

## 6. Options, policies, banner

### 6.1 Options precedence (P9)

**O-1.** `xakeArgs` MUST keep the options parsed from the command line apart from the initial
`ExecOptions` and apply them in `Run` **over** the result of the script ops (and over
`ExecOptions` fields), field by field. `IgnoreCommandLine = true` keeps skipping the CLI. `-nologo`
and `--help` keep acting at parse time.

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

**P-2.** `HERMETIC=on` MUST refuse a package folder whose `NUGET_PACKAGES` source is `Env`,
`LibraryDefault` or `Default`, or whose value is blank (the reader falls back to the machine
folder), with the 3.13 message of `HermeticMode.fs:155`; it accepts `Request`, `Cli` and `Script`
with a non-blank value. The source is a property of the run, not recorded (DEP-2); the policy's
dependency is the `Var` of `NUGET_PACKAGES` plus `HERMETIC`.

**P-3 (banner).** The banner's vars section prints, sorted by name, one line per name declared at
`Run` and per name defined by `-d` or `var` (undeclared ones with the value of their highest
defining layer, not evaluated, D-7):
`    NAME=value (source)` with `source` ∈ `request | cli | script | env | default | library default`;
a secret's value as `***`; a declared name without value as `    NAME (not set)`. The label
`(script/cli)` of 3.13 goes away.

## 7. Diagnostics (exact texts)

Errors fail the run with exit 2 (`XakeException` under `ThrowOnError`); at `Run` they are printed
before the banner; at first evaluation they fail the reading target.

- **E-1** `Variable 'X' is declared twice with different settings: <a> and <b>; declare it once (or identically)`
  where `<a>`/`<b>` render as `scope=ArgAndEnv env=X default=v secret=no type=string (<owner>)`.
- **E-2** `Variable 'X' was read before its declaration (getVar gave 'a' (cli), the declaration gives 'b' (env)); read it through <decl> or declare it in varschema`
  where `<decl>` is `Name`'s declaring value if known (e.g. `DotNetVars.packages`), else `its declaration`.
  Secret values are `***`.
- **E-3** (3.13 text kept) `Variable 'X' is secret: secret variables come from the environment, set ENV instead of passing -d X=...`
- **E-5** `Variable 'X': the default 'v' does not survive format/parse (got 'w'); fix the declaration's format`.
- **E-6** (3.13 text kept) `Required variable 'X' is not provided`.
- **W-1** `Variable 'X' is read from the environment only (ENV); its -d/var value is ignored`.
- **W-2** (end of a run in `Run` mode) `-d X was not used by the build` (one line per name; not in
  dry-run/dump; a name read only by the banner counts as unused).
- **W-3** (3.13 text kept) the warning for an `ArgAndEnv` secret given by `-d`.
- **W-4** `CSC_SERVER='v' is not 'on', 'off' or a number of seconds; using the compiler server`.
- **L-1** (`Info`, once per name) `NAME=value (source)`, value masked for a secret, e.g.
  `NUGET_PACKAGES=.packages (script)`.
- **C-1** (change reason) `Variable X changed from 'a' to 'b'`; `<unset>` for `None`; for a
  secret the 3.13 `Secret variable X is now set|is now unset|changed`. Replaces
  `Global script variable %s was changed '%A'->'%A'` (`DependencyAnalysis.fs:101`).

## 8. Compatibility and versioning (P11)

**V-1.** Xake **3.14**, breaking: `ExecContext.Vars` removed (`ExecContext.Variables : VarStore`);
`VarStore`, `VarSource`, `VarDecl`, `envName`, `inLibrary`, `Var.create ?parse ?format`,
`varschema` over `VarDecl list` added; CLI over ops for options (O-2); database 0.7 (read 0.4+).
`Util.getVar` (internal) goes. Xake.Dotnet (in the Xake package) reads through `DotNetVars`.

**V-2.** Xake.Hermetic.Dotnet 0.9 references Xake `[3.14, 3.15)` and reads `CI`, `LOCK_REFRESH`,
`LOCK_STALE` through `HermeticVars`, `NETFX-TARGET` and `HERMETIC` through `DotNetVars`.

**V-3 (release note, observable changes).**
- A script that pins a value with `var` now yields to `-d` (and to engine-mode `?vars`).
- `-d X=1 -d X=2` gives `2` (was `1`); `ExecOptions.Vars` no longer beats `-d`.
- `consolelog`, `filelog`, `rootdir` yield to `-ll`, `-fl`/`-fll`, `-r`.
- `varschema ... withDefault` defaults are now values seen by `getVar` (once declared).
- `Var.env`/`Var.secret` and env-sourced `Var.*` values are recorded as `Var` (canonical), not
  `EnvVar`; a value read from the environment is still a dependency, only the record differs.
- `Var.env` given by `-d` warns (W-1) instead of silently ignoring.
- The banner shows the real source; unused `-d` names are reported (W-2).
- `XAKE_CSC_SERVER=<n>` now means a keepalive of n seconds (D-9).
- One rebuild after upgrading is possible for targets whose recorded `Var` came from a 3.13 reader
  that recorded `None` and an `EnvVar` beside it (e.g. `Var.string` falling back to the
  environment): the new evaluator sees the env value under the same name.

**V-4 (migration for scripts).** `var` stays the way to set a value in the script; to make it a
default the CLI cannot see, nothing is needed (it already yields). `getVar "NUGET_PACKAGES"` in a
script recipe keeps working but may hit E-2 when the library reads the name through its declaration
with a different result (e.g. the value comes from the environment only); read
`DotNetVars.packages` instead, or add `varschema DotNetVars.all`. `ctx.Vars` users switch to
`ctx.Variables.TryGet`.

## 9. Conformance tests

Each line: given / when / then, with the rules it covers.

1. Given `-d X=a` and a declaration `Var.string "X"`; when one recipe binds it and another calls `getVar "X"`; then both get `a` (G-1, D-7).
2. Given `-d X=a` and `var "X" "b"`; when read; then `a (cli)` (M-5, V-3).
3. Given `var "X" "b"` and env `X=e`, `Var.string "X"`; then `b (script)` (M-5).
4. Given env `X=e`, `Var.string "X" |> withDefault "d"`; then `e (env)`; without env, `d (default)` (M-5).
5. Given env `X=e`, `Var.arg "X"`; then `None` (M-10).
6. Given `-d X=v`, `Var.env "X"`; then `None` and W-1 once; with `Var.secret "X"` the run fails with E-3 and `v` is masked (M-9).
7. Given `-d X=1 -d X=2`; then `2` (M-7).
8. Given `ExecOptions.Vars = [X,a]` and `var "X" "b"` then `var "X" "c"`; then `c (script)` (M-7).
9. Given `-d X=`; then `Some "" (cli)` and the recorded dependency is `Var (X, Some "")` (M-6, DEP-1).
10. Given X undefined, a target reads `getVar "X"`; then `Var (X, None)` is recorded; next run with `-d X=a` rebuilds it with C-1 `from <unset> to 'a'` (DEP-1, DEP-5).
11. Given a target built with `-d X=a`; when rerun with `var "X" "a"` and no `-d`; then up to date (G-2, DEP-2).
12. Given a target built with `-d X=a`; when rerun with `-d X=b`; then rebuilt, reason C-1 (DEP-5).
13. Given `Var.int "N"` read with `-d N=04`; when rerun with `-d N=4`; then up to date, recorded `Some "4"` (D-5, DEP-1).
14. Given `Var.string "S" |> secretHashed` from env; then `SecretDep (S, SecretArg, SecretHmac _)` recorded, value never in the db; changed value rebuilds with the masked reason (DEP-4).
15. Given a 0.6 database with `EnvVar (X, Some e)` for a target and env unchanged; when 3.14 runs; then up to date, no migration; with env changed, rebuilt (DEP-6).
16. Given `Var.string "X" |> withDefault "a"` in `varschema` and `Var.string "X" |> withDefault "b"` read by a recipe; then E-1 naming both (D-6).
17. Given `getVar "X"` read first (from `-d X=a`), then a strong `Var.string "X" |> withDefault "d"`; then no error (equal values) (D-7).
18. Given `getVar "X"` read first (undefined → `None`), then a strong declaration with default `d`; then E-2 (D-7).
19. Given no `varschema`, no `-d`/`var`/env for `NUGET_PACKAGES`; when a recipe binds `DotNetVars.packages`; then `~/.nuget/packages (library default)` and L-1 logged once (D-1 b, D-2, M-3).
20. Given env `NUGET_PACKAGES=/m` only; when a script recipe calls `getVar "NUGET_PACKAGES"` (→ `None`) before a library compile reads `DotNetVars.packages` (→ `/m`); then E-2 (D-7, V-4).
21. Given `HERMETIC=on` and `NUGET_PACKAGES` from env (resp. library default, resp. `-d`); then refused (resp. refused, resp. accepted) (P-2).
22. Given `-d A=1`, `var "B" "2"`, env `C=3` with `varschema` declaring C (`ArgAndEnv`) and D (default 4); then the banner shows `A=1 (cli)`, `B=2 (script)`, `C=3 (env)`, `D=4 (default)` (P-3).
23. Given `-d UNUSED=1` that nothing reads; when the run ends; then W-2 (D-2).
24. Given a library declaration first evaluated after the banner; then L-1 once, with a secret masked (D-2, S-6).
25. Given `consolelog Quiet` and `-ll Loud`; then console level `Loud`; `filelog "a.log" Diag` with `-fl b.log` gives `b.log` at `Diag`; `rootdir "x"` with `-r y` gives `y` (O-1, O-2).
26. Given `wantOverride ["t"]` and CLI target `u`; then `t`; `want ["t"]` with CLI `u` gives `u` (O-2).
27. Given an engine with `var "X" "s"`; when `Demand("t", ["X","r1"])` and concurrently `Demand("t2", ["X","r2"])` and a plain `Demand("t3")`; then they see `r1 (request)`, `r2 (request)`, `s (script)` respectively (S-7).
28. Given `Var.create<T>(format = f, parse = p) |> withDefault x` with `p (f x) <> x`; then E-5 at registration (D-5).
29. Given `Var.string "R" |> required` in `varschema` and R undefined; then the run fails with E-6 before the banner; the same declaration only read in a recipe fails that target at the read (S-3, M-11).
30. Given a target that read `DotNetVars.packages` (library default) in run 1; when run 2 starts with no change; then the check uses the persisted declaration and the target is up to date (DEP-7).
31. Given `Var.secret "T"` in `varschema` and env `T=s`; then `s` is masked in the banner and every log line, including L-1 and C-1 (S-6).
32. Given `-d CSC_SERVER=30` (resp. env `XAKE_CSC_SERVER=off`, resp. nothing); then keepalive 30 (resp. in-process, resp. server) (D-9).
33. Given `getEnv "PATH"` in a recipe; then `EnvVar (PATH, _)` is recorded and no variable is registered (N-4, DEP-2).

## 10. Gaps filled, open questions

Gaps the decisions left open, filled here (owner to confirm or change):
1. M-2: the store key is `name` > `cliArg` > field name; default env name is the 3.13 UPPER_SNAKE derivation.
2. M-3: `Default` vs `LibraryDefault` is told by an explicit `inLibrary` owner on the declaration.
3. D-6: `TypeName` is part of declaration equality (`Var.int "N"` vs `Var.string "N"` conflict).
4. D-8: library variables keep their 3.13 scopes; `HERMETIC` belongs to `DotNetVars` (it lives in Xake.Dotnet).
5. D-9: one parser for `CSC_SERVER`/`XAKE_CSC_SERVER`.
6. DEP-6: no `Var` shape change is needed (already `string option` in 0.6); 0.7 is for DEP-7.
7. DEP-7: persisted declarations in the database, so library defaults do not rebuild every run.
8. O-2: per-field CLI overlay for `filelog`; booleans by OR.
9. P-2: `HERMETIC=on` also refuses `Default`/`LibraryDefault` and a blank value (as 3.13 refused `UserProfile`).
10. P-3: the banner shows undeclared `-d`/`var` names without evaluating them, so it never triggers D-7.

Open questions:
- **Q-1** `CI` parsing: 3.13 was strict on/off for the script variable but "set and not 0/false =
  on" for the environment. One parser is needed (G-1). Proposal: on/true/yes/1 → on, off/false/no/0/""
  → off, anything else → on with a warning (CI services set `true`, some set other strings).
- **Q-2** Should W-2 (unused `-d`) also run in engine mode for `?vars`? Proposal: no.
- **Q-3** (analysis.md Q3, still open) env fallback for `NUGET_FETCH`, `NETFX`, `HERMETIC`: D-8 keeps
  them `ArgOnly` until decided.
