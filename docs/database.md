# The build database (`.xake`)

Xake keeps what it built, from what, in a file next to the script, `.xake` by default
(`ExecOptions.DbFileName`). This page describes format 0.7 (Xake 5.0): what is stored, how paths are kept short and
relocatable, and what happens when the file or the roots change.

## Path roots

Every file path in the database is stored relative to a **root**: the longest root that contains it.

- `$(ProjectRoot)` is always a root: `ExecOptions.ProjectRoot` (`rootdir` in the script), the folder the script
  runs in by default.
- A script declares more roots with `pathRoot` (or `ExecOptions.PathRoots`):

  ```fsharp
  xakeScript {
      pathRoot "REPO" ".."                              // relative: resolved against the project root
      pathRoot "PACKAGES" (fromVar "NUGET_PACKAGES")    // the launch-time value of a variable
      pathRoot "SDK" "/usr/share/dotnet"                // absolute
      ...
  }
  ```

- A path under no root is stored under its file-system root (`/`, `C:\`, `\\server\share`), i.e. absolute.

When the database is read, each root resolves to its value **in this run**. Moving a working copy (with `.xake` and
the outputs, timestamps kept), or pointing a declared root at a copied folder, keeps every record valid: nothing is
rebuilt. If the files behind a root are not the same, their stamps differ and the dependants rebuild as usual.

Rules for roots, checked when the script starts (a violation stops the run like any launch error, and no database is
opened):

- a name is letters, digits and `_`; `ProjectRoot` is taken; names are unique;
- a value is an absolute path, or a path relative to the project root (it then moves with the project, and the
  database records it as relative);
- two roots with the same folder, or a declared root equal to the project root, are an error;
- `fromVar "NAME"` takes the variable's value from the launch-time variable set (relative values resolve against the
  project root). A secret variable is rejected. An unset or empty variable leaves the root undeclared for this run.

### A script below the repository root

A script in `repo/build/` has `$(ProjectRoot)` = `repo/build`, so sources in `repo/src` are outside it and stored
absolute: moving the repository rebuilds them. Declare the repository as a root:

```fsharp
pathRoot "REPO" ".."
```

At open the log says `<n> files outside all roots` when records name files under no declared root; when the
script declares no roots at all, it adds a hint to declare one.

### What relocation does not cover

- **A variable that holds a path** (`Var`, `EnvVar` dependencies) is compared by value: a rule that read an output
  folder or a tool path from a variable reruns when the value moves, whatever the roots.
- **A fileset with a `BaseDir`** (`GetFiles` dependencies): the base folder is kept as written. An absolute
  `BaseDir` is not relocated, so the rule reruns after a move; a relative one is resolved as today.
- **Another spelling of the same folder** (a symlink such as macOS `/tmp` -> `/private/tmp`, or other letter case
  on a case-insensitive file system) does not match a root's prefix: such paths are stored under the file-system
  root and are not relocatable. They are counted in `files outside all roots`.
- **Same stamps, other content.** A root re-pointed at a folder whose files carry equal write times (within 10 ms) is
  taken as unchanged, as in 4.0.

## A root this run does not declare

Records that name a root the current run does not declare (it was declared in an earlier run and the script was
since edited, or its variable is unset now) are reported as an error at open:

```
[ERROR] database: 412 records name the root $(PACKAGES) (last value /home/u/.nuget/packages), which this run does
        not declare; their targets rebuild. Declare it (pathRoot "PACKAGES" ...) or set its variable.
```

Those records are not loaded, so their targets rebuild; the run continues. They are **kept** in the file: no rewrite
drops them, so declaring the root again brings them back.

## Messages at open

- `database: <n> records, <d> dirs, <f> files, <size>` (Info).
- `root $(X) moved: <old> -> <new>` (Info) when a root's value differs from the run that recorded it. A relative
  root is recorded as written, so it never "moves".
- `<n> files outside all roots` (Info), see above.
- `Upgrading database to version 0.7`, `Compacting database`, `Re-rooting database paths`, `Recovering database:
  <k> records kept, damaged tail dropped` (Message) when the file is rewritten.

`--dump` and `--dryrun` open the database read-only: no upgrade, compaction, re-rooting or recovery is written.

## Format 0.7

The file is a header (`XAKE`, the version, a date) followed by an append-only sequence of records. Each record is
its length (a 7-bit encoded integer, counting the tag and the body), a one-byte tag and the body:

| tag | record | body |
|---:|---|---|
| 0 | `DirRec` | id, parent id + 1 (0: a root), name: one path segment, or the root's name (`$(ProjectRoot)`, `$(NAME)`, `/`, `C:\`) |
| 1 | `FileRec` | id, directory id, file name |
| 2 | `ResultRec` | a build result: targets, time, dependencies, steps; every file in it is a file id. Read as a result without content data (written by no 5.0 run) |
| 3 | `RootRec` | root name, the value it had in the run that wrote it (as written, for a relative root) |
| 4 | `ResultContentRec` | the body of tag 2, then the result's content data ([content-cutoff.md](content-cutoff.md)): the size each file dependency had (file id, size), the static needs (strings), the size of each `need` group, a "recorded" flag. Every result is written with this tag |
| 5 | `ContentRec` | a file's content history: file id, then its entries newest first, each `(time, size, SHA-256 or none)`; the last record per file wins, a rewrite keeps one per file |
| 6-15 | reserved | later records of content cut-off |

- Ids are 7-bit encoded integers from one counter shared by directories and files. An id is never reused, even
  after a failed write.
- A `DirRec`/`FileRec` comes before every record that names it; a parent before its children.
- A record type never changes its body: a new body is a new tag.
- Phony targets, variables and step names stay strings.
- On load every distinct path becomes one `File` value, shared by all records that name it.

### Writing a result

The database agent (`Storage.openDb`) owns the path table and serialises all writes. To store a result it pickles
the result into a buffer, giving each new path a fresh id and collecting the `DirRec`/`FileRec` records it needs,
then writes those records and the `ResultRec` in one write. Only after the write succeeds are the new ids committed
to the table; a failed pickle writes nothing, and the next result naming the same paths writes their records again
(with new ids). After a failed write the agent stops appending for the run (results stay in memory; the next run
rebuilds them).

### Rewrites

The file is rewritten at open (not in a read-only run) when:

- it is an older version (0.4-0.6): upgraded to 0.7, every record kept;
- it holds the clear value of a variable now declared secret;
- it has more than five times as many records as it needs (compaction);
- a path is stored under a root that is no longer its longest root in this run (a root was declared after the path
  was first stored): it is re-rooted, so the next move is relocatable;
- its tail is damaged (a torn last record after a crash, or an unreadable record): the whole records before it are
  kept, the rest dropped.

A rewrite keeps the ids of the paths it keeps, and carries the records under undeclared roots over unchanged.

### Older and newer databases

0.4-0.6 databases are read and upgraded with no rebuild. A database older than 0.4 is discarded. Xake 4.x cannot
read a 0.7 database: it discards it and rebuilds.
