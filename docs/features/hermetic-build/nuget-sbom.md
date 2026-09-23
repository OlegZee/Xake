# Nuget: SBOM evidence from the restore, not a rebuild of it

`Xake.Dotnet.Nuget` (`src/dotnet/Nuget.fs`) reads two files a `dotnet restore` already produced
-- it never talks to the network or re-resolves anything. It is the evidence layer slice 2's
`Sbom` module builds on (brief.md §8e, §11): what packages did the compile actually see, and
what do we know about each one from the cache.

## What it reads, and from where

| Source file | What it holds | Read by |
|---|---|---|
| `obj/project.assets.json` | the restore graph for every target framework: which packages, which depend on which, and the project's own direct `PackageReference`s | `Nuget.readAssets` |
| `<cache>/<id>/<version>/.nupkg.metadata` | the package's content hash (sha512, base64) and the feed it came from | `Nuget.readCache` |
| `<cache>/<id>/<version>/<id>.nuspec` | authors, license, repository url/commit | `Nuget.readCache` |

Both files are parsed with the existing `Fsproj.Json` module (a hand-written JSON parser --
`parse`, `field`, `asString`, `asArray`), not `System.Text.Json`: the assembly stays
dependency-free on netstandard2.0, same reasoning as `Fsproj.fs`. The nuspec is XML; it is read
with `System.Xml.XmlDocument`, matching elements by local name only, because nuspec schema
versions use different namespace URIs for the same element.

## `readAssets`

`project.assets.json`'s `targets` section keys on `"<TFM>"` or `"<TFM>/<rid>"`. `readAssets`
picks the entry whose key equals the framework alias exactly (falling back to a mere prefix
match if that ever fails) -- the rid-qualified entries are for a self-contained publish, not the
compile. Each entry there is `"<Id>/<Version>": { type: "package" | "project", dependencies: {
"<Id>": "<range>" } }`; a dependency's resolved version is found by looking up its id among the
*same* target's entries -- a range like `[8.0.0, )` is never parsed, only matched by id, because
the graph already carries the resolved version as the entry's own key.

- `Packages` excludes `type: "project"` entries -- a project reference is not a package this
  build's SBOM should list as a component; its own dependencies still show up in `Graph`, so a
  reference's transitive packages are not lost.
- `Graph` is every dependency edge, `(id, version) -> (depId, depVersion)`, from every entry
  (package or project).
- `Direct` comes from `project.frameworks.<tfm>.dependencies`, matched against the requested
  framework case-insensitively -- the SDK's own TFM aliases (`net8.0`, `netstandard2.0`) are not
  consistently cased between the `targets` and `project` sections in practice.

## `readCache`

Directory is `<cacheRoot>/<id lower>/<version lower>/`, matching how NuGet actually lays out the
global packages folder. Every field defaults to `""` rather than throwing:

- no `.nupkg.metadata` -> `Sha512 = ""`, `Source = ""` (seen with `NETStandard.Library 2.0.3`
  used while developing this, restored via an older client)
- no nuspec -> `Supplier`, `License`, `Repository`, `Commit` all `""`
- no package directory at all -> `Directory = ""` and everything else `""`; the caller (`Sbom`,
  eventually a policy check) decides whether a missing package is fatal, not this module

`License` prefers the modern `<license type="expression">MIT</license>` (or `type="file"`,
where the element's text is the file name inside the package); when neither is present it falls
back to the legacy `<licenseUrl>`. `Repository`/`Commit` come from `<repository url="..."
commit="..." />`; either is `""` when the nuspec has no `<repository>` element at all (older
packages, e.g. `NETStandard.Library`).

## What is not available offline

Vulnerability data (CVEs, advisories) is not in `project.assets.json` or the cache -- it needs a
feed (`dotnet list package --vulnerable`, GitHub Advisory Database, OSV) and is explicitly out
of this module's scope, same conclusion as brief.md §8e: the build produces the SBOM, VEX and
vulnerability matching are downstream of it (Dependency-Track or similar, by purl).

## From `Lock.Project.References` to a package

`Lock.Project.References` (`src/dotnet/Project.fs`) is the list of files the compiler was
actually given, as absolute paths under the package cache, each already hashed at import time.
`Nuget.packageOf cacheRoot path` maps one such path back to the `(id, version)` it came from, by
its first two path segments below the cache root -- the same shape `readCache` writes to.
Comparison is case-insensitive on Windows, ordinal elsewhere (`Xake.Env.isUnix`), matching how
the rest of the codebase compares paths (`src/core/Path.fs`, `src/core/File.fs`). A path outside
`cacheRoot` (a project reference's own output, a framework reference from the SDK, not the
NuGet cache) yields `None` -- the caller's job to decide those aren't NuGet components.

This is the join `Sbom.forAssembly` needs: for every hashed reference in a compiled project's
lock, `packageOf` gives the package it belongs to, and `readCache` gives that package's identity
and license -- nested `file` components under a `purl`-identified package component, per §8e's
design ("the hash of each dll actually linked -- the from-evidence layer").

## Sbom

`Xake.Dotnet.Sbom` (`src/dotnet/Sbom.fs`) turns `Nuget.Assets` and a `Lock.Project` into a
CycloneDX 1.6 bill of materials. Pure: `Sbom.cycloneDx` only renders a `Bom` to a string, and
`Sbom.forAssembly` only reads the assembly file it hashes -- no restore, no cache writes, no
network. Two-piece split for the same reason `Nuget` and `Project` are separate: `forAssembly`
is the join (what evidence says about what got compiled), `cycloneDx` is pure formatting that
tests can drive with a hand-built `Bom` and no lock at all.

### Lock/restore-graph fields to CycloneDX

| BOM field | Source |
|---|---|
| `metadata.component` (root) | the shipped assembly: `Name = lock.Name`; `Version` from `lock.Properties.["Version"]`, falling back to `InformationalVersion`; one SHA-256 hash of the assembly file (`Lock.sha256`) |
| `metadata.tools.components[0]` | `{ type: application, name: "Xake", version }`, version from `Xake.Dotnet`'s own assembly version |
| `components[].purl` | `pkg:nuget/<Id>@<Version>`, one component per package found via `Nuget.packageOf cacheRoot` on `lock.References`, grouped |
| `components[].supplier`, `.licenses` | `Nuget.readCache`'s `Supplier`/`License` |
| `components[].hashes` (package) | `Nuget.readCache`'s `Sha512` (`.nupkg.metadata`'s base64 `contentHash`), converted to hex -- CycloneDX hashes are hex, NuGet's cache stores base64 |
| `components[].components[]` (nested) | the package's own referenced files, `type: file`, SHA-256 from the matching `lock.References` entry; only files with a non-empty hash are listed |
| top-level `components[]` (non-nested `file`) | `lock.References` entries `Nuget.packageOf` cannot place under `cacheRoot` -- a project reference, or an SDK reference pack under `$(DotnetRoot)` -- `scope: "required"`, hash omitted when the lock has none yet |
| `dependencies[]` | root depends on every *direct* (`assets.Direct`), *`scope: required`* package; package-to-package edges come from `assets.Graph`, kept only where both ends are packages already in the BOM |
| `formulation[0].components[]` | one `file` per `lock.Analyzers` entry, one `application` for the compiler (`csc`, `lock.Compiler.Sha256`), one for the SDK (`.NET SDK`, `lock.Compiler.Sdk`) -- all `scope: "excluded"` |

### `ref/` vs `lib/` scope

A package can ship both a compile-time reference assembly (under a `ref/<tfm>/` folder --
a facade with no method bodies, used only to compile against) and the real implementation
(`lib/<tfm>/`, or a runtime-specific `runtimes/<rid>/lib/`). Only what a project actually
*references* shows up in `lock.References`, so the rule is per package, from what was
referenced: if **every** file `Nuget.packageOf` places under that package came from a `ref/`
folder, the whole package component is `scope: "excluded"` -- it never shipped, so a scanner
should not flag it as a runtime dependency. If **any** referenced file is under `lib/` (or
anywhere else), the package is `scope: "required"`. The match is on `ref` as a whole path
segment, case-insensitively, not a substring test -- a package id or file name that merely
contains "ref" does not trip it.

### Determinism

`cycloneDx` never writes `metadata.timestamp` -- two builds of the same tag produce the same
document. `serialNumber` is computed from the document's own content instead of a random GUID:
the JSON is rendered once with a placeholder serial, SHA-256'd, and the first 16 bytes of that
hash become a UUID (version nibble and variant bits forced, the rest is hash bytes) written
back in as the real `serialNumber`. Two calls on the same `Bom` byte-for-byte match; changing
one hash anywhere in the document changes the serial, since it changes the hashed content.
Every array that CycloneDX does not itself order is sorted before rendering (`components` by
`bom-ref`, nested file components by name, `dependsOn` lists sorted and de-duplicated) so a
lock diff and a BOM diff tell the same story. A later option can set `metadata.timestamp` from
`SOURCE_DATE_EPOCH` for a build system that wants a real date without losing byte-identity
across a rebuild of the same commit -- not wired up yet.

### What `forPackage` will add later

`forAssembly` answers "what is in this dll". `Sbom.forPackage` (not built yet) is the per-nupkg
BOM brief.md §8e asks for: the union of its assemblies' `forAssembly` BOMs, with the nupkg's own
hash as `metadata.component`, so a customer who takes a package rather than individual dlls
still gets one document that covers everything inside it -- the assembly-level BOMs stay
available underneath for anyone who took the dlls directly (installer users).
