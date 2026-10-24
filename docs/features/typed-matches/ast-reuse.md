# Reusing `PatternAst` in the type provider

Question: the provider must use the engine's one pattern parser (`PatternAst`, `src/core/Path.fs`,
merged in #89) instead of the prototype's own regexes (experiment 03). How does the design-time dll
get it, where does the runtime half live, and how is it all packed into the `Xake` nupkg
(brief decision 1)? Proven in [`experiments/05-ast-reuse`](experiments/05-ast-reuse/) (see the
README there); nothing in `src/` was changed.

## 1. What `PatternAst` is today

`module internal PatternAst`, nested in `module Xake.Path` (`Path.fs:171-253`; `Specificity` at `:33`):

| piece | what |
|---|---|
| `Token = Lit of string \| Star \| DoubleStar \| AnyChar \| Group of string * Token list` | `*`, `**`, `?`, `(name:...)`; groups may nest |
| `parse : string -> Token list` | hand-written scanner; `\` normalized to `/`; `(` is a group opener only when followed by `word+:`, else a literal; an unclosed group runs to the end |
| `toRegex : Token list -> string` | unanchored regex: `*` -> `([^/\\]*)`, `?` -> `([^/\\])`, `**` -> `(.*)`, group -> `(?<name>...)`; literals escape only `.$^+!={}` |
| `specificity : Token list -> Specificity` | `(literal chars, -#**, -wildcard weight)` |

Dependencies: only `System.Text.RegularExpressions` and the `Specificity` alias (`int*int*int`,
declared at the top of `Path.fs`). The rest of `Path.fs` needs `Pickler` and `Env` (`Env.isUnix`
picks `IgnoreCase` in `matchImpl.buildRegex`), so `Path.fs` as a whole does not compile standalone,
but the `PatternAst` slice does: the experiment cuts it out of the real `Path.fs` with `awk`
(`slice-ast.sh`) and compiles it unchanged into two dlls. Users: `matchImpl.buildRegex` (via
`maskToRegex`) and `SpecImpl.compute`; tests in `src/tests/PatternAstTests.fs` (golden tests against the former regexes).

**How bare wildcards appear.** Every `Star`/`DoubleStar`/`AnyChar` emits an *unnamed* capture group,
at any depth (the `*` inside `(name:*)` too). .NET numbers unnamed groups first, left to right,
then named ones — so the regex numbers are exactly the `"1"`, `"2"`, ... keys of `getRuleMatches`.
For decision 6 (positional), the k-th **top-level** wildcard is read under its regex number,
computed off the tokens (`wildcards`, below). Example `gen/*/(name:*)-?.txt`: `_1` -> `"1"`,
the group's own `*` -> `"2"`, `_2` (the `?`) -> `"3"`; verified at run time.

Two quirks the provider has to know (both are today's engine behaviour, not changed here):
- **A literal `(` is a regex group.** `escapeLiteral` leaves `(`/`)`/`[`/`]` alone, so
  `lit(x)/*.txt` matches `litx/a.txt` and its `*` is captured as `"2"`, not `"1"` (`edge.fsx`).
  `wildcards` counts literal `(` to stay correct. The same holds for a **project root** containing
  parentheses (`matchGroups` prepends the root to the pattern) — such a root probably breaks matching
  outright (by reading; not run). Escaping parentheses in `escapeLiteral` would fix both and
  simplify the count, but changes matching — a separate engine fix.
- **No inverse exists.** Generation today is `ExecCore.applyWildcards` (`ExecCore.fs:20-39`): two
  more regexes (`wildcardsRegex`, `patternTagRegex`) over the pattern text — a third encoding of the
  grammar, used for a multi-file rule's other targets. By reading: it leaves `(lib:)` in the path
  when a group value is empty (`[^)]+` needs a character) and breaks on a value containing `)`.

## 2. Generation on the AST (proposed, implemented in the experiment)

Pure functions over `Token list`, next to `parse`/`toRegex`/`specificity`
(`experiments/05-ast-reuse/PatternAstGen.fs`, 80 lines):

```fsharp
val unparse    : Token list -> string                   // inverse of parse (text form)
val groupNames : Token list -> string list              // first appearance, nested included
val wildcards  : Token list -> (Token * int) list       // top-level bare wildcards + regex number
val format     : named: (string -> string) -> positional: (int -> string) -> Token list -> string
val glob       : named: (string -> string option) -> positional: (int -> string option)
                 -> Token list -> string                // unbound group -> its own body as a mask
```

The provider's members map onto them: `Make`/`Format` = `format` + a **re-match with the engine's
own `Path.matchGroups`** (the values must come back unchanged: this rejects `ext = "fs"` for
`(ext:c*)` and `fwk = "net8.0/x"` for a `*`); `Target`/multi-file outputs = `format` per pattern;
`All` = cross product of `make`; `Glob` = `glob` per pattern -> `Fileset (DefaultOptions,
[Includes (Path.parse mask)])` (decision 8). Nested groups unwrap: `Glob()` of
`(root:*/(leaf:*-a)).ss` is `*/*-a.ss`.

Follow-up for the engine (not needed by the provider): `applyWildcards` can become
`format (groups.TryFind >> default "") (wildcard number -> groups.TryFind ...)`, removing the third
encoding and its empty-value / `)` edge cases while keeping decision 7 (missing -> `""`).

## 3. How the design-time dll gets the AST

The design-time dll **must reference Xake.dll whatever we choose**: the provided signatures mention
`Recipe<ExecContext, T>` and `ExecContext Rule` (`Get`, `Rule`), and the erased bodies are
quotations calling the runtime module in Xake.dll. (The TP SDK template avoids a runtime reference
by compiling the runtime's *sources* into the design-time dll plus an `assemblyReplacementMap`; for
Xake that means compiling most of `src/core` twice — not considered.) So (b) is a given; the
choice is only how `PatternAst` reaches the provider code:

| option | how | verdict |
|---|---|---|
| (a) link `Path.fs` | `<Compile Include="../core/Path.fs">` | needs `Pickler.fs`, `Env.fs` too, and defines a second `Xake.Path` next to the referenced one — no |
| (b) reference + IVT | `InternalsVisibleTo("Xake.DesignTime")` on Xake.dll; the provider calls `PatternAst.parse` of the loaded Xake.dll | **proven** (`AstVia=ivt`, default) |
| (c) extract + link | `src/core/PatternAst.fs` (no dependencies), compiled into Xake.dll *and* linked into the design-time dll | **proven** (`AstVia=link`): same output, byte for byte |

**Recommendation: (c)'s extraction, (b)'s access.** Move `PatternAst` (and the `Specificity`
alias) into its own file `src/core/PatternAst.fs`, `module internal Xake.PatternAst`, compiled
before `Path.fs`, and add the generation functions there. The design-time dll reaches it through
`InternalsVisibleTo("Xake.DesignTime")` — one compiled copy, the design-time checks run the very
code the engine matches with, from the same package version (Xake.dll is loaded into the compiler
process anyway, for the types above). The extraction costs nothing, gives the generation code and
its tests a home outside `Path.fs` (whose segment-mask half, `PathMask`, serves `Fileset`, a
different grammar), and keeps the link variant a one-line fallback if the design-time dll ever has
to stop referencing Xake.dll. Today's `Path.PatternAst` users change to `PatternAst` (two call
sites) and `Path.Specificity` stays as an alias.

## 4. The runtime half and how erased members call it

A new public module in Xake.dll, e.g. `src/core/Patterns.fs` (`module Xake.PatternRuntime`), after
`ScriptFuncs.fs` (it needs `getRuleMatches`, `recipe`, `Rule`, `Fileset`). Experiment:
`Runtime/Runtime.fs`, ~75 lines: `group`, `format`, `make`, `all`, `globMasks`, `glob`, `get`,
`rule`, with a token cache per pattern string.

- Erased members compile into **direct calls in the consumer's assembly**, so the module must be
  **public** (hide it from completion with `[<CompilerMessage(..., IsHidden = true)>]` or
  `EditorBrowsable(Never)`); its signature becomes an API surface to keep stable across versions
  (a script compiled against one Xake package version runs with that same version, so drift is
  limited to IDE caches).
- The provider bakes constants into the quotations: the pattern strings (`string[]`) and the keys
  (`string[]`: group names, then wildcard regex numbers as strings). Example:
  `<@@ PatternRuntime.make patterns keys (%%values : string[]) @@>`.
- A typed match stays erased to `Map<string,string>` = `getRuleMatches ()`, so `Rule`/`Get` need no
  engine change (decision 7 untouched).
- Provided types must be attributed to the runtime assembly: either
  `ProvidedTypeDefinition(typeof<ExecContext>.Assembly, "Xake", "Pattern", ...)` or
  `assemblyReplacementMap = [ "Xake.DesignTime", "Xake" ]`. Without it fsi says
  `FS1108 ... You must add a reference to assembly 'Xake.Patterns.DesignTime'` (hit and fixed in 05).

## 5. Packaging into the `Xake` nupkg

Proven with a stand-in for `src/dotnet` (`experiments/05-ast-reuse/Pkg`, both TFMs). The F# SDK
already has the target (`Microsoft.FSharp.NetSdk.targets`, `PackageFSharpDesignTimeTools`, wired into
`TargetsForTfmSpecificContentInPackage`): it packs the **whole output folder** (minus FSharp.Core)
of every ProjectReference marked `IsFSharpDesignTimeProvider`, under
`$(FSharpToolsDirectory)/fsharp41/<tfm>/`; `FSharpToolsDirectory` defaults to `tools`. Result:

```
lib/{net462,netstandard2.0}/  Xake.dll (runtime half + TypeProviderAssembly), Xake.Dotnet.dll
typeproviders/fsharp41/netstandard2.0/  Xake.DesignTime.dll, Xake.dll (+ Xake.xml)
```

`#r "nuget: ..."` from a local feed works under F# 10 and the SDK 8 fsi (`use-nuget.fsx`). The
compiler found the design-time dll from `lib/netstandard2.0/` via `../../typeproviders/...`;
the same probe works in a build tree (`probe.fsx`: `bin/Release/netstandard2.0/X.dll` finds
`bin/typeproviders/fsharp41/netstandard2.0/`; removing that folder gives FS3031).

**The trap: FS3031 in-repo.** Once Xake.dll carries `TypeProviderAssembly("Xake.DesignTime")`,
**every compilation that references it fails** (`error FS3031 ... designer assembly
'Xake.DesignTime' which cannot be loaded`) unless the design-time dll is next to it or in that
`typeproviders` folder — whether or not the code uses `Pattern<>` (seen when packing `Pkg` before
the copy step). The design-time project builds *after* core (it references it), so it has to copy
itself back. Changes, described (not made):

- `src/core/Xake.fsproj`: `PatternAst.fs` before `Path.fs`; `Patterns.fs` after `ScriptFuncs.fs`;
  `InternalsVisibleTo` `Xake.DesignTime`; `TypeProviderAssembly("Xake.DesignTime")` (an
  `AssemblyAttribute` item or an `AssemblyInfo.fs`). No reference to the design-time project (cycle).
- New `src/designtime/Xake.DesignTime.fsproj`: netstandard2.0 only; FSharp.Core 8.0.100 pinned like
  core (the compiler supplies its own at design time; the F# pack target excludes FSharp.Core.dll);
  `FSharp.TypeProviders.SDK` 8.11.0 with `ExcludeAssets=all GeneratePathProperty=true` and
  `ProvidedTypes.fsi/.fs` compiled from `$(PkgFSharp_TypeProviders_SDK)/src`; ProjectReference core;
  `NoWarn FS3005` (it compiles against a Xake.dll whose attribute names this very assembly); an
  `AfterTargets="Build"` copy of `$(TargetPath)` to `src/core/bin/typeproviders/fsharp41/netstandard2.0/`
  (one place serving every configuration and TFM folder under `src/core/bin`). Clean build 6 s,
  dll 1.2 MB (ProvidedTypes), as in 03.
- `src/dotnet/Xake.Dotnet.fsproj`: `<FSharpToolsDirectory>typeproviders</FSharpToolsDirectory>` and
  `<ProjectReference Include="..\designtime\Xake.DesignTime.fsproj" IsFSharpDesignTimeProvider="true"
  PrivateAssets="all" ReferenceOutputAssembly="false" />`. This also orders the build: anything that
  builds `src/dotnet` (tests, pack) builds the design-time dll first, so the copy is in place before
  `Xake.Dotnet` compiles against Xake.dll. Package grows by ~1.9 MB uncompressed (design-time dll +
  a second Xake.dll). Pack warns `NU5100` (dlls outside `lib/`) and `NU5118` (the same file from both TFMs, added once) — expected, to be suppressed in `src/dotnet`.
- `src/tests`: nothing if it references `src/dotnet` (it does); a typed-pattern test then just works.
- `build.fsx`: the `libraries` list gets `Xake.DesignTime` (`Dir = "src/designtime"`, `Needs =
  ["Xake"]`, netstandard2.0 only — `binaries` crosses every library with every framework today, so
  the framework list becomes per library) and `Xake.Dotnet` needs it. With `dotnet build ...
  --output out/<fwk>`, the referenced design-time project lands in `out/<fwk>/` next to Xake.dll,
  which satisfies the compiler too. The pack rule (`dotnet pack src/dotnet`) is unchanged; a check
  like `checkXakeDependency` could assert `typeproviders/fsharp41/netstandard2.0/Xake.DesignTime.dll`
  is in the nupkg.
- `build.fsc.fsx` (hermetic): needs a description for `src/designtime` whose sources include files
  from a *package* (`ProvidedTypes.fs` from the TP SDK's `src/`) — open whether descriptions/locks
  support that; and its own placement of the design-time dll next to `out/<cfg>/<fwk>/Xake.dll`
  before compiling anything against it (or the FS3031 above). Pack from `out/Release` (its PR 8)
  must lay out `typeproviders/` by hand.
- Experiments 01-03 `#r` `src/core/bin/Release/netstandard2.0/Xake.dll` directly: after the change
  they need the design-time project built too (FS3031 otherwise).

## 6. Risks

- **FS3031 everywhere Xake.dll is referenced without the design-time dll** — every in-repo build
  path (msbuild, `build.fsx`, `build.fsc.fsx`, ad-hoc `#r` of a bin dll) must place it. The copy-back
  step is the one fragile piece; a missed path fails loudly, not silently.
- **Public runtime module** = API surface; changing a function signature breaks scripts compiled
  against older erased calls (normally same package version, so mostly an IDE-cache concern).
- **Design-time loads Xake.dll into the compiler/IDE process**; IDEs lock it while open — only bites
  Xake's own development (rebuilding core with tests open in an IDE).
- **Positional keys depend on regex numbering**: literal `(` in a pattern or in the project root
  shifts it (and the root case likely breaks matching anyway); fixed properly by escaping parentheses
  in `escapeLiteral`.
- Static parameters: the experiment uses `pattern, out2, out3, out4` (decision 2) without labels
  (decision 3 is not in it); fsi reports only the first provider error per run.

## 7. Open questions

1. IVT vs link is settled in favour of IVT here; confirm, or prefer link for symmetry with a
   possible future design-time dll that does not reference Xake.dll.
2. Name of the design-time assembly: `Xake.DesignTime` assumed.
3. Should `applyWildcards` move to `PatternAst.format` in the same PR (one grammar for real) or
   separately? It is an engine behaviour change only in the empty-value / `)` edge cases.
4. Escaping `(`/`)`/`[`/`]` in literals (engine fix) — before or after the provider?
5. `build.fsc.fsx`: can a description compile sources from a package (`ProvidedTypes.fs`), or does the
   TP SDK source get vendored into the repo (two files, ~16.4k lines, MIT)?
6. Untested: IDEs (Ionide/Rider/VS), a net462 consumer, a compiled project consuming the nupkg.
