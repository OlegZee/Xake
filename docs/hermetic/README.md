# Xake.Hermetic.Dotnet

## 0.4 (next release)

Built on **Xake 3.8** (`[3.8.0.29, 3.9)`, nuget.org 3.8.0.29). Everything under "Next" below,
and:

- **Xake 3.8 floor**: the run folder rule relies on the core's `ArtifactDep2` (a dependent sees
  a dependee rebuilt by an earlier run); the temporary file dependencies on the closure's write
  times are gone. `Described.refreshResources` stays: Xake 3.8's runner still compiles a
  missing `.resources` only.
- **`Pack.fromNuspec`** (S68): the minimal pack from a project's own nuspec, with `$token$`s
  from the description (`Pack.tokensOf`: id, version, authors, description, ...) plus the
  script's, its `<files>`, and `lib/<tfm>` from flat output folders (`Pack.libOf`); `Pack.expand`
  fails naming a token without a value ([pack.md](pack.md#the-minimal-pack-from-a-projects-own-nuspec)).
- **An analyzer crash under the compiler server** (S72, `AD0001`): the compile writes an error
  log; a failure whose log names a crashed analyzer is reported with the analyzer and compiled
  once more without the server ([lock.md](lock.md#building-a-recorded-lock)).
- **`Locks.check`** documented with an example for a script's check target (S67;
  [lock.md](lock.md#run-folders)).
- Verified with tests (fixed in the critic round): no host path in a description written by
  `Import.record` for an output folder outside the project (S70); one SDK pin warning per
  import.

## Next (hermetic, after PR 2; 0.4.3-night with PR 4 and the critic fixes)

- **`Import.record`**: the import that records msbuild's resolved compile inputs (references,
  defines, generated sources, resource names, compiler switches) per configuration and
  framework, and writes the locks itself; there is no `update-locks` step for such a project
  ([lock.md](lock.md#recorded-locks-importrecord)). It writes into the projects' `obj/` (the
  restore's assets file, the design-time builds' own `obj/xake-record/` folder) and creates
  their `bin/` folders, as an IDE does. Every configuration and the script's `Platform` are
  restored for themselves.
- **Lock format 2** (breaking for imported locks): the references, analyzers and compiler of a
  recorded entry are identities (`pkg:`, `pack:`, `sdk:`, `project:`), `Recorded` marks the
  entry, and with masks `Sources` holds only the files the masks do not put in place. A format-1
  lock of an imported project is refused: run the import again. Composed locks read as before.
- **`Project.import`** stays, as legacy: its locks are `"Format": 1` with
  `"Legacy": "Project.import"` and read as before. Use `Import.record` for new imports.
- **Outputs named by `TargetFileName`**: a .NET Framework application is `app.exe`; a script
  that builds one declares an `*.exe` rule beside its `*.dll` rule.
- **The SDK pin is stricter than `global.json`**: a recorded entry always has the `dotnet-sdk`
  prerequisite of the SDK that recorded it. Another SDK means a new import (all its locks
  change); the import warns and shows the exact `global.json` pin.
- **Stale locks fail the build**: a project file or other msbuild file changed since the import
  fails `Locks.compileAs` with "run the import again" (`LOCK_STALE=warn`: a warning, except
  under CI).
- **Generated binaries beside the lock**: a file msbuild generates that is not text (a
  `.licenses`, a `g.resources`) is copied to `<lock>.files/<framework>/`; commit the folder.
- **`Restore.download` in rounds**: one version of a package id per synthesized restore, so a
  lock set that needs two versions of one id fetches both.
- **Applications (PR 3)**: a recorded entry with runtime output has an `App` section -- the
  apphost (the SDK's own without a `RuntimeIdentifier`, identified by the SDK version; with one,
  the pack's template pinned with its hash), msbuild's `runtimeconfig.json`, `deps.json` and
  config file beside the lock (`<lock>.files/<framework>/app/`), msbuild's copy-local files.
  `Locks.compileAs` writes them beside the assembly; a lock of an application changes on the
  next import ([lock.md](lock.md#applications-the-app-section)). The import runs a second
  msbuild pass over the applications. Not yet: binding redirects, satellites.
- **Run folders (PR 4)**: `Locks.runFolderWith` builds the folder an application or a test
  project runs from, from its own lock closure only, by hard links (fallbacks: clone, copy), with
  the manifest `.xake-run.txt` as the rule's target, a conflicts report, stale files removed and
  a no-op that writes nothing; `Locks.runFoldersWith`, `Locks.flatOutOf`, `Locks.runOf`,
  `Locks.runs`; the file side in `RunFolder` ([lock.md](lock.md#run-folders)). Replaces the
  night builds' `Locks.deployWith` (it copied every file of the project's output folder, which a
  flat layout cannot use); it is not in this package.
- **Critic fixes (0.4.3-night)**: a run folder needs its closure's assemblies first, so
  `deploy` on an unbuilt tree builds and places the whole folder; every run folder path stays
  inside the folder (no rooted path, `..` or link on the way), a file changed since a run placed
  it is never replaced or removed, a case-only rename keeps the file; each manifest line carries
  the source's stamp, so a recompile rewrites the manifest and a rule needing it runs again;
  **`Locks.runFolder` places by clone** (APFS, btrfs/XFS reflink, ReFS; else a copy), hard
  links are the opt-in (`RunFolder.Link`; on Windows they lock the compile's output while a test
  runs); `Locks.check`/`checkWith`/`staleWith`/`staleOf` for a script's check target; a
  referenced project's package file the root's restore lacks is warned about. The import: a
  deps.json stand-in version left over fails it naming the file; an application's `App.config`
  and `runtimeconfig.template.json` are lock inputs (an edit: *run the import again*); a WPF
  project is refused up front; description excludes are relative and hold for every
  configuration; one SDK pin warning per import; package files hashed once (memo). The
  apphost: the SDK's own pack missing is a broken prerequisite (nothing fetched at build time);
  one SDK's HostModel per process, a failing one falls back to the byte-patch writer, which
  sets the GUI subsystem on every host.
- **Satellite assemblies: deferred by decision** (S71). Neither a project's own satellites
  (`ja/X.resources.dll`) nor a referenced project's are built or placed, and their
  `"resources"` lines are missing from `deps.json`; against 0.3.7-0.3.8 this is a regression
  for the package entries that shipped them. Recording msbuild's `GenerateSatelliteAssemblies`
  inputs is a later step.
- **The compile no longer copies copy-local files** (breaking against PR 3's previews):
  `Locks.compileAs` writes an application's `Files` and apphost only; the copy-local files and
  items are the run folder's. A script that ran an application from its output folder builds its
  run folder.
- **`App.Content`** and **an `App` for libraries**: the import records the project's own
  copy-to-output items (`None`, `Content`, `EmbeddedResource`, `Compile` with
  `CopyToOutputDirectory`) and gives a library an `App` section when msbuild copies something
  into its output (copy-local files of a .NET Framework test library: S59). `IsTestProject` is
  kept in `Evaluation.Properties`. Every lock with project references changes on the next import.
- **`pack:` covers apphost and runtime packs** (`Microsoft.NETCore.App.Host.<rid>`,
  `*.App.Runtime.<rid>`), wherever the machine has them.

Version **0.3.0, preview**: the API may break while the package is 0.x. 0.3 is built on
**Xake 3.7** (`[3.7.0.26, 3.8)`) and adds:

- **The SDK compiler by the SDK version**: a compiler of the .NET SDK (`fsc.dll`, Roslyn
  `csc.dll`) is identified in the lock by the SDK version, not by a hash, so a lock holds across
  OS and architecture ([lock.md](lock.md#the-compiler-a-lock-names)).
- **`Description` / `Import` / `Described`**: a project described once in JSON, imported and
  compiled with `fsc { project; config; packages }` / `csc { ... }`
  ([description.md](description.md), [lock.md](lock.md#import-the-project-decides-msbuild-answers)).
- **`Locks.generate` / `Locks.compileAs` / `Locks.needAll`**: Xake-generated locks, one per
  described project and configuration, and the layout rules that build from them
  ([lock.md](lock.md#xake-generated-locks)).

## 0.2

0.2 was built on **Xake 3.6** (`[3.6.0.24, 3.7)`) and added:

- **F# in the lock**: a lock entry holds a C# or an F# compilation (`Lock.Compilation`),
  `fsc { lock }` gates an `fsc {}` block, and `Project.import` imports an fsproj like a csproj.
- **Prerequisites**: the .NET SDK at an exact version, recorded per entry and checked before a
  replay (below).
- **`nofetch`** after `lock`, and `Restore.Options.Default` following the base's `NUGET_FETCH`.
- **The `HERMETIC=on` gate on the lock side**: a lock that names a path outside
  `$(ProjectRoot)` and `$(NuGetPackageRoot)` not covered by a prerequisite is neither written
  nor replayed, and an import of a project whose SDK is not pinned exactly fails
  ([hermetic-mode.md](hermetic-mode.md), [lock.md](lock.md#hermetic-mode)).
- **The base's helpers** instead of this package's copies: the build's package folder
  (`DotNetFwk.packageRoot ()`, so the script variable `NUGET_PACKAGES` tokenizes as
  `$(NuGetPackageRoot)`), `DotNetFwk.fetchEnabled ()`, the `global.json` reader
  (`DotNetFwk.globalJsonPin`) and `DotNetFwk.sdkVersionOf`.
- **`RuntimeConfig`** (Xake 3.6): a `.runtimeconfig.json` the rule declares is recorded in the
  entry (`"RuntimeConfig"` under `Compilation`, written only when there is one) and written again
  by a replay.

Breaking against 0.1 and the earlier 0.2 previews:

- **`Lock.build` takes a `Compilation`** (`Lock.Compilation.Csc c` / `Lock.Compilation.Fsc f`),
  and so do `compile`, `record` and `verify`; **`Entry.Csc` became `Entry.Compilation`** (the
  `entry.Csc` member remains, and fails for an F# entry).
- **`Fsproj` removed**: `Project.import` imports an fsproj.
- **Depends on Xake 3.6** (`[3.6.0.24, 3.7)` in 0.2; 0.3 moves to 3.7): the `Csc`/`Fsc` records gained fields, and F#
  records are not binary-compatible across such a change, so 0.2 does not run on 3.5. The upper
  bound is the next minor for the same reason.

- **`Entry.Prerequisites`**: a lock entry records what the environment must provide by itself,
  today the .NET SDK at an exact version (`{ "Kind": "dotnet-sdk", "Version": "<v>", "Pin":
  "$(ProjectRoot)/global.json" }`), written at record time when the entry depends on an SDK that
  `global.json` pins exactly, and checked before every replay with a message naming what to
  install. Written only when not empty, so existing locks stay byte-identical. Code that builds a
  `Lock.Entry` record by hand adds `Prerequisites = []`. See
  [lock.md](lock.md#prerequisites).
- **`packageroot` removed** from `csc { lock }` / `fsc { lock }`: the package folder is the
  build's one (`NUGET_PACKAGES`). `Restore.into` and `Lock.loadWith (Roots.packageRootOverride
  dir)` stay for scripts that read a lock against another folder.
- **`norestore` renamed `nofetch`**, after the base's `NUGET_FETCH`.
- **`Restore.Options.Default` follows `NUGET_FETCH`** and the build's package folder: where a
  build context exists (`Restore.ensure`, `Lock.compileWith`, `Lock.buildWith`), `PackageRoot =
  None` is `DotNetFwk.packageRoot ()` (the script variable `NUGET_PACKAGES` first) and
  `Enabled = true` means "fetch unless `NUGET_FETCH=off`" (`Restore.resolve`).

`Xake.Hermetic.Dotnet` builds .NET assemblies and packages so that what shipped can be
explained and checked: a **lock** file records exactly what the compiler was handed and the hash
of everything it read, the build compiles from the lock and fails when anything differs, and the
result can be turned into a deterministic package with an SBOM and a signing step. It is a
separate NuGet package that depends on `Xake` (the engine and the `csc`/`fsc`/`msbuild`/`resgen`
tasks of `Xake.Dotnet`, Xake 3.7.0.26 or later, below 3.8); nothing in `Xake` knows about it.

```fsharp
#r "nuget: Xake.Hermetic.Dotnet, 0.3.0"

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet
```

## The pieces

| Piece | What it is | Page |
|---|---|---|
| Lock | the lock file format (C# and F# entries), `Lock.build` / `compile` / `record` / `verify`, `csc { lock }`, `fsc { lock }`, `Project.import` (msbuild design-time build of a csproj or fsproj into a lock) | [lock.md](lock.md) |
| Description | `xake.project.json`: what a project is, as data (name, language, frameworks, sources, references, packages, options, package metadata); `Description.load`/`save`/`find`/`names`/`beside`; schema in [../schemas/xake.project.schema.json](../schemas/xake.project.schema.json) | [description.md](description.md) |
| Hermetic mode | `HERMETIC=on`: the invariant, prerequisites, what each input needs, the messages; what is shipped and what is pending | [hermetic-mode.md](hermetic-mode.md) |
| Restore | fetch the packages a lock names into a folder of the build's choice, one `dotnet restore` for the whole set | [restore.md](restore.md) |
| Nuget and Sbom | read the restore graph and package cache; write a deterministic CycloneDX 1.6 SBOM per assembly and per package | [nuget-sbom.md](nuget-sbom.md) |
| Verify | Authenticode PE hash and labelled byte differences between two binaries | [verify.md](verify.md) |
| Pack | deterministic zip and `.nupkg` (sorted entries, fixed timestamps, content-derived ids) | [pack.md](pack.md) |
| StrongName | PE timestamp and checksum normalisation, strong-name (re-)signing without `sn.exe` | [strongname.md](strongname.md) |
| Sign | Authenticode and NuGet signing as a delegated rule with a pluggable signer | [signing.md](signing.md) |
| Workflows | developer, teammate, CI (GitHub Actions, GitLab CI), release and offline flows end to end; what to commit; how a missing lock fails CI | [workflows.md](workflows.md) |

The compilation itself, `csc {}`/`fsc {}`, `Csc.ofSettings`/`Fsc.ofSettings` and the runners
`Csc.run`/`Fsc.run`, stay in `Xake.Dotnet`:
[../csc-syntax.md](../csc-syntax.md), [../tasks.md](../tasks.md).

- [Architecture](../hermetic-architecture.md): the design, where it meets `Xake`, the evidence
  and the known limits, for maintainers and reviewers.
- [Guide](../hermetic-guide.md) (draft): a task-oriented walk-through, from the first lock to
  packing and signing.

## Two ways in

Gate a `csc {}` block by a lock. The first build records `locks/app.json` and compiles; later
builds fail with a diff when the settings, a reference or the compiler no longer match it:

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

do xakeScript {
    rules [
        "out/app.dll" ..> csc {
            targetfwk "net-4.6.2"
            src !!"src/*.cs"
            grefs ["System.dll"]
            lock "locks/app.json"
        }
    ]
}
```

The same thing without the sugar, with the resolved `Csc` in hand:

```fsharp
open Xake.Dotnet
open Xake.Hermetic.Dotnet

"out/app.dll" ..> recipe {
    let! c = csc { targetfwk "net-4.6.2"; src !!"src/*.cs"; grefs ["System.dll"]
                   out (File.make "out/app.dll"); resolve }
    do! Lock.build "locks/app.json" (Lock.Compilation.Csc c)
}
```

`Lock.build` gates against the lock, restores what it names, resolves the revision token and
then calls `Csc.run` (`Fsc.run` for `Lock.Compilation.Fsc f`).

An F# library the same way. The compiler is the SDK's `fsc.dll`, so pin the SDK
(`global.json` with `rollForward: disable`):

```fsharp
"out/lib.dll" ..> fsc {
    targetfwk "net8.0"
    src !!"src/Lib.fs"
    lock "locks/lib.json"
}
```

To update a lock deliberately, delete the file or run a target that calls
`Lock.record`.

## Building from source

`src/hermetic` compiles against the published `Xake` package (the range above, `[3.6.0.24, 3.7)`),
not against `src/core` and `src/dotnet` in the same checkout. A plain restore takes it from
nuget.org; nothing else is needed:

```bash
dotnet restore src/hermetic.tests
dotnet build src/hermetic -c Release --no-restore
dotnet test src/hermetic.tests -c Release --no-restore
```

The lower bound is the exact version nuget.org carries (the release pipeline appends the run
number to the version, so 3.7.0 is published as 3.7.0.26). A bound with no published package at
it makes NuGet pick the next one up and warn NU1603 on every restore.

Only when working against an *unreleased* base (a change in `src/core` or `src/dotnet` that
`src/hermetic` needs before it is on nuget.org) pack the base into a folder with a version inside
the range and hand that folder to the restore as an extra source; nothing in the repository
points at it:

```bash
dotnet pack src/dotnet -c Release -p:Version=3.7.0.99 -o /tmp/xake-feed
dotnet restore src/hermetic.tests -p:RestoreAdditionalProjectSources=/tmp/xake-feed
dotnet test src/hermetic.tests -c Release --no-restore
```

The package lands in the NuGet global cache (`~/.nuget/packages/xake/3.7.0.99`) and wins over
the released one from there on; delete that folder when done, and raise the lower bound in
`Xake.Hermetic.Dotnet.fsproj` to the released version once the base is published.
