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
