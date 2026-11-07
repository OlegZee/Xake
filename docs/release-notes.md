# Release notes: `Xake`

The `Xake` package: the engine (up to 3.15 it also carried `Xake.Dotnet`). From 4.0
`Xake.Dotnet` and the hermetic library are developed and released in
[OlegZee/Xake.Dotnet](https://github.com/OlegZee/Xake.Dotnet), with their own notes; the entries below about them up to
4.0 are the history. Links to their docs point to that repository.

## 4.0.0 (2026-11-03)

### Summary

Xake 4.0 is a breaking release. What it is:

- **One grammar for rule patterns and file masks** (the pattern module `Xake.PatternAst`, spec:
  `docs/features/pattern-module/spec.md`): `( ) [ ] { } |` are reserved, `{a,b}` is an alternation,
  `\` escapes, `dir/**` means every file below `dir`, and what 3.x read silently in an unintended
  way is an error with a numbered `XPnnn` code.
- **Variables are declared and evaluated once, at launch** (`var`, `varschema`, secrets masked in
  every log): a `Var` read needs a declaration, the environment source of a declaration is
  `XAKE__<NAME>`, libraries read only the variable set.
- **The `Xake` package is the engine alone.** `Xake.Dotnet` (`csc`, `fsc`, `msbuild`, `resgen`) and
  the hermetic library ship together as the package `Xake.Dotnet` 4.0.0 from
  [OlegZee/Xake.Dotnet](https://github.com/OlegZee/Xake.Dotnet); `Xake.Hermetic.Dotnet` ends at 0.10.0.
- **New:** typed targets (`Xake.Typed`), `Command`, `ExecOptions.IgnoreEnvironment`, a public seam
  for task libraries (`hasRule`, `ProcessExec`).

Who must act:

| You | Action |
|---|---|
| a script with `open Xake.Dotnet` or `Xake.Hermetic.Dotnet` | add `#r "nuget: Xake.Dotnet, 4.0.0"` |
| a script with `(`, `)`, `[`, `{`, `|` in a rule or mask | read the `XPnnn` errors at load; escape or respell |
| a script that lists, excludes or copies with `dir/**`, `**/` or `-- "dir/"` | check the result: the meaning changed with no error |
| a script with `Var.*` reads, `var` redefinitions, or `-d` for secrets | declare the keys, define a name once, pass secrets as `XAKE__<NAME>` |
| a CI that exported `NUGET_PACKAGES`, `XAKE_CSC_*`, `XAKE_FSC_JOBS`, `CI`, or a plain-named variable for a field | declare it with `Env "NAME"` or rename the variable to `XAKE__NAME` |
| code that builds `ExecOptions`/`EngineOptions`, or calls `Path.matchesPattern`, `Fileset.toFileList1` | use `with`, `ScriptVars`, `Path.matches`, `toFileList` |

Upgrade path: run the build on 4.0, fix every `XPnnn` error and launch error (they stop the run
before any target), then review the warnings in `build.log`; the checklist is in
[Migration checklist](#migration-checklist).

The sections below, in order: breaking changes (with before and after), new features, the
migration checklist, fixes and smaller changes, an announcement; then the full reference of each
area (patterns, typed targets, variables, library readers, the public seam).

### Breaking changes

#### Patterns

Rule patterns (`..>`, `*..>`, `=>`, `<==`, `<<<`, `target`, `targets`, `command`) and masks (`!!`,
`ls`, `++`, `--`, `fileset { }`, `includefile`/`excludefile`) follow one grammar. An invalid
pattern raises `PatternException` when the rule or fileset is created, usually as the script
loads:

```text
Invalid pattern 'out/a(1).txt' (rule) at character 6: '(' is reserved (Xake 3.x: a regex group); write \( for a literal '(' [XP003]
```

"Rule" and "mask" below name where the 3.x meaning applied. "Silent" means no error: the result
changes, only a warning at evidence (`XP1nn`, in the build log, once per pattern) tells.

| Pattern | 3.x | 4.0 | Code |
|---|---|---|---|
| `out/(ext:dll\|exe)` (rule) | regex alternation | error; write `out/(ext:{dll,exe})` | XP003 |
| `out/a(1).txt` (rule) | regex group, matched `out/a1.txt` | error; write `@"out/a\(1\).txt"` | XP003 |
| `src/a(1)/*.cs` (mask) | literal `(1)` | error; write `\(1\)`, or `PatternAst.escapePath` for a real path | XP003 |
| `src/{a,b}.cs` | literal text `{a,b}` | alternation: `src/a.cs`, `src/b.cs`; `\{a,b\}` for the literal | XP104 on evidence (mask) |
| `{x}` (no comma) | literal text | error; `\{x\}` or `{a,b}` | XP005 |
| `[abc]`, `[a-z]`, `[^a]` in a mask | literal text | a character class | XP104 on evidence |
| `[c]` in a rule | the character `c` | the character `c`, deprecated (write `c` or `\c`) | XP105 |
| `sub[1]/*.c` in an include | literal `sub[1]` | `sub1` | XP106 |
| `[!a]` in a rule or a wildcard exclude segment | class of `!` and `a` | negated class (`[^a]` has the same meaning in both) | XP117 |
| `[a`, `]`, `}`, `{` unbalanced | literal in masks, crash in rules | error | XP002, XP004 |
| `[a/b]`, `[z-a]`, `[[:alpha:]]` | split or regex meaning | error | XP010 |
| `**/*.dll` in a rule | one or more directories | zero or more (`a.dll` at the root matches); `*/**/*.dll` requires one | XP119 on evidence |
| `a**b` in a rule | crossed `/` | within one segment, like `a*b` | XP112 |
| `dir/**` in a mask | include: the directories below; exclude: nothing | every file below `dir`; `dir/**/` for the directories | XP113 on evidence |
| `dir/**/*` | every file below | unchanged | |
| `-- "obj/"` (trailing `/` in a mask) | the `/` was dropped, removed nothing | the directory `obj`: every file below it is removed and not entered | silent |
| `-- "obj"` | a file named `obj` | unchanged | |
| `!! "*.*"` | any name | names with a `.` | XP114 on evidence |
| `?` before a `.`, `*.rdl` on .NET Framework | `?` could match zero characters; `*.rdl` found `.rdlx` | the module decides every name: `?` is exactly one character | silent |
| `out/` (trailing `/` in a rule) | matched nothing | error | XP011 |
| `src/*/../x.cs`, `src/*/./x.cs` | `..` a parent step in masks, never matched in rules | error: `.`/`..` only before the first wildcard | XP012 |
| `src/(x:*)/*.cs` (named group in a mask) | literal text, or the body in an exclude | error | XP008 |
| `(1:*)` | accepted | error: a name must not start with a digit | XP007 |
| `(p:*)/(p:*)` | each captured, the last won | error | XP015 |
| `(name:` unclosed in a string rule | ran to the end of the pattern | error | XP006 |
| `*..>` outputs with different groups, or a class or braces outside a group | names like `out/net8.0/.xml` | error when the rule is created | XP013, XP014 |
| `""` | an empty mask matching the empty path | error | XP018 |
| `{a/,b}` ending a pattern | literal text | error; write `{a,b}/` | XP019 |
| `src\*.cs` | `\` is a separator | still a separator, deprecated; an escape from 5.0 | XP110 |
| `src\(x\)\*.cs` | `\` is a separator | `\` before `( ) [ ] { } \|` is an escape | |
| `\,` directly inside braces | separator | error; write `[,]` | XP017 |
| a literal `*`, `?` or `,` | | no `\` escape in 4.0: `[*]`, `[?]`, `[,]`, `[\\]` for a backslash | |
| `a//b` | `matchGroups` compared the raw text | one separator everywhere | silent |
| a case difference (`Src/a.cs` for `src/*.cs`) | `Path.matches` ignored case, `matchGroups` did not; a literal exclude segment ignored case everywhere; macOS wildcard includes ignored case | one comparer per OS: ordinal on Linux and macOS, ignore-case on Windows | silent |
| a scan result | file-system order, duplicates kept | ordinal order per directory (files before subdirectories), includes in declaration order, duplicates removed | silent |
| `**` over a directory symlink or junction | followed (a cycle ended at the OS limit) | not followed, logged once at level Message; a literal or one-segment pattern (`node_modules/x/**`, `*/`) still enters it | silent |
| an unreadable directory in a scan | `UnauthorizedAccessException` ended the build | skipped, one warning | silent |
| rule selection with `**` next to `/` | the `/` counted in specificity | it counts 0: `*/?.dll` now beats `**/x.dll` for `a/x.dll` | silent |

Which of these fail the script and which change a result:

- **Fail at load** (an error with the 4.0 spelling in the message): the rows with a code `XP0nn`.
- **Change a result without an error**: `dir/**`, a trailing `/` in an exclude, `*.*`, `**/` in a
  rule, `[abc]` and `{a,b}` in a mask, `[c]` in an include, `[!a]`, `a**b`, case, ordering, symlinks,
  specificity. Read the warnings in `build.log` and compare the file lists once.
- A fileset recorded by 3.x with a trailing `**` reads back differently, so its target reruns once.

The diagnostic codes a user meets:

| Code | Kind | Meaning |
|---|---|---|
| XP002, XP004 | error | unbalanced `[`, `{`, a stray `]` or `}`; `\[`, `\{` for a literal |
| XP003 | error | `(`, `)`, `\|` outside a named group; `(ext:dll\|exe)` is `(ext:{dll,exe})` |
| XP005 | error | `{x}` without a comma |
| XP006 | error | unclosed `(name:` |
| XP007, XP015 | error | a group name that starts with a digit; a name used twice |
| XP008 | error | a named group in a mask |
| XP010 | error | `/` in a class, a reversed range, `[[:alpha:]]` |
| XP011 | error | a rule pattern ending in `/` |
| XP012 | error | `.` or `..` after a wildcard |
| XP013, XP014 | error | multi-output rule: outputs with different groups; a class or braces outside a group in a pattern that is formatted (multi-output, typed targets) |
| XP016 | error | a UNC root (`//server/share`) with a wildcard server or share |
| XP017 | error | `\,` directly inside braces |
| XP018, XP019 | error | an empty pattern; `{a/,b}` ending the pattern |
| XP104 | warning | `[ab]` or `{a,b}` in a mask where an entry named like the 3.x literal exists |
| XP105, XP106 | warning | `[c]` is the character `c` (rule; include) |
| XP110 | warning | `\` used as a separator |
| XP112, XP113, XP114, XP117, XP119 | warning | the 3.x reading differs: `a**b`, `dir/**`, `*.*`, `[!a]`, `**/` that took zero directories |
| XP120 | warning | an absolute exclude outside the fileset that removes nothing |

The 3.x clauses in the reasons and the legacy warnings (XP104 to XP119) are to be removed in 5.0;
XP120 stays. The full table with the exact texts is section 7.2 and 7.3 of the pattern spec, the
guide is [overview.md](overview.md#patterns-masks-and-paths).

Before and after:

```fsharp
// 3.x                                           // 4.0
"out/(ext:dll|exe)" ..> recipe { ... }           "out/(ext:{dll,exe})" ..> recipe { ... }
"out/a(1).txt" ..> recipe { ... }                @"out/a\(1\).txt" ..> recipe { ... }
!! "src/a(1)/*.cs"                               !! (PatternAst.escapePath "src/a(1)/*.cs")
!! "bin/**"             // directories          !! "bin/**/"      // directories
!! "src/**/*.cs" -- "obj/"   // removed nothing  // now prunes obj; same text, different result
"**/*.dll" ..> ...      // needs a directory    "*/**/*.dll" ..> ...   // needs a directory
```

#### Variables

How a value is resolved:

| | 3.x | 4.0 |
|---|---|---|
| when | when a recipe read it | once at launch, before `setup` and any target; a missing `required` value or a bad conversion stops the run before any target |
| order | `-d`, the script's values (a later `var` replaced an earlier one; `var` beat `ExecOptions.Vars`), a field's environment name; per-demand values were shadowed by the script | per-demand values, `-d` (the last wins), the script's one definition (`var "N" "v"` or an `ExecOptions.ScriptVars` entry), the declaration's sources in list order |
| environment name of a declaration without `envVar` | the derived `BUILD_CONFIG` | `XAKE__BUILD_CONFIG`; an explicit `Env "X"` / `envVar = "X"` replaces it |
| a constant `var "N" "v"` | ignored the environment | `XAKE__N` overrides it when present; `-d` still wins |
| `Var` read of an undeclared name (`vars.field` without a matching declaration) | worked, read the environment | the reading target fails (E-8); a different declaration fails too (E-7); use `getVar`, `getEnv` |
| key of a field | `cliArg` when given | `name`, else the field name; `cliArg` is only the `-d` name |
| a name defined twice | the later `var` replaced the earlier; `var` beat `ExecOptions.Vars` | stops the run (E-2) |
| `-d` of a secret | a warning | stops the run (E-1a); pass `XAKE__<KEY>` |
| `-d` of a `Var.env` field | ignored | stops the run |
| `-d` pairs | in `ExecOptions.Vars` | in `ExecOptions.CliVars` |
| `Demand(target, vars)` | shadowed by the script's values | wins over everything |
| libraries (Xake.Dotnet) | read `NUGET_PACKAGES`, `XAKE_CSC_SERVER`, `XAKE_CSC_JOBS`, `XAKE_FSC_JOBS`, `CI`, ... from the environment | read only the variable set; the CI declares what it wants |
| package folder | `NUGET_PACKAGES`, else `~/.nuget/packages` | `<project root>/.packages` |
| `NUGET_FETCH`, `CSC_SERVER`, `XAKE_CSC_SERVER` | a bad value warned or meant "on" | the reading target fails; an empty value is the default |
| banner | `script/cli` for everything | one source per variable, secrets `***` |

```fsharp
// 3.x: BUILD_CONFIG was read for the field, a Var read needed no declaration
let vars = {| buildConfig = Var.string() |}

// 4.0: the field reads XAKE__BUILD_CONFIG; keep the old name explicitly
let vars = {| buildConfig = Var.string(envVar = "BUILD_CONFIG") |}
```

```fsharp
// 3.x: getVar "pkg" read the field p = Var.string(cliArg = "pkg")
// 4.0: the key is the field name; -d pkg=... still sets it
let vars = {| p = Var.string(cliArg = "pkg") |}
// ... let! p = getVar "p"   (or let! p = vars.p)
```

```fsharp
// 3.x CI: NUGET_PACKAGES=/cache and XAKE_CSC_JOBS=4 in the environment reached Xake.Dotnet
// 4.0: declare them (or pass -d NUGET_PACKAGES=/cache)
xakeScript {
    var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]
    var "CSC_JOBS" [Env "XAKE_CSC_JOBS"]
    ...
}
```

Dependencies: a target whose record comes from a 3.15 `Var` read (`Var (N, None)` plus `EnvVar`)
rebuilds once; a secret field's record moves from `SecretDep (env name, SecretEnv)` to
`SecretDep (key, SecretArg)`, one rebuild. The database format is unchanged (0.6).

Details and the error ids E-1a to E-12: [Variables](#variables-evaluated-at-launch) below and
[variables.md](variables.md).

#### Packages

| 3.x | 4.0 | Change in the script |
|---|---|---|
| `Xake` (engine, `Xake.Dotnet.dll` inside) | `Xake`: the engine alone (`Xake.dll`, `Xake.xml`); no `System.Resources.Extensions`, no `Microsoft.Win32.Registry` | nothing for an engine-only script |
| `Xake.Dotnet` (inside `Xake`; `open Xake.Dotnet`) | the package `Xake.Dotnet` 4.0.0, depends on `Xake [4.0.0, 5.0)` | add `#r "nuget: Xake.Dotnet, 4.0.0"`; `open Xake.Dotnet` unchanged |
| `Xake.Hermetic.Dotnet` 0.x | no package from 4.0 (its line ends at 0.10.0); the assembly ships inside `Xake.Dotnet` 4.0.0, the id points to `Xake.Dotnet` | replace `#r "nuget: Xake.Hermetic.Dotnet, 0.10.0"` with `#r "nuget: Xake.Dotnet, 4.0.0"`; `open Xake.Hermetic.Dotnet` unchanged |
| sources in `src/dotnet`, `src/hermetic` of this repository | [OlegZee/Xake.Dotnet](https://github.com/OlegZee/Xake.Dotnet) with their tests, docs, samples and the fsc/locks self-build | |

- `Xake.Dotnet.dll` left the `Xake` package from `4.0.0-preview.2`; the 3.x `Xake.Dotnet` line is
  not republished from this repository (#129, S-D14). `Xake.Hermetic.Dotnet` is no longer packed or
  published here (#129, S-D17).
- The moved parts: `src/dotnet`, `src/hermetic`, their tests, docs (toolchain discovery,
  `csc`/`fsc` syntax, compiler server, project descriptions, the hermetic reference, guide and
  architecture) and samples, and the fsc/locks self-build (`build.fsc.fsx`, `locks/`,
  `xake.fingerprints`) (dotnet-split S7). This repository builds, tests and packs the engine only;
  `publish.yml` takes `v*` tags only (no `hermetic-v*`).

```fsharp
// 3.x                                  // 4.0
#r "nuget: Xake, 3.15.0"                #r "nuget: Xake, 4.0.0"
open Xake                               #r "nuget: Xake.Dotnet, 4.0.0"
open Xake.Dotnet                        open Xake
                                        open Xake.Dotnet
```

#### Removed or renamed APIs

| 3.x | 4.0 |
|---|---|
| `Path.matchesPattern` | removed; `Path.matches mask root path` (#124). It keeps its signature, uses the module's mask semantics and raises `PatternException` on an invalid mask |
| `Fileset.toFileList1`, the `FileSystem` adapter (`FileSystemType`) | removed, not kept obsolete (D46); `toFileList` |
| `Path.specificity` | is `PatternAst.specificity` (#123) |
| `Path.parse`/`parseDir` | split as the lexer does (not inside `[...]`/`{...}`, not at an escaping `\`) and keep a trailing separator as a directory mask |
| `ExecOptions.Vars` | `ExecOptions.ScriptVars`, no alias (#115): `{ ExecOptions.Default with ScriptVars = [...] }`; `-d` pairs are in the new `ExecOptions.CliVars` |
| a full record expression for `ExecOptions`/`EngineOptions` | breaks (new fields `CliVars`, `VarDecls`, `VarDefinedBy`, `IgnoreEnvironment`, `EngineOptions.Variables`); build them with `with` |
| `Var.*` read without a declaration | needs a `varschema` field or an equal `var [...]` rule |
| `PackageRootSource.EnvironmentVariable`/`UserProfile`, `CompilerServer.fromEnvironment` (Xake.Dotnet) | `[<Obsolete>]`; the package root is the build's own folder, `fromEnvironment` returns `Shared None` |
| `RegexParseException` for a lone `(`/`)` or an unbalanced `[` | `PatternException` |
| `Xake.Dotnet` internals through `InternalsVisibleTo` | removed; the public seam: `hasRule`, `Xake.ProcessExec` |

### New features

- **Typed targets `Xake.Typed`** (#127, #130): a rule's groups are the fields of an F# record.
  `open Xake.Typed` after `open Xake`. Guide: [typed-targets.md](typed-targets.md).

  ```fsharp
  open Xake
  open Xake.Typed

  type AsmG = { cfg: string; fwk: string; name: string }
  let Asm = Target<AsmG> "out/(cfg:*)/(fwk:*)/(name:*).dll"

  let build = [
      Asm.Rule <| fun m -> recipe { do! trace Message "%s for %s" m.name m.fwk }   // m.fkw does not compile
  ]
  let want = need [ Asm.Format { cfg = "Release"; fwk = "net8.0"; name = "Core" } ]
  ```

- **`Command<'G>`** (typed, always reruns, as `command "name" { }`):

  ```fsharp
  type LockG = { name: string; cfg: string }
  let Lock = Command<LockG> "lock::(name:*)::(cfg:*)"
  let lockRule = Lock.Rule <| fun { name = name; cfg = cfg } -> recipe { do! trace Message "lock %s %s" name cfg }
  let wantLocks = need [ Lock.Format { name = "Core"; cfg = "Release" } ]
  ```

- **`varschema` and `var "N" [sources]`** (#111): typed fields and rules with sources `Env`,
  `Default`, `Cli`, `Secret`, `SecretHashed`; `--help` lists them, the banner shows each source.

  ```fsharp
  let vars = {|
      config = Var.string(cliArg = "config") |> withDefault "Debug"   // -d config=..., XAKE__CONFIG, Debug
      jobs   = Var.int()                                              // int option
  |}
  xakeScript {
      varschema vars
      var "OUT" [Cli "out"; Default "bin"]            // -d out=..., XAKE__OUT, bin
      rules [ "main" => recipe { let! config = vars.config in do! trace Message "%s" config } ]
  }
  ```

- **Secrets** (#111): `Secret`/`SecretHashed` in a rule, `Var.secret`, `|> secret`,
  `|> secretHashed` on a field. Every Xake logger replaces a secret value of 4 or more characters
  with `***`; the database keeps only whether it was set (or a salted HMAC for `secretHashed`);
  the value comes from `XAKE__<KEY>` or its `Env` name, `-d` of it stops the run.

  ```fsharp
  let vars = {| token = Var.secret<string>() |> required |}   // XAKE__TOKEN=... dotnet fsi build.fsx
  ```

- **`ExecOptions.IgnoreEnvironment`** (#115): no declaration reads the environment at launch;
  for tests and for programs that embed Xake. `getEnv` is not affected.

  ```fsharp
  xakeArgs [] { ExecOptions.Default with IgnoreCommandLine = true; IgnoreEnvironment = true } { ... }
  ```

- **`Var.read<'t> "NAME"`** and the optional `DotNetVars.Default` schema (#112) for libraries.
- **The pattern module `Xake.PatternAst`** (#117 to #124): public `Rule.parse`/`tryParse`/`check`,
  `Mask.parse`/`tryParse`/`check`, `escape`, `escapePath`, `unparse`, `groupNames`, `wildcards`,
  `specificity`, `format`, `formatMatches`, `glob`; one fileset walker. Not opened by `open Xake`.
  Guide: [overview.md](overview.md#patterns-masks-and-paths).
- **Fileset and string entries in `rm` and `cp`** (#135): a fileset or a mask string stands on
  its own line in the block and mixes with the operations in any order; the old forms are
  unchanged. A string entry is a file mask, as `file`.

  ```fsharp
  do! rm { !!"out/*.*" -- "out/keep.txt" }        // 3.x: rm { files (fileset { includes "out/*.*"; excludes "out/keep.txt" }) }
  do! cp { !!"bin/*.dll" -- "bin/*.Tests.dll"; "README.md"; todir "deploy" }
  ```

- **A public seam for task libraries** (#122): `hasRule : string -> Recipe<ExecContext, bool>` and
  the public `Xake.ProcessExec`.

### Migration checklist

In order. Each step stops the run with a message or changes a result; do them before comparing
build outputs.

1. **Packages.** Scripts that use `csc`, `fsc`, `msbuild`, `resgen` or the hermetic library: add
   `#r "nuget: Xake.Dotnet, 4.0.0"` and remove `#r "nuget: Xake.Hermetic.Dotnet"`.
   `grep -rn "Xake.Dotnet\|Xake.Hermetic" --include=*.fsx --include=*.fsproj .`
2. **Build once and read the errors.** Every `XPnnn` error carries the 4.0 spelling. Escape
   literal `( ) [ ] { } |` with `\` (or `PatternAst.escapePath` for a real path); `(a|b)` becomes
   `{a,b}`; `(ext:dll|exe)` becomes `(ext:{dll,exe})`.
   `grep -rnE '\(\w*:[^)]*\|' --include=*.fsx .` finds alternations.
3. **Rules whose meaning changed silently.** Search for `**/` in rule patterns (it now takes zero
   directories; write `*/**/` to require one), `dir/**` and trailing `/` in masks, `*.*`, `[` and
   `{` in masks, `\` as a separator.
   `grep -rnE '\*\*|\*\.\*|\\' --include=*.fsx .`
4. **Read the warnings in `build.log`** (`Pattern '...' (rule|include|exclude): ... [XPnnn]`); fix
   `XP105`, `XP106`, `XP110` (use `/`), `XP112`, `XP117`; XP113, XP114, XP119 mean a result differs.
5. **Removed APIs.** `Path.matchesPattern` -> `Path.matches`, `toFileList1` -> `toFileList`,
   `ExecOptions.Vars` -> `ExecOptions.ScriptVars`; `with` instead of full record expressions.
   `grep -rn "matchesPattern\|toFileList1\|FileSystemType\|Vars *=" --include=*.fsx --include=*.fs .`
6. **Variables.** Every recipe that reads `Var.*` has a declaring `varschema` field; a name is
   defined once (`var`, `ExecOptions.ScriptVars`, field); `getVar` reads of a `cliArg` field use the
   field's key.
   `grep -rn "Var\.\(string\|int\|bool\|create\|env\|secret\|arg\)\|getVar" --include=*.fsx .`
7. **Environment names.** A field or `var` rule without an environment name now reads
   `XAKE__<NAME>`: keep the old name with `envVar = "NAME"` / `Env "NAME"`, or export `XAKE__NAME`;
   a CI that exports `XAKE__<NAME>` for a constant `var` checks that it means it. Libraries read
   only the variable set: declare `NUGET_PACKAGES`, `XAKE_CSC_SERVER`, `XAKE_CSC_JOBS`,
   `XAKE_FSC_JOBS`, `CI`, `LOCK_REFRESH`, `SOURCE_DATE_EPOCH` as needed, or pass `-d`.
8. **Secrets.** Remove `-d TOKEN=...`; pass `XAKE__TOKEN=...`; mark credential fields `secret`.
   `grep -rn "\-d [A-Z_]*\(KEY\|TOKEN\|SECRET\|PASSWORD\)" .`
9. **Package folder.** Xake.Dotnet's package folder is `<project root>/.packages` unless
   `NUGET_PACKAGES` is declared; move or ignore the old cache, add `.packages` to `.gitignore`.
10. **Rebuild once.** Targets with a 3.15 `Var` read or a secret dependency, and filesets with a
    trailing `**`, rerun once; the database format is unchanged (0.6).

### Fixes and smaller changes

- A project root holding `( ) [ ] { } |` matches literally; `a//b` in a pattern is one separator;
  an empty group value no longer leaves `(lib:)` in a multi-output name and a `)` in a value no
  longer breaks it (#123).
- An unreadable directory in a scan is skipped with a warning (3.x: the build failed with
  `UnauthorizedAccessException`); a `**` scan no longer loops on a directory link cycle (#124).
- `fileset { includefile ... }` and `excludefile` work (their arguments were swapped);
  `includefile` skips blank lines (#124).
- `Demand(target, vars)` values win over the script's values (they were shadowed) (#111).
- File loggers are flushed at the end of the build, and a locked previous log file is tolerated
  (#123).
- `PatternException.ToString()` is its message alone: a script failing at load shows no stack
  trace. `XakeEngine.Start` raises `XakeException` on a launch error (#111).
- The banner lists each variable once with its source (`cli`, `cli:<name>`, `script`,
  `env:<NAME>`, `default`, `request`, `(not set)`), secrets as `***`; `--help` lists `var [...]`
  rules and the environment name of every rule and field and evaluates nothing (#111).
- `cp` in directory mode scans `dir/**/*` (3.x: `dir/**/*.*`), so it still copies every file
  (#124).
- `build.fsx` takes the NuGet key as a secret: `NUGET_KEY = Var.env(envVar = "NUGET_KEY") |> secret`
  (#111).
- Docs: variables ([variables.md](variables.md)), patterns, typed targets
  ([typed-targets.md](typed-targets.md)) (#113, #131); secret masking measurements (#116).

### Announcement: phony `=>` is to be phased out in favour of `Command`

Not a deprecation: in 4.0 `=>` (and the builder operation `phony`) compiles without a warning and
behaves as before. The direction is that `Command` (typed, `Command<'G>`, or the string
`command "name" { }`) replaces phony rules, which is the way to retire "phony" and its semantics.
Migrating a `=>` rule to `Command<'G>.Rule` **adds `AlwaysRerun`** (a phony rule does not rerun by
itself; a command always does), so a rule that relied on skipping stays a `=>` for now. The
migration recipe is in [typed-targets.md](typed-targets.md#a-phony-rule-with-groups-becomes-a-command).

The rest of this section is the reference of each area; the numbers in brackets are the items of
section 3 of the pattern spec.

### Patterns: rule matching on the pattern module

Rule patterns (`..>`, `*..>`, `=>`, `<==`, `<<<`, `target`, `targets`, `command`) are parsed and
matched by the new pattern module (`Xake.PatternAst`; spec:
`docs/features/pattern-module/spec.md`). Fileset scanning follows in the next section. Numbers
in brackets are the items of spec section 3.

- **Errors when the rule is created.** An invalid rule pattern raises `PatternException` when the
  script creates the rule (`Invalid pattern '<text>' (rule) at character <n>: <reason> [XPnnn]`,
  the reason naming the 3.x meaning and the 4.0 spelling); a rule built from the `Rule` cases
  directly fails the build with the same message the first time it is matched.
- **Warnings** on legacy spellings go to the build log at level Warning, once per pattern
  (`Pattern '<text>' (rule): ... [XPnnn]`); those raised while the script loads are delivered when
  the build starts.
- **Breaking: `|` inside a named group** (`(ext:dll|exe|nupkg)`, a regex alternation in 3.x) is an
  error that gives the 4.0 spelling `(ext:{dll,exe,nupkg})` [4, XP003]. `(`, `)` and `|` outside a
  named group (3.x: a regex group or alternation) are errors [3, XP003]; a lone `(`/`)` or an
  unbalanced `[` raise `PatternException` instead of `RegexParseException` [5].
- **Breaking: `**/` takes zero or more directories in a rule** (3.x: one or more): `**/*.dll` now
  also matches `a.dll` at the root (a rule `*.dll`, if present, still wins). A match that took zero
  directories logs XP119 once per pattern; write `*/**/*.dll` (with a capture `(path:*/**)/`) to
  require a directory [12].
- **Breaking: braces are an alternation** in rules, `{a,b}` (3.x: literal text); `{x}` without a
  comma is an error [6, 7, XP005]. Classes `[abc]`, `[a-z]`, `[^a]` keep their 3.x meaning; `[!a]`
  is now a negated class [11, XP117]; `[c]` is still the character `c`, deprecated in favour of
  `\c` or `c` [10, XP105].
- **`\`**: before `( ) [ ] { } |` it is an escape (`src\(x\)` names `src(x)`) [1]; before any
  other character it is still a separator, deprecated (XP110), an escape from 5.0 [2]. On the
  target side `\` is a separator on Windows only.
- **Breaking: errors for 3.x spellings that meant nothing or something unintended:** a group
  name starting with a digit `(1:*)` [18, XP007]; a repeated group name `(p:*)/(p:*)` (3.x: the
  last value won) [19, XP015]; an unclosed `(name:` [20, XP006]; `.` or `..` after a wildcard
  [21, XP012]; a trailing `/` [22, XP011]; `/` inside a class, `[z-a]`, `[[:` [24, XP010]; an empty
  pattern [35, XP018]; `{a/,b}` ending the pattern [36, XP019].
- **Segment-local `**`** (`a**b`) stays within one segment, like `*` (3.x: crossed `/`) [13, XP112].
- **Multi-output rules** (`*..>`, `targets`) check their outputs when created: every output has
  the same named groups [31, XP013] and no class or braces outside a group [32, XP014]. Output
  names are formatted from the match by the module (`PatternAst.formatMatches`): an empty group
  value no longer leaves `(lib:)` in a name and a `)` in a value no longer breaks it [30].
- **Rule selection:** a `/` next to `**` no longer counts as a literal in specificity, so a
  globstar rule can lose a tie it won in 3.x (`*/?.dll` now beats `**/x.dll` for `a/x.dll`) [33].
- **Fixes:** a project root holding `( ) [ ] { } |` matches literally [29]; `a//b` in a pattern
  is one separator [34]; case follows one comparer per OS, ordinal on Linux and macOS,
  ignore-case on Windows (3.x `Path.matches` ignored case everywhere) [25].
- **API:** `Path.matchGroups` keeps its signature and keys (named groups, positional `"1"`,
  `"2"`, ... of every wildcard at any depth, `"0"` the whole path); `getRuleMatch` is unchanged.
  `Path.matches` keeps its signature and uses the module's mask semantics (an invalid mask raises
  `PatternException`; `.`/`..` in the file path are resolved). `Path.specificity` is
  `PatternAst.specificity`. `Path.matchesPattern` is removed with the 3.x part matcher (see the
  next section).

### Patterns: fileset scanning on the pattern module

Masks (`!!`, `ls`, `++`, `--`, the `fileset { }` operations, `includefile`/`excludefile`) are
parsed and scanned by the pattern module: one walker, the same grammar as rules (classes,
braces, `\` escapes), the 3.x part matcher is gone. Numbers in brackets are the items of spec
section 3.

- **Errors when the fileset is built.** An invalid mask raises `PatternException` where the mask
  is added (`Invalid pattern '<text>' (mask) at character <n>: <reason> [XPnnn]`), usually when the
  script loads; a mask built directly as `Includes (Path.parse m)` fails when the fileset is
  evaluated. `(`, `)`, `|` [3, XP003], a named group [17, XP008], `{x}` [7, XP005], an
  unbalanced `[` [5, XP002], `/` inside a class [24, XP010], `.`/`..` after a wildcard [21, XP012]
  and an empty mask [35, XP018] are errors (3.x: literal text or a silent parent step). Write
  `\(` for a literal `(`; `PatternAst.escapePath` turns a real path into a mask. `includefile`
  skips blank lines.
- **Warnings** on legacy spellings go to the build log at level Warning, once per mask and use
  (`Pattern '<text>' (include): ... [XPnnn]`); some only when the scan sees the evidence (an
  entry named like the 3.x literal reading of `[ab]`/`{a,b}` [XP104], a `dir/**` that finds
  something [XP113], a `*.*` that rejects a dotless name [XP114], an absolute exclude outside the
  fileset [XP120]).
- **Breaking: classes and braces are wildcards in masks** (3.x: literal text): `src/{a,b}.cs`,
  `[abc]`, `[a-z]`, `[^a]` [6, 8; XP104 on evidence]; `[c]` in an include is the character `c`,
  so `sub[1]/*.c` finds `sub1` [9, XP106]; `[!a]` in an exclude wildcard segment is a negated
  class [11, XP117].
- **Breaking: `dir/**` is every file below `dir`** (3.x: an include listed the directories below,
  an exclude matched nothing); write `dir/**/` for the directories [14, XP113]. `dir/**/*` keeps
  its meaning.
- **Breaking: a trailing `/` names directories in every mask**, including `++`, `--` and the
  builder (3.x dropped it there): `-- "obj/"` removes every file below `obj` and the scan does not
  enter it (3.x: removed nothing); `-- "obj"` removes only a file named `obj` [23].
- **Breaking: `*.*` needs a `.` in the name** (3.x: any name, the Win32 listing rule) [15, XP114];
  the other OS-listing quirks are gone: `?` never matches zero characters, and `*.rdl` no longer
  finds `.rdlx` on .NET Framework [16]. The `cp` task's directory mode scans `dir/**/*` (3.x:
  `dir/**/*.*`, whose OS listing took dotless names too), so it still copies every file.
- **Breaking: case follows one comparer per OS**, ordinal on Linux and macOS, ignore-case on
  Windows: a literal exclude segment is no longer case-insensitive everywhere, and a wildcard
  include no longer ignores case on macOS through the .NET listing [25]. A literal segment
  (`SRC/a.cs`) is probed and keeps the mask's spelling, whatever the volume stores.
- **Breaking: `**` does not follow directory symlinks and junctions** (3.x followed them, a cycle
  ended only at the OS limit); each link skipped is logged once at level Message. A link is still
  entered by a literal or a one-segment pattern (`node_modules/x/**`, `*/`) [26].
- **Breaking: results are ordered and duplicate-free**: within a directory, entries in ordinal
  order, files before subdirectories; includes in declaration order; a file found twice is kept
  once, at its first place (3.x: file-system order, duplicates kept) [27].
- **An unreadable directory** is skipped and logged once as a warning (3.x: the build failed with
  `UnauthorizedAccessException`) [28].
- **Unchanged:** an exclude applies to the includes before it; a mask without any wildcard is an
  explicit name, listed whether or not the file exists (D13); `\` before a non-reserved character
  is still a separator, deprecated [2, XP110]; `a//b` is one separator [34].
- **API:** `Fileset`, `FilesetElement` (`Includes of PathMask`), the operators and the builder keep
  their shapes. `Path.parse`/`parseDir` split as the lexer does (not inside `[...]`/`{...}`, not at
  an escaping `\`) and keep the segment text; `Path.parse` keeps a trailing separator as a
  directory mask; `Path.maskText` gives the text back. `Path.matchesPattern` is removed: use
  `Path.matches mask root path`. `Fileset.toFileList1` and the `FileSystem` adapter
  (`FileSystemType`) are removed: the walker reads the file system; use `toFileList` (D46). The `fileset { includefile ... }` and
  `excludefile` operations work (their arguments were swapped).

### Typed targets (`Xake.Typed`)

New: rule patterns whose groups are the fields of an F# record, checked by the compiler.
`open Xake.Typed` after `open Xake`. Guide: [typed-targets.md](typed-targets.md); the pattern grammar is described in
[overview.md](overview.md#patterns-masks-and-paths).

- `Target<'G> "out/(cfg:*)/(name:*).dll"` (or a list of outputs), `Target<'G, 'O>` (the outputs
  as a record of patterns, `Make` gives the record of paths) and `Command<'G>` (always runs, as
  `command`). The carrier `'G` is a record (named or anonymous) whose `string` fields are the
  named groups plus `_1`, `_2`, ... for the bare wildcards, `string` for one group, `unit` for
  none.
- Rules: `X.Rule <| fun m -> recipe { ... m.cfg ... }` or `X.rule { let! m = matched ... }`.
  Generation: `Format`, `Paths`, `For`, `Any`, `Glob` (a `Fileset`, bound values escaped);
  every generated path is matched back against its pattern.
- The record and the pattern are compared when the value is created (usually at script start);
  a mismatch, an invalid pattern (its `XPnnn` error) or outputs that disagree (XP013, XP014)
  raise `PatternException` with the declaration in front (`Target<{| fwk; lib |}> "...": ...`).
  Bare wildcards cannot pair across several outputs: name them.
- **API:** `PatternAst.glob` (spec 5.7) turns a rule pattern with some values bound into a mask;
  `PatternException.ToString()` is its message alone, so a script failing at load shows no stack
  trace.

### Variables: evaluated at launch

Variables are evaluated once, before the build, by declarations the script states.
Details: [tasks.md](tasks.md#script-variables).

- **New: `var "N" [sources]`.** `var "N" [Env "X"; Default "v"]` takes the first defined source
  in list order; `Cli "ext"` sets the name's command-line name (`-d ext=...`); `Secret` /
  `SecretHashed` make it a secret. `var "N" "v"` keeps its meaning (a script value).
- **Evaluation at launch.** `var` rules and `varschema` fields are evaluated once, before `setup`
  and the first target (run, `--dryrun`, `--dump`, engine `start`): the environment is read
  there, and a missing `required` value or a value that does not convert to the field's type
  stops the run before any target (before: when a recipe read it). A `varschema` field's value is
  visible to `getVar` and in the banner. `--help` evaluates nothing.
- **Precedence:** engine per-demand variables > `-d` (the last wins) > the script's one definition
  of the name (`var` or `ExecOptions.ScriptVars`) > the declaration's sources. A read records the final
  value (`Var (N, value)`), not its source, so moving an equal value between the environment,
  `-d` and `var` no longer rebuilds.
- **Breaking: a `Var.*` read in a recipe needs a declaration** of its key: a `varschema` field
  (or an equal `var [...]` rule). Without one the reading target fails ("... is read through Var
  but is not declared: declare it in varschema, or use getVar ..."); with a different one (another
  env name, default, type, required flag, secret mode or command-line name) it fails too. A Var
  read no longer reads the environment and no longer records `EnvVar`; use `getVar` for script
  values and `getEnv` for the environment.
- **Breaking: the key of a field is its `name`, else its field name.** `cliArg` is only its
  command-line name: with `p = Var.string(cliArg = "pkg")`, `-d pkg=v` sets `p` and `getVar "p"`
  reads it (3.15 keyed it by `pkg`; a script reading `getVar "pkg"` changes). `-d p=v` for such
  a field stops the run (`-d p: the variable is set on the command line as pkg; use -d pkg=...`).
- **Breaking: a name defined twice in the script stops the run**: two `var` ops, a `var` op and an
  `ExecOptions.ScriptVars` entry, a `varschema` field and a rule (before: the later `var` replaced the
  earlier value, `var` won over `ExecOptions.ScriptVars`).
- **Breaking: `-d` of a secret stops the run**, under its key, its command-line name or one of its
  environment names (3.15 warned for an argument secret); `-d` of a `Var.env` field stops the run
  (3.15 ignored it). `getVar`/`getEnv` of a secret return the value; masking applies to output.
  Mark credential fields secret: `build.fsx` now has `NUGET_KEY = Var.env(envVar = "NUGET_KEY") |> secret`.
- **Breaking: an implicit `XAKE__<NAME>` environment source.** Every declaration without an
  explicit environment name reads `XAKE__<NAME>` first, `NAME` being its key in UPPER_SNAKE
  (`buildConfig` -> `XAKE__BUILD_CONFIG`, `netfx-target` -> `XAKE__NETFX_TARGET`): a `var` rule
  without an `Env` source (`var "N" [Default "d"]` reads `XAKE__N`, then the default), a
  `Var.string`/`int`/`bool`/`create`/`env`/`secret` field without `envVar`. The 3.15 derived name
  without the prefix (`BUILD_CONFIG`) is no longer read, with no compatibility path. An explicit
  `Env "X"` / `envVar = "X"` replaces the implicit source; `Var.arg` has no environment source.
  Migration: keep a plain name with `envVar = "NAME"` / `Env "NAME"`, or export `XAKE__NAME`.
- **Breaking: a constant is overridden by `XAKE__<NAME>`.** `var "N" "v"` and an
  `ExecOptions.ScriptVars` entry take `XAKE__N` when it is present (banner `env:XAKE__N`), else `v`;
  `-d` still wins. Names that nothing declares get no implicit source: `XAKE__*` variables are not
  scanned into the variable set. Two keys whose implicit names are equal (`a-b` and `A_B`,
  `fooBar` and `FOO_BAR`) stop the run: `Variables 'A' and 'B' both read the environment variable
  XAKE__N; rename one, or give one an explicit Env source`.
- **Secrets through `XAKE__`:** `var "T" [Secret]` reads `XAKE__T`, the normal way to pass a
  secret; `-d T=...` and `-d XAKE__T=...` stop the run ("set XAKE__T instead"). `--help` shows the
  environment name of every rule and field, implicit or explicit.
- **New: `ExecOptions.IgnoreEnvironment`** (default `false`), for tests and for programs that
  embed Xake: no declaration reads the environment at launch (neither `XAKE__<NAME>` nor an
  explicit `Env`/`envVar`); values come from per-demand values, `-d` (unless
  `IgnoreCommandLine`), the constant and the defaults. `getEnv` is not affected; the banner shows
  `environment:  ignored (IgnoreEnvironment)`. No command-line flag; `xakeEngine` leaves it off.
  See [variables.md](variables.md#pinning-values-tests-and-embedding).
- **Engine:** `Demand(target, vars)` values now win over the script's values (they were shadowed);
  a per-demand value for a secret, an environment-only or a mistyped variable fails the demand
  before any target. `XakeEngine.Start` raises `XakeException` on a launch error.
- **Output:** the banner lists each variable once with its source (`cli`, `cli:<name>`, `script`,
  `env:<NAME>`, `default`, `(not set)`), secrets as `***` (3.14 labelled all `script/cli`);
  `--help` lists `var [...]` rules after the `varschema` fields, with their command-line names;
  the `Debug` options dump shows the variables, secrets masked.
- **Dependencies:** a target whose record comes from a 3.15 Var read (`Var (N, None)` +
  `EnvVar`) rebuilds once; a secret field's record moves from `SecretDep (env name, SecretEnv)` to
  `SecretDep (key, SecretArg)`, one rebuild. Repeated reads of a name in one recipe record one
  dependency. The database format is unchanged (0.6).
- **Breaking: `ExecOptions.Vars` is renamed `ExecOptions.ScriptVars`** (the script's values: the
  entries given to `xake`/`xakeArgs` and every `var "N" "v"`; `CliVars` holds `-d`). No alias:
  `{ ExecOptions.Default with Vars = [...] }` becomes `{ ExecOptions.Default with ScriptVars = [...] }`.
  `ExecContext.Vars` (what a recipe reads) keeps its name. The E-2 text names
  `ExecOptions.ScriptVars`.
- **API:** new `VarSource`, `VarOrigin`, `VarDecl`, `VarEntry`; new fields `ExecOptions.CliVars`
  (the `-d` pairs; `-d` no longer goes into `ExecOptions.ScriptVars`), `ExecOptions.VarDecls`,
  `ExecOptions.VarDefinedBy` (where each `var`/`varschema` definition came from, for the
  diagnostics) and `EngineOptions.Variables` (the variable set built at launch). Binary break for
  code that builds `ExecOptions`/`EngineOptions` with a full record expression (not `with`);
  `ExecContext` and the records Xake.Hermetic.Dotnet builds are unchanged.

### Library readers (Xake.Dotnet) and the optional import of library declarations

- **Breaking: Xake.Dotnet reads its build variables only from the variable set**, never from
  the environment. `NUGET_PACKAGES`, `XAKE_CSC_SERVER`, `XAKE_CSC_JOBS`, `XAKE_FSC_JOBS` set
  only in the environment are ignored; a CI that relied on one adds a declaration, e.g.
  `var "NUGET_PACKAGES" [Env "NUGET_PACKAGES"]`, `var "XAKE_CSC_SERVER" [Env "XAKE_CSC_SERVER"]`,
  `var "CSC_JOBS" [Env "XAKE_CSC_JOBS"]`, or passes `-d`.
- **Breaking: the package folder defaults to `<project root>/.packages`** (was the environment
  variable `NUGET_PACKAGES`, else `~/.nuget/packages`). It is always the build's own:
  `packageRootWithSource` returns `ScriptVariable` for it, so `HERMETIC=on` accepts it without a
  `NUGET_PACKAGES` variable; `PackageRootSource.EnvironmentVariable`/`UserProfile` are
  `[<Obsolete>]`. A child `dotnet restore` gets the folder as `NUGET_PACKAGES`. The context-free
  `DotNetFwk.nugetRoot ()` is `~/.nuget/packages` and reads no environment.
- **Breaking: strict switches.** `NUGET_FETCH`, `XAKE_CSC_SERVER` (`on`/`off`, `true`/`false`,
  `yes`/`no`, `1`/`0`) and `CSC_SERVER` (`on`, `off`, keepalive seconds) fail the reading target
  on any other value (`CSC_SERVER='bogus': expected on, off or a number of seconds`; 3.15 warned
  or took it as on). An empty value is the default (`-d NUGET_FETCH=` is on).
- `RunOptions.Default.Server` is `Shared None` and reads nothing; `CompilerServer.fromEnvironment`
  is `[<Obsolete>]` and returns `Shared None`. A recipe resolves the server from `CSC_SERVER`,
  then `XAKE_CSC_SERVER` (`CompilerServer.resolve`).
- **`Var.read<'t> "NAME"`**: the read for a library. A declared name is read through the
  script's declaration, whatever its sources and default, only its type checked
  (`Variable 'N' is read as 'T' but is declared by ... as 'U'`); an undeclared one is `getVar`.
  No environment read, the dependency `getVar` records.
- **`DotNetVars.Default`**: the variables Xake.Dotnet reads, as an optional schema
  (`NUGET_PACKAGES`, `NUGET_FETCH`, `HERMETIC`, `CSC_SERVER`, `XAKE_CSC_SERVER`, `CSC_JOBS`,
  `FSC_JOBS`; strings, `-d` only, the library's defaults). `varschema DotNetVars.Default` lists them
  in `--help` and the banner; `{ DotNetVars.Default with NUGET_PACKAGES = Var.string(envVar = "NUGET_PACKAGES") |> withDefault ".packages" }`
  also takes the environment (without `envVar`, `XAKE__NUGET_PACKAGES`). Importing changes no value; a target built before the import
  rebuilds once (its dependency records the default).

### Public seam for task libraries

Xake.Dotnet uses public engine API only; no `InternalsVisibleTo` to it.

- **New: `hasRule : string -> Recipe<ExecContext, bool>`**: whether a rule of the script would
  build a target (the name resolved as `need` resolves it), without building it.
- **`Xake.ProcessExec` is public** (`RequireQualifiedAccess`): `pexec`, `pexecSync`,
  `drainTimeoutMs`, low-level process plumbing for libraries; scripts use `shell`/`sh`.

## 3.15.0 (2026-10-29)

- **The published version is the tag.** `v3.15.0` goes to nuget.org as `3.15.0`, no longer with
  the run number as a fourth component (3.14.0 is `3.14.0.45`); the same for
  Xake.Hermetic.Dotnet from its next release. A dependency range or `#r` can be written from the
  tag. Older releases keep their four-part versions.
- **`ExecContext.withLogger logger ctx`**: the context with its logger replaced, for code
  outside `Xake.dll` (a `{ ctx with Logger = ... }` compiled elsewhere breaks when
  `ExecContext` gains a field). The records Xake.Hermetic.Dotnet builds — `ExecContext`,
  `CscSettingsType`, `RunOptions`, `Csc`, `FscSettingsType`, `FscRunOptions`, `Fsc` — get no new
  fields until 4.0 (`RecordShapeTests` guards the field lists; docs/devprocess.md).

## 3.14.0 (released as 3.14.0.45, 2026-10-28)

- **`setup` op.** `setup ["a"; "b"]` runs the targets sequentially before the build (CLI: after
  options and variables are final; engine: once at start). Symmetric to `teardown`, but several
  `setup` ops accumulate while `teardown` replaces. A setup failure is logged, skips the build,
  still runs teardown and fails the run (exit code 2, `XakeException` with `ThrowOnError`).
  Not run in `--dump`/`--dryrun`; the dry run lists the setup targets. New `ExecOptions.Setup`
  (binary break for code that builds `ExecOptions` with a record expression, not `with`).
  Declare setup targets that must run every time with `command "name" { ... }` (always reruns).
- **Run banner at `Info`.** Version, mode, targets, root, database, threads, rule matching, log
  settings, setup/teardown and the script variables (secrets as `***`, source labelled
  `script/cli`) are logged once per run, also for `--dump` and `--dryrun`. Visible in the default
  `build.log`, not on the default console.
- **Behaviour change: the command line wins over the script.** A script that pinned a value with
  `var "NAME" v` now yields to `-d NAME=value`; `-d` also wins over `ExecOptions.Vars` passed to
  `xake`/`xakeArgs`. The last `-d` wins when a name is given twice (before: the first); `var`
  still wins over `ExecOptions.Vars`. `consolelog` yields to `-ll`, `filelog` to `-fl` (path) and
  `-fll` (level), each separately, and `rootdir` to `-r`, the way `want` already yielded to
  targets on the command line. `IgnoreCommandLine = true` still skips the command line entirely.
  The environment is not involved. Engine mode's per-demand variables are unchanged. Mechanism:
  `Run` applies the command line over the script's options, changing only the fields whose flags
  are given; the logo and `--help` text are printed then. This adds nothing to `ExecOptions` or
  `XakeScript`.
- **Breaking (source): `wantOverride` removed; `want` always yields to command-line targets.** A
  script that uses `wantOverride` fails to compile; replace it with `want`. `want` now replaces
  `ExecOptions.Targets` passed to `xake`/`xakeArgs` (before: it yielded to them), the way `var`
  wins over `ExecOptions.Vars`; command-line targets replace both. `src/hermetic.tests` moved to `want`
  with Xake.Hermetic.Dotnet 0.9.0.

## 3.13.0 (released as 3.13.0.43, 2026-10-27)

- **msbuild and NuGet diagnostics in the log.** `Tool.diagnosticLevel` (and the forwarder
  `levelFromString`) also recognizes `<origin> : error|warning CODE: text [entry]`
  (`MSBUILD : error MSB1001`, `CSC : error CS2001`, `/p/a.csproj : error NU1101`) and NuGet's
  `warning NU1xxx:` without an origin. Errors go to `Error` (before: `Verbose`, invisible at the
  default verbosity), these warnings to `Info`. The `msbuild` task, csc/fsc and the
  reference-pack `dotnet restore` classify through the new `Tool.DiagnosticLog` (a repeated
  warning is logged once per run) and fail with the first 5 distinct errors, then
  "+N more, see the log" (`Tool.failOnExitCodeWith`, `Tool.errorSummary`). Signatures unchanged;
  more lines now reach `Error`. See [dotnet-build.md](https://github.com/OlegZee/Xake.Dotnet/blob/dev/docs/dotnet-build.md#troubleshooting).
- **Breaking (binary): secret script variables.** `Var.secret<'t>(envVar = ...)` (environment
  only), `|> secret` and `|> secretHashed` on any var. The database keeps a digest, never the
  value: presence only by default (a rotated token rebuilds nothing, unset/set does), a salted
  HMAC-SHA256 with `secretHashed` (a changed value rebuilds). Change reasons name the variable
  only. Every logger masks resolved secret values (4 characters or more) as `***`, `--dump` and
  `--dryrun` included; `--help` shows no default for a secret; `-d` is refused for `Var.secret`
  and warned about for an argument var marked `secret`. A plain `getEnv`/`getVar` of a name
  declared secret is treated as secret. `OptionalVar`/`RequiredVar` gain a `Secret` field and
  `VarHelp` an `IsSecret` field: code compiled against an older Xake that builds these records
  must be recompiled (Xake.Hermetic.Dotnet needs a range bump). Database format 0.6 (new
  dependency case `SecretDep`); 0.4/0.5 databases load and are upgraded, clear values of names
  now declared secret are replaced by their digest. See [tasks.md](tasks.md#secret-variables).

## 3.12.0 (tagged v3.12.0, 2026-10-24)

- **Rule selection: the most specific matching rule wins.** When several rules match a target,
  Xake now picks the one with the most literal characters, then the fewest `**`, then the
  fewest/tightest wildcards (`?` tighter than `*`; a named group scores like its bare wildcard),
  instead of simply the last declared. Two *different* patterns that tie fall back to last-declared
  and log a warning; two rules with the *same* pattern string keep the silent last-wins override.
  This only changes builds that relied on declaration order against specificity. `ruleMatch LastDeclared`
  in the script restores the old order — a **deprecated** one-release migration aid.
  `-d EXPLAIN_RULE=<target>` prints the candidate rules and the winner. See
  [overview.md](overview.md#rule-selection--the-most-specific-rule-wins).
- Internal: one pattern AST drives both rule matching and the specificity score (no behaviour change).

## 3.11.0 (tagged v3.11.0, 2026-10-22)

- `csc`/`fsc` compile from an SDK path that contains a space: SDK and response-file paths are
  quoted (`Tool.quoteArgument`, see [hermetic/README.md](https://github.com/OlegZee/Xake.Dotnet/blob/dev/docs/hermetic/README.md) for the package side).

## 3.10.1 (tagged v3.10.1, 2026-10-21)

- **Progress line: running tasks as coloured Braille dots** (one dot per task, cells filled
  column-wise `⠁⠃⠇⡇⡏⡟⡿⣿`, at most 4 cells for 30 tasks, `⣿⣿⣿⣿+` above; green, yellow past
  half, red above the cap). Redirected output keeps the `N tasks` text.
- **`rm { }` and `cp { }` take many targets**: `dir`, `file` and `files` may repeat and combine,
  applied in declaration order (before, each op overwrote the previous one and only the first
  kind ran). `rm` skips paths removed by an earlier target; `cp` keeps each source's relative
  path rule (`file` always flattened). `RmArgs` is now `{ targets: RmTarget list; verbose }`,
  `CpArgs` has `sources: CpSource list` instead of `dir`/`file`/`files`.
- Build scripts (not the package): `test` runs 8 parallel jobs (`testjobs.fsx`, shared by
  build.fsx and build.fsc.fsx) on test projects built once; each job logs to
  `out/test/<project>/<slice>/`. `test` takes about 55 s instead of 3 min 40 s.
  `-d FILTER=...` now reaches the scripts' variable (only `$FILTER` did).

## 3.10.0 (released as 3.10.0.35)

- **Breaking: `need` of a rule target while a resource is held fails.** Inside `withResource`,
  and anywhere in a `requiring` rule, a `need`/`needFiles`/`dependsOn` of a target that has a rule
  raises an error naming the resource, the needing and the needed target. It could deadlock the
  build (every unit held by a recipe waiting for dependencies that wait for a unit; a hang at zero
  CPU). Plain files without a rule are still allowed. Need first, then acquire; `allowNeed body`
  lifts the check when the dependency never takes the resource. See [resources.md](resources.md)
  and [delegated.md](delegated.md#1-resource).
- **`CSC_JOBS` / `FSC_JOBS`** cap the concurrent compiler processes of `Csc.run` / `Fsc.run`
  (script variable, then env `XAKE_CSC_JOBS` / `XAKE_FSC_JOBS`; unset or `0` unlimited). Taken
  around the process launch only, after every input is built, so it replaces a `withResource`
  around the compile rule. Not a dependency: changing it rebuilds nothing. See
  [csc-server.md](https://github.com/OlegZee/Xake.Dotnet/blob/dev/docs/csc-server.md#limiting-concurrent-compiles-csc_jobs-fsc_jobs).
- **`throttle (resource, units)`** in `csc { }` / `fsc { }` (`Throttle` in `CscSettingsType`,
  `RunOptions`, `FscSettingsType`, `FscRunOptions`; default `None`): the compiler launch takes
  the script's own resource, at the same place, in place of `CSC_JOBS` / `FSC_JOBS`.
- **Breaking for record construction:** `ExecContext` gains `HeldResources`. Code built on 3.9
  that copies an `ExecContext` (`{ ctx with ... }`) has to be rebuilt; Xake.Hermetic.Dotnet
  0.4 (`[3.9.0.30, 3.10)`) does, and needs a release on 3.10.
- **Xake.Hermetic.Dotnet: `Locks.compile settings lockPath fwk`**, with `Locks.Settings = { Roots;
  Versions; Lock: Lock.Options }` (`Settings.Default`: no roots, no versions, default options).
  `compileAsWith`/`compileAs` are thin wrappers over it, unchanged. The script's `Lock.Options`
  reach the runner (an explicit `Run.Server` wins over `CSC_SERVER`); on a release built on 3.10
  that is where `Run.Throttle`/`FscRun.Throttle` go. See
  [hermetic/lock.md](https://github.com/OlegZee/Xake.Dotnet/blob/dev/docs/hermetic/lock.md#building-from-xake-generated-locks).

## 3.9.0 (tagged v3.9.0, 2026-10-18; 3.9.1 the same day)

- **Project description in the core.** `xake.project.json` (the format Xake.Hermetic.Dotnet
  introduced) is now `Xake.Dotnet.Description`: parse, validate, `load`/`save`/`find`/`beside`,
  schema `docs/schemas/xake.project.schema.json`. The schema is the hermetic package's latest plus
  `frameworkReferences` (shared frameworks). See [description.md](https://github.com/OlegZee/Xake.Dotnet/blob/dev/docs/description.md).
- **`csc { project "path" }` and `fsc { project "path" }`** compose a compilation from a
  description: sources (masks, `!` excludes), resources, defines and the SDK's framework symbols,
  options, `internalsVisibleTo`/`assemblyAttributes`, framework assemblies by name or path, and
  `frameworkReferences` (`Microsoft.AspNetCore.App`, `Microsoft.WindowsDesktop.App`) from the
  SDK's targeting packs. What the block gives explicitly wins. No msbuild, no restore: a
  description with `packages` or an unresolved `projectRefs` entry is rejected, naming
  Xake.Hermetic.Dotnet.
- `DotNetFwk.sharedFrameworkReferences`: a shared framework's reference assemblies for a .NET
  target framework.
- **Breaking for record construction:** `CscSettingsType` and `FscSettingsType` gain a `Project`
  field (build them with `{ CscSettings with ... }` as before; a full record expression needs the
  new field). Packages built on the 3.8 records (Xake.Hermetic.Dotnet 0.3 builds on `[3.7.0.26,
  3.8)`) need a release on 3.9.

## 3.8.0 (tagged v3.8.0, 2026-10-17)

- **`ArtifactDep2`: a dependency on a target with a rule carries the dependee's change stamp**, so
  a dependent goes stale when its dependee was rebuilt by an earlier separate run (database
  format 0.6; `ArtifactDep` is read from older databases). See [implnotes.md](implnotes.md).
- **`.resx` compiles on any host** (`Resx.compile`/`Resx.refresh`, the SDK's `GenerateResource`
  format: typed values, `ResXFileRef`, code pages), with csc crash diagnostics and platform
  target-framework monikers (`net8.0-windows`).

## 3.7.0 (tagged v3.7.0, 2026-10-12)

- `Fingerprints` library for committed generated files (recorded atomically, thread-safe).

## 3.6.0 (tagged v3.6.0, 2026-10-10)

- **`HERMETIC=on`**: the completeness gate in `Csc.ofSettings`/`Fsc.ofSettings` (see
  [dotnet-build.md](https://github.com/OlegZee/Xake.Dotnet/blob/dev/docs/dotnet-build.md#hermetic-mode)).
- **`NUGET_PACKAGES` and `NUGET_FETCH`** script variables; the .NET targeting pack is restored when
  it is not installed, `NETCORE_REF_VERSION` pins one per major.minor.
- Reference packages for the SDK-sourced defaults: netstandard2.1, the net46x `netstandard.dll`
  facade, `FSharp.Core` for `netN.0` targets.
- `csc`/`fsc` write `<out>.runtimeconfig.json` for `netN.0` executables only when the rule
  declares it as a target.

## 3.5.0 (tagged v3.5.0, 2026-10-04)

- **`Fsc` through the shared compiler runner**: `Fsc` record, `Fsc.ofSettings`, `Fsc.run`,
  `fsc { resolve }`.
- The default target framework is the SDK's own .NET; `net5.0` and later compose.
- **`fsc` builds for .NET Framework from any host**: `FSharp.Core` and a `netstandard.dll` facade
  are referenced implicitly (before: FS0074).
- Reference packs go through the one restore mechanism.

## 3.4.0 (tagged v3.4.0, 2026-10-01)

- **`Csc` as the resolved compilation**, `csc { resolve }`, `Csc.run`, the Roslyn compiler server
  (`CSC_SERVER`) and a compiler `toolset` (`CSC_TOOLSET`). See [csc-syntax.md](https://github.com/OlegZee/Xake.Dotnet/blob/dev/docs/csc-syntax.md).
- The SDK probe honours the project's `global.json`.
- A target requested twice is built once per run; file rules whose pattern contains `..` match;
  a transient sharing violation opening the database is retried instead of recreating it.
- The Xake.Hermetic.Dotnet package appears in this period; see
  [hermetic/README.md](https://github.com/OlegZee/Xake.Dotnet/blob/dev/docs/hermetic/README.md).
