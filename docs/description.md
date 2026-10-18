# Project description (`xake.project.json`)

A project description says what a project is: its name, language, project file, target
frameworks, sources, resources, references, packages, options and package metadata. It is
**data only**, changes rarely and is committed next to the project. A person or an explicit import
(Xake.Hermetic.Dotnet's `Import`) writes it; the build reads it. There are no placeholders: a
variant that msbuild evaluates differently (a brand, for example) gets its own description.
Configurations, the output layout and versions belong to the build script, not to the
description.

The point is to keep the central build script short: it declares projects and where their
outputs go, and the description says what each project is.

```fsharp
open Xake
open Xake.Dotnet

do xakeScript {
    rules [
        "out/(fwk:*)/Sample.dll" ..> recipe {
            let! fwk = getRuleMatch "fwk"
            do! csc { targetfwk fwk; project "src/Sample/xake.project.json" }
        }
        "out/net8.0/Web.dll" ..> csc { project "src/Web/xake.project.json" }
    ]
}
```

Schema: [schemas/xake.project.schema.json](schemas/xake.project.schema.json). Module:
`Xake.Dotnet.Description` (`src/dotnet/Description.fs`), in the `Xake` package since 3.9. The
composition is `Xake.Dotnet.ProjectPlan` (`src/dotnet/ProjectPlan.fs`).

## Status

Xake 3.9 moves the description model from Xake.Hermetic.Dotnet into the Xake package so that the
composed `csc {}` / `fsc {}` can be fed from a committed description. That is the whole move:

- **In the Xake package (this page):** the format (`Description`: parse, validate, format,
  load/save/find, `beside`), the schema, and `csc { project }` / `fsc { project }` for what a
  description says by itself -- sources, resources, defines, options, assembly attributes,
  framework assemblies, shared frameworks of the SDK. No msbuild runs, no package is restored,
  nothing is locked.
- **In Xake.Hermetic.Dotnet (unchanged):** the import from `.csproj`/`.fsproj` (`Import`), package
  restore (`Described.restore`, `packages`), locks (`lock`, `Lock`, `Locks`), configuration
  presets (`config`), SBOM, verification, packing and signing. A description that needs any of
  that is rejected by the composed path with a message naming the package.
- **Not built:** msbuild emulation. The composed path is for simple projects and ASP.NET Core
  projects whose only "package" is a shared framework (`Microsoft.AspNetCore.App`).

The schema is the hermetic package's latest (0.3 on branch `pr/deploy`: `resources` with
`byFramework` resources, `noWarn`, `assemblyAttributes`) plus one field, `frameworkReferences`.
Until Xake.Hermetic.Dotnet builds on Xake `[3.9, 4.0)` it keeps its own copy of the module
(`src/hermetic/Description.fs`) and of the schema (`docs/hermetic/xake.project.schema.json`),
which does not know `frameworkReferences`; after the 3.9 release it deletes both and uses
`Xake.Dotnet.Description`.

## Example

```json
{
  "$schema": "../../docs/schemas/xake.project.schema.json",
  "name": "Web",
  "project": "Web.csproj",
  "rootNamespace": "Sample.Web",
  "language": "csharp",
  "kind": "exe",
  "frameworks": ["net8.0"],
  "sources": ["**/*.cs", "!obj/**/*", "!bin/**/*"],
  "resources": [{ "file": "data/greeting.txt" }, { "file": "Properties/Strings.resx" }],
  "defines": ["FEATURE"],
  "references": ["../lib/Vendor.dll"],
  "frameworkReferences": ["Microsoft.AspNetCore.App"],
  "options": { "langVersion": "12.0", "nullable": "enable", "noWarn": ["CS1591"] },
  "internalsVisibleTo": ["Web.Tests"]
}
```

## Fields

`project` is relative to the description. Every other path is relative to the project file's
folder. The last column says what the composed `csc { project }` / `fsc { project }` does with a
field.

| Field | Type | Meaning | Composed path |
|---|---|---|---|
| `$schema` | string | optional; kept as written | -- |
| `name` | string, required | project key: the project file name without extension; unique in a build | messages; the default root namespace |
| `project` | string, required | the project file, relative to the description | its folder is the base of every other path and the compiler's working directory; the file itself is never read |
| `assemblyName` | string | default: `name` | not used: the output is the block's `out` or the rule's target |
| `rootNamespace` | string | | default manifest names of resources |
| `language` | `fsharp` \| `csharp`, required | | must match the builder (`fsc`/`csc`) |
| `kind` | `library` \| `exe` | default `library`; always written | `/target:` unless the block gives `target` |
| `frameworks` | string list, required, at least one | target frameworks (TFMs) | `targetfwk` picks one, else the only one, else `NETFX-TARGET` |
| `sources` | string list, required | in compile order (it matters for F#); Xake masks allowed, `!` excludes what the entries before it included | expanded through `getFiles` (an added file reruns the target); listed order when no entry has a wildcard, sorted (ordinal) otherwise; replaced by the block's `src` |
| `resources` | `{ file, name? }` list | a plain file, embedded as is, or a `.resx`, compiled to a `.resources`; `name`: the manifest name, default msbuild's (below) | `/resource:path,name`; a `.resx` is compiled under `obj/xake/<key>/<framework>/`; a culture-specific file (`Strings.de.resx`) is **rejected** (satellite assemblies are not built) |
| `defines` | string list | | added to the SDK's framework symbols (`NETSTANDARD2_0`, `NET8_0_OR_GREATER`, ...); replaced by the block's `define` (the framework symbols stay) |
| `references` | string list | framework assemblies by name (`System.Windows.Forms`); an entry with a folder (`lib/Foo.dll`) is an assembly by path | by name: `grefs` from the target framework's reference assemblies (plus the SDK's implicit ones for .NET Framework, `netstandard` for C# netstandard); by path: `ref` |
| `frameworkReferences` | list of `Microsoft.NETCore.App` \| `Microsoft.AspNetCore.App` \| `Microsoft.WindowsDesktop.App` | shared frameworks (msbuild's `FrameworkReference`) | the targeting pack's assemblies from `<dotnet>/packs/<name>.Ref/<version>/ref/<tfm>` at the version the SDK's `BundledVersions.props` names, or that one NuGet package when no pack is installed (`NUGET_FETCH` honoured); .NET 5+ only; WindowsDesktop needs a `-windows` framework |
| `projectRefs` | string list | names of other described projects | each one's output must be among the block's `ref` (file name = project name), else **rejected**: the composed path neither builds nor locates project references |
| `packages` | `{ id, version?, private? }` list | `version` as declared (exact or a NuGet range) | **rejected** (needs a restore: Xake.Hermetic.Dotnet). F# `FSharp.Core` is the exception: fsc composes its own (`FSHARP_CORE_VERSION`, default 8.0.100; the SDK's for .NET) |
| `options` | object | `doc`, `langVersion`, `warningsAsErrors`, `noWarn`, `nullable`, `keyFile`, `args` | below |
| `internalsVisibleTo` | list of string or `{ name, key? }` | friend assemblies | a generated source, first among the sources |
| `assemblyAttributes` | `{ type, args? }` list | assembly-level attributes, `args` as source text (`"\"text\""` for a string) | the same generated source, after the friends |
| `byFramework` | object keyed by TFM | additions for one framework: `defines`, `references`, `frameworkReferences`, `packages`, `args`, `resources`; each key must be one of `frameworks` | the chosen framework's are appended to the project-wide ones |
| `package` | object | packing metadata: `id` (required), `description`, `authors`, `copyright`, `tags`, `license`, `projectUrl`, `repository`, `icon`, `readme`, `bundle` | not used (packing is Xake.Hermetic.Dotnet's `Pack`) |

`options` in the composed path (C# switch / F# switch):

| Option | C# | F# |
|---|---|---|
| always | `/deterministic+` (the SDK's default) | `--deterministic+` |
| `langVersion` | `/langversion:<v>` | `--langversion:<v>` |
| `warningsAsErrors` | `/warnaserror+` | `--warnaserror+` |
| `noWarn` | `/nowarn:1701,1702,<list>` (the SDK's defaults first) | `--nowarn:<list>` |
| `nullable` | `/nullable:<v>` | `enable` -> `--checknulls+`, others nothing |
| `keyFile` | `/keyfile:<path>` | `--keyfile:<path>` |
| `doc` | `/doc:<out>.xml` | `--doc:<out>.xml` unless the block gives `doc` |
| `args` | appended after the above, before the block's own `args` | same |

Configuration (`optimize`, `debug`, `TRACE`/`RELEASE`), platform, `unsafe` and `checked` are not
description fields: the block's own operations (`platform`, `unsafe`, `args ["/optimize+"]`) or the
description's raw `args` carry them. `config "Release"` (the SDK's presets) stays in
Xake.Hermetic.Dotnet for now. The SDK's generated `AssemblyInfo` (company, version, target
framework attribute) and implicit global usings are not generated: the description lists the
attributes it wants, and the sources their `using`s.

The default manifest name of a resource is msbuild's (`CreateCSharpManifestResourceName`,
`ProjectPlan.manifestName`): `rootNamespace` (else `name`), the file's folder with `/` -> `.` and
each part made an identifier (`1st Folder` -> `_1st_Folder`; a folder that comes out as `_` is
`__`), the file name; a `.resx` without its extension, plus `.resources`
(`Properties/Resources.resx` -> `<ns>.Properties.Resources.resources`).

Unknown fields are errors. Every error names the field by its path, e.g.
`'packages[0].id': required`, `'byFramework.net462': not one of the frameworks` or
`'frameworkReferences[0]': 'Web' is not one of Microsoft.NETCore.App, ...`.

## The block and the description

`project "path"` can stand anywhere in the block: it records the description, and the
composition reads it when the block runs (the file becomes an input of the target). What the
block gives explicitly wins:

| Block | Description |
|---|---|
| `targetfwk` | picks the framework; it must be one of `frameworks` |
| `out` | the output; without it, the rule's target |
| `target` | wins over `kind` |
| `src` | replaces `sources` |
| `define` | replaces `defines`; the framework symbols stay |
| `ref`, `grefs`, `resources` | added after the description's (and `ref` carries the `projectRefs` outputs) |
| `args` | after the description's switches: the compiler takes the last occurrence |
| `doc` (fsc) | wins over `options.doc` |
| `platform`, `unsafe`, `toolset`, `cscpath`, `noserver`, ... | as for any block |

`resolve` returns the composed `Csc`/`Fsc` (sources expanded), as for settings.

Rejections, each naming the description:

- `the description references packages (Newtonsoft.Json 13.0.3) for netstandard2.0; the composed
  csc does not restore packages. Build it with Xake.Hermetic.Dotnet ...`
- `projectRefs 'Core' have no output path; pass each one's output with ref ...`
- `resource 'Strings.de.resx' is culture-specific (de): satellite assemblies are not built ...`
- `the description 'x/xake.project.json' is C#, not for fsc`
- `the description targets netstandard2.0, net8.0; choose one with targetfwk ...`

## Format

`save` writes a fixed format, so that a description changes only when its content changes:
fields in the order of the schema, absent and empty fields left out (`kind` always written), a
value on one line when it fits in 100 columns and one item per line otherwise, two-space indent,
LF line ends, a final newline. Reading and writing a file in this format gives back the same
bytes.

## API

| Function | Signature | What it does |
|---|---|---|
| `Description.parse` | `string -> Project` | parse and validate JSON text |
| `Description.read` | `string -> Project` | `parse` a file outside a recipe; errors are prefixed with the path |
| `Description.load` | `string -> Recipe<ExecContext, Project>` | read a file (against the project root) and depend on it |
| `Description.format` | `Project -> string` | the text `save` writes |
| `Description.write` | `string -> Project -> bool` | write unless the file already holds the same text; returns whether it wrote |
| `Description.save` | `string -> Project -> Recipe<ExecContext, unit>` | `write` against the project root; records no input |
| `Description.find` | `Fileset -> string -> Recipe<ExecContext, Project>` | the description named `name` among the files of the fileset; only that file becomes an input |
| `Description.names` | `Project list -> string list` | the project names, in order |
| `Description.beside` | `string -> string` | `xake.project.json` in the project file's folder |
| `ProjectPlan.make` | `Language -> string -> string -> string -> File list -> Recipe<ExecContext, Plan>` | what a description adds to a compilation for one framework (used by `Csc.ofSettings`/`Fsc.ofSettings`) |
| `Csc.describedSettings`, `Fsc.describedSettings` | `settings -> string -> Recipe<ExecContext, settings * Plan>` | the block's settings with the description applied |
| `DotNetFwk.sharedFrameworkReferences` | `string -> string -> Recipe<ExecContext, string list>` | a shared framework's reference assemblies for a .NET framework |
