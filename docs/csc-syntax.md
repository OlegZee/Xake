# `csc {}` as it stands

`csc {}` is the C# compiler task. It resolves its settings into one `Csc` record -- a project's
exact compiler command line, plus what it references and how to check it -- and hands that to a
single runner, `Csc.run`. Settings are intent, `Csc` is the resolved compilation: there are
several ways to arrive at a `Csc` (compose one from settings with `csc { ...; resolve }` or
`Csc.ofSettings`, take the `Csc` of a lock entry, or `Csc.ofArgs` from a command line) but only
one thing that ever shells out to the compiler.

The base reference for the settings, `Csc.compile`, `resolve`, `Csc.run` and `RunOptions` is
[tasks.md](tasks.md); this file keeps the details behind them. Locks -- the file that records a
`Csc` with the hashes of everything it reads, `Lock.compile`, `Lock.build`, `csc { ...; lock }`
and `Project.import` -- are not part of `Xake.Dotnet`; they live in the package
`Xake.Hermetic.Dotnet` and are described in [hermetic/lock.md](hermetic/lock.md).

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

`csc {}` (through `Csc.compile`) calls `Csc.ofSettings`, which turns the settings below into a
`Csc` at recipe time -- same argument order the task has always produced, save that
references are now spelled `/reference:` rather than `/r:` -- and passes it to `Csc.run`.
`csc { ...; resolve }` stops after `Csc.ofSettings` and returns the `Csc`; `resolve` must be the
last operation, and outside a file rule needs an explicit `out`. Every custom operation of
`CscSettingsBuilder`:

| Keyword | Argument | Does | Default |
|---|---|---|---|
| `platform` | `TargetPlatform` | `/platform:` | `AnyCpu` |
| `target` | `TargetType` | `/target:`, resolved from the output name when `Auto` | `Auto` |
| `targetfwk` | `string` | target framework to compile against; see [`docs/dotnet-build.md`](dotnet-build.md) | `null`: the `NETFX-TARGET` var, else the SDK's own .NET framework (`net10.0` for SDK 10.0.x) |
| `out` | `File` | output file; `/out:` | `File.undefined` (task infers a target file) |
| `src` | `Fileset` | source files | `Fileset.Empty` |
| `ref` | `Fileset` | adds to the reference fileset (`+`) | -- |
| `refif` | `bool * Fileset` | adds the fileset conditionally (`+?`) | -- |
| `refs` | `Fileset` | replaces the reference fileset | -- |
| `grefs` | `string list` | framework references by file name, resolved against the target framework's reference directory | `[]` |
| `resources` | `ResourceFileset` | adds one embedded-resource fileset (prepended) | -- |
| `resourceslist` | `ResourceFileset list` | adds several at once | -- |
| `define` | `string list` | `/define:`, joined with `;` | `[]` |
| `unsafe` | `bool` | `/unsafe` | `false` |
| `cscpath` | `string` | compiler executable, bypassing framework discovery entirely | `None` |
| `toolset` | `string` (package version) | compiler from `Microsoft.Net.Compilers.Toolset/<version>` in the NuGet cache instead of the SDK's; see below | `None` |
| `resolve` | (none) | returns the `Csc` (`Csc.ofSettings`) instead of compiling; must be the last operation | -- |
| `args` | `string list` | raw extra switches, appended last (`CommandArgs`) | `[]` |
| `nofailonerror` | (none) | do not fail the build on a compile error | `FailOnError = true` |
| `noserver` | (none) | compile in a fresh csc process instead of through the Roslyn compiler server (`VBCSCompiler`); see [csc-server.md](csc-server.md) | `Server = Shared None` (env `XAKE_CSC_SERVER=0` turns it off) |
| `keepalive` | `int` (seconds) | idle time after which a compiler server this build starts exits (`/keepalive`) | Roslyn's own default, 600 |

`out`, `platform`, `unsafe`, `nostdlib` (from `targetfwk`) and `define` are composition
settings; replaying a lock entry (`Lock.compile`, [hermetic/lock.md](hermetic/lock.md)) does not go through them at all. The
argument list `Csc.ofSettings` builds is,
in order: `/noconfig` (when the target framework requires it), `/nologo`, `/target:`,
`/platform:`, `/unsafe`, `/nostdlib+`, `/out:`, `/define:`, sources, `/reference:` refs, global
refs, `/res:`, then `CommandArgs`. The list then goes through `Csc.ofArgs` exactly
like a command line reported by msbuild and gets the same round-trip check (`Csc.Args` must equal
the list it was built from); it always passes, there being one item per switch.

**Composed-mode `.resx` resources.** A `resources`/`resourceslist` fileset entry
that names a `.resx` file is not compiled by `Csc.ofSettings` itself -- unlike an ordinary
embedded-resource file (already the file the compiler reads), a `.resx` needs turning into a
`.resources` first, and `resolve` used to do that eagerly into a temp file with a random name,
deleted once the compile was done. That left nothing for `resolve` to hand back: a lock
recorded from settings with `.resx` resources had `/res:` arguments naming files that no longer
existed. Instead `resolve` records a permanent
`(resx, .resources)` pair in `Csc.Resources` --
`<ProjectRoot>/obj/xake/<assembly name>/<manifestName>` where `manifestName` is the `/res:`
logical name `Impl.makeResourceName` computes (e.g. `Sample.Application.Strings.resources`) --
and emits `/res:<resourcesPath>,<manifestName>`, exactly the shape `Project.import`
([hermetic/lock.md](hermetic/lock.md)) produces for an imported project's resx. `run`'s existing resource step (see below) then compiles
it when the output is missing and `needFiles` the resx itself, so the engine decides when a resx
edit reruns the compile, not `resolve`. Non-resx resources are unaffected -- they were already
the file the compiler reads and stay a plain `/res:` file input. `resolve` no longer produces any
temp files of its own; the only temp file `run` still cleans up is its own response file.

### Composed-mode outputs: `runtimeconfig.json`

A composed compilation of an executable (`target Exe`/`WinExe`, or `Auto` with an output ending in
`.exe`) for `netN.0` (the SDK default included) also gets `<output>.runtimeconfig.json` next to it,
without which `dotnet <output>.exe` cannot start. It is recorded as a `Generated` file of the
compilation (so `run` writes it back whenever it is missing or changed, before the compiler runs)
with the content `dotnet build` writes by default: `tfm`, the `Microsoft.NETCore.App` framework at
`<major>.0.0`, and the `EnableUnsafeBinaryFormatterSerialization: false` config property; no
roll-forward settings, no timestamps. Libraries, `netstandard` and .NET Framework targets get none;
a lock or an imported project carries whatever its `Generated` list holds.

### Composed-mode reference assemblies

`targetfwk` (else `NETFX-TARGET`, else the SDK's own framework) decides where the framework's
reference assemblies come from. For .NET (`net8.0`, `net10.0`) every `*.dll` of the targeting
pack is referenced, as the SDK does; for netstandard and .NET Framework `mscorlib.dll` is. Every
`grefs` name is looked up there too. They become ordinary `/reference:` entries of the
resolved `Csc` (`Dependencies.References`), hashed and locked like any other reference.

The default, with neither `targetfwk` nor `NETFX-TARGET`, is the .NET framework of the SDK the
build runs on -- the one `dotnet --version` selects in the project root, `global.json`
honoured -- read from the SDK's `Microsoft.NETCoreSdk.BundledVersions.props`
(`BundledNETCoreAppTargetFrameworkVersion`): `net10.0` for SDK 10.0.x. Only when that cannot
be determined (no SDK) does the compile fail with `csc needs a target framework`.

| `targetfwk` | Reference assemblies | Version |
|---|---|---|
| `net10.0`, `net8.0`, ... (also `sdk-net10.0`) | `<dotnet>/packs/Microsoft.NETCore.App.Ref/<v>/ref/<moniker>`, the targeting pack installed with the SDK | for the SDK's own framework the exact version it bundles (`BundledNETCoreAppPackageVersion`); for another one the newest `<major>.<minor>.*` pack present. Never downloaded: a missing pack fails naming the folder |
| `netstandard2.0` | `NETStandard.Library/<v>/build/netstandard2.0/ref` in the package folder | `2.0.3`, or the script variable `NETSTANDARD_LIBRARY_VERSION` |
| `netstandard2.1` | the SDK's `packs/NETStandard.Library.Ref/*/ref/netstandard2.1` | comes with the SDK; not restored |
| `net-4.6.2`, `net472`, ... (SDK provider) | `Microsoft.NETFramework.ReferenceAssemblies.<moniker>/<v>/build/.NETFramework/v4.x` (and its `Facades`) | `1.0.3`, or the script variable `NETFX_REFERENCE_ASSEMBLIES_VERSION` |

- The version is exact. A different version that happens to be in the cache is not used.
  The defaults are constants in `DotNetFwk` (`defaultReferencePackVersions`): 2.0.3 is the
  last `NETStandard.Library` 2.0 release and 1.0.3 the last
  `Microsoft.NETFramework.ReferenceAssemblies` release, so a cache that already had the newest
  version resolves the same paths as before. Both variables are read through `getVar`, so
  changing one reruns the compile.
- When the package is not in the folder, `DotNetFwk.resolveFramework` fetches it with
  `DotNetFwk.restorePackage`, the same mechanism `toolset` uses: one synthesized
  `PackageDownload` project under `<ProjectRoot>/obj/xake/restore/<n>/`, so the repository's
  `nuget.config` and private feeds apply. The folder is `NUGET_PACKAGES`, else
  `~/.nuget/packages`.
- A failed download fails the build with
  `restoring 1 package(s) into '<folder>' failed with exit code 1 (see '<project>'): NETStandard.Library 2.0.3`.
  A package that downloads but has no reference assemblies where they are expected fails
  with a message naming the package and version.
- `DotNetFwk.locateFramework` (no build context, no script variables) uses the default
  versions and restores the same way, from a synthesized project under the directory it was
  given (the current directory for `locateFramework`).
- On Windows a .NET Framework found through the registry still wins over the package, and
  the package is fetched only when the SDK provider is the one consulted.
- In a lock the references read
  `$(NuGetPackageRoot)/netstandard.library/2.0.3/build/netstandard2.0/ref/...`. The path is
  the same as before this change, as long as the cache held the default versions.
- A .NET targeting pack is under the SDK installation, so in a lock its references read
  `$(DotnetRoot)/packs/Microsoft.NETCore.App.Ref/10.0.12/ref/net10.0/...`.

## Compiler sources

### Composed mode: three sources, first match wins

| Setting | Compiler | Notes |
|---|---|---|
| `cscpath "<exe>"` | that executable | no framework or package lookup at all |
| `toolset "<version>"` | `csc.dll` from `microsoft.net.compilers.toolset/<version>/tasks/netcore/bincore` in the NuGet cache | the package is fetched into the machine's NuGet cache if missing (`DotNetFwk.restorePackage`); still missing afterwards fails the build |
| neither | whatever `targetfwk` / `NETFX-TARGET` resolves through `DotNetFwk.resolveFramework` | the SDK's `csc.dll`, or `csc.exe` / `mcs` on a framework that ships one |

- `toolset` replaces only the compiler. References, defines and environment variables still
  come from the targeted framework.
- A lock records the compiler that ran (`Dependencies.Compiler`: path, SHA-256, version) and
  `Csc.run` verifies it; see [hermetic/lock.md](hermetic/lock.md).

## The runner: `Csc.run`

`Csc.run` (`Csc.fs`) is the one runner for C#, shared by every entry point. Obtaining anything
that is missing (packages, a revision token) is not its business: `Lock.compile` does that before
it hands the `Csc` here. Since 3.5 the steps below live in an internal `CompilerRunner.run`
over a `RunPlan` record that `Csc.run` fills. It does, in order (after a
`trace Info "compiling '<name>' (<tool> <version>)"`):

1. A compiler that does not exist (with no `RunOptions.CscPath`) is traced as an error and fails
   the build when `FailOnError` is set (`'<name>': the compiler <path> does not exist`).
2. `needFiles` the compiler itself: the hash check
   below covers the compiler's path, but nothing before this made it a tracked
   dependency, so an SDK or toolset update that changed `csc.dll`'s bytes left the target looking
   up to date and the hash check never ran.
3. Writes back every `Generated` file that is missing or whose content differs from what is on
   disk -- the resolved compilation is the source of truth for msbuild-generated inputs like
   `AssemblyInfo.cs` (and `sourcelink.json` with its token already resolved, see [hermetic/lock.md](hermetic/lock.md)). The
   composed mode never populates `Generated`, so this is a no-op there.
4. Creates the output directories, for every path `CscArgs.outputs args` names.
5. `needFiles` every resx in `Csc.Resources` (so a resx edit rebuilds the dll) and, for each
   `(resx, resources)` pair, compiles the resx to that `.resources` path with `Xake.Dotnet.Resx`
   when the output is **missing** -- so a machine with only the lock, or a cleaned `obj/`, still
   ends up with the exact file the recorded `/resource:` switch names. This is the same step for
   both modes: the composed mode's `resolve` records its own `.resx` resources
   here too (see the composed-mode resx paragraph above), at a permanent path under
   `obj/xake/<name>/`, instead of compiling them itself into a temp file at recipe time. This
   dropped a `.resources`-vs-`.resx`
   timestamp comparison that used to gate regeneration as well: that
   was a second rebuilder living next to the engine's -- the engine already decides whether this
   recipe runs at all, from the `FileDep` `needFiles` puts on the resx, so `run` re-deciding with
   its own mtime check was redundant and, worse, implied a staleness check the lock does not
   actually make. **Gate semantics, stated plainly**: the engine decides *whether* a recipe runs
   (dependency tracking, `needFiles`/`FileDep`); `run`'s hash and existence checks only decide
   whether to *fail* a run that the engine already started -- they never trigger one. A swapped
   file with an unchanged timestamp is still caught (the hash check runs every time `run` runs
   for other reasons); a swapped file that also updates the timestamp is caught because the
   timestamp change is what makes the engine run `run` in the first place.
6. `needFiles` on `CscArgs.inputs args` -- every file any input switch names, plus the
   sources. For the composed mode this covers everything the args name, including the
   framework's global references, not only sources/refs/resources. It comes after step 5
   because the `.resources` files named by `/res:` have no rule and must already exist, and
   before the hash check because a reference produced by another rule of the script is
   (re)built here: the check has to see the file the compiler is about to read. A hashed file
   that is missing and that no rule of the script produces cannot be obtained this way, so just
   before this step every such file is reported at once, as a hash mismatch (`expected <sha>,
   got missing`), instead of `needFiles` stopping at the first with "Neither rule nor file".
7. Verifies the SHA-256 of every hashed reference, analyzer, and the compiler itself against what
   is on disk. An empty recorded hash means "not checked" (the composed mode never records one,
   and neither does an unbuilt project reference). Any mismatch is collected and reported
   together (`('<name>') hash mismatch:` then one line per path), then fails the build when
   `FailOnError` is set (`XakeException`, message containing the path).
8. Writes the arguments to a response file, with `Impl.escapeArgument`.
    `/noconfig` cannot go inside the rsp -- csc warns `CS2023` and ignores it there -- so it stays
    on the command line and everything else goes into `@<rspfile>`.
9. Picks the compiler: `RunOptions.CscPath` wins if set; otherwise, when the `Csc`'s recorded
    compiler path ends in `.dll`, it runs through `dotnet <path>`; otherwise the path is run
    directly (a native launcher, e.g. a `cscpath` naming the SDK's `csc` apphost). The composed
    mode records the SDK's `Roslyn/bincore/csc.dll` rather than the `csc` launcher the framework
    probe returns next to it (`Csc.managedCompiler`), the same compiler an imported lock
    hashes; a `cscpath` override is used verbatim.
10. Adds the compiler-server switches ([csc-server.md](csc-server.md)) -- `/shared`,
    plus `/keepalive:<s>` when `RunOptions.Server` names one -- on the command line, ahead of
    `/noconfig` and the `@rsp`, never in the rsp and never in the lock: csc parses them out on the
    client side before anything reaches `VBCSCompiler`, so `Csc.Args` is unchanged by them. Only when a
    `VBCSCompiler.dll` sits next to the compiler file about to run (the SDK's `Roslyn/bincore`,
    a toolset package's), `RunOptions.Environment` is empty, and the compiler is not under the temp
    directory (`serverArgs`); the legacy `csc.exe`, `mcs`, an arbitrary `cscpath` and a
    mono/registry toolchain compile in-process as before.
11. Runs the compiler with the working directory set to `Csc.Directory` (what `dotnet
    build` uses; an XML-doc `<include file='..'>` resolves against it), `RunOptions.Environment`
    as env vars (empty on `RunOptions.Default`), and fails on a non-zero exit when
    `FailOnError` is set.

The rsp file is deleted once the compiler exits, success or failure -- the only temp file `run`
produces now that the composed mode's `.resx` resources are permanent outputs (see above),
not temp files.

## Behaviour notes

- The one-resolved-form refactor changed composed mode in two ways: it now creates the output
  directory before compiling (previously a sample needed the directory to pre-exist), and it
  `needFiles` everything the args name, including framework global references, not only the
  sources/refs/resources it used to `needFiles` directly.
- `/noconfig` has to stay on the command line, never in the rsp: inside the rsp csc emits
  `CS2023` and ignores it.
- A hash mismatch reports every mismatching path at once, `"<path>: expected <hash>, got
  <hash-or-\"missing\">"`, one line per path, and fails when `FailOnError` is set.

## Not yet

- `Resx.read`/`compile` only support plain string entries (`<data name="X"><value>...</value></data>`).
  A typed value (a `type` attribute) or a `ResXFileRef`/binary value (`mimetype`) fails with a
  clear message rather than being compiled; the real projects this targets have none.
