# Reproducible .NET builds with Xake.Hermetic.Dotnet

Draft of the user guide. Status: package version 0.1.0, a preview; the API may change while the
package is 0.x.

`Xake.Hermetic.Dotnet` adds a **lock** to Xake's `csc` task. A lock is a JSON file, kept in git,
that records exactly what the C# compiler is handed and the SHA-256 of every file it reads from
outside your repository: the compiler itself, every reference, every analyzer. Later builds
compile from the lock and fail when anything differs. On top of the lock the package can
restore missing packages, write a CycloneDX SBOM, compare two binaries, pack a deterministic
nupkg and run a signing step.

What it does not do: it does not replace msbuild's evaluation (importing a project still runs
msbuild once), it does not work offline by default, and it does not lock F# compilations yet.
The design and its limits are in [hermetic-architecture.md](hermetic-architecture.md).

Snippets marked **(untested)** were written against the source but not run for this guide. The
others were run in a scratch directory against the assemblies built from the current source,
loaded with `#r` on the dll files instead of the NuGet package.

## 1. Install

The package depends on `Xake` 3.4 or later, below 4.0. Reference it from your build script and
open three namespaces:

```fsharp
#r "nuget: Xake.Hermetic.Dotnet, 0.1.0"

open Xake
open Xake.Dotnet
open Xake.Hermetic.Dotnet
```

**(untested)**: neither 3.4.0 nor 0.1.0 is on nuget.org at the time of writing. The package
brings `Xake` in as a dependency; add `#r "nuget: Xake, 3.4.0"` if you want to pin the base too.

Two things change when you open `Xake.Hermetic.Dotnet`:

- `csc {}` gains the operation `lock`.
- `sign` becomes the signing rule builder. FSharp.Core's numeric `sign` is still available as
  `Operators.sign`.

Requirements: the .NET SDK (8.0 or later), and for byte-identical results a `global.json` that
pins the SDK exactly (`"rollForward": "disable"`).

## 2. Your first lock

Add `lock "<path>"` as the **last** operation of a `csc {}` block:

```fsharp
do xakeScript {
    rules [
        "main" <== ["out/app.dll"]

        "out/app.dll" ..> csc {
            targetfwk "net-4.6.2"
            target Library
            src !!"src/*.cs"
            grefs ["System.dll"]
            lock "locks/app.json"
        }
    ]
}
```

```bash
dotnet fsi build.fsx -- -- main
```

On the first build the lock file does not exist. Xake resolves the settings (expands the
filesets, picks the compiler and the framework reference assemblies), hashes every reference and
the compiler, writes `locks/app.json`, and compiles. Commit the lock.

What lands in the lock (shortened; the full format is in [hermetic/lock.md](hermetic/lock.md)):

```json
"Compilation": {
  "Directory": "$(ProjectRoot)",
  "Options": [ "/noconfig", "/nologo", "/target:library", "/platform:anycpu", "/nostdlib+",
               "/out:$(ProjectRoot)/out/app.dll", "@Sources", "@References" ],
  "Sources": [ "$(ProjectRoot)/src/App.cs" ], ...
},
"Dependencies": {
  "Compiler": { "Tool": "csc", "Path": "$(DotnetRoot)/sdk/10.0.401/Roslyn/bincore/csc.dll", "Sha256": "...", "Version": "5.9.0-1.26423.113" },
  "References": [ { "Path": "$(NuGetPackageRoot)/microsoft.netframework.referenceassemblies.net462/1.0.3/build/.NETFramework/v4.6.2/mscorlib.dll", "Sha256": "f8b1..." }, ... ]
}
```

Paths start with a token (`$(ProjectRoot)`, `$(NuGetPackageRoot)`, `$(DotnetRoot)`), so the file
is the same on every machine. The `@Sources` and `@References` markers show where those blocks
sit on the real command line.

Notes:

- `lock` must be the last operation. An operation after it does not compile.
- `targetfwk` (or the script variable `NETFX-TARGET`) is required. The composed `csc` task
  targets .NET Framework monikers (`net-4.6.2`, `net472`, ...) and `netstandard2.0`/`2.1`; see
  [tasks.md](tasks.md#csc).
- The compiler is the block's `toolset "<version>"`, else the script variable `CSC_TOOLSET`
  (same meaning: a `Microsoft.Net.Compilers.Toolset` version), else the SDK's `csc.dll`; `cscpath`
  overrides all three. The SDK is the one a `global.json` in (or above) the project root selects,
  as `dotnet --version` run there reports it, and the newest installed when there is none; a pin
  to an SDK that is not installed falls back to the newest with a warning. Setting
  `CSC_TOOLSET` for the whole script (`var "CSC_TOOLSET" "4.12.0"`, or `-d CSC_TOOLSET:4.12.0`)
  makes every lock entry name a restorable NuGet compiler instead of an installed SDK.
- The path is relative to the project root, or absolute.
- Use one lock file per `csc` block. Two blocks sharing one lock path overwrite each other.

The same thing without the builder sugar, with the resolved compilation in hand:

```fsharp
"out/app.dll" ..> recipe {
    let! c = csc {
        targetfwk "net-4.6.2"
        target Library
        src !!"src/*.cs"
        grefs ["System.dll"]
        out (File.make "out/app.dll")
        resolve }
    do! Lock.build "locks/app.json" c
}
```

`resolve` makes the block return the resolved `Csc` instead of compiling it. Outside a file rule
it needs an explicit `out`. `Lock.build` gates `c` against the lock and then compiles.

## 3. The second run, and a drift

On the second run, with nothing changed, the target is up to date and the lock file is not
touched, not even its timestamp.

When the target does rebuild (a source was edited, a reference changed), Xake resolves the
settings again and compares them with the lock:

| Situation | What happens |
|---|---|
| The resolved compilation matches the lock | the **recorded** entry is compiled; its hashes are checked against the files on disk |
| It differs | the build fails with the differences |
| A recorded reference or the compiler has different bytes | the build fails with a hash mismatch |

A drift, for example a new source file `src/Extra.cs` matched by `src/*.cs`:

```
[ERROR] 2> 'app': the resolved compilation differs from the lock 'locks/app.json':
+ /home/me/repo/src/Extra.cs
Update the lock deliberately: delete 'locks/app.json', or run the target that calls Lock.record "locks/app.json".
```

The lines between the first and the last are `Lock.diff` output: `+ x` and `- x` for options and
sources, `~ Compiler.Version: a -> b`, `+ Reference <path>`, `~ Package Id: 1.0 -> 2.0`, and so
on. Nothing is written when the build fails.

A hash mismatch:

```
[ERROR] 1> ('user') hash mismatch:
/home/me/repo/out/lib.dll: expected 55bc2c71..., got d3cd0ca2...
```

Every mismatching file is listed at once.
Be aware: adding a new file that the glob matches does not by itself rerun the target (the
fileset is expanded, not tracked). The gate fires the next time the target rebuilds for another
reason. Run a check target (next section) in CI to catch it.

`nofailonerror` in the block turns a drift into a logged error and compiles what the settings
say, without touching the lock.

## 4. Updating a lock deliberately

The lock never updates itself. There are two ways to update it:

| Way | When |
|---|---|
| delete the lock file and build | a missing lock means "record it" |
| run a target of your own that calls `Lock.record` | the normal way; reviewable in the same PR |

Share the settings between the compile and the update by keeping them in one recipe:

```fsharp
let app = recipe {
    let! c = csc {
        targetfwk "net-4.6.2"
        target Library
        src !!"src/*.cs"
        grefs ["System.dll"]
        out (File.make "out/app.dll")
        resolve }
    return c }

do xakeScript {
    rules [
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

```bash
dotnet fsi build.fsx -- -- update-locks     # rewrite the lock, compile nothing
dotnet fsi build.fsx -- -- check-locks      # CI: fail when the lock is stale, write nothing
```

`Lock.record` hashes the compilation and overwrites the lock. `Lock.verify` returns the same
differences `Lock.build` would fail on, `[]` when the lock is current.

## 5. Importing an msbuild project

For an existing `.csproj`, do not rewrite it as `csc {}` settings. Import it: `Project.import`
asks msbuild, once, what it would hand the compiler (a design-time build: the compiler reports
its command line instead of running) and writes that into a lock.

```fsharp
do xakeScript {
    rules [
        "locks/hello.json" ..> recipe {
            let! lockFile = getTargetFile ()
            do! Project.import {
                Project.ImportOptions.Default with
                    Projects = [ "src/Hello/Hello.csproj" ]
                    Frameworks = [ "netstandard2.0"; "net472" ]
                    Output = lockFile.FullName }
        }
    ]
}
```

`ImportOptions`:

| Field | Meaning | Default |
|---|---|---|
| `Projects` | project files; all land in one lock | `[]` (an error) |
| `Frameworks` | target frameworks; one entry per (project, framework) the project declares | `[]` (an error) |
| `Configuration` | msbuild configuration | `"Release"` |
| `Properties` | extra msbuild properties, for example `["Brand", "Acme"]` | `[]` |
| `Variant` | names the `obj/xake/<framework>/<variant>/` subtree; give each property set its own | `""` |
| `Output` | the lock file to write | `""` |
| `Roots` | extra `("$(Name)", path)` roots for sibling repositories | `[]` |

Make the import the recipe of a file rule over the lock, as above: msbuild then runs only when a
project file or one of the msbuild files it imports changes, or when the git commit changes. A
frameworks list may name frameworks some projects do not target; those are skipped with a
message.

The import warns when the SDK is not pinned:

```
[WARN] 'Hello': the SDK is not pinned (none) -- the lock's compiler (5.9.0-1.26423.113, SDK 10.0.401) will drift with every SDK the machine picks; pin it with global.json { sdk: { version, rollForward: "disable" } }
```

Several variants of the same projects (brands, editions) are several locks: one import rule
each, with its own `Properties`, `Variant` and `Output`. See the `ImportOptions` reference in
[hermetic/lock.md](hermetic/lock.md#where-a-lockentry-comes-from).

A project reference into a sibling checkout needs an extra root, or its paths land in the lock
untokenized:

```fsharp
Roots = [ "$(OtherRepoRoot)", "../other-repo" ]      // relative to the project root
```

A script that reads such a lock passes the same roots: `Lock.loadWith roots path`.
**(untested)**

## 6. Building from an imported lock

`Lock.build` is for compilations composed in the script. An imported lock is replayed entry by
entry with `Lock.compile`, which restores what is missing, resolves the commit token, and runs
the compiler with exactly the recorded command line. No msbuild runs.

The smallest form, compiling every entry (this was run):

```fsharp
"build" => recipe {
    let! lock = Lock.load "locks/hello.json"
    for entry in lock.Entries do
        do! Lock.compile entry
}
```

`Lock.load` depends on the lock file, so the import rule from section 5 runs first when needed.
Each assembly is written where msbuild's compiler would write it, the `/out:` of the entry
(under `obj/xake/<framework>/` for an import).

With project references between the imported projects, use one file rule per output instead. A
project reference in the lock has no hash and points at the path msbuild would have built it
at; map it to the path your rule builds it at, `need` that first, then compile. This is the
pattern of the maintainers' dataengine script **(untested here)**:

```fsharp
target "src/(proj:*)/obj/xake/(fwk:*)/(brand:*)/(name:*).dll" {
    let! m = getRuleMatches()
    let fwk, brand, name = m.["fwk"], m.["brand"], m.["name"]
    let! options = getCtxOptions()
    let relative (p: string) = System.IO.Path.GetRelativePath (options.ProjectRoot, p)

    let! lock = Lock.load $"locks/%s{brand}.json"
    let entry = Lock.entryFor fwk name lock

    // the output of another entry of the same lock, by project file name
    let outputOf (refPath: string) =
        (Lock.entryFor fwk (System.IO.Path.GetFileNameWithoutExtension refPath) lock).Csc.Output
        |> Option.defaultWith (fun () -> failwithf "'%s' has no /out: in the lock" refPath)

    let unbuilt =
        entry.Csc.Dependencies.References
        |> List.filter (fun r -> r.Sha256 = "")
        |> List.map (fun r -> r.Path) |> Set.ofList
    let mapped = entry |> Lock.mapPaths (fun p -> if unbuilt.Contains p then outputOf p else p)

    do! need (unbuilt |> Set.toList |> List.map (outputOf >> relative))
    do! Lock.compile mapped
}
```

`Lock.entryFor framework name` is the unambiguous lookup; `Lock.entry name` fails when a name
exists for several frameworks. Caution: keying the remap on an empty hash breaks when a stale
`bin/Release` output existed at import time (it gets hashed). Keying it on "the file name is
another entry of the same framework" is more robust.

## 7. Restore on a clean machine

A lock names every package file by id and version, so a machine that does not have them can
fetch exactly those, with one `dotnet restore` for the whole set, and nothing else. By default
`Lock.compile` and `Lock.build` do this automatically into the machine's NuGet cache.

To keep the packages in a folder of the build's own (for a build agent cache), name the folder
once and use it both to read the lock and to restore (this was run):

```fsharp
"restore" => recipe {
    let! options = Restore.into ".packages"              // relative to the project root
    let! lock = Lock.loadWith (Roots.packageRootOverride (Restore.packageRoot options)) "locks/app.json"
    do! Lock.restore options lock
}
```

and in the compile rule:

```fsharp
let! options = Restore.into ".packages"
let! lock = Lock.loadWith (Roots.packageRootOverride (Restore.packageRoot options)) "locks/app.json"
let entry = Lock.entryFor "net-4.6.2" "app" lock
do! Lock.compileWith { Lock.Options.Default with Restore = options } entry
```

The same folder has to appear in both places; otherwise `$(NuGetPackageRoot)` in the lock
expands to the machine cache and the restore fills a folder nobody reads.

To forbid the network, turn restore off. A missing package then fails the build before the
compiler runs, listing every missing file with its expected hash:

```fsharp
do! Lock.compileWith { Lock.Options.Default with Restore = { options with Enabled = false } } entry
```

```
[WARN] 1 package(s) named by the lock are not in '/home/me/repo/.packages' and automatic restore is off (Restore.Options.Enabled): microsoft.netframework.referenceassemblies.net462 1.0.3
[ERROR] ('app') hash mismatch:
.../mscorlib.dll: expected f8b1..., got missing
```

`Lock.restore` fails when a restored package's SHA-512 is not the one the lock recorded:

```
restoring the packages of the lock failed:
Foo.Bar 1.2.3: expected sha512 <base64>, got <base64>
```

Only what the compiler reads is restored (the packages behind references, analyzers and the
compiler), not the whole restore graph. Details: [hermetic/restore.md](hermetic/restore.md).

## 8. Producing an SBOM

`Sbom.forAssembly` builds a CycloneDX 1.6 bill of materials for one assembly from its lock entry:
every package of the recorded restore graph as a component (`pkg:nuget/<Id>@<Version>`), the
files the compiler referenced nested under their package with SHA-256, and the compiler, SDK and
analyzers under `formulation` (this was run):

```fsharp
"sbom/hello.cdx.json" ..> recipe {
    let! lock = Lock.load "locks/hello.json"
    let entry = Lock.entryFor "netstandard2.0" "Hello" lock
    let assembly = entry.Csc.Output.Value
    do! need [ assembly ]                                  // however your script builds it
    let bom = Sbom.forAssembly (Roots.nugetRoot ()) entry assembly
    let! target = getTargetFullName ()
    System.IO.Directory.CreateDirectory (System.IO.Path.GetDirectoryName target) |> ignore
    System.IO.File.WriteAllText (target, Sbom.cycloneDx bom)
}
```

The first argument is the package cache the supplier and license fields are read from. If you
restore into your own folder, pass `Restore.packageRoot options` instead. The `need` line works
when a rule produces the assembly or the file already exists (it was run after the phony
"build" of section 6).

The output is deterministic: no timestamp, and a serial number derived from the content. Two
builds of the same commit give the same bytes, so the SBOM can be committed or compared.

For a package there are two documents:

| Function | Scope | Use |
|---|---|---|
| `Sbom.forPackage nupkg [ bom1; bom2 ]` | restore scope: the union of the assemblies' SBOMs, the nupkg hashed as root | what the build saw |
| `Sbom.forPackageScoped nupkg framework [ bom1; ... ]` | package scope: the files shipped for that framework, the nuspec dependencies as declared, nothing transitive | what a customer receives; meant to be packed into the nupkg at `Sbom.packageSbomPath framework` |

Check a package-scope document against the nupkg it describes (this was run):

```fsharp
let bom = Sbom.forPackageScoped "out/Demo.App.1.0.0.nupkg" "net462" [ evidence ]
let findings = Sbom.checkPackageScope Sbom.PackageScopeOptions.Default "out/Demo.App.1.0.0.nupkg" "net462" bom
// [] means the document passes
```

`Sbom.PackageScopeOptions.Default` treats package ids starting with `DS.`, `MESCIUS.` or
`GrapeCity.` as internal. Override `IsInternal` for your own:

```fsharp
let options = { Sbom.PackageScopeOptions.Default with IsInternal = Sbom.PackageScope.idPrefixes [ "Acme." ] }
let bom = Sbom.forPackageScopedWith options nupkg framework evidence
```

The package-scope rules still have open questions (whether to
keep `formulation`, the annotation timestamp, and four more); see
[hermetic/nuget-sbom.md](hermetic/nuget-sbom.md). The function names may change with them.

## 9. Verifying a shipped assembly

`Verify` answers "is this the same binary". It measures; it does not validate signatures.

| Function | Result |
|---|---|
| `Verify.authenticodeHash path` | SHA-256 of the PE image without `CheckSum` and the certificate table. Equal for a signed file and its unsigned build |
| `Verify.compare a b` | the differing byte ranges, each labelled `TimeDateStamp`, `CheckSum`, `CertificateTable`, `DebugDirectory/PDB id (MVID/GUID)`, `StrongNameSignature` or `Content` |
| `Verify.verdict diffs` | one line: `identical`, `identical except: <fields>, <n> bytes`, or `content differs: <n> ranges, ...` |

The functions were run on local files; a real shipped binary was not used for this guide:

```fsharp
"verify" => recipe {
    let shipped, local = "shipped/app.dll", "out/app.dll"
    do! trace Message "authenticode %s / %s" (Verify.authenticodeHash shipped) (Verify.authenticodeHash local)
    do! trace Message "%s" (Verify.verdict (Verify.compare shipped local))
}
```

Reading the result:

| Verdict | Meaning |
|---|---|
| equal Authenticode hashes | the shipped file is your build plus a signature |
| `identical except: CheckSum, CertificateTable` | the same, seen byte by byte |
| `identical except: TimeDateStamp, CheckSum, StrongNameSignature` | a tool stamped wall-clock time; see `StrongName.normalise` below |
| `content differs` | a different build; check the SDK, the checkout path and the lock |

For byte identity with `dotnet build`, build the baseline in the same directory with the same
intermediate path and `-t:Rebuild`: csc embeds absolute source paths.

## 10. Packing and signing

`Pack.nupkg` writes a nupkg whose bytes depend only on its inputs: sorted entries, one fixed
timestamp (`SOURCE_DATE_EPOCH`, else 1980-01-01), and a content-derived GUID in the package
metadata part instead of a random one (this was run):

```fsharp
"out/Demo.App.1.0.0.nupkg" ..> recipe {
    do! need [ "out/app.dll"; "app.nuspec" ]
    Pack.nupkg "out/Demo.App.1.0.0.nupkg" "app.nuspec"
        [ { Path = "lib/net462/app.dll"; Source = "out/app.dll" } ]
        Pack.Options.Default
}
```

`Pack.zip output entries options` writes a plain zip the same way, and `Pack.list` reads one
back. Details: [hermetic/pack.md](hermetic/pack.md).

Signing is a rule built with `sign {}`. The signer is a function from a request to the signed
bytes; the package ships only `Sign.fakeSigner`, which writes a real certificate-table container
around a fake payload so the pipeline can be tested end to end (this was run):

```fsharp
sign {
    target "signed/(name:*).dll"
    input (fun t -> "out/" + System.IO.Path.GetFileName t)
    certificate (Sign.KeyId "release-key")
    signer Sign.fakeSigner
}

"check-sign" => recipe {
    do! need [ "signed/app.dll" ]
    do! trace Message "same image: %b" (Sign.verifySameImage "out/app.dll" "signed/app.dll")
}
```

The other operations are `timestamp` (an RFC 3161 URL), `hashalg` (`Sign.Sha256` by default),
`description` (cosmetic, not part of the identity key), and `store` plus `budget`, which go
together and make the rule delegated: signatures are fetched from and published to the store by
identity key, and `budget` (a `Resource`) caps concurrent signer calls. `certificate` takes
`Sign.Thumbprint`, `Sign.TrustedSigning (account, profile)` or `Sign.KeyId`: a name, never key
material.

A signed file is not byte-reproducible (the timestamp embeds the signing moment). The check
that ties it back to your build is `Sign.verifySameImage`: the file is signed, and its
Authenticode hash equals the input's. Inside a script that opens `Xake.Hermetic.Dotnet`, do not
name your own values `signer`, `store` or `budget`: they clash with the operations inside the
block (FS3095). Details: [hermetic/signing.md](hermetic/signing.md).

An obfuscator or another tool that rewrites a strong-named assembly usually stamps wall-clock
time. `StrongName.normalise path timeDateStamp snkPath` sets the timestamp, re-signs the strong
name with your `.snk`, and recomputes the checksum, so two runs give the same bytes
**(untested here; covered by the package's tests)**. Details:
[hermetic/strongname.md](hermetic/strongname.md).

## 11. The compiler server switch

By default `csc` goes through the Roslyn compiler server, which saves the compiler's startup on
every compile. It does not change the output bytes and never enters the lock. Turn it off or
tune it in one place, first match wins:

| Where | Value |
|---|---|
| `noserver` or `keepalive <seconds>` in a `csc {}` block | this target only |
| script variable `CSC_SERVER` | `on`, `off`, or keepalive seconds |
| environment variable `XAKE_CSC_SERVER` | `0`, `false`, `off` or `no` turn it off |
| default | on |

```bash
dotnet fsi build.fsx -- -- build -d CSC_SERVER:off
XAKE_CSC_SERVER=0 dotnet fsi build.fsx -- -- build
```

**(untested)** for both command lines. Changing `CSC_SERVER` rebuilds the targets that read it.
A leftover server after the build is normal; `dotnet build-server shutdown` stops the SDK's.
Details: [csc-server.md](csc-server.md).

## 12. Troubleshooting

### The compiler is missing

On a machine that does not have the compiler the lock names, `Lock.compile` and `Lock.build`
explain why. `<name>` is the assembly name.

| Message | Cause | Fix |
|---|---|---|
| `'<name>': the lock names the compiler of SDK <version> (<path>), which is not installed; install that SDK or re-import with the installed one` | the lock was made with another SDK | install that SDK, or re-import (or re-record) and commit the new lock; pin the SDK in `global.json` |
| `'<name>': the compiler <path> is not available and restoring <id> <version> did not provide it` | a compiler package (`toolset "<version>"`) could not be restored | check the feed and credentials; delete a partial package folder |
| `'<name>': the compiler <path> named by the lock is not installed` | a path under the SDK root, but not under `sdk/` | re-import on this machine |
| `'<name>': the compiler <path> named by the lock does not exist` | anywhere else | re-import or re-record |
| `'<name>': the compiler <path> does not exist` | from `Csc.run` directly (no lock involved) | check `cscpath`, `toolset`/`CSC_TOOLSET` or the SDK (`global.json`) |

### A hash mismatch

```
('<name>') hash mismatch:
<path>: expected <sha256>, got <sha256>
<path>: expected <sha256>, got missing
```

| Pattern | Cause |
|---|---|
| `got missing` for many files under the package folder | restore is off, or the lock is read against a different package folder than the one restored into (section 7) |
| a single package file with a different hash | the package in the cache is not the one recorded; clear that package directory and build again |
| the compiler | a different SDK or toolset build under the same path |
| a file your own script builds | a locally built reference was recorded with its hash; prefer the imported-lock pattern (section 6), or keep such references out of a `csc { lock }` block |

### `targetfwk` required

```
'<name>': csc needs a target framework: set targetfwk in the csc block or the NETFX-TARGET script variable (e.g. targetfwk "netstandard2.0")
```

Add `targetfwk` to the block, or set `NETFX-TARGET` for the whole script
(`-d NETFX-TARGET:net-4.6.2`). The composed `csc` task accepts .NET Framework monikers and
`netstandard2.0`/`netstandard2.1`; `targetfwk "net8.0"` fails with
`'net8.0' is not a known .NET Framework profile`. For a modern target framework, import the
project instead (section 5).

### Other messages

| Message | Cause |
|---|---|
| `project '<name>' is not in the lock (<entries>)` | wrong name, or a `csc { lock }` path shared by two blocks |
| `project '<name>' is in the lock for N frameworks (...); ask for one with Lock.entryFor` | use `Lock.entryFor framework name` |
| `('<name>') restoring the packages the lock names failed:` followed by `<id> <version>: not restored (expected it at '<dir>')` | the restore could not download; the restore's own output is in the log at verbose level |
| `'<name>': the lock needs $(SourceRevisionId) but no git repository was found at or above '<dir>' ...` | the lock was imported from a git checkout with SourceLink; compile it inside a checkout, not an exported copy |
| `lock written by an older Xake (flat format with 'Projects'/'Args'); re-import` | a lock from an unreleased pre-0.1 format |
| `ImportOptions.Projects is empty: ...` | the script ran from the wrong directory and filtered its project list to nothing |
| `'<token>' is not a valid root token: expected the form $(Name)` | an extra root in `ImportOptions.Roots` or `Roots.make` |
| `sign { target "<mask>" }: no ...signer... given` | add the `signer` operation |

### The output is not byte-identical to `dotnet build`

- The SDK differs: compare `Dependencies.Compiler.Version` in the lock with the SDK msbuild used.
- The checkout path differs: csc embeds absolute paths. Build in the same directory, or add a
  `/pathmap`.
- The baseline reused stale output: build it with `-t:Rebuild` and the same
  `IntermediateOutputPath` (`obj/xake/<framework>/<variant>/`).
- A composed `csc {}` block does not pass `/deterministic` unless you add it with `args`.

## See also

The package reference: [hermetic/README.md](hermetic/README.md), [hermetic/lock.md](hermetic/lock.md),
[hermetic/restore.md](hermetic/restore.md), [hermetic/nuget-sbom.md](hermetic/nuget-sbom.md),
[hermetic/verify.md](hermetic/verify.md), [hermetic/pack.md](hermetic/pack.md),
[hermetic/strongname.md](hermetic/strongname.md), [hermetic/signing.md](hermetic/signing.md). The base
`csc` task, `resolve` and `Csc.run`: [tasks.md](tasks.md), [csc-syntax.md](csc-syntax.md). The compiler
server: [csc-server.md](csc-server.md). Design, evidence and limits:
[hermetic-architecture.md](hermetic-architecture.md).
