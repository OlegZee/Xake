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
