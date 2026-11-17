# Brief: compact, relocatable paths in the build database (Xake, database 0.7)

Audience: the agent working in the Xake repository. Xake is public, so this brief names no product or company of the
consumer. Engine facts are taken from the Xake sources at 43706d3: `src/core/Database.fs`, `Fileset.fs`, `File.fs`,
`Pickler.fs` and `ExecTypes.fs`.

## Decisions (owner)

- 2026-11-17, answers to the plan's questions ([plan.md](plan.md) Q1-Q6):
  - **Q1:** in the file every path under a root is stored relative to that root (as planned). In memory a loaded
    `File` keeps its full path as `Name` (as in 4.0); root-relative display in logs, progress and `--dump` is a
    separate later feature. This supersedes §5 "Display names of loaded targets".
  - **Q2:** roots are declared through `ExecOptions.PathRoots` and a `pathRoot` operation of the script builder; no
    command-line form. A root's path may also be given relative: it is resolved against `ProjectRoot` and recorded
    as relative, so it moves together with the project.
  - **Q3:** a root may take its value from a variable (`fromVar`): the launch-time value; an unset variable makes it
    an undeclared root.
  - **Q4:** records naming a root this run does not declare (one declared in an earlier run, the script since edited
    or the variable unset) are not loaded; those targets rebuild, the run does not fail.
  - **Q5:** the implementer's call: `Fileset.BaseDir` of `GetFiles` stays a string (an absolute `BaseDir` is not
    relocated), documented.
  - **Q6:** one shared 0.7 with dep-cutoff: record tags 4-15 are reserved for its records; no 0.8.

- 2026-11-17, triage of the critic ([critic.md](critic.md), Q-B..Q-I; Q-A pending):
  - **Q-B:** `--dump` and `--dryrun` open the database read-only: no upgrade, compaction or dropping of records.
  - **Q-C:** every record starts with its length; a torn or unreadable tail is recovered by keeping the whole records
    before it (salvage), not by discarding the file.
  - **Q-D:** a record tag never changes its body; dep-cutoff's result with content data takes its own tag from 4-15.
  - **Q-E:** two roots with the same value, or a declared root equal to `ProjectRoot`, is a launch error.
  - **Q-F:** at open, a path that is not under its longest root of this run is re-rooted (one rewrite).
  - **Q-G:** the Info line reports `<n> files outside all roots` when files fall outside every root, as a hint to
    declare one; the documented recipe for a script below the repository root is a relative root (`pathRoot ".."`).
    Owner's words: "if no roots are declared; we encourage declaring them" (implementer's proposal for the exact
    condition in session.md).
  - **Q-H:** a secret variable as a root is rejected at launch.
  - **Q-I:** a synthetic test with paths pinned to 114 bytes is the size regression guard; the measurement on a real
    database is an `[<Explicit>]` test the consumer runs on its file.
  - Note (dep-cutoff decision): no content epochs in 5.0, so no epoch counter in 0.7.

- 2026-11-17, **Q-A:** records that name a root this run does not declare are an **error printed on the console**:
  at open, one Error per such root with its last recorded value, the number of records and the fix (declare the root
  or set its variable). The records are not loaded (Q4; those targets rebuild), and no rewrite (upgrade, compaction,
  re-root, scrub) drops them: they are carried over as whole length-framed records with the path entries they name,
  so declaring the root again brings them back. 2026-11-17, owner: the error **only prints**; it does not fail the
  run (exit code unchanged).

## Problem

The database stores every file path as a full string, once per reference:

- `Database.fs`, `Persist.file = wrap (File.make, fun a -> a.FullName) str` (used by `FileDep` and file targets);
- `Fileset.fs`, `PicklerImpl.fileinfo = wrap (File.make, File.getFullName) str` (the file lists of `GetFiles`).

Measured on a consumer's database after a full build: 100–200 rules × several configurations, each compiling against
some 560 files.

| | |
|---|---:|
| file size | 88.6 MB |
| path strings in it | 712 760 |
| bytes taken by those paths | 81.6 MB (92 %) |
| distinct paths | 36 150 |
| distinct directories | 3 478 |

Consequences:

1. **Size and load time.** About 200 ms per run go into reading and deserialising the file, and the heap grows with
   it. Every unpickled path also calls `File.make`, which allocates a new `FileInfo` per reference (712 760 of them for
   36 150 distinct files).
2. **Not relocatable.** Every path is absolute. If the working copy is moved, or a shared folder outside it (such as a
   package cache) gets another location, all records are invalidated and everything is rebuilt.

The first full rewrite (compaction) does not help: the records are simply large (`recordCount > Count * 5` never
fires).

## What to add

### 1. Path tables in the file (format 0.7)

The file stays an append-only log, but a record gets a tag:

```fsharp
type Record =
  | DirRec    of id: int * parent: int * name: string    // parent = -1: a root (see 2)
  | FileRec   of id: int * dir: int * name: string
  | ResultRec of BuildResult
```

- A `File` in any pickle (`FileDep`, `FileTarget`, the `Filelist` of `GetFiles`) is written as its `FileRec` id, an
  `int`. With the measured database that is ~2.9 MB of references plus ~1.5 MB of tables instead of 81.6 MB,
  so the file would be about **11 MB instead of 89**. Measure it, do not trust the estimate.
- `PhonyAction` names stay strings.
- Invariant: a `DirRec`/`FileRec` comes in the file before the first record that names it, and parents come before
  children.

### 2. Named roots (relocation)

- Before the path is interned it is split by the longest matching root. The roots are:
  - `ExecOptions.ProjectRoot`, as `$(ProjectRoot)`, always;
  - roots declared by the script through a new option, for example
    `ExecOptions.PathRoots : (string * string) list` (name → current absolute path);
  - for any other path, the file system root (`/`, `C:\`), i.e. an absolute path as today.
- A root is a `DirRec` with `parent = -1` and its name in place of `name`: `$(ProjectRoot)`, `$(NUGET_PACKAGES)`, `/`.
- On load a root resolves to its value in **this** run, so relocating a working copy or a declared folder keeps the
  records valid. If the files behind the root are not the same, the stamps differ and the dependants are rebuilt as
  usual. A root that a record names and this run does not declare: those records are treated as stale and the file is
  not failed.
- Comparisons and hashing of paths keep today's rules (`File.fs`, `compareNames`/`getFileHash`). The split must use
  the same case rule: a root matched case-insensitively must also be compared that way.

### 3. Where the ids live

- Ids exist only in the file. In memory the table is owned by the database agent
  (`MailboxProcessor` in `Storage.openDb`), which already serialises all writes, so no locking is needed.
- `Store result`:
  1. Collect the files of `result` that are not in the table yet, and their missing directories.
  2. Give them tentative ids.
  3. Pickle the `DirRec`/`FileRec` records and then `ResultRec` into the `MemoryStream`, as today.
  4. Write the buffer in one `Write`.
  5. Only then commit the ids into the table.
  
  If pickling fails, nothing is committed and nothing reaches the file. This keeps the current guarantee that the
  file is a sequence of whole records.
- The picklers of `dependency`, `result` and `filelist` become functions of the table (`result table`). `Pickler.fs`
  itself does not change.
- Do not change `File`, `Dependency` or `BuildResult` in this brief.

### 4. Load and compaction

- Load: a `DirRec`/`FileRec` fills the table (id → `File`). A `ResultRec` gets `File` values from it, so every reference
  to the same path is **the same `File` object** (one `FileInfo` per distinct file, not per reference). New ids
  continue from `max + 1`.
- Compaction and upgrade (the existing branch in `openDatabaseFile`): rewrite with a fresh table holding only the
  paths that live records name. Ids are renumbered freely.
- 0.4–0.6 databases are read with the old loop and then rewritten as 0.7 (the existing `isOldVersion` path).
  `XakeDbVersion = "0.7"`.

### 5. Display names of loaded targets

Today `FileTarget` is pickled by `f.Name` (the path as written in the script, often relative), while `FileDep` is pickled
by `f.FullName`. In 0.7 both go through the file table, so a loaded `File` has only its full path. Comparison is not
affected (it already uses `FullName`), but the name shown in logs, progress and `--dump` must not become a long
absolute path:

- a loaded `File` gets as `Name` its path relative to its root. Under `$(ProjectRoot)` that is the plain relative path
  (`out/a.dll`, as a script would write it). Under another root it is `$(NAME)/rest`. Outside all roots it is the
  absolute path;
- a `File` created during the run keeps its `Name` as today, so a target shows the same name whether it came from the
  database or from the script.

### 6. Diagnostics

- `--dump` (or the existing database dump, if any) prints paths resolved with their root: `$(ProjectRoot)/src/a.cs`.
- At Info: `database: <n> records, <d> dirs, <f> files, <size>`. When a root differs from the run that wrote it:
  `root $(X) moved: <old> -> <new>`.

## Out of scope (separate work)

- Removing `FileInfo` from `File` (a public type) and keeping the full name as a string. Together with that, check the
  case rule in `File.fs`: on Unix `compareNames` ignores case but `getFileHash` does not, and on Windows it is the
  other way round. This looks inverted and breaks the `Equals`/`GetHashCode` contract for names that differ only in
  case. It is not verified and needs its own investigation.
- Holding (dir, name) pairs in memory instead of strings: `File` is used throughout the engine, and the gain on top of
  §4 is small.
- Content cut-off (`content-cutoff-brief.md`): independent, but both bump the database version. If they are released
  together, it is one version.

## Acceptance

- On a database like the one measured: the size before and after, and the load time (cold and warm) before and after.
  The size should go down by at least 5×.
- A build without changes after the upgrade does not rebuild anything: the 0.6 → 0.7 upgrade keeps all records valid.
- Relocation: copy the working copy (with `.xake` and outputs, timestamps preserved) to another folder and build
  again. Nothing is rebuilt. The same for a declared root pointed at a copied folder.
- An undeclared root that a record names: those targets rebuild, the run does not fail.
- A failing `Store` (an injected pickling error) leaves the file readable, and the next `Store` that names the same
  paths writes their tables again.
- Old databases 0.4–0.6 load and upgrade. A script that declares no roots behaves as today, apart from the file
  format.
- Several rules storing results concurrently: the file reads back identically (an order-independence test on the
  agent).
