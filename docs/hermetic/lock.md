# The lock: format, `Lock.*` and `Project.import`

A lock is a JSON file that records, per project and target framework, exactly what the compiler
is handed (the command line, generated inputs, resx pairs) and the SHA-256 of everything it
reads that is not in the repository: the compiler, references, analyzers, the msbuild files
that produced the answer, and the package graph. `Lock.build` and `Lock.compile` then compile
*from* the lock and fail when anything differs. This page is the reference for the file
format, the `Lock` module, `Project.import` and the `csc { ...; lock "path" }` operation.

Everything here is in the package `Xake.Hermetic.Dotnet` (`src/hermetic/`: `Lock.fs`,
`Project.fs`, `Roots.fs`, `Git.fs`, `Fsproj.fs`, `Json.fs`); the compilation a lock entry wraps,
`Csc`, and the runner `Csc.run` belong to `Xake.Dotnet` (inside the `Xake` package) and are
described in [../csc-syntax.md](../csc-syntax.md). Scripts open both:

```fsharp
#r "nuget: Xake.Hermetic.Dotnet"

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet
```

## The API at a glance

| Name | Kind | What it does |
|---|---|---|
| `Lock.Entry = { Csc; Evaluation; Packages }` | type | one project's compilation: the `Csc` the compiler is handed, where the answer came from, the restore graph |
| `Lock.Document = { Configuration; Properties; Entries }` | type | one lock file |
| `Project.import ImportOptions` | recipe | msbuild design-time build per project and framework, writes the lock ([below](#where-a-lockentry-comes-from)) |
| `Lock.load` / `loadWith extraRoots` | recipe | read a lock, `needFiles` it, expand paths for this machine |
| `Lock.save` / `saveWith extraRoots` | recipe | write a lock, tokenizing paths |
| `Lock.format roots doc` / `parse roots text` / `read roots path` | pure | the file format, with the roots passed explicitly |
| `Lock.entry name doc` / `entryFor framework name doc` | pure | look an entry up (`entryFor` is the unambiguous one) |
| `Lock.ofCsc c` | pure | a composed `Csc` as an entry: empty `Evaluation`, no packages |
| `Lock.rehash entry` | pure (reads disk) | fill every empty `Sha256` from what is on disk now |
| `Lock.diff a b` | pure | the differences between two entries, `[]` = identical |
| `Lock.diffText roots a b` | pure | `diff` with every path tokenized against `roots`, as `buildWith`/`verify` print it |
| `Lock.underCi` | recipe | whether this build runs under CI (script variable `CI`, then the environment variable `CI`); under CI `buildWith` fails on a missing lock ([The CI flag](#the-ci-flag)) |
| `Lock.mapPaths f entry` / `mapText f entry` | pure | rewrite paths / text of an entry |
| `Lock.packagesOf cacheRoot assets` | pure | the package graph of an entry from a `Nuget.Assets` |
| `Lock.restoreRequest entries` / `Lock.restore options doc` | pure / recipe | what a restore must provide for entries; populate the package folder from a whole lock ([restore.md](restore.md)) |
| `Lock.compile entry` / `compileWith Lock.Options entry` | recipe | replay an entry: restore, compiler check, revision token, then `Csc.run` |
| `Lock.build path c` / `buildWith Lock.Options path c` | recipe | build a `Csc` gated by the lock at `path`; a missing lock is recorded, or fails under CI |
| `Lock.record path c` | recipe | hash `c` and (over)write the lock at `path`, compile nothing |
| `Lock.verify path c` | recipe | the diff between the lock at `path` and `c`, write and compile nothing; a missing lock fails |
| `csc { ...; lock "path" }` | operation | `Lock.build` as an operation of a `csc {}` block; only `packageroot`/`norestore` may follow |

`Lock.Options = { Run: RunOptions; Restore: Restore.Options }` with `Lock.Options.Default`.

The two routes to a lock, and both end in the same functions:

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

// 1. from composed settings: a lock gates the compilation the script describes
"out/app.dll" ..> csc {
    targetfwk "net-4.6.2"
    src !!"src/*.cs"
    grefs ["System.dll"]
    lock "locks/app.json"
}

// 2. the same thing without the sugar: resolve, then Lock.build
"out/app.dll" ..> recipe {
    let! c = csc { src !!"src/*.cs"; targetfwk "net-4.6.2"; grefs ["System.dll"]; out (File.make "out/app.dll"); resolve }
    do! Lock.build "locks/app.json" c        // gate against the lock, restore, revision, Csc.run
}
```

## The compiler a lock names

The lock records what ran in `Dependencies.Compiler`: `Path` and `Sha256` name the compiler
file, `Version` is the compiler's own product version read from the file (`4.12.0-3.24572.7`
for the toolset package, `4.11.0-3.25569.22` for SDK 8.0.425's; for the SDK's native `csc`
launcher the `csc.dll` next to it is read). The SDK that evaluated the project is
`Evaluation.Sdk`, empty for a composed compilation. For a composed compilation the compiler
comes from `cscpath`, `toolset` or the framework (see [../csc-syntax.md](../csc-syntax.md));
`Lock.compile` restores a toolset package the lock names when it is not on this machine yet,
through its restore step (`Restore.ensure`, step 1 below), which treats the compiler as one
package among the entry's references and analyzers.

### Import: the project decides, msbuild answers

`Project.import` does not choose a source. `parseImport` reads which compiler msbuild wired up:

- The `Microsoft.Net.Compilers.Toolset` package does **not** set `CscToolPath`/`CscToolExe`.
  It redirects `CSharpCoreTargetsPath` (and the `Csc` task assembly) to its own `tasks/netcore/`
  directory; the task's tool path then defaults to the `bincore` next to that targets file.
- So the compiler is `<dir of CSharpCoreTargetsPath>/bincore/csc.dll`: the SDK's
  `Roslyn/bincore/csc.dll` for an unpinned project, the package's for a pinned one.
  `RoslynTargetsPath` always reports the SDK's Roslyn and is only the fallback.
- **A project referencing the package also needs `<RoslynCompilerType>Toolset</RoslynCompilerType>`**
  (csproj or `-p:`). Without it, on SDK 9 and later, `Microsoft.NET.Sdk.BeforeCommon.targets`
  silently sets `CSharpCoreTargetsPath` back to the SDK's, and the package has no effect.

## Replaying a lock: `Lock.compile`

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

do! Lock.compile entry                              // entry : Lock.Entry
do! Lock.compileWith { Lock.Options.Default with Run = { RunOptions.Default with FailOnError = false } } entry
do! Csc.run RunOptions.Default entry.Csc            // the runner alone: no restore, no revision token
```

`Lock.compile` hands a lock entry's `Csc` to the runner after the two hermetic steps that must
happen first (restore, revision token; below), with no extra env vars and no temp files: the
`Csc`'s own `Args` is the whole compilation. It is an entry point of its own, not a mode of
`csc {}`: a setting inside the record would let `csc { fromlock p; src !!"*.cs" }` compile while
silently ignoring `src` and every other composition setting. What applies to every entry point is
`RunOptions = { FailOnError; CscPath; Server; Environment }` -- how the runner behaves, not what it
compiles (`Server` is whether csc runs as a thin client of the Roslyn compiler server,
[../csc-server.md](../csc-server.md); `Environment` is the compiler process's env vars). `Csc.compile`
builds one with `Csc.runOptions settings` (the settings' `FailOnError`/`CscPath`, the server
resolved through `CompilerServer.resolve`, the target framework's `EnvVars`).
`Lock.compileWith` takes a `Lock.Options = { Run: RunOptions; Restore: Restore.Options }`;
`Lock.compile` uses `Lock.Options.Default` with the server resolved (`CSC_SERVER`, then
`XAKE_CSC_SERVER`). Signatures: `Csc.run : RunOptions -> Csc -> Recipe<ExecContext, unit>`,
`Lock.compile : Lock.Entry -> Recipe<ExecContext, unit>`, `Lock.compileWith : Lock.Options ->
Lock.Entry -> Recipe<ExecContext, unit>`. The module is `Csc` (`[<ModuleSuffix>]` next to the
record `Csc`); the 3.3 function `Csc settings` no longer exists, `Csc.compile settings` replaces it.

`Lock.compileWith` does, before handing the entry to `Csc.run` (`Lock.fs`):

1. **Restores what the lock names and this machine lacks** (`Restore.ensure options.Restore
   (Lock.restoreRequest [entry])`, see [restore.md](restore.md)): the compiler when it lives in a
   `Microsoft.Net.Compilers.Toolset`-shaped package, and every reference and analyzer under the
   package folder, in one `dotnet restore`. With nothing missing (the normal case) this is one
   `File.Exists` per path and no process. Any problem it reports fails with `('<name>') restoring
   the packages the lock names failed:` and the list.
2. **Explains a compiler that is still missing** (`ensureCompilerAvailable`; it restores
   nothing itself, step 1 did that):
   - already on disk (`Dependencies.Compiler.Path` exists) -- nothing to do.
   - under the package folder (`Restore.packageRoot options.Restore`) -- a toolset-shaped package
     the restore did not, or was not allowed to, provide: `'<name>': the compiler <path> is not
     available and restoring <id> <version> did not provide it`. A hash mismatch *after* a
     successful restore is left to the hash check below -- it means a different package build,
     not a missing one.
   - under `$(DotnetRoot)/sdk/<version>/` -- an SDK this machine does not have; nothing to
     restore, so this fails immediately: `'<name>': the lock names the compiler of SDK
     <version> (<path>), which is not installed; install that SDK or re-import with the
     installed one` (or, under `$(DotnetRoot)` but not `sdk/`, a generic "not installed"
     message naming the path).
   - anywhere else -- `'<name>': the compiler <path> named by the lock does not exist`.

   Every failure here goes through the same `trace Error` + `FailOnError`-gated `failwith` shape
   (`failStep`) as the hash-mismatch check and `Tool.failOnExitCode`.
3. **Resolves `$(SourceRevisionId)`**: the lock never
   carries a commit sha itself (see `Generated` and `Git.tokenize` below) -- when
   `Generated`'s content or `Csc.Args` carries the literal token `$(SourceRevisionId)`,
   it is replaced everywhere `Csc.mapText` reaches (`Generated` content, `Options`, `Defines`)
   with `Git.headSha c.Directory` (walking up from the
   project's own directory for a `.git`; no `git` executable). No token anywhere -- nothing
   happens, the composed mode included, since it never populates `Generated`. A token present but no
   repository found (or `HEAD` unresolvable) fails with `'<name>': the lock needs
   $(SourceRevisionId) but no git repository was found at or above '<dir>' -- a lock that needs
   a revision must be compiled in a repository`, the same `trace Error` + `FailOnError` shape as
   the other checks here.

`Lock.build`/`Lock.buildWith` (see "Locking composed settings") do the lock gate first and then
this same `compileWith`.

## Where a `Lock.Entry` comes from

`Project.import` (`src/hermetic/Project.fs`) runs an msbuild design-time build per project and
target framework -- `ProvideCommandLineArgs`/`SkipCompilerExecution`, so the compiler reports
its command line instead of running -- and writes **one lock file per variant, holding every
framework**. `ImportOptions`:

| Field | Meaning |
|---|---|
| `Projects` | project files; all land in one lock |
| `Frameworks` | the target frameworks to import each project for; all of them land in that same lock, one `Lock.Entry` per (project, framework). A project gets entries only for the ones it declares -- see below |
| `Configuration` | msbuild `Configuration`, default `Release` |
| `Properties` | extra `-p:` properties, e.g. `["Brand", "MESCIUS"]` |
| `Variant` | names the `obj/xake/<framework>/<variant>/` subtree; keeps distinct property sets from overwriting each other's generated files |
| `Output` | the lock file to write |
| `Roots` | extra `(token, path)` roots to tokenize against, beyond the built-in three (a relative path is taken against the project root; a built-in token's name replaces that built-in); default `[]` |

**One restore, then a design-time build per framework.** Three msbuild phases per
project, all of them inside that project's `withProjectLock`:

1. **`-t:Restore`, with no `TargetFramework` and with `-p:RestoreRecursive=false`.** Without
   `TargetFramework` NuGet resolves *every* target of a multi-targeted project into the one
   `obj/project.assets.json`; with `RestoreRecursive=false` it stops walking the project graph,
   so a project's restore no longer rewrites the assets files of the projects it references.
2. **A design-time build per framework**, `-p:TargetFramework=<f>` and **no** `-restore` -- the
   restore above already produced everything it reads. `IntermediateOutputPath` stays
   per (framework, variant).
3. **A `-pp` preprocess per framework**, for the evaluation's import list.

`Frameworks` is the matrix asked for, not a claim that every project has every leg of it, and
a repository rarely multi-targets all its projects the same way. The restore run -- the one
msbuild phase here that is not per framework -- therefore also reports the project's own
`TargetFrameworks` (or single `TargetFramework`), and the import builds only the intersection
(`Project.frameworksToImport`, pure and unit-tested), tracing each framework it skips. Asking
msbuild for a leg the project never declared would build one that was never configured,
against a restore that has no target for it -- the same `NETSDK1005` this design exists to
remove. A project reporting no framework list at all is "msbuild said nothing", not "nothing
declared": everything requested is kept.

*Why.* One lock per (framework, variant) meant the same multi-targeted project was imported
once per framework, each pass passing a global `-p:TargetFramework=X` on a `-restore`; NuGet
then wrote an assets file with only X's target, and `-restore`'s walk of the project graph
rewrote the *referenced* projects' assets files too -- which a per-project lock cannot cover.
Concurrent imports failed with `NETSDK1005 ... doesn't have a target for '<fwk>'` or silently
recorded an entry with `packages 0` (measured while verifying the lock against a real multi-project solution). The fix is structural: the
framework matrix of one variant is one import, so the two frameworks cannot race at all, and
`RestoreRecursive=false` keeps one project's restore out of another's obj. Both were verified
by hand on the dataengine fixture -- a restore without `TargetFramework` yields
`['.NETFramework,Version=v4.7.2', '.NETStandard,Version=v2.0']` where one with it yields a
single target, and with `RestoreRecursive=false` only the restored project's own assets file
appears. The `withProjectLock` `Resource` stays, now held for the whole project (restore plus
every framework's build), because two *brands* still restore into the same
`obj/project.assets.json`; what it no longer has to cover is the cross-project rewrite.
`Nuget.readAssets` now **fails** when the assets file has no target for the framework asked
for, rather than returning an empty graph -- an SBOM without its package components and
nothing saying so was the worst half of that defect.

**Extra roots.** The three built-in roots (`$(NuGetPackageRoot)`, `$(ProjectRoot)` = the
build's project root, `$(DotnetRoot)`) tokenize everything under the package cache, the checkout being
imported, and the SDK -- but a cross-repo `ProjectReference` (page's `LocalBuild=true` pointing
at a sibling `ar-net-core-dataengine` checkout) resolves to paths under neither, and would
otherwise land in the lock untokenized and machine-specific. `ImportOptions.Roots` declares one
extra token per sibling repository (the decision: no shared parent root, since siblings can move
independently and a shared root would tokenize more than intended) -- e.g. `Roots = [
"$(DataEngineRoot)", "/abs/path/to/ar-net-core-dataengine" ]`. `Project.import` combines them
with the built-in three via `Roots.make projectRoot extra` (inside the import:
`Roots.currentWith options.Roots`), which validates each token is well-formed (`$(Name)`) --
failing early rather than writing a lock that silently didn't tokenize -- takes a relative path
against the project root (not the process's cwd), lets an extra token of a built-in's name
*replace* that built-in (how `Roots.packageRootOverride dir` points `$(NuGetPackageRoot)` at a
build's own package folder; it used to be refused), and keeps the longest-root-first order
`Roots.builtin` already relies on. A script reading such a lock back must pass the same roots:
`Lock.loadWith extraRoots path` inside a recipe, or `Lock.read (Roots.make projectRoot
extra) path` outside one. `Lock.load`/`save` use the built-in three only.

**Where the project root comes from (review §2.5).** `$(ProjectRoot)` is the engine's
`ExecOptions.ProjectRoot` -- what `need`, `getFiles` and rule matching already resolve against --
not the process's current directory. The `Roots` module holds the whole thing: `nugetRoot ()`,
`dotnetRoot ()`, `nugetPackageRootToken`, `builtinTokens`, `packageRootOverride dir`, the pure `builtin projectRoot` / `make projectRoot extra`,
and the recipes `current` / `currentWith extra` that read the root from `getCtxOptions()`. The
recipe-level lock and evaluation entry points (`Lock.load`/`loadWith`/`save`/`saveWith`,
`Fsproj.load`) are recipes for exactly this reason; the pure `format`/`parse`/`read`
still take a roots list. The json reader lives in its own `Json` module. Neither is under
`Fsproj` any more, which is again just the F# project evaluation it is named for.

`Lock.Entry`, one per project, is `{ Csc; Evaluation; Packages }` : the `Csc` record holds identity (`Name`, `Framework`), what is compiled and its `Dependencies`; `Evaluation` and `Packages` are the provenance only the lock has. The three sections of the file  are:

- `Name` -- `AssemblyName`
- `Framework` -- the target framework this compilation is for. With every framework in one
  lock, `(Name, Framework)` and not `Name` is what identifies an entry. It sits next to `Name`
  rather than in `Evaluation` because it is identity, not evidence: it is what a script looks
  an entry up by, and a composed `csc {}` compilation (whose `Evaluation` is empty by
  contract) still has one, from `targetfwk`
- `Evaluation` -- where the answer came from; `run` never reads it, and every field is empty
  for a compilation composed from `csc {}` settings:
  - `Project` -- the project file path ("" when composed)
  - `ProjectRefs` -- `ProjectReference` items, as project files
  - `Imports` -- the msbuild files (outside the SDK) whose evaluation produced this entry, hashed
  - `Sdk` -- `NETCoreSdkVersion`, the SDK that evaluated the project
  - `SdkPin` -- `Lock.SdkPin option`, typed (`NoGlobalJson | Pinned v | RollsForward (v, policy)
    | NoVersion file`); written as `"none"`, `"exact <v>"`, `"<v> rollForward:<policy>"`, `"no
    version (<file>)"` and `""` for `None`
  - `Properties` -- a small whitelist (`AssemblyName`, `TargetFrameworkMoniker`, `LangVersion`,
    `Version`, `InformationalVersion`, `SignAssembly`, `AssemblyOriginatorKeyFile`,
    `Deterministic`, `TargetPath`, `IntermediateOutputPath`); `SdkPin` and `NETCoreSdkVersion`
    have their own fields now and are no longer in it
- `Compilation` -- what is compiled; changes with every PR (in memory these are fields of `Csc` -- `Directory`, `Options`, `Defines`, `Sources`, `Generated`, `Resources` -- the file's `"Compilation"` key groups them):
  - `Directory` -- the compiler's working directory
  - `Options` -- every argument that is not a source, a reference, an analyzer or a define, in
    msbuild's order, with the four **section markers** in place (below)
  - `Defines` -- the `/define:` symbols, split on `;`
  - `Sources` -- the source files, absolute
  - `Generated` -- msbuild-written *text* compiler inputs, by path, with content (assembly
    attributes, the derived `.editorconfig`, SourceLink's `sourcelink.json` -- `sourcelink` is
    one of `CscArgs.inputSwitches` since it names a file the compiler reads); a compiled
    `.resources` file, though also a `/resource:` input under the intermediate directory, is
    excluded here and tracked in `Resources` instead, since it is binary and
    `File.ReadAllText`ing it would corrupt it
  - `Resources` -- `.resx` files this project embeds: `(resx path, .resources output path)`
    pairs, both absolute; `run` regenerates the output from the resx (byte-identical to
    msbuild's) when it is missing, so a machine with only the lock can still reproduce it
- `Dependencies` -- what it is compiled with and against, hashed; changes rarely and is
  reviewed when it does:
  - `Compiler` -- `{ Tool; Path; Sha256; Version }`, `Version` the compiler's own product version
  - `References` -- `{ Path; Sha256; Alias }` in command-line order; `Alias` is the `alias=`
    prefix of an `extern alias` reference and "" otherwise (written to the file only when set)
  - `Analyzers` -- `{ Path; Sha256 }`
  - `Packages` -- the restore graph, `{ Id; Version; Sha512; Direct; DependsOn }` per package
    (below); in memory this is `Lock.Entry.Packages`, not part of `Csc.Dependencies`
- `Args` (member of `Csc`) -- the exact command line, rebuilt from `Options` and `Dependencies`;
  `Sources` and `Output` (from `/out:` in `Options`) are members too, nothing is stored twice

**Structured `Options` and the four section markers.** `Csc.Options` holds every
argument except the four factored sections, in msbuild's original order, with a marker string
at the position each section occupied: `"@Sources"`, `"@References"`, `"@Analyzers"`,
`"@Defines"`. `Csc.Args` rebuilds the list by expanding each marker: `@References` to one
`/reference:<alias=>path` per entry (a path containing a comma re-quoted, as msbuild quotes
it -- `CscArgs.quoteIfNeeded`), `@Analyzers` to one `/analyzer:path` per entry, `@Defines` to one
`/define:A;B`, `@Sources` to the sources. `Csc.ofArgs : string list -> Csc` does the factoring: a `/reference:`/`/r:` switch with exactly one
item goes to the references (alias split with the quote-aware `CscArgs` logic), `/analyzer:`/`/a:`
with one item to the analyzers, every `/define:`/`/d:` to `Defines` (concatenated, one marker at
the first), every source to `Sources` (one marker at the first source). A contiguous block
collapses to one marker; should msbuild ever emit two non-contiguous blocks of one kind, the
marker stays at the first and the round-trip check decides. A leading `@` in `Options` is
always a marker and never a csc response-file reference: msbuild never puts one on the compiler's
command line (the rsp is `run`'s own, written around the whole list).

*Why markers and not a canonical order.* The real locks are the argument: dataengine's have
`/warnaserror+:NU1605` **after** the sources, and the relative order of the reference, analyzer
and source blocks differs between Roslyn versions. A canonical order would have rebuilt a
different command line on a new SDK and failed every import there; markers keep msbuild's own
order without storing the flat list, so the structured form and the verbatim one are the same
information.

**The round-trip check (the fidelity guarantee: what the lock describes is what msbuild reported).** `parseImport` builds the entry
through `ofArgs`, then requires `entry.Csc.Args = msbuild's absolute args` -- otherwise it fails,
naming the project and printing `Lock.diffList` of the two lists (`- <msbuild's>` / `+
<rebuilt>`), and nothing is written. So the lock never describes a compilation other than the
one msbuild reported: "do not reconstruct" is checked at import rather than trusted. `resolve`
(composed mode) goes through the same `ofArgs` and the same check. Two `/define:` switches
would be the one case that fails by design (they fold into one section) -- msbuild emits one.

**Packages: the restore graph in the lock (decided 2026-09-24).** Right after the design-time
build, still inside `withProjectLock`, `import` reads `project.assets.json` (the
`ProjectAssetsFile` property from the same result dump) with `Nuget.readAssets` for the import's
framework and builds `Dependencies.Packages` with `Lock.packagesOf cacheRoot assets`, the cache
being the `NuGetPackageRoot` property (fallback `Roots.nugetRoot ()`): per package `Id`,
`Version` (the assets file's casing), `Sha512` (base64 `contentHash` from the cache's
`.nupkg.metadata`, "" when the cache lacks it), `Direct` (a `PackageReference` of the project's
own framework section) and `DependsOn` (the ids this package's assets entry depends on).
`type: project` entries are not packages and do not appear. Reading inside the lock is what
closes the second half of the import race (concurrent imports rewriting shared assets files): the per-variant assets copy and the `ProjectAssetsFile`
property hack are gone -- the lock carries the graph, and `Sbom.forAssembly` reads the lock only.
Trap found here: a single-`TargetFramework` project's assets keys `targets` by the *full*
framework name (`.NETStandard,Version=v2.0`), only a multi-target project uses the alias;
`Nuget.readAssets` tries both (`Nuget.frameworkFullName`).

**File format.** One entry is `{ "Name", "Framework", "Evaluation": {...}, "Compilation": {...},
"Dependencies": {...} }` under `"Entries"`; the document itself carries only `"Configuration"`,
`"Properties"` and `"Entries"`. A lock written when the framework was a property of the *file*
(a document-level `"Framework"`) still reads -- the value is distributed into every entry that
has none of its own -- and is always written back in the new shape; tokenization (`Roots.tokenizeAll`) and the
one-line-per-item style are unchanged, the file is deterministic (write, parse, write again is
byte-identical -- tested). The shape `Lock.format` produces (two-space indent per level, one
item per line; `Alias` only on a reference that has one; each package one inline object):

```json
{
  "Configuration": "Release",
  "Properties": {
    "Brand": "MESCIUS"
  },
  "Entries": [
    {
      "Name": "DataEngine",
      "Framework": "netstandard2.0",
      "Evaluation": {
        "Project": "$(ProjectRoot)/src/DataEngine/DataEngine.csproj",
        "ProjectRefs": [ ... ],
        "Imports": [
          { "Path": "$(ProjectRoot)/Directory.Build.props", "Sha256": "..." }
        ],
        "Sdk": "8.0.425",
        "SdkPin": "exact 8.0.425",
        "Properties": {
          "AssemblyName": "DataEngine", ...
        }
      },
      "Compilation": {
        "Directory": "$(ProjectRoot)/src/DataEngine",
        "Options": [ "/noconfig", ..., "@Defines", "@References", "@Analyzers", "@Sources", ... ],
        "Defines": [ "TRACE", ... ],
        "Sources": [ "$(ProjectRoot)/src/DataEngine/A.cs", ... ],
        "Generated": { "<path>": "<content>", ... },
        "Resources": { "<resx path>": "<.resources path>", ... }
      },
      "Dependencies": {
        "Compiler": { "Tool": "csc", "Path": "$(DotnetRoot)/sdk/8.0.425/Roslyn/bincore/csc.dll", "Sha256": "...", "Version": "4.11.0-3.25569.22" },
        "References": [
          { "Path": "$(NuGetPackageRoot)/...", "Sha256": "..." },
          { "Path": "...", "Sha256": "...", "Alias": "legacy" }
        ],
        "Analyzers": [ { "Path": "...", "Sha256": "..." } ],
        "Packages": [
          { "Id": "Newtonsoft.Json", "Version": "13.0.3", "Sha512": "...", "Direct": true, "DependsOn": [] }
        ]
      }
    }
  ]
}
```

(Lists are written one item per line; they are folded to `[ ... ]` above for space. `Generated`
and `Resources` are JSON objects keyed by path, `Properties` too.) A lock in the old flat format (`"Projects"` with `"Args"`) is refused
by `Lock.parse` with "lock written by an older Xake; re-import" -- nothing released used it, so
there is no reader for it. `samples/hermetic/dataengine/locks/` shows the shape on the real
fixture: 889 lines per brand, 27 `Options` (`@Defines`, `@References`, `@Analyzers`, `@Sources`
in msbuild's positions), 12 `Defines`, 113-115 `References`, 4 `Packages`.

`Lock.diff a b` covers every section: `Framework`, `Options` and `Sources` as ordered lists, `Defines`,
`ProjectRefs` as sets, `Compiler` incl. `Version`, `Evaluation.Sdk`, the hashed lists,
`Generated`/`Resources` pairs, `Packages` (added, removed, version changed, sha512 changed).
`Lock.mapPaths f` rewrites `Options` (markers left alone), `Sources`, `References` and
`Analyzers` (each dropping its hash when the path changed), `Generated` keys and both paths of
`Resources`; `Lock.mapText f`
applies `f` to `Generated` content, `Options`, `Defines` and the evaluation's property values
(`Git.tokenize` and `run`'s resolution of `$(SourceRevisionId)` both use it).

`Lock.load path` (a recipe) parses a lock file (`Lock.Document`: `Configuration`, `Properties`,
`Entries` -- the framework is on the entry), paths expanded for this machine. **`load` and
`loadWith` `needFiles` the lock themselves**: reading a lock is depending on it, so a recipe
that reads one records the `FileDep` and, when a rule produces the lock, builds it first. The
`do! need [lockFile ...]` that used to precede every `Lock.load` in a script is gone -- it was
easy to forget and impossible to notice the absence of. `save`/`saveWith` deliberately do not:
writing a file is not depending on it, and a `need` there would be wrong in both of their uses
(a rule writing the lock as its own target would depend on itself, and `csc { lock }` writes
its lock from inside the compile recipe, where the lock is not a target at all). Both `load`
and `save` resolve a relative path against `ExecOptions.ProjectRoot`, the way a target path is
resolved.

`Lock.entry name lock` looks an entry up by assembly name or project file name and now fails,
naming the frameworks, when the name matches more than one entry; `Lock.entryFor framework name
lock` is the unambiguous lookup and the one a rule that carries the framework should use.

**`$(SourceRevisionId)` and the commit sha.** The SDK's
built-in SourceLink writes `sourcelink.json` (a Bitbucket/GitHub-shaped URL template with the
commit sha in it) and passes it on `/sourcelink:<path>` -- `sourcelink` is one of
`CscArgs.inputSwitches`, so that file lands in `Generated` like any other msbuild-written text
input. Left as is, its content would carry the commit sha, and so the lock's content -- and the
lock file itself -- would change on every commit even though the compilation did not change.
`parseImport` asks msbuild for the `SourceRevisionId` property (in `wantedProperties`) and, when
it is non-empty, calls `entry |> Lock.mapText (Git.tokenize sha)` (pure): every
occurrence of `sha` in `Generated` content, `Options`, `Defines` and the evaluation's `Properties`
values is replaced with the literal token `$(SourceRevisionId)`. The sha itself is **not** recorded anywhere in the lock --
`SourceRevisionId` is asked from msbuild only to drive this substitution, never added to the
`Properties` whitelist. `Lock.compileWith` (step 3 of "Replaying a lock") resolves the token back at
compile time, from the project's own repository (`Git.headSha entry.Csc.Directory`) -- see below.

`Project.import` also `needFiles`s `Git.headFiles (project's directory)` -- `.git/HEAD` and the
ref file (or `packed-refs`) it resolves through -- for every project, whether or not it uses the
token: a token in the lock's *content* is commit-independent by design, so nothing else tracked
by the import changes on a new commit, and without this the lock would go stale (compile with an
out-of-date resolved sha) instead of re-importing.

**`Git` (`Xake.Hermetic.Dotnet.Git`, `Git.fs`).** Reads `.git` directly, no `git` executable:
`Git.headSha dir : string option` and `Git.headFiles dir : string list` both walk up from `dir`
for a `.git` entry (stopping at the filesystem root) -- a **directory** (an ordinary checkout:
`HEAD` lives there, and refs resolve against the same directory unless a `commondir` file says
otherwise) or a **file** (a linked worktree: `gitdir: <path>` names the worktree's own private
git directory, which holds its own `HEAD` but a `commondir` file pointing at the main
repository's `.git`, where `refs/` and `packed-refs` actually live). `HEAD` is either symbolic
(`ref: refs/heads/<name>\n`, resolved against a loose ref file under the common directory, or a
`packed-refs` line `<sha> <refname>` when there is no loose file) or detached (the sha directly).
`headFiles` returns exactly the files that change when the commit does -- `HEAD` alone for a
detached HEAD, `HEAD` plus the loose ref or `packed-refs` for a symbolic one -- so `import` can
`needFiles` them; `headSha` returns the sha itself, for `run`. Both return `[]`/`None` when `dir`
is not inside a repository.

**SDK pin check.** `Project.sdkPin` (pure) walks up from each project's directory for a
`global.json` and reads `sdk.version`/`sdk.rollForward`, producing a `Lock.SdkPin`:
`NoGlobalJson`, `Pinned version` (only `rollForward: "disable"` counts as pinned), `RollsForward
(version, policy)` (any other policy, or the absent-policy default `latestPatch`), or `NoVersion
file` (a `global.json` with no `sdk.version`). `import` computes it per project before calling
`parseImport`, which records it typed in `Evaluation.SdkPin` (written as `Lock.sdkPinText`, read
back with `Lock.parseSdkPin`), next to `Evaluation.Sdk`. When the pin is anything but `Pinned`,
`import` warns once per project: `"'<name>': the SDK is not pinned (<pin>) -- the lock's
compiler (<version>, SDK <sdk>) will drift with every SDK the machine picks; pin it with
global.json { sdk: { version, rollForward: "disable" } }"`. When it is `Pinned v` but msbuild's
`NETCoreSdkVersion` differs from `v` -- the pinned SDK is not installed and a different one ran
-- it warns separately with both versions named.

The project-reference pattern, from `import.fsx`:

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

let unbuilt = project.Csc.Dependencies.References |> List.filter (fun r -> r.Sha256 = "") |> List.map (fun r -> r.Path) |> Set.ofList
let mapped = project |> Lock.mapPaths (fun p -> if unbuilt.Contains p then outputOf p else p)

do! need (unbuilt |> Set.toList |> List.map (outputOf >> relative))
do! Lock.compile mapped
```

A project reference in the lock is unhashed and points at the referenced project's own build
output (not built yet at import time). `Lock.mapPaths f entry` rewrites every path the
entry's `Options`, `Sources`, `References`, `Analyzers`, `Generated` and `Resources` keys carry through `f`;
a rewritten
`Hashed` entry loses its hash, since the recorded hash was computed for the old path. The script
maps those references to the path its own rule will produce them at, `need`s those targets
first, and only then runs `Lock.compile mapped` -- the runner itself does not `need` the
mapped outputs, it only `needFiles` what the (already-mapped) args name.

## Recording a lock from composed `csc` settings

`Project.import` produces a `Lock.Entry` from an msbuild project; `csc { ...; resolve }` produces
the `Csc` of one from composed settings (`Csc.ofSettings` for record syntax), and `Lock.ofCsc`
wraps it in an entry with an empty `Evaluation` and no `Packages`, so a lock-recording rule can
write it out the way an import rule does.
The API:

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

let! c = csc { src !!"*.cs"; out (File.make "app.dll"); resolve }   // Csc
do! Lock.record "locks/app.json" c
```

```fsharp
module Csc =
    val ofSettings : CscSettingsType -> Recipe<ExecContext, Csc>

module Lock =
    val ofCsc : Csc -> Entry
    val rehash : Entry -> Entry
    val diff : Entry -> Entry -> string list
```

`resolve` produces no temp files of its own to clean up (see the composed-mode resx paragraph
above), so a `Csc` it returns, `.resx` resources included, is compilable and recordable as is.
The record is `Csc` and the operations are `Csc.ofSettings`/`Csc.run`/`Csc.compile` because F#
does not let a `module` and a `let`-bound function share one name (`FS0039`, verified) -- which
is why the 3.3 function `Csc settings` went (`Csc.compile settings` replaces it).

**`Lock.rehash entry`** fills `Sha256` for every hashed entry (`References`, `Analyzers`,
`Imports`) and for `Compiler` (its `Version` too, when empty), from what is on disk right now
(`Csc.sha256`, which gives "" for a file that does not exist; `Hash.sha256` itself throws). `Csc.ofSettings` never hashes -- hashing every reference on every composed compile
would tax the common case for nothing -- so a lock-recording rule calls `rehash` itself, once,
after `resolve`; the cost is then paid only when that rule reruns, like everything else
in Xake.

**`Lock.diff a b`** is a pure, human-readable comparison of two lock entries of the same
project: `[]` means identical. In order: `Framework` (`~ Framework: <a> -> <b>`), then `Options` and `Sources` as ordered lists (bare `+`/`-`
lines from an LCS diff -- a moved argument shows as a removal at its old position and an
addition at its new one, there being no separate "moved" marker in an ordered diff), `Defines`
as a set, `Compiler` (`Path`/`Sha256`/`Version`, one line per differing field), `Evaluation.Sdk`,
each hashed list (`References`, `Analyzers`, `Imports`) by path (added, removed, or `~ <label>
<path>: <old> -> <new>` when both sides have a hash and they differ), `Generated`/`Resources` by
key (added, removed, or `~ <label> <key>: content changed`), `ProjectRefs` as a set
(added/removed), and `Packages` by id (`+ Package Id@Version`, `- Package Id@Version`, `~ Package
Id: <old> -> <new>` for a version change, `~ Package Id@Version: sha512 changed`). This is the primitive `csc { lock "path" }` (below) and `Lock.verify` are built on; no `Policy`
wiring exists. `Lock.diff` stays usable stand-alone (e.g. from an
fsx-level "verify" rule).

Tests: `src/hermetic.tests/LockDiffTests.fs` (`rehash`, `diff` identical and with changes, pure, no
msbuild/compiler needed); `src/hermetic.tests/FromLockTests.fs`, `Csc.ofSettings resolves composed
settings into a hashable, round-trippable lock` (Integration: resolves a trivial library,
asserts `Sources`/`Args`/`Compiler.Path`/empty reference hashes, then `Lock.rehash` and a
`Lock.format`/`Lock.parse` round trip).

## Locking composed settings: `lock`

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

"out/app.dll" ..> csc {
    src !!"src/*.cs"
    grefs ["System.dll"]
    targetfwk "net-4.6.2"
    out (File.make "out/app.dll")
    lock "locks/app.json"
}

"update-locks" => recipe {
    let! c = csc { src !!"src/*.cs"; grefs ["System.dll"]; targetfwk "net-4.6.2"
                   out (File.make "out/app.dll"); resolve }
    do! Lock.record "locks/app.json" c
}
```

`lock "<path>"` is the migration path for an existing script: a tuned `csc { }` block
stays where it is and gains a lock. It is a custom operation defined next to the lock, not in the
base builder (`CscLockBuilder` in `Lock.fs`, an extension of `CscSettingsBuilder` with its own
`Run`). Only `packageroot` and `norestore` may follow it (below); the block then means
`Csc.ofSettings`, `Csc.runOptions`, `Lock.buildWith`. The same thing without the sugar is
`Lock.build "locks/app.json" c` on a `Csc` from `resolve`. The path is relative to the project
root, like every other target path, or absolute. There is no `Lock` field on `CscSettingsType`.

The settings remain the source of truth for *what* is compiled (`Csc.ofSettings` runs on every
build); the lock decides whether this is the compilation that was recorded. An update is always
explicit (the user's decision, 2026-09-24: no "follow the settings and warn" default).

### What happens, step by step

On a developer machine:

1. **The first build records the lock.** There is no lock file yet: the resolved compilation is
   hashed, written to `locks/app.json` and compiled. Commit the lock with the change that
   introduced it.
2. **The next builds compile the lock.** The settings still match it, so the *recorded* entry is
   compiled and the lock file is not touched. Its hashes gate the build: a reference swapped on
   disk after recording fails the hash check, although the settings did not move.
3. **Drift fails the build.** When the settings move away from the lock (a source added, an
   option changed, another reference), the build fails with the differences and the two ways to
   update:

   ```
   'app': the resolved compilation differs from the lock 'locks/app.json':
   + $(ProjectRoot)/src/Extra.cs
   Update the lock deliberately: delete 'locks/app.json', or run the target that calls
   Lock.record "locks/app.json".
   ```

   Run `update-locks` (or delete the lock and build), review the lock's diff, commit it. Paths in
   the message carry the same tokens as the lock file (`$(ProjectRoot)`, `$(NuGetPackageRoot)`,
   `$(DotnetRoot)`), so it reads the same on every machine.

On CI:

4. **The committed lock is compiled.** The settings match it; the packages it names are restored
   (into `packageroot` when given, below) and the recorded entry is compiled, its hashes checked.
5. **Drift fails the build**, with the same message as step 3: CI never updates a lock.
6. **A missing lock fails the build**, before anything is recorded or compiled:

   ```
   'app': the lock 'locks/app.json' is not there. Under CI a lock is never recorded: record it
   on a developer machine (build once, or run the target that calls Lock.record
   "locks/app.json", e.g. update-locks) and commit it.
   ```

   Recording it on the runner would pass the build and leave a lock that exists only there.
   `nofailonerror` (`FailOnError = false`) does not turn this into a warning -- it would be a way
   for CI to pass without a lock.

`nofailonerror` turns the drift failure (steps 3 and 5) into a warning and compiles the
**resolved** entry: the settings are the source of truth, and nothing is written.

The comparison is structural, not hash-based: the resolved side never hashes anything
(hashing every reference on every compile would tax the common case), and both `diffHashed`
and `diffCompiler` skip a hash that is empty on either side -- an empty hash means "not
computed", not "zero bytes". So `Lock.diff recorded resolved` reports what the settings and the
filesets say: sources, options, defines, reference/analyzer paths, the compiler path. The
printed lines are `Lock.diffText roots`, the same diff with every path tokenized against the
roots the lock is written with; `Lock.diff` itself keeps the expanded paths.

**Updating.**

| Way | What it is |
|---|---|
| `dotnet fsi build.fsx -- -- update-locks` | the script's own phony target calling `Lock.record`; the normal way |
| `rm locks/app.json` | a missing lock is recorded on the next build -- not under CI (step 6) |

`Lock.record : string -> Csc -> Recipe<ExecContext, unit>` rehashes and overwrites
the lock, compiling nothing. `Lock.verify : string -> Csc -> Recipe<ExecContext, string list>`
returns the same diff `lock` fails on (tokenized paths; an empty list means the lock is
current), writing nothing and compiling nothing -- scenario 3 of `lock-from-settings.md` as a
stand-alone check, e.g. a `check-locks` target in CI. A lock that does not exist fails `verify`
with a message naming the path and `Lock.record` (it used to fail with the engine's
`Neither rule nor file is found`). `Lock.compile` remains the entry point for a lock that came
from `Project.import`.

### The CI flag

Whether a build is under CI is decided once per build, by `Lock.underCi : Recipe<ExecContext,
bool>`:

| Source | Value |
|---|---|
| script variable `CI` (`-d CI=on`, `xakeScript { var "CI" "on" }`, `Vars`), when set | `on`/`true`/`yes`/`1`: CI; `off`/`false`/`no`/`0`: not CI (case-insensitive); anything else fails |
| otherwise the environment variable `CI` (set by GitHub Actions, GitLab CI, Azure Pipelines and most others) | non-empty and not `0`/`false`: CI |
| neither | not CI |

Same convention as `NETFX` and `CSC_SERVER`: `-d CI=off` builds as a developer machine would
(records a missing lock) even on a runner, `-d CI=on` rehearses the CI behaviour locally. Reading
the flag (`getVar`, then `getEnv`) makes it a tracked dependency of the target, so flipping it
reruns the target.

`Lock.buildWith` reads the flag itself, so `Lock.build`, `csc { lock }` and every script calling
`buildWith` with options of its own get the CI behaviour; there is no option for it. A script
that must record on CI opts out with `-d CI=off`. The flag only says whether an *absent* lock may
be written; a present lock is only ever rewritten by `Lock.record` or by deleting it.

### Restore options: `packageroot`, `norestore`

`csc { lock }` compiles through `Lock.buildWith`, whose restore step needs to know where the
lock's packages live and whether a missing one may be downloaded (`Restore.Options`,
[restore.md](restore.md)). Two operations set that, and they come **after** `lock`:

```fsharp
"out/app.dll" ..> csc {
    src !!"src/*.cs"
    grefs ["System.dll"]
    targetfwk "net-4.6.2"
    out (File.make "out/app.dll")
    lock "locks/app.json"
    packageroot ".packages"     // Restore.into ".packages": a folder of the build's own
    norestore                   // Restore.Options.Enabled = false: never download
}
```

| Operation | Effect |
|---|---|
| `packageroot "<dir>"` | the package folder, relative to the project root or absolute (`Restore.into`); default the machine cache |
| `norestore` | a package the folder lacks is not downloaded: a warning (`automatic restore is off`), then the hash check fails naming every missing file, before anything is compiled |

Why after: `lock` turns the block's state from `CscSettingsType` into `CscLocked` (path,
settings, package root, restore flag), and these two are operations on that state. Putting them
before `lock` would need fields on `CscSettingsType`, which knows nothing about restore.

With a package root, `buildWith` compares and writes the lock with the build's ordinary roots
(so the lock file and the drift check do not depend on the folder) and re-roots the entry it
compiles at the folder, in memory: the entry is written with the ordinary roots and read back
with `Roots.packageRootOverride` (`Lock.format`, then `Lock.parse`), so `$(NuGetPackageRoot)/...`
names files in `.packages/`, which the restore fills. The same holds for `Lock.buildWith` called
with `Restore = { ... PackageRoot = Some dir }`.

**The lock file is not a target of the engine on this path.** It is written from inside the
compile recipe, which is what lets the `csc { }` block stay in place; the engine neither
`need`s it nor rebuilds it, and the update target is an ordinary phony action of the script's
own. The alternative design (the lock as a real file target, built by a
rule of its own from `csc { ...; resolve }`) still stands for scripts that want it and needs nothing
new. As before, one lock file per compilation: two `csc` calls sharing one path would
read-modify-write the same file and is an authoring error, not something the library guards.

**Sharing the settings.** `csc { ...; resolve }` returns the `Csc`, so one block can feed both
the compile and `Lock.record`/`Lock.verify` -- define the block once as a recipe and use its
result. (Path B of §9, a second builder `cscSettings { ... }`, is not needed any more.)

Tests: `src/hermetic.tests/CscLockTests.fs` (18, all Integration) -- first build records and compiles,
second build leaves the lock byte-identical, an added source fails with the diff, `Lock.record`
overwrites and the next build passes, a reference tampered after recording fails the hash check,
and `Lock.verify` reports the difference without writing or compiling; under CI (script
variable) a missing lock fails with the message above and nothing is recorded or compiled,
`FailOnError = false` does not help, the environment variable `CI=true` fails it through
`csc { lock }`, `-d CI=off` with `CI=true` in the environment records, not under CI records,
drift under CI fails as before, an unrecognized `CI` value fails; drift messages and `verify`
lines carry `$(ProjectRoot)`, not the absolute root; `verify` on a missing lock; `norestore`
with an empty package folder fails before compiling; `packageroot` restores into a throwaway
folder and compiles. The tests that record set the script variable `CI=off`, so the suite also
passes with `CI=true` in the environment.

## Behaviour notes

- A `Lock.Entry` in memory has absolute paths throughout; `Lock.save`/`format` tokenizes
  them against known roots (project root, NuGet package cache, SDK) so the file on disk is
  portable and diffable, and `Lock.load`/`parse` expands them back on load.
- A hash mismatch reports every mismatching path at once (`Csc.run`, see
  [../csc-syntax.md](../csc-syntax.md)).
