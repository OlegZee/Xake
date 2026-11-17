# Brief: the `File` type — one path semantics, one case rule (Xake, after 4.0)

Audience: the agent working in the Xake repository. Xake is public: this brief names no product or company of the
consumer. Facts below are from Xake sources at `43706d3` (`src/core/File.fs`, `File.fsi`, `Types.fs`, `Path.fs`,
`Database.fs`, `ExecTypes.fs`). Related candidates: compact relocatable paths in the database (db-paths) and content
cut-off (`ArtifactDep3`) — one database format bump should cover all three.

## Problem

`File = T of string * FileInfo`: the string as given (`Name`, for output) and a `FileInfo` used only for `FullName`.

1. **Case rule is inverted.** `compareNames` uses `ignoreCase = Env.isUnix`: case-insensitive on Linux, case-sensitive
   on Windows. `Target` (`Types.fs`) does the opposite (`Ordinal` on Unix, `OrdinalIgnoreCase` on Windows). `File` and
   `Target` disagree about the same paths.
2. **`Equals` and `GetHashCode` disagree on Unix**: equality ignores case, the hash does not. Two "equal" files land in
   different buckets of `Set`/`Map`/`Dictionary`.
3. **Path bound to the process cwd at creation.** `FileInfo n` resolves a relative path against the current directory,
   not `ProjectRoot`; it works only while `ProjectRoot = GetCurrentDirectory()` (`ExecTypes.fs:79`).
4. **Database stores two different things.** `FileDep` is pickled by `FullName`, `FileTarget` by `Name` (the raw,
   possibly relative string), re-resolved against cwd on load. Absolute paths also bloat the database and do not survive
   moving the project.
5. **Small things:** `GetHashCode` compares with `File.undefined` on every call; `undefined` carries a `null`;
   normalisation exists twice (`FileInfo` and `Path.collapsePath`, which says "as `File.make` does") with different
   handling of `\`; `Name` has no defined meaning.
6. **Rule masks follow the OS** (`PathMask`, `RuleImpl`, D11: ignore-case on Windows): a rule matches on Windows and not
   on Linux for the same script.

## Decision (owner)

### One case rule everywhere: ordinal

`File`, `Target`, `PathMask`/rule matching and `WorkerPool.mapKey` compare **ordinally on every OS** through one shared
comparer (`Path.comparer`). Same script → same targets, same rule matches, same database on Windows, macOS and Linux.
`\` stays a separator on Windows (normalisation, not case).

The cost — on a case-insensitive file system two spellings of one file become two targets — is covered by a check:

- During a run Exec keeps a map `case-folded key → first spelling` for every `File` it meets as a target or dependency.
- A second spelling of the same key: for **targets** an error (`target 'out/Foo.dll' is also named 'out/foo.dll'`),
  for **sources** (`FileDep`, fileset results) a warning. On all OSes, so a Linux-only failure shows on the developer's
  machine first.
- The spelling in the script is the truth; a different case on disk alone is not reported.

### `File` is a normalised absolute path

Nothing else: no disk state, no root inside.

- **Normalisation** is `Path.collapsePath` only: `/` on every OS (OS form rebuilt for output and IO), `.` and `..`
  collapsed, repeated separators removed, the root (`/`, `C:`) kept, case kept. Lexical: symlinks are not resolved.
- **Creation:** `File.make path` — absolute as is, relative against the cwd at the call (today's meaning; enough for
  scripts and tests outside Exec). With a known root: `File.make (root </> path)`. No root is stored.
- **Identity:** `Equals`, `GetHashCode`, `CompareTo` on the normalised string, ordinal. Equal iff the strings are equal.
- **Display:** `File.display root f` — relative to `root` when under it, else absolute. `Name` becomes
  `display <cwd>` (kept for source compatibility, stable output) or is removed; your call, note it in release notes.
- **Disk:** `exists`, `getLastWriteTime` query the disk each time, no caching. `FileInfo` is dropped entirely (not
  exposed, not `Obsolete`: `File.fsi` never exposed it). If a helper is wanted: `File.info f = FileInfo (fullName f)`.
- **`undefined`** removed; use `File option` where it is used.

### Database

- `FileDep` and `FileTarget` are both written as `File.display ProjectRoot f` (absolute when outside the root) and
  joined with the current `ProjectRoot` on load — the same rule for both. Coordinate the encoding with db-paths.
- Old records (absolute `FullName`, raw target `Name`) load: normalisation accepts them as they are. Bump
  `XakeDbVersion` together with db-paths / cut-off; old Xake does not read the new database (as at every bump).

## Compatibility

- API: `File.make : string -> File` and the `File` module functions keep their signatures and meaning; added:
  `File.display`, `Path.comparer`.
- Behaviour change (release notes, minor/major version): case. On Linux `A.cs` and `a.cs` stop being one file; on
  Windows/macOS `Foo` and `foo` stop being one target and the new check reports it. Rule masks become case-sensitive on
  Windows.

## Acceptance

- One test per OS-independent rule: two paths differing only in case are different in `Set<File>`, `Set<Target>`,
  rule lookup and `WorkerPool`, on every OS; `Equals`/`GetHashCode` consistent (property test).
- Normalisation: `a/./b/../c`, `a//b`, `C:\x\..\y` (Windows), roots, trailing separator — one function, one result,
  shared by `File` and masks.
- Two spellings of one target in a run → error naming both; of one source → warning.
- A database written by the old Xake loads and gives the same "up to date" verdicts; a new database moved with the
  project to another folder gives the same verdicts (with db-paths).
- `File.make` outside Exec (no `ProjectRoot`) works as today; no `FileInfo` left in `File`.
