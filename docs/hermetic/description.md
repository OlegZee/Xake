# Project description (`xake.project.json`)

A project description says what a project is: its name, language, project file, target
frameworks, sources, references, packages, options and package metadata. It is **data only**,
changes rarely, and is committed. An explicit import (or a person) writes it; the build reads it.
There are no placeholders: a variant that msbuild evaluates differently (a brand, for example)
gets its own description. Configurations, the output layout and versions belong to the build
script, not to the description.

Schema: [../schemas/xake.project.schema.json](../schemas/xake.project.schema.json). Module:
`Description` in `Xake.Hermetic.Dotnet` (`src/hermetic/Description.fs`).

## Example

```json
{
  "$schema": "../../docs/schemas/xake.project.schema.json",
  "name": "Xake.Dotnet",
  "project": "Xake.Dotnet.fsproj",
  "language": "fsharp",
  "kind": "library",
  "frameworks": ["netstandard2.0", "net462"],
  "sources": ["Tool.fs", "Hash.fs", "DotNetFwk.fs"],
  "projectRefs": ["Xake"],
  "packages": [{ "id": "FSharp.Core", "version": "8.0.100" }],
  "options": { "doc": true },
  "internalsVisibleTo": ["tests"],
  "byFramework": {
    "net462": { "references": ["System.Windows.Forms"] }
  },
  "package": { "id": "Xake", "license": "MIT", "bundle": ["Xake"] }
}
```

## Fields

`project` is relative to the description. Every other path is relative to the project file's
folder.

| Field | Type | Meaning |
|---|---|---|
| `$schema` | string | optional; kept as written |
| `name` | string, required | project key: the project file name without extension; unique in a build; names the lock folder |
| `project` | string, required | the project file, relative to the description (a description may live outside the project's repository) |
| `assemblyName` | string | default: `name` |
| `rootNamespace` | string | |
| `language` | `fsharp` \| `csharp`, required | |
| `kind` | `library` \| `exe` | default `library`; always written |
| `frameworks` | string list, required, at least one | target frameworks (TFMs) |
| `sources` | string list, required | in compile order (it matters for F#); for C#, msbuild's globs as Xake masks |
| `resources` | `{ file, name? }` list | a plain file, embedded as is, or a `.resx`, compiled to a `.resources` by `project`; `name`: the manifest name (msbuild's `LogicalName`, or the `ManifestResourceName` msbuild gave it when `Import.record` ran its targets), default msbuild's (below) |
| `defines` | string list | |
| `references` | string list | framework assemblies by name, e.g. `System.Windows.Forms` |
| `projectRefs` | string list | names of other described projects |
| `packages` | `{ id, version?, private? }` list | `version` as declared (exact or a NuGet range), resolved at lock generation; `private`: compile time only |
| `options` | object | `doc`, `langVersion`, `warningsAsErrors`, `noWarn` (warnings suppressed beyond the SDK's defaults, which `project` adds: C# `1701`, `1702`; F# none), `nullable` (`enable`/`disable`/`warnings`/`annotations`), `keyFile`, `args` (raw switches, appended last) |
| `internalsVisibleTo` | list of string or `{ name, key? }` | friend assemblies |
| `assemblyAttributes` | `{ type, args? }` list | assembly-level attributes, e.g. `{ "type": "System.CLSCompliantAttribute", "args": ["true"] }`; `args` are source text, emitted verbatim (`"\"text\""` for a string) |
| `byFramework` | object keyed by TFM | additions for one framework: `defines`, `references`, `packages`, `args`, `resources` (an `EmbeddedResource` under a condition on the framework); each key must be one of `frameworks` |
| `package` | object | present when the project is packed: `id` (required), `description`, `authors`, `copyright`, `tags`, `license` (SPDX), `projectUrl`, `repository { type, url }`, `icon`, `readme`, `bundle` (projectRefs packed into this package) |

The default manifest name of a resource is msbuild's (`CreateCSharpManifestResourceName`,
`Described.manifestName`): `rootNamespace` (else `name`), the file's folder with `/` -> `.` and
each part made an identifier (`1st Folder` -> `_1st_Folder`; a folder that comes out as `_`, such
as `_` or `-`, is `__`), the file name; a `.resx` without its extension, plus `.resources`
(`Properties/Resources.resx` -> `<ns>.Properties.Resources.resources`).

Unknown fields are errors. Every error names the field by its path, e.g.
`'packages[0].id': required` or `'byFramework.net462': not one of the frameworks`.

## Format

`save` writes a fixed format, so that a description changes only when its content changes:
fields in the order of the table, absent and empty fields left out (`kind` always written), a
value on one line when it fits in 100 columns and one item per line otherwise, two-space
indent, LF line ends, a final newline. Reading and writing a file in this format gives back the
same bytes.

## Import

`Import.descriptions projects descriptionOf globals` (module `Import`, `src/hermetic/Import.fs`)
writes the description of each project file from msbuild's evaluation under `globals`, e.g.
`["Configuration", "Release"; "Brand", "Acme"]`; `descriptionOf` maps a project file to its
description file (`Description.beside`, or a brand folder). msbuild evaluates the project once
without and once per target framework, through the same runner as the lock import; no targets
run, nothing is restored, and a file is rewritten only when its content changes (an existing
`$schema` is kept). How the fields are taken:

- `name`: the project file name; `assemblyName`/`rootNamespace` only when they differ from it.
- `sources`: F#, the `Compile` items in order. C#, Xake masks, not files: `**/*.cs` and
  msbuild's default excludes (`!bin/**/*`, `!obj/**/*`, `!**/.*/**/*`), then the `Compile`
  `Include`/`Remove` attributes of the repository files; `!` marks an exclude, applied in order,
  and msbuild's `dir/**` is written `dir/**/*`. The masks are expanded and compared with
  msbuild's own list; a difference is reported.
- `defines`, `references`, `packages`: what every framework has; the rest under `byFramework`.
  `TRACE` and the configuration's define, the SDK's implicit packages (`NETStandard.Library`,
  `System.ValueTuple`) and references are left out. Package versions are msbuild's
  (`Version`, `VersionOverride` or central `PackageVersion`); `PrivateAssets=all` is `private`.
  Item metadata is read whatever its case (`version="8.0.5"`, `privateassets`, `logicalname`).
- `resources`: the `EmbeddedResource` items, plain files and `.resx` alike, `LogicalName` as
  `name`. Without one, `name` is what msbuild names it when that is not the default of the file
  path: a `Link` gives the default name of the link path (`Link="Shared\x.txt"` ->
  `<ns>.Shared.x.txt`); `ManifestResourceName` on a `.resx` gives `<that>.resources`, on another
  file the file name (msbuild then sets no `LogicalName`, so csc names it after the file). A culture-specific one (`Strings.de.resx`: a satellite assembly) is left out and
  reported; one msbuild names after a class (`DependentUpon`, or a `.resx` with a `.cs` of the
  same name beside it) is kept with the default name and reported (set `name` by hand).
- `options`: `GenerateDocumentationFile`, `TreatWarningsAsErrors`, `Nullable`, `LangVersion`
  (only when a repository file sets it), the key file when `SignAssembly`, F# `OtherFlags`,
  `NoWarn` without the SDK's defaults (C# `1701;1702`). The lists `WarningsAsErrors` and
  `WarningsNotAsErrors` have no field: reported in `Dropped` when a repository file sets them,
  not seen at all when the SDK or an import outside the repository does.
- `assemblyAttributes`: the `AssemblyAttribute` items other than `InternalsVisibleTo` (those are
  `internalsVisibleTo`), arguments as msbuild's `WriteCodeFragment` writes them: `_Parameter1`,
  `_Parameter2`... positional, other metadata named (`Name = value`); `_ParameterN_IsLiteral`
  verbatim, `_ParameterN_TypeName` the type; else the types of the first constructor of the
  attribute type (when the importing process knows it, e.g. `System.CLSCompliantAttribute`:
  `true` stays `true`) that takes every value, strings otherwise. A string with `"` or `\` is
  written as a verbatim literal (`@"a""b"`).
- `package`, when `IsPackable`: `PackageId`, `Description` and `Authors` (when a repository
  file sets them), `Copyright`, `PackageTags`, `PackageLicenseExpression`, `PackageProjectUrl`,
  `RepositoryType`/`RepositoryUrl`, `PackageIcon`/`PackageReadmeFile` as the packed file's
  path; `bundle` is every `PrivateAssets=all` project reference.

Each result (`Import.Imported`) names the project and description files, whether it was
written, `Inputs` (the msbuild files read: the project, `Directory.Build.props`/`.targets` and
other repository imports; not the SDK's, package or `obj/` files) and `Dropped` (what msbuild
knows that the description has no field for: other properties and item types of the repository
files, targets, attribute arguments it cannot write, the SDK's implicit packages).

## Import that records msbuild's compile inputs: `Import.record`

`Import.record options projects` writes, for each project file, its description (as
`Import.descriptions` does) **and** one lock per configuration (`options.LockOf name cfg`), with
one entry per target framework. The lock entry is msbuild's own answer, not a Xake
derivation: see [lock.md, "Recorded locks"](lock.md#recorded-locks-importrecord). For the
description, `record` differs from `descriptions` in two points:

- The resource names and cultures come from msbuild's targets: `ManifestResourceName` and
  `LogicalName` (a `.resx` is `<ManifestResourceName>.resources`), `WithCulture` for a
  satellite resource. `name` is written only when it is not the default name. No C# parsing and
  no culture table of the importing machine are used.
- `byFramework.<tfm>.resources` holds the resources only one framework has.

For a recorded project the build takes the source masks from the lock (`Recorded.Masks`), not
from the description: an edit of the description's `sources` has no effect on the build. Change
the project file and import again.

An application (an exe, a winexe, a .NET test project) has nothing more in its description:
the apphost, `runtimeconfig.json`, `deps.json`, the config file and the copy-local files are in
the lock's `App` section, recorded from msbuild
([lock.md, "Applications"](lock.md#applications-the-app-section)). The description schema has
no field for them.

Both imports read a `Compile Include` with a property (`$(BuildTmpPath)\VersionInfo.cs`, a
shared project's `$(MSBuildThisFileDirectory)X.cs`) as the evaluated path, relative to the
project folder, and write the path of a shared project's resource relative to the project
folder.

| `RecordOptions` field | Meaning |
|---|---|
| `Configurations` | one lock per configuration, e.g. `["Release"]` |
| `Properties` | the global properties msbuild runs with, e.g. `["Brand", "Globex"]` (not `Configuration`); written into the lock, tokenized |
| `Roots` | the script's extra roots (`Roots.make`) |
| `DescriptionOf` | project file -> description file (default `Description.beside`) |
| `LockOf` | description name -> configuration -> lock file |
| `OutOf` | configuration -> framework -> project name -> output folder: where a project reference is found |

## API

| Function | Signature | What it does |
|---|---|---|
| `Description.parse` | `string -> Project` | parse and validate JSON text |
| `Description.read` | `string -> Project` | `parse` a file outside a recipe; errors are prefixed with the path |
| `Description.load` | `string -> Recipe<ExecContext, Project>` | read a file (against the project root) and depend on it |
| `Description.format` | `Project -> string` | the text `save` writes |
| `Description.write` | `string -> Project -> bool` | write unless the file already holds the same text (CRLF and LF count as equal); the timestamp of an unchanged file is kept; returns whether it wrote |
| `Description.save` | `string -> Project -> Recipe<ExecContext, unit>` | `write` against the project root; records no input |
| `Description.find` | `Fileset -> string -> Recipe<ExecContext, Project>` | the description named `name` among the files of the fileset; only that file becomes an input (one file dependency). It fails when no file, or more than one file, describes the name |
| `Description.names` | `Project list -> string list` | the project names, in order |
| `Description.beside` | `string -> string` | the conventional description of a project file: `xake.project.json` in the same folder |
| `Import.descriptions` | `string list -> (string -> string) -> (string * string) list -> Recipe<ExecContext, Import.Imported list>` | import project files into descriptions (above) |
| `Import.project` | `string -> (string -> string) -> (string * string) list -> Recipe<ExecContext, Import.Imported>` | the same for one project |
| `Import.record` | `Import.RecordOptions -> string list -> Recipe<ExecContext, Import.Imported list>` | descriptions and recorded locks ([above](#import-that-records-msbuilds-compile-inputs-importrecord)); `Imported.Locks` names the locks written |

```fsharp
open Xake
open Xake.Hermetic.Dotnet

let descriptions = !! "src/*/xake.project.json"

"pack-xake" => recipe {
    let! d = Description.find descriptions "Xake.Dotnet"   // depends on that one file only
    do! trace Info "%s targets %A" d.Name d.Frameworks
}
```
