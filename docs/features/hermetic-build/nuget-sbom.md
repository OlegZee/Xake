# Nuget: SBOM evidence from the restore, not a rebuild of it

`Xake.Dotnet.Nuget` (`src/dotnet/Nuget.fs`) reads two files a `dotnet restore` already produced
-- it never talks to the network or re-resolves anything. It is the evidence layer slice 2's
`Sbom` module builds on (brief.md §8e, §11): what packages did the compile actually see, and
what do we know about each one from the cache.

## What it reads, and from where

| Source file | What it holds | Read by |
|---|---|---|
| `obj/project.assets.json` | the restore graph for every target framework: which packages, which depend on which, and the project's own direct `PackageReference`s | `Nuget.readAssets`, at **import time** (`Project.import` folds the graph into the lock as `Dependencies.Packages`, see below) |
| `<cache>/<id>/<version>/.nupkg.metadata` | the package's content hash (sha512, base64) and the feed it came from | `Nuget.readCache` |
| `<cache>/<id>/<version>/<id>.nuspec` | authors, license, repository url/commit | `Nuget.readCache` |

Both files are parsed with the existing `Json` module (a hand-written JSON parser --
`parse`, `field`, `asString`, `asArray`), not `System.Text.Json`: the assembly stays
dependency-free on netstandard2.0, same reasoning as `Fsproj.fs`. The nuspec is XML; it is read
with `System.Xml.XmlDocument`, matching elements by local name only, because nuspec schema
versions use different namespace URIs for the same element.

## `readAssets`

`project.assets.json`'s `targets` section keys on `"<framework>"` or `"<framework>/<rid>"`,
where `<framework>` is the alias (`netstandard2.0`) for a project with `TargetFrameworks` and the
**full framework name** (`.NETStandard,Version=v2.0`) for one with a single `TargetFramework` --
dataengine's, found when the import first read its assets (2026-09-24; page's multi-target
projects had hidden it). `readAssets` tries the alias and `Nuget.frameworkFullName alias`
(`netstandard*`, `netcoreapp*`, `net4xx` folded; `net5.0`+ is its own full name), exact match
first, then a prefix match -- the rid-qualified entries are for a self-contained publish, not the
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

## The graph lives in the lock (Stage B, 2026-09-24)

`Nuget.readAssets` is an import-time reader now. `Project.import` calls it right after the
design-time build, inside the per-project lock (import-race.md), and records the result as
`Lock.Package list` in the entry's `Dependencies.Packages` via `Project.packages cacheRoot
assets`: per package `Id` and `Version` (the assets file's own casing), `Sha512` (the cache's
`.nupkg.metadata` `contentHash`, base64, "" when the cache lacks it), `Direct` (a
`PackageReference` of the project's framework section) and `DependsOn` (the ids of its assets
`dependencies`). `type: project` entries are not packages and are dropped, edges included. The
SBOM step (`Sbom.forAssembly`) reads the lock only -- `obj/project.assets.json` is a restore
output and no longer a hidden input of the SBOM; the per-variant assets copy that
import-race.md's stopgap kept is gone. `readCache`, `ships` and `packageOf` stay public and are
still what the SBOM consults for supplier, license and the "does it ship" test -- the lock does
not carry those.

## From `Dependencies.References` to a package

`Lock.Entry`'s `Dependencies.References` (`src/dotnet/Project.fs`) is the list of files the compiler was
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

`Xake.Dotnet.Sbom` (`src/dotnet/Sbom.fs`) turns a `Lock.Entry` -- its references and its
package graph -- into a CycloneDX 1.6 bill of materials: `Sbom.forAssembly cacheRoot entry
assemblyPath`. Pure: `Sbom.cycloneDx` only renders a `Bom` to a string, and
`Sbom.forAssembly` only reads the assembly file it hashes -- no restore, no cache writes, no
network. Two-piece split for the same reason `Nuget` and `Project` are separate: `forAssembly`
is the join (what evidence says about what got compiled), `cycloneDx` is pure formatting that
tests can drive with a hand-built `Bom` and no lock at all.

### Lock/restore-graph fields to CycloneDX

| BOM field | Source |
|---|---|
| `metadata.component` (root) | the shipped assembly: `Name = entry.Name`; `Version` from `entry.Evaluation.Properties.["Version"]`, falling back to `InformationalVersion`; one SHA-256 hash of the assembly file (`Lock.sha256`) |
| `metadata.tools.components[0]` | `{ type: application, name: "Xake", version }`, version from `Xake.Dotnet`'s own assembly version |
| `components[].purl` | `pkg:nuget/<Id>@<Version>`, one component per package in `entry.Dependencies.Packages` -- every package the restore graph carried at import, not just the ones a compiled reference happens to be attributed to; `<Id>`/`<Version>` keep `project.assets.json`'s own casing |
| `components[].supplier`, `.licenses` | `Nuget.readCache`'s `Supplier`/`License` -- matched to the package's cache directory case-insensitively (the cache always lowercases `id`/`version`, the lock usually does not) |
| `components[].hashes` (package) | the lock's `Package.Sha512` (recorded at import from `.nupkg.metadata`'s base64 `contentHash`), converted to hex -- CycloneDX hashes are hex, NuGet's cache stores base64; empty in the lock means no hash, the cache is deliberately not consulted again |
| `components[].components[]` (nested) | the package's own referenced files, `type: file`, SHA-256 from the matching `Dependencies.References` entry; only files with a non-empty hash are listed |
| top-level `components[]` (non-nested `file`) | `Dependencies.References` entries `Nuget.packageOf` cannot place under `cacheRoot` -- a project reference, or an SDK reference pack under `$(DotnetRoot)` -- `scope: "required"`, hash omitted when the lock has none yet |
| `dependencies[]` | root depends on every *direct* (`Package.Direct`), *`scope: required`* package; package-to-package edges come from `Package.DependsOn` (a dependency's version resolved by id within the same `Packages` list), kept only where both ends are packages already in the BOM |
| `formulation[0].components[]` | one `file` per `Dependencies.Analyzers` entry, one `application` for the compiler (`csc`, `Compiler.Sha256`, version `Compiler.Version`), one for the SDK (`.NET SDK`, `Evaluation.Sdk`) -- all `scope: "excluded"` |

### Scope: every restore-graph package is a component

`forAssembly` no longer starts from the references and works backwards to the packages it
can attribute a file to -- it starts from `entry.Dependencies.Packages`, the *whole* restore graph, and
attaches referenced files where `Nuget.packageOf` finds them. A scanner wants the graph, not
just what happened to get linked (brief.md §8e): a package nobody referenced but that the
restore still pulled in (a platform/reference-assembly pack, a runtime-only package) is still a
component, just `scope: "excluded"` rather than dropped.

Two cases, by whether any file was ever attributed to the package:

- **Has a referenced file** (`Nuget.packageOf` placed at least one `Dependencies.References` entry
  under it): a package can ship both a compile-time reference assembly (under a `ref/<tfm>/`
  folder -- a facade with no method bodies, used only to compile against) and the real
  implementation (`lib/<tfm>/`, or a runtime-specific `runtimes/<rid>/lib/`). If **every**
  referenced file is under `ref/`, the component is `scope: "excluded"` -- it never shipped. If
  **any** referenced file is under `lib/` (or anywhere else), `scope: "required"`. The `ref/`
  match is on a whole path segment, case-insensitively, not a substring test -- a package id or
  file name that merely contains "ref" does not trip it. Nested `file` components carry those
  referenced dlls.
- **No referenced file at all** (nothing in `Dependencies.References` maps to it -- true of every
  package `dotnet cyclonedx`'s own SBOM lists that ours previously dropped, e.g.
  `Microsoft.NETCore.Platforms`, a `*.ReferenceAssemblies.*` pack, `System.ValueTuple`): `scope:
  "required"`, with no nested files, when the package is reachable from a direct dependency
  (`Package.Direct`) by following `DependsOn` edges *and* `Nuget.ships` finds a `lib/` or
  `runtimes/` directory with files in its cache entry -- a runtime-only package that ships
  without ever being `/reference`d. Otherwise `scope: "excluded"` -- unreachable, or a pure
  reference-assembly pack with nothing that actually ships.

### Ids: the restore graph's own casing, matched case-insensitively against the cache

`project.assets.json` spells package ids the way the `PackageReference` (or a dependency's own
metadata) spelled them -- `Foo.Bar`, not `foo.bar`. `bom-ref`/`purl` use that casing verbatim, so
`pkg:nuget/Foo.Bar@1.2.3` matches what `dotnet cyclonedx` and other scanners emit for the same
package (the comparison in `sbom-compare.txt` found ours all-lowercase, theirs original-cased --
this closes that gap). `Nuget.packageOf` still returns the cache's own lowercase directory
names, so referenced files are joined to the lock's package entries via a case-insensitive key
(`id.ToLowerInvariant(), version.ToLowerInvariant()`), never by requiring the two to already
agree on case.

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

### `forPackage`: one BOM per shipped nupkg

`forAssembly` answers "what is in this dll". `Sbom.forPackage (nupkgPath: string) (assemblies:
Bom list) : Bom` is the per-nupkg BOM brief.md §8e asks for: the union of its assemblies'
`forAssembly` BOMs, with the nupkg's own identity and hash as `metadata.component`, so a
customer who takes a package rather than individual dlls still gets one document that covers
everything inside it -- the assembly-level BOMs stay available underneath for anyone who took
the dlls directly (installer users).

- **Root**: `bom-ref = "nupkg:" + <nupkg file name without extension>`; `name`/`version` read
  from the `.nuspec` entry inside the nupkg (`System.IO.Compression.ZipFile`/`ZipArchive` --
  checked to compile and resolve on **both** `net462` and `netstandard2.0` from the SDK's own
  framework reference assemblies, no `<Reference Include="System.IO.Compression" />` needed in
  the fsproj, so `forPackage` has one implementation, not an `#if NETFRAMEWORK` split);
  `hashes` = SHA-256 and SHA-512 of the nupkg file itself.
- **Root's `components[]`** (nested): each input `Bom.Root` (the assembly roots), as `library`
  components carrying their own hashes, with no further nesting -- their evidence is already
  surfaced at the top level below, so it is not duplicated here.
- **`Components`** (top level): the union, by `bom-ref`, of every assembly BOM's `Components` --
  a package referenced by two of the nupkg's assemblies appears once, with its nested `file`
  evidence merged (the union of both assemblies' referenced files under it); `scope` widens to
  `"required"` if either assembly saw it that way.
- **`Dependencies`**: the nupkg root depends on each assembly's own root, plus the union of the
  assemblies' own dependency edges (merged by `ref`, `dependsOn` unioned).
- **`Formulation`**: the union, by `bom-ref`, of the assemblies' formulations (analyzers, `csc`,
  the SDK).

Every list is sorted by its ref before being returned, matching `cycloneDx`'s own determinism:
two calls with the same inputs produce the same `Bom`, and rendering it is byte-identical.
