# Project description (`xake.project.json`)

A project description says what a project is: its name, language, project file, target
frameworks, sources, references, packages, options and package metadata. It is **data only**,
changes rarely, and is committed. An explicit import (or a person) writes it; the build reads it.
There are no placeholders: a variant that msbuild evaluates differently (a brand, for example)
gets its own description. Configurations, the output layout and versions belong to the build
script, not to the description.

Schema: [../schemas/xake.project.schema.json](../schemas/xake.project.schema.json). Module:
`Description` in `Xake.Hermetic.Dotnet` (`src/hermetic/Description.fs`).

> The model is moving into the Xake package (3.9): `Xake.Dotnet.Description`, page
> [../description.md](../description.md), schema `docs/schemas/xake.project.schema.json` (adds
> `frameworkReferences`). Until this package builds on Xake 3.9, its own `Description` reads the
> schema copy [xake.project.schema.json](xake.project.schema.json) beside this page.

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
| `resources` | `{ file, name? }` list | `name`: manifest name |
| `defines` | string list | |
| `references` | string list | framework assemblies by name, e.g. `System.Windows.Forms` |
| `projectRefs` | string list | names of other described projects |
| `packages` | `{ id, version?, private? }` list | `version` as declared (exact or a NuGet range), resolved at lock generation; `private`: compile time only |
| `options` | object | `doc`, `langVersion`, `warningsAsErrors`, `nullable` (`enable`/`disable`/`warnings`/`annotations`), `keyFile`, `args` (raw switches, appended last) |
| `internalsVisibleTo` | list of string or `{ name, key? }` | friend assemblies |
| `byFramework` | object keyed by TFM | additions for one framework: `defines`, `references`, `packages`, `args`; each key must be one of `frameworks` |
| `package` | object | present when the project is packed: `id` (required), `description`, `authors`, `copyright`, `tags`, `license` (SPDX), `projectUrl`, `repository { type, url }`, `icon`, `readme`, `bundle` (projectRefs packed into this package) |

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
`["Configuration", "Release"; "Brand", "MESCIUS"]`; `descriptionOf` maps a project file to its
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
- `options`: `GenerateDocumentationFile`, `TreatWarningsAsErrors`, `Nullable`, `LangVersion`
  (only when a repository file sets it), the key file when `SignAssembly`, F# `OtherFlags`.
- `package`, when `IsPackable`: `PackageId`, `Description` and `Authors` (when a repository
  file sets them), `Copyright`, `PackageTags`, `PackageLicenseExpression`, `PackageProjectUrl`,
  `RepositoryType`/`RepositoryUrl`, `PackageIcon`/`PackageReadmeFile` as the packed file's
  path; `bundle` is every `PrivateAssets=all` project reference.

Each result (`Import.Imported`) names the project and description files, whether it was
written, `Inputs` (the msbuild files read: the project, `Directory.Build.props`/`.targets` and
other repository imports; not the SDK's, package or `obj/` files) and `Dropped` (what msbuild
knows that the description has no field for: other properties and item types of the repository
files, targets, other assembly attributes, the SDK's implicit packages).

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

```fsharp
open Xake
open Xake.Hermetic.Dotnet

let descriptions = !! "src/*/xake.project.json"

"pack-xake" => recipe {
    let! d = Description.find descriptions "Xake.Dotnet"   // depends on that one file only
    do! trace Info "%s targets %A" d.Name d.Frameworks
}
```
