# Experiments: typed rule matches

Throwaway prototypes for [`../research.md`](../research.md). Nothing here is referenced by the
build. Run on macOS, SDK 8.0.426 and 10.0.402 installed (`dotnet fsi` resolves to F# 10 via
`global.json` `rollForward: latestMajor`; experiment 03 was also run under the SDK 8 fsi).

Prerequisite for all: `dotnet build src/core -c Release` (the experiments `#r` the built
`src/core/bin/Release/netstandard2.0/Xake.dll`). Each `output.txt` is the captured run, with the
`XAKE build tool` banner lines and blank lines removed.

## 01-current-behaviour — what today's API does

`dotnet fsi current.fsx` (in `01-current-behaviour/`). Output: `01-current-behaviour/output.txt`.

| case | result |
|---|---|
| `getRuleMatch "fkw"` (typo for `fwk`) | returns `""`, no error, no log; the recipe would run `--framework ` with an empty value |
| `getRuleMatches ()` | the map also holds `"0"` (full path) and `"1"`, `"2"`, ... (every `*`, including the ones inside named groups) |
| `["out/(fwk:*)/(lib:*).dll"; "out/(fwk:*)/(name:*).xml"] *..>` | the second target is generated as `out/a/.xml` — silently, the missing group becomes `""` |
| multi-file rule, the `.xml` requested | groups come from the pattern that matched; targets are both files in declaration order |

## 02-positional — non-type-provider options as a library over today's engine

`dotnet fsi positional.fsx` (in `02-positional/`). Output: `02-positional/output.txt`.

- **A. Positional lambda via arity overloads** (`Rule.file (pattern, fun fwk lib -> ...)`):
  works; F# resolves the overload by lambda arity. Arity mismatch fails when the rule value is
  created (script start). **Swapped names (`fun lib fwk`) compile and bind wrongly, silently.**
  Multi-file form `Rule.files ((p1, p2), fun (fwk, lib) (dll, xml) -> ...)` works.
- **B. Bind by lambda parameter names (reflection)**: names are read from the closure's
  `Invoke`; binding by name makes order irrelevant; a typo fails at rule creation with the list
  of groups. A named function passed instead of a lambda exposes *its* parameter names.
  `names/` (a compiled net8.0 exe, `dotnet run -c Release` and `-c Debug`): names survive Release
  for lambdas, eta-expanded lambdas, nested `fun`, and named functions; a **tupled** lambda
  `fun (fwk, lib) -> ...` gives `tupledArg` — names lost.
- **C. `Pattern2` value (arity in the type)**: `Dll.Path ("net8.0", "Core")`, `Lib.All`,
  `Lib.Glob (b = "Xake")`, `Dll.Rule (fun fwk lib -> ...)`. Group count checked at creation;
  every call arity-checked by the compiler; names are positional (`Glob (b = ...)` names the
  *slot*, not the group).
- **D. Record-declared groups** (`type DllM = { fwk: string; lib: string }`,
  `Pattern<DllM> [...]`): the record and the pattern string are compared at creation (a mismatch
  names both sides); access `m.fwk` and generation `Paths { fwk = ...; lib = ... }` are typed by
  the record.

## 03-type-provider — an erased `Xake.Pattern<"...">` provider

`Xake.Patterns/` is a same-assembly erased type provider (runtime + design time in one
netstandard2.0 dll): `ProvidedTypes.fs`/`.fsi` compiled in from the `FSharp.TypeProviders.SDK`
8.11.0 package's `src/` (the package's own dll is excluded), FSharp.Core pinned to 8.0.100,
`Xake.dll` referenced by path. Build: `dotnet build -c Release` in `Xake.Patterns/` — **clean
build 6.2 s, output dll 1.2 MB** (almost all of it ProvidedTypes).

Provided members of `Pattern<"p1; p2; ...">` (erased to `Map<string,string>`): static `Text`,
`Texts`, `Get : Recipe<ExecContext, T>`, `Rule (T -> Recipe<...>)`, `Make (g1, g2, ...) : T`,
`Path (g1, ...) : string`, `All (g1: string list, ...) : string list` (cross product, all
outputs), `Glob (?g1, ...) : string list` (unbound groups -> their sub-masks); instance: one
`string` property per group, one path property per pattern named after its extension
(`m.dll`, `m.xml`, else `Out<i>`).

| run (in `consumer/`) | result |
|---|---|
| `dotnet fsi use.fsx` (output: `consumer/output.txt`) | everything works: matching (`Rule`, `Get`), multi-file typed outputs, `Make`/`Path`, `All`, `Glob`, `(ext:c*)` constraint rejected at run time for `ext = "fs"`, `Dll.Path` usable as a first-class `string * string -> string` |
| `dotnet /usr/local/share/dotnet/sdk/8.0.426/FSharp/fsi.dll use.fsx` | same output under F# 8 fsi |
| `dotnet fsi inline.fsx` | `Pattern<"gen/(x:*)/(y:*).txt">.Rule(fun m -> ...)` inline in `rules [...]` works |
| `dotnet run -c Release` (`Consumer.fsproj`, net8.0, FSharp.Core 8.0.100) | works in a compiled project |
| `dotnet pack` of `Xake.Patterns` into `/tmp/xake-tp-feed`, then `dotnet fsi use-nuget.fsx` (`#i "nuget: /tmp/xake-tp-feed"`, `#r "nuget: Xake.Patterns, 0.0.1-exp"`) | works: a TP in `lib/netstandard2.0` loads from a `#r "nuget:"` reference |
| `neg-typo.fsx` (`m.fkw`) | `error FS0039: The type 'Pattern<out/(fwk:*)/(lib:*).dll>' does not define a field, constructor, or member named 'fkw'. Maybe you want one of the following: fwk` |
| `neg-multi.fsx` (`...(lib:*).dll; ...(name:*).xml`) | `error FS3033: The type provider ... reported an error: Pattern 'out/(fwk:*)/(name:*).xml': groups ["fwk"; "name"] differ from the first pattern's ["fwk"; "lib"] (missing ["lib"], extra ["name"])` |
| `neg-make.fsx` (`Dll.Path(fwk = "net8.0")`) | `error FS0496: The member or object constructor 'Path' requires 1 argument(s). The required signature is 'Pattern<...>.Path(fwk: string, lib: string) : string'.` (the count in the message is odd, the signature is right) |
| `neg-wild.fsx` (`Pattern<"out/*/(lib:*).dll">.Path`) | `error FS0039: ... does not define ... 'Path'` — by design, no generation for unnamed wildcards |
| `neg-interp.fsx` | `Pattern<"a" + Pkg + ...>` inline: `FS0010 Unexpected infix operator in type arguments`; a `[<Literal>]` built by `+` from other literals: **works**; `[<Literal>] let I = $"out/{Pkg}..."` (tried separately): `FS0267 This is not a valid constant expression` |

Not tested here (no GUI): Ionide / Rider / Visual Studio. FSharp.Data and
FSharp.Text.RegexProvider are erased TPs of the same shape and work in all three, in `.fsx` too.

## 04-command-type — a second provided type for phony rules, `Xake.Command<"...">`

For [`../phony-types.md`](../phony-types.md) (brief decision 5, the cost question). A copy of
03's `Xake.Patterns` (03 is untouched), with `Provider.fs` split into shared helpers (erased type,
group properties, `Text`, `Get`, `Make`, cross product) and two roots: `Pattern<>` (unchanged
members) and `Command<"name pattern">` — one static parameter; `Rule` (a `PhonyRule`, `=>`
semantics), instance `Name` (the matched name, the engine's `"0"` group) and group properties,
`Make`, `Target(g..) : string`, `For(g: list..) : string list`, `Need(g..) : Recipe<unit>`; no
`Texts`, no output properties, no `Glob`. Size: `Provider.fs` 119 -> 145 lines (shared 40,
Pattern-only 53, Command-only 29), `Runtime.fs` 63 -> 83. No engine change.

Build: `dotnet build -c Release` in `04-command-type/Xake.Patterns/` (6 s, 0 errors).

| run (in `04-command-type/consumer/`) | result |
|---|---|
| `dotnet fsi use.fsx` (output: `consumer/output.txt`) | `Lock.Target(name = "Xake", cfg = "Release")` = `lock::Xake::Release`; `Lock.For` cross product; `Make(..).Name`; `Test.Target(project = "a/b", ..)` fails at run time (`*` does not cross `/`); `Lock.Target` as a first-class `string * string -> string`. Engine run: `Lock.Rule` with `Lock.Need` on its references (`Core` before `App`, both configs), `"update-locks" <== Lock.For(..)`, `Test.Rule` needing `TestBuild.Need` and a `Pattern<>` file in the same recipe — generated names resolved as phony, the file as a file; `Command<"testsuite-*">.Rule` matches `testsuite-007`, `m.Name` = `testsuite-007` |
| `neg-glob.fsx` (`Lock.Glob(..)`) | `error FS0039: The type 'Command<lock::(name:*)::(cfg:*)>' does not define a field, constructor, or member named 'Glob'.` |
| `neg-out.fsx` (`m.Out0`) | `error FS0039: ... does not define a field, constructor, or member named 'Out0'.` |
| `neg-typo.fsx` (`m.cgf`) | `error FS0039: ... named 'cgf'. Maybe you want one of the following:` (`cfg`) |
| `neg-target.fsx` (`Lock.Target(name = "Xake")`) | `error FS0496: ... The required signature is 'Command<...>.Target(name: string, cfg: string) : string'.` |
| `neg-wild.fsx` (`Command<"testsuite-*">.Target()`) | `error FS0039: ... does not define ... 'Target'` — as 03, no generation with a bare wildcard (positional `_n` is decision 6, not prototyped) |

## 05-ast-reuse — the provider on the engine's `PatternAst`, split into runtime + design-time dlls

For [`../ast-reuse.md`](../ast-reuse.md). Experiment 03's provider with its own regexes replaced
by `PatternAst` cut out of the real `src/core/Path.fs` (`sh slice-ast.sh` writes
`generated/PatternAst.fs`: the merged module verbatim, dedented, with a top-level header) plus the
proposed generation functions (`PatternAstGen.fs`: `unparse`, `groupNames`, `wildcards`, `format`,
`glob`). Three projects, all netstandard2.0, FSharp.Core 8.0.100:

- `Runtime/` — stand-in for the runtime half that would live in Xake.dll (`PatternRuntime`:
  `group`, `format`, `make`, `all`, `globMasks`, `glob : Fileset`, `get`, `rule`); carries
  `TypeProviderAssembly("Xake.Patterns.DesignTime")`.
- `DesignTime/` — the provider (`Pattern<"p1", "p2", ...>`, separate static parameters, bare
  wildcards as `_1`, `_2`, ...), referencing Xake.dll and the runtime dll. `-p:AstVia=ivt`
  (default): the AST comes from the runtime dll through `InternalsVisibleTo`; `-p:AstVia=link`: the
  same two files compiled into the design-time dll too. After build it copies itself to
  `Runtime/bin/typeproviders/fsharp41/netstandard2.0/`. Clean build 6 s, dll 1.2 MB.
- `Pkg/` — stand-in for `src/dotnet`: packs the runtime into `lib/` and the design-time project
  (`IsFSharpDesignTimeProvider="true"`, `FSharpToolsDirectory=typeproviders`) into
  `typeproviders/fsharp41/netstandard2.0/`.

| run (in `05-ast-reuse/`) | result |
|---|---|
| `sh slice-ast.sh; cd DesignTime; dotnet build -c Release`, then `dotnet fsi use.fsx` in `consumer/` (output: `consumer/output.txt`) | everything works: `Format`/`Make` (constraint `(ext:c*)` and a `/` in a `*` value rejected by re-matching with `Path.matchGroups`), positional `Pos.Format(name = "core", _1 = "a", _2 = "7")` = `gen/a/core-7.txt`, `**` as `_1`, nested groups, `All`, `GlobMasks` (`Nest.GlobMasks()` = `*/*-a.ss`), `Glob() : Fileset`, `Rule`/multi-file/positional match (`_2` read under regex number `"3"`), `Get` |
| same with `dotnet build -c Release -p:AstVia=link --no-incremental` | output identical (`diff` empty) |
| the SDK 8 fsi (`dotnet .../sdk/8.0.426/FSharp/fsi.dll use.fsx`) | identical |
| first try, provided types declared in the design-time assembly without `assemblyReplacementMap` | `error FS1108: The type 'Pattern,pattern="..."' is required here and is unavailable. You must add a reference to assembly 'Xake.Patterns.DesignTime, ...'` — fixed by the map |
| `consumer/neg.fsx` (one line at a time) | multi-file group mismatch: `FS3033 ... groups ["fwk"; "name"] differ from the first pattern's ["fwk"; "lib"] (missing ["lib"], extra ["name"])`; `out/[(lib:*).dll`: `FS3033 ... does not compile to a regex: Invalid pattern '^out/[(?<lib>([^/\\]*))\.dll$' at offset 22. Too many )'s.`; bare wildcards in a multi-file pattern: `FS3033 ... may not use unnamed wildcards` |
| `consumer/edge.fsx` (`Pattern<"lit(x)/*.txt">`, target `litx/a.txt`) | matches: the literal `(x)` is a regex group; matches `[("1","x"); ("2","a")]`, `_1 = a` (the `wildcards` count accounts for it) |
| `dotnet pack` in `Pkg/` **before** the copy step existed | `error FS3031: ... refers to a designer assembly 'Xake.Patterns.DesignTime' which cannot be loaded from path '.../Runtime/bin/Release/netstandard2.0/Xake.Patterns.DesignTime.dll'` — any compilation against the runtime dll fails without the design-time dll beside it |
| `dotnet pack -c Release -o /tmp/xake-tp-feed-05` in `Pkg/` | nupkg: `lib/{net462,netstandard2.0}/` (runtime, Xake.dll, Pkg dll), `typeproviders/fsharp41/netstandard2.0/` (DesignTime dll, runtime dll, Xake.dll, Xake.xml); warnings `NU5100`, `NU5118` (suppressed in the fsproj) |
| `dotnet fsi use-nuget.fsx` (`#r "nuget: Xake.PatternsExp, 0.0.1-exp"`), also under the SDK 8 fsi | works: the compiler finds the design-time dll in `typeproviders/fsharp41/netstandard2.0` |
| `consumer/probe.fsx` (runtime `#r` from `Runtime/bin/Release/netstandard2.0`, design-time only in `Runtime/bin/typeproviders/fsharp41/netstandard2.0`) | works (output identical); with that folder moved away: `FS3031` — the compiler probes `<runtime dir>/../../typeproviders/fsharp41/netstandard2.0/` |

## 06-separate-dll — `TypeProviderAssembly` on a separate `Xake.Patterns.dll`, not on `Xake.dll`

For brief "Under discussion" (the FS3031 risk in [`../ast-reuse.md`](../ast-reuse.md) section 6). Copy of 05
restructured; all netstandard2.0, FSharp.Core 8.0.100; the real `src/core` build of `Xake.dll` is used unchanged.

- `Core/` (`XakeLike.dll`) — stand-in for the additions to `Xake.dll`: `PatternAst` and `PatternAstGen` as **public**
  modules (05's generated file with `internal` dropped; decision 13).
- `Runtime/` (`Xake.Patterns.dll`, namespace `Xake.Patterns`) — 05's `PatternRuntime`, references `Xake.dll` + `XakeLike.dll`,
  carries `TypeProviderAssembly("Xake.Patterns.DesignTime")`. No `InternalsVisibleTo`.
- `DesignTime/` — 05's provider (replacement map now `Xake.Patterns.DesignTime -> Xake.Patterns`); no copy-back step.
- `Pkg/` — stand-in for `src/dotnet`, package `Xake.SepDllExp 0.0.1-exp06`; `app/` — compiled net8.0 consumer.
- `make-stage.sh` lays out `stage/{with,without,xake-only}` (loose dlls); `consumer/*.fsx`; outputs in `*output.txt`,
  `nupkg-layout.txt`, `stage-layout.txt`.

| hypothesis / run | result |
|---|---|
| H1 `consumer/h1-xake-only.fsx`: `#r` only `Xake.dll`, no design-time dll anywhere (`h1-output.txt`) | **pass**: compiles, rule runs; `TypeProviderAssembly` attributes on `Xake.dll` = 0. (Trivially so: the attribute is simply not there. The in-repo corollary: every project that references only `Xake.dll` — engine, tests, `Xake.Dotnet`, hermetic build — never needs the design-time dll.) |
| H5 no `InternalsVisibleTo` (`Core/` public modules; `Runtime`/`DesignTime` reference them as plain public API) | **pass**: both build, provider works (`use-output.txt`) |
| positive control `consumer/use.fsx` (= 05's `use.fsx`: `Format`/`Make`, constraints, positional `_n`, `All`, `GlobMasks`, `Glob`, `Rule`, `Get`) against `stage/with` (design-time folder at `../../typeproviders/fsharp41/netstandard2.0` of `lib/netstandard2.0`), SDK 10 and SDK 8 fsi | works, output identical to 05's |
| first try, `stage/with` whose typeproviders folder held only the design-time dll | `error FS3033: The type provider ... reported an error: Could not load file or assembly 'XakeLike ...'` — the typeproviders folder must hold the **whole design-time output folder** (`Xake.dll`, `Xake.Patterns.dll`, helpers), which is what the F# SDK's `PackageFSharpDesignTimeTools` packs |
| H4 `consumer/h4-without.fsx` (`stage/without`: `Xake.dll` + `Xake.Patterns.dll`, no design-time dll), both SDKs (`h4-output.txt`) | **pass** (FS3031 as expected), reported at line 7, the `#r` of `Xake.Patterns.dll`, not line 6 (`Xake.dll`): `error FS3031: The type provider '.../stage/without/lib/netstandard2.0/Xake.Patterns.dll' reported an error: Assembly attribute 'TypeProviderAssemblyAttribute' refers to a designer assembly 'Xake.Patterns.DesignTime' which cannot be loaded from path '.../stage/without/lib/netstandard2.0/Xake.Patterns.DesignTime.dll'. The exception reported was: System.IO.FileNotFoundException - Could not load file or assembly '...Xake.Patterns.DesignTime.dll'. The system cannot find the file specified.` (fsi then adds `error FS0193: internal error`) |
| H4 in the build: `Pkg` first referenced `Xake.Patterns.fsproj` for compilation (as 05's `Pkg` did) | `error FS3031` for both TFMs (same text, path `Runtime/bin/Release/netstandard2.0/...`): any project that **references `Xake.Patterns.dll`** needs the design-time dll. Fix: `ReferenceOutputAssembly="false"` (build order only) and pack the dll by hand with `<None Pack PackagePath="lib/...">`. Also `warning FS3005` in the design-time project itself (references the runtime dll, no provider class in it) — cosmetic, `NoWarn` it |
| `dotnet pack -c Release -o <feed>` in `Pkg/` (`pack-output.txt`, `nupkg-layout.txt`) | `lib/{net462,netstandard2.0}/` `Xake.dll`, `Xake.Patterns.dll`, `XakeLike.dll`, `Xake.SepDllExp.dll`; `typeproviders/fsharp41/netstandard2.0/` `Xake.Patterns.DesignTime.dll`, `Xake.Patterns.dll`, `Xake.dll`, `XakeLike.dll`; no NU5100/NU5118 this time (suppressed in the fsproj) |
| H2 `consumer/use-nuget.fsx` (`#i` the feed, `#r "nuget: Xake.SepDllExp, 0.0.1-exp06"`), SDK 10 and SDK 8 fsi (`use-nuget-output.txt`) | **pass**: `All`, positional `Format`, `GlobMasks`, multi-file `Rule` with typed `m.fwk`/`m.lib`/`m.dll` |
| H3 `app/` (net8.0, `PackageReference` to the nupkg, `dotnet run -c Release`, `h3-output.txt`) | **pass**: compiled typed groups, positional `Format`, `Glob`, `Rule` |

Not covered: `Command<>` (not in 05's provider; 04's is separate), IDE completion. Conclusions: the attribute on a
separate dll behaves exactly as described, and the cost moves to one rule — **only the packing project may
not compile against `Xake.Patterns.dll`**, and nothing else in the repo should reference it except scripts/user projects
through the package. In the real repo `Xake.Patterns.dll` would be its own project (`src/patterns`) referencing `src/core`;
the design-time project references both; `src/dotnet` packs both plus the design-time folder.

## 07-ce-syntax — lower-noise rule syntax (`Dll.rule { }`, `Dll.Pattern ..>`, `Dll.Rule <|`)

For [`../ce-syntax.md`](../ce-syntax.md). A copy of 05 (`slice-ast.sh`, `PatternAstGen.fs`,
`Runtime/`, `DesignTime/`; 05 is untouched) with 04's `Command<>` ported onto the AST and the
members the forms need: static `Pattern` (single-output types and `Command<>` only) and `Patterns`
(operands for `..>`/`*..>`/`=>`), `Get` checked at run time against the rule's matches
(`PatternRuntime.getFor`), and a static property `rule : TypedRuleBuilder<T>` — a generic builder
class of the runtime dll (inherits `RecipeBuilder`, adds `Bind(Matched, T -> Recipe)`, `Run` makes
the `FileRule`/`MultiFileRule`/`PhonyRule`), instantiated at the provided type itself;
`matched` is the marker value (`namespace Xake`). Generation cut to `Format` and `Target`.

Build: `sh slice-ast.sh; cd DesignTime; dotnet build -c Release` (7 s, 0 errors).

| run (in `07-ce-syntax/consumer/`) | result |
|---|---|
| `dotnet fsi forms.fsx` (output: `consumer/output.txt`) | three real rules (build.fsc.fsx's `out/(cfg:*)/(fwk:*)/(name:*).dll`, build.fsx's dll+xml multi-file rule, build.fsx's `test::(project:*)/(slice:*)` needing `test-build::..`) in forms A (`Dll.Pattern ..> recipe { let! m = Dll.Get ..}`), B (`Dll.rule { let! m = matched ..}`), D (`Dll.Rule <\| fun m -> recipe {..}`) and 05's `Dll.Rule(fun m -> ..)`: all compile and build the same; a mixed `rules [...]` with A, B, D, the string API, `command` and `target` builds `main` |
| the SDK 8 fsi on `forms.fsx` | same lines (one pair of independent targets in another order) |
| `dotnet fsi form-c.fsx` | form C: `rule Dll.rule { .. }` in `xakeScript`: `FS3099 'rule' is used with an incorrect number of arguments ... Expected 1 argument(s), but given 2`; `rule (Dll.rule { .. })` and `rule (Test.Rule <\| fun m -> ..)` work |
| `sh neg/run-neg.sh` (14 cases from `neg/cases.fsx`, output: `neg/output.txt`) | `m.fkw` in A, B, D: `FS0039 ... does not define ... 'fkw'. Maybe you want one of the following: fwk`; `m.pdb` on the multi-file type in A, B: FS0039; `m.slcie` in `Test.rule`: FS0039 with `slice`; `Lib.Pattern ..>`: FS0039 `Maybe you want ... Patterns`; `Test.Pattern ..> recipe {..}` (phony name, file operator): compiles to `FileRule "test::..."`; `matched` in a plain `recipe`: `FS0072 The type of this expression could not be inferred`, in a nested `recipe` inside `Dll.rule`: `FS0041 No overloads match for method 'Bind'. Known types of arguments: Matched * ...`; `let! m = Lib.Get` inside a `Dll` rule (A and B): run time `XakeException "[\|...\|].Get: the rule being run has no groups [\|"lib"\|] (it has [..; "cfg"; "fwk"; "name"]); a Get of another pattern than the rule's?"`; `Dll.rule { }` without `matched` and a one-line `Dll.rule { let! m = matched in .. }`: run |

## 08-consolidated — the decided design end to end (template for `src/`)

### Names first: provided `Target<...>` next to the engine's DU `Xake.Target`

Asked mid-experiment (the user's rename `Pattern<>` -> `Target<>`, `Command<>.Target` ->
`Command<>.Pattern`); the prototype is built with the new names. The DU is
`Xake.DomainTypes.Target` (an `[<AutoOpen>]` module, so `open Xake` brings `Target`, `FileTarget`,
`PhonyAction`). Checked in `ns-check/cases.fsx` (`sh ns-check/run.sh`: every case with the SDK 10 and
the SDK 8 fsi, identical results, `ns-check/output.txt`), in a compiled net8.0 project against the
build output (`ns-check/compiled`, `sh run.sh`, `compiled/output.txt`) and through the package
(`app/run.sh`, `-p:NsCase=A`, `app/build-output.txt`) — all three agree:

| opens | `type Bin = Target<"...">` | DU as a type (`t: Target`, `Target list`) | union cases `FileTarget f`, `PhonyAction a` |
|---|---|---|---|
| `open Xake` then `open Xake.Patterns` | provided type, works | **fails**: `error FS3081: The static parameter 'pattern' of the provided type or method 'Target' requires a value. Static parameters to type providers may be optionally specified using named arguments, e.g. 'Target<pattern=...>'.` | work |
| `open Xake.Patterns` then `open Xake` | **fails**: `error FS0033: The non-generic type 'Xake.DomainTypes.Target' does not expect any type arguments, but here is given 1 type argument(s)` (the compiled project adds `FS0039: The type 'Object' does not define a field, constructor, or member named 'Format'.`) | works | work |
| only `open Xake.Patterns` | provided type, works | `Xake.Target` fails: `FS0039: The type 'Target' is not defined in 'Xake'.` — **not new**: `Xake.Target` never resolves, even with no provider referenced; the full name is `Xake.DomainTypes.Target` | `Xake.FileTarget` fails likewise (`Xake.DomainTypes.FileTarget` works) |
| only `open Xake` | `FS0033` as above; `Patterns.Target<"...">` works | works | work |

The last `open` takes the name `Target` outright; F# does not tell the two apart by "takes static
arguments or not". Workarounds that work (both SDKs and compiled): with `open Xake; open Xake.Patterns`,
write the DU as `DomainTypes.Target` (case w1); or abbreviate it before the second open,
`type BuildTarget = Target` (w3); or open `Xake.Patterns` first and qualify the provided type,
`Patterns.Target<"...">` / `Xake.Patterns.Target<"...">` (w2). Code that only matches
`FileTarget`/`PhonyAction` without annotating the type is unaffected (w4). Scripts that annotate the
DU today: `samples/delegated.fsx:38`, `samples/delegated-tests.fsx:43` (`targets: Target list`);
`src/dotnet/DotnetTasks.fs:77` too, but it never opens `Xake.Patterns`.
**Verdict: the rename works if the documented order is `open Xake` then `open Xake.Patterns` and the
rare DU annotation is written `DomainTypes.Target`.** Any other combination fails with one of the two
errors above, neither of which names the cause (FS3081 talks about a missing static argument).
The prototype's consumer (`consumer/Rules.fs`, `app/Program.fs`) uses that order.

`Command<>.Pattern(...)` (renamed from `Target`) returns the **concrete action name**
(`TestBuild.Pattern(project = "Xake")` = `"test-build::Xake"`, what `need` takes). It reads as if it
returned the pattern text, which it does not. `Target<>`'s string generator is `Format`; suggest
`Format` on both types (`Target<>.Format` = the first output's path, `Command<>.Format` = the
name), or keep `Command<>.Target`.

### Layout (mirrors `src/`)

| folder | would be | what |
|---|---|---|
| `engine.patch` (`sh make-engine.sh` -> `engine/src/core/`, git-ignored) | `src/core` | **the proposed engine change as a patch** against today's `src/core` (`patch -p1 < engine.patch` at the repo root); `make-engine.sh` copies `src/core` and applies it. New `PatternAst.fs` (public `module Xake.PatternAst`, compiled before `Path.fs`, also listed in `xake.project.json`): the parser moved out of `Path.fs` unchanged, `Specificity` moved with it (`Path.Specificity` is now an alias), literal `( ) [ ]` escaped (decision 15), generation `unparse`/`groupNames`/`wildcards`/`format`/`glob` and `formatMatches` (rule-match map -> path). `ExecCore.applyWildcards` and its two regexes deleted; a multi-file rule names its other targets with `PatternAst.formatMatches` (14) |
| `tests.patch` | `src/tests` | `PatternAstTests` (the golden test skips the two bracket/paren patterns; new tests: escaping, a literal-only pattern formats to itself, positional format, the empty-value and `)`-in-value cases `applyWildcards` got wrong, `glob`); the `XakeScriptTests`/`PathTests` cases that used `[(]` as a regex class to match a literal `(` |
| `patterns/` (`Xake.Patterns.dll`, net462 + netstandard2.0) | `src/patterns` | `PatternRuntime` (what erased members call; `EditorBrowsable(Never)`), `Matched`/`matched`/`TypedRuleBuilder<'M>` (form B), `PatternBuilders`, and `TypeProviderAssembly("Xake.Patterns.DesignTime")`; all in namespace `Xake.Patterns` |
| `designtime/` (`Xake.Patterns.DesignTime.dll`, netstandard2.0, FSharp.Core 8.0.100) | `src/patterns.designtime` | the provider; `ProvidedTypes.fs(i)` compiled from the package (below); after build it copies its whole output to `patterns/bin/typeproviders/fsharp41/netstandard2.0/`, so an in-repo `#r` of the bin dll works |
| `pkg/` (package `Xake.Exp08 0.0.8-exp08`) | `src/dotnet` | packs everything into one nupkg; the lines that would be new in `Xake.Dotnet.fsproj` are marked `NEW` |
| `consumer/` | — | `Rules.fs` (the rules in both forms, plus the incremental scenario), `build.fsx` (via the package), `build-local.fsx` (bin dlls), `engine-only.fsx`, `without-designtime.fsx` |
| `app/`, `app462/` | — | compiled consumers: net8.0 runs the scenario; net462 only builds |
| `ns-check/`, `neg/` | — | name resolution; negative cases (both use the runner `ns-check/run.sh`) |

**TP SDK sources (decision 16).** The `FSharp.TypeProviders.SDK` 8.11.0 package carries
`src/ProvidedTypes.fsi` and `src/ProvidedTypes.fs` (16,986 lines) as plain files — no
`contentFiles`, no build props — beside a compiled `lib/` dll. `<PackageReference ...
GeneratePathProperty="true" ExcludeAssets="all" PrivateAssets="all" />` restores it without using
any asset, and `<Compile Include="$(PkgFSharp_TypeProviders_SDK)/src/ProvidedTypes.fs" Link=... />`
compiles the files (as 03–07 already did). No fallback needed, nothing vendored. For `build.fsc.fsx`
(the open part of decision 16) the same files sit in `~/.nuget/packages/fsharp.typeproviders.sdk/8.11.0/src/`
after any restore; not tried here.

### Types and members as built

`Target<"p1", ?"p2", ?"p3", ?"p4">`: one static parameter per output; `"label=pattern"` names an
output (`^\w+\s*=`), unlabelled outputs are `Out1`, `Out2`, ... (by position, 1-based). Instance:
group properties, `_1`, `_2`, ... for top-level bare wildcards, one property per output. Static:
`Make(g..) : T`, `Format(g..) : string` (the first output), `For(g: string list ..) : string list`
(cross product, every output), `Glob(?g..) : Fileset`, `Rule(T -> Recipe)` (form D), `rule` (form B).
`Command<"name pattern">`: instance groups, `_n`, `Name`; static `Make`, `Pattern(g..) : string`,
`For`, `Rule`, `rule`. With no groups, `Rule : (unit -> Recipe) -> Rule` and `rule` is today's
`command "name" { }` builder itself. There are no `Need`, `TryMatch`, `Get`, `Matched` or `Text`
members. `Command<>.Rule` and `.rule` run `(command name).Run body`, i.e. today's `command` code with
AlwaysRerun. A group or label that collides with a member name, or a label equal to a group name, is
rejected with FS3033.

### Results

| check | result |
|---|---|
| `sh make-engine.sh; cd designtime; dotnet build -c Release` (`build-output.txt`) | engine, runtime, design-time: 0 warnings, **clean build 6.4 s**; design-time dll 1.2 MB, runtime dll 23 KB |
| the real `src/tests` with `engine.patch` + `tests.patch`, on a copy of the repo (`engine-tests-output.txt`) | 426 passed, 1 failed (the copy lacks `docs/schemas`, unrelated). Without the test updates: `MaskTests("c:\\[(]*[)].c", ...)` and `MaskTests("c:\\[(]*.c", ...)` fail, and `XakeScript tests.Escaped brackets` (`(name:[(]*).ss`) **crashes the test host**. Decision 15 also changes `Fileset` masks (they share `maskToRegex`), and `[(]` was the documented way to match a literal `(` |
| `dotnet pack -c Release -o $CLAUDE_JOB_DIR/tmp/feed-08` in `pkg/` (`nupkg-layout.txt`) | `lib/{net462,netstandard2.0}/`: Xake.dll, Xake.Patterns.dll (+ xml), Xake.Exp08.dll; `typeproviders/fsharp41/netstandard2.0/`: Xake.Patterns.DesignTime.dll, Xake.Patterns.dll, Xake.dll (+ xml). Dependencies: FSharp.Core 8.0.100 (+ System.ValueTuple for net462, as today). 1.4 MB, no warnings |
| `consumer/build.fsx` (`#r "nuget: Xake.Exp08, 0.0.8-exp08"`), SDK 10 (`output.txt`) and SDK 8 fsi (`output-sdk8.txt`) | the same lines (sorted) on both SDKs, and the same as `build-local.fsx` against the bin dlls (`local-output.txt`). Forms D and B give the same lines (one pair of independent rules runs in another order) |
| incremental scenario (both forms) | 1: everything builds. 2 (nothing changed): **only the `Command<>` rules rerun** — `test-build::*` although its input `out/Release/netstandard2.0/*.dll` is unchanged, and `test::*`; the compile rules and the string-API `docs::Xake` (`=>`, needs an xml) do not. 3 (`src/core/sub/Engine.fs` changed, picked up through `Sources.Glob(dir = "core")`): `Xake` for both frameworks, `docs::Xake`, then `Xake.Dotnet` (its `Lib.Make(..).dll` dependency changed), both `compileAs`, the commands. 4: `build` does nothing. 5 and 6: `Clean.Rule <| fun () ->` and `"clean-string" =>` both run every time: a phony rule with **no** dependency reruns anyway, so AlwaysRerun only shows on a command that has dependencies (step 2) |
| `app/` net8.0, `PackageReference` (`app/output.txt`) | the same lines as fsi |
| `app/run.sh`: clean build, build again, touch + build, `--no-incremental` (`app/build-output.txt`) | all succeed in 0–2 s; `lsof` finds no process holding the design-time dll after any build (macOS has no F# compiler server). Windows file locking and IDE locking cannot be tested here |
| `app462/` net462, `dotnet build -c Release` (`app462/build-output.txt`) | builds against `lib/net462/` (reference assemblies from the SDK's package); not run (no Mono) |
| `consumer/engine-only.fsx`: only the engine's Xake.dll, copied alone into a folder (`engine-only-output.txt`) | compiles and runs, no FS3031. The public `PatternAst` is usable; the multi-file rule `out/(lit)/(n:*).dll` + `.xml`, asked for `out/(lit)/x.xml`, names `out/(lit)/x.dll`; `out/lit/x.xml` does not match (before decision 15, `(lit)` was a regex group) |
| `consumer/without-designtime.fsx`: Xake.dll + Xake.Patterns.dll, no design-time dll (`without-designtime-output.txt`) | `error FS3031: ... refers to a designer assembly 'Xake.Patterns.DesignTime' which cannot be loaded from path '.../Xake.Patterns.DesignTime.dll'` at the `#r` of `Xake.Patterns.dll`, as in 06 |

Negative cases (`sh ns-check/run.sh neg/cases.fsx`, `neg/output.txt`; SDK 10 text shown, the SDK 8 text differs only in the wording of FS0039/FS0072):

| case | message |
|---|---|
| misspelt group, form D and form B (`m.fkw`) | `FS0039: The type 'Target<out/(cfg:*)/(fwk:*)/(name:*).dll>' does not define a field, constructor, or member named 'fkw'. Maybe you want one of the following: fwk` (the same in both forms) |
| misspelt output label (`m.pdb`) | `FS0039: The type 'Target<dll=out/(fwk:*)/(lib:*).dll,xml=out/(fwk:*)/(lib:*).xml>' does not define ... 'pdb'.` |
| unlabelled outputs read by extension (`m.dll`) | `FS0039: ... does not define ... 'dll'.` (they are `Out1`/`Out2`) |
| missing Make argument | `FS0496: The member or object constructor 'Make' requires 1 argument(s). The required signature is 'Target<...>.Make(fwk: string, lib: string) : Target<...>'.` (the odd count is the compiler's, as in 03) |
| `Glob` on `Command<>` | `FS0039: The type 'Command<test::(project:*)/(slice:*)>' does not define ... 'Glob'.` |
| `matched` outside a rule (`recipe { let! m = matched ... m.fwk }`) | `FS0072: The type of this expression could not be inferred before accessing its members...` |
| `matched` in a nested `recipe` inside `Asm.rule`, or in a no-group `Clean.rule` | `FS0041: No overloads match for method 'Bind'. Known types of arguments: Matched * ('a -> Recipe<'b,unit>)` followed by the overload list |
| group named like a member (`(Make:*)`) | `FS3033: ... reported an error: Target: the name 'Make' is taken by a member of the type (Make, Format, For, Glob, Rule, rule); rename the group or label` |
| label equal to a group (`lib=out/(lib:*).dll`) | `FS3033: ... Target: the names ["lib"] are used twice (groups and output labels share one namespace)` |
| `Command<"run::(Name:*)">` | `FS3033: ... Command: the name 'Name' is taken by a member of the type (Make, Pattern, For, Rule, rule, Name); ...` |
| multi-output group mismatch | `FS3033: ... Target 'out/(fwk:*)/(name:*).xml': groups ["fwk"; "name"] differ from the first pattern's ["fwk"; "lib"] (missing ["lib"], extra ["name"])` |
| `Asm.Pattern(..)` / `Test.Format(..)` | FS0039 (for `Test.Format`: `Maybe you want ... For`) |
| `Clean.Rule <\| fun m -> ...` (a no-group command given a named parameter) | **compiles**, with `m : unit` |
| `Test.Pattern(project = "a/b", ...)` | at run time: `Pattern 'test::(project:*)/(slice:*)': [("project", "a/b"); ("slice", "1")] do not give a name the pattern matches back (got 'test::a/b/1')` |

### Line counts

| component | lines |
|---|---|
| engine (`engine.patch`: +181 / -119 in 5 files) | `PatternAst.fs` 176 = 102 moved from `Path.fs` (parser, regex, specificity, their docs) + 74 of generation; `Path.fs` -98; `ExecCore.fs` -24 (`applyWildcards` and its helpers gone, one call changed); `Xake.fsproj` and `xake.project.json` +1 each |
| engine tests (`tests.patch`: +57 / -5) | `PatternAstTests` +48 / -1, `XakeScriptTests` +5 / -2, `PathTests` +5 / -2 |
| runtime `Xake.Patterns.dll` | `Runtime.fs` 112, `AssemblyInfo.fs` 8, fsproj 22 |
| design-time | `Provider.fs` 148 (plus the 16,986 lines of TP SDK sources, from the package), fsproj 39 |
| packing (the `src/dotnet` delta) | about 12 lines: `FSharpToolsDirectory`, `NoWarn NU5100;NU5118`, two `ProjectReference`s, three lines in `IncludeCoreOutput` |

### Deviations from the brief, and why

- **Renames** `Pattern<>` -> `Target<>` and `Command<>.Target` -> `Command<>.Pattern`: the user's change during the run (see the top of this section).
- **Namespace (decision 17)**: the provided types, `matched` and the builders all live in `Xake.Patterns`, so a script needs `open Xake.Patterns` after `open Xake` (the name resolution above requires that order anyway). The provided types could sit in namespace `Xake` (06 did that), but that would not avoid the clash.
- **Decision 4**: `Format` only on `Target<>`, `Pattern` (formerly `Target`) only on `Command<>` — one string generator per type; `Make` and `For` on both, `Glob` only on `Target<>`.
- **Decision 3**: the label syntax was chosen here: `"label=pattern"`. A pattern that really starts with `word=` (a file `a=b.txt`) would be read as a label; `./a=b.txt` should avoid it (not tried).
- **Decision 6**: bare wildcards are positional, but rejected in multi-output patterns (their positions could differ between outputs), as in 05.
- **Decision 23**: a no-group `Command<"clean">.rule` erases to today's `command "clean"` builder (exactly today's semantics). `Clean.Rule <| fun m -> ...` compiles, with `m : unit`.
- `Command<>` keeps the instance `Name` (the matched name, group `"0"`), as in 04 and 07.

### What should change in the brief

1. Record the `Target<>` naming rule: `open Xake` before `open Xake.Patterns`, and the DU written `DomainTypes.Target` in annotations; otherwise FS3081/FS0033 with misleading text.
2. Rename `Command<>.Pattern` to `Format` (or back to `Target`): it returns a name, not a pattern.
3. Decision 15 reaches beyond rule patterns: `Fileset` masks change too, and `[(]`/`[)]` stop working as the way to match a literal parenthesis (two `PathTests` and two `XakeScriptTests` cases rewritten). `|` is still unescaped (a literal `|` in a path is regex alternation); add it to 15.
4. Decision 21: AlwaysRerun only matters for a command that has dependencies; a phony rule with no dependency reruns every time already (`=>` too).
5. Decision 16 is settled for msbuild: the package's `src/` files compile directly via `$(PkgFSharp_TypeProviders_SDK)`; `build.fsc.fsx` is still to be tried.
6. Decision 13: the module is `Xake.PatternAst` (its own file, before `Path.fs`), and `Specificity` moves into it.

Files outside the worktree: `$CLAUDE_JOB_DIR/tmp/feed-08/` (the nupkg; `consumer/build.fsx` and `app*/`
restore from it, the path is hard-coded), `$CLAUDE_JOB_DIR/tmp/stage-08/` (`xake-only/`, `without/`),
`$CLAUDE_JOB_DIR/tmp/xake-08-*` (the scenario's build roots), the test logs `$CLAUDE_JOB_DIR/tmp/test*.log`.
`~/.nuget/packages/xake.exp08/` was deleted after the runs (a rerun restores it from the feed).

### Opening in an IDE

Why the editor showed errors (Ionide / FsAutoComplete): `neg/cases.fsx` `#r`s
`patterns/bin/Release/netstandard2.0/Xake.Patterns.dll`, whose `TypeProviderAssembly` names the
design-time dll, but nothing put that dll where the compiler looks (next to the `#r`'d dll, or
`<its dir>/../../typeproviders/fsharp41/netstandard2.0/`, i.e. `patterns/bin/typeproviders/...`; the
command-line checks had used staging folders). Without it every use of `Target<>` is FS3031 plus a
cascade. Also `neg/cases.fsx` is red by design (repeated `let r`, deliberate errors).

Now `dotnet build patterns -c Release` also builds `designtime/` (a post-build `Exec` in
`patterns/Xake.Patterns.fsproj`, guarded by `InDesignTimeBuild` against recursion), which copies its
output to `patterns/bin/typeproviders/fsharp41/netstandard2.0/`. Build order, from `08-consolidated/`:

```
sh make-engine.sh                                       # engine/ (git-ignored), once
dotnet build engine/src/core -c Release -f netstandard2.0
dotnet build patterns -c Release                        # runtime dll + design-time dll in place
sh consumer/stage.sh feed                               # optional: .stage/ and .feed/ for build.fsx, engine-only.fsx, without-designtime.fsx
```

Then open `consumer/showcase.fsx` (zero errors: single and multi-output `Target<>`, `Command<>` with and
without groups, forms D and B, `Format`/`Make`/`For`/`Glob`, `need` over a generated list), in VSCode
open the `08-consolidated` folder and restart the F# language server after a rebuild (the design-time dll
is locked and cached by FSAC). `neg/cases/*.fsx` are single negative cases; `neg/cases.fsx` stays red.
`dotnet fsi --typecheck-only consumer/showcase.fsx` passes from the script folder and from the repo root.
Scripts no longer use `$CLAUDE_JOB_DIR` (`consumer/nuget.config` points `build.fsx` at `consumer/.feed`).
Rename applied to the prototype: `Command<>.Pattern(...)` is now `Command<>.Format(...)` (the generating
member on both types; `neg/cases/16` now compiles, `15` still fails: there is no `Pattern` member).
