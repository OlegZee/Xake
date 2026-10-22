# Typed rule matches: research

Status: research material for discussion (2026-10-23). Nothing here is decided. The experiments
it cites are in [`experiments/`](experiments/README.md), with exact commands and outputs.

The question: a syntax for rules (single- and multi-file) whose pattern groups are accessed
**typed and checked**, instead of `getRuleMatch "name"`; F# type providers in particular. Two
scope additions: the **inverse** of matching — producing paths from the same pattern — and
producing **lists** of paths for `need`.

---

## 1. The problem, on real code

Every rule that uses its pattern starts with a block of string lookups, the group name repeated
once in the pattern and once per use:

```fsharp
// build.fsc.fsx:64
target "out/(cfg:*)/(fwk:*)/(name:*).dll" {
    let! cfg = getRuleMatch "cfg"
    let! fwk = getRuleMatch "fwk"
    let! name = getRuleMatch "name"
    do! Locks.compileAs (lockOf name cfg) fwk versions
}
```

117 `getRuleMatch` occurrences in `.fs`/`.fsx` (27 in `BuildFromLockTests.fs`, 24 in
`RecordTests.fs`, 8 + 7 in the two build scripts), plus the same blocks copied through the docs
(`docs/hermetic/lock.md:534,1635,1768,1824`, `docs/hermetic-guide.md:647`, `docs/csc-syntax.md:178`, ...).

What actually goes wrong — measured in experiment 01 (`experiments/01-current-behaviour`):

| mistake | today's result |
|---|---|
| typo: `getRuleMatch "fkw"` for `(fwk:*)` | `""`, no error, no log. The recipe goes on and runs `dotnet build --framework ` (empty) — the failure surfaces, if at all, in a tool's error message far from the cause |
| multi-file rule, patterns name groups differently: `["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(name:*).xml"]` | the second target is generated as `out/a/.xml`, silently (`applyWildcards` substitutes `""` for a missing group, `ExecCore.fs:28-39`) |
| multi-file outputs bound by a list pattern: `let! [appfile; xmlfile] = getTargetFiles()` (`samples/features.fsx:133`) | FS0025 incomplete-match warning; order is declaration order, unchecked |
| `getRuleMatches ()` | the map carries noise: `"0"` (the full path) and `"1"`, `"2"`, ... for every `*`, including the `*` *inside* named groups |

There is no compile-time help of any kind: group names are strings at both ends, values are
`string`, and the multi-file rule's other outputs are reachable only positionally through
`getTargetFiles()`.

## 2. Inventory of the current API

| piece | where | notes |
|---|---|---|
| `Rule` union: `FileRule of string * Recipe`, `MultiFileRule of string list * Recipe`, `PhonyRule of string * Recipe`, `FileConditionRule of (string -> bool) * Recipe`, `DelegatedRule` | `src/core/Types.fs:85-91` | the pattern is a plain `string`; nothing is parsed at rule creation |
| operators `..>` (file), `*..>` (multi-file), `=>` (phony), `..?>` (condition) | `src/core/XakeScript.fs:91-101` | |
| builders `target "p" { }`, `targets ["p1"; "p2"] { }`, `command "n" { }` | `src/core/RuleBuilder.fs` | CE-based synonyms of the operators |
| `locateRule` — matches a target against rules, returns `rule, groups, targets` | `src/core/ExecCore.fs:43-83` | multi-file: groups come from the first pattern that matches; all targets are generated from the other patterns via `applyWildcards` |
| `applyWildcards` — substitutes groups back into a pattern | `src/core/ExecCore.fs:28-39` | **the engine already does the inverse of matching**, for multi-file outputs only; positional `*` by index, named groups by tag, missing -> `""` |
| `Path.matchGroups pattern root file` | `src/core/Path.fs:274-292` | regex built from the mask (`(name:mask)` -> `(?<name>...)`); returns every group of the regex, named and numbered |
| `ExecContext.RuleMatches : Map<string,string>` | `src/core/ExecTypes.fs:42` | set per task in `newTaskContext` (`ExecCore.fs:91-100`) |
| `getRuleMatches ()`, `getRuleMatch key` (`""` when absent) | `src/core/ScriptFuncs.fs:193-207` | `getRuleMatch` on a missing key returning `""` is pinned by a test (`XakeScriptTests.fs:339`, "Escaped brackets") |
| `getTargetFiles ()`, `getTargetFile ()`, `getTargetFullName ()` | `src/core/ScriptFuncs.fs:173-187` | multi-file outputs, positional |

Pattern features a typed design must cope with (all covered by `XakeScriptTests.fs:285-290`):
named groups with a sub-mask `(ext:c*)`, unnamed `*` / `**` / `?`, a group spanning segments
`(root:*/*-a).ss`, nested groups `(name:(plat:*)-a.ss)`, escaped brackets `[(]`. Phony rules use the
same syntax (`"lock::(name:*)::(cfg:*)"`, `"test::(project:*)/(slice:*)"`).

Multi-file rules in real use: `build.fsx:140` (`targets ["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"]`),
`samples/features.fsx:131`, `samples/book/intro.fsx:9`, `src/tests/FscTests.fs:190`,
`docs/cheatsheet.md:98`, `docs/tasks.md:145` (`["hw.dll"; "hw.runtimeconfig.json"] *..> csc {...}`).
In the multi-file rules of the docs the second output is a *side* output the recipe never names;
in `build.fsx:140` the recipe does not touch the outputs at all (dotnet build writes them).

## 3. The inverse: paths built by hand that duplicate a rule's pattern

The same layout is written twice: once as a rule pattern, once as string formatting at the
`need` site or in a helper. Nothing ties the two together.

| rule pattern | hand-built path elsewhere | drift risk |
|---|---|---|
| `build.fsx:140` `"out/(fwk:*)/(lib:*).dll"` + `.xml` | `build.fsx:50-54` `binaries name = [for fwk in frameworks do for ext in ["dll"; "xml"] -> $"out/%s{fwk}/%s{name}.%s{ext}"]` (used by `"build" <==`, `:103`); `build.fsx:152` `need [for dep in lib.Needs -> $"out/%s{framework}/%s{dep}.dll"]`; `:162` `"--output"; "./out/" + framework` | change `out/` to `bin/` in the rule and `build` asks for targets no rule makes |
| `build.fsx:180,190` `$"out/pkg/%s{xakePackage}/%s{xakePackage}.(ver:*).nupkg"` | `build.fsx:67` `packagePath id version = $"out/pkg/%s{id}/%s{id}.%s{version}.nupkg"` (used at `:172,177,217,227`); `:186,201` `$"out/pkg/%s{xakePackage}/"` | three spellings of one layout |
| `build.fsc.fsx:64` `"out/(cfg:*)/(fwk:*)/(name:*).dll"` | `build.fsc.fsx:26` `outOf cfg fwk _ = $"out/{cfg}/{fwk}"`, passed into `Locks.generate`, which appends `<assembly>.dll` itself (`src/hermetic/Locks.fs:207,230`) | the lock records output paths from `outOf`; if `outOf` and the rule disagree, `Locks.needAll` asks for files no rule builds — found at build time, in another file |
| `build.fsc.fsx:47` `"lock::(name:*)::(cfg:*)"` | `:51` `need [ $"lock::{r}::{cfg}" ]`, `:56` `$"lock::{name}::Release"`; `docs/hermetic/lock.md:1638,1644`; `Locks.fs:164,301` (doc comments) | |
| `build.fsx:117`/`build.fsc.fsx:81` `"test::(project:*)/(slice:*)"` | `testjobs.fsx:46` `testJobName job = $"{job.Project}/{job.Name}"`, used as `$"test::{testJobName j}"` (`build.fsx:106`, `testjobs.fsx:54`) | the pattern and its formatter live in different files |
| `build.fsx:109` `"test-build::(project:*)"` | `:121` `need [ $"test-build::{project}" ]` | |
| `samples/resources.fsx:40,55` `"download:(pkg:*)"`, `"restore:(project:*)"` | `:48-49` `[ for pkg in packages -> sprintf "download:%s" pkg ]` | |
| hermetic tests: rule `"out/(cfg:*)/(fwk:*)/(name:*).dll"` | `BuildFromLockTests.fs:21-23` `lockOf`, `outOf`, `dll cfg name = sprintf "out/%s/netstandard2.0/%s.dll"`; `:351-352` `sprintf "out/Release/%s/%s.dll"` / `.xml` | |
| (library API) | `Locks.fs:788,791` `flatOutOf`, `runOf` = `sprintf "out/%s/%s"`, `sprintf "run/%s/%s/%s"` | the hermetic API's convention is "pass a path function" (`lockOf`, `outOf`, `runOf`) — a typed pattern can supply those functions |

**Lists for `need`** are built by comprehension, often as a cross product:
`build.fsx:50-54` (frameworks x {dll, xml} — the multi-file rule's outputs listed by hand),
`build.fsx:152` (deps of one lib), `build.fsx:106` / `build.fsc.fsx:73` (one per test job),
`build.fsc.fsx:56` and `docs/hermetic/lock.md:1644` (projects x configurations),
`samples/resources.fsx:48-49`. Every one of them re-spells a pattern.

## 4. Options

Each option shows the same three uses: **access** in the recipe, **generation** of one path,
**lists** for `need`. Running example: `build.fsx:140-163` and `build.fsc.fsx:47-68`.

### Option 0 — engine checks, no new syntax

Independent of any syntax, and cheap:
- `*..>` / `targets` compare the group sets of all patterns when the rule is created and fail
  ("pattern 2 has groups [fwk; name], pattern 1 [fwk; lib]"). Fixes the silent `out/a/.xml`.
- `getRuleMatch` on a name absent from the pattern: fail (or at least log a warning) instead of `""`.
  Breaking for anyone relying on `""` (one test does, by design).
- `getRuleMatches` without the numeric keys (or a separate `getRuleGroups`).

Safety: typos still found only when the recipe runs, but loudly. Generation: none.

### Option 1 — F# type provider `Xake.Pattern<"...">` (prototyped)

```fsharp
type Bin  = Pattern<"out/(fwk:*)/(lib:*).dll; out/(fwk:*)/(lib:*).xml">   // multi-file: ';'
type Lock = Pattern<"lock::(name:*)::(cfg:*)">
type Dll  = Pattern<"out/(cfg:*)/(fwk:*)/(name:*).dll">
```

Before (`build.fsx:50-54, 103, 140-152`):
```fsharp
let binaries name =
    [ for fwk in frameworks do for ext in ["dll"; "xml"] -> $"out/%s{fwk}/%s{name}.%s{ext}" ]
"build" <== List.collect (fun lib -> binaries lib.Name) libraries
targets ["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"] {
    let! framework = getRuleMatch "fwk"
    let! lib = getRuleMatch "lib" |> Recipe.map library
    ...
    do! need [for dep in lib.Needs -> $"out/%s{framework}/%s{dep}.dll"]
```
After:
```fsharp
"build" <== Bin.All(fwk = frameworks, lib = [ for l in libraries -> l.Name ])   // cross product, dll+xml each
Bin.Rule(fun m -> recipe {
    let lib = library m.lib
    ...
    do! need [ for dep in lib.Needs -> Bin.Make(fwk = m.fwk, lib = dep).dll ]
    // m.dll, m.xml: this target's outputs, typed
})
```
Phony and the `outOf`-style API (`build.fsc.fsx:47-68`):
```fsharp
Lock.Phony(fun m -> recipe {                       // a Phony member is not in the prototype
    let! d = Description.load (descriptionOf m.name)
    for r in d.ProjectRefs do do! need [ Lock.Path(name = r, cfg = m.cfg) ]
    ...
})
command "update-locks" { do! need (Lock.All(name = [ for n, _ in projects -> n ], cfg = ["Release"])) }
Dll.Rule(fun m -> recipe { do! Locks.compileAs (lockOf m.name m.cfg) m.fwk versions })
```

Members (all in the prototype unless noted): `Text`/`Texts` (the strings, for `..>` and `*..>`),
`Rule (T -> Recipe)`, `Get : Recipe<_, T>` (for use inside an existing `..>`/`target` body:
`let! m = Dll.Get`), `Make(g1 = .., g2 = ..) : T` (then `.dll`, `.xml`, `.fwk`), `Path(...) : string`
(first pattern), `All(g1 = [..], ...) : string list` (cross product, every output), `Glob(?g1, ...)`
(unbound groups -> their sub-masks: `Bin.Glob(lib = "Xake")` = `["out/*/Xake.dll"; "out/*/Xake.xml"]`, a
fileset mask for `getFiles`). `Dll.Path` is also a first-class `string * string * string -> string`.

What the compiler checks (all verified, experiment 03): property names (`m.fkw` -> FS0039 with
"Maybe you want: fwk"); multi-file group consistency (FS3033 with the missing/extra groups);
`Make`/`Path` require every group by name (FS0496 with the full signature); no `Make`/`Path` for
a pattern with an unnamed wildcard (FS0039).

### Option 2 — record-declared groups, `Pattern<'r>` (prototyped, experiment 02 D)

No type provider; the groups are declared once more, as a record, and compared with the pattern
string when the value is created (script start):

```fsharp
type BinG = { fwk: string; lib: string }
let Bin = Pattern<BinG> [ "out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml" ]

Bin.Rule(fun m -> recipe {
    let lib = library m.lib
    do! need [ for dep in lib.Needs -> Bin.Path { m with lib = dep } ]   // record copy-and-update
})
"build" <== [ for f in frameworks do for l in libraries do yield! Bin.Paths { fwk = f; lib = l.Name } ]
```
Access and generation are typed (by the record); the record/pattern agreement and multi-file
consistency are checked at script start with a precise message (`Pattern<BadM>: '...' has groups
["fwk"; "lib"], the record has fields ["fwk"; "library"]`). `{ m with lib = dep }` is a nice
fit for "same target, another library". Cost: the group list is written twice (record + pattern),
one record type per distinct group set (shared across patterns with the same groups: the record
for `out/(cfg:*)/(fwk:*)/(name:*).dll` serves `run/(cfg:*)/(fwk:*)/(name:*)` too). A vectorized
`All` needs a list-of-records input or a helper per arity; comprehensions are the natural form.

### Option 3 — a pattern value with the arity in its type, `Pattern2`, `Pattern3`, ... (prototyped, 02 C)

```fsharp
let Bin = patterns2 [ "out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml" ]
Bin.Rule(fun fwk lib -> recipe { ... need [ for dep in deps -> Bin.Path (fwk, dep) ] })
Bin.All ([ "net462"; "net8.0" ], [ "Xake" ]);  Bin.Glob (b = "Xake")
```
Arity is checked by the compiler at every use; the group count is checked at creation. Names are
**positional**: `fun lib fwk` or `Path (lib, fwk)` compiles and is silently wrong; `Glob (b = ...)`
names a slot, not the group. One type per arity (1..4 or 1..6).

### Option 4 — positional lambda, the roadmap's "Rules with arguments" (prototyped, 02 A)

```fsharp
"out/(fwk:*)/(lib:*).dll" ==> fun fwk lib -> recipe { ... }
["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(lib:*).xml"] ==> fun (fwk, lib) (dll, xml) -> recipe { ... }
```
Overloads by lambda arity work (F# picks the overload from the curried lambda's shape). Arity
checked when the rule is created. Swapped names bind wrongly, silently (verified). **No
generation story**: the pattern stays an inline string, so every `need` keeps formatting by hand.
It is Option 3 without the reusable value.

### Option 5 — bind by lambda parameter names, via reflection (prototyped, 02 B)

```fsharp
rule "out/(fwk:*)/(lib:*).dll" (fun lib fwk -> recipe { ... })   // order free, matched by name
```
F# keeps lambda parameter names in the closure's `Invoke` — verified in fsi and in a compiled
Release build, also for eta-expanded and nested `fun`. A typo fails at script start with the
group list. Fragile edges: a **tupled** lambda loses names (`tupledArg`); a named function
passed instead of a lambda exposes *its* parameter names (`framework`, `library` -> error); it
depends on a compiler implementation detail. No generation story (same as 4).

### Option 6 — rejected or weak alternatives (not prototyped)

- **Active patterns / map destructuring**: `let! (Groups ["fwk"; "lib"] [fwk; lib]) = ...` or the
  current one-liner `let! m = getRuleMatches() in let fwk, lib = m.["fwk"], m.["lib"]`
  (`docs/hermetic-guide.md:397`, adopted in the hermetic tracker): shorter, still stringly, same
  runtime failure. Active patterns cannot take a name list and return a tuple of matching arity
  in a typed way.
- **A pattern value with string accessors** (`p.Group "dir"`, `p.Make ["dir", "bin"]`): one
  definition point, but both access and generation are stringly again; only the multi-file check
  and "unknown group" errors at run time are gained.
- **String interpolation as the pattern** (`$"out/{fwk}/{lib}.dll"`): `fwk` must be in scope to
  compile; a `FormattableString` exposes the format and the *values*, not the names. It works for
  generation (which is what scripts already do by hand — the drift problem), not for matching.
- **Quotations** (a builder with `Quote` that reads `let! fwk = group` names from the CE's
  quotation): names are visible, but the recipe body would have to be evaluated from a quotation
  (slow, partial support for CEs). Same safety as Option 5, much heavier.
- **CE custom operations** (`target "p" { group fwk; ... }`): custom operations cannot introduce
  a variable whose name comes from the operation's argument; `MaintainsVariableSpace` gives no
  name binding from strings.

## 5. Generation: edge cases (apply to Options 1-3)

- **Unnamed wildcards** (`*`, `**`, `?`) have no name to take a value from. The prototype provides
  no `Make`/`Path`/`All` for such a pattern (matching still works) and refuses a multi-file
  pattern with unnamed wildcards outright (today's engine pairs them by position across
  patterns). Alternatives: forbid unnamed wildcards in typed patterns; or positional parameters
  `_1`, `_2`.
- **Constraints** (`(ext:c*)`): the prototype's `Make` substitutes, then matches the produced
  path against the pattern and requires the same group values back — `Src.Path(dir = "src",
  file = "a", ext = "fs")` fails at run time with the pattern and the values. A value containing
  `/` for a `*` group is caught the same way (`*` does not cross segments; `**` does).
- **Nested groups** (`(name:(plat:*)-a.ss)`): the inner group is determined by the outer one; a
  `Make` taking both is redundant and can disagree. Simplest: no `Make` for nested patterns.
- **Root and normalization**: matching resolves the pattern against `ProjectRoot`
  (`Path.matchGroups pattern projectRoot`, with `..` collapsed in the literal prefix). Generated
  paths in the prototype are **relative, as written in the pattern** — exactly what `need`
  accepts today (`need` resolves relative to the project root), so the round trip holds. A full
  path variant would need the context (a recipe), i.e. `Bin.FullPath(...) : Recipe<_, string>`.
- **Phony vs file**: the pattern text cannot say which; the engine decides by operator. A typed
  pattern needs two registration members (`Rule`/`Phony`) or two provided types.

## 6. Lists for `need`

| form | Option 1 (TP) | Option 2 (record) | Option 3 (arity) | today |
|---|---|---|---|---|
| comprehension | `[ for f in fws -> Dll.Path(fwk = f, lib = "core") ]` typed, names checked | `[ for f in fws -> Dll.Path { fwk = f; lib = "core" } ]` typed | `[ for f in fws -> Dll.Path (f, "core") ]` arity only | `$"out/{f}/core.dll"` |
| vectorized | `Bin.All(fwk = fws, lib = libs)`: cross product, all outputs flattened (prototyped) | needs a records list; no natural `All` | `Bin.All (fws, libs)` positional | hand cross product |
| partial application | no `With(name = ..)` returning a typed function of the rest (needs 2^n provided types); a lambda does it: `fun f -> Dll.Path(fwk = f, lib = "core")` | `{ m with lib = dep }` | lambda | lambda |
| glob of existing files | `Bin.Glob(lib = "Xake")` -> `["out/*/Xake.dll"; ...]` (prototyped); could return a `Fileset` | possible (option fields) | `Glob (b = ..)` positional | hand mask |

Cross product vs zip: `All` is a cross product (the common case: frameworks x projects). Zipped
pairs (`[("Xake", "src/core"); ...]`) are a comprehension over `Make`, which keeps names.
Glob is a natural third use of the same value: the pattern is the layout, so "what exists for
lib = Xake" is the same layout with one group bound. It returns Ant masks today; returning
`Fileset` and also getting the bound groups back per file (glob + match) would be the
fully typed form.

## 7. Type provider feasibility

Verified (experiment 03):
- **Erased, same-assembly provider** built against `FSharp.TypeProviders.SDK` 8.11.0 sources
  (`ProvidedTypes.fs`/`.fsi` compiled in; the package also ships a dll, unused), netstandard2.0,
  **FSharp.Core pinned to 8.0.100** (the SDK package needs >= 4.7.2): builds clean in 6.2 s, dll 1.2 MB.
- Erased types work with generics of Xake's types: `Recipe<ExecContext, T>` (for `Get`) and
  `T -> Recipe<ExecContext, unit>` (for `Rule`) via `ProvidedTypeBuilder.MakeGenericType`; F#
  infers `m`'s provided type inside `Rule(fun m -> ...)`.
- Works in `dotnet fsi` (F# 10, SDK 10.0.402) **and in the SDK 8 fsi** (F# 8); in a compiled
  net8.0 project; and through `#r "nuget: ..."` from a local feed (a TP in `lib/netstandard2.0`).
- Inline use without a `type` line works: `Pattern<"gen/(x:*)/(y:*).txt">.Rule(fun m -> ...)`.
- **Static arguments must be constants**: `[<Literal>] let P = "out/pkg/" + Pkg + "/" + Pkg +
  ".(ver:*).nupkg"` then `Pattern<P>` works; `+` inside the type argument is a syntax error; an
  interpolated `[<Literal>]` is FS0267. So `build.fsx:180`'s `$"out/pkg/%s{xakePackage}/..."`
  needs `xakePackage` turned into a `[<Literal>]`. Patterns computed from run-time data
  (`DotnetTasksTests.fs:662` builds the rule from a `targets` list) cannot be typed this way —
  the string API stays as the escape hatch.
- Can it check multi-file consistency? **Yes**, at compile time, with the missing/extra groups in
  the message. It can also check unnamed-wildcard counts, nested groups, malformed patterns
  (anything `Path.matchGroups` would reject) — the regex construction can run at design time.

Assessed, not tested:
- **Packaging into the Xake nupkg** (net462 + netstandard2.0). The runtime half is ~40 lines that
  belong in `Xake.dll` (both TFMs). The design-time half is netstandard2.0 (the compiler loads it,
  and the compiler runs on .NET 8+ whatever the target TFM is), shipped as
  `typeproviders/fsharp41/netstandard2.0/Xake.DesignTime.dll` with
  `[<assembly: TypeProviderAssembly("Xake.DesignTime")>]` in `Xake.dll`. That keeps the 1.2 MB of
  ProvidedTypes out of `Xake.dll` and out of every build's runtime. The alternative — a separate
  `Xake.Patterns` package, as prototyped — is simpler to start with and opt-in.
- **Build time**: compiling `ProvidedTypes.fs` (870 KB of source) adds a few seconds to whichever
  project holds it; the TP itself runs per distinct `Pattern<...>` at compile time (regex parsing,
  negligible).
- **IDE**: FSharp.Data and FSharp.Text.RegexProvider (`Regex<"(?<name>...)">`, the closest
  precedent: typed regex groups) are erased TPs of the same kind and work in Ionide, Rider and VS,
  including `.fsx`. Not tested here (no GUI).
- **Maintenance**: one vendored file updated by bumping a package; provider errors surface as
  FS3033 text; the provider is small (~120 lines). Debugging a provider needs a second compiler
  instance. Erased types do not exist at run time, so a library cannot take "any pattern" as a
  parameter — libraries (e.g. `Locks.compileAs`, `outOf`) keep taking strings or path functions,
  which `Dll.Path` supplies as a first-class function.
- **Generative instead of erased**: not needed — nothing has to exist at run time beyond a
  `Map<string,string>`; generative TPs would add real types to the user's assembly and do not work
  as smoothly in fsi.

## 8. Comparison

Error timing: **C** compile time (editor squiggle), **S** script start (rule creation, before
any target runs), **R** when the recipe runs, **—** never (silent).

| option | access safety | typo in a name | multi-file consistency | generation (single path) | lists for `need` | readability | engine change | backward compat | cost |
|---|---|---|---|---|---|---|---|---|---|
| today | strings | — (`""`) | — (`out/a/.xml`) | by hand, duplicated | by hand | verbose | — | — | — |
| 0 engine checks | strings | R (loud) | S | — | — | same | small | `""` semantics change | small |
| 1 TP `Pattern<"...">` | typed props | **C** | **C** | **typed, named, all groups required (C)**; constraints R | `All` cross product, `Glob`, comprehension (typed) | good; pattern hoisted into a `type` line (or inline) | none (library on top); optional `Phony` | full (strings stay) | medium: provider + packaging |
| 2 record `Pattern<'r>` | typed fields | **C** in the recipe; record vs pattern **S** | **S** | typed by record; `{ m with .. }` | comprehension | good; groups written twice | none | full | small |
| 3 `PatternN` value | positional | swap — | **S** | arity C, names — | `All`/`Glob` positional | fair | none | full | small |
| 4 positional lambda | positional | swap — | **S** | none | none | good for access only | none/small | full | small |
| 5 lambda names | by name | **S** | **S** | none | none | best for access | none | full | small, fragile |
| 6 string accessors | strings | R | S | stringly | stringly | fair | none | full | small |

## 9. Other tools

- **Shake**: `"*.o" %> \out -> ...`, multi-output `["*.o", "*.hi"] &%> \[o, hi] -> ...` — outputs bound
  positionally as a list pattern (partial, like `let! [a; b] = getTargetFiles()`); no named
  captures; inputs are derived with path functions (`-<.>`, `takeBaseName`). Generation is
  plain path manipulation; nothing ties it to the rule.
- **FAKE**: targets are names, files are globs; no pattern captures at all.
- **FSharp.Text.RegexProvider**: `type R = Regex<"(?<year>\d+)">`, `R().TypedMatch(s).year.Value` —
  erased TP over a literal pattern; the precedent for Option 1, generation not offered.

## 10. Leaning (labelled as such — not a decision)

- **Option 0 regardless**: the multi-file group check at rule creation and a loud unknown-group
  `getRuleMatch` are cheap, catch real silent failures, and help every script, typed or not.
- **Option 1 (TP) is the only option that gives compile-time checks for names, multi-file
  consistency and generation together**, and generation is where it pays most: `binaries`,
  `packagePath`, `outOf`, `$"lock::{r}::{cfg}"` disappear into the pattern. The prototype shows it
  works in fsi (F# 8 and 10), compiled, and via `#r "nuget:"`; the remaining risk is packaging
  into `Xake` and IDE behaviour, both well-trodden by FSharp.Data.
- **Option 2 (record) is the strong non-TP fallback**: about 80% of the value (typed access,
  typed generation, precise errors at script start), no tooling, ~40 lines. It could ship first
  and the TP later; the two can share the runtime.
- Options 3-5 solve access only partly and have no (or only positional) generation; 4 is the
  roadmap item, which this research would supersede.

## 11. Open questions for the user

1. Ship the TP inside the `Xake` package (design-time dll under `typeproviders/`) or as an opt-in
   `Xake.Patterns` package first?
2. Multi-file syntax: one static string with `;` (prototyped), separate static parameters
   (`Pattern<"a.dll", "a.xml">`, a fixed maximum), or a separate `Patterns<...>` type?
3. Output property names: by extension (`m.dll`, `m.xml` — prototyped) breaks for two outputs with
   the same extension or for `hw.runtimeconfig.json` (`m.json`); alternatives `Out1`/`Out2`, or a
   label syntax inside the pattern?
4. Names of the generation members: `Make` (typed value) / `Path` (string) / `All` / `Glob` — or
   `Format`, `Target`, `For`? For phony patterns `Path` reads oddly.
5. Phony: `Pattern<...>.Phony(...)` next to `.Rule(...)`, or a separate provided type
   (`Command<"lock::(name:*)::(cfg:*)">`)?
6. Unnamed wildcards in typed patterns: allow for matching only (prototyped), forbid, or expose
   positionally?
7. Should `getRuleMatch` on an unknown group start failing (Option 0), given the `""` contract
   and its test?
8. `Glob`: return Ant masks, a `Fileset`, or a recipe yielding `(T * File) list` (glob + match back)?
9. Should the hermetic API's path-function parameters (`lockOf`, `outOf`, `runOf`) be fed from
   patterns in the build scripts, and is the `outOf cfg fwk name </> assembly.dll` composition
   (`Locks.fs:207,230`) worth expressing as one pattern?
10. Is a `[<Literal>]` requirement for constants inside patterns (`build.fsx:180`) acceptable in
    scripts?
11. Does this replace the roadmap's "Rules with arguments" entry?
