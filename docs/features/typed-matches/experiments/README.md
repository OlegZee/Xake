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
