# `csc {}` as it stands

`csc {}` is the C# compiler task. It builds one `Lock.Entry` -- a project's exact compiler
command line, plus what it references and how to check it -- and hands that to a single runner.
Settings are intent, `Lock.Entry` is the resolved compilation: there are two ways to arrive
at a `Lock.Entry` (compose one from settings, or bring one in from a lock) but only one thing
that ever shells out to the compiler. (Before Stage B, 2026-09-24, the entry was `Lock.Project`
and the lock file `Lock.File`; the format then was a flat record with a verbatim `Args` list.)

## Composed mode

```fsharp
"temp/helloworld.exe" ..> csc {
    targetfwk "net-4.6.2"
    src !!"helloworld.cs"
    grefs ["System.dll"]
}
```

(`samples/fullframework.fsx`; `src/tests/DotnetTasksTests.fs`, `runs csc task (full test)`, shows
the same shape with `out`, `Src`, `TargetFramework`, `RefGlobal` set directly on the record.)

`Csc` calls `resolve`, which turns the settings below into a
`Lock.Entry` at recipe time -- same argument order the task has always produced, save that
references are now spelled `/reference:` rather than `/r:` -- and passes it to the runner. Every custom operation of `CscSettingsBuilder`:

| Keyword | Argument | Does | Default |
|---|---|---|---|
| `platform` | `TargetPlatform` | `/platform:` | `AnyCpu` |
| `target` | `TargetType` | `/target:`, resolved from the output name when `Auto` | `Auto` |
| `targetfwk` | `string` | target framework to compile against; see [`docs/dotnet-build.md`](../../dotnet-build.md) | `null` (falls back to the `NETFX-TARGET` var) |
| `out` | `File` | output file; `/out:` | `File.undefined` (task infers a target file) |
| `src` | `Fileset` | source files | `Fileset.Empty` |
| `ref` | `Fileset` | adds to the reference fileset (`+`) | -- |
| `refif` | `bool * Fileset` | adds the fileset conditionally (`+?`) | -- |
| `refs` | `Fileset` | replaces the reference fileset | -- |
| `grefs` | `string list` | GAC/framework-global references, resolved through the framework's `AssemblyDirs` when `targetfwk`/`NETFX-TARGET` is set | `[]` |
| `resources` | `ResourceFileset` | adds one embedded-resource fileset (prepended) | -- |
| `resourceslist` | `ResourceFileset list` | adds several at once | -- |
| `define` | `string list` | `/define:`, joined with `;` | `[]` |
| `unsafe` | `bool` | `/unsafe` | `false` |
| `cscpath` | `string` | compiler executable, bypassing framework discovery entirely | `None` |
| `toolset` | `string` (package version) | compiler from `Microsoft.Net.Compilers.Toolset/<version>` in the NuGet cache instead of the SDK's; see below | `None` |
| `lock` | `string` | records this compilation in a lock file at that path (project-root-relative or absolute) and, once it exists, refuses to compile anything that differs from it; see "Locking composed settings" below | `None` |
| `args` | `string list` | raw extra switches, appended last (`CommandArgs`) | `[]` |
| `nofailonerror` | (none) | do not fail the build on a compile error | `FailOnError = true` |

`out`, `platform`, `unsafe`, `nostdlib` (from `targetfwk`) and `define` are composition
settings; replaying a lock (`CscLock.compile`, below) does not go through them at all. The
argument list `resolve` builds is,
in order: `/noconfig` (when the target framework requires it), `/nologo`, `/target:`,
`/platform:`, `/unsafe`, `/nostdlib+`, `/out:`, `/define:`, sources, `/reference:` refs, global
refs, `/res:`, then `CommandArgs`. The list then goes through `Lock.Compilation.ofArgs` exactly
like an imported one (below) and gets the same round-trip check; it always passes, there being
one item per switch.

**Composed-mode `.resx` resources (2026-09-23).** A `resources`/`resourceslist` fileset entry
that names a `.resx` file is not compiled by `resolve` itself -- unlike an ordinary
embedded-resource file (already the file the compiler reads), a `.resx` needs turning into a
`.resources` first, and `resolve` used to do that eagerly into a temp file with a random name,
deleted once the compile was done. That left nothing for `CscLock.resolve` to hand back: a lock
recorded from settings with `.resx` resources had `/res:` arguments naming files that no longer
existed. Instead `resolve` records a permanent
`(resx, .resources)` pair in `Lock.Compilation.Resources` --
`<ProjectRoot>/obj/xake/<assembly name>/<manifestName>` where `manifestName` is the `/res:`
logical name `Impl.makeResourceName` computes (e.g. `Sample.Application.Strings.resources`) --
and emits `/res:<resourcesPath>,<manifestName>`, exactly the shape `Project.import` already
produces for an imported project's resx. `run`'s existing resource step (see below) then compiles
it when the output is missing and `needFiles` the resx itself, so the engine decides when a resx
edit reruns the compile, not `resolve`. Non-resx resources are unaffected -- they were already
the file the compiler reads and stay a plain `/res:` file input. `resolve` no longer produces any
temp files of its own; the only temp file `run` still cleans up is its own response file.

## Compiler sources

### Composed mode: three sources, first match wins

| Setting | Compiler | Notes |
|---|---|---|
| `cscpath "<exe>"` | that executable | no framework or package lookup at all |
| `toolset "<version>"` | `csc.dll` from `microsoft.net.compilers.toolset/<version>/tasks/netcore/bincore` in the NuGet cache | the package is restored into the cache if missing (same mechanism as the reference-assembly packages); still missing afterwards fails the build |
| neither | whatever `targetfwk` / `NETFX-TARGET` resolves through `DotNetFwk.locateFramework` | the SDK's `csc.dll`, or `csc.exe` / `mcs` on a framework that ships one |

- `toolset` replaces only the compiler. References, defines and environment variables still
  come from the targeted framework.
- The lock records what ran in `Dependencies.Compiler`: `Path` and `Sha256` name the compiler
  file, `Version` is the compiler's own product version read from the file (`4.12.0-3.24572.7`
  for the toolset package, `4.11.0-3.25569.22` for SDK 8.0.425's; for the SDK's native `csc`
  launcher the `csc.dll` next to it is read). The SDK that evaluated the project is
  `Evaluation.Sdk`, empty for a composed compilation -- the two used to share one overloaded
  `Compiler.Sdk` field.
- `CscLock.compile` restores a toolset package the lock names when it is not on this machine yet
  (`ensureCompilerAvailable`, see below) -- the same restore mechanism as `toolset` above, just
  triggered by replaying a lock instead of by the `toolset` operation.

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

## Replaying a lock: `CscLock.compile`

```fsharp
do! CscLock.compile mapped                          // mapped : Lock.Entry
do! CscLock.compileWith { RunOptions.Default with FailOnError = false } mapped
```

`CscLock.compile` hands a resolved compilation straight to the runner, with no extra env vars
and no temp files: the project's own `Args` is the whole compilation. It is an entry point of
its own, not a mode of `csc {}` (changed 2026-09-24, conceptual-review.md 2.2): as a `fromlock`
setting inside the record it made `csc { fromlock p; src !!"*.cs" }` compile while silently
ignoring `src` and every other composition setting, and the type said nothing about it. What
does apply to both entry points is `RunOptions = { FailOnError; CscPath }` -- how the runner
behaves, not what it compiles; `Csc` builds one from the settings' own `FailOnError`/`CscPath`,
and `CscLock.compile` uses `RunOptions.Default` (`compileWith` takes them explicitly). The
module is `CscLock`, not `Csc`, because F# will not let a module and the `let`-bound function
`Csc` share a name (see the doc comment on `CscLock.resolve`).

The runner (`run` in `Dotnet.csc.fs`, shared by both entry points) does, in order:

1. **Makes the compiler available** (`ensureCompilerAvailable`, raised 2026-09-23), before
   anything else touches it, so the hash check below has something to check:
   - already on disk (`Dependencies.Compiler.Path` exists) -- nothing to do.
   - under `$(NuGetPackageRoot)` -- the path names a `Microsoft.Net.Compilers.Toolset`-shaped
     package (`<root>/<packageId>/<version>/...`) that just is not restored yet on this
     machine: `trace Info "restoring compiler package %s %s"` and restore it, the same
     mechanism the composed mode's `toolset` uses (factored into a shared
     `restoreToolsetCompiler`). Still missing afterwards fails with `'<name>': the compiler
     <path> is not available and restoring <id> <version> did not provide it`. A hash mismatch
     *after* a successful restore is left to step 4 -- it means a different package build, not
     a missing one.
   - under `$(DotnetRoot)/sdk/<version>/` -- an SDK this machine does not have; nothing to
     restore, so this fails immediately: `'<name>': the lock names the compiler of SDK
     <version> (<path>), which is not installed; install that SDK or re-import with the
     installed one` (or, under `$(DotnetRoot)` but not `sdk/`, a generic "not installed"
     message naming the path).
   - anywhere else -- `'<name>': the compiler <path> named by the lock does not exist`.

   Every failure here goes through the same `trace Error` + `FailOnError`-gated `failwith` shape
   as the hash-mismatch check (step 6) and `Impl.failOnExitCode`.
2. **Resolves `$(SourceRevisionId)`** (raised 2026-09-23, "Lock stability"): the lock never
   carries a commit sha itself (see `Generated` and `Project.tokenizeRevision` below) -- when
   `Generated`'s content, `Options` or `Defines` carries the literal token `$(SourceRevisionId)`,
   it is replaced here (`Lock.mapText`) with `Git.headSha compilation.Directory` (walking up from the project's own
   directory for a `.git`; no `git` executable). No token anywhere -- nothing happens, the
   composed mode included, since it never populates `Generated`. A token present but no
   repository found (or `HEAD` unresolvable) fails with `'<name>': the lock needs
   $(SourceRevisionId) but no git repository was found at or above '<dir>' -- a lock that needs
   a revision must be compiled in a repository`, the same `trace Error` + `FailOnError` shape as
   the other checks here.
3. Writes back every `Generated` file that is missing or whose content differs from what is on
   disk -- the resolved project is the source of truth for msbuild-generated inputs like
   `AssemblyInfo.cs` (and, now, `sourcelink.json` with its token already resolved by step 2). The
   composed mode never populates `Generated`, so this is a no-op there.
4. Creates the output directories, for every path `CscArgs.outputs entry.Args` names.
5. `needFiles` the compiler itself (raised 2026-09-23, conceptual-review.md 2.4): the hash check
   in step 7 covers the compiler's path, but nothing before this made it a tracked
   dependency, so an SDK or toolset update that changed `csc.dll`'s bytes left the target looking
   up to date and the hash check never ran. `needFiles [compiler.Path]`, right after
   `ensureCompilerAvailable`, closes that.
6. `needFiles` every resx in `Compilation.Resources` (so a resx edit rebuilds the dll) and, for each
   `(resx, resources)` pair, compiles the resx to that `.resources` path with `Xake.Dotnet.Resx`
   when the output is **missing** -- so a machine with only the lock, or a cleaned `obj/`, still
   ends up with the exact file the recorded `/resource:` switch names. This is the same step for
   both modes now (2026-09-23): the composed mode's `resolve` records its own `.resx` resources
   here too (see the composed-mode resx paragraph above), at a permanent path under
   `obj/xake/<name>/`, instead of compiling them itself into a temp file at recipe time. This
   dropped a `.resources`-vs-`.resx`
   timestamp comparison that used to gate regeneration as well (conceptual-review.md 2.3): that
   was a second rebuilder living next to the engine's -- the engine already decides whether this
   recipe runs at all, from the `FileDep` `needFiles` puts on the resx, so `run` re-deciding with
   its own mtime check was redundant and, worse, implied a staleness check the lock does not
   actually make. **Gate semantics, stated plainly**: the engine decides *whether* a recipe runs
   (dependency tracking, `needFiles`/`FileDep`); `run`'s hash and existence checks only decide
   whether to *fail* a run that the engine already started -- they never trigger one. A swapped
   file with an unchanged timestamp is still caught (the hash check runs every time `run` runs
   for other reasons); a swapped file that also updates the timestamp is caught because the
   timestamp change is what makes the engine run `run` in the first place.
7. Verifies the SHA-256 of every hashed reference, analyzer, and the compiler itself against what
   is on disk. An empty recorded hash means "not checked" (the composed mode never records one,
   and neither does an unbuilt project reference). Any mismatch is collected and reported
   together, then fails the build when `FailOnError` is set (`XakeException`, message containing
   the path).
8. `needFiles` on `CscArgs.inputs entry.Args` -- every file any input switch names, plus the
   sources. For the composed mode this now covers everything the args name, including the
   framework's global references, not only sources/refs/resources.
9. Writes the arguments to a response file, with `Impl.escapeArgument`, and runs the compiler.
   `/noconfig` cannot go inside the rsp -- csc warns `CS2023` and ignores it there -- so it stays
   on the command line and everything else goes into `@<rspfile>`.
10. Picks the compiler: `settings.CscPath` wins if set; otherwise, when the project's recorded
    compiler path ends in `.dll`, it runs through `dotnet <path>`; otherwise the path is run
    directly (a native launcher, e.g. the SDK's `csc` apphost).

The rsp file is deleted once the compiler exits, success or failure -- the only temp file `run`
produces now that the composed mode's `.resx` resources are permanent outputs (see above),
not temp files.

## Where a `Lock.Entry` comes from

`Project.import` (`src/dotnet/Project.fs`) runs an msbuild design-time build per project and
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
| `Roots` | extra `(token, absolute path)` roots to tokenize against, beyond the built-in three; default `[]` |

**One restore, then a design-time build per framework (2026-09-23).** Three msbuild phases per
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
recorded an entry with `packages 0` (verify-dataengine.md §6). The fix is structural: the
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
with the built-in three via `Roots.withExtra`, which validates each token is well-formed
(`$(Name)`), is not one of the built-ins, and that its path is absolute -- failing early rather
than writing a lock that silently didn't tokenize -- and keeps the longest-root-first order
`Roots.builtin` already relies on. A script reading such a lock back must pass the same roots:
`Lock.loadWith extraRoots path` inside a recipe, or `Lock.readWith (Roots.withExtra projectRoot
extra) path` outside one. `Lock.load`/`save` use the built-in three only.

**Where the project root comes from (review §2.5).** `$(ProjectRoot)` is the engine's
`ExecOptions.ProjectRoot` -- what `need`, `getFiles` and rule matching already resolve against --
not the process's current directory. The `Roots` module holds the whole thing: `nugetRoot ()`,
`dotnetRoot ()`, `builtinTokens`, the pure `builtin projectRoot` / `withExtra projectRoot extra`,
and the recipes `current` / `currentWith extra` that read the root from `getCtxOptions()`. The
recipe-level lock and evaluation entry points (`Lock.load`/`loadWith`/`save`/`saveWith`,
`Fsproj.load`) are recipes for exactly this reason; the pure `writeWith`/`parseWith`/`readWith`
still take a roots list. The json reader lives in its own `Json` module. Neither is under
`Fsproj` any more, which is again just the F# project evaluation it is named for.

`Lock.Entry`, one per project, is three records (conceptual-review.md 2.1, Stage B 2026-09-24):

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
- `Compilation` -- what is compiled; changes with every PR:
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
    (below)
- `Args` (member) -- the exact command line, rebuilt from `Compilation` and `Dependencies`;
  `Sources` and `Output` (from `/out:` in `Options`) are members too, nothing is stored twice

**Structured `Options` and the four section markers.** `Compilation.Options` holds every
argument except the four factored sections, in msbuild's original order, with a marker string
at the position each section occupied: `"@Sources"`, `"@References"`, `"@Analyzers"`,
`"@Defines"`. `Entry.Args` rebuilds the list by expanding each marker: `@References` to one
`/reference:<alias=>path` per entry (a path containing a comma re-quoted, as msbuild quotes
it -- `CscArgs.quoteIfNeeded`), `@Analyzers` to one `/analyzer:path` per entry, `@Defines` to one
`/define:A;B`, `@Sources` to the sources. `Lock.Compilation.ofArgs : string list -> Compilation *
Reference list * Hashed list` does the factoring: a `/reference:`/`/r:` switch with exactly one
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

**The round-trip check (brief §8c's fidelity guarantee).** `parseImport` builds the entry
through `ofArgs`, then requires `entry.Args = msbuild's absolute args` -- otherwise it fails,
naming the project and printing `Lock.diffList` of the two lists (`- <msbuild's>` / `+
<rebuilt>`), and nothing is written. So the lock never describes a compilation other than the
one msbuild reported: "do not reconstruct" is checked at import rather than trusted. `resolve`
(composed mode) goes through the same `ofArgs` and the same check. Two `/define:` switches
would be the one case that fails by design (they fold into one section) -- msbuild emits one.

**Packages: the restore graph in the lock (decided 2026-09-24).** Right after the design-time
build, still inside `withProjectLock`, `import` reads `project.assets.json` (the
`ProjectAssetsFile` property from the same result dump) with `Nuget.readAssets` for the import's
framework and builds `Dependencies.Packages` with `Project.packages cacheRoot assets`, the cache
being the `NuGetPackageRoot` property (fallback `Roots.nugetRoot ()`): per package `Id`,
`Version` (the assets file's casing), `Sha512` (base64 `contentHash` from the cache's
`.nupkg.metadata`, "" when the cache lacks it), `Direct` (a `PackageReference` of the project's
own framework section) and `DependsOn` (the ids this package's assets entry depends on).
`type: project` entries are not packages and do not appear. Reading inside the lock is what
closes import-race.md's second half: the per-variant assets copy and the `ProjectAssetsFile`
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
byte-identical -- tested). A lock in the old flat format (`"Projects"` with `"Args"`) is refused
by `parseWith` with "lock written by an older Xake; re-import" -- nothing released used it, so
there is no reader for it. `samples/hermetic/dataengine/locks/` shows the shape on the real
fixture: 889 lines per brand, 27 `Options` (`@Defines`, `@References`, `@Analyzers`, `@Sources`
in msbuild's positions), 12 `Defines`, 113-115 `References`, 4 `Packages`.

`Lock.diff a b` covers every section: `Framework`, `Options` and `Sources` as ordered lists, `Defines`,
`ProjectRefs` as sets, `Compiler` incl. `Version`, `Evaluation.Sdk`, the hashed lists,
`Generated`/`Resources` pairs, `Packages` (added, removed, version changed, sha512 changed).
`Lock.mapPaths f` rewrites `Options` (markers left alone), `Sources`, `References` (dropping the
hash when the path changed), `Analyzers`, `Generated` keys and `Resources`; `Lock.mapText f`
applies `f` to `Generated` content, `Options`, `Defines` and the evaluation's property values
(`tokenizeRevision` and `run`'s resolution of `$(SourceRevisionId)` both use it).

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

**`$(SourceRevisionId)` and the commit sha (raised 2026-09-23, "Lock stability").** The SDK's
built-in SourceLink writes `sourcelink.json` (a Bitbucket/GitHub-shaped URL template with the
commit sha in it) and passes it on `/sourcelink:<path>` -- `sourcelink` is one of
`CscArgs.inputSwitches`, so that file lands in `Generated` like any other msbuild-written text
input. Left as is, its content would carry the commit sha, and so the lock's content -- and the
lock file itself -- would change on every commit even though the compilation did not change.
`parseImport` asks msbuild for the `SourceRevisionId` property (in `wantedProperties`) and, when
it is non-empty, calls the pure `Project.tokenizeRevision sha entry : Lock.Entry`: every
occurrence of `sha` in `Generated` content, `Options`, `Defines` and the evaluation's `Properties`
values is replaced with the literal token `$(SourceRevisionId)`. The sha itself is **not** recorded anywhere in the lock --
`SourceRevisionId` is asked from msbuild only to drive this substitution, never added to the
`Properties` whitelist. `run` (`Dotnet.csc.fs`, step 2 of the runner) resolves the token back at
compile time, from the project's own repository (`Git.headSha project.Directory`) -- see below.

`Project.import` also `needFiles`s `Git.headFiles (project's directory)` -- `.git/HEAD` and the
ref file (or `packed-refs`) it resolves through -- for every project, whether or not it uses the
token: a token in the lock's *content* is commit-independent by design, so nothing else tracked
by the import changes on a new commit, and without this the lock would go stale (compile with an
out-of-date resolved sha) instead of re-importing.

**`Git` (`Xake.Dotnet.Git`, in `Project.fs`).** Reads `.git` directly, no `git` executable:
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
let unbuilt = project.Dependencies.References |> List.filter (fun r -> r.Sha256 = "") |> List.map (fun r -> r.Path) |> Set.ofList
let mapped = project |> Lock.mapPaths (fun p -> if unbuilt.Contains p then outputOf p else p)

do! need (unbuilt |> Set.toList |> List.map (outputOf >> relative))
do! CscLock.compile mapped
```

A project reference in the lock is unhashed and points at the referenced project's own build
output (not built yet at import time). `Lock.mapPaths f entry` rewrites every path the
entry's `Options`, `Sources`, `References`, `Analyzers`, `Generated` and `Resources` keys carry through `f`;
a rewritten
`Hashed` entry loses its hash, since the recorded hash was computed for the old path. The script
maps those references to the path its own rule will produce them at, `need`s those targets
first, and only then runs `CscLock.compile mapped` -- the runner itself does not `need` the
mapped outputs, it only `needFiles` what the (already-mapped) args name.

## Behaviour notes

- The one-resolved-form refactor changed composed mode in two ways: it now creates the output
  directory before compiling (previously a sample needed the directory to pre-exist), and it
  `needFiles` everything the args name, including framework global references, not only the
  sources/refs/resources it used to `needFiles` directly.
- `/noconfig` has to stay on the command line, never in the rsp: inside the rsp csc emits
  `CS2023` and ignores it.
- A hash mismatch reports every mismatching path at once, `"<path>: expected <hash>, got
  <hash-or-\"missing\">"`, one line per path, and fails when `FailOnError` is set.
- A `Lock.Entry` in memory has absolute paths throughout; `Lock.save`/`writeWith` tokenizes
  them against known roots (project root, NuGet package cache, SDK) so the file on disk is
  portable and diffable, and `Lock.load`/`parseWith` expands them back on load.

## Recording a lock from composed `csc` settings

`Project.import` produces a `Lock.Entry` from an msbuild project; `resolve` (private, above)
produces one from composed `csc {}` settings, but only for `run`'s own use -- discarded once
compiled. `lock-from-settings.md` (design note, recommendation 1b) asks for that value to be
reachable, so a lock-recording rule can write it out the way an import rule already does. The
smallest API for that:

```fsharp
module CscLock =
    val resolve : CscSettingsType -> Recipe<Lock.Entry>

module Lock =
    val rehash : Entry -> Entry
    val diff : Entry -> Entry -> string list
```

**`CscLock.resolve settings`** runs `resolve` and returns just the `Lock.Entry` -- `resolve`
produces no temp files of its own to clean up (see the composed-mode resx paragraph above), so a
lock it returns, `.resx` resources included, is compilable and recordable as is. Not
`Csc.resolve`: F# does not let a `module` and a `let`-bound function share one name in a
namespace the way it lets a `type` and a `module` share one via
`[<CompilationRepresentation(ModuleSuffix)>]` -- verified by compiling a minimal repro (`let Csc
x = ...` next to `module Csc = ...` leaves `Csc.resolve` unresolved, `FS0039`, in either
definition order). `Csc` stays the function it always was; the new module is `CscLock` instead.

**`Lock.rehash entry`** fills `Sha256` for every hashed entry (`References`, `Analyzers`,
`Imports`) and for `Compiler` (its `Version` too, when empty), from what is on disk right now
(`Lock.sha256`, empty when the file does not exist). `resolve` never hashes -- hashing every reference on every composed compile
would tax the common case for nothing -- so a lock-recording rule calls `rehash` itself, once,
after `CscLock.resolve`; the cost is then paid only when that rule reruns, like everything else
in Xake.

**`Lock.diff a b`** is a pure, human-readable comparison of two lock entries of the same
project: `[]` means identical. In order: `Options` and `Sources` as ordered lists (bare `+`/`-`
lines from an LCS diff -- a moved argument shows as a removal at its old position and an
addition at its new one, there being no separate "moved" marker in an ordered diff), `Defines`
as a set, `Compiler` (`Path`/`Sha256`/`Version`, one line per differing field), `Evaluation.Sdk`,
each hashed list (`References`, `Analyzers`, `Imports`) by path (added, removed, or `~ <label>
<path>: <old> -> <new>` when both sides have a hash and they differ), `Generated`/`Resources` by
key (added, removed, or `~ <label> <key>: content changed`), `ProjectRefs` as a set
(added/removed), and `Packages` by id (`+ Package Id@Version`, `- Package Id@Version`, `~ Package
Id: <old> -> <new>` for a version change, `~ Package Id@Version: sha512 changed`). This is the primitive `csc { locked "path" }` sugar and
any `Policy` wiring would build on (`lock-from-settings.md` scenario 3); neither exists yet --
`Lock.diff` is deliberately usable stand-alone (e.g. from an fsx-level "verify" rule) before
either does.

Tests: `src/tests/LockDiffTests.fs` (`rehash`, `diff` identical and with changes, pure, no
msbuild/compiler needed); `src/tests/FromLockTests.fs`, `CscLock.resolve resolves composed
settings into a hashable, round-trippable lock` (Integration: resolves a trivial library,
asserts `Sources`/`Args`/`Compiler.Path`/empty reference hashes, then `Lock.rehash` and a
`Lock.writeWith`/`Lock.parseWith` round trip).

## Locking composed settings: `lock`

```fsharp
let settings = { CscSettings with
                    Src = !!"src/*.cs"
                    RefGlobal = ["System.dll"]
                    TargetFramework = "net-4.6.2"
                    Out = File.make "out/app.dll"
                    Lock = Some "locks/app.json" }

"out/app.dll" ..> Csc settings
"update-locks" => recipe { do! CscLock.record "locks/app.json" settings }
```

`lock "<path>"` (`CscSettingsType.Lock: string option`) is migration path A of
`lock-from-settings.md` §9: a tuned `csc { }` block stays where it is and gains a lock. The
path is relative to the project root, like every other target path, or absolute.

**The semantics are strict, and an update is always explicit** (the user's decision,
2026-09-24 -- no engine mode, no global variable, no "follow the settings and warn" default).
`resolve` runs as it always does, so the settings remain the source of truth for *what* is
compiled; the lock decides whether this is the compilation that was recorded:

1. **No lock file yet** -- `Lock.rehash` the resolved entry, write it as a one-entry
   `Lock.Document` (`Configuration` "", `Properties` []; the entry's own `Framework` is the
   settings' `targetfwk` or "") with `Lock.save`, and compile *that* entry. The hashes `run` verifies were
   taken a moment earlier, so the check is trivially true -- it is the next build it is for.
2. **Lock present, and the resolved settings match it** (`Lock.diff recorded resolved` empty)
   -- compile the **recorded** entry, not the resolved one. That is the whole point: the
   recorded entry carries hashes, and `run`'s hash check (step 7 above) then gates the build,
   so a reference swapped on disk after recording fails even though the settings did not move.
3. **Lock present and different** -- the build **fails**, printing the diff and naming the two
   ways to update:

   ```
   'app': the resolved compilation differs from the lock 'locks/app.json':
   + /repo/src/Extra.cs
   Update the lock deliberately: delete 'locks/app.json', or run the target that calls
   CscLock.record "locks/app.json".
   ```

   `nofailonerror` (`FailOnError = false`) turns that failure into a warning and compiles from
   the **resolved** entry -- the settings are the source of truth, and nothing is written.

The comparison is structural, not hash-based: the resolved side never hashes anything
(hashing every reference on every compile would tax the common case), and both `diffHashed`
and `diffCompiler` skip a hash that is empty on either side -- an empty hash means "not
computed", not "zero bytes". So `Lock.diff recorded resolved` reports what the settings and the
filesets say: sources, options, defines, reference/analyzer paths, the compiler path.

**Updating.**

| Way | What it is |
|---|---|
| `dotnet fsi build.fsx -- -- update-locks` | the script's own phony target calling `CscLock.record`; the normal way |
| `rm locks/app.json` | a missing lock means "record it" -- the same rule as any missing target |

`CscLock.record : string -> CscSettingsType -> Recipe<unit>` resolves, rehashes and overwrites
the lock, compiling nothing. `CscLock.verify : string -> CscSettingsType -> Recipe<string list>`
returns the same diff `lock` fails on (an empty list means the lock is current), writing nothing and compiling
nothing -- `lock-from-settings.md` scenario 3 as a stand-alone check, e.g. a `check-locks`
target in CI. `CscLock.compile` remains the entry point for a lock that came from
`Project.import`.

**The lock file is not a target of the engine on this path.** It is written from inside the
compile recipe, which is what lets the `csc { }` block stay in place; the engine neither
`need`s it nor rebuilds it, and the update target is an ordinary phony action of the script's
own. Recommendation 1b of `lock-from-settings.md` (the lock as a real file target, built by a
rule of its own from `CscLock.resolve`) still stands for scripts that want it and needs nothing
new. As before, one lock file per compilation: two `csc` calls sharing one path would
read-modify-write the same file and is an authoring error, not something the library guards.

**Sharing the settings.** `csc { ... }`'s `Run` returns the compile recipe, not the settings,
so a block cannot be handed to `CscLock.record` as it stands; the settings have to be a value,
which in practice means record syntax (`{ CscSettings with ... }`, as above). Path B of
§9 -- a second builder `cscSettings { ... }` whose `Run` returns the `CscSettingsType`, so a
tuned block moves over unchanged -- is **not built**: ~15 lines whenever a real block asks for
it.

Tests: `src/tests/CscLockTests.fs` (6, Integration) -- first build records and compiles, second
build leaves the lock byte-identical, an added source fails with the diff, `CscLock.record`
overwrites and the next build passes, a reference tampered after recording fails the hash check,
and `CscLock.verify` reports the difference without writing or compiling.

## Not yet

- Running `fsc` through the same `Lock.Entry`/runner pair -- today only `csc` does. See
  `tracker.md`.
- `Resx.read`/`compile` only support plain string entries (`<data name="X"><value>...</value></data>`).
  A typed value (a `type` attribute) or a `ResXFileRef`/binary value (`mimetype`) fails with a
  clear message rather than being compiled; the real projects this targets have none. See
  `tracker.md`.
