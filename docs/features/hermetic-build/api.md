# Xake.Dotnet: the public surface as of 2026-09-29

The input to the API-polish and namespace-extraction plan. Everything below is in namespace
`Xake.Dotnet`, assembly `Xake.Dotnet.dll`, compiled in this order: `DotNetFwk`,
`ResourceFileset`, `Resx`, `DotnetTasks`, `Json` (internal), `Roots`, `Fsproj`, `Nuget`,
`Project.fs` (`CscArgs`, `Git`, `Lock`, `Project`), `Restore`, `Sbom`, `Verify`, `StrongName`,
`Pack`, `Sign`, `Dotnet.csc.fs` (`CscImpl`), `Dotnet.fsc.fs`, `Dotnet.resgen.fs`,
`Dotnet.Msbuild.fs`.

Signatures are as the source declares them. `Recipe<'r>` below is short for
`Recipe<ExecContext, 'r>`. `internal` and `private` names are left out, except where
the name matters for the plan (`Json`, `run`, `resolve`). Record fields are listed in
declaration order.

⚠ marks a name that reads awkwardly or does not match its neighbours, with the reason in one
line. It is a flag for the plan. Nothing has been renamed.

---

## Part 1: hermetic (feature/hermetic-build)

### `CscArgs` (Project.fs): the csc command line as data

| Name | Signature | Purpose |
|---|---|---|
| `Arg` | `Source of string \| Switch of name: string * value: string` | one argument |
| `parse` | `string -> Arg` | `/r:a.dll` → `Switch ("r", "a.dll")`; name lowercased; an absolute Unix path stays a `Source` |
| `canonical` | `string -> string` | folds aliases (`r`→`reference`, `a`→`analyzer`, `res`→`resource`, `d`→`define`, ...) |
| `paths` | `Arg -> string list` | the file paths one argument names |
| `mapPaths` | `(string -> string) -> Arg -> Arg` | rewrites the paths inside one argument |
| `format` | `Arg -> string` | back to text |
| `inputs` | `string list -> string list` | files a command line reads (sources and input switches) |
| `outputs` | `string list -> string list` | files it writes |
| `sources` | `string list -> string list` | source files only |
| `switchValues` | `name: string -> string list -> string list` | paths named by every switch of that canonical name |
| `absolutize` | `dir: string -> string list -> string list` | every path made absolute against `dir`, forward slashes |

⚠ `CscArgs`: a module named for csc in `Project.fs`, but `Lock.Compilation` is the structured form actually stored. There are two command-line models and neither name points to the other.

### `Git` (Project.fs): the current commit, read from `.git` with no `git` executable

| Name | Signature | Purpose |
|---|---|---|
| `headFiles` | `dir: string -> string list` | `HEAD` plus the ref file (or `packed-refs`) it resolves through; the import `needFiles` them |
| `headSha` | `dir: string -> string option` | the commit sha; `run` resolves `$(SourceRevisionId)` with it |

⚠ `Git`: a generic, public, top-level name in `Xake.Dotnet` for two helpers only the import and the runner use.

### `Lock` (Project.fs): the recorded compilation

Types:

| Name | Shape | Purpose |
|---|---|---|
| `Hashed` | `{ Path; Sha256 }` | a non-source file the build reads; `Sha256` "" = not computed / did not exist |
| `Reference` | `{ Path; Sha256; Alias }` | one `/reference:` item; `Alias` "" unless `extern alias` |
| `Compiler` | `{ Tool; Path; Sha256; Version }` | `Tool` = "csc"/"fsc"; `Version` from the file version resource |
| `Package` | `{ Id; Version; Sha512; Direct; DependsOn: string list }` | one package of the restore graph |
| `SdkPin` | `NoGlobalJson \| Pinned of version \| RollsForward of version * policy \| NoVersion of file` | how `global.json` selects the SDK |
| `Evaluation` | `{ Project; ProjectRefs: string list; Imports: Hashed list; Sdk; SdkPin: SdkPin option; Properties: Map<string,string> }` | where the entry came from; all empty for composed `csc {}` |
| `Compilation` | `{ Directory; Options; Defines; Sources; Generated: (string*string) list; Resources: (string*string) list }` | what is compiled; `Options` carries the markers `@Sources` `@References` `@Analyzers` `@Defines` |
| `Dependencies` | `{ Compiler; References: Reference list; Analyzers: Hashed list; Packages: Package list }` | what it is compiled with and against |
| `Entry` | `{ Name; Framework; Evaluation; Compilation; Dependencies }` + members `Args: string list`, `Sources: string list`, `Output: string option` | one project for one framework; `(Name, Framework)` is the identity; `Args` is rebuilt, not stored |
| `Document` | `{ Configuration; Properties: (string*string) list; Entries: Entry list }` | one lock file = one variant, every framework |

Functions:

| Name | Signature | Purpose |
|---|---|---|
| `sdkPinText` | `SdkPin -> string` | the text written in the lock (`none`, `exact 8.0.100`, `8.0.100 rollForward:latestPatch`, `no version (<file>)`) |
| `parseSdkPin` | `string -> SdkPin option` | the inverse; "" → `None` |
| `isMarker` | `string -> bool` | whether an `Options` element is one of the four markers |
| `Compilation.ofArgs` | `string list -> Compilation * Reference list * Hashed list` | factors a flat command line into the structure (hashes empty) |
| `Compilation.args` | `Compilation -> Reference list -> Hashed list -> string list` | the flat command line back |
| `hashed` | `path: string -> Hashed` | hashes one file now |
| `compilerVersion` | `path: string -> string` | `ProductVersion` cut at `+`/space; reads the `.dll` next to a native apphost |
| `rehash` | `Entry -> Entry` | fills every `Sha256` (and an empty `Compiler.Version`) from disk: the record-time step |
| `mapPaths` | `(string -> string) -> Entry -> Entry` | rewrites every path; a rewritten hashed item loses its hash |
| `mapText` | `(string -> string) -> Entry -> Entry` | rewrites `Generated` content, `Options`, `Defines` and property values |
| `diffList` | `string list -> string list -> string list` | LCS diff, `- x` / `+ x` |
| `diff` | `Entry -> Entry -> string list` | human-readable differences between two entries of one project; `[]` = identical |
| `writeWith` | `roots -> Document -> string` | the lock text, paths tokenized against the full root list |
| `parseWith` | `roots -> string -> Document` | the inverse; refuses the flat pre-split format; distributes a legacy document-level `Framework` |
| `readWith` | `roots -> path: string -> Document` | `parseWith` of a file |
| `load` | `path: string -> Recipe<Document>` | reads a lock against the build's roots and `needFiles` it |
| `loadWith` | `extraRoots: (string*string) list -> path: string -> Recipe<Document>` | `load` with extra roots |
| `save` | `path: string -> Document -> Recipe<unit>` | writes a lock against the build's roots (no `need`) |
| `saveWith` | `extraRoots -> path -> Document -> Recipe<unit>` | `save` with extra roots |
| `entry` | `name: string -> Document -> Entry` | lookup by assembly or project file name; fails if the name matches more than one framework |
| `entryFor` | `framework: string -> name: string -> Document -> Entry` | the unambiguous lookup |

⚠ `Lock.entry` vs `Lock.Entry` vs `Lock.Document.Entries`: the lookup function has the type's name in lower case, and `Entries` is the list it searches, so `Lock.entry name doc` reads like a constructor.
⚠ `Lock.entryFor framework name` vs `Lock.entry name`: the extra key goes first, so the two lookups do not line up when read side by side.
⚠ `Lock.Compilation` is both a record type and a module (`ofArgs`, `args`), which makes `Compilation.args` look like a field access.
⚠ `Hashed` vs `Reference`: `Reference` is `Hashed` plus `Alias`, but the two are separate records (`diff` converts with a private `toHashed`). `Hashed` is an adjective where every other type name is a noun.
⚠ `Lock.hashed` vs `Lock.rehash`: one makes a `Hashed` from a path, the other re-hashes a whole `Entry`. The names do not show that they work on different levels.
⚠ `load`/`loadWith`/`save`/`saveWith` take *extra* roots, while `readWith`/`parseWith`/`writeWith` take the *full* root list, so the `With` suffix means two different things in one module.
⚠ `Lock.Package` vs `Nuget.Package`: two records with the same name and different fields (`Direct`/`DependsOn` vs `Source`/`License`/`Supplier`/`Repository`/`Commit`/`Directory`).
⚠ `Lock.SdkPin` is defined in `Lock`, but it is computed by `Project.sdkPin`. `sdkPinText`/`parseSdkPin` are not a symmetric pair (compare `format`/`parse`).
⚠ `Lock.diffList`: a generic list-diff helper that is public in `Lock`.

### `Project` (Project.fs): importing a C# project through a design-time build

| Name | Signature | Purpose |
|---|---|---|
| `sdkPin` | `projectDir: string -> SdkPin` | walks up to `global.json` |
| `ImportOptions` | `{ Projects: string list; Frameworks: string list; Configuration; Properties: (string*string) list; Variant; Output; Roots: (string*string) list }`, `Default` (Configuration "Release") | what to import and where the lock goes |
| `packages` | `cacheRoot: string -> Nuget.Assets -> Lock.Package list` | the restore graph as the lock records it (cache sha512, direct, edges) |
| `tokenizeRevision` | `sha: string -> Lock.Entry -> Lock.Entry` | replaces the commit sha with `$(SourceRevisionId)` |
| `import` | `ImportOptions -> Recipe<unit>` | per project: one restore without `TargetFramework` (`RestoreRecursive=false`), a design-time build and a `-pp` per framework; writes one `Lock.Document` to `Output` |

⚠ `Project` (C#, produces a `Lock.Entry`) and `Fsproj` (F#, produces a `ProjectInfo`) are two msbuild front ends with unrelated outputs. Neither module name says it is C#-only or F#-only.
⚠ `ImportOptions.Output` is the lock path, but in `csc {}` the same thing is `CscSettingsType.Lock`, and in `Fsproj.EvalOptions` `Output` is a different kind of file.
⚠ `Project.packages` and `Project.tokenizeRevision` are pure `Lock`/`Nuget` transforms that live in `Project`.
⚠ `Project` also names the field `Lock.Evaluation.Project` (a path), which collides in prose and in `open Lock`.

### `Roots` (Roots.fs): path tokens for machine-independent files

| Name | Signature | Purpose |
|---|---|---|
| `nugetRoot` | `unit -> string` | `NUGET_PACKAGES` or `~/.nuget/packages` |
| `dotnetRoot` | `unit -> string option` | the SDK installation root |
| `nugetPackageRootToken` | `string` = `"$(NuGetPackageRoot)"` | |
| `builtinTokens` | `string list` | `$(NuGetPackageRoot)`, `$(ProjectRoot)`, `$(DotnetRoot)` |
| `packageRootOverride` | `dir: string -> (string*string) list` | the extra-root list that points `$(NuGetPackageRoot)` at `dir` |
| `builtin` | `projectRoot: string -> (string*string) list` | the three built-in roots, longest first (pure) |
| `withExtra` | `projectRoot: string -> extra: (string*string) list -> (string*string) list` | built-in plus extra roots; an extra root replaces a built-in root of the same name; a relative path is resolved against `projectRoot` (pure) |
| `current` | `Recipe<(string*string) list>` | `builtin` for the engine's `ProjectRoot` |
| `currentWith` | `extra -> Recipe<(string*string) list>` | `withExtra` for the engine's `ProjectRoot` |

(`tokenize`, `tokenizeAll`, `expand` are internal.)

⚠ `Roots.current` vs `currentWith`: `current` is a recipe *value* and `currentWith` a function. The pure pair is `builtin`/`withExtra`, so the pure↔recipe mapping is `builtin`↔`current` and `withExtra`↔`currentWith`, and the names do not show it.
⚠ `Roots.packageRootOverride` returns an extra-roots list, while `Restore.packageRoot` returns a path. The names are close but the kinds differ.

### `Nuget` (Nuget.fs): what restore already knows (read-only)

| Name | Signature | Purpose |
|---|---|---|
| `Package` | `{ Id; Version; Sha512; Source; License; Supplier; Repository; Commit; Directory }` | one package as the cache describes it |
| `Assets` | `{ Packages: (string*string) list; Graph: ((string*string)*(string*string)) list; Direct: string list; Framework }` | one target's restore graph |
| `readAssets` | `assetsFile: string -> framework: string -> Assets` | reads `project.assets.json`; fails when there is no target for `framework` |
| `readCache` | `cacheRoot -> id -> version -> Package` | `.nupkg.metadata` + nuspec; never throws |
| `NuspecDependency` | `{ Id; Range }` | a `<dependency>` exactly as written |
| `NuspecGroup` | `{ TargetFramework; Dependencies: NuspecDependency list }` | one `<group>`; "" = ungrouped |
| `Nuspec` | `{ Id; Version; Authors; License; Groups }` | a parsed nuspec |
| `parseNuspec` | `xml: string -> Nuspec` | namespace-agnostic |
| `nuspecFrameworkMatches` | `alias -> groupFramework -> bool` | `netstandard2.0` ≡ `.NETStandard2.0` |
| `nuspecDependenciesFor` | `framework -> Nuspec -> NuspecDependency list` | exact group, else ungrouped, never "nearest" |
| `ships` | `cacheRoot -> id -> version -> bool` | has content under `lib/` or `runtimes/` |
| `packageOf` | `cacheRoot -> path -> (string*string) option` | (id, version) of a file under the cache |

⚠ `Nuget` is spelled that way here, while the product and the `$(NuGetPackageRoot)` token use `NuGet`.
⚠ `Nuget.Package` duplicates the name `Lock.Package` (see `Lock`).
⚠ `nuspecFrameworkMatches` / `nuspecDependenciesFor` repeat the `nuspec` prefix inside `Nuget`. A `Nuspec` submodule would say it once.

### `Restore` (Restore.fs): getting the packages a lock names

| Name | Signature | Purpose |
|---|---|---|
| `Options` | `{ PackageRoot: string option; Enabled: bool }`, `Default` = `{ None; true }` | the folder, and whether a missing package may be fetched |
| `packageRoot` | `Options -> string` | the folder in effect, normalized |
| `into` | `dir: string -> Recipe<Options>` | options with the folder at `dir`, relative to the project root |
| `Missing` | `{ Id; Version; Sha512; Files: string list }` | one package whose files are absent |
| `missing` | `Options -> Lock.Entry list -> Missing list` | one `File.Exists` per distinct path |
| `verify` | `Options -> Missing list -> string list` | package dir present and nupkg sha512 = the lock's |
| `download` | `Options -> (string*string) list -> Recipe<unit>` | one `dotnet restore` of a synthesized `PackageDownload` project |
| `ensure` | `Options -> Lock.Entry list -> Recipe<string list>` | missing → download → verify, memoized, one `Resource` per folder; returns problems |
| `prepare` | `Options -> Lock.Document -> Recipe<unit>` | `ensure` for a whole lock; fails on a problem |

⚠ `Restore.verify` is one of four unrelated `verify`s (`CscLock.verify`, `StrongName.verify`, the `Verify` module).
⚠ `Restore.into`: a preposition as a function name, and it returns a recipe of options, not a restore.
⚠ `Restore.ensure` returns problems while `prepare` fails. The names do not tell which of the two reports and which one throws.

### `Sbom` (Sbom.fs): CycloneDX 1.6 from the lock

| Name | Signature | Purpose |
|---|---|---|
| `Hash` | `{ Alg; Content }` | |
| `Property` | `{ Name; Value }` | |
| `Component` | `{ Type; BomRef; Name; Version; Supplier; Purl; Hashes; License; Scope; Components; Properties }` | |
| `Composition` | `{ Aggregate; Assemblies; Dependencies }` | |
| `Annotation` | `{ BomRef; Subjects; Annotator; Timestamp; Text }` | |
| `Bom` | `{ Root; Components; Dependencies: (string * string list) list; Formulation; Compositions; Annotations }` | |
| `cycloneDx` | `Bom -> string` | deterministic JSON, content-derived `serialNumber` |
| `forAssembly` | `cacheRoot: string -> entry: Lock.Entry -> assemblyPath: string -> Bom` | restore scope, one assembly |
| `forPackage` | `nupkgPath: string -> assemblies: Bom list -> Bom` | restore scope, one nupkg (union of assembly BOMs) |
| `packageSbomPath` | `framework: string -> string` | `sbom/<tfm>/bom.cdx.json` |
| `PackageScope.idPrefixes` | `string list -> (string -> bool)` | |
| `PackageScope.plumbing` | `path -> bool` | OPC parts, docs, the `sbom/` tree |
| `PackageScope.shippedFor` | `framework -> path -> bool` | |
| `PackageScope.assembly` / `native` | `path -> bool` | |
| `PackageScope.internalIds` / `toolingIds` | `string -> bool` | |
| `PackageScope.boundaryText` | `string` | |
| `PackageScope.timestamp` | `unit -> string` | `SOURCE_DATE_EPOCH`, else 1980-01-01 |
| `PackageScopeOptions` | `{ IsPlumbing; IsShipped; IsAssembly; IsNative; IsInternal; IsTooling; Subcomponents; DeclaredRangeProperty; RootProperties; BoundaryText; AnnotationTimestamp; KeepFormulation }` | the variable parts of the package-scope rule |
| `defaultPackageScope` | `PackageScopeOptions` | the RFC's rules |
| `shippedPaths` | `PackageScopeOptions -> framework -> (string * byte[]) list -> string list` | tier-1 inventory, shared with the verifier |
| `pathProperty` | `string` = `"xake:nuget:path"` | |
| `forPackageScopedWith` | `PackageScopeOptions -> nupkgPath -> framework -> Bom list -> Bom` | package scope, one nupkg × TFM |
| `forPackageScoped` | `nupkgPath -> framework -> Bom list -> Bom` | `…With defaultPackageScope` |

⚠ `forAssembly`/`forPackage` (restore scope) vs `forPackageScoped` (package scope): "scope" appears in the name of only one of the two scopes, so `forPackage` and `forPackageScoped` read like near-synonyms.
⚠ `PackageScope` module, `PackageScopeOptions` type and `defaultPackageScope` value are three names for one concept. Elsewhere the default is a `Default` static member (`Restore.Options.Default`, `RunOptions.Default`).
⚠ `shippedPaths` takes `(string * byte[]) list` produced by the internal `nupkgEntries`, so a caller outside the assembly cannot build its argument from a nupkg path.

### `Verify` (Verify.fs): PE comparison, and the SBOM acceptance checks

| Name | Signature | Purpose |
|---|---|---|
| `sha256` | `path: string -> string` | lowercase hex; throws on a missing file |
| `authenticodeHash` | `path: string -> string` | PE image hash, signature excluded |
| `Difference` | `{ Offset: int; Length: int; Field: string }` | one differing range, labelled |
| `compare` | `a: string -> b: string -> Difference list` | byte ranges, labelled by `a`'s layout; bytes only in a longer `b` labelled by `b`'s |
| `verdict` | `Difference list -> string` | one line: fields or range count, total differing bytes, the largest range when unlabelled content is involved |
| `sbomPackageScopeWith` | `Sbom.PackageScopeOptions -> nupkgPath -> framework -> Sbom.Bom -> string list` | checks 3.1–3.4; `[]` = passes |
| `sbomPackageScope` | `nupkgPath -> framework -> Sbom.Bom -> string list` | `…With Sbom.defaultPackageScope` |

⚠ `Verify.compare` shadows F#'s `compare` whenever `Verify` is opened.
⚠ `Verify.sha256` (throws on a missing file) and the internal `Lock.sha256` ("" on a missing file) share a name but behave differently.
⚠ `Verify.sbomPackageScope`: an SBOM rule inside the PE-comparison module, and a noun phrase for a function that returns findings.

### `StrongName` (StrongName.fs): PE stamp, checksum, strong-name re-sign

| Name | Signature | Purpose |
|---|---|---|
| `checksum` | `byte[] -> byte[]` | recomputes the PE `CheckSum` |
| `stamp` | `byte[] -> timeDateStamp: uint32 -> byte[]` | sets `TimeDateStamp` + checksum |
| `readSnk` | `path: string -> RSAParameters * bool` | `.snk` → key, has-private-key |
| `sign` | `byte[] -> RSAParameters -> byte[]` | strong-name signature (checksum not recomputed) |
| `verify` | `byte[] -> RSAParameters -> bool` | checks an existing strong-name signature |
| `signFile` | `path -> snkPath -> unit` | re-signs in place |
| `normalise` | `path -> timeDateStamp -> snkPath -> unit` | stamp, re-sign, checksum (E4) |

⚠ `StrongName.sign` vs `Sign` module vs `Sign.sign` builder: "sign" means strong naming here and Authenticode/NuGet signing next door.
⚠ `normalise` is British spelling in a codebase that otherwise writes `normalize`.
⚠ `sign` leaves the checksum stale while `stamp` refreshes it. The names do not say which one refreshes the checksum.

### `Pack` (Pack.fs): deterministic zip and nupkg

| Name | Signature | Purpose |
|---|---|---|
| `Entry` | `{ Path; Source }` | a zip path and the file on disk |
| `Options` | `{ Timestamp: DateTime; Level: CompressionLevel }` | |
| `defaultOptions` | `Options` | 1980-01-01 or `SOURCE_DATE_EPOCH`; `Optimal` |
| `zip` | `output -> Entry list -> Options -> unit` | sorted entries, fixed timestamp |
| `nupkg` | `output -> nuspec -> files: Entry list -> Options -> unit` | adds nuspec, `[Content_Types].xml`, `_rels/.rels`, a derived-GUID psmdcp |
| `entries` | `zipPath -> (string * int64 * uint32 * DateTime) list` | path, size, crc32, time |

⚠ `Pack.defaultOptions` is a value, while `Restore.Options.Default`/`RunOptions.Default` are static members.
⚠ `Pack.Entry` (path + source file) vs `Pack.entries` (anonymous 4-tuples) vs `Lock.Entry`: same word, three shapes.

### `Sign` (Sign.fs): signing as a delegated rule

| Name | Signature | Purpose |
|---|---|---|
| `Algorithm` | `Sha256 \| Sha384 \| Sha512` | |
| `Certificate` | `Thumbprint of string \| TrustedSigning of account * profile \| KeyId of string` | a key reference, never key material |
| `Kind` | `PeImage \| Nupkg` | |
| `Request` | `{ File; Kind; Certificate; TimestampServer: string option; Hash: Algorithm; Description: string option }` | what the signer receives |
| `Signer` | `Request -> Async<byte[]>` | the part a deployment replaces |
| `Store` | `{ TryFetch: string -> Async<byte[] option>; Publish: string -> byte[] -> Async<unit> }` | content-addressed delivery |
| `Settings` | `{ Target; Input: string -> string; Certificate; TimestampServer; Hash; Description }`, `Default` | rule mask, input mapping, policy |
| `kindOf` | `path -> Kind` | by extension |
| `imageHash` | `path -> string` | Authenticode hash (PE) or sha256 (nupkg) |
| `identity` | `Settings -> input: string -> string` | `sha256(image hash \| certificate \| timestamp server \| algorithm)` |
| `request` | `Settings -> input -> Request` | |
| `SignatureEntry` | `[<Literal>] ".signature.p7s"` | |
| `isSigned` | `path -> bool` | has a certificate table / `.signature.p7s` (validity not checked) |
| `verifySameImage` | `input -> signed -> bool` | same image and signed |
| `fakeSigner` | `Signer` | real `WIN_CERTIFICATE`/p7s container, fake payload |
| `directoryStore` | `root: string -> Store` | one file per identity key |
| `rule` | `Settings -> Signer -> ExecContext Rule` | the local rule: the body signs |
| `executor` | `Settings -> Signer -> Store -> budget: Resource -> DelegatedExecutor<ExecContext>` | never calls the body; store hit / in-flight join / signer under `budget` |
| `SignSettingsBuilder`, `sign` | ops `target`, `input`, `certificate`, `timestamp`, `hashalg`, `description`; `Run` returns `Settings` | `Sign.sign { ... }` |

⚠ `Sign.sign`: the module and the builder share a name (FSharp.Core's `sign` blocks a top-level `sign`). Also, unlike `csc {}`, it builds a settings record, not a recipe.
⚠ `Sign.Settings` vs `CscSettingsType`, `Restore.Options`, `Pack.Options`, `RunOptions`, `ImportOptions`, `EvalOptions`, `PackageScopeOptions`: eight spellings for "configuration record".
⚠ `Settings.Hash: Algorithm` vs `Request.Hash`: the field is called `Hash` but holds an algorithm, and the builder op is `hashalg`.
⚠ builder op `timestamp` sets `TimestampServer` (a URL), not a timestamp.

### `CscImpl` (Dotnet.csc.fs, `[<AutoOpen>]`): the csc task, composed and replayed

| Name | Signature | Purpose |
|---|---|---|
| `CompilerServer` | `Shared of keepAlive: int option \| InProcess` | Roslyn compiler server use |
| `CompilerServer.fromEnvironment` | `unit -> CompilerServer` | `InProcess` when `XAKE_CSC_SERVER` is `0`/`false`/`off`/`no` |
| `CscSettingsType` | `{ Platform; Target; Out: File; Src: Fileset; Ref: Fileset; RefGlobal; Resources: ResourceFileset list; Define; Unsafe; TargetFramework; CommandArgs; FailOnError; CscPath: string option; Toolset: string option; Lock: string option; Server: CompilerServer }`, `Default` | composed settings |
| `CscSettings` | `CscSettingsType` | `= CscSettingsType.Default` |
| `RunOptions` | `{ FailOnError; CscPath: string option; Restore: Restore.Options; Server: CompilerServer }`, `Default` | what the runner needs, apart from what it compiles |
| `CscLock.resolve` | `CscSettingsType -> Recipe<Lock.Entry>` | composes the entry without compiling (hashes empty) |
| `CscLock.compile` | `Lock.Entry -> Recipe<unit>` | replays an entry with `RunOptions.Default` |
| `CscLock.compileWith` | `RunOptions -> Lock.Entry -> Recipe<unit>` | replays with explicit runner options |
| `CscLock.record` | `path: string -> CscSettingsType -> Recipe<unit>` | resolve, `Lock.rehash`, write a one-entry lock (overwrites) |
| `CscLock.verify` | `path: string -> CscSettingsType -> Recipe<string list>` | `Lock.diff` of the recorded entry against the resolved one; writes nothing |
| `Csc` | `CscSettingsType -> Recipe<unit>` | resolve, then `lock` handling, then `run` |
| `CscSettingsBuilder`, `csc` | ops `platform`, `target`, `targetfwk`, `out`, `src`, `ref`, `refif`, `refs`, `grefs`, `resources`, `resourceslist`, `define`, `unsafe`, `cscpath`, `toolset`, `lock`, `noserver`, `keepalive`, `args`, `nofailonerror` | `csc { ... }`; `Run` = `Csc` |

(`run`, `resolve`, `serverArgs`, `recordLock`, `ensureCompilerAvailable` are private/internal: `run` is the only place that starts csc.)

⚠ `CscLock` module next to the `Csc` function: F# will not let a module share the name of a `let`, so the replay entry point is in a module named after the lock, even though `compile`/`compileWith`/`resolve` have nothing to do with a lock file.
⚠ `CscLock.compile` takes a `Lock.Entry` from any source (import, hand-built, `resolve`). "Lock" in the name suggests it needs a lock file, and it does not.
⚠ `CscSettingsType.Lock` (a path) is a field named after the `Lock` module, and the builder op `lock` is a verb.
⚠ `CscSettingsType` ("Type" suffix) + `CscSettings` value (the default) is the pre-existing convention. `RunOptions` next to it follows the hermetic one.
⚠ `CscImpl`: public API in an `[<AutoOpen>]` module named "Impl" (same for `FscImpl`, `ResgenImpl`, `MsbuildImpl`).
⚠ `RunOptions` carries `FailOnError`/`CscPath`/`Server` that `CscSettingsType` also has, and `Csc` copies them across by hand.
⚠ `CompilerServer.Shared`/`InProcess` vs builder ops `noserver`/`keepalive`: the builder and the type use different words for the same choice.

---

## Part 2: pre-existing (csc/fsc/resgen/msbuild, DotNetFwk, Fsproj)

### `DotNetFwk` (DotNetFwk.fs)

| Name | Signature | Purpose |
|---|---|---|
| `FrameworkInfo` | `{ Version; InstallPath; AssemblyDirs: string list; ToolDir; CscTool; FscTool: string option -> string option; MsbuildTool; EnvVars: (string*string) list }` | a located toolchain |
| `locateFramework` | `string option -> FrameworkInfo` (memoized) | SDK, then MS registry, then mono (see `docs/dotnet-build.md`) |
| `locateAssembly` | `FrameworkInfo -> string -> string` | a global assembly in `AssemblyDirs`, else the name as given |

(`sdkImpl`/`msImpl`/`monoFwkImpl` are internal. `Roots.nugetRoot`/`dotnetRoot` forward to `sdkImpl`.)

⚠ `DotNetFwk` vs `Dotnet`/`DotnetTasks`/`Xake.Dotnet`: three casings of one word.
⚠ `FrameworkInfo.CscTool` is a path while `FscTool` is a function of a version, so the two tool fields have different shapes.

### `DotNetTaskTypes` (DotnetTasks.fs, `[<AutoOpen>]`)

| Name | Signature | Purpose |
|---|---|---|
| `TargetType` | `Auto \| AppContainerExe \| Exe \| Library \| Module \| WinExe \| WinmdObj` | |
| `TargetPlatform` | `AnyCpu \| AnyCpu32Preferred \| ARM \| X64 \| X86 \| Itanium` | |
| `MsbVerbosity` | `Quiet \| Minimal \| Normal \| Detailed \| Diag` | |

(`Impl` is internal.)

### `ResourceFileset` (ResourceFileset.fs, `[<AutoOpen>]`), `Resx` (Resx.fs)

| Name | Signature | Purpose |
|---|---|---|
| `ResourceSetOptions` | `{ Prefix: string option; DynamicPrefix: bool }` | |
| `ResourceFileset` | `ResourceFileset of ResourceSetOptions * Fileset` | |
| `DefaultOptions`, `Empty` | values | |
| `ResourceFilesetBuilder`, `resourceset` | ops `prefix`, `dynamic`, `files`, `basedir`, `includes`, `excludes` | |
| `Resx.read` | `path -> (string*string) list` | string entries only |
| `Resx.compile` | `resxFile -> resourcesFile -> unit` | byte-identical to `GenerateResource` for string-only resx |

⚠ `ResourceFileset` is the name of an AutoOpen module, a DU type and its case at once.
⚠ `Resx.compile` (hermetic, pure) and `ResGen` (pre-existing task, uses `ResXResourceReader`) are two resx compilers with unrelated names.

### `FscImpl` (Dotnet.fsc.fs), `ResgenImpl` (Dotnet.resgen.fs), `MsbuildImpl` (Dotnet.Msbuild.fs), all `[<AutoOpen>]`

| Name | Signature | Purpose |
|---|---|---|
| `FscSettingsType` | `{ Platform; Target; Out; Src; Ref; RefGlobal; Resources; Define; TargetFramework; FscVersion; Doc; CommandArgs; FailOnError; NoFramework; Tailcalls }` | |
| `FscSettings`, `Fsc`, `fsc` | value / `FscSettingsType -> Recipe<unit>` / builder | ops `platform`, `target`, `targetfwk`, `out`, `src`, `ref`, `refif`, `refs`, `grefs`, `resources`, `resourceslist`, `define`, `doc`, `fscver`, `noframework`, `notailcalls`, `args`, `nofailonerror` |
| `ResgenSettingsType` | `{ Resources; TargetDir: DirectoryInfo; UseSourcePath }` | |
| `ResgenSettings`, `ResGen`, `resgen` | ops `resources`, `resourceslist`, `targetdir`, `nosourcepath` | |
| `MSBuildSettingsType` | `{ BuildFile; Target; Property; MaxCpuCount; ToolsVersion; Verbosity; RspFile; FailOnError }` | |
| `MSBuildSettings`, `MSBuild`, `msbuild` | ops `buildfile`, `target`, `targets`, `prop`, `props`, `maxcpu`, `toolsversion`, `verbosity`, `rspfile`, `nofailonerror` | |

⚠ `Csc`/`Fsc`/`ResGen`/`MSBuild`: PascalCase functions, cased three different ways (`ResGen`, `MSBuild`).
⚠ `fsc {}` has no lock, no `RunOptions` and no server, so the F# side is still what `csc {}` was before this branch.

### `Fsproj` (Fsproj.fs): F# project evaluation

| Name | Signature | Purpose |
|---|---|---|
| `ProjectInfo` | `{ AssemblyName; Sources; References; ProjectRefs; Defines; Properties: Map<string,string> }` | what msbuild says about one framework |
| `EvalOptions` | `{ Project; Framework; Configuration; Properties; Output }`, `Default` | |
| `evaluate` | `EvalOptions -> Recipe<unit>` | `-restore`, `PrepareForBuild;GenerateAssemblyInfo;ResolveReferences`; writes a tokenized file |
| `parseWith` | `roots -> resultFile -> ProjectInfo` | pure read |
| `load` | `resultFile -> Recipe<ProjectInfo>` | read against the build's roots |

⚠ `Fsproj` still holds the F# evaluation, while `Project` holds the C# import, so the "project" modules are split by language and neither name says so.
⚠ `Fsproj.load` does not `needFiles` its file, but `Lock.load` does. Same verb, different dependency behaviour.
⚠ `Fsproj.parseWith roots resultFile` reads a *file*, while `Lock.parseWith roots text` parses *text* (`Lock.readWith` is the file form).

---

## Not public

`Json` (`module internal`) is shared by `Lock`, `Nuget`, `Sbom`, `Sign`, `Fsproj`, `Project`.
The tests reach it and every other `internal` through `InternalsVisibleTo("tests")`.
