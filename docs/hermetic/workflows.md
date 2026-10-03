# Workflows: from a developer's lock to a CI build

This page walks through the everyday flows of a team that uses `Xake.Hermetic.Dotnet`: a
developer records a lock and commits it, a teammate builds from it, CI builds from it and must
fail when the lock is missing or stale, and a release build turns the result into SBOMs and
packages. Each scenario says what happens, what to write in `build.fsx`, what to commit, what
the machine needs, and what the failure messages look like.

Versions: `Xake 3.4.0.21`, `Xake.Hermetic.Dotnet 0.1.0.22`.

Marks used below:

- **(run)**: the snippet or message was run for this page (macOS, SDK 8.0.425 pinned by
  `global.json`, the published packages above, scratch folders under `/tmp`).
- **(untested)**: written from the source, not run.

Reference pages: [lock.md](lock.md), [restore.md](restore.md), [nuget-sbom.md](nuget-sbom.md),
[verify.md](verify.md), [pack.md](pack.md), [signing.md](signing.md), the task-oriented
[guide](../hermetic-guide.md), the compiler server note [../csc-server.md](../csc-server.md).

## What goes into the repository

| File | Commit? | Why |
|---|---|---|
| `build.fsx` | yes | the build itself |
| `locks/*.json` | **yes** | the lock is the reviewed record of what the compiler reads |
| sources, `.csproj`, `Directory.Build.props` | yes | inputs |
| `global.json` | yes | selects the SDK for `dotnet fsi`, for msbuild and for the SDK compiler |
| `nuget.config` | yes, when you use your own feeds | the restore reads it (see C) |
| `.xake` (the build database) | **no** | per machine; it is in `.gitignore` as `.xake*` |
| `obj/`, `out/`, `.packages/` | no | build products and package folders |

A lock never contains a machine path. Paths are written against three tokens,
`$(ProjectRoot)`, `$(NuGetPackageRoot)` and `$(DotnetRoot)`, and expanded on read
([lock.md](lock.md)). The same lock therefore works in any checkout directory, with any NuGet
cache location.

## A. Developer: a composed `csc {}` block

### What happens

`Lock.build path c` (and its sugar `csc { ...; lock path }`) has three cases
(`Lock.buildWith` in `src/hermetic/Lock.fs`):

| State of the lock file | What the build does |
|---|---|
| missing | records the resolved compilation, hashed, and compiles it |
| present, the resolved settings match | compiles the **recorded** entry; the runner checks every recorded SHA-256 |
| present, the settings differ | fails with the differences; nothing is written |

"Match" is structural: sources, options, defines, reference and analyzer paths, compiler path
and version. Hashes are compared by the runner, against the files on disk, when the recorded
entry is compiled.

### The script

One recipe holds the settings, so the build, the update and the check use the same ones
**(run)**:

```fsharp
#r "nuget: Xake, 3.4.0.21"
#r "nuget: Xake.Hermetic.Dotnet, 0.1.0.22"
open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet

let app = recipe {
    let! c = csc {
        targetfwk "net-4.6.2"
        target Library
        src !!"src/*.cs"
        grefs ["System.dll"]
        args ["/deterministic"]
        out (File.make "out/app.dll")
        resolve }
    return c }

do xakeScript {
    var "CSC_TOOLSET" "4.12.0"          // the compiler is a NuGet package, see below
    rules [
        "build" <== [ "out/app.dll" ]

        "out/app.dll" ..> recipe {
            let! c = app
            do! Lock.build "locks/app.json" c
        }

        "update-locks" => recipe {
            let! c = app
            do! Lock.record "locks/app.json" c
        }

        "check-locks" => recipe {
            let! c = app
            let! diff = Lock.verify "locks/app.json" c
            if not (List.isEmpty diff) then
                failwithf "locks/app.json is stale:\n%s" (String.concat "\n" diff)
        }
    ]
}
```

### Step by step

1. `dotnet fsi build.fsx -- -- build`. There is no lock: it is recorded at `locks/app.json`
   and the assembly is compiled **(run)**.
2. Run it again with nothing changed. The target is skipped as up to date; the lock is not
   rewritten, not even its timestamp **(run)**.
3. Add `src/Extra.cs` and touch a source so the target rebuilds. The build fails
   **(run)**:

   ```
   [ERROR] 2> 'app': the resolved compilation differs from the lock 'locks/app.json':
   + /private/tmp/xake-wf/src/Extra.cs
   Update the lock deliberately: delete 'locks/app.json', or run the target that calls Lock.record "locks/app.json".
   ```

   Exit code 2. Note that a new file matched by a glob does not by itself rerun the target (the
   fileset is expanded, not tracked); the gate fires when the target rebuilds. `check-locks`
   catches it at any time.
4. Accept the change on purpose: `dotnet fsi build.fsx -- -- update-locks`, then `build`
   passes **(run)**. Review the lock diff in the same PR as the source change.
5. `dotnet fsi build.fsx -- -- check-locks` compiles nothing and writes nothing. It fails with
   `locks/app.json is stale:` and the same diff lines, exit code 2 **(run)**.

### The compiler: `toolset` / `CSC_TOOLSET`

Without a toolset the lock names the SDK's compiler **(run)**:

```
"Compiler": { "Tool": "csc", "Path": "$(DotnetRoot)/sdk/8.0.425/Roslyn/bincore/csc.dll", ... "Version": "4.11.0-3.25569.22" }
```

Every machine then needs exactly that SDK, or the build fails with a compiler drift (see D).
With `toolset "4.12.0"` in the block, or the script variable `CSC_TOOLSET` for every block that
names none, the compiler is `Microsoft.Net.Compilers.Toolset` from the package folder, and a
machine that does not have it restores it **(run)**:

```
"Compiler": { "Tool": "csc", "Path": "$(NuGetPackageRoot)/microsoft.net.compilers.toolset/4.12.0/tasks/netcore/bincore/csc.dll", ... }
```

Precedence: `cscpath`, then the block's `toolset`, then `CSC_TOOLSET`, then the SDK
([../tasks.md](../tasks.md#compiler-toolset-for-the-whole-script)). Set it in the script
(`var "CSC_TOOLSET" "4.12.0"`) rather than on the command line, so every machine gets the
same value.

### The SDK: `global.json`

Commit a `global.json` with an exact pin:

```json
{ "sdk": { "version": "8.0.425", "rollForward": "disable" } }
```

It selects the SDK for `dotnet fsi` itself, for the SDK compiler (`DotNetFwk` asks
`dotnet --version` in the project root) and for msbuild in scenario B. A pin the machine does
not have makes `dotnet fsi` fail before Xake starts: "A compatible .NET SDK was not found.
Requested SDK version: ..." **(run)**. With `CSC_TOOLSET` the composed compile no longer
depends on the SDK's compiler, but `dotnet fsi` still does.

## B. Developer: an imported msbuild project

### What the lock records

`Project.import` runs one restore per project and one design-time build per framework (the
compiler reports its command line instead of running), and writes one lock for the whole
project set ([lock.md](lock.md)). Per entry, i.e. per (project, framework):

| Section | Content |
|---|---|
| `Compilation` | msbuild's exact command line, split into options, defines, sources; generated files (`AssemblyInfo.cs`, ...) with their content; resx pairs |
| `Dependencies.Compiler` | the compiler msbuild would run, path, SHA-256, version |
| `Dependencies.References`, `Analyzers` | every file, with SHA-256 (project references have an empty hash) |
| `Dependencies.Packages` | the restore graph from `project.assets.json`: id, version, nupkg sha512 from the cache, direct or transitive, edges |
| `Evaluation` | project file, imported msbuild files with SHA-256, `Sdk` (the SDK that evaluated it), `SdkPin` (from `global.json`), a few properties |

The import warns when the SDK is not pinned exactly: `the SDK is not pinned (...) -- the
lock's compiler (...) will drift with every SDK the machine picks; pin it with global.json { sdk:
{ version, rollForward: "disable" } }`.

### Two ways to wire the import

**As a file rule over the lock** (the guide's and `import.fsx`'s pattern):

```fsharp
"locks/hello.json" ..> recipe {
    let! lockFile = getTargetFile ()
    do! Project.import {
        Project.ImportOptions.Default with
            Projects = [ "src/Hello/Hello.csproj" ]
            Frameworks = [ "netstandard2.0" ]
            Output = lockFile.FullName }
}
```

Locally this is convenient: the import re-runs when a project file, an imported msbuild file or
the git commit changes (`Project.import` `needFiles` `HEAD` and its ref file). But the lock is
then a build product. On a fresh clone there is no `.xake` database, the rule reports "Not built
yet", and the import **runs again and overwrites the committed lock** **(run)**: on CI that
means msbuild, a NuGet restore, and a build from a lock nobody reviewed. It also re-imports
after every commit.

**As a deliberate update target** (recommended once the lock is committed). No rule produces
the lock, so the build reads it as a plain file, and a missing lock fails with
`Neither rule nor file is found for '<path>/locks/hello.json'`, exit code 2 **(run)**:

```fsharp
let lockPath = "locks/hello.json"

let importInto (output: string) = recipe {
    let! options = getCtxOptions ()
    do! Project.import {
        Project.ImportOptions.Default with
            Projects = [ "src/Hello/Hello.csproj" ]
            Frameworks = [ "netstandard2.0" ]
            Output = System.IO.Path.Combine (options.ProjectRoot, output) }
}

do xakeScript {
    rules [
        // the only place that writes the lock: run it, review the diff, commit
        "update-locks" => importInto lockPath

        // compares a fresh import with the committed lock; writes only under obj/
        "check-locks" => recipe {
            let fresh = "obj/xake/check/hello.json"
            do! importInto fresh
            let! options = getCtxOptions ()
            let! roots = Roots.current
            let read (p: string) = Lock.read roots (System.IO.Path.Combine (options.ProjectRoot, p))
            let committed, current = read lockPath, read fresh
            let key (e: Lock.Entry) = e.Csc.Name, e.Csc.Framework
            let diffs =
                [ for e in current.Entries do
                    match committed.Entries |> List.tryFind (fun c -> key c = key e) with
                    | Some c -> for d in Lock.diff c e -> sprintf "%s (%s): %s" e.Csc.Name e.Csc.Framework d
                    | None -> yield sprintf "+ entry %s (%s)" e.Csc.Name e.Csc.Framework
                  for c in committed.Entries do
                    if not (current.Entries |> List.exists (fun e -> key e = key c)) then
                        yield sprintf "- entry %s (%s)" c.Csc.Name c.Csc.Framework ]
            if not (List.isEmpty diffs) then
                failwithf "%s is stale; run update-locks and commit:\n%s" lockPath (String.concat "\n" diffs)
        }

        "build" => recipe {
            let! lock = Lock.load lockPath
            for entry in lock.Entries do
                do! Lock.compile entry
        }
    ]
}
```

(run) for all three targets. `Output` is made absolute because `Project.import` writes it as
given, relative to the process's current directory. Adding a `PackageReference` and running
`check-locks` gave:

```
[ERROR] Error 'locks/hello.json is stale; run update-locks and commit:
Hello (netstandard2.0): - Reference /Users/olegz/.nuget/packages/system.buffers/4.5.1/ref/netstandard2.0/System.Buffers.dll
Hello (netstandard2.0): + Reference /Users/olegz/.nuget/packages/system.buffers/4.6.0/lib/netstandard2.0/System.Buffers.dll
Hello (netstandard2.0): ~ Import /private/tmp/xake-imp2/src/Hello/Hello.csproj: a206...fefe -> 0b73...fa1e
Hello (netstandard2.0): ~ Package System.Buffers: 4.5.1 -> 4.6.0'.
```

`check-locks` for an imported lock runs msbuild and a restore, so it needs the SDK and the
feeds. `build` needs neither msbuild nor the project's own restore.

### When to re-import

Run `update-locks` after a change to a project file, `Directory.Build.props`/`.targets`,
`Directory.Packages.props`, a package version, `global.json` (a new SDK means a new compiler),
or the framework list. Files the import read are listed under `Evaluation.Imports`; a change to
any of them shows in `check-locks` as `~ Import <path>`.

### Projects that reference each other

`build` above compiles entries in lock order. With `ProjectReference`s, a reference in the lock
has no hash and points at the path msbuild would have built it at. Use one file rule per
output and map the reference to the other entry's `/out:`: the rule in
[../hermetic-guide.md](../hermetic-guide.md#6-building-from-an-imported-lock) (from
`docs/features/hermetic-build/import.fsx`, run by the maintainers on dataengine, 36/36
byte-identical).

### `$(SourceRevisionId)`

A project with SourceLink writes the commit sha into generated files. The import replaces it
with the token `$(SourceRevisionId)`, so the lock does not change with every commit.
`Lock.compile` resolves it from the checkout's `.git` (no `git` executable, detached `HEAD`
works). Outside a repository it fails: `'<name>': the lock needs $(SourceRevisionId) but no git
repository was found at or above '<dir>' -- a lock that needs a revision must be compiled in a
repository`. A `git archive` copy is not a repository.

## C. Teammate or fresh machine

A teammate clones, runs `dotnet fsi build.fsx -- -- build`, and gets the same compilation.
What makes that work is the restore step inside `Lock.compile` and `Lock.build`.

### What is restored

`Lock.compileWith` calls `Restore.ensure` with `Lock.restoreRequest [entry]`: every compiler,
reference and analyzer path the entry names. A path under the package folder names its own
package (`<root>/<id>/<version>/...`), so the missing files group into a list of
`(id, version)`. Paths outside the package folder (SDK packs, the SDK compiler, project outputs)
are not restorable and are left to the hash check.

| Item | Restored? |
|---|---|
| reference and analyzer packages under `$(NuGetPackageRoot)` | yes |
| a toolset compiler (`toolset`, `CSC_TOOLSET`) | yes |
| an SDK compiler under `$(DotnetRoot)/sdk/<v>` | no: `the lock names the compiler of SDK <v> (<path>), which is not installed; install that SDK or re-import with the installed one` |
| SDK reference packs and analyzers under `$(DotnetRoot)` | no: they come with the SDK; a missing one reads `got missing` in the hash check |
| packages of the graph that the compiler does not read | no (runtime-only, build-only packages) |

For a composed `csc {}` block two more restores happen earlier, when the settings are resolved
(`Csc.ofSettings`): the toolset package, and the reference assemblies of the target framework
(`Microsoft.NETFramework.ReferenceAssemblies.<moniker>` or `NETStandard.Library`).

Measured on a cold folder (`NUGET_PACKAGES=/tmp/cold-pkgs`): the imported `Hello` restored 5
packages in one restore and compiled; the composed block restored the toolset and the reference
assemblies and compiled **(run)**.

### Where it restores from

One `dotnet restore` of a synthesized project, `obj/xake/restore/<n>/restore.csproj` under the
build's project root, with one `PackageDownload` per missing package at an exact version
(`[1.2.3]`), and the package folder passed as `NUGET_PACKAGES`
(`DotNetFwk.downloadPackages`). Consequences:

- **NuGet sources are the machine's normal configuration.** NuGet looks for `nuget.config`
  from the synthesized project's directory upwards, so the repository's `nuget.config` (and the
  user-level and machine-wide configs) apply. A company feed declared in the repository works.
- **Credentials** are whatever NuGet itself uses: `packageSourceCredentials` in a config file,
  a credential provider, or the `NuGetPackageSourceCredentials_<source name>` environment
  variable **(untested)**. Xake adds nothing. A failure reads `restoring N package(s) into
  '<folder>' failed with exit code 1 (see '<project>'): <ids>`; NuGet's own message is in the
  log at verbose level (`filelog "build.log" Verbosity.Diag`).
- `Directory.Build.props`/`.targets`, `Directory.Packages.props` and central package management
  are switched off for that one restore, and `NuGetAudit=false`.
- A failed attempt's project stays on disk for a manual re-run.

Exception: the composed block's reference-assembly restore (`DotNetFwk`, before any lock is
read) runs from a folder under the system temp directory, so a repository `nuget.config` is
not seen there, only the user-level and machine configs. See Gaps.

### What is verified

Two levels, both from source (`Restore.verify`, `Csc.run`):

| Check | When | What is compared |
|---|---|---|
| package | only for packages **restored in this run** | the package directory exists, and `.nupkg.metadata`'s `contentHash` equals the lock's `Sha512` (skipped when the lock has no sha512 for it, e.g. a composed lock, whose package graph is empty) |
| file | every compile, every hashed file | SHA-256 of every reference, analyzer and the compiler against the lock |

A package already in the folder is not sha512-checked; its files are SHA-256-checked by the
runner, which is the check that gates the compile. The nupkg itself is not re-hashed: the
`contentHash` NuGet wrote is trusted ([restore.md](restore.md#left-open)).

### Memoization

- Nothing missing: one `File.Exists` per path, no process, no network.
- Restored packages are memoized per process (folder + id + version), so a hundred entries
  naming one package launch one restore.
- One restore at a time per package folder (a `Resource` of 1); the missing set is recomputed
  after waiting.

### `Restore.Options`

| Field | Default | Meaning |
|---|---|---|
| `PackageRoot` | `None` | the package folder; `None` is the machine cache, `NUGET_PACKAGES` or `~/.nuget/packages` |
| `Enabled` | `true` | whether a missing package may be downloaded |

Three ways to choose the folder:

| How | Covers | Notes |
|---|---|---|
| `NUGET_PACKAGES=<dir>` in the environment | everything: `dotnet fsi`'s own `#r "nuget:"`, `Lock.load`, every restore, `csc { lock }` | simplest; **(run)** |
| `Restore.into ".packages"` + `Lock.loadWith (Roots.packageRootOverride ...)` + `Lock.compileWith` | imported locks compiled through `compileWith` | the folder is relative to the project root; must appear in both places |
| `Restore.into` with `csc { lock }` | not possible | the sugar always uses `Lock.Options.Default` (Gaps) |

The `Restore.into` form, from [restore.md](restore.md) (run by the maintainers on dataengine,
not for this page):

```fsharp
let loadLock path = recipe {
    let! options = Restore.into ".packages"
    let! lock = Lock.loadWith (Roots.packageRootOverride (Restore.packageRoot options)) path
    return options, lock
}

// a target that fills the folder from the whole lock, for a CI cache step
"restore" => recipe {
    let! options, lock = loadLock "locks/hello.json"
    do! Lock.restore options lock
}

// in the compile rule
let! options, lock = loadLock "locks/hello.json"
let entry = Lock.entryFor "netstandard2.0" "Hello" lock
do! Lock.compileWith { Lock.Options.Default with Restore = options } entry
```

`Lock.compileWith` with explicit options does not resolve the compiler server: it uses
`RunOptions.Default`, whose `Server` follows `XAKE_CSC_SERVER` only. `Lock.compile` also reads
the script variable `CSC_SERVER`.

## D. CI: GitHub Actions and GitLab CI

### What CI must do

1. Install the SDK `global.json` names.
2. Restore the package cache (optional, for speed).
3. Fail when a lock is missing or stale.
4. Build from the committed locks.
5. Fail when the build changed or created a lock.

### The missing lock

Under CI a missing lock fails the build, before anything is recorded or compiled
(`Lock.buildWith`, hence `Lock.build` and `csc { lock }`; [lock.md](lock.md#what-happens-step-by-step)):

```
'app': the lock 'locks/app.json' is not there. Under CI a lock is never recorded: record it
on a developer machine (build once, or run the target that calls Lock.record
"locks/app.json", e.g. update-locks) and commit it.
```

"Under CI" is `Lock.underCi`: the script variable `CI` when set (`-d CI=on|off`), otherwise the
environment variable `CI`, which GitHub Actions, GitLab CI and Azure Pipelines set. Nothing to
configure on the runner; `-d CI=off` opts out (records as a developer machine would), and
`nofailonerror` does not soften it. Off CI a missing lock is still recorded and compiled.

Two more guards stay useful as extra safety:

| Guard | Covers | Cost |
|---|---|---|
| run `check-locks` before `build` | composed locks (`Lock.verify` fails on a missing lock naming it and `Lock.record`) and imported ones | composed: free; imported: an msbuild import |
| the import as `update-locks`, not as a file rule (B) | imported locks: a missing lock fails `build` itself | none |
| `git status --porcelain locks/` after the build | anything that wrote a lock (e.g. a run with `-d CI=off`) | none; a safety net |

Command-line targets run one after another, and a failure stops the rest, so
`dotnet fsi build.fsx -- -- check-locks build` never reaches `build` when the lock is missing
**(run)**. (Targets joined with `;` in one argument run in parallel; do not join these two.)

### Drift on CI

A stale lock fails `build` (when the target rebuilds, which on a fresh runner it always does:
there is no `.xake`) and `check-locks`, with the diff. Do not cache `.xake` between CI runs:
a fresh database is what makes every target rebuild and every gate run.

### A different SDK on the runner

| Lock's compiler | Runner's SDK differs | Result |
|---|---|---|
| composed, SDK compiler | `global.json` selects another SDK | fails with `~ Compiler.Path: .../sdk/8.0.425/... -> .../sdk/10.0.401/...` and `~ Compiler.Version: 4.11.0-3.25569.22 -> 5.9.0-1.26423.113` **(run)** |
| imported, SDK compiler | the SDK is not installed | `'<name>': the lock names the compiler of SDK <v> (<path>), which is not installed; install that SDK or re-import with the installed one` (covered by a test) |
| toolset (`toolset`, `CSC_TOOLSET`) | any | the compiler package is restored; the SDK only has to run `dotnet fsi` |

An imported lock also names SDK reference packs and analyzers under `$(DotnetRoot)` (for
`net8.0` and similar targets). Those exist only with the same SDK, so for imported locks the
SDK must match on every machine. Install it from `global.json`.

### The compiler server on CI

By default `csc` goes through the Roslyn compiler server, which stays alive up to 10 minutes
after the last compile ([../csc-server.md](../csc-server.md)). It does not change the output.
On an ephemeral hosted runner it costs nothing. On a self-hosted runner or a GitLab shell
executor the process outlives the job. Set `XAKE_CSC_SERVER=off` on CI unless you measured
that the warm server matters. `XAKE_CSC_SERVER` is the environment variable, `0`, `false`,
`off` or `no` turn it off; the script variable `CSC_SERVER` (`-d CSC_SERVER:off`) wins over it.

### The package cache

What to cache: the NuGet package folder. It holds the build's packages and also the
`Xake`/`Xake.Hermetic.Dotnet` packages `dotnet fsi` restores for `#r "nuget:"`.

| Question | Answer |
|---|---|
| key | hash of `locks/*.json`, `build.fsx`, `global.json`, `nuget.config` |
| fallback key | the OS prefix only: a package folder is append-only and versioned, a stale one is a partial hit |
| on a hit | `Restore.ensure` finds the files, starts no process, no network; the per-file SHA-256 check still runs on every compile |
| on a partial hit | only the missing packages are restored, in one `dotnet restore` |
| a tampered or corrupted cached file | the compile fails with `hash mismatch: <path>: expected <sha256>, got <sha256>`; delete that package directory from the cache |

### GitHub Actions (untested)

```yaml
# .github/workflows/build.yml
name: build
on: [push, pull_request]

jobs:
  build:
    runs-on: ubuntu-latest
    env:
      XAKE_CSC_SERVER: "off"
      DOTNET_NOLOGO: "1"
      DOTNET_CLI_TELEMETRY_OPTOUT: "1"
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('locks/*.json', 'build.fsx', 'global.json', '**/nuget.config') }}
          restore-keys: |
            nuget-${{ runner.os }}-

      # fails when a lock is missing or stale; writes nothing
      - run: dotnet fsi build.fsx -- -- check-locks

      - run: dotnet fsi build.fsx -- -- build

      # the build must not have created or changed a lock
      - name: locks unchanged
        run: |
          if [ -n "$(git status --porcelain -- locks)" ]; then
            git status --porcelain -- locks
            echo "the build wrote a lock: record it locally and commit it"
            exit 1
          fi
```

Private feeds: declare them in the repository's `nuget.config` and pass the credential as an
environment variable NuGet reads, e.g. `NuGetPackageSourceCredentials_MyFeed:
"Username=ci;Password=${{ secrets.FEED_TOKEN }}"` **(untested)**. `setup-dotnet`'s own
`cache: true` keys on `packages.lock.json` files and does not apply here.

### GitLab CI (untested)

GitLab caches only paths inside the project directory, so point the package folder there with
`NUGET_PACKAGES`. Everything (the lock's `$(NuGetPackageRoot)`, the restores, `dotnet fsi`)
follows it.

```yaml
# .gitlab-ci.yml
build:
  image: mcr.microsoft.com/dotnet/sdk:8.0.425     # the version in global.json
  variables:
    NUGET_PACKAGES: "$CI_PROJECT_DIR/.nuget/packages"
    XAKE_CSC_SERVER: "off"
    DOTNET_NOLOGO: "1"
  cache:
    key:
      files:                                       # at most two files
        - global.json
        - locks/app.json
      prefix: nuget
    fallback_keys:
      - nuget-default
    paths:
      - .nuget/packages/
  script:
    - dotnet fsi build.fsx -- -- check-locks
    - dotnet fsi build.fsx -- -- build
    - test -z "$(git status --porcelain -- locks)" || { git status --porcelain -- locks; echo "the build wrote a lock"; exit 1; }
```

`cache:key:files` takes at most two files; pick the lock that changes most, or accept a key on
`global.json` alone, since a stale package folder is only a partial hit. The image tag must
match `global.json` exactly when it pins with `rollForward: "disable"`. Add `.nuget/` to
`.gitignore`.

## E. Release build

A release build is scenario D plus four steps. Each has its own page; the guide has a short
run example of each ([../hermetic-guide.md](../hermetic-guide.md), sections 8 to 10).

**SBOM.** `Sbom.forAssembly cacheRoot entry assemblyPath` builds a CycloneDX 1.6 document from
the lock entry alone: every package of the restore graph, the referenced files under their
package with SHA-256, compiler, SDK and analyzers under `formulation`. `cacheRoot` is the
package folder (supplier and license come from the nuspecs there): `Roots.nugetRoot ()`, or
`Restore.packageRoot options` when you restore into your own folder. A composed lock has no
package graph, so its SBOM has no package components. For a nupkg, `Sbom.forPackage` (restore
scope) and `Sbom.forPackageScoped` (what the customer receives), checked by
`Sbom.checkPackageScope`. The output is deterministic. See [nuget-sbom.md](nuget-sbom.md).

**Verify.** `Verify.authenticodeHash` is equal for a signed file and its unsigned build;
`Verify.compare` and `Verify.verdict` name the differing fields. Use it to show that a shipped
binary is the lock-built one. Byte identity needs the same checkout path (csc embeds absolute
source paths in the PDB) or a `/pathmap`. See [verify.md](verify.md).

**Pack.** `Pack.nupkg` and `Pack.zip` write deterministic archives (sorted entries,
`SOURCE_DATE_EPOCH` or 1980-01-01, content-derived ids). See [pack.md](pack.md).

**Sign.** `sign {}` is a delegated rule with a pluggable signer; the package ships only
`Sign.fakeSigner`. `Sign.verifySameImage` ties a signed file back to its input. See
[signing.md](signing.md).

Artifacts to keep with a release: the tagged commit (it holds the locks), the locks themselves
as build artifacts, the per-assembly and per-package SBOMs, the unsigned outputs with their
`Verify.authenticodeHash`, and the build log.

## F. Offline and air-gapped

The lock fixes every package by id, version and hash, so an offline build needs only a
pre-populated package folder.

1. On a connected machine, fill a folder from the locks. Either
   `NUGET_PACKAGES=/path/pkgs dotnet fsi build.fsx -- -- build`, or a `restore` target with
   `Restore.into` and `Lock.restore` (C). Include the `Xake` packages: `dotnet fsi` needs them
   too, and they land in the same folder when `NUGET_PACKAGES` points there.
2. Move the folder to the offline machine.
3. Build with the same folder, and with restore off so nothing tries the network:

```fsharp
let! options = Restore.into ".packages"
let! lock = Lock.loadWith (Roots.packageRootOverride (Restore.packageRoot options)) "locks/hello.json"
let entry = Lock.entryFor "netstandard2.0" "Hello" lock
do! Lock.compileWith { Lock.Options.Default with Restore = { options with Enabled = false } } entry
```

With a package missing, the build fails before the compiler runs (covered by
`RestoreTests`):

```
[WARN] N package(s) named by the lock are not in '<folder>' and automatic restore is off (Restore.Options.Enabled): <id> <version>, ...
[ERROR] ('<name>') hash mismatch:
<folder>/<id>/<version>/.../X.dll: expected <sha256>, got missing
```

What breaks offline:

| Piece | Why |
|---|---|
| `Project.import`, imported `check-locks` | msbuild restores the project from the feeds |
| `csc { toolset }` / `CSC_TOOLSET` on a folder without the package | resolved before any lock, always restores, ignores `Enabled` |
| composed reference assemblies missing | `DotNetFwk` tries a restore from a temp folder, ignores `Enabled`, and swallows the error; the message is then `reference assemblies for '<moniker>' are not available: failed to restore package ...` |
| `csc { lock }` | always `Restore.Options.Default`; it cannot be switched off (use `NUGET_PACKAGES` and an offline NuGet config) |
| `dotnet fsi` `#r "nuget:"` | needs `Xake` and `Xake.Hermetic.Dotnet` in the folder |

## G. Troubleshooting

| Message | Cause | Action |
|---|---|---|
| `'<name>': the resolved compilation differs from the lock '<path>':` + diff lines | settings, files matched by a glob, the compiler or the SDK changed | review the diff; `update-locks` and commit, or revert the change |
| `<path> is stale:` (your `check-locks`) | the same, found without compiling | the same |
| `Neither rule nor file is found for '<root>/locks/<x>.json'` | the lock is not committed, or the path is wrong | record it locally (`build` or `update-locks`) and commit it |
| `<path> is missing: record it on a developer machine ...` (the CI guard) | the same, from `requireLock` | the same |
| `('<name>') hash mismatch:` / `<path>: expected <a>, got <b>` | a file on disk is not the recorded one: a different package build, a corrupted cache, a rebuilt reference | delete that package directory and rebuild; for a reference your script builds, see the guide |
| `... expected <sha256>, got missing` for many files | restore off, or the lock was read against another package folder than the one restored into | use one folder for `Lock.loadWith` and `Restore` (C), or `NUGET_PACKAGES` |
| `('<name>') restoring the packages the lock names failed:` + `<id> <v>: not restored (expected it at '<dir>')` | the restore ran but did not deliver | check the feed in `nuget.config`; NuGet's output is in the log at verbose level |
| `<id> <v>: expected sha512 <a>, got <b>` | the feed served a different nupkg than the one recorded | a republished package: find out why before re-importing |
| `restoring N package(s) into '<folder>' failed with exit code <n> (see '<project>'): <ids>` | `dotnet restore` failed: feed unreachable, credentials, package absent | fix the source or credentials; re-run the left `restore.csproj` by hand to see NuGet's message |
| `N package(s) named by the lock are not in '<folder>' and automatic restore is off` (warning) | `Restore.Options.Enabled = false` | pre-populate the folder, or enable restore |
| `'<name>': the lock names the compiler of SDK <v> (<path>), which is not installed; install that SDK or re-import with the installed one` | imported lock, other SDK on this machine | install the SDK from `global.json` |
| `'<name>': the compiler <path> is not available and restoring <id> <v> did not provide it` | a toolset compiler the restore could not provide | check the feed; delete a partial package folder |
| `toolset <id> <v>: the package folder <dir> has no compiler at <path> (a partial restore? ...)` | composed `toolset`, broken package directory | delete `<dir>` and rebuild |
| `'<name>': the compiler <path> named by the lock is not installed` / `... does not exist` | a compiler path outside the SDK's `sdk/` or anywhere else | re-import or re-record on this machine |
| `~ Compiler.Path: .../sdk/<a>/... -> .../sdk/<b>/...` in a drift | composed lock, the SDK changed | pin `global.json`, or use `CSC_TOOLSET` and re-record |
| `'<name>': csc needs a target framework: set targetfwk in the csc block or the NETFX-TARGET script variable ...` | composed block without `targetfwk` | add it |
| `reference assemblies for '<moniker>' are not available: failed to restore package ...` | composed block, reference assemblies could not be restored | check that `nuget.org` (or a mirror in the user-level NuGet config) is reachable |
| `'<name>': the lock needs $(SourceRevisionId) but no git repository was found ...` | compiling an imported SourceLink lock outside a checkout | build in a git checkout |
| `project '<name>' is in the lock for N frameworks (...); ask for one with Lock.entryFor` | multi-framework lock | `Lock.entryFor framework name` |
| `A compatible .NET SDK was not found. Requested SDK version: <v>` (from `dotnet`, before Xake) | `global.json` pins an SDK the machine lacks | install it (`setup-dotnet` with `global-json-file`, the matching image tag) |
| `the SDK is not pinned (...) -- the lock's compiler (...) will drift ...` (import warning) | no exact pin | `global.json` with `rollForward: "disable"` |

More messages: [../hermetic-guide.md](../hermetic-guide.md#12-troubleshooting).

## Gaps

What the product does not do yet, with the smallest change that would close each.

1. *(done)* **A missing lock is recorded on CI.** Under CI (`Lock.underCi`: the script variable
   `CI`, else the environment variable `CI`) `Lock.buildWith` -- and so `Lock.build` and
   `csc { lock }` -- fails on a missing lock naming it and "record it on a developer machine
   and commit it"; `-d CI=off` opts out (D). `check-locks` and the `git status` step remain as
   extra safety.
2. *(in progress, PRs open)* **The import-as-file-rule pattern re-imports on every fresh clone** and overwrites the
   committed lock (B). The guide and `import.fsx` present it as the way to wire an import.
   Smallest fix: document the `update-locks` pattern as the default for committed locks; later,
   a `Project.import` option that compares instead of writing (the imported `check-locks`).
3. *(in progress, PRs open)* **No document-level diff.** An imported `check-locks` is 15 lines of script around
   `Lock.diff` (B). Smallest fix: `Lock.diffDocuments : Document -> Document -> string list`,
   keyed by (name, framework), reporting added and removed entries.
4. *(in progress, PRs open)* **No one-call build for an imported lock.** `Lock.build` is for composed compilations;
   an imported lock needs a loop over `Lock.compile`, and with project references the
   per-output rule with `mapPaths` (B). Smallest fix: a `Lock.compileAll` that orders entries
   by project reference and maps references to the other entries' `/out:`.
5. *(done)* **`csc { lock }` cannot take `Restore.Options`.** `packageroot "<dir>"` and
   `norestore` after `lock` set the package folder and forbid the network
   ([lock.md](lock.md#restore-options-packageroot-norestore)).
6. **Composed reference assemblies are restored outside the restore mechanism.**
   `DotNetFwk`'s reference-assembly restore (`Microsoft.NETFramework.ReferenceAssemblies.*`,
   `NETStandard.Library`) runs from the temp directory with a `PackageReference`, ignores the
   repository's `nuget.config` and `Restore.Options.Enabled`, and swallows its error. It also
   takes the newest version present in the cache. Smallest fix: route it through
   `DotNetFwk.downloadPackages` (project root, exact version) and report the failure.
7. **A package already in the folder is not sha512-checked**, and the nupkg is never
   re-hashed: `.nupkg.metadata` is trusted (C). The per-file SHA-256 check still gates the
   compile. Smallest fix: compare `contentHash` for every package the entry names, present or
   not (one small file read each).
8. **Drift messages print expanded, machine-specific paths** (`+ /private/tmp/.../Extra.cs`).
   On CI they show the runner's checkout path. Smallest fix: tokenize the diff lines with the
   build's roots before printing.
9. **`Lock.verify` on a missing lock says only `Neither rule nor file is found`.** Smallest
   fix: check `File.Exists` in `Lock.verify` and fail with "lock '<path>' does not exist".
10. **SBOM package-scope defaults are product-specific.** `Sbom.PackageScopeOptions.Default`
    treats `DS.`, `MESCIUS.` and `GrapeCity.` as internal ids. Override `IsInternal` with
    `Sbom.PackageScope.idPrefixes [ ... ]`. Smallest fix: an empty default.
11. **Only `Sign.fakeSigner` ships.** A real signer (Azure Trusted Signing, a certificate
    store) is the user's to provide ([signing.md](signing.md#4-out-of-scope-now-what-the-user-must-provide)).
12. **Only what the compiler reads is restored**, not the whole package graph, so publishing
    or running tests from the restored folder needs a `Restore.download` of the rest
    ([restore.md](restore.md#left-open)).
13. **The CI workflows above have not run.** Nothing in this repository runs a hermetic build
    on GitHub Actions or GitLab CI yet; the first real run is the proof.
