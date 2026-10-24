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
